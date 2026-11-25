"""What a house gets where no capture saw it (house_rules): sensible, and the same on every run."""

import math

import pytest

from drycastle_pipeline.buildings import house_rules as hr
from drycastle_pipeline.photos.landmark_tools import _edges

RING = [[0.0, 0.0], [10.0, 0.0], [10.0, 7.0], [0.0, 7.0]]  # the south wall (edge 0) faces the street
FRONT = [{"kind": "window", "edge": 0, "at_m": 1.5, "width_m": 1.4, "height_m": 1.3, "sill_m": 0.9, "posts": [0.5]},
         {"kind": "window", "edge": 0, "at_m": 6.5, "width_m": 1.4, "height_m": 1.3, "sill_m": 0.9}]  # fmt: skip


def roles(roof="gabled", others=(), ridge=0.0):
    return hr.wall_roles(RING, _edges({"footprint": {"exterior": RING, "holes": []}}), list(others), roof, ridge)


def test_walls_know_their_place_under_the_roof():
    r = roles()
    assert [r[i]["roof_edge"] for i in range(4)] == ["eave", "gable", "eave", "gable"]
    assert {v["roof_edge"] for v in roles("hipped").values()} == {"eave"}
    neighbour = {"id": "n", "footprint": {"exterior": [[10.0, 0.0], [16.0, 0.0], [16.0, 7.0], [10.0, 7.0]], "holes": []}}
    assert roles(others=[neighbour])[1]["shared"]


def test_unseen_walls_get_windows_like_the_front_and_one_door_at_the_back():
    made = hr.invent_openings(roles(), {0}, FRONT, eave=2.9, top=6.4, rest_height=2.9, front_normal=(0.0, -1.0))
    doors = [o for o in made if o["kind"] == "door"]
    assert len(doors) == 1 and doors[0]["edge"] == 2  # the wall turned away from the street
    low = [o for o in made if o["kind"] == "window" and o["sill_m"] < 2]
    assert low and all(o["width_m"] == 1.4 and o["sill_m"] == 0.9 for o in low)
    attic = [o for o in made if o["sill_m"] > 2.9]
    assert {o["edge"] for o in attic} == {1, 3}  # a roof rising 3.5 m has rooms under it: one window high in each gable
    for o in made:  # nothing runs off its wall, nothing overlaps the door
        length = 10.0 if o["edge"] in (0, 2) else 7.0
        assert 0.3 <= o["at_m"] and o["at_m"] + o["width_m"] <= length - 0.3
    assert not any(o["edge"] == 2 and o["kind"] == "window" and o["at_m"] < 0.7 + hr.DOOR_W_M + 0.4 for o in made)


def test_a_low_roof_has_no_attic_window_and_a_front_door_means_no_second_door():
    front = [*FRONT, {"kind": "door", "edge": 0, "at_m": 4.0, "width_m": 1.0, "height_m": 2.1, "sill_m": 0.0}]
    made = hr.invent_openings(roles(), {0}, front, eave=2.9, top=4.5, rest_height=2.9, front_normal=(0.0, -1.0))
    assert not any(o["kind"] == "door" for o in made) and not any(o["sill_m"] > 2.9 for o in made)


def test_a_wall_on_or_near_a_neighbour_stays_blank():
    close = {"id": "n", "footprint": {"exterior": [[11.0, 0.0], [16.0, 0.0], [16.0, 7.0], [11.0, 7.0]], "holes": []}}
    made = hr.invent_openings(roles(others=[close]), {0}, FRONT, eave=2.9, top=6.4, rest_height=2.9, front_normal=(0.0, -1.0))
    assert not any(o["edge"] == 1 for o in made)


def test_pipes_stand_at_the_ends_of_eaves_clear_of_openings():
    pipes = hr.invent_downpipes(roles(), FRONT, "gabled")
    assert {p["edge"] for p in pipes} == {0, 2}  # the eave walls, never a gable wall
    assert all(p["at_m"] > 9.0 or p["at_m"] < 0.5 for p in pipes)
    blocked = hr.invent_downpipes(roles(), [{"kind": "door", "edge": 0, "at_m": 9.0, "width_m": 0.9, "height_m": 2.0, "sill_m": 0.0}], "gabled")
    assert next(p for p in blocked if p["edge"] == 0)["at_m"] < 0.5  # moved to the other end
    assert len(hr.invent_downpipes(roles("hipped"), [], "hipped")) == 2


def test_the_eave_follows_the_roof():
    assert hr.eave_style("hipped", 28.0, None) == {"overhang_m": 0.6, "level_soffit": True, "rafters": False}
    assert hr.eave_style("gabled", 45.0, None) == {"overhang_m": 0.25, "level_soffit": False, "rafters": True}
    assert hr.eave_style("gabled", 30.0, 0.8)["rafters"] is False and hr.eave_style("gabled", 30.0, 0.8)["overhang_m"] == 0.8  # a measured one is kept
    assert hr.eave_style("flat", 0.0, None)["overhang_m"] == 0.0


def test_the_chimney_stands_behind_the_ridge_on_the_side_it_was_seen():
    args = dict(roof="gabled", middle=(5.0, 3.5), ridge_dir=0.0, ridge_len=10.0, top=6.4, left_dir=(-1.0, 0.0), front_normal=(0.0, -1.0), area=70.0)
    left = hr.invent_chimney("w1", "left", **args)
    right = hr.invent_chimney("w1", "right", **args)
    assert left["x"] < 5.0 < right["x"] and left["y"] == pytest.approx(4.1) and left["top_m"] > 6.4
    assert hr.invent_chimney("w1", "none", **args) is None
    assert hr.invent_chimney("w1", "unknown", **args) == hr.invent_chimney("w1", "unknown", **args)  # the same every run
    assert hr.invent_chimney("w1", "unknown", **{**args, "area": 20.0}) is None  # a shed has none
    assert hr.invent_chimney("w1", "left", **{**args, "roof": "flat"}) is None
    assert math.isfinite(left["x"])



def test_trim_keeps_what_was_said_and_makes_only_what_has_room():
    kw = dict(wall_light=False, surround_hex=None, gable_hex="#f2f0ea", roof="gabled", plinth_m=0.3)
    said = {"cornice": "yes", "corner_strips": "yes", "shutter_boxes": "yes"}
    t = hr.trim_style("w1", said, "green", eave=3.2, heads=[2.2, 2.3], **kw)
    assert t["lintel_m"] == hr.SHUTTER_BOX_M and t["bands"] == [{"top_m": 3.2, "height_m": hr.CORNICE_M}]
    assert t["corners"]["top_m"] == round(3.2 - hr.CORNICE_M, 2) and t["gable_colour"] == "#f2f0ea" and t["made"] == []
    tight = hr.trim_style("w1", said, "green", eave=2.5, heads=[2.3], **kw)  # the heads reach the eave: no room for any of it
    assert tight["lintel_m"] == 0 and tight["bands"] == [] and tight["corners"] is None
    brick = hr.trim_style("w1", {"cornice": "yes", "corner_strips": "yes"}, "brick", eave=3.2, heads=[2.2], **kw)
    assert brick["bands"] == [] and brick["corners"] is None  # plaster trim is for plastered walls
    none = hr.trim_style("w1", {"cornice": "no", "corner_strips": "no", "shutter_boxes": "no"}, "green", eave=3.2, heads=[2.2], **kw)
    assert none["bands"] == [] and none["lintel_m"] == 0
    assert hr.trim_style("w9", {}, "green", eave=3.2, heads=[2.2], **kw) == hr.trim_style("w9", {}, "green", eave=3.2, heads=[2.2], **kw)


def test_doors_get_steps_to_the_plinth_and_a_canopy_only_where_the_roof_gives_no_shelter():
    door = {"kind": "door", "edge": 2, "at_m": 0.7, "width_m": 0.95, "height_m": 2.05, "sill_m": 0.0}
    r = roles()
    made = hr.dress_door(door, r[2], eave=2.9, overhang=0.5, plinth_m=0.4, invented=True)
    assert made["sill_m"] == 0.4 and made["steps"] == 3 and made["panels"] == 3 and "canopy_m" not in made  # under a wide eave
    seen = hr.dress_door(door, r[1], eave=2.9, overhang=0.5, plinth_m=0.4, invented=False)
    assert seen["sill_m"] == 0.0 and seen["steps"] == 1 and seen["canopy_m"] == 0.7  # measured: stays put; a gable wall: canopy
    assert hr.dress_door({**door, "kind": "garage_door"}, r[2], 2.9, 0.5, 0.4, True) == {**door, "kind": "garage_door", "panels": 4}


def test_roof_windows_only_under_a_roof_with_rooms_and_no_dormers():
    lit = next(i for i in (f"w{k}" for k in range(50)) if hr.chance(i, "skylights") < 0.7)
    made = hr.invent_skylights(lit, roles(), {0}, "gabled", eave=2.9, top=6.4, half=3.5, dormer_edges=set())
    assert made and {s["edge"] for s in made} == {2}  # the slope behind, not the one the capture saw
    assert hr.invent_skylights(lit, roles(), {0}, "gabled", 2.9, 6.4, 3.5, {0}) == []
    assert hr.invent_skylights(lit, roles(), {0}, "gabled", 2.9, 4.4, 3.5, set()) == []  # too low to stand in
    assert hr.invent_skylights(lit, roles("hipped"), {0}, "hipped", 2.9, 6.4, 3.5, set()) == []


def test_a_house_has_one_window_size_with_heads_on_one_line_evenly_spaced():
    made = hr.invent_openings(roles("hipped"), {0}, FRONT, eave=2.9, top=5.0, rest_height=2.9, front_normal=(0.0, -1.0), building_id="w1")
    wins = [o for o in made if o["kind"] == "window"]
    assert {(o["width_m"], o["height_m"], o["sill_m"]) for o in wins} == {(1.4, 1.3, 0.9)}
    side = sorted(o["at_m"] for o in wins if o["edge"] == 1)  # 7 m of wall: two windows, mirrored about its middle
    assert len(side) == 2 and side[0] == pytest.approx(7.0 - side[1] - 1.4, abs=0.02)


def test_the_house_window_is_corrected_when_its_foot_was_hidden_and_measured_ones_take_it():
    odd = [{"kind": "window", "edge": 0, "at_m": 4.9, "width_m": 1.2, "height_m": 0.78, "sill_m": 1.86, "posts": [0.13]}]
    win = hr.house_window(odd, eave=3.3)
    assert win["sill_m"] == 1.4 and win["height_m"] == pytest.approx(1.24) and win["posts"] == []  # head kept, a bar at the edge dropped
    assert hr.house_window([], 3.0)["width_m"] == 1.2
    snapped = hr.to_house_window(odd[0], win)
    assert (snapped["height_m"], snapped["sill_m"]) == (win["height_m"], win["sill_m"]) and snapped["at_m"] == 4.9
    other = {**odd[0], "width_m": 2.4}
    assert hr.to_house_window(other, win) == other  # clearly another size: a second set, left as measured


def test_a_low_wing_gets_the_small_window_and_a_short_wall_none_or_small():
    r = roles("hipped")
    r[2] = {**r[2], "main": False, "roof_edge": "flat"}
    made = hr.invent_openings(r, {0}, FRONT, eave=2.9, top=5.0, rest_height=2.0, front_normal=(0.0, 1.0), building_id="w1")
    low = [o for o in made if o["edge"] == 2 and o["kind"] == "window"]
    assert low and all(o["width_m"] == hr.SMALL_WINDOW_M and o["sill_m"] + o["height_m"] <= 1.7 + 1e-9 for o in low)


def test_the_faces_of_a_bay_each_get_a_window():
    bay = [[0.0, 0.0], [3.0, 0.0], [4.0, -1.0], [5.5, -1.0], [6.5, 0.0], [10.0, 0.0], [10.0, 7.0], [0.0, 7.0]]
    r = hr.wall_roles(bay, _edges({"footprint": {"exterior": bay, "holes": []}}), [], "hipped", None)
    assert [i for i, v in r.items() if v["bay"]] == [1, 2, 3]
    made = hr.invent_openings(r, {7}, FRONT[:0], eave=2.9, top=5.0, rest_height=2.9, front_normal=(0.0, -1.0), building_id="w1")
    faces = [o for o in made if o["edge"] in (1, 2, 3)]
    assert len(faces) == 3 and all(o["kind"] == "window" and o["width_m"] <= r[o["edge"]]["length"] - 0.5 + 1e-9 for o in faces)
    assert len({(o["height_m"], o["sill_m"]) for o in made if o["kind"] == "window" and o["sill_m"] < 2}) == 1  # one head line


def test_a_balcony_seen_on_a_gable_wall_lies_over_the_ground_floor():
    role = {"roof_edge": "gable", "length": 7.9}
    ops = [{"kind": "door", "edge": 0, "at_m": 0.9, "width_m": 0.9, "height_m": 2.1, "sill_m": 0.0}, {"kind": "window", "edge": 0, "at_m": 5.0, "width_m": 1.2, "height_m": 1.3, "sill_m": 1.4}]
    bal = hr.invent_balcony("yes", 0, role, 3.7, 7.6, ops)
    assert bal == {"edge": 0, "at_m": 0.5, "width_m": 6.9, "floor_m": 2.95}
    assert hr.invent_balcony("no", 0, role, 3.7, 7.6, ops) is None
    assert hr.invent_balcony("yes", 0, {**role, "roof_edge": "eave"}, 3.7, 7.6, ops) is None
    assert hr.invent_balcony("yes", 0, role, 3.7, 5.0, ops) is None  # no attic under so low a roof


def test_measured_openings_are_centred_on_their_wall_and_a_door_is_not_a_slit():
    win = {"kind": "window", "edge": 0, "width_m": 1.2, "height_m": 1.3, "sill_m": 0.9}
    row = hr.tidy_walls([{**win, "at_m": 2.6}, {**win, "at_m": 5.6}, {**win, "at_m": 8.6}, {**win, "edge": 1, "at_m": 0.3}], {0: 10.0, 1: 10.0}, {0})
    assert [o["at_m"] for o in row] == [1.4, 4.4, 7.4, 0.3]  # 1.4 m of wall at both ends; the unseen wall is left
    door = {"kind": "door", "edge": 0, "at_m": 8.9, "width_m": 0.5, "height_m": 2.0, "sill_m": 0.0}
    out = hr.tidy_walls([{**win, "at_m": 1.0}, {**win, "at_m": 7.45}, door], {0: 10.0}, {0})
    d = out[-1]
    assert d["width_m"] == hr.DOOR_MIN_W_M
    assert out[1]["at_m"] + 1.2 <= d["at_m"] - 0.1 and d["at_m"] + d["width_m"] <= 10.0 - 0.1
    assert out[0]["at_m"] == pytest.approx(10.0 - d["at_m"] - d["width_m"], abs=0.02)
    attic = {**win, "at_m": 4.4, "sill_m": 4.5}  # under the ridge: a door at the wall's end below does not move it
    assert hr.tidy_walls([attic, {**door, "at_m": 8.0, "width_m": 0.9}], {0: 10.0}, {0})[0]["at_m"] == 4.4
    roomy = hr.tidy_walls([{**win, "at_m": 1.0}, {**win, "at_m": 4.0}], {0: 10.0}, {0})  # wall at both ends: a lopsided row is the house's
    assert [o["at_m"] for o in roomy] == [1.0, 4.0]


def test_one_boxed_sash_beside_a_two_sash_window_is_a_whole_window():
    win = {"kind": "window", "edge": 0, "height_m": 1.5, "sill_m": 1.0}
    out = hr.pair_sashes([{**win, "at_m": 2.4, "width_m": 1.6, "_pair": True}, {**win, "at_m": 7.5, "width_m": 0.8}, {**win, "at_m": 10.0, "width_m": 1.5}], {0: 12.0})
    assert (out[1]["at_m"], out[1]["width_m"], out[1]["posts"]) == (7.1, 1.6, [0.5])
    assert out[2]["width_m"] == 1.5  # a window of another size is left


def test_the_foot_of_the_house_differs_from_house_to_house_and_stays_for_one():
    styles = [hr.base_style(f"w{k}") for k in range(60)]
    assert {s["strip"] for s in styles} == {"footing", "gravel", "concrete", "slabs"} and {s["plinth"] for s in styles} == {"paint", "stone", "brick"}
    assert hr.base_style("w7") == hr.base_style("w7") and all(0.2 <= s["width_m"] <= 1.05 and 0.2 <= s["plinth_m"] <= 0.6 for s in styles)
