# Vehicle collision and entering (#217)

The decisions of #217 were not grilled with the owner. These are conservative defaults, easy to change.

## Defaults

- **Solid to on-foot players.** Every vehicle is a box for on-foot movement: its hit volume (`half_width`, `half_length`, `height` in `vehicles.json`), ground to roof. One box, so the server's `ServerCar` body and the bullet hit test already agree with it.
- **Shared step.** `Player.Simulate` is the shared move. Its collision mask is `CollisionLayers.OnFoot` (world + vehicles) on foot and world only while seated (the body sits inside the vehicle's box). The client keeps a static copy of each vehicle's box (`VehicleCollider`) at the last snapshot position, so prediction is stopped by the same box the server uses.
- **Run-over unchanged.** Vehicles still ignore players in their own movement (collision exceptions) and run-over is still the footprint overlap test, so a moving vehicle still runs people over; a slow one is just a wall.
- **Enter radius 1 m from the footprint.** `enter_radius` (file level, per-type override) is measured to the nearest edge of the oriented footprint (`VehicleReach`), with the old 2.5 m height gate. It replaces the 3 m from the centre.
- **Seats.** In reach of the vehicle, the player takes the nearest free seat anywhere on it (no per-seat limit), so the rear of a bus works. An empty vehicle still gives the driver seat first, as before.
- Exiting still places the player 1 m beside the footprint, which is inside the enter radius.

## Follow-ups

- Per-type `solid: false` / separate collision box (needs the server box split from the hit volume, and a layer scheme so the player does not collide with a non-solid body).
- Standing on a vehicle's roof or being carried by it; players trapped when a vehicle resets onto them (depenetration only).
- Nearest seat when entering an empty bus from the rear (today the driver seat).
- Verify in game: the player mask vs the car's collision exceptions (written against Godot physics rules, not run).
