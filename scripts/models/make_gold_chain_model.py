#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/gold_chain.glb: the Gold chain, the neck Item of the Thieves event (items.json id `gold_chain`):
a chunky necklace of interlocked links round the neck and over the collar, drooping to a diamond pendant on the chest. Flat-coloured and
faceted like the rest of the map. The Gang Warfare pack (private) may have a better one; point items.json `model` at it then.

Original geometry written as code (no third-party model, no texture); standard library only. The mesh helpers are scripts/lib/meshkit.py:

    python3 -I scripts/models/make_gold_chain_model.py [out.glb]

Frame (the slot frame of a worn neck Item, see items.json `model`): metres, x right, y up, FACING -Z (the game's forward), origin at the
base of the neck (the origin of the rig's Neck bone, see CharacterView.Worn.cs). The ring round the neck is 0.21 m wide at the collar, 0.07 m
below the origin, and the front of the chain stands 0.15 m clear of it and hangs 0.14 m lower, the pendant below that. One mesh node
("gold_chain"), 3 flat-colour materials, about 2400 triangles. The placement on the rig is a guess from the bone origins, not checked on a render.
"""
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from glb import materials_from, write_glb
from meshkit import Node, box, move, rot, sub, unit

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength)
    "Gold": (0.96, 0.75, 0.20, 0.35, 0.3, 0.18),      # every other link, the pendant
    "GoldDark": (0.78, 0.55, 0.10, 0.45, 0.3, 0.08),  # the links between
    "GoldLight": (1.00, 0.88, 0.45, 0.3, 0.3, 0.25),  # the pendant's face
}

LINKS = 38           # round the whole ring
A, B_FRONT, B_BACK = 0.105, 0.150, 0.085   # half width, and how far the ring reaches in front and behind the origin
Y_COLLAR, DROOP = -0.07, 0.14              # the height of the ring at the sides and back, and how far the front hangs below it
LINK_LEN, LINK_W, LINK_T = 0.036, 0.024, 0.013


def point(theta):
    """The chain's centre line at angle theta (0 = straight ahead, towards -z; positive turns towards +x)."""
    c = math.cos(theta)
    droop = DROOP * max(0.0, c) ** 1.6
    return (A * math.sin(theta), Y_COLLAR - droop, -(B_FRONT if c >= 0 else B_BACK) * c)


def link(i):
    th = 2 * math.pi * i / LINKS
    p, q = point(th), point(th + 0.01)
    t = unit(sub(q, p))
    horiz = math.hypot(t[0], t[2])
    yaw = math.degrees(math.atan2(-t[2], t[0]))
    pitch = math.degrees(math.atan2(t[1], horiz))
    # a link along +x; every other one stands on its edge, as in a real chain
    half = (LINK_LEN / 2, LINK_W / 2, LINK_T / 2)
    tris = box("Gold" if i % 2 == 0 else "GoldDark", -half[0], half[0], -half[1], half[1], -half[2], half[2], 0.004)
    if i % 2:
        tris = rot(tris, 'x', 90)
    return move(rot(rot(tris, 'z', pitch), 'y', yaw), p)


def pendant():
    """A diamond hanging from the lowest link, a little proud of the chain: a gold plate with a lighter face."""
    x, y, z = point(0.0)
    cy = y - 0.055
    plate = box("Gold", -0.034, 0.034, -0.042, 0.042, -0.009, 0.009, 0.006, 'z')
    face = box("GoldLight", -0.022, 0.022, -0.028, 0.028, -0.014, -0.006, 0.004, 'z')
    t = rot(plate + face, 'z', 45)
    return move(t, (x, cy, z - 0.004))


def build():
    root = Node("gold_chain")
    for i in range(LINKS):
        root.add(link(i))
    root.add(pendant())
    return root


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parents[2] / "assets" / "drycastle-generated-models" / "gold_chain.glb")
    root = build()
    write_glb(root, out, materials_from(MATERIALS), "scripts/make_gold_chain_model.py (Drycastle)", "gold_chain")
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
