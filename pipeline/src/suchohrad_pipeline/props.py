"""Props stage: fences, trees and spawn candidates -> ``data/work/props.json`` and ``spawns.json``.

Everything random is drawn from ``random.Random`` seeded with sha256 of ``<parcel id>:<purpose>``
(never Python's ``hash()``), so identical input gives identical output. Only places inside the
terrain coverage are used.
"""

import hashlib
import json
import math
import random
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import LineString, MultiLineString, Point, shape
from shapely.ops import linemerge, unary_union

from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.normalise import DEFAULT_WORK_DIR
from suchohrad_pipeline.roads import edge_lines
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section
from suchohrad_pipeline.terrain import Terrain, load_terrain

PROPS_FORMAT_VERSION = 1
SPAWNS_FORMAT_VERSION = 1
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


def make_fences(
    parcels: list[dict], buildings: list[dict], graph: dict, terrain: Terrain, cfg: dict
) -> list[dict]:
    """Fence sections along parcel boundaries: dicts with parcel, a, b (x, y) and za, zb (z)."""
    polys = {p["id"]: shape(p["polygon"]) for p in parcels}
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    bunion = unary_union(bpolys) if bpolys else None
    near = float(cfg["garden_near_building_m"])
    avoid = [bunion.buffer(float(cfg["building_clearance_m"]))] if bunion else []
    zone = road_zone(graph, float(cfg["road_clearance_m"]))
    if zone is not None:
        avoid.append(zone)
    avoid_geom = unary_union(avoid) if avoid else None
    road_parcels = [polys[p["id"]] for p in parcels if p["kind"] == "road"]
    road_union = unary_union(road_parcels).buffer(0.5) if road_parcels else None
    gate = float(cfg["gate_gap_m"])
    done = None  # union of boundaries already fenced (buffered a little)
    sections: list[dict] = []
    for p in sorted(parcels, key=lambda p: p["id"]):
        if p["kind"] not in cfg["kinds"]:
            continue
        poly = polys[p["id"]]
        if p["kind"] == "garden" and (bunion is None or poly.distance(bunion) > near):
            continue
        if seeded(f"{p['id']}:fence").random() >= float(cfg["probability"]):
            continue
        boundary = poly.boundary
        if avoid_geom is not None:
            boundary = boundary.difference(avoid_geom)
        boundary = boundary.intersection(terrain.coverage)
        if gate > 0 and road_union is not None and not boundary.is_empty:
            front = merged_lines(boundary.intersection(road_union))
            front = [ln for ln in front if ln.length > gate * 1.5]
            if front:
                best = max(front, key=lambda ln: (ln.length, ln.coords[0]))
                mid = best.interpolate(best.length / 2)
                boundary = boundary.difference(mid.buffer(gate / 2))
        if done is not None and not boundary.is_empty:
            boundary = boundary.difference(done)
        pieces = [ln for ln in merged_lines(boundary) if ln.length >= _MIN_PIECE_M]
        fenced = []
        for ln in pieces:
            fenced.append(ln)
            k = max(1, math.ceil(ln.length / float(cfg["section_m"])))
            pts = np.array([ln.interpolate(ln.length * i / k).coords[0] for i in range(k + 1)])
            z = terrain.surface(pts[:, 0], pts[:, 1])
            for i in range(k):
                sections.append(
                    {
                        "parcel": p["id"],
                        "a": [_r3(pts[i, 0]), _r3(pts[i, 1])],
                        "b": [_r3(pts[i + 1, 0]), _r3(pts[i + 1, 1])],
                        "za": _r3(z[i]),
                        "zb": _r3(z[i + 1]),
                    }
                )
        if fenced:
            full = unary_union(fenced).buffer(0.3)
            done = full if done is None else done.union(full)
    return sections


# --- trees ----------------------------------------------------------------------------------


def make_trees(
    parcels: list[dict],
    buildings: list[dict],
    graph: dict,
    osm_trees: list[dict],
    terrain: Terrain,
    cfg: dict,
) -> list[dict]:
    """OSM trees plus seeded rule-based trees in parcels (gardens, forests, ...)."""
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    bzone = unary_union(bpolys).buffer(float(cfg["building_clearance_m"])) if bpolys else None
    rzone = road_zone(graph, float(cfg["road_clearance_m"]))
    shapely.prepare(terrain.coverage)
    for g in (bzone, rzone):
        if g is not None:
            shapely.prepare(g)
    trees: list[dict] = []

    def tree(x, y, rng, parcel, forest, src):
        shape_ = "cone" if forest and rng.random() < 0.6 else "box"
        palette = cfg["forest_canopy_colours"] if forest else cfg["canopy_colours"]
        return {
            "src": src,
            "parcel": parcel,
            "x": _r3(x),
            "y": _r3(y),
            "z": _r3(terrain.surface(np.array([x]), np.array([y]))[0]),
            "scale": round(0.8 + 0.45 * rng.random(), 3),
            "rot": round(rng.random() * math.pi / 2, 3),
            "shape": shape_,
            "colour": palette[rng.randrange(len(palette))],
        }

    for f in sorted(osm_trees, key=lambda f: f["properties"]["osm_id"]):
        x, y = f["geometry"]["coordinates"][:2]
        if terrain.coverage.contains(Point(x, y)):
            trees.append(
                tree(x, y, seeded(f"{f['properties']['osm_id']}:tree"), None, False, "osm")
            )

    density = cfg["density_per_1000m2"]
    spacing = float(cfg["min_spacing_m"])
    for p in sorted(parcels, key=lambda p: p["id"]):
        rate = float(density.get(p["kind"], 0.0))
        if rate <= 0:
            continue
        poly = shape(p["polygon"])
        inner = poly.buffer(-float(cfg["parcel_edge_clearance_m"]))
        if inner.is_empty:
            continue
        shapely.prepare(inner)
        rng = seeded(f"{p['id']}:trees")
        expected = rate * poly.area / 1000.0
        count = min(int(cfg["max_per_parcel"]), int(expected) + (rng.random() < expected % 1))
        minx, miny, maxx, maxy = inner.bounds
        placed: list[tuple[float, float]] = []
        for _ in range(count * 40):
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


def run_props(work_dir: Path = DEFAULT_WORK_DIR, rules_path: Path = DEFAULT_RULES) -> dict:
    terrain, _ = load_terrain(work_dir)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    parcels = parcels_doc["parcels"]
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    graph = json.loads((work_dir / "roads.json").read_text())
    osm_trees = json.loads((work_dir / "normalised" / "trees.geojson").read_text())["features"]
    fcfg = load_section("props", rules_path)
    fences = make_fences(parcels, buildings, graph, terrain, fcfg["fences"])
    trees = make_trees(parcels, buildings, graph, osm_trees, terrain, fcfg["trees"])
    spawns = make_spawns(
        parcels,
        buildings,
        graph,
        trees,
        terrain,
        load_section("spawns", rules_path),
        load_section("roads", rules_path),
        float(parcels_doc["chunk_size_m"]),
    )
    props = {"version": PROPS_FORMAT_VERSION, "fences": fences, "trees": trees}
    spawn_doc = {
        "version": SPAWNS_FORMAT_VERSION,
        "axes": "local metres, x east, y north, z up (elevation)",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "spawns": spawns,
    }
    for name, doc in (("props.json", props), ("spawns.json", spawn_doc)):
        (work_dir / name).write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    return {"fences": len(fences), "trees": len(trees), "spawns": len(spawns)}


def run(force: bool = False, **_options: object) -> int:
    try:
        counts = run_props()
    except FileNotFoundError as exc:
        print(f"props: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    print("props: " + ", ".join(f"{k}: {v}" for k, v in counts.items()))
    return 0
