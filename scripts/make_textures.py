#!/usr/bin/env python3
"""Builds assets/drycastle-generated-textures/*.png: the tileable textures of the texture library.

Original pixels computed by this script (no third-party image, no photo); standard library only:

    python3 -I scripts/make_textures.py                 # rebuild every PNG, byte for byte
    python3 -I scripts/make_textures.py --meta          # also print the manifest entries (JSON)
    python3 -I scripts/make_textures.py --sheet out/textures/sheet.png   # contact sheet, each tiled 2x2

Every texture repeats seamlessly in both directions. The style is flat: a few tones, hard edges, very
little noise, so the textures sit next to the flat-colour map. Two kinds:

* coloured   (brick_red, brick_white, stone, dirt, gravel, leaf_litter, soil_rows): carry their own colours; a white tint shows them as drawn.
* tintable   (concrete, picket, wire): near-white, shaded only; the mesh's vertex colour is the paint.

``TEXTURES`` below is the single list: id, size in pixels, the size of one repeat in metres, and the
drawing function. ``assets/manifest.json`` (``textures``) repeats id, file, tile_m, alpha, tintable and
the mean colour so the pipeline and the game do not need this script.
"""
import json
import math
import struct
import sys
import zlib
from pathlib import Path

OUT_DIR = Path(__file__).resolve().parent.parent / "assets" / "drycastle-generated-textures"

# ----------------------------------------------------------------------------- helpers


def rgb(h):
    return (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))


def hash01(ix, iy, seed=0):
    """Integer hash -> [0, 1). The same on every machine."""
    n = (ix * 374761393 + iy * 668265263 + seed * 362437) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    n ^= n >> 16
    return n / 4294967296.0


def vnoise(x, y, period, seed=0):
    """Value noise on a lattice that wraps every ``period`` cells (so the result tiles)."""
    x0, y0 = math.floor(x), math.floor(y)
    fx, fy = x - x0, y - y0
    sx, sy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)

    def at(i, j):
        return hash01(i % period, j % period, seed)

    top = at(x0, y0) * (1 - sx) + at(x0 + 1, y0) * sx
    bot = at(x0, y0 + 1) * (1 - sx) + at(x0 + 1, y0 + 1) * sx
    return top * (1 - sy) + bot * sy


def shade(c, k):
    return tuple(max(0, min(255, round(v * k))) for v in c)


def grey(v):
    g = max(0, min(255, round(v * 255)))
    return (g, g, g)


def write_png(path, w, h, pixels):
    """``pixels``: h rows of w (r, g, b, a) tuples. 8-bit RGBA, no filter, fixed compression."""
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for px in row:
            raw.extend(px)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    path.write_bytes(png)


# ----------------------------------------------------------------------------- textures


def brick(w, h, bricks, mortar, seed):
    """Running bond: 4 bricks across, 8 courses up; one flat tone per brick, light top and dark bottom edge."""
    bw, bh, joint = w // 4, h // 8, 3
    out = []
    for y in range(h):
        row = y // bh
        shift = bw // 2 if row % 2 else 0
        line = []
        for x in range(w):
            bx = (x + shift) % w
            cx, cy = bx % bw, y % bh
            if cx < joint or cy < joint:
                line.append((*shade(mortar, 0.97 + 0.06 * hash01(x, y, seed + 9)), 255))
                continue
            col = bx // bw
            pick = hash01(col, row, seed)
            tone = bricks[int(pick * len(bricks)) % len(bricks)]
            k = 1.0
            if cy == joint:
                k = 1.07  # lit top edge
            elif cy == bh - 1:
                k = 0.88  # shaded bottom edge
            k *= 0.975 + 0.05 * vnoise(x / 8, y / 8, 32, seed + 3)
            line.append((*shade(tone, k), 255))
        out.append(line)
    return out


def brick_red(w, h):
    tones = [rgb(c) for c in ("#a8523a", "#9c4a34", "#b25c40", "#8e4230", "#a04e3c", "#b86446", "#7d3a2c")]
    return brick(w, h, tones, rgb("#bdb5a8"), 11)


def brick_white(w, h):
    tones = [rgb(c) for c in ("#ebe7dd", "#e2ded3", "#f0ede5", "#d9d4c8", "#e6e1d6")]
    return brick(w, h, tones, rgb("#bcb6a9"), 23)


def stone(w, h):
    """Fieldstone: wrapped Voronoi cells on a jittered 5 x 7 grid, one tone per stone, dark joints."""
    gx, gy = 5, 7
    cw, ch = w / gx, h / gy
    tones = [rgb(c) for c in ("#9c978c", "#8b867b", "#a8a296", "#7f7b72", "#b0a998", "#948d7e", "#a39b8b")]
    joint = rgb("#5f5b54")
    seeds = {}
    for j in range(gy):
        for i in range(gx):
            seeds[(i, j)] = ((i + 0.2 + 0.6 * hash01(i, j, 31)) * cw, (j + 0.2 + 0.6 * hash01(i, j, 37)) * ch)
    out = []
    for y in range(h):
        line = []
        cj = int(y // ch)
        for x in range(w):
            ci = int(x // cw)
            best = (1e9, None)
            second = 1e9
            for dj in (-1, 0, 1):
                for di in (-1, 0, 1):
                    i, j = ci + di, cj + dj
                    sx, sy = seeds[(i % gx, j % gy)]
                    sx += (i - i % gx) * cw
                    sy += (j - j % gy) * ch
                    d = math.hypot(x + 0.5 - sx, y + 0.5 - sy)
                    if d < best[0]:
                        second = best[0]
                        best = (d, (i % gx, j % gy))
                    elif d < second:
                        second = d
            edge = (second - best[0]) / 2
            if edge < 2.0:
                line.append((*shade(joint, 0.95 + 0.1 * hash01(x, y, 41)), 255))
                continue
            i, j = best[1]
            tone = tones[int(hash01(i, j, 43) * len(tones)) % len(tones)]
            k = 0.9 + 0.1 * min(1.0, (edge - 2.0) / 5.0)  # darker rim, flat face
            k *= 0.97 + 0.06 * vnoise(x / 6, y / 6, 32, 47)
            line.append((*shade(tone, k), 255))
        out.append(line)
    return out


def concrete(w, h):
    """Near-white cast concrete: soft blotches, fine speckle, a form line every half tile. Tint it to paint it."""
    out = []
    for y in range(h):
        line = []
        for x in range(w):
            v = 0.93
            v += 0.05 * (vnoise(x / 64, y / 64, w // 64, 53) - 0.5)
            v += 0.035 * (vnoise(x / 12, y / 12, w // 4, 59) - 0.5)
            v += 0.02 * (hash01(x, y, 61) - 0.5)
            if y % (h // 2) < 2 or x < 2:
                v -= 0.07  # form joints
            if hash01(x // 2, y // 2, 67) > 0.992:
                v -= 0.1  # pores
            line.append((*grey(v), 255))
        out.append(line)
    return out


def picket(w, h):
    """Four pointed pickets over two rails; transparent between them. The tile is one fence height tall."""
    pitch = w // 4
    board = round(pitch * 0.7)
    tip = round(h * 0.09)
    rails = ((round(h * 0.22), round(h * 0.30)), (round(h * 0.70), round(h * 0.78)))  # from the top
    out = []
    for y in range(h):
        line = []
        for x in range(w):
            n, cx = x // pitch, x % pitch
            body = (0.95 + 0.06 * (hash01(n, 0, 71) - 0.5)) * (0.985 + 0.03 * vnoise(x / 3, y / 40, 64, 73))
            inside = cx < board
            if inside and y < tip:  # the point: a triangle narrowing to the top
                half = board / 2
                inside = abs(cx + 0.5 - half) <= half * (y + 1) / tip
            if inside:
                k = body * (0.86 if cx in (0, board - 1) else 1.0)
                line.append((*grey(k), 255))
            elif any(a <= y < b for a, b in rails):
                line.append((*grey(0.74), 255))
            else:
                line.append((*grey(0.95), 0))  # same tone under the cut-out, so edges do not darken
        out.append(line)
    return out


def wire(w, h):
    """Chain link: two sets of diagonal wires, 8 diamonds per tile; transparent between them."""
    pitch, thick = w // 8, 5
    out = []
    for y in range(h):
        line = []
        for x in range(w):
            a, b = (x + y) % pitch, (x - y) % pitch
            if a < thick:
                line.append((*grey(0.96 if a in (1, 2) else 0.82), 255))
            elif b < thick:
                line.append((*grey(0.88 if b in (1, 2) else 0.74), 255))
            else:
                line.append((*grey(0.9), 0))
        out.append(line)
    return out


def dirt(w, h):
    """Bare earth: two soft brown tones in big blotches, a few darker clods and pale grit. Coloured."""
    base, dark, pale = rgb("#8a6c4c"), rgb("#6f553a"), rgb("#a68a68")
    out = []
    for y in range(h):
        line = []
        for x in range(w):
            n = vnoise(x / 48, y / 48, w // 48 + 1, 71) * 0.65 + vnoise(x / 14, y / 14, w // 14 + 1, 73) * 0.35
            c = base
            if n < 0.42:
                c = dark
            elif n > 0.66:
                c = pale
            k = 0.96 + 0.08 * hash01(x // 2, y // 2, 79)
            if hash01(x // 3, y // 3, 83) > 0.985:
                k *= 0.78  # clods
            line.append((*shade(c, k), 255))
        out.append(line)
    return out


def gravel(w, h):
    """Gravel: wrapped Voronoi pebbles on a jittered 14 x 14 grid, one grey or tan tone each, dark gaps."""
    g = 14
    cw = w / g
    tones = [rgb(c) for c in ("#a8a49a", "#92908a", "#b9b3a3", "#7f7d78", "#a89f8a", "#c2bdb0", "#8e8a80")]
    gap = rgb("#55524c")
    seeds = {(i, j): ((i + 0.2 + 0.6 * hash01(i, j, 89)) * cw, (j + 0.2 + 0.6 * hash01(i, j, 97)) * cw)
             for j in range(g) for i in range(g)}
    out = []
    for y in range(h):
        line = []
        cj = int(y // cw)
        for x in range(w):
            ci = int(x // cw)
            best, second, who = 1e9, 1e9, None
            for dj in (-1, 0, 1):
                for di in (-1, 0, 1):
                    i, j = ci + di, cj + dj
                    sx, sy = seeds[(i % g, j % g)]
                    d = math.hypot(x + 0.5 - (sx + (i - i % g) * cw), y + 0.5 - (sy + (j - j % g) * cw))
                    if d < best:
                        second, best, who = best, d, (i % g, j % g)
                    elif d < second:
                        second = d
            edge = (second - best) / 2
            if edge < 1.2:
                line.append((*shade(gap, 0.9 + 0.2 * hash01(x, y, 101)), 255))
                continue
            tone = tones[int(hash01(*who, 103) * len(tones)) % len(tones)]
            k = 0.88 + 0.12 * min(1.0, (edge - 1.2) / 3.0)
            k *= 0.97 + 0.06 * hash01(x // 2, y // 2, 107)
            line.append((*shade(tone, k), 255))
        out.append(line)
    return out


def leaf_litter(w, h):
    """Fallen leaves on earth: dark brown ground with small hard-edged rust, ochre and olive leaf flecks. Coloured."""
    ground = rgb("#5e4a33")
    leaves = [rgb(c) for c in ("#a2562c", "#c08a34", "#8a6a2a", "#6f7a34", "#b5683a", "#7b4a26")]
    out = [[(*shade(ground, 0.92 + 0.16 * vnoise(x / 20, y / 20, w // 20 + 1, 109)), 255) for x in range(w)] for y in range(h)]
    n = 150
    for k in range(n):
        cx, cy = hash01(k, 1, 113) * w, hash01(k, 2, 113) * h
        a = hash01(k, 3, 113) * math.pi
        ca, sa = math.cos(a), math.sin(a)
        la, lb = 5 + 5 * hash01(k, 4, 113), 2 + 2 * hash01(k, 5, 113)  # half length, half width (pixels)
        tone = leaves[int(hash01(k, 6, 113) * len(leaves)) % len(leaves)]
        tone = shade(tone, 0.9 + 0.2 * hash01(k, 7, 113))
        for dy in range(-12, 13):
            for dx in range(-12, 13):
                u, v = dx * ca + dy * sa, -dx * sa + dy * ca
                if (u / la) ** 2 + (v / lb) ** 2 <= 1:
                    px, py = round(cx + dx) % w, round(cy + dy) % h  # wraps: tiles
                    out[py][px] = (*(shade(tone, 0.8) if abs(v) < 0.5 else tone), 255)
    return out


def soil_rows(w, h):
    """Tilled soil: four ridges running across the tile (along u), light crest and dark furrow, clods. Coloured."""
    crest, mid, furrow = rgb("#7d5e3f"), rgb("#6a4d33"), rgb("#4a3624")
    out = []
    for y in range(h):
        line = []
        phase = (y / h * 4) % 1.0
        wave = 0.5 - 0.5 * math.cos(phase * 2 * math.pi)  # 0 in the furrow, 1 on the crest
        for x in range(w):
            c = furrow if wave < 0.25 else mid if wave < 0.65 else crest
            k = 0.95 + 0.1 * vnoise(x / 10, y / 10, w // 10, 127)
            if hash01(x // 3, y // 3, 131) > 0.97:
                k *= 1.12 if wave > 0.5 else 0.85  # clods
            line.append((*shade(c, k), 255))
        out.append(line)
    return out


# id, pixels (w, h), one repeat in metres (w, h), tintable, drawing function.
# A tile height of 0 means "as tall as the surface": the texture is stretched over the full height once.
TEXTURES = [
    ("brick_red", (256, 256), (1.0, 0.6), False, brick_red),
    ("brick_white", (256, 256), (1.0, 0.6), False, brick_white),
    ("stone", (256, 256), (1.2, 1.2), False, stone),
    ("concrete", (256, 256), (2.0, 2.0), True, concrete),
    ("picket", (256, 256), (0.5, 0.0), True, picket),
    ("wire", (256, 256), (0.5, 0.5), True, wire),
    ("dirt", (256, 256), (2.0, 2.0), False, dirt),
    ("gravel", (256, 256), (1.0, 1.0), False, gravel),
    ("leaf_litter", (256, 256), (1.5, 1.5), False, leaf_litter),
    ("soil_rows", (256, 256), (1.0, 1.0), False, soil_rows),
]


def meta(tid, size, tile, tintable, pixels):
    opaque = [px for row in pixels for px in row if px[3] > 0]
    mean = [round(sum(px[c] for px in opaque) / len(opaque)) for c in range(3)]
    return {
        "id": tid,
        "name": tid.replace("_", " ").capitalize(),
        "file": f"{OUT_DIR.name}/{tid}.png",
        "size_px": list(size),
        "tile_m": list(tile),
        "alpha": len(opaque) < size[0] * size[1],
        "tintable": tintable,
        "mean_colour": "#%02x%02x%02x" % tuple(mean),
        "licence": "CC0",
        "source": "scripts/make_textures.py",
        "pack": OUT_DIR.name,
    }


def sheet(path, images):
    """Each texture tiled 2 x 2 side by side on a checkerboard (seams and cut-outs are easy to see)."""
    pad = 16
    cell = 512
    w, h = pad + len(images) * (cell + pad), cell + 2 * pad
    rows = [[(40, 40, 46, 255)] * w for _ in range(h)]
    for n, (size, pixels) in enumerate(images):
        ox = pad + n * (cell + pad)
        for y in range(cell):
            src = pixels[y % size[1]]
            row = rows[pad + y]
            for x in range(cell):
                r, g, b, a = src[x % size[0]]
                if a < 255:
                    c = 150 if ((x // 32) + (y // 32)) % 2 else 110
                    t = a / 255
                    r, g, b = (round(v * t + c * (1 - t)) for v in (r, g, b))
                row[ox + x] = (r, g, b, 255)
    path.parent.mkdir(parents=True, exist_ok=True)
    write_png(path, w, h, rows)


def main(argv):
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    entries, images = [], []
    for tid, size, tile, tintable, draw in TEXTURES:
        pixels = draw(*size)
        write_png(OUT_DIR / f"{tid}.png", size[0], size[1], pixels)
        entries.append(meta(tid, size, tile, tintable, pixels))
        images.append((size, pixels))
    if "--sheet" in argv:
        sheet(Path(argv[argv.index("--sheet") + 1]), images)
    if "--meta" in argv:
        print(json.dumps(entries, indent=2))
    else:
        print(f"wrote {len(entries)} textures to {OUT_DIR}")


if __name__ == "__main__":
    main(sys.argv[1:])
