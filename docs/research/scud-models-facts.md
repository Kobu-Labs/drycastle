# Scud, Humvee and rocket cart: model facts

Written by `scripts/make_scud_models_facts.py` from the numbers the three model scripts print (`docs/research/scud-models-facts.d/*.json`). For the `vehicles.json` entries.
The GLBs are PRIVATE (Synty SIMPLE Military geometry): `assets-private/synty/glb/simple-military/`, manifest ids `pack:synty-simple-military/<name>`, built by `scripts/build_simple_military.sh`.
All numbers are in the vehicle frame: metres, x right, y up, forward is -Z, origin on the ground at the footprint centre. `model_scale` 1, `model_yaw_deg` 0, `model_offset` [0, 0, 0] for all of them.
The models are loaded as pack prefabs (`MapLoader.LoadPrefab`): the pack's palette texture comes from `glb/simple-military/materials.json`; every other material (glass, lamps, olive paint) is in the GLB. `paint_material`/`paints` are not offered for the Humvee and the Scud (their body wears the pack's palette texture, not a flat colour; a tint would multiply the whole atlas). The cart's paint is the flat material `Olive` (`paint_material: "Olive"` works).

Limit to know first: `VehicleSeat.MaxSeats` is 4 (`godot/ShooterCore/Vehicles/VehicleRules.cs`). The Humvee has five seats (driver, front passenger, two rear passengers, roof gunner): `seats` holds at most 4 records until that constant, `SeatsRecord` and `SeatCodec` are widened. The model carries all five seat nodes.

## humvee

- model `pack:synty-simple-military/humvee`; scale from the pack: body x 0.735, y 0.60, z 0.657 (the pack car is 7.0 m long, 2.94 m wide at the body, 3.1 m to the roof), wheels uniformly shrunk to radius 0.426.
- hit box: `half_width` 1.13, `half_length` 2.32, `height` 1.86 (roof). Sticking out of it: the pintle hook (to z +2.505), the roof gunner and gun (to y 2.4), two whip antennas (to y 2.845), mirrors (x +-1.126).
- model bounds: min [-1.126, 0, -2.316], max [1.126, 2.845, 2.505].
- wheels: radius 0.426, wheelbase 3.153, track 1.877; centres `wheel_fl` [-0.938, 0.426, -1.707], `wheel_fr` [0.938, 0.426, -1.707], `wheel_rl` [-0.938, 0.426, 1.445], `wheel_rr` [0.938, 0.426, 1.445]. Front wheels sit under `wheel_fl_steer`, `wheel_fr_steer` (same position), `wheel_*` are siblings of `hull`. Wheel width 0.34.
- tow hitch (pintle hook, empty node `tow_hitch`, mesh `tow_hook`): [0, 0.62, 2.44], height 0.62 (the rocket cart's eye is at the same height).
- seats (feet, `seats[].offset`; node `seat_*` in the GLB):
  - `seat_driver` [-0.62, 0.6, -0.22]: driver, front left
  - `seat_passenger_1` [0.62, 0.6, -0.22]: front passenger, front right
  - `seat_passenger_2` [-0.45, 0.6, 0.5]: rear left
  - `seat_passenger_3` [0.45, 0.6, 0.5]: rear right
  - `seat_gunner` [0, 0.55, 0.105]: roof gunner, standing in the ring (`standing`/`exposed`; the head and shoulders are above the roof)
- suggested cockpit eye per seat (vehicle frame; eye = seat + [0, 1.05, 0.09], the Golf's rule; the camera sees through the hidden wheel meshes):
  - `seat_driver` [-0.62, 1.65, -0.13]
  - `seat_passenger_1` [0.62, 1.65, -0.13]
  - `seat_passenger_2` [-0.45, 1.65, 0.59]
  - `seat_passenger_3` [0.45, 1.65, 0.59]
  - hide in the cockpit view: `steering_wheel_rim`, `steering_wheel_spokes` (the rig draws a turning wheel). The windscreen top is at y 1.79, the roof inside at about 1.83: the eye is below it.
- roof gun (the amphibious transport's ring-gun node pattern; `door_gun` in `vehicles.json`):
  - `gun_mount` (yaw about Y, rest 0 = forward -Z) at the ring centre [0, 1.854, 0.105]; `gun` (pitch about X) at [0, 2.157, -0.445]; muzzle at [0, 2.157, -1.271] (`muzzle_length` 0.826 from the pivot); `gunner_sight` [0, 2.377, 0.105], i.e. `eye` relative to the gun pivot [0, 0.22, 0.55].
  - `door_gun.pivot` = the ring centre; `ring_radius` 0.55 (gun pivot ahead of the ring centre); `outward_yaw_deg` 0. The roof opening is 0.84 m wide, 0.67 m long, centred on the ring centre; the ring rail is a 0.5 m radius circle on the roof.
  - node chain: `gun_mount` > `gun` > `barrel` (recoil along +Z) > `muzzle`; `gunner_sight` under `gun`. All under `hull`.
- lamps (`lights`): head [-0.77, 0.96, -2.28], [0.77, 0.96, -2.28]; tail [-0.8, 0.88, 2.29], [0.8, 0.88, 2.29]. Materials `Emissive warm headlight lens`, `Emissive red tail lamps`, `Emissive amber indicators`, `Emissive reverse lamps` are in the model, so no extra lens meshes are made.
- glass (`vehicles.json` `glass`, 6 panes; node `glass_N` = list index; windows are real openings, nothing behind a shattered pane):

```json
  { "id": "windscreen_l", "centre": [-0.51, 1.551, -0.945], "normal": [0, 0.075, -0.997], "size": [0.928, 0.477], "outline": [[0.464, 0.239], [-0.462, 0.239], [-0.464, -0.239], [0.464, -0.239]] },
  { "id": "windscreen_r", "centre": [0.51, 1.551, -0.945], "normal": [0, 0.075, -0.997], "size": [0.928, 0.477], "outline": [[0.462, 0.239], [-0.464, 0.239], [-0.464, -0.239], [0.464, -0.239]] },
  { "id": "door_front_l", "centre": [-1.078, 1.48, -0.41], "normal": [-1, 0, 0], "size": [0.709, 0.541], "outline": [[-0.354, -0.27], [0.349, -0.27], [0.354, 0.27], [-0.35, 0.27]] },
  { "id": "door_rear_l", "centre": [-1.078, 1.484, 0.496], "normal": [-1, 0, 0], "size": [0.702, 0.55], "outline": [[-0.351, -0.275], [0.351, -0.275], [0.348, 0.275], [-0.351, 0.266]] },
  { "id": "door_front_r", "centre": [1.078, 1.48, -0.41], "normal": [1, 0, 0], "size": [0.709, 0.541], "outline": [[0.35, 0.27], [-0.354, 0.27], [-0.349, -0.27], [0.354, -0.27]] },
  { "id": "door_rear_r", "centre": [1.078, 1.484, 0.496], "normal": [1, 0, 0], "size": [0.702, 0.55], "outline": [[0.351, 0.266], [-0.348, 0.275], [-0.351, -0.275], [0.351, -0.275]] }
```

- nodes: `humvee` > `hull` (body, interior, glass, lamps, seats, hook, gun) and `wheel_fl_steer` > `wheel_fl`, `wheel_fr_steer` > `wheel_fr`, `wheel_rl`, `wheel_rr` as siblings of `hull`.
- interior (all under `hull`): floor at y 0.53, tunnel, dash, `steering_wheel_rim`/`steering_wheel_spokes` (centre [-0.62, 1.10, -0.60], tilted 25 degrees), four seats (cushion top y 0.85), door cards, rear partition at z 0.955. The body shell is two-sided (pack material `Military_Green_2s`).

## scud

- model `pack:synty-simple-military/scud`, flying missile `pack:synty-simple-military/scud_missile`; scale from the pack 0.8 (pack truck 14.8 m long, 3.9 m wide): 11.9 m long with the lying missile, 3.13 m wide.
- hit box: `half_width` 1.57, `half_length` 5.95 (cab front z -5.91, rear bumper z 5.354, the missile's nose reaches -5.954 and its tail 5.17 when lying, the rear frame bars z 5.954), `height` 3.24 (body) to 3.54 (top of the lying missile; the rack and the fins reach 4.0). Model bounds: min [-1.566, 0, -5.954], max [1.566, 4.002, 5.954].
- wheels: 4 axles, radius 0.736, track 2.671, tyre width 0.44; axle z axle1 -3.539, axle2 -1.38, axle3 1.814, axle4 3.97 (first to last 7.509). Names: `wheel_l1..l4`, `wheel_r1..r4` (l = -x, the number counts from the front). Axles 1 and 2 steer: `wheel_l1_steer`, `wheel_r1_steer`, `wheel_l2_steer`, `wheel_r2_steer` (the parent at the wheel centre, turn about Y), the wheel under it rolls about X. Axles 3 and 4 are fixed. `wheel_*` are siblings of `hull`. Steering geometry (the game's `atan(wheelbase / radius)` rule) wants a front-axle-to-rear-axle distance: take the middle of the steered pair to the middle of the fixed pair, 5.352.
  - centres: `wheel_l1` [-1.336, 0.736, -3.539], `wheel_r1` [1.336, 0.736, -3.539], `wheel_l2` [-1.336, 0.736, -1.38], `wheel_r2` [1.336, 0.736, -1.38], `wheel_l3` [-1.336, 0.736, 1.814], `wheel_r3` [1.336, 0.736, 1.814], `wheel_l4` [-1.336, 0.736, 3.97], `wheel_r4` [1.336, 0.736, 3.97]
- seats (feet): `seat_driver` [-0.892, 1.15, -4.328], `seat_operator` [0.892, 1.15, -4.328]. Driver in the left half cab, operator in the right one (the missile's nose lies between the two half cabs, as on the real truck).
- suggested cockpit eye (seat + [0, 1.05, 0.09]): `seat_driver` [-0.892, 2.2, -4.238], `seat_operator` [0.892, 2.2, -4.238]. Steering wheel centre [-0.892, 1.88, -4.962]; hide `steering_wheel_rim`, `steering_wheel_spokes`. The cab roof inside is at y 2.66, the windscreen spans y 1.99-2.42, side windows 2.11-2.55, so the eye is inside the window band.
- lamps (`lights`): head [-0.92, 1.344, -5.91], [0.92, 1.344, -5.91]; tail [-0.92, 1.344, 5.354], [0.92, 1.344, 5.354]. Emissive head and tail lens materials are in the model.
- rack and missile:
  - node `rack` (under `hull`): pivot on the rear hinge axis at [0, 2.62, 3.634]. Rotation about X: 0 = lying, pointing forward over the cab; +90 degrees = vertical, the tail down (at 90 degrees the missile axis is at z 4.111, the tail end y 1.08 above the ground and the nose y 12.2: measured by the check script). The model does not animate it.
  - node `missile` under `rack`: origin at the missile's centre of mass, [0, 3.097, 0.364] in vehicle frame when lying (axis height 3.097); nose along -Z. Hide it at launch and spawn the missile entity at its world position with the rack's direction.
  - the missile: length 11.126, section 0.88 x 0.88 (fins 0.93 wide, 1.81 high); from the origin the nose is 6.318 ahead and the tail 4.807 behind. Rack length 6.84.
  - `scud_missile.glb`: one mesh node `missile` at the origin (its centre of mass), nose along -Z, tail +Z; size [0.93, 1.81, 11.13] (x, y, z); fins are in the vertical (y) plane. Same material as the truck.
- glass (`vehicles.json` `glass`, 8 panes; the two small windows on the inner walls of the half cabs next to the missile are painted glass-coloured meshes, not panes):

```json
  { "id": "windscreen_l", "centre": [-0.913, 2.204, -6.27], "normal": [0, 0, -1], "size": [0.675, 0.422], "outline": [[0.338, 0.211], [-0.338, 0.211], [-0.338, -0.211], [0.338, -0.211]] },
  { "id": "windscreen_r", "centre": [0.913, 2.204, -6.27], "normal": [0, 0, -1], "size": [0.675, 0.422], "outline": [[0.338, 0.211], [-0.338, 0.211], [-0.338, -0.211], [0.338, -0.211]] },
  { "id": "door_front_l", "centre": [-1.336, 2.333, -5.458], "normal": [-1, 0, 0], "size": [0.562, 0.442], "outline": [[-0.28, -0.221], [0.281, -0.221], [0.28, 0.221], [-0.281, 0.221]] },
  { "id": "door_rear_l", "centre": [-1.336, 2.333, -4.649], "normal": [-1, 0, 0], "size": [0.585, 0.442], "outline": [[-0.292, -0.221], [0.292, -0.221], [0.292, 0.221], [-0.292, 0.221]] },
  { "id": "door_front_r", "centre": [1.336, 2.333, -5.458], "normal": [1, 0, 0], "size": [0.562, 0.442], "outline": [[0.281, 0.221], [-0.28, 0.221], [-0.281, -0.221], [0.28, -0.221]] },
  { "id": "door_rear_r", "centre": [1.336, 2.333, -4.649], "normal": [1, 0, 0], "size": [0.585, 0.442], "outline": [[0.292, 0.221], [-0.292, 0.221], [-0.292, -0.221], [0.292, -0.221]] },
  { "id": "corner_l", "centre": [-1.336, 2.204, -6.063], "normal": [-1, 0, 0], "size": [0.291, 0.422], "outline": [[-0.146, 0.211], [-0.146, -0.211], [0.146, -0.211], [0.144, 0.211]] },
  { "id": "corner_r", "centre": [1.336, 2.204, -6.063], "normal": [1, 0, 0], "size": [0.291, 0.422], "outline": [[-0.144, 0.211], [-0.146, -0.211], [0.146, -0.211], [0.146, 0.211]] }
```

- nodes: `scud` > `hull` (body, cab, glass, `rack` > `missile`, seats, lamps) and the wheel nodes above as siblings of `hull`.

## rocket_cart

- model `pack:synty-simple-military/rocket_cart`; generated cart (olive, flat materials `Olive`, `OliveDark`, `DarkMetal`, `Steel`, `Rubber`, `Khaki`) carrying a copy of the pack's missile mesh (same scale as the truck's, 11.1 m). Model bounds: min [-1.23, 0, -6.298], max [1.23, 2.355, 6.207].
- hit box: `half_width` 1.23, `half_length` 6.3, `height` 2.35 (the missile's fin tips).
- single axle at [0, 0.46, 1.4] (node `axle`): two wheels `wheel_l` [-1.02, 0.46, 1.4], `wheel_r` [1.02, 0.46, 1.4]; radius 0.46, tyre width 0.26, track 2.04; siblings of `hull`, roll about X, no steering.
- drawbar: runs forward under the missile's nose to a towing eye (node `tow_eye`, a horizontal ring) at [0, 0.62, -6.2]; length from the axle to the eye 7.6 m, eye height 0.62 (the Humvee's `tow_hitch` height). A landing leg stands under the drawbar, 0.6 m behind the missile's nose.
- coupling: a tower's tow_hitch must meet this model's tow_eye: cart origin = tower origin + R(yaw) * (tower.tow_hitch - cart.tow_eye) (both vehicle frames, y equal at 0.62). With the Humvee's hitch at [0, 0.62, 2.44] the cart's origin is 8.64 m behind the Humvee's origin, the cart's axle 10.04 m behind it.
- cradle: two saddles with straps at z -1.40 and 4.20, the frame rails at y 0.80. Node `missile` (under `hull`): origin at the missile's centre of mass [0, 1.45, 1.4] (over the axle), axis height 1.45, nose z -4.918, tail z 6.207, length 11.126; hide it when the cart is empty.
- lamps: `light_tail_l` [-0.92, 0.82, 4.95], `light_tail_r` [0.92, 0.82, 4.95] (rear lamp board, red `Emissive red tail lamps` and amber `Emissive amber indicators` lenses in the model). No head lamps, no glass, no seats.
- nodes: `rocket_cart` > `hull` (frame, drawbar, cradle, fenders, lamps, `missile`, `tow_eye`, `axle`, `light_tail_*`), `wheel_l`, `wheel_r` as siblings of `hull`.

