# Utility poles

The concrete electricity poles of the village (line wires on top, often a street lamp, sometimes public
address horns, boxes and a tangle of cables) are generated from parts. Three modules in
`pipeline/src/suchohrad_pipeline/`:

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
primitives: `prim:pole_lamp_1` etc. can be placed like any object (editor, `place_object`), the export writes
`prims/pole_*.glb`. Their category is `pole`: in the shooter only the column is solid (a 0.4 m box as high as
the pole), not the box around the lamp arm. `rotation_deg` turns the line direction; 0 = wires east-west,
lamp to the south.

After changing the variants or any part, run `make poles-manifest` (a test compares the manifest with the
generator) and `make poles-preview` to look at the result.

## Not done yet

- Wires between placed poles. `Pole.placed(x, y, z, rotation_deg)` gives the wire seats in map coordinates
  (`wire_points()`), and `span_mesh(a, b)` strings the wires; nothing in the map stages calls them yet, since
  the pole positions are not in the map. `out/poles/row.png` shows what it gives.
- Light. A pole with a lamp knows where its light is (`Pole.lights`), but it is not in `lamps.json`, so it
  does not glow at night.
- The editor's object list shows `pole` after its own categories (it appends categories it does not know).
