"""Low-poly building meshes: walls plus a flat, gabled or hipped roof, and a collision mesh.

A mesh is an array of triangles ``tris`` (n, 3, 3) in local metres (x east, y north, z up) with
one flat RGB colour per triangle ``colours`` (n, 3) uint8. Triangles are wound counter-clockwise
seen from outside. The roof is built in the ridge frame (u along the ridge, v across): the
footprint is cut along the roof creases, each piece is triangulated with earcut, and its vertices
get the height of the roof surface. Walls rise to the roof surface (so gable ends are closed).
"""

import math
from dataclasses import dataclass
from itertools import pairwise

import mapbox_earcut as earcut
import numpy as np
from shapely import get_coordinates
from shapely.affinity import rotate
from shapely.geometry import LineString, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon, roof_frame

_EDGE_MARGIN = 0.01
_KINK_TOLERANCE_M = 1e-3
_MIN_PIECE_AREA = 1e-6


@dataclass
class Mesh:
    tris: np.ndarray  # (n, 3, 3) float64
    colours: np.ndarray  # (n, 3) uint8

    @classmethod
    def empty(cls) -> "Mesh":
        return cls(np.zeros((0, 3, 3)), np.zeros((0, 3), np.uint8))

    @classmethod
    def concat(cls, meshes: list["Mesh"]) -> "Mesh":
        if not meshes:
            return cls.empty()
        return cls(
            np.concatenate([m.tris for m in meshes]), np.concatenate([m.colours for m in meshes])
        )

    @property
    def face_count(self) -> int:
        return len(self.tris)


def _polygons(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom] if geom.area > _MIN_PIECE_AREA else []
    return [p for g in getattr(geom, "geoms", []) for p in _polygons(g)]


def triangulate(poly: Polygon) -> list[tuple[tuple[float, float], ...]]:
    """Triangles (as 2D vertex triples) covering ``poly`` including holes."""
    rings = [poly.exterior, *poly.interiors]
    pts = np.concatenate([np.asarray(r.coords)[:-1] for r in rings]).astype(np.float64)
    ends = np.cumsum([len(r.coords) - 1 for r in rings]).astype(np.uint32)
    idx = earcut.triangulate_float64(pts, ends)
    return [tuple(map(tuple, pts[idx[i : i + 3]])) for i in range(0, len(idx), 3)]


def _roof_height_fn(roof: str, rise: float, cu: float, cv: float, a: float, b: float):
    def gabled(u: float, v: float) -> float:
        return rise * max(0.0, 1.0 - abs(v - cv) / b)

    def hipped(u: float, v: float) -> float:
        k = min((b - abs(v - cv)) / b, (a - abs(u - cu)) / b)
        return rise * min(1.0, max(0.0, k))

    return gabled if roof == "gabled" else hipped


def _roof_regions(roof: str, cu: float, cv: float, a: float, b: float) -> list[Polygon]:
    """Pieces of the (u, v) plane inside which the roof surface is one plane."""
    e, big = _EDGE_MARGIN, 1.0
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


def building_mesh(b: dict) -> Mesh:
    """Visual mesh of one buildings.json entry (walls, roof, floor), flat colours per face."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    eave, rise, roof = float(b["eave_height_m"]), float(b["roof_height_m"]), b["roof"]
    wall_c, roof_c = b["wall_colour"], b["roof_colour"]
    if roof == "flat" or rise <= 0:
        tris, cols = _extrude(
            poly, lambda u, v: eave, None, [poly], lambda x, y: eave, roof_c, wall_c
        )
        return Mesh(tris, cols)

    angle, cu, cv, a, bb = roof_frame(poly, b["ridge_dir"], hipped=roof == "hipped")
    local = rotate(poly, -angle, origin=(0, 0), use_radians=True)
    height = _roof_height_fn(roof, rise, cu, cv, a, bb)
    regions = _roof_regions(roof, cu, cv, a, bb)
    boundary = unary_union([r.exterior for r in regions])
    pieces = [p for r in regions for p in _polygons(local.intersection(r))]

    def top(u: float, v: float) -> float:
        return eave + height(u, v)

    tris, cols = _extrude(local, top, boundary, pieces, top, roof_c, wall_c)
    return Mesh(_rotate_back(tris, angle), cols)


def collision_mesh(b: dict) -> Mesh:
    """Extruded footprint, no roof detail; pitched roofs are cut at half the roof height."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    top_z = float(b["eave_height_m"]) + float(b["roof_height_m"]) / 2
    grey = [128, 128, 128]
    tris, cols = _extrude(poly, lambda u, v: top_z, None, [poly], lambda x, y: top_z, grey, grey)
    return Mesh(tris, cols)
