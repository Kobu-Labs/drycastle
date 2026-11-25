"""Minimal glTF 2.0 reader: JSON, accessors, node transforms, world-space triangles.

trimesh drops skins, animations and extension data, and merges what it keeps, so the catalogue
reads the file itself. Only triangle lists are supported (the whole asset set uses nothing else).
"""

from __future__ import annotations

import base64
import json
import struct
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

import numpy as np

_DTYPES = {5120: "i1", 5121: "u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
_NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT2": 4, "MAT3": 9, "MAT4": 16}
_NORM_DIV = {5120: 127.0, 5121: 255.0, 5122: 32767.0, 5123: 65535.0}
_TRIANGLES = 4


class GltfError(ValueError):
    pass


def external_files(path: Path) -> list[Path]:
    """Buffer files a .gltf points at (a .glb is self-contained). They belong in cache keys."""
    if path.suffix.lower() != ".gltf":
        return []
    uris = [b.get("uri", "") for b in json.loads(path.read_bytes()).get("buffers", [])]
    return [path.parent / unquote(u) for u in uris if u and not u.startswith("data:")]


@dataclass
class Gltf:
    json: dict
    buffers: list[bytes]
    world: np.ndarray = field(init=False)  # (nodes, 4, 4), column-vector convention
    parent: list[int] = field(init=False)

    def __post_init__(self) -> None:
        nodes = self.json.get("nodes", [])
        self.parent = [-1] * len(nodes)
        for i, n in enumerate(nodes):
            for c in n.get("children", []):
                self.parent[c] = i
        self.world = np.stack([np.eye(4)] * len(nodes)) if nodes else np.zeros((0, 4, 4))
        for i in range(len(nodes)):
            self.world[i] = self._world_of(i)

    def _world_of(self, i: int) -> np.ndarray:
        m = np.eye(4)
        seen = 0
        while i != -1:
            m = local_matrix(self.json["nodes"][i]) @ m
            i = self.parent[i]
            seen += 1
            if seen > len(self.json["nodes"]):
                raise GltfError("node hierarchy has a cycle")
        return m

    def accessor(self, idx: int) -> np.ndarray:
        """Accessor as float64/int64 array shaped (count, components); normalised ints scaled."""
        acc = self.json["accessors"][idx]
        if "sparse" in acc:
            raise GltfError("sparse accessors are not supported")
        comp, n = acc["componentType"], _NCOMP[acc["type"]]
        count = acc["count"]
        if "bufferView" not in acc:
            return np.zeros((count, n))
        view = self.json["bufferViews"][acc["bufferView"]]
        buf = self.buffers[view["buffer"]]
        item = np.dtype(_DTYPES[comp]).itemsize
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        stride = view.get("byteStride") or item * n
        raw = np.frombuffer(buf, dtype="u1", count=(count - 1) * stride + item * n, offset=start)
        rows = np.lib.stride_tricks.as_strided(raw, (count, item * n), (stride, 1))
        out = np.ascontiguousarray(rows).view(_DTYPES[comp]).reshape(count, n)
        if acc.get("normalized") and comp in _NORM_DIV:
            return np.maximum(out / _NORM_DIV[comp], -1.0)
        return out.astype(np.int64) if comp != 5126 else out.astype(np.float64)

    def scene_roots(self) -> list[int]:
        scenes = self.json.get("scenes", [])
        if scenes:
            return list(scenes[self.json.get("scene", 0)].get("nodes", []))
        return [i for i, p in enumerate(self.parent) if p == -1]


def local_matrix(node: dict) -> np.ndarray:
    if "matrix" in node:
        return np.array(node["matrix"], dtype=np.float64).reshape(4, 4).T
    m = np.eye(4)
    x, y, z, w = node.get("rotation", (0.0, 0.0, 0.0, 1.0))
    m[:3, :3] = _quat(x, y, z, w) * np.array(node.get("scale", (1.0, 1.0, 1.0)))
    m[:3, 3] = node.get("translation", (0.0, 0.0, 0.0))
    return m


def _quat(x: float, y: float, z: float, w: float) -> np.ndarray:
    n = np.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    )


def load(path: Path) -> Gltf:
    data = path.read_bytes()
    if data[:4] == b"glTF":
        js, bin_chunk = _split_glb(data)
    else:
        js, bin_chunk = json.loads(data), None
    buffers = [_read_buffer(b, path, bin_chunk) for b in js.get("buffers", [])]
    return Gltf(js, buffers)


def _split_glb(data: bytes) -> tuple[dict, bytes | None]:
    pos, js, bin_chunk = 12, None, None
    while pos + 8 <= len(data):
        length, kind = struct.unpack_from("<II", data, pos)
        body = data[pos + 8 : pos + 8 + length]
        if kind == 0x4E4F534A:
            js = json.loads(body)
        elif kind == 0x004E4942 and bin_chunk is None:
            bin_chunk = body
        pos += 8 + length + (-length % 4)
    if js is None:
        raise GltfError("no JSON chunk")
    return js, bin_chunk


def _read_buffer(buf: dict, path: Path, bin_chunk: bytes | None) -> bytes:
    uri = buf.get("uri")
    if uri is None:
        if bin_chunk is None:
            raise GltfError("buffer without uri and no BIN chunk")
        return bin_chunk
    if uri.startswith("data:"):
        return base64.b64decode(uri.split(",", 1)[1])
    return (path.parent / unquote(uri)).read_bytes()


@dataclass
class Prim:
    """One drawn primitive instance: world-space triangles plus the facts read off the file."""

    node: int
    mesh: int
    material: int | None
    verts: int  # POSITION count
    tris: np.ndarray  # (T, 3, 3) world space
    has_uv: bool
    has_color: bool
    skinned: bool


def _skin_positions(g: Gltf, prim: dict, skin: dict, pos: np.ndarray) -> np.ndarray | None:
    attrs = prim["attributes"]
    if "JOINTS_0" not in attrs or "WEIGHTS_0" not in attrs:
        return None
    joints = skin["joints"]
    ibm_idx = skin.get("inverseBindMatrices")
    if ibm_idx is None:
        ibm = np.stack([np.eye(4)] * len(joints))
    else:
        ibm = g.accessor(ibm_idx).reshape(-1, 4, 4).transpose(0, 2, 1)
    mats = g.world[joints] @ ibm  # (J, 4, 4)
    hom = np.concatenate([pos, np.ones((len(pos), 1))], axis=1)
    out = np.zeros_like(pos)
    s = 0
    while f"JOINTS_{s}" in attrs and f"WEIGHTS_{s}" in attrs:
        j, w = g.accessor(attrs[f"JOINTS_{s}"]), g.accessor(attrs[f"WEIGHTS_{s}"])
        for k in range(j.shape[1]):
            m = mats[j[:, k]]
            out += w[:, k : k + 1] * np.einsum("nij,nj->ni", m, hom)[:, :3]
        s += 1
    return out


def _primitive_triangles(g: Gltf, node: dict, world: np.ndarray, prim: dict) -> np.ndarray:
    attrs = prim["attributes"]
    pos = g.accessor(attrs["POSITION"])
    skin_idx = node.get("skin")
    moved = None
    if skin_idx is not None:  # a skinned mesh ignores its own node transform (glTF 2.0 3.7.3.3)
        moved = _skin_positions(g, prim, g.json["skins"][skin_idx], pos)
    if moved is None:
        moved = (np.c_[pos, np.ones(len(pos))] @ world.T)[:, :3]
    if "indices" in prim:
        idx = g.accessor(prim["indices"]).reshape(-1)
    else:
        idx = np.arange(len(pos))
    idx = idx[: len(idx) // 3 * 3].reshape(-1, 3)
    return moved[idx]


def _visible_nodes(g: Gltf) -> list[int]:
    """Scene nodes in draw order, minus subtrees hidden with KHR_node_visibility."""
    out: list[int] = []
    stack = list(reversed(g.scene_roots()))
    nodes = g.json["nodes"]
    while stack:
        i = stack.pop()
        vis = nodes[i].get("extensions", {}).get("KHR_node_visibility", {})
        if vis.get("visible", True) is False:
            continue
        out.append(i)
        stack.extend(reversed(nodes[i].get("children", [])))
    return out


def visible_nodes(g: Gltf) -> list[int]:
    return _visible_nodes(g)


def primitives(g: Gltf) -> tuple[list[Prim], list[str]]:
    """Every triangle primitive instanced by a visible scene node, in world space.

    The second value lists things skipped (non-triangle primitives), so nothing vanishes quietly.
    """
    prims: list[Prim] = []
    skipped: list[str] = []
    nodes = g.json.get("nodes", [])
    for i in _visible_nodes(g):
        mesh_idx = nodes[i].get("mesh")
        if mesh_idx is None:
            continue
        for p in g.json["meshes"][mesh_idx]["primitives"]:
            if p.get("mode", _TRIANGLES) != _TRIANGLES:
                skipped.append(f"mesh {mesh_idx} primitive mode {p.get('mode')}")
                continue
            attrs = p["attributes"]
            prims.append(
                Prim(
                    node=i,
                    mesh=mesh_idx,
                    material=p.get("material"),
                    verts=g.json["accessors"][attrs["POSITION"]]["count"],
                    tris=_primitive_triangles(g, nodes[i], g.world[i], p),
                    has_uv="TEXCOORD_0" in attrs,
                    has_color="COLOR_0" in attrs,
                    skinned="skin" in nodes[i],
                )
            )
    return prims, skipped
