# Google Street View reference imagery

Photos of the village buildings to look at while modelling them by hand. Not an input of the
generated map (`pipeline/src/drycastle_pipeline/streetview.py`). Mapillary has poor coverage here
(see `docs/mapillary.md`).

## Terms

The Google Maps Platform terms forbid bulk downloading/caching and derived datasets. So: photos stay
in `data/` (gitignored), never ship in a release, and nothing (textures, traced geometry) is
generated from them automatically. They are for human eyes only.

## Setup (once)

1. In Google Cloud Console create a project and enable billing (there is a free monthly allowance).
2. Enable **Street View Static API**.
3. Create an API key, restrict it to that API (and ideally set a low daily quota / budget alert).
4. Put `GOOGLE_MAPS_KEY=...` in the repo-root `.env` (gitignored; never commit it).

## Use

```
cd pipeline
uv run python -m drycastle_pipeline.streetview --area village                 # coverage report, free
uv run python -m drycastle_pipeline.streetview --area village --building w378920327 --download
uv run python -m drycastle_pipeline.streetview --area village --download --max-images 200
```

For each building it probes 8 points around it (free metadata calls), keeps panoramas that look at
the building from different sides (up to `--views`, default 3), and with `--download` saves 640x640
frames to `data/<area>/streetview/<building id>/<n>.jpg`. `index.json` records pano id, capture
date, distance, heading and copyright. Each downloaded image costs about $0.007; `--max-images`
(default 100) caps a run and existing files are skipped.

## Following a Street View tab (character and top-down point)

`pipeline/src/drycastle_pipeline/streetview_follow.py` watches the address of a Chrome tab and writes where you
stand and look to `out/streetview-position.json`. The viewer in editor mode (`--editor`) watches that file
(`godot/viewer/StreetViewFollow.cs`) and moves the character there, facing the way the camera looks. Press **T**
in the editor for the top-down view: a red point with a white rim marks the spot straight below the character
(it keeps about the same size on screen at any height, and is drawn over roofs and trees). The status bar says
where the character was put. **F2** walks from there. The editor's camera follows the character: the first position takes
it to the top-down view straight above the character, after that it stays centred on the character at the height it has
(scroll or fly up and down to change it), and in any other view it moves by the same step as the character. **T** takes
the camera back to where it was. No model and no MCP server is involved.

```
cd pipeline
uv run python -m drycastle_pipeline.streetview_follow --launch     # opens its own Chrome (signed out) on the village
```

Open Street View in that window and walk. With `--points` (off by default) **stopping to look at a spot for about two
seconds** drops a permanent point in the sketch `POLES` of `out/sketch.json`, where you are looking (8 m along the view;
`--ahead M` changes it, `--at camera` puts it where you stand, `--dwell S` the wait, `--min-gap M` stops a second point
close to the last one). Walking or turning drops nothing. The points show in the editor's Sketch tool (key 9) as
A, B, C ... and Claude reads them with `get_sketch`; place poles on them later (`place_object` at each point). Each point is one
undo step in the editor, and Ctrl+Z removes the last one. Without `--launch` it attaches to any Chrome started with
`--remote-debugging-port=9222` (`--port` changes it). It only reads the address of the tab, never the imagery.
Options: `--position-file PATH`, `--interval SECONDS`, `--start LAT,LON`, and `--sketch [PATH]`, which also keeps
the position as a point and a view line in the sketch `STREETVIEW` (what Claude reads for "pole here"; every
move is then an undo step in the editor, so it is off by default).

The viewer ignores what the file held when it started, so an old position never moves the character; start the
viewer, then move in Street View. The heading in the file is on the map grid, not the compass (`heading_grid`):
the projection is turned a little against true north.
