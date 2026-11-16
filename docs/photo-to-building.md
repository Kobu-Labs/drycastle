# Photo to building

Turn photos of a real building into a hand-modelled landmark of the map: a Python module that builds the mesh on the
building's real footprint. Exterior only. Issue #329; the bus garage (`garage.py`, ADR 0027) is the reference model.

## How to trigger it

- **Explicitly:** `/photo-to-building` in Claude Code, then the property and the photos. It is a project skill
  (`.claude/skills/photo-to-building/SKILL.md`), so every session in this repo has it.
- **By asking:** "make a 3d model of this building from the photo", "model G-0377 from this photo, taken from around
  point A". The skill's description makes the model load it on its own.
- **On a cheaper model:** it is written for sonnet at high or medium effort. Switch with `/model` first, or ask the
  session to hand it to a sonnet agent ("run photo-to-building on G-0377 with a sonnet high agent").

What to give it:
1. The property (`G-xxxx`), parcel (`P-xxxx`) or building id.
2. One or more photos, each with its side: a compass side, "front", or better a **viewpoint**: drop a sketch point in
   the editor where the photo was taken and name it ("from point A"). It is read from `~/.local/state/drycastle/sketch.json`.
3. Hand-held or Street View (the camera height differs).

Best photos are taken from a corner, so two walls show, with the ground line and the eave visible at the near corner.
Two photos from opposite corners cover all four walls; walls with no photo are built without openings and reported as guessed.

## What happens

| step | what | tool |
|---|---|---|
| 1 | Footprint: corners, edges, the side each faces, which walls a viewpoint can see | `landmark_tools footprint G-xxxx --view=X,Y` |
| 2 | Each photo is matched to an edge; the table is shown, and you are asked when two edges fit | |
| 3 | A camera is fitted to the photo on the footprint: eave, ridge height and ridge position come out measured; windows and doors are measured from two pixels each; colours are sampled | `landmark_photo grid`, `fit`, `measure`, `sample` |
| 4 | The template is copied and its constants filled; the model is registered | `template_building.py`, `landmarks.py`, `overrides.yaml` |
| 5 | The model is drawn over the photo and rendered from every side; up to 3 rounds of fixes | `landmark_photo overlay`, `landmark_tools render` |
| 6 | Tests, lint, map export, what stands near the building | `make rules && make export`, `landmark_tools near` |
| 7 | Report: measured or guessed for every number, what was left out | |

You get the overlay picture opened (the model half see-through over the photo). Say what is off; it changes that
constant and renders again. Nothing is committed unless you ask.

## By hand

All from `pipeline/`:

    uv run python -m drycastle_pipeline.landmark_tools footprint G-0377 --view=-589057.5,-1250175
    uv run python -m drycastle_pipeline.landmark_photo grid photo.png --out /tmp/fit
    uv run python -m drycastle_pipeline.landmark_photo fit photo.png w378920410 --view=-589057.5,-1250175 --cam /tmp/fit/cam.json \
        c1:ground:443,535 c1:eave:432,318 c0:eave:122,332 c0:ground:125,484 c2:eave:738,352 r1:top:568,120
    uv run python -m drycastle_pipeline.landmark_photo measure /tmp/fit/cam.json --edge 0 158,368 208,440
    uv run python -m drycastle_pipeline.landmark_photo sample photo.png 250,400 --white 331,372
    uv run python -m drycastle_pipeline.landmark_photo overlay photo.png /tmp/fit/cam.json w378920410 --out /tmp/fit
    uv run python -m drycastle_pipeline.landmark_tools render G-0377 --out /tmp/render [--model NAME] [--collision]
    uv run python -m drycastle_pipeline.landmark_tools near G-0377

A fit point is `WHERE:LEVEL:X,Y`: a footprint corner `c<n>` or the ridge end over an edge `r<edge>`; `ground`, `eave`,
`top` or a height in metres; the pixel. Each module's docstring has the details.

## What it covers

- One building with four corners (two sharing a wall: see `garage.py`).
- Gable (the ridge may be off the middle), lean-to or flat roof; sheet metal, tiles or shingles.
- Windows and ground-level doors, drawn on the walls.

It stops and says so on: hipped roofs, L-shapes, more than four corners, a building that already has a model.
It leaves out and lists: outside stairs, balconies, loft doors, gates, fences, sheds, drainpipes, poles.
Interiors, doors that open and parked vehicles are separate work: the exterior is built, the rest is reported (ADR 0027
shows what the garage took).

## Limits

- A single wall photographed square-on cannot give heights (distance and zoom look the same); they come from rules of
  thumb and are marked low confidence. Positions along the wall are still measured.
- Colours of a shaded wall need a white surface in the same shade, else they are brightened by a fixed factor.
- The overlay is judged by eye; there is no overlap score.
- The renderer has no shadows or surroundings, and nothing here runs the game.

## Files

- `.claude/skills/photo-to-building/`: `SKILL.md` (the steps and gates), `template_building.py`, `template_test.py`.
- `pipeline/src/drycastle_pipeline/landmark_kit.py`: walls, windows, roofs.
- `landmark_tools.py`: footprint, render, near. `landmark_photo.py`: grid, fit, measure, sample, overlay.
- Built with it so far: `house_g0377.py` (G-0377, one Street View photo).
