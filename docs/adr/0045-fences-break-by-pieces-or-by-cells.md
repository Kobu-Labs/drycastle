# Fences break by pieces or by cells

Decided for #434 (Destroyable fences), 2026-11-27. The owner asked for fences that break partly, never all at once, with a strength that fits each type; bullets do not break masonry; no repair within a round. Design and contracts: docs/fence-destruction.md.

**1. Two models by how a fence is built.** A Fence wall (brick, stone, concrete) is a voxel grid of house destruction (ADR 0040's machinery, docs/house-destruction.md): one grid per Fence section in house_voxels.bin, cell materials brick, stone and concrete. Blasts carve it and vehicles ram it exactly as a Hollow house's walls; what loses its support falls as house debris. A Thin fence (picket, wire, plain) is a row of Pieces with hit points; a voxel grid of 0.25 m cells would be four times thicker than a picket and blocky where it breaks.

**2. The server owns the pieces' hit points** (`FenceState`); clients get only which pieces broke. Event 224 `FenceBreak` (a section's pieces broken this tick, the push of their fall), 225 `FenceSync` (the join catch-up; a reset at a new round), protocol 88.

**3. The panel is hidden by a mask, not rebuilt.** Every vertex of a thin panel carries its section and piece in TEXCOORD_1 (map 0.16.0); one R8 texture holds a texel per piece and the fence shader discards broken ones. No node per section (there are 4000), no mesh rebuild. A plain fence is drawn with a white library texture (`plain`) so it is a textured node like picket and wire.

**4. Thin fences stay as solid as the map makes them.** The village builds them without collision (rules `props.fences.collision: false`), so rounds and vehicles pass through them and break what they pass: a round every piece its path crosses, a vehicle (checked every tick, by its own speed) the pieces its box meets, stopping when it has not the momentum. Where the rule is on, a piece's break rebuilds its node's trimesh from the standing runs on the server and every client (`FenceColliders`). A Fence wall is always solid: its own collision node, house destruction's collider once carved.

**5. What breaks what.** Rounds: Thin fences only (picket 2, wire 5). Blasts: every fence. Vehicles: walls by house momentum (a ram); thin fences by their own (from 2 m/s, the pieces' hit points are the cost).

Consequences:

- Nothing is repaired within a round; a new round makes every fence whole.
- Fence walls become solid (they were visual only with the rest of the fences): players, bots' nav bake, rounds and vehicles meet them.
- Bots keep the baked navigation (a gap is not a path for them); the thieves' fence hop still sees the whole fence.
- A joiner sees the gaps but not the fallen pieces.
- No sound of its own; the shot's impact plays as before.
