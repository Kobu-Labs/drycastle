"""Low-poly building meshes: walls plus a flat, gabled or hipped roof, and a collision mesh.

The roof is built in the ridge frame (u along the ridge, v across): the footprint is cut along the roof
creases, each piece is triangulated with earcut, and its vertices get the height of the roof surface. Walls
rise to the roof surface (so gable ends are closed).
"""

import math
from itertools import pairwise

import numpy as np
from shapely import get_coordinates
from shapely.affinity import rotate
from shapely.geometry import LineString, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon, roof_frame
from drycastle_pipeline.core.mesh import Mesh, solid_polygons, triangulate

_EDGE_MARGIN = 0.01
_KINK_TOLERANCE_M = 1e-3
_MIN_PIECE_AREA = 1e-6


def _roof_height_fn(roof: str, rise: float, cu: float, cv: float, a: float, b: float):
    def gabled(u: float, v: float) -> float:
        return rise * max(0.0, 1.0 - abs(v - cv) / b)

    def hipped(u: float, v: float) -> float:
        k = min((b - abs(v - cv)) / b, (a - abs(u - cu)) / b)
        return rise * min(1.0, max(0.0, k))

    def lean_to(u: float, v: float) -> float:  # one slope: low at v = cv - b, high at v = cv + b (left of the ridge direction)
        return rise * min(1.0, max(0.0, (v - (cv - b)) / (2 * b)))

    return {"gabled": gabled, "lean_to": lean_to}.get(roof, hipped)


def _roof_regions(roof: str, cu: float, cv: float, a: float, b: float) -> list[Polygon]:
    """Pieces of the (u, v) plane inside which the roof surface is one plane."""
    e, big = _EDGE_MARGIN, 1.0
    if roof == "lean_to":
        x0, x1 = cu - a - big, cu + a + big
        return [Polygon([(x0, cv - b - big), (x1, cv - b - big), (x1, cv + b + big), (x0, cv + b + big)])]
    if roof == "gabled":
        x0, x1 = cu - a - big, cu + a + big
        return [
            Polygon([(x0, cv), (x1, cv), (x1, cv + b + big), (x0, cv + b + big)]),
            Polygon([(x0, cv - b - big), (x1, cv - b - big), (x1, cv), (x0, cv)]),
        ]
    r = a - b  # ridge ends at cu +- r
    w, s = (cu - a - e, cu + a + e), (cv - b - e, cv + b + e)
    regions = [
        [(w[0], s[0]), (w[1], s[0]), (cu + r, cv), (cu - r, cv)],
        [(w[0], s[1]), (w[1], s[1]), (cu + r, cv), (cu - r, cv)],
        [(w[1], s[0]), (w[1], s[1]), (cu + r, cv)],
        [(w[0], s[0]), (w[0], s[1]), (cu - r, cv)],
    ]
    out = []
    for pts in regions:
        dedup = [p for i, p in enumerate(pts) if p != pts[i - 1]]
        poly = Polygon(dedup)
        if poly.area > _MIN_PIECE_AREA:
            out.append(poly)
    return out


def _split_edge(p, q, boundary: BaseGeometry | None, height) -> list[tuple[float, float]]:
    """Points along p->q where the roof height has a kink (including both ends)."""
    ts = [0.0, 1.0]
    d = np.subtract(q, p)
    if boundary is not None and not boundary.is_empty:
        hit = LineString([p, q]).intersection(boundary)
        n2 = float(d @ d)
        for c in get_coordinates(hit):
            t = float((c - np.asarray(p)) @ d / n2)
            if 1e-6 < t < 1 - 1e-6:
                ts.append(t)
        # a boundary line lying along p-q comes back as one merged run: its own corners on p-q are kinks too
        for c in get_coordinates(boundary):
            t = float((c - np.asarray(p)) @ d / n2)
            if 1e-6 < t < 1 - 1e-6 and float(np.hypot(*(np.asarray(p) + t * d - c))) < 1e-6:
                ts.append(t)
    ts = sorted({round(t, 9) for t in ts})
    pts = [(p[0] + t * d[0], p[1] + t * d[1]) for t in ts]
    kept = [pts[0]]
    for i in range(1, len(pts) - 1):
        a0, a1 = kept[-1], pts[i + 1]
        t0 = ts[pts.index(a0)]
        frac = (ts[i] - t0) / (ts[i + 1] - t0)
        lerp = height(*a0) * (1 - frac) + height(*a1) * frac
        if abs(height(*pts[i]) - lerp) > _KINK_TOLERANCE_M:
            kept.append(pts[i])
    kept.append(pts[-1])
    return kept


def _rotate_back(tris: np.ndarray, angle: float) -> np.ndarray:
    c, s = math.cos(angle), math.sin(angle)
    out = tris.copy()
    out[..., 0] = c * tris[..., 0] - s * tris[..., 1]
    out[..., 1] = s * tris[..., 0] + c * tris[..., 1]
    return out


def _extrude(poly: Polygon, top, boundary, roof_pieces, roof_z, roof_colour, wall_colour):
    """Shared body of the visual and collision meshes (all in the working frame)."""
    tris: list[np.ndarray] = []
    colours: list = []

    def add(tri, colour):
        tris.append(np.asarray(tri, dtype=np.float64))
        colours.append(colour)

    for ring in [poly.exterior, *poly.interiors]:
        pts = list(ring.coords)
        for p, q in pairwise(pts):
            if p == q:
                continue
            chain = _split_edge(p, q, boundary, top)
            for s, t in pairwise(chain):
                hs, ht = top(*s), top(*t)
                add([(*s, 0.0), (*t, 0.0), (*t, ht)], wall_colour)
                add([(*s, 0.0), (*t, ht), (*s, hs)], wall_colour)

    for piece in roof_pieces:
        for tri in triangulate(piece):
            v3 = [(x, y, roof_z(x, y)) for x, y in tri]
            n = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
            if n[2] < 0:
                v3 = [v3[0], v3[2], v3[1]]
            add(v3, roof_colour)
    for tri in triangulate(poly):
        a0, a1, a2 = tri
        n = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
        order = (a0, a1, a2) if n < 0 else (a0, a2, a1)
        add([(x, y, 0.0) for x, y in order], wall_colour)
    return np.array(tris), np.array(colours, dtype=np.uint8)


def split_parts(b: dict) -> list[dict]:
    """A building whose pitched roof covers only its main part (``roof_part``, a polygon in map metres: one cadastral
    footprint often holds a house and its lower extensions) as the buildings it is made of: the main part, the footprint
    inside ``roof_part``, keeps the roof and heights; every other piece is a flat-roofed block ``rest_height_m`` high.
    Any other building: itself."""
    if not b.get("roof_part"):
        return [b]
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    # the part is grown by 2 cm so that a cut drawn along a wall takes the wall with it, and what is left is opened by
    # 3 cm: no hair-thin strip of the footprint stays behind as a wall of its own
    cut = Polygon(b["roof_part"]).buffer(0).buffer(0.02, join_style=2)
    main = solid_polygons(poly.intersection(cut))
    rest = solid_polygons(poly.difference(cut).buffer(-0.03, join_style=2).buffer(0.03, join_style=2))
    if not main:
        return [{k: v for k, v in b.items() if k not in ("roof_part", "rest_height_m")}]
    base = {k: v for k, v in b.items() if k not in ("roof_part", "rest_height_m")}

    def ring(p: Polygon) -> dict:
        p = orient(p, 1.0)
        return {"exterior": [list(c) for c in p.exterior.coords[:-1]], "holes": [[list(c) for c in r.coords[:-1]] for r in p.interiors]}

    low = float(b["rest_height_m"]) if b.get("rest_height_m") else min(2.8, float(b["eave_height_m"]))
    out = [{**base, "footprint": ring(p)} for p in main]
    for p in rest:
        lean = _lean_to(p, main, low, base if b.get("rest_roof") == "auto" else None) if b.get("rest_roof") in ("lean_to", "auto") else None
        out.append({**base, "footprint": ring(p), "roof": "flat", "roof_height_m": 0.0, "eave_height_m": low, "height_m": low, "ridge_dir": None, **(lean or {})})
    return out


def _lean_to(p: Polygon, main: list[Polygon], top: float, house: dict | None = None) -> dict | None:
    """A single-slope roof for a lower part ``p`` standing against the main part: its high edge, ``top`` up, along the
    longest wall the two share, falling away from it. None when they share no wall or the part is too low for a slope."""
    shared = p.exterior.intersection(unary_union(main).buffer(0.08, join_style=2))
    segs = [(a, c) for g in getattr(shared, "geoms", [shared]) if g.geom_type == "LineString" for a, c in pairwise(list(g.coords))]
    if not segs:
        return None
    a, c = max(segs, key=lambda s: math.dist(*s))
    if math.dist(a, c) < 1.0:
        return None
    ux, uy = (c[0] - a[0]) / math.dist(a, c), (c[1] - a[1]) / math.dist(a, c)
    if house is not None and house.get("roof") == "gabled" and house.get("ridge_dir") is not None:
        # a wing standing against a gable wall of a gabled house carries the house's roof on, lower: its own gabled roof,
        # ridge the same way, the same slope
        ridge = float(house["ridge_dir"])
        if abs(ux * math.cos(ridge) + uy * math.sin(ridge)) < 0.5:
            _, _, _, _, half_house = roof_frame(max(main, key=lambda g: g.area), ridge)
            _, _, _, _, half = roof_frame(p, ridge)
            rise = min(float(house["roof_height_m"]) / max(half_house, 0.5) * half, float(house["roof_height_m"]) - 0.4)
            if rise >= 0.3:
                return {"roof": "gabled", "roof_height_m": round(rise, 2), "eave_height_m": top, "height_m": round(top + rise, 2), "ridge_dir": round(ridge % math.pi, 4)}
    mid, cen = ((a[0] + c[0]) / 2, (a[1] + c[1]) / 2), p.centroid
    if -uy * (mid[0] - cen.x) + ux * (mid[1] - cen.y) < 0:  # the high edge is on the left of the ridge direction
        ux, uy = -ux, -uy
    depth = max(-uy * x + ux * y for x, y in p.exterior.coords) - min(-uy * x + ux * y for x, y in p.exterior.coords)
    rise = min(1.0, 0.2 * depth, top - 2.1)
    if rise < 0.25:
        return None
    return {"roof": "lean_to", "roof_height_m": round(rise, 2), "eave_height_m": round(top - rise, 2), "height_m": top, "ridge_dir": round(math.atan2(uy, ux) % (2 * math.pi), 4)}


def building_mesh(b: dict) -> Mesh:
    """Visual mesh of one buildings.json entry (walls, roof, floor), flat colours per face."""
    if b.get("roof_part"):
        return Mesh.concat([building_mesh(p) for p in split_parts(b)])
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    eave, rise, roof = float(b["eave_height_m"]), float(b["roof_height_m"]), b["roof"]
    wall_c, roof_c = b["wall_colour"], b["roof_colour"]
    if roof == "flat" or rise <= 0:
        tris, cols = _extrude(
            poly, lambda u, v: eave, None, [poly], lambda x, y: eave, roof_c, wall_c
        )
        return Mesh(tris, cols)

    if b.get("roof_fit"):
        from drycastle_pipeline.buildings.roof_fit import fit_building

        fitted = fit_building(b)
        if fitted is not None:
            tris, cols = _extrude(poly, fitted.top, fitted.wall_lines, [], fitted.top, roof_c, wall_c)
            roof_tris = []
            for piece, (pa, pb, pc) in fitted.pieces:
                for tri in triangulate(piece):
                    v3 = [(x, y, eave + pa * x + pb * y + pc) for x, y in tri]
                    if np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))[2] < 0:
                        v3 = [v3[0], v3[2], v3[1]]
                    roof_tris.append(v3)
            if roof_tris:
                tris = np.concatenate([tris, np.array(roof_tris, dtype=np.float64)])
                cols = np.concatenate([cols, np.tile(np.array(roof_c, dtype=np.uint8), (len(roof_tris), 1))])
            step_tris = []
            for p, q, lo_p, hi_p, lo_q, hi_q in fitted.steps:  # the wall where one part of the roof stands above the next
                a, b2, c, e = (*p, eave + lo_p), (*q, eave + lo_q), (*q, eave + hi_q), (*p, eave + hi_p)
                step_tris += [[a, b2, c], [a, c, e], [a, c, b2], [a, e, c]]
            if step_tris:
                tris = np.concatenate([tris, np.array(step_tris, dtype=np.float64)])
                cols = np.concatenate([cols, np.tile(np.array(wall_c, dtype=np.uint8), (len(step_tris), 1))])
            return Mesh(tris, cols)
    angle, cu, cv, a, bb = roof_frame(poly, b["ridge_dir"], hipped=roof == "hipped")
    local = rotate(poly, -angle, origin=(0, 0), use_radians=True)
    height = _roof_height_fn(roof, rise, cu, cv, a, bb)
    regions = _roof_regions(roof, cu, cv, a, bb)
    boundary = unary_union([r.exterior for r in regions])
    pieces = [p for r in regions for p in solid_polygons(local.intersection(r))]

    def top(u: float, v: float) -> float:
        return eave + height(u, v)

    tris, cols = _extrude(local, top, boundary, pieces, top, roof_c, wall_c)
    return Mesh(_rotate_back(tris, angle), cols)


def collision_mesh(b: dict) -> Mesh:
    """Extruded footprint, no roof detail; pitched roofs are cut at half the roof height."""
    if b.get("roof_part"):
        return Mesh.concat([collision_mesh(p) for p in split_parts(b)])
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    top_z = float(b["eave_height_m"]) + float(b["roof_height_m"]) / 2
    grey = [128, 128, 128]
    tris, cols = _extrude(poly, lambda u, v: top_z, None, [poly], lambda x, y: top_z, grey, grey)
    return Mesh(tris, cols)
