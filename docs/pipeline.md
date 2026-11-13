# Pipeline reference

Details for the map pipeline (`pipeline/`). Setup and the make targets are in the [README](../README.md).

## Configuring the slice and fetching data

The map is built for a small slice of the village first. The slice is a square around the
centroid of one OSM way (for example a building), set in a local, gitignored file:

```sh
cp pipeline/config/slice.example.yaml pipeline/config/slice.local.yaml
# edit osm_way (OSM way id), radius_m (default 100) and margin_m (extra metres fetched, default 25)
make fetch
```

`make fetch` writes to `data/raw/` (gitignored): `osm.json` (Overpass: buildings, highways,
barriers, landuse, natural tree/wood/water), `parcels.geojson` (INSPIRE cadastral parcels
intersecting the box, WFS) and `dmr5.tif` (DMR 5.0 terrain, 1 m, EPSG:3046, via INSPIRE WCS).
Each file has a `<name>.meta.json` sidecar (request, url, fetched-at, sha256, source, licence,
attribution) and `manifest.json` lists the sources, licences and required attribution.

Reruns skip files whose request parameters are unchanged and download nothing; use
`cd pipeline && uv run python -m drycastle_pipeline fetch --force` to refetch everything.
Requests are sequential, use the User-Agent `drycastle-pipeline`, have timeouts and retry with
backoff (Overpass falls back to a second public instance).

## Whole village (areas)

The pipeline runs over two areas around the same centre, chosen with `AREA=village` (default) or
`AREA=slice` for make, or `--area` on the CLI:

| | slice | village |
| --- | --- | --- |
| size | `radius_m` (default 100) | `village.radius_m` (default 700, `village.margin_m` defaults to `margin_m`) |
| raw data | `data/raw/` | `data/village/raw/` |
| work files | `data/work/` | `data/village/work/` |
| export, previews | `out/map/`, `out/preview/` | `out/village/map/`, `out/village/preview/` |
| `map_version` | `0.2.0-slice` | `0.2.0-village` |

Add the `village:` section to `pipeline/config/slice.local.yaml` (see `slice.example.yaml`).
Both areas share the one committed registry `registry/parcels.registry.json`. Fetching the
village is polite: Overpass in one request (an area over about 1.5 km a side in pieces of that size, each cached in
`osm_tiles/` so a failed run resumes, merged into `osm.json` without duplicates), cadastral WFS paged (`startIndex`, 5000 per page, sorted)
when a page is full, the DMR raster in WCS tiles of at most 1000 m per side (cached per tile in
`dmr_tiles/`) that are mosaicked into `dmr5.tif`. All are cached; `--force` refetches.

**Map outline (village only).** `village.polygon` in `slice.local.yaml` replaces the square with any
simple polygon, given as absolute S-JTSK `[x, y]` metres (mapy.com or GPS points convert with pyproj
`EPSG:4326 -> EPSG:5514`, lon first; local game coordinates are these minus `origin_epsg5514` in
`frame.json`). Upstream services only take boxes, so the pipeline fetches the polygon's bounding box
(grown by `margin_m`) and then clips to the polygon: `normalise` drops every parcel, building, road,
tree, lamp and landuse that does not touch it (whole objects are kept, not cut), `fetched_bounds_epsg5514`
becomes the polygon (so the registry only retires entries inside it) and the terrain coverage becomes the
polygon, which every later stage already clips to. The terrain mesh reaches 1.5 cells past the outline so the
edge is not eaten. The centre `osm_way` still sets the local origin, so the frame and all edits stay put.
Changing the polygon needs `make fetch` again (the box changes) and then the stages after it. Not done:
nothing stops a player at the edge (the flight limit is still the parcels' bounding rectangle).

`rules.yaml` has an `areas.<name>` overlay that is merged over a section only for that area. The
village profile uses fewer fences without collision boxes, lower tree densities (the per-chunk tree cap
`props.trees.max_per_chunk` is available but set to 0 = off) and more spawn candidates, to keep chunk files small for 16-64
players on weak GPUs. `make preview AREA=village` also writes `overview.png`: the parcels and
buildings with the 250 m chunk grid and chunk indices.

Village numbers (one real run, default settings): 1165 parcels, 384 buildings, 40 chunk files,
about 400k visual and 75k collision triangles in total, at most about 24k visual triangles and 1.7 MB
per chunk, 29 MB of glb in all (fences without collision and the tree cap took that from 39 MB). A
full run after the fetch takes about half a minute; the fetch takes about two minutes. Nothing was
measured in Godot yet; terrain (4 m grid, 7.7k triangles per chunk) is now the biggest visual part.

## Parcels and ground areas

Only a property is a parcel: a cadastral parcel that holds a building (kind `building_plot`), or one that is in a property
group (`config/edits.json`) with a parcel that does (kind `garden`). The land of every other cadastral parcel (fields,
forest, water, the land the roads lie on, gardens of no property) is not kept as parcels. The parcels stage (`ground.py`,
`ground` in `rules.yaml`) cuts it into **ground areas** by type instead, written to `parcels.json` under `ground` in the
shape of a parcel (`id` `A-0001`..., `kind` = the type, polygon, area, anchor, chunk):

* a region of `edits.json` with a `ground` type is a **hand-drawn ground area**: its polygon outside the parcels is
  ground of that type, whatever the rules say (where two overlap the higher ID wins). Draw the region in the editor,
  then give it a type with the MCP tool `set_region_ground` (or `create_region` with `ground`). The editor paints it as
  a preview at once; the real ground (terrain colour, detail, trees) comes with the next full run (`make map`), and the
  baked map does not paint it as a region;
* then the types are tried in the order of `ground.types`, each takes what the ones above left of its source: `dike`
  (the parcels and clips of `detail.dike`), `water` and `forest` (the OSM natural/landuse polygons, with their own
  outlines), `allotments`, `orchard`, `meadow`, `village` (OSM `landuse=residential`: street sides, greens);
* what no type claims is the default, `field`.

A type is the whole description of a kind of ground, one entry of `ground.types`: its source (`osm` or `rule`), terrain
`colours`, detail `recipe` (field, forest, village, dike) with optional `detail` settings laid over that recipe's
section for this type only (a meadow is the field recipe without furrows and headlands), `water` and `spawns`. A new type is one new entry; the map area can override a type
(`areas.village.ground.types`). There is no road ground: a road is the OSM ribbon on top of whatever lies under it.
Ground areas have no fence and no edits of their own, and their IDs are only stable while the inputs are. Trees are not
part of a type: they come from the orthophoto only (`docs/ortho.md`).

**Carving.** A big cadastral parcel can be a parcel only because a building stands on it (a field, the dike strip, public
land). `ground.carve` in `rules.yaml` lists such parcels: the parcel keeps its ID but becomes the building outline(s)
grown by `margin_m` (a slim yard, mitre corners, never over another parcel), and the rest of its land becomes ground like
any other. Today P-0073 (the dike strip, with a hut) and P-0754 (a 13 ha field with one building).

### Ground types of your own (`edits.json` `ground_types`)

The types can be changed, added and removed without touching `rules.yaml`: `pipeline/config/edits.json` has an optional
top-level `ground_types`, `{"<name>": {...}}`, written only when there is something in it. An entry has the keys of a
`rules.yaml` type (`colours`, `recipe`, `detail`, `water`, `spawns`, `osm`; no `rule`, rule types stay in `rules.yaml`)
or `"deleted": true`:

```json
"ground_types": {
 "meadow": {"colours": ["#8cb86a", "#82ad62"], "detail": {"tone_per_ha": 6}, "spawns": false},
 "dunes": {"colours": ["#d8c38a"], "osm": {"natural": ["sand"]}, "recipe": "field"},
 "orchard": {"deleted": true}
}
```

* **Merge rule** (`ground.ground_types(rules_path, area, edits_path=None)`, `merge_ground_types`): the types of
  `rules.yaml` (with the area overlay) come first; a key of an edits entry overrides that key of the rules entry (the
  dicts `osm` and `detail` are merged key by key, deeper dicts the same way, lists and plain values are replaced, so
  `colours` is replaced whole and `osm: {natural: [...]}` replaces only the natural list); a new name is a new type
  appended after the rules' types (so the rules' types take OSM land first); `deleted` removes a type of the rules.
  Without `edits_path` the result is `rules.yaml` alone. Every stage that reads the types passes the edits path (parcels
  for the OSM sources and `drawn_ground`, scene for the terrain colours, props for `spawns`, detail for the recipe and
  its `detail` settings, export for `water`). A region whose `ground` names a type that no longer exists is skipped with
  a warning by the parcels stage.
* **Validation** (`edits.py`, mirrored by the editor): names are lower_snake (`^[a-z][a-z0-9_]{0,23}$`); `colours` 1 to
  6 `#rrggbb`; `recipe` one of `field`, `forest`, `village`, `dike`, or `null` for no detail; `detail` an object whose
  top-level keys exist in the shipped `detail.<recipe>` section (the recipe is the entry's own or the rules type's);
  `water` and `spawns` booleans; `osm` an object `{landuse|natural: [text, ...]}`; `deleted` is `true` and alone, names a
  type of `rules.yaml`, and is refused for the default type and for a type with a `rule` (the dike, `detail.dike` needs
  it). Unknown keys are errors.
* **Rebuild**: a type change needs the full map build (`make map`): the ground areas, terrain colours and detail are
  made by the parcels, scene and detail stages. Nothing is hot-reloaded. The editor, MCP and this document say so.
* **Export**: `parcels.json` `ground_types` lists every type of the rules (and then the user's own) with the rules'
  own entry (`rules`; `custom: true` instead for a user's type) and a summary at the build (`colour`, `colours`,
  `recipe`, `water`, `spawns`, `source` = `osm`, `rule` or `drawn`); `ground_default` names the default type;
  `ground_recipes` holds the shipped settings of each recipe (without the clutter tables). The editor lays the current
  `edits.json` over `rules`, so its list is live between builds.
Edits of a parcel that is no longer kept are skipped with a warning by the props stage; putting the parcel in a group
with a building plot (by hand in `edits.json`, it is not shown in the editor) brings it back.

## Normalise and the parcel registry

`make normalise` reprojects the OSM data and cadastral parcels from WGS84 to EPSG:5514 and then
shifts them into a local metric frame whose origin is the slice centre (`slice_centre.json`).
Axes stay those of EPSG:5514. It writes `data/work/normalised/{buildings,highways,landuse,parcels}.geojson`
(local metres, gitignored) and `data/work/frame.json`.

`make parcels` gives every cadastral parcel an own stable ID (`P-0001`, 4 digits, growing when
needed) and writes `data/work/parcels.json`: the parcels that are properties (ID, cadastral reference, polygon, area,
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
lies inside the box it fetched (`fetched_bounds_epsg5514` in `frame.json`); entries outside are
left untouched. So the village after the slice keeps every ID and numbers the new parcels after the
highest one, and the slice after the village retires nothing. Shrinking or moving an area retires
only what is inside the new box and no longer in the cadastre (reversible: an entry whose exact
reference reappears is revived). Retiring or
reassigning IDs is a breaking change of the map artifact (see the versioning decision).

## Buildings: rules, meshes, export

`make rules` writes `data/work/buildings.json`, one entry per building assigned to a parcel
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

**Terrain** (`make terrain`): `data/raw/dmr5.tif` (EPSG:3046) is resampled bilinearly into a
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
`out/map/roads.json` (`nodes`: id, x, y; `edges`: id, osm_id, from, to, polyline, highway, width,
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

**Fence types and the texture library.** A fence section with a `type` is drawn in that type's style
(`fencestyles.py`) instead of the plain box: `picket` and `wire` are one thin two-sided quad with a
cut-out texture that follows the ground, with wood or metal posts; `brick_red`, `brick_white`,
`stone_wall` and `concrete` are level-topped walls (they step on a slope) with a repeating texture, a
flat coping and matching piers. Columns stand at every section joint and run end; `column` swaps them
(`none`, `wood_post`, `metal_post`, `brick_red`, `brick_white`, `stone`, `concrete`), e.g. picket
between brick piers. The colour is a tint: picket, wire and concrete are near-white textures, so the
colour is their paint; brick and stone carry their own colours and default to white. Custom (hand-edited)
fences always have a type (default `picket`); rule fences stay plain boxes unless `props.fences.type` is
set in `rules.yaml`. The collision box of a section is unchanged.

The textures are a library, not part of the fences: `assets/manifest.json` `textures` lists each one (id,
PNG under `assets/`, `tile_m` = the size of one repeat in metres, `alpha`, `tintable`, mean colour), and
`textures.py` gives any mesh builder `texture(id).uv(...)` and a `TexturedBuilder`. The PNGs are built by
`python3 -I scripts/make_textures.py` (standard library only, deterministic; `--sheet FILE` writes a contact
sheet). `uv run python -m drycastle_pipeline.fence_preview` (in `pipeline/`) draws every fence type from its
real meshes, UVs and textures into `out/textures/fences.png`, so the look can be checked without the game.

**Hand edits** (`pipeline/config/edits.json`, read by the props stage; `--edits PATH` overrides the
file): per-parcel fence mode (`auto`, `none`, `custom` with a type, column, height, colour and gaps),
property groups (`G-NNNN`: one fence around the members' combined outer edge), and placed objects (`pack:` or `prim:` assets, absolute S-JTSK metres).
Objects go to `work/objects.json`, and with them the `cables` (`C-NNNN`: `from` and `to`, the ids of two
placed utility poles) whose poles are both placed; the game strings the wires (`docs/utility-poles.md`). `regions` (`R-NNNN`: `colour` `#rrggbb`, `polygon` one ring of at
least 3 `[x, y]` absolute S-JTSK points, no self-intersection) are shapes painted over the ground;
they are not read by the props stage, only by the export and `region-run` (below). The format and validator are documented in
`pipeline/src/drycastle_pipeline/edits.py`. Entries for parcels that no longer exist are skipped
with a warning; schema errors fail the stage.

**Spawns**: `out/map/spawns.json` has a seeded subset (default 60) of points on roads and on
garden, field and other parcels, at least `min_building_distance_m` from any building, with
elevation, parcel, highway type (roads) and chunk index.

**Street lamps** (`lamps.py`, run by the props stage, `make props`): `data/work/lamps.json` holds
one point per lamp (`id` `L-NNNN`, `x`, `y`, `z` elevation, `source` `osm` or `rule`, `osm_id`,
`highway`, `parcel`, `chunk`). OSM `highway=street_lamp` and `man_made=street_lamp` nodes are in the
Overpass query and in `normalised/street_lamps.geojson`; each one is kept if it is inside the terrain
coverage and outside building footprints (grown by `lamps.building_clearance_m`). Where a rule road
(`lamps.rule_highways`) has no lamp within `lamps.min_lamp_distance_m`, rule lamps are placed every
`lamps.spacing_m` along each edge, alternating sides, `road_offset_m` beyond the carriageway edge.
Rule lamps also stay out of `lamps.avoid_kinds` parcels (house plots and gardens). Placement is
deterministic (no random numbers). Raw data fetched before street lamps were queried has no
lamp nodes: run `make fetch --force` once, then `make normalise` and `make props`. The export copies
`lamps.json` to `out/map/` (map version 0.5.0, `files.lamps`); lamps are not in the chunk GLBs. The game
builds the pole, the head and the light from primitives, so the viewer ignores them.

**Tree collision (`trees.json`).** Each tree is two or three solids, built in the game from the rule model numbers
(`model`: trunk width and height, canopy edge) and the tree's scale and rotation, with the same arithmetic as the drawn tree
(`propmeshes.tree_mesh`): the trunk box (solid, starts 0.2 m below the ground) and the crown (two stacked boxes for a broadleaf,
two stacked pyramids for a conifer). Trunks are on the world layer, so players and cars stop at them. Crowns are shot-only
(layer 4): bullets, pellets, rockets, turret sight and the client's decals and tracers stop on them, while players and cars drive
through. The geometry is `ShooterCore/Colliders/PropColliders.cs`; the loader (`MapLoader.Trees.cs`) makes one static body per
chunk and role, not one per tree. The surface of a hit (wood or foliage) comes from the tree lookup
(`TreeColliders.GroupAt`). Trees that the viewer edits are not in the game's collision until the map is re-exported.

## Regions

A region (`regions` in `edits.json`) is a hand-drawn colour shape, independent of parcel borders. It
changes only colour: no collision, no effect on trees, fences or objects, no `regions.json`. The
export builds one flat mesh per region (`regions.py`): the polygon is cut along the terrain mesh
triangles and, where it covers a road ribbon, along the ribbon outline, so the edges are exact and
the surface follows the ground; every vertex is lifted `regions.lift_m` (0.03 m) above the terrain,
or `regions.road_lift_m` (0.02 m) above the ribbon's own height on a road. Buildings, trees,
fences and objects are not considered. Where regions overlap the higher ID wins: its polygon is
subtracted from the lower regions before draping, so nothing z-fights. Parts outside the terrain
coverage are cut off; a region wholly outside it is skipped with a warning (`export: warning:
...`), as is any region when there is no terrain. Schema errors fail the run like every other edit.
Nodes are `region_<ID>` in each chunk file the region touches (so a region over a chunk border is
several nodes with the same name, one per chunk file); the manifest lists `regions` in `generated`.

`make region-run [REGION=R-0001] [GLB=file.glb] [OUT=file.json]` (`python -m drycastle_pipeline
region-run --region ID ... --glb PATH`, none named = all) writes a GLB with one `region_<ID>` node
per region (the whole region, in the chunk files' frame) and prints a JSON summary: `regions` (with
a mesh), `replaces` (the baked `region_<ID>` nodes to hide: the named IDs, even deleted ones, plus
every region overlapping a named one, whose visible area changes with it), `triangles`, `glb`,
`timing_s`. Like `parcel-run --glb` it is for hot reload; nothing under `data/` is written.

## Detail (ground patches and clutter)

`make detail` (stage `detail`, in `run-all` between `props` and `export`; `AREA=village` for the village) gives the
ground something to look at. It reads `parcels`, `buildings`, `roads`, `terrain`, `props` (fences, trees), `objects`
and `edits.json` (regions), and writes `data/work/detail.json` (clutter), `detail_patches.npy` and
`detail_patches.json` (patch triangles). `python -m drycastle_pipeline detail --work DIR` reads and writes another work
directory (experiments). The module is `detail.py`; every number is in the `detail` section of `rules.yaml`.

**Area types and recipes.** `detail.area_type(parcel)` says what kind of ground a parcel is and so which recipe runs:
`garden` (parcel kinds `garden` and `building_plot`), `field` (`other`), `forest`, `road`, or nothing (`water`). It is
the only place that reads the parcel kind; a real area-type layer replaces that one function. Parcels are clipped to
the terrain coverage first (a parcel part under 4 m2 gets nothing). Water bodies, buildings, fences, trunks, placed
objects (a circle of half the footprint diagonal plus 0.3 m), hand-drawn regions and the carriageway are kept free.

* **garden**: lawn pieces on tan plots (a green garden gets tones of its own terrain colour instead; `lawn.terrain_mix`),
  worn dirt along walls and in the open, a gravel path from the gate (the middle of the longest unfenced stretch of the
  boundary within 4 m of the carriageway) to the nearest building, up to two vegetable beds (`soil_rows` texture, turned
  with the main building), leaf litter under trees; tufts (thick along walls and fences), flower clusters, pebbles on
  gravel, dirt and along walls, a few rocks, fallen leaves and sticks under trees.
* **field** (meadow, field): large tone patches, dirt headlands along the boundary, a furrowed strip in some of the larger
  parcels, sparse tufts, pebbles, rocks and flowers that thicken towards the edge and on the tones.
* **forest**: leaf litter under some trees, dark and light forest-floor tones, a few bare spots; leaves, sticks, rocks,
  pebbles, tufts and ferns in groups round the trunks.
* **village** (village ground: street sides, greens): grass on all of it beside the OSM carriageway, tufts along the asphalt edge,
  pebbles on the gravel.
* **dike** (`dike.py`, `detail.dike` in `rules.yaml`; the parcels P-0073, P-0821 and P-0805 are the earth dike north of the village, whatever
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
* **verges**: along `track`, `residential`, `tertiary` and `service` roads, gravel and dirt blobs lie at the carriageway
  edge (built once per road edge, every parcel they touch takes its share), whatever the parcel kind.

**Patches** are polygons draped on the terrain (`regions.drape`, so they follow the ground triangle by triangle) 2 cm
above it. The layers (`detail.PATCH_PRIORITY`: gravel, verge gravel, bed, furrow, dirt, verge dirt, litter, two tones,
lawn) are clipped in that order, so no two patches overlap and nothing z-fights; parcels are disjoint. Hand-drawn
regions (3 cm) are cut out of every patch. Patches go into the chunk GLBs as the node `detail_patches` (flat colours)
and one `detail_patches_tex_<texture id>` node per texture (`dirt`, `gravel`, `leaf_litter`, `soil_rows`), at most five per
chunk; they have no collision. A patch triangle belongs to the chunk of its centroid (terrain cells never cross a chunk
border). The village has about 143k patch triangles (garden 76k, forest 33k, field 24k, road 10k), which is why the shapes
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
(`detail_meshes.py`: tuft 15, flower 27, pebble 8, rock 18, leaf 4, stick 12, fern 18). The game draws them as one
instanced mesh per chunk and kind, near the player only; the headless server ignores them. Nothing in the clutter is solid.

Budgets: `detail.caps_per_chunk` caps each kind per chunk (surplus instances go by a hash of the position, so the result is
stable); the village has about 139k instances (`clutter.json` 4.6 MB: tuft 89k, pebble 17k, leaf 11k, flower 8k, fern 8k,
rock 2.4k, stick 2.2k) and the stage takes about 1 to 1.5 minutes. The densities are low in fields and forest on
purpose: a stored instance costs about 33 bytes, so the open land (150 ha) cannot hold meadow-like density; if that is
wanted, generate it near the player at run time from a per-chunk density instead.

All random choices are seeded with `sha256("<parcel id>:detail:<purpose>")` (verges with the road edge ID), so reruns give
identical files. `uv run python -m drycastle_pipeline.detail_preview --parcel P-0637 --area village` draws the detail of
one parcel top-down from the real patches, textures and clutter meshes into `out/detail/`.

**Rust engine** (`pipeline/rust/detail`, `detail_rust.py`). The Python stage takes about 250 s on the full village map (the big
ground areas and the dike are most of it: shapely calls on 100 ha polygons, a Poisson-disc sweep and point-in-polygon tests per
point); the Rust engine does the same work on all cores in 7 to 8 s (26 s on one core) and writes the same four files
(`detail.json`, `detail_patches.npy`, `detail_patches.json`, `detail_meadow.bin`, formats unchanged, so the export and the game read
them as before). The Python stage stays the reference and the default.

* **Build**: `make detail-rust-build` (maturin into the pipeline venv; needs `cargo` and the GEOS development files, e.g. `sudo dnf install geos-devel`
  on Fedora or `libgeos-dev` on Debian, because the `geos` crate links the system GEOS, the library shapely wraps; the build is a
  plain Rust build of about 15 s. Use a GEOS of the same version as shapely's (`python -c "import shapely; print(shapely.geos_version)"`)
  so the geometry matches the Python stage; the module needs the system `libgeos` at run time too). `make setup` (`uv sync`) removes the module again: build it again after.
  `cd pipeline/rust/detail && cargo test --release` runs the Rust tests.
* **Switch**: `python -m drycastle_pipeline detail --area village --engine rust`, `make detail AREA=village ENGINE=rust`, or
  `DRYCASTLE_DETAIL_ENGINE=rust` (also for `run-all`). Without any of them the Python stage runs. `DRYCASTLE_DETAIL_THREADS=N` caps the
  threads (default: all cores); the result is byte-identical for any thread count. Nothing in `rules.yaml` changed: the engine reads the
  same `detail` section (including the `ground.types.<type>.detail` overlays, which Python lays over the recipe section and hands over).
* **What runs where**: Python reads the work files, the rules, the ground types and `edits.json` (regions, placed-object clearance
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
litter), zlib per chunk, about 0.7 MB for the village. A cell is on it only where its whole jitter window is free ground (no buildings, fences, roads, objects, regions)
and the density comes from the recipe's zones (edge band, tone and litter patches, bare soil, near a trunk) times smooth noise; see `detail.meadow` in `rules.yaml` and
`meadow.py`. The game generates the instances at run time (`docs/viewer-and-editor.md`, "Meadow clutter"). `detail_preview --meadow` draws what the game would generate for a parcel.

**Clumps and species zones (map 0.10.0, `meadow.bin` format 3).** Growth is patchy, not even: the meadow density is multiplied by a smoothed noise per layer
(`detail.meadow.layers.<name>.clump`, patches of `clump_m`), and the stored garden tufts likewise (`detail.clutter.tuft.clump`); where nobody mows (within `unmown_m` of a fence, a wall or a
building edge) tufts are thicker (`unmown`). The raster has a *zone byte* per cell: the high nibble is a *mix* (index into `detail.meadow.mixes`: a patch where one species dominates, a
Voronoi patch of `patch_m` ≈ 3 to 8 m, picked from the shares in `zones.<type>.mixes`; the wet mix beside water, the unmown mix where nobody mows, the dry mix in half of a field's edge band), the low
nibble the dryness (8 = neutral, 10 tint units a step: drier at a field's edge, fresher beside water and fences). A layer lists per mix which kinds it makes and in what shares (a mix names only the
layers it changes), so a patch of one mix grows one set of species with strays. `meadow.bin` has a kind table (with the tint flag), the mixes per layer, and the zone plane after the layers (format in `meadow.py`).

**Kinds (map 0.10.0).** `clutter_meshes.glb` has 23 tiny meshes (3 to 30 triangles, `detail_meshes.py`; `detail_preview --kinds` draws a contact sheet to `out/detail/kinds.png`):
the grasses `tuft`, `tall_grass` (seed heads), `straw` (dry), `clover` (a flat mat), `reed` (by water) and `weed` (broad leaves), `fern`; the wild flowers `dandelion` (with a puffball), `daisy`,
`red_clover`, `buttercup`, `poppy`, `cornflower`, `yarrow` (umbels); the garden flowers `tulip`, `sunflower`, `spike` (hollyhock or lupin) and `rose` (a bush); and `pebble`, `rock`, `leaf`, `stick`
(and `flower`, the first mixed-flower mesh, which no recipe uses any more). Wild and garden flowers are kept apart: a yard (garden and plot recipes) grows garden flowers only, in one-species clusters
(`detail.clutter.flower.species`), tall grass beside fences and walls (`tuft.unmown_m`, `tall`) and some clover in the lawn; meadows, fields and road verges grow the wild ones. The meadow's
species mixes (`detail.meadow.mixes`) name which grass profile and which dominant flower each patch has: `meadow` (short grass and clover, daisies), `tall meadow` (red clover, yarrow), `dry` (straw, poppies and
cornflowers), `weedy` (dandelions), `wet` (reed, buttercups), `woodland`, and three flower meadows (cornflower, yarrow, dandelion). Tintable kinds are `detail_meshes.TINTABLE` (the grasses and ferns).

**Tint (map 0.10.0).** Tufts and ferns (`detail_meshes.TINTABLE`) carry a tint byte: the sixth number of their `clutter.json` entry, and in `meadow.bin` (format 2) a flag per layer.
128 is the mesh as drawn, lower is fresher (darker, greener), higher drier. The byte is the smooth tone noise over the ground (2 m cells, 12-cell lattice, `meadow.tone_noise`) plus a per-instance
jitter, shifted by a bias: stored grass within `detail.tint.fresh_m` of a fence or wall is fresher (`fence_fresh`), grass within `water_m` of water likewise (`water_fresh`).
The game computes the same bytes for generated instances (`MeadowGen.Tint`, pinned by the shared fixture).

## Export

`make export` writes `out/map/` (gitignored): `chunks/chunk_<ix>_<iy>.glb` (one glTF per 250 m
chunk that has buildings or terrain; Y-up axes x east, y up, z -north, metres, one frame for all
chunks). Nodes per chunk glb: `parcel_<ID>_visual` (walls and roof, flat per-face vertex colours)
and `parcel_<ID>_collision` (extruded footprint, no roof detail) for each parcel with buildings;
`terrain` and `terrain_collision` (flat colours by parcel kind; collision is every second grid
vertex); `roads`; `parcel_<ID>_props` (that parcel's fences and trees, visual) and
`parcel_<ID>_props_collision` (its fences, only when the `fences.collision` rule is on), placed in
the parcel's own chunk file so one parcel's props can be swapped at runtime; `parcel_<ID>_props_tex_<texture id>`
(map 0.7.0: the textured parts of that parcel's typed fences, one node per texture; it carries UVs and a tint as
vertex colour, and no image: the loader draws a node named `<node>_tex_<id>` with texture `<id>` of the asset
library, and the manifest's `textures` lists the ids the map uses); `region_<ID>` (see Regions); `props` and
`props_collision` remain only for items without a parcel (the orthophoto trees). The manifest says
`"props_layout": "per_parcel"`; `map_version` went from `0.1.0` to `0.2.0` for this node-layout
change (pre-1.0, a node-layout change bumps the minor part). Also
`parcels.json` (ID, cadastral reference, kind, polygon and anchor in local metres with x east and
y north, chunk, building IDs), `roads.json`, `spawns.json` and `manifest.json` (map version
`0.2.0-slice`, chunk list with triangle counts per node, generated parts, terrain grid info,
attribution, input hashes including the work files). The output has no timestamp and is
byte-identical for identical input.

**Bunkers (`bunkers.json`).** The three bunkers are buildings with `model: bunker` in `config/overrides.yaml`
(`pipeline/src/drycastle_pipeline/bunker.py`, ADR 0009): one room behind 0.5 m walls, an open doorway on the wall facing
`model_axis`, firing slits that are real openings, and three more rooms under it (2.4 m floor to floor), one per floor on the
same footprint. A 1 m square hatch in each floor, in a different place on each, leads down; a ladder hangs on one edge of
it. The ground floor has no collision (a player stands on the flattened terrain, so the terrain collision round a bunker is
built at the full grid), and its hatch is cut out of the terrain and `terrain_collision` (`terrain.cut_holes`). The floors,
ceilings, walls and furniture underneath are the collision of the shell. `bunkers.json` (listed under `files.bunkers`, only
when the map has a bunker) holds per bunker `{"id", "parcel", "floors": [{"level", "polygon", "z0", "z1"}], "ladders":
[{"x", "y", "dx", "dy", "z0", "z1"}], "hatch": ring}`: `level` 0 is the ground floor and -1 to -3 the floors under it; the
polygon is a room ring in local metres (x east, y north) and the heights are as in `terrain.json`. A room is grown 0.15 m into
its walls, floor and roof, so a blast on a wall's inner face is inside and one on its outer face is outside. A ladder gives
the spot where the climber stands (`x`, `y`), the way they face (`dx`, `dy`) and the floors it joins (`z0` below, `z1`
above). The shooter's server uses the rooms for the shelter rule (`ShooterCore/Bunkers/Shelters.cs`: a blast hurts a player
only when both are in the open or both are in the same room), and the ladders go to every player body (`Ladders.Climb`, the
shared movement step). The client darkens the rooms and hangs a lamp in each (`shooter/BunkerLights.cs`). The placeable
`prim:bunker` is the closed form (a placed object collides as one box) and has no room.

**Water bodies (`water.json`).** Written when the terrain exists, listed in `manifest.json` under
`files.water`. One body per parcel of kind `water`: `{"id", "polygon", "surface_y", "bed_y"}`, with
the polygon in the same geo-interface form as `parcels.json` (local metres, x east, y north) and the
heights in metres on the terrain's datum (as `terrain.json`). The surface is the median terrain
height along the parcel outline (the shoreline); the bed is the lowest terrain height on a 2 m grid
inside. A water parcel with no terrain under its outline gets no body (a warning is printed). The
shooter reads it as `ShooterCore.Water` (`Water.Parse`, format version 1): depth, surface and
breath queries in Godot ground axes (x east, z = -north). The surface and bed are flat per body, so
depth is one value per body (an approximation). The file is absent for a map without terrain.
`"water"` is listed under `generated`; water surface meshes are not generated.

**Water craft placement (#132).** The amphibious transport spawns on land within 40 m of water when it can (`NetServer.Vehicles`,
using `Water.ShoreDistance`); it is the only vehicle that floats, and nothing is placed on the water itself.

`make preview` renders `out/preview/topdown.png` (parcels by kind, buildings in wall colour),
`iso.png` (isometric view from the mesh data: terrain relief, roads, buildings, fences, trees;
`make preview EXAG=5` changes the relief exaggeration, default 3) and `iso_close.png` (the same,
zoomed on the densest cluster of buildings), all with a scale bar.
