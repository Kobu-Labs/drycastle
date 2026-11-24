"""A pitched roof fitted to the whole footprint, for houses that are not one rectangle (``roof_fit`` on a building).

The footprint, small jogs smoothed away, is covered with overlapping rectangles, each as large as the footprint lets
it be. Every rectangle carries a roof of its own (hipped, or gabled along its ridge), all with the main roof's slope,
and the roof is the highest of them at every point: where two meet a valley or a step forms by itself, as it does on
a real house with wings. The main rectangle is the largest one (for a gabled roof: the largest that lies along or
square to the ridge).

Everything is in the building's own metres. A part is {core, domain (the core grown by the overhang), planes}: its
height above the eave is the lowest of its planes ``a*x + b*y + c``.
"""

import itertools
import math
from dataclasses import dataclass, field

import numpy as np
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import unary_union
from shapely.prepared import prep

_TOL_M = 0.12
_STEP_M = 0.1
_MIN_SIDE_M = 1.0
_MIN_EDGE_M = 1.2
_SMOOTH_M = 0.3
_LEAN_TO_M = 4.5  # a part standing no further than this out of an eave wall of a gabled house gets a single slope
_LEAN_LOW_M = 2.4  # whose low edge is no lower than this
_GABLE_WING_M = 2.0  # a wing standing this far out of an eave wall gets a gable end, a shallower one a hipped end
_BIG = 500.0
_WEDGE_M = 5.5  # how far a wall may run away from a rectangle's side it touches and still be that side's wall
_FOLLOW_M = 0.8  # how far from a rectangle's side the wall may run that a hipped part's side is laid on
_REACH_M = 0.6  # how far outside a rectangle a wall along its side may run and still be its wall
_UNDER_M = 0.02  # walls end a hair under the roof's surface: the two never share a plane
_DROP_M = 0.8  # a wall outside every part ends at most this far below the eave
_WALL_M = 0.05  # a wall this far outside a part (the rectangles stop short of walls that are not quite straight) is still under its roof


@dataclass
class Rect:
    o: np.ndarray  # a corner
    u: np.ndarray  # unit vector along the first side
    n: np.ndarray  # unit vector along the second (left of u)
    w: float
    d: float

    @property
    def corners(self) -> list[tuple[float, float]]:
        o, u, n, w, d = self.o, self.u, self.n, self.w, self.d
        return [tuple(o), tuple(o + u * w), tuple(o + u * w + n * d), tuple(o + n * d)]

    @property
    def middle(self) -> np.ndarray:
        return self.o + self.u * self.w / 2 + self.n * self.d / 2

    def grown(self, m: float) -> "Rect":
        return Rect(self.o - self.u * m - self.n * m, self.u, self.n, self.w + 2 * m, self.d + 2 * m)


@dataclass
class Part:
    core: Polygon
    domain: Polygon
    planes: list[tuple[float, float, float]]
    rect: Rect
    form: str  # hipped | gabled
    ridge: float | None = None  # direction of a gabled part's ridge
    half: float = 0.0
    _core_h: list = field(default_factory=list)
    _domain_h: list = field(default_factory=list)

    def height(self, x: float, y: float) -> float:
        return min(a * x + b * y + c for a, b, c in self.planes)


@dataclass
class Fit:
    parts: list[Part]
    eave: float
    angle: float  # the main part's ridge direction
    middle: tuple[float, float]
    half: float  # the main part's half-width across its ridge
    pieces: list[tuple[Polygon, tuple[float, float, float]]]  # the roof's planes over the grown footprint
    lines: object  # where the roof's planes meet
    wall_lines: object  # where a wall's top may kink
    rim_lines: object  # where the rim's height may kink
    outline: Polygon = None  # the roof's rim: the parts' own edges where they run within 0.3 m of the footprint's
    steps: list = field(default_factory=list)  # ((x, y), (x, y), low, high at the first end, low, high at the second): where one part's roof stands above the next one's, the upright face between them (heights above the eave)

    def top(self, x: float, y: float) -> float:
        """The wall's top at (x, y): the roof over the walls themselves, never below the eave."""
        hs = [p.height(x, y) if p.form == "lean_to" else max(0.0, p.height(x, y)) for p in self.parts if _within(p._core_h, x, y, _WALL_M)]
        if not hs:  # a wall outside every part: under the roof of the nearest, which slopes on down to it
            p = min(self.parts, key=lambda p: _outside(p._core_h, x, y))
            low = -_BIG if p.form == "lean_to" else -_DROP_M  # a single slope falls below the eave: its walls with it
            return self.eave + (max(p.height(x, y), low) if _outside(p._core_h, x, y) <= 1.5 else 0.0) - _UNDER_M
        return self.eave + max(hs) - _UNDER_M

    def roof_z(self, x: float, y: float) -> float:
        hs = [p.height(x, y) for p in self.parts if _within(p._domain_h, x, y)]
        if not hs:  # past every part's rim: the nearest part's roof carried on
            return self.eave + min(self.parts, key=lambda p: _outside(p._domain_h, x, y)).height(x, y)
        return self.eave + max(hs)


def _halves(poly: Polygon) -> list[tuple[float, float, float]]:
    """A convex counter-clockwise polygon as half-planes a*x + b*y + c >= 0."""
    pts = list(poly.exterior.coords)[:-1]
    if Polygon(pts).exterior.is_ccw is False:
        pts = pts[::-1]
    out = []
    for (x0, y0), (x1, y1) in zip(pts, pts[1:] + pts[:1]):
        length = math.hypot(x1 - x0, y1 - y0)
        if length < 1e-9:
            continue
        a, b = -(y1 - y0) / length, (x1 - x0) / length
        out.append((a, b, -(a * x0 + b * y0)))
    return out


def _outside(halves: list, x: float, y: float) -> float:
    """How far (x, y) lies outside the convex outline (0 inside)."""
    return max(0.0, -min(a * x + b * y + c for a, b, c in halves))


def _within(halves: list, x: float, y: float, tol: float = 1e-6) -> bool:
    return all(a * x + b * y + c >= -tol for a, b, c in halves)


def _half_plane(g: tuple[float, float, float], near: tuple[float, float]) -> Polygon | None:
    """The polygon where a*x + b*y + c >= 0, big enough round ``near``. None = everywhere, empty polygon = nowhere."""
    a, b, c = g
    norm = math.hypot(a, b)
    if norm < 1e-9:
        return None if c >= 0 else Polygon()
    a, b, c = a / norm, b / norm, c / norm
    dist = a * near[0] + b * near[1] + c
    px, py = near[0] - a * dist, near[1] - b * dist
    dx, dy = -b, a
    return Polygon([(px - dx * _BIG, py - dy * _BIG), (px + dx * _BIG, py + dy * _BIG),
                    (px + dx * _BIG + a * _BIG, py + dy * _BIG + b * _BIG), (px - dx * _BIG + a * _BIG, py - dy * _BIG + b * _BIG)])  # fmt: skip


def smooth(poly: Polygon) -> Polygon:
    """The footprint as a roofer sees it: notches and bumps narrower than 0.6 m gone."""
    s = poly.buffer(_SMOOTH_M, join_style=2, mitre_limit=2.0).buffer(-2 * _SMOOTH_M, join_style=2, mitre_limit=2.0).buffer(_SMOOTH_M, join_style=2, mitre_limit=2.0)
    s = s.simplify(0.05)
    if s.geom_type != "Polygon" or s.is_empty or s.area < 0.7 * poly.area:
        return poly
    return Polygon(s.exterior)


_SQUARE_DEG = 12.0  # a wall this far off the house's two directions is taken as meant to run along one of them


def squared(shape: Polygon) -> Polygon:
    """The footprint with its walls turned (each about its own middle) onto the house's two directions: few houses
    are quite square on the map, and a rectangle cannot stand on walls that are not. Only for finding the roof's
    rectangles: every roof plane is laid on the real walls afterwards."""
    simple = shape.simplify(0.25)
    if simple.geom_type != "Polygon" or simple.is_empty or len(simple.exterior.coords) < 5:
        return shape
    pts = [np.asarray(p, float) for p in simple.exterior.coords[:-1]]
    if not simple.exterior.is_ccw:
        pts = pts[::-1]
    n = len(pts)
    edges = [(pts[i], pts[(i + 1) % n]) for i in range(n)]
    angs = [math.atan2(q[1] - p[1], q[0] - p[0]) for p, q in edges]
    lens = [float(np.linalg.norm(q - p)) for p, q in edges]
    theta = math.atan2(sum(w * math.sin(4 * a) for a, w in zip(angs, lens)), sum(w * math.cos(4 * a) for a, w in zip(angs, lens))) / 4
    lines = []
    for (p, q), a in zip(edges, angs):
        dev = (a - theta + math.pi / 4) % (math.pi / 2) - math.pi / 4
        if abs(dev) <= math.radians(_SQUARE_DEG):
            a -= dev
        lines.append(((p + q) / 2, np.array([math.cos(a), math.sin(a)])))
    out = []
    for i in range(n):
        (m0, d0), (m1, d1) = lines[i - 1], lines[i]
        cross = float(d0[0] * d1[1] - d0[1] * d1[0])
        if abs(cross) < 0.05:  # two walls in a row along the same direction: a jog between them
            for m, dd in ((m0, d0), (m1, d1)):
                out.append(m + dd * float((pts[i] - m) @ dd))
            continue
        t = float((m1 - m0)[0] * d1[1] - (m1 - m0)[1] * d1[0]) / cross
        out.append(m0 + d0 * t)
    sq = Polygon([tuple(p) for p in out])
    if not sq.is_valid or sq.is_empty or not 0.9 <= sq.area / shape.area <= 1.1 or sq.hausdorff_distance(shape) > 1.2:
        return shape
    return sq


def rectangles(shape: Polygon) -> list[Rect]:
    """For every wall, the largest rectangles standing on it that stay inside the footprint (deepest first, and widest
    first)."""
    big = prep(shape.buffer(_TOL_M, join_style=2))
    pts = list(shape.exterior.coords)[:-1]
    if not shape.exterior.is_ccw:
        pts = pts[::-1]
    out: list[Rect] = []

    def fits(o, u, n, s0, s1, d) -> bool:
        return big.contains(Polygon([tuple(o + u * s0), tuple(o + u * s1), tuple(o + u * s1 + n * d), tuple(o + u * s0 + n * d)]))

    for p, q in zip(pts, pts[1:] + pts[:1]):
        o, length = np.asarray(p, float), math.dist(p, q)
        if length < _MIN_EDGE_M:
            continue
        u = (np.asarray(q, float) - o) / length
        n = np.array([-u[1], u[0]])
        for deep_first in (True, False):
            s0, s1, d = 0.0, length, 0.3
            if not fits(o, u, n, s0, s1, d):
                continue
            for grow_depth in ((True, False) if deep_first else (False, True)):
                if grow_depth:
                    while d < 80.0 and fits(o, u, n, s0, s1, d + _STEP_M):
                        d += _STEP_M
                else:
                    while s0 > -80.0 and fits(o, u, n, s0 - _STEP_M, s1, d):
                        s0 -= _STEP_M
                    while s1 < 80.0 and fits(o, u, n, s0, s1 + _STEP_M, d):
                        s1 += _STEP_M
            # the search lets a rectangle lean 12 cm out over a wall that is not quite straight: taken back, so it ends
            # on the walls (the side on its own wall never left it)
            while fits(o, u, n, s0, s1, d + 0.01):
                d += 0.01
            while fits(o, u, n, s0 - 0.01, s1, d):
                s0 -= 0.01
            while fits(o, u, n, s0, s1 + 0.01, d):
                s1 += 0.01
            s0, s1, d = min(s0 + _TOL_M, 0.0) if s0 < 0 else s0, max(s1 - _TOL_M, length) if s1 > length else s1, d - _TOL_M
            if min(s1 - s0, d) >= _MIN_SIDE_M:
                out.append(Rect(o + u * s0, u, n, s1 - s0, d))
    return out


def _aligned(r: Rect, angle: float, within_deg: float = 12.0) -> bool:
    turn = math.degrees(math.atan2(r.u[1], r.u[0]) - angle) % 90.0
    return min(turn, 90.0 - turn) <= within_deg


_WING_SHARE = 0.5  # and how much of a rectangle askew of the house must be new: less, and it is the roofs already there carried on
_WING_M = 1.5  # what a rectangle must add, in both directions, to be a wing with a roof of its own


def _same_wall(square: Polygon, p, q):
    """The wall of the squared footprint that the real wall p-q became."""
    p, q = np.asarray(p, float), np.asarray(q, float)
    mid, way = (p + q) / 2, (q - p) / max(float(np.linalg.norm(q - p)), 1e-9)
    c = [np.asarray(x, float) for x in square.exterior.coords]
    best = min(itertools.pairwise(c), key=lambda e: float(np.linalg.norm((e[0] + e[1]) / 2 - mid)) + 3.0 * (1.0 - abs(float((e[1] - e[0]) @ way)) / max(float(np.linalg.norm(e[1] - e[0])), 1e-9)))
    return best


def _on_wall(shape: Polygon, p, q) -> Rect | None:
    """The rectangle standing on the wall p-q, as deep as the footprint lets it be."""
    p, q = np.asarray(p, float), np.asarray(q, float)
    length = float(np.linalg.norm(q - p))
    if length < _MIN_EDGE_M:
        return None
    u = (q - p) / length
    n = np.array([-u[1], u[0]])
    from shapely.geometry import Point

    if not shape.buffer(_TOL_M).contains(Point(*(p + u * length / 2 + n * 0.4))):
        p, u = q, -u
        n = np.array([-u[1], u[0]])
    big = prep(shape.buffer(_TOL_M, join_style=2))
    d = 0.3
    rect = lambda d_: Polygon([tuple(p), tuple(p + u * length), tuple(p + u * length + n * d_), tuple(p + n * d_)])
    if not big.contains(rect(d)):
        return None
    while d < 80.0 and big.contains(rect(d + 0.05)):
        d += 0.05
    d -= _TOL_M
    return Rect(p, u, n, length, d) if d >= _MIN_SIDE_M else None


def choose(shape: Polygon, roof: str, ridge_dir: float | None, lead=None) -> list[Rect]:
    """The main rectangle, then every other that roofs a piece of the footprint (1.5 m thick or more) nothing larger
    has roofed yet. What is still bare after that is a bump (``bumps``). Each is stretched out to the walls."""
    square = squared(shape)
    cands = sorted(rectangles(square), key=lambda r: -r.w * r.d)
    if not cands and square is not shape:
        square = shape
        cands = sorted(rectangles(shape), key=lambda r: -r.w * r.d)
    if not cands:
        return []
    main = cands[0]
    if roof == "gabled" and ridge_dir is not None:
        # the house itself has its ridge along its length: a long narrow wing that the ridge would cross is not it
        want = np.array([math.cos(ridge_dir), math.sin(ridge_dir)])

        def as_house(r: Rect) -> float:
            along, across = (r.w, r.d) if abs(float(want @ r.u)) > 0.7 else (r.d, r.w)
            return r.w * r.d * (1.0 if along >= 0.8 * across else 0.5)

        main = max((r for r in cands if _aligned(r, ridge_dir)), key=as_house, default=cands[0])
    if roof == "gabled" and lead is not None:  # the house's roof stands on the gable wall that was seen: as wide as that wall
        main = _on_wall(square, *_same_wall(square, *lead)) or _on_wall(shape, *lead) or main
    def core(r: Rect) -> tuple[Rect, Polygon]:
        if roof == "hipped":
            return r, _follow(r, shape)
        r = _stretch(r, shape)
        return r, _follow(r, shape)

    out = [core(main)]
    covered = out[0][1]
    rest = [g for g in _polys(shape.difference(covered.buffer(0.03, join_style=2))) if not g.buffer(-0.3).is_empty]
    if len(rest) == 1 and rest[0].area >= 0.97 * rest[0].convex_hull.area and rest[0].area < 0.85 * rest[0].minimum_rotated_rectangle.area:
        return out  # what is left is one piece that is no rectangle (a tapering end): a hipped end on its own walls (bumps)
    main_angle = math.atan2(main.u[1], main.u[0])
    askew = lambda r: not _aligned(r, main_angle, 5.0)
    for r in cands:
        if r is main:
            continue
        added = Polygon(r.corners).intersection(shape).difference(covered.buffer(0.05))
        if added.is_empty or added.buffer(-_WING_M / 2).is_empty or (askew(r) and added.area < _WING_SHARE * r.w * r.d):
            continue
        out.append(core(r))
        covered = unary_union([covered, out[-1][1]])
    return out


def _follow(r: Rect, shape: Polygon) -> Polygon:
    """The rectangle with each side laid on the wall that runs along it (within 20 degrees and 0.8 m, over half the
    side or more): a four-sided outline whose sides are the house's own walls, so a hipped roof on it has a level
    eave along every one of them."""
    o, u, n, w, d = r.o, r.u, r.n, r.w, r.d
    near = tuple(r.middle)
    quad = None
    for a, along, out_n, span in ((o, u, -n, w), (o + u * w, n, u, d), (o + u * w + n * d, -u, n, w), (o + n * d, -n, -u, d)):
        strip = Polygon([tuple(a - out_n * _FOLLOW_M), tuple(a + along * span - out_n * _FOLLOW_M), tuple(a + along * span + out_n * _FOLLOW_M), tuple(a + out_n * _FOLLOW_M)])
        hit = shape.exterior.intersection(strip)
        segs = []
        for g in getattr(hit, "geoms", [hit]):
            if g.geom_type != "LineString":
                continue
            c = [np.asarray(p, float) for p in g.coords]
            for p, q in itertools.pairwise(c):
                run = float(np.linalg.norm(q - p))
                if run > 0.3 and abs(float((q - p) @ along)) / run >= 0.94:
                    segs.append((run, p, q))
        p0, way = a, along
        if segs and sum(s[0] for s in segs) >= 0.5 * span:
            _, p, q = max(segs, key=lambda s: s[0])
            p0, way = p, (q - p) / np.linalg.norm(q - p)
            if float(way @ along) < 0:
                way = -way
        else:
            # no wall along the side itself: a wall that touches it at one end and runs away from it at an angle (the
            # rectangle stopped at that wall's near end) is the part's wall all the same, with the wedge between them
            best = 0.0
            for p, q in itertools.pairwise([np.asarray(c, float) for c in shape.exterior.coords]):
                run = float(np.linalg.norm(q - p))
                if run < 1.0 or abs(float((q - p) @ along)) / run < 0.94:
                    continue
                offs = sorted((float((p - a) @ out_n), float((q - a) @ out_n)))
                ts = sorted((float((p - a) @ along), float((q - a) @ along)))
                cover = min(ts[1], span) - max(ts[0], 0.0)
                if not (-_FOLLOW_M <= offs[0] <= _FOLLOW_M and offs[1] <= _WEDGE_M and cover >= 0.6 * span and cover > best):
                    continue
                t_mid = (max(ts[0], 0.0) + min(ts[1], span)) / 2
                on_wall = p + (q - p) * ((t_mid - float((p - a) @ along)) / float((q - p) @ along))
                if not shape.buffer(0.1).contains(Point(*((a + along * t_mid + on_wall) / 2))):
                    continue
                best, p0, way = cover, p, (q - p) / run * (1.0 if float((q - p) @ along) > 0 else -1.0)
        inward = np.array([-way[1], way[0]]) if float(np.array([-way[1], way[0]]) @ -out_n) > 0 else np.array([way[1], -way[0]])
        hp = _half_plane((inward[0], inward[1], -float(inward @ p0)), near)
        quad = hp if quad is None else quad.intersection(hp)
    if quad is None or quad.geom_type != "Polygon" or not 0.7 <= quad.area / (w * d) <= 2.3:
        return Polygon(r.corners)
    if quad.area > 1.5 * w * d:  # a wedge was taken in: no further than the footprint itself goes
        inside = quad.intersection(shape.buffer(0.05, join_style=2))
        if inside.geom_type == "Polygon" and inside.area >= 0.98 * inside.convex_hull.area:
            return inside.convex_hull
        return Polygon(r.corners)
    return quad


def _stretch(r: Rect, shape: Polygon) -> Rect:
    """The rectangle with each side moved out to the wall that runs along it just outside (a footprint's walls are
    seldom quite parallel, and the search stops at the first one a rectangle touches): then no wall stands outside
    the roof that covers it."""
    o, u, n, w, d = r.o.copy(), r.u, r.n, r.w, r.d
    for _ in range(2):
        for side in range(4):
            a, along, out_n, span = ((o, u, -n, w), (o + u * w, n, u, d), (o + u * w + n * d, -u, n, w), (o + n * d, -n, -u, d))[side]
            strip = Polygon([tuple(a), tuple(a + along * span), tuple(a + along * span + out_n * _REACH_M), tuple(a + out_n * _REACH_M)])
            far = 0.0
            hit = shape.exterior.intersection(strip)
            for g in getattr(hit, "geoms", [hit]):
                if g.geom_type != "LineString":
                    continue
                c = [np.asarray(p, float) for p in g.coords]
                for p, q in itertools.pairwise(c):
                    run = np.linalg.norm(q - p)
                    if run > 0.3 and abs(float((q - p) @ along)) / run >= 0.94:  # within 20 degrees of the side
                        far = max(far, float((p - a) @ out_n), float((q - a) @ out_n))
            if far > 0.01:
                if side == 0:
                    o, d = o - n * far, d + far
                elif side == 1:
                    w += far
                elif side == 2:
                    d += far
                else:
                    o, w = o - u * far, w + far
    return Rect(o, u, n, w, d)


def bumps(shape: Polygon, chosen: list[tuple[Rect, Polygon]]) -> list[Polygon]:
    """What the rectangles leave bare, 0.6 m thick or more (a bay, a shallow porch): each as a convex outline mirrored
    in the rectangle side it stands on, so its small hipped roof has its top on that wall and dies into the roof behind."""
    left = shape.difference(unary_union([c for _, c in chosen]).buffer(0.03, join_style=2))
    out = []
    for g in _polys(left):
        if g.buffer(-0.3).is_empty:
            continue
        # without the slivers left along walls that a rectangle stops a few cm short of: they would stretch the outline
        # over open ground
        solid = max(_polys(g.buffer(-0.15, join_style=2).buffer(0.15, join_style=2)), key=lambda q: q.area, default=g)
        hull = solid.convex_hull
        best, line, behind = 0.0, None, None
        for _, core in chosen:
            c = list(core.exterior.coords)[:-1]
            for p, q in zip(c, c[1:] + c[:1]):
                shared = hull.exterior.intersection(LineString([p, q]).buffer(0.1)).length
                if shared > best:
                    best, line, behind = shared, (np.asarray(p, float), np.asarray(q, float)), core
        if line is None or best < 0.5:
            continue
        p, q = line
        u = (q - p) / np.linalg.norm(q - p)
        pts = [np.asarray(c, float) for c in hull.exterior.coords[:-1]]
        mirrored = [2 * (p + u * float((c - p) @ u)) - c for c in pts]
        both = Polygon([tuple(c) for c in pts]).union(Polygon([tuple(c) for c in mirrored])).convex_hull
        # its roof dies into the part behind it: no further in than that part's middle
        nrm = np.array([-u[1], u[0]])
        deep = [float((np.asarray(c, float) - p) @ nrm) for c in behind.exterior.coords]
        far = max(deep, key=abs)
        if abs(far) > 0.5:
            side = 1.0 if far > 0 else -1.0
            limit = p + nrm * far / 2
            hp = _half_plane((-side * nrm[0], -side * nrm[1], side * float(nrm @ limit)), tuple(p))
            if hp is not None:
                cut = both.intersection(hp)
                if cut.geom_type == "Polygon" and not cut.is_empty:
                    both = cut
        if both.geom_type == "Polygon" and both.area > 0.3:
            out.append(both)
    return out


def _polygon_planes(poly: Polygon, slope: float) -> list[tuple[float, float, float]]:
    return [(slope * a, slope * b, slope * c) for a, b, c in _halves(poly)]


def _hipped_planes(r: Rect, slope: float) -> list[tuple[float, float, float]]:
    out = []
    for origin, inward in ((r.o, r.n), (r.o, r.u), (r.o + r.u * r.w + r.n * r.d, -r.n), (r.o + r.u * r.w + r.n * r.d, -r.u)):
        out.append((slope * inward[0], slope * inward[1], -slope * float(inward @ origin)))
    return out


def _gabled_planes(r: Rect, slope: float, ridge: np.ndarray) -> list[tuple[float, float, float]]:
    """Two slopes falling from the middle line that runs along ``ridge`` (one of the rectangle's own two directions)."""
    along_u = abs(float(ridge @ r.u)) > 0.7
    c, far = (r.n, r.d) if along_u else (r.u, r.w)
    lo = float(c @ r.o)
    return [(slope * c[0], slope * c[1], -slope * lo), (-slope * c[0], -slope * c[1], slope * (lo + far))]


def _wall_planes(core: Polygon, slope: float, ridge: np.ndarray) -> list[tuple[float, float, float]] | None:
    """A gabled part's two slopes, each rising from the wall its eave runs along (the two sides of core along the
    ridge): the eaves are level along the walls even where those are not parallel. None when core has no such two
    sides."""
    across = np.array([-ridge[1], ridge[0]])
    sides = [(a, b, c) for a, b, c in _halves(core) if abs(a * across[0] + b * across[1]) > 0.8]
    if len(sides) != 2 or (sides[0][0] * across[0] + sides[0][1] * across[1]) * (sides[1][0] * across[0] + sides[1][1] * across[1]) > 0:
        return None
    return [(slope * a, slope * b, slope * c) for a, b, c in sides]


def fit(poly: Polygon, roof: str, ridge_dir: float | None, rise: float, eave: float, overhang: float = 0.0, lead=None) -> Fit | None:
    """The fitted roof of footprint ``poly`` (counter-clockwise): ``rise`` is the main part's, from eave to ridge.
    None when the footprint gives no rectangle to stand a roof on."""
    shape = smooth(poly)
    chosen = choose(shape, roof, ridge_dir, lead)
    if not chosen:
        return None
    rects = [r for r, _ in chosen]
    main = rects[0]
    main_in = _inradius(chosen[0][1])
    if roof == "gabled":
        if ridge_dir is None:
            ridge_dir = math.atan2(main.u[1], main.u[0]) if main.w >= main.d else math.atan2(main.n[1], main.n[0])
        want = np.array([math.cos(ridge_dir), math.sin(ridge_dir)])
        ru = main.u if abs(float(want @ main.u)) > 0.7 else main.n
        if lead is not None:  # the ridge runs back from the gable wall
            along = (np.asarray(lead[1], float) - np.asarray(lead[0], float)) / math.dist(*lead)
            ru = main.n if abs(float(along @ main.u)) > 0.7 else main.u  # the main rectangle's own side nearest the ridge direction
        rv = np.array([-ru[1], ru[0]])
        half = (main.d if ru is main.u else main.w) / 2
        angle = math.atan2(ru[1], ru[0]) % math.pi
    else:
        ru = main.u if main.w >= main.d else main.n
        rv = np.array([-ru[1], ru[0]])
        half = main_in
        angle = math.atan2(ru[1], ru[0]) % math.pi
    slope = rise / max(half, 0.3)
    mid = main.middle
    v_main = (float(rv @ mid) - half, float(rv @ mid) + half)
    parts: list[Part] = []
    for k, (r, core) in enumerate(chosen):
        cut = None
        form, ridge = "hipped", None
        if roof == "gabled":
            if k == 0:
                form, ridge = "gabled", ru
            elif not _aligned(r, angle):  # a wing at its own angle: its ridge along its length
                form, ridge = "gabled", (r.u if r.w >= r.d else r.n)
            else:
                vs = [float(rv @ np.asarray(c)) for c in r.corners]
                beyond = (v_main[0] - min(vs), max(vs) - v_main[1])
                if max(beyond) <= 0.5:  # in line with the house: its roof carried on, lower
                    form, ridge = "gabled", (r.u if abs(float(ru @ r.u)) > 0.7 else r.n)
                elif min(beyond) <= 0.5 and max(beyond) <= _LEAN_TO_M and min(slope, (eave - _LEAN_LOW_M) / max(beyond)) >= 0.12:
                    # a narrow part beside an eave wall: a single slope falling away from it, from the eave down
                    form, side = "lean_to", (1.0 if beyond[1] > beyond[0] else -1.0)
                    v0 = v_main[1] if side > 0 else v_main[0]
                    lean_s = min(slope, (eave - _LEAN_LOW_M) / max(beyond))
                    cut = _half_plane((side * rv[0], side * rv[1], -side * v0), tuple(mid + rv * side * half))
                    lean = [(-lean_s * side * rv[0], -lean_s * side * rv[1], lean_s * side * v0)]
                elif max(beyond) >= _GABLE_WING_M:  # a wing out of an eave wall: its ridge square to the house's
                    form, ridge = "gabled", (r.u if abs(float(rv @ r.u)) > 0.7 else r.n)
                    if min(beyond) <= 0.5:  # it stops at the house's ridge
                        side = 1.0 if beyond[1] > beyond[0] else -1.0
                        cut = _half_plane((side * rv[0], side * rv[1], -side * float(rv @ mid)), tuple(mid))
        if form == "hipped" and roof == "gabled":
            core = Polygon(r.corners)
        core = _ccw(core)
        if form == "lean_to":
            core = core.intersection(cut)
            if core.is_empty or core.geom_type != "Polygon":
                continue
            core = _ccw(core)
            h, planes = 0.0, lean
        else:
            h = (r.d if ridge is not None and abs(float(ridge @ r.u)) > 0.7 else r.w) / 2 if form == "gabled" else _inradius(core)
            s = min(slope, rise / max(h, 0.3))
            planes = _polygon_planes(core, s)
            if form == "gabled":
                planes = _wall_planes(core, 1.0, ridge)
                if planes is not None:  # between walls that are not parallel the roof is as high as its widest place
                    x0, y0, x1, y1 = core.bounds
                    tall = max((min(a * x + b * y + c for a, b, c in planes) for x in np.linspace(x0, x1, 25) for y in np.linspace(y0, y1, 25) if _within(_halves(core), x, y, 0.05)), default=h)
                    if tall > 1.15 * h:
                        h = float(tall)
                        s = min(slope, rise / max(h, 0.3))
                    planes = [(s * a, s * b, s * c) for a, b, c in planes]
                else:
                    planes = _gabled_planes(r, s, ridge)
        domain = core.buffer(overhang, join_style=2, mitre_limit=5.0) if overhang else core
        if cut is not None:
            core, domain = core.intersection(cut), domain.intersection(cut)
        if core.is_empty or core.geom_type != "Polygon":
            continue
        parts.append(Part(core, domain, planes, r, form, None if ridge is None else math.atan2(ridge[1], ridge[0]) % math.pi, h, _halves(core), _halves(domain)))
    for g in bumps(shape, chosen):
        g = _ccw(g)
        inradius = _inradius(g)
        s = min(slope, (rise if inradius <= 1.5 else 0.7 * rise) / max(inradius, 0.3))  # a large one stays under the ridge
        # its top lies on the wall it stands against: no more than a hand above the roof that comes down to that wall
        # (against an eave it is a low roof; against a gable wall it may rise)
        unit = _polygon_planes(g, 1.0)
        x0, y0, x1, y1 = g.bounds
        peak, px, py = max(((min(a * x + b * y + c for a, b, c in unit), x, y) for x in np.linspace(x0, x1, 21) for y in np.linspace(y0, y1, 21)), default=(0.0, 0.0, 0.0))
        over = [p.height(px, py) for p in parts if _within(p._core_h, px, py, 0.1)]
        if over and peak > 0.05:
            s = min(s, max(max(over), 0.0) + 0.35) / peak if s * peak > max(max(over), 0.0) + 0.35 else s
        domain = g.buffer(overhang, join_style=2, mitre_limit=3.0) if overhang else g
        parts.append(Part(g, domain, _polygon_planes(g, s), main, "hipped", None, float(inradius), _halves(g), _halves(domain)))
    grown = poly.buffer(overhang, join_style=2, mitre_limit=3.0) if overhang else poly
    if overhang:
        # the rim runs along the parts' own edges (level eaves) wherever those lie within 0.3 m of the footprint's
        # rim; the walls themselves are always covered
        rim = unary_union([unary_union([p.domain for p in parts]).intersection(grown.buffer(0.3, join_style=2, mitre_limit=3.0)), poly.buffer(min(overhang, 0.1), join_style=2)])
        if rim.geom_type == "Polygon":
            grown = Polygon(rim.exterior).simplify(0.01)
    pieces = _arrange([(p.domain, p.planes) for p in parts], grown)
    reach = [p.core.buffer(_WALL_M, join_style=2, mitre_limit=3.0) for p in parts]
    wall_pieces = _arrange([(g, p.planes) for g, p in zip(reach, parts)], poly.buffer(_WALL_M + 0.1, join_style=2))
    lines = unary_union([g.exterior for g, _ in pieces])
    step = lambda polys: [g.buffer(0.02, join_style=2).exterior for g in polys]
    wall_lines = unary_union([g.exterior for g, _ in wall_pieces] + [g.exterior for g in reach] + step(reach))
    rim_lines = unary_union([lines] + [p.domain.exterior for p in parts] + step([p.domain for p in parts]))
    return Fit(parts, eave, angle, (float(mid[0]), float(mid[1])), half, pieces, lines, wall_lines, rim_lines, grown, _steps(pieces))


def _steps(pieces: list) -> list:
    """Where two neighbouring pieces of the roof lie at different heights along their shared edge."""
    out = []
    # (the cutting leaves hair-thin spikes on some pieces, which touch pieces they are no neighbours of: taken off)
    solid = [(q, f) for g, f in pieces for q in _polys(g.buffer(-2e-3, join_style=2).buffer(2e-3, join_style=2))]
    for i, (g, f) in enumerate(solid):
        for h, e in solid[i + 1:]:
            if g.distance(h) > 1e-3:
                continue
            shared = g.exterior.intersection(h.buffer(4e-3))
            for line in getattr(shared, "geoms", [shared]):
                if line.geom_type != "LineString" or line.length < 0.05:
                    continue
                for p, q in itertools.pairwise(line.coords):
                    if math.dist(p, q) < 0.02:
                        continue
                    zp = sorted((f[0] * p[0] + f[1] * p[1] + f[2], e[0] * p[0] + e[1] * p[1] + e[2]))
                    zq = sorted((f[0] * q[0] + f[1] * q[1] + f[2], e[0] * q[0] + e[1] * q[1] + e[2]))
                    if max(zp[1] - zp[0], zq[1] - zq[0]) > 0.03:
                        out.append(((p[0], p[1]), (q[0], q[1]), zp[0], zp[1], zq[0], zq[1]))
    return out


def _arrange(parts: list[tuple[Polygon, list]], within: Polygon) -> list[tuple[Polygon, tuple[float, float, float]]]:
    """The pieces of ``within`` on which one plane is the roof (the highest part's lowest plane), and what is left over,
    level at the eave."""
    near = (within.centroid.x, within.centroid.y)
    regions: list[tuple[int, Polygon, tuple]] = []
    for i, (domain, planes) in enumerate(parts):
        for k, f in enumerate(planes):
            g = domain
            for m, other in enumerate(planes):
                if m == k:
                    continue
                hp = _half_plane((other[0] - f[0], other[1] - f[1], other[2] - f[2] + (1e-9 if m > k else -1e-9)), near)
                if hp is not None:
                    g = g.intersection(hp)
            if not g.is_empty and g.area > 1e-6:
                regions.append((i, g, f))
    out = []
    for i, g, f in regions:
        for j, h, e in regions:
            if j == i or g.is_empty:
                continue
            hp = _half_plane((e[0] - f[0], e[1] - f[1], e[2] - f[2] + (1e-9 if j < i else -1e-9)), near)
            g = g.difference(h if hp is None else h.intersection(hp))
        g = g.intersection(within)
        out += [(q, f) for q in _polys(g)]
    # what no part reaches (a rim that runs askew of them): the nearest part's roof carried on over it
    left = within.difference(unary_union([d for d, _ in parts]).buffer(1e-6))
    for q in _polys(left):
        if q.area <= 0.003:
            continue
        domain, planes = min(parts, key=lambda dp: dp[0].distance(q.representative_point()))
        for k, f in enumerate(planes):
            g = q
            for m, other in enumerate(planes):
                if m != k:
                    hp = _half_plane((other[0] - f[0], other[1] - f[1], other[2] - f[2] + (1e-9 if m > k else -1e-9)), near)
                    if hp is not None:
                        g = g.intersection(hp)
            out += [(s, f) for s in _polys(g)]
    return out


def _ccw(g: Polygon) -> Polygon:
    pts = list(g.exterior.coords)[:-1]
    return Polygon(pts if g.exterior.is_ccw else pts[::-1])


def _inradius(g: Polygon) -> float:
    lo, hi = 0.0, 40.0
    for _ in range(24):
        mid = (lo + hi) / 2
        lo, hi = (mid, hi) if not g.buffer(-mid).is_empty else (lo, mid)
    return max(lo, 0.3)


def _polys(g) -> list[Polygon]:
    if g.is_empty:
        return []
    if g.geom_type == "Polygon":
        return [g] if g.area > 1e-4 else []
    return [q for s in getattr(g, "geoms", []) for q in _polys(s)]


def fit_building(b: dict, overhang: float = 0.0) -> Fit | None:
    from shapely.geometry.polygon import orient

    from drycastle_pipeline.buildings.footprint import footprint_polygon

    poly = orient(Polygon(footprint_polygon(b["footprint"]).exterior), 1.0)
    return fit(poly, b["roof"], b.get("ridge_dir"), float(b["roof_height_m"]), float(b["eave_height_m"]), overhang, b.get("roof_lead"))
