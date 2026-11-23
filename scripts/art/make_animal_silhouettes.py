#!/usr/bin/env python3
"""Bakes the side-view silhouette of each wild species for the hunting hit diagram (GLOSSARY "Hit diagram").

For every species of godot/shooter/data/wildlife.json it reads the model named in animals.json, poses it at rest (the skin's joints at their node
transforms), projects every triangle on the side plane (the model's length left to right with the head on the right, its height up) and fills
them: a white shape on a transparent ground, cropped to the model's bounds, so the image is the species' hit box seen from its right flank.

    python3 scripts/make_animal_silhouettes.py [--width 384]

A model of the free packs (assets/) is written to godot/shooter/ui/icons/animals/<id>.png, in the repository. A model of the paid packs
(assets-private/) is written to assets-private/animal-silhouettes/<id>.png, outside it: the game reads both, and draws a plain body shape for a
species with neither. Deterministic. Needs numpy and Pillow.
"""
import argparse
import json
import re
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "godot/shooter/data"
SS = 4  # supersampling of the fill

COMPONENTS = {5120: "i1", 5121: "u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
WIDTHS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def load_jsonc(path: Path) -> dict:
    text = re.sub(r"^\s*//.*$", "", path.read_text(), flags=re.M)
    return json.loads(re.sub(r",(\s*[\]}])", r"\1", text))


def read_glb(path: Path) -> tuple[dict, bytes]:
    raw = path.read_bytes()
    assert raw[:4] == b"glTF", path
    at, doc, blob = 12, None, b""
    while at < len(raw):
        size, kind = struct.unpack_from("<II", raw, at)
        chunk = raw[at + 8:at + 8 + size]
        if kind == 0x4E4F534A:
            doc = json.loads(chunk)
        elif kind == 0x004E4942:
            blob = chunk
        at += 8 + size
    return doc, blob


def accessor(doc: dict, blob: bytes, index: int) -> np.ndarray:
    a = doc["accessors"][index]
    view = doc["bufferViews"][a["bufferView"]]
    dtype = np.dtype(COMPONENTS[a["componentType"]])
    width = WIDTHS[a["type"]]
    start = view.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = view.get("byteStride") or dtype.itemsize * width
    rows = np.lib.stride_tricks.as_strided(
        np.frombuffer(blob, dtype=np.uint8, offset=start, count=stride * (a["count"] - 1) + dtype.itemsize * width),
        shape=(a["count"], dtype.itemsize * width), strides=(stride, 1))
    out = np.ascontiguousarray(rows).view(dtype).reshape(a["count"], width).astype(np.float64)
    if a.get("normalized"):
        out /= np.iinfo(dtype).max
    return out


def local_matrix(node: dict) -> np.ndarray:
    if "matrix" in node:
        return np.array(node["matrix"], dtype=np.float64).reshape(4, 4).T
    m = np.eye(4)
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    m[:3, :3] = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]) * np.array(node.get("scale", [1, 1, 1]))
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


def triangles(path: Path) -> np.ndarray:
    """Every triangle of the model at rest, in the model's own frame: (n, 3, 3)."""
    doc, blob = read_glb(path)
    nodes = doc["nodes"]
    world = [None] * len(nodes)

    def walk(i: int, parent: np.ndarray) -> None:
        world[i] = parent @ local_matrix(nodes[i])
        for c in nodes[i].get("children", []):
            walk(c, world[i])

    for root in doc["scenes"][doc.get("scene", 0)]["nodes"]:
        walk(root, np.eye(4))

    out = []
    for i, node in enumerate(nodes):
        if "mesh" not in node or world[i] is None:
            continue
        for prim in doc["meshes"][node["mesh"]]["primitives"]:
            if prim.get("mode", 4) != 4:
                continue
            attrs = prim["attributes"]
            pos = np.c_[accessor(doc, blob, attrs["POSITION"]), np.ones(doc["accessors"][attrs["POSITION"]]["count"])]
            if "skin" in node and "JOINTS_0" in attrs:
                skin = doc["skins"][node["skin"]]
                inverse = accessor(doc, blob, skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
                bones = np.stack([world[j] @ inverse[k] for k, j in enumerate(skin["joints"])])
                joints = accessor(doc, blob, attrs["JOINTS_0"]).astype(int)
                weights = accessor(doc, blob, attrs["WEIGHTS_0"])
                weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)
                placed = np.einsum("vk,vkij,vj->vi", weights, bones[joints], pos)[:, :3]
            else:
                placed = (pos @ world[i].T)[:, :3]
            idx = accessor(doc, blob, prim["indices"]).astype(int).reshape(-1) if "indices" in prim else np.arange(len(placed))
            out.append(placed[idx].reshape(-1, 3, 3))
    return np.concatenate(out)


def bake(model: Path, out: Path, width: int) -> str:
    tris = triangles(model)
    pts = tris.reshape(-1, 3)
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    length, height = hi[2] - lo[2], hi[1] - lo[1]
    w, h = width * SS, max(1, round(width * SS * height / length))
    img = Image.new("L", (w, h), 0)
    draw = ImageDraw.Draw(img)
    # the models face +Z: the head is on the right
    px = (tris[:, :, 2] - lo[2]) / length * (w - 1)
    py = (hi[1] - tris[:, :, 1]) / height * (h - 1)
    for a, b in zip(px, py):
        draw.polygon(list(zip(a, b)), fill=255)
    alpha = img.resize((width, max(1, h // SS)), Image.LANCZOS)
    white = Image.new("L", alpha.size, 255)
    out.parent.mkdir(parents=True, exist_ok=True)
    Image.merge("LA", (white, alpha)).save(out, optimize=True)
    off = (hi[2] + lo[2]) / 2
    return f"{alpha.size[0]}x{alpha.size[1]}, model {hi[0] - lo[0]:.2f} x {height:.2f} x {length:.2f}, centre {off / length:+.0%} of its length ahead of the origin"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--width", type=int, default=384)
    a = ap.parse_args()
    animals = {x["id"]: x for x in load_jsonc(DATA / "animals.json")["animals"]}
    for species in load_jsonc(DATA / "wildlife.json")["species"]:
        sid = species["id"]
        rel = animals[sid]["model"]
        free, paid = ROOT / "assets" / rel, ROOT / "assets-private" / rel
        if free.exists():
            model, out = free, ROOT / "godot/shooter/ui/icons/animals" / f"{sid}.png"
        elif paid.exists():
            model, out = paid, ROOT / "assets-private/animal-silhouettes" / f"{sid}.png"
        else:
            print(f"{sid}: model not found ({rel}), skipped")
            continue
        print(f"{sid}: {out.relative_to(ROOT)}: {bake(model, out, a.width)}")


if __name__ == "__main__":
    main()
