"""Numpy mesh facts on world-space triangle arrays shaped (T, 3, 3)."""

from __future__ import annotations

import numpy as np

AXES = "xyz"
FACE_GRID = 64  # cells per side when rasterising a box face
SYM_GRID = 16  # cells per axis for the mirror test
SYM_SAMPLES = 20000
MIN_EXTENT = 1e-4  # metres; below this an axis has no extent worth testing


def bounds(tris: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    v = tris.reshape(-1, 3)
    return v.min(axis=0), v.max(axis=0)


def face_tolerance(size: float) -> float:
    """How far from a box face a vertex may sit and still count as lying on it."""
    return min(max(0.005, 0.01 * size), 0.25 * size)


def flat_coverage(tris: np.ndarray, lo: np.ndarray, hi: np.ndarray) -> dict[str, float | None]:
    """Per box face ("-x", "+x", "-y", ...): share of the face area covered by triangles lying on it.

    None when the face has no area (the box is flat in the other axes), so nothing is claimed.
    """
    size = hi - lo
    out: dict[str, float | None] = {}
    for a in range(3):
        u, v = [i for i in range(3) if i != a]
        for sign, plane in (("-", lo[a]), ("+", hi[a])):
            name = f"{sign}{AXES[a]}"
            if size[u] < MIN_EXTENT or size[v] < MIN_EXTENT or size[a] < MIN_EXTENT:
                out[name] = None
                continue
            on = np.all(np.abs(tris[:, :, a] - plane) <= face_tolerance(size[a]), axis=1)
            flat = (tris[on][:, :, [u, v]] - lo[[u, v]]) / size[[u, v]]
            out[name] = round(_covered_fraction(flat), 3)
    return out


def _covered_fraction(tris2: np.ndarray) -> float:
    """Fraction of the unit square covered by 2D triangles (T, 3, 2), by cell-centre sampling."""
    if len(tris2) == 0:
        return 0.0
    g = FACE_GRID
    c = (np.arange(g) + 0.5) / g
    cells = np.stack(np.meshgrid(c, c, indexing="ij"), axis=-1).reshape(-1, 2)  # (g*g, 2)
    covered = np.zeros(len(cells), dtype=bool)
    step = max(1, 4_000_000 // len(cells))
    for i in range(0, len(tris2), step):
        covered |= _inside_any(tris2[i : i + step], cells)
        if covered.all():
            break
    return float(covered.mean())


def _inside_any(t: np.ndarray, pts: np.ndarray) -> np.ndarray:
    a, b, c = t[:, 0:1], t[:, 1:2], t[:, 2:3]  # (K, 1, 2)
    p = pts[None]  # (1, N, 2)

    def cross(o: np.ndarray, d: np.ndarray) -> np.ndarray:
        return (d[..., 0] - o[..., 0]) * (p[..., 1] - o[..., 1]) - (d[..., 1] - o[..., 1]) * (
            p[..., 0] - o[..., 0]
        )

    d1, d2, d3 = cross(a, b), cross(b, c), cross(c, a)
    neg = (d1 < 0) | (d2 < 0) | (d3 < 0)
    pos = (d1 > 0) | (d2 > 0) | (d3 > 0)
    return (~(neg & pos)).any(axis=0)


def surface_samples(tris: np.ndarray, n: int, seed: int = 0) -> np.ndarray:
    """n points spread over the surface, area-weighted, deterministic."""
    e1, e2 = tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]
    area = np.linalg.norm(np.cross(e1, e2), axis=1)
    total = area.sum()
    if total <= 0:
        return tris[:, 0]
    rng = np.random.default_rng(seed)
    pick = rng.choice(len(tris), size=n, p=area / total)
    r1, r2 = rng.random(n), rng.random(n)
    flip = r1 + r2 > 1
    r1, r2 = np.where(flip, 1 - r1, r1), np.where(flip, 1 - r2, r2)
    return tris[pick, 0] + r1[:, None] * e1[pick] + r2[:, None] * e2[pick]


def mirror_symmetry(
    tris: np.ndarray, lo: np.ndarray, hi: np.ndarray
) -> tuple[float | None, float | None]:
    """Overlap (0..1) of the surface with its mirror image about the box mid-plane, for x and z.

    Occupied-cell IoU on a coarse grid of surface samples: 1.0 is a perfect mirror, a lopsided
    object scores well under 0.8. None when the axis has no extent.
    """
    size = hi - lo
    pts = surface_samples(tris, SYM_SAMPLES)
    g = SYM_GRID
    safe = np.where(size < MIN_EXTENT, 1.0, size)
    idx = np.clip(((pts - lo) / safe * g).astype(int), 0, g - 1)
    occ = np.zeros((g, g, g), dtype=bool)
    occ[idx[:, 0], idx[:, 1], idx[:, 2]] = True
    res: list[float | None] = []
    for a in (0, 2):
        if size[a] < MIN_EXTENT:
            res.append(None)
            continue
        flipped = np.flip(occ, axis=a)
        res.append(round(float((occ & flipped).sum() / (occ | flipped).sum()), 3))
    return res[0], res[1]
