"""Terrain stage: DMR 5.0 -> regular height grid in the local frame, flattened under buildings.

Writes ``data/work/terrain.json`` (grid description, coverage polygon, building elevations),
``terrain_heights.npy`` and ``terrain_valid.npy``. Meshes are built from these at export.

Grid: vertices at (i * step, j * step) in local metres, with step = chunk size / n and n an even
number of cells per chunk, so chunk borders are grid lines. A vertex is *valid* when it lies inside
the DMR raster (between the outermost pixel centres); a cell is meshed only when all four corners
are valid. Heights of invalid vertices are clamped samples, used only by height queries for
objects outside coverage.
"""

import functools
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

import numpy as np
import shapely
from shapely.geometry import Polygon

if TYPE_CHECKING:
    import rasterio

from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.config import DEFAULT_AREA, DEFAULT_RAW_DIR, area_paths
from suchohrad_pipeline.meshes import Mesh, triangulate
from suchohrad_pipeline.normalise import DEFAULT_WORK_DIR
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section

TERRAIN_FORMAT_VERSION = 1


@functools.cache
def _to_dmr():
    """S-JTSK -> DMR CRS; pyproj is imported on first use because it is slow to import."""
    from pyproj import Transformer

    return Transformer.from_crs("EPSG:5514", "EPSG:3046", always_xy=True)


@functools.cache
def _from_dmr():
    from pyproj import Transformer

    return Transformer.from_crs("EPSG:3046", "EPSG:5514", always_xy=True)


def cells_per_chunk(chunk_m: float, step_m: float) -> int:
    """Even number of cells per chunk closest to the wanted step."""
    return max(2, 2 * round(chunk_m / step_m / 2))


@dataclass
class Terrain:
    chunk_m: float
    n: int  # cells per chunk edge
    i0: int  # global index of the first vertex column
    j0: int  # global index of the first vertex row
    heights: np.ndarray  # (rows + 1, cols + 1), z in metres
    valid: np.ndarray  # same shape, bool
    coverage: Polygon  # local polygon covered by the DMR raster

    @property
    def step(self) -> float:
        return self.chunk_m / self.n

    @property
    def ncols(self) -> int:
        return self.heights.shape[1] - 1

    @property
    def nrows(self) -> int:
        return self.heights.shape[0] - 1

    def scaled(self, k: float) -> "Terrain":
        """A copy with every height multiplied by ``k`` (preview exaggeration)."""
        return Terrain(
            self.chunk_m, self.n, self.i0, self.j0, self.heights * k, self.valid, self.coverage
        )

    def vertex_xy(self) -> tuple[np.ndarray, np.ndarray]:
        xs = (self.i0 + np.arange(self.ncols + 1)) * self.step
        ys = (self.j0 + np.arange(self.nrows + 1)) * self.step
        return np.meshgrid(xs, ys)

    def surface(self, x, y) -> np.ndarray:
        """Height of the triangle surface of the full-resolution mesh (clamped at the grid edge).

        Each cell is split along its flatter diagonal, exactly as in ``chunk_mesh``.
        """
        x, y = np.asarray(x, float), np.asarray(y, float)
        gx = np.clip(x / self.step - self.i0, 0, self.ncols - 1e-9)
        gy = np.clip(y / self.step - self.j0, 0, self.nrows - 1e-9)
        ci, cj = gx.astype(int), gy.astype(int)
        fx, fy = gx - ci, gy - cj
        h = self.heights
        h00, h10, h01, h11 = h[cj, ci], h[cj, ci + 1], h[cj + 1, ci], h[cj + 1, ci + 1]
        d0_low = h00 + fx * (h10 - h00) + fy * (h11 - h10)
        d0_up = h00 + fx * (h11 - h01) + fy * (h01 - h00)
        d1_low = h00 + fx * (h10 - h00) + fy * (h01 - h00)
        d1_up = h11 + (1 - fx) * (h01 - h11) + (1 - fy) * (h10 - h11)
        diag1 = np.abs(h10 - h01) < np.abs(h00 - h11)
        return np.where(
            diag1, np.where(fx + fy <= 1, d1_low, d1_up), np.where(fx >= fy, d0_low, d0_up)
        )

    def _chunk_cells(self, cx: int, cy: int, dec: int):
        """Vertex slice of a chunk (every ``dec``-th vertex) and the validity of its cells."""
        a, b = cx * self.n - self.i0, cy * self.n - self.j0
        if a < 0 or b < 0 or a + self.n > self.ncols or b + self.n > self.nrows:
            return np.zeros((0, 0), bool), None
        sl = (slice(b, b + self.n + 1, dec), slice(a, a + self.n + 1, dec))
        v = self.valid[sl]
        return v[:-1, :-1] & v[:-1, 1:] & v[1:, :-1] & v[1:, 1:], sl

    def chunks(self) -> list[tuple[int, int]]:
        """Chunk indices with at least one meshed cell."""
        out = []
        for cx in range(self.i0 // self.n, (self.i0 + self.ncols) // self.n):
            for cy in range(self.j0 // self.n, (self.j0 + self.nrows) // self.n):
                if self._chunk_cells(cx, cy, 1)[0].any():
                    out.append((cx, cy))
        return out

    def chunk_mesh(self, cx: int, cy: int, colour_of, dec: int = 1) -> Mesh:
        """Terrain of one chunk. ``colour_of(points (m, 2)) -> (m, 3) uint8`` colours the faces."""
        ok, sl = self._chunk_cells(cx, cy, dec)
        if sl is None or not ok.any():
            return Mesh.empty()
        h = self.heights[sl]
        step = self.step * dec
        x0, y0 = cx * self.chunk_m, cy * self.chunk_m
        cj, ci = np.nonzero(ok)
        px, py = x0 + ci * step, y0 + cj * step
        h00, h10, h01, h11 = h[cj, ci], h[cj, ci + 1], h[cj + 1, ci], h[cj + 1, ci + 1]
        diag1 = np.abs(h10 - h01) < np.abs(h00 - h11)
        v00 = np.stack([px, py, h00], 1)
        v10 = np.stack([px + step, py, h10], 1)
        v01 = np.stack([px, py + step, h01], 1)
        v11 = np.stack([px + step, py + step, h11], 1)
        d = diag1[:, None, None]
        first = np.where(d, np.stack([v00, v10, v01], 1), np.stack([v00, v10, v11], 1))
        second = np.where(d, np.stack([v10, v11, v01], 1), np.stack([v00, v11, v01], 1))
        tris = np.empty((2 * len(cj), 3, 3))
        tris[0::2], tris[1::2] = first, second
        return Mesh(tris, colour_of(tris.mean(axis=1)[:, :2]))


def cut_holes(mesh: Mesh, holes: list[Polygon]) -> Mesh:
    """``mesh`` (an upward-facing surface, such as terrain) with the ``holes`` cut out: a triangle a hole
    crosses is replaced by the triangles of what is left, on the same plane and with the same colour."""
    if not holes or not mesh.face_count:
        return mesh
    lo = np.min([h.bounds[:2] for h in holes], axis=0)
    hi = np.max([h.bounds[2:] for h in holes], axis=0)
    xy = mesh.tris[:, :, :2]
    near = np.nonzero(
        (xy.max(axis=1) >= lo).all(axis=1) & (xy.min(axis=1) <= hi).all(axis=1)
    )[0]
    keep = np.ones(len(mesh.tris), bool)
    tris, cols = [], []
    for n in near:
        tri = mesh.tris[n]
        poly = Polygon(tri[:, :2])
        if not poly.is_valid or poly.area < 1e-9 or not any(poly.intersects(h) for h in holes):
            continue
        keep[n] = False
        left = poly
        for h in holes:
            left = left.difference(h)
        plane = np.linalg.solve(np.c_[tri[:, :2], np.ones(3)], tri[:, 2])  # z = a x + b y + c
        up = np.cross(tri[1] - tri[0], tri[2] - tri[0])[2] > 0
        for piece in getattr(left, "geoms", [left]):
            if not isinstance(piece, Polygon) or piece.is_empty:
                continue
            for t3 in triangulate(piece):
                v = np.array([(x, y, plane[0] * x + plane[1] * y + plane[2]) for x, y in t3])
                if (np.cross(v[1] - v[0], v[2] - v[0])[2] > 0) != up:
                    v = v[[0, 2, 1]]
                tris.append(v)
                cols.append(mesh.colours[n])
    if not tris:
        return mesh
    return Mesh(
        np.concatenate([mesh.tris[keep], np.array(tris)]),
        np.concatenate([mesh.colours[keep], np.array(cols, mesh.colours.dtype)]),
    )


# --- building the grid ----------------------------------------------------------------------


def dmr_coverage(src: "rasterio.DatasetReader", origin: tuple[float, float]) -> Polygon:
    """Polygon (local metres) between the outermost pixel centres of the raster."""
    r = src.res[0]
    left, bottom = src.bounds.left + r / 2, src.bounds.bottom + r / 2
    right, top = src.bounds.right - r / 2, src.bounds.top - r / 2
    n = 20
    ring = (
        [(left + (right - left) * k / n, bottom) for k in range(n)]
        + [(right, bottom + (top - bottom) * k / n) for k in range(n)]
        + [(right - (right - left) * k / n, top) for k in range(n)]
        + [(left, top - (top - bottom) * k / n) for k in range(n)]
    )
    xs, ys = _from_dmr().transform([p[0] for p in ring], [p[1] for p in ring])
    pts = np.column_stack([np.array(xs) - origin[0], np.array(ys) - origin[1]])
    return Polygon(np.round(pts, 3))


def sample_dmr(
    tif: Path, origin: tuple[float, float], x: np.ndarray, y: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    """Bilinear heights at local points; second result is True where inside the raster."""
    import rasterio

    with rasterio.open(tif) as src:
        data = src.read(1).astype(np.float64)
        nodata = src.nodata
        left, top, res = src.bounds.left, src.bounds.top, src.res[0]
    bad = ~np.isfinite(data) | (np.abs(data) > 1e6)
    if nodata is not None:
        bad |= data == nodata
    fill = float(data[~bad].mean()) if (~bad).any() else 0.0
    data = np.where(bad, fill, data)
    e, n = _to_dmr().transform(x + origin[0], y + origin[1])
    col = (np.asarray(e) - left) / res - 0.5
    row = (top - np.asarray(n)) / res - 0.5
    rows, cols = data.shape
    inside = (col >= 0) & (col <= cols - 1) & (row >= 0) & (row <= rows - 1)
    col, row = np.clip(col, 0, cols - 1 - 1e-9), np.clip(row, 0, rows - 1 - 1e-9)
    c0, r0 = col.astype(int), row.astype(int)
    fc, fr = col - c0, row - r0
    z = (
        data[r0, c0] * (1 - fc) * (1 - fr)
        + data[r0, c0 + 1] * fc * (1 - fr)
        + data[r0 + 1, c0] * (1 - fc) * fr
        + data[r0 + 1, c0 + 1] * fc * fr
    )
    badpix = bad[r0, c0] | bad[r0, c0 + 1] | bad[r0 + 1, c0] | bad[r0 + 1, c0 + 1]
    return z, inside & ~badpix


def grid_from_samples(chunk_m: float, step_m: float, coverage: Polygon, sampler) -> Terrain:
    """Grid spanning the chunks touched by ``coverage``; ``sampler(x, y) -> (z, inside)``."""
    n = cells_per_chunk(chunk_m, step_m)
    minx, miny, maxx, maxy = coverage.bounds
    cx0, cx1 = math.floor(minx / chunk_m), math.floor(maxx / chunk_m)
    cy0, cy1 = math.floor(miny / chunk_m), math.floor(maxy / chunk_m)
    shape = ((cy1 - cy0 + 1) * n + 1, (cx1 - cx0 + 1) * n + 1)
    t = Terrain(chunk_m, n, cx0 * n, cy0 * n, np.zeros(shape), np.zeros(shape, bool), coverage)
    xs, ys = t.vertex_xy()
    z, inside = sampler(xs.ravel(), ys.ravel())
    t.heights = np.round(z.reshape(xs.shape), 3)
    t.valid = inside.reshape(xs.shape)
    return t


def flatten_buildings(
    t: Terrain, buildings: list[dict], margin_m: float, blend_m: float
) -> dict[str, float]:
    """Flatten ground under footprints in place; returns the elevation per building id.

    Buildings are processed in id order. A building's elevation is the mean of the current
    surface over its footprint (sampled on a 1 m lattice). Vertices made flat by an earlier
    building are not touched again.
    """
    xs, ys = t.vertex_xy()
    locked = np.zeros(t.heights.shape, bool)
    elevations: dict[str, float] = {}
    for b in sorted(buildings, key=lambda b: b["id"]):
        poly = footprint_polygon(b["footprint"])
        if not poly.is_valid:
            poly = poly.buffer(0)
        minx, miny, maxx, maxy = poly.bounds
        gx, gy = np.meshgrid(
            np.arange(math.floor(minx), math.ceil(maxx) + 1.0),
            np.arange(math.floor(miny), math.ceil(maxy) + 1.0),
        )
        gx, gy = gx.ravel(), gy.ravel()
        inside = shapely.contains_xy(poly, gx, gy)
        if inside.any():
            z = t.surface(gx[inside], gy[inside])
        else:
            c = poly.representative_point()
            z = t.surface(np.array([c.x]), np.array([c.y]))
        elev = float(round(float(z.mean()), 3))
        elevations[b["id"]] = elev
        _apply_flat(t, xs, ys, locked, poly.buffer(margin_m), elev, blend_m)
    return elevations


def _apply_flat(t, xs, ys, locked, grown, elev: float, blend_m: float) -> None:
    s = t.step
    minx, miny, maxx, maxy = grown.bounds
    ci0 = max(0, math.floor(minx / s) - t.i0 - 1)
    ci1 = min(t.ncols - 1, math.ceil(maxx / s) - t.i0 + 1)
    cj0 = max(0, math.floor(miny / s) - t.j0 - 1)
    cj1 = min(t.nrows - 1, math.ceil(maxy / s) - t.j0 + 1)
    if ci0 > ci1 or cj0 > cj1:
        return
    cj, ci = np.meshgrid(np.arange(cj0, cj1 + 1), np.arange(ci0, ci1 + 1), indexing="ij")
    cj, ci = cj.ravel(), ci.ravel()
    boxes = shapely.box((ci + t.i0) * s, (cj + t.j0) * s, (ci + t.i0 + 1) * s, (cj + t.j0 + 1) * s)
    hit = shapely.intersects(boxes, grown)
    flat = np.zeros(t.heights.shape, bool)
    for dj in (0, 1):
        for di in (0, 1):
            flat[cj[hit] + dj, ci[hit] + di] = True
    pad = math.ceil(blend_m / s) + 1
    bi0, bi1 = max(0, ci0 - pad), min(t.ncols, ci1 + pad + 1)
    bj0, bj1 = max(0, cj0 - pad), min(t.nrows, cj1 + pad + 1)
    sl = (slice(bj0, bj1 + 1), slice(bi0, bi1 + 1))
    px, py = xs[sl], ys[sl]
    d = shapely.distance(shapely.points(px.ravel(), py.ravel()), grown).reshape(px.shape)
    w = np.clip(1 - d / blend_m, 0, 1) if blend_m > 0 else (d == 0).astype(float)
    w = np.where(flat[sl], 1.0, w)
    h = t.heights[sl]
    t.heights[sl] = np.where(~locked[sl] & (w > 0), np.round(h * (1 - w) + elev * w, 3), h)
    locked[sl] |= flat[sl]


# --- files ----------------------------------------------------------------------------------


def save_terrain(work_dir: Path, t: Terrain, elevations: dict[str, float]) -> None:
    np.save(work_dir / "terrain_heights.npy", t.heights)
    np.save(work_dir / "terrain_valid.npy", t.valid)
    doc = {
        "version": TERRAIN_FORMAT_VERSION,
        "chunk_m": t.chunk_m,
        "cells_per_chunk": t.n,
        "step_m": t.step,
        "i0": t.i0,
        "j0": t.j0,
        "shape": list(t.heights.shape),
        "coverage": [[float(x), float(y)] for x, y in t.coverage.exterior.coords],
        "building_elevation": elevations,
    }
    (work_dir / "terrain.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")


def load_terrain(work_dir: Path, mmap: bool = False) -> tuple[Terrain, dict[str, float]]:
    """Read the terrain work files; ``mmap`` memory-maps the arrays (read-only, for small queries)."""
    mode = "r" if mmap else None
    doc = json.loads((work_dir / "terrain.json").read_text())
    t = Terrain(
        doc["chunk_m"],
        doc["cells_per_chunk"],
        doc["i0"],
        doc["j0"],
        np.load(work_dir / "terrain_heights.npy", mmap_mode=mode),
        np.load(work_dir / "terrain_valid.npy", mmap_mode=mode),
        Polygon(doc["coverage"]),
    )
    return t, doc["building_elevation"]


def build_terrain(
    work_dir: Path = DEFAULT_WORK_DIR,
    raw_dir: Path = DEFAULT_RAW_DIR,
    rules_path: Path = DEFAULT_RULES,
) -> tuple[Terrain, dict[str, float]]:
    cfg = load_section("terrain", rules_path)
    chunk_m = float(load_section("chunking", rules_path).get("size_m", 250.0))
    frame = json.loads((work_dir / "frame.json").read_text())
    origin = (frame["origin_epsg5514"][0], frame["origin_epsg5514"][1])
    tif = raw_dir / "dmr5.tif"
    import rasterio

    with rasterio.open(tif) as src:
        coverage = dmr_coverage(src, origin)
    t = grid_from_samples(
        chunk_m, float(cfg["grid_step_m"]), coverage, lambda x, y: sample_dmr(tif, origin, x, y)
    )
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    elevations = flatten_buildings(
        t, buildings, float(cfg["flatten_margin_m"]), float(cfg["flatten_blend_m"])
    )
    return t, elevations


def run(force: bool = False, area: str = DEFAULT_AREA, **_options: object) -> int:
    paths = area_paths(area)
    try:
        t, elevations = build_terrain(paths.work, paths.raw)
    except FileNotFoundError as exc:
        print(f"terrain: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    save_terrain(paths.work, t, elevations)
    print(
        f"terrain: grid {t.ncols}x{t.nrows} cells, step {t.step:.3f} m, "
        f"{int(t.valid.sum())} valid vertices, {len(t.chunks())} chunk(s) with terrain, "
        f"{len(elevations)} building elevations"
    )
    return 0
