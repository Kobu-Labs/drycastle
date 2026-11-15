# G: Server game logic, tick budget and persistence

Read-only investigation, 2026-11-13, branch `areas-not-parcels` (HEAD `dc03c96`). Nothing was built, run or measured. Every number below is either a citation, arithmetic on a citation, or marked UNVERIFIED. Audit IDs refer to `docs/perf-audit.md` (2026-11-11, branch `perf-audit` at `ee90afd`). Many audit items have changed since then; section 3 says which.

Unit costs that the budget depends on are not in the repo. The main one is the cost of one `PhysicsDirectSpaceState.IntersectRay` call through Godot's C# binding, which I put at 5 to 30 microseconds (UNVERIFIED). Every "ms per tick" figure that depends on it is a range for that reason.

---

## 1. Verdict

**Fit with changes.** At the 16-player target the design is right: one authoritative 30 Hz tick on the main thread, running pure `ShooterCore` logic over Godot queries, with persistence on its own threads. The measured tick is p50 1.59 ms and p95 2.55 ms against a 33.3 ms budget (`docs/spikes/shooter-loss-test.md`, clean run, 16 bots, 29 parked cars, no killstreaks), so about 5 % of the budget is used. Two things stop the fit as load grows. (a) With every killstreak active, the tick is dominated by physics rays and per-call target lists whose count grows with players and with aircraft. (b) Rockets and the lack of interest management make downstream bytes quadratic in players. By my estimate the all-streak tick reaches the budget at about 32 players, the peer cap, if a ray costs near 30 microseconds (UNVERIFIED). Downstream fan-out is the tighter limit: at 32 clients with 32 attack helicopters, the rocket records alone are about 100 kB/s per client. Changes needed before raising the cap: G-01, G-02, G-03 and the measurements in section 5.

---

## 2. What is healthy

- **Measured headroom.** Tick p50 1.59 ms, p95 2.55 ms, p99 3.20 ms, 30.00 ticks/s, 9 % of one core (`docs/spikes/shooter-loss-test.md`, results table). The loss test runs the real map collision.
- **Frame cap and step cap are set for the dedicated server.** `Engine.MaxFps = 60` and `Engine.MaxPhysicsStepsPerFrame = 2` at `godot/shooter/Shooter.cs:421-422`. This closes audit T1-1 and most of T2-05.
- **Catch-up ticks do not send snapshots.** `CatchUpTick` (`godot/shooter/Net/NetServer.cs:183`, `:212`) skips the snapshot on a tick that starts right after the last one, so a slow frame does not add traffic.
- **Bounded inputs and receive per peer.** Input queue capped at 8 (`NetServer.cs:379`), `PacketsPerTick = 64` with a drop counter (`godot/shooter/Net/NetServer.Telemetry.cs:14`, `NetServer.cs:343`), and a token bucket on profile saves (`NetServer.Telemetry.cs:59-63`). Audit T2-06 is closed.
- **Turret re-pick is at 5 Hz.** `RepickIntervalS = 0.2f` (`godot/ShooterCore/Turrets/TurretEntity.cs:60`); the held target is checked with one ray per tick (`:128-135`). Audit T2-01 is closed.
- **Player and vehicle snapshots are acknowledged.** The input packet carries the snapshot tick (`godot/shooter/Net/NetClient.cs:527`); the server commits it only when it still holds that state (`NetServer.cs:371`); the encoder deltas against that baseline (`godot/ShooterCore/Net/Snapshot.cs:68-70`). A lost delta is repaired by the next tick, not the next keyframe. This closes audit T1-2 for players and vehicles (see G-03 for the world channel).
- **Broadcast encodes once.** `BroadcastEvent` encodes the bytes once and sends them to every recipient (`NetServer.cs:497-503`). Audit T2-10 is closed.
- **Shared baseline history.** One `SnapshotHistory` of 64 ticks serves every client (`Snapshot.cs:10-40`, `NetServer.cs:118`), so the per-tick dictionary is built once, not per client.
- **Player-ray exclusion list is cached.** `PlayerRids()` rebuilds only after a join or drop (`NetServer.cs:708-715`). Audit T2-02 is closed for the common path (`WorldHit`, `:722`).
- **Glass is event-driven.** Panes change only on a hit (`godot/shooter/Net/NetServer.Glass.cs:44-60`, flushed at `:153`), and there are 35 building panes (`out/village/map/windows.json`). Nothing runs per tick.
- **Vehicle contacts use a broad phase.** `ResolveVehicleContacts` checks all 406 pairs of 29 cars with a squared-distance test before any shape test (`godot/shooter/Net/NetServer.Vehicles.cs:399-410`).
- **Caps on the entity sets.** World entities are capped at 256 (`godot/ShooterCore/Net/WorldEntities.cs:84`, `:104`); corpses at 16, oldest first (`godot/ShooterCore/Corpses/Corpses.cs:13`, `:139`).
- **Flak is planned at fire, not per tick.** `FlakRules.Plan` marches shell and aircraft in 1/120 s steps once (`godot/ShooterCore/Vehicles/FlakRules.cs:24`, `:90`). A 2,500 m shell at 700 m/s is about 430 steps, roughly 20 microseconds per aircraft (UNVERIFIED cost).
- **Persistence is off the tick, with bounded queues.** Separate write and read lanes on their own threads (`godot/ShooterServer.Persistence/Sqlite/SqliteWorker.cs:24-28`, `Postgres/PostgresWorker.cs:23-30`); a write budget of 10,000 with a counted drop (`godot/ShooterServer.Persistence/DbLane.cs:42-70`); reconnect backoff from 1 s to 30 s (`DbLane.cs:75-100`); a shutdown join with a limit that logs abandoned jobs (`DbLane.cs`, `Join`). Audit T2-12, T2-14 (mostly), T3-027 are closed.
- **Telemetry exists.** One line every 10 s with tick p50 and p99, overruns, skipped snapshots, players, bots, bytes per client per second, entity count, database queue depth, dropped packets and rejected saves (`godot/shooter/Net/NetServer.Telemetry.cs:32-60`). Audit T2-07 is partly closed (gaps in G-07).
- **Deploy.** Log rotation (`deploy/docker-compose.yml:8-14`), `init: true` and `stop_grace_period: 30s` (`:39-40`), a healthcheck that waits for the map (`:51`, `Shooter.cs` ready marker), image prune in `deploy/deploy.sh:20`. Audit T2-36, T2-37, T2-39, T3-110 are closed.
- **Lamps are headless-safe.** `StreetLamps` turns processing off on the headless server (`godot/shooter/StreetLamps.cs:66`). Audit T3-021 is closed.
- **Connection cap is explicit.** One peer slot over `MaxPeers` (32) is kept to tell the 33rd connection it is full (`NetServer.cs:146`, `:335`). Audit T3-020 is closed.
- **Bots are bounded by design.** They use no vehicles and no killstreaks (`docs/shooter-server.md`, `--bots` row), one route per tick (`godot/shooter/Net/NetServer.Bots.cs:101-117`), and a 0.15 s think (`godot/ShooterCore/Bots/BotBrain.cs:34`).

---

## 3. Findings

### 3.0 Tick budget table

Per-tick work by system. "Worst" is 16 players, every killstreak active (each owner has the three streaks in use: turret, helper, attack helicopter, AC-130 and missile, so T=16, H=16, A=16 aircraft, M=16 missiles), unless stated. Rays are `IntersectRay` calls. LiveTargets is `NetServer.cs:684`, one list allocation per call.

| # | System | Per-tick work | Scales with | Worst at 16 players, all streaks | Note |
|---|---|---|---|---|---|
| 1 | Receive (`Poll`) | ENet service, decode, up to 64 packets per peer | P | ~16 to 40 decodes/tick | Healthy |
| 2 | Match update, scoreboard | O(P); scoreboard 1/s | P | ~0 rays | `MatchSim.cs:286` copies the player list each tick |
| 3 | World entities tick | Tick each entity, `_live.Keys.ToList()` | W (cap 256) | W ~100 | `WorldEntities.cs:133-140` |
| 4 | Bots (`--bots`, not default) | Look every 0.15 s, up to P-1 rays per bot | B x P | 16 bots: ~500 to 1,600 rays/s | `BotBrain.cs:161-168`, `:280-292` |
| 5 | Player simulation and shots | move, weapon, one LiveTargets + 1 to 3 rays per shot | P, shots/s | 160 shots/s: ~480 rays/s, 160 LiveTargets/s | `NetServer.cs:587`, `:743-800` |
| 6 | Turrets | held ray per tick; re-pick at 5 Hz over up to P-1 candidates; LiveTargets every tick | T x P | 480 + 1,200 = 1,680 rays/s; 480 LiveTargets/s | G-01 |
| 7 | Helicopter helpers | enemies list every tick; pick per 3-6 s cycle; ray per burst round | H x P | ~200 rays/s; 480 LiveTargets/s | `HelicopterHelperStreak.cs:100`, `NetServer.Helicopter.cs:63-65` |
| 8 | Aircraft cannon and AC-130 rounds | 1 to 2 rays + 2 LiveTargets + vehicle list per round | A (107/s cannon at A=16) | ~460 rays/s; ~500 LiveTargets/s | 6.7 rounds/s per gunship (`killstreaks.json`, interval 0.15 s) |
| 9 | Rockets in flight | 1 ray + 1 LiveTargets + sweep per rocket per tick; each rocket is a snapshot record for every client | R | 167 gunship rockets (10.4 alive per helicopter x 16) + 60 bazooka rockets: **5,000 + 1,800 rays/s, 6,900 LiveTargets/s** | G-02. Life 12.5 s for a gunship rocket (`RocketFlight.cs:19-22`, range 3,000 m at 250 m/s) |
| 10 | Attack missile | 1 ray + Targets() (LiveTargets) per tick | M | 480 rays/s; 480 LiveTargets/s | `AttackMissile.cs:231`, `NetServer.Missile.cs:69` |
| 11 | Corpses (thrown) | 1 ray per flying corpse per tick | min(16, deaths) | up to 480 rays/s | `NetServer.Corpses.cs:34-44` |
| 12 | Vehicles | 29 `car.Step` (MoveAndSlide), contact check 406 pairs, occupant lists | V | 870 moves/s, parked or not | G-05 |
| 13 | Flak | plan at fire: ~430 steps x A per shot, 4 shots/s per truck | shots x A | ~0.3 ms per shot at A=16 (UNVERIFIED) | `FlakRules.cs:24`, `:90` |
| 14 | Snapshot encode and send | per client: E record compares, ENet sends for changed records only, at least one packet | P x E, E = P + V + extras + R | 16 x ~(16+29+~5+167) = 3,800 compares/tick; packets ~48 to 60/tick plus worst of 2 to 6 per client from rockets | G-02, G-03 |
| 15 | Event broadcast | one encode, P ENet reliable sends per event | events x P | shots 160/s x 16 = 2,560 reliable sends/s | G-06 |
| 16 | Persistence, main-thread part | enqueue, TaskCompletionSource per write | writes/s | ~5 to 10 writes/s at 16 players (estimate from `NetServer.Progression.cs:57-64`, `:77`, `:83`) | G-10, G-11 |
| 17 | Telemetry | one list add per tick; sort every 10 s | 1 | ~0 | `NetServer.Telemetry.cs:32-60` |

**Totals, worst case, excluding bots.** About 10,500 rays per second (about 350 per tick) and about 8,500 LiveTargets calls per second (about 280 per tick). Each LiveTargets list holds 16 `HitTarget` structs of about 24 bytes plus a list header, so roughly 420 bytes per call, about 3.6 MB/s of short-lived allocation (UNVERIFIED GC cost).

**Estimated tick at 16 players, all streaks.** Base 1.6 ms (measured, no streaks) + 350 rays x 5 to 30 microseconds = 1.8 to 10.5 ms + LiveTargets 280 x 1 to 2 microseconds = 0.3 to 0.6 ms. Total p50 about 4 to 13 ms (UNVERIFIED; central about 7 ms). The 33.3 ms budget is not reached.

**Which systems are O(players x entities), and at what n they dominate.** Quadratic terms: turret re-pick (5 x P(P-1) rays/s), helper enemy lists and picks (about P(P-1)/4.5 rays/s), bots' Look (about 6.7 x B x (P-1) rays/s before filters), snapshot compares (P x E per tick), and event fan-out (events x P). Linear terms that scale with aircraft: rockets (about 320 rays/s per attack helicopter). At P=16 the rockets dominate; at P of about 24 to 32 the quadratic turret and helper terms overtake them (rough, UNVERIFIED). At 32 players with all streaks: about 23,000 rays/s (about 770 per tick), which is 11 to 23 ms at 15 to 30 microseconds per ray, plus the base. That is the point where the budget becomes tight.

**Downstream bytes.** A moving entity costs about 14 bytes per tick in a delta (4-byte header and 6-byte position delta, or more with rotation) (`godot/ShooterCore/Net/Snapshot.cs:130`, `WriteDelta`). Per client, all players and vehicles moving: (P + 29) x 14 x 30 = 19.0 kB/s at P=16, 25.6 kB/s at P=32. Egress: 304 kB/s at 16 clients, 818 kB/s (6.5 Mbit/s) at 32. A rocket is 10 bytes per tick (header plus position, no rotation change). With all-streak rockets: 167 rockets x 10 x 30 = 50 kB/s per client at 16; 333 rockets x 10 x 30 = 100 kB/s per client at 32. Reference: measured 4.24 kB/s per client at 16 bots (`docs/spikes/shooter-loss-test.md`).

---

### G-01. Live target lists are built per call, several hundred per tick

- **Mechanism.** `LiveTargets()` allocates a fresh `List<HitTarget>` each call, because callers nest (`NetServer.cs:684-694`, comment says a shared buffer would be cleared under them). Callers include every rocket every tick (`NetServer.cs:845`), every gunship round twice (`NetServer.Aircraft.cs:140`, `:166`), the missile per tick (`NetServer.Missile.cs:69`), turret enemies every tick per turret (`ShooterCore/Turrets/TurretEntity.cs:128`, `NetServer.Turrets.cs:52-54`), helper enemies every tick per helper (`NetServer.Helicopter.cs:63-65`, `HelicopterHelperStreak.cs:100`), and every shot (`NetServer.cs:756`). Turret and helper lists are also built with LINQ `Where().Select().ToList()`.
- **Citation.** `godot/shooter/Net/NetServer.cs:684-694`; callers as above; audit T3-001 (open), T3-002 (open, helper).
- **Cost.** About 8,500 calls per second at worst, about 280 per tick, about 3.6 MB/s (UNVERIFIED GC cost). At 16 bots with no streaks, about 160 calls per second (shots) = 67 kB/s.
- **Fix.** (1) Rent and return lists from a small pool (a stack of lists): the nesting problem goes away because each caller returns its list. (2) Build the turret and helper enemy lists once per tick, like `_botContacts` (`NetServer.Bots.cs:150-153`), and drop the per-turret LINQ. (3) Let the rocket loop reuse one per-tick list and check liveness per target, since StepRockets does not move players.
- **Effort.** S.
- **Confidence.** CONFIRMED for call counts (arithmetic on cited code). UNVERIFIED for GC cost.
- **Audit.** T3-001 (open), T3-002 (open). T2-02 is closed (see section 2).

### G-02. Gunship rockets fly 12.5 s and are sent to every client

- **Mechanism.** A gunship rocket's life is `range / speed + 0.5` = 3,000 / 250 + 0.5 = 12.5 s (`godot/ShooterCore/Weapons/RocketFlight.cs:19-22`, `killstreaks.json` gunship rocket: interval 1.2 s, range 3,000 m, speed 250 m/s). It ends only at a collider or the end of its life (`NetServer.cs:833-858`). Every rocket in flight is a snapshot record sent to every client each tick (`NetServer.cs:909-913`, `:924`) and costs one ray and one LiveTargets call per tick (`NetServer.cs:842-845`).
- **Arithmetic.** At 16 attack helicopters: 12.5 / 1.2 = 10.4 rockets alive per helicopter, so 167 alive. That is 167 rays and 167 LiveTargets calls per tick (5,000 rays/s), and 167 x 10 bytes x 30 = 50 kB/s per client. At 32: 333 rockets, 100 kB/s per client.
- **Citation.** `godot/ShooterCore/Weapons/RocketFlight.cs:19-22`; `godot/shooter/Net/NetServer.cs:833-858`, `:909-913`; `godot/shooter/data/killstreaks.json` (gunship rockets).
- **Fix.** (a) Expire a rocket when it leaves the map footprint or goes below the ground-level bound (`KillY`, `NetServer.cs` field) and cap life to the map diagonal over speed: S. (b) Send rockets only to clients within a radius, with the rest at 10 Hz or skipped (interest for rockets only): M. (c) Pool the per-tick target list (G-01).
- **Effort.** S for (a) and (c); M for (b).
- **Confidence.** CONFIRMED (arithmetic on cited constants). The map diagonal is not in this repo's code path; it must be read from the manifest (UNVERIFIED).
- **Audit.** New. Related to T2-08 (fan-out).

### G-03. The world channel is still last-sent with a 30-tick keyframe

- **Mechanism.** T1-2 is fixed for the player and vehicle encoder (`godot/ShooterCore/Net/Snapshot.cs:68-70`) but not for the world-entity encoder. `WorldSnapshotEncoder.Encode` diffs against what it last sent (`godot/ShooterCore/Net/WorldReplication.cs:95-130`) and keyframes every `Wire.KeyEvery` = 30 ticks (`:115`, `Wire.cs:186`). The server calls it without any acknowledgement (`NetServer.cs:928`). The world channel carries aircraft, turrets, missiles, crates and corpses.
- **Consequence.** A lost world delta is wrong until the next keyframe, up to 1 s (plus stagger), with no acknowledgement-based repair. The loss test's 86-tick bound (`docs/spikes/shooter-loss-test.md`, results table) was measured for players, before the acknowledged baseline existed; it has not been re-run.
- **Fix.** Give the world encoder the same acknowledgement baseline: the server reads the baseline from the input packet and keeps `SnapshotHistory` for world entities too; then drop the age keyframe to first-sight only (audit T2-11 for the world channel). This changes `WorldReplication.cs` on both sides and needs a protocol bump.
- **Effort.** M.
- **Confidence.** CONFIRMED (code path). Effect UNVERIFIED (loss test rerun needed).
- **Audit.** T1-2 (partly closed), T2-11 (open for world channel).

### G-04. No interest management, so downstream grows as P times (P + V)

- **Mechanism.** `SendSnapshots` sends every player, every vehicle, every rocket and every world entity to every client (`NetServer.cs:905-930`). `BroadcastEvent` sends every event to every client (`NetServer.cs:497-503`). ADR 0002 lists radius interest management as optional; the 64-client spike measured it at 78 % less downstream (`docs/spikes/netcode-spike.md`, "custom with interest 0.18 MB/s" vs 0.82 MB/s).
- **Cost.** See section 3.0 "Downstream bytes": 304 kB/s total at 16 clients with everything moving, 818 kB/s at 32; rockets add 50 to 100 kB/s per client at all-streak worst.
- **Fix.** Per-client interest set by distance with hysteresis, always including the client's own vehicle and streak entities; rockets and effects first (G-02b, G-06); far players and vehicles at 10 Hz.
- **Effort.** L for the full set; M for rockets and effects only.
- **Confidence.** CONFIRMED (no filter in code; `grep` for radius filters in `godot/shooter/Net/NetServer*.cs` and `godot/ShooterCore/Net` finds none). Benefit magnitude taken from the spike (CONFIRMED for the spike, UNVERIFIED for this game).
- **Audit.** T2-08 (open). The audit's "16 players x 14 B x 30 = 6.7 kB/s" figure is the players-only term; with vehicles and rockets it is larger (section 3.0).

### G-05. Parked vehicles are stepped every tick

- **Mechanism.** `StepVehicles` steps each live car every tick, including driverless cars at rest, through `car.Step` (`NetServer.Vehicles.cs:347-367`), which ends in `MoveAndSlide` (`godot/shooter/ServerCar.cs`, one call). The occupant list is LINQ per car per tick (`NetServer.Vehicles.cs:277`, `:375`).
- **Cost.** 29 moves per tick, 870 per second. Per-move cost UNVERIFIED. The encoder also compares 29 unchanged records per client per tick, which costs CPU but no bytes.
- **Fix.** Skip `Step` for a driverless car at rest at its origin; wake on seat change, damage or blast. Keep the abandon timer (`NetServer.Vehicles.cs:382`), fall check and detonation running. Replace the LINQ with a seat loop.
- **Effort.** M.
- **Confidence.** CONFIRMED (path). Cost UNVERIFIED.
- **Audit.** T2-04 (open).

### G-06. Shots and effects share the ordered reliable channel

- **Mechanism.** `BroadcastEvent` and `Send` use `ChanReliable` with `FlagReliable` (`NetServer.cs:493`, `:502`). Shot events (`NetServer.cs:776`, `:804`; `NetServer.DoorGun.cs:69`; `NetServer.Vehicles.cs:306`), pellet fans, impact puffs and gunship FX all go this way. One lost packet holds the channel until its retransmit.
- **Cost.** 16 full-auto players at 10 shots/s = 160 shots/s, each sent reliably to 16 clients = 2,560 reliable sends per second. Head-of-line delay under loss UNVERIFIED.
- **Fix.** A third unreliable channel for Shot, PelletFan, ImpactFx and gunship effects. Confirm the client does not require a Shot before its Hit. Keep Hit, Kill, Death, Spawn, MatchState and profile on reliable.
- **Effort.** M.
- **Confidence.** CONFIRMED (channel use). Stall size UNVERIFIED.
- **Audit.** T2-09 (open).

### G-07. Telemetry: the stats line exists, but a live server still cannot be watched

- **What exists.** `godot/shooter/Net/NetServer.Telemetry.cs:32-60`: tick p50 and p99, overruns, skipped snapshots, bytes per client per second, entity count, DB queue depth, dropped packets, rejected saves. Bot routes: mean and max every 10 s (`NetServer.Bots.cs:101-125`). Bot status line every 10 s.
- **Missing.**
  1. Tick p95 and max (only p50 and p99 are printed). Max is in the loss test only.
  2. Phase breakdown: Poll, bots, player simulation, rockets, vehicles, snapshot send. Without it the first slow tick cannot be attributed.
  3. Ray count per tick and per system, and LiveTargets calls. About 12 call sites use `DirectSpaceState` directly (`NetServer.cs:251`, `:728`; `NetServer.Turrets.cs:46`; `NetServer.Aircraft.cs:87`; `NetServer.Corpses.cs:41`; `NetServer.Vehicles.cs:339`; `NetServer.Glass.cs:93`; `NetServer.Flak.cs:64`; `NetServer.Targets.cs:32`; `NetServer.CarePackages.cs:38`; and the other sites listed in `grep -n DirectSpaceState godot/shooter/Net`). A single wrapper gives the count.
  4. GC: `GC.CollectionCount(0)`, `GC.GetTotalAllocatedBytes()`, `GC.GetTotalPauseDuration()`.
  5. Bytes split by channel (reliable and data), and packets per second. Only total bytes per client is printed (`Tx` at `NetServer.cs:484-489`).
  6. Entity counts by type (players, vehicles, rockets, each world type).
  7. Snapshot full versus delta counts. `SnapshotEncoder.FullEntries` and `DeltaEntries` (`Snapshot.cs:60`) are counted but never printed.
  8. Per-peer RTT and packet loss (`ENetPacketPeer.GetStatistic`). The scoreboard ping exists (`NetServer.cs:584`) but not in the log.
  9. Persistence: dropped writes (`WriteBudget.Dropped`, `DbLane.cs:42-70`) and write latency are not in the stats line.
- **Fix.** One stats line with the above, every 10 s, plus a Stopwatch around each phase of `_PhysicsProcess` (`NetServer.cs:175-230`) on a debug flag.
- **Effort.** S.
- **Confidence.** CONFIRMED (absence, by reading the stats method).
- **Audit.** T2-07 (partly closed).

### G-08. The bot benchmark never exercises killstreaks

- **Mechanism.** Bots use no vehicles and no killstreaks (`docs/shooter-server.md`, `--bots N` row). The loss test (`make shooter-loss-test`, bots with an AK) therefore measures players, shots, corpses and 29 parked cars. Turrets, helpers, rockets, aircraft, missiles and flak never run in it. The 1.59 ms p50 says nothing about the streak costs in section 3.0.
- **Fix.** Measure with a streak load, not by extrapolation. Two options: sandbox clients (the server grants every killstreak on demand under `--allow-sandbox`), which need the bot harness to press the Killstreak key; or a server-side synthetic loader that spawns N turrets, helpers and aircraft and fires rockets. Either is a harness change, no game code.
- **Effort.** S to M.
- **Confidence.** CONFIRMED (docs and code). The value of the streak load is UNVERIFIED until run.
- **Audit.** New (the audit did not analyse bots or streaks as load).

### G-09. No container limits and no GC configuration

- **Mechanism.** `deploy/docker-compose.yml:23-40` sets no `mem_limit`, `cpus` or `cpuset`; `deploy/Dockerfile` sets no `DOTNET_GC*` variable (`:4-8` sets invariant globalization only); `godot/shooter/shooter.csproj:10` sets `InvariantGlobalization` only. The server and PostgreSQL share the host without limits.
- **Cost.** UNVERIFIED. The allocation rate at worst is about 4 to 5 MB/s (G-01 plus about 430 kB/s from snapshot packets, from `Snapshot.cs:161` `ToArray` per packet). Gen-0 collections per second and their pause are not measured.
- **Fix.** `mem_limit` for both services with `DOTNET_GCHeapHardLimit` below it; `cpuset` rather than `cpus` quota (a quota throttles in bursts, showing as tick jitter); `ConcurrentGarbageCollection=false` for the server, which has one busy thread (OPINION); a gen-0 budget only if the GC count is high. Choose after measuring.
- **Effort.** S.
- **Confidence.** CONFIRMED (absence). Effect UNVERIFIED.
- **Audit.** T2-35 (open).

### G-10. Persistence: connection timeouts are not set; writes are lost during an outage

- **Mechanism.** `ToConnectionString` builds an Npgsql connection string with host, port, database and credentials only (`godot/ShooterServer.Persistence/ProgressionStores.cs:18-30`). No `Timeout` or `Command Timeout` is set, so Npgsql's own defaults apply (UNVERIFIED values). The reconnect backoff bounds how often a blocked open runs (`DbLane.cs:75-100`), and reads are on their own lane, so a stalled write lane does not block a join. But a write that fails during an outage is logged and dropped (`SqliteWorker.cs:104`, and the Postgres equivalent). There is no retry and no spool. XP and stats written during a 30 s database outage are lost; at 5 to 10 writes/s that is 150 to 300 writes (estimate).
- **Fix.** Add `Timeout=5;Command Timeout=5` in `ToConnectionString` (S). If lost writes matter, spool failed writes to a local file and replay (M), or accept the loss and say so in the docs (OPINION).
- **Effort.** S (timeouts); M (spool).
- **Confidence.** CONFIRMED (path). Npgsql default values UNVERIFIED.
- **Audit.** T2-14 (residual).

### G-11. Progression writes are one commit per event

- **Mechanism.** `AwardXp` queues `AddXpAsync` per grant (`godot/shooter/Net/NetServer.Progression.cs:57-64`); `RecordKill` and `RecordDeath` queue one write each (`:77`, `:83`). `synchronous=NORMAL` is set for SQLite (`SqliteWorker.cs:47-50`), so commits do not fsync per write, which closes most of the cost in audit T2-13.
- **Cost.** About 3 to 5 writes per kill (estimate from the three call sites). At 60 kills per minute (the loss test had 25 kills in 30 s with chase bots), about 5 writes per second. Far below what the worker can do (not measured). Not on the tick.
- **Fix.** In-memory counters flushed every 5 s and on leave and round end, one transaction per flush. Only worth it when writes per second are in the hundreds.
- **Effort.** M.
- **Confidence.** CONFIRMED (path). Rate UNVERIFIED.
- **Audit.** T2-13 (partly closed, low priority).

### G-12. Bot sight rays scale with players times bots

- **Mechanism.** Each bot runs `Look` every 0.15 s (`BotBrain.cs:34`, `:161-168`), which casts a sight ray to every enemy within 120 m and in the view cone or within 6 m (`BotBrain.cs:280-292`). Enemies are all live players, bots included (`NetServer.Bots.cs:150-153`). Routes are answered one per tick (`NetServer.Bots.cs:101-117`); route cost is logged (mean and max every 10 s), so it is measurable without code.
- **Cost.** Worst per bot per second: 6.7 x (P-1) rays. At 16 bots and 16 humans: 16 x 6.7 x 31 = 3,300 rays/s before filters; with the range and cone filters, probably a third, about 1,100 rays/s (UNVERIFIED). At 64 bots (`MaxBots`, `ChatCommands.cs:74`) with 32 humans, the upper bound is about 13,000 rays/s before filters.
- **Fix.** Share one sight result per pair per tick (a pair is looked at by two bots at most once); scale the think interval with distance; cap sight rays per bot per tick. S each.
- **Effort.** S.
- **Confidence.** CONFIRMED (arithmetic). Filter ratio UNVERIFIED.
- **Audit.** New.

### G-13. Smaller items, each CONFIRMED and low priority

- **Snapshot packet per tick for every client.** `SnapshotEncoder.Encode` always returns at least one packet, to carry the acknowledgement (`Snapshot.cs:114`). At 16 clients that is 480 small packets per second even when nothing changes. Audit T3-022 (open), about 0.6 kB/s per client.
- **Per-packet `ToArray()`.** `Finish` copies each packet into a new array (`Snapshot.cs:161`; `WorldReplication.cs:152`). The scratch buffers are reused (audit T2-03 mostly closed); this copy remains. Cost small.
- **Two `Seat.Exit` and two `InVehicle = false` lines** in `LeaveSeat` (`godot/shooter/Net/NetServer.Vehicles.cs:209-216`). Harmless; cleanup.
- **Road surface scan** (audit T3-003, open): `RoadSurfaces.Contains` is linear over about 940 segments per impact. Only on impacts.
- **Dockerfile `chmod` layer** (audit T2-38, open): `deploy/Dockerfile:16`.

---

## 4. Design alternatives considered

Each is priced. "Buys" is in terms of the budget table.

1. **Keep the single main-thread tick; fix the waste (G-01, G-02a, G-05, G-12).** Effort S to M. Buys roughly half the all-streak allocations and rays, and moves the quadratic-in-P ceiling from about 24 to about 32 players. Recommended first step. Consistent with ADR 0002.
2. **Interest management for rockets and effects (G-02b, G-06).** Effort M. Buys most of the rocket fan-out (roughly 80 % of the rocket term at 16, UNVERIFIED) and the shot and effect fan-out. The spike measured 78 % less downstream from a 50 m radius at 64 clients (`docs/spikes/netcode-spike.md`). Consistent with ADR 0002, which lists it as optional.
3. **Full per-client interest set (G-04).** Effort L. Buys the rest of the downstream term. Needs visibility rules for streak entities and events. Needed only above about 32 players.
4. **Encode snapshots on a worker thread.** Effort M. The encoder is pure C# over an immutable entity list, so it can run per client in parallel. ENet `Send` and `Flush` must stay on the main thread (ENet is not thread-safe). Buys the encode time, which is probably under 0.5 ms at 16 (UNVERIFIED), so it pays only at 32 or more. Consistent with ADR 0002.
5. **Route queries on a worker thread.** Effort S to M. `BotNav` tile data is read-only, so `Route` can run off-thread. Buys route milliseconds (cost unknown until logged). Sight rays cannot move: Godot's direct space state is for the physics thread.
6. **One server process per match or per port.** Effort S for code (the server already takes `--port` and `--db`), M for operations. Buys N times the cores and keeps every tick single-threaded. Costs one map load and one process of memory per match (UNVERIFIED size; the map is 48 chunk GLBs, 44 MB on disk, `docs/perf-audit.md` section 1). This is the cheapest route past 32 players and does not change the protocol.
7. **Godot's built-in MultiplayerSynchronizer.** Rejected. ADR 0002 and the spike: frame time 62.7 ms at 64 clients, and the 64-property limit (`docs/spikes/netcode-spike.md`).
8. **Tick rate 20 Hz.** Effort M, because movement, prediction and reconciliation assume 30 Hz. Buys one third of the tick and snapshot cost. Changes feel. OPINION: not recommended while the 16-player budget has 5 % used.
9. **Jolt worker threads.** Effort S to test. Little gain expected: the server's cost is queries and manual movement (`AutoSim = false`), not the world's physics step (UNVERIFIED).
10. **Spatial hash for target queries (replacing LiveTargets scans).** Effort M. Buys O(cells) instead of O(P) per query. Not needed at 16; reconsider with G-04.
11. **Persistence batching (G-11).** Effort M. Buys commit count only. Not on the tick. Not worth it below hundreds of writes per second.
12. **Container limits (G-09).** Effort S. Buys tick jitter isolation from PostgreSQL. Choose after measuring.

---

## 5. Needs measurement

Ordered by value. Nothing here has been run.

| What | Why not static | Cheapest way |
|---|---|---|
| **Cost of one `IntersectRay` on this server build** (5 to 30 microseconds assumed) | It sets the all-streak tick: 350 rays per tick x cost | A Stopwatch around one wrapper (G-07 item 3), run in the existing loss-test mode with synthetic rays, or `dotnet-counters` plus the wrapper's count |
| **All-streak tick**: p50, p95, max, and phase split | Bots never use streaks (G-08) | Harness with sandbox clients or a synthetic loader (G-08), run for 60 s at 16 and 32 players, with the phase line (G-07) |
| **Rays and LiveTargets per second by system** | Counts depend on fire rates and filters | Counters in the wrapper (G-07 item 3) during the streak run |
| **Gen-0 GC rate, pause, allocation rate** | Depends on runtime settings and allocation volume | `dotnet-counters monitor System.Runtime` (`alloc-rate`, `gen-0-gc-count`, `time-in-gc`) during the streak run; or the new GC counters in the stats line (G-07 item 4) |
| **World-channel staleness after loss** | The loss test measured players before the acknowledged baseline | `make shooter-loss-test SCENARIOS=rtt100`, check the "decoded player states wrong" rows for players, and extend the check to world entities (G-03) |
| **Downstream bytes at 16 and 32 clients, all moving, with rockets** | Derived here, not measured | The loss-test byte counters with a streak load (G-08) |
| **Bot route cost** | The code path is logged, not the value | Read the existing "routes ... mean ... max" line of a `make shooter-server BOTS=16 CLUSTER=1` run, 10 s |
| **Bot ray count and filter ratio** | Depends on positions | Counter in `BotWorld.Clear` (`NetServer.Bots.cs:49`) during a bot run |
| **Parked-car move cost** | Per-move cost of `MoveAndSlide` on a parked car | Tick time with 29 parked cars against `--vehicle-set` with none, in loss-test mode (as audit section 6 proposes) |
| **Postgres outage behaviour** | Npgsql defaults, and what is lost | Stop the `db` container during a bot run; read the "db connection down" and "db write failed" lines and the dropped count (G-10) |
| **Container memory and CPU at idle and under load** | Not measured | `docker stats --no-stream` on the compose stack; `docker compose stop -t 30 server` to check the flush on stop |
| **Memory per server process** (for alternative 6) | Map size in RAM unknown | RSS of one headless server after map load (`ps -o rss`) |

---

## Appendix: audit status of server items

| Audit ID | Status at HEAD | Where |
|---|---|---|
| T1-1 frame cap | Closed | `Shooter.cs:421-422` |
| T1-2 acknowledged deltas | Closed for players and vehicles; open for world channel | `NetServer.cs:371`, `Snapshot.cs:68-70`; `WorldReplication.cs:115` (G-03) |
| T2-01 turret re-pick | Closed | `TurretEntity.cs:60`, `:128-135` |
| T2-02 exclude array | Closed (cached); small copies remain on skip | `NetServer.cs:708-715`, `:722-731` |
| T2-03 encoder buffers | Mostly closed; `ToArray` per packet remains | `Snapshot.cs:161` (G-13) |
| T2-04 parked cars | Open | G-05 |
| T2-05 tick overrun | Mostly closed | `Shooter.cs:422`, `NetServer.cs:183`, `:212` |
| T2-06 per-peer budget | Closed | `NetServer.cs:343`, `:379`; `NetServer.Telemetry.cs:59-63` |
| T2-07 telemetry | Partly closed | G-07 |
| T2-08 interest | Open | G-04 |
| T2-09 reliable effects | Open | G-06 |
| T2-10 broadcast encode | Closed | `NetServer.cs:497-503` |
| T2-11 keyframe by age | Closed for players and vehicles; open for world | G-03 |
| T2-12 read lane | Closed | `SqliteWorker.cs:24-28`, `PostgresWorker.cs:23-30` |
| T2-13 per-event commits | Partly closed (`synchronous=NORMAL`) | G-11 |
| T2-14 queue and reconnect | Mostly closed; timeouts not set | `DbLane.cs:42-100`; G-10 |
| T2-35 container limits and GC | Open | G-09 |
| T2-36 log rotation | Closed | `deploy/docker-compose.yml:8-14` |
| T2-37 init and grace | Closed | `deploy/docker-compose.yml:39-40` |
| T2-38 image layers | Open | `deploy/Dockerfile:16` |
| T2-39 image prune | Closed | `deploy/deploy.sh:20` |
| T3-001 LiveTargets | Open | G-01 |
| T3-002 helper per-tick snapshot | Open | G-01 |
| T3-003 impact scans | Partly (tree grid built; road scan linear) | G-13 |
| T3-004 roads parsed on first impact | Closed | `NetServer.cs` `WarmSurfaceIndexes` call |
| T3-008 per-tick `ToList` | Open | `NetServer.cs:203`; `WorldEntities.cs:135` |
| T3-014 collision exceptions | Closed | `NetServer.cs:450` |
| T3-020 peer cap message | Closed | `NetServer.cs:146`, `:335` |
| T3-021 lamps in headless | Closed | `StreetLamps.cs:66` |
| T3-022 empty snapshot packet | Open (by design: carries the ack) | G-13 |
| T3-027 shutdown join | Closed | `DbLane.cs` `Join` |
| T3-110 healthcheck | Closed | `deploy/docker-compose.yml:51` |

---

## Sources cited

`GLOSSARY.md`; `docs/adr/0002`, `0003`, `0005`, `0007`, `0011`; `docs/perf-audit.md`; `docs/spikes/shooter-loss-test.md`; `docs/spikes/netcode-spike.md`; `docs/shooter-v2.md`; `docs/shooter-server.md`; `godot/shooter/Net/NetServer*.cs`; `godot/shooter/Shooter.cs`; `godot/shooter/data/killstreaks.json`, `turret.json`, `vehicles.json`, `weapons.json`; `godot/ShooterCore/Net/Snapshot.cs`, `WorldReplication.cs`, `WorldEntities.cs`, `Wire.cs`; `godot/ShooterCore/Bots/BotBrain.cs`; `godot/ShooterCore/Turrets/TurretEntity.cs`; `godot/ShooterCore/Weapons/RocketFlight.cs`; `godot/ShooterCore/Vehicles/FlakRules.cs`; `godot/ShooterServer.Persistence/*`; `deploy/*`.
