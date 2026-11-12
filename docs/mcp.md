# MCP server for parcel edits

A small stdio MCP server (`pipeline/src/drycastle_pipeline/mcp_server.py`) that lets Claude Code edit parcels in
plain language: fences, placed objects (trees among them: cars, slabs, pots, bushes, ...), property groups and regions (ground colour shapes). It writes
`pipeline/config/edits.json`, the same file the viewer's editor panel writes. It never talks to the viewer: a
viewer started with `--editor --watch` notices the file change within about a second and regenerates just the
affected parcels.

It imports the pipeline's own schema and validator (`edits.py`, `assets.py`), so there is one validator for the
pipeline, the Godot editor, this server and the tests.

## Register it in Claude Code

Nothing registers it for you. Pick one (replace `/path/to/drycastle` with your checkout):

```sh
# user-local, just for this checkout
claude mcp add drycastle-edits -- uv run --directory /path/to/drycastle/pipeline python -m drycastle_pipeline.mcp_server

# the village area instead of the slice
claude mcp add drycastle-edits-village -- uv run --directory /path/to/drycastle/pipeline python -m drycastle_pipeline.mcp_server --area village
```

or check a `.mcp.json` into your own working copy (project scope):

```json
{
  "mcpServers": {
    "drycastle-edits": {
      "command": "uv",
      "args": [
        "run", "--directory", "/path/to/drycastle/pipeline",
        "python", "-m", "drycastle_pipeline.mcp_server", "--area", "slice"
      ]
    }
  }
}
```

Then `/mcp` in Claude Code should list `drycastle-edits` with 23 tools. `make mcp` runs the same thing in a
terminal (it waits for JSON-RPC on stdin, so it is only useful for debugging); the repo does not ship a
`.mcp.json`.

Options (`python -m drycastle_pipeline.mcp_server --help`):

| Option | Default | Meaning |
| --- | --- | --- |
| `--area slice\|village` | `slice` | which map's work files (`data/work` or `data/village/work`) it reads |
| `--edits PATH` | `pipeline/config/edits.json` | the file it reads and writes; must be the one the viewer watches |
| `--state PATH` | `out/editor-state.json` | the viewer's selection and camera, for "the selected parcel" |
| `--sketch PATH` | `sketch.json` next to `--state` | the sketches the viewer and this server share, for "this line", "at A", "sketch ROAD" |
| `--assets PATH` | `assets/manifest.json` | asset manifest |
| `--work DIR` | from `--area` | work directory override (for experiments) |

It needs the work files `parcels.json` (map 0.4.0+, with `origin_epsg5514`), `buildings.json` and `roads.json`, so run
`make map` (or at least `normalise parcels rules terrain roads`) for the area first.

## Workflow with the viewer

1. Start the viewer in editor mode with hot reload: `godot --path godot/viewer -- --editor --map out/map --area slice`
   (editor mode implies `--watch`; see README "Editor mode").
2. Click a parcel (Shift+click for more). The viewer writes the selection to `out/editor-state.json`.
3. In Claude Code beside the viewer, ask for changes. Claude looks up the parcel (`find_parcels` with
   `selected: true`), inspects it (`get_parcel`) and edits it. The viewer shows the result about a second later.
4. To point at a place instead of a parcel, draw it: Sketch tool (9), `P` marks points (`A`, `B`, ...), `L` draws lines
   (`L1`, `L2`, ...; close a line to mean the ground inside). Then name the marks: "a row of five trees along L1",
   "paint the ground inside L2 as gravel", "put a lamp at A and at B", "everything in sketch ROAD". Marks are grouped in named
   sketches. Claude reads them with `get_sketch` and draws its own: "highlight this road under sketch ROAD"
   (`sketch_add_road`). The marks are temporary and never part of `edits.json`; see "Sketch".
5. `edits.json` is a normal tracked file: review the diff, commit it, or `git checkout` it to undo. Editor panel
   and MCP can be used side by side; both reload the file from disk before every write.

Example prompts:

- "Park a car in front of the house on the selected parcel."
- "Put a brick wall fence around P-0084 with a 4 m gate at the front."
- "Put a large tree at the back of P-0084." (`place_object` with `pack:kenney-city-kit-suburban/tree-large`, anchor `back`)
- "Group P-0012 and P-0013 as one property and give them a wire fence."
- "Put a flower pot at each side of the front door of the house on P-0021." (anchors `near_building`, offsets)
- "Remove the car I just placed."
- "Paint a gravel yard, 6 by 8 m, behind the house on P-0021." (`create_region`, see Regions)
- "Make the ground inside L1 a pond." / "Park a car at A, facing B." (`get_sketch`, see Sketch)

## Conventions

- Coordinates in and out are **absolute S-JTSK (EPSG:5514) metres**, x east, y north, the frame of `edits.json`.
  The viewer's `editor-state.json` camera is in the local map frame; the server reads `selected` from it. The sketch is in `sketch.json`, already absolute.
- **Front edge**: the side of the parcel boundary nearest a road (consecutive edges turning less than 8 degrees
  count as one side; the distance is measured to the road carriageways). When standing on its
  midpoint and looking into the parcel, `along` grows to the **right** and `inward` grows away from the road.
  `left` and `right` anchors are as seen in that view. `get_parcel` returns the front edge's midpoint, outward
  normal and axes, so Claude can also compute exact positions itself.
- **Rotation**: `rotation_deg` is counter-clockwise seen from above, absolute (0 = the model's front, glTF +Z,
  points south). The default for `place_object` turns the object's front toward the front edge (the road),
  which is right for a car parked in front of a house. This assumes models face +Z, as the Kenney car kit does.
- **Asset sizes** (`list_assets`) are raw bounding boxes in metres. Some packs are authored small (the Kenney
  suburban kit: a tree is 0.77 m, a fence section 0.5 m), so pass `scale` for those.
- **IDs**: groups `G-NNNN` and regions `R-NNNN` (highest existing + 1, as the Godot editor allocates them), placed objects `o-NNNN`
  (highest existing `O-` id + 1; any other unique string is valid in the file too). IDs of removed items are
  not reserved: the next free number after the current maximum is used.
- **Writes**: every write reloads the file, applies the change, validates the *whole* document with
  `edits.validate`, then writes it atomically (temporary file, rename) in the canonical format (identical bytes to
  the editor's `Apply`). Writers serialise on an `flock` on `edits.json.lock` next to the file. An invalid
  existing file or an invalid result is refused with the list of problems and nothing is changed.
- **Errors** come back as tool errors with an actionable message (unknown parcel/asset, bad value, nothing
  selected, ...), never as stack traces. **Warnings** (overlap with a building or another placed object,
  position outside the parcel, parcel in a group, small asset) are returned in `warnings` and the change is
  applied. The overlap test uses the asset's bounding box, so it is approximate.

## Tools

All inputs and outputs are JSON. Write tools return `summary`, `warnings` and the resulting entry.

| Tool | Arguments | Notes |
| --- | --- | --- |
| `list_assets` | `category?` | id, category, name, `size_m`, tags; includes the `prim:` primitives |
| `find_parcels` | `near?` `{x,y,radius_m?}` or `{parcel,radius_m?}`, `kind?`, `selected?`, `limit?` | id, kind, `area_m2`, `anchor`, group, `has_edits`, `distance_m` when `near`; `selected: true` reads `editor-state.json` (error with guidance when missing, empty or from another area; a warning when older than 10 minutes) and also returns the camera |
| `get_sketch` | `id?` (one mark, e.g. `A` or `L1`) | the marks of the viewer's sketches (`id` may also be a sketch name: all its marks; `sketches` lists each name with its counts): `points` (id, `sketch`, `position`, the `parcel` it is on and its `kind`) and `lines` (id, `sketch`, `closed`, `points` in drawing order, `length_m`, `bbox`; a closed line also `area_m2` and `centroid`; `parcels` it runs over with `length_m`, or covers with `area_m2` and `share_of_parcel`, largest first, at most 30 and then `parcels_total`); see Sketch |
| `create_sketch` | `name` | an empty named sketch (`ROAD`, 1 to 24 letters, digits, `_`, `-`; ignoring case), see Sketch |
| `sketch_add_point` | `x`, `y`, `sketch?` | marks a point (absolute S-JTSK metres): `{id, sketch, position}`; `sketch` is made when new, optional when there is at most one |
| `sketch_add_line` | `points` `[[x, y], ...]`, `closed?`, `sketch?` | draws a line: `{id, sketch, closed, corners}`; a closed line needs 3+ corners and must not cross itself |
| `sketch_add_road` | `road?` (edge id `E-0130`, or OSM way `w84718108` for the whole way) or `near?` (`[x, y]`, or a sketch point id such as `A`: the nearest road within 30 m), `sketch?` | draws the road's centreline as a line: `{sketch, lines, road: {id, osm_id, highway, surface, width, name}, length_m}` |
| `sketch_remove` | `target` (a mark id, or a sketch name with all its marks) | `{removed}` or `{removed_sketch, removed_marks}`; the viewer's undo brings it back |
| `get_parcel` | `id` | kind, area, cadastral ref, polygon bounds and centroid, edges with lengths, front edge, buildings (footprint centroid and bounds), neighbours, group, current edits, effective fence (after group inheritance), rule defaults (fence probability, tree density for its kind) |
| `set_fence` | `target` (parcel or group), `mode?`, `type?`, `column?`, `height_m?`, `thickness_m?`, `colour?`, `gaps?` | same semantics as the editor panel: `mode` auto / none / custom; the other keys are custom-only (given without a mode they imply custom; with auto / none they are rejected); given keys are merged into the existing entry; `type` is picket / wire / brick_red / brick_white / stone_wall / concrete (textured, each with its own columns); `column` swaps the columns (none / wood_post / metal_post / brick_red / brick_white / stone / concrete); `colour` is a tint (the paint of picket, wire and concrete); `gaps` are `{"at": [x, y] or "front", "width_m"}` gates |
| `reset_fence` | `target` | removes the fence entry (rules again) |
| `place_object` | `parcel`, `asset`, `anchor?`, `offset_m?` `{along,inward}`, `position?` `[x,y]`, `rotation_deg?`, `scale?`, `colour?` | anchors `front back left right centre near_building` (default `centre`); an anchor point that falls outside the parcel is moved to the nearest interior point (warning); exact `position` outside the parcel is applied with a warning; returns id, final position, rotation |
| `remove_object` | `id` | the cables of a removed utility pole go with it (`cables_removed`) |
| `connect_poles` | `poles` (2+ pole object ids, in the order the line runs) | strings a cable from each pole to the next (`C-NNNN`); poles are objects placed from the `prim:pole_*` assets; an id may also be a house connector (`prim:connector_*`, fixed to a wall or roof in the editor), so `[pole, connector]` connects a house; a pair already joined is left alone (warning); the poles keep their rotation, the wires are matched left to right |
| `disconnect_poles` | `cable?` (id) or `poles?` (the two pole ids) | removes one cable |
| `list_cables` | `pole?` | cables (id, from, to, `length_m`), the placed poles (id, asset, position, `rotation_deg`, number of cables) and the house connectors (id, asset, position, `z`) |
| `move_object` | `id`, `parcel?`, `anchor?`, `offset_m?`, `position?`, `rotation_deg?`, `scale?`, `colour?` | a new anchor re-faces the road unless `rotation_deg` is given; a new `parcel` re-lists the object |
| `create_group` | `parcels` (2+), `fence?` | new `G-NNNN`; a parcel can be in one group only; warns when the parcels do not touch |
| `merge_groups` | `ids` (2+ groups, or 1 with `parcels`), `parcels?` (loose parcels, in no other group), `fence?` | merges the groups and parcels into a new `G-NNNN` and removes the old groups; parcel-level fence edits stay on the parcels; the fence is `fence` if given, else the shared one of the old groups, else the first group's (with a warning) |
| `dissolve_group` | `id` | removes the group; members and their own fence edits stay |
| `create_region` | `colour` (optional with `ground`), and either `polygon` `[[x,y],..]` or `parcel` + `points` `[[along,inward],..]`; `water_y?`, `ground?` | new `R-NNNN`; see Regions |
| `set_region_colour` | `id`, `colour` | |
| `set_region_water` | `id`, `water_y` (null = ground paint again) | makes the region a body of water: flat surface at `water_y` (metres, terrain datum), also in `water.json`; no tree trunks inside it |
| `set_region_ground` | `id`, `ground` (a type of `list_ground_types`; null = ground paint again) | makes the region a hand-drawn ground area of that type (terrain colour, detail and trees of the type, overriding the rules there); shown as a coloured preview until the next full run (`make map`); not for water regions |
| `list_ground_types` | | the ground types of `rules.yaml` (`ground.types`): colours, recipe, trees, water, source |
| `move_region_point` | `id`, `index`, `position?` `[x,y]` or `offset?` `{dx,dy}` | exactly one of position / offset |
| `delete_region` | `id` | |
| `list_regions` | `near?` `{x,y,radius_m?}` or `{parcel,radius_m?}`, `limit?` | id, colour, `area_m2`, `points`, `bbox`, `centroid`; `distance_m` when `near` |

## Regions

A region (`R-NNNN`) is a hand-drawn shape painted in one `#rrggbb` colour on the ground, independent of parcel
boundaries (see `docs/pipeline.md`, "Regions"). The tools write the `regions` section of `edits.json` and
validate with the same `edits.validate` as the editor: at least 3 points, no repeated consecutive points, edges
may not cross or touch each other. An invalid result (also from `move_region_point`) is refused with the
problems listed and the file stays byte-for-byte as it was. Where regions overlap, the higher ID wins; creating
or moving into an overlap is allowed and reported in `warnings`. Colours are stored lower-case.

`create_region` takes exactly one shape form. Absolute S-JTSK metres:

```json
{"colour": "#8a7a5c", "polygon": [[-596810.0, -1160420.0], [-596800.0, -1160420.0], [-596800.0, -1160410.0]]}
```

or points relative to a parcel, in the same front-edge frame as `place_object` (`along` grows to the right,
`inward` away from the road; `{"along": .., "inward": ..}` objects are accepted too), converted to absolute
coordinates on write:

```json
{"colour": "#b9b3a5", "parcel": "P-0021", "points": [[-3, 8], [3, 8], [3, 14], [-3, 14]]}
```

`move_region_point` indexes the stored polygon from 0: `{"id": "R-0001", "index": 2, "offset": {"dx": 1.5, "dy": 0}}`
(`dx` east, `dy` north) or `"position": [x, y]`. `list_regions` returns for example
`{"id": "R-0001", "colour": "#8a7a5c", "area_m2": 100.0, "points": 4, "bbox": [..], "centroid": [..]}`.

## Sketch

A sketch is a named group (`ROAD`, `FENCE_PLAN`, ...) of the temporary marks drawn on the ground (Sketch tool, rail icon 9; see
`docs/viewer-and-editor.md`): points named `A`, `B`, ... and lines named `L1`, `L2`, ..., a line being open or closed
(closed: the last corner joins the first and the mark means the ground inside). Several sketches exist at once; mark names are
unique across all of them, so "at A" needs no sketch name. The viewer and this server share one file, `out/sketch.json`
(`--sketch`); either side writes it atomically and raises its `version`. The server changes it under a `flock` on
`sketch.json.lock`; the viewer does not lock, so an edit in the viewer and an MCP edit within the same frame can overwrite one
another (last writer wins; Ctrl+Z in the viewer undoes what it took over). The viewer takes a changed file over within a
quarter of a second, as one undo step ("sketch changed by the MCP server"). With the viewer closed, the file simply waits.
The `sketch_add_*` tools create the sketch they name; `sketch` may be left out while there is at most one. Coordinates are
absolute S-JTSK metres, so they go straight into `place_object` `position`, `create_region` `polygon` or a fence gap `at`:

```json
{"count": 2, "state_age_s": 0.4,
 "sketches": [{"name": "ROAD", "points": 1, "lines": 1}],
 "points": [{"id": "A", "sketch": "ROAD", "position": [-596810.0, -1160420.0], "parcel": "P-0021", "kind": "building_plot"}],
 "lines": [{"id": "L1", "sketch": "ROAD", "closed": true, "points": [[-596812.0, -1160431.5], [-596802.5, -1160431.5], [-596804.0, -1160424.0]],
            "length_m": 26.0, "area_m2": 36.4, "bbox": [..], "centroid": [..],
            "parcels": [{"id": "P-0021", "kind": "building_plot", "area_m2": 30.1, "share_of_parcel": 0.04}]}]}
```

"Highlight this road under sketch ROAD": `sketch_add_road(near="A", sketch="ROAD")` (the road under the sketch point `A`), or
`road="E-0130"` for a road you already know. A sketch from an older viewer, kept in `editor-state.json`, is read as the sketch
`DEFAULT` and moved to `sketch.json` the first time the server writes.

Errors say what to do: no sketch file, an empty sketch, an
unknown mark or sketch (the existing names are listed), a bad sketch name. Warnings: a state file older than 10 minutes, a closed line that
crosses itself (returned without parcels), no mark on any parcel of this map (wrong `--area` or `--state`).
What is still missing for "plant trees inside L1": trees are no longer set per parcel (the rules or the orthophoto detector plant
them), so today that means placed tree objects at computed positions; a tree edit bound to a drawn shape is a separate piece of work.

Not yet: moving or editing a mark (remove and draw it again), a screenshot tool (`snapshot(parcel)` can come later, the viewer already has `--screenshot`), and
drawing free-form fence polylines, and inserting or removing region points (delete and recreate).

## Tests

`cd pipeline && uv run pytest tests/test_mcp_server.py` runs every tool on the synthetic workspace (no real
data), checks the canonical bytes (`dump_text(load_edits(file)) == file`), anchors against a parcel next to a road,
warnings, the selection through a fake `editor-state.json`, the sketch tools through `sketch.json`, two writer threads on one file, and a session through the
MCP SDK's in-memory client.
