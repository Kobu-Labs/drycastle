"""Fence sections along parcel boundaries and the ``fences.json`` document."""

import math

import numpy as np
from shapely.geometry import Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import nearest_points, unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.schema import FenceSpec
from drycastle_pipeline.props.helpers import merged_lines, road_zone, seeded
from drycastle_pipeline.terrain.terrain import Terrain

FENCES_FORMAT_VERSION = 1
FENCES_AXES = "local metres, x east, y north, z up (elevation of the ground at each end); height_m above that"
_MIN_PIECE_M = 0.5




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
) -> list[dict]:
    """Fence sections along parcel boundaries: dicts with parcel, a, b (x, y) and za, zb (z).

    ``specs`` holds the effective fence of parcels that have edits (gaps in local metres); a
    parcel without a spec gets the rules' ``auto`` fence. ``groups`` maps a group ID to its member
    parcel IDs: the members' outer edge is fenced as one property, each piece with its owner's
    settings and the seeded roll keyed by the group ID. Custom sections carry their own
    ``type``, ``height_m``, ``thickness_m`` and ``colour`` (and ``column`` when the edit names one).

    The gate gap of a rule fence is on the boundary that faces a road: within ``gate_road_within_m`` of a
    carriageway of ``graph``.
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
    road_union = road_zone(graph, float(cfg["gate_road_within_m"]))
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
                        "a": [r3(pts[i, 0]), r3(pts[i, 1])],
                        "b": [r3(pts[i + 1, 0]), r3(pts[i + 1, 1])],
                        "za": r3(z[i]),
                        "zb": r3(z[i + 1]),
                        **style,
                    }
                )
        if fenced:
            full = unary_union(fenced).buffer(0.3)
            done = full if done is None else done.union(full)
    return sections


def fences_doc(sections: list[dict], groups: dict[str, list[str]], cfg: dict) -> dict:
    """The ``fences.json`` document: every fence section as the game needs it (ground-plan segment, heights, owner).

    ``sections`` is :func:`make_fences`' output, ``groups`` the valid property groups (group ID -> member parcel IDs),
    ``cfg`` the rules' ``props.fences``. ``height_m`` and ``type`` are the effective values (the section's own, else the
    rules'; ``type`` is null for the plain box). ``group`` is the property group of the owning parcel, or null.
    """
    member_of = {pid: gid for gid, pids in groups.items() for pid in pids}
    return {
        "version": FENCES_FORMAT_VERSION,
        "axes": FENCES_AXES,
        "fences": [
            {
                "parcel": s["parcel"],
                "group": member_of.get(s["parcel"]),
                "a": s["a"],
                "b": s["b"],
                "za": s["za"],
                "zb": s["zb"],
                "height_m": float(s.get("height_m", cfg["height_m"])),
                "type": s.get("type", cfg.get("type")),
            }
            for s in sections
        ],
    }
