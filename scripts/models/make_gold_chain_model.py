#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/gold_chain.glb: the Gold chain, the neck Item of the Thieves event (items.json id `gold_chain`):
a chunky necklace of interlocked links round the neck and over the collar, drooping to a diamond pendant on the chest. Flat-coloured and
faceted like the rest of the map. The Gang Warfare pack (private) may have a better one; point items.json `model` at it then.

Original geometry written as code (no third-party model, no texture); standard library only. The mesh helpers are the flak script's, loaded
from the file next to this one:

    python3 -I scripts/make_gold_chain_model.py [out.glb]

Frame (the slot frame of a worn neck Item, see items.json `model`): metres, x right, y up, FACING -Z (the game's forward), origin at the
base of the neck (the origin of the rig's Neck bone, see CharacterView.Worn.cs). The ring round the neck is 0.21 m wide at the collar, 0.07 m
below the origin, and the front of the chain stands 0.15 m clear of it and hangs 0.14 m lower, the pendant below that. One mesh node
("gold_chain"), 3 flat-colour materials, about 2400 triangles. The placement on the rig is a guess from the bone origins, not checked on a render.
"""
import importlib.util
import json
import math
import struct
import sys
from pathlib import Path

_spec = importlib.util.spec_from_file_location("make_flak_model", Path(__file__).resolve().with_name("make_flak_model.py"))
kit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(kit)

unit, sub = kit.unit, kit.sub
box, rot, move = kit.box, kit.rot, kit.move
Node = kit.Node

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


def srgb_to_linear(c): return kit.srgb_to_linear(c)


def write_glb(root, path):
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS)
    materials = []
    for name in mat_names:
        r, g, b, rough, metal, emis = MATERIALS[name]
        lin = [round(srgb_to_linear(v), 4) for v in (r, g, b)]
        m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": metal, "roughnessFactor": rough}}
        if emis:
            m["emissiveFactor"] = [round(v * emis, 4) for v in lin]
        materials.append(m)

    def view(data, target):
        while len(buf) % 4:
            buf.append(0)
        views.append({"buffer": 0, "byteOffset": len(buf), "byteLength": len(data), "target": target})
        buf.extend(data)
        return len(views) - 1

    nodes.append({"name": root.name, "mesh": 0})
    prims = []
    for mi, mname in enumerate(mat_names):
        verts, lookup, idx = [], {}, []
        for m, ps, nr in root.tris:
            if m != mname:
                continue
            fn = unit(kit.cross(sub(ps[1], ps[0]), sub(ps[2], ps[0])))
            for k in range(3):
                n = nr[k] if nr else fn
                key = tuple(round(v, 5) for v in ps[k]) + tuple(round(v, 3) for v in n)
                if key not in lookup:
                    lookup[key] = len(verts)
                    verts.append((ps[k], n))
                idx.append(lookup[key])
        if not idx:
            continue
        assert len(verts) < 65536
        pos = b"".join(struct.pack("<3f", *p) for p, _ in verts)
        nor = b"".join(struct.pack("<3f", *n) for _, n in verts)
        lo = [struct.unpack("<f", struct.pack("<f", min(p[i] for p, _ in verts)))[0] for i in range(3)]
        hi = [struct.unpack("<f", struct.pack("<f", max(p[i] for p, _ in verts)))[0] for i in range(3)]
        accessors.append({"bufferView": view(pos, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3", "min": lo, "max": hi})
        accessors.append({"bufferView": view(nor, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3"})
        accessors.append({"bufferView": view(struct.pack(f"<{len(idx)}H", *idx), 34963), "componentType": 5123, "count": len(idx), "type": "SCALAR"})
        a = len(accessors)
        prims.append({"attributes": {"POSITION": a - 3, "NORMAL": a - 2}, "indices": a - 1, "material": mi})
    meshes.append({"name": root.name, "primitives": prims})
    while len(buf) % 4:
        buf.append(0)
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_gold_chain_model.py (Drycastle)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": "gold_chain", "nodes": [0]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent / "assets" / "drycastle-generated-models" / "gold_chain.glb")
    root = build()
    write_glb(root, out)
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
