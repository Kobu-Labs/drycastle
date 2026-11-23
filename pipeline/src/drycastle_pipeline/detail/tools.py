"""Generic tools of the recipes: blobs, Poisson-disc and jittered-grid scatter, distance and geometry helpers."""

import math
import random

import numpy as np
import shapely
from shapely.geometry import LineString, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.core.geometry import polygons
from drycastle_pipeline.detail.model import _MIN_PIECE_M2, _TILE_M
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.terrain.terrain import Terrain


def blob(
    rng: random.Random,
    cx: float,
    cy: float,
    radius: float,
    wobble: float = 0.3,
    stretch: float = 1.0,
    n: int = 14,
    axis: float | None = None,
) -> Polygon:
    """A smooth irregular blob: radius ``radius`` times (1 + wobble * noise) round the angle, stretched
    along ``axis`` (random without one) by ``stretch`` (1 = round). The noise is three random harmonics."""
    harmonics = [(k, rng.random() * math.tau, 1.0 / k) for k in (2, 3, 5)]
    norm = sum(a for _, _, a in harmonics)
    if axis is None:
        axis = rng.random() * math.pi
    ca, sa = math.cos(axis), math.sin(axis)
    pts = []
    for i in range(n):
        t = math.tau * i / n
        r = radius * (1 + wobble * sum(a * math.sin(k * t + ph) for k, ph, a in harmonics) / norm)
        x, y = r * math.cos(t) * stretch, r * math.sin(t)
        pts.append((cx + x * ca - y * sa, cy + x * sa + y * ca))
    return Polygon(pts).buffer(0)


def poisson_disc(
    rng: random.Random, geom: BaseGeometry, min_dist: float, k: int = 20
) -> list[tuple[float, float]]:
    """Bridson's Poisson-disc sampling inside ``geom``: points at least ``min_dist`` apart, the same
    set for the same generator state. Disconnected parts are reached by restarting from random seeds.
    Pure Python: for the small counts (blob centres, beds); :func:`jitter_grid` is the fast one."""
    if geom.is_empty or min_dist <= 0:
        return []
    minx, miny, maxx, maxy = geom.bounds
    cell = min_dist / math.sqrt(2)
    grid: dict[tuple[int, int], int] = {}
    pts: list[tuple[float, float]] = []
    shapely.prepare(geom)

    def key(x: float, y: float) -> tuple[int, int]:
        return int((x - minx) / cell), int((y - miny) / cell)

    def fits(x: float, y: float) -> bool:
        i, j = key(x, y)
        for dj in range(-2, 3):
            for di in range(-2, 3):
                q = grid.get((i + di, j + dj))
                if q is not None and math.hypot(pts[q][0] - x, pts[q][1] - y) < min_dist:
                    return False
        return True

    def add(x: float, y: float, active: list[int]) -> None:
        grid[key(x, y)] = len(pts)
        active.append(len(pts))
        pts.append((x, y))

    seeds = int(60 + 20 * (maxx - minx) * (maxy - miny) / max(geom.area, 1e-6))
    for _ in range(seeds):
        x, y = minx + rng.random() * (maxx - minx), miny + rng.random() * (maxy - miny)
        if not (shapely.contains_xy(geom, x, y) and fits(x, y)):
            continue
        active: list[int] = []
        add(x, y, active)
        while active:
            a = rng.randrange(len(active))
            px, py = pts[active[a]]
            for _ in range(k):
                r, t = min_dist * (1 + rng.random()), rng.random() * math.tau
                x, y = px + r * math.cos(t), py + r * math.sin(t)
                if minx <= x <= maxx and miny <= y <= maxy and shapely.contains_xy(geom, x, y) and fits(x, y):
                    add(x, y, active)
                    break
            else:
                active.pop(a)
    return pts


def thin(rng: random.Random, pts: list[tuple[float, float]], density) -> list[tuple[float, float]]:
    """Keeps each point with probability ``density(xs, ys)`` (arrays in, arrays in [0, 1] out).
    Thinning a Poisson-disc set keeps its minimum distance."""
    if not pts:
        return []
    xs, ys = np.array([p[0] for p in pts]), np.array([p[1] for p in pts])
    p = np.clip(np.asarray(density(xs, ys), float), 0.0, 1.0)
    return [pt for pt, q in zip(pts, p, strict=True) if rng.random() < q]


def jitter_grid(g: np.random.Generator, geom: BaseGeometry, spacing: float, jitter: float = 0.9) -> np.ndarray:
    """One point per ``spacing`` metre cell of a grid, jittered inside the cell, kept inside ``geom``: (n, 2).

    Even enough for scatter and fast (vectorised, only the tiles that touch ``geom`` are visited), so it is
    what the clutter uses; deterministic for the same generator state."""
    if geom.is_empty or spacing <= 0:
        return np.zeros((0, 2))
    shapely.prepare(geom)
    minx, miny, maxx, maxy = geom.bounds
    per_tile = max(1, int(_TILE_M / spacing))
    tile = per_tile * spacing
    ci, cj = np.meshgrid(np.arange(per_tile), np.arange(per_tile))
    out = []
    for ti in range(int(math.ceil((maxx - minx) / tile))):
        for tj in range(int(math.ceil((maxy - miny) / tile))):
            x0, y0 = minx + ti * tile, miny + tj * tile
            if not shapely.intersects(geom, shapely.box(x0, y0, x0 + tile, y0 + tile)):
                continue
            u = g.random((2, per_tile, per_tile))
            xs = x0 + (ci + 0.5 + (u[0] - 0.5) * jitter) * spacing
            ys = y0 + (cj + 0.5 + (u[1] - 0.5) * jitter) * spacing
            inside = shapely.contains_xy(geom, xs, ys)
            if inside.any():
                out.append(np.stack([xs[inside], ys[inside]], axis=1))
    return np.concatenate(out) if out else np.zeros((0, 2))


def scatter(g: np.random.Generator, geom: BaseGeometry, spacing: float, density=1.0) -> np.ndarray:
    """:func:`jitter_grid` points of ``geom`` kept with probability ``density`` (a number, or a function
    of (xs, ys) returning an array in [0, 1])."""
    pts = jitter_grid(g, geom, spacing)
    if not len(pts):
        return pts
    p = density(pts[:, 0], pts[:, 1]) if callable(density) else density
    return pts[g.random(len(pts)) < np.clip(p, 0.0, 1.0)]


def distance_to(geom: BaseGeometry | None):
    """A function (xs, ys) -> distance of each point to ``geom`` (inf when there is none)."""
    if geom is None or geom.is_empty:
        return lambda xs, ys: np.full(len(xs), np.inf)
    return lambda xs, ys: shapely.distance(shapely.points(xs, ys), geom)


def _lines(geom: BaseGeometry) -> list[LineString]:
    if geom.is_empty:
        return []
    if isinstance(geom, LineString):
        return [geom]
    return [ln for g in getattr(geom, "geoms", []) for ln in _lines(g)]


def _rad(rng: random.Random, span: list[float]) -> float:
    return span[0] + rng.random() * (span[1] - span[0])


def _gen(pid: str, purpose: str) -> np.random.Generator:
    return np.random.default_rng(seeded(f"{pid}:detail:{purpose}").getrandbits(64))


def _clean(geom: BaseGeometry, tol: float = 0.06) -> BaseGeometry:
    """Simplified, without slivers: fewer triangles for the same look."""
    keep = [p.simplify(tol, preserve_topology=True) if tol > 0 else p for p in polygons(geom) if p.area >= _MIN_PIECE_M2]
    keep = [p for p in keep if not p.is_empty and p.area >= _MIN_PIECE_M2]
    return unary_union(keep) if keep else Polygon()


def terrain_ok(t: Terrain, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    """Which points stand on a meshed terrain cell (all four corners valid)."""
    gx = np.floor(xs / t.step - t.i0).astype(int)
    gy = np.floor(ys / t.step - t.j0).astype(int)
    inside = (gx >= 0) & (gx < t.ncols) & (gy >= 0) & (gy < t.nrows)
    cx, cy = np.clip(gx, 0, t.ncols - 1), np.clip(gy, 0, t.nrows - 1)
    v = t.valid
    return inside & v[cy, cx] & v[cy, cx + 1] & v[cy + 1, cx] & v[cy + 1, cx + 1]


def _union(parts: list[BaseGeometry]) -> BaseGeometry:
    parts = [p for p in parts if not p.is_empty]
    return unary_union(parts) if parts else Polygon()


def _difference(geom: BaseGeometry, taken: BaseGeometry) -> BaseGeometry:
    """``geom`` minus ``taken``. An intersection can bring stray lines or points along, and GEOS then fails the
    overlay ("Unable to determine overlay result geometry dimension"); only the polygonal parts matter for scattering
    (a line or a point has no area to scatter over), so that case is retried on those."""
    if taken.is_empty:
        return geom
    try:
        return geom.difference(taken)
    except shapely.errors.GEOSException:
        return _union(polygons(geom)).difference(_union(polygons(taken)))


def _along(rng: random.Random, ring: BaseGeometry, spacing: float, keep: float):
    """Points along a ring every ~``spacing`` metres (each kept with probability ``keep``) with the heading there."""
    length = ring.length
    if length < 1.0:
        return
    steps = max(1, int(length / spacing))
    for i in range(steps):
        if rng.random() > keep:
            continue
        s = (i + rng.random()) / steps * length
        p, q = ring.interpolate(s), ring.interpolate(min(length, s + 0.5))
        yield p, math.atan2(q.y - p.y, q.x - p.x)
