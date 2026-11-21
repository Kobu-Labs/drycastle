# Every ground vehicle is on the drive model

Decided with the user during #325 (Unify all vehicle models), ending the migration ADR 0010 started and ADR 0014 gave the rule for ("the other ground vehicles stay on `CarModel` until each gets a `drive` block of its own"). **Supersedes** that rule: no ground vehicle stays behind.

**Why now.** The old model moves an upright box with Godot's `MoveAndSlide` and draws the vehicle level. A box as long as the vehicle rides a slope on its bottom edge, so the front wheels of the 7 m truck that replaced the jeep hung in the air on a dyke, the edge scraping took its speed, and with a trailer it stuck. The four types already on `DriveModel` (car, Golf, Humvee, amphibious) lie on the slope under their wheels and tow on it. Two models also meant two behaviours for everything measured from a vehicle (seats, guns, glass, holes).

**Decision.** Every vehicle type with a driver that is not a helicopter has a `drive` block: motorcycle, `truck` (the cargo truck, once `jeep`), quad, flak, bus, harvester and the Scud truck join the four. A trailer has none (it follows its tractor, `TowModel`). `EveryVehicleDrivesTests` holds it; `docs/standards/car-design.md` A7 is the rule for a new vehicle.

**What the model needed for them.**
- `drive.track` may be 0: a single-track vehicle (the motorcycle) is sampled under its two wheels and never rolls with the ground.
- `drive.axle_centre`: the ground is sampled round the point between the first and last axle, not the origin (the harvester's axles are 1 m behind its footprint centre).
- A wheel may hang 1.2 m or 0.35 of the wheelbase, whichever is more, and the ground is looked for from correspondingly higher: a bus or the Scud lies whole on a slope of up to about 19 degrees. The four older types are unchanged by this (their wheelbases are under 3.43 m), except that the Humvee's ground rays start 0.1 m higher.
- The Scud truck: the launcher brakes the truck while its rack moves. Both sides now step it with `DriveInput.Held` then (the server from the launcher, the driver's client from the launcher state message), so the prediction agrees; the rule "a launcher truck has no drive block" is gone.
- The harvester's own driver sends no inputs: the server steps its drive with the brain's throttle and steer, once a tick.
- Wheels: `DriveNodes` no longer knows four wheels. It hands the speed and the front angle to `VehicleWheels.StepDriven`, which rolls every wheel the model names by its own radius and turns every steer parent (six-wheelers, the bike's raked fork, handlebars).

**The numbers.** Each new block is the car's (ADR 0014: it reproduces `CarModel`: instant ramps, `grip` 1000, `power_curve` 16, `brake_bite` 1, launch boost 2.5 to 12 m/s) with the type's own `top_speed`, `acceleration`, `brake`, `turn_radius`, so each should drive as it did; step height, climb, body lean and camera are by size. They are first values: none was driven in game when this was written.

**Consequences.**
- The driver of each of the seven now predicts it: steering is immediate instead of about 100 ms plus half a round trip late.
- The driver's chase camera is the drive camera (spring, tilt, slightly wider with speed) with the old distance and height.
- Seats, guns, panes and holes of the seven now lie on the slope with the hull (`AttitudeOf`); the hit box stays upright, as for every driven vehicle.
- `CarModel.Step`, `ServerCar.Step` / `StepParked` and the client's `VehicleWheels.Step` have no shipped user left. They are not removed here (tests and the shared turn-radius rule still reference `CarModel`); removing them is a follow-up.

**Amendment (#295, Vehicles turn about the rear axle).** On its wheels a vehicle turns about its fixed axle, not its origin: `drive.land.pivot_behind` is that axle's z in the model (the mean of several: flak, Scud), set on every drive block (`EveryVehicleDrivesTests`).
- In the shared `DriveModel.Step` the origin gets the sideways speed `-yaw rate x pivot_behind` (the same form as `stern_swing` afloat, opposite sign), so the server and the driver's prediction agree and the reference position (hull sweep, seats, hit volumes, snapshot record, rewind) is where it was. `Slip` on the wire stays the slide alone.
- `turn_radius` is now the radius of the fixed axle's path, numbers unchanged: the origin goes round `sqrt(turn_radius^2 + pivot_behind^2)` (the car 3.6 -> 3.85 m, the bus 9.5 -> 9.9 m) and the nose wider still. This also changes ADR 0014's "the car's numbers reproduce `CarModel`": the yaw rate at a speed is the same, the origin's circle is not.
- A trailer is pulled by the origin's sideways speed (`DriveModel.OriginSlip`), and the jackknife block measures the hitch's swing from the pivot: a hitch near the fixed axle hardly swings, as on a real tractor.
- Afloat and in the air nothing changes (the boat's pivot is `stern_swing`; in the air it turns about its origin).

**Considered.** Porting only the truck (the report), leaving six types on a model nobody should add to. Tilting the old model's box to the ground (a second slope fit, still no prediction, still two models).
