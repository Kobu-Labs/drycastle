#!/usr/bin/env python3
"""Draws a skinned .glb as the file itself says (its skin, its clips), without the game: a turnaround of the rest pose and a contact sheet per clip.

    python3 scripts/preview_glb_clips.py model.glb out_dir [--frames 8] [--size 300] [--clips Idle,Walk]

Writes out_dir/turnaround.png and out_dir/clip_<name>.png (each frame seen from the side and, under it, from the front quarter; time runs left to right, then down).
A dev aid for generated models (scripts/make_bigfoot_model.py). Flat shading, no perspective. Needs numpy and Pillow.
"""
import argparse
import json
import math
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

CTYPE = {5120: "i1", 5121: "u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_glb(path):
    data = Path(path).read_bytes()
    n = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20:20 + n])
    return doc, data[20 + n + 8:]


def accessor(doc, blob, i):
    a = doc["accessors"][i]
    v = doc["bufferViews"][a["bufferView"]]
    w = WIDTH[a["type"]]
    off = v.get("byteOffset", 0) + a.get("byteOffset", 0)
    arr = np.frombuffer(blob, CTYPE[a["componentType"]], a["count"] * w, off)
    return arr.reshape(a["count"], w) if w > 1 else arr


def quat_matrix(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def linear_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


class Model:
    def __init__(self, path):
        self.doc, self.blob = read_glb(path)
        d = self.doc
        self.parent = {}
        for i, n in enumerate(d["nodes"]):
            for c in n.get("children", []):
                self.parent[c] = i
        self.clips = {}
        for a in d.get("animations", []):
            tracks = []
            for ch in a["channels"]:
                s = a["samplers"][ch["sampler"]]
                tracks.append((ch["target"]["node"], ch["target"]["path"], accessor(d, self.blob, s["input"]), accessor(d, self.blob, s["output"])))
            self.clips[a["name"]] = (tracks, max(float(t[2][-1]) for t in tracks))
        self.prims = []
        for ni, n in enumerate(d["nodes"]):
            if "mesh" not in n:
                continue
            skin = d["skins"][n["skin"]] if "skin" in n else None
            for p in d["meshes"][n["mesh"]]["primitives"]:
                at = p["attributes"]
                mat = d["materials"][p["material"]]
                col = np.array(mat["pbrMetallicRoughness"]["baseColorFactor"][:3])
                emis = np.array(mat.get("emissiveFactor", [0, 0, 0])) * mat.get("extensions", {}).get("KHR_materials_emissive_strength", {}).get("emissiveStrength", 1.0)
                self.prims.append({
                    "node": ni, "pos": accessor(d, self.blob, at["POSITION"]).astype(float), "idx": accessor(d, self.blob, p["indices"]).astype(int).reshape(-1, 3),
                    "col": col, "emis": emis, "skin": skin,
                    "joints": accessor(d, self.blob, at["JOINTS_0"]).astype(int) if skin else None,
                    "weights": accessor(d, self.blob, at["WEIGHTS_0"]).astype(float) if skin else None,
                    "inverse": accessor(d, self.blob, skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1) if skin else None,
                })

    def world(self, clip=None, t=0.0):
        """Every node's world matrix, posed by a clip at a time."""
        local = {}
        for i, n in enumerate(self.doc["nodes"]):
            local[i] = [np.array(n.get("translation", [0, 0, 0]), float), np.array(n.get("rotation", [0, 0, 0, 1]), float), np.array(n.get("scale", [1, 1, 1]), float)]
        if clip:
            for node, path, times, vals in self.clips[clip][0]:
                j = int(np.clip(np.searchsorted(times, t, side="right") - 1, 0, len(times) - 2))
                u = float(np.clip((t - times[j]) / max(times[j + 1] - times[j], 1e-9), 0, 1))
                a, b = vals[j].astype(float), vals[j + 1].astype(float)
                if path == "rotation":
                    if a @ b < 0:
                        b = -b
                    q = a + (b - a) * u
                    local[node][1] = q / np.linalg.norm(q)
                else:
                    local[node][{"translation": 0, "scale": 2}[path]] = a + (b - a) * u
        out = {}

        def get(i):
            if i not in out:
                tr, q, s = local[i]
                m = np.eye(4)
                m[:3, :3] = quat_matrix(q) * s
                m[:3, 3] = tr
                out[i] = get(self.parent[i]) @ m if i in self.parent else m
            return out[i]
        for i in local:
            get(i)
        return out

    def triangles(self, clip=None, t=0.0):
        """Posed triangles: positions (n, 3, 3), base colours (n, 3), emission (n, 3)."""
        world = self.world(clip, t)
        tris, cols, emis = [], [], []
        for p in self.prims:
            pos = np.concatenate([p["pos"], np.ones((len(p["pos"]), 1))], axis=1)
            if p["skin"]:
                mats = np.stack([world[j] @ p["inverse"][k] for k, j in enumerate(p["skin"]["joints"])])
                placed = np.einsum("vk,vkij,vj->vi", p["weights"], mats[p["joints"]], pos)[:, :3]
            else:
                placed = (pos @ world[p["node"]].T)[:, :3]
            tris.append(placed[p["idx"]])
            cols.append(np.tile(p["col"], (len(p["idx"]), 1)))
            emis.append(np.tile(p["emis"], (len(p["idx"]), 1)))
        return np.concatenate(tris), np.concatenate(cols), np.concatenate(emis)


def view_matrix(yaw_deg, pitch_deg):
    """The camera looks at the model from this compass angle (0: from the front, the model facing -Z; 90: from its right) and height."""
    y, p = math.radians(yaw_deg), math.radians(pitch_deg)
    ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    rx = np.array([[1, 0, 0], [0, math.cos(p), -math.sin(p)], [0, math.sin(p), math.cos(p)]])
    flip = np.diag([-1.0, 1.0, -1.0])  # from the front: its right is on our left
    return rx @ flip @ ry


SS = 2
LIGHT = np.array([-0.45, 0.75, 0.5]) / np.linalg.norm([-0.45, 0.75, 0.5])
BG = np.array([0.62, 0.66, 0.70])


def render(tris, cols, emis, view, size, box):
    """box: (x0, x1, y0, y1) in view space, mapped to the picture; size: (w, h) pixels."""
    w, h = size[0] * SS, size[1] * SS
    img = np.tile(BG, (h, w, 1))
    depth = np.full((h, w), -1e9)
    p = tris @ view.T
    scale = min(w / (box[1] - box[0]), h / (box[3] - box[2]))
    sx = (p[..., 0] - box[0]) * scale
    sy = h - (p[..., 1] - box[2]) * scale
    ground = int(h - (0 - box[2]) * scale)
    if 0 <= ground < h:
        img[ground:, :] = BG * 0.8
    n = np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0])
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    shade = 0.42 + 0.58 * np.clip(n @ LIGHT, 0, 1) + 0.12 * np.clip(n[:, 1], 0, 1)
    rgb = linear_to_srgb(cols * shade[:, None] + emis)
    for i in np.nonzero(n[:, 2] > 0)[0]:
        x, y, z = sx[i], sy[i], p[i, :, 2]
        x0, x1 = max(int(x.min()), 0), min(int(x.max()) + 1, w - 1)
        y0, y1 = max(int(y.min()), 0), min(int(y.max()) + 1, h - 1)
        if x0 > x1 or y0 > y1:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        den = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2])
        if abs(den) < 1e-9:
            continue
        a = ((y[1] - y[2]) * (gx - x[2]) + (x[2] - x[1]) * (gy - y[2])) / den
        b = ((y[2] - y[0]) * (gx - x[2]) + (x[0] - x[2]) * (gy - y[2])) / den
        c = 1 - a - b
        zz = a * z[0] + b * z[1] + c * z[2]
        d = depth[y0:y1 + 1, x0:x1 + 1]
        hit = (a >= 0) & (b >= 0) & (c >= 0) & (zz > d)
        d[hit] = zz[hit]
        img[y0:y1 + 1, x0:x1 + 1][hit] = rgb[i]
    out = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    return out.resize(size, Image.LANCZOS)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("out")
    ap.add_argument("--frames", type=int, default=8)
    ap.add_argument("--size", type=int, default=300)
    ap.add_argument("--clips", default="")
    ap.add_argument("--cols", type=int, default=4, help="frames across before the sheet wraps")
    ap.add_argument("--span", type=float, default=0, help="metres across a cell (0: fitted to the rest pose)")
    args = ap.parse_args()
    model = Model(args.model)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    rest, cols, emis = model.triangles()
    lo, hi = rest.reshape(-1, 3).min(axis=0), rest.reshape(-1, 3).max(axis=0)
    span = args.span or max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]) * 1.15
    cy = (hi[1] + lo[1]) / 2

    views = [("front", 0, 0), ("front quarter", 35, 8), ("side", 90, 0), ("back quarter", 145, 8), ("back", 180, 0), ("above", 25, 40)]
    s = args.size * 2
    sheet = Image.new("RGB", (s * 3, s * 2))
    draw = ImageDraw.Draw(sheet)
    for i, (label, yaw, pitch) in enumerate(views):
        cell = render(rest, cols, emis, view_matrix(yaw, pitch), (s, s), (-span / 2, span / 2, cy - span / 2, cy + span / 2))
        sheet.paste(cell, ((i % 3) * s, (i // 3) * s))
        draw.text(((i % 3) * s + 8, (i // 3) * s + 6), label, fill=(0, 0, 0))
    sheet.save(out / "turnaround.png")
    print(out / "turnaround.png")

    wanted = [c for c in args.clips.split(",") if c] or list(model.clips)
    for name in wanted:
        length = model.clips[name][1]
        looped = "loop" in name.lower() or name in ("Idle", "Walk", "Run")
        times = [length * k / (args.frames if looped else args.frames - 1) for k in range(args.frames)]
        posed = [model.triangles(name, t) for t in times]
        every = np.concatenate([p[0].reshape(-1, 3) for p in posed])
        cw, h, ncols = int(args.size * 1.5), args.size, min(args.cols, args.frames)
        sheet = Image.new("RGB", (cw * ncols, h * 2 * math.ceil(args.frames / ncols)))
        draw = ImageDraw.Draw(sheet)
        for row, (yaw, pitch) in enumerate(((90, 0), (30, 10))):
            view = view_matrix(yaw, pitch)
            seen = every @ view.T
            mid = (seen[:, 0].min() + seen[:, 0].max()) / 2
            wide = max(seen[:, 0].max() - seen[:, 0].min(), (seen[:, 1].max() + 0.3) * cw / h) * 1.06  # one scale for the whole clip
            box = (mid - wide / 2, mid + wide / 2, -0.3, -0.3 + wide * h / cw)
            for k, (tris, cols, emis) in enumerate(posed):
                sheet.paste(render(tris, cols, emis, view, (cw, h), box), ((k % ncols) * cw, (k // ncols * 2 + row) * h))
        for k, t in enumerate(times):
            draw.text(((k % ncols) * cw + 6, (k // ncols) * 2 * h + 4), f"{name} {t:.2f}s", fill=(0, 0, 0))
        sheet.save(out / f"clip_{name}.png")
        print(out / f"clip_{name}.png")


if __name__ == "__main__":
    main()
