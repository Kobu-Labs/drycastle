# Viewer and map editor reference

Details for `godot/viewer/`. Setup and the make targets are in the [README](../README.md). MCP: [mcp.md](mcp.md).

## Viewer

`godot/viewer` loads the whole map at startup with the shared `godot/MapLoader` library
(namespace `Drycastle.MapLoader`) and lets you walk around in first person.

Map folder: `--map <dir>` after Godot's own args, else the env var `DRYCASTLE_MAP`, else
`../../out/map` relative to `godot/viewer`. With make: `make viewer` (MAP defaults to `out/village/map`, or `out/map` with `AREA=slice`; override with `MAP=/path/to/map`).

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

Regions (`regions` in `edits.json`, schema in `pipeline/src/drycastle_pipeline/edits.py`) are baked into the
chunk files as `region_<ID>` nodes (flat colour, no collision) and rebuilt for hot reload by
`region-run --region ID --glb PATH`, whose JSON summary has `replaces` (the baked `region_<ID>` nodes to hide)
and a GLB with one `region_<ID>` node per region in the chunk frame. `EditsFile` keeps `regions` as read and writes
them canonically (the key is omitted while empty); `ObjectEdits.ValidateDocument` mirrors the schema. The editor UI
for drawing regions is a separate piece of work.

Assets directory: `--assets DIR`, else env `DRYCASTLE_ASSETS`, else `<repo>/assets`.

Loader API additions: `ApplyFragment(path)` takes a `parcel-run --out ... --glb ...` fragment, hides the baked
`parcel_<ID>_props` visual and collider of every parcel in `replaces`, drops that parcel's previous dynamic
nodes (`dyn_props_<ID>`) and objects, adds the fragment's props nodes with colliders and its objects. It is
idempotent and an empty result removes the parcel's props. `RevertParcel(id)` restores the baked nodes and
objects. `AssetsDir`, `ObjectNodes`, `DynamicProps`, `Warnings` are exposed.

Hot reload: `make viewer` with `--watch` (`godot --path godot/viewer -- --watch`; off by default, so normal
viewing and headless servers are unaffected):

- `--edits PATH` the edits file (default `pipeline/config/edits.json`), `--area slice|village` (default: from
  the map version), `--state PATH` (default `out/editor-state.json`), env `DRYCASTLE_UV` (the `uv` executable).
- The file is polled twice a second. The state at startup is assumed to be what the map was built with.
  A change is diffed per parcel and per group (a group change affects all its members and the other way
  round), and only those parcels are regenerated with
  `uv run python -m drycastle_pipeline parcel-run --area A --parcel ... --edits <snapshot> --glb ... --out ...`
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
  changing a building: `make rules export AREA=...`, no restart. Not reloaded: everything else baked into a chunk
  (terrain, roads; fences, trees and regions keep following the edits file). A re-export with different parcels
  or a new chunk is not swapped in; the viewer says to restart.
- `editor-state.json` (written at most once a second while watching, an ignored build output):
  `{"camera": {"x": east, "y": north, "z": elevation, "yaw_deg": view direction counter-clockwise from east},
  "format_version": 1, "selected": []}`; x and y are in the map frame (local metres). `selected` is the list of
  selected parcel IDs while the editor mode is on (empty otherwise).
- `sketch.json` (next to `editor-state.json`, written within a frame of every sketch change, also an ignored build output) holds
  the marks of the Sketch tool in absolute S-JTSK metres, and is the one file the editor and the MCP server both write:
  `{"format_version": 1, "version": 7, "sketches": ["ROAD"], "next_line": 1, "next_point": 1,
  "lines": [{"closed": false, "id": "L1", "points": [[x, y], ...], "sketch": "ROAD"}],
  "points": [{"id": "A", "sketch": "ROAD", "x": .., "y": ..}]}` (see "Sketch tool"). `version` goes up on every write; the editor
  checks the file four times a second and takes a version it did not write over as one undo step. A sketch an older editor kept in
  `editor-state.json` is moved to the sketch `DEFAULT` the first time.
- `make viewer-edit-smoke` exports a fresh slice map and a work-directory copy into a scratch directory
  (`SMOKE_DIR=...`, default a temp dir), starts the viewer headless with `--watch`, writes edits files during the
  run and checks objects, swapped props, colliders, replacement without leaks and survival of a broken file.
- `make viewer-regions-smoke` does the same for regions: exports a slice map with three regions, then recolours,
  reshapes, adds and removes regions through the watcher (alone and together with a parcel edit in one file
  change) and checks that only those regions are swapped (`region-run --glb`, `MapLoader.ApplyRegions`; a region
  split over chunk files is swapped completely), that repeated swaps do not grow the node count and that a broken
  file changes nothing.
  `parcel-run`/`export` take `--work DIR` and `export` takes `--out DIR` for such experiments.

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

### Meadow clutter (generated near the player)

Fields, meadows and forests are about 400 ha, too much to store clutter instances for. Maps with `files.meadow` (`meadow.bin`, map 0.9.0) carry a density raster instead
(layers tuft, flower, fern, pebble, litter; one byte per 2 m cell per layer plus a zone byte per cell, format 3 in `pipeline/src/drycastle_pipeline/meadow.py`; the zone byte's high nibble picks the species
mix, which says per layer which kinds a cell makes and in what shares, its low nibble is a dryness that biases the tint). `MapLoader.Meadow.cs` turns the cells
near the focus into instances with `MeadowGen` (a hash of the cell and layer, the same on every client, nothing is sent; `MeadowData.cs`, pure, mirrors `meadow.generate`
and a test pins both). The ground is cut into 24 m tiles, each tile and layer is one `MultiMeshInstance3D` per kind (pooled), built nearest first at most 6 tiles or 2 ms per
frame, freed 30 m behind its range. A tile and layer with a kind of at least `MeadowSettings.MultiMeshMin` (48) instances draws it as a MultiMesh, and its smaller groups (a stray species of the patch mix) as one merged mesh
(`meadow_merged`, tint baked into the vertex colours), so a layer with seven flower species is about one or two nodes per tile, not seven (`MapLoader.MeadowNodes` counts them). Ranges and `DRYCASTLE_CLUTTER` are those of the stored clutter; live instances per layer are capped (`MeadowSettings.CapOf`).
Instances within a tree's trunk reach (from `trees.json`) are dropped; the height comes from `GroundHeight` (the terrain collision mesh, decimated). The focus is the current camera
unless `MapLoader.SetClutterFocus(position)` is called each frame. A map without `meadow.bin` and the headless server do nothing.

## Editor mode

The first visible part of the world map editor: `--editor` starts the viewer in editor mode (it implies
`--watch`); F2 toggles between walk mode (unchanged) and editor mode. Needs `uv` for the hot reload.
The View page has an **Aerial photo** layer (O) to edit by eye; it is a download for reference only, see `docs/ortho.md`.

```sh
# slice, against the default edits file (pipeline/config/edits.json)
dotnet build godot/viewer/viewer.csproj
godot --path godot/viewer -- --editor --map out/map --area slice
# village
godot --path godot/viewer -- --editor --map out/village/map --area village
```

Options: `--edits PATH` (default `pipeline/config/edits.json`), `--state PATH` (default `out/editor-state.json`),
`--area slice|village`, plus the usual `--map`, `--seed`, `--assets`.

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
  (`pack:kenney-city-kit-suburban/tree-large` or `tree-small`) with the Objects tool.
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
  region), it does not run `parcel-run`: `MapLoader.ApplyObjectChanges` removes the objects that are gone and
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

`make viewer-editor-smoke` (`SMOKE_DIR=...` for the scratch directory) exports a slice map into a scratch dir,
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

### Ground types tool

Rail icon 4 (`Ground types`). Lists every ground type of the map (swatch of the first colour, or the overlay tint of a type
that only adds attributes; name, what it does, and a tag when you edited it, made it, or deleted it) and edits one at a time.
A type is a bundle of **attributes** (ADR 0015); the form groups them by relation and a checkbox ticks each one the type
carries: **Appearance** (Terrain colours: swatch, picker, `#rrggbb` field, add up to 6, remove; Detail recipe: dropdown, with
"Advanced detail settings", the effective `detail` overlay as read-only text plus number fields for furrow probability,
headland keep and tone patches per ha, from the one table `GroundTypeEdits.DetailFields`), **Spawning** (players may spawn
here), **Water** (water body) and **Events** (Event spawn: one checkbox per event, today Bigfoot; it only marks where the
event may spawn, the game does not use it yet). Appearance and Water say "topmost wins" and Spawning and Events "adds up
over every stacked type" under their headings. A type that takes OSM land or is made by a rule cannot untick its last
appearance or water attribute (the checkbox is disabled with the reason as tooltip); OSM values (comma lists for `landuse`
and `natural`; read only for a rule type). Buttons: New type (name field, Create: one green shade and the field recipe;
Create layer: carries nothing yet, for a type that only adds spawning or events; or Duplicate selected, which copies the
attributes but not the OSM tags), Reset to rules (removes your override, also brings a deleted type back), Delete (asks
first; disabled for the default type and the dike). Rename does not exist: names are IDs (a disabled button explains it).
Every change is one save and one undo step; the types live in `edits.json` `ground_types` over `rules.yaml`
(docs/pipeline.md "Ground attributes and stacks", "Ground types of your own"). A banner says the change shows after the next
full map build (`make map`); the editor does not hot-reload ground. The Regions page's ground stack lists the effective types
(your edits included, deleted ones and rule types left out). Keys: `4` opens, Tab reaches the list, `N` the name field, `Del`
deletes, Ctrl+Z undoes, Esc leaves a text field, then deselects, then leaves the tool; `Ctrl+P` lists the types. States,
keyboard walkthrough and what is not verified: `docs/ground-types-ui-notes.md`. A map without `ground_types` in its
`parcels.json` shows an explanation to build it again.

### Regions tool

Rail icon 6. `N` starts a shape, LMB or `C` adds corners (0.5 m grid, Ctrl free), Enter closes it, the colour is chosen
(picker or `#rrggbb`) and Enter creates the region: one save, one undo step. Select a region by clicking it or its row in the panel;
drag corners, `[` `]` plus arrows, recolour, `Del` (asks first). Invalid shapes are refused before commit with the reason in the panel
(a red preview outline while drawing or dragging). `Ctrl+P` opens a command palette. Esc steps back one level. Details and the
keyboard walkthrough: `docs/regions-ui-notes.md`. `make viewer-region-editor-smoke` drives all of it headless and compares every saved
`edits.json` with the Python writer's text.

A selected region has a **Ground stack** (ADR 0015). "Paint only" (an empty stack): the region only colours the ground. Add a
type of the map (`ground_types` in the exported `parcels.json`: field, meadow, orchard, forest, bigfoot_zone, ...) from the Add
dropdown and it goes on top of the stack; each row has Up, Down and Remove, and says what the type does there: it decides the
terrain, adds spawning or events, or is shadowed by the type above. With a type that carries terrain attributes the region is a
hand-drawn ground area (`ground` in `edits.json`, a list, lowest first; see docs/pipeline.md "Parcels and ground areas"): its colour
becomes the colour of the topmost such type and the editor keeps showing it as paint, and the real ground (terrain colour,
grass, trees) appears after the next full map build (`make map`). A stack of only types that add attributes (spawning, events) makes
no ground: the ground stays as it is and the region is drawn as an attribute layer (below), its colour being the overlay colour. One
save and one undo step for each change; disabled for a water region. A deleted or unknown type in a stack is shown with a warning
and skipped by the build.

**Attribute layers.** The regions whose stack has a type that adds spawning or events (not counting the type that decides the
terrain) are drawn as a translucent overlay (about a third opaque, a thin edge in the same colour) draped on the terrain, in the
region's own colour, so they never hide the ground under them. `V` toggles it; the same switch is under View > Layers and on the
Regions page, it is on by default and remembered (`editor_ui.cfg`); `Ctrl+P` has it too. The overlay is rebuilt when the edits or the
ground types change. Nothing is saved by it.
