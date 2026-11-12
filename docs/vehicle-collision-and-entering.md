# Vehicle collision and entering (#217)

The decisions of #217 were not grilled with the owner. These are conservative defaults, easy to change.

## Defaults

- **Solid to on-foot players.** Every vehicle is a box for on-foot movement: its hit volume (`half_width`, `half_length`, `height` in `vehicles.json`), ground to roof, the same size as the `ServerCar` body and the bullet hit test.
- **The box is a body of its own.** `VehicleCollider` is a static body on `CollisionLayers.VehicleSolid` (layer 8). The server keeps one for each `ServerCar` (`ServerCar.PlaceSolid`, called for every vehicle before the players of a tick move) and every client one for each vehicle, at the last snapshot. Both are placed from the numbers of the snapshot record (the server rounds its own place the same way), so for a standing vehicle the two boxes are identical.
- **Why not the `ServerCar` body itself.** That body has a collision exception with every player, so that it drives over them. In Godot (Jolt and Godot Physics alike) an exception works both ways: the player's move ignores the car too. The first version of #217 put the car's layer in the player's mask and relied on the car's body; on the server players walked straight through, while the client's copy stopped the prediction, so the two disagreed on every tick spent against a vehicle (the view shook, and a player who kept pushing came out on the far side).
- **Shared step.** `Player.Simulate` is the shared move. Its collision mask is `CollisionLayers.OnFoot` (world + vehicle solid boxes) on foot and world only while seated (the body sits inside the vehicle's box).
- **Run-over unchanged.** Vehicles still ignore players in their own movement (collision exceptions) and run-over is still the footprint overlap test, so a moving vehicle still runs people over; a slow one is just a wall. A box that moves into a standing player pushes them out on the player's next move.
- **Enter radius 1 m from the footprint.** `enter_radius` (file level, per-type override) is measured to the nearest edge of the oriented footprint (`VehicleReach`), with the old 2.5 m height gate. It replaces the 3 m from the centre.
- **Seats.** In reach of the vehicle, the player takes the nearest free seat anywhere on it (no per-seat limit), so the rear of a bus works. An empty vehicle still gives the driver seat first, as before.
- Exiting still places the player 1 m beside the footprint, which is inside the enter radius.

## Follow-ups

- Per-type `solid: false` / separate collision box (needs the server box split from the hit volume, and a layer scheme so the player does not collide with a non-solid body).
- Standing on a vehicle's roof or being carried by it; players trapped when a vehicle resets onto them (depenetration only).
- Nearest seat when entering an empty bus from the rear (today the driver seat).
- A moving vehicle: the client's box is one snapshot behind the server's and the drawn vehicle another 100 ms behind that, so walking into a vehicle that is driving still corrects a little.
- Bots do not plan around vehicles (the navigation mesh is the world only); they rely on their blocked counter.
