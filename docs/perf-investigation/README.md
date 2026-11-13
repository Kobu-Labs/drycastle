# Performance and architecture-fit investigation

Branch `areas-not-parcels`, 2026-11-13. Read-only: nothing was built, run or measured. Eight specialist reports sit beside
this file (`A-netcode.md` ... `H-growth.md`; C's was transcribed by the lead because its agent could not write files).
Builds on `docs/perf-audit.md` (2026-11-11).

**How the review pass was done (a deviation from the brief).** The brief asks for three adversarial reviewer agents. You
declined those launches, so the lead did a narrower pass itself. Re-read in code and **upheld**:
(1) entity snapshots delta against the client's acked tick (`Snapshot.cs:7-11, 40-44, 65-68`);
(2) the world channel is still last-sent with a 30-tick keyframe (`WorldReplication.cs:110-123`);
(3) `MaxPeers = 32` (`NetServer.cs:437`);
(4) `OnHello` compares only the protocol version (`NetServer.cs:383-388`);
(5) driverless cars still run `ServerCar.Step` and `MoveAndSlide` every tick, with no early-out
(`NetServer.Vehicles.cs:362-367`, `ServerCar.cs:285-303`);
(6) rocket life is `range/speed + 0.5 s` (`RocketFlight.cs:19-22`);
(7) the frame cap exists (`Shooter.cs:421`), so audit T1-1 is fixed and H's one-week list is stale on it;
(8) `Player` sets no `CollisionLayer` (`Player.cs`, only masks at :39, :66), so C-01's configuration claim holds.
**Not re-read:** B-02's 6.7-8.9 m figures, E's triangle counts, F's draw counts, D's byte sizes, G-02's rocket
population, and everything that needs an engine measurement (Jolt kinematic-vs-kinematic, `IntersectRay` cost,
`MoveAndSlide` cost). Those keep the specialists' own confidence tags. Sections 5 and 6 list what the sample and the
cross-check found.

**Map build caveat.** The prompt's scale facts (48 chunks / 44 MB / 998 visual nodes / 12,175 trees) match no map on disk.
Worktree `out/village/map`: 145 chunks, 231 MB (F: 1,959 visual nodes, 58,370 trees). Main checkout `out/village/map`: 41 chunks,
87 MB (E: 20,824 trees). Any number below that depends on map size is **build-dependent**; per-entity and per-tick figures
are not. Please name the reference build (section 5).

---

## 1. Answer first

**Where does it spend time, memory and bandwidth; what is worth fixing?** At the 16-player design target the server is
comfortable: tick p50/p95 1.6/2.6 ms of 33.3 ms and 4.24 kB/s per client (measured, loss test, bots only; the bots never use
killstreaks or vehicles, G-08). The measured numbers say little about the load that matters, because that is combat with
killstreaks and moving vehicles. The cost that is certain and cheap to fix is: every driverless car is physics-stepped every tick
(about 870 moves/s, D-01; cost per move unmeasured, estimated 0.6-1.5 ms per tick, so possibly the biggest single
server cost); `LiveTargets` allocates a list per call (up to about 8,500 calls/s, G-01); gunship rockets fly 12.5 s and each
is a record to every client (G-02); shots and effects share the ordered reliable channel (A-03). On the client the binding
limit is render-thread draw submission, not triangles: one draw per map node, unpooled blast effects, 4x MSAA by default and
unbounded sun shadows (F-01, F-02, F-04..06). Bandwidth is not the first limit at 16; at 32 players it is the ceiling without
interest management (A: about 20 kB/s per client). 64 needs interest management plus effect culling (A-07, H-01).

**Is the architecture a good fit?** Yes, with changes, for the 16-player PvP shooter. Nothing is a "wrong fit" at that scale.
The authoritative, server-owned, custom-ENet design is right, and since commit `628eb7a` the entity channel is no longer the
defect the audit describes. Three design gaps remain that are not inefficiencies:
(1) **no lag compensation**: hits resolve at present positions while the shooter sees a picture that is `RTT + 100 ms` old, so a
walker is 0.75 m off at 50 ms RTT against a 0.56 m body radius, and cars at 44 m/s are 6.7-8.9 m off (B-01, B-02);
(2) **no interest management**, which ADR 0002 names as part of the layer (A-07, H-01);
(3) **ground-vehicle drivers are not predicted**, so every car feels about 100 ms late (D-02). Separately, one process per mod
with a constant 32-peer cap is a wrong fit for an MMO-style game (H-05; OPINION, since no zoning exists), though fine for the shooter.

| Subsystem | Verdict | Stops fitting at | Report |
|---|---|---|---|
| Netcode and replication | fit with changes | 32 players without interest management | A |
| Lag compensation / hit registration | fit with changes (build players-only rewind) | any latency for sprinters and cars; walkers from about 50 ms RTT; rewind itself stops fitting above about 150 ms RTT | B |
| Player movement and prediction | fit with changes | not shown; the 2205 clean-link reconciles are unexplained | C |
| Vehicle models and collisions | fit with changes | feel before count; contacts O(n²) only past about 150 vehicles | D |
| World representation and physics world | fit with changes | map area: about 3x the village (UNVERIFIED), via load time and node count (build-dependent) | E |
| Rendering and client frame | fit with changes | about 1,500-2,000 draws/frame on a GTX 1060-class floor | F |
| Server game logic, tick budget, persistence | fit with changes | about 32 players with all killstreaks active if `IntersectRay` costs near 30 µs (UNVERIFIED) | G |
| Growth: more players, mods, bigger maps | fit with changes; MMO-in-one-process is a wrong fit | about 48-64 humans (send loop); about 50-75 moving entities visible per client | H |

---

## 2. Top 10 actions (impact / effort)

| # | Action | Finding | Owner | Effort | Confidence | ADR? |
|---|---|---|---|---|---|---|
| 1 | Skip driverless at-rest cars with a wake list; drop the per-car LINQ `Occupants` | D-01, G-05 (audit T2-04) | Vehicles / server | S-M | path CONFIRMED, per-move cost UNVERIFIED | no |
| 2 | Pool `LiveTargets` lists; one enemy list per tick for turrets and helpers | G-01 (T3-001, T3-002) | Server logic | S | CONFIRMED | no |
| 3 | Players-only lag-compensation rewind, shadow-mode counter first; vehicles via analytic box test later | B-01, B-02 (B-03, B-05 follow) | Hit registration | S-M (players), M (vehicles) | mechanism CONFIRMED | **yes**, amends 0006/0010/0011/0012 wording, builds on 0002 |
| 4 | Cull gunship rockets at map bounds and ground; later give rockets distance interest | G-02 | Server logic | S, then M | arithmetic from `RocketFlight.cs:19-22` CONFIRMED, population UNVERIFIED | no |
| 5 | Map-build hash in Hello and Welcome (rides a protocol bump, 24 to 25) | E-06 | Netcode / world | S | CONFIRMED | no |
| 6 | Move shots, pellet fans, impacts and gunship FX to the unreliable channel; make `NetSim` drop reliable packets so the loss gate can see it | A-03, A-10, G-06 | Netcode | M | mechanism CONFIRMED, stall length UNVERIFIED | **yes**, channel layout (extends 0002) |
| 7 | Acked baseline for the world channel (aircraft, turrets, crates, corpses); same protocol bump as #5; also the scoreboard delta and the empty-packet floor | A-01, G-03, A-04, A-05 | Netcode | S-M | CONFIRMED | no (completes 0002) |
| 8 | Find the clean-link reconcile floor: give `Player` its own collision layer, count empty-input ticks, then re-run the loss test | C-01, C-02, A-08 | Prediction | S | cause UNVERIFIED | no |
| 9 | Client frame switches: no shadow cast from ground, a shadow max distance, 2x MSAA default, pooled and gated `RocketFx.Explode` | F-02, F-04, F-05, F-06 | Rendering | S-M | CONFIRMED mechanism, gain UNVERIFIED | no |
| 10 | Server watchability: phase timings in the stats line, GC counters, container memory and CPU limits, DB connection timeout | G-07, G-09, G-10 | Server ops | S | CONFIRMED | no |

Not in the top ten, on purpose: **interest management** (A-07/H-01, L) is the largest single lever but only pays above 16
players, so it is a decision (section 3); the **collision-only server artifact** (E-03, M) cuts server read from 79 MB to about
5 MB but only matters at startup and on small hosts; the **heightfield terrain** (E-01) has unverified gain.

---

## 3. Architecture decisions to make

Each is a recommendation; titles only, in the style of `docs/adr/`. None is written.

| Decision | Options | Recommendation | ADR |
|---|---|---|---|
| Lag compensation | never / players-only rewind / players and vehicles | Players-only in shadow mode (count would-be changes, change nothing), then enable; vehicles after, analytic oriented-box test on a rewound pose. Cap 250 ms | add "Shots resolve against rewound poses"; amends wording in 0006, 0010, 0011, 0012; 0002 already anticipates it |
| Interest management | none / effects-only / full spatial filter | Effects (blasts, rockets, tracers) first by distance, S-M; full filter only if the target goes above 32 humans (spike: 50 m filter cut 78%) | add "Snapshots are filtered by relevance" (adds to 0002) |
| Ground-vehicle prediction | keep server-lag / predict one more type through a data `drive` block / dead-reckoning | Try one car type via the data block ADR 0010 already allows; judge by feel before widening | add "Ground vehicles are predicted by their driver", extends 0010 |
| Effects channel | keep reliable / unreliable with a short reliable summary | Unreliable for effects; keep Hit, Kill and scoreboard reliable | add "Effects ride the unreliable channel" |
| Map identity at join | version only / hash | Hash | add "The handshake carries the map build" |
| Server world file | read the full GLB / collision-only file from the same arrays | Defer until startup or RAM is a measured problem (needs the 4 GB VPS RSS) | add "The server loads a collision-only map file" (touches 0004's artifact shape) |
| Terrain collision | trimesh / heightfield per chunk | Defer; gain unverified and the bunker chunks need the 4 m trimesh (ADR 0009) | none now |
| Client map nodes | per-parcel nodes / merged per chunk and material | Merge for the client, keep per-parcel nodes in the viewer for `ReplaceParcel` (E-05 is the related defect: trees have no parcel id) | add "Client merges static map nodes per chunk" |
| Process model for mods | one process per mod / zoned shard model | Keep one process per mod for the shooter and DayZ-style survival; do not plan an MMO on this stack without a zoning design | none now; revisit when an MMO is actually scheduled (H-11 revisit trigger) |

---

## 4. Measurement plan (Phase 2) — NOT run, tell me which

Cheapest first. Nothing here was executed.

1. **No run: read ENet's bundled source** for max packet size, MTU, RTO minimum and header sizes (A needs-measurement #8, settles A-03/A-05/A-09).
2. **No run: name the reference map build** (your decision, section 5).
3. **Re-baseline the loss gate on this branch**: `make shooter-loss-test MAP=out/village/map SCENARIOS=clean,rtt100,loss10 BOTS=16 MEASURE=30`. Metric: staleness in ticks (expect about 5-6, not the old 86), reconcile count per client, tick p50/p95, bytes per client. Output in `out/shooter-loss/<timestamp>/`. The old gate predates the baseline fix (A-02).
4. **Parked-car cost**: tick p50/p95 with the 29-32 parked vehicles against a run with `--vehicle-set` empty, same loss-test mode (D needs-measurement; G table). Metric: tick delta in ms. Settles action #1.
5. **Server startup and RSS**: one headless server on the reference map; read the `map load phases` log line and `ps -o rss`. Settles E-02/E-03 and the 4 GB VPS question (H-06).
6. **Idle and loaded container**: `docker stats --no-stream`; `docker compose stop -t 30 server` for the flush on stop (G-09, G-10).
7. **Jitter and render smoothness**: `--net-sim latency_ms=50,jitter_ms=20,loss=0.01` with a log of remote render step sizes (A-06, C-04).
8. **Per-kind bytes and the scoreboard**: counters on `Tx`, then a 16-bot walking run with `--match-bot` (A-04, A-10).
9. **Scale**: `make shooter-loss-test BOTS=32` then `BOTS=64`; record tick p95, send-loop share, egress per client (A, H-02). The spike's 28.6 ms busy p95 at 64 was spike code, not the shipping server.
10. **Needs a harness first**: all-killstreak tick and `IntersectRay` cost, with a Stopwatch wrapper and a synthetic load (G-07, G-08). This is the biggest unknown behind G's "4-13 ms" estimate.
11. **`dotnet-counters monitor System.Runtime`** (`alloc-rate`, `gen-0-gc-count`, `time-in-gc`) during #10 (G-01).
12. **Two kinematic `Player` bodies 1 m apart on the server**: does `MoveAndSlide` stop? Headless two-body scene (C needs-measurement #1; decides C-01).
13. **Client frame**: Godot monitors (draw calls, objects in frame, frame time) on the reference map, as laid out in F section 5. Needs a GPU; hardware floor is F's OPINION.

---

## 5. Disagreements and open questions (unresolved)

1. **Reference map build.** Prompt 48 chunks/44 MB/12,175 trees; worktree 145/231 MB/58,370; main checkout 41/87 MB/20,824. E and F, and any size number, depend on it. **Needs your decision.**
2. **Bandwidth at 64 players.** A: 40.2 kB/s per client (16 and 32 players: 10.7 and 20.3, effects included). H: 17 kB/s projected from the measured 4.24 kB/s at 16. They model different things; A includes effects and moving vehicles, H scales the idle-plus-bots measurement. G adds that rockets alone could add about 50-100 kB/s per client with many gunships. Not reconciled; measurement #9 and #10 decide it.
3. **Vehicle count.** Prompt and audit say 29; `data/vehicles.json` spawn counts sum to 32 (D-13, H-04). F counts 29 ground spawns in D-02. Treated as 32 spawned, 29 ground.
4. **H's one-week list and "looks scary" list are partly stale.** It treats T1-1 (frame cap) and T2-10 as open and the loss test's 15.4% wrong decoded states as a live defect. Verified fixed: T1-1 (`Shooter.cs:421`), and the entity T1-2 behind the 15.4% figure (`Snapshot.cs:40-44`; A-02). G also lists T2-10 closed. H's "998 visual nodes" is the old build. Use A and G for audit status; H's growth numbers stand.
5. **T2-07 telemetry**: A calls it fixed, G says partly closed with the gaps listed (G-07). Treat as partly closed.
6. **B-07 versus the T1-2 fix.** B says not to lower the 100 ms interpolation delay before T1-2 is fixed. T1-2 is fixed for entities, so the dependency now only applies to the world channel. The measured worst gap (117 ms at 3% loss) is loss-driven and still stands, so the advice to keep 100 ms stands too. C-04 (no jitter buffer) agrees.
7. **C-01 (players solid to each other on the server only)**: configuration confirmed, engine behaviour not. Nobody owned "does Jolt collide two kinematic bodies on one layer". E did not address it. A-08 (no step on empty input) is a second candidate for the clean-link reconciles. Measurement #12 and #7.
8. **`IntersectRay` cost** is G's key unknown and affects D, E (bot sight rays, E-07) and G's all-streak estimate.
9. **Whether the server spawns animals** (H asked G): not answered in either report.
10. **NET-03 and H-03** (all-or-nothing snapshot tick across packets): H asked A whether one fix covers both. Not answered.
11. **Loss-test bot aim** reads the newest decoded snapshot (`NetClient.LossBot.cs:172-190`, B), so it under-represents human lag by about 100 ms. Not independently re-read.
12. **ADR drift.** ADR 0004 says one body per water parcel; the code builds water from ground areas and regions (E-11). B says a rewind amends sentences in ADRs 0006, 0010, 0011 and 0012 ("from its own state") rather than contradicting a decision. No agent found a recommendation that contradicts an ADR.
13. **Per-agent limits.** C's report was transcribed by the lead, so nothing in it was independently re-read here except the layer claim. The reviewers' "ten highest-ranked" pass and the 25% random sample were not run as briefed (header). The sample you declined is the biggest gap in this review.

---

## 6. Changes since the 2026-11-11 audit

Verified in code by the specialists, not from the commit log. Where reports disagree it is marked.

**Fixed**
- T1-1 frame cap (`Shooter.cs:421`; checked by the lead).
- T1-2 for entity snapshots, commit `628eb7a` (#164, protocol 17): `Snapshot.cs` deltas against the acked tick (checked by the lead). T2-11's keyframe is gone for entities.
- T2-05, T2-10 (A, G). T2-01, T2-02 (partly, B), T2-06, T2-12, T2-36, T2-37, T2-39, T3-020, T3-021, T3-027 (G).
- T2-17, T2-20 step 1 (#171), T2-26/T3-021 headless visuals freed (F, E), T3-053 (F). T3-017 (C).
- T3-010 partly (capsule gated, head `TestMove` still per tick, C).

**Partly fixed**
- T2-07 telemetry (A: fixed; G: partly, gaps in G-07).
- T2-13, T2-14: no connect timeout (G-10).
- T2-15 tracers/sparks/impacts pooled; splash and tween remain (F).
- T2-26: visuals freed, materials and lamps skipped headless; the importer still builds them (E).
- T2-27 shape half done (E).

**Still open**
- T2-04 parked vehicles (D-01, G-05), T2-08 interest management (A-07, H-01), T2-09, T2-35 (G).
- T2-16 unpooled `RocketFx.Explode` (F-02), T2-21/T2-23/T3-061 shadows, MSAA, renderer (F).
- T2-28 terrain rim sampling, T2-29 map load all at start (E).
- T3-001, T3-002 (G-01), T3-005 (B-09), T3-063 terrain trimesh (E-01).
- T3-024, T3-025, T3-037, T3-046 open but negligible (C).

**Overturned or corrected**
- T2-11 does not apply to vehicles (D-05): parked costs 0 B, a moving car about 14 B/tick, about 420 B/s per client.
- The audit's `VehicleRules.cs:83-84` citation for T2-04 is wrong (D-13); the code is `NetServer.Vehicles.cs:362-367`.
- The loss-test doc and its `MAX_STALE_TICKS=75` gate describe the pre-fix defect (A-02).

**New findings** (not in the audit): A-01, A-03, A-04, A-05, A-06, A-08, A-09, A-10; B-01 to B-08, B-10; C-01 to C-05;
D-02 to D-12; E-03, E-05 to E-13; F-01, F-02 scale; G-01 to G-12; H-03, H-05, H-07, H-10, H-12.
