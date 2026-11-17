# Viewer and map editor reference

Details for `godot/viewer/`. Setup and the make targets are in the [README](../README.md). MCP: [mcp.md](mcp.md).

## Viewer

`godot/viewer` loads the whole map at startup with the shared `godot/MapLoader` library
(namespace `Drycastle.MapLoader`) and lets you walk around in first person.

Map folder: `--map <dir>` after Godot's own args, else the env var `DRYCASTLE_MAP`, else
`../../out/village/map` relative to `godot/viewer`. With make: `make viewer` (MAP defaults to `out/village/map`; override with `MAP=/path/to/map`).

- Controls: WASD, mouse look, Space jump, Shift sprint, Escape frees or recaptures the mouse,
  R replaces the nearest parcel with a magenta test cube (`test_cube.tscn`) and restores it on the
  next press.
- Spawn: a random candidate from `spawns.json`; `--seed N` makes it repeatable.
- `make viewer-smoke` (headless; exit code 0 on success): prints chunk, parcel, collider and spawn
  counts, drops the character at 3 spawn points and checks it lands and stays on the ground, then
  tests `ReplaceParcel` / `RestoreParcel`.
- `make viewer-shot` (needs a display): renders `overview`, `oblique`, `street` and `player` views
  at 1600x900 into `out/screens/`; options `--screenshot <path> --view <name> --frames <n>`.

Loader API (`MapLoader`): `Load(dir)`, `Root`, `Parcels` (id, kind, cadastral ref, chunk, building
ids, anchor), `SpawnPoints`, `Stats`, `GroundHeight(x, z)`, `ReplaceParcel(id, PackedScene)`
(hides the parcel's `_visual` node, disables its collision, instantiates the scene at the anchor on
the terrain), `RestoreParcel(id)`. Collision meshes (`*_collision`) become hidden static trimesh
colliders. To guarantee the flat colour look the loader
replaces every visual material with a rough, non-metallic one that uses COLOR_0 (sRGB) as albedo.

### Objects, parcel swap and hot reload

The map folder (map version 0.4.0: `parcels.json` also carries `origin_epsg5514`, the local frame's origin in S-JTSK metres, and the manifest `edit_defaults`, the rule values the editor shows as defaults) also holds `objects.json` (the placed objects planned by the props
stage from `pipeline/config/edits.json`; `files.objects` in `manifest.json`) and `prims/<name>.glb`, one
flat-colour GLB per `prim:` primitive (`prims` in the manifest; same axes as the chunk files, standing on
y = 0). The loader instances every object: `pack:<pack>/<model>` from `assets/manifest.json` (`file`,
resolved against the assets directory), `prim:<name>` from `<map>/prims`. Each object is a node
`Objects/objects_<parcel>/object_<id>` (`<parcel>` is the parcel it stands in, `_none` on ground that is no
parcel: an object belongs to no parcel, it is only a position) with position (x east, z = -north, y = `z` or the terrain height
under it when `z` is null), `rotation_deg` counter-clockwise seen from above, uniform `scale` and an optional
`colour` (material override). Unknown or missing assets give a warning and a magenta placeholder box.
Objects have no collision in v1.

Ground patches (`ground_patches` in `edits.json`: `{GP-NNNN: {type, polygon, water_y?}}`, schema in `pipeline/src/drycastle_pipeline/edits.py`,
ADR 0022) are baked into the chunk files as `patch_<ID>` nodes (flat colour = the type's first terrain colour, no collision; patches of a
water type have none) and rebuilt for hot reload by `patch-run --patch ID --glb PATH`, whose JSON summary has `replaces` (the baked
`patch_<ID>` nodes to hide) and a GLB with one `patch_<ID>` node per patch in the chunk frame (`MapLoader.ApplyPatches`; `patches.json`
feeds the minimap). Zones (`zones`: `{Z-NNNN: {kind, polygon}}`) have no paint and no hot reload: a zone edit changes nothing in the
viewer's world, the build turns zones into candidate points. `EditsFile` keeps both keys as read and writes them canonically (omitted
while empty); `ObjectEdits.ValidateDocument` mirrors the schema (a `regions` key is unknown; a patch needs an existing, not deleted
type and a `water_y` exactly when the type is water; a zone's kind must be in the map's `zone_kinds` when the map lists them). The
editor UI is the Ground tool (patches) and the Zones tool, below.

Assets directory: `--assets DIR`, else env `DRYCASTLE_ASSETS`, else `<repo>/assets`.

Loader API additions: `ApplyFragment(path)` takes a `parcel-run --out ... --glb ...` fragment, hides the baked
`parcel_<ID>_props` visual and collider of every parcel in `replaces`, drops that parcel's previous dynamic
nodes (`dyn_props_<ID>`) and objects, adds the fragment's props nodes with colliders and its objects. It is
idempotent and an empty result removes the parcel's props. `RevertParcel(id)` restores the baked nodes and
objects. `AssetsDir`, `ObjectNodes`, `DynamicProps`, `Warnings` are exposed.

Hot reload: `make viewer` with `--watch` (`godot --path godot/viewer -- --watch`; off by default, so normal
viewing and headless servers are unaffected):

- `--edits PATH` the edits file (default `pipeline/config/edits.json`), `--state PATH` (default `out/editor-state.json`), env `DRYCASTLE_UV` (the `uv` executable).
- The file is polled twice a second. The state at startup is assumed to be what the map was built with.
  A change is diffed per parcel and per group (a group change affects all its members and the other way
  round), and only those parcels are regenerated with
  `uv run python -m drycastle_pipeline parcel-run --parcel ... --edits <snapshot> --glb ... --out ...`
  in the `pipeline` directory (0.3 to 0.9 s) in a subprocess; edits that arrive during a run are coalesced into
  the next one. The viewer prints `edits applied: <ids> in <ms> ms`. A half-written or invalid file is ignored
  until it changes again, a failed `parcel-run` only prints its error. Writers must replace the file atomically
  (temp file, then rename). When only placed `objects` changed (see Objects and undo) there is no subprocess:
  the objects are re-instanced in process.
- Buildings are not in the edits file: they come from the pipeline (`overrides.yaml`, `config/buildings/`, the
  model code) and reach the viewer only through the chunk files. So the map folder's `manifest.json` is polled
  too (`EditsWatcher.Map.cs`). The export writes it last, with a `sha256` per chunk file; when it changes, every
  chunk whose hash differs from what is loaded gets its building nodes (`parcel_<ID>_visual`,
  `parcel_<ID>_collision`, `window_*`) swapped with `MapLoader.ReloadChunkBuildings`, one chunk per frame. The
  viewer prints `map reloaded: buildings of N chunk(s) ...`, the editor shows it in the status line. So after
  changing a building: `make rules export`, no restart. Not reloaded: everything else baked into a chunk
  (terrain, roads; fences, trees and ground patches keep following the edits file). A re-export with different parcels
  or a new chunk is not swapped in; the viewer says to restart.
- `editor-state.json` (written at most once a second while watching, an ignored build output):
  `{"camera": {"x": east, "y": north, "z": elevation, "yaw_deg": view direction counter-clockwise from east},
  "format_version": 1, "selected": []}`; x and y are in the map frame (local metres). `selected` is the list of
  selected parcel IDs while the editor mode is on (empty otherwise).
- `sketch.json` (not in `out/`: one shared file per user at `~/.local/state/drycastle/sketch.json`, `$XDG_STATE_HOME` and `$DRYCASTLE_SKETCH`
  respected, so every worktree's editor, MCP server and streetview follower see the same sketches; written within a frame of every sketch change) holds
  the marks of the Sketch tool in absolute S-JTSK metres, and is the one file the editor and the MCP server both write:
  `{"format_version": 1, "version": 7, "sketches": ["ROAD"], "next_line": 1, "next_point": 1,
  "lines": [{"closed": false, "id": "L1", "points": [[x, y], ...], "sketch": "ROAD"}],
  "points": [{"id": "A", "sketch": "ROAD", "x": .., "y": ..}]}` (see "Sketch tool"). `version` goes up on every write; the editor
  checks the file four times a second and takes a version it did not write over as one undo step. A sketch an older editor kept in
  `editor-state.json` is moved to the sketch `DEFAULT` the first time.
- `make viewer-edit-smoke` exports a fresh map and a work-directory copy into a scratch directory
  (`SMOKE_DIR=...`, default a temp dir), starts the viewer headless with `--watch`, writes edits files during the
  run and checks objects, swapped props, colliders, replacement without leaks and survival of a broken file.
- `make viewer-patches-smoke` does the same for ground patches: exports a map with three patches (each colour is a ground
  type of its own, `paint_<rrggbb>`), then retypes, reshapes, adds and removes patches through the watcher (alone and together
  with a parcel edit in one file change) and checks that only those patches are swapped (`patch-run --glb`,
  `MapLoader.ApplyPatches`; a patch split over chunk files is swapped completely), that repeated swaps do not grow the node
  count and that a broken file changes nothing. (`ViewerPatchesSmoke.cs`, `patches_smoke.py`; not run when it was last changed.)
  `parcel-run`/`export` take `--work DIR` and `export` takes `--out DIR` for such experiments.

### Water

The viewer and the editor draw the map's water (`water.json`) with the same surfaces as the shooter: a flat mesh per body,
low-poly look: a colour that deepens smoothly with the depth and a hard foam strip at the bank (from the body's depth grid in `water.json`,
as a texture, not from the depth buffer), real waves on a 2.5 m triangle grid, shaded flat, lit by the scene's sun
(`MapLoader/Water/`: `WaterSurfaces`, `WaterPalettes`, and the shader `water_surface.gdshader`, embedded in the MapLoader assembly so both Godot projects
use one file). It is read once at load: after changing a water patch (`water_y`, `max_depth`) or anything that moves
the bed, re-export and restart the viewer. The terrain carve is not hot reloaded, and neither is the surface. A water
patch's colour is painted on the carved bed (there is no flat mesh at the surface any more), so with no water drawn
(old viewer, headless) it shows as a sunken patch.

### Ground clutter (near-only)

Maps with `files.clutter` in `manifest.json` (and `files.clutter_meshes`) draw small ground clutter: tufts, flowers, pebbles, rocks, leaves, sticks, ferns.
`clutter.json` lists instances per chunk and kind (`[x, y, z, rot_rad, scale]`, map metres, `rot` counter-clockwise seen from above); `clutter_meshes.glb` has one
mesh node per kind (Y-up, on y = 0, unit scale, `COLOR_0` colours). `MapLoader.Clutter.cs` builds one `MultiMeshInstance3D` per 48 m cell and kind (`clutter_<kind>`,
child of the chunk root): no shadows, no collision, the shared flat material, a visibility range of 70 m (pebble, leaf, stick 40 m, flower 55 m, rock 80 m; all in
`ClutterSettings`) plus half a cell and an 8 m fade. Cells, not chunks, because a visibility range is measured to the node as a whole.
A headless load (`CollisionOnly`, the dedicated server) and a map without the files build nothing and print nothing. `DRYCASTLE_CLUTTER=0` (or `off`) turns it off; a number scales the
distances (`0.5` = half); `MapLoader.ClutterScale` is the same setting in code (set before `Load`). Old loaders ignore the files.

**Tint (map 0.10.0).** An instance entry may carry a sixth number, a tint byte (128 = the mesh's own colours, lower = fresher, higher = drier); the meadow's generated instances of a
layer with the tint flag carry one too. The loader sets it as the MultiMesh instance colour (`UseColors`), which multiplies the vertex colours of the shared flat material, so
variety costs no extra nodes: `ClutterTint.Rgb` (fresh 0.80/0.97/0.78 at 0, white at 128, dry 1.0/0.80/0.55 at 255). A product cannot add yellow to a green, so "dry" reads
as olive; real straw is its own kind. The pipeline lays the tint out as a smooth noise over the ground plus a small jitter (`meadow.py` `tone_noise`, `tint_byte`; `MeadowGen.Tint` mirrors it).

**Draw calls (map 0.10.0).** One node per kind and cell would mean a node for every rare species (the village has 23 kinds). A group with at least `ClutterSettings.MultiMeshMin` (16) instances in a 48 m cell
is a `MultiMeshInstance3D`; the cell's smaller groups are merged into one `MeshInstance3D` (`clutter_merged`: the kinds' triangles transformed into place, the tint multiplied into the vertex colours, the
visibility range of its farthest kind). The village: 89k stored instances in about 1,700 nodes (about 19 MB of merged vertices), against 1,824 nodes for 79k instances before the variety.
`MapLoader.ClutterNodes` lists them.

### Crop fields (#328)

Maps with `files.crops` (`crops.json`, only maps with a crop ground patch; format in `docs/pipeline.md`, recipe `crop`) draw the plants and artifacts of crop fields:
wheat, corn, potato, rapeseed; hay bales, tractor, scarecrow and so on. Rows are `[x, y, z, rot_rad, scale]` in map axes, placed exactly like clutter (`ToGodot`).
`MapLoader.Crops.cs` builds one `MultiMeshInstance3D` per 48 m cell, kind and variant (`crop_<kind>[_<variant>]`) in the chunk's node, with a visibility range
(`CropSettings`: plants 110 m, artifacts 220 m, times `ClutterScale`, plus half a cell; artifacts fade over 8 m). Plants have no edge where they end: over the outer 50 m of their range (`CropSettings.PlantFadeM`) each plant grows in at a
distance of its own (`crop_plant.gdshader`, the plant's own material with the grow-in, around the camera), so the crop thins out and shrinks towards the range. **Cosmetic only: no collision shape, nothing blocks a
bullet**; a headless load (`CollisionOnly`) builds nothing. `DRYCASTLE_CLUTTER=0` turns it off with the clutter. `MapLoader.CropNodes` lists the nodes.

Models, per kind in `crops.json` `kinds`: `asset` (an id of `assets/manifest.json`; rapeseed: `pack:drycastle-generated-models/rapeseed_plant`) or `synty` (prefab names of the
paid POLYGON Farm pack, id `pack:synty-farm/<name>`; several names = variants, one per instance chosen by a hash of its position, so every client agrees). A model is
flattened once (every visible mesh of the prefab, so a tractor comes with its wheels, merged with its node transforms) into one `ArrayMesh` with one surface per pack
material; the materials are the pack's own shared ones (`materials.json`, atlas), so an instance costs a transform and nothing else. **Without the pack** (no
`assets-private/synty/glb/farm`, the kind's ids are not in the private `manifest.json`) the kind is a flat-coloured cone (plants, hay pile, scarecrow) or box
(`CropPlaceholder`, a colour per kind), with one warning per kind; rapeseed keeps its own glb. The pack's layout and how its GLBs are built: `assets-private/synty/README.md`
(Farm section), `scripts/private_pack_glb.gd`.

Crops react at run time, still cosmetic: a harvester's reel cuts them to stubble along its real path and vehicles flatten them (`MapLoader.Harvest.cs`: `CropCut`, `CropCrush`, `CropMarkGround`, `CropReset`; events `HarvesterCut`, `CropCrush`). See `docs/events/harvester.md` and `docs/crops-destruction.md`.

### Meadow clutter (generated near the player)

Fields, meadows and forests are about 400 ha, too much to store clutter instances for. Maps with `files.meadow` (`meadow.bin`, map 0.9.0) carry a density raster instead
(layers tuft, flower, fern, pebble, litter, floor; one byte per 2 m cell per layer plus a zone byte per cell, format 3 in `pipeline/src/drycastle_pipeline/meadow.py`; the zone byte's high nibble picks the species
mix, which says per layer which kinds a cell makes and in what shares, its low nibble is a dryness that biases the tint). `MapLoader.Meadow.cs` turns the cells
near the focus into instances with `MeadowGen` (a hash of the cell and layer, the same on every client, nothing is sent; `MeadowData.cs`, pure, mirrors `meadow.generate`
and a test pins both). The ground is cut into 24 m tiles, each tile and layer is one `MultiMeshInstance3D` per kind (pooled), built nearest first at most 6 tiles or 2 ms per
frame, freed 30 m behind its range. A tile and layer with a kind of at least `MeadowSettings.MultiMeshMin` (48) instances draws it as a MultiMesh, and its smaller groups (a stray species of the patch mix) as one merged mesh
(`meadow_merged`, tint baked into the vertex colours), so a layer with seven flower species is about one or two nodes per tile, not seven (`MapLoader.MeadowNodes` counts them). Ranges and `DRYCASTLE_CLUTTER` are those of the stored clutter; live instances per layer are capped (`MeadowSettings.CapOf`).
Instances within a tree's trunk reach (from `trees.json`) are dropped; the height comes from `GroundHeight` (the terrain collision mesh, decimated). The focus is the current camera
unless `MapLoader.SetClutterFocus(position)` is called each frame. A map without `meadow.bin` and the headless server do nothing.

Forest floor (#287, slice 3): `mushroom`, `branch`, `moss_mound`, `fern_big`, `stump_small` are code-built kinds for machines without the pack. A layer shows to its farthest kind and costs at least one draw per tile in reach, so they are two groups, not five layers: `moss_mound`, `branch`, `fern_big` are the layer `floor` (60 m), `mushroom` and `stump_small` are in the woodland mix of the `litter` layer (40 m, merged with the sticks). Extra draws: the floor layer is about `pi * (60 + 17)^2 / 24^2` = 32 tiles in an all-forest 360 degree view, one merged mesh each (a kind with 48+ in a tile gets its own MultiMesh, so at most 3 per tile, only in the densest tiles); the litter additions are free (already merged). While the pack trees are on, `MeadowSettings.IsSuppressed` drops the kinds the pack's undergrowth also draws (`PackUndergrowthKinds`: mushroom, branch, fern_big); moss mounds and small stumps stay.

Pack trees (#287; `MapLoader.PackTrees.cs`, `PackTreeData.cs`, `TreeCutShaders.cs`, `Shaders/`, `shooter/data/pack_trees.json`): with the private nature pack (`assets-private/synty/glb/nature`) installed, trees whose base is within `near_m` of the focus are drawn as pack models and their baked trees are not.
The cut is in the shaders: a map whose `trees.json` has `model.base_uv2` has UV2 on every props mesh that holds trees (a tree's base in the mesh's own (x, z), `1e6` for fences); those meshes (chunk `props`, `parcel_ID_props`, the dynamic ones of a parcel swap) get `props_tree_cut.gdshader` (the flat vertex-colour material plus the cut) instead of the flat material.
The streamer sets the global shader parameter `tree_cut` (focus, radius, fade band) each frame, so the shadow pass cuts the same trees; `tree_cut_under` is the same for the undergrowth (below). Over the last `fade_m` inside the radius each tree swaps whole (#349): both shaders compare the far share (0 at `radius - fade_m`, 1 at the radius) with a threshold hashed from the tree's world base point (`tree_threshold`, same hash on both sides, mirrored by `PackTreeMath.Threshold`/`IsBaked`), so the baked tree goes and the pack tree comes at a distance of its own and no pixel is ever a mix (the old screen-door dither showed as dots, worst through a scope). Not zoom-aware: through a scope the band looks 19-39 m away (see #349). The radius never passes a tile that is not built yet.
Radius (#287): `near_m` 150 m, `fade_m` 30 m: far enough that the swap is not noticed. Cost, by arithmetic on the village map (a forest view): about 45 tiles of 48 m have their box inside 150 m (38 of them with trees, p90 44, max 47; 13 at 70 m), about 1,100 trees in the disc (p90 1,600, max 1,800) at 370-680 triangles each (doc below: 24k-44k per 65-tree tile) = 0.4-0.8 M triangles (1.2 M at the most), plus about 185 ground objects (26 per ha, 46k) and the undergrowth (below); draws 3-5 per tile (the shared atlas, own leaf textures) = 115-190. Tiles are built within `near_m + ahead_m` (182 m: 65 tiles, about 50 with trees) and freed beyond `near_m + 2 * ahead_m` (214 m, 81 tiles, 70 with trees, about 75 MB of meshes at an average 1 MB a tile), nearest first on `max_in_flight` (4) workers, so the first build after a spawn is about 50 tree tiles and 13 undergrowth tiles = about 250 surfaces; the commit takes at least one surface a frame (`budget_ms`), so it takes about 4 s at 60 fps (1.3 s at the old 70 m), and the cut radius grows with it (it never passes a tile that is not built, so the baked trees stay until the pack trees stand; no holes, only the swap arriving ring by ring). Walking: `ahead_m` 32 (was 16) is 4 s of lead at a sprint (8 m/s) against a build of well under a second, so the radius does not dip; a car (25 m/s) outruns it and the radius dips to the unbuilt tile for a moment. The undergrowth has its own radius: `undergrowth.near_m` 60, `fade_m` 12, `ahead_m` 16. It has no baked twin and is about 48k triangles a forest tile (more than the trees of it), so it is built, kept and drawn only for the tiles within that radius (about 15 of the 65), as a mesh of its own per tile (`pack_under`: one more draw per material it wears, 4 in a forest, so about 9 tiles x 4 = 36 draws in view; in return nothing for the other 30 tiles and no 48k triangles of memory there). The shader tells it from a tree by the vertex colour's alpha (tree 1, ground object 0.5, undergrowth 0) and cuts it on `tree_cut_under`; its radius too never passes an unbuilt tile. Solid ground objects swap at the tree radius.
Far trees, pop-in (#362; `PackCardData.cs`, section `cards` of `pack_trees.json`): the baked tree is a box or pyramid and the pack tree a real model, so the swap at `near_m` was plain to see, worst at a forest edge from a field and through a scope. Beyond the pack radius a tree (and a ground object) is now drawn as three flat cards (front, side, top) of the very pack model, scale, turn and tint it gets near the player, so the swap changes almost nothing.
- Pictures: `CardPainter` draws each model's triangles and textures on the CPU (a worker, at load; no render target) into one atlas, colour plus normals in the model's frame; the mipmaps keep each picture's alpha cover so a sparse crown does not thin away. `cell_px` 128: a model takes 3 x 2 cells, about 2 x 28 MB of video memory for some 50 models.
- Mesh: `PackCardMesher`, 12 vertices a tree, one draw per card tile (`cards.tile_m`, 192 m). `cards.far_m` 0 = the whole map (58k trees on the village map, about 700k vertices), built once, nearest first, never freed; above 0 the baked trees stand beyond it.
- Cut: global shader parameter `tree_cut_far` (x, y: the cards' radius and band; z, w: the far undergrowth's). A baked tree is drawn only where neither its pack tree nor its card stands; it still casts the sun's shadow. `pack_tree.gdshader` lights a card with the atlas' normals (the vertex normal carries the model's X axis) and mirrors them on a card seen from behind.
- Undergrowth: a piece grows out of the ground over the fade band (`undergrowth.grow`, `sink_m`; `tree_grow`, mirrored by `PackTreeMath.Grow`) instead of appearing whole; kinds marked `far` (saplings, shrubs, bushes; swamp logs and roots) are a mesh of their own out to `undergrowth.far_m` (110 m). By arithmetic: near radius 60 -> 64 m and the far kinds (7.7k of 47.7k triangles a tile) over 3.4 times the area: about +50% undergrowth triangles in a deep forest view. Not measured.
- Building ahead: tiles are wanted around the stretch from the focus to where it will be in `lead_s` seconds (`PackTreeMath.Lead`, `Velocity`, `DistToBoxAlong`), so a car or an aircraft does not outrun them.
- Every mesh of a tile is a layer with the state machine `PackTile.Layer` (`docs/state-machines.md`). `cards.enabled` false, `undergrowth.far_m` 0 and `lead_s` 0 give the behaviour before #362 (but for the grow-in).
Pack trees are merged per tile (`tile_m`, 48 m) and material into one mesh (`pack_tree.gdshader`: albedo texture and colour, alpha scissor, cull mode of `materials.json`), built on worker threads and handed to the renderer within `budget_ms`; a tile costs one draw per material (bulk models share the atlas). The model of a tree is a hash of its position among the models whose scale to the code-built tree's height lies within `scale_min`..`scale_max`, bulk models by `bulk_share`.
Variety (#287): the model comes from the tree's **stand** (trees.json `stand`: `pine`, `birch`, `mixed`, `thicket`, `swamp`; absent = mixed) and **edge** flag (`edge: true`, a tree at the forest edge). `stands` in `pack_trees.json` maps a stand to model lists (`cone`/`box`, or `any` for both; its own `bulk_share`; a stand or shape without models takes `shapes`): pine = the PolyPine/Pine families, birch = birches and slender round trees, thicket = small pines and birches, small round trees, bushes, swamp = dead trees, willows, bare swamp trees and one swamp-pack LOD. An edge tree takes the `edge` stand (round, bushy broadleaf) with probability `edge_share` (a position hash) unless its stand says `edge: false` (swamp, thicket). The bulk/occasional split and the scale span apply per stand as before. The tags are read from trees.json beside the collision parse (`PackTreeTags`, `_packTags`; the colliders ignore them), so the stand costs nothing at runtime beyond the model list.
Packs: every model entry may name its `pack` (a folder of `assets-private/synty/glb`, default `nature`; names are unique per pack). The swamp pack is loaded beside nature when installed; a pack that is not installed has its models skipped with one warning, and the nature pack stays the only one a map needs. An LOD of a pack is a model by its own name: the swamp pack's trees are 12k-30k triangles at full detail (3.5k-8k even at LOD2) except `SM_Env_Tree_Swamp_04` (3666; LOD2 960, used for the swamp stand) and the mangroves (2.7k-3k; LOD2 about 850), so the swamp stand uses `SM_Env_Tree_Swamp_04_LOD2` and the other swamp-pack trees stay out. Models flagged `materials_guessed` in the manifest (the `SM_Swamp_Tree_*`, `SM_Prop_Stump_*`, tall grass clumps) are not used.
Draws a stand adds, per tile that holds the model (the white PolygonNature atlas, with all trunks, is the one draw every tile has): pine 0 (Leaves_Pine), birch 0 (Birch trunk, birch leaves), thicket 0 (Plants tint), swamp up to +4 (dead-branch leaf card, willow leaves, swamp-pack atlas, swamp-pack leaf), edge 0. Expected triangles of a tile with 65 trees (the village maximum), by stand: pine 23.5k, mixed 24k (cone) to 36k (box), thicket 21k, birch 38k, swamp 44k, edge 43k.
Colour (#287): the pack's atlas gives every tree of a model the same greens, the baked trees had several. The mesher writes a tint per tree into the vertex colour (`PackColourMath.Tint`: a hash of the position, a multiplier on the foliage stored halved in rgb; `hue_shift` toward yellow-green or blue-green, `value` lighter or darker, and `accent_share` of the trees one of the `accents`: spring green, olive, teal, golden), and `pack_tree.gdshader` applies it only to texels that are green (`leaf_lo`..`leaf_hi` on `(g - max(r, b)) / g`: trunks, bark and rocks keep the atlas) with `saturation` and `gain` on top. The undergrowth gets `undergrowth_scale` of the variation (0.5); ground objects none. All of it is the `colour` section of `pack_trees.json` (`enabled: false` = the atlas as it was). The vertex colour costs 4 bytes a vertex.
Fallen trees (#287; `GroundObjectMath.PlaceLog`, `FallenConfig`, `ground_objects.fallen`): a log record lies along `u`, 4-9 m long, 0.3-0.6 m thick (`scale` x 0.45), thick end at -u, a size box as long as the log, as wide as 0.7 of the thickness and 0.65 of it high (`GroundObjectColliders.LogBox`, which `PackGroundData.BoxOf` asks; not solid, #359). A `log` model entry with `lay: true` is an upright model (a dead tree) laid down: tipped a quarter turn about a horizontal axis before the yaw (`PackPlacement.Pitch`), turned about its own axis by a hash (`Roll`), its base at the thick end and the middle of its height on the record's point, scaled uniformly so its height is `len` (inside `scale_min`..`scale_max`), thinned across by at most `stretch_max` (1.25) when its trunk (`PackModelMesh.TrunkRadiusM`, the 30th percentile of the vertices' distance from the axis between 10% and 50% of the height) would stand out of the box, and raised so the trunk rests on the ground with `sink` of its diameter buried. A model whose trunk still does not fit, or whose scale is outside the span, is skipped for that record; branches may stick out of the box. Models without `lay` (`SM_Tree_Log_01/02`, 2.3-3 m) are used as they are, stretched at most `stretch_max`, or on a longer record the fewest end to end that stay within it (`max_pieces`, weight x `piece_weight` for two or more); when nothing fits the nearest is stretched anyway (a baked log must not vanish). The models that fit share the choice by weight and a position hash. Chosen: `SM_Tree_Dead_01/02` (436 and 296 triangles; 3.5 and 2.7 m with trunks 0.1-0.24 m: logs of 4-7 m), `SM_Tree_Pine_Dead_01` (310; 6.7 m, trunk 0.4 m: the nicest, fits 4-9 m logs in part by thickness) and the swamp pack's `SM_Env_Tree_Dead_01` (342; 9.1 m, trunk 0.4 m: 5-9 m logs). Left out: `SM_Tree_Dead_03` (1.2 m), `SM_Tree_Birch_Dead_01` and `SM_Tree_Generic_Dead_01` (trunks 0.5 and 0.7 m, wide crowns), the swamp-pack logs and trunks (`SM_Prop_Swamp_Log_*`, `SM_Env_Swamp_Log_*`, `SM_Prop_Swamp_Trunk_01`: 1.8-15 m thick, chunks of the swamp scale that a 0.3-0.6 m collider cannot hold) and `SM_Env_Tree_Dead_02` (1 m trunk).
Hot swap: `Load` runs `PlanPackTrees`, which frees every tile (in-flight jobs are dropped with their tile), the packs, models, tags and undergrowth sets, and `InitPackTrees` builds them again, so a map reload cannot leave a stale tile. A parcel swap (`ApplyFragment`, `RevertParcel`) and a patch swap never touch trees.json, ground_objects.json, the meadow raster or the terrain heights the tiles were built from (the parcel run's `trees` is empty by design: trees come from the orthophoto, `parcel_run.py`), so no tile goes stale and none is rebuilt: rebuilding would only make the baked trees reappear for a moment (the cut radius shrinks to the nearest unbuilt tile).
Without `base_uv2` (old map), without the pack or with `DRYCASTLE_PACK_TREES=0` nothing changes (one warning for the old map). Collision is unchanged. Counters: `MapLoader.PackTreeDrawn` (tiles, draws, trees, triangles of everything, ground objects, undergrowth pieces); `--perf-route` writes them to `route-frames.csv` (`pack_objects`, `pack_undergrowth`) and `route-summary.json`.

Forest ground (same tiles, same draws; `PackGroundData.cs`, sections `ground_objects` and `undergrowth` of `pack_trees.json`):
- Ground objects: `ground_objects.json` next to `trees.json` (optional; missing = none, silently; needs `model.base_uv2`). The baked object carries its base in UV2 like a tree, so the props shader hides it inside the radius and the pack model of its kind (log: `SM_Tree_Log_*`, stump: `SM_Tree_Stump_*`, boulder: `SM_Rock_*`, rootplate: `SM_Tree_Stump_02`, still a stand-in: no model of the two packs is a disc on edge of about 2.2 m x 2.2 m x 0.4 m, the nearest are the stumps with root flare, 1.9 m x 2.4 m x 2.8 m; the `Swamp_Root*` and `Tree_Swamp_Root*` are 0.2 m x 0.5 m x 1 m twigs, the `Env_Swamp_Tree_Root_Preset_*` 6.5 m x 3.9 m x 6.7 m splayed root balls) is drawn there, picked by a hash of the position. Fit: the model's horizontal radius = `radius_m` (the file's `model.kinds.KIND.radius_m` wins, else the config's) x the object's `scale`; a log is stretched along the model's Z to its `len`; `yaw_deg` is added to `rot`. Collision is the code-built shape, none for a log (#359). The cut is the one shared UV2, so it cannot skip a kind: every kind of the file needs a model or its baked objects vanish near the player (one warning).
- Undergrowth: client-only, no collision, no shadows, no baked twin, per meadow mix (`undergrowth.mixes`, each `{ mix, name, kinds }`; the mix is the high nibble of `meadow.bin`'s zone byte). Mix 5 (forest): ferns, bushes, mushrooms, twigs, small rocks, grass, plus the understory between knee and head height: saplings (`SM_Tree_Pine_Small_*`, `SM_Tree_Birch_Small_01` at scale 0.25-0.5, 0.4 per 100 m2) and tall shrubs (hedge bush, bush, bush leaves, 0.6 per 100 m2), both clumped and thinned at trunks (`foot` 0.3 and 0.4: `foot` below 1 is fewer pieces at a trunk). Mix 9 (swamp, written by the pipeline for swamp ground; a map without any such cell is unaffected, and a cell of a mix that is not listed grows nothing): reeds (`SM_Env_Reeds_*_LOD1`, 32 triangles), swamp grass (`SM_Env_SwampGrass_*_LOD1`, 24-32), moss mounds, roots, logs and branches (the last three are nature-pack models: non-solid clutter, sparse). No lily pads: they need water under them and nothing here can restrict a kind to water. The main thread reads the tile's cells from `meadow.bin` (a cell counts where its zone mix is configured and some layer has density: free ground) and snapshots the terrain; the worker scatters from a hash of each 2 m cell (same on every client, tiles agree at their borders), clumped (`clump`), denser at trunks (`foot`, `foot_m`), clear of trunks (`trunk_clear_m`), then thins by position hash down to `tris_per_tile`, one budget for all mixes of a tile. Beyond `undergrowth.near_m` (60 m) it is gone and not built; inside its own `fade_m` each piece drops out whole at its own distance, like the trees. A tile needs a tree or a ground object to be built; a map without `meadow.bin` has no undergrowth. The swamp models are loaded with the map whether or not a cell has the mix (a few small GLBs).
- Triangles of undergrowth in a full 48 m tile of forest floor, at mean density: ferns 10.6k, twigs 10.5k, grass 13.7k, mushrooms 3.6k, saplings 3.3k, shrubs 2.7k, bushes 1.7k, rocks 1.6k = 47.7k (was 41.7k) against `tris_per_tile` 60,000, so the budget is unchanged; clump and the density at trunks move single tiles around that, and the thinning takes what passes the budget. A full tile of swamp floor: about 13.6k.
- Draws: all models but the Plants ones (ferns, bushes, shrubs, grass) use the atlas of the bulk trees, except those with their own leaf texture (the saplings: the pine and birch draws the trees already have; the leaf-card shrubs `SM_Plant_Bush_Leaves_*`: the birch leaf draw); the Plants atlas is tinted, so it adds one material, one draw per tile. The swamp mix adds reeds, swamp grass and moss mounds, one draw each, on tiles that hold swamp floor only. Remove those kinds from the config to drop them.

## Editor mode

The first visible part of the world map editor: `--editor` starts the viewer in editor mode (it implies
`--watch`); F2 toggles between walk mode (unchanged) and editor mode. Needs `uv` for the hot reload.
The View page has an **Aerial photo** layer (O) to edit by eye; it is a download for reference only, see `docs/ortho.md`.

```sh
# against the default edits file (pipeline/config/edits.json)
dotnet build godot/viewer/viewer.csproj
godot --path godot/viewer -- --editor --map out/village/map
```

Options: `--edits PATH` (default `pipeline/config/edits.json`), `--state PATH` (default `out/editor-state.json`),
plus the usual `--map`, `--seed`, `--assets`.

| Key | Action |
| --- | --- |
| F2 | toggle walk / editor mode; walk mode puts the player back where it was |
| W A S D, Q / E | fly, down / up; Shift is 3x faster |
| right mouse (hold) | look around (the mouse is only captured while held) |
| mouse wheel | change the fly speed (shown in the top-left HUD) |
| left click | select the parcel under the cursor; click on nothing clears |
| Shift + left click | add or remove that parcel in the selection |
| G | create a property group (`G-NNNN`) from 2+ selected parcels |
| T | snap to a top-down view above the cursor; again restores the previous camera |
| B | floor plan of the selected parcel: its buildings hidden, wall lines and a 1 m grid shown; again restores them |
| (Street View) | `streetview_follow.py` moves the character to where a Chrome Street View tab is; in the top-down view (T) a red point marks the spot below it, see `docs/streetview.md` |
| Esc | stop placing / deselect the object / clear the parcel selection (in that order) |
| Ctrl+Z, Ctrl+Y (Ctrl+Shift+Z) | undo / redo the saves made in this session (see Objects and undo) |

Hovering shows `P-0415 garden` (and `[G-0007]` when grouped) at the cursor. Outlines are unshaded ribbons draped
0.25 m over the terrain: yellow for the selection, faint white for the hovered parcel. Nothing is drawn by default: a property group
outlines only when one of its members is hovered or selected, and then as one shape (edges shared by two members are
not drawn). The right-hand panel shows the
selection (ID, kind, area, cadastral reference, group), a collapsible Group section with the buttons Group
selected, Add selected to group, Remove from group, Merge groups and Dissolve group, the Fence section (below) and
the Objects section (palette, selected object, undo; see Objects and undo). The bottom line shows messages (`saved`, `regenerating P-0004...`, `applied in 480 ms`, errors) and the key hints.

Merge groups (button, or `G` when the marked parcels touch any group) puts all parcels of those groups, plus any marked ungrouped parcels, into one new group (so a group plus a loose parcel also works) (highest id + 1) and removes the old ones. The fence is the one the groups share, else the first group's (by id; the status line says so); parcel-level fence edits stay on the parcels. It is one undo step, and `EditsFile.MergeGroups` follows the same rules as the MCP server's `merge_groups`.

Group operations rewrite `edits.json` atomically (temp file, then rename). The file is reloaded from disk
right before every save, so a change by another writer (e.g. the MCP server) is kept, and everything not
related to groups (fences, trees, objects, unknown parcels) is written back unchanged. The text is exactly what
`drycastle_pipeline.edits.dump_edits` writes (sorted keys, indent 1, Python float formatting, trailing
newline), so both writers give byte-identical files; `pipeline/tests/fixtures/edits_roundtrip.json` is the fixture
for that. A parcel is in at most one group; `G` refuses (status line) when a selected parcel is already in one.
The watcher then regenerates the affected parcels (the fence runs along a group's outer edge) by itself.

### Fence panel, Apply and gates

The panel header names the **target**: `P-0084` (one parcel), `3 parcels` (several loose parcels; Fence
applies to every one) or `G-0001 (3 parcels)`. A selection that is exactly one
property group's members targets the group: Fence goes to the group entry. With one
grouped parcel selected, `Edit: whole group | this parcel` chooses (default group); "this parcel" writes the
parcel's own fence override, which wins field by field over the group's.

* **Fence**: `mode` auto | none | custom; `type` picket | wire | brick_red | brick_white | stone_wall | concrete
  (textured styles, see `docs/pipeline.md`; brick_wall is the old name of brick_red; the table lives in `EditSchema`,
  `EditModel.cs`, and `make viewer-panel-smoke` compares it with `edits.py`); the `column` key (other posts or
  piers than the type's own) has no control in the panel yet: set it through MCP or by hand, the panel keeps it
  when it writes; `height`, `thickness` and `colour` (a tint: the paint of picket, wire and concrete)
  each have a `set` box: unset means the type's default, which is shown greyed. These fields and the gate list are
  custom-only and disabled otherwise; switching to auto or none clears them, so the panel never writes a file the
  Python validator rejects (it also checks what it writes before saving). `Reset` removes the fence entry (rules
  again, or the group's fence for a member). The line under the mode says what the rules do in auto.
* **Gates**: `Add gate` arms the tool (custom mode, one parcel or one group, a map exported with
  `origin_epsg5514`, i.e. 0.4.0+). Click on the ground within 6 m of the target's fence boundary (a group's outer
  perimeter, a parcel's own edges): the nearest boundary point is picked. Hold and drag: the width is twice the
  distance from the picked point (the gate is centred there), snapped to 0.5 m, at least 1 m; a click without
  drag gives 3 m. A cyan ribbon with two posts previews it, release adds the gate (`at` in absolute S-JTSK
  metres, to 1 mm), Esc cancels. Existing gates are orange markers; `gate N` in the list selects (magenta), its
  width spin changes it, `delete` removes it.
* **Trees are not edited per parcel** (the Trees tool, `set_trees` and the `trees` key of `edits.json` were removed):
  the orthophoto detector (`docs/ortho.md`) is the only source of trees. To add one by hand, place a tree object
  (`pack:kenney-city-kit-suburban/tree-large` or `tree-small`) with the Objects tool. To remove one, add a circle to `pipeline/config/tree_overrides.yaml` (see `docs/ortho.md`).
* **Apply**: changes are pending per target (`* unsaved changes` under the header, `N unsaved targets` at the
  bottom; selecting something else keeps them). `Apply` (button, or Enter when no text field has focus) reloads
  `edits.json`, writes every pending target in one atomic save in the canonical format and drops what became empty
  (a parcel entry with nothing left disappears, so resetting returns the file to its original bytes); the watcher
  then regenerates and the status line shows `saved`, `regenerating ...`, `applied in N ms`. `Revert` discards the
  current target's pending changes, `Revert all` all of them. Pending values replace the target's fence
  entry as a whole, so an external change to that same entry made meanwhile is overwritten on Apply.

`make viewer-panel-smoke` (`SMOKE_DIR=...`, `SHOTS=dir` for a windowed screenshot of the panel) drives the panel
API headless on a scratch export: exact file contents after each Apply, the Python validator on every saved file,
group and multi-parcel edits, pending state across selections, gates (on the boundary within 1 cm, widths, a gap
that really cuts the fence collider), fence none removing colliders through the watcher, and reset restoring the original bytes.

### Objects and undo

The Objects section holds Undo / Redo buttons, the **selected object** (below) and the **palette**: every asset of
`assets/manifest.json` as a button in a collapsible group per category (car, slab, pot, flower, bush, tree, wall,
fence, other; groups start collapsed), plus a filter box (name, id or category; groups without a match hide,
the others open).

* **Placing**: choosing an asset arms placement. A semi-transparent cyan **ghost** follows the cursor on the
  ground, snapped to 0.5 m in absolute S-JTSK metres, or free while Ctrl is held. Any ground will do, a parcel is
  not needed (a pole beside a road is placed like a car in a yard).
  `R` / `Shift+R` rotate the ghost +-15 degrees, a left click places it (appended to the top-level `objects` list
  of `edits.json`, which is written sorted by id), and placement stays armed. Esc or a right
  click without a look drag disarms. New ids are `o-0001`, `o-0002`, ... (one above the highest `o-NNNN` in the file;
  other id forms in the file are left alone). A placed entry is `id`, `asset`, `position` [x, y] (3 decimals),
  `rotation_deg`; `scale` and `colour` appear only when set, `z` never (the terrain height is used).
* **Selecting and moving**: a click on an object (ray against its box, grown by 15 cm) selects it and takes priority
  over the parcel under it. A yellow box, a cyan **rotation ring** at its feet (unshaded, always visible) and an
  arrow with a magenta knob showing `rotation_deg` are drawn. Drag the body to move it along the ground (0.5 m
  grid, Ctrl = free; the grab point keeps its offset), drag the ring to rotate (15 degree steps, Ctrl = free,
  angle counter-clockwise from east like `rotation_deg`). Arrow keys nudge 0.5 m in the camera's screen directions
  (snapped to the map axes) while the mouse is over the viewport; `Delete` removes, `Ctrl+D` duplicates 1 m east
  and selects the copy, `R` / `Shift+R` rotate the selected object by 15 degrees. An object may be moved anywhere:
  across a parcel boundary or onto ground that is no parcel (only its position changes; the inspector names the
  parcel it stands in, if any).
* **Fields**: asset (read-only), `x` / `y` in S-JTSK metres (3 decimals), `rotation`, `scale` (> 0), a `colour`
  with a `set` box (committed when the picker closes), [Duplicate], [Delete]. Each committed edit is one save.
* **One gesture, one save**: place, drag end, field commit, nudge, delete and duplicate each reload the file,
  apply, validate with the Python validator's rules (`ObjectEdits.ValidateDocument`, it never writes a file
  `edits.validate` rejects), and write it atomically in the canonical format.
* **Instant objects**: when the watcher sees that only placed objects changed (no fence or group changed, no
  ground patch; zones do not count), it does not run `parcel-run`: `MapLoader.ApplyObjectChanges` removes the objects that are gone and
  re-instances the ones that are new or differ, straight from the file (S-JTSK minus `origin_epsg5514`, terrain height
  when `z` is absent), with the same node names (`objects_<parcel>/object_<id>`) and grouping as a fragment, so a
  later fragment swap stays consistent. The same happens for the objects of a save that also needs a run. The editor calls it right after saving (`EditsWatcher.NotifySaved`, about 1 to 3 ms from reading the
  file to the nodes being in the tree); an external writer (MCP, a text editor) is picked up by the poll (at most
  0.5 s). The watcher recognises its own write: the new state is the applied state, so nothing runs twice. Mixed
  changes still go through `parcel-run`. Generated trees avoid placed objects, so a tree under a new object
  only disappears at the next regeneration of that parcel (a fence Apply, or a restart).
* **Session undo/redo**: `Ctrl+Z`, `Ctrl+Y` / `Ctrl+Shift+Z` (or the buttons) undo and redo every save the
  editor made since it opened: objects, group operations, Apply of fence. Each save records the before
  and after content of the top-level entries it touched (`groups.<ID>`, `parcels.<ID>.fence`, each object by id,
  absent included). Undo
  reloads the file from disk, writes the before-entries back (absent deletes the entry), validates and saves; the
  watcher (or the instant path) applies the effect. **Entries somebody else changed in the meantime are
  overwritten only if this operation touched them**; everything else in the file stays. A new edit clears the
  redo stack, the stacks live in memory only and hold at most 200 operations. The status line shows
  `undo: <what>` / `redo: <what>`.

`make viewer-objects-smoke` (`SMOKE_DIR=...`, `SHOTS=dir` for two windowed screenshots) drives all of this headless on
a scratch export, through the editor APIs and key events: the palette against the manifest, the exact text of
`edits.json` after each of 26 saves (checked again with `edits.validate`, `dump_text` and `plan_edits`), no
`parcel-run` for object edits (the watcher counts them: `RunCount`, `InstantCount`), scene nodes against the file,
objects still right after a group and a fence Apply, drag and rotate with snapping, moves across parcels and onto
ground that is no parcel, refusals, field edits, nudge, duplicate, delete, undo and redo of the whole sequence byte for byte, the 200 cap and
the latency. Screenshot options: `--shot-object ID --shot-arm ASSET --shot-ghost X,Y --shot-ghost-rot DEG
--shot-filter TEXT --shot-open car,pot` next to the `--editor-shot` ones.

Classes: `EditorMode` (camera, picking, selection, outlines, UI, group operations; `EditorMode.Edit.cs`: the Fence panel, pending state, gate tool; `EditorMode.Objects.cs`: palette, ghost, gizmo, object gestures; `EditorMode.Undo.cs`: session undo), `ObjectEdits` (object entries, ids, C# validator), `EditModel` (drafts, schema, applying pending changes), `EditsFile` (reader,
canonical writer and group operations for edits.json), `ParcelInfo` (now with `Rings`, `Bounds`, `AreaM2`, `Group`
and `Contains`) and `ParcelIndex` / `MapLoader.ParcelAt(x, z)` (grid index over the parcel boxes; about 5 us per
lookup).

`make viewer-editor-smoke` (`SMOKE_DIR=...` for the scratch directory) exports a map into a scratch dir,
starts the viewer headless with `--editor-smoke` and drives the editor through its API: F2 round trip, top-down
snap, picking, selection, outlines (shared edges not drawn), creating and dissolving a group, saved file content
checked byte for byte against `dump_edits`, `editor-state.json`. Screenshots of the editor mode (windowed, needs a
GPU): `godot --path godot/viewer -- --map DIR --edits FILE --state FILE --editor-shot out.png --select P-0003
--shot-at P-0003 [--hover P-0005] [--shot-edits FILE] [--shot-collapse Group,Fence] [--shot-gate N] [--shot-focus X,Y --shot-dist D]`
(`--shot-edits` writes that edits file first and waits for the regenerated parcels).


### MCP server for parcel edits

`pipeline/src/drycastle_pipeline/mcp_server.py` is a stdio MCP server (`make mcp`, normally started by Claude Code via
`uv run`) that edits `pipeline/config/edits.json` in plain language: "park a car in front of the house on the selected
parcel". It uses the pipeline's own validator, reads the viewer's `editor-state.json` for the selection, and the
viewer's `--watch` applies the change. Registration, tool reference and workflow: [mcp.md](mcp.md).

### Poles tool

Rail icon 7 (View moved to 8): utility poles and the cables between them (`docs/utility-poles.md`). Poles are ordinary
placed objects (`prim:pole_*`), so everything under Objects and undo applies to them; the page shows the poles alone as
renders (the name is the tooltip), and adds the cables.

* **Planning** (`P`): left click where poles should stand, right click (no drag) a house wall or roof to connect it;
  a point clicked again is removed, Backspace removes the last, Esc leaves plan mode (the plan is kept in memory).
  What will be built is drawn while planning (`PolePlanner.cs`): cyan posts with their crossarm, cyan links, red for
  a house with no pole to go to. There is no limit on how far apart two poles may be (the planner is flat, height plays
  no part). `Build poles` (Enter) places a pole at every point,
  strings the cables and connects the houses in one save (one undo step) and says what it skipped. Links follow the
  roads of `roads.json`: each point belongs to its nearest road and to one side of it, and the poles of one side are a
  **stream**: each is joined to the next along the road, so a street with poles on both sides gets two lines that run
  along each side, not a zigzag. The ends of a stream are joined, through junctions, to the nearest stream of the next
  road, loops left out. Where the two streams of a road face each other a wire also crosses the road (between poles
  that stand across from each other, at most one per 80 m of road, `PolePlanner.CrossEveryM`). A point over 25 m from
  every road joins its nearest neighbours; a house connects to its nearest pole. The pole follows from its place: three links or
  more a junction pole, a line end or a turn over 30 degrees a twin pole, off-road or on a track or path a field
  pole, a megaphone pole about every 150 m of plain street, else a lamp or plain pole. Planned poles are turned:
  crossarm across the line, lamp over the road. Existing poles are ignored.
* **Placing**: choose a pole, click on the ground; it stays armed, `R` / `Shift+R` turn the ghost. A pole's wires run
  along its own x axis (the knob of the rotation ring) and its lamp reaches to the right of that.
* **Connecting**: `C` (or the button) turns connect mode on. The first click picks the pole to start from (the selected
  pole when there is one); each further click on another pole strings a cable from the last pole and carries on from
  the clicked one, so a street is a row of clicks. A line from the start pole follows the cursor (cyan over a pole a
  click would join). Esc or a right click stops. A click on a pair that is already joined only moves the start on.
* **Houses**: in connect mode, after a pole, a click on a house wall or roof fixes a house connector there (a placed
  object `prim:connector_*` with a `z`; a wall or a roof kind after the surface, which one picked from the spot) and
  strings a cable to it, in one save. The start stays on the pole, so the next click can be another house. A click on
  a connector that exists strings a cable to it. The selected connector has "Another kind of connector".
* **Cables**: a cable is `cables.<C-NNNN>` in `edits.json` with `from` and `to`, the ids of its two ends (poles, or a
  pole and a house connector). A click on a
  cable selects it (yellow), `Del` removes it; the selected pole lists its cables with a Remove button each. Deleting a
  pole removes its cables in the same save. Each of these is one save and one undo step.
* **Nothing is turned for you**: a pole keeps the rotation you gave it. The wires are matched left to right across each
  span (`ShooterCore/Environment/CableWires.cs`), so they keep their spacing and never cross, whichever way a pole
  faces; turn the pole so that its crossarm stands across the line.
* **Instant**: cables need no pipeline run. `MapLoader` strings them between the poles as they stand (`SetCables`,
  `RebuildCables`), the watcher hands it the file's cables on every change, and the cables of a pole follow it while it
  is dragged. At start the cables of the edits file are shown even when the map was exported before them; a pole placed
  since the last export, like any placed object, appears only after the next `make props export`.

There is no headless smoke for this tool yet.

### Sketch tool

Rail icon 9. Temporary marks on the ground that a prompt can name ("plant trees inside L1", "a lamp at A", "everything in
sketch ROAD"), read by the MCP server's `get_sketch`. Marks are grouped in named **sketches** (`ROAD`, `FENCE_PLAN`, ...; several
at once); a sketch holds any number of **points** (`A`, `B`, ... `Z`, `AA`, ...) and **lines** (`L1`, `L2`, ...), whose names are
unique across all sketches. Every mark shows its name as a tag in the 3D view, in every tool (with the sketch in front,
`ROAD · L1`, when there are several). The marks are not map content: nothing is written to `edits.json`. They live in
`sketch.json`, so they survive a viewer restart, until cleared. Claude draws into the same sketches through the MCP server
(`sketch_add_point`, `sketch_add_line`, `sketch_add_road`): they appear within a second as one undo step.

- **Sketches**: the Sketch panel's picker chooses the active sketch, the one new marks go into (a first mark makes `DEFAULT`);
  selecting a mark makes its sketch active. New makes an empty sketch, Rename and Delete act on the active one (names are 1 to 24
  letters, digits, `_` or `-`, unique ignoring case; Enter applies, Esc cancels; Delete asks first). Every one is an undo step.

- `P` / `L` (or the two buttons) choose what a click makes. **Point**: each LMB (or `C`, the spot under the cursor, the
  screen centre when the cursor is over the panel) marks a point. **Line**: LMB or `C` adds corners (0.5 m grid, Ctrl
  free); Enter, or a click on the last corner again, finishes it; Shift+Enter, or a click on the first corner, closes
  it (3+ corners, refused with the reason when the outline would cross itself). A closed line means the ground inside.
  Enter with one corner makes a point. Backspace or Ctrl+Z takes the last corner back; a ghost shows the next mark.
- Click a mark's handle or its row in the panel, or step with `[` `]`, to select it; `Del` deletes it. "Clear the
  sketch" removes the marks of the active sketch (asks first; the sketch stays) and the names start again at `A` and `L1`
  once no mark is left.
- Every sketch change (a new mark, a delete, a clear) is one step on the editor's undo stack, in order with the saves:
  Ctrl+Z / Ctrl+Y. Esc steps back one level: the line being drawn, the selection, the tool.
- Checked by the build and by the MCP server's sketch tests on the file format only; there is no headless smoke for the
  tool yet (the picker, the naming row and the reload from `sketch.json` are not covered).

### Ground tool

Rail icon 4 (`Ground`). One page, top to bottom: the ground map switch, the ground types, the ground patches, a New type row and the
form of the selected type. Ground is terrain only (ADR 0022): every point of the map has exactly one ground type and a type is its
colours, detail recipe and whether it is water; what a place means in play (Bigfoot, deer, player spawn) is a zone (Zones tool, below).

**Ground types.** Lists every ground type of the map (swatch of the first colour; name, what it does, and a tag when you edited it, made
it, or deleted it) and edits one at a time. The form ticks the **attributes** the type carries (ADR 0022 catalog): **Appearance**
(Terrain colours: swatch, picker, `#rrggbb` field, add up to 6, remove; Detail recipe: dropdown, with "Advanced detail settings", the
effective `detail` overlay as read-only text plus number fields for furrow probability, headland keep and tone patches per ha, from the
one table `GroundTypeEdits.DetailFields`) and **Water** (water body: a patch of it needs a water level). A type with only colours is plain
paint: a colour you want that no type has is a ground type of your own. A type that takes OSM land or is made by a rule cannot untick its
last appearance or water attribute (the checkbox is disabled with the reason as tooltip); OSM values (comma lists for `landuse` and
`natural`; read only for a rule type). Buttons: New type (name field, Create: one green shade and the field recipe; or Duplicate selected,
which copies the attributes but not the OSM tags), Reset to rules (removes your override, also brings a deleted type back), Delete (asks
first; disabled for the default type, the dike and a type some patch uses, with the reason). Ticking or unticking Water on a type a patch
uses is refused with the reason (each patch of a water type needs its water level, a land type refuses one). Rename does not exist:
names are IDs (a disabled button explains it). `edits.json` `ground_types` lays over `rules.yaml`; a type's attributes may only be
`colours`, `recipe`, `detail`, `water` (`spawns`, `event_spawn`, `wildlife` are unknown). A banner says a type change shows after the next
full map build (`make map`).

**Ground map** (`EditorMode.GroundMap.cs`): while the tool is open every ground area is drawn on the terrain in its type's colour
(parcels.json `ground_areas`, as of the last build, plus the hand-drawn patches, shown before the next build). Selecting a type
brightens its areas (white edge) and dims the rest; the list rows say "N areas, X ha" (or "none on the map"); hovering the ground names the
patch or type under the cursor, clicking selects it (click the same type or empty ground to deselect). `M` or the "Show areas on the map"
checkbox toggles it (remembered in `editor_ui.cfg`). Built one type per frame with a progress line; a map built before `ground_areas`
says to build it again.

**Ground patches** (`EditorMode.Patches.cs`, the polygon machinery of `EditorMode.Shapes.cs` shared with the Zones tool). A patch is a
hand-drawn polygon of exactly one ground type, plus a water level when the type is water; where patches overlap the higher ID wins.
Select a type, then **Draw patch of this type** (`N`): LMB or `C` adds corners (0.5 m grid, Ctrl free), Enter closes the shape and
creates `GP-NNNN` (one save, one undo step; clicking the first corner closes too). A water type asks for the water level first (a row
with a number field, starting at the terrain height under the shape; Enter creates, Esc goes back to the corners). Select a patch by
clicking it in the world or its row (this selects its type too); drag its corners, `[` `]` plus arrows, change its type in the form
(a water type gets a starting water level, a land type loses it), edit the water level, `Del` (asks first); each is one save and one undo
step. Invalid shapes are refused before commit with the reason in the panel (a red preview outline while drawing or dragging). Until
`make map` the world shows a patch as paint in its type's first terrain colour (`patch-run` through the watcher) and the ground map
shows it as an area of its type; grass, trees and water come with the build. Details and the keyboard walkthrough:
`docs/patches-zones-ui-notes.md`.

Keys: `4` opens, Tab reaches the list, `N` draws a patch, `Shift+N` the New type name field, `M` the ground map, `Del` deletes the patch
or type last selected (asks), Ctrl+Z undoes, Esc steps back (a field, the drawing, the patch, the type, the tool); `Ctrl+P` lists tools,
actions, types, patch IDs and zone IDs. A map without `ground_types` in its `parcels.json` shows an explanation to build it again.

### Zones tool

Rail icon 6 (`Zones`, `zone` icon). A zone is a hand-drawn polygon of exactly one zone kind (`zones` in `edits.json`, `Z-NNNN`); it marks
what the area means in play and never changes the ground; zones overlap freely and add up. The kinds are a fixed catalog in code
(`pipeline/.../zones.py`), listed in the map's `parcels.json` `zone_kinds` (name, label, group, tint, `where_ground`): `bigfoot`,
`player_spawn` and one per wild species. The page lists them (swatch = the tint, label, how many zones are drawn of it, and `rule: forest,
meadow` for a kind that also applies by rule wherever those ground types lie, with nothing to draw); pick one, **Draw zone of this kind**
(`N`): corners as for patches, Enter creates `Z-NNNN` (one save, one undo step; no extra step). Select a zone by clicking it or its row
(this selects its kind), drag corners, `[` `]` plus arrows, change its kind in the form, `Del` (asks first).

**Zone overlay** (`M`, or the checkbox; remembered in `editor_ui.cfg`; same key and name as the ground map, only while the tool is open):
every zone is drawn translucent on the terrain in its kind's tint with a thin edge. **A selected kind is shown alone**, also with the
overlay off: the other kinds are not drawn, hovered or clicked, and its zones get moving bands of tint and white (still with reduce
motion) so they read on ground of their own colour; `Esc` deselects the kind and shows all again;
**rule zones** (the baked ground areas of a kind's `where_ground` types) are drawn lighter and the hover text says "rule zone". Hover names
the zones and rule kinds under the cursor, a click selects the topmost drawn zone (where zones overlap the selected kind's wins, then the
higher ID); a click on a rule zone selects its kind and says it is by rule. Built one kind per frame with a progress line. Zones have no
paint and no hot reload; nothing is saved by the overlay. A map built before `zone_kinds` shows a red note to build it again and the Draw
action says why it is disabled.

**Mass delete**: mark zones with the tick of a list row, `Ctrl`+click on the map, `X` for the selected zone, or **Mark all** (the
selected kind's zones, or every zone); marked zones are outlined in red and counted in the HUD. **Delete N marked zones** (`Shift+Del`)
asks first, naming counts per kind, then removes them in one save = one undo step (drawn zones leave `zones`, rule zones get a
`zone_cuts` entry each). Marks are not saved.

Esc steps back: the drawing, the zone, the kind, the tool. Notes, states and the keyboard walkthrough: `docs/patches-zones-ui-notes.md`.
`make viewer-shape-editor-smoke` drives the patch and zone gestures headless and compares every saved `edits.json` with the Python writer's
text (needs a pipeline that exports `zone_kinds`).

**Floor plan.** `B` (also View > Layers and `Ctrl+P`) hides the buildings of the one selected parcel and shows their plan on the
ground: the wall lines (a horizontal cut 1.2 m above the ground, so recessed doors and windows show), over a 1 m grid on the
Sketch tool's grid with a stronger line every 5 m. It is for sketching an interior: the ground under a building was always
clickable, this makes it visible. One parcel at a time; it stays on while you select marks or other parcels, `B` again brings the
building back. A view only: nothing is saved to the map, the collision stays, walk mode shows the building, and the parcel is
remembered (`editor_ui.cfg`). There is no headless smoke for it yet.
