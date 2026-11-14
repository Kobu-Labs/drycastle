"""The hollow municipal office (office_interior): the game's contract and the meshes."""

import json
from pathlib import Path

import pytest
from shapely.geometry import Point, Polygon

from drycastle_pipeline import office, office_interior as oi

BUILDINGS = Path(__file__).parents[2] / "data" / "village" / "work" / "buildings.json"


def test_urad_info_puts_everyone_in_their_room():
    info = oi.urad_info({oi.SW_BAR: 100.0, oi.MIDDLE_BAR: 100.0})
    rooms = {r["id"]: Polygon(r["footprint"]) for r in info["volume"]}
    assert set(rooms) == {"entry", "office", "mayor"} and all(r.is_valid and r.area > 15 for r in rooms.values())
    where = {w["role"]: next(k for k, r in rooms.items() if r.contains(Point(w["pos"][:2]))) for w in info["workers"]}
    assert where == {"mayor": "mayor", "announcer": "office", "clerk": "office"}
    assert [w["role"] for w in info["workers"]].count("clerk") == 2
    assert all(w["pos"][2] == pytest.approx(100.0 + oi.F + oi.SEAT_H) for w in info["workers"])
    assert rooms["office"].contains(Point(info["table"]["pos"][:2])) and rooms["mayor"].contains(Point(info["cctv"]["pos"][:2]))
    door = info["doorway"]
    assert rooms["entry"].distance(Point(door["pos"][:2])) < 0.2  # in the entry's front wall
    assert door["yaw"] == pytest.approx(117.9, abs=0.5) and door["pos"][2] == pytest.approx(100.0 + oi.F)


def test_solid_part_keeps_the_openings_on_the_set_back_wall():
    b = {"id": oi.MIDDLE_BAR, "footprint": {"exterior": [list(p) for p in (oi.V0, oi.V1, oi.V2, oi.V3, oi.V4, oi.V5)], "holes": []}}
    d = office._details().get(oi.MIDDLE_BAR)
    b2, d2 = oi.solid_part(b, d)
    ring = Polygon(b2["footprint"]["exterior"])
    assert ring.is_valid and ring.exterior.is_ccw
    assert len(d2.openings) == len([o for o in d.openings if o.edge == 5]) and all(o.edge == 0 and o.at_m >= 0 for o in d2.openings)
    with pytest.raises(ValueError):
        oi.solid_part({**b, "footprint": {"exterior": [[0, 0], [1, 0], [1, 1]], "holes": []}}, d)


@pytest.mark.skipif(not BUILDINGS.exists(), reason="needs the village work data")
def test_meshes_build_for_visual_and_collision():
    by_id = {b["id"]: b for b in json.loads(BUILDINGS.read_text())["buildings"]}
    for bid in (oi.SW_BAR, oi.MIDDLE_BAR):
        assert office.office_mesh(by_id[bid], True).face_count > office.office_mesh(by_id[bid], False).face_count > 100
    assert len(oi.office_panes(by_id[oi.SW_BAR], office._details().get(oi.SW_BAR))) == 4  # two measured on the road wall, two in the mayor's room
