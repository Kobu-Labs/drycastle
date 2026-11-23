#!/usr/bin/env python3
"""Draws the binoculars item icon, godot/shooter/ui/icons/items/binoculars.png (128x128, transparent), in the flat style of the speaker's icon:
a dark outline, grey bodies, an orange band. Two barrels side by side seen from above the eyepieces, a bridge between them. Needs Pillow.

    python3 scripts/art/make_binoculars_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "godot/shooter/ui/icons/items/binoculars.png"
SS = 8
INK, BODY, DARK, LENS, GLASS, BAND = (26, 28, 31, 255), (92, 97, 106, 255), (62, 66, 74, 255), (44, 47, 53, 255), (150, 190, 205, 255), (240, 130, 30, 255)


def main() -> None:
    img = Image.new("RGBA", (128 * SS, 128 * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def box(x0, y0, x1, y1, r, fill, ink=4):
        d.rounded_rectangle([x0 * SS, y0 * SS, x1 * SS, y1 * SS], r * SS, fill=fill, outline=INK, width=ink * SS)

    def disc(cx, cy, r, fill, ink=4):
        d.ellipse([(cx - r) * SS, (cy - r) * SS, (cx + r) * SS, (cy + r) * SS], fill=fill, outline=INK, width=ink * SS)

    box(50, 46, 78, 74, 5, DARK)                      # the bridge
    disc(64, 60, 7, BODY)                             # the focus wheel
    for cx in (37, 91):                               # a barrel: the eyepiece on top, the body, the band, the objective lens at the bottom
        box(cx - 11, 14, cx + 11, 40, 5, DARK)
        box(cx - 19, 30, cx + 19, 104, 9, BODY)
        d.rectangle([(cx - 17) * SS, 44 * SS, (cx + 17) * SS, 52 * SS], fill=BAND)
        disc(cx, 94, 17, LENS)
        disc(cx, 94, 10, GLASS, ink=3)
    img.resize((128, 128), Image.LANCZOS).save(OUT, optimize=True)
    print(OUT)


if __name__ == "__main__":
    main()
