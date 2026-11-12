# Vehicle chase camera (#218)

Decisions were not grilled with the user; these are conservative defaults, easy to change.

## What it does
- The driven ground vehicle's camera sits `distance` behind and `height` above the vehicle's origin, along the mouse-orbit direction (`Player.Chase`).
- Per vehicle, optional `chase_distance` and `chase_height` in `vehicles.json`. When absent they derive from `half_length`:
  `distance = 6.5 + 1.2 * max(0, half_length - 2.2)`, `height = 1.5 + 0.4 * max(0, half_length - 2.2)`.
  Cars, jeeps and smaller keep the old 6.5 / 1.5 (flak truck about 8.1 / 2.0); the bus (half length 6.03) gets about 11.1 / 3.0.
  Helicopters keep their own `camera` block.
- A ray from the focus point (vehicle origin lifted by `height`) to the wanted camera spot, world mask only (vehicles are a separate layer, the player body is excluded). On a hit the camera stops 0.4 m short of the hit, never closer than 1 m.
- Smoothing: pulling in is instant (never show the inside of a wall); going back out eases at 8 m/s, so brushing past a tree does not jitter. Logic lives in `ShooterCore/ChaseCamera.cs` (unit tested).

## Follow-ups
- Speed-dependent distance or FOV for cars (helicopters already widen the FOV).
- The driver's own car drawn from prediction rather than interpolation (lag), separate issue.
- Ray is a single line; a thick sphere cast would stop corners poking into the lens.
- Focus inside geometry (vehicle pressed into a wall) pulls the camera to the 1 m minimum.
- Tune the bus numbers by eye; set `chase_distance` / `chase_height` in `vehicles.json` if the formula is off.
