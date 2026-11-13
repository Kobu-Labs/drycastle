# H: cross-cutting scale and growth ("will it still fit")

Read-only. Nothing built, run, measured or changed. Terms follow `GLOSSARY.md` (base map, mod, parcel, ground area, player token). Builds on `docs/perf-audit.md` (2026-11-11, 159 findings); audit ids are cited, not re-derived. Where a number is derived, the arithmetic is shown. Status: CONFIRMED (read in code or doc, or a measured number quoted from a doc), UNVERIFIED (inferred or not measured), OPINION.

## 1. Verdict

**Fit with changes.** The stack (one headless Godot server process per mod, C# simulation kept free of Godot in `ShooterCore`, custom ENet snapshot layer, versioned base map artifact) fits the 16-player shooter that is built and planned: measured tick p50/p95 1.6/2.6 ms against a 33.3 ms budget, 4.24 kB/s per client at 16 bots, and a 2-vCore VPS target (`docs/release.md:88`). It stops fitting for the shooter at roughly 48 to 64 humans per server, and the first thing to break there is the main-thread send path, not bandwidth: the spike's custom layer at 64 clients (no game logic) already spends 28.6 ms of the 33.3 ms frame at p95 (86 %), with no interest management (H-01, H-02). For the planned survival mod it stops fitting at about 50 to 75 continuously moving entities visible to one client, because nothing filters by distance (H-09), and a persistent world has no storage at all (H-07). For an MMO it is a **wrong fit** as a single-process-per-mod design: one server caps at 32 peers by constant (`godot/shooter/Net/NetServer.cs:437`), and no directory, zoning or handoff exists. Nothing in the stack forbids zoning later; it is simply absent (H-05, alternative E). The shared architecture is the asset; the single-process, no-interest-filter, full-broadcast assumptions are the constraint.

## 2. What is healthy

- **Engine-free core.** 121 non-test files under `godot/ShooterCore` and none has `using Godot;` (grep, CONFIRMED). This is what makes the pure models (`FlightModel`, `DriveModel`, `Animal`) testable and shareable by client and server (ADR 0006 `docs/adr/0006-the-pilots-client-predicts-the-utility-helicopter.md`, ADR 0010 `docs/adr/0010-the-drivers-client-predicts-the-amphibious-transport.md`, ADR 0003 "ShooterCore stays scene-free and database-free").
- **Measured headroom at the design target.** Server tick p50/p95/p99 1.59/2.55/3.20 ms at 16 bots, 30.00 Hz, 9 % of one core (`docs/spikes/shooter-loss-test.md`, results table). Snapshot bytes 4.24 kB/s down, 1.12 kB/s up per client (same table).
- **Bandwidth formula is simple and linear per client.** Each moving entity costs 14 B per tick (perf-audit T2-08 citation) = 0.42 kB/s per client per entity at 30 Hz.
- **Snapshot packets bounded.** `MaxEntitiesPerPacket = 40` keeps each packet under the 1,400 B buffer at any entity count (`godot/ShooterCore/Net/Snapshot.cs:48`, `:116-124`; audit section 7).
- **Custom layer chosen on evidence.** The spike measured built-in replication at 57 ms per frame at 64 clients (quadratic, 3.3/13.7/57 ms at 16/32/64) against 7.7 ms p50 custom; it also found a silent 64-property cap (`docs/spikes/netcode-spike.md`, Results and the #103938 section). ADR 0002 records the decision and its reason (`docs/adr/0002-custom-enet-snapshot-layer-over-builtin-sync.md`).
- **Persistence seam.** Repository interfaces with SQLite and PostgreSQL backends behind them, same contract tests on both (ADR 0003 addendum; `godot/ShooterServer.Persistence/Interfaces.cs`). A world-state store would be one more repository, not a rewrite.
- **Data-driven content.** Vehicles, animals, weapons and killstreaks are JSON files with versions (`godot/shooter/data/*.json`, ADR 0003 "Data files"). New kinds are data, not code, as long as they reuse an existing model.
- **Pure-model pattern for new vehicle physics.** ADR 0010 gives a second driving model behind an interface and states how a later car migrates by data (`docs/adr/0010-...`, Consequences).

## 3. Findings

Status and "in audit" column: "new" means not in `docs/perf-audit.md`; otherwise the audit id.

### H-01 No interest management: full broadcast sets the bandwidth and send-loop growth

- **Mechanism.** `SendSnapshots` puts every joined player, every vehicle and every extra record into every client's packet, and `BroadcastEvent` sends to every client (`godot/shooter/Net/NetServer.cs:905-917`, `:394-398`). No distance filter exists in `godot/shooter/Net` or `godot/ShooterCore/Net` (grep for "interest" and "radius": no hits, CONFIRMED). ADR 0002 names "optional radius interest management" as part of the layer (`docs/adr/0002-...`); the code does not have it, so the ADR overstates readiness rather than contradicting the code.
- **Numbers.** Calibrated per client: 4.24 kB/s at 16 bots (loss test). Spike, custom layer, movement only, no interest: 3.65 kB/s per client at 16, 12.79 at 64 (roughly linear per client, total quadratic); with a 50 m interest radius 2.83 kB/s at 64, a 78 % cut (`docs/spikes/netcode-spike.md`, N = 64 table). Linear projection for the shooter (UNVERIFIED beyond the measured point): 32 humans 8.5 kB/s per client, 272 kB/s total; 64 humans 17 kB/s, 1.1 MB/s total; 100 players 26.5 kB/s, 2.65 MB/s. Bandwidth alone is not the break at these numbers (a 500 Mbit/s netcup VPS is not the limit); the send loop is (H-02).
- **Cost.** Per client: O(N x E) bytes, O(N x E) encode work on the main thread; total O(N^2) for players.
- **Fix.** Per-client interest set by distance with hysteresis; always include the client's own vehicle and streak entities; far entities at 10 Hz; distance-cull Shot, impact and gunship effects (audit T2-08, issue #165).
- **Effort.** L for the full set (audit); the effects-only slice is S to M.
- **Confidence.** CONFIRMED (absence and spike numbers). Projection UNVERIFIED.
- **In audit.** T2-08 (medium at 16; "high beyond 32"). This report adds the growth numbers and the planned-voice and wildlife consumers (H-09, H-14).

### H-02 The main-thread send path, not the tick, sets the per-server budget

- **Mechanism.** The spike's frame busy time (all main-thread work per frame) grows with clients, while tick work does not. Custom layer, no game logic: frame busy p95 9.7 ms at 16 clients, 28.6 ms at 64; tick work p50/p99 3.87/6.40 ms at 64 (`docs/spikes/netcode-spike.md`, N = 64 table). The difference is per-client encode, poll and send work on the same thread (`godot/shooter/Net/NetServer.cs:716-723` per client, allocations per encoder per client per tick, audit T2-03, T2-10).
- **Numbers.** 28.6 / 33.3 = 86 % of the frame budget at p95 at 64 clients, before any game logic (CONFIRMED, localhost, laptop). The spike also reports about one hitch per second of 22 to 60 ms at N >= 32, not explained (spike, "Notes"). Server-side frame timing does not exist on a live server (audit T2-07).
- **Cost.** O(N x E) per frame on one thread; the 64-client case uses most of one frame.
- **Fix.** Reusable buffers and single broadcast encode (audit T2-03, T2-10, both S); then H-01. Explain the hitches before tuning.
- **Effort.** S for the allocation fixes; the hitch attribution is S once telemetry exists (audit T2-07).
- **Confidence.** Numbers CONFIRMED (spike). Attribution to the send loop OPINION until the hitches are explained.
- **In audit.** New as a scaling claim; the allocations are T2-03 and T2-10.

### H-03 A snapshot tick is all-or-nothing across its packets, so loss compounds with entity count

- **Mechanism.** The server splits a tick into `ceil(E / 40)` packets (`godot/ShooterCore/Net/Snapshot.cs:116-124`). The client commits a tick only when all of its packets have arrived; a partial tick is discarded (`godot/ShooterCore/Net/Snapshot.cs:183-185`). Snapshots ride the unreliable channel (`NetServer.cs`, audit T2-09 and NET-03 on the loss behaviour).
- **Arithmetic.** Let p be the per-packet loss and k the packets per tick. P(tick commits) = (1 - p)^k.
  - Design target, 16 humans + 32 vehicles + about 4 extra records (flak, helicopter flight, amphibious drive, door gun) = about 52 entities: k = 2. At p = 3 %: 94.1 %; at p = 10 %: 81 %.
  - Built and allowed: 32 humans + 64 bots + 32 vehicles + about 4 records = about 132 entities: k = 4. At p = 3 %: 88.5 %; at p = 10 %: 65.6 %.
- **Measured context.** Under 100 ms RTT and 3 % loss, 15.4 % of decoded player states were wrong against the server, longest wrong run 86 ticks (`docs/spikes/shooter-loss-test.md`). That is the audit's NET-03 defect (deltas against last sent, not acked); H-03 is the reason the damage grows with entity count, which the loss test (16 bots, few vehicles) did not exercise.
- **Cost.** Probability of a whole tick being lost grows with entities/40; each lost tick forces a stale or missed view until the next keyframe (`Wire.KeyEvery = 30`, `godot/ShooterCore/Net/Wire.cs:186`).
- **Fix.** Either commit each packet independently against its own baseline (the decoder already applies records to a copy of the baseline; per-packet ack instead of per-tick), which is a protocol change; or reduce entities per tick (H-01).
- **Effort.** M, protocol bump (ack per packet).
- **Confidence.** Arithmetic CONFIRMED from code. That loss grows with k in practice UNVERIFIED (loss test not run at 32 vehicles).
- **In audit.** New. Related: T1-2 / NET-03, and the audit's healthy item on the 40-entity cap, which covers MTU only, not loss.

### H-04 Vehicles are the largest growth in entities and per-tick physics; the spawn count is 32, not 29

- **Mechanism.** Each vehicle is a `ServerCar` that runs `MoveAndSlide` every tick unless destroyed (audit T2-04; `godot/shooter/ServerCar.cs:121-145`). Each adds a vehicle record and, for flak, flight, drive and door-gun vehicles, one more record (`godot/shooter/Net/NetServer.cs:912-916`). Admin `/spawn vehicle` adds up to 32 per run and spawned vehicles are not removed (`docs/shooter-v2.md:512`).
- **Numbers.** `godot/shooter/data/vehicles.json` `spawn_count` sums to 32: car 12, motorcycle 3, bicycle 3, jeep 3, quad 4, amphibious 2, boat 2, flak 1, bus 1, utility helicopter 1 (computed from the file). The audit's 29 (`docs/perf-audit.md` section 1) is out of date. At 14 B per moving record and 30 Hz, 32 vehicles moving cost 13.4 kB/s per client, about twice the 16 players' 6.7 kB/s (arithmetic; vehicle record size UNVERIFIED).
- **Cost.** Per tick: one `MoveAndSlide` per vehicle (cost per move UNVERIFIED); per client: about 13 kB/s if all move.
- **Fix.** Park driverless cars at rest (audit T2-04, M). For new vehicle kinds, follow ADR 0010's pure model when the physics is new, so the cost is data-defined.
- **Effort.** M (T2-04); M per vehicle class (ADR 0010 path).
- **Confidence.** Counts CONFIRMED. Cost per move UNVERIFIED.
- **In audit.** T2-04 (count discrepancy new).

### H-05 One process, a hard 32-peer constant, and bots counted as entities but not as peers

- **Mechanism.** `MaxPeers = 32` is a `const` (`godot/shooter/Net/NetServer.cs:437`) and sets the ENet host (`:146`, `MaxPeers + 1`) and the full check (`:335`). Bots need no peer (`godot/shooter/Net/NetServer.Bots.cs:30-31`, `:178`) but are players in `SendSnapshots` (`NetServer.cs:908`), and `MaxBots = 64` (`godot/ShooterCore/Chat/ChatCommands.cs:74`).
- **Numbers.** 32 humans + 64 bots = 96 player entities per tick, plus about 36 vehicle and extra records: about 132 entities, four packets per client per tick (H-03).
- **Cost.** A configuration that is one `const` away from a 16-player target.
- **Fix.** Make `MaxPeers` a server option (CLI flag, default 32) and count bots separately in telemetry. The real MMO fix is zoning (alternative E).
- **Effort.** S.
- **Confidence.** CONFIRMED.
- **In audit.** New (the peer count is in T3-020 as "no server full message").

### H-06 Map scale is linear in area and the server holds all of it; memory is unmeasured on the 4 GB target

- **Mechanism.** Village extent about 4 km x 7 km (`docs/research/slovak-open-data.md:5`, `:57`, "KU is about 4 x 7 km"). The map is 48 chunk GLBs at 44 MB, with 998 visual mesh nodes and 399 collision nodes (perf-audit section 1, measured). The headless server builds the visual meshes too (audit T2-26), and collision is 72,078 triangles. The bot navigation mesh is baked on the server and cached per map version (`godot/shooter/BotNav.cs:14-15`, "a few seconds for the village").
- **Numbers.** Doubling the area roughly doubles every one of these (chunks, triangles, bake time before cache). Server memory: UNVERIFIED. The production VPS is 2 vCores and 4 GB shared with PostgreSQL (`docs/release.md:88`, `deploy/docker-compose.yml`).
- **Cost.** Linear in area, per server process; a mod on a larger base map pays it in every process.
- **Fix.** Collision-only load on the headless server (issue #172, audit T2-26); chunk streaming is not needed for the shooter.
- **Effort.** M.
- **Confidence.** Scale CONFIRMED; memory and bake time UNVERIFIED.
- **In audit.** T2-26, T2-29; issue #172.

### H-07 Persistence covers profiles only; no world state exists, and the write queue is unbounded

- **Mechanism.** ADR 0003 scopes persistence to players, loadouts, unlocks and stats. World entities reset on `/restart` (`docs/shooter-v2.md:514`). Writes go to a `BlockingCollection` with no capacity bound (`godot/ShooterServer.Persistence/DbLane.cs:113`). Writes are fire-and-forget (`docs/shooter-v2.md:503`).
- **Numbers.** Current write rate is "a few per second" (audit, healthy section). A survival mod that persists inventories and placed bases is a new write class, UNVERIFIED in rate; at 16 players and one inventory change per 10 s per player, that is 1.6 writes per second, which Postgres handles (audit). At 100 players and bases changing per second, the unbounded queue is the risk, because nothing applies backpressure.
- **Cost.** A world-state subsystem (new tables, chunk-keyed snapshots, load on start) and a bounded queue.
- **Fix.** A world-state repository beside the progression one, chunk-keyed, with a bounded lane and a log on overflow.
- **Effort.** L (new subsystem; ADR needed).
- **Confidence.** CONFIRMED (absence, unbounded queue). Rates UNVERIFIED.
- **In audit.** Queue unboundedness new; the rest of persistence is healthy in audit section 7.

### H-08 Each player and each AI is a Godot node with a physics body; the per-entity cost of hundreds is unmeasured

- **Mechanism.** A player is a `CharacterBody3D` added to the scene tree at join (`godot/shooter/Player.cs:11`, `godot/shooter/Net/NetServer.cs:405-413`). Vehicles are `ServerCar` nodes. Bots are `Client` objects with a `Player` body too (`NetServer.Bots.cs:178`). The pure-model alternative exists (ADR 0006/0010; `FlightModel`, `DriveModel`, `Animal`).
- **Numbers.** Node and body costs per entity are UNVERIFIED. A survival mod with 200 to 300 zombies as bodies would be 200 to 300 Jolt bodies and nodes on the same main thread as the send loop (H-02).
- **Cost.** Per AI: one node, one body, one brain; plus a snapshot record and bandwidth (H-01).
- **Fix.** Pure models for AI (the `Animal` pattern: a `ShooterCore` class with a world interface), with a server batch and a per-AI record only inside interest.
- **Effort.** M per AI class (the `Animal` class already exists as the template).
- **Confidence.** Structure CONFIRMED; costs UNVERIFIED.
- **In audit.** New.

### H-09 Planned wildlife (#254) and survival AI break bandwidth first; a 20 kB/s budget is spent at about 50 to 75 moving entities

- **Mechanism.** Without interest management (H-01), every moving entity is sent to every client. Animals are world entities of type 50 (glossary "Animal"); the client draws them (`godot/shooter/Net/NetClient.Animals.cs:6-10`). The server-side spawner was not found: `new Animal(` has one hit, its definition (`godot/ShooterCore/Wildlife/Animal.cs:68`). Whether the server owns animals yet is UNVERIFIED. Issue #254 asks for habitat-based spawning and a cap on total animals, and leaves server-authoritative versus client ambience open.
- **Numbers.** Theoretical 0.42 kB/s per moving entity per client (14 B x 30 Hz); calibrated 0.27 kB/s (4.24 kB/s / 16 players). The 20 kB/s per-client budget is my assumption, not a repo value. At 0.42 kB/s, 48 moving entities exhaust it; at 0.27 kB/s, 75.
- **Cost.** Linear in entities times clients.
- **Fix.** Interest management (H-01), a species cap per area, and 10 Hz for far animals. Or client-side ambience with no snapshot cost (the issue's own alternative).
- **Effort.** Depends on H-01; client-only ambience is S.
- **Confidence.** Arithmetic CONFIRMED; budget is OPINION.
- **In audit.** New as a planned-direction break.

### H-10 Protocol and base-map build are lockstep; every protocol change forces every client to update at once

- **Mechanism.** The hello refuses a mismatched `ProtocolVersion` (`godot/ShooterCore/Net/Wire.cs:181`; `docs/shooter-v2.md:522`). The server and clients must load the same map build (`docs/shooter-server.md`; ADR 0003 "Consequences"; ADR 0004 "Consequences"). The protocol is at 24, with a bump for almost every feature since version 6 (`Wire.cs:180`).
- **Numbers.** 19 bumps from 6 to 24 in the history recorded in `Wire.cs:180` (count from the summary). For a live persistent game, each bump is a forced client update for everyone online.
- **Cost.** Operational: a version gate per release, and no rolling upgrade.
- **Fix.** Accept a version range with a compatibility shim for one step back; or treat the map as a separate download with its own hash (the map is about 44 MB of chunks, `audit section 1`).
- **Effort.** M; needs an ADR (the protocol policy is not written down).
- **Confidence.** CONFIRMED (refusal and bump history); impact OPINION.
- **In audit.** New.

### H-11 The engine decision's revisit trigger is still open, and the second mod was built before it

- **Mechanism.** ADR 0001 says to compare Godot against Bevy "once the base map v1 exists … before committing the second mod" (`docs/adr/0001-godot-csharp-for-mods-and-shooter-poc.md`). The shooter is built (protocol 24), and issue #20 still lists the Godot-vs-Bevy comparison as open. `docs/research/engine-comparison.md` recommends Godot and marks several cells unverified.
- **Cost of a switch.** ShooterCore (121 engine-free files) would port to any C# engine (Unity is the named fallback). The Godot-bound code (`Player`, `ServerCar`, `NetServer` scene glue, rendering, `NetClient`) would be rewritten: L to XL, UNVERIFIED size (not counted). Bevy: a Rust port of ShooterCore and no networking (ADR 0001 rejects it).
- **Fix.** Record the comparison result or supersede ADR 0001 explicitly. No code change.
- **Effort.** S (documentation decision).
- **Confidence.** CONFIRMED (trigger text and open issue); OPINION that the choice still holds.
- **In audit.** New (out of performance scope).

### H-12 Planned voice (#95) needs interest management before it can ship

- **Mechanism.** Proximity voice means every speaker's stream goes to every listener in range. Issue #95 proposes reusing the radius interest of H-01, which does not exist.
- **Numbers.** Codec bitrate is an assumption (Opus at about 16 kbps = 2 kB/s per speaker per listener, not in the repo). With 16 speakers and no filter, one listener receives up to 32 kB/s; server egress 16 x 15 x 2 = 480 kB/s when everyone talks.
- **Cost.** Voice is a per-listener fan-out of the same shape as H-01, on the same connection.
- **Fix.** Land H-01 first; voice uses the same interest set. A relay-only server (no decode) is what the issue asks for and costs no CPU.
- **Effort.** Depends on H-01; relay is M.
- **Confidence.** Fan-out CONFIRMED from the issue text; bitrate UNVERIFIED.
- **In audit.** New.

### H-13 No server telemetry, so none of the growth numbers can be watched on a live server

- **Mechanism.** Tick timing exists only in loss-test mode; bytes are not counted (audit T2-07, citing `godot/shooter/Net/NetServer.LossTest.cs:77-86` and `NetServer.cs:386-389`).
- **Cost.** Every scale claim in this report is either a loss-test number on a laptop or UNVERIFIED on the VPS.
- **Fix.** One line every 10 s: tick p50/p99, bytes per client, entity count, queue depth (audit T2-07).
- **Effort.** S. It is the first item of the week list.
- **Confidence.** CONFIRMED.
- **In audit.** T2-07.

### H-14 Targets for the one-week list depend on machine, not code: the 1.6 ms tick is a laptop number

- **Mechanism.** The loss test ran on an i7-11850H (8 cores / 16 threads), shared with other agents (`docs/spikes/shooter-loss-test.md`, Machine). The target is a 2-vCore VPS (`docs/release.md:88`).
- **Cost.** Headroom on the VPS is unknown. If Godot's physics and the send loop use one core and Jolt uses others, the VPS may run the tick at a multiple of the laptop number.
- **Fix.** Measure on the target VM (section 5).
- **Effort.** S (run existing loss test on the VPS).
- **Confidence.** CONFIRMED (hardware mismatch); size UNVERIFIED.
- **In audit.** New.

## 4. Design alternatives considered (each priced)

| # | Alternative | Files and ADRs | Protocol | Cost | Buys | Cheapest test |
|---|---|---|---|---|---|---|
| A | Keep stack; add per-client interest set (#165, T2-08) | `NetServer.cs:905-917`, `Snapshot.cs`, `WorldReplication.cs`; ADR 0002 already lists it as optional | Likely none if removal-on-leave-interest reuses the existing removal records (v17); far 10 Hz cadence may need a note | L (audit) | About 78 % per-client bytes at 64 (spike); keeps the send loop from growing with the square of players | Port the spike's 50 m filter to a branch, loss test at 32 and 64 bots |
| B | Third, unreliable channel for effects (T2-09) | `Wire.cs` channel layout, `NetServer.cs:391-398`, `NetServer.Aircraft.cs:194-196` | Bump (channel layout) | M | Removes head-of-line blocking of Hit and Kill behind shots | Loss test at 10 % with automatic fire; time Hit delivery |
| C | Per-packet independent snapshot commit (H-03) | `Snapshot.cs:116-124`, `:183-185`; ack in input | Bump (ack per packet, 2 bytes or more) | M | Loss cost no longer grows with k | Loss test at 3 and 10 % with E = 40, 80, 120 |
| D | Pure models for AI and animals (ADR 0006/0010 pattern) | `ShooterCore/Wildlife/Animal.cs` as template; server batch | None if records reuse an entity type | M per AI class | No node or body per AI; AI cost in the tick, not the scene tree | Headless microbench: 300 bodies against 300 pure models, tick time |
| E | Zoning: one process per zone, directory and gateway service, client handoff | New service in `deploy/docker-compose.yml`; new ADR; `NetServer` redirect message | Bump (redirect message) | XL, no code exists | An MMO world: 1,000 players as many zones of 32 | Two processes on one map, scripted client handoff |
| F | Switch engine to Unity (C# fallback in ADR 0001) | ShooterCore ports (121 files); Godot-bound code rewritten; ADR 0001 superseded | Full new protocol or port of Wire | L to XL, UNVERIFIED size | Unity dedicated-server target (`docs/research/engine-comparison.md`, UNVERIFIED netcode details) | Not recommended now; the Unity netcode question is open in the research doc |
| G | Bevy (Rust) | Port of ShooterCore to Rust; no networking library in use | Full new protocol | XL | Lean server (research doc) | Rejected by ADR 0001 and the research doc; no test proposed |
| H | Revert to built-in multiplayer | About 350 lines of wire code removed (ADR 0002) | Full | M | Less code to own | Rejected: spike shows 57 ms per frame at 64 |
| I | Unreal (C++ source build) | Full rewrite | Full | XL | Replication graph | Rejected by ADR 0001 and the research doc |

Notes. A is the cheapest move that changes the 64-client picture. B and C each need a protocol bump, so they should land in one bump with A. D is the alternative that makes survival AI affordable. E is the only one that answers MMO; it is not a near-term item and it needs an ADR before any code. F, G and I are rejected or deferred by existing ADRs; the only one with a live argument is F, and it costs more than the problem it solves at 16 to 64 players.

## 5. Planned directions: the first subsystem to break, and at what number

| Direction (source) | First break | Number (derived) | Status |
|---|---|---|---|
| 16 players, shooter (#1, shooter v2) | Nothing yet | 1.6/2.6 ms tick; 4.24 kB/s per client; 2 packets per tick (H-03) | CONFIRMED (laptop) |
| 32 humans (ADR 0002 "replace beyond roughly 32") | Snapshot loss (H-03) and per-client bytes | 8.5 kB/s per client projected; 0.88 to 0.94 tick completion at 2 to 3 % loss | Projection UNVERIFIED |
| 64 humans (spike scale) | Main-thread send loop (H-02) | 28.6 ms p95 of 33.3 ms without game logic | CONFIRMED (spike) |
| 96 to 132 entities (64 bots + 32 humans) | Peer count is not the limit, packets and send loop are (H-05) | 4 packets per tick; 65.6 % tick completion at 10 % loss | Arithmetic CONFIRMED |
| More vehicles (#214 and the vehicle backlog) | Per-tick MoveAndSlide, then bytes (H-04) | 32 today (data); 13.4 kB/s per client if all move | Count CONFIRMED; cost UNVERIFIED |
| Wildlife (#254) and survival AI | Bandwidth without interest (H-09) | 48 to 75 moving entities at a 20 kB/s budget (assumed) | Arithmetic CONFIRMED |
| Proximity voice (#95) | Fan-out without interest (H-12) | 32 kB/s per listener with 16 speakers at an assumed 2 kB/s each | Assumption UNVERIFIED |
| Bigger base map (2x area) | Server memory on 4 GB (H-06) | Everything linear in area; server RSS UNVERIFIED | UNVERIFIED |
| Survival: persistent world and bases (planned as a mod) | World-state storage (H-07) | Not present; queue unbounded | CONFIRMED absence |
| MMO (mod, later per #1) | One process per world (H-05, E) | 32-peer constant, 64 bots; a 1,000-player world is at least 32 zones | CONFIRMED constant |
| Team modes and objectives (#214) | None at scale | Per match; no per-tick cost measured | OPINION |
| Lag compensation (#198) | Agent B's area; the history cost is per player per tick | Not sized here | Not in scope |

## 6. The "one week" list

Ordered by value per day, each with its cost and the finding it moves.

1. **Frame cap and telemetry** (audit T1-1, T2-07; H-13). Day 1. Needed before any other number can be trusted on the server.
2. **Allocation cuts on the send path** (audit T2-03, T2-10; H-02). One to two days. Reduces the 86 % frame-budget figure without changing protocol.
3. **Make MaxPeers an option; count bots separately** (H-05). Under an hour.
4. **Park driverless vehicles** (audit T2-04; H-04). Two days. Removes most of the per-tick MoveAndSlide cost at the 32-vehicle count.
5. **Distance culling of effects and 10 Hz for far entities** (H-01 part, audit T2-08 effects slice; H-09). Two to three days. Cuts the send volume without a protocol bump if records are reused.
6. **Measure, no code**: the plan in section 7 on the VPS and the laptop at 16, 32 and 64 bots.

Total: about one week of implementation plus machine time. Items 5 and 6 are the ones that decide whether the shooter fits at 32 or 64.

## 7. Things that look scary but are fine

- **12,175 trees, 1,165 parcels.** Merged per parcel and per chunk, not nodes (audit section 7, "Rendering"; audit section 1 theme 3 is about 998 visual nodes, which is client-side).
- **998 visual nodes and 689,649 triangles.** Client cost only; the server does not draw. Scary for the client, outside this scope.
- **Built-in multiplayer's quadratic cost.** Real, but the custom layer already replaced it (ADR 0002; spike).
- **Unexplained 22 to 60 ms hitches at N >= 32** (spike). Off the tick path (tick work p99 6.4 ms at 64). It needs attribution (section 5), not redesign.
- **4.24 kB/s per client.** Small. Bandwidth is not the first constraint at 16.
- **Protocol at 24.** Many bumps look bad, but for a hobby deployment they are cheap; the lockstep risk is H-10 only once players are persistent.
- **Persistence write rate.** A few per second; PostgreSQL defaults adequate (audit section 7).
- **Server tick at 2.6 ms p95.** Nine times under the frame budget at 16 bots, laptop (H-14 for the VPS).
- **Jolt collision on 399 static trimesh nodes.** Static, built once per map load; no per-tick cost (audit section 7 "Physics").
- **The 32-peer cap.** Two times the design target; fine until the MMO question (H-05).
- **The loss test's 15.4 % wrong decoded states.** Scary and real, but it is a correctness defect owned by the netcode agent (audit NET-03 / T1-2), not a growth limit; H-03 is the part that grows.
- **Missing frame cap** (audit T1-1). Looks like a runaway process; it is a one-line fix and has no scale consequence once set.

## 8. Needs measurement

Each item is a plan, not an action. Ordered by how much it changes the verdict.

1. **Send-loop breakdown at 32 and 64 humans** (H-02). Split frame busy into poll, encode and send, and count the hitches. Cheapest: the spike's custom variant with timers around the three phases; no game code. Why not static: the split is a runtime ratio.
2. **Packet-count loss sweep** (H-03). Loss test at 3 % and 10 % with three entity counts (about 40, 80, 120), vehicles moving. Cheapest: `make shooter-loss-test` with `SCENARIOS` and `BOTS`, plus a vehicle set. Why not static: needs the actual loss pattern.
3. **Per-vehicle move cost and parked cost** (H-04; audit T2-04). Tick time with 32 parked vehicles against none, in loss-test mode. Why not static: `MoveAndSlide` cost depends on the scene.
4. **Snapshot bytes with 32 vehicles moving** (H-04). Loss test with vehicles driven; compare against the 6.7 kB/s (players only) and 13.4 kB/s (vehicles) arithmetic.
5. **Server resident memory on the VPS spec with the full village map, with and without visual loading** (H-06; audit T2-26). Cheapest: `docker stats` on a 4 GB VM with PostgreSQL running, once with the flag and once without.
6. **Per-entity cost of AI nodes against pure models** (H-08). Headless microbench: 300 `CharacterBody3D` with Jolt bodies against 300 `Animal`-style models; tick time. Why not static: the cost is in the engine.
7. **Bake time and bot navigation tick cost vs area** (H-06). Time the cached bake on the village and a synthetic 2x area; time the bot tick with 64 bots. Cheapest: the existing `BotNav` log.
8. **Loss test on the target VM at 16 bots** (H-14). Cheapest: run the existing loss test on the 2-vCore VPS. This decides whether the 16-player verdict holds.
9. **Voice bitrate** (H-12). Only once voice is built; not testable now.
10. **Hitch attribution at N >= 32** (section 2 and section 7). Cheapest: a Godot profiler capture during a 32-client spike run. Why not static: the hitches are not reproducible from code reading.

No measurement in this list needs a screenshot or a running client in the editor.

## 9. Cross-agent needs

- Agent A (netcode): confirm H-03 at higher entity counts and whether NET-03 and H-03 share one fix.
- Agent B (lag compensation): cost of history per player per tick at 64 bots.
- Agent D (vehicles): cost of `MoveAndSlide` per parked and moving vehicle; whether a parked skip breaks any rider or collision rule.
- Agent G (server logic): whether the server spawns animals (H-09) and where.
- Agent F (rendering): nothing blocking; the client is out of this scope.
