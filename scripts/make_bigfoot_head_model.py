#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/bigfoot_head.glb: the Bigfoot head, the headgear Item of the Bigfoot event (items.json id
`bigfoot_head`): a big shaggy ape-like head worn over the whole head, flat-coloured and faceted like the rest of the map.

Original geometry written as code (no third-party model, no texture); standard library only. The mesh helpers (box, loft, cyl, ...) are
the flak script's, loaded from the file next to this one:

    python3 -I scripts/make_bigfoot_head_model.py [out.glb]

Frame (the slot frame of a worn headgear Item, see items.json `model`): metres, x right, y up, FACING -Z (the game's forward), origin at the
middle of the wearer's head. The game turns it to the character rig's +Z face and scales it to rig units (CharacterView.Worn.cs).
It is sized to swallow the heads of the four player bodies (head about 0.56 m wide, 0.6 m tall; the female body's hair included) with a few
centimetres to spare, so the helmet, hair, face paint and eyes of the body underneath are hidden:

    y -0.34 .. +0.48    the head mass (a faceted dome, narrower at the jaw), half width 0.37 at the brow, fur 0.37 m in front of the middle
    y +0.18 .. +0.60    the sagittal crest on top (the head's highest point)
    z -0.43 .. -0.29    the arched brow ridge and the dark face plate with eyes, nose, mouth and two teeth (the face is 0.39 m in front)
    y -0.52 .. +0.50    the shaggy fur: three rows of spikes round the back, cheeks and jaw, and a few on top

One mesh node ("bigfoot_head"), eight flat-colour materials, about 1100 triangles, 0.88 x 1.12 x 0.87 m with the fur.
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

add, sub, mul, unit = kit.add, kit.sub, kit.mul, kit.unit
loft, extrude, box, cyl, both = kit.loft, kit.extrude, kit.box, kit.cyl, kit.both
Node = kit.Node

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength)
    "Fur": (0.43, 0.29, 0.17, 0.95, 0.0, 0),         # the head mass
    "FurDark": (0.28, 0.18, 0.11, 0.95, 0.0, 0),     # crest, brow, every other spike
    "FurLight": (0.58, 0.42, 0.25, 0.95, 0.0, 0),    # highlights: every third spike
    "Skin": (0.40, 0.31, 0.25, 0.9, 0.0, 0),         # face plate, lips, ears
    "SkinDark": (0.22, 0.16, 0.13, 0.9, 0.0, 0),     # nose
    "Mouth": (0.07, 0.05, 0.05, 0.9, 0.0, 0),        # mouth line, nostrils, pupils
    "Eye": (0.97, 0.80, 0.30, 0.5, 0.0, 0.25),       # amber, a little glow so the eyes read in shade
    "Tooth": (0.90, 0.86, 0.74, 0.7, 0.0, 0),
}

# ----------------------------------------------------------------------------- the head mass

N = 10  # sides of every ring: a flat facet in front (the face) and one behind

# y, half width, extent in front (-z), extent behind (+z): the faceted dome, from the jaw to the top
LEVELS = [
    (-0.34, 0.17, 0.20, 0.20),
    (-0.24, 0.27, 0.30, 0.28),
    (-0.08, 0.34, 0.34, 0.33),
    (0.08, 0.37, 0.37, 0.37),
    (0.22, 0.33, 0.27, 0.38),
    (0.34, 0.24, 0.15, 0.34),
    (0.43, 0.13, 0.07, 0.25),
    (0.48, 0.05, 0.02, 0.1),
]


def level(y):
    """Half width, front and back extent of the dome at height y (linear between LEVELS, clamped to the ends)."""
    if y <= LEVELS[0][0]:
        return LEVELS[0][1:]
    for (y0, *a), (y1, *b) in zip(LEVELS, LEVELS[1:]):
        if y <= y1:
            t = (y - y0) / (y1 - y0)
            return tuple(p + (q - p) * t for p, q in zip(a, b))
    return LEVELS[-1][1:]


def ring_point(y, theta):
    """The dome's surface at height y and angle theta (0 = straight ahead, towards -z; positive turns towards +x)."""
    a, f, b = level(y)
    c = math.cos(theta)
    return (a * math.sin(theta), y, -f * c if c >= 0 else -b * c)


def head():
    rings = []
    for y, a, f, b in LEVELS:
        rings.append([ring_point(y, 2 * math.pi * (k + 0.5) / N) for k in range(N)])
    return loft("Fur", rings)


def crest():
    # profile in (z, y), pushed along x: from the forehead over the top to the nape
    prof = [(-0.12, 0.30), (-0.04, 0.54), (0.12, 0.60), (0.28, 0.42), (0.32, 0.22), (-0.10, 0.18)]
    return extrude("FurDark", prof, 'x', -0.05, 0.05, 0.015)


# ----------------------------------------------------------------------------- the face

Z_FACE = -0.39   # front of the face plate


def face():
    t = []
    # the plate: broad at the brow, narrowing to the chin, standing 2 to 5 cm proud of the fur
    prof = [(-0.07, -0.28), (0.07, -0.28), (0.15, -0.20), (0.19, -0.06), (0.20, 0.06), (0.14, 0.12), (-0.14, 0.12), (-0.20, 0.06), (-0.19, -0.06), (-0.15, -0.20)]
    t += extrude("Skin", prof, 'z', -0.33, Z_FACE, 0.018)
    # the brow: a heavy arched ridge across, overhanging the eyes, thinning to the temples
    arch = [(-0.29, 0.09), (-0.21, 0.175), (0.0, 0.19), (0.21, 0.175), (0.29, 0.09), (0.2, 0.06), (0.0, 0.075), (-0.2, 0.06)]
    t += extrude("FurDark", arch, 'z', -0.405, -0.29, 0.02)
    # the eyes: amber, with a dark pupil, under the brow
    for s in (-1, 1):
        x = s * 0.085
        t += box("Eye", x - 0.034, x + 0.034, -0.008, 0.042, Z_FACE - 0.013, Z_FACE + 0.01)
        t += box("Mouth", x - 0.013, x + 0.013, 0.004, 0.032, Z_FACE - 0.019, Z_FACE - 0.012)
    # the nose: a dark pad with two nostrils
    t += box("SkinDark", -0.055, 0.055, -0.115, -0.03, Z_FACE - 0.03, Z_FACE + 0.01, 0.01, 'x')
    for s in (-1, 1):
        t += box("Mouth", s * 0.028 - 0.011, s * 0.028 + 0.011, -0.098, -0.07, Z_FACE - 0.034, Z_FACE - 0.028)
    # the muzzle: a lip pad over a slit of a mouth with two teeth showing, and the chin below
    t += box("Skin", -0.125, 0.125, -0.215, -0.115, Z_FACE - 0.03, Z_FACE + 0.01, 0.015, 'x')
    t += box("Mouth", -0.095, 0.095, -0.192, -0.176, Z_FACE - 0.036, Z_FACE - 0.03)
    for s in (-1, 1):
        t += box("Tooth", s * 0.04 - 0.012, s * 0.04 + 0.012, -0.19, -0.16, Z_FACE - 0.04, Z_FACE - 0.033)
    t += box("Skin", -0.09, 0.09, -0.285, -0.215, Z_FACE - 0.012, Z_FACE + 0.01, 0.012, 'x')
    # the ears, half buried in the fur
    ear = box("Skin", 0.33, 0.43, -0.01, 0.13, -0.06, 0.06, 0.02, 'x')
    t += both(ear)
    return t


# ----------------------------------------------------------------------------- the fur

def spike(mat, y, theta, length, down, base=0.07, sink=0.93):
    """A four-sided spike rooted in the dome at (y, theta), pointing out and down (0 = straight out, 1 = straight down)."""
    p = ring_point(y, theta)
    root = (p[0] * sink, p[1], p[2] * sink)
    out = unit((p[0], 0.0, p[2]))
    d = unit((out[0] * (1 - down), -down, out[2] * (1 - down)))
    return cyl(mat, root, add(root, mul(d, length)), base, 4, 0.0, False)


def fur():
    t = []
    mats = ["Fur", "FurDark", "FurLight"]
    k = 0
    # two rows round the back, cheeks and sides (the front stays clear for the face)
    for y, n, length, down, a0 in ((-0.02, 14, 0.19, 0.6, 38), (-0.15, 14, 0.21, 0.78, 42), (-0.28, 12, 0.2, 0.9, 52)):
        for i in range(n):
            deg = a0 + (360 - 2 * a0) * (i + 0.5) / n
            wobble = 1 + 0.18 * math.sin(k * 2.399)
            t += spike(mats[k % 3], y + 0.015 * math.cos(k * 1.7), math.radians(deg), length * wobble, down)
            k += 1
    # tufts on top, either side of the crest, and above the brow
    for i in range(6):
        deg = 40 + 280 * (i + 0.5) / 6
        t += spike(mats[(k + i) % 3], 0.30, math.radians(deg), 0.12, -0.35, 0.045, 0.9)
    # the beard: three spikes under the jaw behind the chin
    for deg in (80, 100, 260, 280):
        t += spike(mats[k % 3], -0.30, math.radians(deg), 0.13, 0.9, 0.04, 0.9)
        k += 1
    return t


def build():
    root = Node("bigfoot_head")
    root.add(head(), crest(), face(), fur())
    return root


# ----------------------------------------------------------------------------- GLB

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

    index = len(nodes)
    nodes.append({"name": root.name})
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
    nodes[index]["mesh"] = 0
    meshes.append({"name": root.name, "primitives": prims})
    while len(buf) % 4:
        buf.append(0)
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_bigfoot_head_model.py (Drycastle)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": "bigfoot_head", "nodes": [index]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent / "assets" / "drycastle-generated-models" / "bigfoot_head.glb")
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
