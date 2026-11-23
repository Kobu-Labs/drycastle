"""Preview stage: ``out/preview/{topdown,iso,iso_close}.png`` (matplotlib).

The top-down view is drawn from ``data/village/work/parcels.json`` and ``buildings.json``. The isometric
views use the same triangle meshes that go into the glTF files (terrain, roads, buildings, fences,
trees), with terrain relief exaggerated by a factor. ``iso_close.png`` zooms on the densest
cluster of buildings.
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

from drycastle_pipeline.config import REPO_ROOT, map_paths
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.rules import DEFAULT_RULES
from drycastle_pipeline.ground import ground_units
from drycastle_pipeline.scene import MapScene, build_scene
from drycastle_pipeline.textures import flat_mesh, split_tex_node

DEFAULT_PREVIEW_DIR = REPO_ROOT / "out" / "preview"
KIND_COLOURS = {
    "building_plot": "#e9d9a6",
    "garden": "#b9d9a0",
    "village": "#cfe0b8",
    "dike": "#a9c98a",
    "field": "#d8e2a0",
    "forest": "#5f9a60",
    "water": "#7fb2dc",
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
    parcels = ground_units(json.loads((work_dir / "parcels.json").read_text()))
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    return parcels, buildings


def _nice_bar(span_m: float) -> int:
    target = span_m / 5
    return max((c for c in BAR_CANDIDATES_M if c <= target), default=BAR_CANDIDATES_M[0])


def _extent(buildings: list[dict], pad: float) -> tuple[float, float, float, float]:
    pts = np.array([p for b in buildings for p in b["footprint"]["exterior"]])
    (x0, y0), (x1, y1) = pts.min(axis=0), pts.max(axis=0)
    return x0 - pad, x1 + pad, y0 - pad, y1 + pad


def render_topdown(
    parcels: list[dict],
    buildings: list[dict],
    path: Path,
    chunk_m: float | None = None,
    title: str = "Parcels by kind, buildings in wall colour (top-down)",
) -> None:
    """Top-down map; with ``chunk_m`` the chunk grid is drawn and each chunk labelled."""
    x0, x1, y0, y1 = _extent(buildings, 25)
    width = 18 if chunk_m else 12
    fig, ax = plt.subplots(figsize=(width, width * (y1 - y0) / (x1 - x0)), dpi=110)
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
    ax.set_title(title)
    if chunk_m:
        for gx in np.arange(math.floor(x0 / chunk_m) * chunk_m, x1 + chunk_m, chunk_m):
            ax.axvline(gx, color="#c0392b", linewidth=1.0, linestyle="--", zorder=6)
        for gy in np.arange(math.floor(y0 / chunk_m) * chunk_m, y1 + chunk_m, chunk_m):
            ax.axhline(gy, color="#c0392b", linewidth=1.0, linestyle="--", zorder=6)
        for ix in range(math.floor(x0 / chunk_m), math.ceil(x1 / chunk_m)):
            for iy in range(math.floor(y0 / chunk_m), math.ceil(y1 / chunk_m)):
                lx, ly = (ix + 0.5) * chunk_m, (iy + 0.5) * chunk_m
                if x0 <= lx <= x1 and y0 <= ly <= y1:
                    ax.text(lx, ly, f"chunk {ix},{iy}", color="#c0392b", fontsize=9, ha="center",
                            va="center", zorder=7,
                            bbox={"facecolor": "white", "alpha": 0.7, "edgecolor": "none"})
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
_VIEW = np.array([math.cos(_EL) / math.sqrt(2), math.cos(_EL) / math.sqrt(2), -math.sin(_EL)])
DEFAULT_EXAGGERATION = 3.0
CLOSE_RADIUS_M = 40.0
CLOSE_HALF_X_M, CLOSE_HALF_Y_M = 55.0, 40.0


def _project(pts: np.ndarray) -> np.ndarray:
    """(..., 3) local metres -> (..., 2) screen coordinates (x right, y up)."""
    x, y, z = pts[..., 0], pts[..., 1], pts[..., 2]
    sx = (x - y) / math.sqrt(2)
    depth = (x + y) / math.sqrt(2)
    return np.stack([sx, z * math.cos(_EL) + depth * math.sin(_EL)], axis=-1)


def _layers(scene: MapScene) -> list[Mesh]:
    """Draw layers, bottom first: terrain, roads, then buildings and props sorted together."""
    def node(name: str) -> Mesh:
        return Mesh.concat([n[name] for n in scene.nodes.values() if name in n])

    objects = [v for chunk in scene.parcels.values() for _, v, _ in chunk]
    for chunk in scene.parcel_props.values():
        for _, visual, _, textured in chunk:
            objects += [visual, flat_mesh(textured)]
    objects.append(node("props"))
    for nodes in scene.nodes.values():  # textured chunk-level nodes, in their mean colour
        objects += [flat_mesh({split_tex_node(n)[1]: m}) for n, m in nodes.items() if "_tex_" in n]
    return [node("terrain"), node("roads"), Mesh.concat(objects)]


def densest_cluster(buildings: list[dict], radius: float = CLOSE_RADIUS_M) -> tuple[float, float]:
    """Centroid of the building with most other buildings within ``radius`` metres."""
    pts = np.array([np.mean(b["footprint"]["exterior"], axis=0) for b in buildings])
    d = np.linalg.norm(pts[:, None] - pts[None], axis=2)
    best = int(np.argmax((d <= radius).sum(axis=1)))  # first maximum: deterministic
    near = pts[d[best] <= radius]
    return float(near[:, 0].mean()), float(near[:, 1].mean())


def render_iso(
    scene: MapScene,
    parcels: list[dict],
    buildings: list[dict],
    path: Path,
    extent: tuple[float, float, float, float],
    title: str,
) -> None:
    x0, x1, y0, y1 = extent
    flat_ground = scene.terrain is None
    layers = _layers(scene)
    fig, ax = plt.subplots(figsize=(14, 10), dpi=110)
    z_ref = float(scene.terrain.heights[scene.terrain.valid].min()) if scene.terrain else 0.0
    shifted = np.array([0.0, 0.0, z_ref])
    all_screen = []

    if flat_ground:
        ground = []
        for p in parcels:
            geom = shape(p["polygon"]).intersection(box(x0, y0, x1, y1))
            for poly in (g for g in getattr(geom, "geoms", [geom]) if g.geom_type == "Polygon"):
                pts = np.asarray(poly.exterior.coords)
                ground.append((_project(np.column_stack([pts, np.zeros(len(pts))])),
                               KIND_COLOURS[p["kind"]]))
        ax.add_collection(PolyCollection([g[0] for g in ground], facecolors=[g[1] for g in ground],
                                         edgecolors="#8a8a8a", linewidths=0.4, zorder=1))
        all_screen.append(np.concatenate([g[0] for g in ground]))

    for z, mesh in enumerate(layers, start=2):
        if not mesh.face_count:
            continue
        tris = mesh.tris - shifted
        c = tris.mean(axis=1)
        keep = (c[:, 0] >= x0) & (c[:, 0] <= x1) & (c[:, 1] >= y0) & (c[:, 1] <= y1)
        tris, cols = tris[keep], mesh.colours[keep] / 255.0
        if not len(tris):
            continue
        n = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
        shade = 0.55 + 0.45 * np.clip(n @ _LIGHT, 0, 1)
        face = np.clip(cols * shade[:, None], 0, 1)
        order = np.argsort(-(tris.mean(axis=1) @ _VIEW))  # farthest first
        screen = _project(tris)
        all_screen.append(screen.reshape(-1, 2))
        ax.add_collection(PolyCollection(screen[order], facecolors=face[order],
                                         edgecolors=face[order], linewidths=0.3, zorder=z))
    sc = np.concatenate(all_screen)
    ax.set_xlim(sc[:, 0].min() - 2, sc[:, 0].max() + 2)
    ax.set_ylim(sc[:, 1].min() - 2, sc[:, 1].max() + 8)
    ax.set_aspect("equal")
    ax.axis("off")
    ax.set_title(title)
    # Scale bar along the east axis in the empty upper-left corner: screen length bar / sqrt(2).
    bar = _nice_bar(x1 - x0)
    xl, xr = ax.get_xlim()
    yb, yt = ax.get_ylim()
    start = np.array([xl + 0.04 * (xr - xl), yt - 0.14 * (yt - yb)])
    end = start + _project(np.array([bar, 0.0, 0.0]))
    ax.add_line(Line2D([start[0], end[0]], [start[1], end[1]], color="black", linewidth=4, zorder=9))
    ax.text(start[0], end[1] + 1, f"{bar} m (measured along the east axis)", ha="left",
            va="bottom", zorder=9, bbox={"facecolor": "white", "alpha": 0.8, "edgecolor": "none"})
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path)
    plt.close(fig)


def run_preview(
    work_dir: Path = DEFAULT_WORK_DIR,
    out_dir: Path = DEFAULT_PREVIEW_DIR,
    rules_path: Path = DEFAULT_RULES,
    exaggeration: float = DEFAULT_EXAGGERATION,
) -> list[Path]:
    parcels, buildings = _load(work_dir)
    top, iso, close = out_dir / "topdown.png", out_dir / "iso.png", out_dir / "iso_close.png"
    render_topdown(parcels, buildings, top)
    has_terrain = (work_dir / "terrain.json").is_file()
    k = exaggeration if has_terrain else 1.0
    scene = build_scene(work_dir, rules_path, k)
    what = f"terrain relief x{k:g}, roads, fences, trees, buildings" if has_terrain else "buildings"
    render_iso(scene, parcels, buildings, iso, _extent(buildings, 20),
               f"Isometric view from the south-west, flat colours from the mesh data ({what})")
    cx, cy = densest_cluster(buildings)
    render_iso(scene, parcels, buildings, close,
               (cx - CLOSE_HALF_X_M, cx + CLOSE_HALF_X_M, cy - CLOSE_HALF_Y_M, cy + CLOSE_HALF_Y_M),
               f"Close-up of the densest building cluster (relief x{k:g})")
    chunk_m = float(json.loads((work_dir / "parcels.json").read_text())["chunk_size_m"])
    over = out_dir / "overview.png"
    render_topdown(parcels, buildings, over, chunk_m,
                   f"Overview with the {chunk_m:g} m chunk grid (chunk ix,iy)")
    return [top, iso, close, over]


def run(
    force: bool = False,
    exaggeration: float = DEFAULT_EXAGGERATION,
    **_options: object,
) -> int:
    ap = map_paths()
    try:
        paths = run_preview(ap.work, ap.preview, exaggeration=exaggeration)
    except FileNotFoundError as exc:
        print(f"preview: error: missing input {Path(str(exc.filename)).name}; run rules first")
        return 1
    for p in paths:
        print(f"preview: wrote {p.relative_to(REPO_ROOT)}")
    return 0
