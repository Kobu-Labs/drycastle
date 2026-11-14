"""The inside of the church (Holy Mass event, ``docs/events/README.md``): a nave with two side aisles, a chancel, a gallery.

Same local frame as ``landmarks.church_solids`` (u across, v along, front at +v, z up from the building base).
``build_shell`` replaces the solid nave, aisle and apse blocks by walls with a doorway; the nave opens to the aisles in
arcades. The collision mesh is the real shell (walls, floors, gallery, stair ramp), decoration is in the visual mesh only.
Windows are real openings (``WINDOWS``) with a dark frame and a breakable glass pane each (``church_panes``).
``church_info`` is what the game needs to know, for ``church.json``.
"""

import itertools
import math
from typing import NamedTuple

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.landmarks import IRON, _arch, _rect

T, F = 0.4, 0.25  # wall thickness, floor height (the porch slab's top)
CU = 0.985  # centre line: door, aisle, altar
NX0, NX1, NV0, NV1, ZE = -2.88, 4.86, -7.3, 14.0, 7.4  # nave, outside
IU0, IU1, IV0, IV1 = NX0 + T, NX1 - T, NV0 + T, NV1 - T  # nave, inside
AX0, AX1, AV0, ZA = -1.28, 4.96, -14.25, 5.2  # apse block, outside
CU0, CU1, CV0 = AX0 + T, 2 * CU - (AX0 + T), AV0 + T  # chancel, inside (symmetric about CU)
DOOR_W, DOOR_TOP = 2.0, 2.9
Z_SPRING, VAULT_RISE = 6.4, 0.55  # a shallow vault over the clerestory windows, under the eave
# real openings with a glass pane: the clerestory (above the aisle roofs) in each bay, two in the chancel's rear wall
WIN_V, WIN_W, WIN_Z0, WIN_Z1 = (-4.85, -0.75, 3.35, 7.45, 11.55), 0.9, 5.35, 6.3
CWIN_DU, CWIN_W, CWIN_Z0, CWIN_Z1 = 1.58, 0.45, 2.2, 4.0
FRAME_W, FRAME_D = 0.05, 0.12  # window frame: width in the wall plane, depth across the wall (centred in it; the glass is inlaid)
ARCADE_Z, ARCADE_RISE = 3.6, 1.1  # the nave's side walls below this are pillars and arches; above it they are solid
BAND_Z = 5.0  # where the aisle ceiling meets the nave wall: above it the nave wall has an outside
ROOF_T, ROOF_OH = 0.2, 0.35  # aisle roof slab thickness and its overhang past the outer wall
GV0, GZ = 9.6, 3.6  # gallery: front edge, floor
STAIR_W, STAIR_V0, STEPS = 0.9, 3.6, 17  # along the left wall, up towards the gallery
BAYS = (-2.8, 1.3, 5.4, 9.5)  # pilasters and ribs
PEW_W, PEW_PITCH, PEW_V0, AISLE_W = 2.0, 1.25, -4.5, 1.8  # the Synty pew is 2.0 x 0.76 m
ROWS_RIGHT, ROWS_LEFT = 11, 6  # the stairs take the rear of the left block
GALLERY_PEWS = ((0.2, 10.9), (2.6, 10.9))  # centre u, v
SEAT_H, SEATS_PER_PEW, SEAT_GAP = 0.51, 3, 0.6  # the Synty pew's seat is 0.51 m up
CREAM, MARBLE, RIB, WOOD, CARPET = "#efe6d2", "#d9a99a", "#faf6ee", "#4a2e1c", "#8e1b1f"
FLOOR, GOLD, PIPE, CANVAS = "#d8cdb4", "#c9a03c", "#b9bcc4", "#3b3027"


class Aisle(NamedTuple):
    """A side aisle: a hollow block against the nave with a lean-to roof rising towards it. Outer corners ``u0..u1``, ``v0..v1``."""

    name: str
    u0: float
    u1: float
    v0: float
    v1: float
    left: bool  # the outer wall is at u0 (the nave at u1), else the outer wall is at u1 (the nave at u0)

    @property
    def nave_u(self) -> float:  # outer face of the nave's side wall
        return self.u1 if self.left else self.u0

    @property
    def in_u(self) -> float:  # inner face of the outer wall
        return self.u0 + T if self.left else self.u1 - T

    @property
    def iv(self) -> tuple[float, float]:  # inside, along v
        return self.v0 + T, self.v1 - T

    @property
    def shed(self) -> tuple[float, float, float, float]:
        """The roof slab as ``_Solids.shed`` takes it: x0, x1 and the height of its top at each."""
        return (self.u0 - ROOF_OH, self.u1, 4.2, 5.2) if self.left else (self.u0, self.u1 + ROOF_OH, 5.2, 4.2)

    def ceiling(self, u: float) -> float:
        """Height of the underside of the roof slab at ``u``."""
        xa, xb, za, zb = self.shed
        return za + (zb - za) * (u - xa) / (xb - xa) - ROOF_T

    def wall_top(self, *us: float) -> float:
        """Top of a wall under the roof: a little into the slab."""
        return max(self.ceiling(u) for u in us) + 0.04


AISLES = (Aisle("left", -5.87, NX0, -5.13, 10.76, True), Aisle("right", NX1, 8.38, -7.31, 11.39, False))
# arcade openings (lo, hi along v) of the nave's side walls into each aisle; the left stair keeps the wall solid from 3.4 on
ARCADES = {"left": ((-2.4, 0.9), (1.7, 3.4)), "right": ((-6.5, -3.2), (-2.4, 0.9), (1.7, 5.0), (5.8, 9.1))}
AISLE_WIN_V = tuple(-3.0 + 3.0 * k for k in range(5))  # the five windows of each aisle's outer wall
AISLE_ALTAR_U = {"left": -4.175, "right": 6.42}  # a side altar at the rear wall of each aisle, and its kneeler before it


class Painting(NamedTuple):
    """A framed picture hung on a free wall: centre (``u``, ``v``, ``z`` on the wall face), canvas ``w`` x ``h`` metres, and the
    way it faces into the room as (du, dv). Frame and canvas are the game's (``ChurchPaintings``): they change with the demon."""

    u: float
    v: float
    z: float
    w: float
    h: float
    face: tuple[int, int]


PORTRAIT, LANDSCAPE, SQUARE = (0.9, 1.4), (1.4, 0.8), (1.0, 1.0)
_L, _R = AISLES[0].in_u, AISLES[1].in_u  # inner faces of the aisles' outer walls (the gaps between the windows are at v -1.5, 1.5, ...)
PAINTINGS = (
    # aisles' outer walls, between the windows, at eye height
    Painting(_L, -1.5, 1.75, *PORTRAIT, (1, 0)), Painting(_L, 1.5, 1.75, *LANDSCAPE, (1, 0)),
    Painting(_L, 4.5, 1.75, *SQUARE, (1, 0)), Painting(_L, 7.5, 1.75, *PORTRAIT, (1, 0)),
    Painting(_R, -1.5, 1.75, *LANDSCAPE, (-1, 0)), Painting(_R, 1.5, 1.75, *PORTRAIT, (-1, 0)),
    Painting(_R, 4.5, 1.75, *PORTRAIT, (-1, 0)), Painting(_R, 7.5, 1.75, *SQUARE, (-1, 0)),
    # the side altars: on the backdrop's front face at the aisles' rear walls
    Painting(AISLE_ALTAR_U["left"], AISLES[0].iv[0] + 0.4, 2.2, *PORTRAIT, (0, 1)),
    Painting(AISLE_ALTAR_U["right"], AISLES[1].iv[0] + 0.4, 2.2, *PORTRAIT, (0, 1)),
    # the solid nave wall between the arcade and the first pilaster by the stair (the stair's steps stay under it), and the right wall's front bay
    Painting(IU0, 4.2, 2.3, *PORTRAIT, (1, 0)),
    Painting(IU1, 11.4, 1.8, *LANDSCAPE, (-1, 0)),
)

# standing spots (u, v, the way in from the aisle as (u, v) points): the free floor by the stairs, and a few in the side aisles
# (a kneeler before each aisle altar first). The aisles are entered across the nave where no pews stand: the left at v 2.8
# (between the last pew and the stair), the right at v -5.7 (between the first pew and the rear wall).
_LEFT_IN, _RIGHT_IN = ((CU, 2.8), (-4.175, 2.8)), ((CU, -5.7), (6.42, -5.7))
STANDING = (
    (-0.9, 4.6, ((CU, 3.1), (-0.9, 3.1))), (-0.3, 5.8, ((CU, 3.1), (-0.3, 3.1))),
    (-0.9, 7.0, ((CU, 3.1), (-0.9, 3.1))), (-0.3, 8.2, ((CU, 3.1), (-0.3, 3.1))),
    (-4.175, -3.5, _LEFT_IN), (-4.175, -1.8, _LEFT_IN), (-4.175, -0.2, _LEFT_IN),
    (6.42, -5.5, _RIGHT_IN), (6.42, -3.6, _RIGHT_IN), (6.42, -1.7, _RIGHT_IN), (6.42, 0.2, _RIGHT_IN),
)  # fmt: skip


def _octagon(r: float, zc: float) -> tuple[tuple[float, float], ...]:
    return tuple(
        (r * math.cos(math.pi / 4 * k + math.pi / 8), zc + r * math.sin(math.pi / 4 * k + math.pi / 8)) for k in range(8)
    )


def _arcade_hole(lo: float, hi: float) -> tuple[float, tuple[tuple[float, float], ...]]:
    """An arched opening from the floor, (centre, convex outline relative to it)."""
    w = hi - lo
    rise = min(ARCADE_RISE, w / 2)
    zs = ARCADE_Z - rise
    arc = tuple((w / 2 * math.cos(math.pi * k / 6), zs + rise * math.sin(math.pi * k / 6)) for k in range(7))
    return (lo + hi) / 2, ((-w / 2, 0.0), (w / 2, 0.0), *arc)


class Win(NamedTuple):
    """A window: a convex ``outline`` of (along the wall from ``c``, z) in the wall called ``wall``; the glass is in the plane ``t``."""

    wall: str
    axis: str  # "v": the wall runs along v (its normal is along u), "u": it runs along u
    c: float
    t: float  # the plane across the wall, in the middle of its thickness
    n: int  # outward normal along that axis
    outline: tuple[tuple[float, float], ...]


def _windows() -> tuple[Win, ...]:
    out = []
    clere, chancel = tuple(_rect(WIN_W, WIN_Z0, WIN_Z1)), tuple(_rect(CWIN_W, CWIN_Z0, CWIN_Z1))
    for v in WIN_V:
        out.append(Win("nave_left", "v", v, (NX0 + IU0) / 2, -1, clere))
        out.append(Win("nave_right", "v", v, (IU1 + NX1) / 2, 1, clere))
    for d in (-CWIN_DU, CWIN_DU):
        out.append(Win("chancel", "u", CU + d, (AV0 + CV0) / 2, -1, chancel))
    left, right = AISLES
    for v in AISLE_WIN_V:  # arched on the left, round (an octagon) on the right
        out.append(Win("aisle_left", "v", v, left.u0 + T / 2, -1, tuple(_arch(0.7, 1.4, 2.8, 0.5))))
        out.append(Win("aisle_right", "v", v, right.u1 - T / 2, 1, _octagon(0.38, 2.4)))
    out.append(Win("front", "u", CU, (IV1 + NV1) / 2, 1, tuple(_arch(1.1, 4.5, 6.0, 0.55))))  # lights the gallery
    return tuple(out)


WINDOWS = _windows()


def pews() -> list[tuple[float, float, int]]:
    """Every pew as (centre u, centre v, floor); sitters face the altar (-v)."""
    off = AISLE_W / 2 + PEW_W / 2
    out = [(CU + off, PEW_V0 + PEW_PITCH * k, 0) for k in range(ROWS_RIGHT)]
    out += [(CU - off, PEW_V0 + PEW_PITCH * k, 0) for k in range(ROWS_LEFT)]
    return out + [(u, v, 1) for u, v in GALLERY_PEWS]


def _extrude(S, axis: str, poly, t0: float, t1: float, colour) -> None:
    """Convex (along, z) polygon extruded across a wall from ``t0`` to ``t1``; ``axis`` is the wall's direction ("v": along v)."""

    def at(p, t):
        a, z = p
        return (t, a, z) if axis == "v" else (a, t, z)

    a = [at(p, t0) for p in poly]
    b = [at(p, t1) for p in poly]
    faces = [(a, colour), (b, colour)]
    for k in range(len(poly)):
        j = (k + 1) % len(poly)
        faces.append(([a[k], a[j], b[j], b[k]], colour))
    S.solid(faces)


def _prism(S, profile, v0, v1, colour) -> None:
    """Convex (u, z) polygon extruded along v."""
    _extrude(S, "u", profile, v0, v1, colour)


def _span(poly, x: float) -> tuple[float, float]:
    """Lowest and highest z of a convex (along, z) polygon on the vertical line ``x``."""
    zs = []
    for (xa, za), (xb, zb) in itertools.pairwise([*poly, poly[0]]):
        if xa == xb:
            if abs(xa - x) < 1e-9:
                zs += [za, zb]
        elif min(xa, xb) - 1e-9 <= x <= max(xa, xb) + 1e-9:
            zs.append(za + (zb - za) * (x - xa) / (xb - xa))
    return min(zs), max(zs)


def _inset(poly, d: float):
    """The polygon moved ``d`` in from every edge (mitred corners)."""
    n = len(poly)
    area = sum(poly[k][0] * poly[(k + 1) % n][1] - poly[(k + 1) % n][0] * poly[k][1] for k in range(n))
    sgn = 1 if area > 0 else -1
    normals = []
    for k in range(n):
        (xa, za), (xb, zb) = poly[k], poly[(k + 1) % n]
        ln = math.hypot(xb - xa, zb - za)
        normals.append((-sgn * (zb - za) / ln, sgn * (xb - xa) / ln))  # inward
    out = []
    for k in range(n):
        (px, pz), (qx, qz) = normals[k - 1], normals[k]
        s = d / (1 + px * qx + pz * qz)
        out.append((poly[k][0] + (px + qx) * s, poly[k][1] + (pz + qz) * s))
    return out


def _arc(v0, v1, z_off, thick, S, colour) -> None:
    """The vault's half-ellipse as eight slabs, ``z_off`` above the vault line."""
    c, hw = (IU0 + IU1) / 2, (IU1 - IU0) / 2
    pts = [
        (c + hw * math.cos(math.pi * k / 8), Z_SPRING + VAULT_RISE * math.sin(math.pi * k / 8))
        for k in range(9)
    ]
    for (ua, za), (ub, zb) in itertools.pairwise(pts):
        _prism(
            S,
            [(ua, za + z_off), (ub, zb + z_off), (ub, zb + z_off + thick), (ua, za + z_off + thick)],
            v0,
            v1,
            colour,
        )


def build_shell(S, detail: bool, yellow, plinth) -> None:
    cream, marble, rib, wood, carpet = (hex_to_rgb(c) for c in (CREAM, MARBLE, RIB, WOOD, CARPET))
    floor, gold, pipe, canvas = (hex_to_rgb(c) for c in (FLOOR, GOLD, PIPE, CANVAS))
    iron = hex_to_rgb(IRON)

    def wall(axis, a0, a1, t0, t1, z0, z1, room, wins=(), outer=None, base=True) -> None:
        """A wall running along ``axis`` from ``a0`` to ``a1``, thick from ``t0`` to ``t1``, ``outer`` outside (yellow) and cream
        inside; ``room`` is the side the room is on (+1 or -1 along the thickness). ``wins``: the ``WINDOWS`` cut into it (framed)."""
        outer = yellow if outer is None else outer
        tm = (t0 + t1) / 2
        halves = ((t0, tm, outer), (tm, t1, cream)) if room > 0 else ((t0, tm, cream), (tm, t1, outer))

        def piece(poly) -> None:
            for ta, tb, colour in halves:
                _extrude(S, axis, poly, ta, tb, colour)

        cur = a0
        for w in sorted(wins, key=lambda w: w[0]):
            c, outline = w[0], w[1]
            poly = [(c + da, z) for da, z in outline]
            xs = []
            for x in sorted(p[0] for p in poly):
                if not xs or x > xs[-1] + 1e-6:
                    xs.append(x)
            if xs[0] > cur + 1e-9:
                piece([(cur, z0), (xs[0], z0), (xs[0], z1), (cur, z1)])
            spans = [_span(poly, x) for x in xs]
            for (xa, (la, ha)), (xb, (lb, hb)) in itertools.pairwise(zip(xs, spans, strict=True)):
                if max(la, lb) > z0 + 1e-9:
                    piece([(xa, z0), (xb, z0), (xb, lb), (xa, la)])
                if min(ha, hb) < z1 - 1e-9:
                    piece([(xa, ha), (xb, hb), (xb, z1), (xa, z1)])
            cur = xs[-1]
            if detail and w[2]:  # a dark frame round the opening, centred in the wall; the glass pane is in its middle
                inner = _inset(poly, FRAME_W)
                for k in range(len(poly)):
                    j = (k + 1) % len(poly)
                    _extrude(S, axis, [poly[k], poly[j], inner[j], inner[k]], tm - FRAME_D / 2, tm + FRAME_D / 2, iron)
        if a1 > cur + 1e-9:
            piece([(cur, z0), (a1, z0), (a1, z1), (cur, z1)])
        if z0 == 0.0 and base:
            tb0, tb1 = (t0 - 0.03, tm) if room > 0 else (tm, t1 + 0.03)
            if axis == "v":
                S.box(tb0, tb1, a0, a1, 0.0, 0.6, plinth)
            else:
                S.box(a0, a1, tb0, tb1, 0.0, 0.6, plinth)

    def windows_of(name):
        return [(w.c, w.outline, True) for w in WINDOWS if w.wall == name]

    def arcade_of(name):
        return [(*_arcade_hole(lo, hi), False) for lo, hi in ARCADES[name]]

    # nave: side walls (below BAND_Z hollowed by the arcade where an aisle stands, and the aisle's own face cream),
    # front wall round the doorway and the facade window, rear wall round the chancel arch
    for a in AISLES:
        t0, t1, room = (NX0, IU0, 1) if a.left else (IU1, NX1, -1)
        lo, hi = a.iv
        wall("v", NV0, lo, t0, t1, 0.0, BAND_Z, room)
        wall("v", lo, hi, t0, t1, 0.0, BAND_Z, room, arcade_of(a.name), outer=cream, base=False)
        wall("v", hi, NV1, t0, t1, 0.0, BAND_Z, room)
        wall("v", NV0, NV1, t0, t1, BAND_Z, ZE, room, windows_of("nave_left" if a.left else "nave_right"), base=False)
    wall("u", IU0, CU - DOOR_W / 2, IV1, NV1, 0.0, ZE, -1)
    wall("u", CU + DOOR_W / 2, IU1, IV1, NV1, 0.0, ZE, -1)
    wall("u", CU - DOOR_W / 2, CU + DOOR_W / 2, IV1, NV1, DOOR_TOP, ZE, -1, windows_of("front"), base=False)
    wall("u", IU0, CU0, NV0, IV0, 0.0, ZE, 1)
    wall("u", CU1, IU1, NV0, IV0, 0.0, ZE, 1)
    wall("u", CU0, CU1, NV0, IV0, ZA - 0.4, ZE, 1)
    # chancel in the apse block
    wall("v", AV0, NV0, AX0, CU0, 0.0, ZA, 1)
    wall("v", AV0, NV0, CU1, AX1, 0.0, ZA, -1)
    wall("u", CU0, CU1, AV0, CV0, 0.0, ZA, 1, windows_of("chancel"))
    S.box(CU0, CU1, CV0, NV0, ZA - 0.2, ZA, cream)
    # side aisles: hollow, outer wall with its windows, end walls up to the sloping roof, floor, ceiling
    for a in AISLES:
        lo, hi = a.iv
        t0, t1, room = (a.u0, a.u0 + T, 1) if a.left else (a.u1 - T, a.u1, -1)
        wall("v", a.v0, a.v1, t0, t1, 0.0, a.wall_top(t0, t1), room, windows_of("aisle_" + a.name))
        ua, ub = (a.u0 + T, a.u1) if a.left else (a.u0, a.u1 - T)
        top = [(ua, 0.0), (ub, 0.0), (ub, a.wall_top(ub)), (ua, a.wall_top(ua))]
        for v0, v1, rear in ((a.v0, lo, 1), (hi, a.v1, -1)):  # rear (smaller v) and front end wall: yellow outside, cream inside
            vm = (v0 + v1) / 2
            _prism(S, top, v0, vm, yellow if rear > 0 else cream)
            _prism(S, top, vm, v1, cream if rear > 0 else yellow)
        S.box(ua, ub, a.v1 - T / 2, a.v1 + 0.03, 0.0, 0.6, plinth)
        if a.name == "right":  # the left one stands against the sacristy
            S.box(ua, ub, a.v0 - 0.03, a.v0 + T / 2, 0.0, 0.6, plinth)
        fu0, fu1 = (a.in_u, IU0) if a.left else (IU1, a.in_u)
        S.box(fu0, fu1, lo, hi, 0.0, F, floor)
        if detail:  # a cream lining under the sloping roof
            xa, xb = (a.in_u, a.nave_u) if a.left else (a.nave_u, a.in_u)
            S.shed(xa, xb, lo, hi, a.ceiling(xa) - 0.005, a.ceiling(xb) - 0.005, 0.06, cream)
        # arcade pillars on the aisle side, a marble square with the pilaster on the nave side
        for v in BAYS:
            if any(abs(v - lo_v) < 0.5 or abs(v - hi_v) < 0.5 for lo_v, hi_v in ARCADES[a.name]):
                pu0, pu1 = (a.u1 - 0.2, a.u1) if a.left else (a.u0, a.u0 + 0.2)
                S.box(pu0, pu1, v - 0.4, v + 0.4, 0.0, ARCADE_Z, marble)
    # floors
    S.box(IU0, IU1, NV0, NV1, 0.0, F, floor)
    S.box(CU0, CU1, CV0, NV0, 0.0, F, floor)
    # gallery on two columns, parapet (open at the stair head), organ case
    S.box(IU0, IU1, GV0, IV1, GZ - 0.2, GZ, cream)
    S.box(IU0 + STAIR_W, IU1, GV0, GV0 + 0.15, GZ, GZ + 0.8, rib)
    for dx in (-1.6, 1.6):
        S.box(CU + dx - 0.2, CU + dx + 0.2, GV0, GV0 + 0.4, F, GZ - 0.2, marble)
    S.box(CU - 1.9, CU + 1.9, 12.8, IV1, GZ, GZ + 1.3, wood)
    # stairs: steps to look at, a ramp to walk on
    rise, run = (GZ - F) / STEPS, (GV0 - STAIR_V0) / STEPS
    u0, u1 = IU0, IU0 + STAIR_W
    if detail:
        for k in range(STEPS):
            S.box(u0, u1, STAIR_V0 + k * run, STAIR_V0 + (k + 1) * run, F, F + (k + 1) * rise, floor)
    else:
        a, b, c = (STAIR_V0, F), (GV0, F), (GV0, GZ)
        lo = [(u0, v, z) for v, z in (a, b, c)]
        hi = [(u1, v, z) for v, z in (a, b, c)]
        S.solid(
            [(lo, floor), (hi, floor)]
            + [([lo[i], lo[j], hi[j], hi[i]], floor) for i, j in ((0, 1), (1, 2), (2, 0))]
        )
    # high altar backdrop (pews, altar table, lectern and stoup are props the game places, see church_info)
    S.box(CU - 1.3, CU + 1.3, CV0, CV0 + 0.4, F, 3.6, marble)
    # pilasters on the nave walls over the arcade pillars (and the bays beyond it): solid, so nobody walks through them
    for v in BAYS:
        for x0, x1 in ((IU0, IU0 + 0.2), (IU1 - 0.2, IU1)):
            S.box(x0, x1, v - 0.4, v + 0.4, F, Z_SPRING - 0.4, marble)
    # side altars at the rear wall of each aisle: table and a backdrop
    for a in AISLES:
        v = a.iv[0]
        cu = AISLE_ALTAR_U[a.name]
        S.box(cu - 1.15, cu + 1.15, v, v + 0.7, F, F + 1.0, rib)
        S.box(cu - 1.0, cu + 1.0, v, v + 0.4, F + 1.0, 3.2, marble)
    if not detail:
        return

    # vault, ribs over pilasters, ridge rib
    _arc(IV0, IV1, 0.0, 0.1, S, cream)
    c = (IU0 + IU1) / 2
    S.box(c - 0.1, c + 0.1, IV0, IV1, Z_SPRING + VAULT_RISE - 0.14, Z_SPRING + VAULT_RISE, rib)
    for v in BAYS:
        _arc(v - 0.15, v + 0.15, -0.14, 0.14, S, rib)
        for x0, x1, cap in ((IU0, IU0 + 0.2, IU0 + 0.28), (IU1 - 0.2, IU1, IU1 - 0.28)):
            S.box(min(x0, cap), max(x1, cap), v - 0.38, v + 0.38, Z_SPRING - 0.4, Z_SPRING, rib)
    # chancel: sacristy door, high altar (the paintings of the walls are props, see PAINTINGS)
    S.plate("right", CU0, -9.9, _rect(1.1, F, F + 2.1), 0.05, wood)
    S.plate("front", CV0 + 0.4, CU, _arch(1.4, F + 1.1, 3.0, 0.5), 0.05, gold)
    S.plate("front", CV0 + 0.4, CU, _arch(1.0, F + 1.3, 2.8, 0.4), 0.08, canvas)
    S.box(CU - 0.8, CU + 0.8, CV0, CV0 + 0.4, 3.6, 4.3, gold)
    # organ pipes and gilding, carpet
    for i in range(-9, 10):
        x = CU + 0.19 * i
        S.box(x - 0.07, x + 0.07, 12.9, 13.04, GZ + 1.3, 5.6 + 1.1 * abs(math.cos(i * math.pi / 6)), pipe)
    S.box(CU - 1.95, CU + 1.95, 12.76, 12.8, GZ + 1.15, GZ + 1.3, gold)
    S.box(CU - 0.6, CU + 0.6, -11.4, IV1, F, F + 0.015, carpet)


def church_info(b: dict, dz: float = 0.0) -> dict:
    """``church.json``: door, aisle, seats (``stand``: a standing spot, ``pos`` on the floor) with routes, spots, demon anchors and shelter volumes of church ``b``.

    Points are ``[x east, y north, z up]`` in local metres (as ``parcels.json``; z as ``terrain.json``), yaws are
    compass bearings in degrees (0 north, 90 east), normals are (east, north, up).
    """
    ex = b["footprint"]["exterior"]
    ox, oy = (sum(p[k] for p in ex) / len(ex) for k in (0, 1))
    a = float(b["model_axis"])
    vx, vy = math.cos(a), math.sin(a)  # v axis; the u axis is (vy, -vx)

    def pt(u, v, z):
        return [round(ox + u * vy + v * vx, 3), round(oy - u * vx + v * vy, 3), round(z + dz, 3)]

    def vec(u, v, z):
        return [round(u * vy + v * vx, 4), round(-u * vx + v * vy, 4), z]

    def bearing(du, dv):
        e, n = du * vy + dv * vx, -du * vx + dv * vy
        return round(math.degrees(math.atan2(e, n)) % 360, 2)

    into, out = bearing(0, -1), bearing(0, 1)
    aisle = [pt(CU, 15.2, F), pt(CU, 13.0, F)]
    stair_u = IU0 + STAIR_W / 2
    up = aisle + [
        pt(CU, 3.1, F), pt(stair_u, 3.1, F), pt(stair_u, STAIR_V0, F), pt(stair_u, GV0, GZ),
        pt(stair_u, 10.15, GZ),
    ]  # fmt: skip
    seats, props = [], []
    for u, v, fl in pews():
        z = GZ if fl else F
        props.append({"kind": "pew", "pos": pt(u, v, z), "yaw": into})
        for s in range(SEATS_PER_PEW):
            su = u + SEAT_GAP * (s - (SEATS_PER_PEW - 1) / 2)
            path = up + [pt(su, 10.15, GZ)] if fl else aisle + [pt(CU, v - 0.62, F), pt(su, v - 0.62, F)]
            seats.append({
                "id": len(seats), "pos": pt(su, v, z + SEAT_H), "yaw": into, "path": path, "floor": fl,
            })  # fmt: skip
    for u, v, way in STANDING:
        seats.append({
            "id": len(seats), "pos": pt(u, v, F), "yaw": into, "path": aisle + [pt(wu, wv, F) for wu, wv in way],
            "floor": 0, "stand": True,
        })  # fmt: skip
    props += [
        {"kind": "altar", "pos": pt(CU, -11.9, F), "yaw": out},
        {"kind": "lectern", "pos": pt(CU + 1.4, -8.3, F), "yaw": out},
        {"kind": "holy_water", "pos": pt(CU + 2.3, 12.9, F), "yaw": into},
    ]
    # paintings: the canvas centre on the wall face, facing into the room; w and h are the canvas in metres
    props += [
        {"kind": "painting", "pos": pt(p.u, p.v, p.z), "yaw": bearing(*p.face), "w": p.w, "h": p.h}
        for p in PAINTINGS
    ]

    # demon anchors: rings across the nave (upper wall, vault, upper wall), the pilaster rings with a second wall height,
    # and rings across each aisle (outer wall, ceiling, nave wall); linked round a ring and along each role, aisle to nave
    anchors: list[dict] = []
    roles: dict[str, list[tuple[float, int]]] = {}

    def add(role, v, u, z, n):
        norm = math.sqrt(sum(x * x for x in n))
        anchors.append({"id": len(anchors), "pos": pt(u, v, z), "normal": vec(*(x / norm for x in n)), "links": set()})
        roles.setdefault(role, []).append((v, len(anchors) - 1))
        return len(anchors) - 1

    def link(i, j):
        anchors[i]["links"].add(j)
        anchors[j]["links"].add(i)

    z_vault = Z_SPRING + VAULT_RISE - 0.1
    for v in sorted((*WIN_V[:4], *BAYS)):
        pil = v in BAYS  # a pilaster ring: the anchors sit on the pilaster faces, at two heights
        inset = 0.2 if pil else 0.0
        ring = []
        if pil:
            ring.append(add("L_hi", v, IU0 + inset, 5.5, (1, 0, 0)))
        ring += [add("L_lo", v, IU0 + inset, 4.4, (1, 0, 0)), add("V", v, (IU0 + IU1) / 2, z_vault, (0, 0, -1)),
                 add("R_lo", v, IU1 - inset, 4.4, (-1, 0, 0))]  # fmt: skip
        if pil:
            ring.append(add("R_hi", v, IU1 - inset, 5.5, (-1, 0, 0)))
        for i, j in itertools.pairwise(ring):
            link(i, j)
    for al in AISLES:
        sgn = 1 if al.left else -1  # towards the nave from the outer wall
        slope = (al.ceiling(al.nave_u) - al.ceiling(al.in_u)) / (al.nave_u - al.in_u)  # dz/du of the ceiling
        tilt = (slope, 0, -1)  # normal into the room, down and a little towards the lower side
        for v in (-4.5, -1.5, 1.5, 4.5, 7.5) if not al.left else (-1.5, 1.5, 4.5, 7.5):
            cu_o, cu_n = al.in_u + sgn * 0.45, al.nave_u - sgn * 0.45
            ring = [
                add(f"{al.name}_out", v, al.in_u, 3.0, (sgn, 0, 0)),
                add(f"{al.name}_co", v, cu_o, al.ceiling(cu_o) - 0.065, tilt),
                add(f"{al.name}_cn", v, cu_n, al.ceiling(cu_n) - 0.065, tilt),
                add(f"{al.name}_wall", v, al.nave_u, 4.4, (-sgn, 0, 0)),
            ]
            for i, j in itertools.pairwise(ring):
                link(i, j)
            # over the nave wall to the nearest upper-wall anchor of the nave
            side = roles["L_lo" if al.left else "R_lo"]
            link(ring[-1], min(side, key=lambda e: abs(e[0] - v))[1])
    for members in roles.values():
        for (_, i), (_, j) in itertools.pairwise(sorted(members)):
            link(i, j)
    for an in anchors:
        an["links"] = sorted(an["links"])

    def room(x0, x1, y0, y1, z1):
        ring = [pt(x, y, 0)[:2] for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
        return {"polygon": ring, "z0": round(dz - 0.2, 3), "z1": round(dz + z1, 3)}

    rooms = [room(NX0 + 0.2, NX1 - 0.2, NV0, NV1 - 0.2, ZE), room(AX0 + 0.2, AX1 - 0.2, AV0 + 0.2, NV0, ZA)]
    for al in AISLES:  # the aisles, overlapping the nave's room across the arcade
        x0, x1 = (al.u0 + 0.2, al.u1 + 0.2) if al.left else (al.u0 - 0.2, al.u1 - 0.2)
        rooms.append(room(x0, x1, al.v0 + 0.2, al.v1 - 0.2, 5.2))

    return {
        "door": dict(zip("xyz", pt(CU, 17.2, 0.0)), yaw=into),
        "doorway": {"pos": pt(CU, (IV1 + NV1) / 2, F), "yaw": out, "width": DOOR_W, "height": DOOR_TOP - F},
        "exit": {"pos": pt(CU, 18.5, 0.0), "yaw": into},
        "aisle": aisle,
        "seats": seats,
        "pastor": {"pos": pt(CU, -9.3, F), "yaw": out},
        "monks": [{"pos": pt(CU + dx, -8.9, F), "yaw": out} for dx in (-1.2, 1.2)],
        "sacristy_door": {"pos": pt(CU0 + 0.5, -9.9, F), "yaw": bearing(1, 0)},
        "props": props,
        "anchors": anchors,
        "rooms": rooms,
    }


def church_panes(b: dict) -> list[tuple[str, list, dict]]:
    """The glass of the window openings, one pane each: (node name, two-sided polygon as triangles in map metres standing on
    the building base, the ``windows.json`` entry). The game makes them see-through and breakable like every tagged pane;
    its rectangle is the bounding box of the opening (an arched or round pane is a little smaller than that)."""
    ex = b["footprint"]["exterior"]
    ox, oy = (sum(p[k] for p in ex) / len(ex) for k in (0, 1))
    a = float(b["model_axis"])
    vx, vy = math.cos(a), math.sin(a)

    def pt(u, v, z):
        return (ox + u * vy + v * vx, oy - u * vx + v * vy, z)

    out = []
    for k, w in enumerate(WINDOWS):
        normal = (w.n, 0) if w.axis == "v" else (0, w.n)

        def corner(da, z, w=w):
            u, v = (w.t, w.c + da) if w.axis == "v" else (w.c + da, w.t)
            return pt(u, v, z)

        c = [corner(da, z) for da, z in w.outline]
        tris = []
        for i in range(1, len(c) - 1):
            tris += [[c[0], c[i], c[i + 1]], [c[0], c[i + 1], c[i]]]  # two-sided
        das, zs = [da for da, _ in w.outline], [z for _, z in w.outline]
        width, z0, z1 = max(das) - min(das), min(zs), max(zs)
        mid = (max(das) + min(das)) / 2
        centre = corner(mid, (z0 + z1) / 2)
        node = f"window_{b.get('id', 'church')}_c{k:02d}"
        out.append((node, tris, {
            "building": b.get("id", "church"), "id": f"c{k:02d}", "node": node, "kind": "window", "breakable": True,
            "confidence": "high", "width_m": round(width, 3), "height_m": round(z1 - z0, 3),
            "centre": [round(x, 3) for x in centre],
            "normal": [round(normal[0] * vy + normal[1] * vx, 4), round(-normal[0] * vx + normal[1] * vy, 4)],
        }))  # fmt: skip
    return out
