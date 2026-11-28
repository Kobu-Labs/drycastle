"""house_rules.regularise: the openings as a builder sets them (docs/house-anomalies.md)."""

from drycastle_pipeline.buildings import house_rules as hr


def win(edge, at, w=1.2, sill=0.9, h=1.3, **kw):
    return {"kind": "window", "edge": edge, "at_m": at, "width_m": w, "sill_m": sill, "height_m": h, "confidence": "medium", **kw}


def test_lone_window_on_a_gable_is_mirrored():
    out, said = hr.regularise([win(0, 4.95, w=1.1)], {0: 7.83}, 3.7)
    ats = sorted(o["at_m"] for o in out)
    assert len(out) == 2 and abs(ats[0] - (7.83 - 4.95 - 1.1)) < 0.01
    assert any("mirroring" in s for s in said)


def test_odd_small_window_takes_the_row_size():
    row = [win(0, a) for a in (0.5, 3.0, 5.5, 10.5)] + [win(0, 8.2, w=0.85, sill=0.7, h=0.8)]
    out, _ = hr.regularise(row, {0: 14.0}, 3.8)
    odd = next(o for o in out if abs(o["at_m"] + o["width_m"] / 2 - 8.625) < 0.05)
    assert (odd["width_m"], odd["height_m"], round(odd["sill_m"] + odd["height_m"], 2)) == (1.2, 1.3, 2.2)


def test_a_larger_window_is_not_shrunk():
    row = [win(0, 0.5, w=1.7), win(0, 4.0, w=0.64, h=0.8, sill=1.4), win(0, 6.0, w=0.64, h=0.8, sill=1.4), win(0, 8.0, w=0.64, h=0.8, sill=1.4)]
    out, _ = hr.regularise(row, {0: 12.0}, 3.8)
    assert max(o["width_m"] for o in out) == 1.7


def test_window_over_two_floors_is_split():
    ops = [win(0, 1.0, sill=0.9, h=1.2), win(0, 1.0, sill=3.4, h=1.2), {"kind": "glass_door", "edge": 0, "at_m": 4.7, "width_m": 0.6, "sill_m": 0.0, "height_m": 4.6}]
    out, said = hr.regularise(ops, {0: 10.0}, 5.1)
    parts = sorted((o for o in out if abs(o["at_m"] + o["width_m"] / 2 - 5.0) < 0.05), key=lambda o: o["sill_m"])
    assert len(parts) == 2 and all(o["height_m"] <= hr.TALL_M for o in parts)
    assert parts[0]["sill_m"] + parts[0]["height_m"] < min(p["sill_m"] for p in parts[1:])


def test_squat_garage_door_is_full_height_and_a_wide_one_is_two():
    out, _ = hr.regularise([{"kind": "garage_door", "edge": 0, "at_m": 0.9, "width_m": 2.4, "sill_m": 0.0, "height_m": 0.68}], {0: 10.0}, 5.8)
    assert out[0]["height_m"] == hr.GARAGE_H_M
    out, _ = hr.regularise([{"kind": "garage_door", "edge": 0, "at_m": 0.6, "width_m": 4.6, "sill_m": 0.0, "height_m": 2.5}], {0: 12.0}, 6.0)
    assert len(out) == 2 and all(o["kind"] == "garage_door" for o in out)


def test_garage_door_stops_under_the_window_over_it():
    ops = [{"kind": "garage_door", "edge": 0, "at_m": 1.0, "width_m": 2.3, "sill_m": 0.0, "height_m": 3.5}, win(0, 1.5, sill=2.7, h=0.7)]
    out, _ = hr.regularise(ops, {0: 6.0}, 5.0)
    g = next(o for o in out if o["kind"] == "garage_door")
    assert g["height_m"] <= 2.55 and len(out) == 2


def test_window_reaching_the_eave_is_moved_under_it():
    out, _ = hr.regularise([win(4, 7.8, w=0.72, sill=4.57, h=0.88)], {4: 12.0}, 4.72)
    assert out[0]["sill_m"] + out[0]["height_m"] <= 4.72 - 0.25


def test_sashes_side_by_side_are_one_window():
    out, _ = hr.regularise([win(5, 2.37, w=0.77), win(5, 3.29, w=0.77)], {5: 12.0}, 4.7, walls=set())
    assert len(out) == 1 and out[0]["width_m"] == round(3.29 + 0.77 - 2.37, 2) and out[0]["posts"] == [0.5]


def test_ground_window_head_on_the_door_head_keeps_its_sill():
    ops = [{"kind": "door", "edge": 0, "at_m": 0.7, "width_m": 0.9, "sill_m": 0.0, "height_m": 2.1}, win(0, 4.0, w=1.06, sill=2.02, h=0.97)]
    out, _ = hr.regularise(ops, {0: 7.9}, 5.6)
    w = next(o for o in out if o["kind"] == "window")
    assert round(w["sill_m"] + w["height_m"], 2) == 2.1


def test_nothing_overlaps_after():
    ops = [{"kind": "door", "edge": 0, "at_m": 10.48, "width_m": 1.0, "sill_m": 0.0, "height_m": 2.1}, win(0, 1.36, w=1.81, sill=2.06, h=1.07),
           win(0, 4.7, w=1.81, sill=2.06, h=1.07), win(0, 11.66, w=0.98, sill=2.17, h=0.84), win(0, 13.05, w=1.81, sill=1.02, h=1.07)]
    out, _ = hr.regularise(ops, {0: 24.1}, 4.27)
    assert any(o["kind"] == "door" for o in out)
    for a in out:
        for b in out:
            if a is not b:
                assert not (a["at_m"] < b["at_m"] + b["width_m"] and b["at_m"] < a["at_m"] + a["width_m"]
                            and a["sill_m"] < b["sill_m"] + b["height_m"] and b["sill_m"] < a["sill_m"] + a["height_m"])


def test_missed_items_from_words_and_from_the_list():
    got = hr.missed_items("upper window at about 8 m behind the pole; small window left of the entrance door at about 6.8 m")
    assert [(m["kind"], m["s0"], m["floor"], m["small"]) for m in got] == [("window", 8.0, "upper", False), ("window", 6.8, "ground", True)]
    assert hr.missed_items("garage door at about 1.0-2.7 m")[0]["s1"] == 2.7
    assert hr.missed_items([{"kind": "door", "at": 3.0, "floor": "ground"}])[0]["kind"] == "door"
    assert hr.missed_items("") == [] and hr.missed_items("large window right of door behind the tree") == []


def test_two_storey_balcony_on_a_flat_roof():
    role = {"length": 10.0, "roof_edge": "flat"}
    ops = [win(3, 1.0, sill=0.9, h=1.2), win(3, 1.0, sill=3.4, h=1.2), win(3, 7.0, sill=3.4, h=1.2)]
    bal = hr.invent_balcony("yes", 3, role, 5.07, 5.07, ops)
    assert bal is not None and 2.0 <= bal["floor_m"] <= 3.4 and bal["at_m"] <= 1.0 and bal["at_m"] + bal["width_m"] >= 8.2


def test_a_second_pass_changes_nothing():
    cases = [
        ([win(1, 2.26, w=0.96, sill=2.41, h=1.86), {"kind": "door", "edge": 1, "at_m": 2.3, "width_m": 1.0, "sill_m": 0.0, "height_m": 2.09},
          win(1, 4.53, w=0.96, sill=2.9, h=1.37)], {1: 7.74}, 4.32),
        ([{"kind": "garage_door", "edge": 0, "at_m": 0.62, "width_m": 4.59, "sill_m": 0.0, "height_m": 2.96}, win(0, 0.86, w=1.69, sill=4.92, h=0.96),
          {"kind": "door", "edge": 0, "at_m": 7.67, "width_m": 1.06, "sill_m": 0.0, "height_m": 2.1}, win(0, 8.88, w=1.69, sill=1.4, h=1.53),
          win(0, 10.45, w=0.64, sill=2.17, h=0.76)], {0: 12.75}, 5.85),
        ([{"kind": "window", "edge": 1, "at_m": 3.94, "width_m": 1.74, "sill_m": 0.0, "height_m": 3.68}], {1: 9.62}, 3.98),
    ]
    for ops, lengths, eave in cases:
        once, _ = hr.regularise(ops, lengths, eave)
        _, again = hr.regularise(once, lengths, eave)
        assert again == []


def test_a_window_over_the_eave_of_an_eave_wall_comes_down():
    out, _ = hr.regularise([win(4, 7.8, w=0.72, sill=4.57, h=0.88)], {4: 12.0}, 4.33, gables={0, 2})
    assert out[0]["sill_m"] + out[0]["height_m"] <= 4.33 - 0.25
    out, _ = hr.regularise([win(4, 7.8, w=0.72, sill=4.57, h=0.88)], {4: 12.0}, 4.33, gables={4})
    assert out[0]["sill_m"] == 4.57
