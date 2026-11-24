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
    uv run python -m drycastle_pipeline.photos.streetview_follow --launch --capture

Aim the middle of the view at a house and press **C** in the Street View tab. The picture of the tab is saved with the
camera it was taken with (place, heading, tilt, zoom, all from the tab's address) under the building the middle of the
view hits: `data/village/photos/<building>/<n>.png` and `.json`. The follower prints which building and walls it took
it for. Take two per house, from opposite corners, each with the ground line and the eave of the near corner in view.

    uv run python -m drycastle_pipeline.photos.photo_capture list                  # what there is
    uv run python -m drycastle_pipeline.photos.photo_capture todo                  # buildings with captures and no detail yet
    uv run python -m drycastle_pipeline.photos.photo_capture set w123/0 w456       # a capture filed under the wrong building

Then: "run photo-to-building on w378920410", or on everything `todo` lists.

Captures are Street View imagery: gitignored, never shipped. `docs/streetview.md` says such images are for human eyes
only and that nothing is generated from them automatically; measuring buildings from captures goes beyond that, by the
owner's decision of 2026-11-16.

**Your own photo.** Paste it and say where it was taken: a compass side, or better a sketch point dropped in the editor
("from point A"). The camera is not known, so it goes the older way, where the model reads pixels off a grid
(`SHEET.md` in the skill folder, `landmark_photo prep` / `apply`). That way is weak: use captures when you can.

## What happens (captures)

The computer finds the geometry; the model only names what it sees, by letter and number. It is never asked for a
pixel or a size. Output: a building detail (`pipeline/config/buildings/<id>.yaml`: openings) and the building's entry
in `pipeline/config/overrides.yaml` (roof form, heights, colours). No code; the usual generator builds it.

| step | who | what | files |
|---|---|---|---|
| 1 | tool | `facade_pipeline ends <building> [--out DIR]` (default DIR: `data/village/photos/<building>/run`, kept so `apply <building> --replace` re-runs without the model; `facade_pipeline read <building>...` answers the questions by one API call each, `answers REPLY.json` files an agent's batch reply): per capture, heading and tilt from the straight lines in the picture; per wall it sees, the upright lines near each end, lettered | `ends_c<n>_e<edge>_left.png`, `..._right.png`, `ends_ask.md` |
| 2 | model | which letter is where each wall stops | `ends.json` |
| 3 | tool | `facade_pipeline walls DIR`: the camera moved so the footprint's corners fall on the chosen lines, its height from the terrain; each wall flattened to a straight-on picture in metres, level lines lettered, rectangles numbered | `wall_c<n>_e<edge>.png`, `eave_...png`, `walls_ask.md` |
| 4 | model | the line that is the wall's top, what each rectangle is, a plinth's upper line, the roof's form, the roof's, walls' and frames' colours by name | `names.json` |
| 5 | tool | `facade_pipeline apply DIR`: heights, openings, the ridge from the roof's outline against the sky, the overhang, eaves, plinth, downpipes; both files; the model drawn over each photo, beside it, and from four corners | `overlay_<n>.png`, `compare_<n>.png`, `render_<k>.png`, `result.json` |
| 6 | model | one look at the overlay and the comparison, at most one correction | |

`apply --dry` measures and draws without writing. After a batch: `make rules && make export` once. Nothing is
committed unless you ask.

**Capture guide** (`photos/capture_guide.py`, on with `streetview_follow --capture`; `--no-guide` for the bare key). In the
Street View tab the walls of the building in the middle of the view are outlined as the map has them: solid with a
tick = both ends in view and at most 35 degrees off square, dashed with a triangle = usable but steep, dotted with a
cross = not usable (the panel says why). `C` holds the view; for each corner in view put the blue line where the wall
itself ends (mouse or arrow keys; `S` = hidden, Backspace = undo, Esc = discard after a confirmation). The outline
is refitted to the marks and shown. Then, per usable wall: what the roof does above it (`E` eave, `G` gable, `N`
neither) and a line along the wall's top; then a cross on the ridge; then, once per house, roof form and roof, wall
and frame colour from the pipeline's lists (a later capture of the house only asks "still right?"). Enter on the
last step saves. The marks go into the sidecar (`ends`: corner number to picture column; `marks`: `walls`,
`ridge`, `house`, in picture pixels) and stand above the model's answers: `ends` asks nothing for marked corners,
and `apply` takes a marked wall top, roof edge, ridge and house description over what `names.json` says. The model
is left with the rectangles (windows, doors, dormers) and the plinth. The view is read from the tab's address, which Google updates when the view comes to rest: the outline
follows about half a second behind.

How it gets the camera without a clicked point:

- **Heading.** A capture's heading is a compass bearing; the map's grid north is about 6 degrees off it in the village.
  It is turned to the grid, then refined: the walls' directions are known from the footprint, so the straight lines of
  the picture must run along them or straight up.
- **Place.** A capture's place is good to a metre or two. Two wall ends chosen in the picture fix it.
- **Height.** The terrain under the camera plus 2.5 m (the car's mast), against the building's base. A fence hiding
  the wall's foot does not matter.

## Made up by rule (`house_rules.py`)

One capture square in front of the street wall is enough. The guide then asks for that wall's two corners only
(`streetview_follow --capture`; `--full-marks` brings back the longer marking). `apply` measures the front as before
and makes up the rest from it, each part following from facts already known, never from a house "type":

- walls get a role: under the pitched roof or not, eave or gable, on or near a neighbour;
- windows as a house has them: ONE size for the whole house (the middle of the measured ground-floor ones, corrected
  when a hidden foot made it read short and high; measured windows near that size take it), every head on one line,
  evenly spaced along a wall (one per 3.6 m or so). A wall too short for it gets none or, for some, the house's small
  second window (0.6 m square); a wall under a lower roof the small one. None on or within 1.5 m of a neighbour.
  Windows high in a gable when the roof rises 2.3 m or more; one door on the side turned from the street when no
  door was measured;
- the roof: the wall that shows most of itself (long and square-on) decides ridge direction and wall top; its pitch
  must be 25..52 degrees or a 30 degree roof is built. It is fitted to the whole footprint (`roof_fit: true` in the
  override, `roof_fit.py`): the footprint, jogs under 0.6 m smoothed away, is covered with overlapping rectangles,
  each as large as the footprint lets it be; each carries its own roof at the main roof's slope, and the highest
  surface wins, so valleys and steps form by themselves. The largest rectangle is the main roof. On a hipped house
  every part is hipped. On a gabled one a wing in line carries the roof on, lower; a wing 2 m or more out of an eave
  wall ends in a gable of its own, a shallower one in a hip; a wing at its own angle has its ridge along its length.
  What no rectangle covers (a bay, a shallow porch) gets a small hipped roof that dies into the roof behind it.
  When the wall that leads is a gable wall, the main roof stands on that wall (as wide as it, `roof_lead` in the
  override), its ridge height is read over that wall, not over the house's middle, and a pitch up to 60 degrees is
  believed. A part no more than 4.5 m out of an eave wall of a gabled house (a garage beside it) gets a single slope
  from the eave down to 2.4 m. A "wall top" read on a wall that runs across the ridge is not the eave and is not
  used; with no eave measured it is put over the ground floor's openings and raised until the roof passes over the
  gable wall's upper windows.
  A hipped part's sides are laid on the house's own walls (walls are seldom quite square), so its eaves are level;
  the rim runs along the parts' edges, and the roof is carried on to it wherever a wall runs askew;
- downpipes at the ends of eave walls, clear of openings (a hipped roof: its two longest eaves);
- the overhang when not measured, a boxed eave under a hipped roof, rafter tails under a steep gabled one;
- a chimney behind the ridge on the side the model saw it (`chimney` in `names.json`), or, unseen on a house of
  35 m2 or more, a third along the ridge (the end from the building's id, so every run gives the same);
- the band round the windows (`surround_colour`) on every window;
- the trim, one colour for all of it: a cornice band under the eave on plastered walls, a string course on a house of
  two floors, corner strips, shutter boxes over the windows, the gable in its own colour. `names.json` says which
  (`cornice`, `corner_strips`, `shutter_boxes`: yes/no; `gable_colour`); what it leaves unsaid is decided from the
  building's id. A part with no room above the window heads is left out; brick, timber and stone walls get none;
- a balcony when one was seen on the front (`balcony` in `names.json`): on the gable wall, over the ground floor's
  openings, nearly as wide as the wall, a railing of level boards; the window over it becomes the door onto it;
- doors: panels, steps up to the sill (a made-up door stands on the plinth), a canopy on a gable wall or under an
  eave a metre or more above the door's head;
- the roof: cap tiles along ridge and hips, a cap on the chimney, roof windows on unseen slopes of a gabled roof with
  rooms under it and no dormers, pipes that kick out at the foot and slant out to the gutter;
- with `--no-guess` the older split is used instead: the roof over the rectangle behind the eave wall (`roof_part`),
  the rest a lower block (`rest_roof`: flat, `lean_to` or `auto`).

The wall colour is the named one moved slightly toward the photo's hue. `apply` prints one line
`MADE UP BY RULE` listing all of it (also `invented` in `result.json`); made-up openings have confidence low.
`--no-guess` turns every rule off.

## What it covers

- One building; gabled, hipped or flat roof (as the generator builds them: ridge in the middle).
- Windows, doors and garage doors as real openings, on every wall a capture sees with both its ends in the picture.
- One to three captures (the newest); an opening seen twice is taken from the capture that sees it most square-on.
- The roof's overhang, from where the roof's corner lies past the house's corner (not seen: 0.4 m, reported). The
  line named as the wall's top is corrected when it is the underside's outer edge. A hipped roof with a measured
  overhang gets a level boxed eave (`level_soffit`).
- Eaves: fascia and gutter; the fascia light when the board under the gutter is as light as the wall, with the
  gutter and downpipes in the downpipes' colour.
- A plinth (its upper line is named), downpipes, window frames, sills and upright posts. A window whose posts are
  hidden takes those of a window of its size.
- Colours are named, not sampled: a wall in shade photographs grey whatever its paint. `wall_colour`,
  `roof_colour` and `frame_colour` each pick from a fixed list in `facade_pipeline.py`.
- Walls no capture saw get windows like the measured ones, confidence low, reported; `apply --no-guess` leaves
  them blank.

Left out and reported (`PROBLEM:` lines): an opening the computer drew no
rectangle for; a wall with fewer than two chosen ends; a ridge the outline does not give. Not modelled at all: stairs,
balconies, gates, fences, sheds, chimneys (the generator has `chimneys`; nothing measures them yet), a ridge off the middle, lower wings under the same footprint.

First test (w378920491, one capture, 2026-11-23): the wall outline, its foot and top and both windows sat on the
photo; three sonnet runs agreed on a wall end where the candidates were distinct and disagreed where they were closer
than 15 px (such lines are now one candidate).

## Special buildings

Lean-to or split roofs, two buildings sharing a wall, a hollow part, an L-shape: `LANDMARK.md` in the skill folder, the
long way. It writes a Python module on `landmark_kit` (reference: `buildings/landmarks/garage.py`, ADR 0027) and costs several times more.
Interiors, doors that open and parked vehicles are separate work on top of that.

## Limits

- A wall seen at more than 78 degrees off square, or with an end outside the picture, is not measured.
- Windows are found by their edges and by their colour against the wall; a window with neither (a white frame on a
  white wall in flat light) gets no rectangle and is reported as missed.
- The wall's top is a choice between lines that can be 10 cm apart; the stretched `eave_...png` is there for that.
- One capture cannot check itself. Two captures from different corners that give the same wall top are the only
  objective check.
- No automatic score: the one look at the overlay and the comparison is the only review.
- Nothing here runs the game.

## Files

- `.claude/skills/photo-to-building/`: `SKILL.md` (captures, the short flow), `SHEET.md` (a photo of your own),
  `LANDMARK.md` and the two templates (special buildings).
- `pipeline/src/drycastle_pipeline/photos/photo_capture.py`: the capture key, the picture and camera of the tab, list/todo/set.
- `photos/facade_rectify.py`: segments, heading and tilt from them, wall-end candidates, the flattened wall.
- `photos/facade_pipeline.py`: the three commands (`ends`, `walls`, `apply`), level lines, rectangles, ridge, colours.
- `photos/landmark_photo.py`: the older way (prep, apply; grid, fit, measure, sample, overlay) and the shared camera and
  overlay code.
- `photos/landmark_tools.py`: footprint, render, near. `buildings/landmarks/kit.py`: walls, windows, roofs for landmarks.
