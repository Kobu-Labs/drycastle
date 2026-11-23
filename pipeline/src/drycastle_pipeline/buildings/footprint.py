"""A building's footprint as geometry: its polygon and the frame of its roof ridge."""

import math
from itertools import pairwise

from shapely.affinity import rotate
from shapely.geometry import MultiPolygon, Polygon


def footprint_polygon(fp: dict) -> Polygon:
    return Polygon(fp["exterior"], fp["holes"])


def largest_polygon(geom) -> Polygon:
    if isinstance(geom, MultiPolygon):
        return max(geom.geoms, key=lambda g: g.area)
    return geom


def roof_frame(poly: Polygon, angle: float | None = None, hipped: bool = False):
    """Frame of the ridge: (angle, cu, cv, a, b).

    Without ``angle`` it is the minimum rotated rectangle with the angle of its longest side.
    ``a`` and ``b`` are half-extents along and across the ridge; (cu, cv) is the centre in the
    frame rotated by ``-angle``. Hipped roofs always run the ridge along the longer side.
    """
    if angle is None:
        rect = poly.minimum_rotated_rectangle
        pts = list(rect.exterior.coords)
        best, angle = -1.0, 0.0
        for p, q in pairwise(pts):
            length = math.hypot(q[0] - p[0], q[1] - p[1])
            if length > best + 1e-9:
                best, angle = length, math.atan2(q[1] - p[1], q[0] - p[0])
        angle %= math.pi
        angle = round(angle, 4)
    for _ in range(2):
        r = rotate(poly, -angle, origin=(0, 0), use_radians=True)
        x0, y0, x1, y1 = r.bounds
        a, b = (x1 - x0) / 2, (y1 - y0) / 2
        if hipped and a < b:
            angle = (angle + math.pi / 2) % math.pi
            continue
        return angle, (x0 + x1) / 2, (y0 + y1) / 2, a, b
    raise AssertionError("unreachable")
