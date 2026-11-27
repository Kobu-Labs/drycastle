# House destruction (design, v1)

Voxel destruction of the **hollow houses** (docs/interiors.md): explosives carve blocky holes, parts that lose their support fall and break into pieces. In the spirit of Cube World, Minecraft and Terraria, but the house keeps its photo-modelled look until it is hit.
Status: designed 2026-11-26, nothing built. This doc is the contract for parallel work: formats, APIs, file ownership, work packages.

## Decisions

| # | decision |
|---|---|
| D1 | **Hidden voxel grid.** Every hollow house gets a grid of 0.25 m cells in its own frame. The smooth mesh stays. A carved cell is discarded from it in the shader, and the exposed cut faces of the cells around it are drawn as voxels. *Assumed, not confirmed by the owner (with D2).* |
| D2 | Cell size **0.25 m** (a player is about 7 cells tall; a 0.3 m wall is 1 to 2 cells). |
| D3 | Only **explosives** carve: rockets, grenades, cannon and flak rounds with a blast, missiles, the Scud, the MOAB, vehicle blasts. Small-calibre bullets never carve. |
| D4 | **Hollow houses only.** Landmarks (church, office, garages, bunkers) and solid houses stay as they are. The brick, stone and concrete fences are grids of this file too (`fence_<index>`, materials brick, stone, concrete; docs/fence-destruction.md, ADR 0045). |
| D5 | **Server-authoritative and deterministic.** The server sends carve ops. Carving, support and detach are pure integer functions of the cells in ShooterCore, run the same way by the server and every client. The server never sends collapse results. |
| D6 | **Structure: detach, then load.** A part with no path to the foundation falls. A cell carrying more than its material's strength crumbles (a one-cell neck under a roof fails). Repeat until stable. |
| D7 | **Debris is client-side cosmetic.** A falling part is one Jolt rigid body. On its first hard impact it is split into Voronoi pieces. The server has no debris, and debris collides with nobody. |
| D8 | **Rubble mode is a toggle:** "fade" (pieces sink away after a while) or "stay" (frozen as cosmetic piles for the round). Set in `house_destruction.json` and live via the admin command `/rubble fade\|stay`. |
| D9 | **Round only.** Round reset restores every house. A joiner gets the removed cells (no debris). |
| D10 | **Vehicles ram walls by momentum** (see "Ramming"). Any vehicle that loses 50 km/h or more to a hollow house's wall in one tick rams it. Its momentum against the hole its cross-section needs decides: through (slowed by the hole) or a dent (it stops). Sent as ordinary carve ops. |

**v2, not in scope:** debris and collapse hurting players (needs server-side debris); terrain craters; digging or placing blocks by hand; bots and nav re-bake; other building types.

## Ramming

`HouseRam` (ShooterCore), `HouseDestructionServer.Ram`, called from `VehicleLoop.StepCar` before the crash rule. `house_destruction.json` `ram`.

- **Trigger.** The speed a wall took in one tick (`ServerCar.LastImpact`, the number the crash rule reads) is at least `ram.min_speed` (13.9 m/s = 50 km/h). Every vehicle: driven, coasting, flown; not a thrown one (the Bigfoot's), not a towed trailer.
- **Contact.** The vehicle's upright box (`ContactShape`), shifted up to `probe_m` along the velocity it hit with (`LastImpactVelocity`), meets live cells (not foundation or floor) ahead of its centre.
- **Cost.** The hole: one sphere whose circle at the wall's face covers the box's cross-section square to the velocity, `margin_m` larger on every side, centred `depth_m` / 2 past the face. One op, so the support pass runs once (a 4-sphere hole cost 55 ms on the biggest house). Its cost is `momentum_per_cell` times the resist of every live cell it would remove. A car's hole in a typical outer wall is about 157 cells, so 130 kg m/s a cell is about 20 000.
- **Through** (mass times speed lost above the cost, summed over every house it meets): the holes are cut, the vehicle goes on along its velocity, slowed by cost / mass, and the crash rule reads only that loss. A car (1200 kg) goes through from about 60 km/h; a truck, a bus or a Humvee at 50 km/h.
- **Dent** (less): each house gets one sphere at the contact, `dent_max_m` times the share of the momentum. The vehicle stops as at any wall.
- **Net.** Only carve ops (242), and the vehicle state the reconciliation already carries. The driver's client predicts the stop and is corrected a round trip later.

## How it fits (existing code)

- Every server blast that damages glass calls `GlassServer.Blast(at, radius)`: `RocketSim.cs:183`, `CarBlast.cs:79`, MissileServer (`NetServer.cs:329`), DemonServer (`NetServer.cs:203`). The Scud (`ScudBlastServer.cs:57`) and gunship / flak (`GunshipServer.cs:253`, `FlakServer.cs:150`) have their own blast paths. All of them get one fan-out (WP-E).
- Map nodes: `parcel_<ID>_visual` and `parcel_<ID>_collision` are merged per parcel in the chunk glbs. The loader registers collision by name (`godot/MapLoader/Core/MapLoader.cs:509-527`). The server's `WorldQuery` and `Splash` use physics, so swapping a house collider makes holes real for movement, shots and blast line of sight with no further change.
- One shared flat vertex-colour map material (`FlatMaterial`, `MapLoader.cs`). Houses get their own ShaderMaterial variant (WP-F).
- Wire patterns to copy: glass (reliable event per change, plus catch-up: docs/glass.md, ADR 0007), crop crush (bitmask, varint runs, messages of at most 200 bytes: docs/crops-destruction.md).
- Nav mesh is baked from colliders at start. Not re-baked in v1; bots do not enter houses anyway.

## Contract 1: `house_voxels.bin` (pipeline writes, game reads)

Written to `out/<map>/house_voxels.bin`, listed in `manifest.json` `files.house_voxels`. A map without it has no destruction. Little-endian.

```
magic "HVOX"  u16 version = 1  u16 house_count
per house (sorted by id, the index is the house's wire id):
  u16 id_len, utf8 id          building id, as interiors.json "id"
  f32 ox, oy, oz               grid corner (cell 0,0,0 min corner), map local metres: x east, y north, z up (as interiors.json)
  f32 yaw                      radians, ccw from east, direction of grid +x (the floor plan's frame: square to the longest wall)
  f32 cell                     0.25
  u16 nx, ny, nz
  u8  palette_count            <= 64, entry 0 = air
  palette_count x { u8 material, u8 r, u8 g, u8 b }
  u32 rle_bytes, rle           (u8 palette index, varint run) over i = x + nx*(y + ny*z)
```

Materials (u8, `CellMaterial`): 0 air, 1 foundation, 2 floor, 3 wall, 4 partition, 5 ceiling, 6 roof. Colours come from the house's visual (wall colour, roof colour, inner wall colour, ...).

Voxeliser rules (WP-D):
- The grid bbox covers the whole visual of the house, eaves and steps included, plus 1 cell of margin. Anything left outside could never be discarded.
- Walls, floor, ceiling and partitions are rasterised from the interior geometry (`interior.py` `Inside`, the plan's partitions). Use their real thickness: a cell is solid when its centre is inside the solid.
- Walls are at least 1 cell thick: a wall thinner than a cell still marks the cell its middle falls in.
- The outer wall face stays within half a cell of the real facade.
- Openings that go through (`Inside.through`) are air. Closed openings, recesses and windows above the ceiling are wall.
- Roof: a 1-cell skin under the visual roof surface. The **attic is air**, closed by the ceiling slab.
- Foundation: one layer under the floor slab, across the footprint. It is the only support root and is never carved.
- What stands out of the walls and roof (door canopies, steps, balconies, fascia and gutters, downpipes): every cell outside the footprint its faces pass through. Canopies, eaves and pipes are roof, steps and balconies floor. Steps are solid down to the ground and their ground cell is foundation, so they stand on their own; a balcony is held by its wall and falls with it.
- Chimneys are solid from the skin up. A dormer is a 1-cell shell whose inside is air joined to the attic under it.
- Deterministic: same input, same bytes.

## Contract 2: map nodes (pipeline writes, MapLoader reads)

- Each hollow house is taken out of its parcel's merged meshes into `house_<bid>_visual` and `house_<bid>_collision`, in the chunk of its centroid. The collision is the house's existing exact shell (`interior.collision`), unchanged.
- MapLoader keeps them by bid (`HouseNodes`: visual MeshInstance3D, CollisionShape3D). They also go into the normal collision set, so the nav bake and `WorldQuery` still see them.

## Contract 3: ShooterCore API (`godot/ShooterCore/Buildings/Destruction/`, namespace `Drycastle.ShooterCore.Buildings.Destruction`)

Pure C#, System.Numerics, no Godot. Every decision is in integers. Signatures are fixed; WP-0 lands them as stubs.

```csharp
public enum CellMaterial : byte { Air, Foundation, Floor, Wall, Partition, Ceiling, Roof }

public sealed class HouseGrid                       // immutable, as read from the file
{
    public ushort Index; public string Id;
    public Vector3 Origin; public float Yaw; public float Cell;
    public int Nx, Ny, Nz;
    public ReadOnlyMemory<(CellMaterial Material, byte R, byte G, byte B)> Palette;
    public ReadOnlyMemory<byte> Cells;               // palette index per cell, 0 = air
    public int IndexOf(int x, int y, int z);
    public Vector3 ToGrid(Vector3 map);              // map local (z up) -> grid units (cells, float)
    public Vector3 ToMap(Vector3 grid);
}

public static class HouseVoxelFile
{
    public static IReadOnlyList<HouseGrid> Read(Stream s);
    public static void Write(Stream s, IReadOnlyList<HouseGrid> houses);   // tests and fixtures
}

public readonly record struct CarveOp(short Cx, short Cy, short Cz, ushort RadiusQ)  // centre and radius in quarter-cells, grid frame
{
    public static CarveOp? From(HouseGrid g, Vector3 map, float radiusM);           // null: the sphere misses the grid
}

public sealed record CarveResult(int[] Carved, int[] Crumbled, IReadOnlyList<int[]> Fallen, uint Hash);

public sealed class HouseCells                      // live state of one house
{
    public HouseCells(HouseGrid grid);
    public HouseGrid Grid { get; }
    public ushort OpSeq { get; }                     // ops applied this round
    public bool Solid(int i);                        // original solid and not removed
    public bool Damaged { get; }
    public CarveResult Apply(CarveOp op, DestructionConfig cfg);   // carve, then Support.Settle, OpSeq++
    public uint Hash();                              // FNV-1a 32 over the removed bitmask
    public void Restore();
    public byte[] EncodeRemoved();                   // varint runs (kept, removed, ...) over the original solid cells in index order
    public void DecodeRemoved(ReadOnlySpan<byte> runs, ushort opSeq);
}

public static class Support                         // WP-B
{
    public static (int[] Crumbled, IReadOnlyList<int[]> Fallen) Settle(HouseCells c, DestructionConfig cfg);
}

public readonly record struct Quad(Vector3 Corner, Vector3 U, Vector3 V, Vector3 Normal, byte Palette);  // grid units
public static class HouseMesher                     // WP-C
{
    public static void Surface(HouseCells c, List<Quad> into);                      // every open face, greedy, no allocation beyond 'into'
    public static void Surface(HouseCells c, CellBox region, List<Quad> into);      // the same for one region (a collider column)
    public static void Damaged(HouseCells c, HouseDamageLook look, List<Quad> into); // the client's voxel faces near holes and toward the attic
    public static void Surface(HouseGrid g, ReadOnlySpan<int> cells, List<Quad> into); // a debris piece, all faces
    public static void Boxes(HouseCells c, List<(Vector3 Min, Vector3 Max)> into);  // greedy box cover, for colliders
}

public static class Fracture                        // WP-C
{
    public static IReadOnlyList<int[]> Split(HouseGrid g, int[] cells, Vector3 impactGrid, uint seed, FractureConfig cfg);
}

public static class HouseCodec { /* events 242-244, see Contract 4 */ }
public sealed record DestructionConfig(/* ... */);   // from house_destruction.json, see Contract 5
public static class TestHouses { public static HouseGrid Box(int nx, int ny, int nz /* , ... */); }  // fixture: walls, floor, ceiling, roof skin, a door
```

Rules (all of them in WP-A and WP-B unless said):
- **Carve.** Cell i (centre p, in quarter-cells) is removed when it is solid, not foundation, and `d2(p, c) * resist^2 <= RadiusQ^2 * 256`. `resist` is the material's resistance in 1/16 units (16 = 1.0); 0 means never. `long` maths.
- **Support (D6).**
  1. Detach: flood fill (6-neighbour) from the foundation cells. Any solid component without one is Fallen.
  2. Load: a breadth-first pass from the foundation gives every cell its distance to the foundation. Go from the largest distance to the smallest; ties are broken by cell index. Each cell hands `own weight + received load` to its neighbours with a smaller distance, split evenly (integer division; the remainder goes to the lowest index).
  3. A cell whose load is over its material's `strength` is Crumbled (removed with no debris, dust only).
  4. Repeat from step 1, at most `support.max_passes` times.
  - Optional `span` rule per material: at most N cells sideways from the nearest vertical support, else crumble. Off by default.
- Fallen and Crumbled cells are removed from the state, and `Hash` is taken after that.
- **Fracture.** Seeds come from a xorshift seeded with `hash(house index, OpSeq, component's lowest cell)`. Seed count = cells / `fracture.cells_per_piece`. Seeds are packed closer near `impactGrid` (`fracture.impact_bias`). Each cell goes to its nearest seed (squared distance, ties by seed index). A piece that ends up disconnected is split into its components. Pieces under `fracture.dust_below_cells` come back as one last group: dust.
- **Speed (micro-benchmarks in tests, not frame tests).** A 40x32x28 house:
  - an RPG op (r = 2 m) with Settle: under 1 ms
  - `Surface(CutFaces)`: under 2 ms
  - `Boxes`: under 2 ms

## Contract 4: wire

Reliable, ordered. Kinds **242, 243, 244** (free at protocol 86; check `Wire.cs` `EventKind` and bump `Wire.ProtocolVersion` once, in WP-0).

| kind | name | payload | when |
|---|---|---|---|
| 242 | `HouseCarve` | u16 house, u16 op_seq (after), i16 cx, cy, cz, u16 radius_q, u32 hash (after Settle) | each op; one blast can send several (one per house it reaches) |
| 243 | `HouseCells` | u16 house, u16 op_seq, u32 hash, u32 first_run, runs (varint) | join catch-up and resync; split into messages of at most 200 bytes |
| 244 | `HouseSettings` | u8 flags (bit 0 rubble stay), u8 reset (1 = every house whole) | round start, settings change, join |

Client:
- Applies 242 with `HouseCells.Apply`.
- When its `op_seq` or `hash` differs from the server's, it logs `house desync <id>` and asks for 243 (249), again every 5 s. Still unlike the server's after 20 s with no new op on that house (its map data differs, so a full 243 never matches; the join check's build hash covers `house_voxels.bin`, so this is a safety net), it warns once and stays frozen, its 242s ignored, until the round resets. The request is a client message on a free client kind (picked in WP-0).
- Resync request: client to server event **249 `HouseResync`**, payload u16 house (protocol 87).
- A joiner gets 244, then 243 for every damaged house.

## Contract 5: `godot/shooter/data/house_destruction.json`

```json
{
  "enabled": true,
  "carve": { "default_share": 0.2,
             "by_source": { "rpg": 2.0, "grenade": 1.5, "cannon": 0.6, "flak": 0.0, "gunship_rocket": 1.6,
                            "gunship_cannon": 2.5, "missile": 3.0, "car": 1.5, "scud": 10.0, "moab": 14.0, "demon": 0.0 } },
  "materials": {
    "foundation": { "resist": 0,   "weight": 0, "strength": 0 },
    "floor":      { "resist": 1.2, "weight": 3, "strength": 60000 },
    "wall":       { "resist": 1.0, "weight": 4, "strength": 1500 },
    "partition":  { "resist": 0.6, "weight": 2, "strength": 800 },
    "ceiling":    { "resist": 0.9, "weight": 2, "strength": 2500 },
    "roof":       { "resist": 0.5, "weight": 1, "strength": 400 }
  },
  "support": { "max_passes": 4, "span_cells": 0 },
  "fracture": { "cells_per_piece": 24, "impact_bias": 0.6, "dust_below_cells": 4 },
  "debris": { "rubble": "fade", "fade_after_s": 15, "fade_s": 2, "max_bodies": 64, "max_distance_m": 150,
              "break_impulse": 4.0, "second_break": true, "stay_cap_cells": 20000 }
}
```

- Carve radius in metres = `by_source[source]`, else `splash radius * default_share`. 0 = no carve.
- `resist` is a float in json. `DestructionConfig` turns it into 1/16 ints.
- Weights and strengths are starting values; tune them in play.
- `debris.rubble` is the D8 toggle. `/rubble fade|stay` overrides it live for the round; the server sends it to everyone in 244.

## Work packages

WP-0 runs first, alone and small. After it, WP-A to WP-G run in parallel. WP-H comes last.

| WP | what | owns (only this WP edits these) | needs |
|---|---|---|---|
| **0 Skeleton** | Contract 3 as stubs (`NotImplementedException`); `TestHouses.Box`; `HouseVoxelFile` read and write (real); config record and json; event kinds, empty `HouseCodec`, protocol bump; client resync kind; the `IHouseDebris` interface (below); `BlastSource` enum; an `internal` setter on `HouseCells` for B's tests | `ShooterCore/Buildings/Destruction/*` (stubs), `Wire.cs` lines, `data/house_destruction.json` | none |
| **A Cells** | `CarveOp.From`, `HouseCells` (carve, removed bitmask, hash, encode / decode), `HouseCodec` | `HouseCells.cs`, `CarveOp.cs`, `HouseCodec.cs`, tests `ShooterCore.Tests/Buildings/Destruction/Cells*` | 0 |
| **B Support** | `Support.Settle` (detach, load, span, passes) | `Support.cs`, tests `Support*` | 0 |
| **C Mesher and fracture** | `HouseMesher` (cut faces, all faces, piece, boxes); `Fracture.Split` | `HouseMesher.cs`, `Fracture.cs`, tests | 0 |
| **D Pipeline** | voxeliser, `house_voxels.bin`, manifest entry, `house_<bid>_*` nodes, debug view (`show <bid>`: coloured .obj and one PNG per z layer under `out/village/voxels/`, opened with xdg-open), pytest | `pipeline/src/drycastle_pipeline/buildings/voxels.py`, export hooks in `export/scene.py` and `gltf.py`, `pipeline/tests/buildings/test_voxels.py` | Contracts 1 and 2 only |
| **E Server** | `HouseDestructionServer`: load the file; `WorldBlasts.Blast(at, splashRadius, source)` fan-out (glass plus houses) at every call site above; apply ops, rebuild the server collider (one per house per tick, from `HouseMesher.Boxes` or `AllFaces`); send 242 and 244; join catch-up 243; resync; round reset; admin `/carve <r>` (at the crosshair), `/houses restore`, `/rubble fade\|stay` | `godot/shooter/Net/Server/Houses/HouseDestructionServer.cs`, `Net/Server/Combat/WorldBlasts.cs`, call sites, `ChatCommandTable.cs` and `ChatCommands.cs` rows | 0 (runs on the stubs; real behaviour once A and B land) |
| **F Client world** | MapLoader `HouseNodes`; `house_destruction.gdshader` (discard by 3D texture); per-house `ImageTexture3D` (R8, 1 = removed); cut-face mesh node (vertex colours, `Surface(CutFaces)`); client collider swap (the same collider as the server's, for predicted movement); handle 242, 243 and 244; hand `CarveResult` to `IHouseDebris`; round reset; first-use shader warm-up | `godot/MapLoader/Core/MapLoader.Houses.cs`, `godot/shooter/Buildings/Destruction/HouseWorldNode.cs`, the shader, the `NetClient` handlers | 0, D (fixture glb from `TestHouses` until D lands) |
| **G Debris FX** | `IHouseDebris` implementation: a body per Fallen component (mesh `Surface(piece)`, collider from `Boxes`, capped at 16 boxes and otherwise its AABB); break on first impact over `break_impulse` with `Fracture.Split`, velocity inherited plus an outward kick; dust for Crumbled cells and the dust group; rubble modes and cap; limits; headless clients skip; `DebrisPhase` machine | `godot/shooter/Buildings/Destruction/Debris*.cs`, `ShooterCore/Buildings/Destruction/DebrisPhase.cs` (machine spec), tests | 0, C |
| **H Tie-ins and docs** | glass (a pane is shattered and then hidden when the wall cells ringing it lose 25 %, or when it falls); doors (a leaf falls when a frame cell is gone); GLOSSARY entries (Hollow house cell, Carve op, Rubble); an ADR (D1, D5, D6, D7; amends ADR 0007 §3 and ADR 0040 §4); this doc, from plan to as-built; docs/interiors.md "Not built" | the files named | A, E, F |

`IHouseDebris` (WP-0, used by F, implemented by G):

```csharp
public interface IHouseDebris
{
    void Fall(HouseGrid g, CarveResult r, Vector3 blastMap, uint seed);  // Fallen -> bodies, Crumbled and Carved -> dust
    void SetRubble(bool stay);
    void ResetAll();                                                      // round reset
}
```

**Debris machine (WP-G).** `DebrisPhase { Falling, Broken, Resting, Fading }`:
- Falling -> Broken: first impact over `break_impulse`.
- Falling or Broken -> Resting: the body sleeps.
- Resting -> Fading: fade mode, after `fade_after_s`.
- Resting -> frozen into the house's rubble mesh: stay mode (the piece leaves the machine).
- A `StateMachineSpec` sits next to the owner. Then run `UPDATE_STATE_DOCS=1 dotnet test godot/ShooterCore.Tests --filter StateMachineCatalogTests`.

**Shader detail (WP-F).**
- Map the fragment's world position to the grid with a uniform matrix.
- Step 0.01 m against the normal before sampling, so a face lying on a cell boundary reads the cell behind it.
- Sample the nearest texel; removed means discard.
- An undamaged house keeps the flat material (no 3D texture). It gets the shader variant on its first carve.

## Integration order

1. WP-0 merged. A, B, C, D and G start; E and F start on the stubs.
2. A + B + C done: E and F turn on real behaviour behind `enabled`.
3. D done: the real map. Check the debug view for 20 houses (the owner looks).
4. H.
5. Play test (the owner): RPG on a wall, a roof held up by one column, a Scud on a street, rubble fade and stay.

## Agent rules

- Read this doc, then CLAUDE.md. Edit only the files your WP owns. Anything that needs a contract change goes to the owner first, not into code.
- Unit tests in ShooterCore.Tests and pipeline pytest only. No running the game, no screenshots. Keep tests focused, not exhaustive.
- No per-frame allocation on the client paths (mesher into reused lists; texture updates once per house per frame).
- Cost (as built): the look and the near-hole mesh read only the removed cells' box (plus 4); the attic faces only the attic's box. The collider is columns of 16 x 16 cells (`HouseVoxelCollider`), only the changed ones rebuilt. The client rebuilds the houses of one frame beyond the first within 6 ms and leaves the rest for the next frames; a house with falling parts is rebuilt first, then they fall.
- Full village map only: `MAP=out/village/map`.

## Open questions

1. D1 (look) and D2 (0.25 m cells) are assumed; confirm.
2. Nav: is a destroyed house that still blocks bots acceptable for v1?
3. Collision of an undamaged house is the exact shell. On its first carve it becomes voxel boxes (up to half a cell off). Acceptable?
