"""Fence sections along parcel boundaries and the ``fences.json`` document."""

import math

import numpy as np
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import nearest_points, unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.schema import SIDE_CORNER_DEG, FenceSpec
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


def _ring_sides(rings: list) -> tuple[list[LineString], set[tuple[float, float]]]:
    """The sides of closed rings (split at every vertex turning more than SIDE_CORNER_DEG) and their corner points (rounded)."""
    sides: list[LineString] = []
    corners: set[tuple[float, float]] = set()
    cos_max = math.cos(math.radians(SIDE_CORNER_DEG))
    for ring in rings:
        pts = [tuple(c) for c in ring.coords[:-1]]
        n = len(pts)
        cut = []
        for i in range(n):
            a, b, c = np.array(pts[i - 1]), np.array(pts[i]), np.array(pts[(i + 1) % n])
            u, v = b - a, c - b
            nu, nv = np.linalg.norm(u), np.linalg.norm(v)
            if nu > 1e-9 and nv > 1e-9 and float(u @ v) / (nu * nv) < cos_max:
                cut.append(i)
        if not cut:
            sides.append(LineString([*pts, pts[0]]))
            continue
        for k, i in enumerate(cut):
            j = cut[(k + 1) % len(cut)]
            idx = list(range(i, j + 1)) if j > i else [*range(i, n), *range(0, j + 1)]
            sides.append(LineString([pts[q] for q in idx]))
            corners.add(_key(pts[i]))
    return sides, corners


def _key(p) -> tuple[float, float]:
    return (round(float(p[0]), 3), round(float(p[1]), 3))


def _split_at(ln: LineString, corners: set[tuple[float, float]]) -> list[LineString]:
    """The line cut at its vertices that are corners."""
    coords = list(ln.coords)
    out, cur = [], [coords[0]]
    for c in coords[1:]:
        cur.append(c)
        if _key(c) in corners and len(cur) >= 2:
            out.append(LineString(cur))
            cur = [c]
    if len(cur) >= 2:
        out.append(LineString(cur))
    return [x for x in out if x.length > 0.05]


def _outer_rings(geom) -> list:
    return [g.exterior for g in getattr(geom, "geoms", [geom])]


def _house_colours(buildings: list[dict], groups: dict[str, list[str]]) -> dict[str, str]:
    """The house colour of each parcel (and group ID): the wall colour of its largest building; a group's is its members' largest."""
    best: dict[str, tuple[float, str]] = {}
    for b in buildings:
        pid, rgb = b.get("parcel"), b.get("wall_colour")
        if pid is None or rgb is None:
            continue
        area = footprint_polygon(b["footprint"]).area
        if area > best.get(pid, (-1.0, ""))[0]:
            best[pid] = (area, "#{:02x}{:02x}{:02x}".format(*(int(c) for c in rgb[:3])))
    for gid, pids in groups.items():
        have = [best[p] for p in pids if p in best]
        if have:
            best[gid] = max(have)
    return {k: v[1] for k, v in best.items()}


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
    ``type``, ``height_m``, ``thickness_m`` and ``colour`` (and ``column`` when the edit names one). A spec with
    ``house_colour`` takes the colour of the parcel's house in ``buildings`` (else its group's), when there is one. A spec with
    ``sides`` cuts its pieces at the corners and draws (or, mode none, leaves out) each side with an entry in that entry's style.

    The gate gap of a rule fence is on the boundary that faces a road: within ``gate_road_within_m`` of a
    carriageway of ``graph``.
    """
    specs = specs or {}
    polys = {p["id"]: shape(p["polygon"]) for p in parcels}
    member_of: dict[str, str] = {}
    others: dict[str, BaseGeometry | None] = {}
    outer: dict[str, BaseGeometry] = {}
    group_rings: dict[str, list] = {}
    for gid, pids in (groups or {}).items():
        union = unary_union([polys[q] for q in pids])
        group_rings[gid] = _outer_rings(union)
        outer[gid] = unary_union(
            [Polygon(g.exterior).boundary for g in getattr(union, "geoms", [union])]
        )
        for pid in pids:
            member_of[pid] = gid
            rest = [polys[q] for q in pids if q != pid]
            others[pid] = unary_union(rest) if rest else None
    houses = _house_colours(buildings, groups or {})
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

        def style_of(sp: FenceSpec) -> dict:
            colour = houses.get(pid) or houses.get(gid or "", sp.colour) if sp.house_colour else sp.colour
            return {"type": sp.type, "height_m": sp.height_m, "thickness_m": sp.thickness_m, "colour": colour,
                    **({"column": sp.column} if sp.column is not None else {})}

        style = style_of(spec) if custom and spec is not None else {}
        styled = [(ln, style) for ln in pieces]
        if custom and spec is not None and spec.sides:
            rings = group_rings[gid] if gid is not None else [poly.exterior, *poly.interiors]
            side_lines, corners = _ring_sides(rings)
            own: dict[int, FenceSpec] = {}
            for x, y, sub in spec.sides:  # a later entry for the same side wins
                p = Point(x, y)
                own[min(range(len(side_lines)), key=lambda i: side_lines[i].distance(p))] = sub
            styled = []
            for ln in pieces:
                for part in _split_at(ln, corners):
                    mid = part.interpolate(0.5, normalized=True)
                    sub = own.get(min(range(len(side_lines)), key=lambda i: side_lines[i].distance(mid)))
                    if sub is not None and sub.mode == "none":
                        continue
                    styled.append((part, style if sub is None else style_of(sub)))
        fenced = []
        for ln, style in styled:
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
