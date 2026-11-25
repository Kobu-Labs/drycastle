"""Per-vertex view of the glTF primitives the measure stage counts (same visible nodes, same skinning)."""

from __future__ import annotations

import base64
import json
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

import numpy as np

from .. import gltf as core


@dataclass
class Prim:
    """One drawable primitive in world space (y up, metres)."""

    pos: np.ndarray  # (N,3) float32
    tris: np.ndarray  # (M,3) int32
    material: int | None
    normals: np.ndarray | None = None
    uvs: dict[int, np.ndarray] = field(default_factory=dict)
    color: np.ndarray | None = None  # (N,4) float32, 0..1
    skinned: bool = False


def read_json(path: Path) -> dict:
    """Only the JSON part of a .glb/.gltf (cheap: for cache keys)."""
    with path.open("rb") as f:
        head = f.read(20)
        if head[:4] != b"glTF":
            return json.loads(path.read_bytes())
        return json.loads(f.read(int.from_bytes(head[12:16], "little")))


class Doc:
    """A loaded glTF plus the file it came from (for external images)."""

    def __init__(self, path: Path):
        self.path = path
        self.g = core.load(path)
        self.json = self.g.json

    def image_bytes(self, i: int) -> bytes | None:
        img = self.json["images"][i]
        if "bufferView" in img:
            v = self.json["bufferViews"][img["bufferView"]]
            off = v.get("byteOffset", 0)
            return self.g.buffers[v["buffer"]][off : off + v["byteLength"]]
        uri = img.get("uri", "")
        if uri.startswith("data:"):
            return base64.b64decode(uri.split(",", 1)[1])
        f = self.path.parent / unquote(uri)
        return f.read_bytes() if f.is_file() else None


def load_prims(doc: Doc) -> tuple[list[Prim], list[str]]:
    """Primitives of every visible scene node; a skinned mesh is posed as in the file."""
    g = doc.g
    nodes = g.json.get("nodes", [])
    out: list[Prim] = []
    skipped: list[str] = []
    for i in core.visible_nodes(g):
        mesh = nodes[i].get("mesh")
        if mesh is None:
            continue
        for p in g.json["meshes"][mesh]["primitives"]:
            if p.get("mode", 4) != 4 or "POSITION" not in p["attributes"]:
                skipped.append(f"mesh {mesh}: primitive mode {p.get('mode')}")
                continue
            prim = _prim(g, nodes[i], g.world[i], p)
            if prim is not None:
                out.append(prim)
    return out, skipped


def _prim(g: core.Gltf, node: dict, world: np.ndarray, p: dict) -> Prim | None:
    at = p["attributes"]
    pos = g.accessor(at["POSITION"])
    idx = g.accessor(p["indices"]).reshape(-1) if "indices" in p else np.arange(len(pos))
    tris = idx[: len(idx) // 3 * 3].reshape(-1, 3).astype(np.int32)
    if len(tris) == 0:
        return None
    moved = None
    if "skin" in node:
        moved = core._skin_positions(g, p, g.json["skins"][node["skin"]], pos)
    if moved is not None:
        out = Prim(moved.astype(np.float32), tris, p.get("material"), skinned=True)
    else:
        w = world
        out = Prim(
            ((np.c_[pos, np.ones(len(pos))] @ w.T)[:, :3]).astype(np.float32),
            tris,
            p.get("material"),
        )
        if "NORMAL" in at:
            n = g.accessor(at["NORMAL"]) @ np.linalg.inv(w[:3, :3])
            ln = np.linalg.norm(n, axis=1, keepdims=True)
            out.normals = (n / np.where(ln > 1e-12, ln, 1.0)).astype(np.float32)
        if np.linalg.det(w[:3, :3]) < 0:
            out.tris = out.tris[:, [0, 2, 1]]
    for k, a in at.items():
        if k.startswith("TEXCOORD_"):
            out.uvs[int(k[9:])] = g.accessor(a).astype(np.float32)
    if "COLOR_0" in at:
        c = g.accessor(at["COLOR_0"])
        acc = g.json["accessors"][at["COLOR_0"]]
        if not acc.get("normalized") and acc["componentType"] in (5121, 5123):
            c = c / (255.0 if acc["componentType"] == 5121 else 65535.0)
        c = c.astype(np.float32)
        c = c if c.shape[1] == 4 else np.hstack([c, np.ones((len(c), 1), np.float32)])
        if c[:, :3].max() > 1e-3:  # an all-black COLOR_0 is exporter filler, not a colour
            out.color = c
    return out
