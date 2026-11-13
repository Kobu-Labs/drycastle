# B. Lag compensation and hit registration

Read-only investigation (agent B). Scope: `godot/ShooterCore/Weapons/HitMath.cs`, shot resolution in `godot/shooter/Net/NetServer.cs` (`FireWeapon`, `Deal`, `StepRockets`, `Explode`), pellets, melee, the rockets and killstreak weapons that resolve hits on the server. No code was built, run or changed. Every number is either a citation or arithmetic written out below. Confidence tags: CONFIRMED (read in code or docs, or arithmetic on cited constants), UNVERIFIED (needs a run or a number not in the repo), OPINION.

Terms follow `GLOSSARY.md`: Shot, Pellet, Killstreak, Vehicle, Rider, Glass pane, Hit volume. "Lag" below means the shooter's view age, defined in B-01.

## 1. Verdict

**Fit with changes.** The server is authoritative, and its hit tests are already analytic and deterministic (`HitMath.cs:200-216`, `:285-304`, `:133-149`). The snapshots are tick-stamped and every remote entity is drawn at one interpolation delay (`NetClient.cs:20`, `:536`; `NetClient.Cars.cs:167`), which is exactly the time a rewind needs. So rewinding is cheap: a player history ring is about 6 kB, adds no wire bytes, and costs roughly 1 to 2 µs per shot. The change is needed at any latency, not only at high ping. A shooter's drawn target is already about 100 ms old at zero RTT, so a walking target moving across the aim at 3.7 m/s or faster is missed by the body radius (0.56 m) even on a LAN. The answer flips to "build" at RTT of about 0 ms for sprinters and cars, and at about 12 ms for walkers. Players should be rewound first (S to M), vehicles second (M), rockets later. The design fails to fit above about 150 ms RTT with a 250 ms cap, where vehicles keep a residual of more than a metre. Load does not break it: the history grows linearly with players (about 25 kB at 64 players).

## 2. What is healthy

- **Analytic hit tests, no physics per player.** Capsule and head sphere, with the head counted only when the ray reaches it soon after entering the body: `godot/ShooterCore/Weapons/HitMath.cs:200-216`, `RayCapsule` `:235-`, melee `:285-304`. The audit already notes "Hit detection is analytic" (`docs/perf-audit.md:394`).
- **Server owns hits and tells both sides.** `Deal` applies damage and sends one reliable `Hit` to the attacker and the victim (`godot/shooter/Net/NetServer.cs:811-830`). Clients do not predict a hit, so there are no false kill markers (`godot/shooter/Net/NetClient.Predict.cs:114-145` predicts only sound, flash and a tracer from the camera ray).
- **Deterministic pellet fans.** The fan is rebuilt from a seed on both sides (`godot/ShooterCore/Weapons/HitMath.cs:133-149`, `godot/shooter/Net/NetServer.Pellets.cs:20-21`, `godot/shooter/Net/NetClient.Pellets.cs:26`). A rewind does not change the event format.
- **One time base for drawn remote things.** Players, vehicles, animals, aircraft and the helicopter's flight all use `rt = _lastTick + elapsed*30 - InterpDelayTicks` with `InterpDelayTicks = 3` (`godot/shooter/Net/NetClient.cs:20`, `:536`; `NetClient.Animals.cs:89`; `NetClient.Aircraft.cs:101`; `NetClient.Cars.cs:167`). Clients see the world at a single past time, so the server can reconstruct it.
- **Players do not block shots.** Player bodies are excluded from every bullet ray (`godot/shooter/Net/NetServer.cs:708-715`, `:727`). Only the world, vehicles and shot-only cover stop bullets (`godot/ShooterCore/Colliders/PropColliders.cs:29`). A rewind therefore only has to move targets, not occluders, for players.
- **Dummies are static** (`godot/shooter/Net/NetServer.Targets.cs:44-45`), so they need no history.
- **Round-trip time is already on the server.** `Ping` reads ENet's `RoundTripTime` per peer (`godot/shooter/Net/NetServer.cs:584-585`).
- **Precedent for trusting a bounded client view.** The gunship's aim point is sent by the client and clamped (`docs/shooter-v2.md:182-192`, `GunshipAim.ClampAim`), shipped at protocol 15. It trusts the view, not the hit, which is the line ADR 0006 draws.
- **Headroom.** Server tick p50/p95 is 1.6/2.6 ms against a 33 ms budget (`docs/spikes/shooter-loss-test.md:31`). A shot is at most a few tens of µs of extra work (B-06 costs below).

## 3. Mechanism: how far behind the shooter's picture the server resolves

Let the shooter's input be sent at server-time `S`, and the server resolve it on arrival at `T = S + OWD`. The shooter draws remote things from the newest snapshot it holds, delayed by 3 ticks (`NetClient.cs:536`). The newest snapshot it holds was sent at `S - OWD`, so the drawn world is at `S - OWD - 100 ms`. The server resolves at `T = S + OWD`, so the gap is

    L = T - (S - OWD - 100 ms) = 2·OWD + 100 ms = RTT + 100 ms

plus input queueing on the server (0 to one tick normally, `NetServer.cs:590-594`). The table uses `L = RTT + 100 ms` (CONFIRMED arithmetic on `NetClient.cs:20`, `:536`; RTT from `NetServer.cs:585`).

Lateral error of a hitscan against a target moving at speed `v` perpendicular to the ray is `v·L`, independent of distance. The hit tolerance is the body capsule radius `PlayerHitBox.Radius = 0.56 m` (`godot/ShooterCore/Weapons/PlayerHitBox.cs:28`) and the head radius `0.25 m` (`:31`). Speeds: walk 5, sprint 8 (`godot/ShooterCore/Movement.cs:8-9`), car 44.4, jeep 30, utility helicopter 40 (`godot/shooter/data/vehicles.json:19`, `:40`, `:150`), killstreak helicopter 38 (`godot/shooter/data/killstreaks.json:29`).

| target, speed | RTT 0 (L 100 ms) | RTT 50 (L 150 ms) | RTT 100 (L 200 ms) | RTT 200 (L 300 ms) |
|---|---|---|---|---|
| walk 5 m/s | 0.50 m | 0.75 m | 1.00 m | 1.50 m |
| sprint 8 m/s | 0.80 m | 1.20 m | 1.60 m | 2.40 m |
| jeep 30 m/s | 3.0 m | 4.5 m | 6.0 m | 9.0 m |
| car 44.4 m/s | 4.4 m | 6.7 m | 8.9 m | 13.3 m |
| heli 40 m/s | 4.0 m | 6.0 m | 8.0 m | 12.0 m |

Body hit (0.56 m) fails at perpendicular speed above 5.6 / L: 3.7 m/s at RTT 50, 2.8 m/s at RTT 100, 1.9 m/s at RTT 200. Head hit (0.25 m) fails above 2.5 / L: 1.7 m/s at RTT 50, 1.2 m/s at RTT 100. A target walking across the shot is therefore missed at any RTT for headshots, and at RTT 50 for body shots. The error is always "behind" the target (the shot lands where the target was), so tracking the target with the crosshair does not cancel it: only leading by `v·L` does. OPINION on what players do; CONFIRMED arithmetic.

What the server does today: `FireWeapon` takes the shooter's eye from the present body (`NetServer.cs:747-748`), the targets from `LiveTargets()` at present (`:684-693`, `:756`), and walls from a physics ray at present (`:722-732`). The doc comment says so: "no lag compensation" (`NetServer.cs:737`, `godot/ShooterCore/Weapons/HitMath.cs:11-14`). ADR 0002 anticipated the need ("A shooter also needs tick-stamped snapshots for interpolation and lag compensation", `docs/adr/0002-custom-enet-snapshot-layer-over-builtin-sync.md`). ADRs 0006, 0010, 0011 and 0012 each state that the server resolves shots "from its own state, as every shot is" (`docs/adr/0006-...md` consequences; `0010` consequences; `0011` consequences; `0012` "What a rider is to a bullet"). A rewind amends that sentence; it does not contradict a decision.

Per-weapon consequences (CONFIRMED arithmetic on the cited constants):

- **Hitscan rifles and pistols** (`godot/shooter/data/weapons.json`, kind `hitscan`, fire intervals 0.066 to 0.15 s): a sprinter at RTT 50 is 1.2 m off the drawn position against a 0.56 m body radius; a walker at RTT 50 is 0.75 m off, 0.19 m beyond the radius. Snipers have longer ranges (600 to 800 m) and the same metres of error at any distance. CONFIRMED arithmetic.
- **Shotguns** (ids 15 to 18: 8 pellets, `pellet_spread` 3.0 to 3.5 degrees, range 60 m, `weapons.json`): the fan radius is `d·tan(3°) = 0.052·d`, and a pellet hits within the body radius, so the usable lateral reach is `0.052·d + 0.56`. At 10 m that is 1.08 m: a sprinter at RTT 0 (0.8 m) is still covered, at RTT 50 (1.2 m) and RTT 100 (1.6 m) the whole fan misses. At 30 m the reach is 2.2 m and covers a sprinter at RTT 100. Short-range shotgun fights against moving targets are where the lag bites hardest. CONFIRMED arithmetic.
- **Melee** (`weapons.json` id 3: reach 1.8 m, 60 degree cone; `HitMath.cs:285-304`): the test is `len - PlayerHitBox.Radius <= reach`, so reach is 2.36 m from the target's feet. A target retreating at 8 m/s from 2.3 m gains 1.2 m at RTT 50, 1.6 m at RTT 100 and 2.4 m at RTT 200 before the knife lands, so the knife misses at every RTT. CONFIRMED arithmetic (the melee has no lateral tolerance to absorb it).
- **Rockets** (`weapons.json` id 2: 40 m/s, 3.75 s life; splash 6 m, full damage to 1.5 m, edge 20; `ProjectileMath.cs:45-53`): a rocket explodes only when its sweep meets a player (`RocketFlight.cs:36`) or at a world hit or expiry (`NetServer.cs:850-863`). There is no proximity fuse. A rocket that passes 1.5 m from a sprinter at RTT 100 does not explode and the target is not hit; the splash never sees it. The lag adds to the flight-time lead problem, which no rewind removes. CONFIRMED (fuse); arithmetic on the lateral error.
- **Gunship rounds and shells** (`docs/shooter-v2.md:182-230`): the aim point is the client's (B-10 below); the damage to bodies is resolved at present by `GunshipBurst` (`NetServer.Aircraft.cs:202-230`). A 3 m blast (`docs/shooter-v2.md:227`) covers the 1.2 to 1.6 m lag of a sprinter, so the damage mostly lands through splash. CONFIRMED arithmetic.
- **Shots at moving vehicles** (B-02): a car broadside at 44 m/s is 6.7 m off at RTT 50 and 8.9 m off at RTT 100, against a 1.28 m half-width (`vehicles.json:19`). A car shot head-on is still missed beyond its 2.18 m half-length. Only a stationary or slow vehicle is reliably hit at any RTT.

## 4. Findings

Verified: `docs/perf-audit.md` has 159 findings and none of them is about lag compensation or hit registration. Items B-01 to B-08 are new. Status of the audit's items in this area is in B-09.

### B-01 Hitscan and pellet hits resolve against present positions; drawn targets are `RTT + 100 ms` old

- **Mechanism:** see section 3. The shooter aims at what it draws, which is `RTT + 100 ms` old when the server resolves.
- **Citation:** `godot/shooter/Net/NetServer.cs:747-760`, `:763-783`; `godot/shooter/Net/NetServer.Pellets.cs:17-68`; `godot/ShooterCore/Weapons/HitMath.cs:11-14`; `godot/shooter/Net/NetClient.cs:20`, `:536`.
- **Cost:** no CPU or bandwidth cost in the status quo. Error: a sprinter 1.2 m (RTT 50) to 2.4 m (RTT 200) off, head hits impossible at walking speed from RTT 50.
- **Fix:** rewind players to the shooter's view tick (section 5, design D1).
- **Effort:** S to M (history ring, a `LiveTargets(tick)` variant, a shadow-mode counter, tests).
- **Confidence:** CONFIRMED mechanism and arithmetic; magnitude UNVERIFIED in play (no measured human run).
- **Audit:** new.

### B-02 Vehicles are hit by a physics ray at their present pose, so they cannot be rewound without an analytic test

- **Mechanism:** `WorldHit` casts one ray with mask `CollisionLayers.Bullets` (world, vehicles, shot-only cover) and reads the collider's `ServerCar` (`godot/shooter/Net/NetServer.cs:722-732`; mask `godot/ShooterCore/Colliders/PropColliders.cs:29`). Bodies are at their present pose. A physics space cannot be queried at a past pose, so the vehicle test must be replaced by an oriented-box test on a stored pose. Glass panes are rectangles in the vehicle frame (`godot/shooter/Net/NetServer.Glass.cs:83-101`, `GlassRules.ToVehicle(n, feet, yaw)` at present), and rider exposure uses `SeatPos(car, seat)` at present (`NetServer.cs:700-705`, ADR 0012).
- **Cost:** the change adds about 29 box tests per shot (about 2 µs) and about 232 per pellet fan of 8 (about 15 to 20 µs, assuming 50 to 80 ns per test, UNVERIFIED). Worst case 240 shots/s (16 players at 0.066 s intervals): about 5 ms per second, about 0.5% of one core. Vehicle history: 29 cars x 32 B x 16 ticks, about 15 kB.
- **Fix:** split `WorldHit` into a walls-only ray (World | ShotOnly) and an analytic vehicle test on the rewound pose; rewind the glass frame and the rider's seat with the same pose (ADR 0012 requires the rider's hit volume to be the one "every client draws").
- **Effort:** M.
- **Confidence:** CONFIRMED mechanism; cost UNVERIFIED.
- **Audit:** new.

### B-03 Melee does not absorb lag: its reach is a distance, not a width

- **Mechanism:** `HitMath.Melee` tests the target's feet against a 2.36 m reach and a 60 degree cone at present (`godot/ShooterCore/Weapons/HitMath.cs:285-304`; caller `NetServer.cs:793-807`). A target that retreats 8 m/s gains 1.6 m at RTT 100 before the swing resolves, so a knife that visibly connected misses.
- **Cost:** none in the status quo. Fixed by the same history lookup as B-01 (the melee already takes `HitTarget`s), so the cost is S once B-01 is in.
- **Confidence:** CONFIRMED arithmetic.
- **Audit:** new.

### B-04 Rockets pass targets: the fuse is contact only, and their targets are at present

- **Mechanism:** `RocketFlight.Step` ends in `Player` only when the sweep meets a victim (`godot/ShooterCore/Weapons/RocketFlight.cs:36`), and splash runs only on explode (`godot/shooter/Net/NetServer.cs:876-903`). Targets are taken from `LiveTargets()` at present each tick (`:845`, `:885`). A lateral miss of 0.56 m or more is a miss, and at 40 m/s the rocket is past in one tick.
- **Cost:** nothing extra in the status quo. Per-step rewind of targets (D3) costs 16 history lookups per rocket per tick (about 1 µs per rocket-tick, bounded by the rocket count, which is small).
- **Fix:** D3 (rewind the target to the shooter's view tick for each step; the rocket path stays at present) or a proximity fuse (content and gameplay change, needs a weapon decision, not a netcode one). Recommend D3 only if rocket hits are measured as a problem.
- **Effort:** S to M for D3; M for a proximity fuse.
- **Confidence:** CONFIRMED (fuse); OPINION on the fix choice.
- **Audit:** new.

### B-05 Shotgun fans at short range miss moving targets entirely

- **Mechanism:** the fan radius grows with distance (`0.052·d`), the lateral lag does not (section 3). At 10 m the usable lateral reach is 0.52 m (fan) + 0.56 m (body) = 1.08 m, so a sprinter at RTT 50 (1.2 m) is outside every pellet's reach.
- **Citation:** `godot/shooter/data/weapons.json` (ids 15 to 18); `godot/ShooterCore/Weapons/HitMath.cs:133-149`, `:168-175`; `godot/shooter/Net/NetServer.Pellets.cs:17-68`.
- **Cost:** none. The fix is B-01.
- **Confidence:** CONFIRMED arithmetic; the play effect UNVERIFIED.
- **Audit:** new.

### B-06 Shots in one tick see targets at mixed times

- **Mechanism:** `_PhysicsProcess` runs `SimulateClient` for each player (where shots resolve) at `NetServer.cs:203`, then `StepRockets` (`:204`), then `StepVehicles` (`:206`). Players processed earlier this tick have moved; later ones and all vehicles are at the end of the previous tick (vehicles were placed by `PlaceSolid` at `:202`). A shot therefore sees targets up to one tick (33 ms) apart: 0.27 m for a sprinter, 1.5 m for a car at 44.4 m/s.
- **Cost:** none. A capture of the history at tick start (B-01) makes every shot in a tick see the same time, at a copy of about 1.3 kB per tick.
- **Fix:** capture the poses at the start of the tick and resolve all shots against that snapshot.
- **Effort:** S (part of B-01).
- **Confidence:** CONFIRMED (order in code); magnitude is arithmetic.
- **Audit:** new.

### B-07 The 100 ms interpolation delay is the floor of L, and it is already below the measured worst snapshot gap

- **Mechanism:** `rt = lastTick + elapsed*30 - 3`; the interpolator clamps to the buffered range and does not extrapolate (`godot/ShooterCore/Net/Interpolator.cs:32`). If a gap between snapshots exceeds 3 ticks (100 ms), the drawn remote entities freeze for the excess. The loss test measured a max gap of 117 ms worst-client at 100 ms RTT and 3% loss (`docs/spikes/shooter-loss-test.md:21`, mean 90 ms). So at that impairment the delay is already at or below the gap.
- **Cost:** a freeze of drawn remote players of up to about 17 ms per gap (CONFIRMED arithmetic). The shot consequence: lowering the delay to save shot lag is only safe after the snapshot loss issue (T1-2 in `docs/perf-audit.md:65-69`, the loss-test defect) is fixed. Do not lower `InterpDelayTicks` now; each tick removed saves 33 ms of L.
- **Fix:** keep 3 ticks until T1-2 is fixed; then consider 2 ticks with an adaptive buffer (A's area).
- **Effort:** S to decide, M to make adaptive.
- **Confidence:** CONFIRMED (the number from the spike; the clamp from the code); the effect on drawn players UNVERIFIED.
- **Audit:** partly T1-2 (`docs/perf-audit.md:65`); the interpolation delay as a shot-latency factor is new.

### B-08 The shooter's own hit feedback is RTT/2 late, and its tracer may not match the server's hit

- **Mechanism:** the hit marker waits for the server's `Hit` (`godot/shooter/Net/NetClient.cs:844-848`, one way after resolution). The tracer is drawn from the camera ray against the world and against vehicles' drawn nodes (`NetClient.Predict.cs:128-143`), so for vehicles the predicted tracer already uses the interpolated (`RTT + 100 ms`) pose. That is the same time as a rewind would use, so the predicted tracer and a rewound server hit would agree for vehicles; without a rewind they disagree (the server hits the present car).
- **Cost:** none. Keep hit markers unpredicted: predicting them would create false kills.
- **Fix:** none needed beyond B-02; a rewind makes the tracer and the hit agree.
- **Confidence:** CONFIRMED (code); the agreement is arithmetic on the same interp delay.
- **Audit:** new.

### B-09 Status of the audit's items in this area (verified in code)

| audit id | claim | status now | evidence |
|---|---|---|---|
| T2-02 (exclude array per ray) | rebuilt per call | **partly fixed**: `PlayerRids()` is cached and rebuilt only on join or drop (`NetServer.cs:707-715`). Still per call in `WorldHit` with a skip vehicle (`:727`) and in `BulletHit` with an ignored vehicle (`godot/shooter/Net/NetServer.Glass.cs:86`), which is the rider path. | CONFIRMED |
| T3-001 (`LiveTargets` builds a list per call) | open | **open**. The comment defends a fresh list because callers nest (`NetServer.cs:684-687`). A per-depth pool would keep the benefit. | CONFIRMED |
| T3-005 (HitMath boxed enumerator and `float[2]`) | open | **open**: `Raycast` iterates an `IEnumerable<HitTarget>` (`HitMath.cs:204`, boxing when the source is an iterator, e.g. `TargetHitTargets` in `NetServer.Targets.cs:44`), and `RayCapsule` allocates an array per call (`HitMath.cs:260`). | CONFIRMED |
| "Hit detection is analytic" (audit, `docs/perf-audit.md:394`) | healthy | confirmed (section 2) | CONFIRMED |

Cost: none of these matters at 240 shots/s; they are hygiene, S each.

### B-10 Gunship cannon rounds and shells: the client's aim point is trusted within a bound, and splash absorbs the lag

- **Mechanism:** rounds fire at a client-chosen ground point, bounded by `GunshipAim.ClampAim` (`docs/shooter-v2.md:182-192`); damage to bodies is resolved at present (`NetServer.Aircraft.cs:202-230`). The 3 m blast absorbs a sprinter's 1.2 to 1.6 m lag; a shell detonates at the tracer's arrival (`docs/shooter-v2.md:199-206`).
- **Cost:** none. Lag matters for a direct hit only, and the direct hit is `route.TargetId` (`NetServer.Aircraft.cs:205`).
- **Fix:** none needed for now. If rewinding goes in, the burst can take the rewound targets (`GunshipBurst` already takes a `List<HitTarget>`).
- **Confidence:** CONFIRMED arithmetic; OPINION that it is acceptable.
- **Audit:** new (the audit's gunship items are about FX, T2-16 and T2-09).

## 5. Design alternatives considered, each priced

Common facts: no wire change for any server-side rewind (the client needs no new field, because RTT comes from ENet); protocol version unchanged for D1 to D3. The history ring is a fixed array per tick: players 16 x 24 B x 16 ticks ring = 6 kB; with cars 29 x 32 B x 16 = 15 kB; total about 21 kB. Writes: about 1.3 kB per tick (45 records) and about 39 kB/s into the ring (no allocation). The rewind tick is `round((RTT_ms + 100) / 33.3)`, so RTT 50 gives 4 to 5 ticks, RTT 100 gives 6, RTT 200 gives 9. Cap options and the residual at each RTT:

| cap | residual at RTT 50 / 100 / 200 | sprinter residual at RTT 200 | car residual at RTT 200 |
|---|---|---|---|
| 200 ms (6 ticks) | 0 / 0 / 100 ms | 0.8 m | 4.4 m |
| 250 ms (8 ticks) | 0 / 0 / 50 ms | 0.4 m | 2.2 m |
| 300 ms (9 ticks) | 0 / 0 / 0 | 0 | 0 |

Victim fairness is the window the victim was out of cover on the shooter's screen but not on the victim's: `min(L, cap) × v`. At cap 250 and RTT 100 that is 2.0 m of sprint, about one body width. This is the "shot behind cover" risk; it exists only with a rewind.

**D0. Status quo (present-state resolution).** Cost: 0. Error: B-01 to B-06. Victim side: never shot after moving out of cover on the server. Shooter side: shots fail on targets moving across the aim.

**D1. Players-only rewind to the shooter's view tick, all shot kinds that hit bodies (recommended first step).** Cost: S to M. Files: `godot/shooter/Net/NetServer.cs` (capture at tick start before `:203`; `LiveTargets(tick)`; `Deal` and `FireWeapon` take the rewound list), `godot/ShooterCore/Weapons/HitMath.cs` (unchanged signatures; it already takes `HitTarget`), a new pure history type in `godot/ShooterCore/` with tests in `godot/ShooterCore.Tests/`, and the ADR wording in 0006 and 0010 to 0012. Buys: B-01, B-03, B-05 for players and B-06. Does not buy vehicles. Life-epoch check needed: a player who died and respawned within the window must not be rewound into their previous life (store a spawn counter in the history; OPINION on the mechanism, CONFIRMED that `PlayerSpawned` teleports, `NetServer.cs:547-551`). Test first: shadow mode (compute both hits, count disagreements, no behaviour change).

**D2. D1 plus vehicles, glass frame and rider seats on the rewound pose.** Cost: M to L (B-02). Adds about 15 kB of history and about 0.5% of one core at the worst fire rate (UNVERIFIED per-test cost). Buys B-02, the car and heli case. Changes `WorldHit` (`NetServer.cs:722-732`), `BulletHit` (`NetServer.Glass.cs:83-101`), `ExposedGunnerBehind` (`godot/shooter/Net/NetServer.Flak.cs:55`), `HitTargetOf` (`NetServer.cs:700-705`). ADR amendments: 0006, 0010, 0011, 0012. Risk: a rider's seat in the past must use the car's past pose (cheap if D2 is in).

**D3. Rockets rewind targets per step.** Cost: S to M. 16 history lookups per rocket per tick. Buys part of B-04: lateral misses still pass because there is no proximity fuse. Recommend only with measurement.

**D4. Proximity fuse for rockets.** Cost: M, plus a weapon-balance decision (more splash, more kills; `weapons.json` id 2). Not a netcode fix; OPINION that it is a separate decision.

**D5. Lower the interpolation delay from 3 ticks to 2 (or 1).** Saves 33 ms (or 67 ms) of L for every shot and every drawn entity. Cost: S in code (`NetClient.cs:20`), but a freeze risk at the measured 117 ms worst gap (B-07). Not before the T1-2 fix. Buys less than D1 for players, and more for vehicles.

**D6. Client-authoritative hits (the client reports "I hit player X at view tick t").** Cost: S to write, but a protocol bump, 2 to 4 B per shot up, and a cheat surface (ADR 0006 rejects "a cheat's free pass"). Reject. The gunship's bounded aim (B-10) is the most the project has accepted, and it is an aim, not a hit.

**D7. Extrapolate the victim forward by `L` and resolve at present+L.** Cost: S if velocities are in the snapshot (UNVERIFIED for players), error bounded by the victim's acceleration. A rewind is exact for the shooter's picture; extrapolation is exact only for constant velocity. OPINION: prefer D1, which reproduces the picture the shooter actually had.

**D8. Do nothing and widen the hit tolerance** (e.g. 0.56 to 0.9 m). Cost: S (a constant). Buys part of the body case; does not help headshots; widens every hit for everyone, including the victim who is behind cover. Reject as a substitute for D1.

## 6. Needs measurement

The measurements below are plans, not actions taken here.

1. **Bots under-represent the lag.** The loss-test bot aims at the newest decoded snapshot (`godot/shooter/Net/NetClient.LossBot.cs:172-190`, `_dec.States`), which is `OWD` old, not `OWD + 100 ms`. Its effective L is therefore RTT, not RTT + 100 ms, and the loss test's 81 to 89 hits per run (`docs/spikes/shooter-loss-test.md:25`, `:50`) are a lower bound on the human effect. Cheapest: make one bot class aim at its `Interpolator` position (the human's view) and bucket the hit rate by target speed at `rtt100` and `rtt200` (the latter has not been run, `docs/spikes/shooter-loss-test.md:10`, `:51`). About 2 minutes per scenario.
2. **Shadow rewind counter.** With D1 behind a flag and no behaviour change, log per shot: present hit vs rewound hit, and the target speed. This gives the disagreement rate without playtesting. Cheapest of all, and it gives the value of D1 before it ships.
3. **Shot-path cost.** `Stopwatch` around `FireWeapon` with 16 bots on full auto (`godot/shooter/Net/NetServer.Bots.cs`). Expected a few µs per shot today; the number decides whether D2's 0.5% estimate matters.
4. **Interpolation gap at 3% loss.** A histogram of `rt - _lastTick` and of clamped renders (`godot/shooter/Net/NetClient.cs:536`, `godot/ShooterCore/Net/Interpolator.cs:32`), to see how often B-07 freezes a drawn player.
5. **Human effect.** Only a real playtest at 50, 100 and 200 ms RTT (netem or `--net-sim` on the client only) can say how much players feel B-01. Not runnable from this investigation.

## 7. Needs from other agents

- **A (netcode):** the interpolation buffer and snapshot gaps (B-07), the loss-test bot's aim source, and whether T1-2 (the ack and delta baseline) lands first, since D5 depends on it.
- **C (players and prediction):** the own-player reconciliation and the life/respawn epoch needed for D1 (`godot/shooter/Net/NetServer.cs:547-551`).
- **D (vehicles):** the car body's pose history fields and whether the car's collider is a compound box; the cost of an analytic vehicle test (B-02).
- **E (world and physics):** the cost of the `WorldHit` split (B-02) and whether the vehicle layer's ray cost matters.
- **G (server logic):** the tick order (B-06) and whether a history capture can sit at the top of `_PhysicsProcess`.
- **H (growth):** player counts and fire rates for the per-shot budget at 16 and 64 players.
