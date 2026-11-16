---
name: photo-to-building
description: Model one real building of the village as a 3D landmark in the map from photos of its sides (one or more photos, each naming a side). Use for "make a 3d model of this building from the photo", "model parcel P-xxxx / building wxxxx from these photos", "this house looks like this [image]". Exterior only. Not for interiors, doors that open, or game code.
---

# Photo to building

Turn photos of a building into a hand-modelled landmark: a Python module in `pipeline/src/drycastle_pipeline/` that
builds the mesh on the building's real footprint. Reference model: `garage.py` (read it once before step 4).

Follow the steps in order. Do not skip a gate. All commands run from `pipeline/` unless they start with `make`.

## Rules (never break)

1. Never change the footprint. The plan comes from the footprint; heights, openings and colours come from the photos.
2. Never edit `pipeline/config/edits.json`, never `git stash`, never run the game, never take screenshots.
3. Build only with `landmark_kit` functions. Do not write your own triangles.
4. Roof and wall colour varies per WHOLE sheet, tile strip or wall. Never cut a surface into random rectangles.
5. No `random` without a fixed seed (`sheet_roof` handles it). The same input must give the same mesh.
6. Interior, an opening door, a parked vehicle, anything the game must know about: finish the exterior, then tell the
   user it needs separate work (ADR 0027 shows what that took). Do not attempt it.
7. Leave out, and list in the report, everything that is not the building's own walls, roof, windows and ground-level
   doors: outside stairs, balconies, a door high on a wall, gates, boundary walls and fences, sheds and lean-tos of the
   neighbour, drainpipes, meter boxes, poles, cables, plants. Do not approximate them with boxes.
8. Numbers you did not read from a photo are guesses. Write every guess in the module docstring and in the report.

## Step 1: footprint

    uv run python -m drycastle_pipeline.landmark_tools footprint <P-xxxx | G-xxxx | building ids> [--view=X,Y]

`--view`: where a photo was taken, when the user gives it (map metres, or a sketch point of the editor: read
`~/.local/state/drycastle/sketch.json`, `points[].x, y`, and pass them as they are). Each edge then says whether that
photo shows it (VISIBLE / HIDDEN). Run it once per photo viewpoint.

Keep the whole output. It gives corners, edges (index, length, the compass side each faces, shared walls, road distance)
and the `RING = (...)` line to paste. Stop and ask the user if it says CLOCKWISE, if the first line shows a `model`
other than `None` (it is already hand-modelled), or if it says MORE THAN 4 CORNERS (the template does not fit).
"NOT square" with 4 corners is fine: carry on.

More than one building in the parcel: ask the user which one(s) the photos show unless they said so. Two buildings
sharing a wall that are one real building: model both in one module as `garage.py` does (`RINGS` with two entries);
otherwise one building per module.

## Step 2: match each photo to an edge (GATE)

Fill this table, one row per photo, and show it to the user:

| photo | side the user named | edge | edge length | why |
|---|---|---|---|---|

How to pick the edge:
- A viewpoint was given: only edges marked VISIBLE can be in that photo. A photo from a corner shows two edges: give
  it two rows. The viewpoint is approximate: use it to rule edges out, not to decide which wall is more head-on.
- The user named a compass side or a street: take the edge that `faces` it.
- The user said "front" and nothing else: the outer edge with the smallest `road` distance.
- Check with shape: a long low side in the photo is a long edge; a gable end is usually a short edge.
- Seen from outside, an edge runs from its first corner on the LEFT to its second corner on the RIGHT. Use this to
  check that what is on the left in the photo is at the start of the edge.

If two edges fit a photo equally, ask the user. Do not guess. Sides with no photo: copy the opposite side's roof line,
draw no openings, and list them as guessed.

## Step 3: measure (do this as careful, slow work)

Scale, wall seen square-on: the edge length from step 1 is the photo's width of that wall; measure along it as a
fraction of that width.

Scale, wall seen at an angle (it gets smaller towards one end): a fraction of the width is WRONG. Do this instead:
1. Heights: measure them at the corner nearest the camera, where the wall is largest. Use a known height there as the
   ruler (below).
2. Along the wall: count, do not measure. Place openings by order and by even spacing between the wall's two ends
   ("2 windows, the first about a quarter along, the second about two thirds"), then turn that into metres with the
   edge length. Mark every such position confidence low.
3. Two walls in one photo have two different scales. Never carry a pixel size from one wall to the other.

Rulers: a house door is 2.0 m high, a window sill is 0.8 to 0.9 m up, a storey is about 2.8 m, a car garage door 2.1
to 2.4 m, a fence or gate 1.5 to 1.8 m.

Ridge height when nothing measures it: village houses here have a roof pitch of 35 to 45 degrees. Take
TOP = EAVE + 0.4 x (length of ROOF_EDGE) and mark it low confidence. The generated height printed in step 1 is often
wrong; do not use it as evidence.

Colours: sample a sunlit part of the surface, not a shaded one. If the whole wall is in shade, brighten the sample by
about a quarter. Compare by eye with the render in step 5 and correct.

Write this sheet down before any code (in your reply, not a file):

    roof form:   gable | lean_to | flat | split (two lean-tos, see garage.py) | other -> stop, ask
    ROOF_EDGE:   index of the edge the roof slopes across (gable: an edge with a triangle on top;
                 lean-to: an edge whose wall top is slanted, starting at its LOW corner)
    EAVE, TOP:   metres above the ground (lean-to: wall top at the first / second corner of ROOF_EDGE)
    overhang:    metres the roof passes the wall
    colours:     wall, gable wall if different, plinth, frames, door, roof (3 close shades), each as [r, g, b]
    roof cover:  sheet metal (panel 0.55, seam_height 0.04) | tiles or shingles (panel 0.4, seam_height 0.0);
                 weathered? which colours
    left out:    what rule 7 removes from this photo
    openings:    per edge: kind (window|door), s0 = metres from the LEFT end, width, z0 (sill), height, casements
                 (a door stands on the ground; a door higher up is left out)
    confidence:  high | medium | low for each line, and what is guessed

Roof form by look:
- gable: two slopes meeting at a ridge, a triangle wall at each end.
- lean_to: one slope, one long wall higher than the opposite one.
- flat: no visible slope.
- lean-to with the low side at the SECOND corner of the edge: use the opposite edge as ROOF_EDGE instead.
- hipped, L-shaped, dormers, towers: not covered. Stop and say so; do not approximate silently.

## Step 4: write the module

1. Copy `.claude/skills/photo-to-building/template_building.py` to `pipeline/src/drycastle_pipeline/<name>.py`.
2. Fill ONLY the constants above `_N = len(RING)` and the docstring. Paste `RING` exactly as printed.
3. Leave `building_mesh` alone unless the sheet needs something it lacks; then copy the pattern from `garage.py`
   (`_bay`, `_side`, `_roof`), using kit functions only. Anything you add that is trim (frames, bands, boxes on a
   wall) goes AFTER the `if not detail: return` line, or it ends up in the collision.
4. Register it. In `landmarks.py` add next to `_bus_garage`, and one entry in `FOOTPRINT_MODELS`:

       def _<name>(b: dict, detail: bool) -> Mesh:
           """<what and where> (see ``<name>.py``)."""
           from drycastle_pipeline.<name> import building_mesh

           return building_mesh(b, detail)

5. In `pipeline/config/overrides.yaml` add the building next to the `bus_garage` entries, with a comment (parcel,
   what it is, photo date, what was guessed): `model: <name>`, `wall_colour`, `roof_colour` (hex), `levels`,
   `roof: flat`, `height_m: <TOP>`. (`roof: flat` is right for every model: these fields only describe the building to
   other stages, the mesh comes from the module. `levels`: 1 for one storey with or without an attic.)

## Step 5: look at it (GATE, at most 3 rounds)

    uv run python -m drycastle_pipeline.landmark_tools render <ids> --model <name> --out <scratchpad>/render [--view=X,Y]

It writes `edge_<building>_<i>.png` (each side seen square-on), `roof_a.png`, `roof_b.png`, and with `--view` (the
photo's viewpoint from step 1) `view.png`: the building as that photo sees it. Compare `view.png` with the photo when
you have a viewpoint; otherwise compare each photographed edge's picture with its photo. The renderer has no sun
shadows and no surroundings; judge shape and colour only. Answer each line yes/no:

- [ ] same outline: roof form, which side is higher, where the ridge is
- [ ] same number of openings, in the same left-to-right order (not counting what rule 7 left out)
- [ ] each opening about where it is in the photo, and about that size (oblique photo: same order and spacing)
- [ ] wall, roof and frame colours close to the photo
- [ ] roof: no patchwork, no steps; sheets or tiles run from eave to ridge
- [ ] nothing floats, pokes through the roof or hangs outside the footprint

Any "no": fix the constant and render again. After 3 rounds stop and report what still differs. Also render once with
`--collision` and check it is the same massing without trim.

## Step 6: test, export

1. Copy `template_test.py` to `pipeline/tests/test_<name>.py`. Replace the word `MODULE` (twice: the docstring and
   the import line, which becomes `from drycastle_pipeline import <name> as m`) and `NAME`.
2. `uv run pytest tests/test_<name>.py tests/test_garage.py tests/test_landmarks.py -q` and
   `uv run ruff check src/drycastle_pipeline/<name>.py tests/test_<name>.py`. Both must pass. (`test_dishes` fails on
   its own in the full suite; that is known and not yours.)
3. From the repo root: `make rules && make export`. This rebuilds `out/village/map` (the only map to use).
4. `uv run python -m drycastle_pipeline.landmark_tools near <ids>` lists fences, objects, lamps and trees within 6 m.
   Report those that stand off an edge with a door, and any at 0.0 m (inside the walls). Do not remove anything.
5. Open the final pictures for the user: `xdg-open <file>` for the photo-side renders and `roof_a.png`.

## Step 7: report

Short bullets: the photo-to-edge table; the sheet as built; every guess; anything that differs from the photos and
why; obstacles from step 6.4; files changed; "not seen in-game". Do not commit unless asked; if asked, work on an issue
branch and stage only your own files (the working tree is shared and edited live).

## Kit cheat sheet (`landmark_kit.py`)

| need | call |
|---|---|
| solid block under a roof | `prism(S, corners, z0 list, z1 list, colour)` (convex only: split an L in two) |
| box on a wall face | `edge(S, p, q, s0, s1, d0, d1, z0, z1, colour)` (`d` outward, 0 = wall face) |
| wall with real holes | `wall(S, p, q, top_p, top_q, thickness, colour, holes=[(s0, s1, z0, z1)])` |
| window drawn on a wall | `window(S, p, q, s0, s1, z0, z1, frame, glass, casements=2)` |
| door frame, plinth | `door_frame(...)`, `plinth(S, p, q, colour, gaps=[(s0, s1)])` |
| one roof slope | `sheet_roof(S, frame, roof_z, lo, hi, v_lo, v_hi, detail, seed=..., sheets=..., ...)` |
| gable | `gable_roof(S, frame, width, v_lo, v_hi, eave_z, ridge_z, detail, seed=..., **style)` |
| footprint side that is not straight | `boundary(frame, corners)` gives `a = f(v)` for `lo` / `hi` |
