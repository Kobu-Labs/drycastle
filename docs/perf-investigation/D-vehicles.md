# D: Vehicle models and collisions (read-only investigation)

Scope: `godot/ShooterCore/Vehicles/*`, `godot/ShooterCore/Aircraft/*`, `godot/shooter/ServerCar.cs`, `godot/shooter/VehicleCollider.cs`, `godot/shooter/Net/NetServer.Vehicles.cs` (incl. `ResolveVehicleContacts`), `godot/shooter/data/vehicles.json`, ADR 0008. Read also ADRs 0002, 0005, 0006, 0007, 0010, 0011, 0012, `docs/vehicle-collision-and-entering.md`. Nothing built, run or measured. Every "UNVERIFIED" cost is a bound from code and arithmetic, to be settled by the measurements in section 5.

Citations marked `~` are approximate line numbers (the function is found, the exact line was not pinned); all others were read.

Vehicle counts from `vehicles.json` `spawn_count`: car 12, motorcycle 3, bicycle 3, jeep 3, quad 4, amphibious 2, boat 2, flak 1, bus 1, utility_helicopter 1. **Sum 32**, not the 29 the audit and the brief use (`docs/perf-audit.md:9`, `:88`). Placement can skip candidates (`godot/shooter/Net/NetServer.Vehicles.cs:~96-100`, "no spot for"), so 29 may be the placed count. UNVERIFIED (see section 5, item 5).

---

## 1. Verdict

**Fit with changes.** Kinematic arcade models (planar `CarModel` moved by `MoveAndSlide`, a hull-sweep `DriveModel` for the amphibious transport, the `FlightModel` for the helicopter), each with one upright box for the hit volume, are the right shape for 32 spawned vehicles of ten types and for the gameplay they carry: ramming, run-over, vehicle-to-vehicle bumps, boats, amphibious driving, a helicopter that is flown and shot down. The model does not support stacking, towing, riding on a roof, stepping onto kerbs (UNVERIFIED that kerbs matter), or terrain damage; for a game of this kind those are acceptable (OPINION), and ADR 0012 and `docs/vehicle-collision-and-entering.md` already list roof-riding as follow-up. Three changes are needed: (a) stop stepping parked vehicles (D-01, the only cost that scales with vehicle count, and probably the largest vehicle cost in the tick), (b) decide whether ground vehicles get the driver prediction the helicopter and amphibious transport already have (D-02), and (c) stop reading the audit's T2-11 as applying to vehicles (D-05). The model stops fitting on **feel**, not on count: driven ground vehicles at 16 players are a 100 ms-plus-RTT steering loop (D-02), and it stops fitting on **count** only past about 150 vehicles, where the O(n²) contact pass reaches about 11,000 pair tests per tick (D-08) and un-skipped parked stepping grows linearly with move cost (D-01). Neither bound is reached by 29 vehicles.

---

## 2. What is healthy

- **Pure, Godot-free models with quantised inputs.** `VehicleRules.cs`, `DriveModel.cs` (582 lines), `FlightModel.cs` (463 lines) and `FlakRules.cs` read `NetInput`, the numbers in `vehicles.json` and a world interface (`IDriveWorld`, `IFlightWorld`). Prediction on the client shares them (`godot/shooter/Net/NetServer.Vehicles.cs:~290-345`, ADR 0006 and 0010). This is the right seam.
- **Parked vehicles cost zero snapshot bytes.** The entity encoder omits any entity whose delta flags are zero (`godot/ShooterCore/Net/Snapshot.cs:91-97`: `if (flags == 0) { o = at; continue; }`). A car standing still is sent once, then never again. The vehicle records go through this encoder (`godot/shooter/Net/NetServer.cs:912-915`).
- **Solid box placement is guarded and rounded the way the client rounds.** `ServerCar.PlaceSolid` rounds to the snapshot's quantisation and returns early when nothing changed (`godot/shooter/ServerCar.cs:124-131`, early-out at `:130`). `VehicleCollider.Place` is called only when the server's place changes.
- **Vehicle-to-vehicle broad phase is a distance-squared reject.** `ResolveVehicleContacts` skips pairs beyond `ContactBroadRange` 20 m before any shape work (`godot/shooter/Net/NetServer.Vehicles.cs:399-420`, reject at `:~411`). At 29 vehicles that is 406 pair checks per tick, about 12,000 per second, each a few flops (UNVERIFIED, under 0.05 ms).
- **Destroyed vehicles leave the physics world.** `ServerCar.Destroy` sets `CollisionLayer = 0` and zeroes speeds (`godot/shooter/ServerCar.cs:~158-166`); the contact pass and the step both skip destroyed cars (`NetServer.Vehicles.cs:354`, `:404`, `:408`).
- **Run-over is gated twice.** It runs only when a driver is aboard and the car is on land (`NetServer.Vehicles.cs:381`), and only above `run_over_min_speed` 8 m/s (`godot/ShooterCore/Vehicles/VehicleRules.cs:9-11`; `vehicles.json` `run_over_min_speed`). Its per-tick cost is one oriented-footprint test per living player per driven car: at most 16 x 16 = 256 tests per tick (UNVERIFIED per-test cost, expected sub-microsecond).
- **Drivers that stop sending inputs hand off.** `DriveStarveTicks = 6` and `FlightStarveTicks = 6` (`NetServer.Vehicles.cs:~274`, `:~335`) mean a stalled client cannot freeze a vehicle in the air or on the road.
- **Collision exceptions are removed on leave.** T3-014 is fixed: `NetServer.cs:450` removes the car's exception with a dropped player's body, with the comment that the exception would outlive the freed body. Verified in code.
- **Client beam selection has no per-frame LINQ.** T3-031 is fixed for the car path: `NetClient.Cars.cs` `PickBeams` reuses its buffers (`godot/shooter/Net/NetClient.Cars.cs:~131-140`, comment cites T3-031).
- **Glass hit test is bounded.** A shot is tested against at most the panes of one vehicle (at most 24 by the ADR 0008 limit), by a few cross products each (ADR 0008, `GlassPaneRect.Contains`).
- **Seat arrays are fixed size.** `VehicleSeat` holds `MaxSeats = 4` in an array (`godot/ShooterCore/Vehicles/VehicleRules.cs:104-110`); no allocation per seat.
- **Record id spaces are disjoint and cheap to map.** Vehicle 0x4000+index, drive 0x5000, flight 0x6000, turret aim 0x7000 (`godot/ShooterCore/Vehicles/FlightModel.cs:~367`, `DriveModel.cs:~371`); the id is an offset, no lookup.

---

## 3. Findings

Audit status for my area first, then new findings.

| Audit id | Status now | Evidence |
|---|---|---|
| T2-04 (parked vehicles stepped every tick) | **Open. CONFIRMED.** Cost UNVERIFIED. | D-01. Audit cites `VehicleRules.cs:83-84` for the LINQ occupant list; that is wrong, the code is at `NetServer.Vehicles.cs:375`. |
| T2-11 (every entity keyframed every 30 ticks) | **Does not apply to vehicles. Overturned for them.** | D-05. The entity encoder has no age-based keyframe (`Snapshot.cs:70-108`); the keyframe-on-age lives only in the world encoder (`godot/ShooterCore/Net/WorldReplication.cs:115`), and vehicles are entity records. The audit's `Snapshot.cs:33-39` is `SnapshotHistory.Store`. |
| T2-08 (no interest management, vehicles to every client) | **Open.** Vehicle records are sent to every client. | `NetServer.cs:912-915`. Bytes bounded in D-05. |
| T3-014 (collision exceptions not removed on Drop) | **Fixed.** | `NetServer.cs:450`. |
| T3-031 (RenderCars LINQ sort per frame) | **Fixed for the car path.** | `NetClient.Cars.cs:~131-140`. |
| T3-011 (quadratic spawn clustering) | **Open, cold.** Not re-measured. | `NetServer.Vehicles.cs:~84-100`, `spots.Any` per candidate. Startup only. |

### D-01 Parked vehicles are stepped every tick, with a LINQ list per car
- **Mechanism.** `StepVehicles` calls `car.Step(def, 0, 0, false, dt, Water)` for every non-destroyed car with no driver (`godot/shooter/Net/NetServer.Vehicles.cs:367`). `ServerCar.Step` runs `CarModel.Step`, sets the velocity (gravity term `-1` on floor, `-20 dt` in air), and calls `MoveAndSlide()` (`godot/shooter/ServerCar.cs:284-305`, the call at `:303`). Separately, the seat-loop `foreach (var o in Occupants(car))` at `NetServer.Vehicles.cs:375` allocates a LINQ `Enumerable.Range.Select.OfType.ToList()` per car per tick (`:~497-498` defines `Occupants`).
- **Cost.** 29 cars x 30 Hz = **870 `MoveAndSlide` calls per second** with nobody aboard, plus 870 list allocations per second. The move cost is UNVERIFIED. Bound: at 20 to 50 us per call (a `CharacterBody3D` query against the 399-node collision mesh, 72,078 triangles, plus floor snap), the parked stepping is 0.58 to 1.45 ms per tick. The measured server tick p50 is 1.6 ms, so **parked stepping could be a third to nine-tenths of the tick**. That is the one number that decides this finding; it is in section 5.
- **Fix.** Skip `Step` for a car that has no driver, is at rest (`Velocity` and `State.Speed` zero), and stands at its origin (`AtOrigin`, `ServerCar.cs:~153`). Keep, unchanged, the fall check (`:369`), the detonation timer (`:~372`), the abandon timer (`:~385`) and the destruction return (`:354`). Wake the car on seat change (`SwitchSeat`, `:~218`), on a bump (`ApplyBump`, `ServerCar.cs:~250`, which calls `MoveAndCollide`), on damage and on blast. Replace `Occupants` with a seat loop over `car.Seat.Count` (`VehicleSeat` already has `Occupant(seat)`). Note that a car displaced off its origin by a bump and left there is not "at origin", so it keeps stepping until it settles, which is correct.
- **Effort.** S for the skip and the seat loop; the audit said M, because of the wake list. I agree the wake list is the careful part; it is S with a test.
- **Confidence.** CONFIRMED (code path, arithmetic). Per-move cost UNVERIFIED.
- **Audit.** T2-04 (confirmed open, with the citation corrected above). Also NET-13, MB-11, DB-13, MC-09 per the audit's sources.

### D-02 The driver of a ground vehicle is not predicted; the car is drawn about 100 ms late
- **Mechanism.** Only the helicopter (ADR 0006) and the amphibious transport (ADR 0010) are stepped by their driver's client. The rest are the server's only: `ServerCar.cs` header (`:9-14`) and `NetClient.Cars.cs` draw them interpolated (`:~59-82`, `v.Buf.Push`). ADR 0010 states the deferral: "Predicting every vehicle now ... worth doing, but not as a side effect of this" (ADR 0006, Considered). Types that are not predicted: car, motorcycle, bicycle, jeep, quad, boat, flak truck, bus. Their spawn counts: 12 + 3 + 3 + 3 + 4 + 2 + 1 + 1 = **29** (the number the audit calls the vehicle count; by coincidence the 29 unpredicted, plus 3 predicted, make the 32 in the JSON).
- **Cost.** Steering latency for the driver: one interpolation delay (100 ms, ADR 0010 "Consequences") plus the round trip. At 100 ms RTT and 30 Hz, the car the driver sees is about 200 ms behind what they steered. The helicopter's ADR 0006 rationale ("the same delay makes it overshoot everything") applies to a car in a different degree (lateral slide, pivot), so the car's sensitivity is an OPINION until the driver's experience is measured (section 5, item 2).
- **Fix options.** Priced in section 4 (B). The cheapest first step is data only: give one ground type a `drive` block and check it under the existing validation. ADR 0010 explicitly allows that ("A later car can move onto `DriveModel` by data, one type at a time"), and the validation that limits the block to an amphibious type is the one thing to change (`VehicleData`, ADR 0010 Consequences).
- **Effort.** S-M for the data trial on one type; L to move all ground types (a `CarModel` that is pure and a prediction path per type).
- **Confidence.** CONFIRMED (structure and deferral). The feel consequence is OPINION.
- **Audit.** New. The audit looked for inefficiency; this is a design gap.

### D-03 One hit box serves five jobs; the server carries two bodies per vehicle
- **Mechanism.** The same `HalfWidth`, `HalfLength` and `Height` from `vehicles.json` build: (a) the server's `CharacterBody3D` collision shape, layer 2, mask 1 (`ServerCar.cs:~96-100`); (b) the on-foot solid, a separate `StaticBody3D` on layer 8 (`godot/shooter/VehicleCollider.cs:14-20`); (c) the bullet hit, which is a physics ray on the layer-2 body on the server and box maths on the client (`godot/shooter/Net/NetServer.cs` `BulletHit`, `godot/ShooterCore/Vehicles/VehicleRules.cs:~60-95` `DistanceToBox`); (d) the vehicle-to-vehicle contact shape (`ServerCar.ContactShape`, `ServerCar.cs:~259-265`); (e) the run-over footprint sweep (`NetServer.Vehicles.cs:550-552`, `CarFootprint.Hits`), and (f) the blast distance (`VehicleRules.cs:~90-100`, `DistanceToBox`). The server's body keeps a collision exception with every player (`NetServer.cs:412`), so the body drives over players, and the solid box is what stops them.
- **Cost.** Server: 29 `CharacterBody3D` plus 29 static solids = **58 physics bodies**; client: 29 static solids (`NetClient.Cars.cs:67`). Each solid that moves is `ForceUpdateTransform` (`VehicleCollider.cs:29`) once per tick. UNVERIFIED cost; the count is exact.
- **Liability.** The one shape is a fit for the bus (a 12 m box, 3.1 m tall) and the car. It is a poor fit for the amphibious transport: its hit box is 1.2 m tall, so riders stand out of it (ADR 0012, Consequences), and the on-foot box for a helicopter hovering at 1 m is 2.95 m tall, which blocks walking under it at low hover (UNVERIFIED, minor). The follow-up in `docs/vehicle-collision-and-entering.md` ("per-type `solid: false` / separate collision box") is the right fix when a type needs it.
- **Fix.** Keep it. When a type needs a different solid or hit shape, split the data (`solid`, `hit` volumes) in `vehicles.json`; no protocol change (the box is not on the wire).
- **Effort.** S for the data split, M once a type actually needs it.
- **Confidence.** CONFIRMED (structure, counts). Liability for the helicopter at low hover UNVERIFIED.
- **Audit.** New.

### D-04 The client re-places every vehicle's solid on every record, with no change guard
- **Mechanism.** `OnVehicleState` calls `VehicleCollider.Place` unconditionally for each record it receives (`godot/shooter/Net/NetClient.Cars.cs:81`). The server guards the same call (`ServerCar.PlaceSolid`, `:130`); the client does not. A parked car sends no record after its keyframe (D-05), so only moving vehicles reach this line.
- **Cost.** Up to 29 `ForceUpdateTransform` per snapshot, 30 snapshots per second, when all vehicles move: at most **870 per second per client** (UNVERIFIED cost each).
- **Fix.** Compare the rounded place with the last placed one, as `PlaceSolid` does. S.
- **Confidence.** CONFIRMED (path), cost UNVERIFIED. Low priority.
- **Audit.** New. Not a duplicate of T3-028 or T3-031.

### D-05 The vehicle snapshot cost is correctly sized; the audit's keyframe finding does not apply to vehicles
- **Arithmetic from the encoder** (`Snapshot.cs`, `WriteDelta` and `WriteKeyframe` at `:130-160`; header 4 bytes per record, `:~76-108`):
  - Keyframe (a new entity, or none acknowledged): 4 + 12 (abs position) + 4 (rotation) + 3 (status) = **23 B**.
  - Delta: 4 + 6 if moved (three 16-bit deltas) + 4 if rotated + 3 if status changed: a moving ground car is about **14 B per tick** (position and yaw), maximum 17 B.
  - Parked, unchanged: **0 B**.
  - Driven amphibious transport: vehicle record plus drive record (`NetServer.cs:915`), about 28 B per tick.
  - One moving car: 14 x 30 = **420 B/s per client**. All 29 ground vehicles moving: 12.2 kB/s per client, 195 kB/s of server egress across 16 clients (vehicles alone).
  - The measured total is 4.24 kB/s per client at 16 bots (brief). Vehicles are therefore a bounded share in normal play; the 12.2 kB/s figure needs all 29 moving, which the design does not produce.
- **Mechanism of the correction.** `WorldSnapshotEncoder` keyframes every `Wire.KeyEvery` ticks (`godot/ShooterCore/Net/WorldReplication.cs:115`), but that encoder carries world entities (`WorldReplication.cs:17`, `:79`), not vehicles. Vehicles go through the delta encoder with a baseline, so a lost packet costs only the entities it held and the next tick repairs them (`Snapshot.cs:19-23` comment, ADR 0002).
- **Fix.** None for vehicles. If bytes matter, the lever is interest management (T2-08), which is A's or H's call and not a vehicle-model change. Interest filtering for vehicle records would need the always-include rule for the client's own vehicle (the audit's T2-08 fix).
- **Effort.** None now; L for interest management.
- **Confidence.** CONFIRMED (arithmetic and code). Per-entity byte widths follow from `Snapshot.cs:130-160`; the 16-bit delta width is read, not measured.
- **Audit.** T2-11 overturned for vehicles; T2-08 stands.

### D-06 Glass: 88 pane nodes across the village's vehicles, with an index tied to the model file
- **Mechanism.** ADR 0008: each breakable pane is its own glTF node `glass_N`, plus a crack overlay and fragments built when it breaks (`godot/shooter/Net/NetClient.Cars.cs` glass paths, ADR 0008). Count: 4 panes x 12 cars = 48, 4 x 3 jeeps = 12, 6 x 1 flak = 6, 18 x 1 bus = 18, 4 x 1 helicopter = 4. **88 pane nodes** in the village.
- **Cost.** 88 extra nodes and, on each breakage, a clipped overlay and fragments. Draw cost UNVERIFIED (each pane is likely one draw call; merged per vehicle it would be one). The shot test is bounded (at most 24 panes per vehicle, few cross products, ADR 0008).
- **Liability.** ADR 0008, Consequences: the index is the same as the `glb` node, so a generator run that changes order silently detaches panes from their data. There is no check.
- **Fix.** A load-time check that each `glass` index has a `glass_N` node (S). Merging into one mesh per vehicle with per-pane surfaces (M) only if draw calls show up in the render measurement (rendering agent F).
- **Effort.** S for the check.
- **Confidence.** CONFIRMED (counts, structure). Draw cost UNVERIFIED.
- **Audit.** New. Mostly a fit.

### D-07 Three driving implementations to keep in step
- **Mechanism.** The ground car is `CarModel` plus `MoveAndSlide` (`ServerCar.Step`, `ServerCar.cs:284-305`). The amphibious transport is `DriveModel` with a hull sweep and four ground rays per step (ADR 0010; `DriveModel.cs`, 582 lines). The helicopter is `FlightModel` (463 lines). They differ on steps (`DriveModel` has `step_height`; ADR 0010 "a ledge higher than `step_height` is a wall"), on slopes, on water (`ServerCar.Step` sets the body's Y to the surface; `DriveModel` floats at `float_depth`) and on the collision rules. A tuning change to one does not carry to the others.
- **Cost.** Maintenance and drift; no measurable runtime cost. OPINION.
- **Fix.** Keep three, and say in the ADRs which one a new type uses. Or move ground cars to `DriveModel` (B in section 4), which leaves two.
- **Effort.** L to unify.
- **Confidence.** OPINION.
- **Audit.** New.

### D-08 Contacts are O(n^2), and fine at this count
- **Mechanism.** `ResolveVehicleContacts` tests every pair (`NetServer.Vehicles.cs:402-410`, double loop) and rejects beyond 20 m before the narrow test.
- **Cost.** At 29 vehicles: 406 pairs per tick, about 12,000 per second. At 150 vehicles: 11,175 pairs per tick, about 335,000 per second; if a reject costs 0.1 us, that is about 1 ms per tick (UNVERIFIED). A spatial grid over the 20 m cell is the fix past about 150 vehicles (S-M).
- **Confidence.** CONFIRMED (loop shape). Cost UNVERIFIED.
- **Audit.** New. Not a hot spot at 29.

### D-09 No step-up for ground cars; kerbs may stop them
- **Mechanism.** `ServerCar` relies on `MoveAndSlide` with `FloorSnapLength = 0.4` and `FloorMaxAngle = 50 deg` (`godot/shooter/ServerCar.cs:108-109`). There is no step-up logic. `DriveModel` has `step_height` for the amphibious transport.
- **Cost.** Gameplay, not CPU. A kerb higher than what the step-free move climbs stops a car as a wall. The kerb heights in the village are not in the code I read.
- **Fix.** Measure the kerb heights first (static scan, section 5, item 4). If they matter, port the step from `DriveModel` into `CarModel`'s move (M), which changes all ground cars and their tuning.
- **Confidence.** UNVERIFIED (the kerb heights are the open question).
- **Audit.** New.

### D-10 Stacking, towing, roof riding and terrain damage are not supported
- **Mechanism.** The contact pass moves vehicles apart horizontally for ground types, and a helicopter may be pushed vertically only when it is on a skid (`ServerCar.ApplyBump`, `ServerCar.cs:~245-262`; ADR-like text in `docs/vehicle-collision-and-entering.md`, "Vehicle against vehicle"). No car can stand on a car's roof, be carried, or tow another. Terrain is not deformed; the map is static (no code path). This is the stated scope, listed as follow-up in `docs/vehicle-collision-and-entering.md`.
- **Fit.** Acceptable for 16 players and the intended gameplay (OPINION). Each would need its own ADR; roof riding is a change to the solid box (D-03) and to the rider model (ADR 0011, 0012).
- **Confidence.** CONFIRMED (absence in code) for the scope; the acceptability is OPINION.

### D-11 `LeaveSeat` repeats its own lines
- **Mechanism.** `car.Seat.Exit(c.Id);` appears twice (`godot/shooter/Net/NetServer.Vehicles.cs:209-210`), and `c.Car = null; c.Body.InVehicle = false;` appears twice (`:213-216`).
- **Cost.** Idempotent, so no bug, and no cost. A sign of a copy during an edit, and a place where a later change could be applied once and not twice.
- **Fix.** Delete the duplicates (S).
- **Confidence.** CONFIRMED.

### D-12 Pilot prediction depends on both sides querying the same Jolt world
- **Mechanism.** `FlightProbe` and `DriveProbe` answer ground, water and sweep queries on the client against the map's colliders, built on both sides from the same map (`godot/shooter/FlightProbe.cs`, `godot/shooter/DriveProbe.cs`, ADR 0006 "What it takes"). Agreement is required for the reconciliation to converge. A mismatch between a client build and the server's (different Jolt results on a different CPU, a different collider build) shows as corrections.
- **Cost.** Each mismatch is one snap of the helicopter or the transport, and the replay of the inputs not yet acknowledged (ADR 0006). Rate UNVERIFIED.
- **Fix.** Count corrections in the prediction classes (section 5, item 6). Only if the rate is high, consider quantising the probe answers (M).
- **Confidence.** UNVERIFIED.
- **Audit.** New. Owned jointly with B (lag compensation) and C (prediction).

### D-13 Minor: the audit's spawn count and its citation
- The audit's `VehicleRules.cs:83-84` citation for the LINQ occupant list is wrong (see D-01). The spawn count of 29 does not match `vehicles.json` (32). Both are corrections to the audit, not to the code. CONFIRMED.

---

## 4. Design alternatives considered (each priced)

**A. Keep the kinematic move; skip parked cars (D-01).** Cost: S-M (skip, seat loop, wake list, one test for bump-wake). Files: `godot/shooter/Net/NetServer.Vehicles.cs`, `godot/shooter/ServerCar.cs`. No ADR, no protocol bump. Buys: all of the parked share of 870 moves per second. Cheapest test: time `StepVehicles` over 1,000 ticks with the 29 cars parked against the same run with none (section 5, item 1).

**B. Predict ground vehicles by the ADR 0010 route (drive block per type).** Cost: first type M (validation in `VehicleData` to allow a non-amphibious `drive` block, tuning, the prediction path is generic per ADR 0010's "by data"); each later type S. Files: `vehicles.json`, `godot/ShooterCore/VehicleData.cs` validation, `godot/ShooterCore/Vehicles/DriveModel.cs` (the hull and ground numbers), `NetServer.Vehicles.cs` (the drive step already exists). Protocol: none, since the drive record is already sent for every `Drives` type (`NetServer.cs:915`). ADR: amend ADR 0010 ("validation limits the block to an amphibious type"). Buys: the driver's steering without the interpolation delay for the cars, the bus and the flak truck. Costs: a hull sweep and four ground rays per input on the client, plus a replay on each correction. Cheapest test: a scratch copy of `vehicles.json` with a `drive` block on `car`, and validation relaxed in that copy only; drive it at the loss-test RTT (plan, section 5 item 2).

**C. Replace `MoveAndSlide` with Jolt `VehicleBody3D`.** Rejected by the code's own comment (`ServerCar.cs:6-8`: determinism, no flip or jitter). Cost: L; all ten types re-tuned, the move replaced, wheels modelled. Buys: real suspension and tyres, not stacking. Not recommended.

**D. Split solid, hit and contact shapes per type (`solid: false`, separate hit volume).** Cost: M. Files: `VehicleCollider.cs`, `ServerCar._Ready`, the hit maths, a new layer scheme (the follow-up in `docs/vehicle-collision-and-entering.md`). Protocol: none. ADR: a new one for the split. Buys: a bus that can be walked through, a boat that does not block on-foot movement, a helicopter that does not block at low hover. Not needed at this count; needed the day a type wants it.

**E. Spatial grid for vehicle contacts.** Cost: S-M (a cell per 20 m, rebuilt per tick). Buys: O(n) contacts beyond about 150 vehicles. No gain at 29 (D-08).

**F. Interest management for vehicle records (T2-08).** Cost: L (protocol bump, per-client sets, always-include the own vehicle). Owned by A or H. Buys: the vehicle share of the 12.2 kB/s worst case, and the share for far vehicles. Not needed at normal motion (D-05).

**G. Port the step-up from `DriveModel` into `CarModel` (D-09).** Cost: M, all ground types re-tuned, an ADR since ADR 0010 says not to change the car's move as a side effect. Buys: kerb climbing, if kerbs matter. Do the measurement first.

---

## 5. Needs measurement (plan only; nothing run)

1. **Parked stepping cost (D-01, the decisive one).** Wrap `car.Step` at `NetServer.Vehicles.cs:367` in a `Stopwatch` (or `Performance` monitors) and accumulate per tick, in the existing loss-test or smoke mode, with the 29 vehicles parked and with `--vehicle-set` set to none. Compare the tick's p50 and p95. Why not static: the cost depends on the Jolt query against the 399-node collision mesh and the floor snap, which only a run shows. Cheapest: a counter, one run, no new build target.
2. **Driver-perceived latency for a ground car (D-02).** Time from input send to the driver's drawn car moving, at 100 ms RTT, for the car and for the amphibious transport (which is predicted). Why not static: it is the sum of interpolation delay, RTT and the time to the next snapshot. Cheapest: timestamps on the input send and the first drawn displacement.
3. **Vehicle bytes per tick by type (D-05).** Count `SnapshotEncoder` bytes by `EntityType` in one bot run with moving vehicles. Why not static: how many vehicles move, and how often yaw changes, is run data. Cheapest: a per-type byte counter in the encoder for one run.
4. **Kerb heights on the village roads (D-09).** Static, no run needed: sample the collision mesh's height step at road edges in the glb, or the road edge data. Cheapest: a script over the exported map, reading only.
5. **Vehicles placed: 29 or 32 (D-13).** Log count of `server: no spot for` lines and of `spots` at startup in one dry start. Why not static: placement uses the physics space (`RestHeight`, `NetServer.Vehicles.cs:~97-100`). Cheapest: count the spots printed.
6. **Prediction corrections for helicopter and amphibious (D-12).** Count `FlightPrediction` and `DrivePrediction` snaps per minute at 100 ms RTT and 3% loss. Why not static: depends on Jolt answers that agree or not across runs. Cheapest: two counters in the prediction classes.
7. **Client solid placement cost (D-04).** Time `VehicleCollider.Place` over a frame with all 29 moving. Cheapest: a `Stopwatch` at `NetClient.Cars.cs:81`.

---

## Notes for the lead

- Agent A (netcode): D-05 bytes and T2-08; the vehicle records are on the delta encoder, not the world one, so T2-11 does not apply to them.
- Agent B (lag comp): D-02 (driver's car is interpolated, not predicted) and D-12 (probe agreement).
- Agent C (players and prediction): D-02 and D-12 are the prediction side.
- Agent E (world and physics): D-01 and D-03 (the Jolt cost of 58 bodies and parked moves), and D-09 (kerbs).
- Agent F (rendering): D-06 draw calls for 88 glass nodes.
- Agent G (server logic): D-11, and the `Occupants` LINQ in D-01.
- Agent H (growth): D-08 and the 150-vehicle bound; D-05 interest management.
