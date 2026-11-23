"""Pictures of the generated graves (``graves.py``), to judge shapes and colours without the game::

    uv run python -m drycastle_pipeline.graves_preview [--out DIR]

writes into ``out/graves`` (by default) ``samples.png`` (the hand-picked sample graves, from the foot),
``samples_back.png`` (from the head side), ``random.png`` (what :func:`graves.spec_for` picks for a run of seeds) and
``neglect.png`` (one grave from kept to abandoned). Drawn with a depth buffer (:func:`render_sheet` here), not the
painter's sort of ``poles_preview``: that one mis-orders small parts lying on big faces (writing, moss, flowers).
"""

import argparse
import math
from dataclasses import replace
from pathlib import Path

import numpy as np

from drycastle_pipeline.config import REPO_ROOT
from drycastle_pipeline.graves import GraveSpec, grave_mesh, spec_for
from drycastle_pipeline.meshes import Mesh

DEFAULT_OUT = REPO_ROOT / "out" / "graves"
FOOT, HEAD = (-68.0, 24.0), (112.0, 24.0)  # where the camera stands: compass direction, elevation

# name, length, width, spec
SAMPLES: list[tuple[str, float, float, GraveSpec]] = [
    ("terrazzo, slab, upright", 2.66, 1.52, GraveSpec(dressing=("lantern", "vase"))),
    ("black granite, plate, step", 2.66, 1.52, GraveSpec(stone="black_granite", head="round", step=True, plaque="plate", dressing=("lantern", "wreath"))),
    ("light frame, dark shouldered stone", 2.66, 1.52, GraveSpec(stone="terrazzo", head="shoulder", head_stone="black_granite", cover="half", dressing=("flowers", "candle"))),
    ("red granite, gable, gravel", 2.66, 1.52, GraveSpec(stone="red_granite", head="gable", cover="gravel", posts=True, dressing=("lantern", "flowers", "vase"))),
    ("double grave, two slabs", 2.6, 2.3, GraveSpec(stone="grey_granite", head="rect", cover="double_slab", head_h=0.95, plaque="plate", dressing=("lantern", "lantern", "vase"))),
    ("desk stone, gravel", 2.3, 1.2, GraveSpec(stone="white_marble", head="desk", head_h=0.4, cover="gravel", plaque="plate", dressing=("candle", "flowers"))),
    ("stone cross, planted bed", 2.5, 1.3, GraveSpec(stone="concrete", head="cross", head_h=1.2, cover="soil", dressing=("flowers", "lantern"))),
    ("urn grave", 1.1, 0.9, GraveSpec(stone="black_granite", head="desk", head_h=0.36, kerb_h=0.16, plaque="plate", dressing=("candle",))),
    ("old iron cross, grass", 2.3, 1.1, GraveSpec(stone="sandstone", head="iron_cross", head_h=1.2, cover="grass", kerb_h=0.15, neglect=0.45)),
    ("old obelisk, half neglected", 2.6, 1.5, GraveSpec(stone="sandstone", head="obelisk", head_h=1.5, cover="soil", kerb_h=0.18, neglect=0.6)),
    ("abandoned, cracked slab", 2.66, 1.52, GraveSpec(stone="terrazzo", head="round", neglect=0.8)),
    ("abandoned, broken stone", 2.66, 1.52, GraveSpec(stone="concrete", head="gable", cover="soil", neglect=0.97)),
]


SKY, GROUND = np.array([224, 231, 240], np.uint8), np.array([127, 154, 98], np.uint8)
LIGHT = np.array([-0.35, -0.55, 0.76])


def _draw(mesh: Mesh, view: tuple[float, float], size_px: tuple[int, int], px_per_m: float, ground: float = 0.68) -> np.ndarray:
    """The mesh seen from compass direction and elevation ``view`` (degrees), orthographic, with a depth buffer; the ground
    z = 0 is drawn as a plane. The origin is in the middle of the picture, ``ground`` of the way down."""
    a, e = math.radians(view[0]), math.radians(view[1])
    f = -np.array([math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)])
    right = np.cross(f, [0, 0, 1.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, f)
    w, h = size_px
    img = np.empty((h, w, 3), np.uint8)
    img[:] = GROUND  # seen from above the horizon the plane fills the picture
    depth = np.full((h, w), np.inf)
    t = mesh.tris
    n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    ln = np.linalg.norm(n, axis=1)
    keep = (ln > 1e-12) & ((n @ f) < 0)
    t, n, cols = t[keep], n[keep] / ln[keep, None], mesh.colours[keep]
    lit = 0.55 + 0.45 * np.clip(n @ (LIGHT / np.linalg.norm(LIGHT)), 0, 1)
    sx = (t @ right) * px_per_m + w / 2
    sy = h * ground - (t @ up) * px_per_m
    sz = t @ f
    for k in range(len(t)):
        x, y, z = sx[k], sy[k], sz[k]
        x0, x1 = max(int(math.floor(x.min())), 0), min(int(math.ceil(x.max())) + 1, w)
        y0, y1 = max(int(math.floor(y.min())), 0), min(int(math.ceil(y.max())) + 1, h)
        if x0 >= x1 or y0 >= y1:
            continue
        den = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2])
        if abs(den) < 1e-12:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        l0 = ((y[1] - y[2]) * (gx - x[2]) + (x[2] - x[1]) * (gy - y[2])) / den
        l1 = ((y[2] - y[0]) * (gx - x[2]) + (x[0] - x[2]) * (gy - y[2])) / den
        l2 = 1 - l0 - l1
        d = l0 * z[0] + l1 * z[1] + l2 * z[2]
        hit = (l0 >= 0) & (l1 >= 0) & (l2 >= 0) & (d < depth[y0:y1, x0:x1])
        depth[y0:y1, x0:x1][hit] = d[hit]
        img[y0:y1, x0:x1][hit] = np.clip(cols[k] * lit[k], 0, 255).astype(np.uint8)
    return img


def render_sheet(cells: list[tuple[str, Mesh]], path: Path, cols: int, cell_m: tuple[float, float], px_per_m: float = 150.0,
                 view: tuple[float, float] = (-68.0, 24.0), title: str = "", ground: float = 0.68) -> None:
    """A grid of meshes at one scale, each in a cell of ``cell_m`` (width, height) metres, its label under it."""
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    ss = 2  # drawn twice the size and averaged down: smooth edges
    cw, ch = int(cell_m[0] * px_per_m), int(cell_m[1] * px_per_m)
    rows = math.ceil(len(cells) / cols)
    sheet = np.empty((rows * ch, cols * cw, 3), np.uint8)
    sheet[:] = GROUND
    for i, (_, mesh) in enumerate(cells):
        big = _draw(mesh, view, (cw * ss, ch * ss), px_per_m * ss, ground).astype(np.float32)
        r, c = divmod(i, cols)
        sheet[r * ch:(r + 1) * ch, c * cw:(c + 1) * cw] = big.reshape(ch, ss, cw, ss, 3).mean(axis=(1, 3)).astype(np.uint8)
    fig = plt.figure(figsize=(sheet.shape[1] / 100, sheet.shape[0] / 100), dpi=100)
    ax = fig.add_axes((0, 0, 1, 1))
    ax.imshow(sheet)
    ax.axis("off")
    for i, (label, _) in enumerate(cells):
        r, c = divmod(i, cols)
        ax.text((c + 0.5) * cw, (r + 1) * ch - 10, label, ha="center", va="bottom", fontsize=9, color="#10140c")
    if title:
        ax.text(12, 10, title, ha="left", va="top", fontsize=11, color="#10140c")
    fig.savefig(path)
    plt.close(fig)


def sample_cells() -> list[tuple[str, Mesh]]:
    return [(f"{i + 1}. {name}", grave_mesh(length, width, spec, f"sample-{i}")) for i, (name, length, width, spec) in enumerate(SAMPLES)]


def random_cells(count: int = 24) -> list[tuple[str, Mesh]]:
    cells = []
    for i in range(count):
        length, width = (2.66, 1.52) if i % 5 else (2.5, 2.2)
        spec = spec_for(f"o-{4673 + i}", length, width)
        cells.append((f"{spec.stone}, {spec.head}, {spec.cover}, neglect {spec.neglect:.1f}", grave_mesh(length, width, spec, f"o-{4673 + i}")))
    return cells


def neglect_cells() -> list[tuple[str, Mesh]]:
    spec = GraveSpec(stone="terrazzo", head="round", cover="half", dressing=("lantern", "flowers", "vase"))
    return [(f"neglect {n:.1f}", grave_mesh(2.66, 1.52, replace(spec, neglect=n), "neglect")) for n in (0.0, 0.2, 0.4, 0.6, 0.8, 1.0)]


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Pictures of the generated graves.")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    a = ap.parse_args(argv)
    a.out.mkdir(parents=True, exist_ok=True)
    render_sheet(sample_cells(), a.out / "samples.png", 4, (3.4, 2.6), px_per_m=150.0, view=FOOT, title="Sample graves, from the foot")
    render_sheet(sample_cells(), a.out / "samples_back.png", 4, (3.4, 2.6), px_per_m=150.0, view=HEAD, title="Sample graves, from the head side")
    render_sheet(random_cells(), a.out / "random.png", 6, (3.4, 2.6), px_per_m=110.0, view=FOOT, title="What spec_for picks for 24 seeds")
    render_sheet(neglect_cells(), a.out / "neglect.png", 6, (3.4, 2.6), px_per_m=150.0, view=FOOT, title="One grave, kept to abandoned")
    from drycastle_pipeline.graves import crucifix_mesh

    cross = [("from the front", crucifix_mesh())]
    render_sheet(cross, a.out / "crucifix.png", 1, (4.6, 7.4), px_per_m=130.0, view=(-78.0, 12.0), title="The graveyard cross (prim:crucifix)", ground=0.86)
    render_sheet([("from the side", crucifix_mesh())], a.out / "crucifix_side.png", 1, (4.6, 7.4), px_per_m=130.0, view=(-20.0, 12.0), ground=0.86)
    for f in sorted(a.out.iterdir()):
        print(f)


if __name__ == "__main__":
    main()
