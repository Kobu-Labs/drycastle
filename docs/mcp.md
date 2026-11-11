# MCP server for parcel edits

A small stdio MCP server (`pipeline/src/suchohrad_pipeline/mcp_server.py`) that lets Claude Code edit parcels in
plain language: fences, trees, placed objects (cars, slabs, pots, bushes, ...), property groups and regions (ground colour shapes). It writes
`pipeline/config/edits.json`, the same file the viewer's editor panel writes. It never talks to the viewer: a
viewer started with `--editor --watch` notices the file change within about a second and regenerates just the
affected parcels.

It imports the pipeline's own schema and validator (`edits.py`, `assets.py`), so there is one validator for the
pipeline, the Godot editor, this server and the tests.

## Register it in Claude Code

Nothing registers it for you. Pick one (replace `/path/to/drycastle` with your checkout):

```sh
# user-local, just for this checkout
claude mcp add suchohrad-edits -- uv run --directory /path/to/drycastle/pipeline python -m suchohrad_pipeline.mcp_server

# the village area instead of the slice
claude mcp add suchohrad-edits-village -- uv run --directory /path/to/drycastle/pipeline python -m suchohrad_pipeline.mcp_server --area village
```

or check a `.mcp.json` into your own working copy (project scope):

```json
{
  "mcpServers": {
    "suchohrad-edits": {
      "command": "uv",
      "args": [
        "run", "--directory", "/path/to/drycastle/pipeline",
        "python", "-m", "suchohrad_pipeline.mcp_server", "--area", "slice"
      ]
    }
  }
}
```

Then `/mcp` in Claude Code should list `suchohrad-edits` with 18 tools. `make mcp` runs the same thing in a
terminal (it waits for JSON-RPC on stdin, so it is only useful for debugging); the repo does not ship a
`.mcp.json`.

Options (`python -m suchohrad_pipeline.mcp_server --help`):

| Option | Default | Meaning |
| --- | --- | --- |
| `--area slice\|village` | `slice` | which map's work files (`data/work` or `data/village/work`) it reads |
| `--edits PATH` | `pipeline/config/edits.json` | the file it reads and writes; must be the one the viewer watches |
| `--state PATH` | `out/editor-state.json` | the viewer's selection and camera, for "the selected parcel" |
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
4. `edits.json` is a normal tracked file: review the diff, commit it, or `git checkout` it to undo. Editor panel
   and MCP can be used side by side; both reload the file from disk before every write.

Example prompts:

- "Park a car in front of the house on the selected parcel."
- "Put a brick wall fence around P-0084 with a 4 m gate at the front."
- "Plant five trees on the selected parcel, a different layout than before." (`count` plus a new `seed_offset`)
- "Group P-0012 and P-0013 as one property and give them a wire fence."
- "Put a flower pot at each side of the front door of the house on P-0021." (anchors `near_building`, offsets)
- "Remove the car I just placed."
- "Paint a gravel yard, 6 by 8 m, behind the house on P-0021." (`create_region`, see Regions)

## Conventions

- Coordinates in and out are **absolute S-JTSK (EPSG:5514) metres**, x east, y north, the frame of `edits.json`.
  The viewer's `editor-state.json` camera is in the local map frame; the server only reads `selected` from it.
- **Front edge**: the side of the parcel boundary nearest a road (consecutive edges turning less than 8 degrees
  count as one side; the distance is measured to the road carriageways and road parcels). When standing on its
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
| `get_parcel` | `id` | kind, area, cadastral ref, polygon bounds and centroid, edges with lengths, front edge, buildings (footprint centroid and bounds), neighbours, group, current edits, effective fence (after group inheritance), rule defaults (fence probability, tree density for its kind) |
| `set_fence` | `target` (parcel or group), `mode?`, `type?`, `column?`, `height_m?`, `thickness_m?`, `colour?`, `gaps?` | same semantics as the editor panel: `mode` auto / none / custom; the other keys are custom-only (given without a mode they imply custom; with auto / none they are rejected); given keys are merged into the existing entry; `type` is picket / wire / brick_red / brick_white / stone_wall / concrete (textured, each with its own columns); `column` swaps the columns (none / wood_post / metal_post / brick_red / brick_white / stone / concrete); `colour` is a tint (the paint of picket, wire and concrete); `gaps` are `{"at": [x, y] or "front", "width_m"}` gates |
| `reset_fence` | `target` | removes the fence entry (rules again) |
| `set_trees` | `parcel`, `density_per_1000m2?` or `count?`, `seed_offset?` | exactly one of density / count (a `seed_offset` alone works only on top of an existing one) |
| `reset_trees` | `parcel` | removes the trees entry |
| `place_object` | `parcel`, `asset`, `anchor?`, `offset_m?` `{along,inward}`, `position?` `[x,y]`, `rotation_deg?`, `scale?`, `colour?` | anchors `front back left right centre near_building` (default `centre`); an anchor point that falls outside the parcel is moved to the nearest interior point (warning); exact `position` outside the parcel is applied with a warning; returns id, final position, rotation |
| `remove_object` | `id` | |
| `move_object` | `id`, `parcel?`, `anchor?`, `offset_m?`, `position?`, `rotation_deg?`, `scale?`, `colour?` | a new anchor re-faces the road unless `rotation_deg` is given; a new `parcel` re-lists the object |
| `create_group` | `parcels` (2+), `fence?` | new `G-NNNN`; a parcel can be in one group only; warns when the parcels do not touch |
| `merge_groups` | `ids` (2+ groups, or 1 with `parcels`), `parcels?` (loose parcels, in no other group), `fence?` | merges the groups and parcels into a new `G-NNNN` and removes the old groups; parcel-level fence edits stay on the parcels; the fence is `fence` if given, else the shared one of the old groups, else the first group's (with a warning) |
| `dissolve_group` | `id` | removes the group; members and their own fence edits stay |
| `create_region` | `colour`, and either `polygon` `[[x,y],..]` or `parcel` + `points` `[[along,inward],..]` | new `R-NNNN`; see Regions |
| `set_region_colour` | `id`, `colour` | |
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

Not in v1: a screenshot tool (`snapshot(parcel)` can come later, the viewer already has `--screenshot`), and
drawing free-form fence polylines, and inserting or removing region points (delete and recreate).

## Tests

`cd pipeline && uv run pytest tests/test_mcp_server.py` runs every tool on the synthetic workspace (no real
data), checks the canonical bytes (`dump_text(load_edits(file)) == file`), anchors against a parcel next to a road,
warnings, the selection through a fake `editor-state.json`, two writer threads on one file, and a session through the
MCP SDK's in-memory client.
