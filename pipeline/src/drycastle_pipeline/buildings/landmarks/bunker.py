"""Bunker model: the footprint extruded with a rounded concrete dome and pale weathering streaks.

``bunker_mesh(ring, door_dir)`` takes a footprint ring in any plane-metre frame and the direction the
door faces (a unit vector in that frame); it returns a :class:`Mesh` standing on z = 0 in the same frame.

Two forms:

- enterable (the ``bunker`` landmark, a building override): walls ``WALL_T`` thick around one room, an
  open doorway on the wall facing ``door_dir`` and firing slits that are real openings in up to four of
  the longest other walls. ``detail=False`` gives the collision mesh: the same shell (walls with their
  openings, ceiling, dome) without the floor, the door frame and the streaks. The floor is drawn
  ``FLOOR_Z`` above the flattened ground and has no collision: a player inside stands on the terrain.
- closed (the ``prim:bunker`` primitive, which collides as one box): a solid block with a door and
  slits painted on. Also what an enterable bunker falls back to when its footprint leaves no room.

Output is deterministic and flat-coloured like every other mesh.
"""

import math
from dataclasses import dataclass, replace
from itertools import pairwise

import numpy as np
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.geometry import bearing_deg
from drycastle_pipeline.core.mesh import Mesh, triangulate

CONCRETE, DOME, STREAK = "#a9a698", "#8d8e76", "#d8d5ca"
DOOR_FRAME, DOOR_LEAF, SLIT = "#6e4a35", "#2a2420", "#1b1b1b"
INNER, PAINT, CEILING, FLOOR = "#b4b1a4", "#5f4a33", "#9d9a8e", "#6a6860"
BOARD, TIMBER, BEAM, GAP = "#4d3a27", "#6e5236", "#54402b", "#2e2419"  # panelling boards, pillars, beams, the dark between planks
PLANKS = ("#7a6044", "#6e5539", "#836a4c", "#74593e")  # floor planks, in turn

WALL_H = 1.8  # vertical wall of the closed form, metres
RISE = 1.0  # dome height above the wall
RINGS = 4  # dome steps
DOME_END = 0.48  # scale of the flat cap relative to the footprint

WALL_T = 0.5  # wall thickness of the enterable form
FLOOR_Z = 0.04  # the drawn floor, just above the flattened ground
CLEAR_H = 2.1  # floor to ceiling; the player stands 1.8 m
ENTER_WALL_H = 2.3  # vertical wall of the enterable form: the room plus the roof slab
PAINT_H = 1.0  # top of the timber panelling round the room
DOOR_W, DOOR_H = 1.0, 2.0  # the doorway, always open; the player is 0.7 m wide
SLIT_W, SLIT_Z0, SLIT_Z1 = 0.5, 1.0, 1.25  # a firing slit, at the eye height of a crouched player
MIN_ROOM_M2 = 6.0  # a smaller room is not worth entering: the bunker stays closed
EDGE_MARGIN = 0.1  # an opening keeps this far from the ends of its inner wall
SHELTER_GROW = 0.15  # how far a room's shelter volume reaches into its walls, floor and roof
EPS = 1e-6
PILLAR = 0.16  # a timber pillar standing against the wall: its side
BEAM_W, BEAM_H = 0.18, 0.16  # ceiling beam (across the room) and wall plate (along the wall)
BEAM_STEP = 1.7  # a beam about every this far along the room
PLANK_W = 0.2  # floor planks
BOARD_W = 0.2  # wall panelling boards: every other one is darker

UNDER = 3  # floors below the ground floor, one room each under the same footprint
STOREY = 2.4  # floor to floor: CLEAR_H of room and a slab
SLAB = STOREY - CLEAR_H
HATCH = 1.0  # the square hole in a floor that the ladder goes through
HATCH_MARGIN = 0.05  # a hatch keeps this far from the walls of its corner
HATCH_DOOR_CLEAR = 1.8  # the ground floor hatch keeps this far from the doorway
HATCH_APART = 2.5  # the hatch of a floor is at least this far from the one above it
HATCH_SLIDE = 0.25  # steps in which a hatch is tried along a wall
RAIL_SET = 0.45  # the climber stands this far inside the hatch from the edge the ladder hangs on
# the bottom step-off is STEP_OFF past the rail on the same open side as the top (Ladders.StepOffBottom, #412)
STEP_OFF = 1.0  # at the top the player is put this far past the climbing spot, on the floor beyond the edge
STEEL, RUNG = "#4b5357", "#6b6f6c"
LAMP_CAGE, LAMP_GLOW = "#2f3133", "#f0c987"
LAMP_SIZE = 0.3  # a caged lamp hangs under the ceiling of every room
LANDING = 0.9  # props stay this far from the hatches (and from the ladder landings next to them)
PROP_GAP = 0.03  # between a prop and its wall
SCREEN_Z0, SCREEN_H = 0.85, 1.1  # the intel screen in the command post: its lower edge above the floor, and its height (the room is 2.1 m)
SCREEN_W_MIN, SCREEN_W_MAX = 1.6, 3.0  # the widest it hangs, the narrowest worth hanging
SCREEN_MARGIN = 0.25  # it keeps this far from the ends of its wall
SCREEN_ZONE = 0.9  # no furniture stands this deep in front of it (the deepest piece is 0.9)
SCREEN_STEP, SCREEN_SLIDE = 0.2, 0.1  # widths and positions tried
# POLYGON War props (private pack, glb/war/<model>.glb, recentred: origin in the middle of the footprint on the floor, front +Z),
# measured size (x, y, z) in metres at scale 1; the pack is not in the repo, so the sizes live here
PACK = {
    "prop_crate_01": (1.31, 0.33, 0.58), "prop_crate_02": (1.01, 0.90, 1.60), "prop_crate_03": (1.02, 1.02, 1.02),
    "prop_crate_04": (1.01, 1.01, 1.02), "prop_crate_pile_01": (2.40, 2.03, 2.45), "prop_camp_bed_01": (0.93, 0.55, 2.55),
    "prop_camp_bed_02": (0.93, 0.64, 2.55), "prop_dresser_01": (2.09, 1.27, 1.09), "prop_chair_01": (0.59, 1.02, 0.67),
    "prop_chair_02": (0.47, 1.26, 0.68), "prop_gramophone_01": (0.89, 1.29, 0.94), "prop_barrel_01": (0.76, 1.05, 0.76),
    "prop_barrel_02": (0.76, 1.05, 0.76), "prop_barrel_pile_01": (3.45, 1.26, 2.66), "prop_barrel_pile_02": (3.00, 1.05, 0.95),
    "prop_shelves_01": (1.50, 1.06, 0.25), "prop_shell_01": (0.16, 0.48, 0.17), "prop_shell_02": (0.94, 0.27, 1.09),
    "prop_table_01": (2.41, 0.82, 1.08), "prop_coffee_table_01": (0.80, 0.77, 0.80), "prop_table_small_01": (1.05, 0.79, 1.05),
    "prop_typewriter_01": (0.38, 0.33, 0.44), "prop_radio_box_01": (0.52, 0.30, 0.34), "prop_radio_box_02": (0.52, 0.30, 0.34),
    "prop_gas_can_01": (0.53, 0.67, 0.18), "prop_stretcher_01": (0.93, 0.12, 3.10), "prop_wire_03": (1.03, 1.12, 1.03),
    "prop_wire_01": (3.23, 1.11, 0.87), "prop_landmine_01": (0.47, 0.13, 0.53), "prop_searchlight_base_01": (1.08, 1.46, 0.96),
    "prop_cup_01": (0.17, 0.17, 0.20), "char_attach_german_helmet_01": (0.36, 0.30, 0.37),
    "char_attach_british_helmet_01": (0.44, 0.20, 0.44), "char_attach_german_canteen_01": (0.14, 0.20, 0.10),
    "char_attach_german_pack_01": (0.24, 0.20, 0.12), "char_attach_german_bedroll_01": (0.45, 0.19, 0.12),
    "char_attach_field_glasses_01": (0.25, 0.09, 0.08), "wep_german_grenade_01": (0.09, 0.42, 0.09),
    "wep_german_rifle_01": (0.06, 0.19, 1.10), "wep_british_rifle_01": (0.07, 0.20, 1.13),
    "wep_german_heavy_machine_gun_01": (0.41, 0.37, 1.19),
    **{f"env_poster_0{k}": (0.52, 0.84, 0.02) for k in range(1, 7)},
}
PROP_ATLAS = "glb/atlas/war_01_A.png"
POSTER_ATLAS = "glb/atlas/war_posters_signs_flags_01.png"
FREE_GAP = 0.45  # a free-standing piece keeps this far from the walls and from other pieces, so a player gets round it


@dataclass(frozen=True)
class _Item:
    """A piece of furniture: pack ``model`` scaled ``scale`` (x, y, z of the model), stood against a wall with its back to
    it (``turn`` 90: its long side along the wall), or ``free`` in the open floor, or (``hung`` = height above the floor)
    hung flat on a wall with no collision. ``top`` pieces ride on it: (model, scale, along, depth, yaw offset[, up]),
    ``along`` and ``depth`` from the middle of the item (depth positive towards the room), ``up`` above its top; they have
    no collision. ``atlas`` is the texture when it is not the bunker's own."""

    model: str
    scale: tuple[float, float, float]
    turn: int = 0
    top: tuple = ()
    free: bool = False
    hung: float | None = None
    atlas: str = ""

    @property
    def size(self) -> tuple[float, float, float]:
        x, y, z = (a * b for a, b in zip(PACK[self.model], self.scale))
        return (z, y, x) if self.turn else (x, y, z)  # along the wall, up, deep


def _i(model, sx, sy=None, sz=None, **kw):
    return _Item(model, (sx, sx if sy is None else sy, sx if sz is None else sz), **kw)


def _poster(k: int, sx: float = 1.0, z: float = 1.1):
    return _i(f"env_poster_0{k}", sx, hung=z, atlas=POSTER_ATLAS)


_HELMET = ("char_attach_german_helmet_01", (1.0, 1.0, 1.0))
_CUP = ("prop_cup_01", (1.0, 1.0, 1.0))
# furnishing per level (0 ground, 1..3 down), in the order they are placed (walls first, then the open floor, then the posters)
FURNITURE = {
    0: [  # the ground floor: ammunition crates under the firing slits (see _furnish), and what a firing post keeps about
        _i("prop_stretcher_01", 0.65, turn=90), _i("prop_crate_pile_01", 0.5), _i("prop_barrel_pile_02", 0.55, turn=90),
        _i("prop_crate_03", 0.6, top=((*_HELMET, 0.0, 0.0, 30), ("wep_german_rifle_01", (1.0, 1.0, 1.0), 0.0, 0.15, 90, 0.0))),
        _i("prop_gas_can_01", 1.0), _poster(1), _poster(2),
    ],
    1: [  # crew quarters
        _i("prop_camp_bed_01", 0.75, turn=90, top=((*_HELMET, 0.7, 0.0, 20), ("char_attach_german_bedroll_01", (1.6, 1.6, 1.6), -0.55, 0.0, 0))),
        _i("prop_camp_bed_02", 0.75, turn=90, top=(("char_attach_british_helmet_01", (0.9, 0.9, 0.9), 0.7, 0.0, -15),)),
        _i("prop_camp_bed_01", 0.75, turn=90, top=(("char_attach_german_bedroll_01", (1.6, 1.6, 1.6), -0.55, 0.0, 0),)),
        _i("prop_camp_bed_02", 0.75, turn=90),
        _i("prop_dresser_01", 0.5, 0.8, 0.6, top=(("prop_gramophone_01", (0.5, 0.5, 0.5), 0.0, 0.0, 0),)),
        _i("prop_shelves_01", 0.6, 1.7, 1.0), _i("prop_shelves_01", 0.6, 1.7, 1.0),
        _i("prop_chair_01", 0.9), _i("prop_barrel_02", 0.55), _i("char_attach_german_pack_01", 1.7), _i("char_attach_german_pack_01", 1.7),
        _i("prop_coffee_table_01", 0.9, free=True, top=(_CUP[0:2] + (-0.15, 0.1, 0), ("char_attach_field_glasses_01", (1.2, 1.2, 1.2), 0.15, -0.1, 40))),
        _i("prop_chair_01", 0.9, free=True),
        _poster(2), _poster(3), _poster(4), _poster(5),
    ],
    2: [  # ammunition store
        _i("prop_shelves_01", 1.0, 1.6, 1.4), _i("prop_shelves_01", 1.0, 1.6, 1.4),
        _i("prop_crate_03", 0.7, top=(("wep_german_grenade_01", (1.3, 1.3, 1.3), -0.15, 0.0, 0), ("wep_german_grenade_01", (1.3, 1.3, 1.3), 0.1, 0.05, 0))),
        _i("prop_crate_02", 0.55, turn=90, top=(("wep_german_rifle_01", (1.0, 1.0, 1.0), 0.0, 0.0, 90),)),
        _i("prop_crate_04", 0.6), _i("prop_crate_03", 0.7), _i("prop_crate_pile_01", 0.5), _i("prop_barrel_pile_01", 0.45, turn=90),
        _i("prop_shell_01", 1.6), _i("prop_shell_01", 1.6), _i("prop_shell_01", 1.6), _i("prop_shell_01", 1.6),
        _i("prop_barrel_01", 0.6), _i("prop_barrel_01", 0.6), _i("prop_wire_03", 0.7),
        _i("prop_crate_03", 0.7, free=True, top=(("prop_crate_04", (0.5, 0.5, 0.5), 0.0, 0.0, 25, 0.0),)),
        _i("prop_shell_02", 0.8, free=True), _i("prop_landmine_01", 1.0, free=True),
        _poster(1), _poster(6),
    ],
    3: [  # command post
        _i("prop_table_01", 0.55, 0.95, 0.7, top=(("prop_typewriter_01", (0.7, 0.7, 0.7), -0.28, 0.0, 20),
                                                  ("prop_radio_box_02", (0.9, 0.9, 0.9), 0.32, 0.0, -10))),
        _i("prop_chair_02", 0.9), _i("prop_shelves_01", 0.6, 1.7, 1.0), _i("prop_shelves_01", 0.6, 1.7, 1.0),
        _i("prop_gas_can_01", 1.0), _i("prop_gas_can_01", 1.0), _i("prop_barrel_02", 0.55),
        _i("prop_searchlight_base_01", 0.6), _i("prop_radio_box_01", 1.2),
        _i("prop_table_small_01", 0.8, free=True, top=((*_HELMET, 0.0, 0.0, 10), (_CUP[0], _CUP[1], 0.25, 0.2, 0))),
        _i("prop_chair_02", 0.9, free=True), _i("prop_chair_01", 0.9, free=True),
        _poster(1), _poster(3), _poster(4), _poster(6),
    ],
}
AMMO = _i("prop_crate_01", 0.5, 1.0, 0.6)  # ground floor ammunition crate under a firing slit


@dataclass(frozen=True)
class _Edge:
    k: int
    p: np.ndarray  # start point
    t: np.ndarray  # unit direction along the edge
    n: np.ndarray  # unit normal to the right of the direction (outward for a counter-clockwise ring)
    ln: float


@dataclass(frozen=True)
class _Opening:
    """A rectangular hole through a wall: ``a..b`` along outer edge ``edge``, ``z0..z1`` up, and the
    same hole on inner edge ``inner`` at ``ia..ib``."""

    edge: int
    a: float
    b: float
    z0: float
    z1: float
    inner: int
    ia: float
    ib: float
    door: bool


class _Builder:
    def __init__(self) -> None:
        self.tris: list = []
        self.cols: list = []

    def tri(self, v0, v1, v2, facing, colour) -> None:
        """One triangle, wound so its front faces ``facing``."""
        v0, v1, v2 = (np.asarray(v, float) for v in (v0, v1, v2))
        if np.cross(v1 - v0, v2 - v0) @ np.asarray(facing, float) < 0:
            v1, v2 = v2, v1
        self.tris.append([tuple(v0), tuple(v1), tuple(v2)])
        self.cols.append(colour)

    def quad(self, v0, v1, v2, v3, facing, colour) -> None:
        self.tri(v0, v1, v2, facing, colour)
        self.tri(v0, v2, v3, facing, colour)

    def flat(self, poly: Polygon, z: float, up: bool, colour) -> None:
        """``poly`` as a horizontal face at height ``z``, seen from above (``up``) or from below."""
        for t3 in triangulate(poly):
            self.tri(*[(x, y, z) for x, y in t3], (0, 0, 1 if up else -1), colour)

    def wall(self, e: _Edge, bands, holes, facing) -> None:
        """Vertical wall along ``e``, seen from ``facing``. ``bands`` are ``(z0, z1, colour)`` stripes;
        ``holes`` are ``(s0, s1, z0, z1)`` rectangles left open."""
        cuts = sorted({0.0, e.ln, *(h[0] for h in holes), *(h[1] for h in holes)})
        for s0, s1 in pairwise(cuts):
            if s1 - s0 < EPS:
                continue
            gaps = [(h[2], h[3]) for h in holes if h[0] <= s0 + EPS and h[1] >= s1 - EPS]
            p0, p1 = e.p + e.t * s0, e.p + e.t * s1
            for z0, z1, colour in bands:
                spans = [(z0, z1)]
                for g0, g1 in gaps:
                    spans = [
                        piece
                        for lo, hi in spans
                        for piece in ((lo, min(hi, g0)), (max(lo, g1), hi))
                        if piece[1] - piece[0] > EPS
                    ]
                for lo, hi in spans:
                    self.quad((*p0, lo), (*p1, lo), (*p1, hi), (*p0, hi), (*facing, 0.0), colour)

    def plate(self, e: _Edge, a, b, z0, z1, depth, colour) -> None:
        """Box standing ``depth`` proud of wall ``e``, spanning ``a..b`` along it and ``z0..z1`` up."""
        corners = []
        for d in (-0.02, depth):  # slightly inside the wall so there is no gap
            for s in (a, b):
                for z in (z0, z1):
                    corners.append(np.array((*(e.p + e.t * s + e.n * d), z)))
        c = np.mean(corners, axis=0)
        # six quads of the 8 corners (index = d*4 + s*2 + z)
        for q in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
            v = [corners[i] for i in q]
            self.quad(*v, np.mean(v, axis=0) - c, colour)

    def obox(self, c, t, f, ht: float, hf: float, z0: float, z1: float, colour, bottom: bool = True) -> None:
        """Box centred on ``c`` (xy), ``ht`` either side along ``t`` and ``hf`` along ``f``, from ``z0`` to ``z1``."""
        c, t, f = (np.asarray(v, float) for v in (c, t, f))
        pts = [c + t * st * ht + f * sf * hf for st, sf in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        mid = np.array((*c, (z0 + z1) / 2))
        lo = [(*q, z0) for q in pts]
        hi = [(*q, z1) for q in pts]
        for quad in (*([lo[::-1]] if bottom else []), hi, *((lo[i], lo[(i + 1) % 4], hi[(i + 1) % 4], hi[i]) for i in range(4))):
            self.quad(*quad, np.mean(quad, axis=0) - mid, colour)

    def mesh(self) -> Mesh:
        return Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8))


@dataclass(frozen=True)
class _Hatch:
    """A square hole in a floor with a ladder hanging on one edge. The climber stands at ``rail`` facing
    ``facing``; the floor beyond the ladder's edge is where they step off at the top."""

    square: Polygon
    rail: np.ndarray
    facing: np.ndarray
    edge_mid: np.ndarray


def _edges(pts: np.ndarray) -> list[_Edge]:
    out = []
    for k in range(len(pts)):
        p, q = pts[k], pts[(k + 1) % len(pts)]
        d = q - p
        ln = float(np.hypot(*d))
        if ln > EPS:
            t = d / ln
            out.append(_Edge(k, p, t, np.array([t[1], -t[0]]), ln))
    return out


def _scaled(ring: np.ndarray, c: np.ndarray, s: float) -> np.ndarray:
    return c + (ring - c) * s


def _through(e: _Edge, a: float, b: float, inner: list[_Edge]) -> tuple[int, float, float] | None:
    """Where ``a..b`` of outer edge ``e`` comes out on the room side: the inner edge and the span along
    it, or None when the wall behind is not one straight inner edge there (a corner is too close)."""
    pa, pb = (e.p + e.t * s - e.n * WALL_T for s in (a, b))
    for i in inner:
        if i.t @ e.t < 1 - EPS:
            continue
        if abs((pa - i.p) @ i.n) > EPS or abs((pb - i.p) @ i.n) > EPS:
            continue
        ia, ib = float((pa - i.p) @ i.t), float((pb - i.p) @ i.t)
        if ia >= EDGE_MARGIN and ib <= i.ln - EDGE_MARGIN:
            return i.k, ia, ib
    return None


def _openings(outer: list[_Edge], inner: list[_Edge], door_dir) -> list[_Opening] | None:
    """The doorway (first) and the firing slits; None when no wall facing ``door_dir`` takes a door."""

    def centred(e: _Edge, width: float, z0: float, z1: float, door: bool) -> _Opening | None:
        a, b = e.ln / 2 - width / 2, e.ln / 2 + width / 2
        hit = _through(e, a, b, inner) if a >= EDGE_MARGIN else None
        return _Opening(e.k, a, b, z0, z1, *hit, door) if hit else None

    dd = np.asarray(door_dir, float)
    dd = dd / np.linalg.norm(dd)
    facing = sorted((e for e in outer if e.n @ dd > 0.5), key=lambda e: (-e.ln * (e.n @ dd), e.k))
    door = next((o for e in facing if (o := centred(e, DOOR_W, FLOOR_Z, DOOR_H, True))), None)
    if door is None:
        return None
    out = [door]
    for e in sorted((e for e in outer if e.k != door.edge), key=lambda e: (-e.ln, e.k)):
        if len(out) == 5:
            break
        if o := centred(e, SLIT_W, SLIT_Z0, SLIT_Z1, False):
            out.append(o)
    return out


def _hatch_candidates(room: Polygon) -> list[_Hatch]:
    """Hatches that fit the room: a square against a straight wall, sliding along it, with the ladder
    on the side farthest from the wall (so the floor beyond it is the open room)."""
    pts = np.array(room.exterior.coords)[:-1]
    roomy = room.buffer(-0.02, join_style="mitre")
    stand = room.buffer(-0.4, join_style="mitre")  # where a player's centre can be
    if not isinstance(roomy, Polygon) or not isinstance(stand, Polygon):
        return []
    out = []
    for e in _edges(pts):
        if e.ln < HATCH + 2 * HATCH_MARGIN:
            continue
        inward = -e.n  # the outward normal of a counter-clockwise ring points away from the room
        slide = np.arange(HATCH_MARGIN, e.ln - HATCH - HATCH_MARGIN + EPS, HATCH_SLIDE)
        for s0 in slide:
            base = e.p + e.t * s0 + inward * HATCH_MARGIN
            sq = Polygon([base, base + e.t * HATCH, base + e.t * HATCH + inward * HATCH, base + inward * HATCH])
            mid = base + e.t * HATCH / 2 + inward * HATCH
            if roomy.contains(sq) and stand.contains(Point(*(mid + inward * (STEP_OFF - RAIL_SET)))):
                out.append(_Hatch(sq, mid - inward * RAIL_SET, inward, mid))
    return out


def _hatches(room: Polygon, door_xy) -> list[_Hatch] | None:
    """The hatches of floors 0..UNDER-1 (the hole in each floor, leading down): the first as far from the
    doorway as the room allows, each next one as far from the one before, so every floor is crossed to
    reach the way down. None when the room has no two hatches ``HATCH_APART`` from each other."""
    cands = _hatch_candidates(room)
    door = Point(*door_xy)
    first = [h for h in cands if h.square.distance(door) >= HATCH_DOOR_CLEAR]
    if not first:
        return None
    key = lambda h: (round(h.square.centroid.x, 3), round(h.square.centroid.y, 3))
    chosen = [max(first, key=lambda h: (h.square.distance(door), key(h)))]
    while len(chosen) < UNDER:
        prev = chosen[-1]
        far = [h for h in cands if h.square.centroid.distance(prev.square.centroid) >= HATCH_APART]
        if not far:
            return None
        chosen.append(max(far, key=lambda h: (h.square.centroid.distance(prev.square.centroid), key(h))))
    return chosen


def _dome(b: _Builder, pts: np.ndarray, c: np.ndarray, wall_h: float, colour) -> None:
    prev, prev_z = pts, wall_h
    n = len(pts)
    for k in range(1, RINGS + 1):
        theta = (k / RINGS) * math.acos(DOME_END)
        cur, cur_z = _scaled(pts, c, math.cos(theta)), wall_h + RISE * math.sin(theta)
        for i in range(n):
            j = (i + 1) % n
            a0, b0 = (*prev[i], prev_z), (*prev[j], prev_z)
            a1, b1 = (*cur[i], cur_z), (*cur[j], cur_z)
            b.tris.append([a0, b0, b1])  # counter-clockwise ring: these face outward and up
            b.cols.append(colour)
            b.tris.append([a0, b1, a1])
            b.cols.append(colour)
        prev, prev_z = cur, cur_z
    b.flat(Polygon(prev), prev_z, True, colour)


def _streaks(b: _Builder, outer: list[_Edge], wall_h: float, taken: dict[int, list], colour) -> None:
    """Pale streaks running down from the dome, clear of whatever already sits on a wall."""
    for e in outer:
        if e.ln < 1.2:
            continue
        for f in (0.3, 0.68):
            if e.ln * f > 0.6 and e.ln * (1 - f) > 0.6:
                w = 0.22 + 0.1 * ((e.k * 7 + int(f * 10)) % 3)
                lo, hi = e.ln * f - w, e.ln * f + w
                if any(lo < t1 + 0.05 and hi > t0 - 0.05 for t0, t1 in taken.get(e.k, [])):
                    continue
                b.plate(e, lo, hi, 0.7 + 0.1 * (e.k % 3), wall_h, 0.02, colour)


def _closed(b: _Builder, poly, outer: list[_Edge], door_dir, detail: bool) -> None:
    concrete = hex_to_rgb(CONCRETE)
    for e in outer:
        b.wall(e, [(0.0, WALL_H, concrete)], [], e.n)
    b.flat(poly, 0.0, False, concrete)
    if not detail:
        return
    wide = [e for e in outer if e.ln >= 1.2]
    dd = np.asarray(door_dir, float)
    dd = dd / np.linalg.norm(dd)
    door = max(
        (e for e in wide if e.n @ dd > 0.5 and e.ln >= 1.6),
        key=lambda e: e.ln * (e.n @ dd),
        default=None,
    )
    taken: dict[int, list] = {}
    if door:
        mid = door.ln / 2
        b.plate(door, mid - 0.55, mid + 0.55, 0.0, 1.55, 0.05, hex_to_rgb(DOOR_FRAME))
        b.plate(door, mid - 0.42, mid + 0.42, 0.1, 1.45, 0.05, hex_to_rgb(DOOR_LEAF))
    # one firing slit on each of the longest other walls
    for e in sorted((e for e in wide if e is not door), key=lambda e: -e.ln)[:4]:
        b.plate(e, e.ln / 2 - 0.25, e.ln / 2 + 0.25, 1.0, 1.25, 0.05, hex_to_rgb(SLIT))
    _streaks(b, outer, WALL_H, taken, hex_to_rgb(STREAK))


def _walk_holes(room: Polygon, *squares: Polygon) -> Polygon:
    """The room's floor or ceiling: ``room`` with the hatch squares cut out."""
    out = room
    for q in squares:
        out = out.difference(q)
    return out


def _tube(b: _Builder, sq: Polygon, z0: float, z1: float, colour) -> None:
    """The four sides of a hatch hole through a slab, seen from inside the hole."""
    pts = list(sq.exterior.coords)[:-1]
    c = np.array(sq.centroid.coords[0])
    for p, q in zip(pts, pts[1:] + pts[:1], strict=True):
        mid = (np.array(p) + np.array(q)) / 2
        b.quad((*p, z0), (*q, z0), (*q, z1), (*p, z1), (*(c - mid), 0.0), colour)


def _ladder(b: _Builder, h: _Hatch, y_bottom: float, y_top: float) -> None:
    """A steel ladder hanging on the edge of hatch ``h`` from just above the floor above to the floor below."""
    f = h.facing
    t = np.array([-f[1], f[0]])
    steel, rung = hex_to_rgb(STEEL), hex_to_rgb(RUNG)
    at = h.edge_mid - f * 0.07
    for s_ in (-0.2, 0.2):
        b.obox(at + t * s_, t, f, 0.025, 0.025, y_bottom + 0.05, y_top + 0.9, steel)
    z = y_bottom + 0.3
    while z < y_top + 0.8:
        b.obox(at, t, f, 0.2, 0.015, z - 0.015, z + 0.015, rung)
        z += 0.3


def _furnish(
    room: Polygon, edges: list[_Edge], level: int, keep: list, z: float, slits: list[tuple[_Edge, float]],
) -> list[dict]:
    """The furniture of a floor as game-placed props (``bunkers.json`` ``props``; ADR 0009 §7), against its walls and clear of
    ``keep`` (hatches, landings, the doorway), then free-standing pieces in the open floor, then posters on the bare wall.
    Level 0 gets ammunition crates under each firing slit first. A prop is ``{"model", "pos": [x, y, z], "yaw", "scale",
    "boxes"}`` (and ``atlas`` when it is not the bunker's own): ``pos`` the middle of its footprint on the floor (``z``
    relative to the building base), ``yaw`` the compass bearing its front (+Z) looks along (into the room), ``boxes`` its
    collision boxes ``[x, y, z, size x, size y, size z]`` in its own frame."""
    inside = room.buffer(-0.03 + 1e-4, join_style="mitre")  # a prop touches its wall: the 0.1 mm absorbs rounding
    open_floor = room.buffer(-FREE_GAP)
    placed: list[tuple[Polygon, float]] = []  # footprint, height
    out: list[dict] = []

    def emit(item: _Item, mid, yaw: float, h: float, zz: float, box: bool = True) -> None:
        mx, _, mz = (a * c for a, c in zip(PACK[item.model], item.scale))  # the box is in the model's own frame, turned or not
        prop = {
            "model": f"war/{item.model}", "pos": [round(float(mid[0]), 3), round(float(mid[1]), 3), round(zz, 3)],
            "yaw": round(yaw % 360, 2), "scale": [round(v, 3) for v in item.scale],
            "boxes": [[0.0, round(h / 2, 3), 0.0, round(mx, 3), round(h, 3), round(mz, 3)]] if box else [],
        }
        if item.atlas:
            prop["atlas"] = item.atlas
        out.append(prop)

    def tops(item: _Item, mid, along, into, h: float) -> None:
        for model, scale, du, dv, dyaw, *up in item.top:
            xy = mid + along * du + into * dv
            out.append({
                "model": f"war/{model}", "pos": [round(float(xy[0]), 3), round(float(xy[1]), 3), round(z + h + (up[0] if up else 0.0), 3)],
                "yaw": round((bearing_deg(*into) + dyaw) % 360, 2), "scale": list(scale), "boxes": [],
            })

    def put(e: _Edge, s0: float, item: _Item) -> bool:
        w, h, d = item.size
        gap = PROP_GAP if item.hung is None else 0.035
        d = d if item.hung is None else 0.02
        base = e.p + e.t * s0 + (-e.n) * gap
        fp = Polygon([base, base + e.t * w, base + e.t * w - e.n * d, base - e.n * d])
        if not inside.contains(fp) or any(fp.intersects(k) for k in keep):
            return False
        if item.hung is not None and any(se.k == e.k and abs(sm - (s0 + w / 2)) < w / 2 + 0.4 for se, sm in slits):
            return False  # not over a firing slit
        if item.hung is None:
            if any(fp.intersects(q) for q, _ in placed):
                return False
            placed.append((fp, h))
        elif any(fp.intersects(q) and hq > item.hung - 0.05 for q, hq in placed):
            return False  # something tall stands under it
        mid = base + e.t * w / 2 - e.n * d / 2
        yaw = bearing_deg(*(-e.n)) + item.turn  # the room side; a turned piece lies along the wall
        if item.hung is None:
            emit(item, mid, yaw, h, z)
            tops(item, mid, e.t, -e.n, h)
        else:
            emit(item, mid, yaw, h, z + item.hung, box=False)
        return True

    def put_free(item: _Item, f: _Edge) -> bool:
        """In the open floor, aligned with wall ``f`` (the longest), nearest the middle of the room first."""
        w, h, d = item.size
        c = room.centroid
        minx, miny, maxx, maxy = room.bounds
        cands = sorted(
            ((x, y) for x in np.arange(minx, maxx, 0.25) for y in np.arange(miny, maxy, 0.25)),
            key=lambda q: (q[0] - c.x) ** 2 + (q[1] - c.y) ** 2,
        )
        for x, y in cands:
            mid = np.array((x, y))
            fp = Polygon([mid + f.t * sx * w / 2 + (-f.n) * sy * d / 2 for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))])
            if not open_floor.contains(fp) or any(fp.intersects(k) for k in keep) or any(fp.intersects(q.buffer(FREE_GAP)) for q, _ in placed):
                continue
            placed.append((fp, h))
            emit(item, mid, bearing_deg(*(-f.n)) + item.turn, h, z)
            tops(item, mid, f.t, -f.n, h)
            return True
        return False

    longest = sorted(edges, key=lambda e: (-e.ln, e.k))
    if level == 0:
        for e, mid in slits:
            put(e, mid - AMMO.size[0] / 2, AMMO)
    walls = [it for it in FURNITURE[level] if not it.free and it.hung is None]
    for item in walls:
        # a long piece (a bed) lies along a wall, or with its head to it when no wall is long enough
        for variant in (item, replace(item, turn=90 - item.turn) if item.turn else item):
            done = False
            for e in longest:
                if e.ln < variant.size[0] + 0.2:
                    continue
                for s0 in np.arange(0.1, e.ln - variant.size[0] - 0.1 + EPS, 0.25):
                    if put(e, float(s0), variant):
                        done = True
                        break
                if done:
                    break
            if done:
                break
    for item in (it for it in FURNITURE[level] if it.free):
        put_free(item, longest[0])
    used = {e.k: 0 for e in edges}
    for item in (it for it in FURNITURE[level] if it.hung is not None):
        for e in sorted(edges, key=lambda e: (used[e.k], -e.ln, e.k)):
            if e.ln < item.size[0] + 0.4:
                continue
            slides = np.arange(0.2, e.ln - item.size[0] - 0.2 + EPS, 0.2)
            if any(put(e, float(s0), item) for s0 in slides):
                used[e.k] += 1
                break
    return out


def _lamp(b: _Builder, xy, ceiling_z: float) -> None:
    cage = hex_to_rgb(LAMP_CAGE)
    b.obox(xy, (1, 0), (0, 1), LAMP_SIZE / 2, LAMP_SIZE / 2, ceiling_z - LAMP_SIZE, ceiling_z, cage)
    b.obox(xy, (1, 0), (0, 1), LAMP_SIZE / 2 - 0.04, LAMP_SIZE / 2 - 0.04, ceiling_z - LAMP_SIZE + 0.04, ceiling_z - 0.04,
           hex_to_rgb(LAMP_GLOW))


def _keepouts(level: int, hatches: list[_Hatch] | None, door_xy) -> list:
    """Where a floor's furniture must not stand: round each hatch in its floor (where the ladder comes up),
    under the hatch above (where it lets you off), and in front of the doorway."""
    keep = []
    if hatches:
        if level < UNDER:
            keep.append(hatches[level].square.buffer(LANDING))
        if level > 0:
            keep.append(hatches[level - 1].square.buffer(LANDING))
    if level == 0:
        keep.append(Point(*door_xy).buffer(1.4))
    return keep


@dataclass(frozen=True)
class _Screen:
    """Where the intel screen hangs in the command post: ``centre`` on the wall, ``facing`` into the room,
    ``zone`` the strip in front of it that no furniture stands in."""

    centre: np.ndarray
    facing: np.ndarray
    width: float
    zone: Polygon


def _intel_screen(room: Polygon, inner: list[_Edge], hatches: list[_Hatch] | None) -> _Screen | None:
    """The wall of the command post (the lowest floor) that the intel screen hangs on: the widest stretch of
    straight wall at most ``SCREEN_W_MAX`` wide that is clear of the ladder landing, longest wall first, nearest
    the middle of the wall. None when no wall takes a screen of at least ``SCREEN_W_MIN`` (then there is no intel
    room in this bunker). The floors under the ground have no openings, so any wall will do."""
    if not hatches:
        return None
    keep = hatches[UNDER - 1].square.buffer(LANDING)
    roomy = room.buffer(1e-3, join_style="mitre")
    best = None
    for e in inner:
        usable = e.ln - 2 * SCREEN_MARGIN
        for width in np.arange(min(SCREEN_W_MAX, usable), SCREEN_W_MIN - EPS, -SCREEN_STEP):
            for s0 in np.arange(SCREEN_MARGIN, e.ln - SCREEN_MARGIN - width + EPS, SCREEN_SLIDE):
                a, b = e.p + e.t * s0, e.p + e.t * (s0 + width)
                d = -e.n * SCREEN_ZONE
                zone = Polygon([a, b, b + d, a + d])
                if not roomy.contains(zone) or zone.intersects(keep):
                    continue
                key = (round(float(width), 2), round(e.ln, 2), -round(abs(s0 + width / 2 - e.ln / 2), 2), -e.k)
                if best is None or key > best[0]:
                    best = (key, _Screen((a + b) / 2, -e.n, float(width), zone))
    return best[1] if best else None


def _screen_export(s: _Screen) -> dict:
    z0 = -STOREY * UNDER + SCREEN_Z0
    return {
        "x": round(float(s.centre[0]), 3),
        "y": round(float(s.centre[1]), 3),
        "dx": round(float(s.facing[0]), 4),
        "dy": round(float(s.facing[1]), 4),
        "z0": round(z0, 3),
        "z1": round(z0 + SCREEN_H, 3),
        "width": round(s.width, 3),
    }


def lamp_xy(grown_ring) -> tuple[float, float]:
    """Where the lamp of a room hangs: the mean of its shelter polygon's corners. The game hangs its light at the same place."""
    pts = np.asarray(grown_ring, float)
    return float(pts[:, 0].mean()), float(pts[:, 1].mean())


def _free_spans(length: float, holes: list, z_top: float) -> list[tuple[float, float]]:
    """The stretches of a wall of ``length`` that have no opening reaching below ``z_top`` (``holes`` are ``(s0, s1, z0, z1)``)."""
    cuts = sorted(
        (max(0.0, h[0]), min(length, h[1])) for h in holes if h[2] < z_top  # an opening up to the panelling's top
    )
    out, at = [], 0.0
    for a, c in cuts:
        if a > at + EPS:
            out.append((at, a))
        at = max(at, c)
    if length > at + EPS:
        out.append((at, length))
    return out


def _floor(b: _Builder, poly: Polygon, z: float, edges: list[_Edge], detail: bool) -> None:
    """A floor: flat grey for the collision mesh, laid planks (along the longest wall, in four tones) for the picture."""
    if not detail or poly.is_empty:
        b.flat(poly, z, True, hex_to_rgb(FLOOR))
        return
    t = max(edges, key=lambda e: (e.ln, -e.k)).t
    n = np.array((-t[1], t[0]))
    minx, miny, maxx, maxy = poly.bounds
    corners = np.array(((minx, miny), (maxx, miny), (maxx, maxy), (minx, maxy)))
    lo, hi = (corners @ n).min(), (corners @ n).max()
    reach = (corners @ t).min() - 1, (corners @ t).max() + 1
    b.flat(poly, z, True, hex_to_rgb(GAP))
    for i, off in enumerate(np.arange(lo, hi, PLANK_W)):
        w = PLANK_W - 0.012
        strip = Polygon([t * reach[0] + n * off, t * reach[1] + n * off, t * reach[1] + n * (off + w), t * reach[0] + n * (off + w)])
        # a plank is cut at staggered joints: its own stretch, 2.4 m long, shifted by plank
        piece = poly.intersection(strip)
        for g in getattr(piece, "geoms", [piece]):
            if isinstance(g, Polygon) and g.area > 1e-4:
                b.flat(g, z + 0.004, True, hex_to_rgb(PLANKS[(i * 7) % len(PLANKS)]))


def _prop_polys(props: list[dict], z0: float, z1: float) -> list[Polygon]:
    """Footprints of the furniture of the floor ``z0``..``z1`` (what a pillar must not stand in), from the prop list."""
    out = []
    for q in props:
        if not z0 - 0.05 <= q["pos"][2] <= z1:
            continue
        name = q["model"].split("/", 1)[1]
        if name not in PACK:
            continue
        hx, hz = (PACK[name][0] * q["scale"][0] / 2, PACK[name][2] * q["scale"][2] / 2)
        yaw = math.radians(q["yaw"])
        f, r = np.array((math.sin(yaw), math.cos(yaw))), np.array((math.cos(yaw), -math.sin(yaw)))
        c = np.array(q["pos"][:2])
        out.append(Polygon([c + r * sx * hx + f * sz * hz for sx, sz in ((-1, -1), (1, -1), (1, 1), (-1, 1))]).buffer(0.04))
    return out


def _timber(
    b: _Builder, room: Polygon, inner: list[_Edge], by_inner: dict[int, list], z0: float, z1: float,
    keep: list, avoid: list, lamp, props: list[dict],
) -> None:
    """Timber of a room for the picture: panelling boards up to ``PAINT_H``, a plate along the top of every wall, beams across
    the ceiling about every ``BEAM_STEP``, and a pillar under the end of each beam where nothing else stands. ``keep`` are
    zones that stay clear (hatch landings, the doorway, the intel wall), ``avoid`` the ceiling's hatch holes, ``props`` the
    furniture (``_props``). None of it is collision."""
    board, tim, beam = hex_to_rgb(BOARD), hex_to_rgb(TIMBER), hex_to_rgb(BEAM)
    for e in inner:  # panelling: every other board is darker
        spans = _free_spans(e.ln, by_inner.get(e.k, []), PAINT_H)
        for a, c in spans:
            for k in range(int(a // BOARD_W), math.ceil(c / BOARD_W)):
                if k % 2:
                    continue
                s0, s1 = max(a, k * BOARD_W), min(c, (k + 1) * BOARD_W - 0.03)
                if s1 - s0 < 0.04:
                    continue
                off = -e.n * 0.012
                p0, p1 = e.p + e.t * s0 + off, e.p + e.t * s1 + off
                b.quad((*p0, z0 + 0.04), (*p1, z0 + 0.04), (*p1, z0 + PAINT_H), (*p0, z0 + PAINT_H), (*-e.n, 0.0), board)
    avoid_g = unary_union([q.buffer(0.12) for q in avoid] + [Point(*lamp).buffer(0.3)]) if lamp is not None else unary_union([q.buffer(0.12) for q in avoid])

    def pieces(a, c, width: float) -> list[tuple[np.ndarray, np.ndarray]]:
        seg = LineString([a, c]).intersection(room.buffer(-0.01))
        if not avoid_g.is_empty:
            seg = seg.difference(avoid_g)
        out = []
        for g in getattr(seg, "geoms", [seg]):
            if isinstance(g, LineString) and g.length > 0.4:
                out.append((np.array(g.coords[0]), np.array(g.coords[-1])))
        return out

    def rod(p, q, width: float, h: float, colour) -> None:
        d = q - p
        ln = float(np.hypot(*d))
        t = d / ln
        b.obox((p + q) / 2, t, np.array((-t[1], t[0])), ln / 2, width / 2, z1 - h, z1, colour)

    for e in inner:  # the plate along the top of each wall, in from the wall by half its width
        a, c = e.p - e.n * 0.0 + (-e.n) * (BEAM_W / 2 - 0.0), e.p + e.t * e.ln + (-e.n) * (BEAM_W / 2)
        for p, q in pieces(a + e.t * 0.0, c, BEAM_W):
            rod(p, q, BEAM_W, BEAM_H * 0.75, beam)
    longest = max(inner, key=lambda e: (e.ln, -e.k))
    t = longest.t
    n = np.array((-t[1], t[0]))
    minx, miny, maxx, maxy = room.bounds
    corners = np.array(((minx, miny), (maxx, miny), (maxx, maxy), (minx, maxy)))
    lo, hi = (corners @ t).min(), (corners @ t).max()
    cross = (corners @ n).min() - 1, (corners @ n).max() + 1
    count = max(1, round((hi - lo) / BEAM_STEP))
    blockers = _prop_polys(props, z0, z1)
    posts: list[Polygon] = []
    for i in range(count):
        at = lo + (hi - lo) * (i + 0.5) / count
        a, c = t * at + n * cross[0], t * at + n * cross[1]
        for p, q in pieces(a, c, BEAM_W):
            rod(p, q, BEAM_W, BEAM_H, beam)
            for end in (p, q):  # a pillar under each end that meets a wall
                e = min(inner, key=lambda e: abs(float(np.dot(end - e.p, e.n))) if 0 <= np.dot(end - e.p, e.t) <= e.ln else 1e9)
                dist = abs(float(np.dot(end - e.p, e.n)))
                if dist > 0.2:
                    continue
                s = float(np.clip(np.dot(end - e.p, e.t), PILLAR / 2, e.ln - PILLAR / 2))
                if any(h[0] - PILLAR / 2 < s < h[1] + PILLAR / 2 for h in by_inner.get(e.k, [])):
                    continue
                ctr = e.p + e.t * s + (-e.n) * (PILLAR / 2 + 0.005)
                fp = Polygon([ctr + e.t * sx * PILLAR / 2 + (-e.n) * sy * PILLAR / 2 for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))])
                if any(fp.intersects(k) for k in keep) or any(fp.intersects(q2) for q2 in blockers + posts):
                    continue
                posts.append(fp.buffer(0.05))
                b.obox(ctr, e.t, -e.n, PILLAR / 2, PILLAR / 2, z0, z1 - BEAM_H * 0.75, tim)


def _underground(
    b: _Builder, room: Polygon, inner: list[_Edge], hatches: list[_Hatch], detail: bool, lamp, door_xy, props: list[dict],
) -> None:
    """One room per floor under the ground floor, joined by the hatches. Floors, ceilings and walls are
    always built: they are the collision of the way down. The furniture is not in the mesh (``_props``)."""
    concrete = hex_to_rgb(CONCRETE)
    for k in range(1, UNDER + 1):
        z0, z1 = -STOREY * k, -STOREY * k + CLEAR_H
        bands = [(z0, z0 + PAINT_H, hex_to_rgb(PAINT)), (z0 + PAINT_H, z1, hex_to_rgb(INNER))]
        for i in inner:
            b.wall(i, bands if detail else [(z0, z1, concrete)], [], -i.n)
        below = [hatches[k].square] if k < UNDER else []
        _floor(b, _walk_holes(room, *below), z0, inner, detail)
        b.flat(_walk_holes(room, hatches[k - 1].square), z1, False, hex_to_rgb(CEILING))
        if detail:
            _timber(b, room, inner, {}, z0, z1, _level_keep(k, room, inner, hatches, door_xy), [hatches[k - 1].square], lamp, props)
        top = z1 + SLAB + (FLOOR_Z if k == 1 else 0.0)
        _tube(b, hatches[k - 1].square, z1, top, hex_to_rgb(INNER) if detail else concrete)
        if detail:
            _ladder(b, hatches[k - 1], z0, -STOREY * (k - 1) + (FLOOR_Z if k == 1 else 0.0))
            _lamp(b, lamp, z1)


def _enterable(
    b: _Builder, room: Polygon, outer: list[_Edge], inner: list[_Edge], holes: list[_Opening],
    detail: bool, hatches: list[_Hatch] | None = None, lamp=None, props: list[dict] | None = None,
) -> None:
    concrete, slit = hex_to_rgb(CONCRETE), hex_to_rgb(SLIT)
    ceil_z = FLOOR_Z + CLEAR_H
    by_outer: dict[int, list] = {}
    by_inner: dict[int, list] = {}
    for o in holes:
        by_outer.setdefault(o.edge, []).append((o.a, o.b, o.z0, o.z1))
        by_inner.setdefault(o.inner, []).append((o.ia, o.ib, o.z0, o.z1))
    for e in outer:
        b.wall(e, [(0.0, ENTER_WALL_H, concrete)], by_outer.get(e.k, []), e.n)
    bands = [(FLOOR_Z, PAINT_H, hex_to_rgb(PAINT)), (PAINT_H, ceil_z, hex_to_rgb(INNER))]
    for i in inner:  # the room side of the walls: the inner ring's own normal points into the wall
        b.wall(i, bands if detail else [(FLOOR_Z, ceil_z, concrete)], by_inner.get(i.k, []), -i.n)
    b.flat(room, ceil_z, False, hex_to_rgb(CEILING))
    if detail:
        _floor(b, _walk_holes(room, hatches[0].square) if hatches else room, FLOOR_Z, inner, True)
    edge = {e.k: e for e in outer}
    d0 = holes[0]
    door_xy = edge[d0.edge].p + edge[d0.edge].t * (d0.a + d0.b) / 2
    props = props or []
    if detail:
        _timber(b, room, inner, by_inner, FLOOR_Z, ceil_z, _keepouts(0, hatches, door_xy), [], lamp, props)
    if hatches:
        _underground(b, room, inner, hatches, detail, lamp, door_xy, props)
    if detail and lamp is not None:
        _lamp(b, lamp, ceil_z)

    taken: dict[int, list] = {}
    for o in holes:  # the four faces through the wall
        e = edge[o.edge]
        taken.setdefault(e.k, []).append((o.a, o.b))
        back = -e.n * WALL_T
        pa, pb = e.p + e.t * o.a, e.p + e.t * o.b
        colour = concrete if o.door else slit
        b.quad((*pa, o.z0), (*(pa + back), o.z0), (*(pa + back), o.z1), (*pa, o.z1), (*e.t, 0), colour)
        b.quad((*pb, o.z0), (*(pb + back), o.z0), (*(pb + back), o.z1), (*pb, o.z1), (*-e.t, 0), colour)
        b.quad((*pa, o.z1), (*pb, o.z1), (*(pb + back), o.z1), (*(pa + back), o.z1), (0, 0, -1), colour)
        if detail or not o.door:  # the door's threshold is floor: drawn, never collided with
            sill = hex_to_rgb(FLOOR) if o.door else slit
            b.quad((*pa, o.z0), (*pb, o.z0), (*(pb + back), o.z0), (*(pa + back), o.z0), (0, 0, 1), sill)
    if not detail:
        return

    door = holes[0]
    e = edge[door.edge]
    frame = hex_to_rgb(DOOR_FRAME)
    b.plate(e, door.a - 0.1, door.a, 0.0, DOOR_H + 0.1, 0.05, frame)
    b.plate(e, door.b, door.b + 0.1, 0.0, DOOR_H + 0.1, 0.05, frame)
    b.plate(e, door.a, door.b, DOOR_H, DOOR_H + 0.1, 0.05, frame)
    taken[e.k].append((door.a - 0.1, door.b + 0.1))
    # the steel door, swung right back against the wall beside the doorway (where there is room)
    for lo, hi in ((door.b + 0.1, door.b + 0.1 + DOOR_W), (door.a - 0.1 - DOOR_W, door.a - 0.1)):
        if lo >= 0.05 and hi <= e.ln - 0.05:
            b.plate(e, lo, hi, FLOOR_Z, DOOR_H, 0.06, hex_to_rgb(DOOR_LEAF))
            taken[e.k].append((lo, hi))
            break
    _streaks(b, outer, ENTER_WALL_H, taken, hex_to_rgb(STREAK))


def _level_keep(level: int, room: Polygon, inner: list[_Edge], hatches: list[_Hatch] | None, door_xy) -> list:
    """``_keepouts`` and, in the command post, the strip in front of the intel screen: where nothing stands."""
    keep = _keepouts(level, hatches, door_xy)
    if hatches and level == UNDER and (screen := _intel_screen(room, inner, hatches)):
        keep.append(screen.zone)  # the wall the intel screen hangs on stays free of furniture
    return keep


def _props(room: Polygon, outer: list[_Edge], inner: list[_Edge], holes: list[_Opening], hatches: list[_Hatch] | None) -> list[dict]:
    """The furniture of every floor of an enterable bunker, ground floor first (see ``_furnish``)."""
    edge = {e.k: e for e in outer}
    d0 = holes[0]
    door_xy = edge[d0.edge].p + edge[d0.edge].t * (d0.a + d0.b) / 2
    inner_by_k = {i.k: i for i in inner}
    slits = [(inner_by_k[o.inner], (o.ia + o.ib) / 2) for o in holes if not o.door]
    out = _furnish(room, inner, 0, _keepouts(0, hatches, door_xy), FLOOR_Z, slits)
    for k in range(1, UNDER + 1) if hatches else ():
        out += _furnish(room, inner, k, _level_keep(k, room, inner, hatches, door_xy), -STOREY * k, [])
    return out


def _plan(poly: Polygon, outer: list[_Edge], door_dir):
    """The room, its wall edges, the openings and the hatches (None: no way down) of an enterable
    bunker on ``poly``; None when the footprint leaves no room worth entering or no wall facing
    ``door_dir`` takes the door."""
    room = poly.buffer(-WALL_T, join_style="mitre")
    if not isinstance(room, Polygon) or room.interiors or room.area < MIN_ROOM_M2:
        return None
    room = orient(room, 1.0)
    inner = _edges(np.array(room.exterior.coords)[:-1])
    holes = _openings(outer, inner, door_dir)
    if not holes:
        return None
    e = next(e for e in outer if e.k == holes[0].edge)
    door_xy = e.p + e.t * (holes[0].a + holes[0].b) / 2
    return room, inner, holes, _hatches(room, door_xy)


def _shelter_ring(room: Polygon) -> list[list[float]]:
    """The room grown into its walls (``SHELTER_GROW``), as the rounded ring ``bunkers.json`` carries."""
    grown = orient(room.buffer(SHELTER_GROW, join_style="mitre"), 1.0)
    return [[round(x, 3), round(y, 3)] for x, y in list(grown.exterior.coords)[:-1]]


def bunker_mesh(ring, door_dir, detail: bool = True, enterable: bool = True) -> Mesh:
    poly = orient(Polygon(ring), 1.0)
    pts = np.array(poly.exterior.coords)[:-1]
    outer = _edges(pts)
    b = _Builder()
    plan = _plan(poly, outer, door_dir) if enterable else None
    if plan:
        room, inner, holes, hatches = plan
        _enterable(b, room, outer, inner, holes, detail, hatches, lamp_xy(_shelter_ring(room)), _props(room, outer, inner, holes, hatches))
    else:
        _closed(b, poly, outer, door_dir, detail)
    _dome(b, pts, np.array(poly.centroid.coords[0]), ENTER_WALL_H if plan else WALL_H, hex_to_rgb(DOME))
    return b.mesh()


def bunker_rooms(ring, door_dir) -> dict:
    """What the game needs to know of the enterable bunker on ``ring``, for ``bunkers.json``; ``{}`` for a
    bunker that stays closed. Everything is in the frame of ``ring``, heights relative to the building's base.

    - ``floors``: one room per floor, ``level`` 0 (ground) down to ``-UNDER``. The game shelters a player in
      a room from a blast outside it. ``polygon`` is the room grown ``SHELTER_GROW`` into its walls, and the
      heights reach a little below the floor and into the slab above, so a blast on a wall's inner face counts
      as inside and one on its outer face as outside.
    - ``ladders``: where a climber stands (``x``, ``y``), the way they face (``dx``, ``dy``) and the heights of the
      floor below and the floor above (``z0``, ``z1``).
    - ``props``: the furniture of every floor, ``atlas`` the texture the models wear (see ``_furnish``).
    - ``hatch``: the hole in the ground floor, which the terrain is cut by.
    - ``intel`` (when a wall of the command post takes one): where the intel screen hangs, on the lowest floor.
      ``x``, ``y`` is the middle of the screen on the wall, ``dx``, ``dy`` the way it faces (into the room),
      ``z0``, ``z1`` its lower and upper edge and ``width`` its width. The game draws the screen there.
    """
    poly = orient(Polygon(ring), 1.0)
    poly_pts = np.array(poly.exterior.coords)[:-1]
    plan = _plan(poly, _edges(poly_pts), door_dir)
    if not plan:
        return {}
    room, inner_e, holes, hatches = plan
    polygon = _shelter_ring(room)
    levels = range(UNDER + 1) if hatches else range(1)
    floors = [
        {
            "level": -k,
            "polygon": polygon,
            "z0": round(-STOREY * k + (FLOOR_Z if k == 0 else 0) - SHELTER_GROW, 3),
            "z1": round(-STOREY * k + (FLOOR_Z if k == 0 else 0) + CLEAR_H + SHELTER_GROW, 3),
        }
        for k in levels
    ]
    out: dict = {"floors": floors, "ladders": [], "hatch": [], "atlas": PROP_ATLAS,
                 "props": _props(room, _edges(poly_pts), inner_e, holes, hatches)}
    for j, h in enumerate(hatches or []):
        out["ladders"].append(
            {
                "x": round(float(h.rail[0]), 3),
                "y": round(float(h.rail[1]), 3),
                "dx": round(float(h.facing[0]), 4),
                "dy": round(float(h.facing[1]), 4),
                "z0": round(-STOREY * (j + 1), 3),
                "z1": round(-STOREY * j + (FLOOR_Z if j == 0 else 0), 3),
            }
        )
    if hatches:
        out["hatch"] = [[round(x, 3), round(y, 3)] for x, y in list(hatches[0].square.exterior.coords)[:-1]]
        if screen := _intel_screen(room, inner_e, hatches):
            out["intel"] = _screen_export(screen)
    return out
