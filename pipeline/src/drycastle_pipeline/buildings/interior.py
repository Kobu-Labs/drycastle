"""The inside of a house with a building detail: walls with a width, a floor, a ceiling, openings that go through.

A detail opts in with ``interior: true``. The outer face of every wall stays on the footprint (what the photos measured);
the wall grows ``WALL_M`` inward. The floor lies at the sill of the lowest door (the steps in front already climb to
it), the ceiling one storey above it (at most ``STOREY_M``); what is above the ceiling stays closed (upper floors: later). A window or a door
between floor and ceiling is a hole through the whole wall; a garage door, a recess and what is cut into a recess stay
closed, as do windows above the ceiling.

``openings.build_detail`` calls in here for the visual parts; ``collision`` is the same shell as the collision mesh
(ADR 0040), with a ramp where the visual has door steps.
"""

import math
from dataclasses import dataclass

from shapely.geometry import Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.mesh import Mesh, triangulate
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

WALL_M = 0.3
MIN_FLOOR_M = 0.05  # above the flattened ground, so the floor and the terrain do not fight
STOREY_M = 3.0  # a room is not higher than this: a taller wall has an attic or a floor above it, which stays closed
HEAD_CLEAR_M = 0.05  # an opening's head stays this far under the ceiling to go through
INNER_WALL, FLOOR, CEILING = (236, 232, 222), (146, 118, 88), (244, 242, 236)
GREY = (128, 128, 128)
RAMP_RUN_PER_RISE = 2.0  # a door above the ground with no steps: the collision ramp's run


@dataclass(frozen=True)
class Inside:
    floor: float
    ceiling: float

    def through(self, o) -> bool:
        """Does opening ``o`` go through the wall into the room?"""
        if o.kind not in ("window", "door") or o.parent is not None:
            return False
        return o.sill_m >= self.floor - MIN_FLOOR_M - 0.01 and o.sill_m + o.height_m <= self.ceiling - HEAD_CLEAR_M


def inside_of(b: dict, d) -> Inside | None:
    """The room of building ``b`` with detail ``d``, or None when the detail asks for none."""
    if not getattr(d, "interior", False):
        return None
    eave = float(b["eave_height_m"])
    doors = [o.sill_m for o in d.openings if o.kind == "door" and o.parent is None and o.sill_m < eave / 2]
    floor = min(doors) if doors else (d.plinth.height_m if d.plinth is not None else 0.15)
    floor = max(floor, MIN_FLOOR_M)
    levels = max(int(b.get("levels") or 1), 1)
    ceiling = min(eave, floor + min((eave - floor) / levels, STOREY_M))
    if ceiling - floor < 2.0:
        from drycastle_pipeline.buildings.openings import DetailError

        raise DetailError(f"{d.id}: interior: only {ceiling - floor:.2f} m between the floor ({floor:.2f}) and the ceiling ({ceiling:.2f})")
    return Inside(round(floor, 3), round(ceiling, 3))


def _holes(ops, ins: Inside):
    return [box(o.at_m, o.sill_m, o.at_m + o.width_m, o.sill_m + o.height_m) for o in ops if ins.through(o)]


def _face(out, to3, face, depth: float, colour, inward: bool) -> None:
    """A wall face (a polygon of the wall's own s, z plane) at ``depth`` behind the wall line."""
    for g in _polygons(face):
        for a0, a1, a2 in triangulate(orient(g, 1.0)):
            area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
            order = (a0, a1, a2) if (area > 0) != inward else (a0, a2, a1)
            out.add([to3(*c, depth) for c in order], colour)


def add_inner_wall(out, to3, ring: list, i: int, ops, ins: Inside, colour=INNER_WALL) -> None:
    """The room's side of wall ``i``: floor to ceiling between the corners' diagonals, with the holes of what goes through."""
    from drycastle_pipeline.buildings.openings import _band_ends

    lo, hi = _band_ends(ring, i, -WALL_M)
    if hi - lo < 0.02:
        return
    face = box(lo, ins.floor, hi, ins.ceiling)
    holes = _holes(ops, ins)
    if holes:
        face = face.difference(unary_union(holes))
    _face(out, to3, face, WALL_M, colour, inward=True)


def add_floor_ceiling(out, poly: Polygon, ins: Inside, floor=FLOOR, ceiling=CEILING) -> None:
    """The floor (seen from above) and the ceiling (seen from below), over the whole footprint: their rims are in the walls."""
    for tri in triangulate(poly):
        a0, a1, a2 = tri
        ccw = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0]) > 0
        up = (a0, a1, a2) if ccw else (a0, a2, a1)
        out.add([(x, y, ins.floor) for x, y in up], floor)
        out.add([(x, y, ins.ceiling) for x, y in reversed(up)], ceiling)


def plan_of(b: dict, d, ins: Inside | None = None):
    """The generated floor plan (buildings/floorplan.py) of a hollow house, or None."""
    from drycastle_pipeline.buildings import floorplan

    ins = ins or inside_of(b, d)
    return floorplan.make_plan(b, d, ins, WALL_M) if ins is not None else None


def add_partitions(out, plan, ins: Inside, colour=INNER_WALL) -> None:
    """The partitions of the plan: a slab per wall from floor to ceiling, left out where a doorway is, with the piece of
    wall over a door or an archway."""
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
                out.cuboid(to3, (cursor, s0), (ins.floor, ins.ceiling), (-half, half), colour, ("s0", "s1", "d0", "d1"))
            head = ins.floor + (fp.DOOR_H_M if kind == "door" else fp.ARCH_H_M)
            if kind in ("door", "arch") and ins.ceiling - head > 0.05:
                out.cuboid(to3, (s0, s1), (head, ins.ceiling), (-half, half), colour, ("z0", "d0", "d1"))
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
