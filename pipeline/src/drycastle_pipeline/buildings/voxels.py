"""The hidden voxel grid of every hollow house (docs/house-destruction.md, Contract 1): 0.25 m cells in the house's own
frame, each air or one material (foundation, floor, wall, partition, ceiling, roof) with a colour. The game carves
these cells with explosives; the smooth visual stays and is discarded cell by cell.

How a house becomes cells (all decided at the cell's centre, so the outer wall face stays within half a cell of the
facade):

* the frame is square to the longest wall (grid +x along it), and that wall's line, the floor and the grid corner all
  fall on cell boundaries; the grid covers the whole visual plus one cell of margin;
* walls: the footprint minus its inside (``interior.WALL_M``), and every cell their middle line passes through (so a
  wall thinner than a cell still has one); from the floor slab up to the roof skin (gables included);
* openings that go through (``Inside.through``) are air; closed ones, recesses, windows above the ceiling stay wall;
* floor: one slab layer just under the floor; foundation: one layer under it across the footprint (the support root);
* ceiling: one layer just over the ceiling height; the attic above it is air, closed all round (an attic cell beside the
  outside, as where a high roof steps down to a low one, becomes roof; the export fails on an open attic);
* roof: a skin under the visual roof surface (found by rasterising the up-facing roof triangles), one cell thick and as
  much more as the slope needs to stay face-connected; chimneys fill from the skin up to their tops; a dormer is a shell
  one cell thick, its inside air joined to the attic under it;
* what stands out of the walls and roof (door canopies, steps, balconies, fascia and gutters, downpipes): every cell
  outside the footprint its faces pass through (canopies, eaves and pipes roof; steps and balconies floor, the lowest cell
  of each step column foundation, so steps stand and a balcony falls with its wall);
* partitions of the floor plan: the cells their middle line passes through, from floor to ceiling, open in the gaps
  (a doorway up to its head).

Same input, same bytes. Debug view: ``uv run python -m drycastle_pipeline.buildings.voxels show <bid>``.
"""

import argparse
import math
import struct
import subprocess
import sys
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient

from drycastle_pipeline.buildings import interior
from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

CELL_M = 0.25
FILE_NAME = "house_voxels.bin"
MAGIC, VERSION = b"HVOX", 1
AIR, FOUNDATION, FLOOR, WALL, PARTITION, CEILING, ROOF, BRICK, STONE, CONCRETE = range(10)  # the last three: fence walls (props/fencewalls.py)
MATERIALS = ("air", "foundation", "floor", "wall", "partition", "ceiling", "roof", "brick", "stone", "concrete")
MAX_PALETTE = 64
FOUNDATION_COLOUR = (112, 108, 102)
SKIN_MAX_CELLS = 8  # the roof skin is at most this many cells deep where the roof steps (a fitted roof's step wall)
UP_MIN = 0.05  # a triangle is part of the roof surface when its normal's z is at least this (of a unit normal)


@dataclass
class HouseGrid:
    """One house's cells as in the file: ``cells`` is (nz, ny, nx) palette indices, so its C order is
    i = x + nx * (y + ny * z); ``palette[0]`` is air; origin is the min corner of cell (0, 0, 0), map local metres."""

    id: str
    origin: tuple[float, float, float]
    yaw: float
    cell: float
    palette: list[tuple[int, int, int, int]]  # (material, r, g, b)
    cells: np.ndarray

    @property
    def shape(self) -> tuple[int, int, int]:
        nz, ny, nx = self.cells.shape
        return nx, ny, nz

    def materials(self) -> np.ndarray:
        """The material of every cell, (nz, ny, nx)."""
        return np.array([p[0] for p in self.palette], np.uint8)[self.cells]


# --- the file ------------------------------------------------------------------------------------------------------


def _varint(v: int) -> bytes:
    out = bytearray()
    while v >= 0x80:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    out.append(v)
    return bytes(out)


def _rle(flat: np.ndarray) -> bytes:
    """(u8 palette index, varint run) over the cells in index order."""
    if not len(flat):
        return b""
    starts = np.concatenate([[0], np.flatnonzero(np.diff(flat)) + 1])
    runs = np.diff(np.concatenate([starts, [len(flat)]]))
    return b"".join(bytes([int(flat[s])]) + _varint(int(n)) for s, n in zip(starts, runs, strict=True))


def encode(houses: list[HouseGrid]) -> bytes:
    """``house_voxels.bin``: the houses sorted by id (ordinal; the index is the wire id), little-endian."""
    houses = sorted(houses, key=lambda h: h.id.encode())
    out = [MAGIC, struct.pack("<HH", VERSION, len(houses))]
    for h in houses:
        nx, ny, nz = h.shape
        if not 0 < len(h.palette) <= MAX_PALETTE or h.palette[0][0] != AIR:
            raise ValueError(f"house {h.id}: palette of {len(h.palette)} entries")
        bid = h.id.encode()
        rle = _rle(h.cells.astype(np.uint8).ravel())
        out += [struct.pack("<H", len(bid)), bid, struct.pack("<5f", *h.origin, h.yaw, h.cell), struct.pack("<3HB", nx, ny, nz, len(h.palette)),
                b"".join(struct.pack("<4B", *p) for p in h.palette), struct.pack("<I", len(rle)), rle]  # fmt: skip
    return b"".join(out)


def decode(data: bytes) -> list[HouseGrid]:
    if data[:4] != MAGIC:
        raise ValueError("no HVOX magic")
    version, count = struct.unpack_from("<HH", data, 4)
    if version != VERSION:
        raise ValueError(f"version {version}")
    p, out = 8, []
    for _ in range(count):
        (n,) = struct.unpack_from("<H", data, p)
        bid = data[p + 2 : p + 2 + n].decode()
        p += 2 + n
        ox, oy, oz, yaw, cell = struct.unpack_from("<5f", data, p)
        nx, ny, nz, pc = struct.unpack_from("<3HB", data, p + 20)
        p += 27
        palette = [struct.unpack_from("<4B", data, p + 4 * k) for k in range(pc)]
        p += 4 * pc
        (rb,) = struct.unpack_from("<I", data, p)
        p += 4
        rle, q, vals, runs = data[p : p + rb], 0, [], []
        p += rb
        while q < len(rle):
            vals.append(rle[q])
            q += 1
            v = shift = 0
            while True:
                b = rle[q]
                q += 1
                v |= (b & 0x7F) << shift
                shift += 7
                if b < 0x80:
                    break
            runs.append(v)
        cells = np.repeat(np.array(vals, np.uint8), runs).reshape(nz, ny, nx)
        out.append(HouseGrid(bid, (ox, oy, oz), yaw, cell, [tuple(e) for e in palette], cells))
    if p != len(data):
        raise ValueError(f"{len(data) - p} bytes after the last house")
    return out


def write_file(out_dir: Path, houses: list[HouseGrid], files: dict) -> None:
    """``house_voxels.bin`` and ``files.house_voxels``; no hollow house: no file (a stale one is removed)."""
    path = out_dir / FILE_NAME
    if not houses:
        path.unlink(missing_ok=True)
        return
    path.write_bytes(encode(houses))
    files["house_voxels"] = FILE_NAME


# --- voxelising ----------------------------------------------------------------------------------------------------


def _f32(v: float) -> float:
    return float(np.float32(v))


def palette_of(mat: np.ndarray, colour: np.ndarray) -> tuple[list[tuple[int, int, int, int]], np.ndarray]:
    """The palette (entry 0 = air; sorted: same input, same bytes) and the flat palette index of every cell, from the
    material (nz, ny, nx) and colour (nz, ny, nx, 3) of each. Too many colours (> MAX_PALETTE): each material keeps its first."""
    keys = mat.astype(np.uint32) << 24 | colour[..., 0].astype(np.uint32) << 16 | colour[..., 1].astype(np.uint32) << 8 | colour[..., 2]
    keys[mat == AIR] = 0
    uniq, inv = np.unique(keys, return_inverse=True)
    if len(uniq) + (uniq[0] != 0) > MAX_PALETTE:
        first: dict[int, int] = {}
        for kk in uniq.tolist():
            first.setdefault(kk >> 24, kk)
        keys = np.array([first[kk >> 24] for kk in uniq.tolist()], np.uint32)[inv]
        uniq, inv = np.unique(keys, return_inverse=True)
    palette = [(kk >> 24 & 0xFF, kk >> 16 & 0xFF, kk >> 8 & 0xFF, kk & 0xFF) for kk in uniq.tolist()]
    if palette[0][0] != AIR:
        palette.insert(0, (AIR, 0, 0, 0))
        inv = inv + 1
    return palette, inv


def _tops(tris: np.ndarray, nx: int, ny: int) -> tuple[np.ndarray, np.ndarray]:
    """The highest up-facing triangle over each column centre: its z (-inf: none) and the triangle's index (-1).
    ``tris`` is (n, 3, 3) with x, y in grid units and z in metres."""
    top, which = np.full(nx * ny, -np.inf), np.full(nx * ny, -1)
    if not len(tris):
        return top.reshape(ny, nx), which.reshape(ny, nx)
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    n = np.cross(b - a, c - a)
    up = n[:, 2] > UP_MIN * np.maximum(np.linalg.norm(n, axis=1), 1e-12)
    idx = np.flatnonzero(up)
    a, b, c = a[idx], b[idx], c[idx]
    lo, hi = np.minimum(np.minimum(a, b), c), np.maximum(np.maximum(a, b), c)
    i0 = np.clip(np.ceil(lo[:, 0] - 0.5), 0, nx).astype(np.int64)
    i1 = np.clip(np.floor(hi[:, 0] - 0.5), -1, nx - 1).astype(np.int64)
    j0 = np.clip(np.ceil(lo[:, 1] - 0.5), 0, ny).astype(np.int64)
    j1 = np.clip(np.floor(hi[:, 1] - 0.5), -1, ny - 1).astype(np.int64)
    w, h = np.maximum(i1 - i0 + 1, 0), np.maximum(j1 - j0 + 1, 0)
    counts = w * h
    if not counts.sum():
        return top.reshape(ny, nx), which.reshape(ny, nx)
    t = np.repeat(np.arange(len(idx)), counts)
    k = np.arange(len(t)) - np.repeat(np.cumsum(counts) - counts, counts)
    ii, jj = i0[t] + k % w[t], j0[t] + k // w[t]
    px, py = ii + 0.5, jj + 0.5
    ax, ay, bx, by, cx, cy = a[t, 0], a[t, 1], b[t, 0], b[t, 1], c[t, 0], c[t, 1]
    d = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
    l1 = ((px - ax) * (cy - ay) - (py - ay) * (cx - ax)) / d
    l2 = ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) / d
    l0 = 1.0 - l1 - l2
    ok = (l0 >= -1e-9) & (l1 >= -1e-9) & (l2 >= -1e-9)
    z = (a[t, 2] * l0 + b[t, 2] * l1 + c[t, 2] * l2)[ok]
    col, tri = (jj * nx + ii)[ok], idx[t[ok]]
    order = np.lexsort((tri, z, col))  # per column the highest last, ties by the triangle's index
    col, z, tri = col[order], z[order], tri[order]
    last = np.flatnonzero(np.r_[col[1:] != col[:-1], True]) if len(col) else np.zeros(0, int)
    top[col[last]], which[col[last]] = z[last], tri[last]
    return top.reshape(ny, nx), which.reshape(ny, nx)


def _flood(passable: np.ndarray, start: np.ndarray) -> np.ndarray:
    """The passable cells joined face to face to a start cell. A flood fill by whole runs: each sweep along one axis
    reaches every run of passable cells that has a reached cell, so it takes as many sweeps as a path has turns."""
    solid = passable
    reached, n = start & passable, -1
    while (m := int(reached.sum())) != n:
        n = m
        for axis in range(3):
            s, r = np.moveaxis(solid, axis, -1), np.moveaxis(reached, axis, -1)
            start = s.copy()
            start[..., 1:] &= ~s[..., :-1]
            run = np.where(s, np.cumsum(start.ravel()).reshape(s.shape), 0)
            hit = np.zeros(int(run.max()) + 1, bool)
            hit[run[r]] = True
            hit[0] = False
            reached = np.moveaxis(hit[run], -1, axis)
    return reached


def _supported(mat: np.ndarray) -> np.ndarray:
    """Air, and every solid cell joined face to face to the foundation."""
    solid = mat != AIR
    return _flood(solid, mat == FOUNDATION) | ~solid


def _beside(m: np.ndarray) -> np.ndarray:
    """The cells face to face with a cell of ``m`` (a cell of ``m`` itself only when it is beside another)."""
    out = np.zeros_like(m)
    for axis in range(3):
        lo = [slice(None)] * 3
        hi = [slice(None)] * 3
        lo[axis], hi[axis] = slice(None, -1), slice(1, None)
        out[tuple(lo)] |= m[tuple(hi)]
        out[tuple(hi)] |= m[tuple(lo)]
    return out


def _seal_attic(mat: np.ndarray, attic: np.ndarray) -> None:
    """Closes the attic: each of its air cells face to face with air that is not attic (the outside over a lower roof, a room
    with no ceiling) becomes roof, so the attic is never open to the outside, whatever left the gap (the game draws the
    attic's inside only when it is closed, docs/house-destruction.md)."""
    mat[attic & _beside(~attic & (mat == AIR))] = ROOF


def attic_open(mat: np.ndarray) -> bool:
    """Whether air over a ceiling cell can be reached through air from the grid's edge (an attic open to the outside).
    ``mat`` is (nz, ny, nx) materials."""
    air = mat == AIR
    edge = np.zeros_like(air)
    edge[0], edge[-1], edge[:, 0], edge[:, -1], edge[:, :, 0], edge[:, :, -1] = True, True, True, True, True, True
    outside = _flood(air, edge)
    over_ceiling = np.zeros_like(air)
    over_ceiling[1:] = (mat[:-1] == CEILING) & air[1:]
    return bool((outside & over_ceiling).any())


def _roof_only(d):
    """The detail with nothing on or in its roof and walls: then only the roof deck faces up above the floor."""
    return replace(d, openings=[], dormers=[], chimneys=[], skylights=[], balconies=[], downpipes=[], eaves=None, ridge_caps=False,
                   roof_tiles=False, apron=None, interior=False, bands=[], corners=None, plinth=None)  # fmt: skip


def _line_columns(p, q, to_grid, step: float) -> np.ndarray:
    """The (j, i) of every cell the segment p-q (map metres) passes through, sampled every ``step`` metres."""
    n = max(math.ceil(math.dist(p, q) / step), 1)
    t = np.linspace(0.0, 1.0, n + 1)
    g = to_grid(p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
    return np.unique(np.floor(np.stack([g[1], g[0]], axis=1)).astype(np.int64), axis=0)


def _added(full: Mesh, prev: Mesh) -> Mesh:
    """The triangles of ``full`` that ``prev`` does not have (as a multiset; the same build of the same part gives the same floats)."""
    left: dict[bytes, int] = {}
    for t in prev.tris:
        k = t.tobytes()
        left[k] = left.get(k, 0) + 1
    keep = np.zeros(len(full.tris), bool)
    for n, t in enumerate(full.tris):
        k = t.tobytes()
        if left.get(k, 0):
            left[k] -= 1
        else:
            keep[n] = True
    return Mesh(full.tris[keep], full.colours[keep])


_BARY: dict[int, np.ndarray] = {}


def _surface_cells(tris: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """The (k, j, i) of every cell a triangle passes through (sampled every quarter cell) and the triangle's index.
    ``tris`` is (n, 3, 3) in grid units (x, y, z)."""
    cells, which = [], []
    for n, t in enumerate(tris):
        m = max(1, math.ceil(4 * max(np.linalg.norm(t[1] - t[0]), np.linalg.norm(t[2] - t[0]), np.linalg.norm(t[2] - t[1]))))
        if m not in _BARY:
            a, b = np.meshgrid(np.arange(m + 1), np.arange(m + 1))
            ok = a + b <= m
            _BARY[m] = np.stack([a[ok], b[ok]], axis=1) / m
        w = _BARY[m]
        p = t[0] + w[:, :1] * (t[1] - t[0]) + w[:, 1:] * (t[2] - t[0])
        cells.append(np.floor(p[:, ::-1]).astype(np.int64))
        which.append(np.full(len(p), n))
    if not cells:
        return np.zeros((0, 3), np.int64), np.zeros(0, np.int64)
    return np.concatenate(cells), np.concatenate(which)


def voxelise(bid: str, b: dict, d, ins, visual: Mesh, dz: float = 0.0, plan=None, cell: float = CELL_M) -> HouseGrid:
    """The cells of hollow house ``bid`` (building ``b``, detail ``d``, room ``ins``), from its visual (house local: z 0 at
    its base) standing ``dz`` high on the map. ``plan``: its floor plan (None: ``interior.plan_of``; False: no partitions)."""
    from drycastle_pipeline.buildings import floorplan as fp
    from drycastle_pipeline.buildings.openings import build_detail

    if plan is None:
        plan = interior.plan_of(b, d, ins)
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    ring = list(poly.exterior.coords)[:-1]
    n_edges = len(ring)
    k_long = max(range(n_edges), key=lambda i: math.dist(ring[i], ring[(i + 1) % n_edges]))
    p0, p1 = ring[k_long], ring[(k_long + 1) % n_edges]
    yaw = _f32(math.atan2(p1[1] - p0[1], p1[0] - p0[0]))
    ca, sa = math.cos(yaw), math.sin(yaw)

    # the grid: every vertex of the visual inside, one cell of margin, the longest wall's line and the floor on boundaries
    v = visual.tris.reshape(-1, 3)
    u_all, w_all = v[:, 0] * ca + v[:, 1] * sa, -v[:, 0] * sa + v[:, 1] * ca
    u0, w0 = p0[0] * ca + p0[1] * sa, -p0[0] * sa + p0[1] * ca
    zlo = min(float(v[:, 2].min()), ins.floor - 2 * cell)
    ku = math.ceil((u0 - float(u_all.min())) / cell + 1 - 1e-9)
    kw = math.ceil((w0 - float(w_all.min())) / cell + 1 - 1e-9)
    kz = math.ceil((ins.floor - zlo) / cell + 1 - 1e-9)
    ou, ow, oz = u0 - ku * cell, w0 - kw * cell, ins.floor - kz * cell
    nx = math.ceil((float(u_all.max()) - ou) / cell + 1 - 1e-9)
    ny = math.ceil((float(w_all.max()) - ow) / cell + 1 - 1e-9)
    nz = math.ceil((float(v[:, 2].max()) - oz) / cell + 1 - 1e-9)
    origin = (_f32(ou * ca - ow * sa), _f32(ou * sa + ow * ca), _f32(oz + dz))
    ou, ow = origin[0] * ca + origin[1] * sa, -origin[0] * sa + origin[1] * ca  # from the stored corner, as the game will
    oz = origin[2] - dz

    def to_grid(x, y):
        x, y = np.asarray(x, float), np.asarray(y, float)
        return ((x * ca + y * sa - ou) / cell, (-x * sa + y * ca - ow) / cell)

    def frame_tris(m: Mesh) -> np.ndarray:
        gx, gy = to_grid(m.tris[..., 0], m.tris[..., 1])
        return np.stack([gx, gy, m.tris[..., 2]], axis=-1)

    gi, gj = np.meshgrid(np.arange(nx) + 0.5, np.arange(ny) + 0.5)  # (ny, nx) column centres, grid units
    X = ou * ca - ow * sa + cell * (gi * ca - gj * sa)
    Y = ou * sa + ow * ca + cell * (gi * sa + gj * ca)
    zc = oz + (np.arange(nz) + 0.5) * cell  # layer centres, house local
    Z = zc[:, None, None]
    K = np.arange(nz)[:, None, None]

    # columns: wall, room
    inner = max(_polygons(poly.buffer(-interior.WALL_M, join_style=2)), key=lambda g: g.area, default=Polygon())
    in_poly = shapely.contains_xy(poly, X, Y)
    wall2d = in_poly & ~shapely.contains_xy(inner, X, Y)
    half = interior.WALL_M / 2
    for i in range(n_edges):  # the middle line of every wall: no wall thinner than a cell, none outside the facade
        p, q = ring[i], ring[(i + 1) % n_edges]
        length = math.dist(p, q)
        if length < 1e-6:
            continue
        inx, iny = -(q[1] - p[1]) / length, (q[0] - p[0]) / length  # inward normal (ccw ring)
        cols = _line_columns((p[0] + inx * half, p[1] + iny * half), (q[0] + inx * half, q[1] + iny * half), to_grid, cell / 4)
        cols = cols[(cols[:, 0] >= 0) & (cols[:, 0] < ny) & (cols[:, 1] >= 0) & (cols[:, 1] < nx)]
        wall2d[cols[:, 0], cols[:, 1]] |= in_poly[cols[:, 0], cols[:, 1]]
    room2d = in_poly & ~wall2d

    # the roof surface: the deck of the bare roof, and whatever stands above it on the real one (chimneys, dormers)
    bare, _ = build_detail(b, _roof_only(d))
    roof_top, _ = _tops(frame_tris(bare), nx, ny)
    full_top, full_tri = _tops(frame_tris(visual), nx, ny)
    has_roof = np.isfinite(roof_top) & (roof_top > ins.floor + cell)
    roof_top = np.where(has_roof, roof_top, -np.inf)
    depth = np.full((ny, nx), cell)
    padded = np.pad(np.where(has_roof, roof_top, np.nan), 1, constant_values=np.nan)
    for dj, di in ((0, 1), (2, 1), (1, 0), (1, 2)):  # deep enough to touch the skin of each neighbour face to face
        step = np.abs(padded[dj : dj + ny, di : di + nx] - np.where(has_roof, roof_top, np.nan))
        depth = np.maximum(depth, np.where(np.isfinite(step), cell + step, cell))
    depth = np.where(wall2d, cell, np.minimum(depth, SKIN_MAX_CELLS * cell))  # over a wall the skin is one cell, the wall below holds it
    under = np.where(has_roof, roof_top - depth, -np.inf)  # walls and the ceiling end at the skin's bottom
    extra = has_roof & (full_top > roof_top + cell / 2)

    k_floor = kz - 1  # the floor slab: the layer just under the floor
    mat = np.zeros((nz, ny, nx), np.uint8)
    mat[k_floor - 1][wall2d | room2d] = FOUNDATION
    mat[k_floor][room2d] = FLOOR
    mat[wall2d[None] & (K >= k_floor) & (Z <= under[None])] = WALL
    k_ceil = math.ceil((ins.ceiling - oz) / cell - 0.5 - 1e-9)  # the first layer whose centre is at or over the ceiling
    if 0 <= k_ceil < nz:
        mat[k_ceil][room2d & (zc[k_ceil] <= under)] = CEILING
    roof = has_roof[None] & (Z <= roof_top[None]) & (Z > under[None]) & (mat == AIR) & (K > k_floor)
    roof |= extra[None] & (Z > roof_top[None]) & (Z <= full_top[None])
    mat[roof] = ROOF

    # openings that go through are air in the wall they are in
    for o in d.openings:
        if not ins.through(o) or not 0 <= o.edge < n_edges:
            continue
        p, q = ring[o.edge], ring[(o.edge + 1) % n_edges]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        s = (X - p[0]) * ux + (Y - p[1]) * uy
        dep = (X - p[0]) * -uy + (Y - p[1]) * ux
        cols = wall2d & (s >= o.at_m) & (s <= o.at_m + o.width_m) & (dep >= -cell / 2) & (dep <= interior.WALL_M + cell / 2)
        zs = (zc >= o.sill_m) & (zc <= o.sill_m + o.height_m)
        mat[zs[:, None, None] & cols[None] & (mat == WALL)] = AIR

    # partitions of the plan
    if plan:
        part = np.zeros((nz, ny, nx), bool)
        room_z = (zc > ins.floor) & (zc < ins.ceiling)
        for wl in plan.walls:
            length = math.dist(wl.p, wl.q)
            if length < 0.05:
                continue
            ux, uy = (wl.q[0] - wl.p[0]) / length, (wl.q[1] - wl.p[1]) / length
            for j, i in _line_columns(wl.p, wl.q, to_grid, cell / 4).tolist():
                if not (0 <= j < ny and 0 <= i < nx) or not room2d[j, i]:
                    continue
                s = (X[j, i] - wl.p[0]) * ux + (Y[j, i] - wl.p[1]) * uy
                gap = next((g for g in wl.gaps if g[0] <= s <= g[1]), None)
                if gap is None:
                    zs = room_z
                elif gap[2] in ("door", "arch"):
                    zs = room_z & (zc >= ins.floor + (fp.DOOR_H_M if gap[2] == "door" else fp.ARCH_H_M))
                else:
                    continue
                part[zs, j, i] = True
        mat[part & (mat == AIR)] = PARTITION

    # what stands out of the walls and the roof: the cells its faces pass through, outside the footprint, so it falls with
    # what holds it (a balcony with its wall) instead of floating; steps stand on their own lowest cells (foundation)
    att_colour = np.zeros((nz, ny, nx, 3), np.uint8)
    att = np.zeros((nz, ny, nx), bool)
    no_steps = [replace(o, steps=0, canopy_m=0.0) for o in d.openings]
    base = replace(_roof_only(d), openings=no_steps, interior=d.interior)  # as the visual's walls: a door's steps need its opening
    built = [build_detail(b, base)[0]]
    parts = (  # each built on the one before it: what it adds, in its material
        (replace(base, openings=[replace(o, steps=0) for o in d.openings]), ROOF),  # door canopies
        (replace(base, openings=d.openings), FLOOR),  # steps
        (replace(base, openings=d.openings, balconies=d.balconies), FLOOR),
        (replace(base, openings=d.openings, balconies=d.balconies, eaves=d.eaves, downpipes=d.downpipes), ROOF),  # fascia, gutters, pipes
    )
    prev = base
    for k_part, (dd, m_part) in enumerate(parts):
        built.append(built[-1] if dd == prev else build_detail(b, dd)[0])  # nothing of this part: no build
        prev = dd
        add = _added(built[-1], built[-2])
        if not len(add.tris):
            continue
        t = frame_tris(add)
        t[..., 2] = (t[..., 2] - oz) / cell
        c, which = _surface_cells(t)
        ok = (c[:, 0] >= 0) & (c[:, 0] < nz) & (c[:, 1] >= 0) & (c[:, 1] < ny) & (c[:, 2] >= 0) & (c[:, 2] < nx)
        c, which = c[ok], which[ok]
        ok = ~in_poly[c[:, 1], c[:, 2]] & (mat[c[:, 0], c[:, 1], c[:, 2]] == AIR)
        c, which = c[ok], which[ok]
        mat[c[:, 0], c[:, 1], c[:, 2]] = m_part
        att[c[:, 0], c[:, 1], c[:, 2]] = True
        att_colour[c[:, 0], c[:, 1], c[:, 2]] = add.colours[which]
        if k_part == 1 and len(c):  # steps are solid down to the ground (their faces have no bottom), the ground cell foundation
            top = np.full((ny, nx), -1)
            np.maximum.at(top, (c[:, 1], c[:, 2]), c[:, 0])
            colour_of = np.zeros((ny, nx, 3), np.uint8)
            colour_of[c[:, 1], c[:, 2]] = add.colours[which]
            k_ground = max(0, math.floor(-oz / cell))  # the cell of the house's foot (z 0)
            fill = (K >= k_ground) & (K <= top[None]) & (mat == AIR) & ~in_poly[None]
            mat[fill] = FLOOR
            att[fill] = True
            att_colour[fill] = np.broadcast_to(colour_of[None], att_colour.shape)[fill]
            foot = fill & (K == k_ground)
            mat[foot] = FOUNDATION

    # a dormer is hollow: a shell one cell thick, its inside open to the attic under it (not a solid block on the roof)
    pocket = np.zeros((nz, ny, nx), bool)
    if d.dormers and 0 <= k_ceil < nz:
        dorm_top, _ = _tops(frame_tris(build_detail(b, replace(_roof_only(d), dormers=d.dormers))[0]), nx, ny)
        dcol = has_roof & (dorm_top > roof_top + cell / 2)
        vol = dcol[None] & (Z > roof_top[None]) & (Z <= dorm_top[None]) & (mat == ROOF)
        skin = dcol[None] & (Z <= roof_top[None]) & (Z > under[None]) & (mat == ROOF)
        pocket = vol & ~_beside(~(vol | skin))
        ceiled = mat[k_ceil] == CEILING
        cols = pocket.any(axis=0) & ceiled  # under the pocket, over an attic: the skin opens into it
        pocket |= skin & cols[None]
        mat[pocket] = AIR

    if 0 <= k_ceil < nz:  # the attic: air over the ceiling, under the roof skin (and a dormer's inside)
        ceiled = mat[k_ceil] == CEILING
        _seal_attic(mat, ((mat == AIR) & ceiled[None] & (K > k_ceil) & (Z < roof_top[None])) | pocket)
    mat[~_supported(mat)] = AIR  # a piece the foundation does not hold (a scrap of roof) would fall at the first carve
    if attic_open(mat):
        raise ValueError(f"house {bid}: its attic is open to the outside (house_voxels.bin needs it closed)")

    # colours, then the palette (sorted: same input, same bytes)
    wall_c = tuple(int(c) for c in b["wall_colour"][:3])
    roof_c = tuple(int(c) for c in b["roof_colour"][:3])
    plinth_c = hex_to_rgb(d.plinth.colour) if d.plinth is not None else None
    out2d = np.pad(~in_poly, 1, constant_values=True)  # a wall column beside the outside shows the wall colour, the rest the room's
    face = wall2d & (out2d[:-2, 1:-1] | out2d[2:, 1:-1] | out2d[1:-1, :-2] | out2d[1:-1, 2:])
    colour = np.zeros((nz, ny, nx, 3), np.uint8)
    colour[mat == FOUNDATION] = plinth_c or FOUNDATION_COLOUR
    colour[mat == FLOOR] = interior.FLOOR
    colour[mat == CEILING] = interior.CEILING
    colour[mat == PARTITION] = interior.INNER_WALL
    colour[mat == ROOF] = roof_c
    colour[(mat == WALL) & ~face[None]] = interior.INNER_WALL
    outer = (mat == WALL) & face[None]
    colour[outer] = wall_c
    if d.gable_colour:
        g = d.gable_from_m if d.gable_from_m is not None else float(b["eave_height_m"])
        colour[outer & (Z >= g)] = hex_to_rgb(d.gable_colour)
    if plinth_c is not None:
        colour[outer & (Z < d.plinth.height_m)] = plinth_c
    on_roof = extra[None] & (Z > roof_top[None]) & (mat == ROOF)
    if on_roof.any():  # what stands on the roof, in its own colour
        colour[on_roof] = np.broadcast_to(visual.colours[np.maximum(full_tri, 0)][None], colour.shape)[on_roof]
    kept = att & (mat != AIR)
    colour[kept] = att_colour[kept]  # steps, balconies, gutters: the colour of their faces
    palette, inv = palette_of(mat, colour)
    return HouseGrid(bid, origin, yaw, _f32(cell), palette, inv.reshape(nz, ny, nx).astype(np.uint8))


# --- debug view ----------------------------------------------------------------------------------------------------

_FACES = (  # per direction: the neighbour offset (z, y, x) and the face's corners (x, y, z), anticlockwise from outside
    ((0, 0, 1), ((1, 0, 0), (1, 1, 0), (1, 1, 1), (1, 0, 1)), 0.85),
    ((0, 0, -1), ((0, 0, 0), (0, 0, 1), (0, 1, 1), (0, 1, 0)), 0.85),
    ((0, 1, 0), ((0, 1, 0), (0, 1, 1), (1, 1, 1), (1, 1, 0)), 0.75),
    ((0, -1, 0), ((0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)), 0.75),
    ((1, 0, 0), ((0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)), 1.0),
    ((-1, 0, 0), ((0, 0, 0), (0, 1, 0), (1, 1, 0), (1, 0, 0)), 0.6),
)


def write_obj(g: HouseGrid, path: Path) -> int:
    """Every face of a solid cell that touches air, in its palette colour (vertex colours, lightly shaded by direction),
    in the grid's frame in metres and Y up (x along the grid, y up, z = -grid y). Returns the face count."""
    solid = g.cells != 0
    pad = np.pad(solid, 1)
    nz, ny, nx = solid.shape
    rgb = np.array([p[1:] for p in g.palette], float) / 255.0
    verts, faces, n = [], [], 0
    for (dz_, dy_, dx_), corners, shade in _FACES:
        nb = pad[1 + dz_ : 1 + dz_ + nz, 1 + dy_ : 1 + dy_ + ny, 1 + dx_ : 1 + dx_ + nx]
        k, j, i = np.nonzero(solid & ~nb)
        if not len(k):
            continue
        col = rgb[g.cells[k, j, i]] * shade
        for cx, cy, cz in corners:
            x, y, z = (i + cx) * g.cell, (j + cy) * g.cell, (k + cz) * g.cell
            verts.append(np.stack([x, z, -y, *col.T], axis=1))
        m = len(k)
        base = n + np.arange(m)
        faces.append(np.stack([base + c * m for c in range(4)], axis=1) + 1)
        n += 4 * m
    with path.open("w") as f:
        f.write(f"# {g.id}: {g.shape[0]}x{g.shape[1]}x{g.shape[2]} cells of {g.cell} m (buildings/voxels.py)\n")
        for block in verts:
            np.savetxt(f, block, fmt="v %.3f %.3f %.3f %.3f %.3f %.3f")
        for block in faces:
            np.savetxt(f, block, fmt="f %d %d %d %d")
    return sum(len(b) for b in faces)


def write_layers(g: HouseGrid, folder: Path, px: int = 10) -> list[Path]:
    """One PNG per z layer (bottom first): each cell in its palette colour, air dark, a line every metre; +y up."""
    import cv2

    folder.mkdir(parents=True, exist_ok=True)
    for old in folder.glob("z*.png"):
        old.unlink()
    nz = g.cells.shape[0]
    rgb = np.array([(40, 40, 44)] + [p[1:] for p in g.palette[1:]], np.uint8)
    mats = g.materials()
    out = []
    per_m = round(1.0 / g.cell)
    for k in range(nz):
        img = np.repeat(np.repeat(rgb[g.cells[k][::-1]], px, axis=0), px, axis=1)
        img[px - 1 :: px, :] = img[px - 1 :: px, :] * 0.8
        img[:, px - 1 :: px] = img[:, px - 1 :: px] * 0.8
        img[:: px * per_m, :] = (90, 90, 96)
        img[:, :: px * per_m] = (90, 90, 96)
        head = np.full((28, img.shape[1], 3), 255, np.uint8)
        counts = np.bincount(mats[k].ravel(), minlength=len(MATERIALS))
        label = f"z {k}  {g.origin[2] + (k + 0.5) * g.cell:.2f} m  " + " ".join(f"{MATERIALS[m]} {c}" for m, c in enumerate(counts) if m and c)
        canvas = np.concatenate([head, img])
        cv2.putText(canvas, label, (4, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 0, 0), 1, cv2.LINE_AA)
        path = folder / f"z{k:03d}.png"
        cv2.imwrite(str(path), canvas[..., ::-1])
        out.append(path)
    return out


def _from_work(bid: str) -> HouseGrid:
    """House ``bid`` voxelised from the work files (standing at z 0), when the map has no grid of it."""
    import json

    from drycastle_pipeline.buildings.openings import build_detail, load_details
    from drycastle_pipeline.core.config import map_paths

    buildings = {b["id"]: b for b in json.loads((map_paths().work / "buildings.json").read_text())["buildings"]}
    if bid not in buildings:
        raise SystemExit(f"voxels: no building {bid}")
    b, d = buildings[bid], load_details().get(bid)
    ins = interior.inside_of(b, d) if d is not None and not b.get("model") else None
    if ins is None:
        raise SystemExit(f"voxels: {bid} is not a hollow house")
    return voxelise(bid, b, d, ins, build_detail(b, d)[0])


def main(argv: list[str] | None = None) -> int:
    from drycastle_pipeline.core.config import map_paths

    paths = map_paths()
    ap = argparse.ArgumentParser(prog="python -m drycastle_pipeline.buildings.voxels")
    sub = ap.add_subparsers(dest="cmd", required=True)
    show = sub.add_parser("show", help="one house's cells as a coloured .obj and one PNG per z layer")
    show.add_argument("bid")
    show.add_argument("--map", type=Path, default=paths.out, help="map folder with house_voxels.bin (default: the village map)")
    show.add_argument("--out", type=Path, default=paths.out.parent / "voxels")
    show.add_argument("--open", action="store_true", help="open the folder with xdg-open")
    sub.add_parser("list", help="the houses of the map's house_voxels.bin").add_argument("--map", type=Path, default=paths.out)
    a = ap.parse_args(argv)
    path = a.map / FILE_NAME
    houses = {h.id: h for h in decode(path.read_bytes())} if path.is_file() else {}
    if a.cmd == "list":
        for h in houses.values():
            print(h.id, "x".join(map(str, h.shape)), int((h.cells != 0).sum()), "solid")
        return 0
    g = houses.get(a.bid)
    if g is None:
        print(f"voxels: {a.bid} not in {path}; voxelised from the work files", file=sys.stderr)
        g = _from_work(a.bid)
    a.out.mkdir(parents=True, exist_ok=True)
    obj = a.out / f"{a.bid}.obj"
    faces = write_obj(g, obj)
    layers = write_layers(g, a.out / a.bid)
    print(f"voxels: {obj} ({faces} faces), {len(layers)} layers in {a.out / a.bid}/")
    if a.open:
        subprocess.Popen(["xdg-open", str(a.out / a.bid)])
    return 0


if __name__ == "__main__":
    sys.exit(main())
