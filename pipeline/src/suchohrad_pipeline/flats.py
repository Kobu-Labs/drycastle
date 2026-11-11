"""Hand-modelled block of flats, Suchohrad 295 (parcel P-0561, footprint ``w378900224``).

A two-storey house of four flats under one gabled roof, modelled from four Street View frames (2022):
both halves of the street front, the west gable and the rear wall. The street front is what makes the
house: per half a flat end bay with two windows per floor, then a loggia cut into the wall on both floors
(the upper one with a balcony slab standing 0.37 m proud, the lower one a raised terrace), then a plain
wall, and in the middle the staircase block that stands 0.76 m proud from the first floor up and leaves the
entrance open underneath, the door set back in a recess. The roof overhangs the front by more than a metre
on exposed rafters, enough to cover the staircase block.

Local frame (metres): x along the front from the west gable (0) to the east gable (``L``), y from the main
front wall plane (0) to the rear wall (``D``), negative in front of the wall, z up from the ground. The
frame is laid on the footprint by its longest edge (the rear wall) and the two corners next to it, so the
gables and the rear wall follow the real outline; the stepped front follows the outline to a few centimetres.

Where the numbers come from:

* plan: the cadastral footprint (bay widths 4.3 / 3.8 / 2.0 / 2.2 / 2.0 / 3.8 / 4.3 m, steps 0.37 and 0.76 m);
* heights and the roof: the west gable frame, rectified with a fitted camera (vertical and horizontal
  vanishing points; gable 9.15 m wide as the scale). Eave 5.45 m, ridge 7.1 m (ridge in the middle),
  windows 0.9 x 1.25 m with sills at 0.85 and 3.7 m, front eave 1.15 m out. All +-0.15 m;
* rear wall: window positions by a projective fit along the wall (corners and the stair windows as the
  fixed points), +-0.3 m; the wall is symmetric about the staircase windows, which sit half a storey up;
* loggia depth (1.2 m) and the entrance recess (1.5 m) from parallax between the two front frames, +-0.3 m.

Not modelled: satellite dishes, the aerial and the vent pipes on the roof, lamps, the intercom, the low
steel barriers along the pavement, and anything behind the glass. The east gable was not photographed and
mirrors the west one. Window panes are part of the mesh (no ``window_`` nodes, unlike ``building_detail``).
"""

import math
from dataclasses import dataclass

import numpy as np
from shapely.geometry import Polygon
from shapely.geometry import box as _rect
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from suchohrad_pipeline.buildings import footprint_polygon, hex_to_rgb
from suchohrad_pipeline.meshes import Mesh, _polygons, triangulate

# ---- measures ------------------------------------------------------------------------------------
L, D = 22.4, 9.15  # front length, depth
EAVE, RISE, ROOF_T = (
    5.45,
    1.55,
    0.15,
)  # wall top, rise of the roof underside to the ridge, roof thickness
OH_FRONT, OH_BACK, OH_VERGE = 1.15, 0.6, 0.25
PLINTH = 0.5
SLAB0, SLAB1, CURB = (
    2.5,
    2.85,
    2.95,
)  # first-floor slab: underside, top (balcony floor), top of its upstand
TERRACE = 0.35  # floor of the ground-floor terraces
LOGGIA_TOP = 5.1  # ceiling of the upper loggias
LOGGIA_DEPTH, BALCONY_OUT = 1.2, 0.37
RAIL_H = 1.0
TOWER_X, TOWER_OUT = (10.085, 12.315), 0.76  # staircase block
ENTRY_DEPTH, ENTRY_FLOOR = 1.5, 0.1
# per half: balcony slab (the cadastral bay) and the loggia cut into the wall, 0.25 m narrower on each side
BAYS = (((4.275, 8.085), (4.525, 7.835), True), ((14.315, 18.125), (14.565, 17.875), False))
LO, UP = (0.85, 2.15), (3.7, 4.95)  # sill and head of the ordinary windows, ground and first floor

WALL, PLINTH_C, ROOF, SOFFIT, RAFTER = "#ece6cc", "#9c7466", "#5f5650", "#5a3f33", "#422d1f"
GUTTER, SCREEN, RAIL, FRAME, MAILBOX = "#b4b8bb", "#4d7b68", "#6a4a3a", "#f4f3ee", "#a9674c"
LANDING, CABINET, VENT, PLATE = "#d9bca6", "#eceae2", "#3a2c22", "#dfe6ee"
DOOR_ORANGE, DOOR_BROWN = "#b06a3c", "#5e3d2c"
GLASS = [150, 190, 215]


def _under(y: float) -> float:
    """Height of the roof underside above y (it keeps sloping past the walls)."""
    return EAVE + RISE * (1.0 - abs(y - D / 2) / (D / 2))


@dataclass(frozen=True)
class _Wall:
    """A vertical wall plane: origin, unit direction along it and outward normal (all in x, y)."""

    ox: float
    oy: float
    dx: float
    dy: float
    nx: float
    ny: float

    def at(self, a: float, z: float, depth: float = 0.0) -> tuple[float, float, float]:
        """Point ``a`` along the wall at height z, ``depth`` behind the plane (negative: in front)."""
        return (self.ox + self.dx * a - self.nx * depth, self.oy + self.dy * a - self.ny * depth, z)

    @property
    def normal(self) -> tuple[float, float, float]:
        return (self.nx, self.ny, 0.0)


def _along_x(y: float, out: int) -> _Wall:  # ``a`` is x; faces -y (out = -1) or +y
    return _Wall(0.0, y, 1.0, 0.0, 0.0, float(out))


def _along_y(x: float, out: int) -> _Wall:  # ``a`` is y; faces -x (out = -1) or +x
    return _Wall(x, 0.0, 0.0, 1.0, float(out), 0.0)


@dataclass(frozen=True)
class _Op:
    """Opening in a wall: window (``panes`` lights side by side), door (solid lower panel) or french door."""

    a0: float
    a1: float
    z0: float
    z1: float
    kind: str = "window"
    panes: int = 1
    recess: float = 0.12
    frame: str = FRAME


class _Build:
    def __init__(self, detail: bool) -> None:
        self.detail = detail
        self.tris: list = []
        self.cols: list = []

    def face(self, pts, colour, normal) -> None:
        """Convex planar polygon, wound counter-clockwise seen from the side ``normal`` points to."""
        p = [np.asarray(q, float) for q in pts]
        n = sum(np.cross(p[k] - p[0], p[k + 1] - p[0]) for k in range(1, len(p) - 1))
        if np.linalg.norm(n) < 1e-10:
            return
        if n @ np.asarray(normal, float) < 0:
            p = p[::-1]
        for k in range(1, len(p) - 1):
            self.tris.append([p[0], p[k], p[k + 1]])
            self.cols.append(colour)

    def region(self, shape, to3, colour, normal) -> None:
        """Any flat region (may have holes), given in the two coordinates ``to3`` lifts into space."""
        for poly in _polygons(shape):
            for tri in triangulate(orient(poly, 1.0)):
                self.face([to3(*c) for c in tri], colour, normal)

    def box(self, x0, x1, y0, y1, z0, z1, colour, skip: str = "") -> None:
        """Axis-aligned box; ``skip`` names faces to leave out, e.g. ``"+y -z"``."""
        x0, x1, y0, y1, z0, z1 = *sorted((x0, x1)), *sorted((y0, y1)), *sorted((z0, z1))
        faces = {
            "-x": ([(x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1)], (-1, 0, 0)),
            "+x": ([(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)], (1, 0, 0)),
            "-y": ([(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)], (0, -1, 0)),
            "+y": ([(x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1)], (0, 1, 0)),
            "-z": ([(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0)], (0, 0, -1)),
            "+z": ([(x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], (0, 0, 1)),
        }
        for name, (pts, normal) in faces.items():
            if name not in skip.split():
                self.face(pts, colour, normal)

    def wbox(self, w: _Wall, a0, a1, out, z0, z1, colour) -> None:
        """Box standing ``out`` proud of wall ``w`` (no face against the wall)."""
        (xa, ya, _), (xb, yb, _) = w.at(a0, 0.0), w.at(a1, 0.0, -out)
        back = ("+" if w.nx + w.ny < 0 else "-") + ("x" if w.nx else "y")
        self.box(xa, xb, ya, yb, z0, z1, colour, skip=back)

    def beam(self, p, q, w: float, h: float, colour) -> None:
        """Bar of section w (sideways) x h from p to q."""
        p, q = np.asarray(p, float), np.asarray(q, float)
        u = (q - p) / np.linalg.norm(q - p)
        side = np.cross(u, (0.0, 0.0, 1.0))
        side = (
            side / np.linalg.norm(side)
            if np.linalg.norm(side) > 1e-9
            else np.array([1.0, 0.0, 0.0])
        )
        up = np.cross(side, u)
        ring = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
        a = [p + sx * side * w / 2 + sz * up * h / 2 for sx, sz in ring]
        b = [q + sx * side * w / 2 + sz * up * h / 2 for sx, sz in ring]
        self.face(a, colour, -u)
        self.face(b, colour, u)
        for k in range(4):
            j = (k + 1) % 4
            quad = [a[k], a[j], b[j], b[k]]
            self.face(quad, colour, np.mean(quad, axis=0) - (p + q) / 2)

    def wall(self, w: _Wall, outline, colour, ops=(), cuts=()) -> None:
        """Wall face ``outline`` (a, z) minus ``cuts`` (a0, z0, a1, z1), with the openings ``ops`` in it."""
        holes = [_rect(*c) for c in cuts]
        if self.detail:
            holes += [_rect(o.a0, o.z0, o.a1, o.z1) for o in ops]
        shape = Polygon(outline)
        if holes:
            shape = shape.difference(unary_union(holes))
        self.region(shape, lambda a, z: w.at(a, z), colour, w.normal)
        if self.detail:
            for o in ops:
                self._opening(w, o, colour)

    def _opening(self, w: _Wall, o: _Op, wall_colour) -> None:
        r, f = o.recess, 0.06
        frame = hex_to_rgb(o.frame)
        mid = np.array(w.at((o.a0 + o.a1) / 2, (o.z0 + o.z1) / 2, r / 2))
        c = [(o.a0, o.z0), (o.a1, o.z0), (o.a1, o.z1), (o.a0, o.z1)]
        for k in range(4):  # reveals
            pa, pb = c[k], c[(k + 1) % 4]
            quad = [w.at(*pa), w.at(*pb), w.at(*pb, r), w.at(*pa, r)]
            self.face(quad, wall_colour, mid - np.mean(quad, axis=0))
        n = max(1, o.panes)
        lw = (o.a1 - o.a0 - f * (n + 1)) / n
        lights = [
            (o.a0 + f + k * (lw + f), o.z0 + f, o.a0 + f + k * (lw + f) + lw, o.z1 - f)
            for k in range(n)
        ]

        def back(a: float, z: float):
            return w.at(a, z, r)

        ring = _rect(o.a0, o.z0, o.a1, o.z1).difference(unary_union([_rect(*t) for t in lights]))
        self.region(ring, back, frame, w.normal)
        for a0, z0, a1, z1 in lights:
            if o.kind == "door":  # solid lower panel, glass above
                zp = z0 + 0.7
                self.face([back(a0, z0), back(a1, z0), back(a1, zp), back(a0, zp)], frame, w.normal)
                z0 = zp
            self.face([back(a0, z0), back(a1, z0), back(a1, z1), back(a0, z1)], GLASS, w.normal)
        if o.kind == "window":  # sill
            self.wbox(w, o.a0 - 0.04, o.a1 + 0.04, 0.05, o.z0 - 0.04, o.z0, frame)

    def railing(self, x0, x1, y_front, z0, z1) -> None:
        """Three-sided railing (front and both returns to the wall at y = 0) with a green screen on it."""
        rail, screen = hex_to_rgb(RAIL), hex_to_rgb(SCREEN)
        yc, xa, xb = y_front + 0.05, x0 + 0.05, x1 - 0.05
        if not self.detail:  # collision: plain panels
            self.box(x0, x1, y_front, y_front + 0.1, z0, z1, screen)
            self.box(x0, x0 + 0.1, y_front + 0.1, 0.0, z0, z1, screen, skip="+y")
            self.box(x1 - 0.1, x1, y_front + 0.1, 0.0, z0, z1, screen, skip="+y")
            return
        n = max(1, round((xb - xa) / 1.3))
        posts = [(xa + (xb - xa) * k / n, yc) for k in range(n + 1)] + [(xa, -0.04), (xb, -0.04)]
        for px, py in posts:
            self.beam((px, py, z0), (px, py, z1 - 0.04), 0.04, 0.04, rail)
        zr = z1 - 0.02
        self.beam((xa - 0.025, yc, zr), (xb + 0.025, yc, zr), 0.05, 0.04, rail)
        self.beam((xa, yc, zr), (xa, 0.0, zr), 0.05, 0.04, rail)
        self.beam((xb, yc, zr), (xb, 0.0, zr), 0.05, 0.04, rail)
        s0, s1 = z0 + 0.04, z1 - 0.07
        self.box(xa - 0.03, xb + 0.03, yc - 0.04, yc - 0.025, s0, s1, screen)
        self.box(xa - 0.04, xa - 0.025, yc - 0.025, -0.01, s0, s1, screen)
        self.box(xb + 0.025, xb + 0.04, yc - 0.025, -0.01, s0, s1, screen)

    def mesh(self) -> Mesh:
        return Mesh(
            np.array(self.tris, float).reshape(-1, 3, 3),
            np.array(self.cols, np.uint8).reshape(-1, 3),
        )


def _mirror(a0: float, a1: float) -> tuple[float, float]:
    return (L - a1, L - a0)


def _both(spans) -> list[tuple[float, float]]:
    """Spans given for the west half, plus their mirror images in the east half."""
    return [*spans, *(_mirror(*s) for s in spans)]


def _shell(B: _Build) -> None:
    wall = hex_to_rgb(WALL)
    front, rear = _along_x(0.0, -1), _along_x(D, 1)
    # street front: the main wall plane with the loggias and the staircase bay cut out
    cuts = [(rx[0], 0.0, rx[1], LOGGIA_TOP) for _, rx, _ in BAYS] + [
        (TOWER_X[0], 0.0, TOWER_X[1], EAVE)
    ]
    ops = [_Op(a0, a1, *zz) for a0, a1 in _both([(1.0, 2.0), (2.55, 3.55)]) for zz in (LO, UP)]
    B.wall(front, [(0, 0), (L, 0), (L, EAVE), (0, EAVE)], wall, ops, cuts)
    # rear wall: per half two pairs of windows and two small ones on each floor, stair windows between
    ops = [
        _Op(a0, a1, *zz)
        for a0, a1 in _both([(0.55, 1.5), (1.8, 2.75), (4.0, 4.95), (5.25, 6.2)])
        for zz in (LO, UP)
    ]
    ops += [
        _Op(a0, a1, z1 - 0.5, z1)
        for a0, a1 in _both([(7.35, 7.85), (9.2, 9.7)])
        for z1 in (LO[1], UP[1])
    ]
    ops += [_Op(a0, a1, 3.2, 4.6) for a0, a1 in _both([(10.2, 11.05)])]
    B.wall(rear, [(0, 0), (L, 0), (L, EAVE), (0, EAVE)], wall, ops)
    # gables
    gable = [(0, 0), (D, 0), (D, EAVE), (D / 2, EAVE + RISE), (0, EAVE)]
    for w in (_along_y(0.0, -1), _along_y(L, 1)):
        ops = [_Op(a0, a1, *zz) for a0, a1 in ((2.85, 3.75), (5.4, 6.3)) for zz in (LO, UP)]
        B.wall(w, gable, wall, ops)
        if B.detail:
            for a0 in (2.5, 6.2):  # attic vent slots
                B.wbox(w, a0, a0 + 0.45, 0.01, 5.55, 5.63, hex_to_rgb(VENT))


def _loggias(B: _Build) -> None:
    wall, plinth = hex_to_rgb(WALL), hex_to_rgb(PLINTH_C)
    back = _along_x(LOGGIA_DEPTH, -1)
    for (bx0, bx1), (rx0, rx1), west in BAYS:

        def span(o0: float, o1: float, west=west, rx0=rx0, rx1=rx1) -> tuple[float, float]:
            """Offsets from the outer end of the loggia (the end bay side)."""
            return (rx0 + o0, rx0 + o1) if west else (rx1 - o1, rx1 - o0)

        for floor, (z0, z1) in enumerate(((TERRACE, SLAB0), (SLAB1, LOGGIA_TOP))):
            # balcony door at the outer end, double window, narrow window at the staircase end
            brown = {(True, 0): DOOR_ORANGE, (False, 1): DOOR_BROWN}.get((west, floor), FRAME)
            head = z1 - 0.22
            ops = [
                _Op(*span(0.1, 0.9), z0 + 0.03, head, kind="french", recess=0.1, frame=brown),
                _Op(*span(1.1, 2.6), z0 + 0.9, head, panes=2, recess=0.1),
                _Op(*span(2.8, 3.25), z0 + 0.9, head, recess=0.1),
            ]
            B.wall(back, [(rx0, z0), (rx1, z0), (rx1, z1), (rx0, z1)], wall, ops)
            side = [(0, z0), (LOGGIA_DEPTH, z0), (LOGGIA_DEPTH, z1), (0, z1)]
            B.wall(_along_y(rx0, 1), side, wall)
            B.wall(_along_y(rx1, -1), side, wall)
        c = [(rx0, 0), (rx1, 0), (rx1, LOGGIA_DEPTH), (rx0, LOGGIA_DEPTH)]
        o = [(bx0, -BALCONY_OUT), (bx1, -BALCONY_OUT), (bx1, 0), (bx0, 0)]
        B.face([(x, y, LOGGIA_TOP) for x, y in c], wall, (0, 0, -1))  # ceiling of the upper loggia
        # first-floor slab: ceiling of the terrace, balcony floor, upstand all round the balcony
        B.face([(x, y, SLAB0) for x, y in c], wall, (0, 0, -1))
        B.face([(x, y, SLAB0) for x, y in o], wall, (0, 0, -1))
        B.face([(x, y, SLAB1) for x, y in c], wall, (0, 0, 1))
        t = 0.1
        B.face(
            [(bx0 + t, -BALCONY_OUT + t, SLAB1), (bx1 - t, -BALCONY_OUT + t, SLAB1), (bx1 - t, 0, SLAB1),
             (bx0 + t, 0, SLAB1)], wall, (0, 0, 1),
        )  # fmt: skip
        B.box(bx0, bx1, -BALCONY_OUT, -BALCONY_OUT + t, SLAB0, CURB, wall, skip="-z")
        B.box(bx0, bx0 + t, -BALCONY_OUT + t, 0.0, SLAB0, CURB, wall, skip="-z -y +y")
        B.box(bx1 - t, bx1, -BALCONY_OUT + t, 0.0, SLAB0, CURB, wall, skip="-z -y +y")
        B.railing(bx0, bx1, -BALCONY_OUT, CURB, CURB + RAIL_H)
        # ground-floor terrace: a low slab, its railing
        B.face([(x, y, TERRACE) for x, y in c], plinth, (0, 0, 1))
        B.box(bx0, bx1, -BALCONY_OUT, 0.0, 0.0, TERRACE, plinth, skip="-z +y")
        B.railing(bx0, bx1, -BALCONY_OUT, TERRACE, TERRACE + 0.9)
    # the white closet built into the staircase end of the west terrace
    rx1 = BAYS[0][1][1]
    B.box(rx1 - 0.55, rx1, 0.1, LOGGIA_DEPTH, TERRACE, SLAB0, wall, skip="+x +y -z +z")


def _staircase(B: _Build) -> None:
    wall = hex_to_rgb(WALL)
    x0, x1 = TOWER_X
    top = _under(-TOWER_OUT)
    # the block over the entrance: front and both cheeks, each cheek with a small window under the eave
    B.wall(_along_x(-TOWER_OUT, -1), [(x0, SLAB0), (x1, SLAB0), (x1, top), (x0, top)], wall)
    cheek = [(-TOWER_OUT, SLAB0), (0, SLAB0), (0, EAVE), (-TOWER_OUT, top)]
    for w in (_along_y(x0, -1), _along_y(x1, 1)):
        B.wall(w, cheek, wall, [_Op(-0.62, -0.17, 4.45, 4.95, recess=0.08)])
    B.face(
        [(x0, -TOWER_OUT, SLAB0), (x1, -TOWER_OUT, SLAB0), (x1, ENTRY_DEPTH, SLAB0), (x0, ENTRY_DEPTH, SLAB0)],
        wall, (0, 0, -1),
    )  # fmt: skip
    # entrance recess under it: side walls, the door wall, a tiled landing
    side = [(0, ENTRY_FLOOR), (ENTRY_DEPTH, ENTRY_FLOOR), (ENTRY_DEPTH, SLAB0), (0, SLAB0)]
    B.wall(_along_y(x0, 1), side, wall)
    B.wall(_along_y(x1, -1), side, wall)
    xm = (x0 + x1) / 2
    door = _Op(xm - 0.475, xm + 0.475, ENTRY_FLOOR + 0.03, 2.15, kind="door", recess=0.1)
    B.wall(
        _along_x(ENTRY_DEPTH, -1),
        [(x0, ENTRY_FLOOR), (x1, ENTRY_FLOOR), (x1, SLAB0), (x0, SLAB0)],
        wall,
        [door],
    )
    landing = hex_to_rgb(LANDING)
    B.face(
        [(x0, -TOWER_OUT, ENTRY_FLOOR), (x1, -TOWER_OUT, ENTRY_FLOOR), (x1, ENTRY_DEPTH, ENTRY_FLOOR),
         (x0, ENTRY_DEPTH, ENTRY_FLOOR)], landing, (0, 0, 1),
    )  # fmt: skip
    B.box(x0, x1, -TOWER_OUT, 0.0, 0.0, ENTRY_FLOOR, landing, skip="+z -z +y")
    if not B.detail:
        return
    cab = hex_to_rgb(CABINET)  # meter cabinets along both side walls of the recess
    B.box(x0, x0 + 0.3, 0.05, 1.4, 0.25, 1.3, cab, skip="-x")
    B.box(x1 - 0.3, x1, 0.05, 1.4, 0.25, 1.3, cab, skip="+x")
    front = _along_x(0.0, -1)
    for a0, a1 in _both(
        [(x0 - 0.97, x0 - 0.59), (x0 - 0.57, x0 - 0.19)]
    ):  # letter boxes on the plain walls
        B.wbox(front, a0, a1, 0.1, 1.45, 1.75, hex_to_rgb(MAILBOX))
    B.wbox(front, x0 - 0.5, x0 - 0.26, 0.015, 2.16, 2.38, hex_to_rgb(PLATE))  # house number


def _plinth(B: _Build) -> None:
    if not B.detail:
        return
    c = hex_to_rgb(PLINTH_C)
    front = _along_x(0.0, -1)
    x = [0.0, *(v for (b0, b1), _, _ in BAYS for v in (b0, b1)), L]
    for a0, a1 in ((x[0], x[1]), (x[2], TOWER_X[0]), (TOWER_X[1], x[3]), (x[4], x[5])):
        B.wbox(front, a0, a1, 0.02, 0.0, PLINTH, c)
    B.wbox(_along_x(D, 1), 0.0, L, 0.02, 0.0, PLINTH, c)
    for w in (_along_y(0.0, -1), _along_y(L, 1)):
        B.wbox(w, -0.02, D + 0.02, 0.02, 0.0, PLINTH, c)


def _roof(B: _Build) -> None:
    roof, soffit, rafter, gutter = (hex_to_rgb(c) for c in (ROOF, SOFFIT, RAFTER, GUTTER))
    xa, xb = -OH_VERGE, L + OH_VERGE
    yf, yb, ym = -OH_FRONT, D + OH_BACK, D / 2

    def top(x: float, y: float):
        return (x, y, _under(y) + ROOF_T)

    def under(x: float, y: float):
        return (x, y, _under(y))

    for y0, y1, out in ((yf, ym, -1), (yb, ym, 1)):
        B.face([top(xa, y0), top(xb, y0), top(xb, y1), top(xa, y1)], roof, (0, 0, 1))
        B.face(
            [under(xa, y0), under(xb, y0), top(xb, y0), top(xa, y0)], rafter, (0, out, 0)
        )  # eave board
        for x, nx in ((xa, -1), (xb, 1)):  # barge boards
            B.face([under(x, y0), under(x, y1), top(x, y1), top(x, y0)], rafter, (nx, 0, 0))
        wall_y = 0.0 if out < 0 else D
        B.face(
            [under(xa, y0), under(xb, y0), under(xb, wall_y), under(xa, wall_y)], soffit, (0, 0, -1)
        )
        for x0, x1 in ((xa, 0.0), (L, xb)):  # underside of the verges
            B.face(
                [under(x0, wall_y), under(x1, wall_y), under(x1, y1), under(x0, y1)],
                soffit,
                (0, 0, -1),
            )
    if not B.detail:
        return
    n = round((L - 0.4) / 0.9)
    for k in range(n + 1):  # exposed rafter tails under both eaves
        x = 0.2 + (L - 0.4) * k / n
        y_in = -TOWER_OUT if TOWER_X[0] - 0.05 < x < TOWER_X[1] + 0.05 else 0.0
        for y0, y1 in ((yf + 0.02, y_in), (yb - 0.02, D)):
            B.beam((x, y0, _under(y0) - 0.06), (x, y1, _under(y1) - 0.06), 0.08, 0.12, rafter)
    for y0, y1, y_wall in (
        (yf - 0.12, yf, -0.07),
        (yb, yb + 0.12, D + 0.07),
    ):  # gutters and downpipes
        ye = yf if y_wall < 0 else yb
        zg = _under(ye)
        B.box(xa, xb, y0, y1, zg - 0.07, zg + 0.04, gutter)
        z_bend = zg - (0.75 if y_wall < 0 else 0.45)
        for x in (0.33, L - 0.33):
            B.beam((x, (y0 + y1) / 2, zg - 0.08), (x, y_wall, z_bend), 0.09, 0.09, gutter)
            B.beam((x, y_wall, z_bend + 0.03), (x, y_wall, 0.05), 0.09, 0.09, gutter)


def _corners(b: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Map positions of the local corners (0, 0), (L, 0), (L, D), (0, D): the rear wall is the longest edge."""
    ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
    n = len(ring)
    k = max(range(n), key=lambda i: math.dist(ring[i], ring[(i + 1) % n]))
    rear_right, rear_left = ring[k], ring[(k + 1) % n]
    front_left, front_right = ring[(k + 2) % n], ring[k - 1]
    return tuple(np.array(p, float) for p in (front_left, front_right, rear_right, rear_left))


def flats_mesh(b: dict, detail: bool = True) -> Mesh:
    """The house on the footprint of ``b`` (map metres); ``detail=False`` gives the lighter collision mesh."""
    B = _Build(detail)
    _shell(B)
    _loggias(B)
    _staircase(B)
    _plinth(B)
    _roof(B)
    m = B.mesh()
    fl, fr, rr, rl = _corners(b)
    u, v = m.tris[..., 0:1] / L, m.tris[..., 1:2] / D
    out = m.tris.copy()
    out[..., 0:2] = fl * (1 - u) * (1 - v) + fr * u * (1 - v) + rr * u * v + rl * (1 - u) * v
    return Mesh(out, m.colours)
