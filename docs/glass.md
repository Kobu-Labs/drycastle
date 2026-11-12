# Glass panes

Glass panes on vehicles and buildings have hit points, crack as they lose them and shatter at zero. The decisions are in ADR 0007 (`docs/adr/0007-glass-panes-are-declared-rectangles-with-server-owned-hit-points.md`) and, for the generated vehicles, ADR 0008 (`docs/adr/0008-generated-vehicle-glass-is-a-node-per-pane-with-an-outline.md`); the terms are in `GLOSSARY.md` (Glass pane, Crack stage, Shattered).

## What exists

| Glass | Source | Panes |
|---|---|---|
| Buildings | `windows.json` of the map (window openings of buildings with a detail file, `pipeline/config/buildings/`) | every pane whose `breakable` is not false (the village map lists 35 panes today) |
| Car | `vehicles.json` `glass` | windscreen, rear, two side |
| War jeep | `vehicles.json` `glass` | windscreen, rear, two side |
| Bus | `vehicles.json` `glass` | windscreen and its two wrapped corners, rear, six left windows, four right windows, four door leaves (18) |
| Flak truck | `vehicles.json` `glass` | two windscreen halves, a door window and a quarter light on each side (6) |
| Utility helicopter | `vehicles.json` `glass` | two windscreen halves, two cockpit doors |

The motorcycle, bicycle, quad, amphibious transport and boat have no glass.

**Generated models (bus, flak truck, helicopter).** The model script builds every pane as its own node `glass_N` (N = the pane's place in the vehicle's `glass` list) and, when run without an output path, rewrites that list in `vehicles.json`: `centre`, `normal`, `size` (the bounding rectangle) and `outline` (a convex counter-clockwise polygon `[u, v]` in metres from the centre, so a trapezoid or a triangle is its real shape). Do not edit those lists by hand; change the script and run it (`python3 -I scripts/make_bus_model.py`, and the same for `make_flak_model.py` and `make_utility_helicopter_model.py`). The client hides the `glass_N` node while the pane is shattered. Shots, the crack overlay and the fragments use the outline.

**Kenney models (car, jeep).** Their glass is painted into one colour map, so the panes are rectangles measured from the glass cells and moved into the vehicle frame with the model's scale, yaw and offset; they have no pane node and no outline, and the original glass stays under a shattered pane.

## Rules

- Server owns the hit points (`GlassPanes`). A whole pane is not stored.
- A bullet, pellet, roof gun or door gun round that hit no player damages the pane it crossed where it stopped, by its damage at that range.
- A bazooka rocket, an attack missile and a vehicle explosion damage every pane in the blast radius (`blast_damage` at the centre, nothing at the edge).
- Crack stage from the fraction of hit points left: 1 when damaged, 2 at 66 %, 3 at 33 % (`crack_stages`).
- At zero the pane is shattered. The fragments fly once, on the clients that see it happen.
- A bullet, pellet, roof gun or door gun round goes through a vehicle's glass, whole or shattered (`BulletHit`): it damages the pane it enters by, goes on inside (riders are open to it) and leaves by the next pane it crosses, then on to whatever is beyond, through further panes the same way. With no pane to leave by the hull stops it 1.5 m in. A building's wall stays solid. This replaces ADR 0007 decision 3; `shattered_exposes_riders` in `glass.json` is no longer read.
- A shattered pane of a model with its own `glass_N` node leaves a real opening (no dark fill); the flak truck (solid glasshouse) and the Kenney cars keep a dark fill.
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
| Client visuals | `godot/shooter/GlassFx.cs`, `shaders/glass_crack.gdshader`, `shaders/glass_shards.gdshader` |
| Model scripts that write the pane list | `scripts/make_bus_model.py`, `make_flak_model.py`, `make_utility_helicopter_model.py` (`GlassSet` in the flak script) |
| Tests | `godot/ShooterCore.Tests/GlassTests.cs` |

## Not done

- The look has not been seen in the game: the shaders (the outline clip included) and the vehicle panes were written without running it or compiling the shaders. Ticket #227 (react to the look and cost) is still open.
- The Kenney car and jeep have no pane nodes: their shattered panes are darkened, not removed, and are rectangles.
- The bus's two cab partition glass and the helicopter's chin and overhead windows are not panes (they are glass that does not break), and the flak truck's rear window has none either.
- A door leaf's crack overlay stays where the leaf is closed when the door is open.
- Outlines must be convex; the crack shader takes at most 12 points per pane.
- Buildings without a detail file, the flats and the loggia houses (their panes are part of the wall mesh) and the landmarks have no panes.
- Gunship rounds, flak bursts, the MOAB, a vehicle crash and a knife do not damage glass.
- No sound for a crack or a shatter.
- The server tests a vehicle's panes in its upright hit box frame (yaw only), so a banking helicopter's panes are tested level.
- The server looks through every building pane for each shot that stops on the world. Fine for tens of panes; thousands need a grid.
- No bullet hole decal on glass, and the crack does not start where the bullet hit.
