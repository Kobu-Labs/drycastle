---
name: photo-to-building
description: Model one real building of the village in the map from photos of its sides (one or more photos, each with its side or viewpoint). Use for "make a 3d model of this building from the photo", "model parcel P-xxxx / G-xxxx / building wxxxx from these photos", "this house looks like this [image]". Exterior only. Not for interiors, doors that open, or game code.
---

# Photo to building

Measure a house from its Street View captures and write its building detail: `pipeline/config/buildings/<id>.yaml`
plus its entry in `pipeline/config/overrides.yaml`. No code. The computer finds the geometry; you only name what you
see: letters and numbers, never pixels or sizes. Three commands, two answer files.
Commands run from `pipeline/`. Scratch files go in your scratchpad directory (`OUT` below).

## Rules

- Never edit the footprint, `edits.json`, or anything but the two files `apply` writes. No game, no screenshots, no
  commit, no `make`, no tests: the map is exported once after a batch, not per building.
- Never estimate a size or a position yourself, and never change a number in `state.json`. If the candidates do not
  hold the right thing, answer `"none"` (or `"no"`): the tool reports it. A wrong letter is worse than `"none"`.
- The building has no captures (`photo_capture list`), or the user gave a photo of their own: that is the other way,
  `SHEET.md` in this folder. Lean-to or split roofs, two buildings that are one, an interior: stop and say it needs
  `LANDMARK.md` (same folder). Do not read either file otherwise.

## Who answers (cheapest first)

The two answers (`ends.json`, `names.json`) can be given three ways; the questions and files are the same.

1. **One API call per question, no agent** (about 9k tokens a house instead of about 30k). Needs `ANTHROPIC_API_KEY`
   (repo `.env`) and `uv sync --extra reader`:

       uv run python -m drycastle_pipeline.photos.facade_pipeline read <id> <id> ...

   It answers each folder's open question (ends first, then walls), checks the reply against the template and writes
   the file. Run it after `ends` and again after `walls`.
2. **The `facade-reader` agent, several houses at once** (no key; sonnet, effort medium). Give it up to 5 run
   folders with their ask file; it reads only and replies one JSON object `{folder: answer}`. Save the reply to a
   file and run `facade_pipeline answers REPLY.json`: it checks each answer and writes it into its folder.
3. Answer yourself, as steps 1 and 2 below describe.

## 1. Wall ends

    uv run python -m drycastle_pipeline.photos.facade_pipeline ends <building id>   # OUT defaults to data/village/photos/<id>/run; walls/apply take OUT or the id

Wall ends marked by hand at capture are not asked ("every wall end was marked by hand": `ends.json` is already
written, go to step 2). It prints which walls each capture can measure. "NO wall ... measurable" for every capture: stop and report; the
house needs a capture from another place. Read `OUT/ends_ask.md`, then every picture it lists, and write
`OUT/ends.json` as it says.

## 2. What is on each wall

    uv run python -m drycastle_pipeline.photos.facade_pipeline walls OUT

Read `OUT/walls_ask.md`, then every `wall_*.png` it lists and the capture photos it names, and write
`OUT/names.json` as it says.

## 3. Apply, and one look

    uv run python -m drycastle_pipeline.photos.facade_pipeline apply OUT

It measures, writes both files and draws, one per capture, `OUT/overlay_<n>.png` (the model, half see-through with a
magenta outline, over the photo) and `OUT/compare_<n>.png` (the photo beside the model from the same place). Read
each once. In the overlay the wall's two ends, its foot and its top, and the windows must sit on the photo's. In the
comparison the two houses must look like the same house: roof form and colour, wall colour, frames.

- A wall end clearly beside the house's (the outline runs on over an attached wing or stops short of the corner, by
  more than a window frame's width): that letter in `ends.json` was wrong. Fix it, run `walls` again, answer
  `names.json` again from the new pictures (its letters and numbers are new), then `apply --replace`.
- A window or door that is not one, or one missing though it had a rectangle, or a colour plainly not the house's:
  fix `names.json`, `apply --replace`.
- The roof higher or lower than the photo's, or everything else: leave it, say so in the report.

Walls no capture saw get windows like the measured ones (a `PROBLEM:` line says which walls): a guess, to be
replaced by a capture of those sides. `--no-guess` leaves them blank; use it only if the user asks.

`--replace` is needed after a correction because the first `apply` has already written the entry. Do not loop past
one correction. "already has an entry" on the FIRST apply: the building was done before; stop and report unless the
user asked to redo it (`--replace`).

## 4. Report (6 lines at most)

Building and parcel; the roof's form with the wall top and roof top heights; number of openings measured and, apart, guessed; every `PROBLEM:`
line; what the overlay and the comparison showed; what was not modelled.
