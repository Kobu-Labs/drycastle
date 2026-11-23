"""Seeded spawn points inside the ``player_spawn`` zones."""

import math

import numpy as np
import shapely
from shapely.geometry import Point
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits import zones as zone_kinds
from drycastle_pipeline.props.helpers import chunk_of, road_zone, seeded
from drycastle_pipeline.terrain.terrain import Terrain


def make_spawns(
    zones: list[tuple[str, BaseGeometry]],
    buildings: list[dict],
    graph: dict,
    trees: list[dict],
    terrain: Terrain,
    cfg: dict,
    chunk_m: float,
    water: BaseGeometry | None = None,
) -> list[dict]:
    """Seeded spawn points inside the ``player_spawn`` zones and nowhere else.

    ``zones`` is ``(zone id, polygon)``: the drawn zones and the rule zone's ground areas (``zones.kind_areas``). A point lies
    inside the terrain and clear of buildings (``min_building_distance_m``), trees (``min_tree_distance_m``) and ``water``
    (``water_clearance_m``). A zone gets one point per ``m2_per_point`` of its usable ground, rounded up, so any zone with
    usable ground has points and a bigger one has more: no minimum, no maximum. No zone at all: the points are drawn over the
    whole terrain instead, off the roads (``kind`` ``random``, no zone id); a zone with no usable ground gets none and does not
    trigger that."""
    bpolys = [footprint_polygon(b["footprint"]) for b in buildings]
    tree_gap = float(cfg["min_tree_distance_m"])
    if trees and zones:  # only the trees within reach of a zone can take ground from it: the rest need no disc
        spots = shapely.points([t["x"] for t in trees], [t["y"] for t in trees])
        near = np.unique(shapely.STRtree(spots).query([poly for _, poly in zones], predicate="dwithin", distance=tree_gap + 1.0)[1])
        trees = [trees[i] for i in near]
    avoid = [
        g
        for g in (
            unary_union(bpolys).buffer(float(cfg["min_building_distance_m"])) if bpolys else None,
            unary_union([Point(t["x"], t["y"]).buffer(tree_gap) for t in trees]) if trees else None,
            water.buffer(float(cfg["water_clearance_m"])) if water is not None and not water.is_empty else None,
        )
        if g is not None
    ]
    clear = unary_union(avoid) if avoid else None
    kind = "zone"
    if not zones:
        kind = "random"
        rzone = road_zone(graph, 0.5)
        zones = [("", terrain.coverage.difference(rzone) if rzone is not None else terrain.coverage)]
    usable: list[tuple[str, BaseGeometry]] = []
    for zid, poly in zones:
        region = (poly if poly.is_valid else poly.buffer(0)).intersection(terrain.coverage)
        if clear is not None:
            region = region.difference(clear)
        pieces = [g for g in getattr(region, "geoms", [region]) if g.geom_type == "Polygon" and g.area > 0.01]
        if pieces:
            usable.append((zid, unary_union(pieces)))
    picked: list[tuple[str, float, float]] = []
    for zid, region in usable:
        n = math.ceil(region.area / float(cfg["m2_per_point"]))
        picked += [(zid, x, y) for x, y in zone_kinds.sample_points(region, n, seeded(f"spawns:{zid}"))]
    picked.sort(key=lambda s: (s[0], s[1], s[2]))
    out = []
    for i, (zid, x, y) in enumerate(picked, 1):
        z = float(terrain.surface(np.array([x]), np.array([y]))[0])
        out.append(
            {
                "id": f"S-{i:03d}",
                "x": r3(x),
                "y": r3(y),
                "z": r3(z),
                "kind": kind,
                "zone": zid,
                "chunk": chunk_of(x, y, chunk_m),
            }
        )
    return out
