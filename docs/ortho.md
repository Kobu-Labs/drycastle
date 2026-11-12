# Aerial photo (reference only)

The orthophoto of the area, to look at while editing: `make ortho` downloads it, the map editor lays it on the ground
(View page, "Aerial photo", or the **O** key). It is **never part of the map**: not exported, not in `out/`, not in a
release. The licence of the source forbids redistributing it and altering it (see `docs/research/slovak-open-data.md`,
"Orthophoto"), so it only lives in the gitignored `data/` and is shown as it is.

```
make fetch                 # once: the area box (area.json) is needed
make ortho                 # data/village/ortho/ortho.jpg + ortho.json, level 11 (0.6 m per pixel, ~530 tiles for the polygon)
make ortho ORTHO_LEVEL=12  # 0.3 m per pixel, four times the tiles
```

Source: GKU Bratislava's ZBGIS orthophoto mosaic, WMTS in EPSG:5514 (`zbgisws.skgeodesy.sk`), attribution "GKU Bratislava,
NLC". The tiles are in S-JTSK already, so there is no reprojection: the pixel grid is the service's tile matrix
`default028mm` (256 px tiles, pixel size = scale denominator x 0.00028 m, level 11 = 0.595 m, level 12 = 0.298 m).

`drycastle_pipeline.ortho` reads `area.json` (the polygon, else the fetched box, plus 25 m), downloads the tiles that cover
it politely (sequential, 0.2 s apart, retries) into `data/AREA/ortho/tiles/LEVEL/ROW_COL.jpg`, and joins them into
`ortho.jpg` cropped to the area. `ortho.json` holds the absolute S-JTSK bounds, the pixel size, the attribution and the
"reference only" notice. Tiles already on disk are not fetched again (`--force` does), a 404 tile stays blank, and more than
4000 tiles stops with a message (use a lower level). Not a map stage: `make run-all` does not run it.

## In the editor

The picture is a mesh draped on the terrain 0.25 m above it (8 m grid, built once when first shown, a second or two for the
whole village), unshaded, with an opacity slider (10-100 %, default 85 %). **O** toggles it, the switch and the slider are on
the View page, and both are remembered in `editor_ui.cfg`. It is a child of the editor outlines, so it disappears in walk
mode. Without a download the switch says what to run. The picture is one texture for the whole area (about 35 MP at
level 11), so it costs some video memory; the lower level is plenty to place things by eye.

## Trees read from the photo

`make ortho-trees` (after `make ortho`; module `drycastle_pipeline.ortho_trees`) reads where the trees are and writes
`data/AREA/ortho/trees.json` and `canopy.png`. It is a local, derived result: only facts read from the photo (positions,
a mask), never its pixels, and into the gitignored `data/`. Whether the map may use them is the licence judgement made in
`docs/research/slovak-open-data.md`; nothing reads them yet, and nothing here reaches `out/`.

The two kinds of tree look different from above, so there are two detectors:

* **Canopy** (forest, dense tree belts). A logistic regression on each pixel's colour, excess green and brightness texture
  at three scales (5, 13, 31 px). Its training labels come from the photo's own area: OSM forest and wood polygons are
  "tree", meadow, farmland, buildings and roads "not tree", each polygon pulled 15 m in from its edge. Because it learns
  context, a dark meadow is not mistaken for forest (the first, threshold-only try was). On polygons it never saw it is
  right 74-88 % of the time, the labels being coarse (clearings inside forests, trees inside meadows). The photo does not
  resolve single crowns in a forest, so it gives an **extent**, not trunks.
* **Standalone trees** (gardens, hedges, roadsides). Compact green blobs darker than a ring around them, at three crown
  sizes (radius about 2, 3.5 and 5.5 m), one circle per crown (a weaker crown overlapping a stronger one is dropped).
  Darkness is **relative** (a tree in dark scrub counts like one on a lawn). The threshold is strict in open ground
  (0.18) and sensitive (0.10) where the neighbourhood is somewhat tree-like (mean canopy probability over about 8 m above
  0.12: gardens, hedges), so open meadow stays clean while a hedge along a path is found. Never inside the canopy, never
  within 2.4 m of a building (roofs and their shadows), and a blob must be green (shadows are not).

Output: `trees.json` lists the standalone trees (`x`, `y` in the map's local metres, `radius_m`, `score`), with the
parameters used, and `canopy.png` is a 1-bit mask on the photo's pixel grid (bounds in the json). Everything is clipped to
the area polygon. The photo is processed in strips of 1024 rows, so it runs on the whole village (about 1.5 minutes for
7 km2: 4200 standalone trees, 18 % canopy); `tree_model.npz` caches the trained model (`make ortho-trees RETRAIN=1`
fits it again), `--tau-open` and `--tau-wooded` move the two thresholds.

Known limits: a forest edge is read as canopy plus a few standalone circles; shrubs and hedges count as trees (they are
indistinguishable in an RGB photo); some roadside shadows still pass. There is no ground truth, so the checks were by
eye on windows of the village (forest, hedge and allotments, open meadow, house gardens). A canopy-height model (surface
minus ground) would find trees far more reliably, but only the ground model is served by the INSPIRE WCS.

### Planting from the detector

The props stage plants **every** tree from `trees.json` and `canopy.png` (`props.ortho_trees`); this is the only tree source.
There are no rule-based trees, OSM trees or shore rows, and no per-parcel tree settings (see `docs/viewer-and-editor.md`).
The detector's output is read from `data/<area>/ortho/`; an area without it, or a `parcels.json` without a frame origin,
has no trees, and `make props` warns. Run `make ortho-trees` first.

* Each standalone tree is planted where it was found, scale = 2 x crown radius / `canopy_m`, kept within 0.8-3.
* The canopy is planted so the forest looks closed from above, as in the photo: `props.trees.ortho` in `rules.yaml` gives
  `canopy_per_1000m2` (25: about one tree per 40 m2), `canopy_min_spacing_m` (4) and `canopy_crown_scale` (1.7-2.5, a
  crown edge of 4.4-6.5 m). The village profile's own forest density (4 per 1000 m2, crowns 2-3 m) is far too thin for this:
  it gives a forest that is mostly bare ground when seen at true scale. Seeded per 25 m cell, never on a building, road,
  placed object or water.

Numbers on the 7.15 km2 village: 218 ha of canopy, 54,000 forest trees and 4,200 standalone (58,000 in all, against 17,800
for the rules); `out/village/map` 236 MB, the largest chunk glb 4.4 MB (the village profile aims at 1.7 MB). Not yet seen
in the game, so the frame-rate cost is unknown.
