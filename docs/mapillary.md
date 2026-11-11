# Mapillary reference imagery

Street-level photos of the village, used as a visual reference when recreating buildings by hand.
Not an input of the generated map (see `pipeline/src/suchohrad_pipeline/mapillary.py`).

## Setup (once)

1. Sign in at <https://www.mapillary.com/dashboard/developers> and register an application
   (any name; read-only access to public data is enough).
2. Copy its **Client Token** (starts with `MLY|`).
3. Put `MAPILLARY_TOKEN=MLY|...` in the repo-root `.env` (gitignored; never commit it).

## Use

```
cd pipeline
uv run python -m suchohrad_pipeline.mapillary --area village             # image index only
uv run python -m suchohrad_pipeline.mapillary --area village --download  # + 2048 px photos
uv run python -m suchohrad_pipeline.mapillary --near 16.8601,48.4044 --radius 40 --download
```

Output goes to `data/<area>/mapillary/` (`images.json`, `photos/<id>.jpg`; gitignored). The index
holds position, compass angle and creator per image, so a photo can be matched to the nearest
building. Images are CC BY-SA 4.0: keep the creator when a photo informs a model.
