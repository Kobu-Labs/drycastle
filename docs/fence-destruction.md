# Fence destruction (design, v1, #434 Destroyable fences)

Fences break partly, never all at once, and what breaks falls. Each fence type has its own strength.
Status: built 2026-11-27 (map 0.16.0, protocol 88; posts break, map 0.16.1), not yet play-tested. This doc was the contract for parallel work and is kept as built.

**Thin fences are not solid in the village** (rules.yaml `props.fences.collision: false`: no `props_collision` nodes, `collider` null in fences.json). Rounds fly through them and vehicles drive through them, so what breaks a piece is what passes it: a round's path (F3), a vehicle's box (F6). Where the rule is on, the same code also handles a round that stops on one and a vehicle that stops at one, and rebuilds their colliders (F7). Fence walls are always solid (their `house_fence_*_collision`).

## Decisions

| # | decision |
|---|---|
| F1 | **Two models by build.** A **fence wall** (types brick_red, brick_white, brick_wall, stone_wall, concrete) is a voxel grid of house destruction (docs/house-destruction.md): carved by blasts and rams, parts that lose support fall as debris. A **thin fence** (picket, wire, and a section with no type, "plain") breaks by **pieces**: equal strips along the section, each with its own hit points. |
| F2 | **Pieces.** Picket: one board (0.125 m, four pickets per 0.5 m texture tile; piece edges in the gaps between boards). Wire: 0.5 m. Plain: 0.25 m. At most 64 pieces a section (sections are at most 4 m). A **post** (wood, metal) has no hit points: it breaks and falls with the piece it stands in (at a joint: the piece of the first section that met it). |
| F3 | **What damages what.** Bullets, pellets and vehicle gun rounds: thin fences only (masonry ignores small arms), every standing piece the round's path crosses (`FenceRules.PathPieces`; a round that stopped on a solid one is followed past the near face). Blasts: every type. Vehicles: every type (walls by HouseRam, unchanged; thin fences by F6). Players on foot: nothing. |
| F4 | **Strength per type** (fence_destruction.json for thin, house_destruction.json materials for walls): plain and picket 100 hp a piece, a round does 50 (2 rounds); wire 100 hp, a round does 20 (5 rounds). Walls: concrete resist 2.0 (an RPG's 2 m carve is a 1 m hole), brick 2.6, stone 3.2. Starting values, tuned in play. |
| F5 | **Nothing vanishes.** A broken piece falls as a cosmetic body (with the posts that break with it) on every client (tips over, lies, fades or stays with the house rubble mode `/rubble`). A wall's fallen parts are house debris. A joiner sees what is broken, not the bodies. |
| F6 | **Driving into a thin fence.** Every ground vehicle every tick (`FenceDestructionServer.Drive`): at `ram.min_speed` (2 m/s) or more (its own speed; where fences are solid and it stopped at one, the speed it lost), the standing pieces its box touches (shifted `probe_m` along its velocity) cost `momentum_per_hp` times their hit points. Momentum (mass times that speed) above the cost: they all break, it goes on slowed by cost / mass (HouseRam.Decide). Less: each takes its share of the momentum as damage and the vehicle stops. A car (1200 kg) goes through a picket fence from about 10 km/h. |
| F7 | **Collision follows.** A thin fence's collider is its node's original trimesh until a piece of the node breaks; then the node's trimesh is rebuilt from the standing runs of all its thin sections (closed boxes). Server and every client build the same. A wall's collider is house destruction's (voxel columns). |
| F8 | **Round only.** A new round makes every fence whole. No respawn within a round. No nav re-bake (bots keep the baked nav). |
| F9 | **Server-authoritative.** Hit points live on the server only. The server sends which pieces broke (and a push for the falling bodies); clients never decide a break. |

**Not in scope:** sound; a damaged-but-standing look; thieves' fence hop seeing gaps; nav.

## Terms

- **Fence section**: one straight entry of `fences.json` `fences`; its **index** (position in that list) is its id everywhere (u16 on the wire, the mask row, TEXCOORD_1.x, `fence_<index:05d>` grid id).
- **Thin fence**, **fence wall**, **piece**: F1, F2. **Kind**: plain, picket, wire, wall.

## Contract 1: fences.json (pipeline writes, game reads)

Version stays 1; keys are only added (the thieves' FenceBook ignores them). Every section, besides its present keys (a, b, za, zb, height_m, type, parcel, group):

| key | what |
|---|---|
| `kind` | "plain", "picket", "wire" or "wall" |
| `thickness_m` | the thickness it is drawn and collides with |
| `colour` | "#rrggbb", the tint it is drawn with |

A thin section (kind plain, picket, wire) also has:

| key | what |
|---|---|
| `pieces` | 1..64 |
| `piece_m` | width of a piece, metres along a -> b |
| `piece0_m` | where piece 0 starts, metres from a along a -> b (<= 0); piece j covers [piece0_m + j piece_m, piece0_m + (j+1) piece_m] clipped to [0, length] |
| `uv` | [u at a, u per metre along, v at the bottom (ground - 0.1), v at the top]: the texture coordinates the panel is drawn with (the falling piece is drawn with the same) |
| `posts` | the posts that break with it: `[along_m, width_m, z0, z1, "#rrggbb"]` each (along from a, z map heights; map 0.16.1). Absent: none |
| `collider` | the collision node that holds it: "parcel_<ID>_props_collision", or "props_collision@<ix>,<iy>" for a chunk-level one (chunk ix, iy) |

A wall section (kind wall) also has `grid`: its grid id in house_voxels.bin, "fence_<index:05d>".

## Contract 2: map nodes (pipeline writes, MapLoader reads). Map 0.16.0

- **Thin sections**: the breakable panel is in the textured node `<base>_tex_<picket|wire|plain>` (base `parcel_<ID>_props`, or the chunk's `props`). A plain section's box is drawn with the new library texture **plain** (white, tintable: looks as the flat box did). Every vertex of these three textures carries TEXCOORD_1 = (index + 0.5, (along - piece0_m) / piece_m), along = metres from a toward b of the vertex's projection: floor of y is the piece. A thin section's posts are in its `plain` textured node too (map 0.16.1; their colour as tint), every vertex TEXCOORD_1 = (index + 0.5, piece + 0.5) of the piece they break with. Collision is unchanged: the section's closed box in its `props_collision` node.
- **Wall sections**: out of the parcel's (or chunk's) props, visual and collision. Each is `house_fence_<index:05d>_visual` (its flat parts: caps), `house_fence_<index:05d>_visual_tex_<texture>` (its textured faces) and `house_fence_<index:05d>_collision` (closed boxes of the wall and its piers), in the chunk of the section's midpoint. A pier at a joint of two wall sections belongs to the lower index (drawn and celled once).
- **house_voxels.bin**: one grid per wall section, id `fence_<index:05d>`, sorted with the houses by id (ordinal), 0.25 m cells, grid +x from a to b, +y to its left, z up, origin the min corner. Materials appended to CellMaterial: **7 brick** (brick_red, brick_white, brick_wall), **8 stone** (stone_wall), **9 concrete**. A pier is its column's material (brick_red / brick_white column: brick, stone: stone, concrete: concrete; wood or metal posts: the wall's). Cells: solid where the wall box, a pier it owns or a cap covers the cell's centre; at least one cell thick (the middle line); **foundation** where the cell's centre is below the ground line (za to zb along x) and the lowest layer. Colours: the texture's mean colour under the tint; caps the style's cap colour. Same input, same bytes.

## Contract 3: ShooterCore (`godot/ShooterCore/Fences/`, namespace `Drycastle.ShooterCore.Fences`)

Pure C#, System.Numerics, Godot axes, **map-root local** (x east, y up, z = -north; what fences.json's a/b/za become, as FenceBook). Signatures are fixed; WP-0 lands them (stubs throw).

```csharp
public enum FenceKind : byte { Plain, Picket, Wire, Wall }

public sealed record FenceSectionInfo                 // one fences.json section
{
    public ushort Index; public FenceKind Kind; public string? Type;
    public Vector2 A, B;                              // ground points (x, z)
    public float ZA, ZB, HeightM, ThicknessM;         // ground heights (y) at A and B
    public uint Tint;                                 // 0xRRGGBB
    public int Pieces; public float PieceM, Piece0M;  // thin only (Pieces 0 for a wall)
    public Vector4 Uv;                                // thin: u at A, u per metre, v bottom, v top
    public string? Collider; public string? Grid;
    public float Length; public Vector2 Dir;          // derived
    public float Bottom; public float Top;            // min(ZA, ZB) - 0.1, max(ZA, ZB) + HeightM
    public bool Thin;                                 // Kind != Wall
    public (float From, float To) PieceSpan(int j);   // along metres, clipped to [0, Length]
    public Vector3 PieceCentre(int j);                // middle of the piece at half its height
    public float AlongOf(Vector2 p);                  // metres from A along Dir (unclamped)
    public int? PieceAt(float along);                 // null outside [0, Length]
}

public static class FenceFile
{
    public static IReadOnlyList<FenceSectionInfo> Parse(string json);   // bad JSON throws JsonException; unknown version or no list: empty
}

public sealed class FenceIndex                        // 8 m grid of the thin sections
{
    public FenceIndex(IReadOnlyList<FenceSectionInfo> sections);
    public void Near(Vector2 p, float radius, List<int> into);          // indices of thin sections within radius (flat), each once
    public void AlongSegment(Vector2 from, Vector2 to, List<int> into); // the thin sections of every cell a flat segment passes, each once
}

public readonly record struct PieceHit(ushort Section, byte Piece, int Damage);
public readonly record struct FenceRamBox(Vector3 Feet, float Yaw, float HalfWidth, float HalfLength, float Height); // as ServerCar.ContactShape

public static class FenceRules
{
    // Every standing piece a round's path (from, unit dir, length) crosses: the flat path against each section's centre line, the height there between Bottom and Top; nearest first.
    public static void PathPieces(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, Vector3 from, Vector3 dir, float length, List<(ushort Section, byte Piece)> into);
    // Every standing piece whose centre is within 'radius' of 'at' takes damage * (1 - d / radius), rounded, at least 1.
    public static void Blast(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, Vector3 at, float radius, int damage, List<PieceHit> into);
    // The standing pieces the box touches, shifted 'probe' along unit 'dir' (flat OBB against the piece's segment widened by thickness/2, heights overlapping).
    public static void RamPieces(IReadOnlyList<FenceSectionInfo> s, FenceIndex idx, FenceState state, FenceRamBox box, Vector3 dir, float probe, List<(ushort Section, byte Piece)> into);
}

public sealed class FenceState                        // which pieces are broken; hit points on the server
{
    public FenceState(IReadOnlyList<FenceSectionInfo> sections, FenceDestructionConfig cfg);
    public ulong Broken(int section);                 // bit j: piece j broken
    public bool Damaged { get; }                      // any piece of any section broken
    public int Hp(int section, int piece);            // the kind's piece_hp until damaged; 0 when broken
    public bool Damage(int section, int piece, int amount);  // true when this broke it (it was standing)
    public void SetBroken(int section, ulong mask);   // client: exactly this mask
    public void Restore();                            // every piece whole, full hp
    public IEnumerable<int> BrokenSections();         // sections with a broken piece, ascending
}

public static class FenceGeometry
{
    // Closed boxes (12 triangles each, outward) of the standing runs of a thin section, from Bottom to Top, ThicknessM wide: what its collider is.
    public static void ColliderTriangles(FenceSectionInfo s, ulong broken, List<Vector3> into);
    // Runs of standing pieces as along ranges (metres from A).
    public static void StandingRuns(FenceSectionInfo s, ulong broken, List<(float From, float To)> into);
}

public readonly record struct FenceBreakMsg(ushort Section, ulong Pieces, sbyte PushX, sbyte PushZ, byte Speed10); // pieces broken now; push: flat unit direction * 127, m/s * 10
public sealed record FenceSyncMsg(bool Reset, IReadOnlyList<(ushort Section, ulong Broken)> Sections);

public static class FenceCodec                        // events 224, 225; little-endian; bad payload: null
{
    public const int BreakBytes = 13, MaxSyncBytes = 200;
    public static byte[] EncodeBreak(FenceBreakMsg m);          // u16 section, u64 pieces, i8 push x, i8 push z, u8 speed10
    public static FenceBreakMsg? DecodeBreak(ReadOnlySpan<byte> p);
    public static List<byte[]> EncodeSync(FenceSyncMsg m);      // u8 flags (bit 0 reset: only the first message), u16 count, count x (u16 section, u64 broken); at most MaxSyncBytes each; a reset with nothing broken is one message
    public static FenceSyncMsg? DecodeSync(ReadOnlySpan<byte> p);
}
```

`FenceDestructionConfig` (fence_destruction.json, WP-0, real): see Contract 5.

## Contract 4: wire

Reliable, ordered. Kinds **224 FenceBreak**, **225 FenceSync** (224-229 belong to fence destruction). Protocol **88**.

- 224 to everyone: the pieces of one section that broke this tick (one message per section per tick) and the push of their fall: a round's direction, away from a blast's centre, a vehicle's velocity.
- 225 to a joiner: every section with a broken piece (no message when none). To everyone at a new round or `/fences restore`: Reset, then nothing.
- Client: 224 ORs the pieces into its state, hides them, rebuilds the colliders, drops a falling body per piece; 225 sets the masks (Reset clears first), no bodies.

## Contract 5: `godot/shooter/data/fence_destruction.json`

```json
{
  "version": 1,
  "enabled": true,
  "kinds": {
    "plain":  { "piece_hp": 100, "bullet_damage": 50 },
    "picket": { "piece_hp": 100, "bullet_damage": 50 },
    "wire":   { "piece_hp": 100, "bullet_damage": 20 }
  },
  "blast": { "radius_share": 1.0, "damage": 400 },
  "ram": { "enabled": true, "min_speed": 2.0, "momentum_per_hp": 2.0, "probe_m": 0.5 },
  "debris": { "max_bodies": 96, "max_distance_m": 120, "fade_after_s": 20, "fade_s": 2, "stay_cap": 3000, "push_speed": 2.5 }
}
```

- Blast radius = splash radius * radius_share; a piece's damage falls off linearly from `damage` at the centre.
- The rubble mode is house destruction's (244, `/rubble`); `stay_cap` resting pieces at most stay, the oldest go first.
- Walls: house_destruction.json `materials` gains `brick` { resist 2.6, weight 4, strength 1500 }, `stone` { 3.2, 5, 2000 }, `concrete` { 2.0, 4, 1500 }.

## Work packages

WP-0 (done first, by the lead): this doc; `FenceKind`, `FenceSectionInfo`, the Contract 3 stubs; `FenceDestructionConfig` and its json, GameData / ServerConfig plumbing; CellMaterial 7-9, house_destruction.json materials, HouseVoxelFile accepting them; EventKind 224, 225, protocol 88.

| WP | what | owns (only this WP edits these) |
|---|---|---|
| **P Pipeline** | Contract 1 and 2: fences.json keys; pieces (picket boards aligned to the texture); TEXCOORD_1 on the picket, wire and plain textured nodes; the `plain` texture (scripts/art/make_textures.py, assets/manifest.json, the png); wall sections out of props into house_fence_* nodes; their grids in house_voxels.bin; map 0.16.0 (export.py history note); docs/pipeline.md fences.json; pytest | `pipeline/**`, `scripts/art/make_textures.py`, `assets/drycastle-generated-textures/plain.png`, `assets/manifest.json` |
| **C Core** | Contract 3 bodies, tests `ShooterCore.Tests/Fences/*` | `godot/ShooterCore/Fences/*` (not FenceDestructionConfig.cs), `godot/ShooterCore.Tests/Fences/*` |
| **S Server** | `FenceDestructionServer` (godot/shooter/Net/Server/Fences/): load fences.json (manifest `files.fences`), FenceState, shots (SceneryServer.Shot forwards every round's path), blasts (WorldBlasts, both paths), vehicles (VehicleLoop's ram, every ground vehicle every tick: houses first, then `Drive`), batched 224 per tick, colliders flushed once a tick, join 225, round reset, admin `/fences restore`. `FenceColliders` (godot/shooter/Fences/FenceColliders.cs, shared with the client). MapLoader: chunk-level `props_collision` shapes by chunk | the files named, `Net/Server/Scenery/SceneryServer.cs`, `Net/Server/Combat/WorldBlasts.cs`, `NetServer.cs` lines, chat command table rows, `godot/MapLoader/Core/MapLoader.cs` (collision registration only) |
| **V Client** | `FenceWorldNode` (godot/shooter/Fences/): mask texture (R8, 64 x sections, a texel per piece, 1 = broken; row i % 16384, column piece + 64 * (i / 16384)), `fence_break.gdshader` (the library texture times the vertex colour, alpha scissor for cut-outs, discard where the mask says broken), the material swap at the first break (warmed up at load), the colliders (FenceColliders), 224 / 225 handlers (Net/Client), falling pieces (`FenceDebris`: a body per piece, the map's texture material, rubble mode, caps, headless skips; reuse the house DebrisPhase machine). Walls' textured visuals: MapLoader `HouseNode` textured siblings, `house_destruction_tex.gdshader`, HouseWorldNode applying it | `godot/shooter/Fences/*` (not FenceColliders.cs), the shaders, `Net/Client` fence handler, `godot/MapLoader/Core/MapLoader.Houses.cs` and the house visual registration, `HouseWorldNode.cs`, Shooter.cs line |
| **D Docs** (lead, last) | GLOSSARY (Fence section, Thin fence, Fence wall, Piece), ADR 0045, this doc as built | |

## Agent rules

- Read this doc, then CLAUDE.md. Edit only the files your WP owns; a contract change goes to the lead first.
- Unit tests in ShooterCore.Tests and pipeline pytest only, focused, not exhaustive. No running the game, no screenshots.
- Other agents build in the same worktree: a build that fails on a locked file or obj race, wait 30 s and retry.
- No per-frame allocation on client paths. Full village map only: `MAP=out/village/map`.
