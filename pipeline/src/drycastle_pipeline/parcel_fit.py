"""Fit road parcels to the OSM road they belong to (``parcel_fit`` in ``rules.yaml``, applied by the parcels stage).

The cadastre's road parcels are often offset from the carriageway and ragged along its edges. A rule names parcels
and the OSM ways they hold; each such parcel becomes a strip of constant width (``half_width_m`` each side of the
way's centre line, flat ends), and the tiling of the village stays whole:

* the ground the strips give up (the old parcel outside the strip) goes to the neighbouring parcels, each part to the
  neighbour whose boundary lies nearest (a Voronoi split of the shared boundary), so no holes open;
* the ground the strips take (where the old parcel fell short of the road) is cut out of its neighbours.

Parcel IDs, cadastral references and the registry are not touched (the registry holds the cadastre's own outlines).
"""

import math

from shapely import voronoi_polygons
from shapely.geometry import LineString, MultiPoint, Point
from shapely.geometry.base import BaseGeometry
from shapely.ops import linemerge, substring, unary_union

_SAMPLE_M = 1.0  # spacing of the boundary samples that decide which neighbour gets a piece of given-up ground
_MIN_PIECE_M2 = 0.01
_MIN_PART_M2 = 2.0  # a part of a fitted outline smaller than this is a crumb of the split and is dropped


def _polys(g: BaseGeometry) -> list:
    if g.is_empty:
        return []
    return list(g.geoms) if hasattr(g, "geoms") else [g]


def _along(line: LineString, g: BaseGeometry) -> tuple[float, float]:
    """The stretch of ``line`` (distances from its start) that ``g`` spans, from its outline's projections."""
    ds = [line.project(Point(c)) for poly in _polys(g) for c in poly.exterior.coords]
    return min(ds), max(ds)


def _strips(line: LineString, spans: dict[str, tuple[float, float]], half: float) -> dict[str, BaseGeometry]:
    """One flat-ended strip per parcel along ``line``; neighbouring spans meet halfway between them."""
    order = sorted(spans, key=lambda k: spans[k])
    ranges = {k: list(spans[k]) for k in order}
    for a, b in zip(order, order[1:], strict=False):
        if ranges[a][1] >= ranges[b][0] or ranges[b][0] - ranges[a][1] < 2 * half:  # they overlap or nearly touch
            mid = (ranges[a][1] + ranges[b][0]) / 2
            ranges[a][1] = ranges[b][0] = mid
    out = {}
    for k, (s0, s1) in ranges.items():
        if s1 - s0 < 0.5:
            continue
        out[k] = substring(line, s0, s1).buffer(half, cap_style="flat", join_style="mitre")
    return out


def _owners(given: BaseGeometry, neighbours: dict[str, BaseGeometry]) -> dict[str, BaseGeometry]:
    """Split ``given`` among the neighbours: each point goes to the neighbour whose boundary sample is nearest."""
    pts, who = [], []
    seen = set()
    for pid, geom in neighbours.items():
        edge = geom.boundary.intersection(given.buffer(0.05))
        for part in _polys(edge):
            if not isinstance(part, LineString) or part.length == 0:
                continue
            n = max(1, math.ceil(part.length / _SAMPLE_M))
            for i in range(n + 1):
                p = part.interpolate(part.length * i / n)
                key = (round(p.x, 2), round(p.y, 2))
                if key not in seen:
                    seen.add(key)
                    pts.append(p)
                    who.append(pid)
    if not pts:
        return {}
    if len(pts) < 3:
        return {who[0]: given}
    cells = voronoi_polygons(MultiPoint(pts), extend_to=given.envelope.buffer(50))
    out: dict[str, list] = {}
    for cell in _polys(cells):
        piece = cell.intersection(given)
        if piece.is_empty or piece.area < _MIN_PIECE_M2:
            continue
        i = next((j for j, p in enumerate(pts) if cell.contains(p)), None)
        if i is None:
            continue
        out.setdefault(who[i], []).append(piece)
    return {pid: unary_union(parts) for pid, parts in out.items()}


def fit_roads(
    geoms: dict[str, BaseGeometry], ways: dict[str, BaseGeometry], cfg: dict
) -> dict[str, BaseGeometry]:
    """The parcel outlines (by parcel ID) after every rule of ``cfg["rules"]``; parcels no rule names are returned
    unchanged unless a rule moved ground to or from them. ``ways`` maps OSM way ids (``w123``) to their lines."""
    out = dict(geoms)
    for rule in cfg.get("rules") or []:
        ids = [i for i in rule["parcels"] if i in out]
        parts = [ways[w] for w in rule["ways"] if w in ways]
        if not ids or not parts:
            continue
        joined = unary_union(parts)
        merged = joined if joined.geom_type == "LineString" else linemerge(joined)
        if merged.geom_type != "LineString":
            raise ValueError(f"parcel_fit: ways {rule['ways']} do not form one line")
        half = float(rule.get("half_width_m", 3.0))
        old = {i: out[i] for i in ids}
        strips = _strips(merged, {i: _along(merged, g) for i, g in old.items()}, half)
        if not strips:
            continue
        taken = unary_union(list(strips.values()))
        given = unary_union(list(old.values())).difference(taken)
        others = {i: g for i, g in out.items() if i not in old and g.distance(given) < 0.05}
        for pid, piece in _owners(given, others).items():
            out[pid] = out[pid].union(piece)
        for pid in out:
            if pid not in old:
                if out[pid].intersects(taken):
                    out[pid] = out[pid].difference(taken)
        for pid in old:
            out[pid] = strips.get(pid, out[pid].difference(taken))
    return {k: (_tidy(g) if g is not geoms[k] else g) for k, g in out.items()}


def _tidy(g: BaseGeometry) -> BaseGeometry:
    """A valid outline without crumbs: parts under ``_MIN_PART_M2`` left over from the splits are dropped."""
    if not g.is_valid:
        g = g.buffer(0)
    parts = _polys(g)
    if len(parts) > 1:
        big = [p for p in parts if p.area >= _MIN_PART_M2] or [max(parts, key=lambda p: p.area)]
        g = big[0] if len(big) == 1 else unary_union(big)
    return g
