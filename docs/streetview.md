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
