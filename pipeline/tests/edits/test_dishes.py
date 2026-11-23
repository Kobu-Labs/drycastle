"""Satellite dishes: which houses get one (seeded), where on the wall, what keeps them off, and their ids."""

import json
import math

from shapely.geometry import LineString

from drycastle_pipeline.assets import load_manifest, validate_asset_ref
from drycastle_pipeline.building_detail import Detail, Opening
from drycastle_pipeline.dishes import DISH_ASSET, add_dishes, plan_dishes
from drycastle_pipeline.rules import load_section

CFG = load_section("dishes")
ROAD = [LineString([(-50, -8), (50, -8)])]  # south of a house at the origin


def house(hid: str, x0: float = 0.0, y0: float = 0.0, w: float = 10.0, d: float = 10.0, eave: float = 5.0, cls: str = "area:house", **extra) -> dict:
    ring = [[x0, y0], [x0 + w, y0], [x0 + w, y0 + d], [x0, y0 + d], [x0, y0]]
    return {"id": hid, "class": cls, "footprint": {"exterior": ring, "holes": []}, "eave_height_m": eave, "roof": "gabled", **extra}


ELEV = {"h1": 100.0}


def one(buildings=None, **kw):
    out = plan_dishes(buildings or [house("h1")], {b["id"]: 100.0 for b in (buildings or [house("h1")])}, kw.pop("roads", ROAD), chance=kw.pop("chance", 1.0), **kw)
    return out


def test_the_dish_is_on_the_wall_that_faces_the_road_under_the_eave() -> None:
    (dish,) = one()
    assert dish.asset == DISH_ASSET and dish.house == "h1"
    assert dish.y == 0.0 and 0.8 <= dish.x <= 9.2  # the south wall, away from the corners
    assert math.isclose(dish.rotation_deg, 270.0)  # which faces south, and the model's +x points out of the wall
    assert 100.0 + 5.0 - 0.9 - 1e-6 <= dish.z <= 100.0 + 5.0 - 0.6 + 1e-6  # an elevation: ground under the house plus a height


def test_the_wall_follows_the_road() -> None:
    (dish,) = one(roads=[LineString([(-8, -50), (-8, 50)])])  # a road to the west
    assert dish.x == 0.0 and math.isclose(dish.rotation_deg, 180.0)
    (dish,) = one(roads=[LineString([(50, 12), (-50, 12)])])  # and to the north
    assert dish.y == 10.0 and math.isclose(dish.rotation_deg, 90.0)


def test_a_low_eave_is_not_hung_below_head_height() -> None:
    (dish,) = one([house("h1", eave=2.7)])
    assert math.isclose(dish.z, 102.0)
    assert one([house("h1", eave=2.4)]) == []  # no room under the eave


def test_the_choice_is_seeded_stable_and_follows_the_chance() -> None:
    houses = [house(f"h{i}", i * 20.0) for i in range(300)]
    elev = {b["id"]: 100.0 for b in houses}
    roads = [LineString([(-50, -8), (6100, -8)])]
    chosen = {c: [d.house for d in plan_dishes(houses, elev, roads, chance=c)] for c in (0.0, 0.2, 0.4, 1.0)}
    assert chosen[0.0] == [] and len(chosen[1.0]) == 300
    assert 0.3 < len(chosen[0.4]) / 300 < 0.5  # the default share is 0.4
    assert chosen[0.4] == [d.house for d in plan_dishes(houses, elev, roads, chance=0.4)]
    assert plan_dishes(houses, elev, roads, chance=0.4) == plan_dishes(list(reversed(houses)), elev, roads, chance=0.4)  # whatever the order
    assert set(chosen[0.2]) <= set(chosen[0.4])  # a house keeps its roll: raising the chance only adds dishes
    default = plan_dishes(houses, elev, roads)  # no chance given: rules.yaml's dish_chance
    assert CFG["dish_chance"] == 0.4 and [d.house for d in default] == chosen[0.4]


def test_a_house_that_has_a_dish_keeps_it_wherever_it_was_dragged() -> None:
    (dish,) = one()
    assert one(existing=[(dish.x, dish.y)]) == []
    assert one(existing=[(dish.x + 3.0, dish.y - 1.0)]) == []  # moved 1 m off the wall: still its dish
    assert len(one(existing=[(dish.x, dish.y - 30.0)])) == 1  # far away: not this house's


def test_ids_are_the_next_free_ones_and_the_others_do_not_change() -> None:
    objects = [{"id": "o-0003", "asset": "prim:pole_lamp_1", "position": [5.0, 5.0]}, {"id": "o-0010", "asset": "prim:wall", "position": [6.0, 6.0]}]
    before = json.dumps(objects)
    houses = [house("h1"), house("h2", 30.0)]
    new = plan_dishes(houses, {"h1": 100.0, "h2": 100.0}, ROAD, chance=1.0)
    ids = add_dishes(objects, new, [1000.0, 2000.0])
    assert ids == ["o-0011", "o-0012"] and json.dumps(objects[:2]) == before
    assert objects[2]["position"] == [round(new[0].x + 1000.0, 3), round(new[0].y + 2000.0, 3)] and objects[2]["z"] == new[0].z
    # a rebuild changes nothing: the dishes are in the file, so a rerun finds both houses done
    again = plan_dishes(houses, {"h1": 100.0, "h2": 100.0}, ROAD, existing=[(o["position"][0] - 1000.0, o["position"][1] - 2000.0) for o in objects[2:]], chance=1.0)
    assert again == []


def opening(edge: int, at: float, width: float, sill: float = 0.9, height: float = 1.2, kind: str = "window") -> Opening:
    return Opening(id=f"o{edge}_{at}", kind=kind, edge=edge, at_m=at, width_m=width, height_m=height, sill_m=sill)


def test_a_dish_keeps_clear_of_the_known_windows() -> None:
    plain = one()[0]
    detail = Detail(id="h1", openings=[opening(0, plain.x - 0.5, 1.0, sill=3.0, height=1.0)])  # a window just where it would go
    (moved,) = one(details={"h1": detail})
    assert abs(moved.x - plain.x) > 0.5 and moved.y == 0.0
    window = detail.openings[0]
    assert moved.x <= window.at_m - 0.5 or moved.x >= window.at_m + window.width_m + 0.5  # half a dish and a margin clear of it


def test_a_window_below_the_dish_does_not_matter() -> None:
    plain = one()[0]
    low = Detail(id="h1", openings=[opening(0, 0.0, 10.0, sill=0.5, height=1.2)])  # a band of windows low on the wall
    assert one(details={"h1": low}) == [plain]


def test_a_wall_with_no_room_gets_no_dish() -> None:
    wide = Detail(id="h1", openings=[opening(0, 0.3, 9.4, sill=3.0, height=1.0)])  # windows all along the road wall at dish height
    assert one(details={"h1": wide}) == []
    assert one([house("h1", w=1.4)], roads=[LineString([(-50, -8), (50, -8)])]) == []  # a wall too short for a dish
    assert one([house("h1", w=1.4, d=10.0)]) == []


def test_a_dish_keeps_clear_of_the_house_connector() -> None:
    (plain,) = one()
    (moved,) = one(connectors=[(plain.x, 0.0)])  # a wall connector right there
    assert abs(moved.x - plain.x) >= 0.9 and moved.y == 0.0
    assert one(connectors=[(plain.x, 5.0)]) == [plain]  # one deep inside the house (a roof mast) far from this wall does not matter
    assert one(connectors=[(k * 1.5, 0.0) for k in range(8)]) == []  # connectors all along the wall leave no room


def test_only_dwellings_are_considered() -> None:
    assert one([house("h1", cls="area:shed")]) == []
    assert one([house("h1", model="church")]) == []
    assert one(roads=[]) == []  # no road to face
    assert one(roads=[LineString([(0, -500), (10, -500)])]) == []  # too far from any
    assert len(one([house("h1", cls="type:commercial")])) == 1


def test_the_dish_asset_is_in_the_manifest_at_a_sensible_size() -> None:
    manifest = load_manifest(private=False)
    assert validate_asset_ref(DISH_ASSET, manifest) is None
    (entry,) = [a for a in manifest["assets"] if a["id"] == DISH_ASSET]
    assert entry["category"] == "other" and entry["licence"] == "CC0" and "generated" in entry["tags"]
    assert all(0.4 <= v <= 1.0 for v in entry["size_m"])  # a household dish, about 0.7 m across
