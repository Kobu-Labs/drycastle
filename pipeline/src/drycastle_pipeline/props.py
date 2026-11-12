"""Props stage: fences, trees and spawn candidates -> ``data/work/props.json`` and ``spawns.json``.

Hand edits (``config/edits.json``, see ``edits.py``) are applied here too: fence modes and groups,
tree density or count overrides, and placed objects, which are written to ``objects.json``.

Everything random is drawn from ``random.Random`` seeded with sha256 of ``<parcel id>:<purpose>``
(never Python's ``hash()``), so identical input gives identical output. Only places inside the
terrain coverage are used.
"""

import hashlib
import json
import math
import os
import random
import sys
from dataclasses import dataclass, field, replace
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import LineString, MultiLineString, Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import linemerge, nearest_points, unary_union

from drycastle_pipeline.assets import load_manifest, validate_asset_ref
from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import (
    DEFAULT_EDITS,
    WIRE_ASSET_PREFIXES,
    Edits,
    EditsError,
    FenceSpec,
    load_edits,
    region_ring,
    resolve_fence,
)
from drycastle_pipeline.lamps import make_lamps, read_osm_lamps, write_lamps
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.roads import edge_lines
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import Terrain, load_terrain

PROPS_FORMAT_VERSION = 1
SPAWNS_FORMAT_VERSION = 1
OBJECTS_FORMAT_VERSION = 1
_MIN_PIECE_M = 0.5


def seeded(key: str) -> random.Random:
    return random.Random(int.from_bytes(hashlib.sha256(key.encode()).digest()[:8], "big"))


def _r3(v: float) -> float:
    return round(float(v), 3)


def chunk_of(x: float, y: float, chunk_m: float) -> list[int]:
    return [math.floor(x / chunk_m), math.floor(y / chunk_m)]


def _lines(geom) -> list[LineString]:
    if geom.is_empty:
        return []
    if isinstance(geom, LineString):
        return [geom]
    if isinstance(geom, MultiLineString) or hasattr(geom, "geoms"):
        return [ln for g in geom.geoms for ln in _lines(g)]
    return []


def merged_lines(geom) -> list[LineString]:
    """Line parts of a geometry with touching parts joined."""
    parts = [ln for ln in _lines(geom) if not ln.is_empty]
    return _lines(linemerge(parts)) if parts else []


def road_zone(graph: dict, extra: float):
    """Union of the road carriageways grown by ``extra`` metres."""
    parts = [ln.buffer(e["width"] / 2 + extra) for e, ln in edge_lines(graph)]
    return unary_union(parts) if parts else None


# --- fences ---------------------------------------------------------------------------------


def _owned_edge(poly: Polygon, others: BaseGeometry | None, outer: BaseGeometry) -> BaseGeometry:
    """The part of a group member's boundary that is on the group's outer edge."""
    own = poly.boundary
    if others is not None and not others.is_empty:
        own = own.difference(others)
    return own.intersection(outer)


def _gap_zone(base: BaseGeometry, x: float, y: float, width: float) -> BaseGeometry:
    """A gap of ``width`` metres centred on the boundary point nearest to (x, y)."""
    near, _ = nearest_points(base, Point(x, y))
    return near.buffer(width / 2)


def make_fences(
    parcels: list[dict],
    buildings: list[dict],
    graph: dict,
    terrain: Terrain,
    cfg: dict,
    specs: dict[str, FenceSpec] | None = None,
    groups: dict[str, list[str]] | None = None,
    road_polys: list[BaseGeometry] | None = None,
) -> list[dict]:
    """Fence sections along parcel boundaries: dicts with parcel, a, b (x, y) and za, zb (z).

    ``specs`` holds the effective fence of parcels that have edits (gaps in local metres); a
    parcel without a spec gets the rules' ``auto`` fence. ``groups`` maps a group ID to its member
    parcel IDs: the members' outer edge is fenced as one property, each piece with its owner's
    settings and the seeded roll keyed by the group ID. Custom sections carry their own
    ``type``, ``height_m``, ``thickness_m`` and ``colour`` (and ``column`` when the edit names one).

    ``road_polys`` replaces the road parcels taken from ``parcels`` for the gate-gap logic (the
    per-parcel run passes only the road parcels near its targets).
    """
    specs = specs or {}
    polys = {p["id"]: shape(p["polygon"]) for p in parcels}
    member_of: dict[str, str] = {}
    others: dict[str, BaseGeometry | None] = {}
    outer: dict[str, BaseGeometry] = {}
    for gid, pids in (groups or {}).items():
        union = unary_union([polys[q] for q in pids])
        outer[gid] = unary_union(
            [Polygon(g.exterior).boundary for g in getattr(union, "geoms", [union])]
        )
        for pid in pids:
            member_of[pid] = gid
            rest = [polys[q] for q in pids if q != pid]
            others[pid] = unary_union(rest) if rest else None
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    bunion = unary_union(bpolys) if bpolys else None
    near = float(cfg["garden_near_building_m"])
    avoid = [bunion.buffer(float(cfg["building_clearance_m"]))] if bunion else []
    zone = road_zone(graph, float(cfg["road_clearance_m"]))
    if zone is not None:
        avoid.append(zone)
    avoid_geom = unary_union(avoid) if avoid else None
    road_parcels = (
        road_polys
        if road_polys is not None
        else [polys[p["id"]] for p in parcels if p["kind"] == "road"]
    )
    road_union = unary_union(road_parcels).buffer(0.5) if road_parcels else None
    gate = float(cfg["gate_gap_m"])
    done = None  # union of boundaries already fenced (buffered a little)
    sections: list[dict] = []
    for p in sorted(parcels, key=lambda p: p["id"]):
        pid = p["id"]
        spec = specs.get(pid)
        if spec is not None and spec.mode == "none":
            continue
        custom = spec is not None and spec.mode == "custom"
        gid = member_of.get(pid)
        poly = polys[pid]
        if not custom:
            if p["kind"] not in cfg["kinds"]:
                continue
            if p["kind"] == "garden" and (bunion is None or poly.distance(bunion) > near):
                continue
            if seeded(f"{gid or pid}:fence").random() >= float(cfg["probability"]):
                continue
        base = poly.boundary if gid is None else _owned_edge(poly, others[pid], outer[gid])
        boundary = base
        if avoid_geom is not None:
            boundary = boundary.difference(avoid_geom)
        boundary = boundary.intersection(terrain.coverage)
        if not custom and gate > 0 and road_union is not None and not boundary.is_empty:
            front = merged_lines(boundary.intersection(road_union))
            front = [ln for ln in front if ln.length > gate * 1.5]
            if front:
                best = max(front, key=lambda ln: (ln.length, ln.coords[0]))
                mid = best.interpolate(best.length / 2)
                boundary = boundary.difference(mid.buffer(gate / 2))
        if spec is not None and not boundary.is_empty:
            for x, y, width in spec.gaps:
                boundary = boundary.difference(_gap_zone(base, x, y, width))
        if done is not None and not boundary.is_empty:
            boundary = boundary.difference(done)
        pieces = [ln for ln in merged_lines(boundary) if ln.length >= _MIN_PIECE_M]
        style = (
            {
                "type": spec.type,
                "height_m": spec.height_m,
                "thickness_m": spec.thickness_m,
                "colour": spec.colour,
                **({"column": spec.column} if spec.column is not None else {}),
            }
            if custom and spec is not None
            else {}
        )
        fenced = []
        for ln in pieces:
            fenced.append(ln)
            k = max(1, math.ceil(ln.length / float(cfg["section_m"])))
            pts = np.array([ln.interpolate(ln.length * i / k).coords[0] for i in range(k + 1)])
            z = terrain.surface(pts[:, 0], pts[:, 1])
            for i in range(k):
                sections.append(
                    {
                        "parcel": pid,
                        "a": [_r3(pts[i, 0]), _r3(pts[i, 1])],
                        "b": [_r3(pts[i + 1, 0]), _r3(pts[i + 1, 1])],
                        "za": _r3(z[i]),
                        "zb": _r3(z[i + 1]),
                        **style,
                    }
                )
        if fenced:
            full = unary_union(fenced).buffer(0.3)
            done = full if done is None else done.union(full)
    return sections


# --- trees ----------------------------------------------------------------------------------


def shore_candidates(water: BaseGeometry, cfg: dict) -> list[tuple[float, float]]:
    """Tree positions in rows along the water's edge: ``cfg["rows"]`` (offset from the shore, share kept), ``spacing_m`` apart.

    Seeded per candidate, so the same water gives the same points whichever parcels are generated.
    """
    pts: list[tuple[float, float]] = []
    spacing = float(cfg["spacing_m"])
    for r, row in enumerate(cfg["rows"]):
        grown = water.buffer(float(row["offset_m"]))
        parts = sorted(getattr(grown, "geoms", [grown]), key=lambda g: g.bounds)
        for k, part in enumerate(parts):
            for ring in (part.exterior, *part.interiors):
                n = int(ring.length // spacing)
                for i in range(n):
                    rng = seeded(f"shore:{r}:{k}:{i}")
                    if rng.random() >= float(row["keep"]):
                        continue
                    at = ring.interpolate((i + rng.uniform(-0.3, 0.3)) * ring.length / n)
                    x, y = at.x + rng.uniform(-0.8, 0.8), at.y + rng.uniform(-0.8, 0.8)
                    if not water.contains(Point(x, y)):
                        pts.append((x, y))
    return pts


CANOPY_CELL_PX = 42  # the canopy mask is planted cell by cell (about 25 m at the photo's 0.6 m per pixel)


def ortho_trees(tree, terrain: Terrain, zones: tuple, cfg: dict, origin: tuple[float, float] | None) -> list[dict]:
    """EXPERIMENT: every tree from the orthophoto detector (``make ortho-trees``), none from the rules.

    * each standalone tree of ``trees.json`` is planted where the detector found it, its size following the crown
      radius (``scale = 2 r / canopy_m``, kept within 0.8-3);
    * the canopy mask (``canopy.png``) is planted so that the forest looks closed from above, as in the photo:
      ``ortho.canopy_per_1000m2`` trees per 1000 m2 of mask at least ``ortho.canopy_min_spacing_m`` apart, each with a
      crown ``ortho.canopy_crown_scale`` times the normal one (the rules' own forest density is far too thin for
      that). Seeded per 25 m cell; never on a building, road, placed object or water.

    The photo does not resolve single crowns in a forest, so the canopy trunks are random within the detected extent.
    OSM trees, the rule-based densities, the shore rows and the hand-set trees of edits.json are all left out.
    """
    from PIL import Image

    if origin is None:
        raise ValueError("trees from the orthophoto need the frame origin (parcels.json origin_epsg5514)")
    ortho_dir = Path(cfg["ortho_dir"])
    doc = json.loads((ortho_dir / "trees.json").read_text())
    Image.MAX_IMAGE_PIXELS = None
    mask = np.asarray(Image.open(ortho_dir / doc["canopy"]["file"]).convert("L")) > 0
    minx, _, _, maxy = doc["canopy"]["bounds_epsg5514"]
    res = float(doc["canopy"]["pixel_m"])
    ox, oy = origin
    ocfg = cfg.get("ortho", {})
    spacing = float(ocfg.get("canopy_min_spacing_m", 4.0))
    canopy_m = float(cfg["canopy_m"])
    shapely.prepare(terrain.coverage)

    trees: list[dict] = []
    grid: dict[tuple[int, int], list[tuple[float, float]]] = {}

    def free(x: float, y: float) -> bool:
        gx, gy = int(x // spacing), int(y // spacing)
        return not any(
            math.hypot(x - a, y - b) < spacing
            for i in (gx - 1, gx, gx + 1)
            for j in (gy - 1, gy, gy + 1)
            for a, b in grid.get((i, j), ())
        )

    def add(x: float, y: float) -> None:
        grid.setdefault((int(x // spacing), int(y // spacing)), []).append((x, y))

    for t in doc["standalone"]:
        x, y = float(t["x"]), float(t["y"])
        if not shapely.contains_xy(terrain.coverage, x, y):
            continue
        scale = min(3.0, max(0.8, 2.0 * float(t["radius_m"]) / canopy_m))
        trees.append(tree(x, y, seeded(f"ortho:{t['x']}:{t['y']}"), None, False, "ortho", scale))
        add(x, y)

    density = float(ocfg.get("canopy_per_1000m2", 25.0))
    crown_lo, crown_hi = (float(v) for v in ocfg.get("canopy_crown_scale", (1.7, 2.5)))
    h, w = mask.shape
    for r0 in range(0, h, CANOPY_CELL_PX):
        for c0 in range(0, w, CANOPY_CELL_PX):
            cell = mask[r0 : r0 + CANOPY_CELL_PX, c0 : c0 + CANOPY_CELL_PX]
            n = int(cell.sum())
            if n == 0:
                continue
            rng = seeded(f"ortho:canopy:{r0}:{c0}")
            expected = density * n * res * res / 1000.0
            count = int(expected) + (rng.random() < expected % 1)
            x_lo, y_hi = minx + c0 * res - ox, maxy - r0 * res - oy  # the cell's north-west corner, local metres
            size = CANOPY_CELL_PX * res
            placed = 0
            for _ in range(count * 100):
                if placed >= count:
                    break
                x, y = x_lo + rng.random() * size, y_hi - rng.random() * size
                row, col = int((y_hi - y) / res), int((x - x_lo) / res)
                if row >= cell.shape[0] or col >= cell.shape[1] or not cell[row, col]:
                    continue
                if not shapely.contains_xy(terrain.coverage, x, y):
                    continue
                if any(g is not None and shapely.contains_xy(g, x, y) for g in zones):
                    continue
                if not free(x, y):
                    continue
                add(x, y)
                trees.append(tree(x, y, rng, None, True, "ortho-canopy", crown_lo + (crown_hi - crown_lo) * rng.random()))
                placed += 1
    return trees


def make_trees(
    parcels: list[dict],
    buildings: list[dict],
    graph: dict,
    osm_trees: list[dict],
    terrain: Terrain,
    cfg: dict,
    overrides: dict[str, dict] | None = None,
    objects: list[tuple[float, float]] | None = None,
    only: set[str] | None = None,
    include_osm: bool = True,
    water: BaseGeometry | None = None,
    origin: tuple[float, float] | None = None,
) -> list[dict]:
    """OSM trees plus seeded rule-based trees in parcels (gardens, forests, ...).

    EXPERIMENT: with ``cfg["source"] == "ortho"`` (``DRYCASTLE_TREE_SOURCE=ortho``, see :func:`ortho_trees`) none of that
    happens: every tree comes from the orthophoto detector instead (``origin`` is then the frame origin, EPSG:5514).

    ``only`` limits the rule-based trees to those parcel IDs (each parcel's trees depend only on
    its own seed and the context geometry); ``include_osm=False`` leaves the OSM trees out.

    ``overrides`` maps a parcel ID to its edited trees (``density_per_1000m2`` or ``count``, and
    ``seed_offset``). ``objects`` are placed object positions (local metres) that trees keep
    ``object_clearance_m`` away from. ``water`` (local metres) is where the water regions lie: rule-based trees get
    no trunk in it (their crowns may overhang it from the bank), and ``cfg["shore"]`` adds rows of trees along
    its edge (see :func:`shore_candidates`).
    """
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    bzone = unary_union(bpolys).buffer(float(cfg["building_clearance_m"])) if bpolys else None
    rzone = road_zone(graph, float(cfg["road_clearance_m"]))
    ozone = (
        unary_union([Point(x, y).buffer(float(cfg["object_clearance_m"])) for x, y in objects])
        if objects
        else None
    )
    shapely.prepare(terrain.coverage)
    for g in (bzone, rzone, ozone, water):
        if g is not None:
            shapely.prepare(g)
    trees: list[dict] = []

    def tree(x, y, rng, parcel, forest, src, scale=None):
        shape_ = "cone" if forest and rng.random() < 0.6 else "box"
        palette = cfg["forest_canopy_colours"] if forest else cfg["canopy_colours"]
        return {
            "src": src,
            "parcel": parcel,
            "x": _r3(x),
            "y": _r3(y),
            "z": _r3(terrain.surface(np.array([x]), np.array([y]))[0]),
            "scale": round(0.8 + 0.45 * rng.random(), 3) if scale is None else round(scale, 3),
            "rot": round(rng.random() * math.pi / 2, 3),
            "shape": shape_,
            "colour": palette[rng.randrange(len(palette))],
        }

    if cfg.get("source") == "ortho":
        return ortho_trees(tree, terrain, (bzone, rzone, ozone, water), cfg, origin)

    for f in sorted(osm_trees if include_osm else [], key=lambda f: f["properties"]["osm_id"]):
        x, y = f["geometry"]["coordinates"][:2]
        if terrain.coverage.contains(Point(x, y)):
            trees.append(
                tree(x, y, seeded(f"{f['properties']['osm_id']}:tree"), None, False, "osm")
            )

    density = cfg["density_per_1000m2"]
    spacing = float(cfg["min_spacing_m"])
    shore_pts = (
        shore_candidates(water, cfg["shore"]) if water is not None and cfg.get("shore", {}).get("rows") else None
    )
    shore_arr = np.array(shore_pts, float).reshape(-1, 2) if shore_pts else None
    if shore_arr is None:
        shore_pts = None
    for p in sorted(parcels, key=lambda p: p["id"]):
        if only is not None and p["id"] not in only:
            continue
        ov = (overrides or {}).get(p["id"])
        seed_key = f"{p['id']}:trees"
        fixed: int | None = None
        if ov is None:
            rate = float(density.get(p["kind"], 0.0))
        else:
            rate = float(ov.get("density_per_1000m2", 0.0))
            fixed = ov.get("count")
            if int(ov.get("seed_offset", 0)):
                seed_key += f":{int(ov['seed_offset'])}"
        shore_here = shore_pts is not None
        if fixed is None and rate <= 0 and not shore_here:
            continue
        poly = shape(p["polygon"])
        inner = poly.buffer(-float(cfg["parcel_edge_clearance_m"]))
        if inner.is_empty:
            continue
        shapely.prepare(inner)
        rng = seeded(seed_key)
        if fixed is None and rate <= 0:
            count = 0
        elif fixed is not None:
            count = int(fixed)
        else:
            expected = rate * poly.area / 1000.0
            count = int(expected) + (rng.random() < expected % 1)
        minx, miny, maxx, maxy = inner.bounds
        placed: list[tuple[float, float]] = []
        for _ in range(count * 40 if count else 0):
            if len(placed) >= count:
                break
            x, y = rng.uniform(minx, maxx), rng.uniform(miny, maxy)
            if not shapely.contains_xy(inner, x, y) or not shapely.contains_xy(
                terrain.coverage, x, y
            ):
                continue
            if bzone is not None and shapely.contains_xy(bzone, x, y):
                continue
            if rzone is not None and shapely.contains_xy(rzone, x, y):
                continue
            if ozone is not None and shapely.contains_xy(ozone, x, y):
                continue
            if water is not None and shapely.contains_xy(water, x, y):
                continue
            if any(math.hypot(x - a, y - b) < spacing for a, b in placed):
                continue
            placed.append((x, y))
        if shore_here:
            srng = seeded(f"{p['id']}:shore")
            near = (shore_arr[:, 0] >= minx) & (shore_arr[:, 0] <= maxx) & (shore_arr[:, 1] >= miny) & (shore_arr[:, 1] <= maxy)
            for x, y in shore_arr[near]:
                x, y = float(x), float(y)
                if not shapely.contains_xy(inner, x, y) or not shapely.contains_xy(terrain.coverage, x, y):
                    continue
                if any(g is not None and shapely.contains_xy(g, x, y) for g in (bzone, rzone, ozone, water)):
                    continue
                if any(math.hypot(x - a, y - b) < spacing for a, b in placed):
                    continue
                placed.append((x, y))
        for x, y in placed:
            trees.append(tree(x, y, rng, p["id"], p["kind"] == "forest", "rule"))
    return trees


# --- spawns ---------------------------------------------------------------------------------


def make_spawns(
    parcels: list[dict],
    buildings: list[dict],
    graph: dict,
    trees: list[dict],
    terrain: Terrain,
    cfg: dict,
    road_cfg: dict,
    chunk_m: float,
) -> list[dict]:
    """A seeded subset of candidate points on roads and open ground, away from buildings."""
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    mind = float(cfg["min_building_distance_m"])
    bzone = unary_union(bpolys).buffer(mind) if bpolys else None
    tzone = (
        unary_union(
            [Point(t["x"], t["y"]).buffer(float(cfg["min_tree_distance_m"])) for t in trees]
        )
        if trees
        else None
    )
    rzone = road_zone(graph, 0.5)
    for g in (bzone, tzone, rzone, terrain.coverage):
        if g is not None:
            shapely.prepare(g)
    parcel_polys = [(p, shape(p["polygon"])) for p in sorted(parcels, key=lambda p: p["id"])]
    tree_idx = shapely.STRtree([g for _, g in parcel_polys])

    def ok(x: float, y: float, on_road: bool) -> bool:
        if not shapely.contains_xy(terrain.coverage, x, y):
            return False
        if bzone is not None and shapely.contains_xy(bzone, x, y):
            return False
        if tzone is not None and shapely.contains_xy(tzone, x, y):
            return False
        return on_road or rzone is None or not shapely.contains_xy(rzone, x, y)

    def parcel_at(x: float, y: float) -> dict | None:
        hits = sorted(int(i) for i in tree_idx.query(Point(x, y), predicate="within"))
        return parcel_polys[hits[0]][0] if hits else None

    road_pool: list[dict] = []
    spacing = float(cfg["road_spacing_m"])
    for e, ln in sorted(edge_lines(graph), key=lambda t: t[0]["id"]):
        k = max(1, int(ln.length // spacing))
        for i in range(k):
            pt = ln.interpolate((i + 0.5) * ln.length / k)
            if ok(pt.x, pt.y, True):
                par = parcel_at(pt.x, pt.y)
                road_pool.append(
                    {
                        "x": pt.x,
                        "y": pt.y,
                        "kind": "road",
                        "parcel": par and par["id"],
                        "highway": e["highway"],
                    }
                )
    ground_pool: list[dict] = []
    for p, poly in parcel_polys:
        if p["kind"] not in cfg["ground_kinds"]:
            continue
        inner = poly.buffer(-1.5)
        if inner.is_empty:
            continue
        shapely.prepare(inner)
        rng = seeded(f"{p['id']}:spawns")
        expected = float(cfg["ground_per_1000m2"]) * poly.area / 1000.0
        count = int(expected) + (rng.random() < expected % 1)
        minx, miny, maxx, maxy = inner.bounds
        got = 0
        for _ in range(count * 30):
            if got >= count:
                break
            x, y = rng.uniform(minx, maxx), rng.uniform(miny, maxy)
            if shapely.contains_xy(inner, x, y) and ok(x, y, False):
                ground_pool.append(
                    {"x": x, "y": y, "kind": p["kind"], "parcel": p["id"], "highway": None}
                )
                got += 1

    rng = seeded("spawns")
    total = int(cfg["count"])
    want_road = min(len(road_pool), round(total * float(cfg["road_fraction"])))
    want_ground = min(len(ground_pool), total - want_road)
    want_road = min(len(road_pool), total - want_ground)
    picked = rng.sample(road_pool, want_road) + rng.sample(ground_pool, want_ground)
    picked.sort(key=lambda s: (s["kind"] != "road", round(s["x"], 3), round(s["y"], 3)))
    lift = float(road_cfg["lift_m"])
    out = []
    for i, s in enumerate(picked, 1):
        z = float(terrain.surface(np.array([s["x"]]), np.array([s["y"]]))[0])
        out.append(
            {
                "id": f"S-{i:03d}",
                "x": _r3(s["x"]),
                "y": _r3(s["y"]),
                "z": _r3(z + (lift if s["kind"] == "road" else 0.0)),
                "kind": s["kind"],
                "parcel": s["parcel"],
                "highway": s["highway"],
                "chunk": chunk_of(s["x"], s["y"], chunk_m),
            }
        )
    return out


def cap_trees(trees: list[dict], chunk_m: float, limit: int) -> list[dict]:
    """Keep at most ``limit`` rule-based trees per chunk (OSM trees always stay); 0 = no cap.

    Which trees go is decided by a hash of position, so it is stable and spread over the chunk.
    """
    if limit <= 0:
        return trees
    by_chunk: dict[tuple[int, int], list[dict]] = {}
    for tr in trees:
        if tr["src"] == "rule":
            by_chunk.setdefault(tuple(chunk_of(tr["x"], tr["y"], chunk_m)), []).append(tr)
    drop: set[int] = set()
    for members in by_chunk.values():
        if len(members) > limit:
            ranked = sorted(
                members,
                key=lambda t: hashlib.sha256(f"{t['x']}:{t['y']}:cap".encode()).hexdigest(),
            )
            drop.update(id(t) for t in ranked[limit:])
    return [t for t in trees if id(t) not in drop]


@dataclass
class EditPlan:
    """Edits resolved against the parcels of this run (see :func:`plan_edits`)."""

    specs: dict[str, FenceSpec] = field(default_factory=dict)  # gaps in local metres
    groups: dict[str, list[str]] = field(default_factory=dict)  # valid member parcels only
    tree_overrides: dict[str, dict] = field(default_factory=dict)
    objects: list[dict] = field(default_factory=list)  # objects.json entries, sorted by id
    cables: list[dict] = field(default_factory=list)  # {"id", "from", "to"} between placed poles and connectors, by id
    object_owner: dict[str, str] = field(default_factory=dict)  # object id -> parcel listing it
    warnings: list[str] = field(default_factory=list)
    water_regions: list[tuple[Polygon, float]] = field(default_factory=list)  # (outline in local metres, water_y)


def _local_xy(x: float, y: float, origin: list[float] | None) -> tuple[float, float]:
    if origin is None:
        raise ValueError("parcels.json has no origin_epsg5514; run the parcels stage again")
    return x - origin[0], y - origin[1]


def plan_edits(
    edits: Edits, parcels: list[dict], origin: list[float] | None, chunk_m: float
) -> EditPlan:
    """Resolve the edits against this run's parcels.

    Entries for parcels that no longer exist (parcel entries, group members, objects) are skipped
    with a warning. An object outside every parcel, or outside the parcel it is listed under, is
    still placed, with a warning; its ``parcel`` is the parcel containing it, or null. A cable is
    kept when both its ends are placed utility poles or house connectors, else skipped with a warning.
    """
    plan = EditPlan()
    polys = {p["id"]: shape(p["polygon"]) for p in parcels}
    for gid in sorted(edits.groups):
        members = []
        for pid in edits.groups[gid].get("parcels", []):
            if pid in polys:
                members.append(pid)
            else:
                plan.warnings.append(f"groups.{gid}: parcel {pid} not found; skipped")
        if members:
            plan.groups[gid] = sorted(members)
        else:
            plan.warnings.append(f"groups.{gid}: no member parcels left; group skipped")
    member_group = {pid: gid for gid, pids in plan.groups.items() for pid in pids}
    for pid in sorted(edits.parcels):
        if pid not in polys:
            plan.warnings.append(f"parcels.{pid}: parcel not found; entry skipped")
    for pid in sorted(polys):
        entry = edits.parcels.get(pid, {})
        group_fence = edits.groups[member_group[pid]].get("fence") if pid in member_group else None
        if "fence" in entry or group_fence is not None:
            spec = resolve_fence(entry.get("fence"), group_fence)
            gaps = tuple((*_local_xy(x, y, origin), w) for x, y, w in spec.gaps)
            plan.specs[pid] = replace(spec, gaps=gaps)
        if "trees" in entry:
            plan.tree_overrides[pid] = entry["trees"]
    for pid in sorted(edits.parcels):
        if pid not in polys:
            continue
        for obj in edits.parcels[pid].get("objects", []):
            lx, ly = _local_xy(*obj["position"], origin)
            hits = sorted(q for q, g in polys.items() if g.covers(Point(lx, ly)))
            inside = hits[0] if hits else None
            if inside is None:
                plan.warnings.append(f"objects {obj['id']}: outside every parcel; placed anyway")
            elif inside != pid:
                plan.warnings.append(
                    f"objects {obj['id']}: outside its parcel {pid} (inside {inside}); placed anyway"
                )
            plan.object_owner[obj["id"]] = pid
            plan.objects.append(
                {
                    "id": obj["id"],
                    "asset": obj["asset"],
                    "position": [_r3(lx), _r3(ly)],
                    "z": obj.get("z"),
                    "rotation_deg": float(obj.get("rotation_deg", 0)),
                    "scale": float(obj.get("scale", 1)),
                    "colour": obj.get("colour"),
                    "parcel": inside,
                    "owner": pid,  # the parcel entry that lists it (may differ from "parcel")
                    "chunk": chunk_of(lx, ly, chunk_m),
                }
            )
    plan.objects.sort(key=lambda o: o["id"])
    assets = {o["id"]: o["asset"] for o in plan.objects}
    for cid in sorted(edits.cables):
        cable = edits.cables[cid]
        bad = [
            f"{end} is not placed" if end not in assets else f"{end} is not a utility pole or a house connector"
            for end in (cable["from"], cable["to"])
            if not assets.get(end, "").startswith(WIRE_ASSET_PREFIXES)
        ]
        if bad:
            plan.warnings.append(f"cables.{cid}: {'; '.join(bad)}; cable skipped")
        else:
            plan.cables.append({"id": cid, "from": cable["from"], "to": cable["to"]})
    plan.water_regions = [
        (Polygon([_local_xy(x, y, origin) for x, y in region_ring(r["polygon"])]), float(r["water_y"]))
        for _, r in sorted(edits.regions.items())
        if "water_y" in r
    ]
    return plan


def water_zone(plan: EditPlan, terrain: Terrain) -> BaseGeometry | None:
    """Where the water regions of the plan really lie (see :func:`water.wet_area`), or None without any."""
    from drycastle_pipeline.water import wet_area

    zones = [wet_area(outline, terrain, level) for outline, level in plan.water_regions]
    zones = [z for z in zones if not z.is_empty]
    return unary_union(zones) if zones else None


def unknown_asset_warnings(objects: list[dict]) -> list[str]:
    """Warnings for placed objects whose asset id is not in ``assets/manifest.json``.

    Output is unaffected: the objects are written anyway. Nothing is reported when there is no
    manifest to check against.
    """
    try:
        manifest = load_manifest()
    except (OSError, ValueError):
        return []
    out = []
    for obj in objects:
        problem = validate_asset_ref(obj["asset"], manifest)
        if problem:
            out.append(f"objects {obj['id']}: {problem}")
    return out


def run_props(
    work_dir: Path = DEFAULT_WORK_DIR,
    rules_path: Path = DEFAULT_RULES,
    area: str | None = None,
    edits_path: Path = DEFAULT_EDITS,
) -> dict:
    terrain, _ = load_terrain(work_dir)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    parcels = parcels_doc["parcels"]
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    graph = json.loads((work_dir / "roads.json").read_text())
    osm_trees = json.loads((work_dir / "normalised" / "trees.geojson").read_text())["features"]
    fcfg = load_section("props", rules_path, area)
    chunk_m = float(parcels_doc["chunk_size_m"])
    plan = plan_edits(load_edits(edits_path), parcels, parcels_doc.get("origin_epsg5514"), chunk_m)
    for warning in plan.warnings + unknown_asset_warnings(plan.objects):
        print(f"props: warning: {warning}", file=sys.stderr)
    fences = make_fences(
        parcels, buildings, graph, terrain, fcfg["fences"], specs=plan.specs, groups=plan.groups
    )
    tree_cfg = fcfg["trees"]
    if os.environ.get("DRYCASTLE_TREE_SOURCE") == "ortho":  # EXPERIMENT, off by default
        tree_cfg = {**tree_cfg, "source": "ortho", "ortho_dir": str(work_dir.parent / "ortho")}
        print("props: EXPERIMENT: every tree comes from the orthophoto detector (DRYCASTLE_TREE_SOURCE=ortho)",
              file=sys.stderr)
        if plan.tree_overrides:
            print(f"props: warning: {len(plan.tree_overrides)} hand-set tree overrides of edits.json are ignored",
                  file=sys.stderr)
    trees = make_trees(
        parcels,
        buildings,
        graph,
        osm_trees,
        terrain,
        tree_cfg,
        overrides=plan.tree_overrides,
        objects=[(o["position"][0], o["position"][1]) for o in plan.objects],
        water=water_zone(plan, terrain),
        origin=tuple(parcels_doc["origin_epsg5514"]) if parcels_doc.get("origin_epsg5514") else None,
    )
    trees = cap_trees(trees, chunk_m, int(fcfg["trees"]["max_per_chunk"]))
    spawns = make_spawns(
        parcels,
        buildings,
        graph,
        trees,
        terrain,
        load_section("spawns", rules_path, area),
        load_section("roads", rules_path),
        float(parcels_doc["chunk_size_m"]),
    )
    objects_doc = {"format_version": OBJECTS_FORMAT_VERSION, "objects": plan.objects}
    if plan.cables:  # the wires between placed utility poles; the game strings them (docs/utility-poles.md)
        objects_doc["cables"] = plan.cables
    (work_dir / "objects.json").write_text(json.dumps(objects_doc, indent=1, sort_keys=True) + "\n")
    props = {"version": PROPS_FORMAT_VERSION, "fences": fences, "trees": trees}
    spawn_doc = {
        "version": SPAWNS_FORMAT_VERSION,
        "axes": "local metres, x east, y north, z up (elevation)",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "spawns": spawns,
    }
    for name, doc in (("props.json", props), ("spawns.json", spawn_doc)):
        (work_dir / name).write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    lamps_cfg = load_section("lamps", rules_path, area)
    lamps = make_lamps(
        parcels,
        buildings,
        graph,
        read_osm_lamps(work_dir),
        terrain,
        lamps_cfg,
        float(parcels_doc["chunk_size_m"]),
    )
    write_lamps(work_dir, lamps, float(parcels_doc["chunk_size_m"]), float(lamps_cfg["spacing_m"]))
    return {
        "fences": len(fences),
        "trees": len(trees),
        "spawns": len(spawns),
        "objects": len(plan.objects),
        "lamps": len(lamps),
    }


def run(
    force: bool = False,
    area: str = DEFAULT_AREA,
    edits: str | None = None,
    **_options: object,
) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    try:
        counts = run_props(area_paths(area).work, area=area, edits_path=edits_path)
    except FileNotFoundError as exc:
        print(f"props: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"props: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    except ValueError as exc:
        print(f"props: error: {exc}", file=sys.stderr)
        return 1
    print("props: " + ", ".join(f"{k}: {v}" for k, v in counts.items()))
    return 0
