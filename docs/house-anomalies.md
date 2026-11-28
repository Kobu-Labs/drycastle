# House anomalies (photo-built houses)

Anomalies found by hand in photo-built houses (2026-11-27), the detection for each and the fix. `house_lint` checks every house for these; the fixes run in the pipeline, not by hand per house.

## Reported (group, building, what is wrong)

- G-0204 w378920436 - second floor has balcony, that was not captured, there is also a window spanning two floors
- G-0205 w378920346 - the street facing wall has only one window; a wall of this shape is in 99% symmetrical, so two centred windows
- G-0207 w378920542 - same issue as above
- G-0208 w378920465 - same issue as above
- G-0438 w378920324 - same issue as above
- G-0185 w378920518 - overflowing geometry of walls over the roof
- G-0017 w378920522 - different asymmetric windows on street facing wall, weird small window, almost floating, on one wall
- G-0035 w378920527 - two roofs intersecting and clipping through each other
- G-0221 w378920494 - geometry clipping through roof
- G-0025 w378920434 - white strip flickering at the ground of the garage doorway
- G-0073 w378900219 - weird unreal window layout on street facing wall
- G-0165 w378900292 - one huge triangle, does not reflect the actual layout of the house
- G-0343 w378920415 - one window was obstructed in the capture and is rendered very irregularly; one floor of one wall should have mostly consistent windows
- G-0427 w378920533 - similar as above
- G-0350 w378920345 - same as above and missing window
- G-0352 w378920390 - very unusual window layout
- G-0202 w378920368 - window spanning two floors, weird brown rectangle, not sure if door or garage door

## Classes

| Class | Houses | Detect | Fix |
| --- | --- | --- | --- |
| Gable/short street wall, one off-centre window | 0205, 0207, 0208, 0438 | short wall, 1 window, off-centre | two windows symmetric about the centre |
| Irregular windows on one floor of a wall (obstructed, missing, odd sizes) | 0343, 0427, 0350, 0352, 0073, 0017 | spread of sill, head, width, spacing per wall and floor | common sill, head and width; even spacing; fill gaps; drop tiny strays |
| Window spanning two floors | 0436, 0368 | height > storey | split into one per floor, or a stair window only when flagged |
| Walls or gable through the roof | 0518, 0494, 0292 | wall top above the roof surface; gable triangle area | clamp to the eave; another roof form |
| Two roofs crossing | 0527 | roof parts intersect | merge, or cut the lower at the higher |
| Flicker at the garage doorway | 0434 | z-fight check at the doorway, sill vs plinth and apron | offset |
| Brown rectangle: door or garage door | 0368 | door with garage proportions | classify by width |
| Balcony not captured | 0436 | | the wall prompt asks for balconies |

## What is done (2026-11-28)

`uv run python -m drycastle_pipeline.buildings.house_lint [ids] [--no-geometry] [--fix]` lists the anomalies of every photo-built house:
- opening checks: the changes `house_rules.regularise` would make; `--fix` writes them, for houses with no run folder;
- geometry checks: `buildings/lint_geometry.py`, also runnable on its own.

`facade_pipeline apply` runs the same rules, so a re-applied house comes out fixed.

| Class | Fix |
| --- | --- |
| Lone off-centre window | `mirror_lone`: mirrored about the wall's (or a central door's) middle; a row of 2 or 3 like windows is centred |
| Irregular row | `fix_rows`: heads on one line; a smaller odd one takes the row's size (never shrinks a larger one); a half-hidden narrow one is widened. `door_heads`: ground windows on the door's head line. `join_sashes`: boxes under 0.45 m apart are one window |
| Openings seen but not boxed | the wall question's `missed` is now a list `{kind, at, floor}`; old word answers are parsed (`missed_items`). Apply puts them where the answer said |
| Window over two floors | `split_tall`: one opening per floor; cut under the eave on a one-storey house |
| Window reaching the eave | `under_eave`: moved under the eave, onto its floor's head line |
| Garage door | `fix_garages`: at least 2.4 x 2.1 m, capped under a window over it; 4.2 m or wider is two doors |
| Balcony not built | `invent_balcony` also on two-storey and flat-roofed houses, at the upper floor |
| Overlaps | `drop_overlaps`: a guessed opening yields to a measured one, a window to a door |
| Walls or gable through the roof | the interior (inner walls, ceiling) is clipped under the roof; a wing meeting a higher gabled part is cut at its ridge |
| Huge gable | a gable read on a wall far longer than the house is deep is taken as an eave wall (`roof_fit.gable_believable`) |
| Doorway flicker | a 3 cm threshold with a riser; no sill board at ground level |

## Geometry pass (2026-11-28, second)

`uv run python -m drycastle_pipeline.buildings.lint_geometry [ids]` over every house with a detail. What it found and the fix (general, in the builder or in `apply`; a house picks the apply-side ones up when re-applied):

| Class | Cause | Fix |
| --- | --- | --- |
| Board, gutter or cap through a lower roof at a wing junction | at a step between parts of a fitted roof the rim's and the cap's end heights were the higher part's | `openings._run_ends`: heights taken just inside the run; a ridge cap stops where a higher roof covers its sides; its skirt is at most 13 cm deep |
| Canopy, surround or lintel through the roof | a canopy under an eave at the door's head; a gable window's surround over the wall's top | no canopy under such an eave; surrounds and lintels end under the wall's top |
| A part's roof up through another's | the largest rectangle went next even when it only crossed the corner of an L; a wing stood out of a hipped (or in-line) part and came up in its far half; a lower part far wider across its ridge than long | `roof_fit.choose`: the rectangle that roofs most of what is still bare goes next; `_stop_inside`: a part that stands out of a higher one on one side stops at its middle; a lower part's ridge runs along it when its gable would be over 8 m and 1.5 times its length |
| Roof not believable | a ridge across a long house (an eave read on an end wall, a misread gable): one gable 16 to 38 m wide; a roof read steeper than any was built at 60 degrees | `roof_fit.fit` turns a main ridge whose gable is not believable (`gable_believable`), `apply` writes the turned ridge (also for a plain roof); a too-steep reading, or one over 50 degrees and 1.3 times the walls, is built at 45 degrees |
| Dormer cheeks above the dormer roof; dormer roof through the house's | the cheek's back foot rose above its top; the dormer's eaves ran on under the house's roof | the cheek ends where the roof comes up to its top; the dormer's eave ends where it meets the roof |
| Ground flicker | a door step's front in the plane of the apron's edge; a low window's sill board on the plinth's ledge | the step stands 3 cm past it; no ledge under a sill board |
| Rafter tails up a step | a rafter under an eave next to a higher part took that part's roof height | a rafter whose ends differ more than a slope gives is left out |
| A narrow wing beside the house with a single slope, the house's gable standing over it (w378920518) | a lean-to was given to a part that runs on past the house's gable end | such a part gets its own gabled roof, ridge along the house's |
| Eave too high, two storeys on a one-storey house (w378900292) | the gable guard turned the gable wall into an eave wall, and the readings on the end wall (now across the ridge) were thrown away | when a named gable is not believed: an end wall the roof was seen coming down to keeps its reading, and the roof is hipped (the roof comes down to the end); the not-a-gable wall's own top reading is not used when another wall was read; with no wall along the ridge read and no gable verge read, a gable wall's top over the ground floor's openings and under 4.2 m is the eave |

Lint changes (false positives, checked on the renders): a trim piece passes through a roof only when it is above and below one roof plane (under a higher part's overhang it passes through none); a part's roof piece that runs on from its own area across the other part's wall (5 cm away) is not "coming up"; a roof that rises more than 1.35 times its walls is not believed only when it is also steeper than 50 degrees (a 44 degree roof over an attic, w378920362, is real), and one roof climbing more than 8 m from its eave (a gable 16 m or more wide) or steeper than 62 degrees is never believed.

`apply` refuses to write when the overrides entry exists and `--replace` was not given before anything is written (w378920491's detail was written for a gabled roof, its overrides entry stayed hipped).

What is left, as the geometry lint reports it:
- w378920323: an askew main part with four parts around it: one comes up 4.75 m2 inside it;
- w378900386, w378900384: a small bump at a corner comes up under 1 m2 inside the house's roof;
- w378920451: `apply` stops: a capture's photo is missing;
- w378900271: `apply` stops: `house_rules` cuts a window under the eave to a negative height (eave read 2.82 m);
- w378920522: its high window on edge 4 counts as a gable window, because its sill is at the eave.
