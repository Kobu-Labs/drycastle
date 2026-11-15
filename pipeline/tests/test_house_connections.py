"""Service drops: which wall a wire lands on, how high, which houses and poles take part."""

import math

from drycastle_pipeline.house_connections import PER_POLE, plan_drops


def house(hid: str, x0: float, y0: float, size: float = 10.0, roof: str = "gabled", eave: float = 5.0, cls: str = "area:house") -> dict:
    ring = [[x0, y0], [x0 + size, y0], [x0 + size, y0 + size], [x0, y0 + size], [x0, y0]]
    return {"id": hid, "class": cls, "footprint": {"exterior": ring, "holes": []}, "eave_height_m": eave, "roof": roof}


ELEV = {"h1": 100.0, "h2": 100.0, "h3": 100.0}


def test_the_wire_lands_on_the_wall_that_faces_the_pole():
    (drop,) = plan_drops([house("h1", 0, 0)], ELEV, [("p1", 5.0, -8.0)])  # a pole south of the house
    assert drop.house == "h1" and drop.pole == "p1" and drop.asset.startswith("prim:connector_wall_")
    assert drop.y == 0.0 and 0.8 <= drop.x <= 9.2  # on the south wall, away from the corners
    assert math.isclose(drop.rotation_deg, 270.0)  # the wall faces south
    assert math.isclose(drop.z, 100.0 + 5.0 - 0.8)  # under the eave, an elevation: the ground under the house plus a height


def test_the_wire_stays_clear_of_the_corners():
    (drop,) = plan_drops([house("h1", 0, 0)], ELEV, [("p1", -3.0, -8.0)])  # south-west of the house
    assert drop.y == 0.0 and drop.x >= 0.8


def test_a_low_eave_is_not_hung_below_head_height():
    (drop,) = plan_drops([house("h1", 0, 0, eave=2.4)], ELEV, [("p1", 5.0, -8.0)])
    assert math.isclose(drop.z, 100.0 + 2.2)


def test_a_flat_roof_can_take_a_mast_turned_towards_its_pole():
    seen = set()
    for i in range(40):  # the kind is seeded by the building id, so vary it
        hid = f"f{i}"
        (drop,) = plan_drops([house(hid, 0, 0, roof="flat", eave=6.0)], {hid: 100.0}, [("p1", 5.0, -8.0)])
        seen.add(drop.asset.split("_")[1])
        if drop.asset.startswith("prim:connector_roof_mast"):
            assert math.isclose(drop.z, 106.0) and 0.0 < drop.y < 10.0  # at roof level, in from the wall
            assert 240 < drop.rotation_deg < 300  # looking south at the pole
    assert seen == {"wall", "roof"}


def test_the_same_building_always_gets_the_same_answer():
    a = plan_drops([house("h1", 0, 0, roof="flat")], ELEV, [("p1", 5.0, -8.0)])
    b = plan_drops([house("h1", 0, 0, roof="flat")], ELEV, [("p1", 5.0, -8.0)])
    assert a == b


def test_a_house_takes_the_nearest_pole_with_room_and_a_pole_feeds_a_few_houses():
    houses = [house(f"h{i}", i * 12.0, 0) for i in range(PER_POLE + 1)]
    elev = {h["id"]: 100.0 for h in houses}
    poles = [("near", 24.0, -4.0), ("far", 24.0, -20.0)]
    drops = plan_drops(houses, elev, poles)
    fed = {p: sum(1 for d in drops if d.pole == p) for p in ("near", "far")}
    assert len(drops) == PER_POLE + 1 and fed["near"] == PER_POLE and fed["far"] == 1


def test_far_houses_sheds_and_houses_already_connected_get_nothing():
    pole = [("p1", 5.0, -8.0)]
    assert plan_drops([house("h1", 0, 0)], ELEV, [("p1", 5.0, -200.0)]) == []  # beyond the service distance
    assert plan_drops([house("h1", 0, 0, cls="area:tiny")], ELEV, pole) == []  # a shed
    assert plan_drops([house("h1", 0, 0)], ELEV, pole, taken=[(5.0, 0.0)]) == []  # a connector is there already
    assert len(plan_drops([house("h1", 0, 0)], ELEV, pole, service_m=100.0)) == 1


def test_a_wire_does_not_go_through_another_building():
    houses = [house("h1", 0, 0), house("h2", 0, -9.0, size=6.0)]  # h2 stands between the pole and h1's south wall
    drops = plan_drops(houses, {"h1": 100.0, "h2": 100.0}, [("p1", 3.0, -12.0)])
    assert [d.house for d in drops] == ["h2"]
