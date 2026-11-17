# Vehicle design standard

Guiderail for anything that draws, attaches to, or measures a vehicle. Read before touching vehicle models, `vehicles.json`, `NetClient.Cars/Drive*/Glass/Cockpit`, `CarLights`, `GlassFx`, `CockpitRig`, `make_*_model.py`. Origin of the frames rule: #326 (glass shards and overlays stayed put while the Golf's body swayed). The contract and pipelines (parts A to C): issue "Unify all vehicle models" (#325).

Related: `docs/glass.md`, ADR 0007/0008 (glass), ADR 0010/0014 (prediction), ADR 0011/0012 (armed and exposed seats), `docs/vehicle-collision-and-entering.md`, `docs/vehicle-chase-camera.md`.

Layout: A contract (every vehicle) | B Synty import | C from scratch | Definition of done | Reference (sections 0 to 13: the detail behind the contract, node table, per-system rules).

Status marks: **[code]** checked by `VehicleContractTests` or already enforced; **[model]** a rule on the .glb, checked by the model script / the manual pass; **[NOT YET]** the contract says it, the code does not do it. Do not read a [NOT YET] item as current behaviour.

## A. The contract (Synty-derived or generated, same rules)

### A1. Frames and nodes

- Three frames: root, `hull`, parts (section 0). Everything of the body hangs under `hull`, never the root. **[model]**
- Origin on the ground at the centre, forward -Z, +Y up, metres. `model_scale` 1, `model_offset` [0,0,0], `model_yaw_deg` 0: a model is authored (or standardized) in the game frame; no fitting numbers in `vehicles.json`. **[code]** (`default_model_transform`)
- Required nodes (names are API, node table in section 1):

| Node | Rule |
|---|---|
| `hull` | body, interior, glass, lamps, seats, steering wheel; sways on springs |
| wheels | one node per wheel, centre at the axle, roll about X, `Position.Y` = radius; siblings of `hull` (they follow the ground, the hull leans) |
| `_steer` parents | one per steered wheel (`wheel_fl_steer` > `wheel_fl`); turn about Y; the roll stays on the wheel. A fork with a rake (a bike): the parent is oriented so its local Y is the tilted steering axis and the animator turns it about its local Y, never about world Y |
| `steer_bars` | handlebars (and fork) of an open vehicle: the steering control instead of a steering wheel. Bike: under `wheel_f_steer`, turns with the front wheel (so it follows the ground like a wheel and does not lean with the hull). Quad: under `hull`, turns about its own tilted column axis (local Y) |
| `glass_N` | one per declared pane, N = index in `vehicles.json` `glass`; under `hull` (a door-leaf pane under the leaf) |
| `seat_*` | one empty per seat at the rider's feet on the seat, under `hull` |
| `steering_wheel_rim` / `_spokes` (or a vehicle's own control) | hidden in the cockpit view, the rig draws a turning wheel |
| `light_head_*`, `light_tail_*` or the emissive materials | lamps (section 4) |

- Two wheels is legal: `wheel_f` (steered, `wheel_f_steer`) and `wheel_r`, each with its own radius (the bike's 0.350 and 0.321); `Position.Y` of each is its own radius, measured through the steer parent. More than four wheels is legal: names `wheel_l1..lN`, `wheel_r1..rN` (axle 1 at the front, the Scud's pattern); the animator takes the list from the model, not a fixed four. Steered axles get `_steer` parents. **[model]**; the generic animator (`VehicleWheels`, #295) takes the wheels from the layout table `WheelMath.Layouts` (`wheel_f/r`, `wheel_fl/fr/rl/rr`, `wheel_fl/fr/ml/mr/rl/rr`, `wheel_l1..lN/r1..rN`: one more layout is one more row). Predicted `drive` vehicles keep `DriveNodes` (four names, steer from the keys).
  - Six wheels (the truck): `wheel_l1`, `wheel_r1` (front axle, steered: `wheel_l1_steer`, `wheel_r1_steer`), `wheel_l2`, `wheel_r2` (middle), `wheel_l3`, `wheel_r3` (rear); l = -x, axle number from the front, the Scud's rule. The facts file names them (`wheels[].name`), the standardizer writes `wheel_<name>`; nothing else is special-cased. The prefab's own names (`_fl`, `_rl_01`, `_rl_02`, `_rr_03` the pack's slip) stay in the facts file only.
- Wheel stealing (Thieves) hides the nodes `wheel_fl/fr/rl/rr` (`WheelNodeNames`): a model with other names (the truck's `wheel_l1..r3`, the bike's `wheel_f/r`) ignores it. `loot.tire` in thieves.json holds the wheel positions of the car (`wheel_side_m`, `wheel_end_m` = the mean of its two axles).

### A2. Interior (minimum)

- Floor, dash, a seat per `seats` entry, a steering control (wheel, bars, stick). **[model]**
- Visible from every seat: from each seat's eye the cabin reads as a room (no see-through shell, no missing back faces). **[model]**
- A shell that is one skin (every Synty prefab) is drawn **two-sided** (`..._2s` material, section 1), or the inside of the walls shows through the cut windows.
- Open vehicles (quad, motorcycle, amphibious) need the control (`steer_bars`) and seats, not floor and dash. Their riders are `exposed` (ADR 0012): the seat has no `eye_height` (the crouch stance gives the eye) and no cabin check applies; the rider is seated with the hips on the saddle (feet = saddle top - 0.45 m, the crouch figure).

### A3. Seats

- 1..5 seats (`VehicleSeat.MaxSeats`), seat 0 the driver, `seats` block written (a trailer writes `[]`). **[code]** (`has_seats_block`, `seat_count`, `seats_inside_hit_box`)
- Each seat has an **eye** for the cockpit view (`SeatEye.For`, in the hull frame): the rider's feet + `eye_height` (or the standing eye 1.65 m); an exposed seat the rider's head (crouch 1.05 m seated, 1.65 m standing); the seven pre-#339 driver eyes and the Golf's passenger eyes are hand-set in `SeatEye.HandSetEyes`. The eye of a covered seat is inside the hit box, inside the cabin, above the seat, not in a pillar, and sees a pane (`HumveeTests` does the pane check for the Humvee). **[code]** eye inside the hit box (`covered_seat_eye_inside_hit_box`); sees-a-pane and in-cabin: **[model]**, Humvee only.
- Every seat has a switchable cockpit / chase view: the eye above, the view key, the mode `Rider.View` (section 5). A seat that works a gun or a screen (armed, `gunner`, the Scud operator) keeps its sight as its first-person view and ignores the key. **[code]** `SeatEye`, `RiderViewMode`, `SeatViewTests`; what the cockpit looks like from a seat is **[model]** (the interior, A2).
- Every seat declares its **cover** (the rider's protection), `"cover"` on the seat in `vehicles.json` (ADR 0035): **[code]** (`every_seat_has_a_cover`). `exposed_gunner` on the vehicle stands for its gunner seat's `exposed`; `"exposed": true` is the old spelling.

| `cover` | Rider | Hit by |
|---|---|---|
| `armoured` | hidden | explosive damage only |
| `glass` | protected by the panes in front of it (the shot's geometry says which) | a normal pane: bullet passes at once, full damage; a reinforced pane stops it until it shatters, then full damage |
| `exposed` | a target of its own (ADR 0012) | rounds that reach the body |

- The vehicle exploding kills everybody aboard, whatever the seat (`DriverDamage` to every occupant); a blast from outside hurts a covered rider (glass or armoured) at half, an exposed one in full (`SplashShare`). **[code]** (ADR 0035).
- A covered rider is hit as the server body's stance, not a volume from the seat pose: a hit volume from the seat pose for every seat is **[NOT YET]** (#301 slice 4), needed before cover reads exactly right.

### A4. Glass

- Panes are declared in `vehicles.json` `glass` (`centre`, `normal`, `size`, convex CCW `outline`, optional `hp`), max 24, inside the hit box, each with an outline and a `glass_N` node (section 2). **[code]** inside box, outline (`glass_inside_hit_box`, `glass_has_outline`); node: **[model]**
- Each pane has a **kind** (`"kind"`, default `normal`; hit points in `glass.json` `kinds`, a pane's own `hp` wins): **[code]** (`every_pane_has_a_known_kind`, ADR 0035).

| `kind` | Rule |
|---|---|
| `normal` | a bullet passes at once, full damage to what is behind (today's rule for every pane) |
| `reinforced` | the pane stops bullets until it shatters (more `hp`), then full damage |

- Explosions damage every pane in the radius, whatever the kind (unchanged, `docs/glass.md`).

### A5. Hit box fit

- `half_width`, `half_length`, `height` = server body = hit box = on-foot solid = run-over footprint (section 7). The model fits it (roof, mirrors, bumpers); panes, seats and eyes are inside it. **[code]** seats, panes, eyes; the model fit **[model]** (the model script prints the bounds; the Humvee squeezes its mirrors in).
- Changing the box (every model swap): re-check seats, lamps, glass, spawn clearance, `fixed_spots`, chase camera, tow hitch, Thieves.

### A6. Materials and colour variants

- Materials by name, matched in code: lamp materials (section 4), the Synty palette material (`Military_Green` and `_2s`), the paint material. Never rename one without the code.
- Generated model: `paint_material` + `paints` in `vehicles.json` (only that material is recoloured; `Variants` = `paints` count).
- Synty model: colour variants are an **atlas swap** (another palette texture), **never a tint** (a tint multiplies the whole atlas). **[code]** no `tint`/`paint_material`/`paints` on a `pack:` model (`synty_not_tinted`). The skin list is `skins` in `vehicles.json` (one standardized model per colour, the first is `model`); every client picks the same one from the colour index of the vehicle record (`VehicleDef.SkinModel(colour)`, the server deals the index: `VehicleColours.Pick`, `Variants` = the skins), no wire field. **[code]** (`StandardizedVehicleTests`). The car has five (yellow, blue, green, red, grey).
- Model source: `pack:<pack>/<name>` (private) or `drycastle-generated-models/<name>.glb`; no third-party loose models: no vehicle draws a Kenney or Zsky model any more (`assets/kenney-car-kit/` stays: map objects and the viewer smoke tests place its cars as props). `has_model`, `public_model_file_exists` **[code]**.

### A7. Wheels and drive

- Roll by the drawn speed, steer by the drawn steer (section 3). A type with a `drive` block is predicted and animated by `DriveNodes` (roll, steer from the keys, lean, water). Every other ground vehicle (quad, motorcycle, flak, bus, jeep/truck) is animated by the generic `VehicleWheels` from the DRAWN motion: no `drive` block needed (adding one switches the type to the predicted drive model, ADR 0010/0014). Tuning: #297 (amphibious), rear-axle pivot #295 slice 4.

### Where this stands (the allow-lists)

`VehicleContractTests` (`godot/ShooterCore.Tests/`) lists, per rule, the vehicles that fail today and why; the list only shrinks, an entry that starts passing fails the test until it is deleted. Goal: empty.

## B. Synty import pipeline

One standardization step per pack prefab: **`scripts/standardize_vehicle.py`**, driven by a committed facts file `scripts/vehicles/<id>.json`. One command rebuilds it: `scripts/build_vehicle_packs.sh` (its last step runs the standardizer for every `scripts/vehicles/*.json` and checks the output with `scripts/vehicle_std_check.gd`). The first vehicle through it is the car (`SM_Veh_Car_Small_01`, `scripts/vehicles/car_small.json`). The Humvee (`scripts/make_humvee_model.py`, `scripts/build_simple_military.sh`) predates it and is built by hand from the kit; the Scud and rocket cart use the same kit.

### B1. Steps

1. **Import the pack** once: `.unitypackage` / Godot zip > prefab GLBs under `assets-private/synty/glb/<pack>/` (`scripts/private_pack_glb.gd`, `quadbike_export.gd`, `build_vehicle_packs.sh`). Measure the model (`scripts/vehicle_model_probe.gd`, `vehicle_model_dump.gd` + `vehicle_model_analyze.py`) and write the facts into `docs/research/vehicle-models-facts.md`. Nothing of the pack is committed.
2. **Write the facts file** `scripts/vehicles/<id>.json`, positions in the SOURCE frame (as imported): the source node names (body, glass, steering wheel, each wheel and whether it steers), seat feet and eye heights, lamp positions and lens sizes, the skins (atlas names), `two_sided_shell`.
3. **Run the standardizer** (it does all of the following, no per-vehicle code):
   - **Reorient**: `facing` +Z (every Synty prefab) is turned half a circle about Y to forward -Z; **ground origin**: the lowest wheel point is y 0, the footprint centre is x 0 / z 0. `model_scale` 1, `model_offset` 0, `model_yaw_deg` 0 in `vehicles.json`.
   - **Strip** the prefab's collision bodies (`OMI_physics_*`): the vehicle's solid is the server's box.
   - **`hull`**: body (interior included), glass panes, steering wheel, seat and lamp empties, lamp lenses, extra empties. Wheels are **siblings** of `hull`.
   - **Wheels**: any number, named `wheel_<name>` (fl/fr/rl/rr for four, `l1..r3` for six, the Scud's pattern), each **centred on its tyre** (measured from the wheel mesh: the pack's pivots are off the tyre centre), `Position.Y` = radius; a steered wheel hangs under `wheel_<name>_steer`. Per-wheel radii are kept (a bike's two differ). Two and six wheels need no change here, only the animator (`DriveNodes` finds four). A wheel the pack lets float or sink by more than 2 mm is moved to stand on the ground (`Position.Y` = radius); `dx` in the facts file moves a wheel the pack put off its pair's x (the quad's front-left).
   - **Steering assembly**: a steered wheel with `axis` + `pivot` (source frame) gets a TILTED parent `wheel_<name>_steer` (the bike: the HandleBars node's local Y, raked -31.9 degrees); `controls` lists the meshes that turn with the steering and become `steer_bars`: `parent_wheel` puts them under that wheel's parent (bike: HandleBars + Front_Shock, the wheel turns under them), without it they hang under the hull and turn about their own `axis` + `pivot` (quad: the column, measured by the principal axis of the stem component of the handlebar mesh, 45 degrees back). `parts` joins more prefab meshes to the hull (racks, swing arm). Glass and steering wheel are optional (an open vehicle has neither).
   - **Glass**: the pack's glass is one mesh of welded pieces: each piece is a pane, a `glass_N` node (double-sided `Transparent blue glass`) with its centre, outward normal, size and convex outline (3..12 corners, hull of the real vertices). Ids: `windscreen`, `rear`, `side_left_N`, `side_right_N`. A concave piece must be cut by hand (the script asserts convexity).
   - **Steering wheel**: split by radius from its axis into `steering_wheel_rim` and `steering_wheel_spokes` (the names the cockpit rig hides).
   - **Seats and lamps**: `seat_*` empties at the rider's feet, `light_head_l/r`, `light_tail_l/r` empties plus thin emissive lens plates wearing the contract's lamp materials (`CarLights` then lights the model's own lens). The eye is checked against the roof lining of the body mesh (a ray up from each eye: it must stay 3 cm under it).
   - **Skins**: the same model written once per skin, `<id>_<skin>.glb`, only the atlas material name differs; `materials.json` of the output pack gets one atlas material per skin.
   - **Outputs**: GLBs + `materials.json` + manifest rows (`pack:synty-vehicles/<id>_<skin>`) in `assets-private/synty/glb/vehicles-std/` (private, shipped by `glb/**`), the measured numbers in **`docs/research/vehicle-facts.d/<id>.json`** (committed: bounds, suggested hit box, wheels, seats with eyes and the ceiling above them, lamps, panes with outlines, node list), and with `--vehicles-json godot/shooter/data/vehicles.json` the model-owned keys of the vehicle's entry (`model`, `skins`, `model_*`, `half_width/length`, `height`, `drive.wheelbase/track`, `seats`, `glass`, `lights`), spliced in place: **generated, never hand-copied**. `StandardizedVehicleTests` fails when the entry and the facts drift.
4. **Interior**: a prefab that has none (bike, quad: no floor or dash needed, open vehicles) gets only the control and seat points; a prefab with a modelled cabin (the small car: floor, seats, bench, dash, double-skinned shell) needs nothing. Building an interior the pack lacks is still per vehicle (the Humvee: `make_humvee_model.py`, from the kit's `box/beam/ring/cylinder/loft`).
5. **Gameplay numbers stay by hand** in the entry (speed, hp, mass, spawn, `drive` tuning, `hood`, `tow`, guns): only the keys above are overwritten.
6. **Check**: `scripts/vehicle_std_check.gd` (reloads the GLBs like the game: hull a direct child of the root, wheels its siblings with `Position.Y` = radius and the right `_steer` parents, panes/seats/lamps under the hull, no collision bodies, every material in `materials.json`, bounds = the facts), `VehicleContractTests`, `StandardizedVehicleTests`, the per-vehicle tests, then the manual pass (section 13).

### B2. Generic and per vehicle

| Generic (`scripts/standardize_vehicle.py`, `scripts/simple_military_kit.py`) | Per vehicle (`scripts/vehicles/<id>.json`) |
|---|---|
| prefab reader, collision strip, reorient, ground and footprint centre, `hull` regrouping, wheel naming/centring/`_steer` (also on a tilted axis), `steer_bars`, any wheel count and radii, glass cut into panes with outlines and the `glass` list, steering wheel split, seat and lamp empties and lenses, eye-under-ceiling check, skins as atlas swaps, `materials.json`, manifest, facts file, the `vehicles.json` splice, GLB writer, `outline_of`, `glass_faces` (Humvee) | source node names, which wheels steer, seat feet and eye heights, lamp points and lens sizes, skin atlases, `two_sided_shell`, the interior of a vehicle that has none (not done by the standardizer), a steering assembly (`axis`/`pivot`/`controls`/`parts`/`dx`), exposed seats, the gameplay numbers |

- `empties` in the facts file: extra named empties under the hull (the truck's `tow_hitch`: on the centre line, behind the footprint by at least 0.1 m so a trailer starts clear of the vehicle, at the rocket cart's `tow_eye` height 0.62; `tow.hitch` in `vehicles.json` is the same point by hand, `TruckModelTests` checks the rules). No hook mesh.
- Texture repeat: a prefab whose UVs leave 0..1 (the truck's bed, u to 2.64) needs the atlas to tile; the game's materials repeat by default (`StandardMaterial3D`), nothing to set.
- Not done by the standardizer yet (later #325 steps): the two-sided shell material for a one-skin shell (`two_sided_shell: true` writes the `_2s` materials, not exercised by the car), seat cover and pane kinds (#301), per-seat views (#339).

## C. A vehicle designed from scratch (`scripts/make_<type>_model.py`)

1. **Reference first**: real dimensions of the real vehicle (length, width, height, wheelbase, track, wheel radius, seat positions, roof height inside). Put them in the script header with the source. The hit box = the real footprint, not a guess.
2. **Contract**: all of A. The script writes `hull`, wheels with `_steer` parents (more than four allowed), `glass_N` panes with outlines, `seat_*` empties, the steering control, lamp materials/empties; origin on the ground at the centre, forward -Z, metres, scale 1.
3. **Pivots at axes**: wheels at the axle, rotors at the hub, doors at the hinge, a rack at its hinge, a gun at its pitch axis. **One mesh per moving part**.
4. **Materials by name**: lamp materials (section 4), `paint_material` (the body colour only) + `paints`; a two-sided material where a shell is one skin.
5. **Interior**: floor, dash, seats, steering control; check from every seat eye that you see the room, not the back of a face.
6. **Poly budget**: the generated vehicles are 3.3k (utility helicopter), 5.9k (amphibious, flak), 8.2k (bus), 9.7k (Golf, the most detailed) triangles, 155 to 710 KB. Budget: aim at 6k, 10k is the ceiling (a village has several on screen), no hidden detail inside a mesh nobody sees.
7. **`vehicles.json`**: size from the model, `glass` written by the script when run without an output path (do not hand-edit it), `seats` with `eye_height`, `lights`, `drive`, paints. **The .glb and `vehicles.json` in one commit.**
8. A model test next to the others if the vehicle has its own rules (`GolfMk2Tests`, `AmphibiousModelTests`).
9. Update `docs/glass.md` pane table and this file's node table.

## Definition of done (both paths)

- [ ] `VehicleContractTests` passes and the vehicle is **not** on any allow-list.
- [ ] Contract A met: nodes and frames, interior, seats (eye, cover, views once they exist), panes (kind once it exists), box fit, `model_scale` 1 / `model_offset` 0 / `model_yaw_deg` 0, material names, colour variants.
- [ ] .glb (public, or private under `assets-private/synty/glb/<pack>/` with a manifest row) and `vehicles.json` in one commit; the facts file / the script committed; `make release-check` covers a private model (it fails when a `pack:` model or skin that `vehicles.json` names has no manifest row, file or entry in the pack zip).
- [ ] Per-vehicle test and `VehicleTypesTests`, `GlassTests`, `HeadlightsTests` green; `docs/glass.md` and the node table updated.
- [ ] Manual pass on the full village map (section 13, item 6).

## Reference (sections 0 to 13)

## 0. The rule that caused #326: which node moves?

A vehicle has **three frames**. Pick the right one for every attachment.

| Frame | Node | Moves with | Does NOT move with |
|---|---|---|---|
| Root | `CarView.Node` | position + yaw (+ flight pitch/roll, + drive slope) | body lean on springs |
| Hull | child `hull` (driven vehicles: Golf, amphibious) | root + `BodyPitch`/`BodyRoll` sway (`DriveNodes.Step`) | wheels (they are siblings, see 3) |
| Part | `wheel_*`, `propeller`, rotors, turret, door leaves | hull + own animation | |

- **Anything that is part of the body hangs under the hull, never the root**: glass overlays and shards, lamps and beams, damage smoke/fire, bullet holes, riders and their figures, cockpit eye and rig, gun mounts, decals, plate/paint additions.
- If a model has no `hull` node, root = hull. Code must still work (`FindChild("hull") ?? root`).
- Offsets are authored in the **vehicle frame** (x right, y up, -Z forward, origin on the ground at the centre). Converting to hull space = inverse of the hull's *rest* transform (`CarLights.HostFrame`, `NetClient.GlassHost`). Rest = rotation zeroed. Never read the hull's live rotation for this.
- World-space is only right for things that must be **left behind**: skid marks, dropped debris, an explosion, a wreck's smoke after it is destroyed.
- `Node3D.GlobalTransform` of the root ignores the lean. For anything seen from inside (cockpit eye, rider heads, gun sights) use the hull's `GlobalTransform`.
- Test every new attachment by: Golf at speed, hard turn, brake dive. If it does not sway with the body it is under the wrong node.

### Known gaps (still on the root; fix when touched)
- (Fixed with the Humvee: the cockpit rig and eye now hang under the hull's rest frame, `GlassHost`; the sway is in the view.)
- Bullet holes: `BulletHoles.PlaceOn(Owned, car.Node, ...)`.
- Damage smoke: `VehicleFx.Damage.Make(v.Node, ...)`.
- Door gun / flak use `gunRot = rot + BodyPitch/BodyRoll` by hand: keep that in step with the hull.

## 1. Model (.glb) contract

Generated by `scripts/make_<type>_model.py`; the .glb in `assets/drycastle-generated-models/` and `vehicles.json` ship in **one commit**. Do not hand-edit the generated `glass` list. The rules are part A; this section is the node table and details.

- A model that carries geometry of a paid pack (Humvee, Scud, rocket cart: `scripts/build_simple_military.sh`) is private: the .glb lives in `assets-private/synty/glb/<pack>/` and `vehicles.json` names it `pack:<pack>/<name>`; only the script and `docs/research/scud-models-facts.md` are committed. Its glass list is written by hand from the facts file (part B).
- Origin on the ground at the centre, forward -Z, +Y up, metres. `model_scale` 1, `model_yaw_deg` 0, `model_offset` 0 are the contract (A1). The only non-default value left is the harvester's yaw, on the allow-list of `VehicleContractTests`.
- Node names are API. Code finds them with `FindChild(name, true, false)`; a missing node silently disables the feature. Names in use:

| Name | Used by | Notes |
|---|---|---|
| `hull` | lean, lamps, riders | body group; everything that sways goes under it |
| `wheel_fl/fr/rl/rr` | roll animation (`DriveNodes` for drive types, `VehicleWheels` for the rest) | centre at wheel axle; `rear.Position.Y` = radius |
| `wheel_fl_steer`, `wheel_fr_steer` | steering angle (Y rot) | parent of the rolling wheel node; wheel keeps its own X roll |
| `wheel_f`, `wheel_r`, `wheel_f_steer`, `steer_bars` | the bike (two wheels, radii 0.350 / 0.321) | `wheel_f_steer` sits on the steering head, oriented so its local Y is the raked axis (0, 0.849, 0.528); `wheel_f` and `steer_bars` (HandleBars + Front_Shock) are its children; turn it about its local Y. `wheel_r` is a sibling of `hull`. Animated by `VehicleWheels` (#295) |
| `steer_bars` (quad) | the quad's handlebars (SM_Veh_QuadBike_HandleBar_01) | under `hull`, oriented so its local Y is the column axis (0, 0.709, 0.706); turn about local Y. The front wheels use `wheel_fl_steer` / `wheel_fr_steer` (Y). Animated by `VehicleWheels` (#295) |
| `wheel_fl/fr/rl/rr` | Thieves taking wheels (`WheelNodeNames`, the bit order fl, fr, rl, rr) | the contract's four wheel names; toggles `Visible` of the wheel (under its `_steer` parent when it has one) |
| `glass_N` | shatter hides it | N = index in `vehicles.json` `glass`; door-leaf pane is a child of the leaf |
| `steering_wheel_rim`, `steering_wheel_spokes`, `cab`, `fuselage` | hidden in the cockpit view (`CockpitRig.Spec.Hide`) | hide = invisible but still casts shadow |
| `propeller`, `wake_bow`, `wake_stern_l/r` | amphibious FX | |
| `rotor_main`, `rotor_tail` | helicopter rotors (`RotorFx`) | |
| `SM_Veh_Harvester_Reel` | harvester reel (`NetClient.Harvester`: spins on X while driven) | Synty prefab node; the model is `pack:synty-farm/...`, a box without the pack |
| `SM_Veh_Harvester_Wheel_fl/fr/rl/rr` | harvester wheels (`NetClient.Harvester`: roll with ground speed, front pair steers; radii 0.733 / 0.554 m) | no hull: parts hang under the model root |
| `SM_Veh_Harvester_SteeringW`, `SM_Veh_Harvester_Glass` | hidden in the cockpit view (`CockpitRig.Spec.Hide`) | rig draws a turning wheel and hands |
| `turret`, `gun_mount`, `gunner_sight`, `seat_*` | guns and seats | see 6. The Humvee's roof ring gun is the amphibious transport's pattern (`gun_mount` > `gun` > `barrel` > `muzzle`, `gunner_sight` under `gun`, all under the hull; `door_gun.pivot` = ring centre in x/z and the gun's height in y, `ring_radius` = the gun ahead of the ring centre, `eye` = `gunner_sight` - gun pivot); `seat_passenger_1..3`, `seat_gunner` for the other four seats |
| `wheel_l1..l4`, `wheel_r1..r4`, `wheel_l1_steer`, `wheel_r1_steer`, `wheel_l2_steer`, `wheel_r2_steer` | the Scud's 8 wheels (l = -x, the number counts the axles from the front) | same rule as the four-wheel names: centre at the axle, roll about X; axles 1 and 2 steer through the `_steer` parent; siblings of `hull`. `scripts/make_scud_model.py` |
| `wheel_l`, `wheel_r` | trailer wheels (`NetClient.Tow`: roll with the drawn speed about the parent's X; radius = the node's `Position.Y`, 0.45 m without) | rocket cart: no hull, no steering, parts under the model root; `scripts/make_rocket_cart_model.py` |
| `rack`, `missile` | the Scud's missile rack and the missile on it (also the rocket cart's `missile`) | `rack` pivot on the rear hinge axis, rotate about X: 0 lying, +90 deg vertical; `missile` under it (on the cart: under `hull`) with its origin at the missile's centre of mass, nose -Z; hidden when launched / when the cart is empty. Both under the hull |
| `tow_hitch`, `tow_hook`, `tow_eye`, `axle` | towing: the Humvee's pintle point and hook mesh, the cart's drawbar eye and axle centre | empties under the hull; a tower's `tow_hitch` meets the towed vehicle's `tow_eye` (heights equal) |
| `car_small_<skin>` node tree | the car (`scripts/standardize_vehicle.py`, `scripts/vehicles/car_small.json`, private) | `hull` (body with its interior, `glass_0..5`, `steering_wheel_rim`/`_spokes`, `seat_driver`, `seat_passenger_1..3`, `light_head_l/r`, `light_tail_l/r` and their lens plates) with `wheel_fl_steer` > `wheel_fl`, `wheel_fr_steer` > `wheel_fr`, `wheel_rl`, `wheel_rr` as siblings (l = -x, centred on the tyre, `Position.Y` 0.314). One GLB per skin (`pack:synty-vehicles/car_small_yellow/blue/green/red/grey`), only the atlas material differs (`Land_Vehicles_03/01/02/04/09_mat`, `glb/vehicles-std/materials.json`) |
| `truck_<skin>` node tree | the `jeep` type (`scripts/standardize_vehicle.py`, `scripts/vehicles/truck.json`, private) | `hull` (cab, cargo bed, `glass_0..4` = windscreen, `rear_1`/`rear_2` (the cab-back panes), `side_left`/`side_right`, `steering_wheel_rim`/`_spokes`, `seat_driver`, `seat_passenger_1/2` (the bench), `light_head_l/r`, `light_tail_l/r` and lenses, `tow_hitch`) with `wheel_l1_steer` > `wheel_l1`, `wheel_r1_steer` > `wheel_r1`, `wheel_l2`, `wheel_r2`, `wheel_l3`, `wheel_r3` as siblings (the Scud's naming, l = -x, centred on the tyre, `Position.Y` 0.64). No gun, no gunner seat. Skins `truck_woodland/desert/khaki/black/sand/grey` (Battle Royale atlases 01..06). Not rolled or steered yet (#295) |
| `bike_<skin>`, `quad_<skin>` node trees | the motorcycle and the quad (`scripts/standardize_vehicle.py`, `scripts/vehicles/bike.json` / `quad.json`, private) | bike: `hull` (frame, swing arm, `seat_driver`, `light_head_c`, `light_tail_c` and their lenses) with `wheel_f_steer` (> `wheel_f`, `steer_bars`) and `wheel_r` as siblings; quad: `hull` (frame, racks, shovel, `steer_bars`, `seat_driver`, `light_head_l/r`, `light_tail_c`) with `wheel_fl_steer` > `wheel_fl`, `wheel_fr_steer` > `wheel_fr`, `wheel_rl`, `wheel_rr`. Skins: bike woodland, desert, khaki, black, sand, grey (Battle Royale atlases 01..06); quad red, blue, olive, sand (QuadBike atlases 01..04). One rider, `exposed` |
| `humvee` node tree | the Humvee (`scripts/make_humvee_model.py`, private) | `hull` (body, interior, `glass_0..5`, `steering_wheel_rim`/`_spokes`, the seats, `gun_mount` and the lamps, `tow_hitch`, `tow_hook`) with `wheel_fl_steer` > `wheel_fl`, `wheel_fr_steer` > `wheel_fr`, `wheel_rl`, `wheel_rr` as siblings; roll/steer/lean need the `drive` block (`DriveNodes` is only found for a type that has one) |
| `Military_Green`, `Military_Green_2s` | the Synty palette material of the private SIMPLE Military models (`pack:synty-simple-military/*`) | name-only in the GLB, built from `glb/simple-military/materials.json` (`_2s` = two-sided: the pack's shells are one skin, so the inside of the walls shows through the cut windows). Not for `paint_material` (a tint multiplies the whole atlas) |
| `rack` | Scud truck (`NetClient.Scud`: `ScudRules.RackAngle`) | pivot on the rear hinge, lying along -Z from it; rotating about X from 0 to 90 degrees raises it. Hangs under `hull`. A model without it is not animated |
| `missile` | Scud truck (`NetClient.Scud`) | the rocket on the rack, child of `rack`, nose along -Z; shown until the launch and in the second half of a reload (`ScudRules.RocketOnRack`) |
| `wheel_l1..l4`, `wheel_r1..r4` (Scud) | `NetClient.Scud`: all eight roll by the distance driven (`launcher.wheel_radius` 0.736), about the truck's own X over the wheel's rest pose | siblings of `hull`; `wheel_*_steer` are not rolled. Skipped when absent |
| `wheel_l1_steer`, `wheel_r1_steer`, `wheel_l2_steer`, `wheel_r2_steer` | `NetClient.Scud`: the front two axles turn about Y by `ScudRules.WheelAngle(steer_arms[axle], curvature)` (curvature = the drawn yaw over the drawn distance: the truck is not predicted) | the front axle turns more than the second |

- Materials by name, matched in code: `Emissive warm headlight lens`, `Emissive red tail lamps`, reverse lamp, indicator (`CarLights`). Renaming one removes the lamp. The vehicle gets its **own copy** of each (a shared resource lights every car of the type).
- `paint_material` + `paints` in `vehicles.json`: only that material is recoloured.
- One mesh per moving part. A part that must rotate needs its **pivot at its axis**, not at the model origin.

## 2. Glass (`docs/glass.md`, ADR 0007/0008)

- Pane = `centre`, `normal`, `size`, convex CCW `outline` (3-12 pts) in vehicle frame. Max 24 per vehicle.
- Overlay (crack, hole) and shatter fragments are under `GlassHost` = hull in rest frame. Shards fly relative to the vehicle, never world.
- `glass_N` node hidden while shattered; model with its own node leaves a real opening (hole_darkness 0), solid-glasshouse models (flak) and Kenney cars keep a dark fill.
- Panes and `open_sides` are tested server-side in the yaw-only hit-box frame; keep panes inside the hit box.
- Concave pane = several panes. Door leaf with an open door: overlay stays at the closed spot (known).
- Whole pane = no state; do not store it.

## 3. Wheels

- Predicted `drive` vehicles (`DriveNodes`): four named nodes. Roll: `-speed / radius * dt` about X; afloat 30 % of the speed. Front pair steer through the `_steer` parent: `atan(wheelbase / turn radius)` capped by `WheelLockDeg`, from the driver's steer input.
- Every other ground vehicle (`VehicleWheels`, `WheelMath`): wheels found by `WheelMath.Layouts`. Speed along the heading and yaw rate come from how the vehicle is drawn (eased, a jump of over 5 m is not driving); roll = `-speed / radius * dt` about the wheel's parent X over its rest pose, radius = the wheel's rest height in the root frame (through its steer parent). Each `_steer` parent turns about its LOCAL Y by `atan(arm * curvature)`, curvature = yaw rate / speed, arm = that axle's distance ahead of the mean of the axles that do not steer, capped at 0.6 rad. `steer_bars` outside a steer parent turn about their local Y with the foremost steer angle; inside one they ride along. Not used for the Scud, the harvester and the trailer (own code below).
- Wheels are driven by the **drawn** state (`DriveState` as drawn), not the last snapshot.
- Wheels follow the root's slope; the hull leans over them. If hull and wheels are in one group, the wheels will tilt with the body: keep them as siblings of `hull` when the body leans.
- Thieves: `WheelsOff` mask hides wheel nodes; a model without them ignores it. Collider/physics unchanged.

## 4. Lights

- `lights` in `vehicles.json`: `head`, `tail` (vehicle-frame offsets), `BeamRange`, `BeamAngle`. Spots/lenses are added under the hull (`CarLights.Attach`).
- If the model has the emissive materials above, no extra lens meshes are made (`_modelHead` / `_modelTail`); the spots still are.
- States: headlights (record bit), brake (a fall in drawn speed, there is no brake bit), reverse (drawn signed speed < -0.3), indicators stay off.
- Never more than a few `SpotLight3D` per vehicle, `ShadowEnabled = false`. Beams only for vehicles in `beams` (nearest set).
- Destroyed vehicle: lamps off.

## 5. Cockpit view (`SeatEye`, `RiderViewMode`, `CockpitRig`)

- **Every seat** has a chase and a cockpit view (#339). Eye: `SeatEye.For(def, seat)` in the hull frame, from the seat's `offset` + `eye_height` (covered), the head's height (exposed: motorcycle, quad, amphibious, harvester), or a hand-set eye in `SeatEye.HandSetEyes` (the driver of golf_mk2, humvee, bus, flak, harvester, scud, utility_helicopter: as the rigs had them, the Humvee's view is unchanged; the Golf's passengers). `VehicleContractTests` uses the same function. A new vehicle needs no code: `eye_height` on covered seats whose roof is under the standing eye.
- **Mode** `Rider.View` (`RiderViewMode`, `docs/state-machines.md`): Chase, Cockpit, Sight. One key (`vehicle_view`) flips Chase and Cockpit in every seat; the choice is kept for the session. Entering: the last choice; none yet = driver Chase, passenger Cockpit; a seat in the open starts in Chase. Seat switch: the mode stays; into a sight seat Sight, back out the choice. Leaving keeps the choice.
- **Sight** seats (armed seat, `gunner`, the Scud operator): the gun's or the screen's own first-person view (`RideSeat`, flak and door gun cameras); the key is ignored there.
- A **passenger's** cockpit: the camera at the seat's eye on the hull, head free look (all the way round, pitch -0.9..0.7), the own figure not drawn for the viewer but its shadow stays (`ShowSelfInCar(viewerHides)`), no driver rig: the model's own interior is what is seen.
- **Driver's rig** (`CockpitRig.Specs[vehicleId]`: hide list + builder, no eye any more): only on seat 0 and only for golf_mk2, humvee, bus, flak, utility_helicopter, harvester and scud. Another driver's cockpit is the plain eye in the model.
- Rig and camera sit in the vehicle frame; the camera sees through the hidden parts, so check the eye is inside the cabin and above the seat, not inside a pillar.
- The own figure is hidden for the viewer (shadow kept); the rig draws the driver's hands. Steering wheel turns with keys (`WheelLock` 1.3 rad), helicopter cyclic/collective follow the keys. Reduce motion keeps the view level.
- Eye, steering wheel, mirrors move with the **hull**: `CockpitFrame` hangs the rig under `GlassHost` and takes the eye from it. A cockpit eye that ignores sway reads as the car wobbling around you.
- The Humvee: eye [-0.62, 1.65, -0.13] (seat + [0, 1.05, 0.09], the Golf's rule), hide `steering_wheel_rim`/`steering_wheel_spokes`, the rig draws a turning wheel (centre [-0.62, 1.10, -0.60], 25 degrees from upright, lean 65) and the hands.
- The Scud truck: eye [-0.892, 2.2, -4.238] (seat_driver + [0, 1.05, 0.09]) in the left half cab (roof inside 2.66, windscreen 1.99-2.42), hide `steering_wheel_rim`/`steering_wheel_spokes`, the rig draws a turning wheel (centre [-0.892, 1.88, -4.962], lean 70) and the hands. The operator's seat (+0.892) has the same height and no rig: its eye is the seat's (`eye_height` 1.05 on both seats, the standing eye would be over the roof).
- A new cockpit rig: its eye (if the generic one is not right) goes in `SeatEye.HandSetEyes` next to the model script's numbers, one comment pointing at the script.

## 6. Seats, riders, guns

- `seats` in `vehicles.json`; seat 0 = driver, 1..4 = others, five seats at most (`VehicleSeat.MaxSeats`; the seat switch keys 1..5). Riders are drawn on the seat anchor = **hull** (`SeatAnchor`). Seated players are placed by the client from `seats` records, not by physics.
- A seat may leave its rider uncovered by the hull (ADR 0012); declare it, do not rely on the box.
- A rider in a seat the hull covers looks from their feet + the standing eye (1.65 m) unless the seat sets `eye_height`: a low roof (the Humvee, roof 1.86 over seats at 0.6) needs `eye_height` 1.05 (the crouch's) or the eye is outside the body. The eye must be inside the cabin and see a window (`HumveeTests`).
- Armed seat fights with the rider's own weapons (ADR 0011). Door gun / flak / roof gun nodes: `FlakNodes.Find`, `DoorGunNodes.Find` by name; their rotation is in the **hull** frame.
- Driver/pilot's corpse stays seated; others fall out (ADR 0026).
- Scud truck: seat 1 (`launcher.operator_seat`) is the operator's, seat 0 the driver's, both under the cab (covered). The operator has the targeting screen, the others a read-only feed of it; the rack, the rocket and the launch smoke hang under the `hull` (`docs/state-machines.md` Scud.Launcher). Its hit box (3.13 x 11.9 m, 3.54 m high, the lying missile's top) stays the same while the rack is up (the rack and the fins, 4.0 m, are not a target). `launcher.muzzle` is the erected missile's centre (where the missile entity spawns), `exhaust` its tail end; `wheel_radius` and `steer_arms` drive the wheels.
- Entering: nearest free seat within `enter_radius` of the footprint (docs/vehicle-collision-and-entering.md). Fixed spots (`fixed_spots`: the Golf in the mechanic garage, the utility helicopter on the heliport) are placed before the random deal, which keeps 12 m clear; the Golf's is behind the garage plate, the helicopter's is in the open and its `yaw_deg` is the hangars' `rotation_deg` + 180 (nose out; object and vehicle yaw are the same Godot Y turn).

## 7. Hit volume, collision, physics

- `half_width`, `half_length`, `height` = server body, bullet hit box, on-foot solid, run-over footprint. All the same numbers; model must fit them.
- `hull` list (helicopter) = boxes tilted by pitch/roll, ADR 0028; ground types are one yaw-only box.
- Vehicle solid on layer `VehicleSolid` (8). Do not give a vehicle body a collision exception with players for the on-foot solid (it works both ways).
- Hit points, mass, `run_over_mass`, crash tuning only in `vehicles.json`; no per-type constants in code.

## 8. Prediction and drawing

- Ground cars and the amphibious/Golf: the driver's client predicts (ADR 0010/0014); others interpolate from `Buf.At(rt)`. All visuals use the **drawn** pose, never the raw snapshot.
- Visual-only state (lean, wheel roll, wake, lamps) is client-owned and must not appear on the wire (`DriveFx`).
- Flight: pitch/roll from the flight record; `PoseFlight`.
- A destroyed vehicle: `Node.Visible = false`, FX stop, `continue` in the render loop. A respawn resets visibility and glass (new round / repair makes panes whole).
- Headless clients (`ShotFx.Enabled == false`) must not create any node-dependent effect; guard with `ShotFx.Enabled`.

## 9. Sound

- Engine (`VehicleEngineSound`) and per-type `sounds` block (Golf). Pitch from drawn speed, not snapshot.
- Position the emitter on the **root** (it is a point in the world), not the hull.
- No sound loops for far vehicles (`Sfx.ListenerDistance`), glass has no sound yet.
- **The listener is the camera** (there is no `AudioListener3D`, `Sfx.cs`). The chase camera sits metres behind and above the vehicle and orbits, so a sound at a point of the vehicle you ride gets louder, quieter and panned with the camera (#352). Every sound of the vehicle the local player rides, in any seat (engine, horn, hits, event sounds on it: the harvester's reel, chew, pop), goes through the one path `NetClient.RidesCar(CarView)`: at `Sfx.ListenerPosition` with `exact: true`, no distance cull. One-shots: `PlayCarSound`; sounds longer than about 0.5 s: `PlayCarVoice` (kept on the vehicle by `StepCarVoices`). Never `Sfx.Play3D(id, point on the vehicle)` for it.
- Other vehicles stay positional (`Sfx.Play3D`, range from sounds.json); a sound longer than about 0.5 s follows the moving vehicle (`PlayCarVoice`), not a fixed world point.

## 10. Damage and destruction

- Damage stages (`damage_stages`): smoke/fire attach to the hull (see Known gaps), near-only (`Near(<90 m)`), particle budget respected, reduce motion respected.
- Destroyed: explosion, blast damages panes in radius, wreck return timer `destroyed_return_time`.
- Crash/bump rules in `vehicles.json`: server-only resolution.

## 11. Chase camera

- Distance/height derived from `half_length` unless `chase_distance` / `chase_height` are set; helicopters use their `camera` block. Camera uses root yaw and slope, **not** the lean (it would shake). Ray vs world mask only. Every seat that is not a gun's sight has it (#339): a passenger in chase sees their own figure on their seat.

## 12. Settings and perf

- `ReduceMotion`: no sway-driven camera, no wake/spray, glass shards still fine.
- No per-frame allocation in the vehicle loop (`Dictionary` lookups, `new` nodes only on first draw / state change).
- Cap shatter effects (`MaxShatterEffects`), range-limit effects (`ShatterEffectRange`, `FxRange` 160 m).

## 12b. Trailers (the rocket cart; `docs/shooter-v2.md` "Towing and the rocket cart")

- A trailer type has `"seats": []` and a `trailer` block; a zero-seat type is legal only with it. Code that reads seats (`Seats[0]`, `Seat.Occupant(0)`, the driver, the schematic, entering) must cope with none: `VehicleSeat(0)` is valid and its driver is 0.
- Frames: the origin is on the ground at the centre of the **whole** footprint (drawbar included, `half_length` covers it), forward -Z, the drawbar to -Z. `trailer.eye` is the drawbar's tip (on the centre line, z negative), `trailer.axle` the wheels' axle (centre line, the model's wheel centres sit at its `y`/`z`, `wheel.Position.Y` = radius). The model has no `hull`: wheels and body are children of the root, the whole trailer pitches and rolls with the ground (`NetClient.Tow`).
- The client draws a hitched trailer from the **drawn tractor** (its node transform at the tractor's `tow.hitch`) and the articulation, never from the trailer's own raw record; any new attachment to the trailer hangs under its root. Loose, it is drawn at its record.
- Sound: the couple/uncouple clunk is `tow.clunk` through `PlayCarSound` with the **tractor** as the vehicle (heard from the cab as from outside); a trailer has no engine.
- Checklist for a new trailer type: `trailer` block numbers fit the model (eye, axle, drawbar at least 0.5 m), `half_length`/`half_width`/`height` the box, `loaded_hp` only for a type with a load, nodes above, `spawn_count` 0 unless it is dealt, `RocketCartTests` and `ScudModelTests` show what a shipped one must satisfy. The rocket cart: 12.6 m long (it carries the whole 11.1 m missile), drawbar 7.6 m, `trailer.eye` [0, 0.62, -6.2] (the Humvee's hitch height; the jeep's 0.5 and the flak's 0.7 pitch the drawn cart by under 1 degree), `trailer.mass` the loaded cart's, `trailer.empty_mass` the empty one's (`TowLoad.Of(..., loaded)`), `returns` false (the Scud event replaces it). A trailer as long as this samples the ground under its middle too (`TrailerProbe`: a crest between the ends lifts the swept box).

## 13. Checklist: new or changed vehicle

See also the definition of done above and part B/C.

1. `scripts/make_<type>_model.py` produces: `hull`, wheel nodes, `glass_N`, seat nodes, lamp materials. Run it; commit .glb + `vehicles.json` together.
2. `vehicles.json`: size, hp, mass, glass, lights, seats, paint, sounds, spots. `VehicleTypesTests` stays green.
3. Every attachment under the right frame (section 0).
4. Cockpit spec (if any), hide list, eye inside cabin.
5. Tests: `VehicleTypesTests`, `GlassTests`, `HeadlightsTests`, per-vehicle (`GolfMk2Tests`).
6. Manual pass on the full village map: drive, turn hard, brake, shoot a pane, look from cockpit, take a wheel, damage to destruction.
7. Update `docs/glass.md` pane table and this file's node table.
8. Every new sound of a vehicle (engine layer, horn, gun, hit, event) is played through `RidesCar` / `PlayCarSound` / `PlayCarVoice`, so it sounds the same from the cockpit and the chase camera (section 9).
