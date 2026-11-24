import random

import pytest

from drycastle_pipeline.buildings import roomtypes as rt


@pytest.mark.parametrize("style", ["closed", "mixed", "open"])
@pytest.mark.parametrize("area", [15.0, 28.0, 45.0, 70.0, 100.0, 150.0, 240.0])
def test_programme_fills_the_floor_with_known_types(style, area):
    wants = rt.programme(area, style, random.Random(7))
    assert sum(w.area for w in wants) == pytest.approx(area)
    assert all(w.type in rt.TYPES and w.type != rt.HALL for w in wants)
    types = [w.type for w in wants]
    assert types.count(rt.LIVING) + types.count(rt.LIVING_KITCHEN) == 1


def test_a_family_house_has_rooms_of_unequal_size():
    wants = rt.programme(100.0, "closed", random.Random(3))
    by = {t: [w.area for w in wants if w.type == t] for t in rt.TYPES}
    assert len(by[rt.LIVING]) == 1 and len(by[rt.KITCHEN]) == 1 and len(by[rt.BEDROOM]) == 3 and by[rt.BATHROOM]
    assert by[rt.LIVING][0] > max(by[rt.BEDROOM]) > min(by[rt.BEDROOM]) > max(by[rt.BATHROOM])
    assert max(by[rt.BATHROOM]) < 9.0 and all(a < 8.0 for a in by[rt.STORAGE])
    assert wants[-1].type == rt.BEDROOM and wants[-1].area == max(by[rt.BEDROOM])  # the largest bedroom is the last


def test_style_decides_the_living_space():
    assert [w.type for w in rt.programme(90.0, "open", random.Random(1))].count(rt.LIVING_KITCHEN) == 1
    closed = [w.type for w in rt.programme(90.0, "closed", random.Random(1))]
    assert rt.LIVING in closed and rt.KITCHEN in closed and rt.LIVING_KITCHEN not in closed


def test_small_houses():
    assert [w.type for w in rt.programme(15.0, "closed", random.Random(1))] == [rt.LIVING_KITCHEN]
    assert [w.type for w in rt.programme(28.0, "closed", random.Random(1))] == [rt.LIVING_KITCHEN, rt.BATHROOM]


def test_a_large_house_gets_more_rooms_not_a_hall_to_live_in():
    wants = rt.programme(240.0, "open", random.Random(5))
    assert len(wants) >= 12 and max(w.area for w in wants) < 80.0


def test_scaled_keeps_small_rooms_small():
    wants = rt.scaled([rt.Want(rt.LIVING, 20.0), rt.Want(rt.BATHROOM, 5.0)], 50.0)
    assert sum(w.area for w in wants) == pytest.approx(50.0) and wants[1].area == pytest.approx(6.25)
