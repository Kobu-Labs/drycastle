"""Faces that fight for the same pixels (z-fighting): two triangles of one mesh in the same plane, facing the same way,
overlapping, in different colours. The depth buffer cannot order them, so the picture flickers between the two as the
camera moves. (The same colour in the same place shows no flicker; a pair facing apart is never seen at once.)"""

from collections import defaultdict

import numpy as np
import shapely
from shapely.strtree import STRtree

PLANE_TOL_M = 0.001  # nearer than this is one plane
AREA_TOL_M2 = 1e-4  # overlaps smaller than this are rounding
WARN_M2 = 0.05  # a mesh whose fights add up to less than this has only slivers nobody sees flicker


def fights(tris: np.ndarray, colours: np.ndarray, plane_tol: float = PLANE_TOL_M, area_tol: float = AREA_TOL_M2) -> list[tuple[int, int, float]]:
    """(i, j, overlap m2) of the triangles that fight. ``tris``: (n, 3, 3) metres; ``colours``: (n, 3) per triangle.
    Distances are measured from the larger triangle's plane, in coordinates about the mesh's own centre (a thin
    triangle's normal is too rough to place its plane far from the origin)."""
    tris = np.asarray(tris, float)
    if not len(tris):
        return []
    tris = tris - tris.reshape(-1, 3).mean(0)
    colours = np.asarray(colours)
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    cross = np.cross(b - a, c - a)
    double_area = np.linalg.norm(cross, axis=1)
    keep = np.flatnonzero(double_area > 1e-9)
    normal = cross[keep] / double_area[keep, None]
    groups: dict[tuple, list[int]] = defaultdict(list)
    for k, key in enumerate(map(tuple, np.round(normal * 100).astype(np.int64))):
        groups[key].append(int(keep[k]))
    unit = np.zeros_like(cross)
    unit[keep] = normal
    out = []
    for members in groups.values():
        if len(members) < 2:
            continue
        members = np.array(members)
        n0 = unit[members[0]]
        u = np.cross(n0, (0.0, 0.0, 1.0) if abs(n0[2]) < 0.9 else (1.0, 0.0, 0.0))
        u /= np.linalg.norm(u)
        v = np.cross(n0, u)
        # every triangle of the group as a polygon in the plane's own 2D frame (rings close themselves)
        flat = shapely.buffer(shapely.polygons(shapely.linearrings(np.stack([tris[members] @ u, tris[members] @ v], axis=-1))), 0)
        xs, ys = STRtree(flat).query(flat)  # the pairs whose boxes overlap
        sel = ys > xs
        xs, ys = xs[sel], ys[sel]
        i, j = members[xs], members[ys]
        sel = ~(colours[i] == colours[j]).all(axis=1) & (np.einsum("ij,ij->i", unit[i], unit[j]) >= 0.9999)
        xs, ys, i, j = xs[sel], ys[sel], i[sel], j[sel]
        a_first = double_area[i] >= double_area[j]
        big, small = np.where(a_first, i, j), np.where(a_first, j, i)
        off = np.abs(np.einsum("kvi,ki->kv", tris[small] - tris[big][:, 0:1], unit[big])).max(axis=1)
        sel = off <= plane_tol
        xs, ys, i, j = xs[sel], ys[sel], i[sel], j[sel]
        area = shapely.area(shapely.intersection(flat[xs], flat[ys]))
        for k in np.flatnonzero(area > area_tol):
            out.append((int(i[k]), int(j[k]), float(area[k])))
    return out
