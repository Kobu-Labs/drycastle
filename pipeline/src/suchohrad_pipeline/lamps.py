"""Street lamps: OSM ``street_lamp`` nodes plus rule lamps along roads -> ``lamps.json``.

Part of the props stage (it needs the same parcels, buildings, road graph and terrain). A lamp is a
point with an elevation; the game draws the pole and the head from primitives and adds the light,
so no mesh goes into the chunk GLBs. Rule lamps are deterministic (no random numbers): on each
eligible road edge they sit every ``spacing_m`` metres, alternating sides, just beyond the carriageway.
"""

import json
import math
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import Point, shape
from shapely.ops import unary_union

from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.roads import edge_lines
from suchohrad_pipeline.terrain import Terrain

LAMPS_FORMAT_VERSION = 1
LAMPS_AXES = "local metres, x east, y north, z up (elevation); the lamp head sits on top of the pole"


def _r3(v: float) -> float:
    return round(float(v), 3)


def _chunk(x: float, y: float, chunk_m: float) -> list[int]:
    return [math.floor(x / chunk_m), math.floor(y / chunk_m)]


def _zone(geoms: list):
    """Union of ``geoms`` prepared for point tests, or None when there are none."""
    if not geoms:
        return None
    zone = unary_union(geoms)
    shapely.prepare(zone)
    return zone


def make_lamps(
    parcels: list[dict],
    buildings: list[dict],
    graph: dict,
    osm_lamps: list[dict],
    terrain: Terrain,
    cfg: dict,
    chunk_m: float,
) -> list[dict]:
    """Lamp positions, sorted by place and numbered ``L-0001``...

    ``osm_lamps`` are GeoJSON point features (local metres, ``osm_id`` property). ``cfg`` is the
    ``lamps`` section of rules.yaml.
    """
    clear = float(cfg["building_clearance_m"])
    bzone = _zone([footprint_polygon(b["footprint"]).buffer(clear) for b in buildings])
    avoid = set(cfg["avoid_kinds"])
    azone = _zone([shape(p["polygon"]) for p in parcels if p["kind"] in avoid])
    parcel_polys = sorted(((p, shape(p["polygon"])) for p in parcels), key=lambda t: t[0]["id"])
    parcel_idx = shapely.STRtree([g for _, g in parcel_polys]) if parcel_polys else None

    def ok(x: float, y: float, avoid_parcels: bool) -> bool:
        if not shapely.contains_xy(terrain.coverage, x, y):
            return False
        if bzone is not None and shapely.contains_xy(bzone, x, y):
            return False
        return not (avoid_parcels and azone is not None and shapely.contains_xy(azone, x, y))

    def parcel_at(x: float, y: float) -> str | None:
        if parcel_idx is None:
            return None
        hits = sorted(int(i) for i in parcel_idx.query(Point(x, y), predicate="within"))
        return parcel_polys[hits[0]][0]["id"] if hits else None

    lamps: list[dict] = []  # {"x", "y", "source", "osm_id", "highway"}
    for f in sorted(osm_lamps, key=lambda f: f["properties"]["osm_id"]):
        pt = shape(f["geometry"])
        if ok(pt.x, pt.y, False):
            lamps.append({"x": pt.x, "y": pt.y, "source": "osm",
                          "osm_id": f["properties"]["osm_id"], "highway": None})

    spacing = float(cfg["spacing_m"])
    min_d = float(cfg["min_lamp_distance_m"])
    offset_extra = float(cfg["road_offset_m"])
    rule_highways = set(cfg["rule_highways"])
    placed = np.array([[lp["x"], lp["y"]] for lp in lamps], float).reshape(-1, 2)
    for e, ln in sorted(edge_lines(graph), key=lambda t: t[0]["id"]):
        if e["highway"] not in rule_highways or ln.length <= 0:
            continue
        k = max(1, int(ln.length // spacing))
        step = ln.length / k
        for i in range(k):
            d = (i + 0.5) * step
            pt = ln.interpolate(d)
            a = ln.interpolate(max(0.0, d - 0.5))
            b = ln.interpolate(min(ln.length, d + 0.5))
            dx, dy = b.x - a.x, b.y - a.y
            norm = math.hypot(dx, dy)
            if norm == 0:
                continue
            side = 1.0 if i % 2 == 0 else -1.0
            off = float(e["width"]) / 2 + offset_extra
            x = pt.x + side * (-dy / norm) * off
            y = pt.y + side * (dx / norm) * off
            if not ok(x, y, True):
                continue
            if len(placed) and float(np.min(np.hypot(placed[:, 0] - x, placed[:, 1] - y))) < min_d:
                continue
            placed = np.vstack([placed, [[x, y]]])
            lamps.append({"x": x, "y": y, "source": "rule", "osm_id": None, "highway": e["highway"]})

    lamps.sort(key=lambda lp: (round(lp["x"], 3), round(lp["y"], 3), lp["source"]))
    out = []
    for n, lp in enumerate(lamps, 1):
        x, y = lp["x"], lp["y"]
        out.append(
            {
                "id": f"L-{n:04d}",
                "x": _r3(x),
                "y": _r3(y),
                "z": _r3(float(terrain.surface(np.array([x]), np.array([y]))[0])),
                "source": lp["source"],
                "osm_id": lp["osm_id"],
                "highway": lp["highway"],
                "parcel": parcel_at(x, y),
                "chunk": _chunk(x, y, chunk_m),
            }
        )
    return out


def write_lamps(work_dir: Path, lamps: list[dict], chunk_m: float, spacing_m: float) -> None:
    doc = {
        "version": LAMPS_FORMAT_VERSION,
        "axes": LAMPS_AXES,
        "chunk_size_m": chunk_m,
        "spacing_m": spacing_m,
        "lamps": lamps,
    }
    (work_dir / "lamps.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")


def read_osm_lamps(work_dir: Path) -> list[dict]:
    """OSM lamp features from ``normalised/street_lamps.geojson``; none when the file is missing.

    Raw data fetched before street lamps were queried has no such file: ``make fetch --force``
    then ``make normalise`` brings it in. Until then only rule lamps are placed.
    """
    path = work_dir / "normalised" / "street_lamps.geojson"
    return json.loads(path.read_text())["features"] if path.is_file() else []
