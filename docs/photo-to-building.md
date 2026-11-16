# Photo to building

Turn photos of a real building into its model in the map, measured on the building's real footprint. Exterior only.
Issue #329.

## How to trigger it

- **Explicitly:** `/photo-to-building` in Claude Code, then the property and the photos. It is a project skill
  (`.claude/skills/photo-to-building/SKILL.md`), so every session in this repo has it.
- **By asking:** "make a 3d model of this building from the photo", "model G-0377 from this photo, taken from around
  point A". The skill's description makes the model load it on its own.
- **On a cheaper model:** it is written for sonnet at high or medium effort. Switch with `/model` first, or ask the
  session to hand it to a sonnet agent ("run photo-to-building on G-0377 with a sonnet high agent").

What to give it: the building. Its pictures come from one of two places.

**Captures (the cheap, reliable way).** Walk the village in Street View with the follower running:

    cd pipeline
    uv run python -m drycastle_pipeline.streetview_follow --launch --capture

Aim the middle of the view at a house and press **C** in the Street View tab. The picture of the tab is saved with the
camera it was taken with (place, heading, tilt, zoom, all from the tab's address) under the building the middle of the
view hits: `data/village/photos/<building>/<n>.png` and `.json`. The follower prints which building and walls it took
it for. Take two per house, from opposite corners, each with the ground line and the eave of the near corner in view.

    uv run python -m drycastle_pipeline.photo_capture list                  # what there is
    uv run python -m drycastle_pipeline.photo_capture todo                  # buildings with captures and no detail yet
    uv run python -m drycastle_pipeline.photo_capture set w123/0 w456       # a capture filed under the wrong building

Then: "run photo-to-building on w378920410", or on everything `todo` lists.

Captures are Street View imagery: gitignored, never shipped. `docs/streetview.md` says such images are for human eyes
only and that nothing is generated from them automatically; measuring buildings from captures goes beyond that, by the
owner's decision of 2026-11-16.

**Your own photo.** Paste it and say where it was taken: a compass side, or better a sketch point dropped in the editor
("from point A"). It needs more points and fits less surely, because the camera is not known.

## What happens (ordinary houses)

The output is a building detail (`pipeline/config/buildings/<id>.yaml`: openings) and the building's entry in
`pipeline/config/overrides.yaml` (roof form, heights, colours). No code; the usual generator builds it, with real
window holes and breakable panes. Built to be cheap: two commands, two pictures read.

| step | what | tool |
|---|---|---|
| 1 | Footprint (corners, edges, which walls each picture sees) and the pictures with a pixel grid | `landmark_photo prep <building> --out DIR` |
| 2 | The model writes one sheet: building corners seen in the photo, two pixels per opening, a pixel per colour | `DIR/sheet.json` |
| 3 | Camera fit, measured heights and openings, colours, both files written, the model drawn over the photo | `landmark_photo apply DIR/sheet.json --out DIR` |
| 4 | One look at the overlay, at most one correction (`--replace`) | `DIR/overlay_0.png` |

After a batch: `make rules && make export` once. Nothing is committed unless you ask.

The sheet's format is in the docstring of `landmark_photo.py`; `--dry` fits and draws without writing.

The single commands still exist (`grid`, `fit`, `measure`, `sample`, `overlay`; `landmark_tools footprint`, `render`,
`near`) for looking closer.

## What it covers

- One building; gabled, flat or hipped roof (as the generator builds them: ridge in the middle).
- Windows and doors as real openings, also high on a gable.
- One or more photos; each gets its own camera fit, a bad fit is not used.

- The roof is read, not declared: the sheet marks the roof's outline on a side wall (its peak and where it ends), and
  the tool decides gable or slope-then-flat, the ridge height, the house's width and whether the roof covers only part
  of the footprint. Without a visible outline the building gets a flat roof and is flagged; a ridge is never guessed.
- `slope_then_flat`: one slope to the front, flat behind at its top (a house raised at the back). The generator has a
  single-slope roof for it (`roof: lean_to`, the high edge on the left of `ridge_dir`).
- A footprint that holds a house and its extensions: `main_only` puts the pitched roof over the house alone (the strip
  behind its gable wall, sized from the measured ridge) and makes the rest a lower flat block (`roof_part`,
  `rest_height_m` in `overrides.yaml`).

Left out and reported: stairs, balconies, gates, fences, sheds, pipes; a different colour on the gable ends; a ridge
off the middle; an opening that reaches above the generated roof line.

Accuracy, from the first real captures (w378920531): reading points on the full 2532 px picture gave fit errors of 8
to 11 px and eaves 0.6 m apart between two captures; on the grids cut to the building it gave 4 to 7 px and eaves
3 cm apart.

## Special buildings

Lean-to or split roofs, two buildings sharing a wall, a hollow part, an L-shape: `LANDMARK.md` in the skill folder, the
long way. It writes a Python module on `landmark_kit` (reference: `garage.py`, ADR 0027) and costs several times more.
Interiors, doors that open and parked vehicles are separate work on top of that.

## Limits

- A single wall photographed square-on cannot give heights (distance and zoom look the same).
- A wall in shade is brightened by a fixed factor unless something white is in the same light.
- No automatic score and no review step: the one look at the overlay is the only check.
- Nothing here runs the game.

## Files

- `.claude/skills/photo-to-building/`: `SKILL.md` (the short flow), `LANDMARK.md` and the two templates (the long way).
- `pipeline/src/drycastle_pipeline/photo_capture.py`: the capture key, the picture and camera of the tab, list/todo/set.
- `landmark_photo.py`: prep, apply; grid, fit, measure, sample, overlay.
- `landmark_tools.py`: footprint, render, near. `landmark_kit.py`: walls, windows, roofs for landmarks.
- Done with it so far: `config/buildings/w378920410.yaml` (G-0377, one Street View photo).
