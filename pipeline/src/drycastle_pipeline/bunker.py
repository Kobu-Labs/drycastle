"""WW2 bunker model: the footprint extruded with a rounded concrete dome and pale weathering streaks.

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
from dataclasses import dataclass
from itertools import pairwise

import numpy as np
from shapely.geometry import Point, Polygon
from shapely.geometry.polygon import orient

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meshes import Mesh, triangulate

CONCRETE, DOME, STREAK = "#a9a698", "#8d8e76", "#d8d5ca"
DOOR_FRAME, DOOR_LEAF, SLIT = "#6e4a35", "#2a2420", "#1b1b1b"
INNER, PAINT, CEILING, FLOOR = "#b4b1a4", "#5a6150", "#9d9a8e", "#6a6860"

WALL_H = 1.8  # vertical wall of the closed form, metres
RISE = 1.0  # dome height above the wall
RINGS = 4  # dome steps
DOME_END = 0.48  # scale of the flat cap relative to the footprint

WALL_T = 0.5  # wall thickness of the enterable form
FLOOR_Z = 0.04  # the drawn floor, just above the flattened ground
CLEAR_H = 2.1  # floor to ceiling; the player stands 1.8 m
ENTER_WALL_H = 2.3  # vertical wall of the enterable form: the room plus the roof slab
PAINT_H = 0.9  # top of the dark band painted round the room
DOOR_W, DOOR_H = 1.0, 2.0  # the doorway, always open; the player is 0.7 m wide
SLIT_W, SLIT_Z0, SLIT_Z1 = 0.5, 1.0, 1.25  # a firing slit, at the eye height of a crouched player
MIN_ROOM_M2 = 6.0  # a smaller room is not worth entering: the bunker stays closed
EDGE_MARGIN = 0.1  # an opening keeps this far from the ends of its inner wall
SHELTER_GROW = 0.15  # how far a room's shelter volume reaches into its walls, floor and roof
EPS = 1e-6

UNDER = 3  # floors below the ground floor, one room each under the same footprint
STOREY = 2.4  # floor to floor: CLEAR_H of room and a slab
SLAB = STOREY - CLEAR_H
HATCH = 1.0  # the square hole in a floor that the ladder goes through
HATCH_MARGIN = 0.05  # a hatch keeps this far from the walls of its corner
HATCH_DOOR_CLEAR = 1.8  # the ground floor hatch keeps this far from the doorway
HATCH_APART = 2.5  # the hatch of a floor is at least this far from the one above it
HATCH_SLIDE = 0.25  # steps in which a hatch is tried along a wall
RAIL_SET = 0.45  # the climber stands this far inside the hatch from the edge the ladder hangs on
STEP_OFF = 1.0  # at the top the player is put this far past the climbing spot, on the floor beyond the edge
STEEL, RUNG = "#4b5357", "#6b6f6c"
LAMP_CAGE, LAMP_GLOW = "#2f3133", "#f0c987"
LAMP_SIZE = 0.3  # a caged lamp hangs under the ceiling of every room
LANDING = 0.9  # props stay this far from the hatches (and from the ladder landings next to them)
PROP_GAP = 0.03  # between a prop and its wall
# furnishing per level (0 ground, 1..3 down): (name, width along the wall, depth, height, colour)
FURNITURE = {
    0: [],  # the ground floor: ammunition boxes under the firing slits (see _furnish)
    1: [  # crew quarters
        ("bunk", 1.9, 0.8, 1.6, "#4d5241"), ("bunk", 1.9, 0.8, 1.6, "#4d5241"), ("locker", 0.5, 0.5, 1.9, "#6d7466"),
        ("locker", 0.5, 0.5, 1.9, "#6d7466"), ("locker", 0.5, 0.5, 1.9, "#6d7466"),
    ],
    2: [  # ammunition store
        ("shelf", 1.4, 0.4, 1.9, "#5a4a35"), ("crate", 0.8, 0.6, 0.8, "#6b5a3c"), ("crate", 0.8, 0.6, 0.8, "#6b5a3c"),
        ("rack", 1.2, 0.25, 1.2, "#3c3f36"), ("crate", 0.8, 0.6, 0.55, "#6b5a3c"),
    ],
    3: [  # command post
        ("table", 1.6, 0.9, 0.85, "#5f5a4a"), ("generator", 1.0, 0.6, 0.8, "#4a5a45"), ("radio", 0.5, 0.4, 1.2, "#3c423c"),
        ("locker", 0.5, 0.5, 1.9, "#6d7466"),
    ],
}
AMMO = (0.6, 0.35, 0.35, "#5b6240")  # ground floor ammunition box: width, depth, height, colour


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
    b: _Builder, room: Polygon, edges: list[_Edge], level: int, keep: list, z: float,
    slits: list[tuple[_Edge, float]],
) -> None:
    """The furniture of a floor, against its walls and clear of ``keep`` (hatches, landings, the doorway).
    Level 0 gets ammunition boxes under each firing slit; the others the ``FURNITURE`` of their purpose."""
    inside = room.buffer(-0.03, join_style="mitre")
    placed: list[Polygon] = []

    def put(e: _Edge, s0: float, w: float, d: float, h: float, colour: str) -> bool:
        base = e.p + e.t * s0 + (-e.n) * PROP_GAP
        fp = Polygon([base, base + e.t * w, base + e.t * w - e.n * d, base - e.n * d])
        if not inside.contains(fp) or any(fp.intersects(k) for k in keep) or any(fp.intersects(q) for q in placed):
            return False
        placed.append(fp)
        b.obox(base + e.t * w / 2 - e.n * d / 2, e.t, -e.n, w / 2, d / 2, z, z + h, hex_to_rgb(colour), bottom=False)
        return True

    if level == 0:
        w, d, h, colour = AMMO
        for e, mid in slits:
            put(e, mid - w / 2, w, d, h, colour)
        return
    for _name, w, d, h, colour in FURNITURE[level]:
        done = False
        for e in sorted(edges, key=lambda e: (-e.ln, e.k)):
            if e.ln < w + 0.2:
                continue
            for s0 in np.arange(0.1, e.ln - w - 0.1 + EPS, 0.25):
                if put(e, float(s0), w, d, h, colour):
                    done = True
                    break
            if done:
                break


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


def lamp_xy(grown_ring) -> tuple[float, float]:
    """Where the lamp of a room hangs: the mean of its shelter polygon's corners. The game hangs its light at the same place."""
    pts = np.asarray(grown_ring, float)
    return float(pts[:, 0].mean()), float(pts[:, 1].mean())


def _underground(
    b: _Builder, room: Polygon, inner: list[_Edge], hatches: list[_Hatch], detail: bool, door_xy, lamp,
) -> None:
    """One room per floor under the ground floor, joined by the hatches. Floors, ceilings and walls are
    always built: they are the collision of the way down."""
    concrete = hex_to_rgb(CONCRETE)
    for k in range(1, UNDER + 1):
        z0, z1 = -STOREY * k, -STOREY * k + CLEAR_H
        bands = [(z0, z0 + PAINT_H, hex_to_rgb(PAINT)), (z0 + PAINT_H, z1, hex_to_rgb(INNER))]
        for i in inner:
            b.wall(i, bands if detail else [(z0, z1, concrete)], [], -i.n)
        below = [hatches[k].square] if k < UNDER else []
        b.flat(_walk_holes(room, *below), z0, True, hex_to_rgb(FLOOR))
        b.flat(_walk_holes(room, hatches[k - 1].square), z1, False, hex_to_rgb(CEILING))
        top = z1 + SLAB + (FLOOR_Z if k == 1 else 0.0)
        _tube(b, hatches[k - 1].square, z1, top, hex_to_rgb(INNER) if detail else concrete)
        _furnish(b, room, inner, k, _keepouts(k, hatches, door_xy), z0, [])
        if detail:
            _ladder(b, hatches[k - 1], z0, -STOREY * (k - 1) + (FLOOR_Z if k == 1 else 0.0))
            _lamp(b, lamp, z1)


def _enterable(
    b: _Builder, room: Polygon, outer: list[_Edge], inner: list[_Edge], holes: list[_Opening],
    detail: bool, hatches: list[_Hatch] | None = None, lamp=None,
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
        b.flat(_walk_holes(room, hatches[0].square) if hatches else room, FLOOR_Z, True, hex_to_rgb(FLOOR))
    edge = {e.k: e for e in outer}
    d0 = holes[0]
    door_xy = edge[d0.edge].p + edge[d0.edge].t * (d0.a + d0.b) / 2
    inner_by_k = {i.k: i for i in inner}
    slits = [(inner_by_k[o.inner], (o.ia + o.ib) / 2) for o in holes if not o.door]
    _furnish(b, room, inner, 0, _keepouts(0, hatches, door_xy), FLOOR_Z, slits)
    if hatches:
        _underground(b, room, inner, hatches, detail, door_xy, lamp)
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
        _enterable(b, room, outer, inner, holes, detail, hatches, lamp_xy(_shelter_ring(room)))
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
    - ``hatch``: the hole in the ground floor, which the terrain is cut by.
    """
    poly = orient(Polygon(ring), 1.0)
    plan = _plan(poly, _edges(np.array(poly.exterior.coords)[:-1]), door_dir)
    if not plan:
        return {}
    room, _, _, hatches = plan
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
    out: dict = {"floors": floors, "ladders": [], "hatch": []}
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
    return out
