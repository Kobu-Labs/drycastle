"""Trees of the map: per-tree overrides, clearance zones, ``make_trees``, the per-chunk cap and the forest ground objects plan."""

import hashlib
import math
import sys
from pathlib import Path

import numpy as np
import shapely
import yaml
from shapely.geometry import Point, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.schema import DEFAULT_EDITS
from drycastle_pipeline.ground.areas import ground_units
from drycastle_pipeline.props.canopy import ortho_trees
from drycastle_pipeline.props.helpers import chunk_of, road_zone, seeded
from drycastle_pipeline.props.stands import CANOPY_CELL_PX, canopy_mask
from drycastle_pipeline.terrain.terrain import Terrain

DEFAULT_TREE_OVERRIDES = DEFAULT_EDITS.parent / "tree_overrides.yaml"


def load_tree_overrides(path: Path | None, origin: tuple[float, float] | None) -> list[tuple[float, float, float]]:
    """Per-tree overrides of the planted trees (``config/tree_overrides.yaml``): the circles to clear, as ``(x, y, radius)`` in local map metres.

    The file has one key, ``remove``: a list of ``{at: [x, y], radius: m, note: text}``. ``at`` is absolute EPSG:5514 metres (like a sketch
    point or a gate: it survives a change of the map's frame origin), ``radius`` defaults to 1 m: every tree whose trunk stands within it is
    left out, whether the detector found it alone or the canopy planting placed it. A missing file means no overrides.
    """
    if path is None or not path.is_file():
        return []
    doc = yaml.safe_load(path.read_text()) or {}
    if not isinstance(doc, dict) or set(doc) - {"remove"}:
        raise ValueError(f"{path.name}: the only top-level key is `remove`")
    out: list[tuple[float, float, float]] = []
    entries = doc.get("remove") or []
    if not isinstance(entries, list):
        raise ValueError(f"{path.name}: `remove` must be a list")
    if entries and origin is None:
        raise ValueError(f"{path.name}: tree overrides need the frame origin (parcels.json origin_epsg5514)")
    for i, e in enumerate(entries):
        where = f"{path.name}: remove[{i}]"
        if not isinstance(e, dict) or set(e) - {"at", "radius", "note"}:
            raise ValueError(f"{where}: keys are at, radius, note")
        at = e.get("at")
        if not (isinstance(at, list) and len(at) == 2 and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in at)):
            raise ValueError(f"{where}: `at` is [x, y] in EPSG:5514 metres")
        radius = e.get("radius", 1.0)
        if not isinstance(radius, (int, float)) or isinstance(radius, bool) or not 0 < radius <= 50:
            raise ValueError(f"{where}: `radius` is metres, above 0 and at most 50")
        out.append((float(at[0]) - origin[0], float(at[1]) - origin[1], float(radius)))  # type: ignore[index]
    return out


def clearance_zones(buildings: list[dict], graph: dict, cfg: dict, objects: list[tuple[float, float]] | None):
    """(buildings, roads, placed objects) grown by the cfg's ``building_clearance_m``, ``road_clearance_m``, ``object_clearance_m``; None where empty."""
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    bzone = unary_union(bpolys).buffer(float(cfg["building_clearance_m"])) if bpolys else None
    rzone = road_zone(graph, float(cfg["road_clearance_m"]))
    ozone = (
        unary_union([Point(x, y).buffer(float(cfg["object_clearance_m"])) for x, y in objects])
        if objects
        else None
    )
    return bzone, rzone, ozone


def make_trees(
    buildings: list[dict],
    graph: dict,
    terrain: Terrain,
    cfg: dict,
    objects: list[tuple[float, float]] | None = None,
    water: BaseGeometry | None = None,
    origin: tuple[float, float] | None = None,
    removed: list[tuple[float, float, float]] | None = None,
    wet: BaseGeometry | None = None,
    engine: str = "auto",
) -> list[dict]:
    """Every tree of the map: planted from the orthophoto detector (:func:`ortho_trees`, ``make ortho-trees``).
    ``engine``: who plants the canopy, "auto" (the Rust engine when installed), "rust" or "python"; the trees are the same.

    There is no other source: no OSM trees, no per-parcel density, no shore rows. ``cfg["ortho_dir"]`` is the folder
    of the detector's output (``trees.json`` and the canopy mask); without it there are no trees. ``origin`` is the
    frame origin (EPSG:5514; without it there are no trees). ``objects`` are placed object positions (local metres) that trees keep
    ``object_clearance_m`` away from; ``water`` (local metres) is where the water patches lie: no trunk in it. ``removed`` are the per-tree
    overrides (:func:`load_tree_overrides`): circles ``(x, y, radius)`` in local metres, every planted tree with its trunk in one is dropped
    after the planting, so no other tree moves.
    ``wet`` (see :func:`wet_zone`: that and the water ground areas) is what the swamp stand keeps near; none: no swamp.
    """
    if origin is None or not (Path(cfg["ortho_dir"]) / "trees.json").is_file():
        return []
    bzone, rzone, ozone = clearance_zones(buildings, graph, cfg, objects)
    shapely.prepare(terrain.coverage)
    if wet is not None:  # every water: the water ground areas too, not only the wet areas of the water patches
        water = wet
    for g in (bzone, rzone, ozone, water):
        if g is not None:
            shapely.prepare(g)

    def tree(x, y, rng, parcel, forest, src, scale=None, cone_share=0.6):
        shape_ = "cone" if forest and rng.random() < cone_share else "box"
        palette = cfg["forest_canopy_colours"] if forest else cfg["canopy_colours"]
        return {
            "src": src,
            "parcel": parcel,
            "x": r3(x),
            "y": r3(y),
            "z": r3(terrain.surface(np.array([x]), np.array([y]))[0]),
            "scale": round(0.8 + 0.45 * rng.random(), 3) if scale is None else round(scale, 3),
            "rot": round(rng.random() * math.pi / 2, 3),
            "shape": shape_,
            "colour": palette[rng.randrange(len(palette))],
            "forest": forest,
        }

    trees = ortho_trees(tree, terrain, (bzone, rzone, ozone, water), cfg, origin, wet, engine=engine)
    if removed:
        kept = [t for t in trees if not any(math.hypot(t["x"] - x, t["y"] - y) <= r for x, y, r in removed)]
        for x, y, r in removed:
            if not any(math.hypot(t["x"] - x, t["y"] - y) <= r for t in trees):
                print(f"props: warning: {DEFAULT_TREE_OVERRIDES.name}: no tree within {r:g} m of local ({x:.1f}, {y:.1f}); the entry does nothing", file=sys.stderr)
        trees = kept
    return trees


def plan_ground_objects(
    parcels_doc: dict,
    buildings: list[dict],
    graph: dict,
    terrain: Terrain,
    trees: list[dict],
    tree_cfg: dict,
    cfg: dict,
    objects: list[tuple[float, float]] | None = None,
    water: BaseGeometry | None = None,
    wet: BaseGeometry | None = None,
) -> list[dict]:
    """Logs, stumps, boulders, root plates, anthills and burrows on the forest ground (see :mod:`groundobjects`); none without orthophoto canopy data.
    ``wet`` (see :func:`wet_zone`) buffered by the swamp stand's reach is swamp ground: no anthills there.

    Allowed where the canopy mask is set, on ground of the ``ground_kinds`` types, not on a property (parcel of kind
    ``avoid_parcel_kinds``), not within the clearances of a building, road, placed object or water.
    """
    from drycastle_pipeline.props.groundobjects import make_ground_objects

    origin = parcels_doc.get("origin_epsg5514")
    if origin is None or not (Path(tree_cfg["ortho_dir"]) / "trees.json").is_file():
        return []
    _, mask, minx, maxy, res = canopy_mask(tree_cfg)
    ox, oy = origin
    x0, y_top = minx - ox, maxy - oy
    units = ground_units(parcels_doc)
    forest = [shape(u["polygon"]) for u in units if u["kind"] in cfg["ground_kinds"]]
    avoid = [shape(p["polygon"]) for p in parcels_doc["parcels"] if p["kind"] in cfg["avoid_parcel_kinds"]]
    bzone, rzone, ozone = clearance_zones(buildings, graph, cfg, objects)
    keep_out = [g for g in (bzone, rzone, ozone, water, *avoid) if g is not None]
    forest_zone = unary_union(forest) if forest else None
    if forest_zone is None:
        return []
    off = unary_union(keep_out) if keep_out else None
    for g in (terrain.coverage, forest_zone, off):
        if g is not None:
            shapely.prepare(g)
    h, w = mask.shape

    def inside(xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
        col = np.floor((xs - x0) / res).astype(int)
        row = np.floor((y_top - ys) / res).astype(int)
        ok = (row >= 0) & (row < h) & (col >= 0) & (col < w)
        ok[ok] = mask[row[ok], col[ok]]
        ok &= shapely.contains_xy(terrain.coverage, xs, ys) & shapely.contains_xy(forest_zone, xs, ys)
        if off is not None:
            ok &= ~shapely.contains_xy(off, xs, ys)
        return ok

    stands = tree_cfg.get("stands")
    swamp = wet.buffer(float(stands["swamp"]["reach_m"])) if wet is not None and stands and "swamp" in stands else None
    if swamp is not None:
        shapely.prepare(swamp)
    return make_ground_objects(
        trees, tree_cfg, cfg, mask, (x0, y_top, res), inside, lambda xs, ys: terrain.surface(xs, ys), seeded, CANOPY_CELL_PX,
        (lambda xs, ys: shapely.contains_xy(swamp, xs, ys)) if swamp is not None else None,
    )



def cap_trees(trees: list[dict], chunk_m: float, limit: int) -> list[dict]:
    """Keep at most ``limit`` planted canopy trees per chunk (the single trees the detector found always stay); 0 = no cap.

    Which trees go is decided by a hash of position, so it is stable and spread over the chunk.
    """
    if limit <= 0:
        return trees
    by_chunk: dict[tuple[int, int], list[dict]] = {}
    for tr in trees:
        if tr["src"] == "ortho-canopy":
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
