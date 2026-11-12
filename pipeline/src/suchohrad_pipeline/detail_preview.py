"""A top-down picture of the ground detail of one parcel (Pillow), for judging the look without the game.

    uv run python -m suchohrad_pipeline.detail_preview [--parcel P-0637] [--area village] [--out DIR]
                                                       [--px-per-m 28] [--zoom 70]

writes ``<out>/detail_<parcel>.png`` (whole parcel) and ``detail_<parcel>_zoom.png`` (the busiest 14 m
square, at ``--zoom`` pixels per metre) into ``out/detail`` by default: terrain colour, the patches with
their real library textures, the clutter as small shapes by kind, then buildings, fences and trees, a scale
bar and a legend. The shapes of the clutter are stand-ins for the meshes the game will draw.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from suchohrad_pipeline.assets import ASSETS_DIR
from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.config import REPO_ROOT, area_paths
from suchohrad_pipeline.detail import ParcelContext, DetailResult, run_parcel
from suchohrad_pipeline.rules import load_section
from suchohrad_pipeline.textures import texture

DEFAULT_OUT = REPO_ROOT / "out" / "detail"
OUTSIDE = (122, 154, 106)
ROAD = (138, 140, 144)
TUFT = [(88, 140, 60), (112, 160, 72), (70, 118, 54)]
FLOWER = [(245, 245, 235), (240, 205, 70), (232, 120, 150), (214, 70, 60), (150, 110, 200)]
PEBBLE = [(150, 148, 140), (120, 118, 112), (176, 168, 150)]
ROCK = [(112, 110, 106), (132, 126, 116), (98, 96, 94)]
LEAF = [(176, 92, 46), (200, 140, 52), (140, 110, 44), (110, 122, 52)]


def _font(size: int):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


class Canvas:
    """World (x east, y north, metres) to pixels, y flipped, over the box ``(x0, y0, x1, y1)``."""

    def __init__(self, box: tuple[float, float, float, float], px: float) -> None:
        self.x0, self.y0, self.x1, self.y1, self.px = *box, px
        self.w, self.h = int(math.ceil((self.x1 - self.x0) * px)), int(math.ceil((self.y1 - self.y0) * px))
        self.img = Image.new("RGB", (self.w, self.h), OUTSIDE)
        xs = self.x0 + (np.arange(self.w) + 0.5) / px
        ys = self.y1 - (np.arange(self.h) + 0.5) / px
        self.X, self.Y = np.meshgrid(xs, ys)

    def pt(self, x: float, y: float) -> tuple[float, float]:
        return (x - self.x0) * self.px, (self.y1 - y) * self.px

    def poly(self, geom) -> list[list[tuple[float, float]]]:
        """Pixel rings (exterior first, then holes) of every polygon of ``geom``."""
        out = []
        for p in getattr(geom, "geoms", [geom]):
            if p.is_empty or p.geom_type != "Polygon":
                continue
            out.append([self.pt(x, y) for x, y in p.exterior.coords])
            out += [[self.pt(x, y) for x, y in r.coords] for r in p.interiors]
        return out

    def mask(self, geom) -> np.ndarray:
        """Boolean pixel mask of ``geom`` (holes cut out)."""
        m = Image.new("L", (self.w, self.h), 0)
        d = ImageDraw.Draw(m)
        for p in getattr(geom, "geoms", [geom]):
            if p.is_empty or p.geom_type != "Polygon":
                continue
            d.polygon([self.pt(x, y) for x, y in p.exterior.coords], fill=255)
            for r in p.interiors:
                d.polygon([self.pt(x, y) for x, y in r.coords], fill=0)
        return np.asarray(m) > 0

    def fill(self, geom, colour) -> None:
        a = np.asarray(self.img).copy()
        a[self.mask(geom)] = colour
        self.img = Image.fromarray(a)

    def fill_texture(self, geom, tid: str, angle: float, origin) -> None:
        """Fill with a library texture, laid out exactly as ``detail.patch_meshes`` lays out the UVs."""
        tex = texture(tid)
        png = np.asarray(Image.open(ASSETS_DIR / "suchohrad-generated-textures" / f"{tid}.png").convert("RGB"))
        c, s = math.cos(-angle), math.sin(-angle)
        dx, dy = self.X - origin[0], self.Y - origin[1]
        u, v = (dx * c - dy * s) / tex.tile_w, (dx * s + dy * c) / tex.tile_h
        h, w = png.shape[:2]
        tx, ty = (np.floor(u * w) % w).astype(int), (np.floor((1 - v) * h) % h).astype(int)
        a = np.asarray(self.img).copy()
        m = self.mask(geom)
        a[m] = png[ty, tx][m]
        self.img = Image.fromarray(a)


def render(ctx: ParcelContext, res: DetailResult, cfg: dict, box: tuple[float, float, float, float], px: float,
           buildings_cfg: dict, trees_cfg: dict, title: str) -> Image.Image:
    cv = Canvas(box, px)
    kind_colour = hex_to_rgb(load_section("terrain")["colours"].get(ctx.kind, ["#c9b78f"])[0])
    if ctx.road is not None:
        cv.fill(ctx.road, ROAD)
    cv.fill(ctx.polygon.difference(ctx.road) if ctx.road is not None else ctx.polygon, kind_colour)
    for p in res.patches:
        if p.texture:
            cv.fill_texture(p.geom, p.texture, p.angle, p.origin)
        else:
            cv.fill(p.geom, p.colour)
    d = ImageDraw.Draw(cv.img)
    s = px / 28  # clutter shapes are drawn for 28 px per metre

    def inside(x: float, y: float) -> bool:
        return box[0] - 1 <= x <= box[2] + 1 and box[1] - 1 <= y <= box[3] + 1

    order = {"leaf": 0, "pebble": 1, "tuft": 2, "rock": 3, "flower": 4}
    for c in sorted(res.clutter, key=lambda c: order[c["kind"]]):
        if not inside(c["x"], c["y"]):
            continue
        x, y = cv.pt(c["x"], c["y"])
        k, v, sc, rot = c["kind"], c["variant"], c["scale"], c["rot"]
        if k == "tuft":
            col, n = TUFT[v % 3], 5
            for i in range(n):  # a few blades fanning up from the base
                a = rot + (i - 2) * 0.55
                d.line([(x, y), (x + math.sin(a) * 6 * s * sc, y - 9 * s * sc * (1 - abs(i - 2) * 0.12))], fill=col, width=max(1, round(1.6 * s)))
        elif k == "flower":
            r = 2.6 * s * sc
            d.line([(x, y), (x, y + 3 * s)], fill=(70, 120, 50), width=max(1, round(s)))
            d.ellipse([x - r, y - r, x + r, y + r], fill=FLOWER[v % 5], outline=(90, 90, 60))
        elif k == "pebble":
            r = 1.7 * s * sc
            d.ellipse([x - r, y - r * 0.75, x + r, y + r * 0.75], fill=PEBBLE[v % 3])
        elif k == "rock":
            r = 6.5 * s * sc
            pts = [(x + r * (0.8 + 0.25 * math.sin(i * 2.3 + v)) * math.cos(rot + i * math.tau / 7), y + r * 0.8 * (0.8 + 0.25 * math.sin(i * 2.3 + v)) * math.sin(rot + i * math.tau / 7)) for i in range(7)]
            d.polygon(pts, fill=ROCK[v % 3], outline=(70, 70, 68))
        else:  # leaf
            r = 2.4 * s * sc
            a = rot
            d.line([(x - math.cos(a) * r, y - math.sin(a) * r), (x + math.cos(a) * r, y + math.sin(a) * r)], fill=LEAF[v % 4], width=max(1, round(2.2 * s)))
    # buildings, fences, trees
    for b in ctx.buildings:
        for ring in cv.poly(b):
            d.polygon(ring, fill=(168, 68, 47), outline=(70, 30, 25))
    for f in ctx.fences:
        d.line([cv.pt(x, y) for x, y in f.coords], fill=(120, 96, 66), width=max(1, round(2 * px / 28)))
    canopy = float(trees_cfg["canopy_m"])
    for x, y, sc in ctx.trees:
        cx, cy = cv.pt(x, y)
        r = canopy * sc / 2 * px
        d.rectangle([cx - r, cy - r, cx + r, cy + r], outline=(40, 90, 40), width=max(2, round(2 * px / 28)))
        t = 0.2 * sc * px
        d.rectangle([cx - t, cy - t, cx + t, cy + t], fill=(107, 74, 47))
    d.line([cv.pt(*c) for c in ctx.polygon.exterior.coords], fill=(255, 255, 255), width=1)
    # scale bar and title
    f1, f2 = _font(max(12, round(px * 0.55))), _font(max(11, round(px * 0.45)))
    bar = 5 if (box[2] - box[0]) > 20 else 2
    bx, by = 14, cv.h - 18
    d.rectangle([bx - 6, by - 26, bx + bar * px + 56, by + 14], fill=(30, 30, 34))
    d.rectangle([bx, by, bx + bar * px, by + 4], fill=(240, 240, 240))
    d.text((bx, by - 22), f"{bar} m", fill=(240, 240, 240), font=f2)
    d.rectangle([0, 0, cv.w, 34], fill=(30, 30, 34))
    d.text((10, 6), title, fill=(240, 240, 240), font=f1)
    return cv.img


def legend(width: int, counts: dict[str, int]) -> Image.Image:
    img = Image.new("RGB", (width, 38), (30, 30, 34))
    d, f = ImageDraw.Draw(img), _font(13)
    x = 10
    for name, col in (("tuft", TUFT[0]), ("flower", FLOWER[3]), ("pebble", PEBBLE[0]), ("rock", ROCK[0]), ("leaf", LEAF[0])):
        d.ellipse([x, 12, x + 12, 24], fill=col)
        d.text((x + 18, 10), f"{name} {counts[name]}", fill=(230, 230, 230), font=f)
        x += 110
    return img


def densest_box(res: DetailResult, size: float, bounds) -> tuple[float, float, float, float]:
    """The ``size``-metre square (corner on a 2 m grid) holding the most clutter, kept inside ``bounds``."""
    best, at = -1, (bounds[0], bounds[1])
    pts = np.array([(c["x"], c["y"]) for c in res.clutter]) if res.clutter else np.zeros((0, 2))
    x = bounds[0]
    while x + size <= bounds[2] + 1e-6 or x == bounds[0]:
        y = bounds[1]
        while y + size <= bounds[3] + 1e-6 or y == bounds[1]:
            n = int(((pts[:, 0] >= x) & (pts[:, 0] < x + size) & (pts[:, 1] >= y) & (pts[:, 1] < y + size)).sum()) if len(pts) else 0
            if n > best:
                best, at = n, (x, y)
            y += 2
        x += 2
    return at[0], at[1], at[0] + size, at[1] + size


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Top-down picture of the ground detail of one parcel.")
    ap.add_argument("--parcel", default="P-0637")
    ap.add_argument("--area", default="village")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--px-per-m", type=float, default=28.0)
    ap.add_argument("--zoom", type=float, default=70.0, help="pixels per metre of the close-up")
    a = ap.parse_args(argv)
    paths = area_paths(a.area)
    ctx, res = run_parcel(a.parcel, paths.work, area=a.area)
    cfg = load_section("detail", area=a.area)
    bcfg, tcfg = {}, load_section("props", area=a.area)["trees"]
    a.out.mkdir(parents=True, exist_ok=True)
    minx, miny, maxx, maxy = ctx.polygon.bounds
    box = (minx - 2, miny - 2, maxx + 2, maxy + 2)
    counts = res.counts()
    title = f"{a.parcel} ({ctx.kind}, {ctx.polygon.area:.0f} m2)  patches {len(res.patches)}  clutter {sum(counts.values())}"
    whole = render(ctx, res, cfg, box, a.px_per_m, bcfg, tcfg, title)
    leg = legend(whole.width, counts)
    full = Image.new("RGB", (whole.width, whole.height + leg.height))
    full.paste(whole, (0, 0))
    full.paste(leg, (0, whole.height))
    p1 = a.out / f"detail_{a.parcel}.png"
    full.save(p1)
    zbox = densest_box(res, 14.0, (minx, miny, maxx, maxy))
    zoom = render(ctx, res, cfg, zbox, a.zoom, bcfg, tcfg, f"{a.parcel} close-up, 14 m square")
    p2 = a.out / f"detail_{a.parcel}_zoom.png"
    zoom.save(p2)
    print(json.dumps({"parcel": a.parcel, "counts": counts, "images": [str(p1), str(p2)]}))


if __name__ == "__main__":
    main()
