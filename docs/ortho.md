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
