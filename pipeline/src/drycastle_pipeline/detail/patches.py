"""Patch triangles: draped meshes, the rows of ``detail_patches.npy``, reading them back and sorting them into chunks."""

import json
import math
from pathlib import Path

import numpy as np

from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.core.textures import TexturedMesh, tex_node_name, texture
from drycastle_pipeline.detail.model import _PATCH_ROW, DetailResult, Patch
from drycastle_pipeline.ground.patches import drape
from drycastle_pipeline.terrain.terrain import Terrain


def patch_rows(flat: Mesh, textured: dict[str, TexturedMesh], textures: list[str]) -> np.ndarray:
    """Patch triangles as rows of :data:`_PATCH_ROW` numbers (the layout of ``detail_patches.npy``)."""
    rows = []
    if flat.face_count:
        r = np.zeros((flat.face_count, _PATCH_ROW))
        r[:, :9] = flat.tris.reshape(-1, 9)
        r[:, 9:12] = flat.colours
        r[:, 18] = -1
        rows.append(r)
    for tid, m in textured.items():
        r = np.zeros((m.face_count, _PATCH_ROW))
        r[:, :9] = m.tris.reshape(-1, 9)
        r[:, 9:12] = m.colours
        r[:, 12:18] = m.uvs.reshape(-1, 6)
        r[:, 18] = textures.index(tid)
        rows.append(r)
    return np.concatenate(rows) if rows else np.zeros((0, _PATCH_ROW))


def patch_meshes(res: DetailResult, terrain: Terrain, lift_m: float, tally: dict | None = None) -> tuple[Mesh, dict[str, TexturedMesh]]:
    """The patches as ground triangles: one flat-colour mesh and one textured mesh per texture id.

    Textured UVs are world coordinates in tile repeats (rotated by the patch's angle), so a texture
    runs on across neighbouring patches and parcels. ``tally`` (a dict) gets the triangle count per layer added.
    """
    flat: list[Mesh] = []
    textured: dict[str, list[TexturedMesh]] = {}
    for p in res.patches:
        if p.texture is None:
            m = drape(p.geom, p.colour or [128, 128, 128], terrain, [], p.lift or lift_m, p.lift or lift_m)
            if m.face_count:
                flat.append(m)
                if tally is not None:
                    tally[p.layer] = tally.get(p.layer, 0) + m.face_count
            continue
        m = drape(p.geom, p.colour or [255, 255, 255], terrain, [], p.lift or lift_m, p.lift or lift_m)  # a textured patch's colour is its tint
        if not m.face_count:
            continue
        if tally is not None:
            tally[p.layer] = tally.get(p.layer, 0) + m.face_count
        tex = texture(p.texture)
        c, s = math.cos(-p.angle), math.sin(-p.angle)
        dx, dy = m.tris[:, :, 0] - p.origin[0], m.tris[:, :, 1] - p.origin[1]
        u, v = (dx * c - dy * s) / tex.tile_w, (dx * s + dy * c) / tex.tile_h
        textured.setdefault(p.texture, []).append(TexturedMesh(m.tris, m.colours, np.stack([u, v], axis=2)))
    return Mesh.concat(flat), {tid: TexturedMesh.concat(ms) for tid, ms in sorted(textured.items())}


def _draped_rows(pending: list[tuple[str, list[Patch]]], drape_patches, terrain: Terrain, lift: float, textures: list[str],
                 by_type: dict[str, dict[str, int]]) -> np.ndarray:
    """The patch rows of all units from one ``drape_patches`` call, in the row order of :func:`patch_meshes` and
    :func:`patch_rows`; adds the triangle count per recipe and layer to ``by_type``."""
    every = [p for _, patches in pending for p in patches]
    draped, counts = drape_patches(every, terrain, lift, textures)
    starts = np.concatenate([[0], np.cumsum(counts)])
    out, at = [], 0
    for kind, patches in pending:
        tally = by_type.setdefault(kind, {})
        idx = range(at, at + len(patches))
        at += len(patches)
        for i, p in zip(idx, patches, strict=True):
            if counts[i]:
                tally[p.layer] = tally.get(p.layer, 0) + counts[i]
        order = [i for i, p in zip(idx, patches, strict=True) if p.texture is None]
        order += sorted((i for i, p in zip(idx, patches, strict=True) if p.texture is not None), key=lambda i: every[i].texture)
        out += [draped[starts[i]:starts[i + 1]] for i in order if counts[i]]
    return np.concatenate(out) if out else np.zeros((0, _PATCH_ROW))


def load_patches(work_dir: Path) -> tuple[np.ndarray, list[str]] | None:
    """The patch rows and texture ids of the work files, or None when the stage has not run."""
    npy, meta = work_dir / "detail_patches.npy", work_dir / "detail_patches.json"
    if not (npy.is_file() and meta.is_file()):
        return None
    return np.load(npy), json.loads(meta.read_text())["textures"]


def patches_by_chunk(rows: np.ndarray, textures: list[str], chunk_m: float, k: float = 1.0) -> dict[tuple[int, int], dict]:
    """``{chunk: {"detail_patches": Mesh, "detail_patches_tex_<id>": TexturedMesh}}`` from the patch rows
    (``k`` scales heights, preview only). A triangle belongs to the chunk of its centroid; terrain cells never
    cross a chunk border, so no patch triangle does."""
    out: dict[tuple[int, int], dict] = {}
    if not len(rows):
        return out
    tris = rows[:, :9].reshape(-1, 3, 3).copy()
    tris[:, :, 2] *= k
    c = tris.mean(axis=1)
    ix, iy = np.floor(c[:, 0] / chunk_m).astype(int), np.floor(c[:, 1] / chunk_m).astype(int)
    tid = rows[:, 18].astype(int)
    # one stable sort by (chunk, texture) instead of a pass over every row per chunk and texture: rows keep their order within a group
    order = np.lexsort((tid, iy, ix))
    sx, sy, st = ix[order], iy[order], tid[order]
    starts = np.concatenate([[0], np.flatnonzero((np.diff(sx) != 0) | (np.diff(sy) != 0) | (np.diff(st) != 0)) + 1, [len(order)]])
    for a, b in zip(starts[:-1].tolist(), starts[1:].tolist(), strict=True):
        sel, t = order[a:b], int(st[a])
        nodes = out.setdefault((int(sx[a]), int(sy[a])), {})
        if t < 0:
            if "detail_patches" in nodes:  # two negative texture indices in one chunk: one flat mesh, rows in their order
                sel = np.sort(np.concatenate([nodes.pop("_flat_rows"), sel]))
            nodes["_flat_rows"] = sel
            nodes["detail_patches"] = Mesh(tris[sel], rows[sel, 9:12].astype(np.uint8))
        elif t < len(textures):
            nodes[tex_node_name("detail_patches", textures[t])] = TexturedMesh(
                tris[sel], rows[sel, 9:12].astype(np.uint8), rows[sel, 12:18].reshape(-1, 3, 2)
            )
    for nodes in out.values():
        nodes.pop("_flat_rows", None)
    return out
