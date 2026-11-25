"""2D drawing on top of a rendered view: label, scale bar, human silhouette, contact sheet."""

from __future__ import annotations

from functools import cache

from PIL import Image, ImageDraw, ImageFont

from .camera import HUMAN_M, VIEWS, Layout

INK = (58, 58, 62)
HUMAN_FILL = (112, 118, 128)
TITLES = {"front": "FRONT", "three_quarter": "3/4", "top": "TOP", "back": "BACK"}


@cache
def font(px: int) -> ImageFont.FreeTypeFont:
    return ImageFont.load_default(size=px)


def annotate(img: Image.Image, lay: Layout) -> Image.Image:
    k = lay.size / 768
    d = ImageDraw.Draw(img)
    title = TITLES[lay.name]
    if lay.elevation is not None:
        title += f"  (from {lay.elevation:g} deg above)"
    d.text((28 * k, 14 * k), title, fill=INK, font=font(round(24 * k)))
    _scale_bar(d, lay, k)
    if lay.human_box is not None:
        _human(img, lay, k)
    return img


def _scale_bar(d: ImageDraw.ImageDraw, lay: Layout, k: float) -> None:
    x0, y = lay.bar_x, lay.bar_y
    x1 = x0 + lay.bar_m * lay.px_per_m
    w = max(2, round(3 * k))
    d.line([(x0, y), (x1, y)], fill=INK, width=w)
    for x in (x0, x1):
        d.line([(x, y - 7 * k), (x, y + 7 * k)], fill=INK, width=w)
    d.text((x0, y - 12 * k), lay.bar_label, fill=INK, font=font(round(20 * k)), anchor="ls")


def _human(img: Image.Image, lay: Layout, k: float) -> None:
    x0, y0, x1, y1 = lay.human_box
    h = y1 - y0
    ss = 4
    cw, ch = max(2, round((x1 - x0) + 4)), max(2, round(h + 4))
    patch = Image.new("L", (cw * ss, ch * ss), 0)
    pd = ImageDraw.Draw(patch)
    cx, feet = cw * ss / 2, (ch - 2) * ss

    def pt(x: float, y: float) -> tuple[float, float]:  # x, y in units of the figure's height
        return cx + x * h * ss, feet - y * h * ss

    pd.ellipse([*pt(-0.052, 0.97), *pt(0.052, 0.866)], fill=255)
    pd.polygon([pt(-0.11, 0.84), pt(0.11, 0.84), pt(0.09, 0.52), pt(-0.09, 0.52)], fill=255)
    for s in (-1, 1):
        pd.polygon(
            [
                pt(s * 0.105, 0.835),
                pt(s * 0.14, 0.80),
                pt(s * 0.14, 0.47),
                pt(s * 0.115, 0.47),
                pt(s * 0.105, 0.70),
            ],
            fill=255,
        )
        pd.polygon(
            [pt(s * 0.01, 0.53), pt(s * 0.09, 0.53), pt(s * 0.08, 0.0), pt(s * 0.015, 0.0)],
            fill=255,
        )
    patch = patch.resize((cw, ch), Image.Resampling.LANCZOS)
    img.paste(
        Image.new("RGB", (cw, ch), HUMAN_FILL),
        (round((x0 + x1) / 2 - cw / 2), round(y0 - 2)),
        patch,
    )
    d = ImageDraw.Draw(img)
    d.text(
        ((x0 + x1) / 2, y0 - 6 * k),
        f"{HUMAN_M:g} m",
        fill=INK,
        font=font(round(18 * k)),
        anchor="ms",
    )


def contact_sheet(tiles: dict[str, Image.Image]) -> Image.Image:
    w, h = tiles[VIEWS[0]].size
    sheet = Image.new("RGB", (2 * w, 2 * h), (255, 255, 255))
    for name, (cx, cy) in zip(
        ("front", "three_quarter", "top", "back"), ((0, 0), (1, 0), (0, 1), (1, 1)), strict=True
    ):
        sheet.paste(tiles[name], (cx * w, cy * h))
    d = ImageDraw.Draw(sheet)
    d.line([(w, 0), (w, 2 * h)], fill=(150, 150, 150), width=2)
    d.line([(0, h), (2 * w, h)], fill=(150, 150, 150), width=2)
    return sheet
