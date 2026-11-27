"""The inside of a house with a building detail: walls with a width, a floor, a ceiling, openings that go through.

A detail opts in with ``interior: true``. The outer face of every wall stays on the footprint (what the photos measured);
the wall grows ``WALL_M`` inward. The floor lies at the sill of the lowest door (the steps in front already climb to
it), the ceiling one storey above it (at most ``STOREY_M``); what is above the ceiling stays closed (upper floors: later). A window or a door
between floor and ceiling is a hole through the whole wall, as is a garage door (it may start below the floor); a recess
and what is cut into a recess stay closed, as do windows above the ceiling. Overview: docs/interiors.md.

``openings.build_detail`` calls in here for the visual parts; ``collision`` is the same shell as the collision mesh
(ADR 0040), with a ramp where the visual has door steps.
"""

import math
from dataclasses import dataclass

from shapely.geometry import Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import bearing_deg
from drycastle_pipeline.core.mesh import Mesh, triangulate
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

WALL_M = 0.3
MIN_FLOOR_M = 0.05  # above the flattened ground, so the floor and the terrain do not fight
STOREY_M = 3.0  # a room is not higher than this: a taller wall has an attic or a floor above it, which stays closed
HEAD_CLEAR_M = 0.05  # an opening's head stays this far under the ceiling to go through
LOW_STOREY_M = 2.3  # a storey of a low house is still this high, where the walls are
INNER_WALL, FLOOR, CEILING = (236, 232, 222), (146, 118, 88), (244, 242, 236)
GREY = (128, 128, 128)
RAMP_RUN_PER_RISE = 2.0  # a door above the ground with no steps: the collision ramp's run


@dataclass(frozen=True)
class Inside:
    floor: float
    ceiling: float

    def through(self, o) -> bool:
        """Does opening ``o`` go through the wall into the room? A garage door may start below the floor (the ground)."""
        if o.kind not in ("window", "door", "garage_door") or o.parent is not None:
            return False
        if o.sill_m + o.height_m > self.ceiling - HEAD_CLEAR_M:
            return False
        if o.kind == "garage_door":
            return o.sill_m + o.height_m > self.floor + 1.5
        return o.sill_m >= self.floor - MIN_FLOOR_M - 0.01


def inside_of(b: dict, d) -> Inside | None:
    """The room of building ``b`` with detail ``d``, or None when the detail asks for none."""
    if not getattr(d, "interior", False):
        return None
    eave = float(b["eave_height_m"])
    doors = [o.sill_m for o in d.openings if o.kind == "door" and o.parent is None and o.sill_m < eave / 2]
    floor = min(doors) if doors else (d.plinth.height_m if d.plinth is not None else 0.15)
    floor = max(floor, MIN_FLOOR_M)
    levels = max(int(b.get("levels") or 1), 1)
    # one storey, but never so low that a door of the ground floor does not go through while the walls have the height
    heads = [o.sill_m + o.height_m + HEAD_CLEAR_M + 0.01 - floor for o in d.openings if o.kind in ("door", "garage_door") and o.parent is None and o.sill_m < eave / 2]
    storey = max(min((eave - floor) / levels, STOREY_M), LOW_STOREY_M, *(h for h in heads if h <= eave - floor))
    ceiling = min(eave, floor + storey)
    if ceiling - floor < 2.0:
        from drycastle_pipeline.buildings.openings import DetailError

        raise DetailError(f"{d.id}: interior: only {ceiling - floor:.2f} m between the floor ({floor:.2f}) and the ceiling ({ceiling:.2f})")
    return Inside(round(floor, 3), round(ceiling, 3))


CAP_BELOW_M = 0.07  # what is inside stays this far under the roof's surface (within the roof's own thickness: never seen from outside)


@dataclass(frozen=True)
class RoofCap:
    """How high the inside may reach under the roof: the roof's underside, less a hair, piece by piece. A low roof (a single
    slope over a wing, the eaves of a steep roof) comes down below the ceiling: walls, partitions and the ceiling stop under
    it there instead of passing through it. ``pieces`` are (polygon, (a, b, c)) with the cap at a*x + b*y + c."""

    pieces: tuple

    def z(self, x: float, y: float) -> float | None:
        from shapely.geometry import Point

        pt = Point(x, y)
        got = [a * x + b * y + c for g, (a, b, c) in self.pieces if g.distance(pt) < 1e-6]
        return max(got) if got else None

    @classmethod
    def of(cls, pieces: list, roof_z, below: float = CAP_BELOW_M) -> "RoofCap":
        """From the roof's pieces (polygons) and its height function: one plane per piece, through three of its points."""
        out = []
        for g in pieces:
            tris = triangulate(g)
            if not tris:
                continue
            (x0, y0), (x1, y1), (x2, y2) = max(tris, key=lambda t: abs((t[1][0] - t[0][0]) * (t[2][1] - t[0][1]) - (t[1][1] - t[0][1]) * (t[2][0] - t[0][0])))
            z0, z1, z2 = roof_z(x0, y0), roof_z(x1, y1), roof_z(x2, y2)
            den = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if abs(den) < 1e-12:
                continue
            a = ((z1 - z0) * (y2 - y0) - (z2 - z0) * (y1 - y0)) / den
            b = ((x1 - x0) * (z2 - z0) - (x2 - x0) * (z1 - z0)) / den
            out.append((g, (a, b, z0 - a * x0 - b * y0 - below)))
        return cls(tuple(out))


def _under(cap: "RoofCap | None", to3, s0: float, s1: float, depth: float, step: float = 0.1):
    """The part of a wall's (s, z) plane under the cap, from s0 to s1 (None: no cap, or the cap is nowhere over the line)."""
    if cap is None or s1 - s0 < 1e-6:
        return None
    n = max(1, math.ceil((s1 - s0) / step))
    top = []
    for k in range(n + 1):
        s = s0 + (s1 - s0) * k / n
        x, y, _ = to3(s, 0.0, depth)
        z = cap.z(x, y)
        if z is not None:
            top.append((s, z))
    if len(top) < 2:
        return None
    return Polygon([(top[0][0], -100.0), (top[-1][0], -100.0), *reversed(top)])


def _clip(face, cap, to3, s0: float, s1: float, depth: float):
    under = _under(cap, to3, s0 - 0.01, s1 + 0.01, depth)
    return face if under is None else face.intersection(under)


def _holes(ops, ins: Inside):
    return [box(o.at_m, o.sill_m, o.at_m + o.width_m, o.sill_m + o.height_m) for o in ops if ins.through(o)]


def _face(out, to3, face, depth: float, colour, inward: bool) -> None:
    """A wall face (a polygon of the wall's own s, z plane) at ``depth`` behind the wall line."""
    for g in _polygons(face):
        for a0, a1, a2 in triangulate(orient(g, 1.0)):
            area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
            order = (a0, a1, a2) if (area > 0) != inward else (a0, a2, a1)
            out.add([to3(*c, depth) for c in order], colour)


def add_inner_wall(out, to3, ring: list, i: int, ops, ins: Inside, colour=INNER_WALL, cap: RoofCap | None = None) -> None:
    """The room's side of wall ``i``: floor to ceiling between the corners' diagonals, with the holes of what goes through
    (and under the roof, ``cap``, where that comes lower)."""
    from drycastle_pipeline.buildings.openings import _band_ends

    lo, hi = _band_ends(ring, i, -WALL_M)
    if hi - lo < 0.02:
        return
    face = box(lo, ins.floor, hi, ins.ceiling)
    holes = _holes(ops, ins)
    if holes:
        face = face.difference(unary_union(holes))
    face = _clip(face, cap, to3, lo, hi, WALL_M)
    _face(out, to3, face, WALL_M, colour, inward=True)
    for o in ops:  # under a garage door that starts below the floor, the floor's edge closes the bottom of the hole
        if o.kind == "garage_door" and ins.through(o) and o.sill_m < ins.floor - 0.005:
            _face(out, to3, box(o.at_m, o.sill_m, o.at_m + o.width_m, ins.floor), WALL_M, colour, inward=False)


def add_floor_ceiling(out, poly: Polygon, ins: Inside, floor=FLOOR, ceiling=CEILING, inset: float = 0.0, cap: RoofCap | None = None) -> None:
    """The floor (seen from above) and the ceiling (seen from below), over the footprint shrunk by ``inset``. The visual passes
    the wall's width: the floor then ends at the walls' inner faces and never lies under a doorway's reveal, whose bottom is
    at the floor's height (two faces in one plane flicker). The collision keeps the whole footprint: its rims are in the walls.
    Where the roof (``cap``) comes lower than the ceiling, the ceiling follows the roof's underside there."""

    def tris_of(g):
        for tri in (t for part in getattr(g, "geoms", [g]) if not part.is_empty and part.geom_type == "Polygon" for t in triangulate(part)):
            a0, a1, a2 = tri
            ccw = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0]) > 0
            yield (a0, a1, a2) if ccw else (a0, a2, a1)

    area = poly if inset <= 0 else poly.buffer(-inset, join_style=2)
    for up in tris_of(area):
        out.add([(x, y, ins.floor) for x, y in up], floor)
    flat = area
    if cap is not None:
        from drycastle_pipeline.buildings.roof_fit import _half_plane

        near = (area.centroid.x, area.centroid.y)
        for g, (a, b, c) in cap.pieces:
            low = _half_plane((-a, -b, ins.ceiling - c), near)  # where the cap is below the ceiling
            if low is not None and low.is_empty:
                continue
            sloped = area.intersection(g) if low is None else area.intersection(g).intersection(low)
            if sloped.is_empty or sloped.area < 1e-4:
                continue
            flat = flat.difference(sloped)
            for up in tris_of(sloped):
                out.add([(x, y, max(a * x + b * y + c, ins.floor + 0.05)) for x, y in reversed(up)], ceiling)
    for up in tris_of(flat):
        out.add([(x, y, ins.ceiling) for x, y in reversed(up)], ceiling)


def plan_of(b: dict, d, ins: Inside | None = None):
    """The generated floor plan (buildings/floorplan.py) of a hollow house, or None."""
    from drycastle_pipeline.buildings import floorplan

    ins = ins or inside_of(b, d)
    return floorplan.make_plan(b, d, ins, WALL_M) if ins is not None else None


def prepare_plans(houses: list[tuple]) -> None:
    """Works out the plans of many ``(building, detail)`` houses at once, on all cores; plan_of then has them at hand."""
    from drycastle_pipeline.buildings import floorplan

    hollow = [(b, d, ins, WALL_M) for b, d in houses if (ins := inside_of(b, d)) is not None]
    if hollow:
        floorplan.make_plans(hollow)


def _slab(out, to3, s: tuple, z: tuple, half: float, colour, faces: tuple, cap: RoofCap | None) -> None:
    """A partition's box (``out.cuboid`` of the partition's frame) with its top under the roof (``cap``) where that comes lower."""
    (s0, s1), (z0, z1) = s, z
    under = [_under(cap, to3, s0, s1, dd) for dd in (-half, half)]
    if all(u is None for u in under) or all(u is None or u.contains(box(s0, z0, s1, z1)) for u in under):
        out.cuboid(to3, s, z, (-half, half), colour, faces)
        return
    for dd, u in zip((-half, half), under):
        name = "d0" if dd < 0 else "d1"
        if name in faces:
            face = box(s0, z0, s1, z1) if u is None else box(s0, z0, s1, z1).intersection(u)
            _face(out, lambda a, b, c, dd=dd: to3(a, b, dd + c), face, 0.0, colour, inward=dd > 0)
    for name, sv in (("s0", s0), ("s1", s1)):
        if name not in faces:
            continue
        tops = [cap.z(*to3(sv, 0.0, dd)[:2]) for dd in (-half, half)] if cap is not None else [None, None]
        t0, t1 = (min(z1, t) if t is not None else z1 for t in tops)
        if max(t0, t1) - z0 > 1e-3:
            quad = [to3(sv, z0, -half), to3(sv, z0, half), to3(sv, max(t1, z0), half), to3(sv, max(t0, z0), -half)]
            out.quad(quad, colour, to3(sv + (-1.0 if name == "s0" else 1.0), (z0 + z1) / 2, 0.0))
    if "z0" in faces:
        out.quad([to3(s0, z0, -half), to3(s1, z0, -half), to3(s1, z0, half), to3(s0, z0, half)], colour, to3((s0 + s1) / 2, z0 - 1.0, 0.0))


def add_partitions(out, plan, ins: Inside, colour=INNER_WALL, cap: RoofCap | None = None) -> None:
    """The partitions of the plan: a slab per wall from floor to ceiling, left out where a doorway is, with the piece of
    wall over a door or an archway; under the roof (``cap``) where that comes lower than the ceiling."""
    from drycastle_pipeline.buildings import floorplan as fp

    if plan is None:
        return
    half = fp.PARTITION_M / 2
    for w in plan.walls:
        length = math.dist(w.p, w.q)
        if length < 0.05:
            continue
        ux, uy = (w.q[0] - w.p[0]) / length, (w.q[1] - w.p[1]) / length

        def to3(s: float, z: float, depth: float = 0.0, p=w.p, ux=ux, uy=uy):
            return (p[0] + ux * s - uy * depth, p[1] + uy * s + ux * depth, z)

        cursor = 0.0
        for s0, s1, kind in [*w.gaps, (length, length, "end")]:
            if s0 - cursor > 0.02:
                _slab(out, to3, (cursor, s0), (ins.floor, ins.ceiling), half, colour, ("s0", "s1", "d0", "d1"), cap)
            head = ins.floor + (fp.DOOR_H_M if kind == "door" else fp.ARCH_H_M)
            if kind in ("door", "arch") and ins.ceiling - head > 0.05:
                _slab(out, to3, (s0, s1), (head, ins.ceiling), half, colour, ("z0", "d0", "d1"), cap)
            cursor = max(cursor, s1)


def room_ring(b: dict) -> list[list[float]]:
    """The room's outline for the game (shelter volume): the footprint shrunk to the middle of the walls."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    inner = max(_polygons(poly.buffer(-WALL_M / 2, join_style=2)), key=lambda g: g.area, default=poly)
    return [[round(x, 3), round(y, 3)] for x, y in list(inner.exterior.coords)[:-1]]


def doorways(b: dict, d, ins: Inside) -> list[dict]:
    """The doors that go through: the middle of each on the floor at the wall's outer face, the outward normal, the size."""
    ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
    out = []
    for o in d.openings:
        if o.kind != "door" or not ins.through(o):
            continue
        p, q = ring[o.edge], ring[(o.edge + 1) % len(ring)]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        s = o.at_m + o.width_m / 2
        out.append({"id": o.id, "at": [round(p[0] + ux * s, 3), round(p[1] + uy * s, 3), round(o.sill_m, 3)],
                    "normal": [round(uy, 4), round(-ux, 4)], "width_m": o.width_m, "height_m": o.height_m})  # fmt: skip
    return out


def leaves(b: dict, d, ins: Inside) -> list[dict]:
    """The doors the game hangs and players open (``interiors.json`` ``doors``), one per door or garage door that goes
    through: ``pos`` the middle of the bottom of the opening, in the middle of the wall; ``yaw`` the compass bearing out of
    the house, degrees; the size and the leaf's colour."""
    from drycastle_pipeline.buildings.openings import DEFAULT_PANEL

    ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
    out = []
    for o in d.openings:
        if o.kind not in ("door", "garage_door") or not ins.through(o):
            continue
        p, q = ring[o.edge], ring[(o.edge + 1) % len(ring)]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        s, deep = o.at_m + o.width_m / 2, WALL_M / 2
        out.append({"id": o.id, "kind": "garage" if o.kind == "garage_door" else "hinged",
                    "pos": [round(p[0] + ux * s - uy * deep, 3), round(p[1] + uy * s + ux * deep, 3), round(o.sill_m, 3)],
                    "yaw": bearing_deg(uy, -ux), "width": o.width_m, "height": o.height_m,
                    "colour": o.colour or DEFAULT_PANEL[o.kind]})  # fmt: skip
    return out


def collision(b: dict, d) -> Mesh:
    """The collision mesh of a house with an interior: the shell (outer and inner wall faces with the holes of what goes
    through, their reveals, floor, ceiling), closed on top where the plain massing ends, and a ramp up to every door
    that stands above the ground."""
    from drycastle_pipeline.buildings.openings import STEP_SIDE_M, STEP_TREAD_M, _Tris

    ins = inside_of(b, d)
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    top_z = float(b["eave_height_m"]) + float(b["roof_height_m"]) / 2
    out = _Tris()
    ring = list(poly.exterior.coords)[:-1]
    n = len(ring)
    for i, p in enumerate(ring):
        q = ring[(i + 1) % n]
        length = math.dist(p, q)
        if length < 1e-6:
            continue
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length

        def to3(s: float, z: float, depth: float = 0.0, p=p, ux=ux, uy=uy):
            return (p[0] + ux * s - uy * depth, p[1] + uy * s + ux * depth, z)

        ops = [o for o in d.openings if o.edge == i]
        holes = _holes(ops, ins)
        face = box(0.0, 0.0, length, top_z)
        _face(out, to3, face.difference(unary_union(holes)) if holes else face, 0.0, GREY, inward=False)
        add_inner_wall(out, to3, ring, i, ops, ins, GREY)
        for o in ops:
            if not ins.through(o):
                continue
            x0, x1, z0, z1 = o.at_m, o.at_m + o.width_m, o.sill_m, o.sill_m + o.height_m
            mid = to3((x0 + x1) / 2, (z0 + z1) / 2, WALL_M / 2)
            for (sa, za), (sb, zb) in (((x0, z0), (x1, z0)), ((x1, z0), (x1, z1)), ((x1, z1), (x0, z1)), ((x0, z1), (x0, z0))):
                out.quad([to3(sa, za, 0.0), to3(sb, zb, 0.0), to3(sb, zb, WALL_M), to3(sa, za, WALL_M)], GREY, mid)
            if o.kind == "door" and z0 > 0.02:  # the steps, as a ramp: its top edge on the sill, closed at both sides
                run = o.steps * STEP_TREAD_M + 0.12 if o.steps else z0 * RAMP_RUN_PER_RISE
                s0, s1 = x0 - STEP_SIDE_M, x1 + STEP_SIDE_M
                out.quad([to3(s0, z0, 0.0), to3(s1, z0, 0.0), to3(s1, 0.0, -run), to3(s0, 0.0, -run)], GREY, to3((s0 + s1) / 2, z0 + 1.0, -run / 2))
                for s in (s0, s1):
                    out.add([to3(s, 0.0, 0.0), to3(s, z0, 0.0), to3(s, 0.0, -run)], GREY)
    add_floor_ceiling(out, poly, ins, GREY, GREY)
    add_partitions(out, plan_of(b, d, ins), ins, GREY)
    for a0, a1, a2 in triangulate(poly):
        ccw = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0]) > 0
        out.add([(x, y, top_z) for x, y in ((a0, a1, a2) if ccw else (a0, a2, a1))], GREY)
    return out.mesh()
