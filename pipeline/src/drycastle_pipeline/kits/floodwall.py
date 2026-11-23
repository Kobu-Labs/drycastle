"""The flood wall: the concrete wall between the river and the village, as a kit of pieces.

The wall is a stepped concrete profile: a thick base with a narrow toe step, a ledge on the village
side that is the walkway, a thinner upper wall along the river side of the ledge and a pale blue
pipe railing on top of it. The pieces (``PIECES``, placeable as ``prim:floodwall_*``, category
``wall``) all share that profile and butt end to end:

* ``floodwall_straight_4`` / ``_2``: a run of wall, 4 m and 2 m long;
* ``floodwall_joint``: a pier that stands on a joint between two pieces. It has no length of its
  own; at a bend (up to ``JOINT_MAX_BEND_DEG``) it is turned to the bisector, where it hides the
  meeting upper walls and fills the wedge that opens in the ledge on the outside of the bend;
* ``floodwall_access``: 4 m of wall with a pedestrian opening between two piers (the stop-log
  slots are in their inner faces), a ladder from the ground, the flood plaque, and a landing with
  railings and a second ladder on the river side;
* ``floodwall_gate``: the road gate, two thick piers and a sill, 4 m clear, 6 m long;
* ``floodwall_gate_pier``: one such pier, to end a wall or to build a gate of another width.

Frame: local metres, z up, ``x`` along the wall. **``-y`` is the village side (the ledge), ``+y``
the river.** The origin is on the ground under the centre line of the upper wall, in the middle
of the piece's length, so a run is placed along the line the wall stands on. The base goes
``-FOOT_Z`` metres into the ground, so a piece also sits on uneven ground.

Collision: a piece says which boxes of it are solid (``Piece.solids``, the ``solids`` of its
``assets/manifest.json`` entry), because one box around the whole piece would bury the walkway.
The ledge, the toe, the upper wall, the piers, the landing and the rails are solid; the ladders
are not (the game has no ladder climbing: the ledge is ``LEDGE_Z`` above the ground, which a jump
reaches).

Faces are flat colours. Long vertical faces are cut into formwork panels of ``PANEL_M`` with a
slightly different tone each and a few dark runs under the edges; horizontal faces have one tone,
so that pieces overlapping at a bend do not flicker. Nothing is random: the tones come from a hash
of the piece name and the panel.
"""

import math
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.poleparts import UP, Kit, rgb, rot_z, shade, vec
from drycastle_pipeline.poles import size_of

# The profile, metres. y across the wall (village side negative), z above the ground.
WALL_HALF_M = 0.15  # half the thickness of the upper wall
BACK_Y = WALL_HALF_M  # the river face, base and upper wall alike
LEDGE_Y = -1.15  # front edge of the ledge: the walkway is 1 m wide
TOE_Y = -1.33  # front of the toe step and of the base
FOOT_Z = -1.0  # how deep the base goes
TOE_Z = 0.62
LEDGE_Z = 0.90  # the walkway; below the 1.1 m a player jumps
TOP_Z = 1.80  # top of the upper wall
RAIL_Z = TOP_Z + 0.45  # axis of the rail
PIER_TOP_Z = 1.95
PIER_FRONT_Y = -0.40  # a pier stands 0.25 m onto the ledge, leaving 0.75 m to walk past
PIER_BACK_Y = 0.25
GATE_PIER_Y = (-1.38, 0.20)  # a gate pier is deeper than the whole profile
GATE_PIER_M = 1.0  # its length along the wall
GATE_CLEAR_M = 4.0
DAMP_Z = 0.22  # the foot of the wall is damp and a little green up to here
PANEL_M = 2.0
JOINT_MAX_BEND_DEG = 25.0  # the sharpest bend one joint covers (its solid floor is 0.6 m wide)

WHITE = "#ecebe4"  # limewashed faces
WHITE_DIRTY = "#dedcd2"
KERB = "#cfcdc3"
TOP = "#a19f96"  # weathered horizontal concrete: ledge, toe, coping
RAW = "#bab8ae"  # the piers are bare concrete
RAW_TOP = "#a09e94"
DAMP = "#a9ad98"
UNDER = "#8a887f"
SLOT = "#6e6d67"
RAIL = "#9cbcd2"
LADDER = "#7fa9c8"
PLAQUE = "#55585a"
LETTER = "#c9cbc8"

SOURCE = ("https://github.com/Kobu-Labs/drycastle (generated flood wall, "
          "pipeline/src/drycastle_pipeline/floodwall.py)")

_X = vec(1.0, 0.0, 0.0)
_Y = vec(0.0, 1.0, 0.0)

# 5 x 7 dot letters of the plaque, rows from the top.
_FONT = {
    "P": ("11110", "10001", "10001", "11110", "10000", "10000", "10000"),
    "O": ("01110", "10001", "10001", "10001", "10001", "10001", "01110"),
    "V": ("10001", "10001", "10001", "10001", "10001", "01010", "00100"),
    "D": ("11110", "10001", "10001", "10001", "10001", "10001", "11110"),
    "E": ("11111", "10000", "10000", "11110", "10000", "10000", "11111"),
    "N": ("10001", "11001", "10101", "10011", "10001", "10001", "10001"),
    "4": ("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
    "2": ("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
    "0": ("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
    "6": ("00110", "01000", "10000", "11110", "10001", "10001", "01110"),
    ".": ("0", "0", "0", "0", "0", "0", "1"),
}
_CARON = ("01010", "00100")  # over the N of POVODEŇ
PLAQUE_LINES = ("POVODEŇ", "4.4.2006")  # "flood", and the day the river came


@dataclass(frozen=True)
class Solid:
    """A solid box of a piece, in the piece's frame."""

    lo: tuple[float, float, float]
    hi: tuple[float, float, float]
    surface: str = "wall"  # "wall" (concrete) or "metal" (a rail)

    def to_dict(self) -> dict:
        return {"min": [round(v, 3) for v in self.lo], "max": [round(v, 3) for v in self.hi],
                "surface": self.surface}

    def contains(self, p, tolerance: float = 0.0) -> bool:
        return all(a - tolerance <= v <= b + tolerance for v, a, b in zip(p, self.lo, self.hi, strict=True))


@dataclass
class Piece:
    mesh: Mesh
    solids: list[Solid]
    length_m: float  # how far the wall runs along x through this piece (0: it stands on a joint)
    colour: str = TOP  # a colour the mesh certainly has (the primitive's listed colour)

    @property
    def size_m(self) -> tuple[float, float, float]:
        return size_of(self.mesh)


def _hash(*keys) -> float:
    """A number in [0, 1) from the keys, the same on every run and machine."""
    s = 2166136261
    for key in keys:
        for ch in str(key).encode() + b"/":
            s = ((s ^ ch) * 16777619) & 0xFFFFFFFF
    s ^= s >> 15
    s = (s * 0x2C1B3C6D) & 0xFFFFFFFF
    s ^= s >> 12
    return s / 2**32


@dataclass
class _Builder:
    """A kit plus the solids, for one piece ``length`` metres long (panels count from its -x end)."""

    name: str
    length: float
    kit: Kit = field(default_factory=Kit)
    solids: list[Solid] = field(default_factory=list)

    # ---- faces

    def quad(self, pts, normal, colour) -> None:
        """A quad through ``pts`` (in order round it), turned to face along ``normal``."""
        a, b, c, d = (np.asarray(p, float) for p in pts)
        if float(np.cross(b - a, c - a) @ np.asarray(normal, float)) < 0:
            a, b, c, d = d, c, b, a
        col = rgb(colour)
        self.kit.tris += [np.array([a, b, c]), np.array([a, c, d])]
        self.kit.cols += [col, col]

    def tri(self, pts, normal, colour) -> None:
        a, b, c = (np.asarray(p, float) for p in pts)
        if float(np.cross(b - a, c - a) @ np.asarray(normal, float)) < 0:
            b, c = c, b
        self.kit.tris.append(np.array([a, b, c]))
        self.kit.cols.append(rgb(colour))

    def _panels(self, x0: float, x1: float):
        """``(index, from, to)`` of the formwork panels between x0 and x1."""
        origin = -self.length / 2
        x = x0
        while x < x1 - 1e-9:
            i = math.floor((x - origin) / PANEL_M + 1e-9)
            nxt = min(x1, origin + (i + 1) * PANEL_M)
            yield i, x, nxt
            x = nxt

    def _tone(self, key: str, i: int) -> int:
        return round(-6 + 10 * _hash(self.name, key, i))

    def _runs(self, key: str, i: int, a: float, b: float, height: float):
        """Dark runs under the top edge of a panel: ``(from, to, how far down, how much darker)``."""
        if b - a < 0.4:
            return []
        out, last = [], a + 0.02
        cuts = []
        for j in range(int(_hash(self.name, key, i, "n") * 3)):
            c = a + (b - a) * (0.12 + 0.76 * _hash(self.name, key, i, j, "at"))
            w = 0.05 + 0.09 * _hash(self.name, key, i, j, "w")
            cuts.append((c - w / 2, c + w / 2, height * (0.2 + 0.45 * _hash(self.name, key, i, j, "d")),
                         -(8 + round(10 * _hash(self.name, key, i, j, "t")))))
        for s0, s1, depth, delta in sorted(cuts):
            if s0 > last and s1 < b - 0.02:
                out.append((s0, s1, depth, delta))
                last = s1
        return out

    def side(self, x0, x1, y, z0, z1, out: int, colour, key: str, runs: bool = False) -> None:
        """A vertical face along the wall at ``y``, facing ``out`` (+1 the river, -1 the village)."""
        n = _Y * out
        for i, a, b in self._panels(x0, x1):
            col = shade(colour, self._tone(key, i))

            def strip(s0, s1, lo, hi, c):
                self.quad([vec(s0, y, lo), vec(s1, y, lo), vec(s1, y, hi), vec(s0, y, hi)], n, c)

            x = a
            for s0, s1, depth, delta in (self._runs(key, i, a, b, z1 - z0) if runs else []):
                strip(x, s0, z0, z1, col)
                strip(s0, s1, z1 - depth, z1, shade(col, delta))
                strip(s0, s1, z0, z1 - depth, col)
                x = s1
            strip(x, b, z0, z1, col)

    def flat(self, x0, x1, y0, y1, z, colour, up: bool = True) -> None:
        """A horizontal face; one tone, so overlapping pieces show no seam."""
        self.quad([vec(x0, y0, z), vec(x1, y0, z), vec(x1, y1, z), vec(x0, y1, z)], UP if up else -UP, colour)

    def end(self, x, y0, y1, z0, z1, out: int, colour) -> None:
        """A face across the wall at ``x``, facing ``out`` along x."""
        self.quad([vec(x, y0, z0), vec(x, y1, z0), vec(x, y1, z1), vec(x, y0, z1)], _X * out, colour)

    def solid(self, x0, y0, z0, x1, y1, z1, surface: str = "wall") -> None:
        self.solids.append(Solid((x0, y0, z0), (x1, y1, z1), surface))

    # ---- the profile

    def base(self, x0: float, x1: float) -> None:
        """Everything up to the ledge: the base with its toe step and the walkway on top."""
        self.flat(x0, x1, TOE_Y, BACK_Y, FOOT_Z, UNDER, up=False)
        self.side(x0, x1, BACK_Y, FOOT_Z, 0.25, 1, DAMP, "river_foot")
        self.side(x0, x1, BACK_Y, 0.25, LEDGE_Z, 1, WHITE, "river")
        self.flat(x0, x1, LEDGE_Y, -WALL_HALF_M, LEDGE_Z, TOP)
        self.side(x0, x1, LEDGE_Y, TOE_Z, LEDGE_Z, -1, KERB, "kerb")
        self.flat(x0, x1, TOE_Y, LEDGE_Y, TOE_Z, TOP)
        self.side(x0, x1, TOE_Y, DAMP_Z, TOE_Z, -1, WHITE_DIRTY, "front", runs=True)
        self.side(x0, x1, TOE_Y, FOOT_Z, DAMP_Z, -1, DAMP, "front_foot")
        for x, out in ((x0, -1), (x1, 1)):
            self.end(x, TOE_Y, BACK_Y, FOOT_Z, TOE_Z, out, RAW)
            self.end(x, LEDGE_Y, BACK_Y, TOE_Z, LEDGE_Z, out, RAW)
        self.solid(x0, LEDGE_Y, FOOT_Z, x1, BACK_Y, LEDGE_Z)
        self.solid(x0, TOE_Y, FOOT_Z, x1, LEDGE_Y, TOE_Z)

    def floor(self, x0: float, x1: float) -> None:
        """The ledge carried through under where the upper wall would stand (an opening)."""
        self.flat(x0, x1, -WALL_HALF_M, BACK_Y, LEDGE_Z, TOP)

    def upper(self, x0: float, x1: float) -> None:
        """The upper wall on the river side of the ledge."""
        self.side(x0, x1, BACK_Y, LEDGE_Z, TOP_Z, 1, WHITE, "river", runs=True)
        self.side(x0, x1, -WALL_HALF_M, LEDGE_Z, TOP_Z, -1, WHITE, "wall", runs=True)
        self.flat(x0, x1, -WALL_HALF_M, WALL_HALF_M, TOP_Z, TOP)
        for x, out in ((x0, -1), (x1, 1)):
            self.end(x, -WALL_HALF_M, WALL_HALF_M, LEDGE_Z, TOP_Z, out, RAW)
        self.solid(x0, -WALL_HALF_M, LEDGE_Z, x1, WALL_HALF_M, TOP_Z)

    def rail(self, x0: float, x1: float, posts: list[float]) -> None:
        """The pipe railing on top of the upper wall: one rail on posts with foot plates."""
        self.kit.tube(vec(x0, 0, RAIL_Z), vec(x1, 0, RAIL_Z), 0.024, RAIL, sides=6)
        for x in posts:
            self.kit.tube(vec(x, 0, TOP_Z), vec(x, 0, RAIL_Z), 0.02, RAIL, sides=6)
            self.kit.box(vec(x - 0.05, -0.04, TOP_Z), vec(x + 0.05, 0.04, TOP_Z + 0.012), shade(RAIL, -18))
        self.solid(x0, -0.03, RAIL_Z - 0.03, x1, 0.03, RAIL_Z + 0.03, "metal")

    def block(self, x0, x1, y0, y1, z0, z1, key: str, slots: tuple[bool, bool] = (False, False),
              solid: bool = True) -> None:
        """A bare concrete pier. ``slots``: a stop-log slot down its -x / +x face, on the wall line."""
        self.side(x0, x1, y0, z0, z1, -1, RAW, key + "_front", runs=True)
        self.side(x0, x1, y1, z0, z1, 1, RAW, key + "_back", runs=True)
        self.flat(x0, x1, y0, y1, z1, RAW_TOP)
        self.flat(x0, x1, y0, y1, z0, UNDER, up=False)
        for x, out, slot in ((x0, -1, slots[0]), (x1, 1, slots[1])):
            col = shade(RAW, self._tone(key + "_end", out))
            if slot and y0 < -0.06 and y1 > 0.06:
                self.end(x, y0, -0.06, z0, z1, out, col)
                self.end(x, -0.06, 0.06, z0, z1, out, SLOT)
                self.end(x, 0.06, y1, z0, z1, out, col)
            else:
                self.end(x, y0, y1, z0, z1, out, col)
        if solid:
            self.solid(x0, y0, z0, x1, y1, z1)

    def plinth(self, x0, x1, y0, y1, z1, key: str) -> None:
        """A limewashed block from the foot up to ``z1`` with a walked-on top (the landing).
        Its ``y0`` face is against the wall and is left out."""
        self.flat(x0, x1, y0, y1, z1, TOP)
        self.flat(x0, x1, y0, y1, FOOT_Z, UNDER, up=False)
        self.side(x0, x1, y1, FOOT_Z, DAMP_Z, 1, DAMP, key + "_foot")
        self.side(x0, x1, y1, DAMP_Z, z1, 1, WHITE, key, runs=True)
        for x, out in ((x0, -1), (x1, 1)):
            self.end(x, y0, y1, FOOT_Z, DAMP_Z, out, DAMP)
            self.end(x, y0, y1, DAMP_Z, z1, out, shade(WHITE, self._tone(key + "_end", out)))
        self.solid(x0, y0, FOOT_Z, x1, y1, z1)

    def drum(self) -> None:
        """The base profile turned round the origin, a quarter turn of it on the village side: what
        shows in the wedge between two pieces that meet at an angle with the ledge on the outside.
        Where the pieces overlap instead (the ledge on the inside) all of it is inside them: a point
        of it is never further from a piece's centre line than that piece's own toe."""
        n = 6
        angles = [math.radians(225 + 90 * i / n) for i in range(n + 1)]

        def p(r: float, a: float, z: float) -> np.ndarray:
            return vec(r * math.cos(a), r * math.sin(a), z)

        r_toe, r_ledge = -TOE_Y, -LEDGE_Y
        for i in range(n):
            a, c = angles[i], angles[i + 1]
            out = p(1.0, (a + c) / 2, 0.0)
            for z0, z1, r, col in ((FOOT_Z, DAMP_Z, r_toe, DAMP),
                                   (DAMP_Z, TOE_Z, r_toe, shade(WHITE_DIRTY, self._tone("drum", i))),
                                   (TOE_Z, LEDGE_Z, r_ledge, KERB)):
                self.quad([p(r, a, z0), p(r, c, z0), p(r, c, z1), p(r, a, z1)], out, col)
            self.quad([p(r_toe, a, TOE_Z), p(r_toe, c, TOE_Z), p(r_ledge, c, TOE_Z), p(r_ledge, a, TOE_Z)], UP, TOP)
            self.tri([vec(0, 0, LEDGE_Z), p(r_ledge, a, LEDGE_Z), p(r_ledge, c, LEDGE_Z)], UP, TOP)
            self.tri([vec(0, 0, FOOT_Z), p(r_toe, a, FOOT_Z), p(r_toe, c, FOOT_Z)], -UP, UNDER)
        for a, sign in ((angles[0], -1), (angles[-1], 1)):  # the two cut faces, inside the pieces
            out = sign * vec(-math.sin(a), math.cos(a), 0.0)
            self.quad([vec(0, 0, FOOT_Z), p(r_toe, a, FOOT_Z), p(r_toe, a, TOE_Z), vec(0, 0, TOE_Z)], out, RAW)
            self.quad([vec(0, 0, TOE_Z), p(r_ledge, a, TOE_Z), p(r_ledge, a, LEDGE_Z), vec(0, 0, LEDGE_Z)], out, RAW)

    # ---- fittings

    def ladder(self, x: float, foot_y: float, edge_y: float, toward: int, top_z: float = LEDGE_Z) -> None:
        """A steel ladder from the ground to the edge of a deck at ``top_z``: two stiles 0.44 m
        apart with rungs, the stiles carried up as grab hoops that come back down onto the deck.
        It stands at ``foot_y`` and reaches the deck at ``edge_y``; ``toward`` is the y direction
        of the deck seen from the ladder. Decorative: it has no collision."""
        hoop = 1.0
        foot, edge = vec(0, foot_y, -0.05), vec(0, edge_y, top_z)
        back = edge_y + toward * 0.26
        path = [foot, edge, vec(0, edge_y, top_z + hoop - 0.08),
                vec(0, edge_y + toward * 0.02, top_z + hoop - 0.02), vec(0, edge_y + toward * 0.07, top_z + hoop),
                vec(0, back - toward * 0.07, top_z + hoop), vec(0, back - toward * 0.02, top_z + hoop - 0.02),
                vec(0, back, top_z + hoop - 0.08), vec(0, back, top_z)]
        for dx in (-0.22, 0.22):
            self.kit.sweep([q + vec(x + dx, 0, 0) for q in path], 0.02, LADDER, sides=5)
        rise = 0.27
        z = top_z - rise
        while z > 0.05:
            at = foot + (edge - foot) * ((z - foot[2]) / (edge[2] - foot[2]))
            self.kit.tube(at + vec(x - 0.22, 0, 0), at + vec(x + 0.22, 0, 0), 0.014, LADDER, sides=5)
            z -= rise

    def frame(self, x: float, y0: float, y1: float, z: float) -> None:
        """A railing panel across the wall line at ``x``: two posts, a top rail and two bars."""
        height = 1.0
        for y in (y0, y1):
            self.kit.tube(vec(x, y, z), vec(x, y, z + height), 0.02, RAIL, sides=6)
        for dz in (height / 3, 2 * height / 3, height):
            self.kit.tube(vec(x, y0, z + dz), vec(x, y1, z + dz), 0.02 if dz == height else 0.016, RAIL, sides=6)
            self.solid(x - 0.03, y0, z + dz - 0.03, x + 0.03, y1, z + dz + 0.03, "metal")

    def plaque(self, x: float, y: float, z: float, width: float = 0.46, height: float = 0.30) -> None:
        """The flood plaque on a face looking at the village: a dark plate with the dot letters of
        ``PLAQUE_LINES``, centred on (x, z), standing off the face at ``y``. The letters are cells
        of the plate's own front face, not something laid over it, so nothing can flicker."""
        dot = 0.0095
        lines = []  # per line: the rows of dots, two rows taller where a caron stands on a letter
        for text in PLAQUE_LINES:
            rows = [""] * 9
            for n, ch in enumerate(text):
                glyph = (*_CARON, *_FONT["N"]) if ch == "Ň" else ("0" * len(_FONT[ch][0]),) * 2 + _FONT[ch]
                rows = [row + ("0" if n else "") + bits for row, bits in zip(rows, glyph, strict=True)]
            lines.append(rows)
        cols = max(len(rows[0]) for rows in lines) + 2
        grid = []
        for n, rows in enumerate(lines):
            pad = (cols - len(rows[0])) // 2
            grid += ["0" * cols] * (2 if n else 0)  # between the lines
            grid += ["0" * pad + row + "0" * (cols - pad - len(row)) for row in rows]
        front = y - 0.012
        x0, x1, z0, z1 = x - width / 2, x + width / 2, z - height / 2, z + height / 2
        gx0, gz1 = x - cols * dot / 2, z + len(grid) * dot / 2
        gx1, gz0 = gx0 + cols * dot, gz1 - len(grid) * dot

        def cell(a, b, lo, hi, colour):
            self.quad([vec(a, front, lo), vec(b, front, lo), vec(b, front, hi), vec(a, front, hi)], -_Y, colour)

        cell(x0, x1, gz1, z1, PLAQUE)  # the margins round the lettering
        cell(x0, x1, z0, gz0, PLAQUE)
        cell(x0, gx0, gz0, gz1, PLAQUE)
        cell(gx1, x1, gz0, gz1, PLAQUE)
        for r, row in enumerate(grid):
            c = 0
            while c < cols:
                c0 = c
                while c < cols and row[c] == row[c0]:
                    c += 1
                cell(gx0 + c0 * dot, gx0 + c * dot, gz1 - (r + 1) * dot, gz1 - r * dot,
                     LETTER if row[c0] == "1" else PLAQUE)
        edge = shade(PLAQUE, -14)
        self.quad([vec(x0, front, z1), vec(x1, front, z1), vec(x1, y, z1), vec(x0, y, z1)], UP, edge)
        self.quad([vec(x0, front, z0), vec(x1, front, z0), vec(x1, y, z0), vec(x0, y, z0)], -UP, edge)
        for xe, out in ((x0, -1), (x1, 1)):
            self.quad([vec(xe, front, z0), vec(xe, y, z0), vec(xe, y, z1), vec(xe, front, z1)], _X * out, edge)

    def piece(self) -> Piece:
        return Piece(self.kit.mesh(), self.solids, self.length)


def _straight(name: str, length: float) -> Piece:
    b = _Builder(name, length)
    h = length / 2
    b.base(-h, h)
    b.upper(-h, h)
    # posts 2 m apart, 1 m in from each end, so the spacing carries on over a joint
    b.rail(-h, h, [-h + 1.0 + 2.0 * i for i in range(round(length / 2))])
    return b.piece()


def _joint(name: str) -> Piece:
    b = _Builder(name, 0.6)
    b.drum()
    b.block(-0.3, 0.3, PIER_FRONT_Y, PIER_BACK_Y, FOOT_Z, PIER_TOP_Z, "pier")
    # the walkway through the wedge: as wide as the gap gets at a bend of JOINT_MAX_BEND_DEG, and
    # held back a little from the edges so that its corners stay inside pieces that overlap
    b.solid(-0.3, LEDGE_Y + 0.04, FOOT_Z, 0.3, PIER_FRONT_Y, LEDGE_Z)
    b.solid(-0.3, TOE_Y + 0.04, FOOT_Z, 0.3, LEDGE_Y + 0.04, TOE_Z)
    piece = b.piece()
    piece.length_m = 0.0
    return piece


def _access(name: str) -> Piece:
    b = _Builder(name, 4.0)
    b.base(-2.0, 2.0)
    b.floor(-1.4, 1.4)
    for sign in (-1, 1):
        x0, x1 = sorted((sign * 2.0, sign * 1.4))
        b.upper(x0, x1)
        b.rail(*sorted((sign * 2.0, sign * 1.45)), [sign * 1.5])
        p0, p1 = sorted((sign * 1.4, sign * 0.6))
        b.block(p0, p1, PIER_FRONT_Y, PIER_BACK_Y, FOOT_Z, PIER_TOP_Z, f"pier{sign}", slots=(sign > 0, sign < 0))
    b.plaque(-1.0, PIER_FRONT_Y, 1.45)
    # village side: the ladder leans on the toe and the edge of the ledge
    b.ladder(0.0, TOE_Y - 0.33, LEDGE_Y - 0.06, 1)
    # river side: a landing at the height of the ledge, railed at its ends, and a ladder down
    land = BACK_Y + 1.0
    b.plinth(-1.0, 1.0, BACK_Y, land, LEDGE_Z, "landing")
    for x in (-0.96, 0.96):
        b.frame(x, PIER_BACK_Y + 0.06, land - 0.05, LEDGE_Z)
    b.ladder(0.0, land + 0.30, land + 0.06, -1)
    return b.piece()


def _gate_pier(b: _Builder, x0: float, x1: float, key: str, slots=(True, True)) -> None:
    b.block(x0, x1, GATE_PIER_Y[0], GATE_PIER_Y[1], FOOT_Z, PIER_TOP_Z, key, slots=slots)


def _gate(name: str) -> Piece:
    half = GATE_CLEAR_M / 2
    b = _Builder(name, GATE_CLEAR_M + 2 * GATE_PIER_M)
    _gate_pier(b, -half - GATE_PIER_M, -half, "left")
    _gate_pier(b, half, half + GATE_PIER_M, "right")
    # the sill between them, with the groove the stop logs stand in
    y0, y1, z0, z1 = -0.75, 0.45, -0.4, 0.05
    b.flat(-half, half, y0, -0.05, z1, TOP)
    b.flat(-half, half, -0.05, 0.05, z1, SLOT)
    b.flat(-half, half, 0.05, y1, z1, TOP)
    b.flat(-half, half, y0, y1, z0, UNDER, up=False)
    b.side(-half, half, y0, z0, z1, -1, RAW, "sill")
    b.side(-half, half, y1, z0, z1, 1, RAW, "sill")
    for x, out in ((-half, -1), (half, 1)):
        b.end(x, y0, y1, z0, z1, out, RAW)
    b.solid(-half, y0, z0, half, y1, z1)
    return b.piece()


def _single_gate_pier(name: str) -> Piece:
    b = _Builder(name, GATE_PIER_M)
    _gate_pier(b, -GATE_PIER_M / 2, GATE_PIER_M / 2, "pier")
    piece = b.piece()
    piece.colour = RAW_TOP  # it has no walkway
    return piece


# prim name -> (builder, what it is)
PIECES: dict[str, tuple[Callable[[str], Piece], str]] = {
    "floodwall_straight_4": (lambda n: _straight(n, 4.0), "4 m of wall with the walkway and the railing"),
    "floodwall_straight_2": (lambda n: _straight(n, 2.0), "2 m of wall with the walkway and the railing"),
    "floodwall_joint": (_joint, "pier for a joint or a bend between two pieces"),
    "floodwall_access": (_access, "4 m with a pedestrian opening, ladders, a landing and the flood plaque"),
    "floodwall_gate": (_gate, "road gate, two piers and a sill, 4 m clear"),
    "floodwall_gate_pier": (_single_gate_pier, "gate pier, ends a wall"),
}


def build_piece(name: str) -> Piece:
    """The piece of ``prim:<name>``."""
    return PIECES[name][0](name)


def manifest_entries() -> list[dict]:
    """The ``assets/manifest.json`` records of the pieces."""
    out = []
    for name, (_build, what) in PIECES.items():
        piece = build_piece(name)
        out.append({
            "id": f"prim:{name}",
            "category": "wall",
            "name": f"Flood wall: {what}",
            "file": None,
            "size_m": list(piece.size_m),
            "tags": ["flood-wall", name.removeprefix("floodwall_"), "flat-colour", "primitive"],
            "licence": "CC0",
            "source": SOURCE,
            "pack": None,
            # how far the wall runs through the piece along its x axis (the step to the next piece)
            "length_m": piece.length_m,
            # the solid boxes, in the piece's frame (z up): the game collides with these, not with one box
            "solids": [s.to_dict() for s in piece.solids],
        })
    return out


# ----------------------------------------------------------------------------- laying a run


@dataclass(frozen=True)
class Placement:
    """Where a piece goes: the values of a placed object (``rotation_deg`` counter-clockwise from
    east, seen from above; the village side is to the right of the direction of travel)."""

    asset: str
    x: float
    y: float
    rotation_deg: float


def lay_run(names: list[str], start: tuple[float, float] = (0.0, 0.0), heading_deg: float = 0.0,
            bends: dict[int, float] | None = None) -> list[Placement]:
    """Pieces end to end from ``start`` along ``heading_deg``. ``bends[i]`` turns the wall by that
    many degrees (counter-clockwise) before piece ``i``, which must be a piece with a length. A
    ``floodwall_joint`` in the list stands on the joint it is at, turned to the bisector of the
    bend of the piece after it."""
    bends = bends or {}
    if any(abs(turn) > JOINT_MAX_BEND_DEG for turn in bends.values()):
        raise ValueError(f"a bend of more than {JOINT_MAX_BEND_DEG:g} degrees needs more than one joint")
    lengths = [build_piece(name).length_m for name in names]
    if any(lengths[i] == 0.0 for i in bends):
        raise ValueError("a bend belongs to the piece after the joint, not to the joint")
    x, y = start
    heading = heading_deg
    out = []
    for i, (name, length) in enumerate(zip(names, lengths, strict=True)):
        if length == 0.0:
            out.append(Placement(f"prim:{name}", round(x, 3), round(y, 3),
                                 round(heading + bends.get(i + 1, 0.0) / 2, 3)))
            continue
        heading += bends.get(i, 0.0)
        a = math.radians(heading)
        cx, cy = x + math.cos(a) * length / 2, y + math.sin(a) * length / 2
        out.append(Placement(f"prim:{name}", round(cx, 3), round(cy, 3), round(heading, 3)))
        x, y = x + math.cos(a) * length, y + math.sin(a) * length
    return out


def run_mesh(placements: list[Placement]) -> Mesh:
    """The pieces of a run as one mesh (for previews)."""
    meshes = []
    for p in placements:
        mesh = build_piece(p.asset.removeprefix("prim:")).mesh
        tris = mesh.tris @ rot_z(p.rotation_deg).T + np.array([p.x, p.y, 0.0])
        meshes.append(Mesh(tris, mesh.colours))
    return Mesh.concat(meshes)
