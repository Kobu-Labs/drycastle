"""Pictures of the utility poles (matplotlib), for looking at the generator's work without the game.

    uv run python -m suchohrad_pipeline.poles_preview [--out DIR] [--glb] [--random N]
    uv run python -m suchohrad_pipeline.poles_preview --sync-manifest

writes into ``out/poles`` (by default):

* ``variants.png``: every placeable pole (``poles.VARIANTS``) side by side, at one scale;
* ``random.png``: ``N`` poles from ``random_spec(seed)`` for seeds 1..N (default 21);
* ``row.png``: a few poles in a row with the wires strung between them (``span_mesh``);
* ``parts.png``: every part of ``poleparts.PARTS`` on its own, each scaled to fit its cell;
* with ``--glb``: ``<variant>.glb`` of every placeable pole, as the map export writes them.

``--sync-manifest`` instead rewrites the ``prim:pole_*`` entries of ``assets/manifest.json`` from
``poles.VARIANTS`` (run it after changing the variants or anything that changes their size).

The drawing is a plain painter's sort of the triangles with a fixed light: good enough to judge
shapes and colours, not a render of the game.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np

from suchohrad_pipeline import poleparts as pp
from suchohrad_pipeline.assets import DEFAULT_MANIFEST
from suchohrad_pipeline.config import REPO_ROOT
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.poles import (
    ARCHETYPES,
    VARIANTS,
    build_pole,
    build_variant,
    random_spec,
    span_mesh,
)

DEFAULT_OUT = REPO_ROOT / "out" / "poles"
SKY, GROUND = "#dfe8f0", "#93a37a"
_LIGHT = np.array([0.45, -0.5, 0.74]) / np.linalg.norm([0.45, -0.5, 0.74])
SOURCE = ("https://github.com/Kobu-Labs/drycastle (generated utility pole, "
          "pipeline/src/suchohrad_pipeline/poles.py)")


def _project(mesh: Mesh, az_deg: float, el_deg: float):
    """Screen polygons (n, 3, 2), their depth order (far first) and shaded colours of a mesh seen
    from compass direction ``az_deg``, ``el_deg`` above the horizon."""
    a, e = math.radians(az_deg), math.radians(el_deg)
    f = -np.array([math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)])
    right = np.cross(f, pp.UP)
    right /= np.linalg.norm(right)
    up = np.cross(right, f)
    t = mesh.tris
    n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    ln = np.linalg.norm(n, axis=1)
    keep = (ln > 1e-12) & ((n @ f) < 0)
    t, n = t[keep], n[keep] / ln[keep, None]
    order = np.argsort(-(t.mean(axis=1) @ f))
    lit = 0.5 + 0.5 * np.clip(n @ _LIGHT, 0, 1)
    cols = mesh.colours[keep] / 255.0 * lit[:, None]
    polys = np.stack([t @ right, t @ up], axis=-1)
    return polys[order], cols[order]


def render_sheet(cells: list[tuple[str, Mesh]], path: Path, cols: int, cell_m: tuple[float, float],
                 px_per_m: float = 100.0, fit: bool = False, view: tuple[float, float] = (-62.0, 9.0),
                 title: str = "", centre: bool = False) -> None:
    """A grid of meshes, each in a cell of ``cell_m`` (width, height) metres with its label under it.

    ``fit``: scale every mesh to fill its cell (parts of very different sizes) and say its size in
    the label; otherwise all share one scale and stand on a common ground line, the mesh's origin
    in the middle of its cell (``centre``: the middle of the mesh instead).
    """
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.collections import PolyCollection

    rows = math.ceil(len(cells) / cols)
    w, h = cell_m
    label_m = 0.085 * h if fit else 0.9
    fig = plt.figure(figsize=(cols * w * px_per_m / 100, rows * (h + label_m) * px_per_m / 100 + 0.4),
                     dpi=100, facecolor=SKY)
    ax = fig.add_axes((0, 0, 1, 1))
    ax.set_facecolor(SKY)
    fs = 8 if fit else 9
    for i, (label, mesh) in enumerate(cells):
        cx = (i % cols + 0.5) * w
        y0 = (rows - 1 - i // cols) * (h + label_m) + label_m
        polys, colours = _project(mesh, *view)
        if not len(polys):
            continue
        lo, hi = polys.reshape(-1, 2).min(axis=0), polys.reshape(-1, 2).max(axis=0)
        if fit:
            s = min(0.86 * w / max(hi[0] - lo[0], 1e-6), 0.86 * h / max(hi[1] - lo[1], 1e-6))
            polys = (polys - (lo + hi) / 2) * s + [cx, y0 + h / 2]
            pts = mesh.tris.reshape(-1, 3)
            size = pts.max(axis=0) - pts.min(axis=0)
            label += "\n" + " x ".join(f"{v:.2f}" for v in size) + " m"
        else:
            ax.fill([cx - w / 2, cx + w / 2, cx + w / 2, cx - w / 2], [y0 - label_m, y0 - label_m, y0, y0],
                    color=GROUND, zorder=0)
            polys = polys + [cx - ((lo[0] + hi[0]) / 2 if centre else 0.0), y0]
        ax.add_collection(PolyCollection(polys, facecolors=colours, edgecolors=colours, linewidths=0.3,
                                         zorder=1))
        ax.text(cx, y0 - label_m * (0.5 if fit else 0.45), label, ha="center", va="center", fontsize=fs,
                zorder=2, color="#1b1b1b")
    ax.set_xlim(0, cols * w)
    ax.set_ylim(0, rows * (h + label_m) + 0.4 * 100 / px_per_m)
    ax.set_aspect("equal")
    ax.axis("off")
    if title:
        ax.text(0.1, rows * (h + label_m) + 0.05, title, fontsize=11, va="bottom", color="#1b1b1b")
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path, facecolor=SKY)
    plt.close(fig)


def variant_cells() -> list[tuple[str, Mesh]]:
    return [(name.removeprefix("pole_"), build_variant(name).mesh) for name in VARIANTS]


def random_cells(n: int) -> list[tuple[str, Mesh]]:
    cells = []
    for seed in range(1, n + 1):
        spec = random_spec(seed)
        cells.append((f"seed {seed}: {spec.archetype}", build_pole(spec).mesh))
    return cells


def row_mesh(names: list[str], gap_m: float = 24.0) -> Mesh:
    """Poles standing in a row along x, their first-tier wires strung from each to the next."""
    poles = [build_variant(n).placed(i * gap_m, 0.0, 0.0) for i, n in enumerate(names)]
    meshes = [p.mesh for p in poles]
    for i in range(len(poles) - 1):
        meshes.append(span_mesh(poles[i].wire_points(), poles[i + 1].wire_points(), seed=i))
        meshes.append(span_mesh(poles[i].wire_points("service", None), poles[i + 1].wire_points("service", None),
                                sag_ratio=0.035, radius=0.014, colour=pp.CABLE, seed=100 + i))
    return Mesh.concat(meshes)


def part_cells() -> list[tuple[str, Mesh]]:
    return [(name, build().kit.mesh()) for name, (build, _what) in pp.PARTS.items()]


def manifest_entries() -> list[dict]:
    """The ``assets/manifest.json`` records of the placeable poles."""
    out = []
    for name, (archetype, seed) in VARIANTS.items():
        pole = build_variant(name)
        n = name.rsplit("_", 1)[1]
        out.append({
            "id": f"prim:{name}",
            "category": "pole",
            "name": f"Utility pole: {ARCHETYPES[archetype][2]} ({n})",
            "file": None,
            "size_m": list(pole.size_m),
            "tags": ["utility-pole", archetype, f"seed-{seed}", "flat-colour", "primitive"]
            + (["street-lamp"] if pole.lights else []),
            "licence": "CC0",
            "source": SOURCE,
            "pack": None,
        })
    return out


def sync_manifest(path: Path = DEFAULT_MANIFEST) -> int:
    """Replace the ``prim:pole_*`` entries of the asset manifest with the current ones (kept where
    the old ones were, or at the end); returns how many were written."""
    doc = json.loads(path.read_text(encoding="utf-8"))
    assets = doc["assets"]
    old = [i for i, a in enumerate(assets) if a["id"].startswith("prim:pole_")]
    at = old[0] if old else len(assets)
    kept = [a for a in assets if not a["id"].startswith("prim:pole_")]
    entries = manifest_entries()
    doc["assets"] = kept[:at] + entries + kept[at:]
    path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    return len(entries)


def write_glbs(out_dir: Path) -> None:
    from suchohrad_pipeline.export import build_chunk_scene

    for name in VARIANTS:
        scene = build_chunk_scene([], {name: build_variant(name).mesh})
        (out_dir / f"{name}.glb").write_bytes(scene.export(file_type="glb"))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Pictures of the generated utility poles.")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--glb", action="store_true", help="also write a GLB of every placeable pole")
    ap.add_argument("--random", type=int, default=21, metavar="N", help="poles in random.png")
    ap.add_argument("--sync-manifest", action="store_true",
                    help="rewrite the prim:pole_* entries of assets/manifest.json and stop")
    a = ap.parse_args(argv)
    if a.sync_manifest:
        print(f"{sync_manifest()} pole entries written to {DEFAULT_MANIFEST}")
        return
    a.out.mkdir(parents=True, exist_ok=True)
    render_sheet(variant_cells(), a.out / "variants.png", 8, (4.6, 11.0),
                 title="Placeable utility poles (prim:pole_*), front (road side) towards the viewer")
    render_sheet(random_cells(a.random), a.out / "random.png", 7, (4.6, 11.0),
                 title="random_spec(seed), archetype picked by weight")
    row = row_mesh(["pole_lamp_1", "pole_twin_lamp_1", "pole_megaphone_1", "pole_junction_3", "pole_lamp_2"])
    render_sheet([("a row of poles 24 m apart, wires strung by span_mesh", row)], a.out / "row.png", 1,
                 (104.0, 12.0), px_per_m=45.0, view=(-78.0, 7.0), centre=True)
    render_sheet(part_cells(), a.out / "parts.png", 7, (3.0, 3.0), fit=True, view=(-55.0, 14.0),
                 title="The parts kit (poleparts.PARTS)")
    if a.glb:
        write_glbs(a.out)
    for f in sorted(a.out.iterdir()):
        print(f)


if __name__ == "__main__":
    main()
