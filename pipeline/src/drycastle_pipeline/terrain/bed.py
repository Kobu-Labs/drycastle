"""Natural beds of the water bodies: a depth grid from each outline, and the terrain carved to it.

A body's bed is generated from its outline alone (plus a seed, so the same outline always gives the same bed):

* **Banks shelve.** Depth grows with the distance from the bank, over a shelf that is steeper where the bank is
  on the outside of a bend (the bank's curvature, measured on the outline, shortens the shelf).
* **Pools and riffles.** A river's target depth follows a seeded value-noise pattern along its main axis (the
  principal axis of the outline): mid-channel about ``mid_depth_m``, deeper in pools (up to ``pool_depth_m``),
  shallow over riffles (``riffle_depth_m``, wadeable). Bars come every 60 to 120 m along the flow and run across it.
* **Outer bends are deeper.** Where the outline curls into the water (its centre of curvature is on the water side),
  the depth is raised by ``bend_depth_m``.
* **Ponds** get a shallow bowl (``pond.max_depth_m``) with a little irregularity, from the same shelf.

A water patch's ``max_depth`` (edits.json) scales the body's depth so its deepest point is that depth.

:func:`carve_terrain` then lowers the terrain grid under each body to surface minus depth, blending the bank outside
the outline back over ``carve_blend_m``, so the terrain mesh and its collision carry the bed.
"""

import base64
import hashlib
import math
from dataclasses import dataclass

import numpy as np
import shapely
from shapely import contains_xy
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient
from shapely.strtree import STRtree

from drycastle_pipeline.terrain.terrain import Terrain

RING_STEP_M = 1.0  # spacing of the outline samples used for the curvature
CURVE_HALF_M = 8.0  # the curvature of the bank is measured over this half span
CURVE_SMOOTH_M = 9.0  # ... and then averaged over this run of outline samples
RIFFLE_RAMP = 0.3  # the pattern value range over which a pool turns into a riffle


@dataclass(frozen=True)
class DepthGrid:
    """Depth (m) on an axis-aligned grid of cell centres; 0 outside the outline.

    ``depth[j, i]`` is the cell whose centre is at ``(x0 + (i + 0.5) * cell_m, y0 + (j + 0.5) * cell_m)``; row 0 is
    the southern row (smallest y).
    """

    x0: float
    y0: float
    cell_m: float
    depth: np.ndarray

    @property
    def cols(self) -> int:
        return int(self.depth.shape[1])

    @property
    def rows(self) -> int:
        return int(self.depth.shape[0])

    def max_depth(self) -> float:
        return float(self.depth.max()) if self.depth.size else 0.0

    def depth_at(self, x, y) -> np.ndarray:
        """Bilinear depth at local points between cell centres; 0 beyond the grid."""
        x, y = np.asarray(x, float), np.asarray(y, float)
        gx = (x - self.x0) / self.cell_m - 0.5
        gy = (y - self.y0) / self.cell_m - 0.5
        i0, j0 = np.floor(gx).astype(int), np.floor(gy).astype(int)
        fx, fy = gx - i0, gy - j0
        rows, cols = self.depth.shape

        def at(j, i):
            ok = (i >= 0) & (i < cols) & (j >= 0) & (j < rows)
            out = np.zeros(x.shape)
            out[ok] = self.depth[j[ok], i[ok]]
            return out

        return (
            at(j0, i0) * (1 - fx) * (1 - fy)
            + at(j0, i0 + 1) * fx * (1 - fy)
            + at(j0 + 1, i0) * (1 - fx) * fy
            + at(j0 + 1, i0 + 1) * fx * fy
        )

    def encode(self) -> str:
        """The depths in centimetres: little-endian uint16, row by row from the south, base64 (see ``terrain/water.py``)."""
        cm = np.clip(np.round(self.depth * 100), 0, 65535).astype("<u2")
        return base64.b64encode(cm.tobytes()).decode("ascii")

    @staticmethod
    def decode(text: str, cols: int, rows: int) -> np.ndarray:
        cm = np.frombuffer(base64.b64decode(text), dtype="<u2")
        return cm.reshape(rows, cols).astype(float) / 100.0


@dataclass
class WaterBody:
    """One body of water: its outline (local metres), its flat surface and its natural bed."""

    id: str
    geom: Polygon
    surface_y: float
    grid: DepthGrid
    river: bool = False

    @property
    def max_depth_m(self) -> float:
        return self.grid.max_depth()

    @property
    def bed_y(self) -> float:
        """The lowest bed (the surface minus the deepest point), kept for readers of the flat-bed format."""
        return round(self.surface_y - self.max_depth_m, 3)

    def scaled(self, k: float) -> "WaterBody":
        """A copy with heights and depths multiplied by ``k`` (the preview's height exaggeration)."""
        g = self.grid
        grid = DepthGrid(g.x0, g.y0, g.cell_m, g.depth * k)
        return WaterBody(self.id, self.geom, self.surface_y * k, grid, self.river)


def _smooth(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _seed(text: str) -> int:
    return int.from_bytes(hashlib.sha256(text.encode()).digest()[:8], "big")


def value_noise(u: np.ndarray, v: np.ndarray, seed: int, lam_u: float, lam_v: float) -> np.ndarray:
    """Seeded smooth value noise in [-1, 1]: a random lattice (spacing ``lam_u`` by ``lam_v``), interpolated."""
    rng = np.random.default_rng(seed)
    gu = (u - u.min()) / lam_u
    gv = (v - v.min()) / lam_v
    nu, nv = int(np.floor(gu.max())) + 2, int(np.floor(gv.max())) + 2
    table = rng.uniform(-1.0, 1.0, size=(nv + 1, nu + 1))
    i0, j0 = np.floor(gu).astype(int), np.floor(gv).astype(int)
    i0, j0 = np.minimum(i0, nu - 1), np.minimum(j0, nv - 1)
    fx, fy = _smooth(gu - i0), _smooth(gv - j0)
    a = table[j0, i0] * (1 - fx) + table[j0, i0 + 1] * fx
    b = table[j0 + 1, i0] * (1 - fx) + table[j0 + 1, i0 + 1] * fx
    return np.clip((a * (1 - fy) + b * fy) * 1.6, -1.0, 1.0)  # the interpolation shrinks the spread: stretch it back


def bank_curvature(geom: Polygon) -> tuple[np.ndarray, np.ndarray]:
    """Outline samples (m, 2) and, per sample, the bank's curvature (1/m) toward the water: positive where the bank
    curls round the water (the outside of a bend), smoothed along the outline."""
    ring = orient(geom, 1.0).exterior  # counter-clockwise: the water lies to the left of every edge
    length = ring.length
    count = max(16, int(round(length / RING_STEP_M)))
    samples = np.linspace(0.0, length, count, endpoint=False)
    pts = shapely.get_coordinates(shapely.line_interpolate_point(ring, samples))
    k = max(2, int(round(CURVE_HALF_M / RING_STEP_M)))
    nxt, prv = np.roll(pts, -k, axis=0), np.roll(pts, k, axis=0)
    tang = nxt - prv
    tang = tang / np.maximum(np.linalg.norm(tang, axis=1, keepdims=True), 1e-9)
    normal_in = np.column_stack([-tang[:, 1], tang[:, 0]])
    second = (nxt - 2 * pts + prv) / (k * RING_STEP_M) ** 2
    kin = np.sum(second * normal_in, axis=1)
    w = max(1, int(round(CURVE_SMOOTH_M / RING_STEP_M)) // 2)
    padded = np.concatenate([kin[-w:], kin, kin[:w]])
    kin = np.convolve(padded, np.ones(2 * w + 1) / (2 * w + 1), mode="valid")
    return pts, kin


def depth_grid(
    geom: Polygon,
    body_id: str,
    params: dict,
    river: bool,
    bed: dict,
    max_depth_m: float | None = None,
) -> DepthGrid:
    """The natural bed of one outline (see the module docstring).

    ``params`` is the class's section of the water rules (``river`` or ``pond``), ``bed`` the shared one (cell size,
    least depth). ``max_depth_m`` replaces the class's deepest point (a water patch's ``max_depth``).
    """
    cell = float(bed["cell_m"])
    minx, miny, maxx, maxy = geom.bounds
    x0, y0 = math.floor(minx / cell) * cell, math.floor(miny / cell) * cell
    cols, rows = int(math.ceil((maxx - x0) / cell)) + 1, int(math.ceil((maxy - y0) / cell)) + 1
    gx, gy = np.meshgrid(x0 + (np.arange(cols) + 0.5) * cell, y0 + (np.arange(rows) + 0.5) * cell)
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(geom, gx, gy)
    out = np.zeros(rows * cols)
    if not inside.any():
        return DepthGrid(x0, y0, cell, out.reshape(rows, cols))
    px, py = gx[inside], gy[inside]
    cells = shapely.points(px, py)

    ring_pts, kin = bank_curvature(geom)
    tree = STRtree(shapely.points(ring_pts))
    found = tree.query_nearest(cells, all_matches=False)
    nearest = found[1][np.argsort(found[0])]
    bank = shapely.distance(cells, geom.exterior)
    bend = np.clip(kin[nearest] / float(params["bend_curvature_per_m"]), 0.0, 1.0) if river else np.zeros(px.size)

    seed = _seed(body_id + ":bed")
    if river:
        centre = np.array([px.mean(), py.mean()])
        rel = np.column_stack([px, py]) - centre
        _, vecs = np.linalg.eigh(np.cov(rel.T))
        axis = vecs[:, -1]  # the principal axis: the flow runs along it
        across = np.array([-axis[1], axis[0]])
        along, cross = rel @ axis, rel @ across
        rng = np.random.default_rng(seed)
        lam = float(params["pattern_m"]) * float(rng.uniform(0.9, 1.1))
        pattern = value_noise(along, cross, seed, lam, float(params["cross_m"]))
        mid, pool = float(params["mid_depth_m"]), float(params["pool_depth_m"])
        riffle = float(params["riffle_depth_m"])
        target = mid + (pool - mid) * pattern  # pools deeper, runs shallower
        bar = _smooth((float(params["riffle_below"]) - pattern) / RIFFLE_RAMP)
        target = target + (riffle - target) * bar
        target = np.clip(target + float(params["bend_depth_m"]) * bend, riffle, pool)
        shelf = float(params["shelf_m"]) * (1.0 - float(params["outer_shelf_steepness"]) * bend)
    else:
        cap = float(params["max_depth_m"])
        pattern = value_noise(px, py, seed, float(params["pattern_m"]), float(params["pattern_m"]))
        # the class's bowl, a little irregular: from (1 - pattern_gain) * cap up to cap itself, never deeper
        target = cap * (1.0 - float(params["pattern_gain"]) * 0.5 * (1.0 - pattern))
        shelf = np.full(px.size, float(params["shelf_m"]))
    target = np.clip(target, 0.0, None)
    depth = target * _smooth(bank / shelf)
    if max_depth_m is not None and depth.max() > 0:
        depth = depth * (float(max_depth_m) / depth.max())  # the deepest point is exactly max_depth
    depth = np.maximum(depth, float(bed["min_depth_m"]))
    out[inside] = depth
    return DepthGrid(x0, y0, cell, np.round(out.reshape(rows, cols), 2))


def carve_terrain(terrain: Terrain, bodies: list[WaterBody], blend_m: float) -> None:
    """Lower the terrain under every body to its bed, in place.

    Inside an outline the ground is set to ``surface_y - depth``. Outside, a bank standing above the surface is cut
    down to it over ``blend_m`` (never raised), so the bank meets the water without a step. Only valid grid vertices
    are touched.
    """
    xs, ys = terrain.vertex_xy()
    for body in sorted(bodies, key=lambda b: b.id):
        geom, surface = body.geom, body.surface_y
        minx, miny, maxx, maxy = geom.buffer(blend_m).bounds
        sel = (xs >= minx) & (xs <= maxx) & (ys >= miny) & (ys <= maxy) & terrain.valid
        if not sel.any():
            continue
        px, py, h = xs[sel], ys[sel], terrain.heights[sel]
        inside = contains_xy(geom, px, py)
        new = h.copy()
        new[inside] = surface - body.grid.depth_at(px[inside], py[inside])
        outside = ~inside
        if outside.any():
            dist = shapely.distance(shapely.points(px[outside], py[outside]), geom.exterior)
            weight = _smooth(1.0 - dist / blend_m)
            above = np.maximum(h[outside] - surface, 0.0)
            new[outside] = h[outside] - above * weight
        terrain.heights[sel] = np.round(new, 3)
