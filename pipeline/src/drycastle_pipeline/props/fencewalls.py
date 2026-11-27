"""Fence walls as house-destruction objects (docs/fence-destruction.md, Contract 2): the nodes and the voxel grid of a wall section.

A wall section (a type that is a brick, stone or concrete wall) is not in the props meshes. It is, in the chunk of its midpoint:
``house_fence_<index>_visual`` (its flat parts: caps, flat posts), ``house_fence_<index>_visual_tex_<texture>`` (its textured
faces) and ``house_fence_<index>_collision`` (closed boxes of the wall and the piers it owns); and a grid ``fence_<index>`` in
house_voxels.bin.

The grid is in the section's frame: +x from a to b, +y to its left, z up; 0.25 m cells; the corner is the min corner. Rows are
centred on the wall's middle line, so a wall thinner than a cell is exactly one cell thick. A cell is solid when its centre is in
the wall box, in a cap, or in a pier the section owns (a pier at a joint of connected walls belongs to the lowest index). Under
the ground line (za to zb along x) and in the lowest layer it is foundation, and every column is filled from the lowest layer up
to its first solid cell, so nothing hangs. Materials: brick (red and white), stone, concrete; a wood or metal post is the wall's.
Colours: the texture's mean colour under the tint, a cap its own colour. Same input, same bytes.
"""

import math

import numpy as np

from drycastle_pipeline.buildings import voxels
from drycastle_pipeline.buildings.voxels import HouseGrid
from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.core.textures import TexturedMesh, tex_node_name, texture
from drycastle_pipeline.props.fencestyles import (
    CAP_HEIGHT_M,
    CAP_OVERHANG_M,
    SINK_M,
    WallPart,
    WallSpec,
)
from drycastle_pipeline.props.propmeshes import box_tris

_MATERIAL = {"brick_red": voxels.BRICK, "brick_white": voxels.BRICK, "stone": voxels.STONE, "concrete": voxels.CONCRETE}
_EPS = 1e-9


def node_base(index: int) -> str:
    return f"house_fence_{index:05d}"


def grid_id(index: int) -> str:
    return f"fence_{index:05d}"


def wall_collision(w: WallSpec) -> Mesh:
    """Closed boxes of the wall and of its piers."""
    a, b = np.asarray(w.a, float), np.asarray(w.b, float)
    tris = box_tris(a, b, w.r.thick, min(w.za, w.zb) - SINK_M, max(w.za, w.zb) + w.r.height, True)
    for p in w.piers:
        half = p.d * p.col.width_m / 2
        c = np.array([p.x, p.y])
        tris += box_tris(c - half, c + half, p.col.width_m, p.z0, p.z1, True)
    return Mesh(np.array(tris, float), np.tile(np.array(w.r.tint, np.uint8), (len(tris), 1)))


def wall_nodes(wp: WallPart) -> dict[str, Mesh | TexturedMesh]:
    """The nodes of one wall section (empty ones left out). Its collision is always there: a wall is an object of house destruction
    (which needs it), whatever the rules' props.fences.collision says about thin fences."""
    base = node_base(wp.spec.index)
    out: dict[str, Mesh | TexturedMesh] = {}
    if wp.flat.face_count:
        out[f"{base}_visual"] = wp.flat
    for tid, mesh in wp.textured.items():
        out[tex_node_name(f"{base}_visual", tid)] = mesh
    out[f"{base}_collision"] = wall_collision(wp.spec)
    return out


def _span(lo: float, hi: float, shift: float) -> tuple[int, int]:
    """The first and last cell index (an empty range: first > last) whose centre, (i + shift) cells, is in [lo, hi]."""
    cell = voxels.CELL_M
    return math.ceil(lo / cell - shift - _EPS), math.floor(hi / cell - shift + _EPS)


def wall_grid(w: WallSpec) -> HouseGrid:
    """The voxel grid of one wall section (see the module docstring)."""
    cell = voxels.CELL_M
    (ax, ay), (bx, by) = w.a, w.b
    length = math.hypot(bx - ax, by - ay)
    yaw = voxels._f32(math.atan2(by - ay, bx - ax))
    ca, sa = math.cos(yaw), math.sin(yaw)
    r = w.r
    material = _MATERIAL[r.style.texture]
    colour = tuple(texture(r.style.texture).flat(r.tint))
    z_lo, z_hi = min(w.za, w.zb) - SINK_M, w.top
    half = r.thick / 2
    # boxes (u0, u1, v0, v1, z0, z1, material, colour) in the frame; later ones win where they overlap
    boxes = [(0.0, length, -half, half, z_lo, z_hi, material, colour)]
    if w.cap:
        grow = half + CAP_OVERHANG_M
        boxes.append((0.0, length, -grow, grow, z_hi, z_hi + CAP_HEIGHT_M, material, w.cap))
    for p in w.piers:
        pu, pv = (p.x - ax) * ca + (p.y - ay) * sa, -(p.x - ax) * sa + (p.y - ay) * ca
        h = p.col.width_m / 2
        pm = _MATERIAL.get(p.col.texture, material)
        pc = tuple(texture(p.col.texture).flat(p.tint)) if p.col.texture else tuple(p.tint)
        boxes.append((pu - h, pu + h, pv - h, pv + h, p.z0, p.z1, pm, pc))
        if p.col.cap:
            g = h + CAP_OVERHANG_M
            boxes.append((pu - g, pu + g, pv - h - CAP_OVERHANG_M, pv + h + CAP_OVERHANG_M, p.z1, p.z1 + CAP_HEIGHT_M, pm, tuple(hex_to_rgb(p.col.cap))))
    # the cells whose centres some box holds: columns i (centre (i + 0.5) cell from a), rows j (centre j cell from the middle line), layers k
    held = []
    for b in boxes:
        spans = (_span(b[0], b[1], 0.5), _span(b[2], b[3], 0.0), _span(b[4], b[5], 0.5))
        if all(lo <= hi for lo, hi in spans):
            held.append(spans)
    i0, i1 = min(h[0][0] for h in held), max(h[0][1] for h in held)
    rows = max(max(abs(h[1][0]), h[1][1]) for h in held)
    k0, k1 = min(h[2][0] for h in held), max(h[2][1] for h in held)
    nx, ny, nz = i1 - i0 + 1, 2 * rows + 1, k1 - k0 + 1
    x0, y0, oz = i0 * cell, -(rows + 0.5) * cell, k0 * cell
    cu, cv, cz = x0 + (np.arange(nx) + 0.5) * cell, y0 + (np.arange(ny) + 0.5) * cell, oz + (np.arange(nz) + 0.5) * cell

    mat = np.zeros((nz, ny, nx), np.uint8)
    col = np.zeros((nz, ny, nx, 3), np.uint8)
    for u0, u1, v0, v1, z0, z1, m, c in boxes:
        inside = (((cz >= z0 - _EPS) & (cz <= z1 + _EPS))[:, None, None] & ((cv >= v0 - _EPS) & (cv <= v1 + _EPS))[None, :, None]
                  & ((cu >= u0 - _EPS) & (cu <= u1 + _EPS))[None, None, :])
        mat[inside], col[inside] = m, c
    ground = w.za + (w.zb - w.za) * np.clip(cu / length, 0.0, 1.0)
    mat[(mat != voxels.AIR) & (cz[:, None, None] < ground[None, None, :])] = voxels.FOUNDATION
    solid = mat != voxels.AIR
    column, first = solid.any(axis=0), solid.argmax(axis=0)
    fill = column[None] & (np.arange(nz)[:, None, None] <= first[None]) & ~solid  # nothing hangs over an empty cell
    col[fill] = np.broadcast_to(np.take_along_axis(col, first[None, :, :, None], axis=0), col.shape)[fill]
    mat[fill] = voxels.FOUNDATION
    mat[0][column] = voxels.FOUNDATION  # the lowest layer is the root
    palette, inv = voxels.palette_of(mat, col)
    origin = (voxels._f32(ax + x0 * ca - y0 * sa), voxels._f32(ay + x0 * sa + y0 * ca), voxels._f32(oz))
    return HouseGrid(grid_id(w.index), origin, yaw, voxels._f32(cell), palette, inv.reshape(nz, ny, nx).astype(np.uint8))
