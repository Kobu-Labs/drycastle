# Viewer and map editor reference

Details for `godot/viewer/`. Setup and the make targets are in the [README](../README.md). MCP: [mcp.md](mcp.md).

## Viewer

`godot/viewer` loads the whole map at startup with the shared `godot/MapLoader` library
(namespace `Suchohrad.MapLoader`) and lets you walk around in first person.

Map folder: `--map <dir>` after Godot's own args, else the env var `SUCHOHRAD_MAP`, else
`../../out/map` relative to `godot/viewer`. With make: `make viewer MAP=/path/to/out/map`.

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
`Objects/objects_<parcel>/object_<id>` with position (x east, z = -north, y = `z` or the terrain height
under it when `z` is null), `rotation_deg` counter-clockwise seen from above, uniform `scale` and an optional
`colour` (material override). Unknown or missing assets give a warning and a magenta placeholder box.
Objects have no collision in v1.

Regions (`regions` in `edits.json`, schema in `pipeline/src/suchohrad_pipeline/edits.py`) are baked into the
chunk files as `region_<ID>` nodes (flat colour, no collision) and rebuilt for hot reload by
`region-run --region ID --glb PATH`, whose JSON summary has `replaces` (the baked `region_<ID>` nodes to hide)
and a GLB with one `region_<ID>` node per region in the chunk frame. `EditsFile` keeps `regions` as read and writes
them canonically (the key is omitted while empty); `ObjectEdits.ValidateDocument` mirrors the schema. The editor UI
for drawing regions is a separate piece of work.

Assets directory: `--assets DIR`, else env `SUCHOHRAD_ASSETS`, else `<repo>/assets`.

Loader API additions: `ApplyFragment(path)` takes a `parcel-run --out ... --glb ...` fragment, hides the baked
`parcel_<ID>_props` visual and collider of every parcel in `replaces`, drops that parcel's previous dynamic
nodes (`dyn_props_<ID>`) and objects, adds the fragment's props nodes with colliders and its objects. It is
idempotent and an empty result removes the parcel's props. `RevertParcel(id)` restores the baked nodes and
objects. `AssetsDir`, `ObjectNodes`, `DynamicProps`, `Warnings` are exposed.

Hot reload: `make viewer` with `--watch` (`godot --path godot/viewer -- --watch`; off by default, so normal
viewing and headless servers are unaffected):

- `--edits PATH` the edits file (default `pipeline/config/edits.json`), `--area slice|village` (default: from
  the map version), `--state PATH` (default `out/editor-state.json`), env `SUCHOHRAD_UV` (the `uv` executable).
- The file is polled twice a second. The state at startup is assumed to be what the map was built with.
  A change is diffed per parcel and per group (a group change affects all its members and the other way
  round), and only those parcels are regenerated with
  `uv run python -m suchohrad_pipeline parcel-run --area A --parcel ... --edits <snapshot> --glb ... --out ...`
  in the `pipeline` directory (0.3 to 0.9 s) in a subprocess; edits that arrive during a run are coalesced into
  the next one. The viewer prints `edits applied: <ids> in <ms> ms`. A half-written or invalid file is ignored
  until it changes again, a failed `parcel-run` only prints its error. Writers must replace the file atomically
  (temp file, then rename). When only placed `objects` changed (see Objects and undo) there is no subprocess:
  the objects are re-instanced in process.
- `editor-state.json` (written at most once a second while watching, an ignored build output):
  `{"camera": {"x": east, "y": north, "z": elevation, "yaw_deg": view direction counter-clockwise from east},
  "format_version": 1, "selected": []}`; x and y are in the map frame (local metres). `selected` is the list of
  selected parcel IDs while the editor mode is on (empty otherwise).
- `make viewer-edit-smoke` exports a fresh slice map and a work-directory copy into a scratch directory
  (`SMOKE_DIR=...`, default a temp dir), starts the viewer headless with `--watch`, writes edits files during the
  run and checks objects, swapped props, colliders, replacement without leaks and survival of a broken file.
- `make viewer-regions-smoke` does the same for regions: exports a slice map with three regions, then recolours,
  reshapes, adds and removes regions through the watcher (alone and together with a parcel edit in one file
  change) and checks that only those regions are swapped (`region-run --glb`, `MapLoader.ApplyRegions`; a region
  split over chunk files is swapped completely), that repeated swaps do not grow the node count and that a broken
  file changes nothing.
  `parcel-run`/`export` take `--work DIR` and `export` takes `--out DIR` for such experiments.

## Editor mode

The first visible part of the world map editor: `--editor` starts the viewer in editor mode (it implies
`--watch`); F2 toggles between walk mode (unchanged) and editor mode. Needs `uv` for the hot reload.

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
| Esc | stop placing / deselect the object / clear the parcel selection (in that order) |
| Ctrl+Z, Ctrl+Y (Ctrl+Shift+Z) | undo / redo the saves made in this session (see Objects and undo) |

Hovering shows `P-0415 garden` (and `[G-0007]` when grouped) at the cursor. Outlines are unshaded ribbons draped
0.25 m over the terrain: yellow for the selection, faint white for the hovered parcel. Nothing is drawn by default: a property group
outlines only when one of its members is hovered or selected, and then as one shape (edges shared by two members are
not drawn). The right-hand panel shows the
selection (ID, kind, area, cadastral reference, group), a collapsible Group section with the buttons Group
selected, Add selected to group, Remove from group and Dissolve group, the Fence and Trees sections (below) and
the Objects section (palette, selected object, undo; see Objects and undo). The bottom line shows messages (`saved`, `regenerating P-0004...`, `applied in 480 ms`, errors) and the key hints.

Group operations rewrite `edits.json` atomically (temp file, then rename). The file is reloaded from disk
right before every save, so a change by another writer (e.g. the MCP server) is kept, and everything not
related to groups (fences, trees, objects, unknown parcels) is written back unchanged. The text is exactly what
`suchohrad_pipeline.edits.dump_edits` writes (sorted keys, indent 1, Python float formatting, trailing
newline), so both writers give byte-identical files; `pipeline/tests/fixtures/edits_roundtrip.json` is the fixture
for that. A parcel is in at most one group; `G` refuses (status line) when a selected parcel is already in one.
The watcher then regenerates the affected parcels (the fence runs along a group's outer edge) by itself.

### Fence and Trees panel, Apply and gates

The panel header names the **target**: `P-0084` (one parcel), `3 parcels` (several loose parcels; Fence and Trees
apply to every one, labelled `applies to N parcels`) or `G-0001 (3 parcels)`. A selection that is exactly one
property group's members targets the group: Fence goes to the group entry, Trees to each member parcel. With one
grouped parcel selected, `Edit: whole group | this parcel` chooses (default group); "this parcel" writes the
parcel's own fence override, which wins field by field over the group's.

* **Fence**: `mode` auto | none | custom; `type` picket | wire | brick_wall (the table lives in `EditSchema`,
  `EditModel.cs`, and `make viewer-panel-smoke` compares it with `edits.py`); `height`, `thickness` and `colour`
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
* **Trees**: `rules (default)` | `density` (per 1000 m2) | `count`; the rules value for the parcel kind is shown
  greyed. `seed offset` and `Reroll` (adds 1; from the rules it first switches to density with the rules' value,
  because a seed alone is not expressible in `edits.json`). `Reset to rules` removes the `trees` entry.
* **Apply**: changes are pending per target (`* unsaved changes` under the header, `N unsaved targets` at the
  bottom; selecting something else keeps them). `Apply` (button, or Enter when no text field has focus) reloads
  `edits.json`, writes every pending target in one atomic save in the canonical format and drops what became empty
  (a parcel entry with nothing left disappears, so resetting returns the file to its original bytes); the watcher
  then regenerates and the status line shows `saved`, `regenerating ...`, `applied in N ms`. `Revert` discards the
  current target's pending changes, `Revert all` all of them. Pending values replace the target's fence/trees
  entry as a whole, so an external change to that same entry made meanwhile is overwritten on Apply.

`make viewer-panel-smoke` (`SMOKE_DIR=...`, `SHOTS=dir` for two windowed screenshots of the panel) drives the panel
API headless on a scratch export: exact file contents after each Apply, the Python validator on every saved file,
group and multi-parcel edits, pending state across selections, gates (on the boundary within 1 cm, widths, a gap
that really cuts the fence collider), fence none removing colliders through the watcher, reroll changing the
regenerated trees, and reset restoring the original bytes.

### Objects and undo

The Objects section holds Undo / Redo buttons, the **selected object** (below) and the **palette**: every asset of
`assets/manifest.json` as a button in a collapsible group per category (car, slab, pot, flower, bush, tree, wall,
fence, other; groups start collapsed), plus a filter box (name, id or category; groups without a match hide,
the others open).

* **Placing**: choosing an asset arms placement. A semi-transparent cyan **ghost** (red when the point is in no
  parcel) follows the cursor on the ground, snapped to 0.5 m in absolute S-JTSK metres, or free while Ctrl is held.
  `R` / `Shift+R` rotate the ghost +-15 degrees, a left click places it (appended to the `objects` of the parcel
  under the point; refused with a status message when there is none), and placement stays armed. Esc or a right
  click without a look drag disarms. New ids are `o-0001`, `o-0002`, ... (one above the highest `o-NNNN` in the file;
  other id forms in the file are left alone). A placed entry is `id`, `asset`, `position` [x, y] (3 decimals),
  `rotation_deg`; `scale` and `colour` appear only when set, `z` never (the terrain height is used).
* **Selecting and moving**: a click on an object (ray against its box, grown by 15 cm) selects it and takes priority
  over the parcel under it. A yellow box, a cyan **rotation ring** at its feet (unshaded, always visible) and an
  arrow with a magenta knob showing `rotation_deg` are drawn. Drag the body to move it along the ground (0.5 m
  grid, Ctrl = free; the grab point keeps its offset), drag the ring to rotate (15 degree steps, Ctrl = free,
  angle counter-clockwise from east like `rotation_deg`). Arrow keys nudge 0.5 m in the camera's screen directions
  (snapped to the map axes) while the mouse is over the viewport; `Delete` removes, `Ctrl+D` duplicates 1 m east
  and selects the copy, `R` / `Shift+R` rotate the selected object by 15 degrees. Moving an object over a parcel
  boundary re-homes it to the parcel it is in now (its entry moves to that parcel's `objects`); a move to a point
  in no parcel is refused and the object snaps back.
* **Fields**: asset (read-only), `x` / `y` in S-JTSK metres (3 decimals), `rotation`, `scale` (> 0), a `colour`
  with a `set` box (committed when the picker closes), [Duplicate], [Delete]. Each committed edit is one save.
* **One gesture, one save**: place, drag end, field commit, nudge, delete and duplicate each reload the file,
  apply, validate with the Python validator's rules (`ObjectEdits.ValidateDocument`, it never writes a file
  `edits.validate` rejects), and write it atomically in the canonical format.
* **Instant objects**: when the watcher sees that, for the changed parcels, only the `objects` entries changed (no
  fence, trees or group change), it does not run `parcel-run`: `MapLoader.ApplyObjects` re-instances those parcels'
  objects straight from the file (S-JTSK minus `origin_epsg5514`, terrain height when `z` is absent), with the
  same node names (`objects_<parcel>/object_<id>`) and ownership as a fragment, so a later fragment swap stays
  consistent. The editor calls it right after saving (`EditsWatcher.NotifySaved`, about 1 to 3 ms from reading the
  file to the nodes being in the tree); an external writer (MCP, a text editor) is picked up by the poll (at most
  0.5 s). The watcher recognises its own write: the new state is the applied state, so nothing runs twice. Mixed
  changes still go through `parcel-run`. Generated trees avoid placed objects, so a tree under a new object
  only disappears at the next regeneration of that parcel (a fence or trees Apply, or a restart).
* **Session undo/redo**: `Ctrl+Z`, `Ctrl+Y` / `Ctrl+Shift+Z` (or the buttons) undo and redo every save the
  editor made since it opened: objects, group operations, Apply of fence and trees. Each save records the before
  and after content of the top-level entries it touched (`parcels.<ID>`, `groups.<ID>`, absent included). Undo
  reloads the file from disk, writes the before-entries back (absent deletes the entry), validates and saves; the
  watcher (or the instant path) applies the effect. **Entries somebody else changed in the meantime are
  overwritten only if this operation touched them**; everything else in the file stays. A new edit clears the
  redo stack, the stacks live in memory only and hold at most 200 operations. The status line shows
  `undo: <what>` / `redo: <what>`.

`make viewer-objects-smoke` (`SMOKE_DIR=...`, `SHOTS=dir` for two windowed screenshots) drives all of this headless on
a scratch export, through the editor APIs and key events: the palette against the manifest, the exact text of
`edits.json` after each of 26 saves (checked again with `edits.validate`, `dump_text` and `plan_edits`), no
`parcel-run` for object edits (the watcher counts them: `RunCount`, `InstantCount`), scene nodes against the file,
objects still right after a group and a fence Apply, drag and rotate with snapping, cross-parcel re-homing,
refusals, field edits, nudge, duplicate, delete, undo and redo of the whole sequence byte for byte, the 200 cap and
the latency. Screenshot options: `--shot-object ID --shot-arm ASSET --shot-ghost X,Y --shot-ghost-rot DEG
--shot-filter TEXT --shot-open car,pot` next to the `--editor-shot` ones.

Classes: `EditorMode` (camera, picking, selection, outlines, UI, group operations; `EditorMode.Edit.cs`: the Fence and Trees panel, pending state, gate tool; `EditorMode.Objects.cs`: palette, ghost, gizmo, object gestures; `EditorMode.Undo.cs`: session undo), `ObjectEdits` (object entries, ids, C# validator), `EditModel` (drafts, schema, applying pending changes), `EditsFile` (reader,
canonical writer and group operations for edits.json), `ParcelInfo` (now with `Rings`, `Bounds`, `AreaM2`, `Group`
and `Contains`) and `ParcelIndex` / `MapLoader.ParcelAt(x, z)` (grid index over the parcel boxes; about 5 us per
lookup).

`make viewer-editor-smoke` (`SMOKE_DIR=...` for the scratch directory) exports a slice map into a scratch dir,
starts the viewer headless with `--editor-smoke` and drives the editor through its API: F2 round trip, top-down
snap, picking, selection, outlines (shared edges not drawn), creating and dissolving a group, saved file content
checked byte for byte against `dump_edits`, `editor-state.json`. Screenshots of the editor mode (windowed, needs a
GPU): `godot --path godot/viewer -- --map DIR --edits FILE --state FILE --editor-shot out.png --select P-0003
--shot-at P-0003 [--hover P-0005] [--shot-edits FILE] [--shot-collapse Group,Fence] [--shot-gate N] [--shot-reroll] [--shot-focus X,Y --shot-dist D]`
(`--shot-edits` writes that edits file first and waits for the regenerated parcels).


### MCP server for parcel edits

`pipeline/src/suchohrad_pipeline/mcp_server.py` is a stdio MCP server (`make mcp`, normally started by Claude Code via
`uv run`) that edits `pipeline/config/edits.json` in plain language: "park a car in front of the house on the selected
parcel". It uses the pipeline's own validator, reads the viewer's `editor-state.json` for the selection, and the
viewer's `--watch` applies the change. Registration, tool reference and workflow: [mcp.md](mcp.md).

### Regions tool

Rail icon 6 (View moved to 7). `N` starts a shape, LMB or `C` adds corners (0.5 m grid, Ctrl free), Enter closes it, the colour is chosen
(picker or `#rrggbb`) and Enter creates the region: one save, one undo step. Select a region by clicking it or its row in the panel;
drag corners, `[` `]` plus arrows, recolour, `Del` (asks first). Invalid shapes are refused before commit with the reason in the panel
(a red preview outline while drawing or dragging). `Ctrl+P` opens a command palette. Esc steps back one level. Details and the
keyboard walkthrough: `docs/regions-ui-notes.md`. `make viewer-region-editor-smoke` drives all of it headless and compares every saved
`edits.json` with the Python writer's text.
