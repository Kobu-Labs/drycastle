"""Hand-modelled two-storey house with corner loggias and a recessed staircase bay (flat roof).

Two blocks of 7.2 m stand either side of a staircase bay that is set 1.3 m back from the street front.
Each block has, at its outer end, a loggia cut into the wall on the upper floor (a solid parapet, the
opening above it up to the roof slab) over a wide glazed opening on the ground floor (the lower loggia,
glazed in), and one ordinary window per floor next to the bay. A low porch with a corrugated lean-to
roof closes the foot of the bay; above it the bay wall has two glass-block lights and two narrow windows.
The rear wall is flat: two windows per floor in each half, in the middle the door in a recess and the
tall glass-block light of the staircase above it. A plinth of 0.7 m stands 4 cm proud all round, the
roof is a thin slab that overhangs 0.2 m and follows the stepped front.

Local frame (metres): x along the street front from the left end as seen from the street (0) to the
right end (``L``), y from the front wall plane of the blocks (0) to the rear wall (``D``), negative in
front of the wall, z up from the ground. The frame is laid on the bounding rectangle of the footprint;
``model_axis`` is the bearing the street front faces. Along the front the model is stretched to the
rectangle's length. In depth only the plain middle is stretched: the front 2 m (loggias, bay, porch) and
the rear 1 m (door recess) keep their size, so the cutouts are as deep on every house.

Where the numbers come from: two Street View frames (2022), one of the street front, one of the rear
wall, both oblique. The frames were read before the house was located, so the scale is the storey: sill
to sill is taken as 2.85 m, which makes the house 6.3 m high and puts the camera 2.4 m above its foot
(the Street View car is at 2.5 m). Positions along a wall are read by a projective fit between its two
corners. The depth is the mean of the cadastral outlines the model stands on (16.6-19.2 x 10.5-11.9 m).

* heights (+-0.1 m): plinth 0.7, sills 1.45 and 4.3, window heads 2.9 and 5.75, wall top 6.15, roof 6.3;
  the wide ground-floor openings 1.6-3.1, the loggia parapets 4.65 (left, white cap) and 4.35 (right).
  The left loggia is painted green on the photographed house; here both are the colour of the walls;
* along the front (+-0.2 m): windows 1.8 m wide, loggias 3.1 and 3.0 m, bay 2.4 m wide and 1.3 m deep;
* rear wall (+-0.3 m): symmetric about the staircase, windows 1.8 m wide;
* guessed, not measured: the loggia depth (1.2 m, from the visible strip of side wall), the left half
  of the bay wall (hidden, mirrors the right half) and the basement lights of the right block (behind
  bushes, mirror the left block). The gables are seen edge-on, showed no openings and have none.

Not modelled: satellite dishes, aerials, clothes lines and drying racks, the lamp and the cables, and
anything behind the glass. The porch door is a dark panel: its foot is hidden behind bushes, so whether
steps lead down to it is unknown. Window panes are part of the mesh (no ``window_`` nodes).
"""

import math

import numpy as np
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient

from suchohrad_pipeline.buildings import footprint_polygon, hex_to_rgb
from suchohrad_pipeline.flats import GLASS, _along_x, _along_y, _Build, _Op, _Wall
from suchohrad_pipeline.meshes import Mesh

# ---- measures ------------------------------------------------------------------------------------
L, D = 16.8, 11.4  # street front, depth
RIGID_FRONT, RIGID_REAR = 2.0, 1.0  # depth at the front and at the rear that is not stretched
BAY = (7.2, 9.6)  # staircase bay between the two blocks
BAY_DEPTH = 1.3
WALL_TOP, ROOF_T, ROOF_OUT = 6.15, 0.15, 0.2
PLINTH, PLINTH_OUT = 0.7, 0.04
FLOOR1 = 3.5  # upper floor (floor of the loggias)
LOGGIA_DEPTH, LOGGIA_TOP, PARAPET_T = 1.2, 6.0, 0.25
LO, UP = (1.45, 2.9), (4.3, 5.75)  # sill and head of the ordinary windows, ground and upper floor
WIDE = (1.6, 3.1)  # sill and head of the glazed-in ground-floor loggias
PORCH_TOP, PORCH_OUT = 1.3, 0.25  # porch front wall; how far its roof sails over it
DOOR_DEPTH, DOOR_HEAD = 0.7, 2.05  # recess of the rear door
STAIR_LIGHT = (3.15, 5.5)  # glass-block light of the staircase, rear wall
# per block: block span, loggia opening, parapet top, wide ground-floor opening, ordinary window, panes
BLOCKS = (
    ((0.0, BAY[0]), (0.4, 3.5), 4.65, (0.45, 3.6), (4.35, 6.15), 4),
    ((BAY[1], L), (13.45, 16.45), 4.35, (13.4, 16.4), (10.65, 12.45), 3),
)
REAR_WINDOWS = ((1.05, 2.85), (4.6, 6.4), (10.4, 12.2), (13.95, 15.75))
HATCHES_FRONT = ((2.2, 2.9), (4.85, 5.5), (11.3, 11.95), (13.9, 14.6))
HATCHES_REAR = ((1.65, 2.4), (5.05, 5.75), (11.05, 11.75), (14.4, 15.15))
REAR_DOOR = (7.4, 9.4)  # the recess; the door stands 0.5 m from its high-x side

WALL, PLINTH_C, ROOF, FASCIA = "#99958b", "#afb2b0", "#6c6e70", "#5b5e60"
CAP, FRAME = "#e6e6e2", "#f4f3ee"
SHUTTER, BAND, BLOCK_A, BLOCK_B, LINTEL = "#d6d6d2", "#c9c8c2", "#bccbc4", "#a9bab3", "#8f8b82"
HATCH, PORCH_DOOR, WOOD_DOOR, PLATE, CANOPY = "#4a3a30", "#2c2927", "#8a4f2e", "#e8e8e4", "#55585a"
GUTTER, PIPE_COPPER, PIPE_RUST, PIPE_DARK, CHIMNEY, VENT = (
    "#8e9496", "#9c6248", "#8a4b34", "#5a4d48", "#8d8a84", "#c9c9c4",
)  # fmt: skip


class _House(_Build):
    """``_Build`` with the openings this house needs beyond windows and doors."""

    def _reveals(self, w: _Wall, o: _Op, colour) -> None:
        mid = np.array(w.at((o.a0 + o.a1) / 2, (o.z0 + o.z1) / 2, o.recess / 2))
        c = [(o.a0, o.z0), (o.a1, o.z0), (o.a1, o.z1), (o.a0, o.z1)]
        for k in range(4):
            pa, pb = c[k], c[(k + 1) % 4]
            quad = [w.at(*pa), w.at(*pb), w.at(*pb, o.recess), w.at(*pa, o.recess)]
            self.face(quad, colour, mid - np.mean(quad, axis=0))

    def _pane(self, w: _Wall, a0, z0, a1, z1, depth, colour) -> None:
        self.face(
            [w.at(a0, z0, depth), w.at(a1, z0, depth), w.at(a1, z1, depth), w.at(a0, z1, depth)],
            colour,
            w.normal,
        )

    def _opening(self, w: _Wall, o: _Op, wall_colour) -> None:
        r = o.recess
        if o.kind == "hatch":
            # a flat panel in the colour ``frame``: basement lights, the porch door
            self._reveals(w, o, wall_colour)
            self._pane(w, o.a0, o.z0, o.a1, o.z1, r, hex_to_rgb(o.frame))
        elif o.kind == "blocks":  # glass blocks, about 0.19 m each, in two tones
            self._reveals(w, o, wall_colour)
            na = max(1, round((o.a1 - o.a0) / 0.19))
            nz = max(1, round((o.z1 - o.z0) / 0.19))
            tones = (hex_to_rgb(BLOCK_A), hex_to_rgb(BLOCK_B))
            for i in range(na):
                for j in range(nz):
                    a0 = o.a0 + (o.a1 - o.a0) * i / na
                    z0 = o.z0 + (o.z1 - o.z0) * j / nz
                    self._pane(
                        w, a0, z0, a0 + (o.a1 - o.a0) / na, z0 + (o.z1 - o.z0) / nz, r, tones[(i + j) % 2]
                    )
            self.wbox(w, o.a0 - 0.04, o.a1 + 0.04, 0.05, o.z0 - 0.04, o.z0, hex_to_rgb(FRAME))
        elif o.kind == "wood":  # timber door with one narrow light
            self._reveals(w, o, wall_colour)
            wood = hex_to_rgb(o.frame)
            la0, la1 = o.a0 + (o.a1 - o.a0) * 0.36, o.a0 + (o.a1 - o.a0) * 0.64
            lz0, lz1 = o.z0 + (o.z1 - o.z0) * 0.42, o.z1 - 0.2
            leaf = Polygon(
                [(o.a0, o.z0), (o.a1, o.z0), (o.a1, o.z1), (o.a0, o.z1)],
                [[(la0, lz0), (la1, lz0), (la1, lz1), (la0, lz1)]],
            )
            self.region(leaf, lambda a, z: w.at(a, z, r), wood, w.normal)
            self._pane(w, la0, lz0, la1, lz1, r, GLASS)
        else:
            super()._opening(w, o, wall_colour)
            if o.kind == "shutters":
                # roller shutters down over the two outer lights, their box above
                f, n = 0.06, max(1, o.panes)
                lw = (o.a1 - o.a0 - f * (n + 1)) / n
                for k in (0, n - 1):
                    a0 = o.a0 + f + k * (lw + f)
                    self._pane(w, a0, o.z0 + f, a0 + lw, o.z1 - f, r - 0.03, hex_to_rgb(SHUTTER))
                self.wbox(w, o.a0 - 0.05, o.a1 + 0.05, 0.04, o.z1, o.z1 + 0.18, hex_to_rgb(BAND))
                self.wbox(w, o.a0 - 0.04, o.a1 + 0.04, 0.05, o.z0 - 0.04, o.z0, hex_to_rgb(FRAME))


def _rect(a0: float, a1: float, z0: float, z1: float) -> list[tuple[float, float]]:
    return [(a0, z0), (a1, z0), (a1, z1), (a0, z1)]


def _hatches(spans, a0: float, a1: float) -> list[_Op]:
    return [
        _Op(h0, h1, 0.12, 0.55, kind="hatch", recess=0.15, frame=HATCH)
        for h0, h1 in spans
        if a0 < h0 and h1 < a1
    ]


def _front(B: _House) -> None:
    wall, plinth = hex_to_rgb(WALL), hex_to_rgb(PLINTH_C)
    front, foot = _along_x(0.0, -1), _along_x(-PLINTH_OUT, -1)
    for k, ((x0, x1), (g0, g1), ptop, (w0, w1), (s0, s1), panes) in enumerate(BLOCKS):
        ops = [_Op(s0, s1, *LO, panes=2, recess=0.15), _Op(s0, s1, *UP, panes=2, recess=0.15)]
        # the lower loggia, glazed in: shutters and their box on the left block, a plain frame on the right
        ops.append(_Op(w0, w1, *WIDE, kind="shutters" if k == 0 else "window", panes=panes, recess=0.08))
        B.wall(front, _rect(x0, x1, PLINTH, WALL_TOP), wall, ops, cuts=[(g0, ptop, g1, LOGGIA_TOP)])
        # plinth, 4 cm proud, with the basement lights; its ledge and the end at the bay
        e0, e1 = (x0 - PLINTH_OUT, x1) if k == 0 else (x0, x1 + PLINTH_OUT)
        B.wall(foot, _rect(e0, e1, 0.0, PLINTH), plinth, _hatches(HATCHES_FRONT, x0, x1))
        B.face([(e0, -PLINTH_OUT, PLINTH), (e1, -PLINTH_OUT, PLINTH), (e1, 0, PLINTH), (e0, 0, PLINTH)],
               plinth, (0, 0, 1))  # fmt: skip
        xe, out = (x1, 1) if k == 0 else (x0, -1)
        B.face([(xe, -PLINTH_OUT, 0), (xe, 0, 0), (xe, 0, PLINTH), (xe, -PLINTH_OUT, PLINTH)],
               plinth, (out, 0, 0))  # fmt: skip
        _loggia(B, g0, g1, ptop, k == 0)


def _loggia(B: _House, x0: float, x1: float, ptop: float, left: bool) -> None:
    """Upper loggia: the room behind the opening, its parapet and the door and window in its back wall."""
    wall = inner = ceiling = hex_to_rgb(WALL)

    def span(o0: float, o1: float) -> tuple[float, float]:
        """Offsets from the end of the loggia next to the staircase bay."""
        return (x1 - o1, x1 - o0) if left else (x0 + o0, x0 + o1)

    ops = [
        _Op(*span(0.2, 0.95), FLOOR1 + 0.05, 5.85, kind="french", recess=0.1),
        _Op(*span(1.05, 2.3), 4.4, 5.85, panes=2, recess=0.1),
    ]
    B.wall(_along_x(LOGGIA_DEPTH, -1), _rect(x0, x1, FLOOR1, LOGGIA_TOP), inner, ops)
    side = [(PARAPET_T, FLOOR1), (LOGGIA_DEPTH, FLOOR1), (LOGGIA_DEPTH, LOGGIA_TOP), (0, LOGGIA_TOP),
            (0, ptop), (PARAPET_T, ptop)]  # fmt: skip
    B.wall(_along_y(x0, 1), side, inner)
    B.wall(_along_y(x1, -1), side, inner)
    B.face([(x0, 0, LOGGIA_TOP), (x1, 0, LOGGIA_TOP), (x1, LOGGIA_DEPTH, LOGGIA_TOP),
            (x0, LOGGIA_DEPTH, LOGGIA_TOP)], ceiling, (0, 0, -1))  # fmt: skip
    B.face([(x0, PARAPET_T, FLOOR1), (x1, PARAPET_T, FLOOR1), (x1, LOGGIA_DEPTH, FLOOR1),
            (x0, LOGGIA_DEPTH, FLOOR1)], wall, (0, 0, 1))  # fmt: skip
    # parapet: its inner face and its top (the outer face is the street wall)
    B.wall(_along_x(PARAPET_T, 1), _rect(x0, x1, FLOOR1, ptop), wall)
    B.face([(x0, 0, ptop), (x1, 0, ptop), (x1, PARAPET_T, ptop), (x0, PARAPET_T, ptop)], wall, (0, 0, 1))
    if B.detail and left:  # white cap on the left parapet
        B.box(x0 - 0.03, x1 + 0.03, -0.05, PARAPET_T, ptop, ptop + 0.04, hex_to_rgb(CAP), skip="-z")


def _bay(B: _House) -> None:
    """Staircase bay: its back wall and cheeks, and the porch with a corrugated lean-to roof at its foot."""
    wall = hex_to_rgb(WALL)
    x0, x1 = BAY
    ops = []
    for a0 in (x0 + 0.2, x1 - 0.95):  # per half a glass-block light, a narrow window above it
        ops.append(_Op(a0, a0 + 0.75, 1.62, 3.15, kind="blocks", recess=0.12))
        ops.append(_Op(a0 + 0.05, a0 + 0.7, *UP, recess=0.12))
    B.wall(_along_x(BAY_DEPTH, -1), _rect(x0, x1, 0.0, WALL_TOP), wall, ops)
    cheek = _rect(0.0, BAY_DEPTH, 0.0, WALL_TOP)
    B.wall(_along_y(x0, 1), cheek, wall)
    B.wall(_along_y(x1, -1), cheek, wall)
    xm = (x0 + x1) / 2
    door = _Op(xm - 0.4, xm + 0.4, 0.0, PORCH_TOP - 0.06, kind="hatch", recess=0.1, frame=PORCH_DOOR)
    B.wall(_along_x(0.0, -1), _rect(x0, x1, 0.0, PORCH_TOP), wall, [door])
    z_back, z_front = 1.6, 1.36
    B.beam((xm, -PORCH_OUT, z_front), (xm, BAY_DEPTH, z_back), x1 - x0, 0.04, hex_to_rgb(CANOPY))
    if not B.detail:
        return
    back = _along_x(BAY_DEPTH, -1)
    B.wbox(_along_x(0.1, -1), xm - 0.1, xm + 0.1, 0.01, 0.95, 1.1, hex_to_rgb(PLATE))  # house number
    for a0 in (x0 + 0.2, x1 - 0.95):  # concrete lintels over the glass blocks
        B.wbox(back, a0 - 0.08, a0 + 0.83, 0.03, 3.15, 3.3, hex_to_rgb(LINTEL))


def _rear(B: _House) -> None:
    wall, plinth = hex_to_rgb(WALL), hex_to_rgb(PLINTH_C)
    rear, foot = _along_x(D, 1), _along_x(D + PLINTH_OUT, 1)
    d0, d1 = REAR_DOOR
    xm = (d0 + d1) / 2
    ops = [_Op(a0, a1, *zz, panes=2, recess=0.2) for a0, a1 in REAR_WINDOWS for zz in (LO, UP)]
    ops.append(_Op(xm - 0.5, xm + 0.5, *STAIR_LIGHT, kind="blocks", recess=0.25))
    B.wall(rear, _rect(0.0, L, PLINTH, WALL_TOP), wall, ops, cuts=[(d0, 0.0, d1, DOOR_HEAD)])
    for a0, a1 in ((-PLINTH_OUT, d0), (d1, L + PLINTH_OUT)):
        B.wall(foot, _rect(a0, a1, 0.0, PLINTH), plinth, _hatches(HATCHES_REAR, a0, a1))
        B.face([(a0, D, PLINTH), (a1, D, PLINTH), (a1, D + PLINTH_OUT, PLINTH), (a0, D + PLINTH_OUT, PLINTH)],
               plinth, (0, 0, 1))  # fmt: skip
    for x, out in ((d0, 1), (d1, -1)):  # ends of the plinth at the door recess
        B.face([(x, D, 0), (x, D + PLINTH_OUT, 0), (x, D + PLINTH_OUT, PLINTH), (x, D, PLINTH)],
               plinth, (out, 0, 0))  # fmt: skip
    # the door recess: side walls, ceiling, a low step, the door a little towards the high-x side
    yb = D - DOOR_DEPTH
    side = _rect(yb, D, 0.0, DOOR_HEAD)
    B.wall(_along_y(d0, 1), side, wall)
    B.wall(_along_y(d1, -1), side, wall)
    B.face([(d0, yb, DOOR_HEAD), (d1, yb, DOOR_HEAD), (d1, D, DOOR_HEAD), (d0, D, DOOR_HEAD)], wall, (0, 0, -1))
    B.box(d0, d1, yb, D, 0.0, 0.1, plinth, skip="-x +x -y -z")
    door = _Op(d1 - 1.4, d1 - 0.5, 0.1, 2.0, kind="wood", recess=0.08, frame=WOOD_DOOR)
    B.wall(_along_x(yb, 1), _rect(d0, d1, 0.1, DOOR_HEAD), wall, [door])
    if B.detail:
        for a0, a1 in REAR_WINDOWS[:2]:  # vent caps under the upper windows of the low-x half
            B.wbox(rear, (a0 + a1) / 2 - 0.07, (a0 + a1) / 2 + 0.07, 0.03, 3.93, 4.07, hex_to_rgb(VENT))


def _gables(B: _House) -> None:
    wall, plinth = hex_to_rgb(WALL), hex_to_rgb(PLINTH_C)
    for x, out in ((0.0, -1), (L, 1)):
        B.wall(_along_y(x, out), _rect(0.0, D, PLINTH, WALL_TOP), wall)
        xo = x + out * PLINTH_OUT
        B.wall(_along_y(xo, out), _rect(-PLINTH_OUT, D + PLINTH_OUT, 0.0, PLINTH), plinth)
        B.face([(x, 0, PLINTH), (xo, 0, PLINTH), (xo, D, PLINTH), (x, D, PLINTH)], plinth, (0, 0, 1))


def _outline() -> Polygon:
    x0, x1 = BAY
    return Polygon([(0, 0), (x0, 0), (x0, BAY_DEPTH), (x1, BAY_DEPTH), (x1, 0), (L, 0), (L, D), (0, D)])


def _roof(B: _House) -> None:
    roof, fascia = hex_to_rgb(ROOF), hex_to_rgb(FASCIA)
    foot = _outline()
    slab = orient(foot.buffer(ROOF_OUT, join_style=2), 1.0)
    z0, z1 = WALL_TOP, WALL_TOP + ROOF_T
    B.region(slab, lambda x, y: (x, y, z1), roof, (0, 0, 1))
    B.region(slab.difference(foot), lambda x, y: (x, y, z0), fascia, (0, 0, -1))
    ring = list(slab.exterior.coords)[:-1]
    for k, (ax, ay) in enumerate(ring):
        bx, by = ring[(k + 1) % len(ring)]
        B.face([(ax, ay, z0), (bx, by, z0), (bx, by, z1), (ax, ay, z1)], fascia, (by - ay, ax - bx, 0))
    if not B.detail:
        return
    gutter = hex_to_rgb(GUTTER)
    yf, yr = -ROOF_OUT, D + ROOF_OUT
    for (x0, x1), *_ in BLOCKS:  # a gutter along each block's street eave and along the rear eave
        a0, a1 = (x0 - ROOF_OUT, x1) if x0 == 0 else (x0, x1 + ROOF_OUT)
        B.box(a0, a1, yf - 0.1, yf, z0 - 0.02, z0 + 0.08, gutter)
    B.box(-ROOF_OUT, L + ROOF_OUT, yr, yr + 0.1, z0 - 0.02, z0 + 0.08, gutter)
    pipes = (
        (0.28, yf - 0.05, -0.07, PIPE_COPPER), (L - 0.22, yf - 0.05, -0.07, GUTTER),
        (0.2, yr + 0.05, D + 0.07, PIPE_RUST), (L - 0.2, yr + 0.05, D + 0.07, PIPE_DARK),
    )  # fmt: skip
    for x, y_gutter, y_wall, colour in pipes:  # downpipes at the four corners
        c = hex_to_rgb(colour)
        B.beam((x, y_gutter, z0 - 0.02), (x, y_wall, z0 - 0.35), 0.09, 0.09, c)
        B.beam((x, y_wall, z0 - 0.32), (x, y_wall, PLINTH + 0.05), 0.09, 0.09, c)
        y_foot = y_wall - math.copysign(PLINTH_OUT, D / 2 - y_wall)
        B.beam((x, y_foot, PLINTH + 0.08), (x, y_foot, 0.05), 0.09, 0.09, c)
    chimney = hex_to_rgb(CHIMNEY)
    for cx, cy in ((0.7, 1.3), (2.8, 2.6), (11.1, 2.6)):
        B.box(cx - 0.22, cx + 0.22, cy - 0.22, cy + 0.22, z1, z1 + 0.6, chimney, skip="-z")


def _frame(b: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray, float, float]:
    """Map position of the local origin, unit vectors of local x and y, and the real length and depth.

    They come from the footprint's bounding rectangle; the street front is the long side whose outward
    normal is nearest to the bearing ``model_axis``.
    """
    rect = orient(footprint_polygon(b["footprint"]).minimum_rotated_rectangle, 1.0)
    c = [np.array(p, float) for p in list(rect.exterior.coords)[:4]]
    if np.linalg.norm(c[1] - c[0]) < np.linalg.norm(c[2] - c[1]):
        c = c[1:] + c[:1]
    a = float(b.get("model_axis") or 0.0)
    d = (c[1] - c[0]) / np.linalg.norm(c[1] - c[0])
    if np.array([d[1], -d[0]]) @ np.array([math.cos(a), math.sin(a)]) < 0:
        c = c[2:] + c[:2]
    length, depth = float(np.linalg.norm(c[1] - c[0])), float(np.linalg.norm(c[3] - c[0]))
    return c[0], (c[1] - c[0]) / length, (c[3] - c[0]) / depth, length, depth


def loggia_house_mesh(b: dict, detail: bool = True) -> Mesh:
    """The house on the footprint of ``b`` (map metres); ``detail=False`` gives the lighter collision mesh."""
    B = _House(detail)
    _front(B)
    _bay(B)
    _rear(B)
    _gables(B)
    _roof(B)
    m = B.mesh()
    origin, ex, ey, length, depth = _frame(b)
    x, y = m.tris[..., 0] * (length / L), m.tris[..., 1]
    # depth: the front and the rear keep their size, the middle takes up the difference
    y0, y1 = RIGID_FRONT, D - RIGID_REAR
    mid = y0 + (y - y0) * (depth - RIGID_FRONT - RIGID_REAR) / (y1 - y0)
    y = np.where(y <= y0, y, np.where(y >= y1, y + depth - D, mid))
    out = m.tris.copy()
    out[..., 0:2] = origin + x[..., None] * ex + y[..., None] * ey
    return Mesh(out, m.colours)
