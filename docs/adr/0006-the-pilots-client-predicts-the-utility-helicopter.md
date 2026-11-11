# The pilot's client predicts the utility helicopter (the first predicted vehicle)

Decided for the utility helicopter. Builds on ADR 0002 (the snapshot layer); **changes** the rule in `VehicleRules.cs` and `NetClient.Cars.cs` that vehicles are simulated by the server only and drawn on every client, the driver's included, about 100 ms in the past.

**What changes.** The utility helicopter's flight is a pure model in ShooterCore (`FlightModel`), and two parties step it: the server, which owns the true state, and the client of whoever sits in the pilot seat (`FlightPrediction`), which steps its own copy with each input as it sends it. The server steps the helicopter once for each of the pilot's inputs, in the same place and order as it steps a body on foot, and not once per tick from the latest input as it does a car. So the snapshot's input acknowledgement is the helicopter's as well: when it arrives, the client compares what it predicted for that input with the server's state, and beyond a small tolerance it takes the server's state and replays the inputs still on their way. Every other client, and the riders who are not the pilot, draw the helicopter interpolated as before.

**Why.** A car's 100 ms of delay plus the round trip is tolerable because a car has one slow degree of freedom to correct. A helicopter is flown by its attitude: the pilot corrects pitch, roll, yaw and lift several times a second, and the same delay makes it overshoot everything. The player on foot already has prediction and reconciliation for the same reason.

**Considered.** Leaving it interpolated (unflyable, above). Letting the pilot's client own the flight and the server accept it (a cheat's free pass, and the server could no longer decide crashes or the door gunner's position). Predicting every vehicle now (the car model moves through `MoveAndSlide` inside `ServerCar` and is not a pure step; worth doing, but not as a side effect of this). Rolling back a full physics simulation (far heavier than a hull sweep).

**What it takes.**
- The world is asked the same questions on both sides through one interface (`IFlightWorld`): the ground under the origin, the water surface, and a sweep of the hull box and the rotor disc. In the game that is `FlightProbe` over the solid colliders, which server and client already share for the player's prediction (the same map build, as `docs/shooter-server.md` requires).
- The snapshot gains a flight record per flying vehicle (entity type 5: velocity, pitch, roll, rotor speed, flags) beside its vehicle record (position, yaw, pilot). Protocol 21. What is not sent (attitude rates, the key ramp) the client keeps from its own prediction, as the player's prediction keeps its velocity.
- The model must not depend on anything only one side knows. Its inputs are the quantised `NetInput`, the type's numbers in `vehicles.json`, and the world.

**Consequences.**
- A pilot whose inputs stop arriving would freeze the helicopter in the air, as a stalled player freezes on foot. After 6 ticks without one the server flies it hands off (a hover) until they resume.
- Stepping per input means two flight steps in a tick when the server catches up on a late input, as for a body on foot.
- The hit volume stays an upright box that turns with the yaw only, on the server and for the clients' bullet holes; the drawn model pitches and rolls inside it.
- The pilot sees the helicopter in the present and everyone else about 100 ms in the past. The door gunner's shots are resolved by the server from its own state, as every shot is.
