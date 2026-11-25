"""Which atlas a Synty GLB wears when the GLB itself only says "synty_atlas".

Those packs ship geometry plus one untextured material; the game picks the atlas in code. The
choices below are the ones documented in assets-private/synty/README.md and the game's data.
An asset that matches no rule gets no atlas (it is drawn neutral grey and reported).
"""

from __future__ import annotations

import re
from pathlib import Path

from .. import config

ATLAS = "assets-private/synty/glb/atlas"
GANG = "assets-private/synty/gang-warfare/textures/PolygonGangWarfare_01_A.png"
KIDS = "assets-private/synty/kids/textures/PolygonKids_01_A.png"

# (source, pack) -> [(name regex, atlas relative to the repo root)], first match wins.
_RULES: dict[str, list[tuple[str, str]]] = {
    "construction": [(".*", f"{ATLAS}/construction_01_A.png")],
    "office": [(".*", f"{ATLAS}/simple_office.png")],
    "war": [(".*", f"{ATLAS}/war_01_A.png")],
    "kids": [(".*", KIDS)],
    "vehicles": [(".*", f"{ATLAS}/military_land_vehicle_01_burnt.png")],
    "props": [
        (r"^(pipe|crowbar|bat|cigarette.*|lighter|jewelry_pile)_\d+$", GANG),
        (r"^(grenade|rpg.*|lock_on_launcher)_\d+$", f"{ATLAS}/military_weapons_01.png"),
        (
            r"^(tire|boombox|speaker|stereo_speaker|dish|downpipe_connector|binoculars)_\d+$",
            f"{ATLAS}/military_01_A.png",
        ),
    ],
    "misc": [
        (r"^(pew|pew_large|altar_table|holy_water)_\d+$", f"{ATLAS}/dark_fantasy_01_A.png"),
        (r"^demon", f"{ATLAS}/horror_mansion_01_A.png"),
        (r"^(church_lectern|priest|monk|peasant)", f"{ATLAS}/fantasy_kingdom_01_A.png"),
        (r"^gang_", GANG),  # the game picks one of 12 looks per person; this is one of them
    ],
}


def atlas_for(pack: str, name: str) -> Path | None:
    for pattern, rel in _RULES.get(pack, ()):
        if re.match(pattern, name):
            return config.REPO_ROOT / rel
    return None
