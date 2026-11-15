#!/usr/bin/env python3
"""Bakes the hit shapes of the wild species (GLOSSARY "Hit shape"): what a bullet is tested against, and what the hit diagram draws.

A hit shape is a grid of 5 cm cells on the animal's side plane (its length ahead of its origin, its height above the ground). A cell the
animal covers holds its half width there, in centimetres, and whether it is head. A bullet hits when its path through the animal's frame
crosses a covered cell no farther from the middle plane than that half width. The shapes come from the model named in animals.json, posed by
its own clips as the game plays them:

The standing (idle) shape carries the look's one picture for the hit diagram: a small PNG over the same grid, the animal standing, drawn
smooth. The diagram always shows that picture and moves the hit onto it (AnimalShape.MapOnto).

    idle   one shape: everything the Idle and Idle_2 clips cover (a client plays either)
    eat    a shape every 0.2 s of the Eating clip (the head goes down and comes up); the server picks by the time the animal has eaten
    walk   one shape: everything the Walk clip covers
    run    one shape: everything the Gallop / Run clip covers (the stride's phase on a client is not known to the server)

    python3 scripts/make_animal_shapes.py

A species with other looks (variants in animals.json) gets a shape set per look, under "<species>/<variant>".

A model of the free packs (assets/) goes to godot/shooter/data/animal_shapes.json, in the repository; a model of the paid packs
(assets-private/) to assets-private/animal-shapes.json, outside it. The game reads both; a species in neither is tested against its plain
body box. Deterministic. Needs numpy and Pillow.
"""
import base64
import importlib.util
import io
import json
import re
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

_spec = importlib.util.spec_from_file_location("make_animal_silhouettes", Path(__file__).resolve().with_name("make_animal_silhouettes.py"))
glb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(glb)

ROOT = glb.ROOT
CELL = 0.05          # metres
SUB = 6              # pixels per cell when the model is drawn
COVER = 0.25         # a cell counts when the animal covers this much of it
HEAD_SHARE = 0.4     # and is head when this much of that is head
EAT_STEP = 0.2       # seconds between two eat shapes
ART = 8              # pixels per cell of a shape's picture (the hit diagram), drawn at twice that and scaled down
CLIPS = {"idle": ["Idle", "Idle_2"], "eat": ["Eat", "Eating"], "walk": ["Walk"], "run": ["Run", "Gallop"]}
HEAD_BONE = re.compile(r"Head|Ear\d|Eye|Neck_Top")  # the paid packs' rigs carry the skull on Neck_Top


class Model:
    """A skinned model that can be posed by one of its clips at a time."""

    def __init__(self, path: Path, scale: float):
        self.doc, self.blob = glb.read_glb(path)
        self.scale = scale
        self.nodes = self.doc["nodes"]
        self.clips = {}
        for a in self.doc.get("animations", []):
            name = a.get("name", "").split("|")[-1]
            if name not in self.clips:
                self.clips[name] = a
        self.prims = []
        for i, node in enumerate(self.nodes):
            if "mesh" not in node:
                continue
            for prim in self.doc["meshes"][node["mesh"]]["primitives"]:
                if prim.get("mode", 4) != 4:
                    continue
                at = prim["attributes"]
                n = self.doc["accessors"][at["POSITION"]]["count"]
                pos = np.c_[glb.accessor(self.doc, self.blob, at["POSITION"]), np.ones(n)]
                idx = glb.accessor(self.doc, self.blob, prim["indices"]).astype(int).reshape(-1) if "indices" in prim else np.arange(n)
                skinned = "skin" in node and "JOINTS_0" in at
                joints = weights = inverse = bones = None
                head = np.zeros(n)
                if skinned:
                    skin = self.doc["skins"][node["skin"]]
                    bones = skin["joints"]
                    inverse = glb.accessor(self.doc, self.blob, skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
                    joints = glb.accessor(self.doc, self.blob, at["JOINTS_0"]).astype(int)
                    weights = glb.accessor(self.doc, self.blob, at["WEIGHTS_0"])
                    weights = weights / np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)
                    is_head = np.array([bool(HEAD_BONE.search(self.nodes[j].get("name", ""))) for j in bones])
                    head = (weights * is_head[joints]).sum(axis=1)
                self.prims.append((i, pos, idx, joints, weights, inverse, bones, head[idx].reshape(-1, 3).mean(axis=1) > 0.5))
        self.head = np.concatenate([p[7] for p in self.prims])

    def length(self, clip: str) -> float:
        a = self.clips[clip]
        return max(self.doc["accessors"][s["input"]]["max"][0] for s in a["samplers"])

    def pose(self, clip: str, time: float) -> np.ndarray:
        """Every triangle at `time` of `clip`, metres, in the model's frame: (n, 3, 3)."""
        over = {}
        anim = self.clips[clip]
        for ch in anim["channels"]:
            sm = anim["samplers"][ch["sampler"]]
            ts = glb.accessor(self.doc, self.blob, sm["input"]).reshape(-1)
            vs = glb.accessor(self.doc, self.blob, sm["output"])
            if sm.get("interpolation") == "CUBICSPLINE":
                vs = vs.reshape(len(ts), 3, -1)[:, 1]
            i = int(np.clip(np.searchsorted(ts, time, side="right") - 1, 0, len(ts) - 1))
            j = min(i + 1, len(ts) - 1)
            k = 0.0 if ts[j] == ts[i] or sm.get("interpolation") == "STEP" else float(np.clip((time - ts[i]) / (ts[j] - ts[i]), 0, 1))
            a, b = vs[i], vs[j]
            if ch["target"]["path"] == "rotation":
                if np.dot(a, b) < 0:
                    b = -b
                v = a + (b - a) * k
                v = v / np.linalg.norm(v)
            else:
                v = a + (b - a) * k
            over.setdefault(ch["target"]["node"], {})[ch["target"]["path"]] = v
        world = [None] * len(self.nodes)

        def local(i: int) -> np.ndarray:
            n = self.nodes[i]
            if i not in over or "matrix" in n:
                return glb.local_matrix(n)
            o = over[i]
            return glb.local_matrix({"translation": list(o.get("translation", n.get("translation", [0, 0, 0]))),
                                     "rotation": list(o.get("rotation", n.get("rotation", [0, 0, 0, 1]))),
                                     "scale": list(o.get("scale", n.get("scale", [1, 1, 1])))})

        def walk(i: int, parent: np.ndarray) -> None:
            world[i] = parent @ local(i)
            for c in self.nodes[i].get("children", []):
                walk(c, world[i])

        for root in self.doc["scenes"][self.doc.get("scene", 0)]["nodes"]:
            walk(root, np.eye(4))
        out = []
        for i, pos, idx, joints, weights, inverse, bones, _ in self.prims:
            if joints is not None:
                mats = np.stack([world[j] @ inverse[k] for k, j in enumerate(bones)])
                placed = np.einsum("vk,vkij,vj->vi", weights, mats[joints], pos)[:, :3]
            else:
                placed = (pos @ world[i].T)[:, :3]
            out.append(placed[idx].reshape(-1, 3, 3))
        return np.concatenate(out) * self.scale


def picture(shown: np.ndarray, z0: float, cols: int, rows: int) -> str:
    """The standing shape's picture for the hit diagram, a PNG over the grid's own bounds (so a place on the grid is the same place on the
    picture): the animal in the one pose `shown`, solid and smooth."""
    w, h = cols * ART * 2, rows * ART * 2
    alpha = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(alpha)

    def fill(tris: np.ndarray, value: int) -> None:
        px = (tris[:, :, 2] - z0) / CELL * ART * 2
        py = h - tris[:, :, 1] / CELL * ART * 2
        for a, b in zip(px, py):
            d.polygon(list(zip(a, b)), fill=value)

    fill(shown, 255)
    alpha = alpha.resize((cols * ART, rows * ART), Image.LANCZOS)
    alpha = alpha.point(lambda v: min(255, round(v / 17) * 17))   # 16 levels: the edges stay smooth and the file is a third the size
    out = io.BytesIO()
    Image.merge("LA", (Image.new("L", alpha.size, 255), alpha)).save(out, "PNG", optimize=True)
    return base64.b64encode(out.getvalue()).decode()


def shape(model: Model, poses: list[np.ndarray], shown: np.ndarray | None = None) -> dict:
    """The cells the animal covers in any of the poses, and with `shown` its picture in that pose. The grid starts at a whole cell behind the
    rearmost point, on the ground."""
    pts = np.concatenate([p.reshape(-1, 3) for p in poses])
    z0 = np.floor(pts[:, 2].min() / CELL) * CELL
    cols = int(np.ceil((pts[:, 2].max() - z0) / CELL))
    rows = int(np.ceil(max(pts[:, 1].max(), CELL) / CELL))
    w, h = cols * SUB, rows * SUB
    width = Image.new("L", (w, h), 0)   # half width in centimetres, the widest of what was drawn there
    head = Image.new("L", (w, h), 0)
    dw, dh = ImageDraw.Draw(width), ImageDraw.Draw(head)
    for tris in poses:
        half = np.clip(np.ceil(np.abs(tris[:, :, 0]).max(axis=1) * 100), 1, 127).astype(int)
        px = (tris[:, :, 2] - z0) / CELL * SUB
        py = h - tris[:, :, 1] / CELL * SUB
        for k in np.argsort(half):          # the widest last, so a pixel keeps the widest triangle over it
            poly = list(zip(px[k], py[k]))
            dw.polygon(poly, fill=int(half[k]))
            if model.head[k]:
                dh.polygon(poly, fill=255)
    wa = np.array(width).reshape(rows, SUB, cols, SUB)
    ha = (np.array(head) > 0).reshape(rows, SUB, cols, SUB)
    cover = (wa > 0).mean(axis=(1, 3))
    cells = np.zeros((rows, cols), dtype=np.uint8)
    on = cover >= COVER
    cells[on] = wa.max(axis=(1, 3))[on]
    is_head = on & (ha.mean(axis=(1, 3)) >= HEAD_SHARE * cover)
    cells[is_head] |= 0x80
    # row 0 is the ground row: the picture's rows run from the top
    out = {"z0": round(float(z0), 3), "cols": cols, "rows": rows, "cells": base64.b64encode(np.flipud(cells).tobytes()).decode()}
    if shown is not None:
        out["picture"] = picture(shown, z0, cols, rows)
    return out


def bake(model: Model) -> dict | None:
    def clip_of(kind: str) -> list[str]:
        return [c for c in CLIPS[kind] if c in model.clips]

    if not clip_of("idle"):
        return None
    out = {}
    idle = [model.pose(c, t) for c in clip_of("idle") for t in np.arange(0, model.length(c), 0.25)]
    out["idle"] = {"frames": [shape(model, idle, idle[0])]}   # the one picture of the look: standing, the first frame of Idle
    for kind in ("walk", "run"):
        if clip_of(kind):
            c = clip_of(kind)[0]
            out[kind] = {"frames": [shape(model, [model.pose(c, t) for t in np.linspace(0, model.length(c), 12, endpoint=False)])]}
    if clip_of("eat"):
        c = clip_of("eat")[0]
        length = model.length(c)
        frames = [shape(model, [model.pose(c, (t + dt) % length) for dt in (-EAT_STEP / 2, 0, EAT_STEP / 2)]) for t in np.arange(0, length, EAT_STEP)]
        out["eat"] = {"step": EAT_STEP, "length": round(float(length), 3), "frames": frames}
    return out


def main() -> None:
    animals = {x["id"]: x for x in glb.load_jsonc(glb.DATA / "animals.json")["animals"]}
    free, paid = {}, {}
    for species in glb.load_jsonc(glb.DATA / "wildlife.json")["species"]:
        a = animals[species["id"]]
        # the species' own model, then each other look of it (a variant) under "<species>/<variant>"
        for key, rel in [(a["id"], a["model"])] + [(f"{a['id']}/{v['id']}", v["model"]) for v in a.get("variants", [])]:
            for base, into in ((ROOT / "assets", free), (ROOT / "assets-private", paid)):
                path = base / rel
                if not path.exists():
                    continue
                baked = bake(Model(path, a["scale"]))
                if baked is None:
                    print(f"{key}: no idle clip in {rel}, skipped")
                    break
                into[key] = baked
                one = baked["idle"]["frames"][0]
                print(f"{key}: idle {one['cols']}x{one['rows']} cells from z {one['z0']:+.2f} m, poses {', '.join(f'{k} ({len(v['frames'])})' for k, v in baked.items())}")
                break
            else:
                print(f"{key}: model not found ({rel}), skipped")
    note = "Generated by scripts/make_animal_shapes.py from the models and clips named in animals.json. Do not edit."
    for shapes, out in ((free, glb.DATA / "animal_shapes.json"), (paid, ROOT / "assets-private/animal-shapes.json")):
        if not shapes and not out.exists():
            continue
        out.write_text(json.dumps({"version": 1, "_note": note, "cell": CELL, "species": shapes}, separators=(",", ":")) + "\n")
        print(f"{out.relative_to(ROOT)}: {len(shapes)} species, {out.stat().st_size // 1024} kB")


if __name__ == "__main__":
    main()
