"""Shared helpers of the three SIMPLE Military vehicle scripts (make_humvee_model.py, make_scud_model.py, make_rocket_cart_model.py).

Needs numpy (no -I: it may live in the user site) and the extracted pack: scripts/build_simple_military.sh makes it.

What is here
- Pack          reads parts/<Name>.glb (re-exported pack FBX, simple_military_parts.gd): every mesh with its world matrix
- Mesh          triangles with normals and uv, in the vehicle frame of the model being built; transform / split / clip helpers
- Model         the node tree + materials + GLB writer (translation-only nodes, vertices stored in the vehicle frame and moved into the
                node's pivot on the way out); the glass list comes from the flak script's GlassSet (ADR 0008)
- Draw          small primitives for the generated parts (box, beam, ring, disk, prism, loft) with flat shading

Pack frame: the Synty/Unity models face +Z and use pack units (the troop car is 7 m long, the Scud truck 14.8 m). Godot's FBX import keeps that.
The model scripts turn them into the game frame (forward -Z) and scale them to real sizes: Pack.world(name, scale) does both.
"""
import importlib.util
import json
import math
import struct
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
PRIVATE = Path("/home/jjakubec/coding/drycastle/assets-private") if not (ROOT / "assets-private").exists() else ROOT / "assets-private"
PROJECT = PRIVATE / "synty" / "simple-military"
OUT_DIR = PRIVATE / "synty" / "glb" / "simple-military"
PACK_ID = "synty-simple-military"

_spec = importlib.util.spec_from_file_location("make_flak_model", HERE / "make_flak_model.py")
flak = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(flak)
GlassSet = flak.GlassSet
vehicles_json_path = flak.vehicles_json_path


# ----------------------------------------------------------------------------- glTF reader (what Godot's exporter writes)

def read_glb(path):
    data = Path(path).read_bytes()
    jlen, = struct.unpack_from("<I", data, 12)
    doc = json.loads(data[20:20 + jlen])
    off = 20 + jlen
    blen, = struct.unpack_from("<I", data, off)
    blob = data[off + 8:off + 8 + blen]
    return doc, blob


def _acc(doc, blob, i):
    a = doc["accessors"][i]
    v = doc["bufferViews"][a["bufferView"]]
    comp = {5126: "<f4", 5125: "<u4", 5123: "<u2", 5121: "u1"}[a["componentType"]]
    n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
    arr = np.frombuffer(blob, dtype=comp, count=a["count"] * n, offset=v.get("byteOffset", 0) + a.get("byteOffset", 0))
    return arr.reshape(-1, n) if n > 1 else arr


def _local(node):
    t = np.eye(4)
    if "matrix" in node:
        return np.array(node["matrix"]).reshape(4, 4).T
    q = node.get("rotation", [0, 0, 0, 1])
    x, y, z, w = q
    r = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                  [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                  [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    s = np.array(node.get("scale", [1, 1, 1]))
    t[:3, :3] = r * s
    t[:3, 3] = node.get("translation", [0, 0, 0])
    return t


class Mesh:
    """Triangles: v (N,3), n (N,3), uv (N,2) or None, f (M,3). `mat` is a material name of the Model."""

    def __init__(self, v, f, n=None, uv=None, mat="", name=""):
        self.v = np.asarray(v, float).reshape(-1, 3)
        self.f = np.asarray(f, int).reshape(-1, 3)
        self.n = None if n is None else np.asarray(n, float).reshape(-1, 3)
        self.uv = None if uv is None else np.asarray(uv, float).reshape(-1, 2)
        self.mat, self.name = mat, name

    def copy(self):
        return Mesh(self.v.copy(), self.f.copy(), None if self.n is None else self.n.copy(), None if self.uv is None else self.uv.copy(), self.mat, self.name)

    def apply(self, m):
        """Transform by a 4x4 matrix (normals by its inverse transpose)."""
        out = self.copy()
        out.v = self.v @ m[:3, :3].T + m[:3, 3]
        if self.n is not None:
            nm = np.linalg.inv(m[:3, :3]).T
            nn = self.n @ nm.T
            out.n = nn / np.maximum(np.linalg.norm(nn, axis=1, keepdims=True), 1e-12)
        if np.linalg.det(m[:3, :3]) < 0:
            out.f = out.f[:, ::-1].copy()
        return out

    def flat(self):
        """Non-indexed copy with one normal per face (flat shading); uv kept."""
        a, b, c = self.v[self.f[:, 0]], self.v[self.f[:, 1]], self.v[self.f[:, 2]]
        nn = np.cross(b - a, c - a)
        ln = np.linalg.norm(nn, axis=1, keepdims=True)
        keep = ln[:, 0] > 1e-12
        nn = nn[keep] / ln[keep]
        v = np.stack([a[keep], b[keep], c[keep]], axis=1).reshape(-1, 3)
        n = np.repeat(nn, 3, axis=0)
        uv = None if self.uv is None else np.stack([self.uv[self.f[keep, 0]], self.uv[self.f[keep, 1]], self.uv[self.f[keep, 2]]], axis=1).reshape(-1, 2)
        return Mesh(v, np.arange(len(v)).reshape(-1, 3), n, uv, self.mat, self.name)

    def tri_pts(self):
        return self.v[self.f]  # (M,3,3)

    def subset(self, mask):
        """The faces where mask is true (vertices compacted)."""
        f = self.f[mask]
        used, inv = np.unique(f, return_inverse=True)
        return Mesh(self.v[used], inv.reshape(-1, 3), None if self.n is None else self.n[used], None if self.uv is None else self.uv[used], self.mat, self.name)

    def bounds(self):
        return self.v.min(axis=0), self.v.max(axis=0)


class Pack:
    """parts/<Name>.glb: {node name: Mesh in the pack's own frame (world matrix applied), parent chain kept in .parent}."""

    def __init__(self, name):
        self.doc, blob = read_glb(PROJECT / "parts" / (name + ".glb"))
        self.meshes, self.origin, self.parent = {}, {}, {}
        nodes = self.doc["nodes"]
        parent = {}
        for i, nd in enumerate(nodes):
            for c in nd.get("children", []):
                parent[c] = i

        def world(i):
            m = _local(nodes[i])
            return world(parent[i]) @ m if i in parent else m
        for i, nd in enumerate(nodes):
            nm = nd.get("name", str(i))
            w = world(i)
            self.origin[nm] = w[:3, 3].copy()
            if i in parent:
                self.parent[nm] = nodes[parent[i]].get("name")
            if "mesh" not in nd:
                continue
            for prim in self.doc["meshes"][nd["mesh"]]["primitives"]:
                at = prim["attributes"]
                v = _acc(self.doc, blob, at["POSITION"]).astype(float)
                n = _acc(self.doc, blob, at["NORMAL"]).astype(float) if "NORMAL" in at else None
                uv = _acc(self.doc, blob, at["TEXCOORD_0"]).astype(float) if "TEXCOORD_0" in at else None
                f = _acc(self.doc, blob, prim["indices"]).astype(int).reshape(-1, 3) if "indices" in prim else np.arange(len(v)).reshape(-1, 3)
                self.meshes[nm] = Mesh(v, f, n, uv).apply(w)

    def get(self, name, mat="Military_Green"):
        m = self.meshes[name].copy()
        m.mat, m.name = mat, name
        return m


def rot_y(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1.0]])


def rot_x(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1.0]])


def scale_m(s):
    return np.diag([s, s, s, 1.0])


def move_m(t):
    m = np.eye(4)
    m[:3, 3] = t
    return m


# The pack faces +Z; the game's forward is -Z: a half turn about Y (x and z flip), then the real-size scale.
def to_game(scale):
    return scale_m(scale) @ rot_y(180)


# ----------------------------------------------------------------------------- generated primitives (flat shaded)

def _quad_faces(idx):
    return [[idx[0], idx[1], idx[2]], [idx[0], idx[2], idx[3]]]


def poly(pts, mat, name=""):
    """A planar convex polygon (fan)."""
    pts = np.asarray(pts, float)
    f = [[0, i, i + 1] for i in range(1, len(pts) - 1)]
    return Mesh(pts, f, mat=mat, name=name)


def box(c, size, mat, name="", yaw=0.0):
    c, s = np.array(c, float), np.array(size, float) / 2
    corners = np.array([(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)], float) * s
    if yaw:
        corners = corners @ rot_y(yaw)[:3, :3].T
    v = corners + c
    f = []
    for q in ([0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]):
        f += _quad_faces(q)
    return Mesh(v, f, mat=mat, name=name)


def hexa(corners, mat, name=""):
    """A box-like solid from 8 corners ordered as box() (bottom -z face 0..3, top +z face 4..7)."""
    f = []
    for q in ([0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [3, 7, 6, 2], [0, 4, 7, 3], [1, 2, 6, 5]):
        f += _quad_faces(q)
    return Mesh(np.asarray(corners, float), f, mat=mat, name=name)


def beam(a, b, w, mat, name="", depth=None):
    a, b = np.array(a, float), np.array(b, float)
    d = b - a
    d /= np.linalg.norm(d)
    u = np.cross(d, [0, 1, 0] if abs(d[1]) < .95 else [1, 0, 0])
    u = u / np.linalg.norm(u) * w / 2
    v = np.cross(d, u)
    v = v / np.linalg.norm(v) * (depth or w) / 2
    pts = [p + su * u + sv * v for p in (a, b) for su, sv in [(-1, -1), (1, -1), (1, 1), (-1, 1)]]
    f = []
    for q in ([0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]):
        f += _quad_faces(q)
    return Mesh(pts, f, mat=mat, name=name)


def ring(c, ro, ri, depth, mat, n=16, axis="x", name=""):
    """A tube segment (washer extruded `depth`) about an axis through c."""
    v = []
    for d, r in [(-depth / 2, ro), (depth / 2, ro), (-depth / 2, ri), (depth / 2, ri)]:
        for i in range(n):
            a = 2 * math.pi * i / n
            p = [d, r * math.cos(a), r * math.sin(a)]
            if axis == "z":
                p = [p[1], p[2], p[0]]
            elif axis == "y":
                p = [p[1], p[0], p[2]]
            v.append(np.array(c, float) + p)
    f = []
    for i in range(n):
        j = (i + 1) % n
        for a, b in [(0, n), (2 * n, 0), (n, 3 * n), (3 * n, 2 * n)]:
            f += _quad_faces([a + i, a + j, b + j, b + i])
    return Mesh(v, f, mat=mat, name=name)


def disk(c, r, mat, n=16, axis="z", name=""):
    pts = []
    for i in range(n):
        a = math.tau * i / n
        d = {"x": [0, r * math.cos(a), r * math.sin(a)], "y": [r * math.cos(a), 0, r * math.sin(a)], "z": [r * math.cos(a), r * math.sin(a), 0]}[axis]
        pts.append(np.array(c, float) + d)
    return poly(pts, mat, name)


def cylinder(a, b, r, mat, n=12, name="", r1=None, caps=True):
    """A cylinder (or cone, r1 at b) between two points."""
    a, b = np.array(a, float), np.array(b, float)
    d = b - a
    d /= np.linalg.norm(d)
    u = np.cross(d, [0, 1, 0] if abs(d[1]) < .95 else [1, 0, 0])
    u /= np.linalg.norm(u)
    w = np.cross(d, u)
    r1 = r if r1 is None else r1
    v = []
    for p, rr in ((a, r), (b, r1)):
        for i in range(n):
            t = math.tau * i / n
            v.append(p + rr * (u * math.cos(t) + w * math.sin(t)))
    f = []
    for i in range(n):
        j = (i + 1) % n
        f += _quad_faces([i, j, n + j, n + i])
    m = Mesh(v, f, mat=mat, name=name)
    if caps:
        m = merge([m, poly(v[:n][::-1], mat), poly(v[n:], mat)], mat, name)
    return m


def merge(meshes, mat=None, name=""):
    v, f, n, uv, off = [], [], [], [], 0
    have_n = all(m.n is not None for m in meshes)
    have_uv = all(m.uv is not None for m in meshes)
    for m in meshes:
        v.append(m.v)
        f.append(m.f + off)
        if have_n:
            n.append(m.n)
        if have_uv:
            uv.append(m.uv)
        off += len(m.v)
    return Mesh(np.vstack(v), np.vstack(f), np.vstack(n) if have_n else None, np.vstack(uv) if have_uv else None, mat or meshes[0].mat, name or meshes[0].name)


def loft(rings, mat, name="", cap=True):
    """Skin between consecutive rings (same point count); caps by fan."""
    n = len(rings[0])
    v = np.vstack([np.asarray(r, float) for r in rings])
    f = []
    for k in range(len(rings) - 1):
        for i in range(n):
            j = (i + 1) % n
            f += _quad_faces([k * n + i, k * n + j, (k + 1) * n + j, (k + 1) * n + i])
    m = Mesh(v, f, mat=mat, name=name)
    if cap:
        m = merge([m, poly(rings[0][::-1], mat), poly(rings[-1], mat)], mat, name)
    return m


def orient_outward(m):
    """Flips faces of a closed solid so its signed volume is positive (normals outward)."""
    a, b, c = m.v[m.f[:, 0]], m.v[m.f[:, 1]], m.v[m.f[:, 2]]
    cen = m.v.mean(axis=0)
    vol = np.einsum("ij,ij->i", a - cen, np.cross(b - cen, c - cen)).sum()
    if vol < 0:
        m.f = m.f[:, ::-1].copy()
    return m


# ----------------------------------------------------------------------------- materials

def gltf_mat(name, color, metal=0.0, rough=0.8, alpha=1.0, emission=None, double=False):
    """A glTF material; colours are given in sRGB (like the flak script) and written linear."""
    def lin(c):
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": [round(lin(c), 4) for c in color] + [alpha], "metallicFactor": metal, "roughnessFactor": rough}}
    if alpha < 1:
        m["alphaMode"] = "BLEND"
    if emission is not None:
        m["emissiveFactor"] = [round(lin(c) * emission, 4) for c in color]
    if double:
        m["doubleSided"] = True
    return m


def frame_from_axis(axis):
    """A right-handed 3x3 whose columns are (x, axis, z): local Y is `axis`, local X the world X made perpendicular to it (a tilt about X keeps X)."""
    y = np.asarray(axis, float)
    y = y / np.linalg.norm(y)
    x = np.array([1.0, 0, 0]) - y[0] * y
    x /= np.linalg.norm(x)
    return np.stack([x, y, np.cross(x, y)], axis=1)


def quat_of(r):
    """(x, y, z, w) of a rotation matrix."""
    t = np.trace(r)
    if t > 0:
        s = math.sqrt(t + 1) * 2
        return [(r[2, 1] - r[1, 2]) / s, (r[0, 2] - r[2, 0]) / s, (r[1, 0] - r[0, 1]) / s, s / 4]
    i = int(np.argmax(np.diag(r)))
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1 + r[i, i] - r[j, j] - r[k, k]) * 2
    q = [0.0] * 4
    q[i] = s / 4
    q[j] = (r[j, i] + r[i, j]) / s
    q[k] = (r[k, i] + r[i, k]) / s
    q[3] = (r[k, j] - r[j, k]) / s
    return q


class Model:
    """Node tree of nodes with a pivot and, optionally, an orientation (`rot`, a steering axis that is not vertical). add(node, mesh): the mesh is in the vehicle frame
    (the node's pivot and orientation are taken off on export)."""

    def __init__(self, name):
        self.name = name
        self.nodes = {name: {"parent": None, "pos": np.zeros(3), "meshes": [], "rot": np.eye(3)}}
        self.order = [name]
        self.materials = {}   # name -> glTF material dict, or None for a name-only pack material
        self.glass = None
        self.tris = 0

    def node(self, name, parent, pos=(0, 0, 0), rot=None):
        """pos: the pivot in the VEHICLE frame (not relative to the parent). rot: the node's orientation in the VEHICLE frame (3x3), default none (identity)."""
        assert name not in self.nodes, name
        self.nodes[name] = {"parent": parent, "pos": np.array(pos, float), "meshes": [], "rot": np.eye(3) if rot is None else np.asarray(rot, float)}
        self.order.append(name)
        return name

    def mat(self, name, *args, **kw):
        self.materials[name] = gltf_mat(name, *args, **kw)
        return name

    def pack_mat(self, name):
        self.materials[name] = None
        return name

    def add(self, node, *meshes):
        for m in meshes:
            if m is not None:
                self.nodes[node]["meshes"].append(m)

    def shift(self, d):
        """Moves everything (node pivots and every mesh) by d: used to put the origin at the footprint centre."""
        d = np.array(d, float)
        seen = set()
        for name, nd in self.nodes.items():
            if name not in (self.name, "hull"):          # the root and the hull keep their pivot at the vehicle origin
                nd["pos"] = nd["pos"] + d
            for m in nd["meshes"]:
                if id(m) not in seen:
                    seen.add(id(m))
                    m.v = m.v + d

    def chain(self, name):
        out, n = [], name
        while n is not None:
            out.append(n)
            n = self.nodes[n]["parent"]
        return out

    def all_points(self, exclude=()):
        pts = []
        for n in self.nodes.values():
            for m in n["meshes"]:
                pts.append(m.v)
        return np.vstack(pts)

    def write(self, path, generator):
        blob = bytearray()
        views, access, meshes, nodes_json = [], [], [], []
        mat_index = {n: i for i, n in enumerate(self.materials)}

        def acc(arr, typ, dtype="<f4", target=34962):
            arr = np.ascontiguousarray(arr, dtype=dtype)
            blob.extend(b"\0" * (-len(blob) % 4))
            off = len(blob)
            blob.extend(arr.tobytes())
            views.append({"buffer": 0, "byteOffset": off, "byteLength": arr.nbytes, "target": target})
            a = {"bufferView": len(views) - 1, "componentType": {"<f4": 5126, "<u4": 5125}[dtype], "count": len(arr), "type": typ}
            if typ == "VEC3":
                a.update(min=[float(x) for x in arr.min(axis=0)], max=[float(x) for x in arr.max(axis=0)])
            access.append(a)
            return len(access) - 1

        index = {}
        for nm in self.order:
            nodes_json.append({"name": nm})
            index[nm] = len(nodes_json) - 1
        for nm in self.order:
            nd = self.nodes[nm]
            p = nd["parent"]
            rp = self.nodes[p]["rot"] if p is not None else np.eye(3)
            if p is not None:
                nodes_json[index[p]].setdefault("children", []).append(index[nm])
                t = rp.T @ (nd["pos"] - self.nodes[p]["pos"])
            else:
                t = nd["pos"]
            if np.any(np.abs(t) > 1e-9):
                nodes_json[index[nm]]["translation"] = [round(float(x), 5) + 0.0 for x in t]
            rl = rp.T @ nd["rot"]
            if np.abs(rl - np.eye(3)).max() > 1e-9:
                nodes_json[index[nm]]["rotation"] = [round(float(x), 6) + 0.0 for x in quat_of(rl)]
        for nm in self.order:
            nd = self.nodes[nm]
            if not nd["meshes"]:
                continue
            by_mat = {}
            for m in nd["meshes"]:
                by_mat.setdefault(m.mat, []).append(m.flat() if m.n is None else m)
            prims = []
            pivot = nd["pos"]
            for mname, group in by_mat.items():
                g = merge(group, mname) if len(group) > 1 else group[0]
                rot = nd["rot"]
                v = (g.v - pivot) @ rot                  # rot.T applied to each point
                gn = g.n @ rot
                self.tris += len(g.f)
                at = {"POSITION": acc(v, "VEC3"), "NORMAL": acc(gn, "VEC3")}
                if g.uv is not None:
                    at["TEXCOORD_0"] = acc(g.uv, "VEC2")
                ix = acc(g.f.reshape(-1), "SCALAR", "<u4", 34963)
                prims.append({"attributes": at, "indices": ix, "material": mat_index[mname]})
            meshes.append({"name": nm, "primitives": prims})
            nodes_json[index[nm]]["mesh"] = len(meshes) - 1
        mats = []
        for n, j in self.materials.items():
            mats.append(j if j is not None else {"name": n})
        doc = {"asset": {"version": "2.0", "generator": generator, "copyright": "Contains geometry of a paid Synty pack: private, never committed"},
               "scene": 0, "scenes": [{"name": self.name, "nodes": [index[self.name]]}], "nodes": nodes_json, "meshes": meshes, "materials": mats,
               "buffers": [{"byteLength": len(blob)}], "bufferViews": views, "accessors": access}
        for a in doc["accessors"]:
            if a["type"] == "SCALAR":
                a["componentType"] = 5125
        js = json.dumps(doc, separators=(",", ":")).encode()
        js += b" " * (-len(js) % 4)
        blob.extend(b"\0" * (-len(blob) % 4))
        data = struct.pack("<III", 0x46546c67, 2, 12 + 8 + len(js) + 8 + len(blob)) + struct.pack("<II", len(js), 0x4e4f534a) + js + struct.pack("<II", len(blob), 0x004e4942) + bytes(blob)
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_bytes(data)
        return len(data)


# ----------------------------------------------------------------------------- the pack's shared material table

def ensure_pack_materials():
    """glb/simple-military/materials.json + tex/: the pack's Military_Green material (name -> StandardMaterial3D settings, the format of
    MapLoader.Prefabs). The 2048 palette is copied as is."""
    import shutil
    d = OUT_DIR
    (d / "tex").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(PROJECT / "textures" / "Tex_Military_Green.png", d / "tex" / "Tex_Military_Green.png")
    (d / "materials.json").write_text(json.dumps({"materials": {
        "Military_Green": {"albedo_color": "#ffffff", "albedo_texture": "tex/Tex_Military_Green.png", "roughness": 0.85, "metallic": 0.0, "metallic_specular": 0.3,
                           "transparency": 0, "cull_mode": 0},
        # the same palette, drawn two-sided: the pack's body shells are one skin, the inside of the walls shows through the window openings
        "Military_Green_2s": {"albedo_color": "#ffffff", "albedo_texture": "tex/Tex_Military_Green.png", "roughness": 0.85, "metallic": 0.0, "metallic_specular": 0.3,
                              "transparency": 0, "cull_mode": 2},
    }}, indent=1) + "\n")


def sample_palette(uv):
    """The colour (sRGB 0..1) of the Military_Green palette at a glTF uv (v down), for generated parts that should match."""
    from PIL import Image
    im = Image.open(PROJECT / "textures" / "Tex_Military_Green.png").convert("RGB")
    w, h = im.size
    x = min(w - 1, max(0, int(uv[0] * w)))
    y = min(h - 1, max(0, int(uv[1] * h)))
    return tuple(c / 255 for c in im.getpixel((x, y)))


GLASS_RGB = (37, 39, 41)   # the palette swatch the pack paints its windows with (a near-black grey)


def glass_faces(mesh):
    """Boolean mask of the faces whose uv lands on the pack's window swatch (the pack draws glass as paint, there is no window geometry)."""
    from PIL import Image
    im = np.array(Image.open(PROJECT / "textures" / "Tex_Military_Green.png").convert("RGB"))
    h, w, _ = im.shape
    uv = mesh.uv[mesh.f].mean(axis=1)
    px = im[np.clip((uv[:, 1] * h).astype(int), 0, h - 1), np.clip((uv[:, 0] * w).astype(int), 0, w - 1)].astype(int)
    return np.abs(px - np.array(GLASS_RGB)).sum(axis=1) <= 8


def outline_of(points):
    """The convex outline (ordered around the centre in the plane of the points) of a set of coplanar 3D points; returns a list of 3D points."""
    pts = np.unique(np.round(np.asarray(points, float), 5), axis=0)
    c = pts.mean(axis=0)
    # plane basis from the two largest spreads
    u, s, vt = np.linalg.svd(pts - c)
    a, b = vt[0], vt[1]
    ang = np.arctan2((pts - c) @ b, (pts - c) @ a)
    order = np.argsort(ang)
    pts = pts[order]
    # drop points that are not hull corners (collinear)
    out = []
    n = len(pts)
    for i in range(n):
        p0, p1, p2 = pts[i - 1], pts[i], pts[(i + 1) % n]
        if np.linalg.norm(np.cross(p1 - p0, p2 - p1)) > 1e-7:
            out.append(p1)
    return out


def rotate_about(mesh, m, center):
    """Mesh rotated by the 4x4 (rotation) matrix m about a point."""
    c = np.array(center, float)
    return mesh.apply(move_m(c) @ m @ move_m(-c))
