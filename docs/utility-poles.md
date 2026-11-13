# Utility poles

The concrete electricity poles of the village (line wires on top, often a street lamp, sometimes public
address horns, boxes and a tangle of cables) are generated from parts. Three modules in
`pipeline/src/drycastle_pipeline/`:

| Module | What it holds |
|---|---|
| `poleparts.py` | The parts kit: `Kit` (flat-coloured solids: loft, lathe, tube, beam, box, swept cable) and one builder per part. `PARTS` lists them. |
| `poles.py` | `PoleSpec` (a pole as plain data), `build_pole(spec)`, `random_spec(seed, archetype)`, `VARIANTS` (the placeable poles) and `span_mesh` (wires between two placed poles). |
| `poles_preview.py` | Pictures of all of it (`make poles-preview`) and the manifest sync (`make poles-manifest`). |

## Frame

Local metres, z up, the foot of the column at the origin; the column goes 0.5 m into the ground so a pole
also stands on a slope. **The line (the wires to the neighbouring poles) runs along `x`; `-y` is the front,
the road side, where the lamp reaches.** Directions on a pole (`az_deg`, `side_deg`) are compass angles,
counter-clockwise seen from above, 0 = `+x`. A part that hangs on the pole is built with its origin on the
pole surface and `+x` pointing away from the pole, then turned and moved into place.

## Parts

`make poles-preview` draws every part in `out/poles/parts.png`.

- Columns: `round` (tapered spun concrete), `twin` (two of them clamped together with steel bands, standing
  along `axis_deg`), `slim` (the thin field pole); a `strut` is a second, leaning column that props the pole.
  Facets are shaded a little differently, the foot is dark and a couple of stains run down.
- On top: `crossarm` (length, number of pins, `even` or `paired` layout, no brace / one strut / a V, offset),
  `insulator` (`brown`, `white`, `glass`, `dark`), `top_pin`.
- On the side: `side_bracket` (`pin`, `spool`, `hook` for a bundled cable), `lamp` (arm `straight` or `swan`,
  head `cobra` or `led`), `megaphone`, `junction_box`, `meter_box`, `band`, `plate`, `warning_mark`.
- On the ground: `ground_cabinet` (one door, or two when wider than 0.6 m).
- Along the column: `riser` (conduit with saddle clips and a weatherhead), `down_cable`, `slack_coil`.
- Between points: `hang` (a cable hanging from one point to another), used for feeds, jumpers and spans.

A new part is a function returning a `Part` (its `Kit` and named points such as `wire`, `feed`, `light`),
an entry in `PARTS`, a field in the spec and a few lines in `build_pole`.

## Specs and archetypes

A `PoleSpec` lists what the pole carries, with heights and directions; `to_dict` / `from_dict` turn it into
JSON and back, so a pole can also be written by hand. `random_spec(seed, archetype)` draws one:

| Archetype | Weight | What it is |
|---|---|---|
| `lamp` | 5 | single column, crossarm, street lamp, often a side bracket and a down cable with a slack coil |
| `plain` | 3 | single column and crossarm, no lamp |
| `twin_lamp` | 2 | two clamped columns, long crossarm (sometimes a second), street lamp |
| `junction` | 2 | two or three crossarm tiers (branch lines), many hanging jumpers and coils, risers, a box, often a cabinet beside it, sometimes a strut or a twin column |
| `twin_riser` | 1 | two columns, paired pins, short lamp, meter box with a white riser and a bundled cable on a hook |
| `field` | 1 | thin leaning pole with a braced crossarm and dark insulators |
| `megaphone` | 1 | single column with two or three public address horns |

Without an archetype the seed picks one by weight. The same seed and archetype always give the same pole.
The spec's own `seed` only drives the small things (weathering, how the jumpers hang).

## Placing them

`VARIANTS` names 15 poles (`pole_lamp_1` ... `pole_megaphone_2`), each an archetype and a seed. They are
primitives: `prim:pole_lamp_1` etc. are placed like any object, the export writes `prims/pole_*.glb`. Their
category is `pole`: in the shooter only the column is solid (a 0.4 m box as high as the pole), not the box
around the lamp arm. `rotation_deg` turns the line direction; 0 = wires east-west, lamp to the south.

In the editor the **Poles tool** (rail icon, key 7) has the poles as renders, the cables and a plan mode that
places and connects a whole line along the roads; see `docs/viewer-and-editor.md`. Through MCP: `place_object` with a pole asset and an exact `position`, then `connect_poles`. A pole belongs to no parcel (it is only a position), so one beside a road, on land that is no parcel, is placed like any other.

After changing the variants or any part, run `make poles-manifest` (a test compares the manifest with the
generator) and `make poles-preview` to look at the result.

## Cables

A cable joins two placed poles: `cables` in `edits.json`, `{"C-0001": {"from": "o-0012", "to": "o-0015"}}`
(schema in `edits.py`; a pair of poles has at most one cable, the direction means nothing). The props stage
copies the cables whose two ends are placed poles into `objects.json` (`cables`, a list of `id`, `from`, `to`);
the others are skipped with a warning. Nothing of a cable is baked into the map: the game strings the wires
when it loads the objects, from the poles as they stand and the wire seats of their assets (`seats` in
`assets/manifest.json`, written by `make poles-manifest`). So the viewer, the editor and the shooter show the
same wires, and moving or turning a pole in the editor moves them at once. Wires have no collision.

The rule (`godot/ShooterCore/Environment/CableWires.cs`, tested in `CableWiresTests`):

- **Poles are not turned.** Each keeps the rotation it was given.
- On each pole the crossarm that stands most squarely across the span is used (a junction pole's branch arm
  for the branch line); the pin on top of a column counts to the first crossarm.
- Its seats are ordered left to right as seen along the span, and the n-th seat of one pole is joined to the
  n-th of the other. So the wires keep their spacing and never cross, whichever way a pole faces. A pole
  turned so that its arm runs along the line still gets its wires, bunched together.
- When one pole has more seats, the other decides how many wires there are; the seats used on the longer arm
  are spread evenly over it.
- If both poles have a side bracket, one thicker, lower service cable joins them; hooks likewise get a
  bundled cable.
- Each wire hangs about 2.5 % of its length (3.5 % for the service cable), a little different per wire, the
  same every time for the same cable id.

`poles.span_mesh` is the older Python version of this, used only by `make poles-preview` (`row.png`).

## Lamps

A pole with a luminaire says where its light is: `lights` in its `assets/manifest.json` entry, the point under
the luminaire in the pole's frame (written by `make poles-manifest`). In the shooter the lamps of placed poles
shine like the street lamps of `lamps.json` (`MapLoader.ObjectLights`, `StreetLamps.PoleHeads`): a lit plate
under the luminaire, a glow and a share of the light pool, all following the night factor. The pole and the
luminaire are the placed object's own model and collider, so nothing else is added. The viewer and the editor
have no night and show no light.

## House connections

A cable can also end on a house: on a **house connector**, a small fitting on a wall or a roof
(`pipeline/src/drycastle_pipeline/connectors.py`, drawn in `out/poles/connectors.png`). There are six,
placeable objects like the poles (`prim:connector_*`, category `connector`, no collision), each with wire seats:

| Asset | Where | What |
|---|---|---|
| `connector_wall_2`, `connector_wall_4` | wall | J-hooks with an insulator each, a conduit with a weatherhead under them |
| `connector_wall_hook` | wall | a hook and clamp for a bundled cable, which loops into a box on the wall |
| `connector_roof_mast_2`, `connector_roof_mast_4` | roof | a pipe through the roof with a gooseneck and a short crossarm of insulators |
| `connector_roof_mast_hook` | roof | a roof mast with a hook for a bundled cable |

Their origin is the point of the wall or roof they sit on and `+x` points at the incoming wires: a wall
connector faces out of the wall (`rotation_deg` is the way the wall faces), a roof mast is turned towards
its pole. A placed connector has a `z` (elevation), so it does not stand on the ground.

In the editor's connect mode a click on a house (after a pole) does it all in one save: the ray is tested
against the building as drawn (`MapLoader.RayBuildings`: the real walls and roof, not the collision block),
a wall or a roof connector is picked from the surface's normal, **which one of them from the spot clicked**
(so houses differ, and the same spot gives the same one), it is placed there and a cable is strung from the
pole. The start stays on the pole, so one pole can feed several houses. "Another kind of connector" on the
selected connector swaps it for the next kind for the same surface.

The wires follow the same rule as between poles. A connector with insulators takes as many of the pole's
line wires as it has insulators (two or four), matched left to right. One that is only a hook gets a single
bundled cable from the pole's hook, else from its side bracket, else from the nearest insulator of its
crossarm.

Through MCP a connector cannot be fixed to a wall (`place_object` has no elevation); `connect_poles` accepts
the id of a connector that exists, e.g. `[pole, connector]`.

### A street at a time

`python -m drycastle_pipeline.house_connections` (`--dry-run` first) places the connectors and their cables
for every dwelling within `--service-m` (default 40) of a pole, into `edits.json` like the editor would: `objects` with a
`z`, and `cables` from the pole. A house takes the nearest pole that has room (`--per-pole`, default 4 drops) over a wire
that does not go through a building. The wire lands on the wall that faces the pole, under the eave and away from the
corners (a wall hook, or two or four J-hooks); about half the flat-roofed houses get a roof mast near that edge instead,
turned towards the pole. Sheds, garages, the church and the hand-made overrides are left out, and so is a house that has
a connector within 6 m already. The choice of kind is seeded by the building id, so a rerun gives the same answer. The
`z` is the flattened ground under the building (`terrain.json` `building_elevation`) plus the height on it. It adds, it
never moves what is there: undo with git, or delete the connectors in the editor.

## Not done yet

- Poles are the 15 fixed variants; a pole of any seed and archetype (a mesh per placed pole) is not possible
  through placed objects.
- The editor's object list shows `pole` after its own categories (it appends categories it does not know).
- No headless smoke test drives the Poles tool.
- House connections in the editor are made by hand, one click each (a whole street at once: `house_connections`, below).
