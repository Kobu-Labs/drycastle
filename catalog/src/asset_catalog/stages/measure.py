"""Measure: facts read off the geometry by code. No model is involved; nothing here is guessed.

The numbers feed exact search filters and the describing LLM, so a fact that cannot be stated
honestly is None, not a best guess. Thresholds live in the constants below and in PARAMS, which
is part of the cache key.
"""

from __future__ import annotations

import hashlib
from collections.abc import Iterable
from typing import Literal

import numpy as np
from pydantic import BaseModel

from .. import geometry, gltf
from ..model import Asset
from ..runner import run_stage
from ..store import make_key

STAGE = "measure"
VERSION = 1

SizeClass = Literal["hand", "tabletop", "furniture", "vehicle-scale", "room", "building", "terrain"]
PivotKind = Literal["base-centre", "centre", "base-corner", "base-edge", "top-centre", "other"]
Form = Literal["plate", "strip", "pole", "block"]  # see form_of

# Upper bound (exclusive) of the longest box side, metres, per size class. The bands are scale
# bands named after what usually sits in them, not a claim about what the asset is.
SIZE_BANDS: tuple[tuple[float, SizeClass], ...] = (
    (0.3, "hand"),
    (0.8, "tabletop"),
    (2.5, "furniture"),
    (6.0, "vehicle-scale"),
    (14.0, "room"),
    (60.0, "building"),
)
MIN_SIZE_M = 0.001  # a longest side below this is a placeholder mesh, not an asset
END_TOL = 0.05  # pivot within this share of the box (or END_TOL_M) of a face counts as on it
END_TOL_M = 0.02
MID_TOL = 0.10  # pivot within this share of the box centre counts as centred
FLAT_MIN = 0.5  # box face counts as flat when this share of it is covered
MIRROR_MIN = 0.85  # surface/mirror overlap that counts as symmetric
PLATE_THIN = 0.25  # thinnest / middle side below this: plate or strip
PLATE_WIDE = 0.2  # plate when middle / longest side is at least this, else strip
POLE_SLIM = 0.2  # middle / longest side below this (and not thin): pole
PARAMS = {
    "min_size": MIN_SIZE_M,
    "bands": SIZE_BANDS,
    "pivot": [END_TOL, END_TOL_M, MID_TOL],
    "flat": [FLAT_MIN, geometry.FACE_GRID],
    "mirror": [MIRROR_MIN, geometry.SYM_GRID, geometry.SYM_SAMPLES],
    "form": [PLATE_THIN, PLATE_WIDE, POLE_SLIM],
}


class Pivot(BaseModel):
    kind: PivotKind
    offset: list[float | None]  # origin's place in the box per axis x,y,z: 0 = min face, 1 = max


class Geometry(BaseModel):
    size_m: list[float]  # world-space box extent x, y, z (Y up, metres)
    min_m: list[float]
    max_m: list[float]
    footprint_m2: float  # box x * z
    volume_m3: float  # box volume, not the mesh's
    size_class: SizeClass  # by the longest side
    pivot: Pivot
    form: Form  # plate: one thin side; strip: thin and long; pole: one side 5x the rest; else block
    form_axis: Literal["x", "y", "z"]  # plate: thin axis; strip, pole: long axis (x/z = lying bar)
    flat: dict[str, float | None]  # per box face "-x".."+z": share covered by flush geometry
    flat_faces: list[str]  # faces where that share is at least FLAT_MIN
    mirror_x: float | None  # 0..1 overlap of the surface with its left-right mirror
    mirror_z: float | None  # same front-back
    symmetric: list[str]  # axes ("x", "z") where the mirror overlap is at least MIRROR_MIN


class Animation(BaseModel):
    name: str
    duration_s: float


class Measure(BaseModel):
    geometry: Geometry | None  # None for a placeholder mesh (animation clips ship one)
    tris: int
    verts: int  # position count summed over drawn primitives
    mesh_count: int  # drawn mesh instances (visible nodes carrying a mesh)
    node_count: int  # visible nodes, collision-only nodes left out
    materials: list[str]  # names as in the file, in first-use order
    unmaterialed_prims: int  # drawn primitives with no material
    has_vertex_colors: bool  # any primitive carries COLOR_0
    has_uvs: bool  # every primitive carries TEXCOORD_0
    has_rig: bool
    joints: int
    animations: list[Animation]
    morph_targets: list[str]
    notes: list[str]  # anything skipped or degenerate, so nothing vanishes quietly


# --- classification -------------------------------------------------------------------------


def size_class(longest_m: float) -> SizeClass:
    for upper, name in SIZE_BANDS:
        if longest_m < upper:
            return name
    return "terrain"


def _axis_state(lo: float, size: float, is_y: bool) -> str:
    """Where the origin sits on one axis: min / mid / max / other (y: base / mid / top)."""
    ends = ("base", "top") if is_y else ("min", "max")
    if size < geometry.MIN_EXTENT:  # flat axis: the origin is on it or it is not
        on = abs(lo) <= END_TOL_M
        return (ends[0] if is_y else "mid") if on else "other"
    o = -lo / size
    if abs(o) <= END_TOL or abs(lo) <= END_TOL_M:
        return ends[0]
    if abs(o - 1) <= END_TOL or abs(lo + size) <= END_TOL_M:
        return ends[1]
    return "mid" if abs(o - 0.5) <= MID_TOL else "other"


def pivot_of(lo: np.ndarray, hi: np.ndarray) -> Pivot:
    size = hi - lo
    ax = [_axis_state(lo[a], size[a], a == 1) for a in range(3)]
    offset = [
        None if size[a] < geometry.MIN_EXTENT else round(float(-lo[a] / size[a]), 3)
        for a in range(3)
    ]
    return Pivot(kind=_pivot_kind(ax[0], ax[1], ax[2]), offset=offset)


def _pivot_kind(x: str, y: str, z: str) -> PivotKind:
    edge = ("min", "max")
    if y == "base":
        if x == "mid" and z == "mid":
            return "base-centre"
        if x in edge and z in edge:
            return "base-corner"
        if (x in edge and z == "mid") or (x == "mid" and z in edge):
            return "base-edge"
    elif x == "mid" and z == "mid":
        return {"mid": "centre", "top": "top-centre"}.get(y, "other")
    return "other"


def form_of(size: np.ndarray) -> tuple[Form, str]:
    order = np.argsort(size)
    a, b, c = (float(size[i]) for i in order)
    thin_axis, long_axis = "xyz"[order[0]], "xyz"[order[2]]
    if c <= 0:
        return "block", long_axis
    if b > 0 and a / b < PLATE_THIN:
        return ("plate", thin_axis) if b / c >= PLATE_WIDE else ("strip", long_axis)
    if b / c < POLE_SLIM:
        return "pole", long_axis
    return "block", long_axis


# --- reading the file -----------------------------------------------------------------------


def _collider_only(g: gltf.Gltf, i: int, memo: dict[int, bool]) -> bool:
    if i not in memo:
        n = g.json["nodes"][i]
        has_physics = any(k.startswith("OMI_physics") for k in n.get("extensions", {}))
        memo[i] = (
            "mesh" not in n
            and has_physics
            and all(_collider_only(g, c, memo) for c in n.get("children", []))
        )
    return memo[i]


def _node_count(g: gltf.Gltf) -> int:
    memo: dict[int, bool] = {}
    return sum(not _collider_only(g, i, memo) for i in gltf.visible_nodes(g))


def _material_names(g: gltf.Gltf, prims: list[gltf.Prim]) -> list[str]:
    mats = g.json.get("materials", [])
    names: list[str] = []
    for p in prims:
        if p.material is None:
            continue
        name = mats[p.material].get("name") or "(unnamed)"
        if name not in names:
            names.append(name)
    return names


def _animations(g: gltf.Gltf) -> list[Animation]:
    out = []
    for i, anim in enumerate(g.json.get("animations", [])):
        ends = []
        for s in anim.get("samplers", []):
            acc = g.json["accessors"][s["input"]]
            ends.append(acc["max"][0] if acc.get("max") else float(g.accessor(s["input"]).max()))
        out.append(
            Animation(
                name=anim.get("name") or f"animation{i}",
                duration_s=round(float(max(ends, default=0.0)), 4),
            )
        )
    return out


def _morph_targets(g: gltf.Gltf, prims: list[gltf.Prim]) -> list[str]:
    names: list[str] = []
    for mesh_idx in dict.fromkeys(p.mesh for p in prims):
        mesh = g.json["meshes"][mesh_idx]
        count = max((len(p.get("targets", [])) for p in mesh["primitives"]), default=0)
        given = mesh.get("extras", {}).get("targetNames", [])
        names += [given[i] if i < len(given) else f"target{i}" for i in range(count)]
    return names


def _geometry(tris: np.ndarray) -> Geometry:
    lo, hi = geometry.bounds(tris)
    size = hi - lo
    form, form_axis = form_of(size)
    flat = geometry.flat_coverage(tris, lo, hi)
    mx, mz = geometry.mirror_symmetry(tris, lo, hi)
    return Geometry(
        size_m=_r(size),
        min_m=_r(lo),
        max_m=_r(hi),
        footprint_m2=round(float(size[0] * size[2]), 4),
        volume_m3=round(float(size.prod()), 4),
        size_class=size_class(float(size.max())),
        pivot=pivot_of(lo, hi),
        form=form,
        form_axis=form_axis,  # type: ignore[arg-type]
        flat=flat,
        flat_faces=[f for f, v in flat.items() if v is not None and v >= FLAT_MIN],
        mirror_x=mx,
        mirror_z=mz,
        symmetric=[a for a, v in (("x", mx), ("z", mz)) if v is not None and v >= MIRROR_MIN],
    )


def _r(v: np.ndarray) -> list[float]:
    return [round(float(x), 4) for x in v]


def compute(asset: Asset) -> Measure:
    g = gltf.load(asset.abs_path)
    prims, skipped = gltf.primitives(g)
    notes = [f"skipped {s}" for s in skipped]
    tris = np.concatenate([p.tris for p in prims]) if prims else np.zeros((0, 3, 3))
    geo = None
    if len(tris) and np.ptp(tris.reshape(-1, 3), axis=0).max() >= MIN_SIZE_M:
        geo = _geometry(tris)
    else:
        notes.append("placeholder mesh (no real surface): geometry facts omitted")
    joints = {j for s in g.json.get("skins", []) for j in s.get("joints", [])}
    return Measure(
        geometry=geo,
        tris=len(tris),
        verts=sum(p.verts for p in prims),
        mesh_count=len({p.node for p in prims}),
        node_count=_node_count(g),
        materials=_material_names(g, prims),
        unmaterialed_prims=sum(p.material is None for p in prims),
        has_vertex_colors=any(p.has_color for p in prims),
        has_uvs=bool(prims) and all(p.has_uv for p in prims),
        has_rig=bool(joints),
        joints=len(joints),
        animations=_animations(g),
        morph_targets=_morph_targets(g, prims),
        notes=notes,
    )


# --- stage wiring ---------------------------------------------------------------------------


def key_of(asset: Asset) -> str:
    external = [
        hashlib.sha256(p.read_bytes()).hexdigest() for p in gltf.external_files(asset.abs_path)
    ]
    return make_key(asset.content_hash, VERSION, PARAMS, external)


def run(assets: Iterable[Asset], workers: int = 1, force: bool = False) -> dict[str, int]:
    models = [a for a in assets if a.media == "model"]
    return run_stage(STAGE, models, key_of, compute, Measure, workers=workers, force=force)
