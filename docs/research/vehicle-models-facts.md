# Vehicle models for #325: facts of the four Synty models

Measured on the imported GLBs, not guessed (anything not measured is marked). Input to the standardization step of `docs/standards/car-design.md` (reorient, ground origin, wheel names and steer parents, `hull`, glass panes, seats and eyes).
The GLBs are PRIVATE (paid Synty geometry): `assets-private/synty/glb/{military,battle-royale,quadbike}/`, manifest ids `pack:synty-military/SM_Veh_Car_Small_01`, `pack:synty-battle-royale/SM_Veh_Truck_01`, `pack:synty-battle-royale/SM_Veh_Bike_01`, `pack:synty-quadbike/SM_Veh_QuadBike_01`. Built by `scripts/build_vehicle_packs.sh` (the military car was already there: `scripts/private_pack_glb.gd`).

## Frame of every number below: AS IMPORTED

Metres, x right of the glTF axes, y up. **All four models face +Z** (contract: forward -Z). The "left" of the pack names (`_fl`, `_rl`) is +x. To reach the contract frame: yaw 180 degrees about Y, i.e. (x, z) -> (-x, -z); a part named `_fl` then sits at -x, which is still the vehicle's left (facing -Z). Nothing needs scaling: all four are real-world size (`model_scale` 1). Origin is already on the ground at x = 0 (lowest wheel point y -0.006 .. +0.011), but NOT at the footprint centre along z (per model below).

Measuring tools (committed): `scripts/vehicle_model_probe.gd` (node tree, pivots, tight world bounds, UV box, tris; `--tris` lists glass triangles), `scripts/vehicle_model_dump.gd` + `scripts/vehicle_model_analyze.py` (welded components, glass panes, rays through the cabin, floor/seat stacks), `scripts/vehicle_atlas_variants.py` (which atlas is a valid paint swap). Rays use Godot's front face (clockwise); "double-skinned" = a ray crosses an inner front face then an outer one.

## Summary

| | `SM_Veh_Car_Small_01` | `SM_Veh_Truck_01` | `SM_Veh_Bike_01` | `SM_Veh_QuadBike_01` |
|---|---|---|---|---|
| pack / file | Military, `glb/military/` | Battle Royale, `glb/battle-royale/` | Battle Royale | QuadBike, `glb/quadbike/` |
| size w x h x l (m) | 2.053 x 1.619 x 3.849 (mirrors; body 1.70 wide) | 3.052 x 3.164 x 7.054 (mirrors; cab 2.72 wide) | 0.824 x 1.187 x 2.183 (handlebar width) | 1.189 x 1.319 x 2.280 |
| bounds min .. max | (-1.026, -0.004, -1.921) .. (1.026, 1.615, 1.928) | (-1.526, 0.003, -3.277) .. (1.526, 3.168, 3.777) | (-0.412, -0.006, -1.015) .. (0.412, 1.181, 1.168) | (-0.595, 0.011, -1.122) .. (0.595, 1.330, 1.158) |
| z centre of bounds | +0.004 | +0.250 | +0.077 | +0.018 |
| faces | +Z | +Z | +Z | +Z |
| wheels | 4, r 0.314 | 6 (3 axles), r 0.640 | 2, r 0.349 front / 0.321 rear | 4, r 0.307 |
| glass | 1 mesh, 6 panes | 1 mesh, 5 panes (one wide windscreen) | none | none |
| interior | floor, 2 front seats + rear bench, wheel, dash (inferred) | floor, 3-seat bench, wheel, dash (inferred) | none (saddle in the frame mesh) | none (saddle in the frame mesh) |
| shell | double-skinned cabin, 0.04-0.08 m walls | double-skinned cab and bed | solid parts | solid parts |
| colour atlases | 10 (5 body colours) | 6 (camo + flat) | 6 | 4 |
| tris | 4489 | 12601 | 5134 | 8372 |
| destroyed variant | one merged mesh, 3161 tris | one merged mesh, 12595 tris | one merged mesh, 5134 tris | none in the pack |
| `hull` node | none | none | none | none |

Every prefab also carries collision: a `MeshCollider` StaticBody3D with a CollisionShape3D under the body mesh (convex hull; 17 points on the car), under the truck's bed and under each wheel (cylinder-like shape turned 90 degrees, offset in x). Shape sizes not measured. The quad bike GLB has no collision (exported from the FBX).

## SM_Veh_Car_Small_01 (military)

- File `assets-private/synty/glb/military/SM_Veh_Car_Small_01.glb`; node tree (name: local position; the wrapper `Prefab` is the exporter's, origin 0):
  - `SM_Veh_Car_Small_01` (body, 2535 tris, pivot 0)
    - `MeshCollider` > `CollisionShape3D`
    - `SM_Veh_Car_Small_01_Glass` (48 tris), **local (0, 0.254, 0)**: the glass is translated up; the world numbers below include it
    - `SM_Veh_Car_Small_01_SteeringW` (242 tris) (0.379, 0.974, 0.252), rotated +25 deg about X; mesh local box x +-0.211, y -0.211..0.210, z -0.033..0.145: rim in the local XY plane, column along local +Z. Wheel centre in world ~(0.379, 0.98, 0.27)
    - `SM_Veh_Car_Small_01_Wheel_fl` (0.649, 0.310, 1.272), `_fr` (-0.649, 0.310, 1.272), `_rl` (0.649, 0.310, -1.373), `_rr` (-0.649, 0.310, -1.373); each 416 tris with its own MeshCollider
- Wheels: 4, radius 0.314 (mesh box y,z +-0.314), width 0.201. The pivot is at the axle height and z but **0.067 inboard of the tyre centre**: tyre centres x +-0.716 (mesh x -0.034..0.167 from the pivot). Wheelbase 2.645 (z 1.272 to -1.373); the bounds centre is at z +0.004, the axle midpoint at z -0.050. Lowest wheel point y -0.004.
- Glass: ONE mesh, 48 tris, six welded pieces = the panes, each a single sheet with the front face outward (the pane is invisible from inside under back-face culling). Panes (world, +Z forward):
  - windscreen: centre (0, 1.289, 0.539), normal (0, 0.76, 0.64), 1.475 x 0.664, 16 tris (a 4-wide x 2-high grid of quads, shallow curve)
  - rear window: centre (0, 1.294, -1.649), normal (0, 0.47, -0.89), 1.308 x 0.560, 16 tris
  - side front, left (+x): centre (0.688, 1.239, 0.064), normal (0.94, 0.34, 0.02), 1.249 x 0.516, 4 tris; right (-x) mirrored at (-0.688, ...)
  - side rear, left (+x): centre (0.695, 1.266, -1.078), normal (0.94, 0.34, 0), 0.889 x 0.463, 4 tris; right mirrored
  - The side panes are bent (x extent 0.17 m across 1.25 m) and slope inwards (normal y +0.34). The body has real openings behind them (a ray from the cabin centre through the side windows at y 1.13 hits nothing).
  - Material of the glass: `PolygonMilitary_01_A_Glass_Alt_mat` (atlas Alts/PolygonMilitary_01_A.png, alpha 0.56, all UVs on one texel).
- Interior: **yes**, modelled. Welded components of the body mesh: cabin shell (1054 tris, x +-0.848, y 0.254..1.615), two front seats (112 tris each: x 0.115..0.659 and -0.634..-0.090, z -0.595..0.111, cushion top y ~0.58-0.60, backrest to ~1.11-1.19), a rear bench (112 tris: x -0.633..0.659, z -1.466..-0.788, y to 1.19), the floor (slab y 0.26..0.35, floor top 0.35), a 132-tri component x +-0.73, y 0.347..1.058, z -0.444..0.762 that I take for the dash/console (inferred, not looked at), roof lining (ceiling underside y ~1.47-1.55 over the cabin). Pedals/handbrake bits not identified.
- Shell: cabin walls, roof and floor are **double-skinned and closed** (rays: inner front face 0.74, outer 0.78-0.83 at the doors; roof 1.60 / 1.55; floor 0.35 / 0.26), so no two-sided material is needed there. The bonnet/boot region (|z| > 0.77, e.g. y 0.72 z 0.77 and 1.54) showed only the outer face (single skin, back-face culled from inside: nothing to see there anyway).
- Seats (feet on the cushion top, measured positions of the seat components, no seat node exists): driver (0.387, 0.60, -0.24) (the wheel is at x +0.379: left-hand drive facing +Z), front passenger (-0.362, 0.60, -0.24), rear bench cushion top y 0.67 (x 0.39 scan), bench centre z -1.13. Eye candidate = cushion + 0.65: y ~1.25, under the ceiling (1.47) and inside the side-window band (y 1.0..1.48): meets "eye inside the cabin and sees a pane".
- Atlas / colours: base `Land_Vehicles_03` (`PolygonMilitary` `Textures/Vehicles/Land_Vehicles_03.png`, material `Land_Vehicles_03_mat`; shared by the pack's pickup, light tank, ...). The car's UVs lie in u 0.04-0.93, v 0.51-0.99: only the flat swatch area, never the camo band. The ten atlases `Land_Vehicles_01..10.png` (2048 x 2048) have one layout and differ only in the swatch colours, so each is a valid paint swap: body (213,165,72) yellow in 03 becomes 01 blue (49,104,161), 02 green (69,110,59), 04 red (124,39,31), 05 blue, 06 green, 07 yellow (identical to 03 on the car), 08 red, 09 light grey (180,177,175), 10 light grey + dark bumpers (43,43,44). **5 body colours: yellow (03, 07), blue (01, 05), green (02, 06), red (04, 08), grey (09, 10)**; the pairs differ only in trim swatches (2-6 tris). Only 01-05 and 08 were referenced by prefabs; `scripts/vehicle_pack_variants.py` added 06, 07, 09, 10 to `glb/military/tex/` (add-only; `glb/military/materials.json` untouched: its entries for 01-05 and 08 exist as `Land_Vehicles_0N_mat`). The `Textures/Land_Vehicle_01..03.png` "Alt" files are NOT variants (63% of the car's area changes, darker greys: another layout/shading).
- Destroyed: `SM_Veh_Car_Small_Destroyed_01.glb`, ONE mesh (wheels, glass fused), 3161 tris, 2.053 x 1.476 x 3.849 m, material `Land_Vehicles_01_Burnt_mat` (`Land_Vehicle_01_Burnt.png`).
- Hard for the contract: no `hull` (wheels, glass and wheel mesh are CHILDREN of the body mesh node: that node cannot become the hull without the wheels swaying with it); glass one mesh, one-sided; no lamp meshes or named lamp materials (lights are atlas texels; `CarLights` needs `light_*` nodes/materials added); wheel pivots inboard of the tyre centre; steering wheel is one mesh named `..._SteeringW` (the cockpit rig hides `steering_wheel_rim/_spokes`); collision bodies in the GLB.

## SM_Veh_Truck_01 (battle royale)

- File `glb/battle-royale/SM_Veh_Truck_01.glb`. Tree (pivots world = local, all parts are children of the body mesh whose pivot is 0):
  - `SM_Veh_Truck_01` (cab + chassis, 6561 tris) > `MeshCollider`
    - `SM_Veh_Truck_Glass_01` (16 tris) local (0, -0.025, 0)
    - `SM_Veh_Truck_SteeringW_01` (250 tris) (0.728, 2.167, 3.052), rotated -38 deg about X; mesh local box x +-0.294, y -0.584..0.043, z +-0.294: rim in the local XZ plane, column along local -Y (steer axis = local Y)
    - `SM_Veh_Truck_Trailer_01` (the cargo bed, 3182 tris) (0, 1.623, -0.920) > `MeshCollider2`
    - wheels (432 tris each, each with a MeshCollider): `SM_Veh_Truck_Wheel_fl` (1.056, 0.643, 2.190), `_fr` (-1.056, 0.643, 2.190), `_rl_01` (1.172, 0.643, -0.565), `_rl_02` (1.172, 0.643, -2.031), `_rr_03` (-1.172, 0.643, -0.565), `_rr_02` (-1.172, 0.643, -2.031). The prefab names the third right wheel `_rr_03` while its mesh resource is `..._rr_01`: pack naming slip, the node name is what the GLB has.
- Wheels: **6** on 3 axles (front z 2.190, rear pair z -0.565 and -2.031: front-to-mid 2.755, rear spacing 1.466), radius 0.640 (mesh y,z +-0.640), width 0.334 (x 1.005..1.340 on the left). Rear pivots are at the tyre centre (x +-1.172); the front pivots are 0.116 inboard (tyre centre x +-1.172). Lowest point y 0.003. Axle line y 0.643. Only the front axle would steer.
- Glass: ONE mesh, 16 tris, 5 welded pieces, one-sided, front face outward (world coordinates, +Z forward, glass offset included):
  - windscreen: centre (0, 2.465, 3.304), normal (0, 0.27, 0.96), 2.265 x 0.629, 8 tris = 4 quads across (x -1.133..1.133, z 3.200..3.407): ONE wide, slightly bowed pane
  - side, left (+x): centre (1.200, 2.416, 2.800), normal (0.99, 0.12, 0), 0.865 x 0.519, 2 tris; right (-x) mirrored
  - cab-back window, 2 panes: centre (+-0.675, 2.609, 1.778), normal (0, 0, -1), 0.740 x 0.325, 2 tris each (look into the bed)
  - Material `PolygonBattleRoyale_Veh_Glass_mat` (flat #357176, alpha 0.24, no texture). The cab has real openings (a ray through y 2.22 at z 3.07 is stopped only by the wheel and the mirror at x 1.49).
- Interior: **yes**, modelled in the cab mesh: closed cab shell (1118 tris, x +-1.36, y 0.679..2.892, z 1.427..3.637), three seats (154 / 112 / 154 tris: x 0.431..1.041, -0.345..0.266, -1.111..-0.500, all z 2.079..2.895, cushion top y 1.87-1.89, backrest top ~2.69-2.72), floor (top y ~1.19-1.34, slab 0.08 thick), dash (113 tris: x +-1.215, y 1.373..2.182, z 3.079..3.494, inferred), steering wheel (separate mesh), roof lining underside y 2.82 (roof top 2.89). Steering wheel at x +0.728 = driver on the left facing +Z, centred in the left seat.
- Shell: cab walls double-skinned and closed (inner front face 1.19, outer 1.26 at y 1.43 z 3.07); the bed is a closed floor (top y 1.49, slab 1.37-1.49) with closed side/tail boards and stake rails (y up to 2.73), open top, no tarp. A closed cab means the inside of a missing/shattered pane shows the real cabin.
- Seats (feet): driver (0.736, 1.88, 2.49), middle (-0.04, 1.88, 2.49), passenger (-0.81, 1.88, 2.49) (seat component centres, z from the cushion span 2.08-2.90; no seat nodes). Eye candidate cushion + 0.65: y ~2.53 below the roof lining (2.82) and inside the window band (y 2.16..2.77 for the sides and windscreen): inside the cabin.
- Atlas / colours: `PolygonBattleRoyale_Vehicles_01.png` (material `PolygonBattleRoyale_Veh_01_mat`, 2048 x 2048). Six atlases `PolygonBattleRoyale_Vehicles_01..06.png`, one layout; the top half of each is a camo band, the lower half flat swatches; the truck uses both (UV v 0.22-0.99; 866 body tris and 938 bed tris sit in the camo band). Changed paint on the truck (base (59,65,53) olive): 02 brown/desert camo (118,99,71), 03 flat khaki (103,102,68), 04 flat black (52,52,52), 05 flat sand (166,144,115), 06 grey camo (94,94,94). 01 = woodland camo (default). **6 valid variants.** Wheels, tyres, glass keep their colours. 02..06 are in `glb/battle-royale/tex/` and `materials.json` as `PolygonBattleRoyale_Veh_0N_mat`. `PolygonBattleRoyale_Vehicles_01_Emmision.png` is the emission map (the pack's material has emission off: lamps are not lit).
- Destroyed: `SM_Veh_Truck_Destroyed_01.glb`, ONE mesh (no separate wheels or glass), 12595 tris, 3.059 x 3.025 x 7.452 m, material `PolygonBattleRoyale_Veh_Destroyed_01_mat` (`Vehicles_01_Damaged.png`).
- Hard for the contract: **6 wheels** (contract and `DriveNodes` have `wheel_fl/fr/rl/rr`; the Scud's `wheel_l1..r4` pattern fits, three axles with one steered); no `hull` (parts are children of the cab mesh); bed (`Trailer`) is a separate 3182-tri node: keep under the hull; **no tow hitch** (the truck tows today): add an empty at the tail (bed tail z -3.28, bounds z -3.277); front-left pivot offsets; the origin is not the footprint centre (bounds centre z +0.25, axle midpoint (2.190 + (-0.565 - 2.031)/2)/2 = +0.446): hit box half_length 3.53 about z 0.25; width 3.05 (mirrors) vs cab 2.72 vs bed 2.77, height 3.16 (a roof item at y 3.0-3.17, x -0.38..0.02, z 1.5-1.9): new `half_width`/`half_length`/`height`; **seats are 1.9 m above the ground** (eye ~2.5 m): chase camera, ground-level hit boxes and rider cover heights change; the bed UVs run to u 2.64 (tiling: the atlas material needs repeat); lamps are texels (no lamp meshes/materials); two window panes the game does not have (cab back, into the bed); windscreen is one pane 2.27 m wide (split into several if a pane must stay roughly flat); no gun, no gunner seat wanted.

### As standardized (the `truck` type, #325)

`scripts/vehicles/truck.json` -> `docs/research/vehicle-facts.d/truck.json`, `pack:synty-vehicles/truck_{woodland,desert,khaki,black,sand,grey}` (Battle Royale atlases 01..06, all valid swaps). Game frame (forward -Z, origin ground at the footprint centre, the bounds centre z +0.25 of the prefab moved to 0).
- Box: half_width 1.53, half_length 3.54, height 3.17 (the old jeep: 1.28 / 2.3 / 2.2); the model is 3.053 x 3.164 x 7.07 m with lens plates. `model_scale` 1, no offset, no yaw. 12649 tris, 950 KB per skin.
- Six wheels, r 0.640, tyre centres x +-1.172, front axle z -1.94, middle 0.815, rear 2.281: `wheel_l1`, `wheel_r1` (steered, under `wheel_l1_steer`, `wheel_r1_steer`), `wheel_l2`, `wheel_r2`, `wheel_l3`, `wheel_r3` (l = -x; the Scud's pattern; the pack's `_fl`, `_fr`, `_rl_01`, `_rr_03`, `_rl_02`, `_rr_02`). Steered pivots at the tyre centre (the pack's front pivot 0.116 inboard is corrected from the mesh).
- Seats (feet = cushion top 1.88 - 0.23 like the car, `eye_height` 1.0, the eye 2.647 with 0.17 m to the roof lining 2.815): `seat_driver` (-0.736, 1.647, -2.24), `seat_passenger_1` (0.04, ...), `seat_passenger_2` (0.81, ...). The window band is y 2.16..2.77, the eyes sit in it.
- Panes (5): `glass_0` windscreen (centre (0, 2.45, -3.041), 2.265 x 0.626, 6-corner outline), `glass_1`/`glass_2` the cab-back windows (+-0.675, 2.606, -1.528; 0.74 x 0.325, normal +Z into the bed), `glass_3`/`glass_4` sides (+-1.2, 2.413, -2.55; 0.865 x 0.519).
- Lamps from the atlas texels (no lamp meshes): round headlights at x +-0.896, y 1.357, front z -3.335 (0.19 lens); red tail lamps x +-0.914, y 1.277, on the bed tail z 3.527 (the chassis lamps at z 3.24 are behind the bed's tail board).
- `tow_hitch`: (0, 0.62, 3.66), 0.13 m behind the bed tail (a trailer's drawbar eye meets it at the rocket cart's `tow_eye` height 0.62; `VehicleTowDef.Hitch` is the same point).
- Steering wheel at x -0.728 (the prefab's +0.728: a left-hand drive), column along its local -Y.
- No gun, no gunner seat (the user's decision); the Humvee's, the amphibious transport's and the utility helicopter's guns carry their own `gun` blocks.

## SM_Veh_Bike_01 (battle royale)

- File `glb/battle-royale/SM_Veh_Bike_01.glb`. A trail motorcycle. Tree (local position; world in brackets):
  - `SM_Veh_Bike_01` (frame, engine, tank, saddle, tail; 1814 tris, pivot 0) > `MeshCollider`
    - `SM_Veh_Bike_HandleBars_01` (1460 tris: handlebar, headlight area, both fork tubes) (0, 0.948, 0.362), **rotated -31.9 deg about X**: the steering head, steer axis = this node's local Y (tilted back at the top, a raked fork). Mesh local box x +-0.412, y -0.451..0.287, z -0.228..0.462
      - `SM_Veh_Bike_Front_Shock_01` (392 tris) local (0, -0.754, 0.068) [world (0, 0.344, 0.819)], a further -1.1 deg about X (the fork's lower leg; the pivot is the wheel axle)
        - `SM_Veh_Bike_Front_Wheel_01` (456 tris) local (0,0,0) [world (0, 0.344, 0.819)]; radius 0.349, width 0.105; roll axis local X
    - `SM_Veh_Bike_Rear_Shock_01` (556 tris: swing arm and shock) (0, 0.408, -0.152)
      - `SM_Veh_Bike_Rear_Wheel_01` (456 tris) local (0, -0.086, -0.498) [world (0, 0.322, -0.650)]; radius 0.321, width 0.105
  - Front wheel lowest point y -0.006, rear 0.001.
- Wheels: **2** (no fl/fr/rl/rr), different radii (front 0.349, rear 0.321), pivots on the axle (the wheel centre); the front wheel is three nodes deep under the raked steering node.
- Glass: **none**. Interior: **none** (a rider is exposed). The saddle is part of the frame mesh: a 248-tri closed component x +-0.198, y 0.606..0.933, z -1.015..-0.122; saddle top y 0.86-0.95 over z -0.9..0.0 (down-ray profile at x 0; lowest at z ~-0.2, 0.86). Rider seat (not a node; suggested from that profile): (0, 0.88, -0.40); a pillion is not modelled separately (not measured whether the saddle length reads as two seats). Handlebar grips x +-0.40 at y ~1.1, z 0.15..0.31 (the 392-tri bar component).
- Shell: not applicable (solid parts, all closed except thin cable/pipe pieces).
- Atlas / colours: the same six `PolygonBattleRoyale_Vehicles_01..06` atlases, material `PolygonBattleRoyale_Veh_01_mat`; UV v 0.25-0.99, 398 frame and 146 handlebar tris in the camo band. Same variants as the truck: 02 desert (118,99,71), 03 flat khaki, 04 flat black, 05 sand, 06 grey camo; also the frame's (88,73,55) brown changes in 04/05. **6 valid variants.**
- Destroyed: `SM_Veh_Bike_Destroyed_01.glb`, ONE mesh, 5134 tris (same tris as the intact bike, fused and lying), 0.965 x 1.169 x 2.196 m, off-centre (x -0.413..0.552).
- Hard for the contract: no `wheel_fl/fr/rl/rr` and no `_steer` pair: front wheel steers by rotating the HandleBars node about its tilted local Y and the DriveNodes animator needs a two-wheel (motorcycle) mode; the raked axis means a plain Y rotation at the wheel is wrong (rotate the whole `HandleBars` subtree about the steering axis, not about world Y); no `hull` (everything is below the frame mesh) and no bike lean model beyond what `hull` roll gives; rear wheel below a swing-arm node (suspension not driven, can stay static); front-wheel radius 0.349 vs rear 0.321 (animator uses one radius per wheel: `Position.Y`); seat point, rider hands (grips) and eye are all hand-declared; lamps are atlas texels; collision bodies in the GLB.

## SM_Veh_QuadBike_01 (quadbike)

- File `glb/quadbike/SM_Veh_QuadBike_01.glb`, made from the FBX by `scripts/quadbike_export.gd` (FBX root renamed `QuadBike`, AnimationPlayer dropped, surfaces wear the empty material `PolygonQuadBike_01`). Tree (all parts are children of the body mesh; part pivots are at the model origin unless stated):
  - `QuadBike` > `SM_Veh_QuadBike_01` (frame, engine, saddle, tank, fenders; 3228 tris)
    - `SM_Veh_QuadBike_Wheel_fl` (0.298, 0.318, 0.765), `_fr` (-0.333, 0.318, 0.765), `_rl` (0.333, 0.318, -0.694), `_rr` (-0.333, 0.318, -0.694); 456 tris each; mesh box x -0.019..0.262 (fl, rl) / -0.262..0.019 (fr, rr), y,z +-0.307/0.295
    - `SM_Veh_QuadBike_HandleBar_01` (808 tris) (0, 1.006, 0.395), no rotation; mesh box x +-0.517, y -0.136..0.128, z -0.201..0.136 (the bar plus the column below it)
    - `SM_Veh_QuadBike_Cargo_Front_01` (912 tris), `_Cargo_Back_01` (1348 tris), `_Shovel_01` (252 tris): pivot (0,0,0), vertices in body space (front rack y 0.60-1.16, z 0.66..1.16; rear rack y 0.61..1.33, z -1.11..-0.38; shovel y 0.63..0.84, z -1.12..-1.02)
- Wheels: 4, radius 0.307 (centre y 0.318, lowest point 0.011), width 0.281. Tyre centres: rear x +-0.454, front **+0.419 (left) / -0.454 (right)**: the front-left pivot is 0.035 m inboard of the others (pack asymmetry: fl pivot x 0.298, the other three +-0.333). Axles z 0.765 (front) and -0.694 (rear): wheelbase 1.459, rear track 0.908, bounds centre z 0.018, axle midpoint 0.036.
- Glass: none. Interior: none. Saddle in the frame mesh: top y 0.86-0.93 over z -0.5..+0.2 (down-ray at x 0; lowest 0.86 at z -0.1). Rider seat (suggested, not a node): (0, 0.88, -0.15). Tank/fenders around it; handlebar bar x +-0.505, y 1.04-1.12, z 0.23-0.39.
- Steer axis: the handlebar node's pivot is on the column (the column part spans z 0.27..0.53 over y 0.87..1.13, i.e. leaned back about 45 deg in the mesh), steering about a vertical Y at that pivot would shear the column: the axis was measured in the standardization (`scripts/vehicles/quad.json`): the handlebar mesh has a 140-tri stem component (a tilted block) whose principal axis (SVD) is (0, 0.7086, -0.7056) through (0, 1.031, 0.374) in the source frame, i.e. the column leans back 44.9 degrees.
- Atlas / colours: four atlases `PolygonQuadBike_01..04.png` (1024 x 1024; materials `PolygonQuadBike_0N` of the pack; `_01_Emissive.png` has emission switched off in the pack's materials), one layout; the model uses only the lower-left swatches (UV u 0.01-0.42, v 0.67-0.97; no camo). The body paint swatch (134,46,38) red in 01 becomes blue (31,88,156) in 02; 03 and 04 change the body and trim swatches: 03 olive/dark green (62,66,46)/(76,81,56) with dark seat (51,51,49), 04 sand/brown (95,84,68)/(135,116,95). The pack's `SM_Veh_QuadBike_01_Military` prefab is the same mesh with material 04 (a prefab, not a model). **4 valid variants: red (01), blue (02), olive (03), sand (04).** In `glb/quadbike/tex/` and `materials.json`.
- Hard for the contract: no `hull` and no `_steer` parents (wheels and handlebar are children of the frame mesh: re-parent); the front-left wheel asymmetry; the handlebar/steer axis above; three accessory meshes with pivots at the origin (racks, shovel; the shovel pokes 0.06 m past the rear: bounds z -1.122); no glass (rider exposed: "exposed" cover), no seat node, no lamp meshes (texels); the pack calls the `_fl` wheel +x; 1024 atlas (lower texel density than the 2048 packs).

## What the standardization step will meet, in order of effort

1. All four face +Z: yaw 180 about Y (flips x too; wheel names keep their meaning).
2. Parts are children of the body mesh in all four: re-parent so `hull` holds body, glass, interior, steering wheel, lamps, seats, and wheels stay siblings of `hull`; the body mesh keeps pivot 0.
3. Wheel counts and names: truck 6 (3 axles), bike 2 (nested, raked fork), car and quad 4 with pivots off the tyre centre in x. The animator and `DriveNodes` only know the four-wheel names plus the Scud's eight.
4. Glass is one mesh per model; cut into `glass_N` panes (welded components are already the panes: car 6, truck 5). Faces point outward only: a second (reversed) face or a two-sided material is needed to see a pane from inside; the car's glass is textured (atlas, alpha 0.56), the truck's flat.
5. Interiors exist for the car and the truck (floor, seats, wheel, dash); the shells are double-skinned there (no two-sided material needed). Bike and quad: nothing to build except a rider seat point.
6. Seat nodes and eyes: none exist; positions above are measured from seat components.
7. Lamps, tow hitch: none as nodes or materials; the truck needs a hitch.
8. Size changes for `vehicles.json`: car 2.05 x 1.62 x 3.85, truck 3.05 x 3.16 x 7.05 (seats 1.9 m up), bike 0.82 x 1.19 x 2.18, quad 1.19 x 1.32 x 2.28.
9. Colour variants are atlas swaps, layout-verified: car 5 body colours of 10 atlases, truck/bike 6, quad 4. Atlas files and materials are in the private pack (`glb/**`). The destroyed models are single merged meshes, so they are wrecks only.
10. The collision StaticBody3D children in each prefab GLB must be ignored or removed by the vehicle loader.

## Reproduce

```
scripts/build_vehicle_packs.sh          # unpack, import, export, variants, check (~12 s)
scripts/build_vehicle_packs.sh --standardize-only   # scripts/vehicles/*.json -> glb/vehicles-std/ + docs/research/vehicle-facts.d/ (car, bike, quad, truck)
python3 scripts/standardize_vehicle.py scripts/vehicles/car_small.json --vehicles-json godot/shooter/data/vehicles.json   # also splices the model-owned keys of the entry
godot --headless --script scripts/vehicle_model_probe.gd -- [--tris] <glb> ...
godot --headless --script scripts/vehicle_model_dump.gd -- <glb> <out.json>
python3 scripts/vehicle_model_analyze.py <out.json>
python3 scripts/vehicle_atlas_variants.py <out.json> <base atlas> <candidate atlas> ...
```
