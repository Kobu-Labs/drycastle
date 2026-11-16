"""Hand-modelled bus garage of Drycastle (parcel P-0666, property G-0124, next to the church): two footprints sharing a wall.

From the owner's photo of the front (2026-11-16): white render, two lean-to roofs falling away from the shared wall (the
right-hand one higher), a brown double garage door in the right-hand part and a barred window in the left-hand one. The
roof is old standing-seam sheet metal, zinc grey with rust (the owner's second photo).

``DEEP`` (``w378900337``, 14.8 m) is the bus bay: hollow, with a doorway in its front wall sized for the bus (12.06 x
2.56 x 3.1 m, ``vehicles.json``), so the walls and the door are higher than in the photo. The door leaves are not in
the mesh: the game hangs them in the ``doorway`` of ``garage.json`` and swings them. ``SIDE`` (``w1226408502``) stays a
solid block with the window drawn on it.

Frame: ``(a, v)`` in metres from the bay's front left corner ``DF`` (seen from inside; the right-hand end of the front
seen from the yard), ``a`` across the front towards the side part, ``v`` into the building. z is up from the building base.
"""

import itertools
import math
import random

import numpy as np

from drycastle_pipeline.landmarks import _Solids
from drycastle_pipeline.meshes import Mesh

DEEP, SIDE = "w378900337", "w1226408502"

# the footprints (data/village/work/buildings.json), counter-clockwise; ``_check`` holds the building against them
SH0, SH1 = (-254.714, 260.372), (-249.69, 268.465)  # the shared wall, back end and front end
DF, DB, DK = (-254.594, 271.394), (-262.161, 258.647), (-257.542, 256.117)  # bay: front corner, back corner, back corner on the side part's side
SB, SF = (-250.403, 257.684), (-245.494, 265.957)  # side part: back corner, front corner
RINGS = {DEEP: (SH0, SH1, DF, DB, DK), SIDE: (SH1, SH0, SB, SF)}

T = 0.3  # walls
F = 0.05  # floor, above the building base
DEEP_LOW, DEEP_HIGH = 4.0, 5.0  # underside of the bay's roof: at its outer long wall, at the shared wall
SIDE_HIGH, SIDE_LOW = 3.9, 2.9  # the side part's roof: at the shared wall, at its outer wall
DOOR_W, DOOR_H = 3.6, 3.5  # the bus is 2.56 wide and 3.1 high
ROOF_T = 0.1  # sheet and boarding
EAVE_OH, END_OH = 0.3, 0.15  # roof overhang: at the low eaves, at the front and back
PANEL = 0.55  # between two standing seams
RAMP = 1.5  # the apron in front of the door slopes down to the ground over this much
PARK_CLEAR = 1.0  # between the closed door and the nose of the parked bus
VEHICLE, VEHICLE_HALF_LENGTH = "bus", 6.03

WALL, PLINTH, FLOOR, STAIN = [238, 236, 230], [204, 205, 190], [128, 126, 120], [84, 82, 78]
DOOR_FRAME, WINDOW_FRAME, GLASS, BARS = [92, 62, 50], [78, 52, 42], [58, 70, 78], [206, 204, 196]
BOARDS, GUTTER, BOX, CHIMNEY = [112, 96, 80], [120, 124, 128], [150, 146, 136], [98, 104, 100]
ZINC = ([150, 158, 166], [144, 152, 160], [154, 161, 168], [140, 149, 158])
RUST = ([150, 84, 48], [172, 104, 58], [126, 70, 44], [186, 128, 84])
PALE_RUST = ([184, 164, 146], [170, 150, 132], [196, 176, 150])  # a sheet that lost its coat: rust showing through all over
SEAM = [112, 120, 128]


def _unit(p, q):
    n = math.dist(p, q)
    return ((q[0] - p[0]) / n, (q[1] - p[1]) / n)


A, V = _unit(DF, SH1), _unit(DF, DB)
_DET = A[0] * V[1] - A[1] * V[0]
WD = math.dist(DF, SH1)  # the bay's front
WS = math.dist(SH1, SF)  # the side part's front


def at(a: float, v: float) -> tuple[float, float]:
    return (DF[0] + A[0] * a + V[0] * v, DF[1] + A[1] * a + V[1] * v)


def av(p) -> tuple[float, float]:
    """(a, v) of a map point."""
    dx, dy = p[0] - DF[0], p[1] - DF[1]
    return ((dx * V[1] - dy * V[0]) / _DET, (A[0] * dy - A[1] * dx) / _DET)


def _roof_z(bid: str, a: float) -> float:
    """Underside of the roof of ``bid`` at ``a``."""
    if bid == DEEP:
        return DEEP_LOW + (DEEP_HIGH - DEEP_LOW) * a / WD
    return SIDE_HIGH - (SIDE_HIGH - SIDE_LOW) * (a - WD) / WS


def _line(pts):
    """``a`` as a function of ``v`` along a boundary given by map points, held flat beyond its ends."""
    k = sorted((av(p)[1], av(p)[0]) for p in pts)

    def f(v: float) -> float:
        if v <= k[0][0]:
            return k[0][1]
        for (v0, a0), (v1, a1) in itertools.pairwise(k):
            if v <= v1:
                return a0 + (a1 - a0) * (v - v0) / (v1 - v0)
        return k[-1][1]

    return f


_SHARED = _line((SH1, SH0, DK))  # the bay's boundary towards the side part
_SIDE_OUT = _line((SF, SB))
# each roof: depth, the low-``a`` edge, the high-``a`` edge. The bay's sheet laps 6 cm over the shared wall.
ROOFS = {
    DEEP: (max(av(DB)[1], av(DK)[1]), lambda v: -EAVE_OH, lambda v: _SHARED(v) + 0.06),
    SIDE: (max(av(SB)[1], av(SH0)[1]), _SHARED, lambda v: _SIDE_OUT(v) + EAVE_OH),
}


def _check(b: dict) -> None:
    want, got = RINGS[b["id"]], b["footprint"]["exterior"]
    if len(got) != len(want) or any(math.dist(p, q) > 0.05 for p, q in zip(got, want)):
        raise ValueError(f"garage: the footprint of {b['id']} changed; the garage is laid out on the old one")


def _prism(S: _Solids, corners, z0, z1, side, top=None, bottom=None) -> None:
    """A prism on a convex polygon (map metres); ``z0`` and ``z1`` give the bottom and the top at each corner."""
    lo = [(x, y, z) for (x, y), z in zip(corners, z0)]
    hi = [(x, y, z) for (x, y), z in zip(corners, z1)]
    faces = [(lo, bottom or side), (hi, top or side)]
    n = len(corners)
    faces += [([lo[k], lo[(k + 1) % n], hi[(k + 1) % n], hi[k]], side) for k in range(n)]
    S.solid(faces)


def _edge(S: _Solids, p, q, s0, s1, d0, d1, z0, z1, colour) -> None:
    """A piece along edge p->q of a counter-clockwise ring: ``s`` along it, ``d`` outward. ``z0`` and ``z1`` are numbers,
    or (at s0, at s1) pairs for a sloped bottom or top."""
    ux, uy = _unit(p, q)
    nx, ny = uy, -ux
    b0, b1 = z0 if isinstance(z0, tuple) else (z0, z0)
    t0, t1 = z1 if isinstance(z1, tuple) else (z1, z1)
    c = [(p[0] + ux * s + nx * d, p[1] + uy * s + ny * d) for s, d in ((s0, d0), (s1, d0), (s1, d1), (s0, d1))]
    _prism(S, c, (b0, b1, b1, b0), (t0, t1, t1, t0), colour)


def _wall(S: _Solids, bid: str, p, q, holes=()) -> None:
    """The wall on edge p->q from under the floor up into the roof, ``T`` thick inward; ``holes`` are (s0, s1, z0, z1).
    A doorway is a hole from the base (z0 = 0): the floor slab fills its foot."""
    length = math.dist(p, q)
    zp, zq = _roof_z(bid, av(p)[0]) + ROOF_T / 2, _roof_z(bid, av(q)[0]) + ROOF_T / 2

    def top(s):
        return zp + (zq - zp) * s / length

    s = 0.0
    for h0, h1, z0, z1 in sorted(holes):
        if h0 > s:
            _edge(S, p, q, s, h0, -T, 0.0, -0.3, (top(s), top(h0)), WALL)
        if z0 > 0:
            _edge(S, p, q, h0, h1, -T, 0.0, -0.3, z0, WALL)
        _edge(S, p, q, h0, h1, -T, 0.0, z1, (top(h0), top(h1)), WALL)
        s = h1
    if length > s:
        _edge(S, p, q, s, length, -T, 0.0, -0.3, (top(s), top(length)), WALL)


def _roof(S: _Solids, bid: str, detail: bool) -> None:
    """The lean-to roof of one part. Visual: one sheet between every two standing seams, each its own zinc or pale rust,
    some with a rust streak (the pattern is fixed per building); collision: one slab."""
    depth, lo, hi = ROOFS[bid]
    v_lo, v_hi = -END_OH, depth + END_OH

    def piece(a0, a1, v0, v1, z0, z1, top, side=None, bottom=None, clip=True):
        ends = [(max(lo(v), min(hi(v), a)) if clip else a, v) for a, v in ((a0, v0), (a1, v0), (a1, v1), (a0, v1))]
        zs = [_roof_z(bid, a) for a, _ in ends]
        _prism(S, [at(a, v) for a, v in ends], [z + z0 for z in zs], [z + z1 for z in zs], side or top, top, bottom)

    if not detail:
        piece(-9.0, 99.0, v_lo, v_hi, 0.0, 0.3, WALL)
        return
    rng = random.Random(bid)
    worn = False
    n = round((v_hi - v_lo) / PANEL)
    step = (v_hi - v_lo) / n
    for k in range(n):
        v0, v1 = v_lo + k * step, v_lo + (k + 1) * step
        a0, a1 = min(lo(v0), lo(v1)), max(hi(v0), hi(v1))
        worn = rng.random() < (0.55 if worn else 0.14)  # a sheet gone pale and rusty all over; they come in runs
        base = [c + rng.randint(-4, 4) for c in rng.choice(PALE_RUST if worn else ZINC)]
        # every sheet is one piece from eave to top; some carry a streak of open rust down their whole length, beside a seam
        streak = rng.uniform(0.25, 0.5) * step if rng.random() < (0.45 if worn else 0.12) else 0.0
        piece(a0, a1, v0 + streak, v1, 0.0, ROOF_T, base, SEAM, BOARDS)
        if streak:
            piece(a0, a1, v0, v0 + streak, 0.0, ROOF_T, rng.choice(RUST), SEAM, BOARDS)
        if k:
            piece(a0, a1, v0 - 0.02, v0 + 0.02, ROOF_T, ROOF_T + 0.04, SEAM)
    # the gutter under the low eave
    g = -EAVE_OH if bid == DEEP else None
    for v0, v1 in ((v_lo, v_hi),):
        if g is not None:
            piece(g - 0.1, g + 0.02, v0, v1, -0.1, 0.0, GUTTER, clip=False)
        else:
            ends = [(hi(v) + da, v) for v in (v0, v1) for da in (-0.02, 0.1)]
            ends = [ends[0], ends[1], ends[3], ends[2]]
            zs = [_roof_z(bid, a) for a, _ in ends]
            _prism(S, [at(a, v) for a, v in ends], [z - 0.1 for z in zs], zs, GUTTER)


def _door_span() -> tuple[float, float]:
    """The doorway along the bay's front edge (SH1 -> DF), centred."""
    return (WD - DOOR_W) / 2, (WD + DOOR_W) / 2


def _bay(S: _Solids, detail: bool) -> None:
    d0, d1 = _door_span()
    ring = RINGS[DEEP]
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        _wall(S, DEEP, p, q, [(d0, d1, 0.0, F + DOOR_H)] if (p, q) == (SH1, DF) else ())
    _prism(S, list(ring), [-0.3] * 5, [F] * 5, FLOOR)
    # the apron: a slope from the floor down into the ground in front of the door
    ux, uy = _unit(SH1, DF)
    nx, ny = uy, -ux
    a = [(SH1[0] + ux * (d0 - 0.2), SH1[1] + uy * (d0 - 0.2)), (SH1[0] + ux * (d1 + 0.2), SH1[1] + uy * (d1 + 0.2))]
    wedge = [[(x + nx * d, y + ny * d, z) for d, z in ((0.0, F), (0.0, -0.3), (RAMP, -0.3))] for x, y in a]
    S.solid([(wedge[0], FLOOR), (wedge[1], FLOOR)] + [([wedge[0][k], wedge[0][(k + 1) % 3], wedge[1][(k + 1) % 3], wedge[1][k]], FLOOR) for k in range(3)])
    if not detail:
        return
    # steel frame round the doorway, on the face of the wall
    top = F + DOOR_H
    _edge(S, SH1, DF, d0 - 0.09, d0, 0.0, 0.03, 0.0, top + 0.09, DOOR_FRAME)
    _edge(S, SH1, DF, d1, d1 + 0.09, 0.0, 0.03, 0.0, top + 0.09, DOOR_FRAME)
    _edge(S, SH1, DF, d0, d1, 0.0, 0.03, top, top + 0.09, DOOR_FRAME)
    # the junction box and its conduit, left of the door as in the photo
    _edge(S, SH1, DF, 0.38, 0.72, 0.0, 0.12, 3.75, 3.9, BOX)
    _edge(S, SH1, DF, 0.53, 0.57, 0.0, 0.04, 3.2, 3.75, DOOR_FRAME)
    # weathered foot of the outer walls
    for p, q in ((SH1, DF), (DF, DB), (DB, DK), (DK, SH0)):
        spans = [(0.0, d0 - 0.09), (d1 + 0.09, math.dist(p, q))] if (p, q) == (SH1, DF) else [(0.0, math.dist(p, q))]
        for s0, s1 in spans:
            _edge(S, p, q, s0, s1, 0.0, 0.012, -0.3, 0.32, PLINTH)
    # an old oil stain where the engine stands
    pa, pv = WD / 2, T + PARK_CLEAR + 1.6
    _prism(S, [at(pa - 0.7, pv - 1.1), at(pa + 0.7, pv - 1.1), at(pa + 0.7, pv + 1.1), at(pa - 0.7, pv + 1.1)], [F] * 4, [F + 0.004] * 4, STAIN)
    # the aerial mast on the shared wall's top, at the front
    m = at(WD - T / 2, 0.25)
    _prism(S, [(m[0] + dx, m[1] + dy) for dx, dy in ((-0.02, -0.02), (0.02, -0.02), (0.02, 0.02), (-0.02, 0.02))], [DEEP_HIGH] * 4, [DEEP_HIGH + 2.2] * 4, GUTTER)


def _side(S: _Solids, detail: bool) -> None:
    ring = RINGS[SIDE]
    _prism(S, list(ring), [-0.3] * 4, [_roof_z(SIDE, av(p)[0]) + ROOF_T / 2 for p in ring], WALL)
    if not detail:
        return
    # the barred window of the front: frame, glass and a grid, all on the face of the wall
    length = math.dist(SF, SH1)
    s0, s1, z0, z1 = (length - 2.4) / 2, (length + 2.4) / 2, 1.0, 2.25
    _edge(S, SF, SH1, s0 - 0.07, s1 + 0.07, 0.0, 0.035, z0 - 0.07, z1 + 0.07, WINDOW_FRAME)
    _edge(S, SF, SH1, s0, s1, 0.0, 0.045, z0, z1, GLASS)
    _edge(S, SF, SH1, s0 - 0.1, s1 + 0.1, 0.0, 0.08, z0 - 0.12, z0 - 0.07, WINDOW_FRAME)  # sill
    for k in (1, 2):  # the three casements
        s = s0 + (s1 - s0) * k / 3
        _edge(S, SF, SH1, s - 0.03, s + 0.03, 0.0, 0.055, z0, z1, WINDOW_FRAME)
    for k in range(1, 12):
        s = s0 + (s1 - s0) * k / 12
        _edge(S, SF, SH1, s - 0.008, s + 0.008, 0.045, 0.07, z0, z1, BARS)
    for k in range(1, 5):
        z = z0 + (z1 - z0) * k / 5
        _edge(S, SF, SH1, s0, s1, 0.045, 0.065, z - 0.008, z + 0.008, BARS)
    for p, q in ((SH0, SB), (SB, SF), (SF, SH1)):
        _edge(S, p, q, 0.0, math.dist(p, q), 0.0, 0.012, -0.3, 0.32, PLINTH)
    # the downpipe at the low corner of the front, and a stove pipe through the roof
    _edge(S, SF, SH1, 0.08, 0.16, 0.0, 0.08, 0.0, SIDE_LOW, GUTTER)
    ca, cv = WD + WS * 0.45, 6.2
    cz = _roof_z(SIDE, ca)
    _prism(S, [at(ca - 0.11, cv - 0.11), at(ca + 0.11, cv - 0.11), at(ca + 0.11, cv + 0.11), at(ca - 0.11, cv + 0.11)], [cz] * 4, [cz + 0.9] * 4, CHIMNEY)
    _prism(S, [at(ca - 0.16, cv - 0.16), at(ca + 0.16, cv - 0.16), at(ca + 0.16, cv + 0.16), at(ca - 0.16, cv + 0.16)], [cz + 0.9] * 4, [cz + 0.95] * 4, RUST[2])


def garage_mesh(b: dict, detail: bool = True) -> Mesh:
    """The visual mesh of one of the two parts, or its collision (``detail`` False: the same walls and floor, the roof as
    one slab, no trim), standing on the building base."""
    _check(b)
    S = _Solids()
    (_bay if b["id"] == DEEP else _side)(S, detail)
    _roof(S, b["id"], detail)
    return Mesh(np.array(S.tris, float).reshape(-1, 3, 3), np.array(S.cols, np.uint8).reshape(-1, 3))


def _bearing(e: float, n: float) -> float:
    return round(math.degrees(math.atan2(e, n)) % 360, 2)


def garage_info(elevation: dict[str, float]) -> dict:
    """``garage.json``: the doorway the game hangs the doors in, the bay, and where the bus stands.

    Points are ``[x east, y north, z up]`` in local metres (as ``parcels.json``; z as ``terrain.json``), yaws are compass
    bearings in degrees (0 north, 90 east). ``elevation`` is the base height of each building.
    """
    z = elevation.get(DEEP, 0.0)
    out = (-V[0], -V[1])  # out of the door

    def pt(xy, h):
        return [round(xy[0], 3), round(xy[1], 3), round(h, 3)]

    back = min(av(DB)[1], av(DK)[1]) - T
    inner = [at(T, T), at(_SHARED(T) - T, T), at(_SHARED(av(SH0)[1]) - T, av(SH0)[1]), at(_SHARED(back) - T, back), at(T, back)]
    return {
        "doorway": {"pos": pt(at(WD / 2, T / 2), z + F), "yaw": _bearing(*out), "width": DOOR_W, "height": DOOR_H},
        "bay": {
            "floor_z": round(z + F, 3), "ceiling_z": round(z + DEEP_LOW, 3),
            "footprint": [[round(x, 3), round(y, 3)] for x, y in inner],
        },
        # the vehicle that stands here at the start of a round, nose to the door
        "parking": {"vehicle": VEHICLE, "pos": pt(at(WD / 2, T + PARK_CLEAR + VEHICLE_HALF_LENGTH), z + F), "yaw": _bearing(*out)},
    }  # fmt: skip
