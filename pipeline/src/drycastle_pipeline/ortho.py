"""Orthophoto of the map -> ``data/village/ortho/`` (reference only, never part of the map).

Not a map stage: the aerial photo is something to look at while editing (the map editor can lay it
under the map), not an input of the generated map and never exported or shipped. The licence of the
source (GKU Bratislava, NLC) forbids redistributing it and altering it, so nothing here may reach
``out/``; the files stay in the gitignored ``data/``. See docs/ortho.md.

    python -m drycastle_pipeline.ortho            # level 11, about 0.6 m per pixel
    python -m drycastle_pipeline.ortho --level 12 # 0.3 m per pixel, 4x the tiles

Reads the box (and polygon) ``fetch`` recorded in ``area.json``, downloads the WMTS tiles that cover
it (EPSG:5514, so no reprojection) into ``tiles/`` and joins them into ``ortho.jpg`` cropped to the
map, with ``ortho.json`` giving its absolute S-JTSK bounds.
"""

import argparse
import json
import math
from collections.abc import Sequence
from pathlib import Path

from drycastle_pipeline.config import map_paths
from drycastle_pipeline.frame import box_to_sjtsk
from drycastle_pipeline.http import HttpError, PoliteClient

WMTS = (
    "https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wmts_sjtsk/service.svc/get/tile/1.0.0/"
    "WMS_zbgis_ortofoto_wmts_sjtsk/default/default028mm/{level}/{row}/{col}"
)
# TileMatrixSet default028mm (GetCapabilities): one tile grid, every level half the scale of the last.
ORIGIN_X, ORIGIN_Y = -3.36998e7, 3.36998e7  # top-left corner of tile (0, 0)
SCALE_0 = 4354125.143421019
PIXEL_SIZE_M = 0.00028  # OGC standard rendering pixel
TILE_PX = 256
MAX_LEVEL = 12
DEFAULT_LEVEL = 11
DEFAULT_PAD_M = 25.0
MAX_TILES = 4000  # a safety stop, level 12 over the whole village is about 2000
ATTRIBUTION = "GKU Bratislava, NLC"
NOTICE = "Reference only: do not redistribute or modify (licence of the orthophoto mosaic)."


def pixel_m(level: int) -> float:
    """Ground size of one pixel (metres) at a zoom level."""
    return SCALE_0 / 2**level * PIXEL_SIZE_M


def tile_url(level: int, row: int, col: int) -> str:
    return WMTS.format(level=level, row=row, col=col)


def pixel_box(extent: tuple[float, float, float, float], level: int) -> tuple[int, int, int, int]:
    """(px0, py0, px1, py1) in the level's global pixel grid (y down) covering the extent."""
    minx, miny, maxx, maxy = extent
    res = pixel_m(level)
    return (
        math.floor((minx - ORIGIN_X) / res),
        math.floor((ORIGIN_Y - maxy) / res),
        math.ceil((maxx - ORIGIN_X) / res),
        math.ceil((ORIGIN_Y - miny) / res),
    )


def pixel_bounds(box: tuple[int, int, int, int], level: int) -> tuple[float, float, float, float]:
    """Absolute S-JTSK (minx, miny, maxx, maxy) of a global pixel box."""
    px0, py0, px1, py1 = box
    res = pixel_m(level)
    return (
        ORIGIN_X + px0 * res, ORIGIN_Y - py1 * res,
        ORIGIN_X + px1 * res, ORIGIN_Y - py0 * res,
    )


def tiles_for(box: tuple[int, int, int, int]) -> list[tuple[int, int]]:
    """(row, col) of every tile touching a pixel box, row by row."""
    px0, py0, px1, py1 = box
    return [
        (row, col)
        for row in range(py0 // TILE_PX, (py1 - 1) // TILE_PX + 1)
        for col in range(px0 // TILE_PX, (px1 - 1) // TILE_PX + 1)
    ]


def map_extent(raw_dir: Path, pad_m: float = DEFAULT_PAD_M) -> tuple[float, float, float, float]:
    """Absolute S-JTSK bounds of what ``fetch`` covered (the polygon's, else the fetched box), padded."""
    doc = json.loads((raw_dir / "area.json").read_text())
    ring = doc.get("polygon_epsg5514")
    if ring:
        xs, ys = [p[0] for p in ring], [p[1] for p in ring]
        minx, miny, maxx, maxy = min(xs), min(ys), max(xs), max(ys)
    else:
        minx, miny, maxx, maxy = box_to_sjtsk(*doc["bbox_wgs84"]).bounds
    return minx - pad_m, miny - pad_m, maxx + pad_m, maxy + pad_m


def _tile_path(folder: Path, level: int, row: int, col: int) -> Path | None:
    for ext in ("jpg", "png"):
        path = folder / str(level) / f"{row}_{col}.{ext}"
        if path.is_file() and path.stat().st_size:
            return path
    return None


def download_tiles(
    client: PoliteClient, folder: Path, level: int, tiles: list[tuple[int, int]], *, force: bool = False
) -> tuple[int, int]:
    """Fetch the tiles not on disk yet. Returns (downloaded, missing); a 404 tile is left blank."""
    (folder / str(level)).mkdir(parents=True, exist_ok=True)
    downloaded = missing = 0
    for row, col in tiles:
        if not force and _tile_path(folder, level, row, col):
            continue
        try:
            resp, _ = client.request("GET", tile_url(level, row, col))
        except HttpError as exc:
            if "HTTP 404" not in str(exc):
                raise
            missing += 1
            continue
        ext = "png" if resp.content[:4] == b"\x89PNG" else "jpg"
        (folder / str(level) / f"{row}_{col}.{ext}").write_bytes(resp.content)
        downloaded += 1
    return downloaded, missing


def mosaic(folder: Path, level: int, box: tuple[int, int, int, int]):
    """The pixel box as one RGB image, built from the tiles on disk (blank where one is missing)."""
    from PIL import Image

    px0, py0, px1, py1 = box
    image = Image.new("RGB", (px1 - px0, py1 - py0), (0, 0, 0))
    for row, col in tiles_for(box):
        path = _tile_path(folder, level, row, col)
        if path is None:
            continue
        with Image.open(path) as tile:
            image.paste(tile.convert("RGB"), (col * TILE_PX - px0, row * TILE_PX - py0))
    return image


def build(
    raw_dir: Path, out_dir: Path, level: int, client: PoliteClient, *,
    force: bool = False, pad_m: float = DEFAULT_PAD_M,
) -> dict:
    """Download and join the tiles; returns the ``ortho.json`` document."""
    if not 0 <= level <= MAX_LEVEL:
        raise ValueError(f"level must be 0..{MAX_LEVEL}")
    box = pixel_box(map_extent(raw_dir, pad_m), level)
    tiles = tiles_for(box)
    if len(tiles) > MAX_TILES:
        raise ValueError(f"{len(tiles)} tiles is over the {MAX_TILES} limit; use a lower --level")
    out_dir.mkdir(parents=True, exist_ok=True)
    downloaded, missing = download_tiles(client, out_dir / "tiles", level, tiles, force=force)
    image = mosaic(out_dir / "tiles", level, box)
    image.save(out_dir / "ortho.jpg", quality=95)
    minx, miny, maxx, maxy = pixel_bounds(box, level)
    doc = {
        "file": "ortho.jpg",
        "level": level,
        "pixel_m": pixel_m(level),
        "size_px": list(image.size),
        "bounds_epsg5514": [minx, miny, maxx, maxy],
        "tiles": len(tiles),
        "tiles_downloaded": downloaded,
        "tiles_missing": missing,
        "source": "ZBGIS orthophoto mosaic, WMTS " + WMTS.split("/get/")[0],
        "attribution": ATTRIBUTION,
        "notice": NOTICE,
    }
    (out_dir / "ortho.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    return doc


def run_ortho(level: int = DEFAULT_LEVEL, force: bool = False) -> int:
    """Download the photo of the map; prints one line and returns the exit status."""
    paths = map_paths()
    try:
        doc = build(paths.raw, paths.raw.parent / "ortho", level, PoliteClient(min_interval=0.2), force=force)
    except FileNotFoundError:
        print("ortho: error: no area.json; run 'make fetch' first")
        return 1
    except (HttpError, ValueError) as exc:
        print(f"ortho: error: {exc}")
        return 1
    w, h = doc["size_px"]
    print(
        f"ortho: {doc['tiles']} tiles ({doc['tiles_downloaded']} downloaded, {doc['tiles_missing']} missing), "
        f"{w}x{h} px at {doc['pixel_m']:.2f} m -> {paths.raw.parent / 'ortho' / 'ortho.jpg'}"
    )
    return 0


def run(force: bool = False, **_options: object) -> int:
    """The ``ortho`` pipeline stage (after ``normalise``): tiles already on disk are not fetched again."""
    return run_ortho(force=force)


def main(argv: Sequence[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="drycastle_pipeline.ortho", description=__doc__.split("\n")[0])
    p.add_argument("--level", type=int, default=DEFAULT_LEVEL, help=f"zoom 0..{MAX_LEVEL} (default {DEFAULT_LEVEL})")
    p.add_argument("--force", action="store_true", help="download the tiles again")
    args = p.parse_args(argv)
    return run_ortho(args.level, args.force)


if __name__ == "__main__":
    raise SystemExit(main())
