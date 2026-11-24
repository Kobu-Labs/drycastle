import math
import random
import zlib

import pytest
from shapely.geometry import LineString, Point
from shapely.ops import unary_union

from drycastle_pipeline.buildings import floorplan, interior
from drycastle_pipeline.buildings.openings import build_detail, parse_detail
from tests.buildings.test_building_detail import house

pytest.importorskip("drycastle_detail", reason="build the Rust engine: make detail-rust-build")

DOOR = {"id": "d", "kind": "door", "edge": 0, "at_m": 8.0, "width_m": 0.95, "height_m": 2.05, "sill_m": 0.3, "steps": 2}
WINDOWS = [
    {"id": f"w{e}{k}", "kind": "window", "edge": e, "at_m": at, "width_m": 1.2, "height_m": 1.2, "sill_m": 1.2}
    for e, ats in ((0, (2.0, 12.0)), (1, (3.5,)), (2, (2.0, 7.0, 12.0)), (3, (3.5,)))
    for k, at in enumerate(ats)
]


def big(bid: str) -> dict:
    b = house()
    return {**b, "id": bid, "footprint": {"exterior": [[0, 0], [16, 0], [16, 9], [0, 9]], "holes": []}}


def detail():
    return parse_detail({"id": "w1", "interior": True, "openings": [DOOR, *WINDOWS]})


def id_of(style: str) -> str:
    """A building id whose seed gives this style."""
    return next(f"w{n}" for n in range(1000) if floorplan.style_of(round(random.Random(zlib.crc32(f"w{n}".encode())).random(), 3)) == style)


def plan(style: str):
    b = big(id_of(style))
    return b, interior.plan_of(b, detail())


@pytest.mark.parametrize("style", ["closed", "mixed", "open"])
def test_rooms_fill_the_house_and_every_one_is_reached(style):
    p = plan(style)[1]
    assert p.style == style
    assert sum(r.polygon.area for r in p.rooms) == pytest.approx((16 - 0.6) * (9 - 0.6), abs=0.05)
    assert unary_union([r.polygon for r in p.rooms]).area == pytest.approx((16 - 0.6) * (9 - 0.6), abs=0.05)  # no overlap
    reached, grew = {next(i for i, r in enumerate(p.rooms) if r.doors)}, True
    while grew:
        more = {x for l in p.links for x in (l.a, l.b) if l.a in reached or l.b in reached} - reached
        reached |= more
        grew = bool(more)
    assert reached == set(range(len(p.rooms)))
    assert all(r.polygon.area >= floorplan.MIN_ROOM_M2 for r in p.rooms)
    assert sorted(w for r in p.rooms for w in r.windows) == sorted(w["id"] for w in WINDOWS)
    assert all(r.type in floorplan.TYPES for r in p.rooms) and [r.type == "hall" for r in p.rooms] == [r.kind == "hall" for r in p.rooms]


@pytest.mark.parametrize("style", ["closed", "mixed", "open"])
def test_it_is_a_house_not_a_grid(style):
    p = plan(style)[1]
    types = [r.type for r in p.rooms]
    assert types.count("living") + types.count("living_kitchen") == 1 and "bathroom" in types and "bedroom" in types
    areas = sorted(r.polygon.area for r in p.rooms if r.kind == "room")
    assert areas[-1] > 2.5 * areas[0]  # rooms of unequal size
    # one way into every room; only the rooms one lives in (and a closed house's kitchen, from the hall) have more
    ways = {i: sum(i in (l.a, l.b) for l in p.links) for i in range(len(p.rooms))}
    for i, r in enumerate(p.rooms):
        if r.type in ("bedroom", "bathroom", "storage"):
            hosted = sum(1 for l in p.links if i in (l.a, l.b) and p.rooms[l.b if l.a == i else l.a].type in ("bathroom", "storage"))
            assert ways[i] - hosted <= 1, (r.id, r.type)  # (a study may be walked through to one more room; nothing else is)
    assert len(p.links) <= len(p.rooms)  # a tree, plus at most the kitchen's second door
    assert all(l.kind == "door" for l in p.links if {p.rooms[l.a].type, p.rooms[l.b].type} & {"bedroom", "bathroom", "storage", "study"})


def test_styles_differ_in_the_living_space():
    closed, opened = plan("closed")[1], plan("open")[1]
    assert closed.rooms[0].kind == "hall" and closed.rooms[0].doors == ["d"]
    assert {"living", "kitchen"} <= {r.type for r in closed.rooms}
    assert "living_kitchen" in {r.type for r in opened.rooms} and "kitchen" not in {r.type for r in opened.rooms}
    assert closed.rooms[0].polygon.area > opened.rooms[0].polygon.area  # a corridor against a vestibule


def test_same_house_same_plan():
    b, p = plan("closed")
    again = interior.plan_of(b, detail())
    assert floorplan.plan_doc(p) == floorplan.plan_doc(again)


@pytest.mark.parametrize("style", ["closed", "mixed", "open"])
def test_no_partition_ends_in_a_window(style):
    b, p = plan(style)
    spans = []
    for o in detail().openings:
        ring = b["footprint"]["exterior"]
        (px, py), (qx, qy) = ring[o.edge], ring[(o.edge + 1) % 4]
        length = math.dist((px, py), (qx, qy))
        ux, uy = (qx - px) / length, (qy - py) / length
        spans.append(LineString([(px + ux * o.at_m, py + uy * o.at_m), (px + ux * (o.at_m + o.width_m), py + uy * (o.at_m + o.width_m))]))
    for w in p.walls:
        for end in (w.p, w.q):
            # (the spans are on the outer face, the partition's end on the inner one: nearer than this it is in front of the glass)
            assert all(s.distance(Point(end)) >= math.hypot(interior.WALL_M, floorplan.CLEAR_LOOSE_M) - 0.01 for s in spans)


def test_doorway_is_open_in_the_collision_and_the_partition_is_not():
    b, p = plan("closed")
    d = detail()
    col = interior.collision(b, d)
    mesh, _ = build_detail(b, d)
    assert mesh.face_count > 0
    from tests.buildings.test_interior import _hit

    wall = next(w for w in p.walls if any(g[2] == "door" for g in w.gaps) and math.dist(w.p, w.q) > 2.5)
    s0, s1, _ = next(g for g in wall.gaps if g[2] == "door")
    length = math.dist(wall.p, wall.q)
    ux, uy = (wall.q[0] - wall.p[0]) / length, (wall.q[1] - wall.p[1]) / length
    solid = (s1 + length) / 2 if length - s1 > s0 else s0 / 2  # the middle of the longer piece of wall beside the door

    def before(s: float, z: float):
        return (wall.p[0] + ux * s + uy * 0.5, wall.p[1] + uy * s - ux * 0.5, 0.3 + z)

    assert _hit(col, before((s0 + s1) / 2, 1.0), (-uy, ux, 0)) > 0.9  # through the doorway
    assert _hit(col, before((s0 + s1) / 2, 2.3), (-uy, ux, 0)) == pytest.approx(0.45, abs=0.01)  # the wall over it
    assert _hit(col, before(solid, 1.0), (-uy, ux, 0)) == pytest.approx(0.45, abs=0.01)  # the partition beside it


def test_a_garage_door_gets_a_room_a_car_fits_in():
    door = {"id": "g", "kind": "garage_door", "edge": 2, "at_m": 0.6, "width_m": 2.6, "height_m": 2.2, "sill_m": 0.0}
    d = parse_detail({"id": "w1", "interior": True, "openings": [DOOR, door, *(w for w in WINDOWS if w["id"] != "w22")]})
    p = interior.plan_of(big(id_of("closed")), d)
    (i, garage), = [(i, r) for i, r in enumerate(p.rooms) if r.type == "garage"]
    assert garage.garage_doors == ["g"] and garage.doors == []
    x0, y0, x1, y1 = garage.polygon.bounds
    assert x1 - x0 >= 2.4 and y1 - y0 >= 4.8  # in from the door, on the far long wall
    assert sum(r.polygon.area for r in p.rooms) == pytest.approx((16 - 0.6) * (9 - 0.6), abs=0.05)
    (link,) = [l for l in p.links if i in (l.a, l.b)]
    assert link.kind == "door" and p.rooms[link.a if link.b == i else link.b].type != "garage"


def test_plan_doc_names_rooms_and_links():
    doc = floorplan.plan_doc(plan("mixed")[1])
    assert {"openness", "style", "rooms", "partitions"} <= set(doc)
    ids = {r["id"] for r in doc["rooms"]}
    assert all(l["to"] in ids and l["kind"] in ("door", "arch", "open") for r in doc["rooms"] for l in r["links"])
