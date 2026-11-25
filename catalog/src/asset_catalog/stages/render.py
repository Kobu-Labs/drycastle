"""Render: four 768px views + a contact sheet per model, and its dominant colours.

Pictures feed a vision LLM (descriptions) and an image embedder (visual search), so they are
lit softly, never tone-mapped (colours are measured from them), framed to the bounds, and carry
a scale bar (plus a 1.8 m human in the front view for human-scale things).
"""

from __future__ import annotations

from typing import Literal

from PIL import Image
from pydantic import BaseModel

from .. import store
from ..model import Asset
from ..render import camera, gltf, overlay, surfaces
from ..render.colors import DominantColor, dominant_colors
from ..render.scene import build_scene
from ..runner import run_stage

STAGE = "render"
VERSION = 2
SIZE = 768
BLANK_COVERAGE = 0.003  # below this share of covered pixels a view counts as blank


class ViewFrame(BaseModel):
    view: str
    px_per_m: float  # picture scale: pixels per metre in the picture plane
    extent_m: tuple[float, float]  # model width, height as seen in this view
    scale_bar_m: float
    scale_bar_label: str
    coverage: float  # share of the picture covered by the model (0..1)
    elevation_deg: float | None = None  # set when a flat/edge-on view was redrawn from above
    human_cue: bool  # a 1.8 m figure is drawn next to the model


class RenderPayload(BaseModel):
    status: Literal["ok", "nothing_to_draw"]
    nothing_to_draw_reason: str | None = None
    image_size: int = SIZE
    views: dict[str, str] = {}  # view name -> png path relative to the data dir
    contact_sheet: str | None = None  # 2x2 labelled: front, three_quarter / top, back
    frames: list[ViewFrame] = []
    bounds_min_m: tuple[float, float, float] | None = None  # y up, after node transforms
    bounds_max_m: tuple[float, float, float] | None = None
    triangles: int = 0
    skinned: bool = False  # drawn in the pose stored in the file (no animation applied)
    camera: str = "orthographic; front = camera on +Z looking -Z; three_quarter = from the front, 35 deg toward +X, 28 deg up; top = looking down, model front at the bottom; back = camera on -Z"
    missing_textures: list[str] = []  # textures that could not be read: drawn neutral grey
    unresolved_materials: list[str] = []  # materials with no known colour/atlas: drawn neutral grey
    placeholder_share: float = 0.0  # share of triangles drawn neutral grey for the reasons above
    blank_views: list[str] = []
    dominant_colors: list[DominantColor] = []  # from an unlit albedo render, real surfaces only
    notes: list[str] = []


def key_of(asset: Asset) -> str:
    js = gltf.read_json(asset.abs_path)
    return store.make_key(asset.content_hash, VERSION, SIZE, surfaces.fingerprint(asset, js))


_renderer = None


def _get_renderer():
    global _renderer
    if _renderer is None:
        from ..render.gl import Renderer

        _renderer = Renderer(SIZE)
    return _renderer


def _nothing(reason: str, **kw) -> RenderPayload:
    return RenderPayload(status="nothing_to_draw", nothing_to_draw_reason=reason, **kw)


def compute(asset: Asset) -> RenderPayload:
    doc = gltf.Doc(asset.abs_path)
    scene = build_scene(asset, doc)
    notes = list(scene.notes)
    if scene.triangles < 2 or float((scene.hi - scene.lo).max()) < 1e-5:
        return _nothing(
            f"{scene.triangles} triangle(s), extent {float((scene.hi - scene.lo).max()):.6f} m",
            triangles=scene.triangles,
            skinned=scene.skinned,
            notes=notes,
        )
    r = _get_renderer()
    gpu = r.upload(scene, doc)
    try:
        out = store.Store().dir(STAGE, asset.id)
        out.mkdir(parents=True, exist_ok=True)
        tiles, frames, blank, colors = {}, [], [], []
        pts = scene.points
        human = camera.wants_human(scene.lo, scene.hi)
        for view in camera.VIEWS:
            lay, rgb, share = _best_view(r, scene, gpu, pts, view, human and view == "front")
            if share < BLANK_COVERAGE:
                blank.append(view)
            tiles[view] = overlay.annotate(Image.fromarray(rgb, "RGB"), lay)
            frames.append(
                ViewFrame(
                    view=view,
                    px_per_m=round(lay.px_per_m, 3),
                    extent_m=(round(lay.width_m, 4), round(lay.height_m, 4)),
                    scale_bar_m=lay.bar_m,
                    scale_bar_label=lay.bar_label,
                    coverage=round(share, 4),
                    human_cue=lay.human_box is not None,
                    elevation_deg=lay.elevation,
                )
            )
            if view == "three_quarter":
                albedo, mask = r.albedo_view(scene, gpu, lay)
                colors = dominant_colors(albedo, mask)
    finally:
        r.release(gpu)
    paths = _save(store.Store(), asset, tiles)
    missing = sorted(set(scene.resolved.missing + gpu.missing))
    if blank:
        notes.append("blank view(s): " + ", ".join(blank))
    if scene.placeholder_triangles:
        notes.append("surfaces without colour information are drawn neutral grey")
    if any(f.elevation_deg for f in frames):
        notes.append("edge-on views redrawn from above (see frames.elevation_deg)")
    return RenderPayload(
        status="ok",
        views={v: paths[v] for v in camera.VIEWS},
        contact_sheet=paths["sheet"],
        frames=frames,
        bounds_min_m=tuple(round(float(v), 4) for v in scene.lo),
        bounds_max_m=tuple(round(float(v), 4) for v in scene.hi),
        triangles=scene.triangles,
        skinned=scene.skinned,
        missing_textures=missing,
        unresolved_materials=sorted(set(scene.resolved.unresolved)),
        placeholder_share=round(scene.placeholder_triangles / scene.triangles, 4),
        blank_views=blank,
        dominant_colors=colors,
        notes=notes,
    )


def _best_view(r, scene, gpu, pts, view: str, human: bool):
    """The view; when it is nearly empty (edge-on flat model) the same view tilted upward."""
    lay = camera.make_layout(view, pts, SIZE, human=human)
    rgb, cov = r.lit_view(scene, gpu, lay)
    share = float((cov > 0.5).mean())
    if share < BLANK_COVERAGE and view in camera.FALLBACK_ELEVATION:
        lay2 = camera.make_layout(
            view, pts, SIZE, human=human, elevation=camera.FALLBACK_ELEVATION[view]
        )
        rgb2, cov2 = r.lit_view(scene, gpu, lay2)
        share2 = float((cov2 > 0.5).mean())
        if share2 > share:
            return lay2, rgb2, share2
    return lay, rgb, share


def _save(st: store.Store, asset: Asset, tiles: dict) -> dict[str, str]:
    out = st.dir(STAGE, asset.id)
    paths = {}
    for name, img in {**tiles, "sheet": overlay.contact_sheet(tiles)}.items():
        f = out / ("contact_sheet.png" if name == "sheet" else f"{name}.png")
        img.save(f, compress_level=4)
        paths[name] = f.relative_to(st.root).as_posix()
    return paths


def run(assets: list[Asset], workers: int = 1, force: bool = False) -> dict[str, int]:
    models = [a for a in assets if a.media == "model"]
    return run_stage(STAGE, models, key_of, compute, RenderPayload, workers=workers, force=force)
