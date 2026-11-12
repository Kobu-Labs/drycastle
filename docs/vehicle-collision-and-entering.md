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

## Vehicle against vehicle (#253)

Decided with the owner (the first three) or by default (the rest, easy to change):

- **Every pair collides**: ground-ground, helicopter-ground, helicopter-helicopter. Always, whatever the speed; the speed only matters for damage.
- **A bump, not a hard stop.** `VehicleContact` (ShooterCore) tests the two hit boxes (oriented boxes in the plane; a pair with a helicopter in it may also separate vertically, so a skid on a roof is pushed up) and, for a helicopter, its rotor disc (`rotor_radius`, `rotor_height`: a thin disc, always pushed aside, never up or down). The pair is moved apart in inverse proportion to mass (`mass` in vehicles.json) and swaps its closing speed along the contact normal, with `bump_restitution` (0.3) coming back as a bounce. The bus barely notices a bicycle; the bicycle is shoved. A ground vehicle keeps only the part of the change along its facing (the arcade model has no sideways speed) and is moved through the world, so a bump cannot push it into a wall.
- **Damage by the crash rules.** The speed each vehicle loses in the bump above `crash_min_speed_loss` hurts it by `crash_damage_per_speed`, so a nudge hurts nobody. A type with `crash_damage: false` (the boat) bumps but takes none. The other vehicle's driver is credited (`LastAttacker`); an empty rammer credits nobody. Parked, empty vehicles are hurt too.
- **Riders take nothing from the bump itself**, only from the blast when the vehicle is destroyed (as before).
- **Server only.** `NetServer.ResolveVehicleContacts` runs at the start of the vehicle step. The pilot's client keeps predicting against the world only (it sees other vehicles a snapshot late, so predicting against them would shake the view); the helicopter reconciliation (ADR 0006) carries the bump to it.
- **Stuck cases.** A bump pushes apart, so a vehicle wedged against another works itself free. A vehicle that respawns inside another is pushed out the same way; there is no respawn hold.

## Follow-ups

- Per-type `solid: false` / separate collision box (needs the server box split from the hit volume, and a layer scheme so the player does not collide with a non-solid body).
- Standing on a vehicle's roof or being carried by it; players trapped when a vehicle resets onto them (depenetration only).
- Nearest seat when entering an empty bus from the rear (today the driver seat).
- A moving vehicle: the client's box is one snapshot behind the server's and the drawn vehicle another 100 ms behind that, so walking into a vehicle that is driving still corrects a little.
- Bots do not plan around vehicles (the navigation mesh is the world only); they rely on their blocked counter.
