# Glass panes

Glass panes on vehicles and buildings have hit points, crack as they lose them and shatter at zero. The decisions are in ADR 0007 (`docs/adr/0007-glass-panes-are-declared-rectangles-with-server-owned-hit-points.md`); the terms are in `GLOSSARY.md` (Glass pane, Crack stage, Shattered).

## What exists

| Glass | Source | Panes |
|---|---|---|
| Buildings | `windows.json` of the map (window openings of buildings with a detail file, `pipeline/config/buildings/`) | every pane whose `breakable` is not false (the village map lists 35 panes today) |
| Car | `vehicles.json` `glass` | windscreen, rear, two side |
| War jeep | `vehicles.json` `glass` | windscreen, rear, two side |
| Bus | `vehicles.json` `glass` | windscreen, rear, six left bays, four right bays, four door leaves |
| Flak truck | `vehicles.json` `glass` | two windscreen halves, two cab sides |
| Utility helicopter | `vehicles.json` `glass` | two windscreen halves, two cockpit doors |

The motorcycle, bicycle, quad, amphibious transport and boat have no glass. The vehicle rectangles were measured from the model files (the faces with the glass material, or the glass cells of the Kenney colour map) and moved into the vehicle frame with the model's scale, yaw and offset.

## Rules

- Server owns the hit points (`GlassPanes`). A whole pane is not stored.
- A bullet, pellet, roof gun or door gun round that hit no player damages the pane it crossed where it stopped, by its damage at that range.
- A bazooka rocket, an attack missile and a vehicle explosion damage every pane in the blast radius (`blast_damage` at the centre, nothing at the edge).
- Crack stage from the fraction of hit points left: 1 when damaged, 2 at 66 %, 3 at 33 % (`crack_stages`).
- At zero the pane is shattered. The fragments fly once, on the clients that see it happen.
- A shot through a shattered vehicle pane can hit a rider behind it (`shattered_exposes_riders`). A building's wall stays solid.
- A new round and a repaired vehicle make panes whole again.

## Code

| Part | File |
|---|---|
| Rules, state, map panes | `godot/ShooterCore/Glass/GlassPanes.cs` |
| Tuning (`glass.json`) | `godot/ShooterCore/Glass/GlassConfig.cs`, `godot/shooter/data/glass.json` |
| Wire (event kinds 170, 171) | `godot/ShooterCore/Glass/GlassCodec.cs` |
| Cutting a pane into fragments | `godot/ShooterCore/Glass/GlassShards.cs` |
| Server | `godot/shooter/Net/NetServer.Glass.cs` |
| Client state | `godot/shooter/Net/NetClient.Glass.cs` |
| Client visuals | `godot/shooter/GlassFx.cs`, `shaders/glass_crack.gdshader`, `shaders/glass_shards.gdshader` |
| Tests | `godot/ShooterCore.Tests/GlassTests.cs` |

## Not done

- The look has not been seen in the game: the shaders and the vehicle rectangles were written without running it. Ticket #227 (react to the look and cost) is still open.
- Panes are not separate meshes in the vehicle models, so a shattered see-through pane is darkened, not removed.
- Buildings without a detail file, the flats and the loggia houses (their panes are part of the wall mesh) and the landmarks have no panes.
- Gunship rounds, flak bursts, the MOAB, a vehicle crash and a knife do not damage glass.
- No sound for a crack or a shatter.
- The server tests a vehicle's panes in its upright hit box frame (yaw only), so a banking helicopter's panes are tested level.
- The server looks through every building pane for each shot that stops on the world. Fine for tens of panes; thousands need a grid.
- No bullet hole decal on glass, and the crack does not start where the bullet hit.
