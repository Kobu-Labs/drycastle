#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/rapeseed_plant.glb: one oilseed-rape plant for the `crop_rapeseed` field type (#328): a thin green
stem, a ring of flat leaves at the foot, five side branches and a big yellow flower head on every branch and on top. Very simple and flat-coloured
like the rest of the map, about 280 triangles, meant to be instanced hundreds of times per field.

Original geometry written as code (no third-party model, no texture); standard library only. The mesh helpers are scripts/lib/meshkit.py:

    python3 -I scripts/models/make_rapeseed_model.py [out.glb]

Frame: metres, x right, y up, facing -Z, origin on the ground at the foot of the stem. About 0.9 m tall, 0.7 m across the heads. One mesh node
("rapeseed_plant"), 4 flat-colour materials.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from glb import materials_from, write_glb
from meshkit import Node, box, cyl, lathe, move, rot

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength)
    "Stem": (0.36, 0.55, 0.22, 0.9, 0.0, 0.0),
    "Leaf": (0.25, 0.46, 0.20, 0.9, 0.0, 0.0),
    "Yellow": (1.00, 0.83, 0.10, 0.8, 0.0, 0.12),       # the top of a flower head
    "YellowDeep": (0.92, 0.68, 0.06, 0.8, 0.0, 0.06),   # its underside
}

STEM_H = 0.62                       # the main stem; its head sits on the top
BRANCH_AT = (0.36, 0.46, 0.55)      # where the five side branches leave the stem
BRANCH_LEN, BRANCH_TILT = 0.24, 34  # metres, degrees off vertical
LEAVES = ((0.07, 0.0), (0.07, 90.0), (0.07, 180.0), (0.07, 270.0), (0.26, 45.0), (0.30, 225.0))  # (height, yaw): lower four big, upper two small


def head(scale):
    """A flower head: a squat dome, deep yellow below and bright yellow on top."""
    s = scale
    return lathe((0, 0, 0), (0, 1, 0), [(0, 0, "YellowDeep"), (0.02 * s, 0.062 * s), (0.040 * s, 0.056 * s, "Yellow"), (0.062 * s, 0.030 * s), (0.070 * s, 0.0)], n=6, smooth=False)


def leaf(y, yaw, size):
    """A flat leaf rising from the stem at an angle, turned round the stem by yaw."""
    t = box("Leaf", 0.012, 0.012 + 0.22 * size, -0.005, 0.005, -0.038 * size, 0.038 * size)
    return rot(move(rot(t, 'z', 24), (0, y, 0)), 'y', yaw)


def build():
    root = Node("rapeseed_plant")
    root.add(cyl("Stem", (0, 0, 0), (0, STEM_H, 0), 0.013, n=4, r1=0.008))
    root.add(move(head(2.0), (0, STEM_H - 0.01, 0)))
    for i, y in enumerate(BRANCH_AT):
        yaw = 144.0 * i + 25.0
        b = cyl("Stem", (0, 0, 0), (0, BRANCH_LEN, 0), 0.007, n=4, r1=0.005)
        tip = (0, BRANCH_LEN - 0.01, 0)
        b = b + move(head(1.5), tip)
        root.add(rot(move(rot(b, 'z', BRANCH_TILT), (0, y, 0)), 'y', yaw))
    for y, yaw in LEAVES:
        root.add(leaf(y, yaw, 1.0 if y < 0.2 else 0.6))
    return root


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parents[2] / "assets" / "drycastle-generated-models" / "rapeseed_plant.glb")
    root = build()
    write_glb(root, out, materials_from(MATERIALS), "scripts/make_rapeseed_model.py (Drycastle)", "rapeseed_plant")
    lo, hi = [1e9] * 3, [-1e9] * 3
    for _, ps, _ in root.tris:
        for p in ps:
            for i in range(3):
                lo[i], hi[i] = min(lo[i], p[i]), max(hi[i], p[i])
    print(f"triangles: {len(root.tris)}")
    print("bounds: x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f  size %.2f x %.2f x %.2f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
