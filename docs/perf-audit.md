# Performance audit

Date: 2026-11-11. Branch `perf-audit` at `ee90afd`. Static analysis only: nothing was run, built or benchmarked, and no code was changed.

## How this was produced

- Twelve parallel auditors, each with its own scope: allocations (three, split by directory), data structures and algorithms (three, same split), viewer and netspike (one, both lenses), Python map pipeline, networking and persistence, rendering, 3D world, Docker and build. They reported 286 numbered items.
- An adversarial pass of three agents: one re-read the code behind ten Tier 1 candidates and seven contradictions, one re-read a seeded random sample of 14 of the Tier 2 findings and repaired citations, one hunted for gaps (threading, logging, startup, HUD files). The lead re-read the Tier 1 code and the disputed items.
- Scale numbers were measured from the exported village map (`out/village/map`, version `0.6.0-village`): 48 chunk GLBs (44 MB), 12,175 trees, 1,165 parcels, 110 street lamps, 135 road edges (about 940 segments), 5 water bodies, 150 spawns, 73 placed objects. The chunks hold 998 visual mesh nodes (689,649 triangles) and 399 collision mesh nodes (72,078 triangles). The server spawns 29 vehicles (`godot/shooter/data/vehicles.json`).
- After merging findings that share a root cause: **159 findings**.

Reliability of citations: every Tier 1 and Tier 2 citation was re-read by a verifier or by the lead. Tier 3 citations are the auditors' own; the ones that pointed past the end of a file were repaired, the rest were spot-checked only. Check a Tier 3 line before acting on it.

`UNVERIFIED` means the code path is certain but the cost or the engine behaviour is not; section 6 gives the cheapest way to confirm each one.

## 1. Executive summary

At the 16-player design target the server and the protocol sit well inside their budgets (the repo's own loss test measured 4.24 kB/s per client with 16 bots). Most findings are therefore medium or low. Two are high: the dedicated server has no frame cap, and snapshot deltas are not taken against an acknowledged baseline. The client rendering findings could move up once measured; none of them could be sized from the code.

### Findings by area and tier

| Area | Tier 1 | Tier 2 | Tier 3 | Total |
|---|---|---|---|---|
| Server simulation and tick | 1 | 7 | 21 | 29 |
| Networking protocol | 1 | 4 | 4 | 9 |
| Persistence | 0 | 3 | 2 | 5 |
| Client frame (FX, HUD, menus) | 0 | 5 | 22 | 27 |
| Rendering | 0 | 6 | 12 | 18 |
| 3D world and map load | 0 | 4 | 7 | 11 |
| Viewer and map editor | 0 | 4 | 14 | 18 |
| Map pipeline | 0 | 1 | 24 | 25 |
| Docker, build and deploy | 0 | 5 | 12 | 17 |
| **Total** | **2** | **39** | **118** | **159** |

Hot-path findings: 2 of 2 in Tier 1, 23 of 39 in Tier 2.

### The five biggest themes

1. **Shared per-tick work is redone per consumer on the server.** The live-target list, the player exclude array for rays, the encoded bytes of a broadcast and the snapshot scratch buffers are all identical for every rocket, turret, ray or client within one tick, and all are rebuilt each time (T2-01, T2-02, T2-03, T2-10, T3-001).
2. **Short-lived client effects are built from fresh nodes.** Every tracer, pellet spark, splash and blast creates nodes, tweens and timers and frees them a moment later. There is no pool and no distance gate, and the nodes a client adds to the persistent world are not all freed on leave (T2-15, T2-16, T2-17).
3. **The static world is drawn and loaded at full detail everywhere.** 998 mesh nodes each have their own material, all cast shadows, none has a visibility range, the props meshes are unindexed, and the headless server builds all of it too (T2-20, T2-21, T2-22, T2-25, T2-26).
4. **The protocol sends everything to everyone, reliably, from an unacknowledged baseline.** No interest management, effects on the ordered reliable channel, and deltas against the last state sent (T1-2, T2-08, T2-09, T2-11).
5. **Per-frame recomputation where a dirty flag would do.** Editor outlines, mouse rays and the region tool; minimap redraw; day/night; HUD prompt strings (T2-30, T2-31, T2-32, T3-034 to T3-039).

Also worth knowing: the server has no always-on timing or bandwidth telemetry (T2-07), so none of the server findings can be watched on a live server today.

### What the adversarial pass changed

- Eight of ten Tier 1 candidates were downgraded. The auditors' "high" ratings for `LiveTargets()` churn, client event logs, interest management at 16 players, MSAA and the static-world rendering did not survive: the measured counts were smaller than estimated, or the cost could not be derived from code.
- Corrected numbers: 998 visual nodes (not about 1,600); 689,649 visual triangles (not about 400k); 399 trimesh bodies (not about 2,300); about 940 road segments (not 5 to 20 thousand); 29 vehicles stepped per tick (one auditor said 3 to 4); client event logs grow about 3 to 5 MB per hour (not 25 MB per 30 minutes).
- Rejected claims: the helicopter helper does not cast sight rays every tick (only at the end of a cycle and per burst round, `godot/ShooterCore/Killstreaks/Helicopter/HelicopterGunner.cs:66`, `:78`, `:87`); sun shadows do not cover the whole village (no distance is set, so the engine default applies); one "API cost" item with no fix was dropped.
- Contradictions resolved: keep PostgreSQL `synchronous_commit` on (the write rate is a few per second on a background thread); the camera far plane alone is not a cost, the missing visibility ranges are; the sniper breath label is rebuilt every frame (`godot/shooter/Hud.cs:768-781`), the underwater air label is not.
- The gap hunt added six items: T2-18, parts of T2-13 and T2-19, and parts of T3-009, T3-026 and T3-048.

## 2. Tier 1: High impact

### T1-1 The dedicated server has no frame cap (hot, effort S, UNVERIFIED at runtime)

Nothing limits the main loop of the headless server in a normal run. `godot/shooter/project.godot:23` sets only `physics_ticks_per_second=30`; there is no `max_fps` or low-processor setting, and neither `deploy/Dockerfile:22` nor `deploy/docker-compose.yml:35` passes a frame-rate flag. The only writes to `Engine.MaxFps` are `godot/shooter/Shooter.cs:300` (200, and only with `--loss-test` or `--net-sim`) and the loss bot. Godot runs idle frames as fast as it can when no cap, V-Sync or low-processor mode applies, so the server is expected to hold one core at 100 % while the simulation needs 30 ticks per second. The loss test's "server CPU 9 % of one core" (`docs/spikes/shooter-loss-test.md:33`) was measured under that 200 fps cap and does not describe the Docker default. On a small VPS a spinning core competes with Jolt's worker threads and PostgreSQL. Per frame the server also runs `StreetLamps._Process` (`godot/shooter/StreetLamps.cs:54-85`) for nothing.

Fix: set `Engine.MaxFps = 60` (about twice `Wire.TickRate`) immediately before `AddChild(server)` at `godot/shooter/Shooter.cs:301`, keeping 200 for the two test modes. The physics accumulator keeps the tick at 30 Hz. Confirm first with `docker stats --no-stream` on an idle server (section 6).

Sources: DOCK-01, REN-21. Verified: code path confirmed by the verifier and the lead; engine behaviour not confirmed statically.

### T1-2 Snapshot deltas are taken against the last state sent, not an acknowledged one (hot, effort M, CONFIRMED)

`SnapshotEncoder` keeps the last record it sent per entity (`godot/ShooterCore/Net/Snapshot.cs:11-12`, `:39`, `:59`), sends each entity as a delta against it and omits unchanged entities (`:42-57`). The world encoder does the same (`godot/ShooterCore/Net/WorldReplication.cs:91-150`). The `ack` in the snapshot header is the server acknowledging the client's inputs; the client's input packet (`godot/ShooterCore/Net/Wire.cs:353-365`) carries no snapshot tick, so the server never learns what arrived. A lost delta leaves the client's copy wrong, later packets omit the entity as unchanged, and the error stays until the next keyframe (`Wire.KeyEvery = 30`, `godot/ShooterCore/Net/Wire.cs:161`), up to a second or more. The project's own loss test measured 15.4 % of decoded player samples wrong and an 86-tick wrong run at 3 % loss and 100 ms RTT, and records it as "real defect, not fixed" (`docs/spikes/shooter-loss-test.md:28-29`, `:40-41`). The same decoder feeds local reconciliation, so the player's own position is pulled as well. This is the one gate ADR 0002 left open.

Fix (as the loss-test doc proposes): the client adds the last applied snapshot tick to the input packet (2 bytes, protocol bump); the server keeps a short ring of sent states per client and deltas against the newest acknowledged one, with keyframes only as a fallback. Stop-gap with no protocol change: lower `Wire.KeyEvery` from 30 to 10 (the doc estimates about 15 % more snapshot bytes; not measured).

Sources: NET-03. Verified: confirmed by the verifier and the lead. Known and documented; reported here because it is still open and is the highest-impact networking item.

## 3. Tier 2: Medium impact

Each entry: mechanism, citation, fix, then effort, confidence and the source findings. Entries marked *sampled* were re-read in the random 25 % sample; entries marked *candidate* were verified as Tier 1 candidates and downgraded.

### Server simulation and tick

**T2-01 Sentry turrets re-pick their target every tick with physics rays.** Hot. Each armed turret rebuilds the enemy list and runs `PickTarget`, which casts a sight ray for each in-range candidate closer than the best so far, 30 times a second, even while a target is held (`godot/ShooterCore/Turrets/TurretEntity.cs:115-133`, `godot/ShooterCore/Turrets/TurretMath.cs:67-80`, `godot/shooter/Net/NetServer.Turrets.cs:40-56`). One turret per owner can be live (`godot/shooter/data/killstreaks.json:14`), so up to 16.
Fix: re-pick at about 5 Hz; between picks validate only the held target (alive, in range, one ray). Handle the held target leaving the list (`godot/ShooterCore/Turrets/TurretEntity.cs:133` uses `First`) and do not reset the acquire delay on re-validation. Effort S. CONFIRMED (*sampled*); rays per tick UNVERIFIED. Sources: DB-01, DC-01, W3D-07, MC-08.

**T2-02 Every physics ray rebuilds an exclude array of all player bodies.** Hot. `WorldHit`, `TurretRay` and `WorldWall` each create a `Godot.Collections.Array<Rid>` and add one RID per player per call (`godot/shooter/Net/NetServer.cs:553-565`, `godot/shooter/Net/NetServer.Turrets.cs:40-50`, `godot/shooter/Net/NetServer.Aircraft.cs:79-88`). Callers: each pellet (`godot/shooter/Net/NetServer.Pellets.cs:28`, 8 per shotgun shot), each rocket each tick (`godot/shooter/Net/NetServer.cs:652`), each gunship round, each turret sight ray.
Fix: keep one array field rebuilt in `Join` and `Drop`; or put player capsules on their own collision layer outside `CollisionLayers.Bullets` (`godot/ShooterCore/Colliders/PropColliders.cs:18`) so no exclude list is needed. Effort S. CONFIRMED. Sources: MB-04, DB-03, NET-11, W3D-15.

**T2-03 Snapshot encoders allocate a set, a list and a 1,400-byte buffer per client per tick.** Hot, always on. Both encoders build a `HashSet` of all ids, a LINQ list of stale keys and `new byte[1400]`, then copy the packet out (`godot/ShooterCore/Net/Snapshot.cs:19-24`, `:76`; `godot/ShooterCore/Net/WorldReplication.cs:93-98`, `:150`), once per client (`godot/shooter/Net/NetServer.cs:716-723`). The world encoder does this even when no world entity is live. 2 encoders x 16 clients x 30 Hz = 960 buffers, about 1.3 MB of garbage per second.
Fix: one reusable scratch buffer per encoder; a per-entry generation stamp instead of the set and the list; return early when the list and `_last` are both empty. Effort S. CONFIRMED (*sampled*). Sources: MB-02, DB-05, NET-02.

**T2-04 All 29 vehicles are stepped every tick, parked or not.** Hot, always on. `StepVehicles` skips only destroyed cars; every other car runs `car.Step`, which ends in `MoveAndSlide` (`godot/shooter/Net/NetServer.Vehicles.cs:258-290`, `godot/shooter/ServerCar.cs:121-145`), plus a LINQ occupant list per car per tick (`godot/shooter/Net/NetServer.Vehicles.cs:208`, `:279`; `godot/ShooterCore/Vehicles/VehicleRules.cs:83-84`). That is 870 kinematic moves per second with nobody driving.
Fix: skip `Step` for a driverless car at rest at its origin; wake on seat change, damage or blast. Keep the abandon timer (`godot/shooter/Net/NetServer.Vehicles.cs:286`), the fall check (`:274`) and the detonation timer running. Replace `Occupants` with a seat loop. Effort M. CONFIRMED (*sampled*); cost per move UNVERIFIED. Sources: NET-13, MB-11, DB-13, MC-09.

**T2-05 No tick overrun handling.** Hot. `_PhysicsProcess` does the whole tick and sends snapshots last, with no budget (`godot/shooter/Net/NetServer.cs:149-171`); `godot/shooter/project.godot:23` does not set `max_physics_steps_per_frame`. When a frame overruns, Godot runs catch-up ticks back to back and each one polls, simulates and sends, so traffic rises exactly when the server is behind.
Fix: set `physics/common/max_physics_steps_per_frame=2` for the server, skip the snapshot send on catch-up ticks after the first, and log overruns with the slow phase. Effort S. Mechanism CONFIRMED; the engine default step cap is UNVERIFIED. Source: NET-16.

**T2-06 No per-peer budget on the receive path.** Hot. `Poll` drains up to 10,000 events and every queued packet per peer each tick (`godot/shooter/Net/NetServer.cs:280-297`). Inputs are capped at 8 (`:324`), reliable messages are not, and some trigger a database write and a profile resend.
Fix: a per-peer packet budget per tick (drop and count the rest) and a rate limit on `SaveLoadout`, `SaveCamo`, `SelectSlot`, `CopyLoadout`, `ResetLoadout`. Effort S. CONFIRMED. Source: NET-15.

**T2-07 No always-on telemetry for tick time, bytes out or database queue depth.** Cold (observability). The only periodic output is the 2-second player list (`godot/shooter/Net/NetServer.cs:174-175`). Tick timing exists only in loss-test mode (`godot/shooter/Net/NetServer.LossTest.cs:77-86` returns when `LossTest == 0`), `Tx` counts no bytes (`godot/shooter/Net/NetServer.cs:386-389`), and the worker queue is private (`godot/ShooterServer.Persistence/Sqlite/SqliteWorker.cs:15`).
Fix: one line every 10 s with tick time p50 and p99, bytes per client per second, entity count and queue depth. Effort S. CONFIRMED (*sampled*). Source: NET-17.

### Networking protocol

**T2-08 No interest management: every entity and every effect goes to every client.** Hot. `SendSnapshots` encodes all players, all 29 vehicles, all dummies and rockets for each client, and `BroadcastEvent` sends to every joined client (`godot/shooter/Net/NetServer.cs:704-726`, `:394-398`). No radius or visibility filter exists in either Net folder. A moving entity costs 14 B per tick: 16 players moving is 16 x 14 x 30 = 6.7 kB/s per client; with all 29 vehicles moving too, about 20 kB/s per client and about 320 kB/s server egress. Growth is quadratic in players. The spike measured a 78 % cut at 64 clients with a 50 m radius (`docs/spikes/netcode-spike.md`).
Fix: a per-client interest set by distance with hysteresis, always including the client's own vehicle and streak entities; far entities at 10 Hz; distance-cull Shot, impact and gunship effects. Effort L. CONFIRMED (*candidate*): low to medium at 16 players, high beyond 32. Sources: NET-01, NET-10.

**T2-09 High-rate effects share the ordered reliable channel with Kill, Hit and Death.** Hot. Every `Send` and `BroadcastEvent` uses `ChanReliable` with `FlagReliable` (`godot/shooter/Net/NetServer.cs:391`, `:394-398`; `godot/ShooterCore/Net/Wire.cs:160`), including shots (up to 15 per second per player), pellet fans, impact puffs and up to three gunship messages per tick (`godot/shooter/Net/NetServer.Aircraft.cs:194-196`). One lost packet holds back everything behind it on the channel until the retransmit.
Fix: a third, unreliable channel for Shot, PelletFan, ImpactFx and gunship kinds 121 to 123; keep Hit, Kill, Death, Spawn, MatchState and profile messages reliable; move the gunner hit flags into a reliable Hit. Check first that the client never depends on a Shot arriving before its Hit. Effort M. Channel use CONFIRMED (*candidate*); stall size UNVERIFIED. Source: NET-05.

**T2-10 Broadcast events are encoded once per recipient.** Hot. `BroadcastEvent` calls `Send` per client and `Send` calls `Wire.Encode` each time (`godot/shooter/Net/NetServer.cs:391`, `:394-397`). `Wire.Encode` grows a new `List<byte>`, makes a small array per field and copies the result (`godot/ShooterCore/Net/Wire.cs:232-287`).
Fix: encode once in `BroadcastEvent` and `Tx` the same bytes to each peer; write into a reusable buffer. Check that `NetSim.Send` does not mutate the buffer. Effort S. CONFIRMED (*sampled*). Sources: MB-01, DB-04, NET-02.

**T2-11 Every entity is keyframed every 30 ticks, changed or not.** Hot. The keyframe fires on age alone (`godot/ShooterCore/Net/Snapshot.cs:33-39`, `godot/ShooterCore/Net/WorldReplication.cs:108-114`). An idle entity costs 23 B per second per client: 16 players + 29 vehicles + 24 dummies is about 1.6 kB/s per client, a large share of the measured 4.24 kB/s.
Fix: with T1-2 in place, keyframe only on first sight or after an acknowledgement gap. Do not lengthen the interval before T1-2. Effort S after T1-2. CONFIRMED (arithmetic). Source: NET-04.

### Persistence

**T2-12 One FIFO worker serves reads and writes; a join that waits too long silently becomes a guest.** Cold (join). Profile reads queue behind every earlier write on one `BlockingCollection` (`godot/ShooterServer.Persistence/Sqlite/SqliteWorker.cs:15`, `:51`, `:65`; `godot/ShooterServer.Persistence/Postgres/PostgresWorker.cs:14`, `:84-88`). After 3 s the player is attached to a guest profile with `Persist = false` and no notice (`godot/shooter/Net/NetServer.Profile.cs:16`, `:68-78`, `:115-119`).
Fix: a separate read connection (a second SQLite WAL reader; a second Npgsql connection) and a message to the player when the guest fallback happens. Effort M. Path CONFIRMED; backlog depth at which it bites UNVERIFIED. Source: NET-07.

**T2-13 Progression writes are one commit per event.** Cold to warm. Each XP grant, kill, death and match record is its own queued write (`godot/shooter/Net/NetServer.Progression.cs:46`, `:60`, `:73`, `:78`; `godot/ShooterServer.Persistence/Sqlite/SqliteStore.cs:114-118`, `:229-244`; `godot/ShooterServer.Persistence/Postgres/PostgresStore.cs:106-117`, `:221-234`). SQLite runs WAL with the default `synchronous=FULL`, so each commit fsyncs (`godot/ShooterServer.Persistence/Sqlite/SqliteWorker.cs:37` sets only `journal_mode`).
Fix: keep per-player counters in memory and flush every 5 s, on leave and at round end, one transaction per flush. Add `PRAGMA synchronous=NORMAL` for SQLite. Keep PostgreSQL `synchronous_commit` on. Effort M. CONFIRMED. Sources: NET-08, GAP-02.

**T2-14 PostgreSQL outage: reconnect per job, no timeouts, unbounded queue.** Cold. With the connection down, every queued job tries `_conn.Open()` first and logs a line (`godot/ShooterServer.Persistence/Postgres/PostgresWorker.cs:84-88`); the connection string sets no `Timeout` or `Command Timeout` (`godot/ShooterServer.Persistence/ProgressionStores.cs:18-30`); the queue has no bound (`godot/ShooterServer.Persistence/Postgres/PostgresWorker.cs:14`). Reads queue behind the failing writes, so joins time out to guests (T2-12).
Fix: back off after a failed open and fail jobs at once while the breaker is open; `Timeout=5;Command Timeout=5`; a bounded queue with a dropped-write counter; rate-limit the log line. Effort S to M. CONFIRMED; the Npgsql default connect timeout is UNVERIFIED. Sources: NET-09, MB-16.

### Client frame

**T2-15 Every shot and pellet builds scene nodes, with no pool and no distance gate.** Hot. A tracer is a new pivot, `MeshInstance3D`, tween and callback; an impact or spark is a new `CpuParticles3D` plus a `SceneTreeTimer` and closure (`godot/shooter/ShotFx.cs:36-87`). Callers: `godot/shooter/Net/NetClient.Pellets.cs:37`, `:45` (one spark per landed pellet, 8 per shotgun shot), `:55`; `godot/shooter/Net/NetClient.Predict.cs:137`, `:143`; `godot/shooter/Net/NetClient.GunshipFx.cs:25`. Each splash also builds a new `SphereMesh` (`godot/shooter/WaterFx.cs:16-31`). Sounds are culled by range in `Sfx.Do3D`; effects are not. 16 players at 10 shots per second is about 160 tracers with tweens per second.
Fix: a distance check at the top of `Spawn` and `Spark`; pool sparks the way `godot/shooter/Net/NetClient.Impacts.cs:14` pools impacts; pool tracers and move them from one `_Process`; cache the splash mesh. Effort M (the gate alone is S). CONFIRMED (*sampled*). Sources: DA-03, MA-01, MA-02, REN-11, MB-13, MB-19, MA-22.

**T2-16 Each explosion builds about 19 objects; a gunship can send 8 blasts per tick.** Hot during gunship fire. `RocketFx.Explode` creates a root, three meshes, a light, four emitters, tweens and a timer per blast (`godot/shooter/RocketFx.cs:127-162`); the gunship handler loops over the message's blasts (`godot/shooter/Net/NetClient.GunshipFx.cs:46-54`); each plume also creates a tween and closures (`godot/shooter/Net/NetClient.Impacts.cs:139-143`); vehicle blasts add more (`godot/shooter/VehicleFx.cs:94-118`).
Fix: a small pool of explosion kits restarted in place, a tick's blasts coalesced into one kit, and ring fades driven from `_Process`. Effort M. Pattern CONFIRMED; blast rate UNVERIFIED. Sources: MA-03, MB-14.

**T2-17 Leaving a match leaves client nodes in the world; each rejoin adds more.** Cold, cumulative. `Teardown` frees only remotes, rockets, cars and the own player (`godot/shooter/Net/NetClient.cs:118-131`). The `Shooter` node and the map persist across joins (`godot/shooter/Shooter.cs:222-236`). Still parented to the world: corpses (`godot/shooter/Net/NetClient.Corpses.cs:51`), crates (`godot/shooter/Net/NetClient.CarePackages.cs:63`), turrets (`godot/shooter/Net/NetClient.Turrets.cs:50`), aircraft (`godot/shooter/Net/NetClient.Aircraft.cs:59`), missiles and the POV camera (`godot/shooter/Net/NetClient.Missile.cs:37`, `:102`), and the per-client impact, dust and ring pools (`godot/shooter/Net/NetClient.Impacts.cs:64`, `:84`, `:129`), which are rebuilt for every new client.
Fix: parent every world-facing node the client creates under one `Node3D` it owns and free that in `Teardown`. Effort S. CONFIRMED (*candidate*; the verifier called it bounded, the lead confirmed the pools leak per reconnect). Source: MB-12.

**T2-18 Models and shaders load on first use, in the middle of a match.** Hot once per asset. `ModelLib` parses a glTF synchronously on the first use of each model (`godot/shooter/ModelLib.cs:25-34`), reached from the first held weapon (`godot/shooter/WeaponFx.cs:51`), the first remote player (`godot/shooter/CharacterFx.cs:52`) and the first vehicle (`godot/shooter/CarFx.cs:19-24`). Shaders are loaded lazily (`godot/shooter/CharacterView.Camo.cs:121`, `:246`; `godot/shooter/GunshipOverlay.cs:25`; `godot/shooter/MissilePovOverlay.cs:35`). The loading steps cover only data, map, world and connect (`godot/shooter/Shooter.cs:162-180`).
Fix: in the loading steps, instance every weapon, character and vehicle model once and load each shader. Effort S. Path CONFIRMED; hitch size UNVERIFIED. Source: GAP-01.

**T2-19 The minimap terrain texture is rasterised on the main thread, per texel, on every join.** Cold hitch. `Rasterise` tests every texel of each parcel's bounding box with a point-in-polygon walk and writes it with `SetPixel`, at up to 2048 x 2048 (`godot/shooter/Minimap.cs:132-160`, `godot/MapLoader/MapData.cs:34-46`). A new `Hud` and minimap are built per connection (`godot/shooter/Net/NetClient.Hud.cs:16-23`), although the map is kept. The source `Image` is not disposed.
Fix: scanline-fill into a byte array and use `Image.CreateFromData`; cache the image per map across joins or build it during the loading screen; dispose the image. Effort M. CONFIRMED; time UNVERIFIED. Sources: W3D-10, DA-02, GAP-03, MA-28.

### Rendering

**T2-20 Every map mesh node has its own material; 998 nodes, 998 materials.** Hot. `FixMaterials` creates a new `StandardMaterial3D` for every visual node of every chunk (`godot/MapLoader/MapLoader.cs:262`, `:303-313`; also `godot/MapLoader/MapLoader.Regions.cs:91`, `godot/MapLoader/MapLoader.Objects.cs:395`). All have identical parameters.
Fix, step 1: one static shared material. It is safe today: nothing mutates `MaterialOverride` on chunk nodes (parcel swaps only toggle `Visible`, `godot/MapLoader/MapLoader.cs:458`, `:476`). This cuts material state, not draw calls. Step 2, to cut the 998 draws: merge static parcel nodes per chunk where parcels are never swapped (the shooter); the editor needs per-parcel nodes. Effort S, then M to L. CONFIRMED (*candidate*); frame cost UNVERIFIED. Sources: REN-01, MC-15, DOCK-17.

**T2-21 Every map node casts sun shadows and the shooter sets no shadow distance.** Hot by day. The sun has `ShadowEnabled = true` (`godot/shooter/DayNightCycle.cs:33`, `:89`); no `DirectionalShadowMaxDistance` is set in the shooter (the viewer sets 250 m, `godot/viewer/Viewer.cs:138-139`); `godot/MapLoader/MapLoader.cs:262-277` sets no `CastShadow`, so terrain and tree props are redrawn in each cascade that contains them.
Fix: `CastShadow = Off` on terrain and `_props` nodes at load, an explicit shadow distance of 60 to 80 m and two splits. Effort S. CONFIRMED (*candidate*); cost UNVERIFIED. Source: REN-02.

**T2-22 No visibility range or LOD on map geometry.** Hot. Chunk nodes get no `VisibilityRangeEnd` (`godot/MapLoader/MapLoader.cs:262-277`) and the camera far plane is 2,000 m (`godot/shooter/Player.cs:16`), so only frustum culling limits 689,649 triangles in 998 nodes.
Fix: a visibility range with a fade margin on per-parcel nodes and a far plane matched to the fog. This conflicts with merging nodes per chunk (T2-20 step 2); measure and choose one. Effort M. Absence CONFIRMED (*candidate*); impact UNVERIFIED. Source: W3D-03.

**T2-23 MSAA 4x is the default for every client.** Hot. `Msaa = 2` means 4x (`godot/shooter/Settings.cs:20`, `:68`, `:129`), and the water shader reads the depth texture (`godot/shooter/shaders/water_surface.gdshader:11`, `:25`), which needs a depth resolve on a multisampled target. The player can lower it (`godot/shooter/Menu/SettingsScreen.cs:160`).
Fix: default to 2x, or off with FXAA, and add a render-scale setting. Effort S. Setting CONFIRMED (*candidate*); cost UNVERIFIED. Source: REN-04.

**T2-24 Each remote player and corpse builds its own materials, and its ghillie tufts cast shadows.** Hot. Per character: three `StandardMaterial3D`, a suit `ShaderMaterial`, a tuft material and up to about eight tuft `MultiMeshInstance3D`, plus a face decal (`godot/shooter/CharacterView.Camo.cs:103-134`, `:247-258`, `:309-315`). About 20 to 25 draws per character, for up to 16 players and 16 corpses.
Fix: cache materials by look; `CastShadow = Off` on tufts and eyes. Effort S to M. CONFIRMED; draw count UNVERIFIED. Source: REN-07.

**T2-25 Tree and fence meshes are unindexed: 1,147,350 vertices for 382,450 triangles.** Hot (vertex memory) and cold (file size, load). `propmeshes.py` emits a triangle list with three vertices per triangle (`pipeline/src/drycastle_pipeline/propmeshes.py:11-55`, `:73-124`), and `to_trimesh` writes every mesh the same way with RGBA per vertex (`pipeline/src/drycastle_pipeline/export.py:52-62`). The chunk files total 44 MB.
Fix: emit indexed boxes and pyramids for props; index the terrain grid if per-vertex colour is acceptable. Effort M. CONFIRMED (measured). Sources: REN-09, PY-18.

### 3D world and map load

**T2-26 The headless server builds every visual mesh of the map.** Cold (startup time and memory). The server runs the same `LoadMap` (`godot/shooter/Shooter.cs:88`, `:246-261`): the glTF importer builds all 998 visual nodes and their materials (`godot/MapLoader/MapLoader.cs:237-279`, `:303-313`), and `StreetLamps` builds lamp visuals, a 12-light pool and a per-frame `_Process` (`godot/shooter/StreetLamps.cs:44-85`). Nothing on the server path reads the visuals.
Fix: a collision-only flag for the server: after `GenerateScene`, free the nodes that are not `*_collision` and skip `FixMaterials` and the triangle count; `StreetLamps` builds only its collision when headless. Do not free the `_collision` nodes: the collision bodies are their children (`godot/MapLoader/MapLoader.cs:323-326`). Effort S to M. CONFIRMED (*sampled*); time saved UNVERIFIED. Sources: W3D-02, REN-10, DOCK-07, W3D-19.

**T2-27 Tree colliders are 36,525 `CollisionShape3D` nodes with 36,525 unshared shapes.** Cold (memory, node count). Each of 12,175 trees has three solid parts, each a node with a new `BoxShape3D` or `ConvexPolygonShape3D` (`godot/MapLoader/MapLoader.Trees.cs:43-62`, `:87-101`; `godot/ShooterCore/Colliders/PropColliders.cs:179-202`), in at most 96 bodies. Placed objects get one body per part (`godot/MapLoader/MapLoader.Objects.cs:66-77`). Commit `c5df0d8` fixed the load time, not the count.
Fix: add shapes through `PhysicsServer3D` directly, with a shape cache keyed by kind and quantised size, keeping `PropColliders` as the single source. Effort M. CONFIRMED (*sampled*); memory UNVERIFIED. Sources: W3D-01, MC-18, W3D-09.

**T2-28 The terrain rim is built from a 1 m grid over the whole terrain box, in one unstepped block.** Cold hitch. `BuildTerrainBoundary` calls `GroundHeight` for every 1 m cell of the terrain's bounding box and allocates three arrays of that size, after the last progress step (`godot/MapLoader/MapLoader.cs:106`, `:118-163`). The time is already printed as `boundary` by the load log (`:108`).
Fix: walk the existing 8 m terrain grid, or sample at the terrain's 4 m step, and make it its own load step. Effort S to M. CONFIRMED; duration UNVERIFIED. Sources: W3D-11, DC-04, MC-16.

**T2-29 Map load runs entirely on the main thread and caches nothing.** Cold. Each chunk step parses the GLB, builds the scene and builds trimesh shapes on the main thread (`godot/MapLoader/MapLoader.cs:62-110`, `:237-244`, `:320`; `godot/shooter/Shooter.cs:172-177`).
Fix: export collision as its own binary so a worker can prepare it, and cache built shape data on disk keyed by the chunk hash already in the manifest. Effort M to L. CONFIRMED; gain UNVERIFIED, so read the existing per-phase log first. Source: W3D-13.

### Viewer and map editor

**T2-30 Each hover change rebuilds every outline for the whole map.** Hot (editor). Crossing a parcel edge sets `_outlinesDirty`, and `RebuildOutlines` regroups all parcels, recomputes every group perimeter and builds three new meshes, with a ground-height lookup per vertex (`godot/viewer/EditorMode.cs:545-586`, `:615-684`).
Fix: a dirty flag per outline kind so hover rebuilds only the hover mesh; cache group perimeters until `ReloadGroups` (`godot/viewer/EditorMode.cs:373`). Effort S to M. CONFIRMED; magnitude UNVERIFIED. Sources: W3D-05, VX-02.

**T2-31 The editor casts up to four physics rays per frame for one mouse position and scans all parcels for a group count.** Hot (editor). Hover, ghost, gate drag and the region tool each call `RayToGround` every frame (`godot/viewer/EditorMode.cs:193-208`, `:550`; `godot/viewer/EditorMode.Objects.cs:548-551`; `godot/viewer/EditorMode.Edit.cs:362`; `godot/viewer/EditorMode.Regions.cs:562`), and a hovered grouped parcel runs a LINQ scan and sort over all parcels each frame (`godot/viewer/EditorMode.cs:250-255`, `:562`).
Fix: one ground hit per frame, cached by mouse position and camera transform; a group-to-members index built in `ReloadGroups`. Effort S. CONFIRMED. Sources: W3D-06, VX-03, VX-04.

**T2-32 The region tool rebuilds a formatted signature and copies the region list every frame.** Hot (editor). `RegionWorld` builds a string from every draft and selected point each frame, `Regions()` returns a copy on every call and `Region(id)` scans it, and handles are repositioned in a per-frame loop with one `SphereMesh` each (`godot/viewer/EditorMode.Regions.cs:85-101`, `:551-568`, `:588-650`).
Fix: a version counter bumped on change; cache the selected region; return the cached list read-only; place handles only on change; share one mesh. Effort S. CONFIRMED. Sources: VX-01, W3D-22.

**T2-33 A fence, tree or region edit starts a Python process that rebuilds its indices every time.** Cold (edit latency). Placed objects are applied in process, but other edits run `uv run` parcel-run or region-run (`godot/viewer/EditsWatcher.cs:492-530`). Each run re-parses `parcels.json`, `buildings.json` and `roads.json` and rebuilds the spatial indices (`pipeline/src/drycastle_pipeline/parcel_run.py:121-127`, `:166-167`, `:280-283`); region-run parses all of `parcels.json` for two origin numbers (`pipeline/src/drycastle_pipeline/region_run.py:51`, `:54`) and `rules.yaml` is parsed with the pure-Python loader per call (`pipeline/src/drycastle_pipeline/rules.py:80-97`). `docs/parcel-run-timing.md` gives 0.3 to 0.85 s warm and lists pre-indexing as not done.
Fix: a long-lived pipeline worker, or a compact index written at map build and keyed by file times; read the origin from `frame.json`; cache parsed rules by modification time. Effort M to L. CONFIRMED. Sources: W3D-12, PY-04, PY-05.

### Map pipeline

**T2-34 Region-run clips terrain triangle by triangle and recomputes road ribbons per region.** Cold (edit latency and export). The region drape overlays the region with one triangle at a time and calls `terrain.surface` per triangle (`pipeline/src/drycastle_pipeline/regions.py:207-226`). Road ribbons test every terrain cell in an edge's bounding box against an unprepared union (`pipeline/src/drycastle_pipeline/roads.py:156-170`, `:207-210`) and are rebuilt for each region and again for the roads node (`pipeline/src/drycastle_pipeline/regions.py:145`, `:246`; `pipeline/src/drycastle_pipeline/scene.py:156`, `:161-165`).
Fix: `shapely.prepare` plus a vectorised `intersects` prefilter as `pipeline/src/drycastle_pipeline/terrain.py:266` already does; compute ribbon meshes once per build and pass them in; one batched `terrain.surface` call. Diff the GLB to prove identical output. Effort S to M. Path CONFIRMED (*sampled*); time UNVERIFIED. Sources: PY-01, PY-02, PY-03.

### Docker, build and deploy

**T2-35 No runtime budget for the server: no GC settings, no container limits.** Hot (UNVERIFIED). `godot/shooter/shooter.csproj:3-9` and `deploy/Dockerfile:11-12` set no GC options, and `deploy/docker-compose.yml:8-38` sets no memory or CPU limit, so the runtime sizes its heap from the host and the server and PostgreSQL can starve each other.
Fix: `mem_limit` for both services with a matching `DOTNET_GCHeapHardLimit`; pin cores with `cpuset` rather than a `cpus` quota (a quota throttles in bursts, which shows as tick jitter). Choose GC settings after measuring. Effort S. Absence CONFIRMED; effect UNVERIFIED. Sources: DOCK-02, DOCK-03.

**T2-36 Container logs are unbounded and the server writes a status line every 2 s.** Cold (disk). No `logging:` block (`deploy/docker-compose.yml:23-37`), so `json-file` grows without rotation; the 2-second line lists every player (`godot/shooter/Net/NetServer.cs:174-175`), about 50 MB a day at 16 players before hit and kill lines.
Fix: `logging: { driver: json-file, options: { max-size: "10m", max-file: "5" } }` on both services. Effort S. Rate CONFIRMED. Source: DOCK-04.

**T2-37 No init process and no stop grace period.** Cold (shutdown safety). The Godot binary is PID 1 (`deploy/Dockerfile:22`), compose sets neither `init` nor `stop_grace_period` (`deploy/docker-compose.yml:23-37`), and queued progression writes are flushed only in `_ExitTree` (`godot/shooter/Shooter.cs:105-109`). Docker kills after 10 s by default.
Fix: `init: true` and `stop_grace_period: 30s`. Effort S. UNVERIFIED. Source: DOCK-05.

**T2-38 The `chmod` layer duplicates the server binary, and one `COPY` layer holds binary, assets and map.** Cold (image size, deploy time). `RUN chmod +x` after `COPY --chown` copies the binary into a second layer (`deploy/Dockerfile:15-16`); there is no `.dockerignore`.
Fix: drop the `chmod` if the export already sets the bit, or use `COPY --chmod=0755`; copy assets, map and binary in separate layers, slowest-changing first; add a `.dockerignore` to the export folder. Effort S. Mechanism CONFIRMED; size UNVERIFIED. Sources: DOCK-10, DOCK-11.

**T2-39 Every deploy leaves the previous image behind.** Cold (disk). `deploy/deploy.sh:19` runs `up -d --build` and never prunes.
Fix: append `docker image prune -f`. Effort S. CONFIRMED. Source: DOCK-13.

## 4. Tier 3: Low impact and polish

Path prefixes: `s/` = `godot/shooter/`, `n/` = `godot/shooter/Net/`, `c/` = `godot/ShooterCore/`, `m/` = `godot/MapLoader/`, `v/` = `godot/viewer/`, `d/` = `godot/ShooterServer.Persistence/`, `p/` = `pipeline/src/drycastle_pipeline/`. Confidence: C = CONFIRMED, U = UNVERIFIED.

| # | area | path:line | hot/cold | effort | confidence | fix |
|---|---|---|---|---|---|---|
| T3-001 | server | `n/NetServer.cs:545-547`; callers `:576`, `:653`, `:684`, `n/NetServer.Pellets.cs:33`, `n/NetServer.Aircraft.cs:132`, `n/NetServer.Vehicles.cs:229`, `:336`, `n/NetServer.Missile.cs:35`, `:69` | hot | S | C | `LiveTargets()` builds a LINQ list per call: fill one reusable `List<HitTarget>` once per tick and pass it down |
| T3-002 | server | `c/Killstreaks/Helicopter/HelicopterHelperStreak.cs:95-99`; `n/NetServer.Helicopter.cs:60-70`; `n/NetServer.Turrets.cs:59-66`; `c/Net/WorldEntities.cs:150-155` | hot | S | C | Helper builds an enemy list and a full entity snapshot per tick for one position: add `WorldEntityManager.TryGet(id)` and pass a lazy enemy provider |
| T3-003 | server | `n/NetServer.Impacts.cs:27-35`; `c/Weapons/RoadSurfaces.cs:51-63`; `c/Colliders/PropColliders.cs:205-215`; `n/NetServer.Aircraft.cs:153-155` | hot | M | C | Impact surface lookup scans all 12,175 trees and 940 road segments (under 0.1 ms each): bucket both in a grid at load and classify only impacts that survive the gunship FX cap |
| T3-004 | server | `n/NetServer.Impacts.cs:24` | hot once | S | C | `roads.json` is parsed on the first impact, on a tick: parse at server start |
| T3-005 | server | `c/Weapons/HitMath.cs:150-181`, `:225`; `c/Weapons/ProjectileMath.cs:22-28`; `n/NetServer.Pellets.cs:22-24`, `:40` | hot | S | C | Hit tests box an enumerator per call and allocate a `float[2]` per capsule and lists per pellet fan: take a list or span, loop with `for`, reuse the buffers |
| T3-006 | server | `c/Weapons/WeaponCodec.cs:125-149`; `n/NetServer.cs:524`, `:537-540` | hot | S | C | Ammo message built with LINQ after every shot: write the fields straight into a buffer |
| T3-007 | server | `c/Aircraft/GunshipFx.cs:178-193`, `:210-255`; `n/NetServer.Aircraft.cs:91-101`, `:186`; `c/Aircraft/GunshipRouting.cs:35-88`; `c/Weapons/ProjectileMath.cs:62` | hot | S-M | C | Gunship path: LINQ flush per tick, a vehicle list per round, a `WeaponDef` per blast, gunner entries never removed: reuse lists, prebuild one blast def per weapon, drop empty gunners in `Flush` |
| T3-008 | server | `n/NetServer.cs:165`, `:173`, `:706`; `n/NetServer.Targets.cs:52-59`; `n/NetServer.Profile.cs:63`; `c/Net/WorldEntities.cs:135`; `c/Match/MatchSim.cs:206`; `c/Match/TargetDummies.cs:131`; `c/Killstreaks/KillstreakService.cs:212` | hot | S | C | Small always-on per-tick lists and `ToList()` copies: iterate in place or reuse fields |
| T3-009 | server, client | `n/NetServer.cs:640`, `:422`, `:528`; `n/NetServer.Vehicles.cs:403`; `n/NetServer.Killstreaks.cs:35`; `n/NetClient.cs:280`, `:724` | hot | S | C | `GD.Print` per hit and per event on the tick thread; the client prints the full scoreboard on every MatchState: gate behind a debug flag or log counters |
| T3-010 | server, client | `s/Player.cs:223`, `:229`, `:243-256` | hot | S | U | Head-clearance `TestMove` every tick while crouched or prone, and the capsule height is written every tick: recompute on stance change or movement only |
| T3-011 | server | `n/NetServer.Vehicles.cs:26-36`, `:76`; `s/ServerCar.cs:16-34` | cold | S | C | Startup: quadratic spawn clustering with LINQ and 119 rays per vehicle spot candidate: grid the candidates, cache rest heights |
| T3-012 | server | `n/NetServer.cs:359-367`; `n/NetServer.Camo.cs:17-24`; `c/Match/MatchSim.cs:130-137` | cold | S-M | C | Each join broadcasts a full scoreboard and sends every world entity as its own reliable message: coalesce the scoreboard to one per tick, batch the world catch-up |
| T3-013 | server | `c/Match/MatchSim.cs:268-276` | cold | S | C | Spawn chooser recomputes the nearest-player distance inside the sort comparator: compute once per candidate |
| T3-014 | server | `n/NetServer.cs:353`, `:373-384`; `n/NetServer.Vehicles.cs:138` | cold | S | U | Collision exceptions with a player body are never removed on `Drop`: call `RemoveCollisionExceptionWith` before freeing the body |
| T3-015 | server, client | `n/NetClient.Camo.cs:17`, `:36`; `n/NetClient.Sandbox.cs:34`, `:50`; `c/Net/WorldReplication.cs:182`, `:215`; `n/NetServer.Vehicles.cs:398`; `n/NetServer.Targets.cs:95`; `n/NetServer.cs:92`, `:173` | cold | S | C | Per-id maps never pruned (appearances, sandbox set, despawned ids, run-over cooldowns, moved-by-name): remove on leave or drop |
| T3-016 | server | `n/NetServer.Sandbox.cs:122-141` | cold | S | C | Sandbox vehicle spawns have no cap and the id can wrap: cap or recycle (dev only) |
| T3-017 | server, client | `c/Water.cs:52-60`, `:99-113`, `:242-271`; `s/Player.cs:224`, `:233` | hot | S | C | Water queries scan all bodies with a boxed enumerator, twice per player per tick; shore distance has no bounding-box reject: store bodies as an array, loop with `for`, prefilter by box (5 bodies today) |
| T3-018 | server | `c/Aircraft/Aircraft.cs:82-135` | hot | S | C | Aircraft recomputes the angle three times per serialise and a constant bank value: cache both |
| T3-019 | server, client | `c/WeaponData.cs:26`; `c/Killstreaks/KillstreakData.cs:41`, `:44`; `c/Perks/PerkData.cs:96`; `c/Camo/CamoFile.cs:69-70`; `n/NetClient.AttackHelicopter.cs:41-46`; `n/NetClient.Ac130.cs:21-27`; `n/NetClient.Missile.cs:114` | hot | S | C | Data-file `Find` is a LINQ scan with a closure, `UnlockLevels` is rebuilt per access, and the gunner HUD calls `Find` per frame: build dictionaries at parse, resolve the streak def once |
| T3-020 | server | `n/NetServer.cs:117` | cold | S | C | The ENet host accepts 32 peers and gives no "server full" message: set the intended cap, add `MaxPlayers`, reject with a reason |
| T3-021 | server | `s/StreetLamps.cs:44-85` | hot | S | C | The headless server runs `StreetLamps._Process` every frame: disable processing when headless (see T2-26) |
| T3-022 | net | `c/Net/Snapshot.cs:65-66` | hot | S | C | An empty snapshot packet is sent every tick: send the ack at 10 Hz when nothing changed |
| T3-023 | net | `c/Net/Snapshot.cs:30-31`; `c/Net/NetTypes.cs:14`; `c/Weapons/WeaponCodec.cs:46-53` | hot | S | C | Entity header is 4 B where 3 fit; a Shot payload is 27 B: pack type and flags into one byte, send Shot ends as offsets |
| T3-024 | net | `n/NetClient.cs:311-331`; `c/Net/Snapshot.cs:100-116`; `c/Net/Interpolator.cs:21`, `:33-45` | hot | S | C | Client does per-tick work per packet (LINQ stale sweep, staging list) and the interpolator shifts a list per push: sweep once per tick, reuse the list, use a ring buffer |
| T3-025 | net | `n/NetClient.cs:399-417`, `:465` | hot | S-M | C | Reconciliation searches and trims a list (`FindIndex`, `RemoveRange`, `RemoveAt(0)`): ring buffer indexed by sequence number |
| T3-026 | persistence | `n/NetServer.Profile.cs:87-113`; `d/Sqlite/SqliteStore.cs:63-100` | cold | S-M | C | A profile load is about ten sequential worker jobs, each resumed on the main thread, with default loadouts written again as separate commits: one read per profile, defaults inside the `GetOrCreate` transaction |
| T3-027 | persistence | `d/Sqlite/SqliteWorker.cs:86-89`; `d/Postgres/PostgresWorker.cs:94-97` | cold | S | C | Shutdown joins the worker with no timeout and never disposes the queue: join with a limit and log the unflushed count |
| T3-028 | client | `n/NetClient.cs:57-64`, `:662`, `:674`, `:685`, `:721`; `n/NetClient.GunshipFx.cs:17`; `n/NetClient.Cars.cs:96` | hot | S | C | Event logs read only by the smoke bots grow for the whole match (3 to 5 MB per hour): append only when a bot is active, or cap as a ring |
| T3-029 | client | `s/ArmsReadyPose.cs:74-115`; `s/CharacterFx.cs:69`, `:189-204` | hot | S | C | Arm IK and animation run for every armed remote character regardless of visibility: gate on a screen notifier or distance |
| T3-030 | client | `s/Menu/CustomizationScreen.cs:260-266`, `:382-400`, `:481-485`; `s/Menu/CamoPreview.cs:53`; `s/CharacterView.Camo.cs:37-57`, `:96-134`, `:152-262`, `:300-318`; `s/Menu/LoadoutScreen.cs:101-110`, `:600-630` | cold (menu) | M | C | The customiser rebuilds the whole look (materials, tufts, face texture, per-triangle bone lookups) on every slider step, and the loadout screen on every sync: coalesce to once per frame, cache the triangle list per body, skip when the spec is unchanged |
| T3-031 | client | `n/NetClient.Cars.cs:107-112`, `:128-132`; `n/NetClient.Corpses.cs:98-105`; `n/NetClient.CarePackages.cs:117-121`; `n/NetClient.Turrets.cs:110-116`; `s/Hud.cs:652-656` | hot | S | C | `RenderCars` sorts all cars with LINQ every frame and builds prompt and speed strings every frame: pick the nearest two in one pass, cache prompts by state |
| T3-032 | client | `s/Hud.cs:768-781` | hot | S | C | The sniper breath label and its key name are rebuilt every frame while visible: cache by phase |
| T3-033 | client | `n/NetClient.Water.cs:28-49` | hot | S | C | `RenderWater` builds two LINQ lists and tests every remote's depth each frame: prune where remotes are removed |
| T3-034 | client | `s/Minimap.cs:162-167`, `:213-231` | hot | S | C | The minimap redraws every frame, transforming and copying every visible road polyline: redraw only when position, yaw, dots or pings change |
| T3-035 | client | `s/DayNightCycle.cs:71-111` | hot | S | C | Day/night writes about 20 properties and 8 shader parameters every frame: skip when the hour has not moved; cache the parameter names |
| T3-036 | client | `n/NetClient.Aircraft.cs:125`; `s/EnemyMarkers.cs:27-73`; `s/GunshipOverlay.cs:117`, `:127`, `:167-175`; `s/MissilePovOverlay.cs:126` | hot | S-M | C | Gunner and missile views format the clock, project each marker twice and redraw all text every frame: format on whole-second change, project once |
| T3-037 | client | `n/NetClient.cs:464-468`, `:631`, `:637`, `:644`; `s/Player.cs:186-205`; `s/Hud.Respawn.cs:62-72`; `s/Hud.cs:689` | hot | S | C | Input path allocates per tick and passes action names as strings (interpolated `slot_N` included): static `StringName` fields, reuse the input buffers |
| T3-038 | client | `s/ShotFx.cs:11`; `s/Sfx.cs:47` | hot | S | C | `ShotFx.Enabled` calls `DisplayServer.GetName()` (a new string) on every sound and effect call: make it a static readonly bool |
| T3-039 | client | `s/Sfx.cs:143-155`, `:177-205`, `:215` | hot | S | C | Each sound allocates capturing lambdas and scans the voice pool: keep the delegates as fields, keep a free-voice list |
| T3-040 | client | `s/AircraftFx.cs:118-137` | hot | S | C | Rotor sound computes trig per sample and pushes one frame per native call, 22,050 times a second per aircraft: table lookup and one `PushBuffer` per frame |
| T3-041 | client | `s/BulletHoles.cs:140-150`; `s/MoabBanner.cs:79-93`; `s/Menu/UiShell.cs:163`; `s/Menu/Spinner.cs:18-23` | hot | S | C | Idle nodes keep running `_Process`: `SetProcess(false)` when idle |
| T3-042 | client | `s/Menu/UiShell.cs:67-68`, `:77-87` | hot | S | C | `UiShell` handles `SceneTree.NodeAdded` for the whole session, so every tracer and spark fires a callback: wire sounds per menu screen |
| T3-043 | client | `s/Menu/SettingsScreen.cs:74-78`, `:121`; `s/Settings.cs:111-132`, `:227-236` | cold (menu) | S-M | C | A settings slider step re-applies everything (about 24 input events rebuilt, window and V-Sync calls): apply only the section that changed |
| T3-044 | client | `s/CharacterFx.cs:224-234`; `s/WeaponFx.cs:47-95`; `s/CarFx.cs:37`, `:69`, `:84-138`; `s/CarLights.cs:36-59`; `n/NetClient.CarePackages.cs:42-60`; `n/NetClient.Turrets.cs:43-48`; `s/RocketFx.cs:77-112` | cold | S-M | C | Models, meshes and materials rebuilt per spawn (remote weapon swap, vehicle paint, car lights, crates, turrets, rockets): cache per id or colour as `WeaponViewmodel` and `TurretMaterials` already do |
| T3-045 | client | `s/UnderwaterView.cs:69-75`; `s/WeaponViewmodel.cs:57`; `s/HelperWarningOverlay.cs:53`, `:99`; `s/Hud.cs:813-836` | hot | S | C | Per-frame native lookups (bus effect, node meta, text size, viewport rect three times): store the value once |
| T3-046 | client | `c/Progression/LoadoutAutosave.cs:96-98`; `n/NetClient.Missile.cs:84-85`; `n/NetClient.cs:319`; `n/NetClient.Predict.cs:31`, `:120-129`, `:263`; `s/Hud.cs:522-534`; `s/BulletHoles.cs:132-137`; `n/NetClient.Pellets.cs:26-47`; `n/NetClient.Cars.cs:146-152` | hot | S | C | Small per-frame and per-shot allocations (a list per autosave poll, boxed dictionary enumerators, sound-id strings and a ray query per shot, ammo text, a car scan per pellet): return a cached empty list, loop over concrete types, precompute ids, reuse the query |
| T3-047 | client | `s/Menu/LoadoutScreen.cs:842-846`; `c/Icons/IconRegistry.cs:71-76`; `c/Progression/LoadoutEditor.cs:27-93` | cold (menu) | S | C | Icons are existence-checked and loaded per card per sync, and picker lists are re-sorted per call: memoise textures and sorted rows |
| T3-048 | client | `s/Sfx.cs:77-98`; `s/Shooter.cs:67` | cold | S | C | All 43 sounds are checked and loaded before the menus appear: load lazily or in a background step |
| T3-049 | client | `c/Hud/HudLogic.cs:31-35`, `:100` | hot | S | C | HUD helpers box enumerators over interface lists: `for` loops |
| T3-050 | rendering | `s/StreetLamps.cs:87-98`, `:118-132`, `:154-166` | hot | S | C | Lamp glow quads and the 12 pool lights are never hidden by day, and poles cast shadows: `Visible = level > 0`, `CastShadow = Off` (110 lamps) |
| T3-051 | rendering | `s/RocketFx.cs:16`, `:28-34`, `:157-160`; `s/VehicleFx.cs:111-116` | hot | S | C | All explosion particles are alpha-blended through an alpha of 0.99: opaque or additive for fire and sparks, alpha for smoke only |
| T3-052 | rendering | `s/shaders/underwater.gdshader:4`, `:13`; `s/shaders/gunship_thermal.gdshader:5`, `:29` | hot (mode) | S-M | C | Underwater and thermal overlays request a mipmapped screen copy every frame: unmipped copy with a few taps; compute average luminance every N frames |
| T3-053 | rendering | `n/NetClient.Impacts.cs:99-109` | hot | S | U | The pooled impact emitter changes `Amount` per impact, which reallocates its buffers: one pool per size tier |
| T3-054 | rendering | `s/WeaponViewmodel.cs:12-23`; `s/CharacterView.Camo.cs:258` | hot | S | C | The first-person viewmodel casts shadows nobody sees: `CastShadow = Off` |
| T3-055 | rendering | `assets/quaternius-stylized-nature-megakit/Leaves.png` | cold | S | C | A 2048 x 2048 leaf texture is loaded once per foliage glTF that references it: downscale to 512 or share an atlas |
| T3-056 | rendering | `s/shaders/camo_suit.gdshader:20-66` | hot | M | C | Camo suit shader computes 3D value noise per fragment: bake to a 3D texture |
| T3-057 | rendering | `s/shaders/water_surface.gdshader:4`, `:11`, `:25`; `s/WaterSurfaces.cs:37` | hot | M | C | Water is alpha-blended and reads depth: opaque water with a shore band, or a per-vertex shore factor |
| T3-058 | rendering | `v/Viewer.cs:138-139`, `:297` | hot (editor) | S | C | Viewer shadow distance is 250 m and shots use MSAA 4x: match the shooter settings after T2-21 |
| T3-059 | rendering | `godot/shooter/ui/icons/weapons/*.png.import` | cold | M | C | UI icons are 47 uncompressed textures without mipmaps: an atlas, or VRAM compression with mipmaps |
| T3-060 | rendering | `m/MapLoader.Objects.cs:208-216` | cold | S | C | Each coloured placed object gets its own material and each placeholder its own mesh: cache by colour |
| T3-061 | rendering | `godot/shooter/project.godot` | hot | M | U | The renderer is the default Forward+ although the target is weak GPUs: compare with Mobile before changing |
| T3-062 | world | `m/MapLoader.cs:317-349` | hot | M | C | One static trimesh body per parcel collision mesh (399 bodies): one body per chunk with per-parcel shapes |
| T3-063 | world | `m/MapLoader.cs:27`, `:320-333`, `:364-393` | hot, memory | M | C | Terrain collision is a trimesh with backface collision, and its face arrays and grid are kept for the map's life: a `HeightMapShape3D` per chunk, keep only a height grid |
| T3-064 | world | `m/MapLoader.cs:263`, `:290-300` | cold | S | C | `TriangleCount` copies every surface array to fill a viewer-only statistic: read the surface lengths, or skip outside the viewer |
| T3-065 | world | `m/MapLoader.cs:68`, `:78-82` | cold | S | C | JSON files are read as strings then parsed, and three are kept for the session: parse from bytes, drop the strings after use |
| T3-066 | world | `m/MapLoader.cs:253`, `:281-288`, `:418-429`; `c/Water.cs:172-184` | cold | S | C | Recursive `GetChildren` walks, LINQ in parsers and a list of every terrain height for a mean: `FindChildren`, plain loops, a running sum |
| T3-067 | world | `m/MapLoader.Objects.cs:261-265` | cold | S | C | Removing a group's objects scans every placed object per parcel: keep a group-to-objects map |
| T3-068 | world | `c/GameData.cs:20`; `c/Camo/CamoFile.cs:202`; `c/Weapons/PlayerWeapons.cs:79` | cold | S | C | Default camo JSON parsed on every construction; a list copied per name lookup; `Hotbar` builds a list per read: lazy default, array lookup, cached array |
| T3-069 | editor | `v/EditorMode.Edit.cs:332-400` | hot (drag) | S | C | A gate drag frees and rebuilds the preview nodes, meshes and material every frame: build once, update on change |
| T3-070 | editor | `v/EditorMode.cs:324-351`; `v/EditsWatcher.cs:155-224`, `:337-401`; `v/EditsFile.cs:57`, `:143-179` | cold | M | C | One save parses `edits.json` five to six times, builds two canonical strings per entry and reprints every number (11 KB today, so sub-millisecond): keep the saved document, hash raw text, canonicalise at write |
| T3-071 | editor | `v/EditorMode.cs:250-255`, `:597`, `:694`, `:712`; `v/EditorMode.Edit.cs:47`, `:618-619` | cold | S | C | Group membership is found by scanning all parcels in several places, and gate markers and outlines are rebuilt on every panel refresh: use the member index from T2-31, rebuild only on change |
| T3-072 | editor | `v/EditorMode.cs:506-507`; `v/EditorMode.Objects.cs:321` | hot | S | C | The editor HUD line and a gizmo key string are built every frame: rebuild when an input changes |
| T3-073 | editor | `v/ObjectEdits.cs:31-43`; `v/EditorMode.Objects.cs:73-76`, `:744-771` | cold | S | C | Placed-object lookups are linear and repeated within one refresh: index by id |
| T3-074 | editor | `v/EditorMode.Regions.cs:767-774`, `:838-857` | cold | S | C | The region list rebuilds every row and swatch texture on each selection: toggle the pressed row, cache swatches by colour |
| T3-075 | editor | `v/EditorMode.Regions.cs:955-989` | cold | S | C | The palette re-sorts all parcels on each keystroke: a sorted lowercase index built once |
| T3-076 | editor | `v/EditorUi.cs:304`, `:343-355`; `v/EditorMode.Regions.cs:259`, `:701-764`; `v/EditsWatcher.cs:566-590` | cold | S | C | Config and state files are written on each tool switch and each colour-drag step: debounce |
| T3-077 | editor | `v/EditorMode.Regions.cs:623`, `:809`, `:828`; `v/ObjectEdits.cs:230-292` | hot (drag) | M | C | Full quadratic ring validation on every drag step: check only the edges next to the moved corner |
| T3-078 | editor | `v/IconGlyph.cs:25-125` | hot | S | C | Every icon polls its state each frame and allocates in `_Draw`: redraw from signals |
| T3-079 | editor | `v/EditorMode.Objects.cs:187-223` | cold | M | C | Object picking is linear and the bounds cache is cleared wholesale at 4,000 entries: bucket by parcel, drop entries when nodes are freed |
| T3-080 | editor | `v/AssetThumbs.cs:46-89` | cold | S | C | Thumbnail queue uses a linear `Contains` and one GPU readback per asset: a set; optionally cache thumbnails on disk |
| T3-081 | editor | `godot/netspike/ServerCore.cs:109-116`; `godot/netspike/CustomServer.cs:108-172` | hot (spike) | S | C | The spike's own measurement loop is quadratic and inflates what it measures: index players by id |
| T3-082 | editor | `v/asset_check.gd:108-113` | cold | S | C | The asset check copies all geometry to count triangles: read surface lengths |
| T3-083 | pipeline | `p/props.py:126-208` | cold | M | C | Fences overlay each parcel against global unions and a growing `done` union: query an `STRtree` for local geometry |
| T3-084 | pipeline | `p/props.py:302-318`; `p/mcp_server.py:866` | cold | M | C | Tree placement is quadratic in the count, and `set_trees` accepts any count: batch the containment tests, use a spacing grid, cap the count |
| T3-085 | pipeline | `p/props.py:341-406` | cold | S-M | C | Spawns build a union of one buffer per tree (12,175): a distance query on a tree index |
| T3-086 | pipeline | `p/propmeshes.py:11-111` | cold | M | C | Tree and fence meshes are built one box at a time in Python: broadcast a template with numpy |
| T3-087 | pipeline | `p/export.py:117`, `:215-232`; `p/roads.py:302`; `p/props.py:601`, `:610` | cold | S | C | Generated map JSON is written with `indent=1` (`trees.json` is 1.8 MB, `parcels.json` 1.15 MB), and the map loader parses it: compact separators for everything under `out/` |
| T3-088 | pipeline | `p/export.py:155-158`; `p/scene.py:147-152`; `p/cli.py:131-137` | cold | L | C | Export rebuilds everything on any change and builds the terrain mesh twice per chunk: hash inputs per node group and reuse unchanged chunks |
| T3-089 | pipeline | `p/props.py:258`, `:266-267`; `p/lamps.py:123` | cold | S | C | `terrain.surface` and coverage tests are called per tree, lamp and spawn: one vectorised call |
| T3-090 | pipeline | `p/rules.py:80-97` | cold, hot for MCP | S | C | `rules.yaml` is parsed with the pure-Python loader on every `load_section` call (about 17 per run, and per MCP `get_parcel`): cache by modification time, use the C loader |
| T3-091 | pipeline | `p/parcels.py:79-113` | cold | S | C | Parcel classification overlays every parcel against global unions and computes one ratio twice: index the inputs, keep the ratio |
| T3-092 | pipeline | `p/mcp_server.py:475-489`, `:1120-1186`, `:1242`; `p/edits.py:409` | hot (MCP) | S | C | An MCP write validates the file three times and `create_group` reloads it: validate once |
| T3-093 | pipeline | `p/registry.py:126-145` | cold | S | C | Registry matching is a nested loop of polygon overlays over unmatched entries (dormant today): query an index first |
| T3-094 | pipeline | `p/props.py:162` | cold | S | C | The garden test measures distance to the union of all buildings: a `dwithin` query on a building index |
| T3-095 | pipeline | `p/stages.py:9`; `p/fetch.py:9-11`; `p/export.py:14`; `p/cli.py:129` | cold | S | C | Every stage command imports `rasterio` and `trimesh` at start: import inside the functions that need them |
| T3-096 | pipeline | `p/normalise.py:186-200`; `p/frame.py:44-54` | cold | S | C | Reprojection runs once per feature, 12k trees included: transform each layer as one array |
| T3-097 | pipeline | `p/scene.py:47-54`, `:75-85` | cold | S | C | Colour and chunk assignment loop in Python: palette table and one `np.unique` |
| T3-098 | pipeline | `p/props.py:76-79`, `:138`, `:238`, `:348` | cold | S | C | The road zone union is built three times in one props run: build once and pass it in |
| T3-099 | pipeline | `p/preview.py:201-276` | cold | M | C | The preview builds one matplotlib collection of every triangle, twice: build layers once; rasterise with numpy |
| T3-100 | pipeline | `p/lamps.py:109-111` | cold | S | C | Lamp placement copies the array per accepted lamp: append to a list |
| T3-101 | pipeline | `p/terrain.py:178-186`, `:266-267` | cold | S | C | The DMR is read as float64 and a full boolean grid is allocated per building: float32, one reusable grid |
| T3-102 | pipeline | `p/cache.py:56-73`; `p/fetch.py:304-309` | cold | S | C | Fetch re-hashes every cached file on each run and re-parses two files to count: compare size and time first |
| T3-103 | pipeline | `p/meshes.py:111-122` | cold | S | C | One overlay per wall edge and a list search per point: vectorise, track the index |
| T3-104 | pipeline | `p/props.py:484-514` | cold | S | C | Object-to-parcel containment tests every object against every parcel: query the parcel index |
| T3-105 | pipeline | `p/mcp_server.py:289-291` | cold | S | C | `sides()` takes three distances to the whole road union per side: nearest query on road pieces |
| T3-106 | pipeline | `p/registry.py:86-91` | cold | S | C | The 790 KB registry is pretty-printed and rewritten on every `make parcels`: skip when unchanged |
| T3-107 | deploy | `Makefile:248`, `:252` | cold | S | C | About 14 MB of client-only models, textures and sounds are copied into the server image: copy `assets/` only for client exports, update `docs/shooter-server.md:40` |
| T3-108 | deploy | `deploy/Dockerfile:3-9` | cold | M | U | The base is `debian:bookworm-slim` plus apt: try a distroless glibc base after checking `libssl3` |
| T3-109 | deploy | `deploy/deploy.sh:15-19` | cold | M | C | The image is built on the VPS from an rsynced folder on every deploy: build once, push to a registry, pull on the VPS |
| T3-110 | deploy | `deploy/docker-compose.yml:23-37`; `n/NetServer.cs:139` | cold | S | C | The server has no healthcheck, so deploy reports "up" before the map is loaded: write a ready marker at `SHOOTER_NET_LISTENING`, add a healthcheck, deploy with `--wait` |
| T3-111 | deploy | `Makefile:247-251` | cold | S | C | A Debug `dotnet build` runs before three release exports that each publish again: remove it, allow exporting one preset |
| T3-112 | deploy | `Makefile:252`, `:258` | cold | S | C | Packaging always re-exports and copies the map and assets three times: incremental copy, real file targets |
| T3-113 | deploy | `godot/shooter/shooter.csproj:14` | cold | L | C | The client export ships the persistence assembly with Npgsql and SQLite: split a server project (measure the size first) |
| T3-114 | deploy | `godot/shooter/shooter.csproj:3-9`; `deploy/Dockerfile:11` | cold | S | C | Invariant globalisation is set only as a container environment variable: add `InvariantGlobalization` to the project |
| T3-115 | deploy | `deploy/Dockerfile:3`; `deploy/docker-compose.yml:9` | cold | S | C | Base and database images use floating tags: pin by digest |
| T3-116 | deploy | `deploy/docker-compose.yml:36-37` | hot | S | U | The game's UDP port goes through Docker NAT and the userland proxy: `network_mode: host` or disable the proxy, after measuring |
| T3-117 | deploy | (no CI configuration in the repo) | cold | M | C | No CI: when added, cache Godot export templates, NuGet and uv, and build the image there |
| T3-118 | deploy | `Makefile:6-7` | cold | S | C | `make setup` installs the pipeline dev group: `uv sync --no-dev --frozen` in CI and containers |

## 5. Quick wins

Effort S, Tier 1 and Tier 2, grouped so each group can ship as one pull request. Tier 3 rows that touch the same lines are listed so they can ride along.

1. **Server runtime hygiene.** T1-1 (frame cap), T2-05 (`max_physics_steps_per_frame`), T2-07 (telemetry line). Ride along: T3-009 (per-hit prints), T3-021.
2. **Compose and deploy script.** T2-36 (log rotation), T2-37 (`init`, `stop_grace_period`), T2-39 (image prune), T2-38 (drop the `chmod` layer), T2-35 (memory limits). Ride along: T3-115.
3. **Server per-tick reuse.** T2-02 (cached exclude array), T2-03 (encoder scratch and early return), T2-10 (encode once), T2-01 (turret re-pick timer). Ride along: T3-001, T3-002, T3-008.
4. **Keyframe stop-gap.** `Wire.KeyEvery` 30 to 10 as the interim for T1-2, measured with the existing loss test.
5. **Map loader: materials and shadows.** T2-20 step 1 (shared material), T2-21 (`CastShadow` off on terrain and props, explicit shadow distance), T2-28 (stepped boundary). Ride along: T3-064, T3-050, T3-054.
6. **Headless map load.** T2-26 (collision-only flag, lamp collision only).
7. **Client leave and load.** T2-17 (free world nodes in `Teardown`), T2-18 (warm models and shaders), the distance gate from T2-15, the per-map cache from T2-19.
8. **Persistence safety.** `PRAGMA synchronous=NORMAL` from T2-13, connection and command timeouts plus the log rate limit from T2-14. Ride along: T3-027.
9. **Receive budget.** T2-06.
10. **Editor per-frame work.** T2-31 (one ground hit per frame, member index), T2-32 (version counter), the hover-only rebuild from T2-30. Ride along: T3-071, T3-072.
11. **MSAA default.** T2-23, after one measurement.

## 6. Needs measurement

Each item is UNVERIFIED in size or behaviour. The cheapest confirmation comes first.

| Finding | What is unknown | Cheapest way to confirm |
|---|---|---|
| T1-1 | Whether the uncapped headless loop spins a core | `docker stats --no-stream` on an idle server; or `top -H -p $(pgrep -f drycastle-server)` |
| T2-20, T2-21, T2-22 | Frame cost of 998 draws, shadow passes and missing ranges | Godot debugger monitors (draw calls, objects drawn, primitives) and the visual profiler's shadow pass, from a spawn looking down the village, before and after each change |
| T2-23 | Cost of MSAA 4x | GPU frame time at MSAA off, 2x and 4x on the same view |
| T3-061 | Forward+ against Mobile | Same view and MSAA, both renderers |
| T2-24 | Draws per character | Objects-drawn monitor with 16 characters in view |
| T2-26 | Server startup time and memory saved | The existing `map load phases` log line and `docker stats` memory after load, with and without the flag |
| T2-28 | Duration of the terrain rim step | The `boundary` value already printed by `godot/MapLoader/MapLoader.cs:108` |
| T2-29 | Whether chunk loading still dominates | The same `map load phases` line (`chunks` value) |
| T2-27 | Memory and node count of tree colliders | `Performance` monitors for object and node count before and after `LoadSteps` |
| T2-19 | Minimap rasterise time | A `Stopwatch` around `Rasterise(2048)` |
| T2-18 | First-use hitch size | Frame-time graph over the first shot and the first remote player of a match |
| T2-15, T2-16 | Node churn under fire | Object-count monitor and `dotnet-counters` allocation rate with 16 bots firing; log `Blasts.Count` per gunship message |
| T2-01 | Turret rays per second | Count `TurretRay` calls in a match with several turrets, once T2-07 exists |
| T2-04 | Cost of a parked `MoveAndSlide` | Tick time with 29 parked vehicles against `--vehicle-set` with none, in loss-test mode |
| T2-03, T2-10, T3-001 | Allocation rate on the tick | `dotnet-counters monitor System.Runtime` (allocation rate, gen 0 count, time in GC) with 16 bots |
| T2-05 | Godot's default physics step cap | Read `physics/common/max_physics_steps_per_frame` in the project settings dialog |
| T2-08 | Real traffic at 16, 32 and 64 players | The loss test's bytes-per-second counters at each bot count |
| T2-09 | Head-of-line delay under loss | Loss test at 10 % loss, timing Hit and Kill delivery during automatic fire |
| T2-12, T2-13 | Commit latency and queue depth | Time the worker's job duration in a kill-heavy bot run; `EXPLAIN ANALYZE` is not needed, every query uses a primary key or unique index |
| T2-14 | Npgsql default timeouts | Stop the `db` container during a bot run and time the worker loop |
| T2-35 | GC behaviour without limits | `dotnet-counters` on the container; read `*.runtimeconfig.json` in the export |
| T2-37 | Whether SIGTERM reaches `_ExitTree` in time | `docker compose stop -t 30 server`, then check the logs and the last kill row |
| T2-38, T3-108 | Layer and base image sizes | `docker history --no-trunc <image>` after one build |
| T3-116 | Latency added by Docker NAT | Loss test or ping comparison with `network_mode: host` |
| T2-34, T2-33 | Region-run and parcel-run time split | The `timing_s` values both commands already print, per region, before and after |
| T3-083 to T3-106 | Which pipeline stage dominates a full run | `cProfile` on `python -m drycastle_pipeline export` and `props`; `python -X importtime` for T3-095 |
| T3-010 | Whether the capsule setter early-outs on an unchanged value | Count shape updates per tick in the profiler |
| T3-014 | Whether freed bodies drop their collision exceptions | Read a vehicle's exception count after 50 join and leave cycles |
| T3-053 | Cost of changing `Amount` | Profile `OnImpactFx` |

## 7. Healthy areas

Checked and found fine:

- **Past fixes hold.** Tree bodies are filled detached and attached once per chunk (`c5df0d8`); gunship effects are coalesced with caps (`253f75f`); placed-object edits apply in process (`d1d71de`); map load is stepped and frames over 250 ms are logged.
- **Server tick.** No blocking waits on the tick: database work is on one worker thread, saves are fire-and-forget, and failures are logged without reaching the match. Input queue capped at 8 with three-input redundancy and sequence dedupe. No quadratic player-versus-player loop. Vehicles are kinematic with one move per tick and no per-wheel rays. Hit detection is analytic.
- **Protocol.** Records are value types; snapshot packets stay under the MTU at any entity count (40-entity cap); keyframes are staggered; a pellet fan is one seeded event; the measured 4.24 kB/s per client at 16 bots is small.
- **Persistence.** Every query is served by a primary key or unique index in both migration sets; commands, readers and transactions are disposed; migrations are cheap at start. PostgreSQL defaults are adequate for this write rate.
- **Client.** HUD setters are change-guarded and the scoreboard redraws only when dirty; bullet holes, sound players, impact emitters and muzzle flashes are pooled; models, the theme and prepared character meshes are cached; `GameSettings.Changed` subscribers all unsubscribe; corpses, kill feed and undo stacks are capped.
- **Rendering.** Trees and fences are merged into one surface per parcel; foliage uses alpha scissor, not blending; lamp and car light budgets are capped and shadowless; the sky shader is cheap; overlays are hidden when inactive; the map uses vertex colours and no textures.
- **Physics.** Jolt at 30 ticks per second; shot-only cover is on its own layer; lamp poles share one body and one shape.
- **Editor.** No edit triggers a full map reload; undo stores per-entry deltas and is capped at 200; drags write on release; the file watcher's 0.5 s stat poll is cheap; parcel picking uses the grid index.
- **Pipeline.** The MCP server caches the map by modification time and its per-call work is small; the HTTP client reuses one session with bounded retries; coordinates are rounded at the source; `parcel_run.py` already uses local `STRtree` neighbourhoods.
- **Deploy.** Non-root user, minimal apt install with cache cleanup, exec-form entrypoint, release exports are optimised and carry no debug symbols, the server pack holds almost no rendering resources, the database has a healthcheck and is not exposed.

## 8. Notes outside the performance scope

Seen while auditing, not counted as findings:

- `docs/shooter-server.md:40` says the server validates every sound against `assets/`; it no longer does. The same doc says SQLite foreign keys are off; `godot/ShooterServer.Persistence/Sqlite/SqliteWorker.cs:31` turns them on.
- `ProjectileMath.WithBlast` (`godot/ShooterCore/Weapons/ProjectileMath.cs:62`) copies only four fields of a `WeaponDef`; every other field reads zero on the copy.
- `pipeline/src/drycastle_pipeline/roads.py:183-191` defines `_area2` twice and repeats the sliver check at `:222-225`.
- The database password travels in environment variables, and backups are a documented manual step with nothing scheduled.
- The editor's watcher detects changes by size and modification time only, so a same-size rewrite within one timestamp tick would be missed.
- Not covered by this audit: test and smoke-script run time, and a full read of `Hud.Streaks.cs`, `Hud.Uav.cs`, `Hud.Sandbox.cs`, `CorpseChooser.cs` and `MoabBanner.cs` for redraw cost (they were read in full for allocations only).
