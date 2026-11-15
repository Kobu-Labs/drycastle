#!/usr/bin/env python3
"""Builds assets/welcome-sign/welcome_sign.glb: the village welcome sign ("VITAJTE V OBCI", the coat of arms, the village name):
an arched board between two light-blue painted posts that bend in below the board to two legs joined by two cross rails,
each post topped by a cap and a ball.

The board's artwork is NOT drawn here: assets/welcome-sign/board.png is the board cut out of the reference picture of the sign
(arch top, blue rim, gold line, lettering and coat of arms exactly as shown) and is embedded in the GLB. The structure around it
is original geometry written as code (standard library only; the mesh helpers are the flak script's, loaded from the file next to
this one):

    python3 -I scripts/make_welcome_sign_model.py [out.glb]                 # rebuild the GLB from board.png, byte for byte
    python3 -I scripts/make_welcome_sign_model.py --photo picture.png       # first re-cut board.png from the reference picture (needs Pillow)

Frame: glTF/Godot, metres, y up, origin on the ground at the centre between the legs. The sign FACES +Z (read the board from +Z);
the board shows the same artwork on both faces, so it also reads from behind. The legs run 0.15 m below the origin so the sign
stands on uneven ground without a gap.

Dimensions are measured on the reference picture (one pixel = PX_M metres, taken from a total height of about 3.2 m):

    ground .. 1.70 m     the two legs (centre to centre 0.31 m, 0.12 x 0.08 m), cross rails at 0.69 m and 1.35 m
    1.52 .. 1.70 m       each leg bends outwards in a 0.15 m rise to a post (centre to centre 0.92 m)
    1.69 .. 3.10 m       the board between the posts, 0.81 m wide, arched crown (shoulders 3.00 m, peak 3.10 m)
    3.04 .. 3.20 m       a cap block and a ball on each post

Node tree: welcome_sign > board, post_l, post_r, rail_lower, rail_upper, finial_l, finial_r (all origins at the sign origin).
"""
import importlib.util
import json
import math
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
OUT_DIR = ROOT / "assets" / "welcome-sign"

_spec = importlib.util.spec_from_file_location("make_flak_model", HERE / "make_flak_model.py")
kit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(kit)
sub, cross, unit, extrude, box, lathe, mirror, Node = kit.sub, kit.cross, kit.unit, kit.extrude, kit.box, kit.lathe, kit.mirror, kit.Node

# ----------------------------------------------------------------------------- reference picture measurements (pixels)

PX_M = 0.0022          # metres per pixel of the reference picture
CX = 513.5             # picture x of the sign's centre line
GROUND_Y = 1485        # picture y of the ground

def X(px): return (px - CX) * PX_M
def Y(py): return (GROUND_Y - py) * PX_M

# the cut-out of the board: picture columns 330 .. 697, rows 74 (crown of the arch) .. 716 (underside of the bottom rim)
BOARD_X0, BOARD_X1, BOARD_Y0, BOARD_Y1 = 330, 697, 74, 716
# the outer edge of the arch (picture x, y) on the left half, measured on the blue rim; mirrored about CX
ARCH = [(330, 120), (338, 119), (350, 117), (362, 114), (374, 109), (386, 105), (398, 100), (410, 95), (422, 91), (434, 87),
        (446, 83), (458, 80), (470, 78), (482, 76), (494, 75), (506, 74)]

XU, XL, H = 209, 70, 26          # post and leg centres (distance from CX) and half width, pixels
POST_TOP, LEG_BOTTOM = 105, 1485 + 68
POST_DEPTH = 0.04                # half depth of posts and legs, metres
BOARD_HALF_T = 0.02              # half thickness of the board

MATERIALS = {
    # name: (r, g, b, roughness, metallic)
    "Blue": (0.29, 0.62, 0.86, 0.5, 0.0),        # the paint of posts, rails, balls and the board's edge
}
TEXTURED = {"Board": (0.55, 0.0)}                # name: (roughness, metallic); base colour = board.png

# ----------------------------------------------------------------------------- geometry

def post_profile(side):
    """Outline of one post with its bent leg in the XY plane (x, y in metres); side -1 = left, +1 = right."""
    pts = [(-XU - H, POST_TOP), (-XU - H, 733), (-XL - H, 795), (-XL - H, LEG_BOTTOM), (-XL + H, LEG_BOTTOM), (-XL + H, 771), (-XU + H, 709), (-XU + H, POST_TOP)]
    prof = [(x * PX_M * (1 if side < 0 else -1), Y(y)) for x, y in pts]
    if side > 0:
        prof = prof[::-1]
    return prof


def arch_profile():
    left = [(X(x), Y(y)) for x, y in ARCH]
    right = [(-x, y) for x, y in left[::-1]]
    return left + right + [(X(BOARD_X1), Y(BOARD_Y1)), (X(BOARD_X0), Y(BOARD_Y1))]


def build():
    root = Node("welcome_sign")

    # the board: a blue-edged slab whose two big faces take the picture
    bx0, bx1, by0, by1 = X(BOARD_X0), X(BOARD_X1), Y(BOARD_Y1), Y(BOARD_Y0)
    slab = extrude("Blue", arch_profile(), 'z', -BOARD_HALF_T, BOARD_HALF_T)
    face = []
    for m, ps, nr in slab:
        zs = [p[2] for p in ps]
        if all(abs(z - BOARD_HALF_T) < 1e-9 for z in zs) or all(abs(z + BOARD_HALF_T) < 1e-9 for z in zs):
            m = "Board"
        face.append((m, ps, nr))
    root.child("board").add(face)

    for name, side in (("post_l", -1), ("post_r", 1)):
        root.child(name).add(extrude("Blue", post_profile(side), 'z', -POST_DEPTH, POST_DEPTH))

    for name, py in (("rail_lower", 1170), ("rail_upper", 870)):
        root.child(name).add(box("Blue", X(CX - XL), X(CX + XL), Y(py + 21), Y(py - 21), -0.03, 0.03))

    for name, side in (("finial_l", -1), ("finial_r", 1)):
        cx = X(CX + side * XU)
        node = root.child(name)
        node.add(box("Blue", cx - 0.067, cx + 0.067, Y(106), Y(88), -0.05, 0.05, 0.003))
        r = 28 * PX_M
        ball = [(-r * math.cos(math.radians(a)), r * math.sin(math.radians(a))) for a in range(0, 181, 15)]
        node.add(lathe((cx, Y(60), 0.0), (0, 1, 0), ball, n=16, mat="Blue"))
    return root, (bx0, bx1, by0, by1)


# ----------------------------------------------------------------------------- GLB

def srgb_to_linear(c): return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def write_glb(root, board, png, path):
    bx0, bx1, by0, by1 = board
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS) + list(TEXTURED)
    materials = []
    for name in mat_names:
        if name in MATERIALS:
            r, g, b, rough, metal = MATERIALS[name]
            lin = [round(srgb_to_linear(v), 4) for v in (r, g, b)]
            materials.append({"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": metal, "roughnessFactor": rough}})
        else:
            rough, metal = TEXTURED[name]
            materials.append({"name": name, "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": metal, "roughnessFactor": rough}})

    def view(data, target=None):
        while len(buf) % 4:
            buf.append(0)
        v = {"buffer": 0, "byteOffset": len(buf), "byteLength": len(data)}
        if target:
            v["target"] = target
        views.append(v)
        buf.extend(data)
        return len(views) - 1

    def emit(node, parent_origin):
        entry = {"name": node.name}
        t = sub(node.origin, parent_origin)
        if any(abs(v) > 1e-9 for v in t):
            entry["translation"] = [round(v, 5) for v in t]
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
                        uv = ()
                        if mname in TEXTURED:
                            u = (ps[k][0] - bx0) / (bx1 - bx0)
                            if fn[2] < 0:
                                u = 1.0 - u          # the back face: the artwork reads left to right for a viewer behind the sign
                            uv = (round(u, 5), round((by1 - ps[k][1]) / (by1 - by0), 5))
                        key = tuple(round(v, 5) for v in p) + tuple(round(v, 3) for v in n) + uv
                        if key not in lookup:
                            lookup[key] = len(verts)
                            verts.append((p, n, uv))
                        idx.append(lookup[key])
                if not idx:
                    continue
                assert len(verts) < 65536
                pos = b"".join(struct.pack("<3f", *p) for p, _, _ in verts)
                nor = b"".join(struct.pack("<3f", *n) for _, n, _ in verts)
                lo = [struct.unpack("<f", struct.pack("<f", min(p[i] for p, _, _ in verts)))[0] for i in range(3)]
                hi = [struct.unpack("<f", struct.pack("<f", max(p[i] for p, _, _ in verts)))[0] for i in range(3)]
                accessors.append({"bufferView": view(pos, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3", "min": lo, "max": hi})
                accessors.append({"bufferView": view(nor, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3"})
                attrs = {"POSITION": len(accessors) - 2, "NORMAL": len(accessors) - 1}
                if mname in TEXTURED:
                    tex = b"".join(struct.pack("<2f", *uv) for _, _, uv in verts)
                    accessors.append({"bufferView": view(tex, 34962), "componentType": 5126, "count": len(verts), "type": "VEC2"})
                    attrs["TEXCOORD_0"] = len(accessors) - 1
                accessors.append({"bufferView": view(struct.pack(f"<{len(idx)}H", *idx), 34963), "componentType": 5123, "count": len(idx), "type": "SCALAR"})
                prims.append({"attributes": attrs, "indices": len(accessors) - 1, "material": mi})
            entry["mesh"] = len(meshes)
            meshes.append({"name": node.name, "primitives": prims})
        kids = [emit(c, node.origin) for c in node.children]
        if kids:
            entry["children"] = kids
        return index

    top = emit(root, (0.0, 0.0, 0.0))
    image_view = view(png)
    while len(buf) % 4:
        buf.append(0)
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_welcome_sign_model.py (Drycastle)"},
            "scene": 0, "scenes": [{"name": "welcome_sign", "nodes": [top]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "images": [{"name": "board", "bufferView": image_view, "mimeType": "image/png"}],
            "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071}],
            "textures": [{"source": 0, "sampler": 0}],
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def cut_board(photo, out_png):
    """Cuts board.png from the reference picture: columns/rows of the board, 2x with Lanczos; what lies above the arch (leaves) is filled with the rim blue."""
    from PIL import Image, ImageDraw
    im = Image.open(photo).convert("RGB")
    crop = im.crop((BOARD_X0, BOARD_Y0, BOARD_X1, BOARD_Y1))
    rim = crop.getpixel((crop.width // 2, 1))
    mask = Image.new("L", crop.size, 0)
    outline = [(x - BOARD_X0, y - BOARD_Y0) for x, y in ARCH]
    outline += [(2 * (CX - BOARD_X0) - x, y) for x, y in outline[::-1]]
    ImageDraw.Draw(mask).polygon(outline + [(crop.width, crop.height), (0, crop.height)], fill=255)
    fill = Image.new("RGB", crop.size, rim)
    crop = Image.composite(crop, fill, mask)
    crop = crop.resize((crop.width * 2, crop.height * 2), Image.LANCZOS)
    crop.save(out_png, optimize=True)
    print(f"wrote {out_png} ({crop.width}x{crop.height})")


def main():
    args = sys.argv[1:]
    png_path = OUT_DIR / "board.png"
    if args[:1] == ["--photo"]:
        cut_board(args[1], png_path)
        args = args[2:]
    out = args[0] if args else str(OUT_DIR / "welcome_sign.glb")
    root, board = build()
    write_glb(root, board, png_path.read_bytes(), out)
    total = 0
    lo, hi = [1e9] * 3, [-1e9] * 3
    for n in root.walk():
        total += len(n.tris)
        for _, ps, _ in n.tris:
            for p in ps:
                for i in range(3):
                    lo[i], hi[i] = min(lo[i], p[i]), max(hi[i], p[i])
        print(f"{n.name:12s} tris={len(n.tris)}")
    print(f"triangles: {total}")
    print("bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f  size %.3f x %.3f x %.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
