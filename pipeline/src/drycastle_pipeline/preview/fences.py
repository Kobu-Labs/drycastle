"""Swatch sheet of the fence types: every type drawn from its real meshes, UVs and library textures.

    uv run python -m drycastle_pipeline.fence_preview [out.png]      # default out/textures/fences.png

A small software renderer (orthographic, z-buffer, back-face culling, nearest texel, alpha test), so
the picture shows what the GLB holds without starting the game: wrong winding shows as holes, wrong
UVs as stretched or flipped patterns. It is a check of the data, not of the game's lighting.
"""

import sys
from itertools import pairwise
from pathlib import Path

import matplotlib.image as mpimg
import numpy as np

from drycastle_pipeline.assets import ASSETS_DIR, DEFAULT_MANIFEST
from drycastle_pipeline.config import REPO_ROOT
from drycastle_pipeline.edits import FENCE_TYPES
from drycastle_pipeline.propmeshes import fence_meshes
from drycastle_pipeline.rules import load_section

DEFAULT_OUT = REPO_ROOT / "out" / "textures" / "fences.png"

# label, fence keys (as in edits.json)
SWATCHES: list[tuple[str, dict]] = [
    ("picket", {"type": "picket"}),
    ("picket, white, brick_red columns", {"type": "picket", "colour": "#f2efe6", "column": "brick_red"}),
    ("wire", {"type": "wire"}),
    ("wire, green, concrete columns", {"type": "wire", "colour": "#3f6b4a", "column": "concrete"}),
    ("brick_red", {"type": "brick_red"}),
    ("brick_white", {"type": "brick_white"}),
    ("stone_wall", {"type": "stone_wall"}),
    ("concrete (default paint)", {"type": "concrete"}),
    ("concrete, ochre", {"type": "concrete", "colour": "#d9b36c"}),
    ("concrete, green, no columns", {"type": "concrete", "colour": "#7fa37a", "column": "none"}),
]

_PX_PER_M = 96
_SS = 2  # supersampling
_BACKGROUND = np.array([0.56, 0.66, 0.50])
_LIGHT = np.array([-0.35, -0.75, 0.56])


def _sections(fence: dict) -> list[dict]:
    """A 9 m run along x that climbs a little, then a 4 m return along y (a corner)."""
    style = {**FENCE_TYPES[fence["type"]], **fence}
    pts = [(0.0, 0.0, 0.0), (3.0, 0.0, 0.0), (6.0, 0.0, 0.25), (9.0, 0.0, 0.5), (9.0, 4.0, 0.5)]
    return [
        {"parcel": "P-0001", "a": [a[0], a[1]], "b": [b[0], b[1]], "za": a[2], "zb": b[2], **style}
        for a, b in pairwise(pts)
    ]


def _camera() -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    forward = np.array([0.42, 0.82, -0.38])
    forward /= np.linalg.norm(forward)
    right = np.cross(forward, [0.0, 0.0, 1.0])
    right /= np.linalg.norm(right)
    return right, np.cross(right, forward), forward


def _draw(img, depth, tris, face_rgb, uvs=None, tex=None) -> None:
    """Rasterise triangles (already in pixels + depth). ``tex`` (h, w, 4) is sampled at ``uvs``."""
    h, w = depth.shape
    for i, t in enumerate(tris):
        x0, x1 = int(max(np.floor(t[:, 0].min()), 0)), int(min(np.ceil(t[:, 0].max()), w - 1))
        y0, y1 = int(max(np.floor(t[:, 1].min()), 0)), int(min(np.ceil(t[:, 1].max()), h - 1))
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = t[0, :2], t[1, :2], t[2, :2]
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            continue
        l0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        l1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        l2 = 1 - l0 - l1
        z = l0 * t[0, 2] + l1 * t[1, 2] + l2 * t[2, 2]
        inside = (l0 >= 0) & (l1 >= 0) & (l2 >= 0) & (z < depth[y0 : y1 + 1, x0 : x1 + 1])
        colour = np.broadcast_to(face_rgb[i], (*z.shape, 3))
        if tex is not None:
            u = l0 * uvs[i, 0, 0] + l1 * uvs[i, 1, 0] + l2 * uvs[i, 2, 0]
            v = l0 * uvs[i, 0, 1] + l1 * uvs[i, 1, 1] + l2 * uvs[i, 2, 1]
            th, tw = tex.shape[:2]
            px = np.floor((u % 1.0) * tw).astype(int) % tw
            py = np.floor(((1.0 - v) % 1.0) * th).astype(int) % th  # v up; image rows go down
            texel = tex[py, px]
            inside &= texel[..., 3] >= 0.5
            colour = colour * texel[..., :3]
        region = img[y0 : y1 + 1, x0 : x1 + 1]
        region[inside] = colour[inside]
        depth[y0 : y1 + 1, x0 : x1 + 1][inside] = z[inside]


def render(fence: dict, images: dict[str, np.ndarray], width_m: float = 12.5, height_m: float = 5.2):
    cfg = load_section("props")["fences"]
    flat, textured = fence_meshes(_sections(fence), cfg)
    right, up, forward = _camera()
    scale = _PX_PER_M * _SS
    w, h = int(width_m * scale), int(height_m * scale)
    img = np.tile(_BACKGROUND, (h, w, 1))
    depth = np.full((h, w), np.inf)
    origin = np.array([4.6, 1.6, 0.9])  # the point at the centre of the picture

    def project(tris):
        p = tris - origin
        out = np.empty_like(p)
        out[..., 0] = p @ right * scale + w / 2
        out[..., 1] = h / 2 - p @ up * scale
        out[..., 2] = p @ forward
        return out

    def shaded(tris, colours):
        n = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
        keep = n @ forward < 0  # facing the camera
        light = 0.55 + 0.45 * np.clip(n @ (_LIGHT / np.linalg.norm(_LIGHT)), 0, 1)
        return keep, (colours / 255.0) * light[:, None]

    if flat.face_count:
        keep, rgb = shaded(flat.tris, flat.colours)
        _draw(img, depth, project(flat.tris[keep]), rgb[keep])
    for tid, mesh in textured.items():
        keep, rgb = shaded(mesh.tris, mesh.colours)
        _draw(img, depth, project(mesh.tris[keep]), rgb[keep], mesh.uvs[keep], images[tid])
    small = img.reshape(h // _SS, _SS, w // _SS, _SS, 3).mean(axis=(1, 3))
    return small, flat.face_count + sum(m.face_count for m in textured.values())


def run(out: Path = DEFAULT_OUT) -> Path:
    import json

    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    doc = json.loads(DEFAULT_MANIFEST.read_text())
    images = {t["id"]: mpimg.imread(ASSETS_DIR / t["file"]) for t in doc["textures"]}
    cols = 2
    rows = (len(SWATCHES) + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(cols * 9.2, rows * 4.0), dpi=110)
    fig.patch.set_facecolor("#22242a")
    for ax, (label, fence) in zip(axes.T.flat, SWATCHES, strict=False):
        picture, triangles = render(fence, images)
        ax.imshow(picture)
        keys = ", ".join(f"{k}: {v}" for k, v in fence.items())
        ax.set_title(f"{label}   ({keys}; {triangles} triangles for 13 m)", color="#e8e8e8", fontsize=10)
        ax.axis("off")
    for ax in axes.T.flat[len(SWATCHES) :]:
        ax.axis("off")
    fig.tight_layout()
    out.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out, facecolor=fig.get_facecolor())
    plt.close(fig)
    return out


if __name__ == "__main__":
    print(run(Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUT))
