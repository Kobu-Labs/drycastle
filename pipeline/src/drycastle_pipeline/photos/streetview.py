"""Google Street View reference photos of the buildings -> ``data/village/streetview/`` (reference only).

Not a map stage. The photos are for looking at while modelling buildings by hand; Google's terms
forbid bulk storage and derived datasets, so they stay local (``data/`` is gitignored), never go
in a release, and nothing (textures, traced models) may be generated from them automatically.
Needs ``GOOGLE_MAPS_KEY`` (Street View Static API enabled, see docs/streetview.md).

    python -m drycastle_pipeline.photos.streetview                                   # coverage report (free)
    python -m drycastle_pipeline.photos.streetview --building w378920327 --download
    python -m drycastle_pipeline.photos.streetview --download --max-images 200

Metadata requests are free; every downloaded image is billed (~$0.007), so ``--download`` is
required and ``--max-images`` (default 100) caps one run. Already downloaded images are skipped.
"""

import argparse
import json
import math
import os
from collections.abc import Sequence

from drycastle_pipeline.core.config import load_dotenv, map_paths
from drycastle_pipeline.core.http import HttpError, PoliteClient

KEY_ENV = "GOOGLE_MAPS_KEY"
META_URL = "https://maps.googleapis.com/maps/api/streetview/metadata"
IMAGE_URL = "https://maps.googleapis.com/maps/api/streetview"
RING_M = 30.0  # probe points around a building, to find panoramas on different sides
RING_BEARINGS = 8
PROBE_RADIUS_M = 20  # snap radius of one metadata probe
MAX_DISTANCE_M = 60.0  # ignore panoramas further than this from the building centre
MIN_BEARING_GAP = 40.0  # chosen views of one building must differ at least this much in heading
M_PER_DEG = 111_320.0
TILE_PX = 640  # the most one Static API request gives
TILE_MARGIN = 1.10  # a tile is this much wider than its cell, so the Google marks at its edge fall outside


def building_points(osm: dict) -> dict[str, tuple[float, float, float]]:
    """OSM building ways -> {id like 'w123': (lon, lat, radius_m)} from the outline centroid."""
    out = {}
    for el in osm["elements"]:
        if el["type"] != "way" or "building" not in el.get("tags", {}) or not el.get("geometry"):
            continue
        lons = [p["lon"] for p in el["geometry"]]
        lats = [p["lat"] for p in el["geometry"]]
        lon, lat = (min(lons) + max(lons)) / 2, (min(lats) + max(lats)) / 2
        r = max(max(lons) - min(lons), max(lats) - min(lats)) * M_PER_DEG / 2
        out[f"w{el['id']}"] = (lon, lat, r)
    return out


def offset(lon: float, lat: float, bearing_deg: float, dist_m: float) -> tuple[float, float]:
    b = math.radians(bearing_deg)
    dlat = dist_m * math.cos(b) / M_PER_DEG
    dlon = dist_m * math.sin(b) / (M_PER_DEG * math.cos(math.radians(lat)))
    return lon + dlon, lat + dlat


def metres_between(lon1: float, lat1: float, lon2: float, lat2: float) -> tuple[float, float]:
    """(distance, bearing from 1 to 2 in degrees clockwise from north)."""
    dx = (lon2 - lon1) * M_PER_DEG * math.cos(math.radians(lat1))
    dy = (lat2 - lat1) * M_PER_DEG
    return math.hypot(dx, dy), math.degrees(math.atan2(dx, dy)) % 360


def angle_gap(a: float, b: float) -> float:
    d = abs(a - b) % 360
    return min(d, 360 - d)


class StreetView:
    def __init__(self, key: str, client: PoliteClient | None = None) -> None:
        self.key = key
        self.client = client or PoliteClient(min_interval=0.05, backoff=2.0)

    def _get(self, url: str, params: dict):
        try:
            resp, _ = self.client.request("GET", url, params={**params, "key": self.key})
        except HttpError as exc:  # the url in the message contains the key
            raise HttpError(str(exc).replace(self.key, "<key>")) from None
        return resp

    def panorama_near(self, lon: float, lat: float) -> dict | None:
        data = self._get(
            META_URL,
            {"location": f"{lat:.7f},{lon:.7f}", "radius": PROBE_RADIUS_M, "source": "outdoor"},
        ).json()
        if data.get("status") == "OK":
            return data
        if data.get("status") in ("ZERO_RESULTS", "NOT_FOUND"):
            return None
        raise HttpError(f"Street View metadata: {data.get('status')} {data.get('error_message', '')}")

    def image(self, pano_id: str, heading: float, fov: float, pitch: float) -> bytes:
        return self._get(
            IMAGE_URL,
            {"pano": pano_id, "size": "640x640", "heading": f"{heading:.2f}", "fov": f"{fov:.2f}",
             "pitch": f"{pitch:.2f}", "return_error_code": "true"},
        ).content


def views_for(sv: StreetView, lon: float, lat: float, radius_m: float, count: int) -> list[dict]:
    """Up to ``count`` panoramas around the building looking at it from different sides."""
    found: dict[str, dict] = {}
    for i in range(RING_BEARINGS):
        plon, plat = offset(lon, lat, i * 360 / RING_BEARINGS, RING_M + radius_m)
        meta = sv.panorama_near(plon, plat)
        if meta:
            found[meta["pano_id"]] = meta
    cands = []
    for meta in found.values():
        dist, heading = metres_between(meta["location"]["lng"], meta["location"]["lat"], lon, lat)
        if dist <= MAX_DISTANCE_M + radius_m and dist > 2:
            cands.append({"pano_id": meta["pano_id"], "date": meta.get("date"),
                          "copyright": meta.get("copyright"), "distance_m": round(dist, 1),
                          "heading": round(heading, 1), "location": meta["location"]})
    cands.sort(key=lambda c: c["distance_m"])
    chosen: list[dict] = []
    for c in cands:
        if all(angle_gap(c["heading"], o["heading"]) >= MIN_BEARING_GAP for o in chosen):
            chosen.append(c)
        if len(chosen) == count:
            break
    return chosen


def camera_axes(heading: float, pitch: float):
    """(forward, right, up) of a level camera; world is x east, y north, z up."""
    import numpy as np

    h, t = math.radians(heading), math.radians(pitch)
    fwd = np.array([math.sin(h) * math.cos(t), math.cos(h) * math.cos(t), math.sin(t)])
    right = np.array([math.cos(h), -math.sin(h), 0.0])
    return fwd, right, np.cross(right, fwd)


def tile_plan(heading: float, fov: float, pitch: float, n: int) -> list[dict]:
    """The n x n narrow views that together cover one view: [{row, col, heading, pitch, fov}].

    Each aims at the middle of its cell of the wide picture and is just wide enough to hold the cell.
    """

    fwd, right, up = camera_axes(heading, pitch)
    half = math.tan(math.radians(fov) / 2)
    plan = []
    for row in range(n):
        for col in range(n):
            x0, x1 = (2 * col / n - 1) * half, (2 * (col + 1) / n - 1) * half
            y0, y1 = (1 - 2 * row / n) * half, (1 - 2 * (row + 1) / n) * half
            mid = fwd + right * (x0 + x1) / 2 + up * (y0 + y1) / 2
            th = math.degrees(math.atan2(mid[0], mid[1])) % 360
            tp = math.degrees(math.atan2(mid[2], math.hypot(mid[0], mid[1])))
            tf, tr, tu = camera_axes(th, tp)
            reach = 0.0
            for x, y in ((x0, y0), (x0, y1), (x1, y0), (x1, y1)):
                ray = fwd + right * x + up * y
                reach = max(reach, abs(ray @ tr) / (ray @ tf), abs(ray @ tu) / (ray @ tf))
            tfov = math.degrees(2 * math.atan(reach)) * TILE_MARGIN
            plan.append({"row": row, "col": col, "heading": th, "pitch": tp, "fov": max(10.0, tfov)})
    return plan


def stitch(tiles: list[tuple[dict, bytes]], heading: float, fov: float, pitch: float, n: int):
    """The tiles of ``tile_plan`` resampled into one flat picture of n * 640 px (a PIL image)."""
    import io

    import numpy as np
    from PIL import Image

    size = n * TILE_PX
    fwd, right, up = camera_axes(heading, pitch)
    half = math.tan(math.radians(fov) / 2)
    out = np.zeros((size, size, 3), dtype=np.uint8)
    cell = size // n
    for plan, data in tiles:
        img = np.asarray(Image.open(io.BytesIO(data)).convert("RGB"), dtype=np.float32)
        ph, pw = img.shape[:2]
        r0, c0 = plan["row"] * cell, plan["col"] * cell
        v, u = np.mgrid[r0:r0 + cell, c0:c0 + cell]
        x = ((u + 0.5) / size * 2 - 1) * half
        y = (1 - (v + 0.5) / size * 2) * half
        rays = fwd + x[..., None] * right + y[..., None] * up
        tf, tr, tu = camera_axes(plan["heading"], plan["pitch"])
        thalf = math.tan(math.radians(plan["fov"]) / 2)
        depth = rays @ tf
        px = ((rays @ tr) / depth / thalf + 1) / 2 * pw - 0.5
        py = (1 - (rays @ tu) / depth / thalf) / 2 * ph - 0.5
        px, py = np.clip(px, 0, pw - 1.001), np.clip(py, 0, ph - 1.001)
        ix, iy = px.astype(int), py.astype(int)
        fx, fy = (px - ix)[..., None], (py - iy)[..., None]
        top = img[iy, ix] * (1 - fx) + img[iy, ix + 1] * fx
        bottom = img[iy + 1, ix] * (1 - fx) + img[iy + 1, ix + 1] * fx
        out[r0:r0 + cell, c0:c0 + cell] = (top * (1 - fy) + bottom * fy + 0.5).astype(np.uint8)
    return Image.fromarray(out)


def tiled_image(sv: StreetView, pano_id: str, heading: float, fov: float, pitch: float, n: int):
    """One view at n times the resolution: n * n billed requests, stitched."""
    plan = tile_plan(heading, fov, pitch, n)
    tiles = [(t, sv.image(pano_id, t["heading"], t["fov"], t["pitch"])) for t in plan]
    return stitch(tiles, heading, fov, pitch, n)


def fov_for(radius_m: float, dist_m: float) -> int:
    """Field of view that frames the building width (clamped to the API range 10-120)."""
    return int(max(20, min(100, math.degrees(2 * math.atan((radius_m * 1.3) / max(dist_m, 1))))))


def main(argv: Sequence[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="drycastle_pipeline.photos.streetview", description=__doc__.split("\n")[0])
    p.add_argument("--building", action="append", default=[], metavar="ID",
                   help="OSM way id as in buildings.json, e.g. w378920327 (repeatable; none = all)")
    p.add_argument("--views", type=int, default=3, help="views per building (default 3)")
    p.add_argument("--download", action="store_true", help="download images (billed); default only reports coverage")
    p.add_argument("--max-images", type=int, default=100, help="cap on images downloaded in one run")
    p.add_argument("--pitch", type=int, default=10, help="camera pitch in degrees (default 10)")
    p.add_argument("--tiles", type=int, default=1, metavar="N",
                   help="N x N requests per view stitched to N * 640 px (billed N * N times; default 1)")
    args = p.parse_args(argv)
    load_dotenv()

    key = os.environ.get(KEY_ENV)
    if not key:
        print(f"{KEY_ENV} is not set; see docs/streetview.md")
        return 2

    paths = map_paths()
    buildings = building_points(json.loads((paths.raw / "osm.json").read_text()))
    if args.building:
        missing = [b for b in args.building if b not in buildings]
        if missing:
            print("unknown building(s): " + ", ".join(missing))
            return 2
        buildings = {b: buildings[b] for b in args.building}

    folder = paths.raw.parent / "streetview"
    index_path = folder / "index.json"
    index = json.loads(index_path.read_text()) if index_path.is_file() else {}
    sv = StreetView(key)
    downloaded = covered = 0
    try:
        for bid, (lon, lat, r) in buildings.items():
            views = views_for(sv, lon, lat, r, args.views)
            covered += bool(views)
            if args.download:
                for n, v in enumerate(views):
                    target = folder / bid / f"{n}.jpg"
                    if target.exists():
                        continue
                    if downloaded >= args.max_images:
                        break
                    target.parent.mkdir(parents=True, exist_ok=True)
                    fov = fov_for(r, v["distance_m"])
                    if args.tiles > 1:
                        tiled_image(sv, v["pano_id"], v["heading"], fov, args.pitch, args.tiles).save(target, quality=92)
                    else:
                        target.write_bytes(sv.image(v["pano_id"], v["heading"], fov, args.pitch))
                    v.update(file=f"{bid}/{n}.jpg", fov=fov, pitch=args.pitch, px=TILE_PX * args.tiles)
                    downloaded += args.tiles**2
            index[bid] = views
            if downloaded >= args.max_images:
                print(f"reached --max-images {args.max_images}; rerun to continue")
                break
    except HttpError as exc:
        print(f"Street View request failed: {exc}")
        return 1
    finally:
        folder.mkdir(parents=True, exist_ok=True)
        index_path.write_text(json.dumps(index, indent=1))
    print(f"{covered}/{len(buildings)} buildings have a panorama nearby; index -> {index_path}")
    if args.download:
        print(f"{downloaded} images downloaded")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
