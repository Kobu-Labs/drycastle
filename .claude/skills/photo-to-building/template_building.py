"""Hand-modelled NAME of Drycastle (parcel P-XXXX): ONE LINE, what it is.

From the owner's photos of DATE: WHAT THE PHOTOS SHOW (render, roof, openings), one line per side.
Guessed (no photo): LIST THE SIDES AND SIZES THAT ARE NOT FROM A PHOTO.

Built with ``landmark_kit`` on the real footprint; ``garage.py`` is the reference for anything this file does not do.
"""

import math

from drycastle_pipeline.buildings.landmarks.kit import (
    Frame,
    check_footprint,
    door_frame,
    edge,
    gable_roof,
    plinth,
    prism,
    sheet_roof,
    to_mesh,
    wall,
    window,
)
from drycastle_pipeline.buildings.landmarks.catalog import _Solids
from drycastle_pipeline.core.mesh import Mesh

MODEL = "my_building"  # the name in landmarks.FOOTPRINT_MODELS and in overrides.yaml
BUILDING = "w000000000"
# the footprint, pasted from `landmark_tools footprint` (the RING line), counter-clockwise
RING = ((0.0, 0.0), (8.0, 0.0), (8.0, 6.0), (0.0, 6.0))
RINGS = {BUILDING: RING}

# The roof slopes ACROSS edge ROOF_EDGE: for a gable that edge is a gable end (the wall with the triangle on top),
# for a lean-to it is a wall whose top is slanted, low at its first corner. The ridge and the eaves run the other way.
ROOF = "gable"  # "gable", "lean_to" or "flat"
ROOF_EDGE = 0
EAVE = 3.0  # wall top at the eave, metres above the base (lean-to: at the first corner of ROOF_EDGE)
TOP = 5.2  # gable: the ridge; lean-to: the wall top at the second corner of ROOF_EDGE; flat: not used
RIDGE_AT = 0.5  # gable: where the ridge sits along ROOF_EDGE, from its first corner (0.5: the middle; `fit` prints it)
OVERHANG = 0.3  # the roof past the eave walls
END_OH = 0.15  # the roof past the two end walls
T = 0.3  # thickness of the gable end walls

WALL, PLINTH = [238, 236, 230], [204, 205, 190]
GABLE_WALL = WALL  # the gable end walls, when they are another colour than the eave walls: [r, g, b]
FRAME_C, GLASS, DOOR = [78, 52, 42], [58, 70, 78], [92, 62, 50]
# the roof: whole sheets (or tile strips) in these colours; `worn` and `streaks` only for weathered roofs
ROOF_STYLE = {
    "sheets": ([172, 84, 58], [164, 78, 54], [180, 92, 64]), "worn": ([150, 96, 78],), "streaks": ([120, 70, 56],),
    "seam": [128, 62, 46], "underside": [112, 96, 80], "panel": 0.4,
    "worn_share": 0.1, "streak_share": 0.0, "worn_streak_share": 0.0,
    "seam_height": 0.0,  # tiles, shingles: 0.0; standing-seam sheet metal: 0.04 with "panel": 0.55
}  # fmt: skip

# Openings, drawn on the face of the wall: (edge, kind, s0, width, z0, height, casements).
# s0 is metres along the edge from its first corner, which is on the LEFT seen from outside. kind: "window" or "door".
# A door always stands on the ground (its z0 is ignored); a door high on a wall (a loft door) cannot be drawn: leave it out.
OPENINGS = (
    (0, "door", 1.0, 1.0, 0.0, 2.0, 1),
    (0, "window", 3.0, 1.4, 0.9, 1.3, 2),
)

_N = len(RING)
P, Q = RING[ROOF_EDGE], RING[(ROOF_EDGE + 1) % _N]
FRAME = Frame(P, Q, RING[(ROOF_EDGE - 1) % _N])  # a along ROOF_EDGE, v into the building
WIDTH = math.dist(P, Q)
DEPTH = max(FRAME.av(p)[1] for p in RING)


def _roof_z(a: float) -> float:
    """Underside of the roof at ``a``."""
    if ROOF == "gable":
        ridge = WIDTH * RIDGE_AT
        return EAVE + (TOP - EAVE) * (1 - abs(a - ridge) / (ridge if a < ridge else WIDTH - ridge))
    if ROOF == "lean_to":
        return EAVE + (TOP - EAVE) * a / WIDTH
    return EAVE


def building_mesh(b: dict, detail: bool = True) -> Mesh:
    """The visual mesh, or the collision (``detail`` False: the same massing, the roof as slabs, no trim)."""
    check_footprint(MODEL, b, RINGS)
    S = _Solids()
    if ROOF == "gable":
        prism(S, list(RING), [-0.3] * _N, [EAVE] * _N, WALL)  # the block up to the eave
        for i in range(_N):  # the triangle on every wall the ridge runs into
            p, q = RING[i], RING[(i + 1) % _N]
            (ap, vp), (aq, vq) = FRAME.av(p), FRAME.av(q)
            if abs(vp - vq) > 0.5:  # an eave wall: nothing above it
                continue
            t = (WIDTH * RIDGE_AT - ap) / (aq - ap)  # where the ridge meets this wall (not its middle when the footprint is skewed)
            mid = (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
            wall(S, p, mid, _roof_z(ap) + 0.05, TOP + 0.05, T, GABLE_WALL, base=EAVE - 0.05)
            wall(S, mid, q, TOP + 0.05, _roof_z(aq) + 0.05, T, GABLE_WALL, base=EAVE - 0.05)
        gable_roof(S, FRAME, WIDTH, -END_OH, DEPTH + END_OH, EAVE, TOP, detail, overhang=OVERHANG, ridge_at=RIDGE_AT, seed=BUILDING, collision=WALL, **ROOF_STYLE)
    else:
        prism(S, list(RING), [-0.3] * _N, [_roof_z(FRAME.av(p)[0]) + 0.05 for p in RING], WALL)
        if ROOF == "lean_to":
            sheet_roof(S, FRAME, _roof_z, lambda v: -OVERHANG, lambda v: WIDTH + 0.06, -END_OH, DEPTH + END_OH, detail, seed=BUILDING, collision=WALL, **ROOF_STYLE)
    if not detail:
        return to_mesh(S)
    # everything below is trim: it must stay after the line above, or it lands in the collision
    if ROOF == "gable" and GABLE_WALL != WALL:  # the gable ends in their own colour, down to the plinth
        for i in range(_N):
            p, q = RING[i], RING[(i + 1) % _N]
            if abs(FRAME.av(p)[1] - FRAME.av(q)[1]) <= 0.5:
                edge(S, p, q, 0.0, math.dist(p, q), 0.0, 0.02, 0.32, EAVE, GABLE_WALL)
    doors: dict[int, list] = {}
    for i, kind, s0, width, z0, height, casements in OPENINGS:
        p, q = RING[i], RING[(i + 1) % _N]
        if kind == "window":
            window(S, p, q, s0, s0 + width, z0, z0 + height, FRAME_C, GLASS, casements=casements)
        else:
            edge(S, p, q, s0, s0 + width, 0.0, 0.02, 0.0, height, DOOR)
            door_frame(S, p, q, s0, s0 + width, height, FRAME_C, width=0.07)
            doors.setdefault(i, []).append((s0 - 0.07, s0 + width + 0.07))
    for i in range(_N):
        plinth(S, RING[i], RING[(i + 1) % _N], PLINTH, gaps=doors.get(i, ()))
    return to_mesh(S)
