"""A top-down picture of the ground detail of one parcel (Pillow), for judging the look without the game.

    uv run python -m suchohrad_pipeline.detail_preview [--parcel P-0637] [--area village] [--out DIR]
                                                       [--work DIR] [--px-per-m 28] [--zoom 70]

writes ``<out>/detail_<parcel>.png`` (the parcel, or the busiest 40 m square of a big one) and
``detail_<parcel>_zoom.png`` (the busiest 14 m square, at ``--zoom`` pixels per metre) into ``out/detail`` by
default: terrain colour, the patches with their real library textures, the clutter drawn from its real meshes
(``detail_meshes``, seen from above, shaded by facing), then buildings, fences and trees, a scale bar and a
legend. ``--work`` reads another work directory (a copy of the area's, for experiments).
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
from suchohrad_pipeline.detail import CLUTTER_KINDS, DetailResult, ParcelContext, run_parcel
from suchohrad_pipeline.detail_meshes import clutter_meshes, tint_rgb
from suchohrad_pipeline.rules import load_section
from suchohrad_pipeline.textures import texture

DEFAULT_OUT = REPO_ROOT / "out" / "detail"
OUTSIDE = (122, 154, 106)
ROAD = (138, 140, 144)
LEGEND = {"tuft": (88, 140, 60), "flower": (232, 120, 150), "pebble": (150, 148, 140), "rock": (112, 110, 106),
          "leaf": (176, 92, 46), "stick": (110, 79, 51), "fern": (61, 122, 58)}
DRAW_ORDER = {k: i for i, k in enumerate(("leaf", "stick", "pebble", "tuft", "fern", "rock", "flower"))}


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
    kind_colour = tuple(ctx.colour) if ctx.colour else hex_to_rgb(load_section("terrain")["colours"].get(ctx.kind, ["#c9b78f"])[0])
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

    meshes = {k: m.tris for k, m in clutter_meshes().items()}
    cols = {k: m.colours for k, m in clutter_meshes().items()}
    for c in sorted(res.clutter, key=lambda c: DRAW_ORDER[c["kind"]]):
        if not inside(c["x"], c["y"]):
            continue
        tris, colours = meshes[c["kind"]], cols[c["kind"]]
        tf = np.array(tint_rgb(c["tint"])) if "tint" in c else 1.0
        co, si = math.cos(c["rot"]), math.sin(c["rot"])
        x = c["x"] + c["scale"] * (tris[:, :, 0] * co - tris[:, :, 1] * si)
        y = c["y"] + c["scale"] * (tris[:, :, 0] * si + tris[:, :, 1] * co)
        z = tris[:, :, 2] * c["scale"]
        # facing: the vertical part of the normal (taller faces seen from above are lit more)
        n = np.cross(np.stack([x, y, z], axis=2)[:, 1] - np.stack([x, y, z], axis=2)[:, 0],
                     np.stack([x, y, z], axis=2)[:, 2] - np.stack([x, y, z], axis=2)[:, 0])
        shade = 0.72 + 0.28 * np.abs(n[:, 2]) / np.maximum(np.linalg.norm(n, axis=1), 1e-9)
        for i in np.argsort(z.mean(axis=1)):
            if n[i, 2] <= 0 and c["kind"] not in ("leaf",):
                continue  # facing down: hidden from above
            d.polygon([cv.pt(x[i, j], y[i, j]) for j in range(3)], fill=tuple(int(min(255, v)) for v in colours[i] * shade[i] * tf))
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
    for name in CLUTTER_KINDS:
        d.ellipse([x, 12, x + 12, 24], fill=LEGEND[name])
        d.text((x + 18, 10), f"{name} {counts[name]}", fill=(230, 230, 230), font=f)
        x += 104
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
    ap.add_argument("--work", type=Path, default=None, help="work directory (default: the area's)")
    ap.add_argument("--window", type=float, default=40.0, help="side of the square shown of a parcel bigger than this, metres")
    ap.add_argument("--px-per-m", type=float, default=28.0)
    ap.add_argument("--zoom", type=float, default=70.0, help="pixels per metre of the close-up")
    ap.add_argument("--meadow", action="store_true", help="field and forest: draw what the game generates from the meadow raster")
    a = ap.parse_args(argv)
    paths = area_paths(a.area)
    ctx, res = run_parcel(a.parcel, a.work or paths.work, area=a.area, with_meadow=a.meadow)
    cfg = load_section("detail", area=a.area)
    bcfg, tcfg = {}, load_section("props", area=a.area)["trees"]
    a.out.mkdir(parents=True, exist_ok=True)
    minx, miny, maxx, maxy = ctx.polygon.bounds
    box = (minx - 2, miny - 2, maxx + 2, maxy + 2)
    if max(maxx - minx, maxy - miny) > a.window * 1.5:  # a big parcel: the busiest window of it
        box = densest_box(res, a.window, (minx, miny, maxx, maxy))
    counts = res.counts()
    title = f"{a.parcel} ({ctx.kind}, {ctx.polygon.area:.0f} m2)  patches {len(res.patches)}  clutter {sum(counts.values())}"
    whole = render(ctx, res, cfg, box, a.px_per_m, bcfg, tcfg, title)
    leg = legend(whole.width, counts)
    full = Image.new("RGB", (whole.width, whole.height + leg.height))
    full.paste(whole, (0, 0))
    full.paste(leg, (0, whole.height))
    suffix = "_meadow" if a.meadow else ""
    p1 = a.out / f"detail_{a.parcel}{suffix}.png"
    full.save(p1)
    zbox = densest_box(res, 14.0, (minx, miny, maxx, maxy))
    zoom = render(ctx, res, cfg, zbox, a.zoom, bcfg, tcfg, f"{a.parcel} ({ctx.kind}) close-up, 14 m square")
    p2 = a.out / f"detail_{a.parcel}{suffix}_zoom.png"
    zoom.save(p2)
    print(json.dumps({"parcel": a.parcel, "counts": counts, "images": [str(p1), str(p2)]}))


if __name__ == "__main__":
    main()
