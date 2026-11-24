"""The floor plan of a hollow house (buildings/interior.py), generated: a hall from the front door, the rooms of the
house's programme (ADR 0042), the partitions between them and the doorways that join them. Nothing is
drawn by hand and nothing is stored: the plan is worked out from the footprint, the openings and a seed (the building's
id), so the same house always gets the same plan.

The search is Rust (pipeline/rust/detail/src/floorplan.rs, built with ``make detail-rust-build``); this module turns a
house into its entry frame and the answer back.

How (all in the *entry frame*: square to the longest wall, x running inward from the front door, so every partition is
parallel or square to the house's main walls):

1. **Openness**, 0..1 from the seed, picks the style: *closed* (a corridor, a door to every room), *mixed* (a shorter
   hall, the living room and the kitchen joined by an archway) or *open* (one space to live and cook in, a vestibule at
   most, the other rooms off that space).
2. **Hall**: a strip from the front door inward (the stem). When the door is in a long wall and the house is deep enough
   the stem ends in a corridor along the house (the spine). A closed house too shallow for that is planned twice, with
   a corridor along the door's wall as well. A strip that would leave a sliver beside it is widened to the wall instead.
   Each house is planned many times (the long hall, an entry only, no hall; TRIES of each) and the plan with the fewest
   faults (``faults``) is kept.
3. **Programme**: the rooms the house is to have and how large each should be, from the floor left beside the hall
   (``programme``): rooms of unequal size, as their uses are.
4. **Rooms**: each piece of floor beside the hall gets its share of the programme, the largest rooms in the largest
   pieces, and is cut to it: the list is halved, the piece cut where the halves' sizes say (moved to where no window
   is in the way and where walls line up), each half again. What is public ends up toward the front door.
5. **Doorways**: a tree, as in a house. Every room has one way in: from the hall where it touches it, else from the
   living room, else from the room that suits it (a bathroom from a bedroom, a pantry from the kitchen). Only the
   living room, the kitchen and the dining room open into each other. A room nothing can reach is merged into its
   neighbour.
"""

import json
import math
import random
import zlib
from dataclasses import dataclass, field

from shapely.affinity import rotate
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.polygon import orient

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

PARTITION_M = 0.1
DOOR_H_M, ARCH_H_M = 2.0, 2.2
MIN_ROOM_M2 = 2.0
CLEAR_LOOSE_M = 0.07  # a partition ends at least this far from a window's or a door's side (mirrors floorplan.rs)
MAX_DWELLING_M2 = 350.0  # more floor than this is no house (a culture house, a farm hall): no plan, one open space
TYPES = ("hall", "living", "kitchen", "living_kitchen", "dining", "bedroom", "bathroom", "storage", "study")


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
    type: str = ""  # what the room is for, one of TYPES


@dataclass
class Plan:
    openness: float
    style: str
    rooms: list[Room]
    walls: list[Wall]
    links: list[Link]


def style_of(openness: float) -> str:
    return "closed" if openness < 0.4 else "mixed" if openness < 0.75 else "open"


class EngineMissing(RuntimeError):
    pass


def _engine():
    hint = "the Rust engine is not installed or out of date: build it with `make detail-rust-build`"
    try:
        from drycastle_detail import floor_plans
    except ImportError as exc:
        raise EngineMissing(hint) from exc
    return floor_plans


def _framed(b: dict, d, ins, wall_m: float) -> tuple[dict, float, float] | None:
    """The house as the engine wants it, in the entry frame (square to the longest wall, so the partitions are; turned so
    that x runs inward from the front door), with the frame's angle and the openness. None when the footprint leaves no room
    inside its walls, or far too much to be a house."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    ring = list(poly.exterior.coords)[:-1]
    inner = max(_polygons(poly.buffer(-wall_m, join_style=2)), key=lambda g: g.area, default=None)
    if inner is None or not 8.0 <= inner.area <= MAX_DWELLING_M2:
        return None
    rng = random.Random(zlib.crc32(str(b.get("id", d.id)).encode()))
    openness = round(rng.random(), 3)

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

    f = orient(to_frame(inner), 1.0)
    e = to_frame(Point(entry)) if entry is not None else None
    job = {
        "exterior": list(f.exterior.coords),
        "holes": [list(r.coords) for r in f.interiors],
        "openings": [
            {"id": i, "kind": k, "seg": list(to_frame(s).coords), "posts": [to_frame(p).coords[0] for p in posts]}
            for i, k, s, posts in ops
        ],
        "entry": [e.x, e.y] if e is not None else None,
        "seed": rng.getrandbits(32),
        "style": style_of(openness),
    }
    return job, angle, openness


_PLANNED: dict[str, dict | None] = {}  # the engine's answer by its input: a house is asked for by several stages of one export


def make_plans(houses: list[tuple]) -> list[Plan | None]:
    """The plans of ``houses``, each ``(b, d, ins, wall_m)``: building, detail, room of interior.inside_of, wall width. In map
    coordinates; the new ones are worked out side by side."""
    framed = [_framed(*h) for h in houses]
    keys = [json.dumps(f[0]) if f else None for f in framed]
    new = list({k for k in keys if k is not None and k not in _PLANNED})
    if new:
        answers = json.loads(_engine()("[" + ",".join(new) + "]"))
        _PLANNED.update(zip(new, answers, strict=True))
    return [_in_map(_PLANNED[k], f) if f and _PLANNED[k] else None for k, f in zip(keys, framed, strict=True)]


def make_plan(b: dict, d, ins, wall_m: float) -> Plan | None:
    """The plan of building ``b``; see make_plans."""
    return make_plans([(b, d, ins, wall_m)])[0]


def _in_map(found: dict, framed: tuple[dict, float, float]) -> Plan:
    job, angle, openness = framed

    def to_map(g):
        return rotate(g, angle, origin=(0, 0), use_radians=True)

    def at(p):
        m = to_map(Point(p))
        return (m.x, m.y)

    rooms = [
        Room(f"r{i}", "hall" if r["type"] == "hall" else "room", orient(to_map(Polygon(r["exterior"], r["holes"])), 1.0),
             r["windows"], r["doors"], r["type"])
        for i, r in enumerate(found["rooms"])
    ]  # fmt: skip
    walls = [Wall(at(p), at(q), [tuple(g) for g in gaps]) for p, q, gaps in found["walls"]]
    links = [Link(a, b, kind, at(p), at(q)) for a, b, kind, p, q in found["links"]]
    return Plan(openness, job["style"], rooms, walls, links)


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
