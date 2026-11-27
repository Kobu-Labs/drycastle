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

What is left, as the geometry lint reports it:
- dormer side walls stand above the dormer roof (a few houses);
- pipes or trim cross a lower roof at a wing junction;
- w378920518: the main gable stands over the wing's lean-to; the wing really has its own gabled roof;
- w378920522: its high window on edge 4 counts as a gable window, because its sill is at the eave.
