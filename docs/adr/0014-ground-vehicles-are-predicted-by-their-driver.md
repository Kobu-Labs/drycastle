# Ground vehicles are predicted by their driver

Decided for issue #258, extending ADR 0010. ADR 0010 made the amphibious transport the first land vehicle whose driver's client steps it (`DriveModel`, vehicles.json `drive`), and left every other car interpolated: "Predicting every vehicle now ... worth doing, but not as a side effect of this". The driver of an unpredicted car saw it about 100 ms late (the interpolation delay) plus half a round trip before their steering took effect. This decision makes the **standard car** the second predicted land vehicle, through the same model, and states the rule for the other ground types. The other 28 ground vehicles (motorcycle, bicycle, jeep, quad, bus, boat, flak truck) are unchanged and stay on `CarModel` until each gets a `drive` block of its own.

**What changes.** A vehicle type with a `drive` block is stepped by its driver's client and by the server, never by `CarModel`. For the car that means:
- The server steps the car once per driver input in `SimulateClient` (`StepDrive`), as it does the helicopter and the transport; `StepVehicles` only fills in the steps for a driver whose inputs have stopped (`DriveStarveTicks`, 6 ticks, then hands off).
- The driver's client steps its own copy with every input it sends (`DrivePrediction`) and reconciles on the acknowledgement.
- The snapshot is unchanged: the vehicle record and the drive record (entity type 6) that ADR 0010 defined. No protocol change.
- `VehicleData` accepts a `drive` block on a land vehicle as well as an amphibious one. A land type's water numbers are not read, so they are not checked.

**The car's numbers.** The car's `drive` block reproduces the `CarModel` it replaces, using the same `top_speed`, `acceleration`, `brake`, `turn_radius` and `high_speed_radius_factor` (the speed-dependent steering of #134 is `CarModel.TurnRadiusAt`, shared by both models) and the car's `VehicleHandling` defaults:
- Launch: `launch_boost` 2.5 at `launch_speed` 12, the same formula.
- Top speed: `power_curve` 16, so the pull fades only in the last few per cent. `DriveModel` cannot cap a speed exactly, so the top end is soft. Measured on a flat road: the speed stays within 4% of `CarModel`'s after 0.5 s; both reach 44.4 m/s.
- Brake and coast: `brake_bite` 1 (the whole brake from the first tick), `coast_drag` 0. Measured from top speed: braking stops in 103.0 m in both models; coasting in 656 m in both.
- Keys: `throttle_rise`, `throttle_fall`, `steer_rise`, `steer_fall` at 200 per second and `steer_calm` 1, so the keys act as the car's raw keys did, without the keyboard ramp the amphibious transport has.
- The tyres hold the car to its heading: `grip` 1000, and the handbrake's `handbrake_grip` 0.3 of it. `CarModel` never slid, so the car does not slide now either. The yaw rate is capped only by the grip, which at this value does not bite.
- Steering: `yaw_response` 20 per second. `CarModel` slews the yaw rate linearly at `YawSlew` 8 rad/s²; the model eases towards the target exponentially. Both reach 90% of a full lock in 0.13 s. The yaw rate at 5, 15 and 40 m/s is within 5% of the circle `TurnRadiusAt` gives.
- Handbrake: `handbrake_decel` 12 and `handbrake_steer` 1.5, the `CarModel` values.
- Reverse: `reverse_top_fraction` 0.2.
- Climb: `max_climb_deg` 50, the floor angle the server's `CharacterBody3D` used; `step_height` 0.4.

**What differs, and is accepted.** Slopes and kerbs are the wheels' business (four ground rays), not `MoveAndSlide`'s, so the car lies on the ground under its wheels and drives over a kerb of 0.4 m or less. The body squats, dives and leans a little under the same springs as the transport (`drive.body`, drawn only, kept small for a car). Riders sit on the seats of the drawn body. None of this changes who is hit or where the car ends up on flat ground.

**The reconcile is in the physics tick.** A snapshot's acknowledgement is applied by `ReplayDriveReconcile` in the next physics tick, with the same replay the on-foot player uses (C-02). The reason is the same: a replay in `_Process` takes the frame's delta where the server takes the tick's. This applies to every predicted vehicle, the transport included. It changes when a correction lands, not the tolerances (`DrivePrediction` keeps its epsilons: 5 cm, 0.3 m/s, 0.01 rad). The helicopter's reconcile still runs in the snapshot (ADR 0006 is not changed here).

**Sleep and the driver.** A driver's input wakes a parked driven vehicle (`Wake` in `SimulateClient`) and the step of that tick runs as before. A driven vehicle with nobody aboard rests when it is still (`DriveRest`), as the transport does.

**What is not changed.** Run-over, crash damage and vehicle-to-vehicle contacts stay on the server (`ResolveVehicleContacts` and `BumpDamage` read the drive state the server steps). A bump with another vehicle or a player is a reconcile on the driver's client, because the client's probe sees only the world's solids (ADR 0010; the bump is accepted). Riders and passengers draw from the snapshots as before. `Wire.ProtocolVersion` is not changed.

**Considered.**
- Making `CarModel`'s move pure for every car (ADR 0010, Considered). Not done: it re-tunes all of them, and the car is served by data alone.
- Adding a slide and a lateral grip to match a car that does not slide. Not done: it would change the feel the issue asks to keep.
- Moving all 29 ground vehicles at once. Not done: the issue asks for one type at a time, judged by feel.

**Consequences.**
- The car is predicted; the other 28 ground vehicles are not, and their `CarModel` stays.
- The car's drive record is sent every tick it moves, as the transport's is (its extra bytes per tick are not measured yet).
- To move another type: write its `drive` block with the same mapping (the summaries of `VehicleDriveDef` say what each number does), check it against `CarModel` or the existing model in `CarDriveTests`, then measure it.
