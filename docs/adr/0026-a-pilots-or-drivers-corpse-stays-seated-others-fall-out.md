# A pilot's or driver's corpse stays seated; every other rider's corpse falls out

Decided for issue #308 (a corpse left hanging at the seat's height). Builds on ADR 0018 (loot rules unchanged), ADR 0002 (no new wire fields) and ADR 0023 (the flat water surface).

**Passengers, gunners, door gunners.** At `PlayerKilled`, before the seat is freed, the vehicle's velocity (`Flight.Velocity` for a helicopter, `Velocity` for a car, zero when parked) is recorded as the death push unless a blast already recorded one. The corpse is thrown with it (`Corpse.Throw`), and `ThrownBody.Drop()` starts a flight at zero velocity, so a body from a parked or hovering vehicle falls (a launch of zero still does nothing). The launch cap (`MaxThrowSpeed`, 30 m/s) stays: a helicopter at 40 m/s throws its rider at 30. A flight that runs out of time (6 s) with ground below snaps onto it instead of freezing in mid-air.

**Pilot and driver (the `driver` seat, ground vehicles too).** The corpse is made in the seat in a new `Seated` state of `Corpse.Flight` and rides with the vehicle: the server moves it to the seat each tick (`Follow`) through the corpse's ordinary position in the world state, so there is no new wire field and a client sees a corpse that moves. The seat is blocked (`VehicleSeat.Block`) while the corpse sits there: nobody can enter or switch to it, and it does not count as occupied (the abandon timer still runs). It drops (`Drop`, then falls) once the vehicle has been at rest for `SeatedRestSeconds` (1 s): at most `RestSpeed` (0.5 m/s), a helicopter also on the ground. It can be looted by the ADR 0018 rules (reach, one item at a time), but only while the vehicle is at rest: a corpse in a moving vehicle is out of reach by rule. A rider killed by a blast (a rocket splash) keeps the throw the blast gave it and does not stay seated.

**Destruction.** `Detonate` throws every corpse still seated in the wreck, and every rider it kills, with the vehicle's velocity (read before the blast zeroes it) plus the blast knockback away from the blast centre, scaled up as for any blast kill (`ThrowFromBlast`). The cap clamps the sum, so a fast helicopter's rider is thrown at 30 m/s, not at the sum.

**Water.** A corpse that reaches the water surface (the flight's water query is `Water.SurfaceY`) lands at the surface height in a new `Floating` state, not on the bed: it is not stepped, stays lootable, and the position replicated is the flat surface (ADR 0023). The client tells a floating corpse from the water under it (a resting corpse whose height is the surface's) and draws it rising and falling with `WaterWaves` and drifting about where the server has it. That is cosmetic: the reach and the pickup use the server position.

## Considered

- **Every corpse falls, the pilot's too (the ticket's main proposal).** Rejected for the seat of control: a pilot who dies at the stick leaves the vehicle on its own path in front of other players, and a corpse that vanishes from the cockpit reads wrong; the vehicle without its pilot is the event. Passengers and gunners have an open door and no stick, so they fall.
- **A new wire field or entity for the seated corpse.** Rejected: the corpse's position already replicates and the client interpolates it; the seat is a server-side fact (ADR 0002).
- **The corpse keeps riding until the vehicle is gone.** Rejected: an abandoned vehicle returns to its origin, and a corpse that rides home is no longer where it can be found; one second of rest is enough to loot it and then it falls beside the wreck's path.

## Consequences

- A corpse that drops from a stopped vehicle falls straight down through it (the ray mask is the world only), so it lies under the vehicle; placing it at a free side is the ticket's step 3 and is not done.
- A seated corpse keeps the yaw it was made with; the vehicle turning does not turn it (#310 gives the pilot's body a yaw of its own).
- The client's pickup prompt shows within reach whether or not the vehicle is at rest; the server refuses while it moves.
- `Corpse.Flight` gains `Seated` and `Floating`; target dummies use the same machine and never enter them.
