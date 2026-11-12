"""Mapillary street-level imagery for the area -> ``data/<area>/mapillary/`` (reference only).

Not a map stage: the photos are a visual reference for recreating buildings by hand, not an input
of the generated map. Needs a client access token (``MAPILLARY_TOKEN``, see docs/mapillary.md).

    python -m drycastle_pipeline.mapillary --area village            # index of images in the bbox
    python -m drycastle_pipeline.mapillary --area village --download # also fetch the photos
    python -m drycastle_pipeline.mapillary --near 16.8601,48.4044 --radius 40 --download

Images are CC BY-SA 4.0; keep the creator in the index when using them.
"""

import argparse
import json
import math
import os
from collections.abc import Sequence
from pathlib import Path

from drycastle_pipeline.config import AREAS, DEFAULT_AREA, area_paths, load_dotenv
from drycastle_pipeline.http import HttpError, PoliteClient

API = "https://graph.mapillary.com/images"
TOKEN_ENV = "MAPILLARY_TOKEN"
FIELDS = "id,captured_at,compass_angle,geometry,is_pano,creator,thumb_2048_url"
TILE_DEG = 0.004  # the API rejects bboxes that are too large; ~300 m tiles are well below it
PAGE = 2000


def tiles(west: float, south: float, east: float, north: float, step: float = TILE_DEG):
    lat = south
    while lat < north:
        lon = west
        while lon < east:
            yield (lon, lat, min(lon + step, east), min(lat + step, north))
            lon += step
        lat += step


def bbox_around(lon: float, lat: float, radius_m: float) -> tuple[float, float, float, float]:
    dlat = radius_m / 111_320
    dlon = radius_m / (111_320 * math.cos(math.radians(lat)))
    return (lon - dlon, lat - dlat, lon + dlon, lat + dlat)


def fetch_index(client: PoliteClient, token: str, bbox: tuple[float, float, float, float]) -> list[dict]:
    seen: dict[str, dict] = {}
    for cell in tiles(*bbox):
        resp, _ = client.request(
            "GET",
            API,
            params={"bbox": ",".join(f"{v:.6f}" for v in cell), "fields": FIELDS, "limit": PAGE},
            headers={"Authorization": f"OAuth {token}"},
        )
        for img in resp.json().get("data", []):
            seen[img["id"]] = img
    return sorted(seen.values(), key=lambda i: i["id"])


def download(client: PoliteClient, images: list[dict], folder: Path) -> int:
    folder.mkdir(parents=True, exist_ok=True)
    count = 0
    for img in images:
        url = img.get("thumb_2048_url")
        target = folder / f"{img['id']}.jpg"
        if not url or target.exists():
            continue
        resp, _ = client.request("GET", url)  # CDN url, no token
        target.write_bytes(resp.content)
        count += 1
    return count


def main(argv: Sequence[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="drycastle_pipeline.mapillary", description=__doc__.split("\n")[0])
    p.add_argument("--area", choices=AREAS, default=DEFAULT_AREA)
    p.add_argument("--near", metavar="LON,LAT", help="only this spot instead of the whole area")
    p.add_argument("--radius", type=float, default=50.0, help="--near radius in metres (default 50)")
    p.add_argument("--download", action="store_true", help="also download the 2048 px photos")
    args = p.parse_args(argv)
    load_dotenv()

    token = os.environ.get(TOKEN_ENV)
    if not token:
        print(f"{TOKEN_ENV} is not set; see docs/mapillary.md")
        return 2

    paths = area_paths(args.area)
    if args.near:
        lon, lat = (float(v) for v in args.near.split(","))
        bbox = bbox_around(lon, lat, args.radius)
    else:
        bbox = tuple(json.loads((paths.raw / "area.json").read_text())["bbox_wgs84"])

    folder = paths.raw.parent / "mapillary"
    client = PoliteClient(min_interval=0.2)
    try:
        images = fetch_index(client, token, bbox)
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "images.json").write_text(json.dumps(images, indent=1))
        print(f"{len(images)} images -> {folder / 'images.json'}")
        if args.download:
            print(f"{download(client, images, folder / 'photos')} photos downloaded")
    except HttpError as exc:
        print(f"Mapillary request failed: {exc}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
