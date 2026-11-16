"""Hand-modelled house of property G-0377 (parcel P-0023, building w378920410): one storey with an attic, gabled.

From the owner's Street View photo of 2026-11-16, taken from around sketch point A: the long eave wall (edge 0, 7.16 m)
is pale green render on a brick plinth with two white windows; the gable end (edge 1, 8.24 m) is white with a small
window to the right; the roof is dark green asphalt shingle, pitched, with white bargeboards.
Guessed: eave 2.6 m and ridge 6.0 m (scaled from the window and gate heights, no scale object in the photo), roof
overhangs, the window positions (read off an oblique view, about 0.3 m), all colours (the photo is in shade), the plinth
colour, and the two sides with no photo (edge 2: eave wall, no openings; edge 3: gable end, white, no openings).
Left out (not covered by the skill): the loft door with its balcony and outside staircase on the gable end, the gate
and the boundary wall in front of it, the lean-to shed at the right, the drainpipe and the junction box.

Built with ``landmark_kit`` on the real footprint; ``garage.py`` is the reference for anything this file does not do.
"""

import math

from drycastle_pipeline.landmark_kit import (
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
from drycastle_pipeline.landmarks import _Solids
from drycastle_pipeline.meshes import Mesh

MODEL = "house_g0377"  # the name in landmarks.FOOTPRINT_MODELS and in overrides.yaml
BUILDING = "w378920410"
# the footprint, pasted from `landmark_tools footprint` (the RING line), counter-clockwise
RING = ((-2.548, -17.302), (-0.543, -24.175), (7.08, -21.049), (4.763, -14.803))
RINGS = {BUILDING: RING}

ROOF = "gable"  # "gable", "lean_to" or "flat"
ROOF_EDGE = 1  # the gable end seen in the photo (8.24 m, faces S)
EAVE = 2.6  # wall top at the eave, metres above the base
TOP = 6.0  # the ridge
OVERHANG = 0.2  # the roof past the eave walls
END_OH = 0.25  # the roof past the two gable ends
T = 0.3  # thickness of the gable end walls

WALL, PLINTH = [150, 186, 166], [128, 80, 64]
GABLE_WALL = [240, 242, 244]  # the white gable ends (edges 1 and 3), the eave walls are green
FRAME_C, GLASS, DOOR = [220, 222, 222], [58, 70, 78], [92, 62, 50]
# the roof: whole sheets (or tile strips) in these colours; `worn` and `streaks` only for weathered roofs
ROOF_STYLE = {
    "sheets": ([92, 108, 88], [100, 116, 92], [84, 100, 82]), "worn": ([84, 100, 82],), "streaks": ([84, 100, 82],),
    "seam": [66, 80, 66], "underside": [200, 204, 204], "panel": 0.4,
    "worn_share": 0.0, "streak_share": 0.0, "worn_streak_share": 0.0, "seam_height": 0.0,
}  # fmt: skip

# Openings, drawn on the face of the wall: (edge, kind, s0, width, z0, height, casements).
OPENINGS = (
    (0, "window", 0.95, 1.1, 0.75, 1.25, 1),
    (0, "window", 4.05, 1.1, 0.75, 1.25, 2),
    (1, "window", 6.0, 0.7, 0.8, 1.2, 1),
)

_N = len(RING)
P, Q = RING[ROOF_EDGE], RING[(ROOF_EDGE + 1) % _N]
FRAME = Frame(P, Q, RING[(ROOF_EDGE - 1) % _N])  # a along ROOF_EDGE, v into the building
WIDTH = math.dist(P, Q)
DEPTH = max(FRAME.av(p)[1] for p in RING)


def _roof_z(a: float) -> float:
    """Underside of the roof at ``a``."""
    if ROOF == "gable":
        return EAVE + (TOP - EAVE) * (1 - abs(a - WIDTH / 2) / (WIDTH / 2))
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
            t = (WIDTH / 2 - ap) / (aq - ap)  # where the ridge meets this wall (not its middle when the footprint is skewed)
            mid = (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
            wall(S, p, mid, _roof_z(ap) + 0.05, TOP + 0.05, T, GABLE_WALL, base=EAVE - 0.05)
            wall(S, mid, q, TOP + 0.05, _roof_z(aq) + 0.05, T, GABLE_WALL, base=EAVE - 0.05)
        gable_roof(S, FRAME, WIDTH, -END_OH, DEPTH + END_OH, EAVE, TOP, detail, overhang=OVERHANG, seed=BUILDING, collision=WALL, **ROOF_STYLE)
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
