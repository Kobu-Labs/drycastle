"""Turn glTF primitives + resolved surfaces into GPU-ready batches (one per surface)."""

from __future__ import annotations

import math
from collections import defaultdict
from dataclasses import dataclass, field

import numpy as np

from ..model import Asset
from . import surfaces
from .gltf import Doc, Prim, load_prims
from .surfaces import Resolved, Surface


@dataclass
class Batch:
    surface: Surface
    verts: np.ndarray  # (V, 12) float32: pos 3, normal 3, uv 2, colour 4


@dataclass
class Scene:
    batches: list[Batch]
    resolved: Resolved
    lo: np.ndarray
    hi: np.ndarray
    triangles: int
    placeholder_triangles: int
    skinned: bool
    notes: list[str] = field(default_factory=list)

    @property
    def points(self) -> np.ndarray:
        return np.concatenate([b.verts[:, :3] for b in self.batches])


def _face_normals(p: np.ndarray) -> np.ndarray:
    n = np.cross(p[1::3] - p[0::3], p[2::3] - p[0::3])
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    n = np.where(ln > 1e-20, n / np.where(ln > 1e-20, ln, 1.0), np.array([0.0, 1.0, 0.0]))
    return np.repeat(n, 3, axis=0).astype(np.float32)


def _uv(uv: np.ndarray, xf: tuple[float, float, float, float, float]) -> np.ndarray:
    sx, sy, ox, oy, rot = xf
    u, v = uv[:, 0] * sx, uv[:, 1] * sy
    if rot:
        c, s = math.cos(rot), math.sin(rot)
        u, v = c * u + s * v, -s * u + c * v
    return np.stack([u + ox, v + oy], 1).astype(np.float32)


def _surface(prim: Prim, res: Resolved) -> Surface:
    s = res.by_material.get(prim.material) if prim.material is not None else None
    if s is None or s.placeholder:
        if prim.color is not None:
            return Surface(None, (1.0, 1.0, 1.0, 1.0), vertex_color=True)
        return s or res.fallback
    if s.tex and s.uv_set not in prim.uvs:
        return res.placeholder_surface()
    return s


def _expand(prim: Prim, s: Surface) -> np.ndarray:
    t = prim.tris
    p = prim.pos[t].reshape(-1, 3)
    if prim.normals is not None and not prim.skinned:
        n = prim.normals[t].reshape(-1, 3)
    else:
        n = _face_normals(p)
    uv = (
        _uv(prim.uvs[s.uv_set][t].reshape(-1, 2), s.uv_xform)
        if s.tex
        else np.zeros((len(p), 2), np.float32)
    )
    if s.vertex_color and prim.color is not None:
        col = prim.color[t].reshape(-1, 4)
    else:
        col = np.ones((len(p), 4), np.float32)
    return np.hstack([p, n, uv, col]).astype(np.float32)


def build_scene(asset: Asset, doc: Doc) -> Scene:
    prims, skipped = load_prims(doc)
    res = surfaces.resolve(asset, doc.json)
    groups: dict[Surface, list[np.ndarray]] = defaultdict(list)
    for prim in prims:
        s = _surface(prim, res)
        groups[s].append(_expand(prim, s))
    batches = [Batch(s, np.concatenate(v)) for s, v in groups.items()]
    tris = sum(len(b.verts) // 3 for b in batches)
    placeholder = sum(len(b.verts) // 3 for b in batches if b.surface.placeholder)
    if batches:
        pts = np.concatenate([b.verts[:, :3] for b in batches])
        lo, hi = pts.min(0).astype(np.float64), pts.max(0).astype(np.float64)
    else:
        lo = hi = np.zeros(3)
    return Scene(batches, res, lo, hi, tris, placeholder, any(p.skinned for p in prims), skipped)
