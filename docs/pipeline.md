# Pipeline reference

Details for the map pipeline (`pipeline/`). Setup and the make targets are in the [README](../README.md).

## Code layout

`pipeline/src/drycastle_pipeline/` (tests mirror it in `pipeline/tests/<package>/`). The package `__init__.py` files are
empty on purpose: some modules import each other lazily inside functions, and an eager `__init__` would turn that into cycles.
`cli.py` lists the stages (`STAGE_MODULES`: name -> module with `run`).

| Package | Holds |
|---|---|
| `core/` | config, rules.yaml loader, bounding-box maths (`geo`), local frame, polite HTTP, disk cache, asset manifest, textures, `Mesh`, colour and geometry helpers |
| `sources/` | the download stages: `fetch`, `ortho`, `ortho_trees` |
| `photos/` | street-level photos (Street View, Mapillary), the live Street View follower, and the photo-to-building tools |
| `parcels/` | `normalise`, the parcel registry, the parcels stage (`build`), hand-edited outlines |
| `ground/` | ground attributes and areas, hand-drawn ground patches and their meshes |
| `terrain/` | the height grid, natural water beds, water bodies |
| `roads/` | the roads stage and street lamps |
| `buildings/` | the rules stage (`derive`), footprints, building meshes, wall openings; `buildings/landmarks/` the hand-modelled landmarks (flats, garage, office, church interior, bunker, ...) |
| `props/` | fences, trees, ground objects, spawns (the props stage) and their meshes |
| `detail/` | ground detail: patches, clutter, crops, meadow, dike, the Rust engine bridge |
| `kits/` | generated structures built from parts: utility poles, connectors, flood wall, graves, `prim:*` primitives |
| `export/` | scene assembly per chunk and the export stage |
| `preview/` | pictures of the map, poles, flood wall, graves, fences and ground detail |
| `edits/` | `edits.json` schema and lock, zones, sketch file, `parcel-run`/`patch-run`, dishes, house connections; `edits/mcp/` the MCP server (see [mcp.md](mcp.md)) |

## Configuring the map and fetching data

The map is the village inside an outline. Set it in a local, gitignored file:

```sh
cp pipeline/config/map.example.yaml pipeline/config/map.local.yaml
# edit osm_way (OSM way id; its centroid is the origin of the local frame), polygon (the outline, required)
# and margin_m (extra metres fetched around it, default 25)
make fetch
```

`make fetch` writes to `data/village/raw/` (gitignored): `osm.json` (Overpass: buildings, highways,
barriers, landuse, natural tree/wood/water), `parcels.geojson` (INSPIRE cadastral parcels
intersecting the box, WFS) and `dmr5.tif` (DMR 5.0 terrain, 1 m, EPSG:3046, via INSPIRE WCS).
Each file has a `<name>.meta.json` sidecar (request, url, fetched-at, sha256, source, licence,
attribution) and `manifest.json` lists the sources, licences and required attribution.

Reruns skip files whose request parameters are unchanged and download nothing; use
`cd pipeline && uv run python -m drycastle_pipeline fetch --force` to refetch everything.
Requests are sequential, use the User-Agent `drycastle-pipeline`, have timeouts and retry with
backoff (Overpass falls back to a second public instance).

## The map

One map is built: the village inside its outline (`polygon`, see below). Its paths:

| | |
| --- | --- |
| raw data | `data/village/raw/` |
| work files | `data/village/work/` |
| export, previews | `out/village/map/`, `out/village/preview/` |

`make map-update` re-runs only `props`, `detail` and `export` (after `edits.json` changed: placed objects, zones, fences; ground patches and parcel outlines also need `make parcels` first). It needs one full `make map` first, does not fetch, run the orthophoto stages or rebuild terrain/roads, and plants the trees again from the cached `ortho/trees.json`. Same output folder as `make map`. Only zones changed: `make export` is enough.
| `map_version` | `<MAP_VERSION_BASE>-village` (`export/export.py`) |

The committed registry `registry/parcels.registry.json` is the one
source of IDs. Fetching is polite: Overpass in one request (a box over about 1.5 km a side in pieces of that size, each cached in
`osm_tiles/` so a failed run resumes, merged into `osm.json` without duplicates), cadastral WFS paged (`startIndex`, 5000 per page, sorted)
when a page is full, the DMR raster in WCS tiles of at most 1000 m per side (cached per tile in
`dmr_tiles/`) that are mosaicked into `dmr5.tif`. All are cached; `--force` refetches.

**Map outline.** `polygon` in `map.local.yaml` (required) is the outline: any
simple polygon, given as absolute S-JTSK `[x, y]` metres (mapy.com or GPS points convert with pyproj
`EPSG:4326 -> EPSG:5514`, lon first; local game coordinates are these minus `origin_epsg5514` in
`frame.json`). Upstream services only take boxes, so the pipeline fetches the polygon's bounding box
(grown by `margin_m`) and then clips to the polygon: `normalise` drops every parcel, building, road,
tree, lamp and landuse that does not touch it (whole objects are kept, not cut), `area_polygon_epsg5514` in
`frame.json` is the polygon (the registry only retires entries inside it, the terrain coverage is it too), which
every later stage already clips to. The terrain mesh reaches 1.5 cells past the outline so the
edge is not eaten. The centre `osm_way` still sets the local origin, so the frame and all edits stay put.
Changing the polygon needs `make fetch` again (the box changes) and then the stages after it. Not done:
nothing stops a player at the edge (the flight limit is still the parcels' bounding rectangle).

`rules.yaml` is the village profile: fewer fences without collision boxes, lower tree densities (the per-chunk tree cap
`props.trees.max_per_chunk` is available but set to 0 = off) and more spawn candidates, to keep chunk files small for 16-64
players on weak GPUs. `make preview` also writes `overview.png`: the parcels and
buildings with the 250 m chunk grid and chunk indices.

Village numbers (one real run, default settings): 1165 parcels, 384 buildings, 40 chunk files,
about 400k visual and 75k collision triangles in total, at most about 24k visual triangles and 1.7 MB
per chunk, 29 MB of glb in all (fences without collision and the tree cap took that from 39 MB). A
full run after the fetch takes about half a minute; the fetch takes about two minutes. Nothing was
measured in Godot yet; terrain (4 m grid, 7.7k triangles per chunk) is now the biggest visual part.

## Parcels and ground areas

Only a property is a parcel: a cadastral parcel that holds a building (kind `building_plot`), or one that is in a property
group (`config/edits.json`) with a parcel that does (kind `garden`). The land of every other cadastral parcel (fields,
forest, water, the land the roads lie on, gardens of no property) is not kept as parcels. The parcels stage (`ground/areas.py`,
`ground` in `rules.yaml`) cuts it into **ground areas** by type instead, written to `parcels.json` under `ground` in the
shape of a parcel (`id` `A-0001`..., `kind` = the type, polygon, area, anchor, chunk):

* a **ground patch** of `edits.json` (`ground_patches`, `GP-0001`...) is a **hand-drawn ground area** of exactly one ground
  type: its polygon outside the parcels is ground of that type, whatever the rules say (where two overlap the higher ID
  wins). Draw the patch in the editor's Ground tool, or with the MCP tool `create_ground_patch`. The editor paints it as a
  preview at once; the real ground (terrain colour, detail, trees) comes with the next full run (`make map`). A patch of a
  water type is no ground area but a body of water, see "Ground patches" below;
* then the types are tried in the order of `ground.types`, each takes what the ones above left of its source: `dike`
  (the parcels and clips of `detail.dike`), `water` and `forest` (the OSM natural/landuse polygons, with their own
  outlines), `allotments`, `orchard`, `meadow`, `village` (OSM `landuse=residential`: street sides, greens);
* what no type claims is the default, `field`.

`export` copies these areas to `parcels.json` `ground_areas` (`id`, `kind`, `area`, `polygon`) for the editor's ground map.

A type is a named bundle of terrain attributes, one entry of `ground.types`: its source (`osm` or `rule`, or none) and the
`attributes` it carries: terrain `colours`, detail `recipe` (field, forest, village, dike) with optional `detail`
settings laid over that recipe's section for this type only (a meadow is the field recipe without furrows and
headlands), `water` (see "Ground attributes"). A new type is one new entry. A type is terrain only: nothing a game system
reads (where players spawn, where wildlife lives) is a property of the ground, that is a zone (see "Zones"). There is no road ground: a road is the OSM ribbon on top of whatever lies under it.
Ground areas have no fence and no edits of their own, and their IDs are only stable while the inputs are. Trees are not
part of a type: they come from the orthophoto only (`docs/ortho.md`).

**Carving.** A big cadastral parcel can be a parcel only because a building stands on it (a field, the dike strip, public
land). `ground.carve` in `rules.yaml` lists such parcels: the parcel keeps its ID but becomes the building outline(s)
grown by `margin_m` (a slim yard, mitre corners, never over another parcel), and the rest of its land becomes ground like
any other. Today P-0073 (the dike strip, with a hut) and P-0754 (a 13 ha field with one building).

### Ground attributes (ADR 0015, 0022)

A ground type ticks the attributes it carries, under `attributes` of its entry. The attributes are a fixed catalog in
code (`ground/attributes.py`, mirrored in `godot/viewer/Edits/GroundTypeEdits.cs`); the editor groups them by relation:

| Group | Attribute | Value |
|---|---|---|
| Appearance | `colours` | 1-6 `#rrggbb` terrain shades |
| Appearance | `recipe` | `field`, `forest`, `village`, `dike`, `crop` (none: no detail) |
| Appearance | `detail` | settings laid over that recipe's `detail` section |
| Water | `water` | `true`: a body of water (`water.json`), no detail |

* **Every point has exactly one ground type.** A ground area has one `kind`, and the terrain, detail and water stages read
  that type. There are no stacks: what an area means in play is a separate overlay, the zones.
* A type with only `colours` (no recipe) is valid: it is the ground of a colour (no detail patches, no clutter). That is how
  a colour that no type has is drawn: as a ground type of your own, then a patch of it.
* A type with an OSM source (`osm`) or a `rule` has to carry an attribute: the land it takes has to look like
  something.

### Ground patches (`edits.json` `ground_patches`)

```json
"ground_patches": {"GP-0001": {"type": "meadow", "polygon": [[x, y], ...]},
                   "GP-0023": {"type": "water", "polygon": [[x, y], ...], "water_y": 186.4}}
```

`GP-` and 4 or more digits (`G-` is a property group, `P-` a parcel, `A-` a ground area). `type` is a ground type of
`rules.yaml` or of `ground_types`, not a deleted one. `polygon` is one ring in absolute S-JTSK metres (x east, y north), at
least 3 corners, no repeated closing point, no self-intersection. `water_y` is required when the type carries the `water`
attribute (the flat surface of the body of water, metres on the terrain datum) and refused otherwise.

* **Ground.** `ground.drawn_ground` turns the patches into ground areas of their type (the higher ID winning an overlap),
  before the rule types, never into a parcel. The terrain, detail, trees and everything else follow from the type.
* **Water.** A patch of a water type is no ground area. It makes a body of water exactly as a `water_y` shape does: the
  drawn outline is only a hint, the water lies wherever the terrain below the outline is under `water_y`
  (`water.wet_area`), and the body goes to `water.json` (swimming, the shooter's water surface; the body's `id`
  is the patch id, `GP-0023`, with `.1`, `.2` when the water has several pieces) with its natural bed (see "Water bodies"
  below; the optional `max_depth` of the patch, 0 to 20 m, scales it so the deepest point is that deep: `edits.json`, or `create_ground_patch`,
  `set_patch_water` and `set_patch_type` of the MCP server). Its colour is also baked into the chunk files as the node `patch_<ID>`
  (see "Ground patches (preview and water)").
* **Colours.** A patch has no colour of its own: it is in the first colour of its type (`patches.type_colour`).

### Zones (ADR 0022)

A **zone kind** says what a marked area means in play. The kinds are a fixed catalog in code (`zones.KINDS`; a kind needs
something in the pipeline or the game that reads it): `bigfoot` and `scud` (group event), `player_spawn` (group player), `cartel_drop` and `cartel_hit` (group cartel: the Drop points and Hit spots of docs/cartel.md), `cartel_heli` and `cartel_boat` (group cartel: a Heist's helicopter pads and boat moorings with their routes, runtime zones exported as outlines like the plantation: `cartel_heli` the pad, the helicopter on its mean point with its nose toward the middle of the first edge; `cartel_boat` a line, the first point the mooring with the bow toward the second and the rest the route to the map's edge, in the order drawn, so the outline must stay simple: the route must not cross the chord from its end back to the mooring), and one kind per
wild species (group wildlife: `deer`, `doe`, `stag`, `moose`, `boar`, `bear`, `fox`, `wolf`, `rabbit`, `raccoon`, `skunk`;
the ids of `godot/shooter/data/animals.json`, a test checks it). A **zone** is an area of one kind. A zone never changes the
terrain (a zone over a forest does not cut the forest), and zones overlap freely and add up. Zones come from two places that
are the same to the export:

* a **drawn zone**: `edits.json` `zones`, `{"Z-0001": {"kind": "bigfoot", "polygon": [[x, y], ...]}}`. `Z-` and 4 or more
  digits; `kind` is in the catalog; the polygon follows the rules of a patch polygon. Draw it in the editor's Zones tool or
  with `create_zone`.
* a **rule zone**: the kind's `where_ground` in `rules.yaml` `zones` lists ground types, and the kind applies wherever a
  ground area of one of them lies (deer wherever there is forest), nothing is drawn.

`rules.yaml` `zones` has one section per kind, the numbers and the rule zone of it:

```yaml
zones:
  bigfoot: {m2_per_point: 1000, min_points: 30, max_points: 300, building_clearance_m: 10, water_clearance_m: 5}
  player_spawn: {where_ground: [allotments, orchard, meadow, village, field]}
  stag: {m2_per_point: 20000, min_points: 5, max_points: 80, building_clearance_m: 15, water_clearance_m: 5, where_ground: [forest]}
```

Every kind but `player_spawn` has all five numbers and may have `where_ground` (a list of ground type names that exist: the
export refuses an unknown one); `player_spawn` has `where_ground` only. A kind that is in the catalog but has no section
does nothing (a drawn zone of it is warned about at the export); a key or kind that does not exist is an error of the
stage, never a silent skip (`zones.rules`).

* **Player spawn.** Players spawn inside `player_spawn` zones and nowhere else. The props stage (`props.make_spawns`,
  `spawns.json`) draws seeded points over the zones (the ground areas of the types of the rule zone, less its
  cuts, and the drawn zones: `zones.kind_areas`): one per `spawns.m2_per_point` of usable ground, rounded up, so no minimum and
  no maximum per zone; each inside the terrain and clear of buildings, trees and water. A point is `kind` `zone` with the id of its `zone`. A map with no
  `player_spawn` zone gets `kind` `random` points over the whole terrain, off the roads. A zone with no usable ground gets none
  (warning). `player_spawn` is not exported as points; roads are never spawn points.
* **`plantation`** becomes polygons for the server at the export (`zone_polygons.json`, see "Zone polygons" under Export).
* **Every other kind** becomes candidate points for the server at the export, see "Zone points (`zone_points.json`)" under
  Export. Ground areas are never cut by a zone.

### Ground type of one parcel (`edits.json` `parcels.<ID>.ground_type`)

`"parcels": {"P-1091": {"ground_type": "graveyard"}}`: the ground of that parcel looks like the ground type (of `rules.yaml` or
your own, see below; not water): its terrain colours (scene) and its detail `recipe` (detail; a type without one: no detail)
instead of the yard of its kind. Kind, layout, fence and other parcels are unchanged. `ground.with_parcel_types` lays the type
on the units, `ground.ground_kind` is the one lookup. A type a parcel names cannot be deleted. `make map-update` shows it; MCP
`set_parcel_ground_type`.

### Outline of one parcel (`edits.json` `parcels.<ID>.outline`)

`"parcels": {"P-1091": {"outline": [[x, y], ...]}}`: replaces the cadastral outline of that one parcel, a simple polygon in absolute
EPSG:5514 metres (like objects; no key: untouched). Its buildings must stay inside; otherwise the parcels stage warns and keeps the
cadastral outline. It is applied once, in the parcels stage (`parcel_outlines.apply_outlines`, after carving), so `parcels.json` and
everything built from it (fence, ground, detail, object clearance, parcel lookup) follow it. The registry and the building assignment
still use the cadastre. Where it overlaps an unedited parcel the edited one wins and the neighbour is cut back by the overlap; two
edited outlines that overlap are a validation error. The land the parcel gives up goes to the ground rules (default ground unless
another outline takes it). Edits are in absolute metres, the work files in local ones (minus `origin_epsg5514`). Needs `make parcels`,
then `make map-update` (terrain and roads do not read parcels); MCP `set_parcel_outline` (a sketch line spliced into the outline, or
the full points) and `reset_parcel_outline`.

### Ground types of your own (`edits.json` `ground_types`)

The types can be changed, added and removed without touching `rules.yaml`: `pipeline/config/edits.json` has an optional
top-level `ground_types`, `{"<name>": {...}}`, written only when there is something in it. An entry has the keys of a
`rules.yaml` type (`osm`, `attributes`; no `rule`, rule types stay in `rules.yaml`) or `"deleted": true`. In `attributes`
a value `null` removes that attribute of the type of the rules (the type stops carrying it):

```json
"ground_types": {
 "meadow": {"attributes": {"colours": ["#8cb86a", "#82ad62"], "detail": {"tone_per_ha": 6}, "recipe": null}},
 "dunes": {"attributes": {"colours": ["#d8c38a"], "recipe": "field"}, "osm": {"natural": ["sand"]}},
 "paint_e2b92c": {"attributes": {"colours": ["#e2b92c"]}},
 "orchard": {"deleted": true}
}
```

and a patch can then be of type `dunes`. `paint_e2b92c` is the ground type of a colour (no recipe), how a paint colour
that no type has is drawn.

* **Merge rule** (`ground.ground_types(rules_path, edits_path=None)`, `merge_ground_types`): the types of
  `rules.yaml` come first; a key of an edits entry overrides that key of the rules entry (the
  dicts `osm`, `attributes` and `detail` are merged key by key, deeper dicts the same way, lists and plain values are replaced, so
  `colours` is replaced whole and `osm: {natural: [...]}` replaces only the natural list); an attribute set to `null` is
  removed; a new name is a new type
  appended after the rules' types (so the rules' types take OSM land first); `deleted` removes a type of the rules.
  Without `edits_path` the result is `rules.yaml` alone. Every stage that reads the types passes the edits path (parcels
  for the OSM sources and `drawn_ground`, scene for the terrain colours, detail for the recipe and its `detail`
  settings, export for `water`). A patch names a type that no longer exists: the validator refuses the file, so a type
  cannot be deleted while a patch is of it (the MCP tool names the patches).
* **Validation** (`edits/schema.py`, mirrored by the editor): names are lower_snake (`^[a-z][a-z0-9_]{0,23}$`); `attributes`
  holds only catalog keys: `colours` 1 to 6 `#rrggbb`; `recipe` one of `field`, `forest`, `village`, `dike`; `detail` an
  object whose top-level keys exist in the shipped `detail.<recipe>` section (the recipe is the entry's own or the rules
  type's, one is needed); `water` `true`; `null` only to
  remove an attribute that the type of `rules.yaml` carries; `osm` an object `{landuse|natural: [text, ...]}`, and a
  type that takes OSM land has terrain attributes; `deleted` is `true` and alone, names a
  type of `rules.yaml`, and is refused for the default type and for a type with a `rule` (the dike, `detail.dike` needs
  it). Unknown keys are errors, also the keys of what a ground type no longer is (`spawns`, `event_spawn`, `wildlife`
  are not attributes) and the flat keys (`colours`, `recipe`, ... at the top of an entry).
* **Rebuild**: a type change needs the full map build (`make map`): the ground areas, terrain colours and detail are
  made by the parcels, scene and detail stages. Nothing is hot-reloaded. The editor, MCP and this document say so.
* **Export**: `parcels.json` `ground_types` lists every type of the rules (and then the user's own) with the rules'
  own entry (`rules`, in the shape of `rules.yaml`; `custom: true` instead for a user's type) and a summary at the build (`colour`, `colours`,
  `recipe`, `water`, `attributes` = the attribute keys carried, `source` = `osm`, `rule` or `drawn`); `ground_default` names the default type;
  `ground_recipes` holds the shipped settings of each recipe (without the clutter tables). The editor lays the current
  `edits.json` over `rules`, so its list is live between builds.
Edits of a parcel that is no longer kept are skipped with a warning by the props stage; putting the parcel in a group
with a building plot (by hand in `edits.json`, it is not shown in the editor) brings it back.

## Normalise and the parcel registry

`make normalise` reprojects the OSM data and cadastral parcels from WGS84 to EPSG:5514 and then
shifts them into a local metric frame whose origin is the centre of the `osm_way` (`centre.json`).
Axes stay those of EPSG:5514. It writes `data/village/work/normalised/{buildings,highways,landuse,parcels}.geojson`
(local metres, gitignored) and `data/village/work/frame.json`.

`make parcels` gives every cadastral parcel an own stable ID (`P-0001`, 4 digits, growing when
needed) and writes `data/village/work/parcels.json`: the parcels that are properties (ID, cadastral reference, polygon, area,
kind, anchor point, building ids, 250 m chunk index) and the ground areas (see "Parcels and ground areas"). It prints
a report (parcels by kind, ground areas by type, buildings assigned or unassigned, new and retired IDs). The
building-assignment and ground rules are documented in `pipeline/config/rules.yaml`. Every cadastral parcel keeps its
registry entry and ID, also the ones whose land is ground.

The committed registry `registry/parcels.registry.json` is the source of truth for IDs. Per
parcel it holds `id`, `cadastral_ref`, `previous_refs`, `status` (`active` or `retired`) and a
geometry signature in absolute EPSG:5514 metres (area, centroid and outline, used for matching;
it comes from the cadastre only). Each regeneration matches current parcels to it:

1. same cadastral reference keeps the ID;
2. otherwise an unmatched registry entry with outline IoU >= 0.5 keeps its ID (the parcel was
   renumbered; the old reference is appended to `previous_refs`);
3. otherwise the parcel is new and gets the next ID after the highest ever used, in cadastral
   reference order;
4. entries nobody claimed become `retired`. They are never deleted and their IDs never reused.

To regenerate after the cadastre or OSM changed: `make fetch` (or `fetch --force`), `make parcels`
(which needs `make normalise` first), then review and commit the `registry/` diff. Re-running
on unchanged input leaves the registry byte-identical. A run only retires entries whose outline
lies inside the outline it fetched (`area_polygon_epsg5514` in `frame.json`); entries outside are
left untouched. So the registry keeps every ID and numbers the new parcels after the highest one. Shrinking or moving the outline retires
only what is inside the new box and no longer in the cadastre (reversible: an entry whose exact
reference reappears is revived). Retiring or
reassigning IDs is a breaking change of the map artifact (see the versioning decision).

## Buildings: rules, meshes, export

`make rules` writes `data/village/work/buildings.json`, one entry per building assigned to a parcel
(buildings without a parcel are skipped and counted): ID, parcel ID, footprint (local metres,
holes allowed), `height_m` (to the roof top), `eave_height_m`, `roof_height_m`, `levels`, `roof`
(`flat`, `gabled`, `hipped`), `ridge_dir` (radians, pitched roofs only), `wall_colour` and
`roof_colour` (RGB). Precedence: `pipeline/config/overrides.yaml` > OSM tags (`height`,
`building:levels`, `roof:shape`, `roof:levels`) > building-type class > footprint-area class.
Thresholds, classes and colour palettes are in the `buildings` section of
`pipeline/config/rules.yaml`. A gabled or hipped ridge runs along the longest side of the
minimum rotated rectangle; if the footprint fills less than 80% of it the roof is flat. Colours
and the small random choices are drawn from a generator seeded with sha256 of
`<parcel id>:<building id>`, so reruns give identical output. The override file format (by parcel
ID or OSM id, any building field) is documented at the top of `overrides.yaml`.

## Terrain, roads, props, spawns

Settings for all of this are in the `terrain`, `roads`, `props` and `spawns` sections of
`pipeline/config/rules.yaml`.

**Terrain** (`make terrain`): `data/village/raw/dmr5.tif` (EPSG:3046) is resampled bilinearly into a
regular height grid in the local frame (default step about 4 m; chunk borders are grid lines, so
neighbouring chunks share border vertices). Terrain is meshed only where the DMR raster covers
all four corners of a grid cell; nothing is clamped or invented outside it, and chunks without
covered cells get no terrain. Heights are metres above the DMR datum, not rebased. The ground under
each building footprint (grown by a margin) is flattened to the footprint's mean elevation and
blends back to the natural height over a few metres; the building stands at that elevation.
Buildings outside coverage (their chunk files have no terrain) take the clamped height of the grid
edge; road ribbons, fences, trees and spawns outside coverage are dropped.

**Roads** (`make roads`): highways from `normalised/highways.geojson` (classes and widths in
`rules.yaml`; an OSM `width` or `lanes` tag wins) become graph nodes and edges in
`out/village/map/roads.json` (`nodes`: id, x, y; `edges`: id, osm_id, from, to, polyline, highway, width,
oneway, surface, length_m; local metres, x east, y north). Ribbon footprints are clipped against the
terrain triangles they cover, so every piece lies in a terrain triangle's plane lifted by `lift_m` (0.05 m)
and no terrain crease can poke through. A higher road class sits a hair above a lower one. Ribbons of
one class share a height, so where two of them differ in colour (asphalt and gravel at a node) the
overlap is split along the bisector of the two ways and each keeps its own side; a junction patch
only fills ground that no ribbon of its class or a higher one covers. Nothing at one height overlaps
a surface of another colour, so nothing z-fights.

**Props** (`make props`): low fences (thin boxes, 1.2 m) along the boundaries of building plots
and of gardens near buildings (seeded per parcel, a gate gap on road frontage, no fence through
buildings or the carriageway), and trees planted from the orthophoto detector only (`docs/ortho.md`; blocky box
canopy, or stacked pyramids in forests). Fences also get a simple closed-box
collision node. Trees are not in the chunk collision: `trees.json` (map 0.6.0, `files.trees`) lists every tree (position,
ground height, scale, rotation, broadleaf or conifer, chunk) and the game builds the solids from it (see below).
The forest floor also gets **ground objects** (fallen logs (not solid), stumps, boulders, root plates, anthills and burrows; `ground_objects.json`, map 0.13.0; see below).

**Fence types and the texture library.** A fence section with a `type` is drawn in that type's style
(`props/fencestyles.py`) instead of the plain box: `picket` and `wire` are one thin two-sided quad with a
cut-out texture that follows the ground, with wood or metal posts; `brick_red`, `brick_white`,
`stone_wall` and `concrete` are level-topped walls (they step on a slope) with a repeating texture, a
flat coping and matching piers. Columns stand at every section joint and run end; `column` swaps them
(`none`, `wood_post`, `metal_post`, `brick_red`, `brick_white`, `stone`, `concrete`), e.g. picket
between brick piers. The colour is a tint: picket, wire and concrete are near-white textures, so the
colour is their paint; brick and stone carry their own colours and default to white. Custom (hand-edited)
fences always have a type (default `picket`); rule fences stay plain boxes (the white `plain` texture of the library: a box that
shows its tint) unless `props.fences.type` is set in `rules.yaml`. The collision box of a thin section is unchanged. A wall type
(`brick_red`, `brick_white`, `stone_wall`, `concrete`) is not in the props at all: it is a house-destruction object (see Fence sections).

The textures are a library, not part of the fences: `assets/manifest.json` `textures` lists each one (id,
PNG under `assets/`, `tile_m` = the size of one repeat in metres, `alpha`, `tintable`, mean colour), and
`core/textures.py` gives any mesh builder `texture(id).uv(...)` and a `TexturedBuilder`. The PNGs are built by
`python3 -I scripts/art/make_textures.py` (standard library only, deterministic; `--sheet FILE` writes a contact
sheet). `uv run python -m drycastle_pipeline.preview.fences` (in `pipeline/`) draws every fence type from its
real meshes, UVs and textures into `out/textures/fences.png`, so the look can be checked without the game.

**Hand edits** (`pipeline/config/edits.json`, read by the props stage; `--edits PATH` overrides the
file): per-parcel fence mode (`auto`, `none`, `custom` with a type, column, height, colour and gaps),
property groups (`G-NNNN`: one fence around the members' combined outer edge), and placed objects (`pack:` or `prim:` assets, absolute S-JTSK metres).
Objects go to `work/objects.json`, and with them the `cables` (`C-NNNN`: `from` and `to`, the ids of two
placed utility poles) whose poles are both placed; an object may carry `z` (absolute metres above the DMR datum; omitted = on the terrain) and `solid: false` (no collision at all, for dressing over a climbable prim; `objects.json` has the key only when false); the game strings the wires (`docs/utility-poles.md`). `ground_patches` (`GP-NNNN`: `type`, `polygon` one ring of at
least 3 `[x, y]` absolute S-JTSK points, no self-intersection, `water_y` for a water type) are hand-drawn ground (see "Parcels and ground areas"); the props stage reads
only their water ones (no trees in a lake). `zones` (`Z-NNNN`: `kind`, `polygon`) are what an area means in play (see "Zones"); the props
stage reads the `player_spawn` ones. The format and validator are documented in
`pipeline/src/drycastle_pipeline/edits/schema.py`. Entries for parcels that no longer exist are skipped
with a warning; schema errors fail the stage.

**Spawns**: `out/village/map/spawns.json` has seeded points inside the `player_spawn` zones only (see "Zones"),
inside the terrain and clear of buildings, trees and water, with elevation, `kind` (`zone`, or `random` on a map without a
spawn zone), `zone` (the id of the zone) and chunk index. No road points.

**Street lamps** (`roads/lamps.py`, run by the props stage, `make props`): `data/village/work/lamps.json` holds
one point per lamp (`id` `L-NNNN`, `x`, `y`, `z` elevation, `source` `osm` or `rule`, `osm_id`,
`highway`, `parcel`, `chunk`). OSM `highway=street_lamp` and `man_made=street_lamp` nodes are in the
Overpass query and in `normalised/street_lamps.geojson`; each one is kept if it is inside the terrain
coverage and outside building footprints (grown by `lamps.building_clearance_m`). Where a rule road
(`lamps.rule_highways`) has no lamp within `lamps.min_lamp_distance_m`, rule lamps are placed every
`lamps.spacing_m` along each edge, alternating sides, `road_offset_m` beyond the carriageway edge.
Rule lamps also stay out of `lamps.avoid_kinds` parcels (house plots and gardens). Placement is
deterministic (no random numbers). Raw data fetched before street lamps were queried has no
lamp nodes: run `make fetch --force` once, then `make normalise` and `make props`. The export copies
`lamps.json` to `out/village/map/` (map version 0.5.0, `files.lamps`); lamps are not in the chunk GLBs. The game
builds the pole, the head and the light from primitives, so the viewer ignores them.

**Fence sections (`fences.json`).** The fences are baked into the chunk GLBs, but the game server also needs where they
stand (a gang member hops a pet's fence, `docs/events/thieves/05-pets.md`) and how they break (`docs/fence-destruction.md`). The props
stage writes the sections that `make_fences` produced (the same list as `fences` of `work/props.json`) to
`data/village/work/fences.json`; the export adds the keys only it can know (what each section is drawn as, its pieces, where its
collider is: map 0.16.0) and writes `out/village/map/fences.json` (`files.fences`; absent when the map has no props stage output, a stale
file is deleted; a loader that does not know it, or a map without it, means "no fence data"). Format, version 1 (keys are only
ever added):

```json
{"version": 1,
 "axes": "local metres, x east, y north, z up (elevation of the ground at each end); height_m above that",
 "fences": [{"parcel": "P-0412", "group": "G-0003", "a": [12.5, 40.0], "b": [16.25, 40.0], "za": 187.1, "zb": 187.2,
             "height_m": 1.2, "type": "picket", "kind": "picket", "thickness_m": 0.08, "colour": "#8a6a47",
             "pieces": 31, "piece_m": 0.125, "piece0_m": -0.01875, "uv": [25.0, 2.0, 0.004, 0.996],
             "collider": "parcel_P-0412_props_collision"}]}
```

One entry per section (a straight piece of at most `props.fences.section_m`, in parcel ID order, along each fence line in order):
`a` and `b` its two end points (`[x, y]`, local metres as `parcels.json`), `za` and `zb` the ground elevation under them (as
`terrain.json`), `height_m` the effective fence height (the section's own for a custom fence, else `props.fences.height_m`),
`type` the effective fence type (`picket`, `wire`, `brick_red`, ... as `edits/schema.py` `FENCE_TYPES`) or `null` for the plain box,
`parcel` the owning parcel and `group` the property group of that parcel (`G-NNNN`, as in `groups.json`) or `null`. A group's fence
is one line round the members' outer edge, each piece owned by its member parcel. The gate gap and the stretches
cleared of buildings and roads are simply missing: the sections are the fence that stands. `make parcel-run` (the editor's live
regeneration) does not rewrite it; run `make map-update` (or `make props export`) after changing fences.

Keys added by the export (`export/scene.py` `fence_docs`; the position of a section in the list is its **index**, the id the game uses
everywhere): `kind` (`plain` = no type, `picket`, `wire` or `wall`), `thickness_m` and `colour` (`#rrggbb`, the tint) as drawn. A *thin*
section (plain, picket, wire) breaks in equal strips, its **pieces**, and also has `pieces` (1 to 64), `piece_m` (their width,
metres along a to b), `piece0_m` (where piece 0 starts, metres from a, at most 0; piece j covers
`[piece0_m + j piece_m, piece0_m + (j + 1) piece_m]` clipped to the section), `uv` (`[u at a, u per metre, v at the bottom, v at the top]`:
the texture coordinates its panel is drawn with), `posts` (map 0.16.1, absent when none: the posts that break and fall with one of its pieces,
`[along_m from a, width_m, z0, z1, "#rrggbb"]` each; a joint's post is the first section's; they are drawn in the `plain` textured node
with that piece's TEXCOORD_1) and `collider` (the `props_collision` node that holds its box:
`parcel_<ID>_props_collision`, or `props_collision@<ix>,<iy>` for a fence with no parcel in chunk ix, iy; `null` when the
`props.fences.collision` rule is off, as it is in the village profile). A picket piece is one board: the run-continuous along that the
panel's u uses puts the piece edges in the middle of the gaps between boards (the first piece may start before a), a wire piece is
0.5 m and a plain one 0.25 m, uniform from a. A *wall* section (brick, stone, concrete types) has `grid`, its grid id in
`house_voxels.bin` (`fence_<index:05d>`).

The panel of a thin section carries the piece in the mesh too: every vertex of its textured node (`<base>_tex_picket`, `_wire` or
`_plain`) has TEXCOORD_1 = (index + 0.5, (metres from a along the section - `piece0_m`) / `piece_m`), so the floor of y is the piece
(`props/fencestyles.py` `section_docs` and `_Pieces`; `core/textures.py` `TexturedMesh.uv2`; `export_glb` renames trimesh's `_UV2`).
The posts of a thin section stay in the flat `props` node. A wall section is out of `props`, `props_tex_*` and `props_collision`: per
section a flat `house_fence_<index:05d>_visual` (caps, flat posts), `house_fence_<index:05d>_visual_tex_<texture>` and
`house_fence_<index:05d>_collision` (closed boxes of the wall and the piers it owns; always written, whatever the collision rule says),
in the chunk of its midpoint. A pier at a joint of connected walls belongs to the lowest index and is drawn and built once. Each
wall also has a voxel grid in `house_voxels.bin` (`props/fencewalls.py`: 0.25 m cells in the section's own frame, +x from a to b,
+y to its left, one cell thick at least, foundation under the ground line and in the lowest layer; materials 7 brick, 8 stone,
9 concrete).

**Satellite dishes** (`edits/dishes.py`, `scripts/models/make_satellite_dish_model.py`; the Thieves event steals them, `docs/events/thieves/06-dishes.md`).
The model is a generated household dish, 0.7 m across, on a short wall arm (`python3 -I scripts/models/make_satellite_dish_model.py`
rebuilds `assets/drycastle-generated-models/satellite_dish.glb`, byte for byte; the asset `pack:drycastle-generated-models/satellite_dish`
is in `assets/manifest.json`, category `other` so the editor's palette offers it, CC0 like the pack's other models). Its origin is the
point of the wall where the arm leaves it, glTF y up and **+X out of the wall**, so a placed dish's `rotation_deg` is the way its wall
faces (compass degrees, counter-clockwise from east, as for the wall connectors) and `z` the elevation of the origin; the dish reaches
0.4 m above it and 0.3 m below, 0.35 m to either side.

The placing is a street-at-a-time command like the house connectors (`docs/utility-poles.md`), into the hand edits so that a dish is a
placed object the editor can see, move and delete, and `objects.json` gets it with the others at the next `make map-update`:

```
cd pipeline
uv run python -m drycastle_pipeline.edits.dishes --dry-run      # what it would do (--chance P overrides dish_chance)
uv run python -m drycastle_pipeline.edits.dishes                # appends the dishes to config/edits.json
```

Each dwelling (`area:house`, `area:large`, `type:commercial`; not hand-modelled landmarks) has a chance `dishes.dish_chance` (rules.yaml,
default 0.4) of a dish, rolled from a generator seeded with `<building id>:dish`: the same houses every time, and raising the chance only adds
houses. The dish goes on the wall that faces the nearest road (the nearest wall whose outward normal looks at the road's nearest point), `drop_m`
(0.6-0.9, seeded) under the eave and never lower than `min_height_m` (2.0) above the ground, at a seeded place along the wall at least
`wall_end_m` (0.8) from the corners. It keeps clear of the wall openings the pipeline knows (the windows, doors and garage doors of
`config/buildings/<id>.yaml` that lie in its height band, with half a dish and 0.15 m beside them) and of any house connector within
`connector_clear_m` (0.9) of it along that wall. When no place is left on that wall (a wall too short, an eave under `min_eave_m`, openings and
connectors in the way) the house gets no dish; no other wall is tried. A house whose eave is below 2.6 m gets none.

Ids and hand edits: a new dish takes the next free `o-NNNN` of `edits.json` and is never renumbered, so ids stay the same across map rebuilds
(the dishes live in the edits file, not in the build). A rerun only adds: a house that has a dish within `owned_m` (1.5) of its footprint (the
nearest house to that dish) is left alone however far the dish was moved; a dish deleted by hand comes back at the next run (set `dish_chance: 0`
or do not rerun); existing objects are never touched. The game finds the dishes as the placed objects whose asset is the dish.

**Tree collision (`trees.json`).** Each tree is two or three solids, built in the game from the rule model numbers
(`model`: trunk width and height, canopy edge) and the tree's scale and rotation, with the same arithmetic as the drawn tree
(`propmeshes.tree_mesh`): the trunk box (solid, starts 0.2 m below the ground) and the crown (two stacked boxes for a broadleaf,
two stacked pyramids for a conifer). **Forest trees** (map 0.11.0: `"forest": 1` on the record, planted inside the detected canopy)
use the `model.forest` block instead (`props.trees.forest` in `rules.yaml`): a tapered trunk (a four-sided frustum, solid, `PartShape.Frustum`)
with a clear stem, and crown tiers (three stacked pyramids, or two boxes) from about 5 m up; all numbers are metres at scale 1 and
the same block feeds the drawn mesh and the solids. The baked forest trunk colour is `forest.trunk_colour` (close to the near pack bark, #350;
unset: the global `trunk_colour`, which standalone trees keep). Records without the flag, or a model without the block (maps before 0.11.0),
use the formula above. A forest tree's record also has `"stand"` (`pine`, `birch`, `mixed`, `thicket`, `swamp`) and, where true,
`"edge": true` (see `docs/ortho.md`, "Stands"); they pick the look only, the solids read neither, and a reader that ignores them
works (a record without them: `mixed`, not edge). Trunks are on the world layer, so players and cars stop at them. Crowns are shot-only
(layer 4): bullets, pellets, rockets, turret sight and the client's decals and tracers stop on them, while players and cars drive
through. The geometry is `ShooterCore/Colliders/PropColliders.cs`; the loader (`MapLoader.Trees.cs`) makes one static body per
chunk and role, not one per tree. The surface of a hit (wood or foliage) comes from the tree lookup
(`TreeColliders.GroupAt`). Trees that the viewer edits are not in the game's collision until the map is re-exported.

**Per-tree hiding (map 0.12.0, `"base_uv2": true` in `trees.json`'s `model`).** Every props mesh that has tree triangles carries a
second UV set (glTF `TEXCOORD_1`, Godot `UV2`, two floats per vertex): for a tree's vertices the tree's base point (`x`, `y` of its
record, on the ground at the trunk centre) as (POSITION x, POSITION z) of that mesh, i.e. `x` and `-y` of the map frame; for every
other vertex (fences) `export/gltf.py` `NO_TREE_UV2` (1e6, 1e6). So `MODEL_MATRIX * vec4(UV2.x, VERTEX.y, UV2.y, 1.0)` is the world point
of the tree base, and a vertex shader can drop a whole tree near the player with the chunk still one draw. Meshes without trees have
no UV2. It is written by `export.to_trimesh` from `Mesh.bases` (set by `propmeshes.trees_mesh`) and renamed from trimesh's
`_UV2` by `export/gltf.py` `export_glb`, which every GLB that may hold trees (chunks, `parcel-run --glb`) goes through.

**Ground objects (`ground_objects.json`, map 0.13.0, `files.ground_objects`; `props.ground_objects` in `rules.yaml`).** Objects
on the forest floor (all solid but the fallen logs, #359), placed by the props stage (`props/groundobjects.py`, seeded per 25 m cell of the canopy mask, so reruns are identical;
`props.json` carries them under `ground_objects`). Only on the canopy of the orthophoto that lies on ground of a `ground_kinds` type
(forest), never on a property (building plot or garden), nor within the clearances of a building, road or path, placed object or
water, never within `trunk_clearance_m` of a tree trunk, and never on another ground object. Densities are per hectare of canopy and
come in clumps (`clump`): `log` 8 (fallen trunk, 4-9 m long, 0.3-0.6 m thick, random heading, ends within 0.3 m in height so it lies
on the ground; 20 % have a `rootplate` at the thick end), `stump` 13 (0.4-0.8 m wide), `boulder` 5 (0.6-2.2 m, clumps of 1-4), about
two thirds to all of them placed after the clearances: the village (about 185 ha of forest ground) gets about 4,900 of these four kinds
(measured). Two more kinds come in a second pass over the cells (own random streams, so they moved none of the others, and not in
clumps): `anthill` (`per_ha` 2, a cone mound 0.8-1.6 m across and 0.53-1.07 m tall, beside a random tree of the stands `pine` and `mixed`
at 0.05-0.8 m from the trunk's clearance ring, never in swamp ground: `props.wet_zone` buffered by the swamp stand's reach, the same zone
as the trees and the wet floor of the ground detail) and `burrow` (`candidates_per_ha` 5, each kept with probability 0.15 plus 0.5 with
open ground within 12 m, plus 0.5 where the ground climbs at least 0.15 (rise over run, over 1.5 m): a low earth mound 1.0-1.6 m across
and 0.31-0.49 m tall whose dark hole faces downhill on a slope, else a random way); the village gets about 260 anthills and 200 burrows
(1.4 and 1.1 per ha, measured). Each is a
low-poly vertex-coloured mesh (`propmeshes.ground_object_mesh`: log = tapered 6-sided prism, 24 triangles; stump = 6-sided taper with
a lighter cut face, 18; boulder = squashed rock of three jittered hexagon rings seeded by its position, 30; root plate = octagonal
slab on edge about 2 m tall, 32; anthill = 8-sided cone of two bands and a peak, 24; burrow = 8-sided mound of a band and a flat peak
with two dark triangles for the hole, 26) baked into the same chunk-level `props` mesh as the trees, with the object's base point as UV2 exactly
as for trees (see above), so a shader can swap a detailed model in near the player.

The file: `{"format_version":1,"axes":<as trees.json>,"chunk_size_m":..,"model":{"base_uv2":true,"kinds":{<kind>:{...}}},"objects":[{"kind","x","y","z","rot","scale","len"(logs only),"chunk":[cx,cy]}]}`.
`z` is the ground height at (`x`, `y`); `rot` is the yaw as for trees, u = (cos rot, sin rot). A log lies along u with its midpoint
at (`x`, `y`), `len` metres long, thick end at -u, `scale` multiplying only its thickness (`model.kinds.log.diameter_m` at scale 1,
`top_ratio` of that at the far end); a root plate stands on edge with its flat faces across u (a log's plate stands at its -u end);
stump, boulder, root plate, anthill and burrow dimensions (`model.kinds`) are metres at scale 1 and multiply with `scale`
(`anthill`: `width_m` 1.2 across the base, `height_m` 0.8; `burrow`: `width_m` 1.3, `height_m` 0.4; a burrow's hole faces u). The model numbers feed the
mesh, the file and the solids alike. Collision (`ShooterCore/Colliders/GroundObjectColliders.cs`, wired by
`MapLoader.GroundObjects.cs`): one box per object on the world layer (solid for players, cars and shots), a little inside the mesh; a fallen log has none (#359: everything passes through it; its box, `GroundObjectColliders.LogBox`, only sizes the laid model);
one static body per chunk and surface (wood: logs, stumps, plates; rock: boulders; ground: anthills and burrows, a square frustum inside the cone, 0.55 and 0.7 of the height). Walking into a ledge up to `MoveConsts.StepHeight`
(0.3 m) lifts the player onto it, so a log thinner than about 0.35 m is stepped over; thicker logs, stumps and boulders are jumped onto
(jump peak about 1.1 m) and stood on (flat tops). The collider and the mesh are checked against each other by
`tests/props/test_ground_objects.py` and `GroundObjectColliderTests` through `fixtures/ground_object_parity.json`
(`UPDATE_GROUND_PARITY=1 uv run pytest tests/props/test_ground_objects.py` rewrites it).

## Ground patches (preview and water)

A ground patch (`ground_patches` in `edits.json`, see "Ground patches" under "Parcels and ground areas") is ground of one type in
the baked map: the parcels stage makes a ground area of it, so the terrain colour, the detail and the trees follow on the next
full run and the chunk files hold no node for it. Two things come from the patch outside of that, both built by
`ground/patches.py` with the same code (so what is hot-reloaded equals what is baked):

* **Preview** (hot reload). Before the full run the editor shows a patch as one flat mesh in the first colour of its type: the polygon is cut along the terrain mesh
  triangles and, where it covers a road ribbon, along the ribbon outline, so the edges are exact and
  the surface follows the ground; every vertex is lifted `ground_patches.lift_m` (0.03 m) above the terrain,
  or `ground_patches.road_lift_m` (0.02 m) above the ribbon's own height on a road. Buildings, trees,
  fences and objects are not considered. Where patches overlap the higher ID wins: its polygon is
  subtracted from the lower patches before draping, so nothing z-fights. Parts outside the terrain
  coverage are cut off; a patch wholly outside it is skipped with a warning (`export: warning:
  ...`), as is any patch when there is no terrain or when its type does not exist. Schema errors fail the run like every other edit.
  The preview has no collision and changes only colour.
* **Water.** A patch of a water type is painted on the bed: the type's colour draped on the carved terrain (a hair above it, like any
  patch) over the terrain below `water_y`. There is no flat mesh at the surface: the shooter draws the water itself from the depth grid in
  `water.json`, and a second flat mesh just under it would only show through the shallows. It is baked into the chunk files as the node
  `patch_<ID>` in each chunk it touches (a patch over a chunk border is several nodes with the same name, one per chunk
  file) and the manifest lists `patches` in `generated`. Only water patches are baked as nodes.

The export also writes `patches.json` (`files.patches`; only when there is a patch of a ground type that has terrain under it): `{"version": 1, "patches": [{"id": "GP-0001",
"type": "forest", "colour": [r, g, b], "ring": [[x, y], ...]}]}` in ID order (the higher ID is drawn last), local metres,
the first colour of the type, for the minimap. Patches of a water type are not in it (they are in `water.json`).

`make patch-run [PATCH=GP-0001] [GLB=file.glb] [OUT=file.json]` (`python -m drycastle_pipeline
patch-run --patch ID ... --glb PATH`, none named = all) writes a GLB with one `patch_<ID>` node
per patch (the whole patch, in the chunk files' frame) and prints a JSON summary: `patches` (with
a mesh), `replaces` (the baked `patch_<ID>` nodes to hide: the named IDs, even deleted ones, plus
every patch overlapping a named one, whose visible area changes with it), `triangles`, `glb`,
`timing_s`. Like `parcel-run --glb` it is for hot reload; nothing under `data/` is written. Zones have no paint and no hot reload.

## Detail (ground patches and clutter)

`make detail` (stage `detail`, in `run-all` between `props` and `export`) gives the
ground something to look at. It reads `parcels`, `buildings`, `roads`, `terrain`, `props` (fences, trees), `objects`
and `edits.json` (water patches), and writes `data/village/work/detail.json` (clutter), `detail_patches.npy` and
`detail_patches.json` (patch triangles). `python -m drycastle_pipeline detail --work DIR` reads and writes another work
directory (experiments). The module is `detail/detail.py`; every number is in the `detail` section of `rules.yaml`.

**Area types and recipes.** `detail.area_type(parcel)` says what kind of ground a parcel is and so which recipe runs:
`garden` (parcel kinds `garden` and `building_plot`), `field` (`other`), `forest`, `road`, or nothing (`water`). It is
the only place that reads the parcel kind; a real area-type layer replaces that one function. Parcels are clipped to
the terrain coverage first (a parcel part under 4 m2 gets nothing). Water bodies, buildings, fences, trunks, placed
objects (a circle of half the footprint diagonal plus 0.3 m), water patches and the carriageway are kept free.

* **garden**: lawn pieces on tan plots (a green garden gets tones of its own terrain colour instead; `lawn.terrain_mix`),
  worn dirt along walls and in the open, a gravel path from the gate (the middle of the longest unfenced stretch of the
  boundary within 4 m of the carriageway) to the nearest building, up to two vegetable beds (`soil_rows` texture, turned
  with the main building), leaf litter under trees; tufts (thick along walls and fences), flower clusters, pebbles on
  gravel, dirt and along walls, a few rocks, fallen leaves and sticks under trees.
* **field** (meadow, field): large tone patches, dirt headlands along the boundary, a furrowed strip in some of the larger
  parcels, sparse tufts, pebbles, rocks and flowers that thicken towards the edge and on the tones. A `meadow` ground area within
  reach of water also gets the swamp floor below (none on the village map: no meadow lies that near water).
* **forest**: a brown floor (the forest ground colour is the litter tone), leaf litter under many trees, **moss** (`moss`
  layer, `moss` texture: bright green irregular blobs at the tree feet and over the open floor, thick in the clumps of a
  position-only noise and thin in the gaps, none within `edge_m` of the carriageway and the verges), dark and light
  forest-floor tones, a few bare spots; leaves, sticks, rocks, pebbles, tufts and ferns in groups round the trunks. The
  numbers are `detail.forest` (`litter`, `moss`); `detail._moss` and the Rust `field::moss` read the same ones. A forest
  costs about 1150 patch triangles per ha with these (about 360 before), so the forest patches of the village stay below 150k.
  **Mud and puddles** (`mud` and `puddle` layers and textures, `detail.forest.mud`): a few patches per hectare outside the swamp, in damp spots
  (a noise like moss's, likelier in hollows) and along the edge of the paths and tracks (`track`, `path`, `footway`; one candidate about every
  30 m of the way, from the way's own stream so every tile and parcel it crosses agrees), a small puddle in the middle of a third of them.
* **swamp ground** (`detail.swamp`; `detail/swamp.py` `swamp_zone`; Rust `wet.rs`): ground of the types `swamp.kinds` (`forest`, `meadow`)
  within the reach of water gets a wet floor in place of the plain one. The zone is `props.wet_zone` (water ground areas and the wet
  areas of water patches) buffered by `props.trees.stands.swamp.reach_m` (25 m), the very zone the swamp stand of trees stands in, so trees and ground
  agree; the Rust engine is handed it as `swamp` in its input. Inside it: a dark wet-earth base (`swamp` layer, flat colours `swamp.colours`,
  under every other layer), large `mud` patches (about 10 per ha of zone before merging, 6-11 m radius), small `puddle` patches (flat dark standing
  water, 0.5-2 m radius, likelier in terrain hollows: `detail.hollow`, the mean height on a 6 m ring minus the point's own, over 0.25 m), moss
  as in any forest, the forest tone patches gone, and leaf litter and bare spots cut to `litter_factor` and `dirt_factor` (0.15). Patches only: no water body, no
  gameplay. The meadow raster marks its cells with mix 9 (`swamp`, see below). Mud and puddle sizes are the triangle budget: patches are cut at the 4 m terrain
  cells, so few large blobs cost much less than many small ones (a first try with 2-4.5 m mud every 8 m cost 2.5 times as many). Measured with the Rust
  engine on the village map and its `edits.json` (no meadow within reach, 17.6 ha of forest in the zone, 11 ha of it the base): forest patch triangles 343k
  before, 399k after (+56k: mud and puddles of the plain forest +17k, the swamp floor +39k; of the 399k the swamp base is 42k, mud 26k, puddle 6k).
* **village** (village ground: street sides, greens): grass on all of it beside the OSM carriageway, tufts along the asphalt edge,
  pebbles on the gravel.
* **dike** (`detail/dike.py`, `detail.dike` in `rules.yaml`; the parcels P-0073, P-0821 and P-0805 are the earth dike north of the village, whatever
  their kind, and `clips` names parcels of which only a part is: the ground of P-1057 and of P-1021 within 12 m of the dike's road, of P-1059 within 12 m of its track, and of P-0847 and
  P-0089 within 12 m of the service road that continues it north through H to G, the rest keeps its recipe): the parcel is the dike and the terrain already holds the hill, so nothing is shaped here. The road edges inside it get a
  gravel track (patch layer `dike_track`, texture `dike_gravel`: fine light pebbles with hard-edged grass patches, 8 m x 4 m tile)
  about 2.2 m wide (`track_width_m` 1.5 plus its edges wandering by up to 0.7 m each side, with loose pebbles beyond), narrower than
  the 3 m carriageway: the track lies 8 cm above the terrain, the lawn 7 cm and so covers the rest of the road ribbon (5 to 6 cm)
  inside the parcel (except the ways of `dike.not_track`, regular roads beside the dike's own such as `w825143074` in P-0821: no track and no lawn over them), and the track is laid in pieces of 8 m, each along its own stretch and start so the patches do not repeat in rows; the rest is flat
  green lawn; the thick grass is clutter (`tall_grass`, `tuft`, `straw`, about 1 per m2, taller the further from the track) plus tufts
  on the track's grass patches (found by reading the texture). Only the part of a parcel inside the terrain coverage gets detail (of
  P-0821's 73,000 m2 about 7,700 m2). About 30k patch triangles and 14k instances for the three parcels; `tall_grass` reaches its
  per-chunk cap in the busiest chunk of P-0073.
* **crop** (`detail/crop.py`, `detail.crop` in `rules.yaml`; ground types `crop_grain`, `crop_corn`, `crop_potato`, `crop_rapeseed`,
  `crop_bare`, drawn only: a ground patch of one of them is a crop field, painted in the Ground tool; an unpainted field keeps
  `field`; the five types hold their own numbers as `detail` over `detail.crop`). Cosmetic: nothing collides or blocks a bullet.
  Ground: flat tones, or a texture turned along the rows (potato `soil_rows`, bare `dirt`). **Border zone**: `edge.feather_m`
  (6 to 10 m by type) wide, `steps` (10) noise-dithered bands (each band edge wanders by `wobble_m`, about a band width, so
  the bands interleave) from the colour of the ground that really lies outside to the field's own. The neighbour colour is
  probed every `sample_m` just outside the edge (`ParcelContext.ground_around`: other ground units by their terrain colour,
  road = `neighbour.road`, water = `neighbour.water`, else `surround`; a cut of a building or fence = no blend), averaged
  over a few probes and grouped by colour; a band is cut into one piece per group (Voronoi cells of the probes). **Row-format
  limit**: a patch row has one flat colour per triangle, no vertex colours, so the gradient is many small bands (about 10 x
  perimeter, about 5k triangles for a 100 x 60 m field with two neighbours), not a smooth ramp. Mode `wall` (corn) only makes the plants stop dead at
  `plant_margin_m`; its ground blends too. Plants thin out over `plant_falloff_m` (same zone) with a ragged edge
  (`plant_jitter_m`). Headland: dirt blobs along the edge, kept in part. Baulk: where two crop patches adjoin (edges within
  0.3 m for 3 m or more) a noise-edged 0.5 to 1 m strip of grass (some dirt), each patch takes its half from the same
  position-based width; their grounds blend towards each other over `baulk.blend_m` (3 m), no headland or plant on it.
  **Per-patch variety** (all seeded by the patch id, probabilities and sizes in `detail.crop`; colour and height jitter only,
  no growth stages or seasons): `variety` (ground tone +-7%, plant height +-15%, row direction: long side, a quarter turn or
  any angle, jittered; the stripe, tracks and textures follow it); `tramlines` (grain and rapeseed: pairs of dark tracks every
  18 to 24 m, no plants, and dirt strips along 1 or 2 edges); `stubble` (grain: 22% of the patches over 900 m2, a noise-edged
  straw stripe along the rows covering 25 to 50%, no plants, darker lanes, round or square bales lined on it, its border
  zone ends in straw); `fallow` (15% of the patches except bare: grass-green ground with tone blobs and wildflower-coloured
  dabs, no crop plants, at most 2 artifacts; the patch keeps its type); `spots` (noise-placed wet = darker, bare soil, weed
  clumps = green with plants a third as tall); artifacts: each patch deals 1 or 2 of its crop's kinds (`kinds_per_patch`) plus
  a chance of the type's `extras` (a lone tractor, a sprinkler line, a hay stack). Plants are instances in rows along
  the row direction (or a jittered grid), ~4 to 10 per 100 m2; artifacts are 2 to 10 per patch (`per_ha` 4, none under 400 m2),
  clustered at a corner, on the edge or alone inside, plants keep `radius_m` clear. No new instance kinds.
  The Rust engine runs the recipe too (`rust/detail/src/crop.rs`, in parallel with the others, written after them in
  `detail_patches.npy`, and writes `detail_crops.json`): a line-by-line port of `detail/crop.py` that reproduces Python's
  `random.Random`, numpy's generator (PCG64) and `round` bit for bit, so for the same inputs it gives the same instances as
  the Python twin (`tests/detail/test_crop_rust.py`). The patches are draped in 24-cell tiles on all cores. `detail/crop.py` stays as the twin
  of the tools (`parcel-run`, `patch-run`, previews) and the reference of the tests: change one, change the other.
  **`crops.json`** (map `files.crops`, copied from `detail_crops.json`; only maps with a crop patch): `{format_version: 1, axes,
  chunk_size_m, kinds: {kind: {role: plant|artifact, asset | synty: [Farm pack prefab names, full `SM_...`]}}, chunks: {"ix_iy": {kind: [[x, y, z, rot,
  scale], ...]}}}`, rows as in `clutter.json` without a tint; at most `detail.crop_kinds.<kind>.cap` per kind and chunk (a hash drops
  the rest). Kinds: plants `crop_wheat`, `crop_corn`, `crop_potato`, `crop_rapeseed` (asset
  `pack:drycastle-generated-models/rapeseed_plant`); artifacts `art_hay_bale_round`, `art_hay_bale_square`, `art_hay_pile`,
  `art_baler`, `art_scarecrow`, `art_tractor`, `art_potato_box`, `art_sign_potatoes`, `art_grain_bag`, `art_sprinkler`, `art_tyre`,
  `art_plough`, `art_wood_stack`. Patch layers: `crop_baulk`, `crop_baulk_dirt`, `crop_headland`, `crop_dirtstrip`, `crop_tramline`, `crop_edge_<k>`,
  `crop_stubble_rows`, `crop_stubble`, `crop_wet`, `crop_bare_spot`, `crop_weeds`, `crop_flower_<i>`, `crop_tone`, `crop_tone_<i>`, `crop_ground`, `crop_rows`. The game side (models, placeholders, culling): `docs/viewer-and-editor.md` "Crop fields".
* **verges**: along `track`, `residential`, `tertiary` and `service` roads, gravel and dirt blobs lie at the carriageway
  edge (built once per road edge, every parcel they touch takes its share), whatever the parcel kind.

**Patches** are polygons draped on the terrain (`patches.drape`, so they follow the ground triangle by triangle) 2 cm
above it. The layers (`detail.PATCH_PRIORITY`: dike track, gravel, verge gravel, bed, furrow, dirt, verge dirt, puddle, mud, moss, litter, two tones,
swamp, lawn) are clipped in that order, so no two patches overlap and nothing z-fights; parcels are disjoint. Water
patches (3 cm) are cut out of every patch. Patches go into the chunk GLBs as the node `detail_patches` (flat colours)
and one `detail_patches_tex_<texture id>` node per texture (`dirt`, `gravel`, `leaf_litter`, `moss`, `mud`, `puddle`, `soil_rows`, and `dike_gravel` on the dike), at most eight per
chunk; they have no collision. A patch triangle belongs to the chunk of its centroid (terrain cells never cross a chunk
border). The village had about 143k patch triangles (garden 76k, forest 33k, field 24k, road 10k) before the moss and denser litter of the forest (about 105k, estimated from the per-ha cost above), which is why the shapes
are few, soft and simplified: the terrain mesh cuts every patch at its 4 m cells, so area costs about 0.125 triangles
per m2 before any outline.

**Clutter** is instances, not triangles. `clutter.json` (map 0.8.0, `files.clutter`; the export copies
`work/detail.json`):

```
{"format_version": 1, "axes": "map metres: x east, y north, z up (ground height at the instance)",
 "chunk_size_m": 250,
 "kinds": {"tuft": {"mesh": "tuft"}, "flower": {...}, "pebble": {...}, "rock": {...}, "leaf": {...}, "stick": {...}, "fern": {...}},
 "chunks": {"<ix>_<iy>": {"<kind>": [[x, y, z, rot_rad, scale], ...]}}}
```

Coordinates have two decimals; `rot` turns about the vertical axis counter-clockwise seen from above; `scale` is uniform;
`z` is the terrain height at (x, y) (instances are only placed on meshed terrain cells). Chunks and kinds are sorted,
a chunk with no instance is absent. `clutter_meshes.glb` (`files.clutter_meshes`) has one node per kind named as the kind
(`mesh`), flat vertex colours (a few shades of the kind's palette across its faces; an instance varies only in turn and
scale), glTF axes (Y up), at scale 1 in metres, standing on y = 0 with its middle on the origin, 4 to 27 triangles
(`detail/meshes.py`: tuft 15, flower 27, pebble 8, rock 18, leaf 4, stick 12, fern 18). The game draws them as one
instanced mesh per chunk and kind, near the player only; the headless server ignores them. Nothing in the clutter is solid.

Budgets: `detail.caps_per_chunk` caps each kind per chunk (surplus instances go by a hash of the position, so the result is
stable); the village has about 139k instances (`clutter.json` 4.6 MB: tuft 89k, pebble 17k, leaf 11k, flower 8k, fern 8k,
rock 2.4k, stick 2.2k) and the stage takes about 1 to 1.5 minutes. The densities are low in fields and forest on
purpose: a stored instance costs about 33 bytes, so the open land (150 ha) cannot hold meadow-like density; if that is
wanted, generate it near the player at run time from a per-chunk density instead.

All random choices are seeded with `sha256("<parcel id>:detail:<purpose>")` (verges with the road edge ID), so reruns give
identical files. `uv run python -m drycastle_pipeline.preview.detail --parcel P-0637 ` draws the detail of
one parcel top-down from the real patches, textures and clutter meshes into `out/detail/`.

**Floor plans** of hollow houses (`buildings/floorplan.py`, ADR 0041) are worked out by the same module (`floorplan.rs`,
`floor_plans`), so `export` needs it built too. Whole flow: [interiors.md](interiors.md).

**Rust engine** (`pipeline/rust/detail`, `detail/rust.py`). The Python stage takes about 250 s on the full village map (the big
ground areas and the dike are most of it: shapely calls on 100 ha polygons, a Poisson-disc sweep and point-in-polygon tests per
point); the Rust engine does the same work on all cores in 7 to 8 s (26 s on one core) and writes the same four files
(`detail.json`, `detail_patches.npy`, `detail_patches.json`, `detail_meadow.bin`, formats unchanged, so the export and the game read
them as before). The stage runs on the Rust engine only; the Python build (`detail.run_reference`, `detail/build.py` `build_village_detail`) stays as the
reference the tests compare it with and is not a stage.

The same module also does three jobs for other stages. Each has its Python reference, gives the reference's output (not merely an
equivalent one: the tests compare them value by value) and is used when the module is installed, else the Python runs:

| Entry | Stage | What | Python reference | Village |
|---|---|---|---|---|
| `detect_trees` (`trees.rs`) | ortho-trees | the per-strip tree detection, strips in parallel (see docs/ortho.md) | `ortho_trees.detect_strip` | 78 s -> 4 s |
| `plant_trees` (`planting.rs`) | props | the canopy planting: Python's `random.Random` and float floor division reproduced, one thread (the planting is one sequence) | `props.ortho_trees` | 5 s -> 1.3 s |
| `drape_roads` (`roads.rs`) | export | road ribbons and junction patches on the terrain, one footprint per task; a footprint with a hole or a piece of over 80 vertices is handed back to Python | `roads.drape_mesh` | 4.5 s -> 0.6 s |

A change to one of these algorithms goes into both sides; the parity tests (`test_the_rust_engine_*`) fail otherwise.

The rest of the speed of props and export is plain Python (#403): the object -> parcel lookup and the terrain colours use a spatial
index with prepared polygons, the spawn stage unions only the tree discs near a zone, `Terrain.surface` has a plain-float path for a
few points, the forest-edge distance comes from one precomputed mask (`props.EdgeField`), `detail.patches_by_chunk` groups by one sort.

* **Build**: `make detail-rust-build` (maturin into the pipeline venv; needs `cargo` and the GEOS development files, e.g. `sudo dnf install geos-devel`
  on Fedora or `libgeos-dev` on Debian, because the `geos` crate links the system GEOS, the library shapely wraps; the build is a
  plain Rust build of about 15 s. Use a GEOS of the same version as shapely's (`python -c "import shapely; print(shapely.geos_version)"`)
  so the geometry matches the Python stage; the module needs the system `libgeos` at run time too). `make setup` (`uv sync`) removes the module again: build it again after.
  `cd pipeline/rust/detail && cargo test --release` runs the Rust tests.
* **Running it**: `make detail` and `make map` build the engine first (`detail-rust-build`), then run it; a stage run without the
  module fails with the build hint. There is no engine switch. `DRYCASTLE_DETAIL_THREADS=N` caps the
  threads (default: all cores); the result is byte-identical for any thread count. Nothing in `rules.yaml` changed: the engine reads the
  same `detail` section (including the `ground.types.<type>.detail` overlays, which Python lays over the recipe section and hands over).
* **What runs where**: Python reads the work files, the rules, the ground types and `edits.json` (water patches, placed-object clearance
  discs, terrain colours, recipes per unit) and passes them as one JSON document plus the terrain arrays; Rust does everything per
  unit: parts and dike clips, free ground, the five recipes (garden, field, forest, village, dike), the patch layers, draping,
  clutter, the meadow raster, the per-chunk caps, and the files. Ported completely; nothing falls back to Python.
* **Parallelism and determinism**: every unit (part of a unit) is a work item on a rayon pool; field and forest parts wider than 160 m
  (the 534 ha field, the big forests, the meadows) are cut into 125 m tiles that are items of their own, so no single area dominates.
  Every random choice is a pure function of its key: `sha256("<id>:detail:<purpose>")` seeds a unit's streams, and everything laid
  out on a lattice (tone blobs, clutter candidates, bare spots) draws from one small stream per lattice cell, derived from the seed and
  the cell, so neighbouring tiles agree along their seam and the result does not depend on how the area was cut. A tile is computed on
  its box plus a halo and its patches are clipped to the exact box (patches of different tiles never overlap); the items are merged
  in (unit id, part, tile) order, and the clutter caps use the same position hash as the Python stage.
* **Not identical to Python**: the draws are not numpy's, so individual instances and blobs differ; densities, counts and layers match
  to within a few percent on the whole map (see the report of the change). Two deliberate differences: the furrow strip of a big field is
  decided once per unit (as in Python) from the unit outline, not from the free ground, and a forest's bare spots follow the rules'
  `per_ha` through a keep share that reproduces what the Python rejection sampling gives a ragged outline.

**Limits.** `parcel-run` (editor hot reload) does not regenerate detail: patches live in chunk-level nodes, not in a
parcel's `props` node, so after editing a parcel's fence, trees or objects run `make props detail export` to move the detail
with them. The stage reads the props work file, so it must run after `make props`. Per-parcel opt-out of detail is not
implemented.

**Meadow (`meadow.bin`, map 0.9.0).** Field and forest parcels keep no dense clutter in `clutter.json` (only `detail.meadow.stored_kinds`, rocks). Instead the stage writes
a density raster, `work/detail_meadow.bin`, copied by the export as `meadow.bin` (`files.meadow`): 2 m cells, one byte per cell per layer (tuft, flower, fern, pebble,
litter, floor), zlib per chunk, about 0.7 MB for the village. A cell is on it only where its whole jitter window is free ground (no buildings, fences, roads, objects, water patches)
and the density comes from the recipe's zones (edge band, tone and litter patches, bare soil, near a trunk) times smooth noise; see `detail.meadow` in `rules.yaml` and
`detail/meadow.py`. The game generates the instances at run time (`docs/viewer-and-editor.md`, "Meadow clutter"). `preview/detail.py --meadow` draws what the game would generate for a parcel.

**Clumps and species zones (map 0.10.0, `meadow.bin` format 3).** Growth is patchy, not even: the meadow density is multiplied by a smoothed noise per layer
(`detail.meadow.layers.<name>.clump`, patches of `clump_m`), and the stored garden tufts likewise (`detail.clutter.tuft.clump`); where nobody mows (within `unmown_m` of a fence, a wall or a
building edge) tufts are thicker (`unmown`). The raster has a *zone byte* per cell: the high nibble is a *mix* (index into `detail.meadow.mixes`: a patch where one species dominates, a
Voronoi patch of `patch_m` ≈ 3 to 8 m, picked from the shares in `zones.<type>.mixes`; the wet mix beside water, the unmown mix where nobody mows, the dry mix in half of a field's edge band, the swamp mix (9) on swamp ground, over all the others), the low
nibble the dryness (8 = neutral, 10 tint units a step: drier at a field's edge, fresher beside water, fences and in swamp). Mix 9, `swamp` (`zones.<type>.swamp_mix`): `tuft` is reed and tall grass,
`flower` buttercups, `litter` leaves and sticks, `floor` moss mounds and branches (the fern and pebble layers keep their kinds). A puddle patch gets nothing at all. A layer lists per mix which kinds it makes and in what shares (a mix names only the
layers it changes), so a patch of one mix grows one set of species with strays. `meadow.bin` has a kind table (with the tint flag), the mixes per layer, and the zone plane after the layers (format in `detail/meadow.py`).

**Kinds (map 0.10.0).** `clutter_meshes.glb` has 28 tiny meshes (3 to 30 triangles, `detail/meshes.py`; `preview/detail.py --kinds` draws a contact sheet to `out/detail/kinds.png`):
the grasses `tuft`, `tall_grass` (seed heads), `straw` (dry), `clover` (a flat mat), `reed` (by water) and `weed` (broad leaves), `fern`; the wild flowers `dandelion` (with a puffball), `daisy`,
`red_clover`, `buttercup`, `poppy`, `cornflower`, `yarrow` (umbels); the garden flowers `tulip`, `sunflower`, `spike` (hollyhock or lupin) and `rose` (a bush); and `pebble`, `rock`, `leaf`, `stick`
(and `flower`, the first mixed-flower mesh, which no recipe uses any more); and the forest floor (`detail_meshes.FOREST_FLOOR`, meadow only): `mushroom` (three caps), `branch` (1.3 m, lying, 30 triangles), `moss_mound` (0.7 m dome), `fern_big` (0.7 m, tintable) and `stump_small` (0.27 m, rotten). Wild and garden flowers are kept apart: a yard (garden and plot recipes) grows garden flowers only, in one-species clusters
(`detail.clutter.flower.species`), tall grass beside fences and walls (`tuft.unmown_m`, `tall`) and some clover in the lawn; meadows, fields and road verges grow the wild ones. The meadow's
species mixes (`detail.meadow.mixes`) name which grass profile and which dominant flower each patch has: `meadow` (short grass and clover, daisies), `tall meadow` (red clover, yarrow), `dry` (straw, poppies and
cornflowers), `weedy` (dandelions), `wet` (reed, buttercups), `woodland`, three flower meadows (cornflower, yarrow, dandelion), and `swamp` (reed, tall grass). Tintable kinds are `detail_meshes.TINTABLE` (the grasses and ferns).

**Forest floor (#287).** The forest's mix (5, `woodland`) gives the `litter` layer toadstools and rotten stumps next to leaves and sticks (`mixes[5].litter`), and a new layer `floor` makes moss mounds, fallen branches and big ferns
(clump 0.7, `max_per_cell` 2). Densities are in `detail.meadow.forest` (`floor`: interior 0.09, tree 0.35, litter 0.3, tone 0.12; `litter` interior 0.14 became 0.10 to make room): plain forest floor is about 21 pieces per 100 m2
(tuft 6.0, fern 1.9, pebble 1.5, litter 7.5, floor 4.5; a cell is 4 m2), more at trunks and on leaf litter, where toadstools (litter 0.6) and moss mounds gather. A test keeps the total in 15 to 25. The game draws them to 60 m (`floor`) and 40 m (`litter`),
and not at all where the pack's undergrowth is on (`docs/viewer-and-editor.md`, "Meadow clutter"). No format change; `meadow.bin` just has more kinds and one more layer.

**Tint (map 0.10.0).** Tufts and ferns (`detail_meshes.TINTABLE`) carry a tint byte: the sixth number of their `clutter.json` entry, and in `meadow.bin` (format 2) a flag per layer.
128 is the mesh as drawn, lower is fresher (darker, greener), higher drier. The byte is the smooth tone noise over the ground (2 m cells, 12-cell lattice, `meadow.tone_noise`) plus a per-instance
jitter, shifted by a bias: stored grass within `detail.tint.fresh_m` of a fence or wall is fresher (`fence_fresh`), grass within `water_m` of water likewise (`water_fresh`).
The game computes the same bytes for generated instances (`MeadowGen.Tint`, pinned by the shared fixture).

## Export

`make export` writes `out/village/map/` (gitignored): `chunks/chunk_<ix>_<iy>.glb` (one glTF per 250 m
chunk that has buildings or terrain; Y-up axes x east, y up, z -north, metres, one frame for all
chunks). Nodes per chunk glb: `parcel_<ID>_visual` (walls and roof, flat per-face vertex colours)
and `parcel_<ID>_collision` (extruded footprint, no roof detail) for each parcel with buildings;
`terrain` and `terrain_collision` (flat colours by parcel kind; collision is every second grid
vertex); `roads`; `parcel_<ID>_props` (that parcel's fences and trees, visual) and
`parcel_<ID>_props_collision` (its fences, only when the `fences.collision` rule is on), placed in
the parcel's own chunk file so one parcel's props can be swapped at runtime; `parcel_<ID>_props_tex_<texture id>`
(map 0.7.0: the textured parts of that parcel's fences, one node per texture, `plain` being the box of an untyped fence since 0.16.0; it carries UVs and a tint as
vertex colour, and no image: the loader draws a node named `<node>_tex_<id>` with texture `<id>` of the asset
library, and the manifest's `textures` lists the ids the map uses); `patch_<ID>` (see Ground patches); `props` and
`props_collision` remain only for items without a parcel (the orthophoto trees). A fence wall (brick, stone, concrete types; map 0.16.0)
has `house_fence_<index>_visual`, `_visual_tex_<texture id>` and `_collision` nodes of its own (Fence sections). The manifest says
`"props_layout": "per_parcel"`; `map_version` went from `0.1.0` to `0.2.0` for this node-layout
change (pre-1.0, a node-layout change bumps the minor part). Also
`parcels.json` (ID, cadastral reference, kind, polygon and anchor in local metres with x east and
y north, chunk, building IDs), `roads.json`, `spawns.json` and `manifest.json` (map version
`<MAP_VERSION_BASE>-village`, chunk list with triangle counts per node, generated parts, terrain grid info,
attribution, input hashes including the work files, `file_sha256`: the sha256 of every file of `files`, so the build hash the game's join check compares covers them). The output has no timestamp and is
byte-identical for identical input.

**Bunkers (`bunkers.json`).** The three bunkers are buildings with `model: bunker` in `config/overrides.yaml`
(`pipeline/src/drycastle_pipeline/buildings/landmarks/bunker.py`, ADR 0009): one room behind 0.5 m walls, an open doorway on the wall facing
`model_axis`, firing slits that are real openings, and three more rooms under it (2.4 m floor to floor), one per floor on the
same footprint. A 1 m square hatch in each floor, in a different place on each, leads down; a ladder hangs on one edge of
it. The ground floor has no collision (a player stands on the flattened terrain, so the terrain collision round a bunker is
built at the full grid), and its hatch is cut out of the terrain and `terrain_collision` (`terrain.cut_holes`). The floors,
ceilings and walls underneath are the collision of the shell; the furniture is `props` (below). `bunkers.json` (listed under `files.bunkers`, only
when the map has a bunker) holds per bunker `{"id", "parcel", "floors": [{"level", "polygon", "z0", "z1"}], "ladders":
[{"x", "y", "dx", "dy", "z0", "z1"}], "hatch": ring, "atlas", "props": [{"model", "pos": [x, y, z], "yaw", "scale", "boxes"}], "intel": {"x", "y", "dx", "dy", "z0", "z1", "width"}}` (`intel` is the wall
of the lowest floor, the command post, that the intel screen hangs on: the middle of the screen on the wall, the way it
faces, its lower and upper edge and its width, 1.6 to 3.0 m; the pipeline keeps furniture off the strip in front of it and
leaves the key out when no wall takes a screen; ADR 0017; since the amendment it is only the game's fallback, used when the map has no hangar object): `level` 0 is the ground floor and -1 to -3 the floors under it; the
polygon is a room ring in local metres (x east, y north) and the heights are as in `terrain.json`. A room is grown 0.15 m into
its walls, floor and roof, so a blast on a wall's inner face is inside and one on its outer face is outside. A ladder gives
the spot where the climber stands (`x`, `y`), the way they face (`dx`, `dy`) and the floors it joins (`z0` below, `z1`
above). The shooter's server uses the rooms for the shelter rule (`ShooterCore/Bunkers/Shelters.cs`: a blast hurts a player
only when both are in the open or both are in the same room), and the ladders go to every player body (`Ladders.Climb`, the
shared movement step). The client darkens the rooms and hangs a lamp in each (`shooter/Bunkers/BunkerLights.cs`). The placeable
`prim:bunker` is the closed form (a placed object collides as one box) and has no room.

**Hollow houses (`interiors.json`).** Per building whose detail says `interior: true`: shell, floor plan and doors.
Listed under `files.interiors`, only when the map has one. Fields: [interiors.md](interiors.md).

**Water bodies (`water.json`, version 2).** Written when the terrain exists, listed in `manifest.json` under
`files.water`. One body per parcel or ground area (`parcels.json` `ground`) of a water kind (a type that carries the `water`
attribute), and one per ground patch with a `water_y` (`edits.json`; a patch is split in pieces where the terrain under its level is
not one piece). Each body is
`{"id", "polygon", "surface_y", "bed_y", "max_depth_m", "depth", "flow"}`: the polygon in the geo-interface form of
`parcels.json` (local metres, x east, y north), the heights in metres on the terrain's datum (as `terrain.json`).
The surface is the median terrain height along the parcel outline (the shoreline), or the patch's `water_y`.
`bed_y` is the lowest bed (`surface_y - max_depth_m`), kept for readers of version 1. `depth` is the bed as a grid:
`{"origin", "cell_m": 2.0, "cols", "rows", "cm"}`, the south-west corner in local metres, and `cm` the depths in
centimetres as base64 of little-endian uint16, rows from the south, 0 outside the outline. `flow` is reserved (an empty
list until flow is added). A water parcel or ground area with no terrain under its outline gets no body (a warning is printed).

*The natural bed (`terrain/bed.py`).* Each body's depth comes from its outline alone (seeded, so the same map gives the same
bed): depth grows from the bank over a shelf (banks shelve), a river (`water.river_ids` in `rules.yaml`, or any body at least
`river_min_length_m` (400 m) long, since a water ground area's ID `A-NNNN` moves with the
inputs and cannot name the Morava) gets pools and riffles from a seeded value-noise pattern along its main axis (mid-channel 2.5 m, pools to
3.5 m, riffles 0.8 m, bars every 60 to 120 m), and the outside of a bend (the outline curls round the water) is deeper
and steeper. Ponds and lakes (every other body) get a shallow bowl (1.5 m). A patch's `max_depth` (edits.json, or the
`set_patch_water` MCP tool) scales its bed so the deepest point is that deep. Every parameter is the `water` section of
`rules.yaml`.

*The carve (`bed.carve_terrain`, stage `export`).* The terrain grid under each body is then lowered to surface minus
depth (the grid's bilinear depth), so the terrain mesh and `terrain_collision` both carry the bed. Outside the outline a
bank standing above the surface is cut down to it over `water.carve_blend_m` (4 m, never raised). The chunks that hold a
body use the full 4 m collision grid, not the decimated one. The water is planned on the terrain before the carve (the
shoreline and wet areas read the ground as it was), and the water patches use the same wet areas as `water.json`.
`patch-run` carves the same way, so a hot-reloaded patch drapes on the bed the export carves.

The shooter reads it as `ShooterCore.Water` (`Water.Parse`, versions 1 and 2): depth (`Water.Depth`, `DepthAt`, the
sample's `WaterDepth`) is bilinear over the grid in Godot ground axes (x east, z = -north, so the grid's y is `-z`), and
a version 1 body keeps its flat bed. The file is absent for a map without terrain. `"water"` is listed under `generated`;
water surface meshes are not generated.

**Water craft placement (#132).** The amphibious transport spawns on land within 40 m of water when it can (`NetServer.Vehicles`,
using `Water.ShoreDistance`); it is the only vehicle that floats, and nothing is placed on the water itself.

**Zone points (`zone_points.json`, ADR 0022).** The game never sees ground areas or zones (the pipeline bakes the ground into
terrain colours and detail), so a zone the server needs while it runs is exported as a pool of **candidate points** instead of
polygons. `zones` in `rules.yaml` has the section of every kind (see "Zones" under Parcels and ground areas). For each kind
but `player_spawn` that has areas, `zones.build_zone_points` (called by the export stage, after the water, which it needs)
takes the areas of the kind: the ground areas of the types of its `where_ground` rule zone and the drawn zones of
`edits.json`. Their union, cut to the terrain coverage and with the clearances taken off, is the usable ground, and the
points are drawn in it the way `props.make_spawns` does (seeded by `zone_points:<kind>`, uniformly
distributed by area: a triangle of the constrained Delaunay triangulation is picked in proportion to its area, a point inside
it, rounded to a millimetre and kept only if still inside). Rules per kind:

| Key | Meaning |
|---|---|
| `m2_per_point` | density: one point per this many m2 of usable ground |
| `min_points`, `max_points` | the count is `round(usable_m2 / m2_per_point)` kept between them; no usable ground, no points |
| `building_clearance_m`, `water_clearance_m` | metres kept clear of buildings and of the water bodies of `water.json` (shore included) |

There is no clearance from trees (the Bigfoot walks through and tramples them) and none from fences, roads or placed objects.
Today `bigfoot` has 1000 m2 per point, 30 to 300 points, 10 m from buildings and 5 m from water; `scud` (the Scud event's hidden truck, 12 m long) has 2000 m2 per point, 10 to 200 points, 15 m from buildings and 10 m from water, no rule zone (drawn only: the village map has none yet, and the server then falls back to open ground, docs/events/scud.md); `cartel_drop` (Drop points, where the Cartel van stops) has 150 m2 per point, 4 to 200 points, 3 m from buildings and 5 m from water, `cartel_hit` (Hit spots) 400 m2 per point, 10 to 300 points, 6 m from buildings and 5 m from water, both drawn only (none drawn: no Delivery or Hit, the Job refuses to start; read as `ShooterCore.Cartel.CartelMap`); `cartel_heli` and `cartel_boat` have no numbers and no points (outlines in `zone_polygons.json`, `CartelMap.Helis` / `Boats`; none drawn: no helicopter or boat drop); the wildlife species have
20000 m2 per point, 5 to 80 points, 15 m from buildings and 5 m from water.

The file is listed under `files.zone_points` in `manifest.json` and written **only when some zone has an area**; otherwise it
is absent (and a stale one from an earlier build is deleted), which the loader reads as "no points".
Format (`version` 2, compact, sorted keys):
`{"version": 2, "axes": "...", "zones": [{"kind": "deer", "areas": ["A-0003", "Z-0001"],
"area_m2": 52000.5, "points": [[x, y, z], ...]}]}`. One entry per kind that has areas, in catalog order. `areas` are the ids of
the ground areas (rule zone) and drawn zones the points were drawn in, `area_m2` the usable ground, a point is local metres
(x east, y north, as `parcels.json`) and `z` the terrain height there (as `terrain.json`). A kind with areas but no usable
ground is listed with `points: []` and the export warns. `player_spawn` has no entry. The map version is not bumped (like
`bunkers.json`, an optional file that a loader may ignore). No terrain, no file (the points need the coverage and the heights).

**Zone polygons (`zone_polygons.json`, issue #364).** A kind the server asks "is this player standing inside?" of (`zones.POLYGON_KINDS`, today
`plantation`, `cartel_heli` and `cartel_boat`) cannot use a pool of points, so the export writes its outlines: `zones.build_zone_polygons` takes the same areas
(`kind_areas`: the rule zone's ground areas less the cuts, and the drawn zones), nothing clipped to the terrain or the buildings, one ring per outline
(holes dropped). Rings are in **game axes** `[x, z]` (x east, z = -north of the local metres of `parcels.json`), rounded to a millimetre. Format (`version` 1):
`{"version": 1, "axes": "...", "zones": [{"kind": "plantation", "areas": ["Z-0001"], "rings": [[[x, z], ...], ...]}]}`, one entry per kind that has a zone.
Listed under `files.zone_polygons` of `manifest.json` and written **only when such a zone exists** (a stale file is deleted, no terrain needed); the map version
is not bumped. A kind of `NO_POINT_KINDS` (`player_spawn`, `plantation`) has no entry in `zone_points.json`, and its `rules.yaml` `zones` section has no keys
(`plantation: {}`). The server reads it as `ShooterCore.ZonePolygons` (`MapLoader.ZonePolygonsJson`, `Net/Server/Highs/PlantationServer.cs`): `Contains(kind, x, z)`,
2D even-odd point-in-polygon; no file or no zone: the feature is off, no error.

`parcels.json` also carries `zone_kinds`, `[{"name", "label", "group", "tint", "where_ground": [types] | null}]` in catalog
order, for the editor's Zones tool; the drawn zones are not in it (the editor reads them from `edits.json`).

The server reads the points as `ShooterCore.ZonePoints` (`Points(kind)`, `Count(kind)`, `Pick(kind, rng)`; the map loader
keeps the text as `MapLoader.ZonePointsJson`, empty without the file): `MapAreas.PickEventSpawn("bigfoot")` (or `"scud"`) returns a random
point in Godot ground axes (x east, y the terrain height, z = -north), or `null` when there is none (the map has no file, no
zone of the kind, or nothing usable was left): the caller must handle it, and the wildlife keeper takes the points of each
species. A broken or newer file is logged and counts as no points.

**Property groups (`groups.json`).** The export copies the `groups` of `edits.json` into the map as
`{"version": 1, "groups": {"G-0001": {"parcels": ["P-0412", ...]}}}` (sorted; only parcels the map has; no fence), so the server
builds the pets' pens from the built map and never reads `edits.json`. Listed under `files.groups`; written only when some group has a
parcel of the map, otherwise absent (a stale file is deleted), which the server reads as "no pets". No map version bump.

`make preview` renders `out/village/preview/topdown.png` (parcels by kind, buildings in wall colour),
`iso.png` (isometric view from the mesh data: terrain relief, roads, buildings, fences, trees;
`make preview EXAG=5` changes the relief exaggeration, default 3) and `iso_close.png` (the same,
zoomed on the densest cluster of buildings), all with a scale bar.
