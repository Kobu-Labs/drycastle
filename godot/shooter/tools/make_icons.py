"""Builds the non-weapon icons of godot/shooter/ui/icons (run by `make shooter-icons` after render_icons.gd).

Perk and killstreak glyphs and the placeholder are authored here as flat SVG (white on transparent, 128x128 viewBox),
written to ui/icons/src/ for review and rasterised with ImageMagick (librsvg). Category badges are white silhouettes
of one representative weapon each, cut from ui/icons/weapons/<id>.png, so run after render_icons.gd.

The theme tints the PNGs; they are never coloured here. Original work of this repository (no third-party art).
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

from PIL import Image

ICONS = Path(__file__).resolve().parent.parent / "ui" / "icons"
SRC = ICONS / "src"

W = '#ffffff'


def svg(body: str) -> str:
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">'
        f'<g fill="{W}" stroke="{W}" stroke-linecap="round" stroke-linejoin="round">{body}</g></svg>\n'
    )


PERKS = {
    # run speed: double chevron
    "fleet_foot": '<g fill="none" stroke-width="12"><path d="M38 32 L72 64 L38 96"/><path d="M66 32 L100 64 L66 96"/></g>',
    # reload speed: circular arrow
    "quick_hands": '<path d="M96 64 A32 32 0 1 1 84 39" fill="none" stroke-width="12"/><path d="M76 24 L102 30 L92 54 Z" stroke-width="2"/>',
    # explosive damage taken: shield
    "flak_jacket": '<path d="M64 14 L106 28 V64 C106 90 88 106 64 116 C40 106 22 90 22 64 V28 Z"/>',
    # weapon swap speed: opposing arrows
    "swift_swap": '<g fill="none" stroke-width="10"><path d="M18 46 H88"/><path d="M110 82 H40"/></g><path d="M84 30 L108 46 L84 62 Z"/><path d="M44 66 L20 82 L44 98 Z"/>',
    # damage to vehicles: tank
    "tank_hunter": '<rect x="12" y="92" width="104" height="22" rx="11"/><rect x="24" y="72" width="80" height="20" rx="4"/><path d="M42 72 A22 18 0 0 1 86 72 Z"/><rect x="60" y="60" width="56" height="8"/>',
    # every killstreak threshold lower: flame
    "hot_streak": '<path d="M64 12 C80 34 100 48 100 76 C100 100 84 116 64 116 C44 116 28 100 28 78 C28 62 38 52 46 40 C52 54 56 62 60 66 C62 48 60 30 64 12 Z"/>',
    # breath hold time: lungs
    "steady_lung": '<path d="M58 40 C40 38 24 52 22 78 C20 98 30 110 44 108 C54 106 58 98 58 88 Z"/><path d="M70 40 C88 38 104 52 106 78 C108 98 98 110 84 108 C74 106 70 98 70 88 Z"/><path d="M64 12 V50" fill="none" stroke-width="8"/>',
    # UAV visibility: ghost (eyes cut out)
    "ghost": '<path fill-rule="evenodd" d="M28 116 V60 C28 36 44 20 64 20 C84 20 100 36 100 60 V116 L90 106 L80 116 L70 106 L64 116 L58 106 L48 116 L38 106 Z M40 58 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 Z M72 58 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 Z"/>',
}

HELI = (
    '<ellipse cx="56" cy="72" rx="30" ry="17"/><path d="M80 66 H112 V76 H80 Z"/><rect x="106" y="56" width="8" height="26"/>'
    '<rect x="18" y="40" width="92" height="6"/><rect x="54" y="44" width="6" height="12"/>'
    '<rect x="36" y="96" width="44" height="5"/><rect x="40" y="88" width="4" height="10"/><rect x="66" y="88" width="4" height="10"/>'
)

STREAKS = {
    # UAV: quadrotor
    "uav": '<g fill="none" stroke-width="8"><path d="M30 30 L98 98 M98 30 L30 98"/><circle cx="30" cy="30" r="15"/><circle cx="98" cy="30" r="15"/><circle cx="30" cy="98" r="15"/><circle cx="98" cy="98" r="15"/></g><circle cx="64" cy="64" r="16"/>',
    # attack missile: missile with fins and exhaust
    "attack_missile": '<path d="M64 8 C78 24 82 44 82 66 V96 H46 V66 C46 44 50 24 64 8 Z"/><path d="M46 76 L30 98 L46 96 Z"/><path d="M82 76 L98 98 L82 96 Z"/><path d="M52 102 L64 124 L76 102 Z"/>',
    # sentry turret: mound, head and barrel
    "turret": '<path d="M28 108 L40 86 H88 L100 108 Z"/><circle cx="64" cy="70" r="20"/><rect x="62" y="63" width="48" height="13"/>',
    # care package: parachute over a crate
    "care_package": '<path d="M16 48 Q64 0 112 48 Z"/><g fill="none" stroke-width="4"><path d="M16 48 L46 86"/><path d="M112 48 L82 86"/><path d="M64 24 L64 86"/></g><rect x="44" y="84" width="40" height="34" rx="3"/>',
    # attack helicopter
    "attack_helicopter": HELI,
    # helicopter helper: smaller helicopter over a target reticle
    "helicopter_helper": '<g transform="translate(14 -6) scale(0.72)">' + HELI + '</g><g fill="none" stroke-width="7"><circle cx="64" cy="100" r="18"/></g><circle cx="64" cy="100" r="5"/><path d="M64 74 V80 M64 120 V126 M38 100 H44 M84 100 H90" fill="none" stroke-width="6"/>',
    # AC-130: top-down gunship
    "ac130": '<path d="M64 10 L72 40 L112 60 V72 L72 66 L70 100 L86 112 V118 L64 112 L42 118 V112 L58 100 L56 66 L16 72 V60 L56 40 Z"/>',
    # MOAB: mushroom cloud
    "moab": '<ellipse cx="64" cy="38" rx="42" ry="24"/><path d="M52 52 H76 L84 100 H44 Z"/><rect x="28" y="104" width="72" height="8" rx="3"/>',
}

CATEGORY_SOURCE = {
    "pistol": "glock17",
    "machine_gun": "rpk",
    "assault_rifle": "ak47",
    "smg": "mp5",
    "shotgun": "remington870",
    "sniper": "sniper",
}

PLACEHOLDER = (
    '<rect x="18" y="18" width="92" height="92" rx="10" fill="none" stroke-width="8"/>'
    '<path d="M40 40 L88 88 M88 40 L40 88" fill="none" stroke-width="8"/>'
)


def rasterise(svg_text: str, name: str, out_dir: Path) -> None:
    SRC.mkdir(parents=True, exist_ok=True)
    (SRC / f"{name}.svg").write_text(svg_text, encoding="utf-8")
    out = out_dir / f"{name}.png"
    subprocess.run(
        ["magick", "-background", "none", "-density", "384", str(SRC / f"{name}.svg"),
         "-resize", "128x128", "-gravity", "center", "-extent", "128x128", "PNG32:" + str(out)],
        check=True,
    )


def badge(weapon_png: Path, out: Path) -> None:
    img = Image.open(weapon_png).convert("RGBA")
    box = img.getchannel("A").getbbox()
    if box is None:
        raise SystemExit(f"empty render: {weapon_png}")
    img = img.crop(box)
    img.thumbnail((116, 116), Image.LANCZOS)
    white = Image.new("RGBA", img.size, (255, 255, 255, 0))
    white.putalpha(img.getchannel("A"))
    canvas = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
    canvas.paste(white, ((128 - img.width) // 2, (128 - img.height) // 2), white)
    canvas.save(out)


def main() -> int:
    perks_dir = ICONS / "perks"
    streaks_dir = ICONS / "streaks"
    cats_dir = ICONS / "categories"
    for d in (perks_dir, streaks_dir, cats_dir):
        d.mkdir(parents=True, exist_ok=True)
    for name, body in PERKS.items():
        rasterise(svg(body), name, perks_dir)
    for name, body in STREAKS.items():
        rasterise(svg(body), name, streaks_dir)
    rasterise(svg(PLACEHOLDER), "placeholder", ICONS)
    for cat, weapon in CATEGORY_SOURCE.items():
        src = ICONS / "weapons" / f"{weapon}.png"
        if not src.exists():
            print(f"missing weapon render {src}; run render_icons.gd first", file=sys.stderr)
            return 1
        badge(src, cats_dir / f"{cat}.png")
    print(f"perks {len(PERKS)}, streaks {len(STREAKS)}, categories {len(CATEGORY_SOURCE)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
