"""Hand-edited parcel outlines (``parcels.<ID>.outline`` of ``edits.json``): the splice the MCP tool uses and the one
place they are applied, the parcels stage (``parcels.build_parcels``). Every later stage reads the edited ``parcels.json``
(fence, ground, detail, object clearance, parcel lookup), so none of them knows about outlines.

All geometry here is in local metres (the frame of ``parcels.json``); ``edits.json`` is absolute S-JTSK, the caller shifts.
"""

from collections.abc import Mapping

from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

# an outline edit and its neighbours: areas below this (m2) are slivers, not overlaps
OVERLAP_EPS_M2 = 0.01
# a building counts as inside the new outline when it pokes out by no more than this (m)
BUILDING_TOLERANCE_M = 0.1


def _polys(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom]
    return [p for g in getattr(geom, "geoms", []) for p in _polys(g)]


def _arc(ring: LineString, a: float, b: float) -> list[tuple[float, float]]:
    """The points of the closed ``ring`` walked forward from distance ``a`` to ``b`` (wrapping past the start)."""
    pts = list(ring.coords)[:-1]
    cum = [ring.project(Point(p)) for p in pts]
    length = ring.length
    span = (b - a) % length
    out = [tuple(ring.interpolate(a).coords[0])]
    inner = sorted(((c - a) % length, p) for c, p in zip(cum, pts, strict=True))
    out += [p for off, p in inner if 0 < off < span]
    out.append(tuple(ring.interpolate(b).coords[0]))
    return out


def splice_outline(outline: list[tuple[float, float]], line: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """``outline`` with the stretch between the two boundary points nearest the ends of ``line`` replaced by ``line``.

    The ends of the line are moved onto the boundary. Of the two possible results the valid one of the larger area is
    kept, so a line that cuts off a corner keeps the rest of the parcel. Raises ValueError when neither is a valid polygon."""
    if len(outline) < 3 or len(line) < 2:
        raise ValueError("needs an outline of at least 3 points and a line of at least 2")
    ring = LineString([*outline, outline[0]])
    d0, d1 = ring.project(Point(line[0])), ring.project(Point(line[-1]))
    if abs(d0 - d1) < 1e-6:
        raise ValueError("both ends of the line are at the same place on the outline")
    middle = [tuple(p) for p in line[1:-1]]
    best: list[tuple[float, float]] | None = None
    best_area = 0.0
    # keep the arc p0 -> p1 and close back along the line (reversed), or keep p1 -> p0 and close along the line
    for keep, closing in ((_arc(ring, d0, d1), middle[::-1]), (_arc(ring, d1, d0), middle)):
        cand = [*keep, *closing]
        poly = Polygon(cand)
        if poly.is_valid and poly.area > best_area and len(cand) >= 3:
            best, best_area = cand, poly.area
    if best is None:
        raise ValueError("the line does not make a simple outline with the boundary (does it cross it, or itself?)")
    return best


def buildings_outside(outline: Polygon, buildings: Mapping[str, BaseGeometry]) -> list[str]:
    """IDs of the ``buildings`` that are not inside ``outline`` (up to :data:`BUILDING_TOLERANCE_M`)."""
    grown = outline.buffer(BUILDING_TOLERANCE_M)
    return sorted(b for b, g in buildings.items() if not grown.covers(g if g.is_valid else g.buffer(0)))


def apply_outlines(
    geoms: Mapping[str, BaseGeometry],
    outlines: Mapping[str, Polygon],
    buildings: Mapping[str, Mapping[str, BaseGeometry]] | None = None,
) -> tuple[dict[str, BaseGeometry], BaseGeometry, list[str]]:
    """Lay the edited ``outlines`` (parcel ID -> polygon) over the parcel ``geoms``: ``(geoms, freed, warnings)``.

    An edited parcel gets its outline; an unedited parcel it overlaps is cut back by exactly the overlap (if that splits it,
    the largest piece stays and the rest is freed). ``freed`` is the land that left a parcel and no parcel took, for the
    caller to hand to the ground. An outline whose parcel is not in ``geoms`` or that leaves a building of ``buildings``
    (parcel ID -> building ID -> geometry) outside is skipped with a warning. Nothing in ``geoms`` is changed."""
    out = dict(geoms)
    warnings: list[str] = []
    applied: dict[str, Polygon] = {}
    for pid, poly in sorted(outlines.items()):
        if pid not in geoms:
            warnings.append(f"parcels.{pid}.outline: {pid} is not a parcel of this map; skipped")
        elif outside := buildings_outside(poly, (buildings or {}).get(pid, {})):
            warnings.append(f"parcels.{pid}.outline: leaves {', '.join(outside)} outside; skipped")
        else:
            applied[pid] = orient(poly, -1.0)
    if not applied:
        return out, Polygon(), warnings
    taken = unary_union(list(applied.values()))
    freed: list[BaseGeometry] = []
    for pid, poly in applied.items():
        old = geoms[pid] if geoms[pid].is_valid else geoms[pid].buffer(0)
        freed.append(old.difference(poly))
        out[pid] = poly
    for pid, geom in geoms.items():
        if pid in applied or not geom.intersects(taken):
            continue
        cut = geom.difference(taken)
        if geom.intersection(taken).area <= OVERLAP_EPS_M2:
            continue
        pieces = sorted(_polys(cut), key=lambda p: -p.area)
        if not pieces:
            warnings.append(f"{pid}: lies wholly inside edited outlines; kept as it is")
            continue
        if len(pieces) > 1:
            warnings.append(f"{pid}: an edited outline split it; the largest piece stays, {len(pieces) - 1} smaller freed")
            freed += pieces[1:]
        out[pid] = orient(pieces[0], -1.0)
    kept = unary_union([g if g.is_valid else g.buffer(0) for g in out.values()])
    return out, unary_union(freed).difference(kept), warnings
