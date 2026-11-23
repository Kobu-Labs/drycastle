---
name: photo-to-building
description: Model one real building of the village in the map from photos of its sides (one or more photos, each with its side or viewpoint). Use for "make a 3d model of this building from the photo", "model parcel P-xxxx / G-xxxx / building wxxxx from these photos", "this house looks like this [image]". Exterior only. Not for interiors, doors that open, or game code.
---

# Photo to building

Measure a house from photos and write its building detail: `pipeline/config/buildings/<id>.yaml` plus its entry in
`pipeline/config/overrides.yaml`. No code. Two tool calls and two pictures to read; keep it that small.
Commands run from `pipeline/`. Scratch files go in your scratchpad directory (`OUT` below).

## Rules

- Never edit the footprint, `edits.json`, or anything but the two files `apply` writes. No game, no screenshots, no
  commit, no `make`, no tests: the map is exported once after a batch, not per building.
- Not the building's own walls, roof, windows and doors (stairs, balconies, gates, fences, sheds, pipes, poles): leave
  out, name them in `not_modelled`.
- Lean-to or split roofs, an L-shaped or many-cornered building, two buildings that are one, an interior: stop and say
  it needs `LANDMARK.md` (same folder). Do not read that file otherwise.
- Do not estimate sizes by eye. Pixels in, metres out.

## 1. Prep (one command)

The building has captures (pictures of a Street View tab saved with their camera, `photo_capture list`):

    uv run python -m drycastle_pipeline.photos.landmark_photo prep <building id> --out OUT

A photo the user gave you instead:

    uv run python -m drycastle_pipeline.photos.landmark_photo prep <G-xxxx | P-xxxx | building id> --photo PHOTO --view=X,Y --out OUT

`--view`: where that photo was taken. A sketch point named by the user: its `x, y` from
`~/.local/state/drycastle/sketch.json`, as they are. None given: leave `--view` out, and in the sheet use a point 10 m
out from the middle of the photographed edge.

It prints, per photo, the footprint: corners `c0..`, edges with length and the side each faces, `view: VISIBLE /
HIDDEN`. Stop if a building shows a `model` other than `None`. Then read `OUT/grid.png` (captures: `OUT/grid_<n>.png`,
the newest two, already cut to where the building is and enlarged). Yellow lines are x, cyan lines are y, and EVERY
line carries its number in pixels of the full photo. To read a point: take the numbers of the two lines it lies between
and its place between them (halfway between 1050 and 1100 is 1075). Never measure the picture as shown to you; it is
scaled. Ignore the map's own buttons and labels.

MORE THAN 4 CORNERS usually means the footprint holds a house and its extensions; the roof outline you mark in step 2
lets the tool sort that out.

Which wall is which: only VISIBLE edges are in the photo. Seen from outside, an edge runs from its first corner on the
LEFT to its second on the RIGHT. Two edges fit equally and the user is there: ask. Otherwise take the likelier one and
say so.

## 2. Sheet, then apply (one file, one command)

Write `OUT/sheet.json`. You mark points; the tool works out heights, the roof's form and how much of the footprint
the house covers. Do not decide those yourself.

    {"building": "w378920451", "what": "house, cream render, dark shingle roof",
     "source": "Street View captures of 2022-09",
     "photos": [
      {"capture": 0,
       "points": ["c4:ground:1088,1075", "c4:eave:1088,392", "c3:eave:547,485"],
       "profile": {"edge": 4, "px": [[1495, 110], [1660, 300]]},
       "openings": []},
      {"capture": 1,
       "points": ["c4:ground:1370,960", "c4:eave:1380,425", "c3:eave:560,485", "c3:ground:560,800"],
       "openings": [{"edge": 3, "kind": "window", "px": [[670, 624], [807, 776]]},
                    {"edge": 3, "kind": "door", "px": [[600, 400], [640, 520]], "colour": "#6b3a2a"}]}],
     "colours": {"wall": [800, 560, 0], "roof": [900, 330, 0], "frame": "#e6e8e8", "wall_in_shade": true},
     "overhang_m": 0.3, "not_modelled": "gate, yard wall, chimney"}

Use BOTH captures: one entry in `photos` each. Every pixel is read off that capture's own grid.

**`points`** (3 or more per photo, `WHERE:LEVEL:X,Y`): building corners you can see. WHERE is a footprint corner
`c<n>`. LEVEL is `ground` (wall meets ground) or `eave` (top of the WALL, under the gutter; not the roof's edge).
- Always: the corner nearest the camera at `ground` AND at `eave`. One `ground` point per photo is a must.
- Then the far end of each wall you can see, at `eave`, and at `ground` if it shows.
- Only real footprint corners. The footprint is often LONGER than the house (a gate, a yard wall, a shed share it):
  if the house's wall ends before the footprint's corner, that corner is not on the house. Leave it out.
- A corner hidden behind a hedge, fence or car: leave it out, do not guess. Fewer good points beat more bad ones.
- The near corner's foot is hidden (very common): follow the visible foot of the wall along to the corner and mark
  where it would meet it. If no foot of that wall shows at all, take the `ground` point at another corner.

**`profile`** (in the ONE photo that best shows the wall on which you see the roof's outline: usually the gable end,
the wall with the triangle; this is how the roof is measured): `edge` = that wall's edge, `px` = two pixels on the
roof's outline where it meets that wall:
1. the PEAK: the highest point of the outline (the tip of the gable, or where a slope stops climbing);
2. the END: following the outline on past the peak, where it finishes: back down at the wall's far corner at eave
   height for a gable, or, if the roof stays up, the furthest point of it you can see. Cannot see past the peak:
   give the peak alone.
Take the underside of the roof (where wall meets roof), not the top of the tiles. A gable whose tip is cut off by a
small sloping face (a half-hip): the PEAK is the end of the ridge, the highest point of the roof above that wall. No side wall with the outline in
any photo (only eave walls are seen): give no profile; the building is then written with a flat roof and flagged.

**`openings`**: two opposite corners of each window's glass or door leaf, and the edge it is on. Each opening once,
in the photo that shows it most square-on. A door high on a wall is fine. A gate in a yard wall is not the building.

**`colours`**: `[x, y, photo number]` well inside a clean, typical part of the surface (not near its edge, not on a
roof window), or `"#rrggbb"`. `wall_in_shade`: true
when the sampled wall is in shadow. `"white": [x, y]` only if something white is in the same light on that wall.

Other keys, only when needed: `"roof": "flat"` or `"hipped"` (then no profile); `"rest_height_m"`: the height of the
lower part of the footprint beside the house, when the tool reports it guessed one and you can see it;
`"photo"`, `"view": [x, y]`, `"cam_height": 1.6` in place of `"capture"` for a photo the user gave (2.5 for a Street
View screenshot); with such a photo give 5 or more points.

Then:

    uv run python -m drycastle_pipeline.photos.landmark_photo apply OUT/sheet.json --out OUT

It fits the camera, measures, samples, writes both files and draws `OUT/overlay_0.png`.

- It prints `roof read from the profile: ...`: check that sentence against the picture (gable or one slope then
  flat; house narrower than the footprint or not). If it is plainly wrong, a profile pixel is wrong: re-read it.
- `fit error ... BAD`: that photo's numbers were not used. A `WARNING` about the camera: its heights are doubtful. Re-read the points it marks on `grid.png`, fix the
  sheet, run again with `--replace`. Still bad on the third run: stop and report; a wall matched to the wrong edge
  gives exactly this.
- `PROBLEM:` lines are things it left out or guessed. Copy them into the report. "the photos disagree on the eave":
  re-read the ground and eave points of the near corner in both pictures once, then accept what it says.
- "already has an entry": the building was done before. Stop and report unless the user asked to redo it.

## 3. One look

Read `OUT/overlay_0.png`: the model, half see-through with a magenta outline, over the photo. Eave line, wall corners
and windows must sit on the photo's. One of them clearly off: fix it in the sheet and apply once more with `--replace`.
Everything too high or too low together: the `ground` point of that photo is wrong. One window off: its two pixels.
The roof off but the walls right: a `profile` pixel. The ridge tip may sit beside the real one (the generated roof has its ridge in the middle); that is known,
leave it. Do not loop past one correction.

## 4. Report (6 lines at most)

Building and parcel; fit error; roof, eave and top; number of openings; every `PROBLEM:` line; what was not modelled.
