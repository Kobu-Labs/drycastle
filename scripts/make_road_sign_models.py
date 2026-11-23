#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/sign_stop.glb, sign_give_way.glb and sign_main_road.glb: the three Slovak road signs of
ticket #425 (dopravne znacky), each a galvanised pole with a metal plate on two clamps.

    sign_stop          Stop (B2): red octagon, white border, white STOP (a block font written as boxes, no texture)
    sign_give_way      Daj prednost v jazde (P6): inverted triangle, red border, white field
    sign_main_road     Hlavna cesta (P3 style): yellow diamond, thin black line, white border

Original geometry written as code (no third-party model, no texture, no photo); standard library only. The mesh helpers are the
flak script's, loaded from the file next to this one:

    python3 -I scripts/make_road_sign_models.py [out_dir]      # rebuild the three GLBs, byte for byte

Frame: glTF/Godot, metres, y up, origin on the ground at the foot of the pole. The plate FACES +Z (read it from +Z; its back is plain
galvanised metal). Pole 2.5 m (60 mm round, cap on top), plate centre 2.15 m, plate 0.5 to 0.75 m across.

Node tree: sign_* > Pole (pole, cap, two clamp bands: static, solid in the game), Plate (face layers, back slab with its rim, two bolt heads).
The Plate node's origin is the UPPER CLAMP (on the pole axis, at plate centre + 0.18 m, 0.042 m in front of the pole's axis), so a client
bends a shot plate by rotating that one node about its own origin: it then hangs from the upper clamp askew (godot/shooter/SignPlates.cs).
The manifest entry (assets/manifest.json "plates") gives the plate's centre as the point a round must pass.

Materials are PBR metal, no emissive: the pole is galvanised steel (metallic 0.75, rough 0.4) and the plate is painted sheet metal
(metallic 0.6, rough 0.3, so it shines in the sun like a glossy plate).
"""
import importlib.util
import json
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT_DIR = HERE.parent / "assets" / "drycastle-generated-models"

_spec = importlib.util.spec_from_file_location("make_flak_model", HERE / "make_flak_model.py")
kit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(kit)
sub, cross, unit, extrude, box, cyl, inset, Node = kit.sub, kit.cross, kit.unit, kit.extrude, kit.box, kit.cyl, kit.inset, kit.Node

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic)
    "Galvanised": (0.62, 0.64, 0.65, 0.40, 0.75),   # pole, cap, clamps, the plate's back and rim
    "SignRed": (0.78, 0.07, 0.09, 0.30, 0.60),
    "SignWhite": (0.93, 0.93, 0.90, 0.30, 0.55),
    "SignYellow": (0.98, 0.78, 0.05, 0.30, 0.60),
    "SignBlack": (0.04, 0.04, 0.045, 0.35, 0.50),
}

# ----------------------------------------------------------------------------- dimensions (metres)

POLE_R = 0.03             # 60 mm round pole
POLE_H = 2.50
CAP_H = 0.02
PLATE_Y = 2.15            # plate centre height
CLAMP_DY = 0.18           # the clamps sit this far above and below the plate centre
STANDOFF = 0.012          # plate back from the pole's surface
BACK_Z = POLE_R + STANDOFF  # the plate's back face, in front of the pole's axis
T_BACK = 0.006            # galvanised back slab
STEP = 0.0015             # each face layer is this much thicker than the one below
RIM = 0.008               # the back slab stands this much proud of the painted edge

# ----------------------------------------------------------------------------- plate outlines (x, y about the plate centre)


def octagon(across_flats):
    h = across_flats / 2
    c = h * 0.41421356  # half of a side: the corner cut
    return [(-c, h), (c, h), (h, c), (h, -c), (c, -h), (-c, -h), (-h, -c), (-h, c)]


def triangle_down(side):
    """Inverted triangle, bounding box centred on the plate centre."""
    h = side * 0.8660254
    return [(-side / 2, h / 2), (side / 2, h / 2), (0.0, -h / 2)]


def diamond(diagonal):
    h = diagonal / 2
    return [(0.0, h), (h, 0.0), (0.0, -h), (-h, 0.0)]


def ccw(profile):
    n = len(profile)
    a = sum(profile[i][0] * profile[(i + 1) % n][1] - profile[(i + 1) % n][0] * profile[i][1] for i in range(n))
    return profile if a > 0 else profile[::-1]


# ----------------------------------------------------------------------------- the block font for STOP (5 x 7 cells)

GLYPHS = {
    "S": ["01110", "10001", "10000", "01110", "00001", "10001", "01110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
}


def word(mat, text, cell, z0, z1, cx, cy):
    """Raised letters centred on (cx, cy), each a run of cells merged into one box."""
    width = len(text) * 5 + (len(text) - 1)
    x_left = cx - width * cell / 2
    y_top = cy + 7 * cell / 2
    tris = []
    for li, ch in enumerate(text):
        for row, bits in enumerate(GLYPHS[ch]):
            col = 0
            while col < 5:
                if bits[col] != "1":
                    col += 1
                    continue
                end = col
                while end < 5 and bits[end] == "1":
                    end += 1
                x0 = x_left + (li * 6 + col) * cell
                tris += box(mat, x0, x0 + (end - col) * cell, y_top - (row + 1) * cell, y_top - row * cell, z0, z1)
                col = end
    return tris


# ----------------------------------------------------------------------------- the three signs


def build(kind):
    names = {"stop": "sign_stop", "give_way": "sign_give_way", "main_road": "sign_main_road"}
    root = Node(names[kind])

    # the pole: round, with a cap and two clamp bands where the plate hangs
    pole = root.child("Pole")
    pole.add(cyl("Galvanised", (0.0, 0.0, 0.0), (0.0, POLE_H, 0.0), POLE_R, 12))
    pole.add(cyl("Galvanised", (0.0, POLE_H, 0.0), (0.0, POLE_H + CAP_H, 0.0), POLE_R + 0.004, 12))
    for dy in (CLAMP_DY, -CLAMP_DY):
        pole.add(cyl("Galvanised", (0.0, PLATE_Y + dy - 0.012, 0.0), (0.0, PLATE_Y + dy + 0.012, 0.0), POLE_R + 0.006, 12))
        pole.add(box("Galvanised", -0.012, 0.012, PLATE_Y + dy - 0.012, PLATE_Y + dy + 0.012, POLE_R, BACK_Z))  # the stand-off block to the plate

    pivot = (0.0, PLATE_Y + CLAMP_DY, BACK_Z)
    plate = root.child("Plate", pivot)

    def at(profile, mat, z0, z1):
        pts = [(x, PLATE_Y + y) for x, y in ccw(profile)]
        plate.add(extrude(mat, pts, 'z', z0, z1))

    z = BACK_Z
    top = BACK_Z + T_BACK

    if kind == "stop":
        base = octagon(0.70)
        at(inset(ccw(base), -RIM), "Galvanised", z, top)                 # back slab and its rim
        at(base, "SignWhite", top, top + STEP)                          # the white border
        at(inset(ccw(base), 0.022), "SignRed", top + STEP, top + 2 * STEP)
        plate.add(word("SignWhite", "STOP", 0.0235, top + 2 * STEP, top + 3 * STEP, 0.0, PLATE_Y))
    elif kind == "give_way":
        base = triangle_down(0.75)
        at(inset(ccw(base), -RIM), "Galvanised", z, top)
        at(base, "SignRed", top, top + STEP)
        at(inset(ccw(base), 0.075), "SignWhite", top + STEP, top + 2 * STEP)
    else:
        base = diamond(0.707)
        at(inset(ccw(base), -RIM), "Galvanised", z, top)
        at(base, "SignWhite", top, top + STEP)
        at(inset(ccw(base), 0.055), "SignBlack", top + STEP, top + 2 * STEP)
        at(inset(ccw(base), 0.063), "SignYellow", top + 2 * STEP, top + 3 * STEP)

    for dy in (CLAMP_DY, -CLAMP_DY):  # bolt heads on the face where the clamps are
        plate.add(cyl("Galvanised", (0.0, PLATE_Y + dy, top), (0.0, PLATE_Y + dy, top + 0.0045 + 3 * STEP), 0.009, 8))
    return root


# ----------------------------------------------------------------------------- GLB


def write_glb(root, path):
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS)
    materials = []
    for name in mat_names:
        r, g, b, rough, metal = MATERIALS[name]
        lin = [round(kit.srgb_to_linear(v), 4) for v in (r, g, b)]
        materials.append({"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": metal, "roughnessFactor": rough}})

    def view(data, target):
        while len(buf) % 4:
            buf.append(0)
        views.append({"buffer": 0, "byteOffset": len(buf), "byteLength": len(data), "target": target})
        buf.extend(data)
        return len(views) - 1

    def emit(node, parent_origin):
        entry = {"name": node.name}
        tr = sub(node.origin, parent_origin)
        if any(abs(v) > 1e-9 for v in tr):
            entry["translation"] = [round(v, 5) + 0.0 for v in tr]
        index = len(nodes)
        nodes.append(entry)
        if node.tris:
            prims = []
            for mi, mname in enumerate(mat_names):
                verts, lookup, idx = [], {}, []
                for m, ps, nr in node.tris:
                    if m != mname:
                        continue
                    fn = unit(cross(sub(ps[1], ps[0]), sub(ps[2], ps[0])))
                    for k in range(3):
                        p = sub(ps[k], node.origin)
                        n = nr[k] if nr else fn
                        key = tuple(round(v, 5) for v in p) + tuple(round(v, 3) for v in n)
                        if key not in lookup:
                            lookup[key] = len(verts)
                            verts.append((p, n))
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
            entry["mesh"] = len(meshes)
            meshes.append({"name": node.name, "primitives": prims})
        kids = [emit(c, node.origin) for c in node.children]
        if kids:
            entry["children"] = kids
        return index

    top = emit(root, (0.0, 0.0, 0.0))
    while len(buf) % 4:
        buf.append(0)
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_road_sign_models.py (Drycastle)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": root.name, "nodes": [top]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


FILES = {"stop": "sign_stop.glb", "give_way": "sign_give_way.glb", "main_road": "sign_main_road.glb"}


def main():
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT_DIR
    for kind, name in FILES.items():
        root = build(kind)
        write_glb(root, out_dir / name)
        lo, hi = [1e9] * 3, [-1e9] * 3
        total = 0
        for n in root.walk():
            total += len(n.tris)
            for _, ps, _ in n.tris:
                for p in ps:
                    for i in range(3):
                        lo[i], hi[i] = min(lo[i], p[i]), max(hi[i], p[i])
        print(f"{name}: triangles {total}, size_m [{hi[0] - lo[0]:.3f}, {hi[1] - lo[1]:.3f}, {hi[2] - lo[2]:.3f}], x {lo[0]:.3f}..{hi[0]:.3f}, y {lo[1]:.3f}..{hi[1]:.3f}, z {lo[2]:.3f}..{hi[2]:.3f}")
    print(f"wrote {out_dir}")


if __name__ == "__main__":
    main()
