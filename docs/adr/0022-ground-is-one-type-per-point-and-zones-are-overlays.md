---
status: accepted
---

# Ground is one type per point; gameplay marks are zones, a separate overlay

The map has two independent things: what the land is, and what an area means in play. Mixing them in one concept (a type that is both terrain and marker, a shape that is paint, ground, marker and water) makes each word mean several things. So every point has exactly one ground type and any number of zones.

- **Ground type**: terrain only (colours, detail recipe, water). Nothing a game system reads. Defined in `rules.yaml`, overridden in `edits.json` `ground_types`; takes OSM land or a rule, or is only drawn.
- **Ground patch** (`GP-NNNN`): a drawn polygon of exactly one ground type, plus `water_y` when the type is water. No stack; where patches overlap the higher ID wins. A colour you want that no type has is a ground type of your own.
- **Zone kind**: what a marked area means in play, from a fixed catalog in code (each kind needs something in the pipeline or the game): `bigfoot`, `deer`, `player_spawn`, ... Its numbers (density, clearances) live in `rules.yaml` `zones`.
- **Zone** (`Z-NNNN`): a drawn polygon of exactly one zone kind. It never changes the terrain. Zones overlap freely and add up.
- **Rule zones**: a kind may set `where_ground: [types]` in `rules.yaml`, so it applies wherever those types lie (deer wherever there is forest) without drawing. Drawn and rule zones are the same to the export. Player spawn is a zone kind; the village and fields get it by rule zones.
- **Export**: ground areas are baked into terrain, detail and water. Zones are never baked into the ground (a zone over a forest does not cut the forest): each zone kind is exported as candidate points (`attribute_points.json`) for the server.
- **Editor**: two tools, Ground (types, patches, the ground map) and Zones (kinds, polygons, an overlay per kind).

**Why a fixed catalog of kinds.** A kind with nothing in the pipeline or the game reading it does nothing; a new kind is one catalog entry, its `rules.yaml` section and the code that reads its points.

**Why no stacks.** A stack exists only to let one polygon be both terrain and marker. With two overlays there is nothing to stack.
