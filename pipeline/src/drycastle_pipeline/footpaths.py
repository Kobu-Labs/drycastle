"""Worn footpaths (``paths`` of ``edits.json``): a line people walk until the grass is gone.

A path is a hand-drawn line with a width. The detail stage lays its ground as bare, trodden earth over whatever ground
is there (it joins the dirt of the road verges, so every recipe that draws those draws it: no tufts on it, a few
pebbles), without touching parcels, ground areas or their kinds. The outline is not a ruler's: the width swells and
narrows along the line, the line wanders a little, and an open path thins out at its ends. Deterministic per path ID.
"""

import hashlib
import math
import random

from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

DEFAULT_WIDTH_M = 0.8
STEP_M = 0.7  # a blob of trodden ground this often along the line
WIDTH_SPAN = (0.7, 1.2)  # the width swells and narrows between these shares of its own
WANDER = 0.18  # the line wanders sideways by up to this share of the width
TAPER_M = 1.6  # an open path thins out over this length at each end


FRINGE_M = (0.55, 1.3)  # the two bands of thin, trodden grass beside the bare ground reach this much wider (both sides together)


def path_polygon(pid: str, entry: dict, origin: tuple[float, float] = (0.0, 0.0), wider_m: float = 0.0, stream: str = "") -> BaseGeometry:
    """The trodden ground of one path in local metres (``origin`` is the map's, in the absolute metres of the points).
    ``wider_m`` / ``stream``: the same path this much wider with its own swelling and wandering (a fringe band)."""
    pts = [(float(x) - origin[0], float(y) - origin[1]) for x, y in entry["points"]]
    closed = bool(entry.get("closed"))
    if closed:
        pts.append(pts[0])
    line = LineString(pts)
    width = float(entry.get("width_m", DEFAULT_WIDTH_M)) + wider_m
    rng = random.Random(int(hashlib.sha256(f"path:{pid}{stream}".encode()).hexdigest()[:12], 16))
    n = max(2, round(line.length / STEP_M))
    blobs, swell, side = [], rng.uniform(*WIDTH_SPAN), 0.0
    for k in range(n + 1):
        d = line.length * k / n
        p, q = line.interpolate(d), line.interpolate(min(d + 0.2, line.length))
        if q.equals(p):
            q, p2 = p, line.interpolate(max(d - 0.2, 0.0))
            hx, hy = p.x - p2.x, p.y - p2.y
        else:
            hx, hy = q.x - p.x, q.y - p.y
        h = math.hypot(hx, hy) or 1.0
        swell += (rng.uniform(*WIDTH_SPAN) - swell) * 0.45  # a slow walk, not a new width at every step
        side += (rng.uniform(-WANDER, WANDER) - side) * 0.5
        end = 1.0 if closed else min(1.0, 0.35 + 0.65 * min(d, line.length - d) / TAPER_M)
        r = width / 2 * swell * end
        blobs.append(Point(p.x - hy / h * side * width, p.y + hx / h * side * width).buffer(r, 6))
    # each blob joined to the next, so the path has no gaps where the blobs are small
    joined = [unary_union([blobs[k], blobs[k + 1]]).convex_hull for k in range(len(blobs) - 1)]
    return unary_union(joined).simplify(0.05)


def path_polygons(paths: dict[str, dict], origin: tuple[float, float] = (0.0, 0.0), band: int | None = None) -> list[Polygon]:
    """The trodden ground of every path (IDs in order), local metres, as single polygons. ``band`` 0 / 1: the inner / outer
    fringe band instead (thin grass beside the bare ground; each has its own outline, so the edge is not a contour line)."""
    out: list[Polygon] = []
    for pid in sorted(paths):
        g = path_polygon(pid, paths[pid], origin) if band is None else path_polygon(pid, paths[pid], origin, FRINGE_M[band], f":fringe{band}")
        out += [p for p in (g.geoms if hasattr(g, "geoms") else [g]) if not p.is_empty]
    return out
