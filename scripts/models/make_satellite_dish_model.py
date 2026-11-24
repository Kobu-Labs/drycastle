#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/satellite_dish.glb: a household satellite dish, about 0.7 m across, on a short
wall arm: a wall plate, a pipe with a diagonal brace, a shallow light-grey dish tilted a little upwards and a feed arm with
the LNB at its focus. Low-poly, flat colours, like the other generated models.

Original geometry written by hand as code (no third-party model, no texture); standard library only. The mesh helpers
(box, lathe, cyl, ...) are scripts/lib/meshkit.py:

    python3 -I scripts/models/make_satellite_dish_model.py [out.glb]

Frame: glTF/Godot, metres, y up. The origin is the point on the wall where the arm leaves it (the middle of the wall plate)
and +X points OUT of the wall. A placed object turned by `rotation_deg` (compass degrees, counter-clockwise from +x on
the map, as the house connectors are) therefore points its dish away from a wall that faces that way, so the placer
sets `rotation_deg` to the way the wall faces and `z` to the height of the origin. The dish looks out and DISH_TILT_DEG
upwards, so it reaches 0.4 m above the origin: hang it at least that far under an eave. No collision of its own is
written here; the manifest entry decides.

Node tree: satellite_dish > mount (plate, pipe, brace, collar), dish (shell), lnb (feed arm, head).
"""
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from glb import materials_from, write_glb
from meshkit import Node, add, box, cyl, lathe, mul, report, sub

OUT = Path(__file__).resolve().parents[2] / "assets" / "drycastle-generated-models" / "satellite_dish.glb"

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic)
    "Dish": (0.84, 0.84, 0.82, 0.55, 0.0),      # the reflector's face
    "DishBack": (0.66, 0.67, 0.66, 0.7, 0.0),   # its back and rim
    "Arm": (0.52, 0.54, 0.55, 0.5, 0.6),        # galvanised wall plate, pipe, brace, feed arm
    "Lnb": (0.15, 0.16, 0.17, 0.5, 0.0),        # the receiver head
    "LnbCap": (0.55, 0.56, 0.52, 0.6, 0.0),     # its cap
}

# ----------------------------------------------------------------------------- dimensions

DISH_R = 0.35                     # reflector radius: 0.7 m across
FOCAL = 0.30                      # focal length; the depth of the dish is R^2 / (4 F)
DEPTH = DISH_R * DISH_R / (4 * FOCAL)
SHELL = 0.012                     # thickness of the reflector shell
DISH_TILT_DEG = 20.0              # the dish looks this far above the wall's normal
PIPE_END_X = 0.30                 # the pipe's end, where the dish is clamped
PIPE_R = 0.022
SEGMENTS = 16


def build():
    root = Node("satellite_dish")
    t = math.radians(DISH_TILT_DEG)
    d = (math.cos(t), math.sin(t), 0.0)          # axis of the dish, out of its face
    up = (-math.sin(t), math.cos(t), 0.0)        # across the dish in the xy plane
    vertex = (PIPE_END_X + 0.03, 0.03, 0.0)      # the middle of the reflector's face (its deepest point)

    mount = root.child("mount")
    mount.add(box("Arm", 0.0, 0.03, -0.09, 0.09, -0.07, 0.07, 0.006))                      # plate on the wall
    mount.add(cyl("Arm", (0.02, 0.0, 0.0), (PIPE_END_X, 0.0, 0.0), PIPE_R, 8))             # the pipe out of the wall
    mount.add(cyl("Arm", (0.03, -0.13, 0.0), (PIPE_END_X - 0.07, -0.012, 0.0), 0.012, 6))  # brace from the wall, below
    mount.add(box("Arm", 0.03, 0.05, -0.17, -0.09, -0.04, 0.04, 0.004))                    # the brace's foot plate
    mount.add(cyl("Arm", (PIPE_END_X - 0.03, 0.0, 0.0), (PIPE_END_X + 0.04, 0.03, 0.0), 0.034, 8))  # clamp collar to the dish

    dish = root.child("dish")
    radii = (0.12, 0.22, 0.30, DISH_R)
    face = [(0.0, 0.0, "Dish")] + [(r * r / (4 * FOCAL), r) for r in radii]            # the face, from the middle to the rim
    rim = [(DEPTH, DISH_R, "DishBack"), (DEPTH - SHELL, DISH_R)]                         # the rim's edge, then the back
    back = [(r * r / (4 * FOCAL) - SHELL, r) for r in reversed(radii[:-1])] + [(-SHELL, 0.0)]
    profile = face + rim + back
    dish.add(lathe(vertex, d, profile, SEGMENTS, smooth=True))

    lnb = root.child("lnb")
    focus = add(vertex, mul(d, FOCAL))
    foot = add(add(vertex, mul(d, DEPTH - 0.01)), mul(up, -(DISH_R - 0.02)))   # the arm leaves the lower rim
    head = add(focus, mul(d, 0.02))
    lnb.add(cyl("Arm", foot, sub(head, mul(d, 0.04)), 0.011, 6))
    lnb.add(lathe(sub(focus, mul(d, 0.05)), d, [(0, 0, "Lnb"), (0, 0.032), (0.07, 0.032), (0.07, 0.02, "LnbCap"), (0.10, 0.02), (0.10, 0)], 8, smooth=False))
    return root


# ----------------------------------------------------------------------------- GLB

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(OUT)
    root = build()
    write_glb(root, out, materials_from(MATERIALS, double_sided=True), "scripts/make_satellite_dish_model.py (Drycastle)")
    lo, hi = report(root)
    print("size_m: [%.2f, %.2f, %.2f]" % (hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
