"""Google Street View reference photos of the buildings -> ``data/<area>/streetview/`` (reference only).

Not a map stage. The photos are for looking at while modelling buildings by hand; Google's terms
forbid bulk storage and derived datasets, so they stay local (``data/`` is gitignored), never go
in a release, and nothing (textures, traced models) may be generated from them automatically.
Needs ``GOOGLE_MAPS_KEY`` (Street View Static API enabled, see docs/streetview.md).

    python -m drycastle_pipeline.streetview --area village                    # coverage report (free)
    python -m drycastle_pipeline.streetview --area village --building w378920327 --download
    python -m drycastle_pipeline.streetview --area village --download --max-images 200

Metadata requests are free; every downloaded image is billed (~$0.007), so ``--download`` is
required and ``--max-images`` (default 100) caps one run. Already downloaded images are skipped.
"""

import argparse
import json
import math
import os
from collections.abc import Sequence

from drycastle_pipeline.config import AREAS, DEFAULT_AREA, area_paths, load_dotenv
from drycastle_pipeline.http import HttpError, PoliteClient

KEY_ENV = "GOOGLE_MAPS_KEY"
META_URL = "https://maps.googleapis.com/maps/api/streetview/metadata"
IMAGE_URL = "https://maps.googleapis.com/maps/api/streetview"
RING_M = 30.0  # probe points around a building, to find panoramas on different sides
RING_BEARINGS = 8
PROBE_RADIUS_M = 20  # snap radius of one metadata probe
MAX_DISTANCE_M = 60.0  # ignore panoramas further than this from the building centre
MIN_BEARING_GAP = 40.0  # chosen views of one building must differ at least this much in heading
M_PER_DEG = 111_320.0


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

    def image(self, pano_id: str, heading: float, fov: int, pitch: int) -> bytes:
        return self._get(
            IMAGE_URL,
            {"pano": pano_id, "size": "640x640", "heading": f"{heading:.1f}", "fov": fov,
             "pitch": pitch, "return_error_code": "true"},
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


def fov_for(radius_m: float, dist_m: float) -> int:
    """Field of view that frames the building width (clamped to the API range 10-120)."""
    return int(max(20, min(100, math.degrees(2 * math.atan((radius_m * 1.3) / max(dist_m, 1))))))


def main(argv: Sequence[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="drycastle_pipeline.streetview", description=__doc__.split("\n")[0])
    p.add_argument("--area", choices=AREAS, default=DEFAULT_AREA)
    p.add_argument("--building", action="append", default=[], metavar="ID",
                   help="OSM way id as in buildings.json, e.g. w378920327 (repeatable; none = all)")
    p.add_argument("--views", type=int, default=3, help="views per building (default 3)")
    p.add_argument("--download", action="store_true", help="download images (billed); default only reports coverage")
    p.add_argument("--max-images", type=int, default=100, help="cap on images downloaded in one run")
    p.add_argument("--pitch", type=int, default=10, help="camera pitch in degrees (default 10)")
    args = p.parse_args(argv)
    load_dotenv()

    key = os.environ.get(KEY_ENV)
    if not key:
        print(f"{KEY_ENV} is not set; see docs/streetview.md")
        return 2

    paths = area_paths(args.area)
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
                    target.write_bytes(sv.image(v["pano_id"], v["heading"], fov, args.pitch))
                    v.update(file=f"{bid}/{n}.jpg", fov=fov, pitch=args.pitch)
                    downloaded += 1
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
