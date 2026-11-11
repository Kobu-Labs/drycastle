"""The texture library: named, tileable textures that any map mesh can use.

The images and their facts live in ``assets/manifest.json`` (``textures``); the PNG files are built by
``scripts/make_textures.py``. Nothing here is specific to fences: a mesh builder asks for a texture by
id, lays UVs out in metres with :meth:`Texture.uv`, and returns a :class:`TexturedMesh` per texture.

How a textured mesh reaches the game:

* the chunk (or parcel-run) GLB gets one extra node per texture, named ``<node>_tex_<texture id>``
  (:func:`tex_node_name`), next to the flat-colour node ``<node>`` it belongs to;
* the node carries TEXCOORD_0 (the UVs) and COLOR_0 (a per-triangle tint, white = as drawn). No image
  is embedded: the loader draws the node with the library texture of that id (``assets/`` on disk);
* a loader that does not know the convention draws the node in its tint, flat.

UV convention in the pipeline: u, v in repeats of the tile, v grows upward (v = 1 is the top row of the
image). The glTF writer flips v, as glTF wants.
"""

import functools
import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from suchohrad_pipeline.assets import DEFAULT_MANIFEST
from suchohrad_pipeline.buildings import hex_to_rgb

TEX_NODE_SEP = "_tex_"
# Keeps the edge texels of a stretched (non-repeating) direction from blending with the opposite edge.
_STRETCH_INSET = 0.004


@dataclass(frozen=True)
class Texture:
    id: str
    tile_w: float  # metres per repeat along u
    tile_h: float  # metres per repeat along v; 0 = stretched once over the surface height
    alpha: bool  # has cut-outs (drawn two-sided by the mesh builder, alpha-tested by the game)
    tintable: bool  # near-white: the tint is the paint
    mean: tuple[int, int, int]  # average colour of the opaque pixels (previews)

    def uv(self, along_m: float, up_m: float, height_m: float = 0.0) -> tuple[float, float]:
        """UV of a point ``along_m`` along the surface and ``up_m`` above its foot.

        ``height_m`` is the surface height: a repeating texture is anchored at the top (so the top course
        of a wall is a whole one); a stretched texture spans the height once.
        """
        u = along_m / self.tile_w
        if self.tile_h > 0:
            return u, (up_m - height_m) / self.tile_h
        t = up_m / height_m if height_m > 0 else 0.0
        return u, _STRETCH_INSET + t * (1 - 2 * _STRETCH_INSET)

    def flat(self, tint: tuple[int, int, int]) -> tuple[int, int, int]:
        """The flat colour that stands for this texture under ``tint`` (previews, old loaders)."""
        return tuple(round(m * t / 255) for m, t in zip(self.mean, tint, strict=True))


@functools.cache
def load_textures(manifest_path: Path = DEFAULT_MANIFEST) -> dict[str, Texture]:
    doc = json.loads(Path(manifest_path).read_text())
    out = {}
    for t in doc.get("textures", []):
        out[t["id"]] = Texture(
            id=t["id"],
            tile_w=float(t["tile_m"][0]),
            tile_h=float(t["tile_m"][1]),
            alpha=bool(t.get("alpha", False)),
            tintable=bool(t.get("tintable", False)),
            mean=tuple(hex_to_rgb(t["mean_colour"])),
        )
    return out


def texture(tid: str) -> Texture:
    lib = load_textures()
    if tid not in lib:
        raise KeyError(f"texture {tid!r} is not in the library (assets/manifest.json textures)")
    return lib[tid]


@dataclass
class TexturedMesh:
    """Triangles of one texture: positions like :class:`meshes.Mesh`, a tint and UVs per triangle."""

    tris: np.ndarray  # (n, 3, 3) float64, local metres, counter-clockwise seen from outside
    colours: np.ndarray  # (n, 3) uint8 tint (white = the texture as drawn)
    uvs: np.ndarray  # (n, 3, 2) float64, see the module docstring

    @classmethod
    def empty(cls) -> "TexturedMesh":
        return cls(np.zeros((0, 3, 3)), np.zeros((0, 3), np.uint8), np.zeros((0, 3, 2)))

    @classmethod
    def concat(cls, meshes: list["TexturedMesh"]) -> "TexturedMesh":
        if not meshes:
            return cls.empty()
        return cls(
            np.concatenate([m.tris for m in meshes]),
            np.concatenate([m.colours for m in meshes]),
            np.concatenate([m.uvs for m in meshes]),
        )

    @property
    def face_count(self) -> int:
        return len(self.tris)


class TexturedBuilder:
    """Collects textured quads and triangles by texture id; ``meshes()`` gives one mesh per texture."""

    def __init__(self) -> None:
        self._parts: dict[str, tuple[list, list, list]] = {}

    def tri(self, tid: str, pts, uvs, tint) -> None:
        tris, cols, tex = self._parts.setdefault(tid, ([], [], []))
        tris.append(pts)
        tex.append(uvs)
        cols.append(tint)

    def quad(self, tid: str, pts, uvs, tint, two_sided: bool = False) -> None:
        """A quad given as four corners in counter-clockwise order (seen from its front)."""
        self.tri(tid, [pts[0], pts[1], pts[2]], [uvs[0], uvs[1], uvs[2]], tint)
        self.tri(tid, [pts[0], pts[2], pts[3]], [uvs[0], uvs[2], uvs[3]], tint)
        if two_sided:
            self.tri(tid, [pts[0], pts[2], pts[1]], [uvs[0], uvs[2], uvs[1]], tint)
            self.tri(tid, [pts[0], pts[3], pts[2]], [uvs[0], uvs[3], uvs[2]], tint)

    def meshes(self) -> dict[str, TexturedMesh]:
        return {
            tid: TexturedMesh(np.array(t, float), np.array(c, np.uint8), np.array(uv, float))
            for tid, (t, c, uv) in sorted(self._parts.items())
            if t
        }


def merge_textured(parts: list[dict[str, TexturedMesh]]) -> dict[str, TexturedMesh]:
    by_id: dict[str, list[TexturedMesh]] = {}
    for part in parts:
        for tid, mesh in part.items():
            by_id.setdefault(tid, []).append(mesh)
    return {tid: TexturedMesh.concat(ms) for tid, ms in sorted(by_id.items())}


def flat_mesh(textured: dict[str, TexturedMesh]):
    """The textured triangles as one flat-colour ``meshes.Mesh`` (each in its texture's mean colour
    under the tint): what previews draw."""
    from suchohrad_pipeline.meshes import Mesh

    parts = []
    for tid, mesh in sorted(textured.items()):
        mean = np.array(texture(tid).mean, float)
        parts.append(Mesh(mesh.tris, np.round(mesh.colours * mean / 255).astype(np.uint8)))
    return Mesh.concat(parts)


def tex_node_name(base: str, tid: str) -> str:
    """Name of the node that holds the ``tid``-textured triangles belonging to node ``base``."""
    return f"{base}{TEX_NODE_SEP}{tid}"


def split_tex_node(name: str) -> tuple[str, str | None]:
    """``(base node, texture id)``; the id is None for an ordinary (flat-colour) node."""
    base, sep, tid = name.partition(TEX_NODE_SEP)
    return (base, tid) if sep else (name, None)
