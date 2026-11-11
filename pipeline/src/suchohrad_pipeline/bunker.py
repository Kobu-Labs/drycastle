"""WW2 pillbox ("bunker") model: the footprint extruded with a rounded concrete dome, a reinforced
door with a grille, small firing slits and pale weathering streaks.

``bunker_mesh(ring, door_dir)`` takes a footprint ring in any plane-metre frame and the direction the
door faces (a unit vector in that frame); it returns a :class:`Mesh` standing on z = 0 in the same frame.
Used by the ``bunker`` landmark (a building override) and by the ``prim:bunker`` primitive.
Output is deterministic and flat-coloured like every other mesh.
"""

import math

import numpy as np
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh, triangulate

CONCRETE, DOME, STREAK = "#a9a698", "#8d8e76", "#d8d5ca"
DOOR_FRAME, DOOR_LEAF, SLIT = "#6e4a35", "#2a2420", "#1b1b1b"

WALL_H = 1.8  # vertical wall, metres
RISE = 1.0  # dome height above the wall
RINGS = 4  # dome steps
DOME_END = 0.48  # scale of the flat cap relative to the footprint


def _scaled(ring: np.ndarray, c: np.ndarray, s: float) -> np.ndarray:
    return c + (ring - c) * s


def _plate(tris: list, cols: list, p, n, t, a, b, z0, z1, depth, colour) -> None:
    """Box standing ``depth`` proud of a wall: centre ``p`` on the wall line, outward normal ``n``,
    tangent ``t``, spanning ``a..b`` along the wall and ``z0..z1`` up."""
    corners = []
    for d in (-0.02, depth):  # slightly inside the wall so there is no gap
        for s in (a, b):
            for z in (z0, z1):
                corners.append((*(np.asarray(p) + t * s + n * d), z))
    c = np.mean(corners, axis=0)
    idx = [  # six quads of the 8 corners (index = d*4 + s*2 + z)
        (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3),
    ]
    for q in idx:
        pts = [np.array(corners[i]) for i in q]
        for tri in ((pts[0], pts[1], pts[2]), (pts[0], pts[2], pts[3])):
            nrm = np.cross(tri[1] - tri[0], tri[2] - tri[0])
            if nrm @ ((tri[0] + tri[1] + tri[2]) / 3 - c) < 0:
                tri = (tri[0], tri[2], tri[1])
            tris.append([tuple(v) for v in tri])
            cols.append(colour)


def bunker_mesh(ring, door_dir, detail: bool = True) -> Mesh:
    poly = orient(Polygon(ring), 1.0)
    pts = np.array(poly.exterior.coords)[:-1]
    c = np.array(poly.centroid.coords[0])
    tris: list = []
    cols: list = []
    concrete, dome, streak = (hex_to_rgb(x) for x in (CONCRETE, DOME, STREAK))

    def strip(lo: np.ndarray, z0: float, hi: np.ndarray, z1: float, colour) -> None:
        n = len(lo)
        for k in range(n):
            j = (k + 1) % n
            a0, b0 = (*lo[k], z0), (*lo[j], z0)
            a1, b1 = (*hi[k], z1), (*hi[j], z1)
            tris.append([a0, b0, b1])
            cols.append(colour)
            tris.append([a0, b1, a1])
            cols.append(colour)

    strip(pts, 0.0, pts, WALL_H, concrete)
    prev, prev_z = pts, WALL_H
    for k in range(1, RINGS + 1):
        theta = (k / RINGS) * math.acos(DOME_END)
        cur, cur_z = _scaled(pts, c, math.cos(theta)), WALL_H + RISE * math.sin(theta)
        strip(prev, prev_z, cur, cur_z, dome)
        prev, prev_z = cur, cur_z
    for t3 in triangulate(Polygon(prev)):
        v = [(x, y, prev_z) for x, y in t3]
        n = np.cross(np.subtract(v[1], v[0]), np.subtract(v[2], v[0]))
        tris.append(v if n[2] > 0 else [v[0], v[2], v[1]])
        cols.append(dome)
    for t3 in triangulate(poly):
        v = [(x, y, 0.0) for x, y in t3]
        n = np.cross(np.subtract(v[1], v[0]), np.subtract(v[2], v[0]))
        tris.append(v if n[2] < 0 else [v[0], v[2], v[1]])
        cols.append(concrete)

    if detail:
        edges = []
        for k in range(len(pts)):
            p, q = pts[k], pts[(k + 1) % len(pts)]
            d = q - p
            ln = float(np.hypot(*d))
            if ln < 1.2:
                continue
            t = d / ln
            edges.append((ln, k, p, t, np.array([t[1], -t[0]])))  # outward = right of a CCW edge
        dd = np.asarray(door_dir, float)
        dd = dd / np.linalg.norm(dd)
        door = max(
            (e for e in edges if e[4] @ dd > 0.5 and e[0] >= 1.6),
            key=lambda e: e[0] * (e[4] @ dd),
            default=None,
        )
        frame, leaf, slit = (hex_to_rgb(x) for x in (DOOR_FRAME, DOOR_LEAF, SLIT))
        if door:
            ln, _, p, t, n = door
            mid = ln / 2
            _plate(tris, cols, p, n, t, mid - 0.55, mid + 0.55, 0.0, 1.55, 0.05, frame)
            _plate(tris, cols, p, n, t, mid - 0.42, mid + 0.42, 0.1, 1.45, 0.05, leaf)
        others = sorted((e for e in edges if e is not door), key=lambda e: -e[0])[:4]
        for ln, k, p, t, n in others:  # one firing slit on each of the longest other walls
            mid = ln / 2
            _plate(tris, cols, p, n, t, mid - 0.25, mid + 0.25, 1.0, 1.25, 0.05, slit)
        for ln, k, p, t, n in edges:  # pale streaks running down from the dome
            for f in (0.3, 0.68):
                if ln * f > 0.6 and ln * (1 - f) > 0.6:
                    w = 0.22 + 0.1 * ((k * 7 + int(f * 10)) % 3)
                    _plate(tris, cols, p, n, t, ln * f - w, ln * f + w, 0.7 + 0.1 * (k % 3),
                           WALL_H, 0.02, streak)
    return Mesh(np.array(tris, float), np.array(cols, np.uint8))
