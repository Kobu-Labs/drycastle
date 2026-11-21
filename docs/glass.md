# Glass panes

Glass panes on vehicles and buildings have hit points, crack as they lose them and shatter at zero. The decisions are in ADR 0035 (pane kinds and seat cover), ADR 0007 (`docs/adr/0007-glass-panes-are-declared-rectangles-with-server-owned-hit-points.md`) and, for the generated vehicles, ADR 0008 (`docs/adr/0008-generated-vehicle-glass-is-a-node-per-pane-with-an-outline.md`); the terms are in `GLOSSARY.md` (Glass pane, Pane kind, Seat cover, Crack stage, Shattered).

## What exists

| Glass | Source | Panes |
|---|---|---|
| Buildings | `windows.json` of the map (window openings of buildings with a detail file, `pipeline/config/buildings/`) | every pane whose `breakable` is not false (the village map lists 35 panes today) |
| Mechanic garage plate | `MechanicGarageSite.AddPane` (not in `windows.json`: added to the map's pane list on the server and every client, sorted by node name `mechanic_garage_plate`) | one OSB plate, 240 hit points (`MapGlassPane.Hp`), black cracks, no rim of shards left in the opening and OSB-chip fragments (`GlassFx.OsbShards`, `glass_crack.gdshader` `crack_colour`/`edge_shards`), its box and mesh go when it shatters (`MechanicGarage.SetPlate`), ADR 0029 |
| Car | `vehicles.json` `glass`, written by `scripts/standardize_vehicle.py` from the private model (numbers: `docs/research/vehicle-facts.d/car_small.json`; the model has the `glass_N` nodes) | windscreen, rear, a front and a rear side window on each side (6) |
| Truck (cargo truck, once `jeep`) | `vehicles.json` `glass`, written by `scripts/standardize_vehicle.py` from the private model (`docs/research/vehicle-facts.d/truck.json`; `glass_N` nodes) | one wide windscreen, a side window on each side, two cab-back windows looking into the bed (5) |
| Humvee | `vehicles.json` `glass` (written by hand from `docs/research/scud-models-facts.md`; the private model has the `glass_N` nodes) | two windscreen halves, a front and a rear door window on each side (6) |
| Scud truck | `vehicles.json` `glass` (written by hand from `docs/research/scud-models-facts.md` with the glass nodes' 0.37 m offset, see below; the private model has the `glass_N` nodes) | two windscreen halves, a front and a rear door window on each side, a corner window on each side (8; the two small windows on the inner walls beside the missile are painted, not panes) |
| Bus | `vehicles.json` `glass` | windscreen and its two wrapped corners, rear, six left windows, four right windows, four door leaves (18) |
| Flak truck | `vehicles.json` `glass` | two windscreen halves, a door window and a quarter light on each side (6) |
| Utility helicopter | `vehicles.json` `glass` | two windscreen halves, two cockpit doors |
| Golf Mk2 | `vehicles.json` `glass` | windscreen, rear, and a door window and a quarter light on each side (6) |

The motorcycle, quad and amphibious transport have no glass. The utility helicopter also has two open sides (its cabin has no doors; `open_sides` in `vehicles.json`, written by hand next to the generated `glass` list, which the model script leaves alone).

**Generated models (bus, flak truck, helicopter).** The model script builds every pane as its own node `glass_N` (N = the pane's place in the vehicle's `glass` list) and, when run without an output path, rewrites that list in `vehicles.json`: `centre`, `normal`, `size` (the bounding rectangle) and `outline` (a convex counter-clockwise polygon `[u, v]` in metres from the centre, so a trapezoid or a triangle is its real shape). Do not edit those lists by hand; change the script and run it (`python3 -I scripts/make_bus_model.py`, and the same for `make_flak_model.py` and `make_utility_helicopter_model.py`; the Golf's `python3 scripts/make_golf_mk2_model.py` needs numpy, so no `-I`). The client hides the `glass_N` node while the pane is shattered. Shots, the crack overlay and the fragments use the outline.

**Scud truck.** Same as the Humvee (`glass_0..7`, a list copied from the facts file, windows are real openings), with one correction: the Scud model puts every `glass_N` node 0.37 m towards +z (checked against the GLB), and the facts file gives the panes' centres without it, so `vehicles.json` has the facts' z plus 0.37 (`ScudModelTests` pins it). The cab is covered: both seats are behind the hull.

**Humvee.** Its model (`scripts/make_humvee_model.py`, private pack) builds every pane as a `glass_N` node like the generated ones, but its list is not rewritten by the script: it is copied from the facts file (`scripts/make_scud_models_facts.py`) and `HumveeTests` checks it against `docs/research/scud-models-facts.d/humvee.json`. The windows are real openings, nothing stands behind a shattered pane. The roof gunner is in the open (an exposed seat): rounds that reach him hit him, the passengers stay behind the hull.

**Small car.** `SM_Veh_Car_Small_01` standardized by `scripts/standardize_vehicle.py`: the pack's one glass mesh is cut into its six welded pieces, each a `glass_N` node (double-sided glass material) with the convex outline of the real pane (raked windscreen, bent side panes). The script writes the `glass` list into `vehicles.json`; `StandardizedVehicleTests` pins it to `docs/research/vehicle-facts.d/car_small.json`. The cabin is a real opening behind a shattered pane (double-skinned shell, seats and dash modelled).

**Cargo truck (`truck`).** `SM_Veh_Truck_01` standardized like the small car: the glass mesh (one, 16 triangles, flat translucent teal, offset -0.025 m in y in the prefab) is cut into its five welded pieces, each a `glass_N` node with its convex outline: `windscreen` (2.27 x 0.63, one bowed pane), `rear_1`/`rear_2` (the two cab-back windows, 0.74 x 0.33, facing the bed), `side_left`/`side_right`. The cab is a double-skinned closed shell with a modelled bench, dash and wheel, so a shattered pane is a real opening onto it.

## Rules

- Server owns the hit points (`GlassPanes`). A whole pane is not stored.
- A bullet, pellet, roof gun or door gun round that hit no player damages the pane it crossed where it stopped, by its damage at that range.
- A rpg rocket, an attack missile and a vehicle explosion damage every pane in the blast radius (`blast_damage` at the centre, nothing at the edge).
- Crack stage from the fraction of hit points left: 1 when damaged, 2 at 66 %, 3 at 33 % (`crack_stages`).
- At zero the pane is shattered. The fragments fly once, on the clients that see it happen.
- A bullet, pellet, roof gun or door gun round goes through a vehicle's glass by the pane's **kind** (`BulletHit`, `PaneRules.Pass`, the geometry in `GlassRules.Through`, ADR 0035): it damages the pane it enters by, goes on inside and leaves by the next pane it crosses from inside, then on to whatever is beyond, through further vehicles the same way. A `normal` pane (the default) lets it through at once, whole or shattered. A `reinforced` pane (`kinds` in `glass.json`, more hit points) stops it while it holds: the pane takes the damage and nothing behind it is reached; shattered, it is a hole. A reinforced pane met from inside stops it there, after the riders on the way. With no way out the hull stops a round on its far side (where the ray leaves the hit box, or where its range ends). A building's wall stays solid. This replaces ADR 0007 decision 3; `shattered_exposes_riders` in `glass.json` is no longer read.
- Who inside is reached is the **seat's cover** (`cover` in `vehicles.json`, ADR 0035): `glass` riders are open to a round that got in, `armoured` riders are never a target of a bullet (`RiderRules.BulletsReach`; only explosive damage and the vehicle exploding reach them), `exposed` riders are ADR 0012's. Which pane guards which seat is the geometry of the shot, not a list.
- An open side (`open_sides` in `vehicles.json`, written like a pane: centre, outward normal, size) is a place where the body has no wall: the two sides of the utility helicopter's cabin. A round gets in and out by an open side exactly as by a pane, only nothing is damaged there. So a shot into the helicopter's cabin can hit the gunner, leave by a cockpit door's pane or the windscreen from the inside (damaging it) or by the other open side, and go on.
- A round only gets into a vehicle by a pane or an open side. Where it meets the hit box anywhere else (a door, the roof, the helicopter's belly, engine housing, cabin rear wall, nose and tail) the hull stops it there, also when a pane lies further along the ray: a pane on the far side is no way out for a round that had no way in.
- The client's own tracer, pellet sparks and bullet holes follow the same rule (`VehicleAlong` in `NetClient.Cars.cs`): nothing is drawn on a vehicle the round went through, and a round stopped inside is drawn on the far side.
- The roof gunner's own car stops his rounds as a hull (its glass is not passed or damaged); the door gunner's own helicopter is left out of his ray altogether.
- A shattered pane of a model with its own `glass_N` node leaves a real opening (no dark fill), the flak truck's included (its cab is hollow since #325).
- A new round and a repaired vehicle make panes whole again.

## Code

| Part | File |
|---|---|
| Rules, state, map panes | `godot/ShooterCore/Glass/GlassPanes.cs` |
| Tuning (`glass.json`) | `godot/ShooterCore/Glass/GlassConfig.cs`, `godot/shooter/data/glass.json` |
| Wire (event kinds 170, 171) | `godot/ShooterCore/Glass/GlassCodec.cs` |
| Cutting a pane into fragments (clipped to the pane's outline) | `godot/ShooterCore/Glass/GlassShards.cs` |
| Server | `godot/shooter/Net/NetServer.Glass.cs` |
| Client state | `godot/shooter/Net/NetClient.Glass.cs` |
| Client visuals | `godot/shooter/GlassFx.cs` (`WarmUp` draws both shaders once at join so their pipelines compile before the first hit), `shaders/glass_crack.gdshader`, `shaders/glass_shards.gdshader` |
| Model scripts that write the pane list | `scripts/make_bus_model.py`, `make_flak_model.py`, `make_utility_helicopter_model.py`, `make_golf_mk2_model.py` (`GlassSet` in the flak script) |
| Pane kind and cover rules | `godot/ShooterCore/Vehicles/PaneRules.cs`, `RiderRules.BulletsReach` |
| Tests | `godot/ShooterCore.Tests/GlassTests.cs`, `SeatCoverTests.cs` |

## Not done

- The look has not been seen in the game: the shaders (the outline clip included) and the vehicle panes were written without running it or compiling the shaders. Ticket #227 (react to the look and cost) is still open.
- The bus's two cab partition glass and the helicopter's chin and overhead windows are not panes (they are glass that does not break), and the flak truck's rear window has none either.
- A door leaf's crack overlay stays where the leaf is closed when the door is open.
- Outlines must be convex; the crack shader takes at most 12 points per pane.
- Buildings without a detail file, the flats and the loggia houses (their panes are part of the wall mesh) and the landmarks (but the church, whose 23 window openings are panes) have no panes.
- No vehicle has a reinforced pane yet; the flak truck's armoured driver is set by seat cover, not by its panes.
- Gunship rounds, flak bursts, the MOAB, a vehicle crash and a knife do not damage glass.
- No sound for a crack or a shatter.
- The server tests a vehicle's panes and open sides in its upright hit box frame (yaw only), so a banking helicopter's are tested level.
- Inside a vehicle nothing but the riders is in a round's way: seats, the helicopter's gun mount and the bus's partitions do not stop it between the way in and the way out.
- The open sides are not written by the model script: when the helicopter's cabin changes, change them in `vehicles.json` by hand.
- The server looks through every building pane for each shot that stops on the world. Fine for tens of panes; thousands need a grid.
- No bullet hole decal on glass, and the crack does not start where the bullet hit.
