"""Preview stage: ``out/preview/topdown.png`` and ``out/preview/iso.png`` (matplotlib).

Both views are drawn from ``data/work/parcels.json`` and ``buildings.json``; the isometric view
uses the same triangle meshes that go into the glTF files.
"""

import json
import math
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from matplotlib.collections import PolyCollection
from matplotlib.lines import Line2D
from matplotlib.patches import Patch, PathPatch
from matplotlib.path import Path as MplPath
from shapely.geometry import box, shape

from suchohrad_pipeline.config import REPO_ROOT
from suchohrad_pipeline.meshes import building_mesh
from suchohrad_pipeline.normalise import DEFAULT_WORK_DIR

DEFAULT_PREVIEW_DIR = REPO_ROOT / "out" / "preview"
KIND_COLOURS = {
    "building_plot": "#e9d9a6",
    "garden": "#b9d9a0",
    "road": "#9a9a9a",
    "field": "#d8e2a0",
    "forest": "#5f9a60",
    "water": "#7fb2dc",
    "other": "#e2dfd8",
}
BAR_CANDIDATES_M = (5, 10, 20, 25, 50, 100, 200)


def _polygon_path(geom) -> MplPath:
    verts, codes = [], []
    for poly in getattr(geom, "geoms", [geom]):
        for ring in [poly.exterior, *poly.interiors]:
            pts = np.asarray(ring.coords)
            verts.extend(pts)
            codes.extend([MplPath.MOVETO, *[MplPath.LINETO] * (len(pts) - 2), MplPath.CLOSEPOLY])
    return MplPath(verts, codes)


def _load(work_dir: Path):
    parcels = json.loads((work_dir / "parcels.json").read_text())["parcels"]
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    return parcels, buildings


def _nice_bar(span_m: float) -> int:
    target = span_m / 5
    return max((c for c in BAR_CANDIDATES_M if c <= target), default=BAR_CANDIDATES_M[0])


def _extent(buildings: list[dict], pad: float) -> tuple[float, float, float, float]:
    pts = np.array([p for b in buildings for p in b["footprint"]["exterior"]])
    (x0, y0), (x1, y1) = pts.min(axis=0), pts.max(axis=0)
    return x0 - pad, x1 + pad, y0 - pad, y1 + pad


def render_topdown(parcels: list[dict], buildings: list[dict], path: Path) -> None:
    x0, x1, y0, y1 = _extent(buildings, 25)
    fig, ax = plt.subplots(figsize=(12, 12 * (y1 - y0) / (x1 - x0)), dpi=110)
    for p in parcels:
        ax.add_patch(
            PathPatch(
                _polygon_path(shape(p["polygon"])),
                facecolor=KIND_COLOURS[p["kind"]],
                edgecolor="#6b6b6b",
                linewidth=0.5,
            )
        )
    for b in buildings:
        fp = b["footprint"]
        verts = [np.array(fp["exterior"]), *map(np.array, fp["holes"])]
        path_ = MplPath(
            np.concatenate([np.vstack([v, v[:1]]) for v in verts]),
            [
                c
                for v in verts
                for c in (MplPath.MOVETO, *[MplPath.LINETO] * (len(v) - 1), MplPath.CLOSEPOLY)
            ],
        )
        ax.add_patch(
            PathPatch(
                path_,
                facecolor=np.array(b["wall_colour"]) / 255,
                edgecolor="#2a2a2a",
                linewidth=1.0,
                zorder=3,
            )
        )
    ax.set_xlim(x0, x1)
    ax.set_ylim(y0, y1)
    ax.set_aspect("equal")
    ax.set_xlabel("local x, east (m)")
    ax.set_ylabel("local y, north (m)")
    ax.set_title("Parcels by kind, buildings in wall colour (top-down)")
    ax.legend(
        handles=[Patch(facecolor=c, edgecolor="#6b6b6b", label=k) for k, c in KIND_COLOURS.items()]
        + [Patch(facecolor="#d9c7a0", edgecolor="#2a2a2a", label="building")],
        loc="upper right",
        fontsize=8,
        framealpha=0.9,
    )
    bar = _nice_bar(x1 - x0)
    bx, by = x0 + 0.04 * (x1 - x0), y0 + 0.04 * (y1 - y0)
    ax.add_line(Line2D([bx, bx + bar], [by, by], color="black", linewidth=4, zorder=5))
    ax.text(
        bx + bar / 2,
        by + 0.01 * (y1 - y0),
        f"{bar} m",
        ha="center",
        va="bottom",
        zorder=5,
        bbox={"facecolor": "white", "alpha": 0.8, "edgecolor": "none"},
    )
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path)
    plt.close(fig)


# Isometric camera from the south-west, looking north-east and down.
_EL = math.radians(30)
_LIGHT = np.array([-0.45, -0.55, 0.7])
_LIGHT = _LIGHT / np.linalg.norm(_LIGHT)


def _project(pts: np.ndarray) -> np.ndarray:
    """(…, 3) local metres -> (…, 2) screen coordinates (x right, y up)."""
    x, y, z = pts[..., 0], pts[..., 1], pts[..., 2]
    sx = (x - y) / math.sqrt(2)
    depth = (x + y) / math.sqrt(2)
    return np.stack([sx, z * math.cos(_EL) + depth * math.sin(_EL)], axis=-1)


def render_iso(parcels: list[dict], buildings: list[dict], path: Path) -> None:
    tris_all, cols_all = [], []
    for b in buildings:
        m = building_mesh(b)
        tris_all.append(m.tris)
        cols_all.append(m.colours)
    tris = np.concatenate(tris_all)
    cols = np.concatenate(cols_all) / 255.0
    n = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    shade = 0.55 + 0.45 * np.clip(n @ _LIGHT, 0, 1)
    face_cols = np.clip(cols * shade[:, None], 0, 1)
    # farthest first: distance along the view direction (towards north-east, downwards)
    view = np.array([math.cos(_EL) / math.sqrt(2), math.cos(_EL) / math.sqrt(2), -math.sin(_EL)])
    order = np.argsort(-(tris.mean(axis=1) @ view))
    screen = _project(tris)

    x0, x1, y0, y1 = _extent(buildings, 20)
    fig, ax = plt.subplots(figsize=(14, 10), dpi=110)
    ground = []
    for p in parcels:
        geom = shape(p["polygon"])
        geom = geom.intersection(box(x0, y0, x1, y1))
        if geom.is_empty:
            continue
        for poly in (g for g in getattr(geom, "geoms", [geom]) if g.geom_type == "Polygon"):
            pts = np.asarray(poly.exterior.coords)
            ground.append(
                (_project(np.column_stack([pts, np.zeros(len(pts))])), KIND_COLOURS[p["kind"]])
            )
    ax.add_collection(
        PolyCollection(
            [g[0] for g in ground],
            facecolors=[g[1] for g in ground],
            edgecolors="#8a8a8a",
            linewidths=0.4,
            zorder=1,
        )
    )
    ax.add_collection(
        PolyCollection(
            screen[order],
            facecolors=face_cols[order],
            edgecolors=face_cols[order],
            linewidths=0.4,
            zorder=2,
        )
    )
    corners = np.array([[x0, y0, 0], [x1, y0, 0], [x0, y1, 0], [x1, y1, 0]], float)
    sc = _project(corners)
    ax.set_xlim(sc[:, 0].min(), sc[:, 0].max())
    ax.set_ylim(sc[:, 1].min() - 2, sc[:, 1].max() + 15)
    ax.set_aspect("equal")
    ax.axis("off")
    ax.set_title("Isometric view from the south-west (flat colours, from the mesh data)")
    # Scale bar along the east axis at the south-west corner: its screen length is bar/sqrt(2).
    bar = _nice_bar(x1 - x0)
    xl, xr = ax.get_xlim()
    yb, yt = ax.get_ylim()
    start = np.array([xl + 0.04 * (xr - xl), yt - 0.12 * (yt - yb)])  # empty upper-left corner
    end = start + _project(np.array([bar, 0.0, 0.0]))
    ax.add_line(
        Line2D([start[0], end[0]], [start[1], end[1]], color="black", linewidth=4, zorder=5)
    )
    ax.text(
        start[0],
        end[1] + 3,
        f"{bar} m (measured along the east axis)",
        ha="left",
        va="bottom",
        zorder=5,
        bbox={"facecolor": "white", "alpha": 0.8, "edgecolor": "none"},
    )
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path)
    plt.close(fig)


def run_preview(
    work_dir: Path = DEFAULT_WORK_DIR, out_dir: Path = DEFAULT_PREVIEW_DIR
) -> list[Path]:
    parcels, buildings = _load(work_dir)
    top, iso = out_dir / "topdown.png", out_dir / "iso.png"
    render_topdown(parcels, buildings, top)
    render_iso(parcels, buildings, iso)
    return [top, iso]


def run(force: bool = False, **_options: object) -> int:
    try:
        paths = run_preview()
    except FileNotFoundError as exc:
        print(f"preview: error: missing input {Path(str(exc.filename)).name}; run rules first")
        return 1
    for p in paths:
        print(f"preview: wrote {p.relative_to(REPO_ROOT)}")
    return 0
