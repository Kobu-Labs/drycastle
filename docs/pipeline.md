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
`cd pipeline && uv run python -m suchohrad_pipeline fetch --force` to refetch everything.
Requests are sequential, use the User-Agent `suchohrad-pipeline`, have timeouts and retry with
backoff (Overpass falls back to a second public instance).

## Whole village (areas)

The pipeline runs over two areas around the same centre, chosen with `AREA=slice` (default) or
`AREA=village` for make, or `--area` on the CLI:

| | slice | village |
| --- | --- | --- |
| size | `radius_m` (default 100) | `village.radius_m` (default 700, `village.margin_m` defaults to `margin_m`) |
| raw data | `data/raw/` | `data/village/raw/` |
| work files | `data/work/` | `data/village/work/` |
| export, previews | `out/map/`, `out/preview/` | `out/village/map/`, `out/village/preview/` |
| `map_version` | `0.2.0-slice` | `0.2.0-village` |

Add the `village:` section to `pipeline/config/slice.local.yaml` (see `slice.example.yaml`).
Both areas share the one committed registry `registry/parcels.registry.json`. Fetching the
village is polite: Overpass in one request, cadastral WFS paged (`startIndex`, 5000 per page, sorted)
when a page is full, the DMR raster in WCS tiles of at most 1000 m per side (cached per tile in
`dmr_tiles/`) that are mosaicked into `dmr5.tif`. All are cached; `--force` refetches.

`rules.yaml` has an `areas.<name>` overlay that is merged over a section only for that area. The
village profile uses fewer fences without collision boxes, fewer trees (`props.trees.max_per_chunk`
caps rule-based trees per chunk) and more spawn candidates, to keep chunk files small for 16-64
players on weak GPUs. `make preview AREA=village` also writes `overview.png`: the parcels and
buildings with the 250 m chunk grid and chunk indices.

Village numbers (one real run, default settings): 1165 parcels, 384 buildings, 40 chunk files,
about 400k visual and 75k collision triangles in total, at most about 24k visual triangles and 1.7 MB
per chunk, 29 MB of glb in all (fences without collision and the tree cap took that from 39 MB). A
full run after the fetch takes about half a minute; the fetch takes about two minutes. Nothing was
measured in Godot yet; terrain (4 m grid, 7.7k triangles per chunk) is now the biggest visual part.

## Normalise and the parcel registry

`make normalise` reprojects the OSM data and cadastral parcels from WGS84 to EPSG:5514 and then
shifts them into a local metric frame whose origin is the slice centre (`slice_centre.json`).
Axes stay those of EPSG:5514. It writes `data/work/normalised/{buildings,highways,landuse,parcels}.geojson`
(local metres, gitignored) and `data/work/frame.json`.

`make parcels` gives every cadastral parcel an own stable ID (`P-0001`, 4 digits, growing when
needed) and writes `data/work/parcels.json` (ID, cadastral reference, polygon, area, kind,
anchor point, building ids, 250 m chunk index) and prints a report (counts by kind, buildings
assigned or unassigned, new and retired IDs). Kind and building-assignment rules, with their
order and thresholds, are documented in `pipeline/config/rules.yaml`.

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
and no terrain crease can poke through; overlapping ribbons at intersections are
accepted in v1, and a higher road class sits a hair above a lower one.

**Props** (`make props`): low fences (thin boxes, 1.2 m) along the boundaries of building plots
and of gardens near buildings (seeded per parcel, a gate gap on road frontage, no fence through
buildings or the carriageway), OSM trees and seeded rule-based trees in gardens, plots and forests
(blocky box canopy, or stacked pyramids in forests). Fences also get a simple closed-box
collision node; trees have no collision in v1.

**Hand edits** (`pipeline/config/edits.json`, read by the props stage; `--edits PATH` overrides the
file): per-parcel fence mode (`auto`, `none`, `custom` with a type, height, colour and gaps),
property groups (`G-NNNN`: one fence around the members' combined outer edge), tree density or
count with a seed offset, and placed objects (`pack:` or `prim:` assets, absolute S-JTSK metres).
Objects go to `work/objects.json`. `regions` (`R-NNNN`: `colour` `#rrggbb`, `polygon` one ring of at
least 3 `[x, y]` absolute S-JTSK points, no self-intersection) are shapes painted over the ground;
they are not read by the props stage, only by the export and `region-run` (below). The format and validator are documented in
`pipeline/src/suchohrad_pipeline/edits.py`. Entries for parcels that no longer exist are skipped
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

`make region-run [REGION=R-0001] [GLB=file.glb] [OUT=file.json]` (`python -m suchohrad_pipeline
region-run --region ID ... --glb PATH`, none named = all) writes a GLB with one `region_<ID>` node
per region (the whole region, in the chunk files' frame) and prints a JSON summary: `regions` (with
a mesh), `replaces` (the baked `region_<ID>` nodes to hide: the named IDs, even deleted ones, plus
every region overlapping a named one, whose visible area changes with it), `triangles`, `glb`,
`timing_s`. Like `parcel-run --glb` it is for hot reload; nothing under `data/` is written.

## Export

`make export` writes `out/map/` (gitignored): `chunks/chunk_<ix>_<iy>.glb` (one glTF per 250 m
chunk that has buildings or terrain; Y-up axes x east, y up, z -north, metres, one frame for all
chunks). Nodes per chunk glb: `parcel_<ID>_visual` (walls and roof, flat per-face vertex colours)
and `parcel_<ID>_collision` (extruded footprint, no roof detail) for each parcel with buildings;
`terrain` and `terrain_collision` (flat colours by parcel kind; collision is every second grid
vertex); `roads`; `parcel_<ID>_props` (that parcel's fences and trees, visual) and
`parcel_<ID>_props_collision` (its fences, only when the `fences.collision` rule is on), placed in
the parcel's own chunk file so one parcel's props can be swapped at runtime; `region_<ID>` (see Regions); `props` and
`props_collision` remain only for items without a parcel (OSM trees). The manifest says
`"props_layout": "per_parcel"`; `map_version` went from `0.1.0` to `0.2.0` for this node-layout
change (pre-1.0, a node-layout change bumps the minor part). Also
`parcels.json` (ID, cadastral reference, kind, polygon and anchor in local metres with x east and
y north, chunk, building IDs), `roads.json`, `spawns.json` and `manifest.json` (map version
`0.2.0-slice`, chunk list with triangle counts per node, generated parts, terrain grid info,
attribution, input hashes including the work files). The output has no timestamp and is
byte-identical for identical input.

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

`make preview` renders `out/preview/topdown.png` (parcels by kind, buildings in wall colour),
`iso.png` (isometric view from the mesh data: terrain relief, roads, buildings, fences, trees;
`make preview EXAG=5` changes the relief exaggeration, default 3) and `iso_close.png` (the same,
zoomed on the densest cluster of buildings), all with a scale bar.
