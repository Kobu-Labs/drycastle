#!/usr/bin/env python3
"""Top-view outlines of every vehicle with seats (issue #334, last step of #325) for the seat panel:
the model's meshes are projected onto the ground plane, closed and simplified to a polygon in vehicle-frame metres, and written to
godot/shooter/data/vehicle_outlines.json {"version":1,"outlines":{"<vehicle id>":[[x,z],...]}} (x right, z backward, so forward is -z).
Regenerate when a hull changes (docs/standards/car-design.md definition of done). Only a 2D silhouette is committed, never mesh data.

    python3 scripts/vehicles/make_vehicle_outlines.py [--private DIR] [--godot godot] [--only id,id]

Needs numpy + opencv (cv2), a godot binary on PATH (headless, scripts/vehicles/vehicle_model_dump.gd loads the GLBs like the game does) and, for `pack:` models,
the private pack (assets-private/). A vehicle whose model cannot be read keeps its previous entry; the game falls back to the hit-box rectangle for
a vehicle with no outline. Parts that are not hull (rotors, gun barrels, steering wheels, tow hooks) are left out; model_yaw_deg is applied.
"""
import argparse
import json
import math
import subprocess
import sys
import tempfile
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
VEHICLES = ROOT / "godot/shooter/data/vehicles.json"
OUT = ROOT / "godot/shooter/data/vehicle_outlines.json"
SKIP = ("rotor", "barrel", "steering_wheel", "tow_hook", "propeller", "missile", "steer_bars_none")
RES = 0.03          # raster cell, metres
CLOSE = 0.12        # gap closed between parts (a wheel and its arch), metres
MAX_POINTS = 48
MIN_POINTS = 20


def model_path(model: str, private: Path) -> Path:
    if model.startswith("pack:"):
        pack, name = model[5:].split("/", 1)
        pack = {"synty-vehicles": "vehicles-std", "synty-simple-military": "simple-military", "synty-farm": "farm"}[pack]
        return private / "glb" / pack / (name + ".glb")
    return ROOT / "assets" / model


def triangles(model: Path, godot: str, yaw: float, scale: float, offset) -> np.ndarray:
    with tempfile.TemporaryDirectory() as t:
        out = Path(t) / "d.json"
        subprocess.run([godot, "--headless", "--script", str(ROOT / "scripts/vehicles/vehicle_model_dump.gd"), "--", str(model), str(out)],
                       check=True, capture_output=True, cwd=ROOT)
        nodes = json.loads(out.read_text())["nodes"]
    tris = []
    c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    for n in nodes:
        name = n["path"].rsplit("/", 1)[-1].lower()
        if any(k in name for k in SKIP) or not n["i"]:
            continue
        v = np.array(n["v"], dtype=np.float64).reshape(-1, 3)
        if yaw:  # rotation about Y, as Node3D.rotation_degrees.y
            v = np.stack([v[:, 0] * c + v[:, 2] * s, v[:, 1], -v[:, 0] * s + v[:, 2] * c], 1)
        v = v * scale + np.array(offset)
        tris.append(v[:, [0, 2]][np.array(n["i"]).reshape(-1, 3)])
    return np.concatenate(tris)


def outline(tris: np.ndarray) -> list:
    lo = tris.reshape(-1, 2).min(0) - 0.5
    hi = tris.reshape(-1, 2).max(0) + 0.5
    w, h = np.ceil((hi - lo) / RES).astype(int)
    img = np.zeros((h, w), np.uint8)
    px = ((tris - lo) / RES).astype(np.float32)
    for t in px:
        cv2.fillConvexPoly(img, np.round(t * 16).astype(np.int32), 255, shift=4)
    k = int(CLOSE / RES) | 1
    img = cv2.morphologyEx(img, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    cs, _ = cv2.findContours(img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    big = max(cv2.contourArea(c) for c in cs)
    keep = [c for c in cs if cv2.contourArea(c) > 0.05 * big]
    mask = np.zeros_like(img)
    cv2.drawContours(mask, keep, -1, 255, -1)
    if len(keep) > 1:  # separate parts (a trailer hitch, a detached wheel): bridge them
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (int(0.6 / RES) | 1,) * 2))
    cs, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    c = max(cs, key=cv2.contourArea)
    eps = RES
    while True:
        a = cv2.approxPolyDP(c, eps, True)
        if len(a) <= MAX_POINTS:
            break
        eps *= 1.08
    pts = a.reshape(-1, 2) * RES + lo + RES / 2
    return [[round(float(x), 2), round(float(z), 2)] for x, z in pts]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--private", default=str(ROOT.parent.parent.parent / "assets-private/synty"))
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    private = Path(a.private)
    only = set(filter(None, a.only.split(",")))
    old = json.loads(OUT.read_text())["outlines"] if OUT.exists() else {}
    res = dict(old)
    for v in json.loads(VEHICLES.read_text())["vehicles"]:
        if not v.get("seats") or (only and v["id"] not in only):
            continue
        p = model_path(v["model"], private)
        if not p.exists():
            print(f"{v['id']}: {p} missing, entry kept", file=sys.stderr)
            continue
        o = outline(triangles(p, a.godot, v.get("model_yaw_deg", 0), v.get("model_scale", 1), v.get("model_offset", [0, 0, 0])))
        res[v["id"]] = o
        xs, zs = [q[0] for q in o], [q[1] for q in o]
        print(f"{v['id']}: {len(o)} points, x {min(xs)}..{max(xs)} z {min(zs)}..{max(zs)} (box {v.get('half_width')} x {v.get('half_length')})")
    ordered = {k: res[k] for k in sorted(res)}
    lines = ['{', '  "version": 1,',
             '  "_note": "Top-view silhouettes of the vehicles for the seat panel (issue #334): [x, z] in vehicle-frame metres (x right, z back, forward is -z), a closed polygon. Generated by scripts/vehicles/make_vehicle_outlines.py from the models; regenerate when a hull changes. A vehicle without one is drawn as its hit box.",',
             '  "outlines": {']
    items = [f'    "{k}": {json.dumps(v, separators=(",", ":"))}' for k, v in ordered.items()]
    lines.append(",\n".join(items))
    lines += ['  }', '}', '']
    OUT.write_text("\n".join(lines))


if __name__ == "__main__":
    main()
