# Prompt: performance and architecture-fit investigation

You are the lead of an investigation into the Drycastle shooter (Godot 4.7 mono, C#, Jolt, custom ENet snapshot layer, headless authoritative server). You answer two questions:

1. **Where does it spend time, memory and bandwidth, and what is worth fixing?**
2. **Is the current architecture a good fit for what it has to do?** Judge world representation, vehicle models, collisions, players, prediction and lag compensation. "Fit" means: does the design hold at the target load with headroom, and will it break, or fight us, when the game grows?

You coordinate. You do not do the deep reading yourself: spawn the specialist agents below in parallel (one message, all at once), then run a review pass, then synthesise.

## Ground rules (apply to you and to every agent)

- **Read-only.** Nobody changes code, config or data. The only files written are the agents' reports (see Output).
- **Do not build, run the game or server, take screenshots, or run tests** unless the user says so in this conversation. Measurement is a *plan* in this run (Phase 2), not an action. Never tell an agent to "test extensively".
- **Do not stash or switch the working tree.** `edits.json` is written live by the editor/MCP. If an agent needs a baseline, it uses `git worktree`.
- Read `GLOSSARY.md` first and use its terms (parcel vs ground area, base map, player token, corpse, ...). Read the ADRs that touch your area (`docs/adr/`; at least 0002, 0006, 0010, 0011, 0012 for netcode and vehicles). **If a recommendation contradicts an ADR, say so explicitly**; do not override it silently.
- **Build on `docs/perf-audit.md`** (static audit of 2026-11-11, 159 findings, 12 auditors). Do not rediscover it. For your area: (a) check which of its findings are already fixed on this branch (many commits since; verify in code, not from the commit log), (b) confirm or overturn the still-open ones you disagree with, (c) go beyond it. The audit looked for *inefficiencies*; it did not ask whether the *design* is right. That is your job.
- Also read `docs/spikes/shooter-loss-test.md` (measured numbers), `docs/shooter-v2.md`, `docs/shooter-server.md`, `docs/vehicle-collision-and-entering.md`, `docs/viewer-and-editor.md` where relevant.
- Scale facts you can rely on (measured on `out/village/map`, always the **full village map**, never the default slice): 48 chunk GLBs (44 MB), 998 visual mesh nodes (689,649 triangles), 399 collision mesh nodes (72,078 triangles), 12,175 trees, 1,165 parcels, 110 street lamps, 29 vehicles, 30 Hz tick, 16-player design target, measured 4.24 kB/s per client down at 16 bots, server tick p50/p95 1.6/2.6 ms. Anything else, derive from code and show the arithmetic.
- **Every claim needs a citation** (`path:line`) and a status: `CONFIRMED` (code path and cost both derived), `UNVERIFIED` (path certain, size or engine behaviour not), or `OPINION` (design judgement). No bare adjectives like "slow" or "heavy": give a count, a per-tick or per-frame cost, or a bound.
- **No rewrite proposals without a price.** For every "wrong fit" verdict, state the alternative, what it would cost to move (files, ADRs, protocol bump, content), what it buys, and the cheapest step that tests the idea.
- Say what is healthy as well as what is broken. A section that only lists problems is not a fit assessment.

## Specialist agents (spawn all in parallel, `general-purpose`, each with its own brief)

Give each agent: the ground rules above, its scope, its questions, and the output format. Do not paste your whole prompt; paste the rules and its own section.

### A. Netcode and replication
Scope: `godot/ShooterCore/Net/*`, `godot/shooter/Net/NetServer*.cs`, `NetClient*.cs`, `NetSim.cs`, ADR 0002, the loss test.
Questions:
- Is "unreliable-sequenced quantised deltas with 30-tick keyframes" still right? The audit's open defect: deltas against the last *sent* state, not an acked one (T1-2). Size the fix and check what else depends on it.
- Bandwidth and CPU as a function of players, entities (vehicles, turrets, aircraft, animals, corpses, glass), and events. At what n does it stop fitting: 16, 32, 64? Is the lack of interest management a real cost, and where is the break-even?
- Channel layout: what rides the ordered reliable channel and what does head-of-line blocking do under loss? Event design (pellet fans, impacts, effects).
- Tick, send rate, interpolation delay, jitter buffer, clock sync. Join, leave, reconnect, and late-join full-state cost.
- Server-side anti-cheat surface that has a performance dimension (input rate limits, queue caps).

### B. Lag compensation and hit registration
Scope: `HitMath.cs`, `NetServer.cs` (shot resolution; its doc comment says "no lag compensation"), projectiles, pellets, melee, killstreak weapons.
Questions:
- Today hits resolve against **current** positions. At 50, 100, 200 ms RTT, what does that do to hitscan, shotguns, melee, and shots at moving vehicles? Quantify with the interpolation delay and target speeds (on foot, car, helicopter).
- Is rewinding a fit? Design the cheapest correct version: history ring per player/vehicle (memory per tick, per second), which tick the shooter saw (interp delay + latency), cap on rewind, what is rewound (players only, or also vehicles and hit boxes), interaction with cover, glass (ADR 0007) and corpses.
- Cost per shot and per tick; how it interacts with client-side prediction of the shooter's own hit feedback. Fairness for the victim, not only the shooter ("shot behind cover").
- Verdict: build it, defer it, or never; with the latency/speed threshold at which the answer flips.

### C. Players: movement, prediction and reconciliation
Scope: `Player.cs`, `Movement.cs`, `NetClient.Predict.cs`, `Interpolator.cs`, ADRs 0006 and 0010.
Questions:
- Is the shared `Player.Simulate` step truly deterministic on both sides (same layers, same collision, same float inputs)? Where can client and server disagree (the vehicle-box bug in `docs/vehicle-collision-and-entering.md` was one)? Cost of reconciliation: why 2205 reconciliations on a *clean* link in the loss test?
- Cost per tick of `MoveAndSlide` per player, per bot, per vehicle on the server; what scales with player count.
- Prediction cost and correctness for the driver and pilot (ADR 0006, 0010): is "driver's client predicts the vehicle" a good fit versus server-only vehicles with interpolation? Where does it fail (bumps, other vehicles, riders)?
- Remote player representation: interpolation, ragdoll/corpse flail, character animation cost.

### D. Vehicle models and collisions
Scope: `godot/ShooterCore/Vehicles/*`, `godot/ShooterCore/Aircraft/*`, `ServerCar.cs`, `VehicleCollider.cs`, `NetServer.Vehicles.cs`, `NetServer.ResolveVehicleContacts`, `data/vehicles.json`, glass ADR 0008.
Questions:
- Kinematic arcade drive/flight models with box colliders on a dedicated layer: right for 29 vehicles and the intended gameplay (ramming, run-over, riding, boats, amphibious, helicopters)? What does it fundamentally not support (stacking, towing, riding on a roof, slopes, ramps, terrain damage), and is that acceptable?
- Per-tick cost: 29 vehicles stepped, parked ones (T2-04), pairwise contacts (O(n²)?), run-over footprint tests against all players, per-pane glass nodes.
- Server box vs. client box vs. hit volume: one shape serving three purposes. Fit or liability?
- Determinism and prediction cost for the driver; bytes per vehicle per snapshot, parked and driven.

### E. World representation and physics world
Scope: `godot/MapLoader/*`, chunk GLBs and collision meshes, `godot/ShooterCore/Colliders`, `Environment`, `Water.cs`, `BotNav.cs`, ADR 0004, `docs/pipeline.md` for the *artifact* shape (not the pipeline's own speed).
Questions:
- Is "chunk GLBs with trimesh colliders, trees as bodies, one shared lamp body" the right world representation for the server (collision only) and the client (rendering)? Alternatives: heightfield terrain plus instanced props, merged static collision, a separate lightweight collision-only artifact for the server, a spatial grid for gameplay queries.
- Server memory and startup: it builds visual meshes and materials it never draws (T2-26). What is the minimum representation the server needs, and what does a dedicated collision artifact cost in pipeline work?
- Ray and overlap query cost against 399 trimeshes and 12k trees: shots, turrets, bots, vehicle ground probes. Broadphase behaviour; which queries run per tick and how many.
- Ground areas, parcels and replacement scenes (mods): does the representation survive a mod swapping a parcel's contents at runtime? Streaming and load hitches.
- Map version, memory and download size for players.

### F. Rendering and client frame
Scope: `godot/shooter/*Fx.cs`, `Hud*.cs`, `Minimap.cs`, `DayNightCycle.cs`, `shaders/`, `WaterFx.cs`, `CharacterFx.cs`, project settings.
Questions:
- Draw calls, shadow passes, visibility ranges, MSAA, Forward+ vs. Mobile, for 998 mesh nodes and 16 characters. CPU-bound (draw submission, node churn) or GPU-bound, and for which hardware floor? State the floor you assume and why.
- Effects: node churn per shot/blast/tracer (T2-15, T2-16, T2-17); which should be pooled, GPU particles, or MultiMesh. Distance gating.
- Which per-frame work belongs on a dirty flag (HUD strings, minimap, tactical map, day/night).
- Frame-time spikes (first use, map load, GC) rather than averages.
- Is a scene-tree-node-per-entity approach a fit for 12k trees, 110 lamps, glass panes, corpses and animals, or do these want MultiMesh/instancing and no nodes?

### G. Server game logic, tick budget and persistence
Scope: `NetServer.*` systems (turrets, killstreaks, bots, aircraft, wildlife, glass, corpses, care packages, UAV, missiles, MOAB), `ShooterServer.Persistence`, `Shooter.cs`, `deploy/`.
Questions:
- Build a **tick budget table**: for each system, per-tick work as a function of its entity count, and its worst case at 16 players with every killstreak active. Which systems are O(players × entities) and at what n do they dominate?
- Allocations and GC on the tick (LiveTargets, exclude arrays, encoded broadcasts, scratch buffers); the frame cap (T1-1); telemetry gaps (T2-07): what must exist to watch a live server?
- Bots: nav cost, ray cost, thinking rate.
- Persistence worker, queue depth, failure behaviour; container limits and GC settings.
- Scheduling: is everything on the main thread by design, and is there a clean place to move work off it (encode on a worker, Jolt threads)?

### H. Cross-cutting scale and growth ("will it still fit")
Read `docs/shooter-v2.md`, `docs/research/engine-comparison.md`, `docs/spikes/netcode-spike.md`, ADRs 0001 and 0002, the README and the issue tracker (`gh issue list`, repo Kobu-Labs/drycastle), to find what is *planned*: more players, mods (separate games on the base map: PvP shooter, DayZ-style survival, MMO RPG), bigger maps, more vehicles.
- For each planned direction, name the first subsystem to break and at what number.
- Does the one-server-per-mod, custom-ENet, Godot-headless stack suit a 16-player shooter *and* a persistent survival or MMO game? Where is the shared architecture an asset and where a constraint?
- Produce the "if we only had a week" list across all areas, and a list of things that look scary but are fine.

## Review pass (after the agents report)

Spawn three adversarial reviewers (fresh context each):
1. Re-read the code behind the **ten highest-ranked** findings and every "wrong fit" verdict; mark each upheld, downgraded or rejected, with the reason.
2. Re-read a random 25 % sample of the remaining findings and repair citations.
3. Hunt for gaps: threading, startup, logging, shutdown, mod boundary, anything no agent owned. Also check the agents did not contradict each other (e.g. netcode wants interest management while the world agent assumes full state).

## Output

Each agent writes its report to `docs/perf-investigation/<letter>-<area>.md` and returns **at most 300 words** to you: the fit verdict, the top three findings, and anything it needs from another agent. Report format:

1. **Verdict**: `good fit` / `fit with changes` / `wrong fit`, one paragraph, with the load at which it stops fitting.
2. **What is healthy** (with citations).
3. **Findings**: id (`A-01`...), title, mechanism, citation, cost (numbers), fix, effort (S/M/L), confidence (`CONFIRMED`/`UNVERIFIED`/`OPINION`), and whether it was already in `docs/perf-audit.md` (give its id) or is new.
4. **Design alternatives considered**, each priced (see ground rules).
5. **Needs measurement**: what, why it cannot be settled statically, and the cheapest way to measure it.

You then write `docs/perf-investigation/README.md`:

1. **Answer first**: one paragraph per question (performance; architectural fit), then a table of subsystem × verdict.
2. **Top 10 actions** ranked by impact ÷ effort, each with owner area, effort, confidence, and whether it needs an ADR.
3. **Architecture decisions to make**: for each, the options, your recommendation, and the ADR it would supersede or add (titles only, in the style of `docs/adr/`; do not write them).
4. **Measurement plan (Phase 2)**: ordered, cheapest first, using what already exists (`make shooter-loss-test`, `--net-sim`, Godot monitors, `dotnet-counters`, the `map load phases` log line). State the exact command and the metric for each. **Do not run any of it**; ask the user which to run.
5. **Disagreements and open questions** between agents or with the audit, unresolved.
6. **Changes since the 2026-11-11 audit**: which findings are fixed, which are still open.

Finish by telling the user, in under 150 words: the answer to both questions, the three actions you would take first, and what you need them to decide or run.
