"""The floor plan of a hollow house (buildings/interior.py), generated: a hall from the front door, the rooms of the
house's programme (buildings/roomtypes.py), the partitions between them and the doorways that join them. Nothing is
drawn by hand and nothing is stored: the plan is worked out from the footprint, the openings and a seed (the building's
id), so the same house always gets the same plan.

How (all in the *entry frame*: square to the longest wall, x running inward from the front door, so every partition is
parallel or square to the house's main walls):

1. **Openness**, 0..1 from the seed, picks the style: *closed* (a corridor, a door to every room), *mixed* (a shorter
   hall, the living room and the kitchen joined by an archway) or *open* (one space to live and cook in, a vestibule at
   most, the other rooms off that space).
2. **Hall**: a strip from the front door inward (the stem). When the door is in a long wall and the house is deep enough
   the stem ends in a corridor along the house (the spine). A closed house too shallow for that is planned twice, with
   a corridor along the door's wall as well. A strip that would leave a sliver beside it is widened to the wall instead.
   Each house is planned many times (the long hall, an entry only, no hall; TRIES of each) and the plan with the fewest
   faults (``_faults``) is kept.
3. **Programme**: the rooms the house is to have and how large each should be, from the floor left beside the hall
   (roomtypes.programme): rooms of unequal size, as their uses are.
4. **Rooms**: each piece of floor beside the hall gets its share of the programme, the largest rooms in the largest
   pieces, and is cut to it: the list is halved, the piece cut where the halves' sizes say (moved to where no window
   is in the way and where walls line up), each half again. What is public ends up toward the front door.
5. **Doorways**: a tree, as in a house. Every room has one way in: from the hall where it touches it, else from the
   living room, else from the room that suits it (a bathroom from a bedroom, a pantry from the kitchen). Only the
   living room, the kitchen and the dining room open into each other. A room nothing can reach is merged into its
   neighbour.
"""

import math
import random
import zlib
from dataclasses import dataclass, field
from itertools import pairwise

from shapely.affinity import rotate
from shapely.geometry import LineString, Point, Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings import roomtypes as rt
from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

PARTITION_M = 0.1
DOOR_W_M, DOOR_H_M, ARCH_H_M = 0.9, 2.0, 2.2
HALL_W_M = (1.2, 1.5)
SLIVER_M = 1.7  # a strip narrower than this between the hall and a wall is made part of the hall
MIN_PIECE_M2 = 5.0  # floor beside the hall smaller than this is hall
MIN_ROOM_M2 = 2.0
CLEAR_M = 0.15  # a partition ends at least this far from a window's or a door's side
CLEAR_LOOSE_M, POST_M = 0.07, 0.04  # when nothing else fits: nearer, or on a window's post (within POST_M of its middle)
SPINE_MIN_DEPTH_M = 6.5  # a corridor along the house needs rooms on both sides of it
SNAP_M = 0.4  # a cut this near a corner of the piece is moved onto it
SLIM, SLIM_LOOSE = 2.4, 3.6  # a room is not longer than this many times its width; when nothing else fits
SIDE_ROOM_DEPTH_M = 2.6  # a corridor along the door's wall needs rooms this deep behind it
SMALL_MOST_M2 = 6.5  # a bathroom or a closet is asked for no larger than this when floor is cut a second time
HALL_MOST_W_M = 2.8  # a stem widened past this (a narrow wing, wall to wall) is no hall: the door opens into a room
CORRIDOR_ROOM_DEPTH_M = 2.2  # a corridor to rooms nothing else reaches makes do with less
NO_HALL_BELOW_M2 = 80.0  # an open house smaller than this has no hall: the door opens into the living space
MAX_DWELLING_M2 = 350.0  # more floor than this is no house (a culture house, a farm hall): no plan, one open space
TRIES = 4  # plans drawn at most for each kind of hall; the one with the fewest faults is kept
GOOD_FAULTS = 0.8  # a plan with so few ends the search
HALL_SHARE, HALL_FREE_M2, HALL_PER_DOOR_M2 = 0.12, 4.0, 3.0  # the floor a hall may take before it counts as waste
MOST_M2 = {rt.LIVING: 45.0, rt.LIVING_KITCHEN: 60.0, rt.KITCHEN: 20.0, rt.DINING: 20.0, rt.BEDROOM: 22.0, rt.STUDY: 16.0, rt.BATHROOM: 9.0, rt.STORAGE: 7.0}


@dataclass
class Link:
    """A way between two rooms: ``kind`` door, arch or open (no wall), over ``p``..``q`` of their shared wall."""

    a: int
    b: int
    kind: str
    p: tuple[float, float]
    q: tuple[float, float]


@dataclass
class Wall:
    """A partition, ``p`` to ``q`` along its middle; ``gaps`` are (from, to, kind) in metres from ``p``."""

    p: tuple[float, float]
    q: tuple[float, float]
    gaps: list[tuple[float, float, str]] = field(default_factory=list)


@dataclass
class Room:
    id: str
    kind: str  # hall | room
    polygon: Polygon
    windows: list[str] = field(default_factory=list)
    doors: list[str] = field(default_factory=list)  # doors to the outside
    type: str = ""  # what the room is for (buildings/roomtypes.py)


@dataclass
class Plan:
    openness: float
    style: str
    rooms: list[Room]
    walls: list[Wall]
    links: list[Link]


def style_of(openness: float) -> str:
    return "closed" if openness < 0.4 else "mixed" if openness < 0.75 else "open"


def _shared(a: Polygon, b: Polygon) -> list[tuple[tuple[float, float], tuple[float, float]]]:
    """The straight pieces of the boundary two cells share."""
    line = a.boundary.intersection(b.boundary.buffer(1e-4))
    out = []
    for g in getattr(line, "geoms", [line]):
        if g.geom_type != "LineString" or g.length < 0.05:
            continue
        cs = list(g.simplify(1e-3).coords)
        out += [(p, q) for p, q in pairwise(cs) if math.dist(p, q) >= 0.05]
    # pieces that follow one another on one line are one wall
    merged: list[list] = []
    for p, q in sorted(out, key=lambda s: (min(s), max(s))):
        p, q = min(p, q), max(p, q)
        if merged and math.dist(merged[-1][1], p) < 1e-3 and abs((merged[-1][1][0] - merged[-1][0][0]) * (q[1] - p[1]) - (merged[-1][1][1] - merged[-1][0][1]) * (q[0] - p[0])) < 1e-3:
            merged[-1][1] = q
        else:
            merged.append([p, q])
    return [(tuple(p), tuple(q)) for p, q in merged]


def _slivers(g, side: float) -> list[Polygon]:
    """The parts of a shape too narrow to be (part of) a room: what a square of ``side`` cannot be pushed into."""
    r = side / 2 - 0.05
    return [s for s in _polygons(g.difference(g.buffer(-r, join_style=2).buffer(r, join_style=2).buffer(0.01, join_style=2))) if s.area > 0.3]


def _shared_len(a: Polygon, b: Polygon) -> float:
    return max((math.dist(p, q) for p, q in _shared(a, b)), default=0.0)


class _Maker:
    def __init__(self, inner: Polygon, openings: list, entry, rng: random.Random, style: str):
        self.f, self.ops, self.entry, self.rng, self.style = inner, openings, entry, rng, style
        self.edge = inner.boundary.buffer(1e-4)

    def clear(self, cut, loose: bool = False) -> bool:
        """Does no end of these partition lines land on a window or a door (or within CLEAR_M of its side)? ``loose``: a
        partition may also meet a window at one of its posts, and come nearer its side."""
        ends = cut.difference(self.edge)
        for g in getattr(ends, "geoms", [ends]):
            if g.geom_type != "LineString" or g.length < 0.05:
                continue
            for end in (g.coords[0], g.coords[-1]):
                pt = Point(end)
                if self.f.boundary.distance(pt) > 0.02:
                    continue  # it ends on another partition
                for _, _, seg, posts in self.ops:
                    if seg.distance(pt) < (CLEAR_LOOSE_M if loose else CLEAR_M) and not (loose and any(post.distance(pt) < POST_M for post in posts)):
                        return False
        return True

    def _run(self, c, d) -> float:
        """How far the room goes from ``c`` in direction ``d``."""
        ray = LineString([(c[0] + d[0] * 0.02, c[1] + d[1] * 0.02), (c[0] + d[0] * 500, c[1] + d[1] * 500)]).intersection(self.f)
        first = min(getattr(ray, "geoms", [ray]), key=lambda g: Point(c).distance(g), default=None)
        return first.length if first is not None and not first.is_empty else 0.0

    def _runs(self) -> tuple[float, float, float]:
        cx, cy = self.entry
        return self._run((cx, cy), (1, 0)), self._run((cx + 0.3, cy), (0, -1)), self._run((cx + 0.3, cy), (0, 1))

    def side_fits(self) -> bool:
        """Can the hall be a corridor along the front door's wall? A closed house too shallow for a spine, with rooms
        of a fair depth left behind the corridor."""
        if self.entry is None or self.style != "closed":
            return False
        reach, left, right = self._runs()
        return reach < 0.75 * (left + right) and reach < SPINE_MIN_DEPTH_M and reach - HALL_W_M[1] >= SIDE_ROOM_DEPTH_M

    def hall(self, kind: str = "stem") -> Polygon | None:
        """The hall of one kind: ``stem`` (see above), ``side`` (a corridor along the door's wall), ``vestibule`` (an entry
        only), or ``none``."""
        side = kind == "side"
        if kind == "none" or self.entry is None or (self.style == "open" and self.f.area < NO_HALL_BELOW_M2):
            return None
        cx, cy = self.entry
        rng = self.rng
        reach, left, right = self._runs()
        along = left + right
        door_w = min(self.ops, key=lambda o: o[2].distance(Point(cx, cy)))[2].length
        w = max(rng.uniform(*HALL_W_M), door_w + 2 * CLEAR_M + 0.1)  # (its sides clear of the door's own)
        end_door = reach >= 0.75 * along  # the door is in an end wall: the hall runs down the house
        if side:
            stem, spine = w, True  # the spine alone, against the door's wall
        elif self.style == "open" or kind == "vestibule":
            stem, spine = min(max(reach * 0.2, 1.8), 3.2), False  # a vestibule
        elif end_door:
            stem, spine = reach * (rng.uniform(0.6, 0.85) if self.style == "closed" else rng.uniform(0.4, 0.6)), False
        elif reach >= SPINE_MIN_DEPTH_M:
            stem, spine = reach / 2 + w / 2, True
        else:
            stem, spine = min(2.4, reach * 0.5), False
        # each end of the spine: at the end wall, or short of it by a room that then takes the whole depth of the house
        ends = []
        for run in (left, right):
            short = run - rng.uniform(2.6, 3.4) - (rng.uniform(1.0, 3.0) if self.style == "mixed" else 0.0)
            opts = [run + 0.5, short] if short >= 2.0 else [run + 0.5]
            ends.append(opts if rng.random() < (0.5 if self.style == "closed" else 0.15) else opts[::-1])
        nudges = (0.0, 0.15, -0.15, 0.3, -0.3, 0.45, -0.45, 0.6, -0.6)
        base_stem = stem
        combos = [(a, b, l, r) for l in ends[0] for r in ends[1] for a in nudges for b in ((0.0,) if side else nudges)] if spine else [(a, 0.0, 0.0, 0.0) for a in nudges]
        tries = []
        for shift, push, end_l, end_r in sorted(combos, key=lambda t: abs(t[0]) + abs(t[1]) + 2.0 * (ends[0].index(t[2]) + ends[1].index(t[3]) if spine else 0)):
            stem = base_stem + push  # (moves the spine across the house)
            y0, y1 = cy - w / 2 + shift, cy + w / 2 + shift
            if left - (cy - y0) < SLIVER_M:
                y0 = cy - left - 0.5
            if right - (y1 - cy) < SLIVER_M:
                y1 = cy + right + 0.5
            if not spine and min(y1, cy + right) - max(y0, cy - left) > HALL_MOST_W_M:
                continue
            parts = [box(cx - 3.0, y0, cx + stem, y1)]  # (from behind the door: its wall may stand at a slant in the frame)
            if spine:
                parts.append(box(cx + stem - w, cy - end_l, cx + stem, cy + end_r))
            door = Point(cx, cy)
            h = min(_polygons(unary_union(parts).intersection(self.f)), key=lambda g: g.distance(door), default=None)
            if h is None or h.area < 2.0:
                continue
            rest = self.f.difference(h)
            crumbs = [r for r in _polygons(rest) if r.area < MIN_PIECE_M2] + _slivers(rest, 1.8)  # too small or too narrow for a room: hall
            if crumbs:
                grown = unary_union([h, *crumbs]).buffer(0.01, join_style=2).buffer(-0.01, join_style=2)  # (closes the seams)
                h = min(_polygons(grown.intersection(self.f)), key=lambda g: g.distance(door))
            if h.area > 0.6 * self.f.area or (not spine and h.area > HALL_MOST_W_M * (stem + 0.5)):
                continue  # (a hall that is most of the house, or a whole wing of it, is no hall)
            tries.append(h)
            if self.clear(h.boundary):
                return h
        return tries[0] if tries else None

    def corridor(self, region: Polygon, contact) -> Polygon | None:
        """A corridor into ``region`` (floor whose rooms nothing reached), from ``contact`` (the piece of its edge a room that
        is reached lies behind): a strip along one long side of the region, the side with fewer windows, stopping a room
        short of the far end. None when the region is too shallow to keep rooms beside the strip."""
        x0, y0, x1, y1 = region.bounds
        axis = 0 if x1 - x0 >= y1 - y0 else 1  # the corridor runs along the region
        lo, hi = (x0, x1) if axis == 0 else (y0, y1)
        c0, c1 = (y0, y1) if axis == 0 else (x0, x1)
        w = self.rng.uniform(*HALL_W_M)
        if c1 - c0 - w < CORRIDOR_ROOM_DEPTH_M or hi - lo < 5.0:
            return None
        p, q = contact
        mid = ((p[0] + q[0]) / 2, (p[1] + q[1]) / 2)
        near_lo = abs(mid[axis] - lo) <= abs(mid[axis] - hi)  # the end the reached room is at
        stop = self.rng.uniform(2.6, 3.4)
        a0, a1 = (lo - 0.5, hi - stop) if near_lo else (lo + stop, hi + 0.5)
        found = []
        for low_side in (True, False):
            for shift in (0.0, 0.15, 0.3, 0.45, 0.6):  # (wider when its partition would end in a window)
                b0, b1 = (c0 - 0.5, c0 + w + shift) if low_side else (c1 - w - shift, c1 + 0.5)
                strip = box(a0, b0, a1, b1) if axis == 0 else box(b0, a0, b1, a1)
                got = max(_polygons(strip.intersection(region)), key=lambda g: g.area, default=None)
                if got is None or got.area < 3.0 or got.distance(LineString([p, q])) > 0.05:
                    continue
                windows = sum(1 for _, kind, seg, _ in self.ops if kind == "window" and got.boundary.distance(seg.interpolate(0.5, normalized=True)) < 0.05)
                found.append((not self.clear(got.boundary), windows, shift, got))
                break
        return min(found, key=lambda t: t[:3])[3] if found else None

    def fill(self, piece: Polygon, wants: list, hall: Polygon | None) -> list[tuple[Polygon, str]]:
        """``piece`` cut into the rooms of ``wants`` (in order from the front door), each about its wanted share."""
        wants = rt.scaled(wants, piece.area)
        if len(wants) == 1:
            return [(piece, wants[0].type)]
        total = sum(w.area for w in wants)
        k = min(range(1, len(wants)), key=lambda k: abs(sum(w.area for w in wants[:k]) / total - 0.5))
        groups = (wants[:k], wants[k:])
        share = sum(w.area for w in groups[0]) / total
        door = Point(self.entry) if self.entry is not None else piece.centroid
        best = None
        x0, y0, x1, y1 = piece.bounds
        for loose, axis in ((l, a) for l in (False, True) for a in (0, 1)):
            if loose and best is not None:
                break
            lo, hi = ((x0, x1), (y0, y1))[axis]
            slim_most = SLIM_LOOSE if loose else SLIM
            def halves(c: float, axis=axis):
                return (box(x0 - 1, y0 - 1, c, y1 + 1), box(c, y0 - 1, x1 + 1, y1 + 1)) if axis == 0 else (box(x0 - 1, y0 - 1, x1 + 1, c), box(x0 - 1, c, x1 + 1, y1 + 1))

            n = max(int((hi - lo) / 0.1), 1)
            below = [(lo + (hi - lo) * i / n, piece.intersection(halves(lo + (hi - lo) * i / n)[0]).area / piece.area) for i in range(n + 1)]
            corners = sorted({round(p[axis], 6) for p in piece.exterior.coords})
            for first_low in (True, False):
                frac = share if first_low else 1 - share
                want = min(below, key=lambda t: abs(t[1] - frac))[0]
                reach = (hi - lo) if loose else max(1.0, 0.3 * (hi - lo))
                tried = set()
                for c in sorted((c for c, _ in below), key=lambda c: abs(c - want)):
                    if abs(c - want) > reach:
                        break
                    near = min(corners, key=lambda v: abs(v - c))
                    if abs(near - c) < SNAP_M and lo < near < hi:
                        c = near
                    if c in tried:
                        continue
                    tried.add(c)
                    kids = [k for h in halves(c) for k in _polygons(piece.intersection(h))]
                    if len(kids) != 2:
                        continue
                    low, high = (kids[0], kids[1]) if kids[0].bounds[axis] <= kids[1].bounds[axis] else (kids[1], kids[0])
                    pair = ((low, groups[0]), (high, groups[1])) if first_low else ((high, groups[0]), (low, groups[1]))
                    ok = True
                    for kid, group in pair:
                        side_m = max(w.min_side for w in group)  # deep enough for every room it is to hold
                        kw, kh = kid.bounds[2] - kid.bounds[0], kid.bounds[3] - kid.bounds[1]
                        asked = sum(w.area for w in group)
                        if kid.area < max(sum(w.min_area for w in group), (0.3 if loose else 0.55) * asked) or kid.area > (3.0 if loose else 1.7) * asked or min(kw, kh) < side_m:
                            ok = False
                        elif max(kw, kh) / min(kw, kh) > slim_most * len(group):
                            ok = False  # a room like a corridor, or a row of them
                        elif sum(s.area for s in _slivers(kid, min(side_m, 1.8))) > sum(s.area for s in _slivers(piece, min(side_m, 1.8))) + 0.2:
                            ok = False  # the cut would leave a room with a narrow arm
                    line = LineString([(c, y0 - 1), (c, y1 + 1)] if axis == 0 else [(x0 - 1, c), (x1 + 1, c)]).intersection(piece)
                    if not ok or not self.clear(line, loose):
                        continue
                    touch = sum(_shared_len(kid, hall) >= DOOR_W_M + 0.3 for kid, _ in pair) if hall is not None else 0
                    slim = sum(max(kid.bounds[2] - kid.bounds[0], kid.bounds[3] - kid.bounds[1]) / min(kid.bounds[2] - kid.bounds[0], kid.bounds[3] - kid.bounds[1]) for kid, g in pair if len(g) == 1)
                    order = 2.0 if pair[0][0].distance(door) <= pair[1][0].distance(door) else 0.0
                    score = touch * 10 + order - slim - abs(c - want) * 2.0
                    if best is None or score > best[0]:
                        best = (score, pair)
                    break
        if best is None:  # no cut fits: one room less (a small one first, never the living room), the others share its floor
            drop = min(range(len(wants)), key=lambda i: (wants[i].type in (rt.LIVING, rt.LIVING_KITCHEN), wants[i].type not in rt.SMALL, wants[i].area))
            return self.fill(piece, wants[:drop] + wants[drop + 1 :], hall)
        return [room for kid, group in best[1] for room in self.fill(orient(kid, 1.0), list(group), hall)]


# who a room is entered from, best first (the hall always comes first where the room touches it)
HOSTS = {
    rt.LIVING: (rt.HALL,),
    rt.LIVING_KITCHEN: (rt.HALL,),
    rt.KITCHEN: (rt.LIVING, rt.DINING, rt.HALL),
    rt.DINING: (rt.KITCHEN, rt.LIVING, rt.LIVING_KITCHEN, rt.HALL),
    rt.BATHROOM: (rt.HALL, rt.BEDROOM, rt.LIVING_KITCHEN, rt.LIVING),
    rt.STORAGE: (rt.HALL, rt.KITCHEN, rt.LIVING_KITCHEN, rt.BEDROOM, rt.LIVING, rt.DINING, rt.STUDY),
    rt.BEDROOM: (rt.HALL, rt.LIVING_KITCHEN, rt.LIVING, rt.DINING),
    rt.STUDY: (rt.HALL, rt.LIVING_KITCHEN, rt.LIVING, rt.DINING),
}
HOSTS[rt.HALL] = (rt.HALL, rt.LIVING_KITCHEN, rt.LIVING, rt.DINING, rt.KITCHEN)  # a corridor further in
LIVING_SPACES = (rt.LIVING, rt.LIVING_KITCHEN, rt.KITCHEN, rt.DINING)
CORRIDOR_FROM = HOSTS[rt.HALL]


def _links(cells: list[Polygon], types: list[str], root: int, rng: random.Random, style: str, by_the_book: bool = False):
    """One way into every cell, as HOSTS says; returns (links, the cells nothing reaches). ``by_the_book``: no way in
    through a room HOSTS does not name (a bedroom behind a bedroom), so such rooms count as not reached."""
    n = len(cells)
    shared = {(i, j): _shared(cells[i], cells[j]) for i in range(n) for j in range(i + 1, n)}
    links: list[Link] = []

    def longest(i, j):
        segs = shared[(min(i, j), max(i, j))]
        return max(segs, key=lambda s: math.dist(*s)) if segs else None

    def join(i, j) -> bool:
        seg = longest(i, j)
        if seg is None:
            return False
        p, q = seg
        length = math.dist(p, q)
        if length < DOOR_W_M + 0.3:
            return False
        both = {types[i], types[j]}
        if both <= set(LIVING_SPACES) and length >= 2.2:  # the rooms one lives in open into each other
            kind = "open" if style == "open" else "arch" if rng.random() < (0.85 if style == "mixed" else 0.35) else "door"
        elif rt.HALL in both and both & {rt.LIVING, rt.LIVING_KITCHEN} and length >= 2.2:
            kind = "arch" if rng.random() < {"closed": 0.15, "mixed": 0.6, "open": 0.9}[style] else "door"
        else:
            kind = "door"
        if kind == "open":
            s0, s1 = 0.0, length
        elif kind == "arch":
            w = min(length - 0.6, rng.uniform(1.5, 2.6))
            s0 = (length - w) / 2
            s1 = s0 + w
        else:
            s0 = 0.2 if rng.random() < 0.5 else length - DOOR_W_M - 0.2
            s1 = s0 + DOOR_W_M
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        links.append(Link(i, j, kind, (p[0] + ux * s0, p[1] + uy * s0), (p[0] + ux * s1, p[1] + uy * s1)))
        return True

    entered: dict[int, int] = {}  # room -> the room it is entered from
    reached, grew = {root}, True
    while grew:
        grew = False
        for strict in (True,) if by_the_book else (True, False):  # first by the book; then any room that is reached, rather than none
            for j in sorted(set(range(n)) - reached, key=lambda j: (types[j] not in LIVING_SPACES, -cells[j].area)):
                wanted = HOSTS.get(types[j], (rt.HALL,))
                private = (rt.BEDROOM, rt.STUDY)
                # (a bedroom may lie behind one other room, itself entered from the hall or a room one lives in, never deeper)
                hosts = [i for i in reached if types[i] in wanted] if strict else [
                    i for i in reached if types[i] not in rt.SMALL
                    and not (types[i] in private and types[j] in private and types[entered.get(i, root)] in private)
                ]  # fmt: skip
                hosts.sort(key=lambda i: (wanted.index(types[i]) if types[i] in wanted else 99, -(math.dist(*longest(i, j)) if longest(i, j) else 0)))
                via = next((i for i in hosts if join(i, j)), None)
                if via is not None:
                    entered[j] = via
                    reached.add(j)
                    grew = True
                    if not strict and types[via] == rt.BEDROOM and types[j] in private and types.count(rt.BEDROOM) > 1:
                        types[via] = rt.STUDY  # a room one walks through to another is no bedroom
            if grew:
                break
    if style == "closed":  # a kitchen entered from the living room often has its own door to the hall as well
        for j in range(n):
            if types[j] == rt.KITCHEN and types[root] == rt.HALL and not any({l.a, l.b} == {root, j} for l in links) and rng.random() < 0.5:
                join(root, j)
    return links, sorted(set(range(n)) - reached)


def _faults(cells: list, types: list, links: list, ops: list, entry, has_hall: bool, style: str) -> float:
    """What is wrong with a plan, in points: floor wasted on halls, rooms a house cannot do without, a front door into
    the wrong room, rooms too large or too thin, rooms without daylight, rooms walked through."""
    floor = sum(c.area for c in cells)
    windows = [seg.interpolate(0.5, normalized=True) for _, kind, seg, _ in ops if kind == "window"]
    halls = [i for i, t in enumerate(types) if t == rt.HALL]
    score = 15.0 * max(0.0, sum(cells[i].area for i in halls) / floor - HALL_SHARE)
    for i in halls:
        doors = sum(1 for l in links if i in (l.a, l.b))
        score += 0.15 * max(0.0, cells[i].area - HALL_FREE_M2 - HALL_PER_DOOR_M2 * doors)
    living = {rt.LIVING, rt.LIVING_KITCHEN} & set(types)
    score += 0.0 if living else 5.0
    score += 0.0 if {rt.KITCHEN, rt.LIVING_KITCHEN} & set(types) else 4.0
    score += 4.0 if floor >= 34.0 and rt.BATHROOM not in types else 0.0
    if entry is not None:
        first = min(range(len(cells)), key=lambda i: cells[i].distance(entry))
        score += 4.0 if types[first] not in (rt.HALL, rt.LIVING, rt.LIVING_KITCHEN) else 0.0
        score += 1.0 if style == "closed" and not has_hall and floor >= 45.0 else 0.0
    for i, (cell, kind) in enumerate(zip(cells, types)):
        if kind == rt.HALL:
            continue
        score += 0.2 * max(0.0, cell.area - MOST_M2[kind])
        c = list(cell.minimum_rotated_rectangle.exterior.coords)
        short, long = sorted((math.dist(c[0], c[1]), math.dist(c[1], c[2])))
        score += 0.6 * max(0.0, long / max(short, 0.1) - 2.2)
        if kind not in rt.SMALL and not any(cell.boundary.distance(w) < 0.05 for w in windows):
            score += {rt.BEDROOM: 1.5, rt.LIVING: 2.0, rt.LIVING_KITCHEN: 2.0}.get(kind, 0.6)
    for l in links:  # (links lead away from the root: l.a is the room walked through)
        if types[l.a] in (rt.BEDROOM, rt.STUDY) and types[l.b] != rt.BATHROOM:
            score += 1.0
    return score


def make_plan(b: dict, d, ins, wall_m: float) -> Plan | None:
    """The plan of building ``b`` (detail ``d``, room ``ins`` of interior.inside_of), in map coordinates; None when the
    footprint leaves no room inside its walls, or far too much to be a house."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    ring = list(poly.exterior.coords)[:-1]
    inner = max(_polygons(poly.buffer(-wall_m, join_style=2)), key=lambda g: g.area, default=None)
    if inner is None or not 8.0 <= inner.area <= MAX_DWELLING_M2:
        return None
    rng = random.Random(zlib.crc32(str(b.get("id", d.id)).encode()))
    openness = round(rng.random(), 3)
    style = style_of(openness)

    # the frame: square to the longest wall, so the partitions are; turned so that x runs inward from the front door
    i = max(range(len(ring)), key=lambda i: math.dist(ring[i], ring[(i + 1) % len(ring)]))
    base = math.atan2(ring[(i + 1) % len(ring)][1] - ring[i][1], ring[(i + 1) % len(ring)][0] - ring[i][0])
    ops, entry, angle = [], None, base
    for o in d.openings:
        if not ins.through(o) or not 0 <= o.edge < len(ring):
            continue
        p, q = ring[o.edge], ring[(o.edge + 1) % len(ring)]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        a = (p[0] + ux * o.at_m - uy * wall_m, p[1] + uy * o.at_m + ux * wall_m)
        c = (a[0] + ux * o.width_m, a[1] + uy * o.width_m)
        posts = [Point(a[0] + ux * o.width_m * t, a[1] + uy * o.width_m * t) for t in (o.posts or []) if o.kind == "window"]
        ops.append((o.id, o.kind, LineString([a, c]), posts))
        if o.kind == "door" and entry is None:
            entry = ((a[0] + c[0]) / 2, (a[1] + c[1]) / 2)
            angle = max((base + k * math.pi / 2 for k in range(4)), key=lambda t: math.cos(t) * -uy + math.sin(t) * ux)

    def to_frame(g):
        return rotate(g, -angle, origin=(0, 0), use_radians=True)

    def to_map(g):
        return rotate(g, angle, origin=(0, 0), use_radians=True)

    f = orient(to_frame(inner), 1.0)
    f_ops = [(i, k, to_frame(s), [to_frame(p) for p in posts]) for i, k, s, posts in ops]
    e = to_frame(Point(entry)) if entry is not None else None
    seed = rng.getrandbits(32)

    def build(variant: str, n: int):
        rng = random.Random(f"{seed}:{variant}:{n}")
        mk = _Maker(f, f_ops, (e.x, e.y) if e is not None else None, rng, style)
        hall = mk.hall(variant)
        pieces = sorted(_polygons(f.difference(hall)), key=lambda g: -g.area) if hall is not None else [f]
        wants = rt.programme(sum(p.area for p in pieces), style, rng)
        # the largest rooms into the pieces with the most floor left; each piece keeps the programme's order
        shares: list[list[int]] = [[] for _ in pieces]
        left = [p.area for p in pieces]
        for w in sorted(range(len(wants)), key=lambda w: -wants[w].area):
            k = max(range(len(pieces)), key=lambda k: left[k])
            shares[k].append(w)
            left[k] -= wants[w].area
        cells, types = ([hall], [rt.HALL]) if hall is not None else ([], [])
        for piece, share in zip(pieces, shares):
            given = [wants[w] for w in sorted(share)]
            if not given:  # more pieces than rooms: what its size suits
                given = [rt.Want(rt.STORAGE if piece.area < 7 else rt.STUDY if piece.area < 10 else rt.BEDROOM, piece.area)]
            for cell, kind in mk.fill(orient(piece, 1.0), given, hall):
                cells.append(orient(cell, 1.0))
                types.append(kind)

        def root_of() -> int:
            living = [i for i, t in enumerate(types) if t in (rt.LIVING, rt.LIVING_KITCHEN)]
            return 0 if hall is not None else min(living or range(len(cells)), key=lambda i: cells[i].distance(e) if e is not None else i)

        # rooms nothing reaches (a row of bedrooms behind one another): a corridor into their floor, and that floor cut again
        _, lost = _links(cells, types, root_of(), random.Random(0), style, by_the_book=True)
        if lost:
            taken = set(lost)
            hosts = [i for i in range(len(cells)) if types[i] in CORRIDOR_FROM]
            for _ in range(3):  # the room between them and the rooms one lives in is theirs: the corridor goes through it
                if len(taken) >= 2 and any(_shared_len(cells[j], cells[i]) >= DOOR_W_M + 0.3 for j in taken for i in hosts if i not in taken):
                    break
                taken |= {i for i in range(len(cells)) if i not in taken and types[i] not in CORRIDOR_FROM and any(_shared_len(cells[i], cells[j]) > 0.5 for j in taken)}
            region = max(_polygons(unary_union([cells[j] for j in taken])), key=lambda g: g.area)
            inside = [j for j in sorted(taken) if cells[j].representative_point().within(region.buffer(0.05))]
            contacts = [(math.dist(*s), s) for i in hosts if i not in taken for s in _shared(region, cells[i])]
            strip = mk.corridor(orient(region, 1.0), max(contacts)[1]) if contacts and max(contacts)[0] >= DOOR_W_M + 0.3 else None
            if strip is not None and len(inside) >= 2:
                redo = [rt.Want(types[j], min(cells[j].area, SMALL_MOST_M2) if types[j] in rt.SMALL else cells[j].area) for j in inside]
                for j in sorted(inside, reverse=True):
                    del cells[j], types[j]
                cells.append(orient(strip, 1.0))
                types.append(rt.HALL)
                parts = sorted(_polygons(region.difference(strip)), key=lambda g: -g.area)
                given: list[list] = [[] for _ in parts]
                room_left = [p.area for p in parts]
                for w in sorted(redo, key=lambda w: -w.area):
                    k = max(range(len(parts)), key=lambda k: room_left[k])
                    given[k].append(w)
                    room_left[k] -= w.area
                for part, ws in zip(parts, given):
                    for cell, kind in mk.fill(orient(part, 1.0), ws or [rt.Want(rt.STORAGE, part.area)], strip):
                        cells.append(orient(cell, 1.0))
                        types.append(kind)
        links: list[Link] = []
        for _ in range(len(cells)):  # a cell no doorway reaches still: it joins a neighbour (a room, not a closet), which keeps its use
            root = root_of()
            state = rng.getstate()
            links, lost = _links(cells, types, root, rng, style)
            if not lost:
                break
            j = lost[0]
            host = max((i for i in range(len(cells)) if i != j and types[i] != rt.HALL),
                       key=lambda i: (sum(math.dist(*s) for s in _shared(cells[i], cells[j])) > 0.5, types[i] not in rt.SMALL, types[i] == types[j], sum(math.dist(*s) for s in _shared(cells[i], cells[j]))))  # fmt: skip
            merged = unary_union([cells[host], cells[j]])
            if merged.geom_type != "Polygon":
                break
            if types[host] in rt.SMALL and types[j] not in rt.SMALL:
                types[host] = types[j]  # (a bedroom that swallows a closet is a bedroom)
            cells[host] = orient(merged, 1.0)
            del cells[j], types[j]
            rng.setstate(state)
        if rt.BATHROOM not in types:  # the merging can cost the house its bathroom: a closet or a study of the right size becomes it
            spare = [i for i, t in enumerate(types) if t in (rt.STORAGE, rt.STUDY) and 3.0 <= cells[i].area <= 9.0]
            if spare:
                types[min(spare, key=lambda i: abs(cells[i].area - 5.5))] = rt.BATHROOM
        faults = _faults(cells, types, links, f_ops, e, hall is not None, style)
        return faults + rng.uniform(0.0, 0.6), cells, types, links  # (some chance: houses are not all the one best shape)

    kinds = ["stem", "vestibule", "none"]
    if _Maker(f, f_ops, (e.x, e.y) if e is not None else None, rng, style).side_fits():
        kinds.append("side")
    best = None
    for n, v in ((n, v) for n in range(TRIES) for v in kinds):
        got = build(v, n)
        if best is None or got[0] < best[0]:
            best = got
        if best[0] < GOOD_FAULTS:
            break
    _, cells, types, links = best

    walls: list[Wall] = []
    for i in range(len(cells)):
        for j in range(i + 1, len(cells)):
            for p, q in _shared(cells[i], cells[j]):
                gaps = []
                for l in links:
                    if {l.a, l.b} == {i, j} and LineString([p, q]).distance(Point(l.p)) < 1e-3 and LineString([p, q]).distance(Point(l.q)) < 1e-3:
                        gaps.append((round(math.dist(p, l.p), 3), round(math.dist(p, l.q), 3), l.kind))
                mp, mq = to_map(Point(p)), to_map(Point(q))
                walls.append(Wall((mp.x, mp.y), (mq.x, mq.y), sorted(gaps)))
    rooms = []
    for i, (cell, kind) in enumerate(zip(cells, types)):
        near = [(oid, okind) for oid, okind, seg, _ in f_ops if cell.boundary.distance(seg.interpolate(0.5, normalized=True)) < 0.05]
        rooms.append(Room(f"r{i}", "hall" if kind == rt.HALL else "room", orient(to_map(cell), 1.0), [o for o, k in near if k == "window"], [o for o, k in near if k == "door"], kind))
    out_links = []
    for l in links:
        mp, mq = to_map(Point(l.p)), to_map(Point(l.q))
        out_links.append(Link(l.a, l.b, l.kind, (mp.x, mp.y), (mq.x, mq.y)))
    return Plan(openness, style, rooms, walls, out_links)


def plan_doc(plan: Plan) -> dict:
    """The plan as it goes into interiors.json (heights are added by the export)."""

    def pt(p):
        return [round(p[0], 3), round(p[1], 3)]

    return {
        "openness": plan.openness,
        "style": plan.style,
        "rooms": [
            {"id": r.id, "kind": r.kind, "type": r.type, "area_m2": round(r.polygon.area, 2), "polygon": [pt(p) for p in list(r.polygon.exterior.coords)[:-1]],
             "windows": r.windows, "doors": r.doors,
             "links": [{"to": plan.rooms[l.b if l.a == i else l.a].id, "kind": l.kind, "p": pt(l.p), "q": pt(l.q)} for l in plan.links if i in (l.a, l.b)]}
            for i, r in enumerate(plan.rooms)
        ],
        "partitions": [{"p": pt(w.p), "q": pt(w.q), "gaps": [[g[0], g[1], g[2]] for g in w.gaps]} for w in plan.walls],
    }
