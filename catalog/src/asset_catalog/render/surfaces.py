"""Surfaces: how each glTF material of an asset is drawn (colour, texture, alpha, culling)."""

from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass, field
from functools import cache
from io import BytesIO
from pathlib import Path

from PIL import Image

from ..model import Asset
from . import atlases
from .gltf import Doc

MAX_TEX = 2048
PLACEHOLDER_SRGB = 0.62
_SYNTY_SOURCES = {"synty", "synty-anim", "synty-parts"}
_LAMBERT = re.compile(r"^lambert_?\d*$")


def srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


@dataclass(frozen=True)
class Surface:
    tex: str | None  # key into Resolved.textures
    color: tuple[float, float, float, float]  # linear rgb + alpha
    alpha: str = "opaque"  # opaque | mask | blend | hash
    cutoff: float = 0.5
    cull: int = 0  # 0 back, 1 front, 2 none
    wrap: int = 0  # 0 repeat, 1 clamp, 2 mirror
    uv_set: int = 0
    uv_xform: tuple[float, float, float, float, float] = (
        1.0,
        1.0,
        0.0,
        0.0,
        0.0,
    )  # sx sy ox oy rot
    vertex_color: bool = False
    unlit: bool = False
    placeholder: bool = False  # no real colour information: drawn neutral grey


@dataclass
class Resolved:
    by_material: dict[int, Surface] = field(default_factory=dict)
    fallback: Surface | None = None  # primitives without a material
    textures: dict[str, tuple[str, str | int]] = field(default_factory=dict)  # key -> (kind, ref)
    missing: list[str] = field(default_factory=list)
    unresolved: list[str] = field(default_factory=list)
    files: list[Path] = field(default_factory=list)  # files the look depends on

    def placeholder_surface(self) -> Surface:
        g = srgb_to_linear(PLACEHOLDER_SRGB)
        return Surface(None, (g, g, g, 1.0), placeholder=True)


@cache
def _table(path: Path) -> dict[str, dict] | None:
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text()).get("materials", {})
    except (OSError, ValueError):
        return None


def _hex_color(s: str | None) -> tuple[float, float, float, float]:
    if not s or not s.startswith("#") or len(s) not in (7, 9):
        return (1.0, 1.0, 1.0, 1.0)
    v = [int(s[i : i + 2], 16) / 255 for i in range(1, len(s), 2)]
    v += [1.0] * (4 - len(v))
    return (*(srgb_to_linear(c) for c in v[:3]), v[3])


def _synty_surface(spec: dict, folder: Path, res: Resolved) -> Surface:
    mode = int(spec.get("transparency", 0))
    alpha = {0: "opaque", 1: "blend", 2: "mask", 3: "hash", 4: "blend"}.get(mode, "opaque")
    tex = None
    rel = spec.get("albedo_texture")
    placeholder = False
    if rel:
        f = Path(os.path.normpath(folder / rel))
        res.files.append(f)
        if f.is_file():
            tex = f"file:{f}"
            res.textures[tex] = ("file", str(f))
        else:
            res.missing.append(f"{folder.name}/{rel}")
            placeholder = True
    sx, sy = spec.get("uv1_scale", (1.0, 1.0))
    ox, oy = spec.get("uv1_offset", (0.0, 0.0))
    color = _hex_color(spec.get("albedo_color"))
    if placeholder:
        g = srgb_to_linear(PLACEHOLDER_SRGB)
        color = (g, g, g, color[3])
    return Surface(
        tex,
        color,
        alpha,
        float(spec.get("alpha_scissor_threshold", 0.5)),
        int(spec.get("cull_mode", 0)),
        0,
        0,
        (sx, sy, ox, oy, 0.0),
        unlit=int(spec.get("shading_mode", 1)) == 0,
        placeholder=placeholder,
    )


def _embedded_texture(
    js: dict, info: dict, res: Resolved, asset: Asset
) -> tuple[str | None, int, tuple, int]:
    """(texture key, uv set, uv transform, wrap) of a glTF textureInfo, or key None."""
    tex = js["textures"][info["index"]]
    ext = info.get("extensions", {}).get("KHR_texture_transform", {})
    ox, oy = ext.get("offset", (0.0, 0.0))
    sx, sy = ext.get("scale", (1.0, 1.0))
    uv_set = ext.get("texCoord", info.get("texCoord", 0))
    wrap = 0
    if "sampler" in tex:
        w = js.get("samplers", [])[tex["sampler"]].get("wrapS", 10497)
        wrap = {10497: 0, 33071: 1, 33648: 2}.get(w, 0)
    src = tex.get("source")
    if src is None:
        for e in tex.get("extensions", {}).values():
            src = e.get("source", src)
    if src is None:
        return None, uv_set, (1.0, 1.0, 0.0, 0.0, 0.0), wrap
    key = f"emb:{asset.content_hash[:16]}:{src}"
    res.textures[key] = ("emb", src)
    return key, uv_set, (sx, sy, ox, oy, float(ext.get("rotation", 0.0))), wrap


def _gltf_surface(js: dict, m: dict, res: Resolved, asset: Asset, atlas_key: str | None) -> Surface:
    pbr = m.get("pbrMetallicRoughness", {})
    f = pbr.get("baseColorFactor", [1, 1, 1, 1])
    alpha = {"OPAQUE": "opaque", "MASK": "mask", "BLEND": "blend"}.get(m.get("alphaMode"), "opaque")
    cull = 2 if m.get("doubleSided") else 0
    cutoff = float(m.get("alphaCutoff", 0.5))
    tex, uv_set, xf, wrap = None, 0, (1.0, 1.0, 0.0, 0.0, 0.0), 0
    if "baseColorTexture" in pbr:
        tex, uv_set, xf, wrap = _embedded_texture(js, pbr["baseColorTexture"], res, asset)
        if tex is None:
            res.missing.append(f"{m.get('name', '?')}: texture without image")
    elif atlas_key:
        tex = atlas_key
    return Surface(tex, tuple(f), alpha, cutoff, cull, wrap, uv_set, xf, vertex_color=True)


def _blank(m: dict) -> bool:
    """A glTF material that carries a name only (white, no texture): no colour information."""
    pbr = m.get("pbrMetallicRoughness", {})
    return "baseColorTexture" not in pbr and list(pbr.get("baseColorFactor", [1] * 4)) == [1] * 4


def _default_lambert(m: dict) -> bool:
    """FBX default material ("lambert3", flat grey): not a colour anyone chose."""
    return bool(_LAMBERT.match(m.get("name", ""))) and "baseColorTexture" not in m.get(
        "pbrMetallicRoughness", {}
    )


def resolve(asset: Asset, js: dict) -> Resolved:
    res = Resolved()
    folder = asset.abs_path.parent
    table = _table(folder / "materials.json")
    atlas = atlases.atlas_for(asset.pack, asset.name) if asset.source == "synty" else None
    atlas_key = None
    if atlas is not None:
        res.files.append(atlas)
        if atlas.is_file():
            atlas_key = f"file:{atlas}"
            res.textures[atlas_key] = ("file", str(atlas))
        else:
            res.missing.append(f"atlas {atlas.name}")
    for i, m in enumerate(js.get("materials", [])):
        name = m.get("name", "")
        if table is not None and name in table:
            res.by_material[i] = _synty_surface(table[name], folder, res)
        elif asset.source in _SYNTY_SOURCES and (_blank(m) or _default_lambert(m)):
            res.by_material[i] = _blank_synty(asset, name, atlas, atlas_key, res)
        else:
            res.by_material[i] = _gltf_surface(js, m, res, asset, None)
    res.fallback = res.placeholder_surface()
    return res


def _blank_synty(
    asset: Asset, name: str, atlas: Path | None, atlas_key: str | None, res: Resolved
) -> Surface:
    if atlas_key is not None:
        return Surface(atlas_key, (1.0, 1.0, 1.0, 1.0))
    if atlas is None:
        res.unresolved.append(f"{name or 'unnamed'}: no atlas known for {asset.id}")
    return res.placeholder_surface()


def fingerprint(asset: Asset, js: dict) -> dict:
    """Everything outside the GLB that changes how it looks: material entries, texture files."""
    res = resolve(asset, js)
    folder = asset.abs_path.parent
    table = _table(folder / "materials.json") or {}
    names = sorted({m.get("name", "") for m in js.get("materials", [])})
    return {
        "materials": {n: table[n] for n in names if n in table},
        "files": {p.name: _file_hash(p) for p in sorted(set(res.files))},
        "unresolved": res.unresolved,
    }


@cache
def _file_hash(path: Path) -> str | None:
    if not path.is_file():
        return None
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()[:16]


def load_image(res: Resolved, key: str, doc: Doc) -> Image.Image | None:
    kind, ref = res.textures[key]
    try:
        if kind == "file":
            img = Image.open(str(ref))
        else:
            raw = doc.image_bytes(int(ref))
            if raw is None:
                return None
            img = Image.open(BytesIO(raw))
        img = img.convert("RGBA")
    except (OSError, ValueError):
        return None
    if max(img.size) > MAX_TEX:
        k = MAX_TEX / max(img.size)
        img = img.resize(
            (max(1, round(img.width * k)), max(1, round(img.height * k))), Image.Resampling.BOX
        )
    return img
