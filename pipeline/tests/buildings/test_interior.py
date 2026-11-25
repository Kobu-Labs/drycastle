import numpy as np
import pytest

from drycastle_pipeline.buildings import interior
from drycastle_pipeline.buildings.openings import DetailError, build_detail, parse_detail
from tests.buildings.test_building_detail import WINDOW, house

DOOR = {"id": "d", "kind": "door", "edge": 0, "at_m": 6.0, "width_m": 0.95, "height_m": 2.05, "sill_m": 0.6, "steps": 4}
HIGH = {**WINDOW, "id": "h", "edge": 1, "at_m": 2.5, "sill_m": 3.2, "height_m": 0.8}  # in the gable, above the ceiling


def detail(*openings, interior=True, **more):
    return parse_detail({"id": "w1", "openings": list(openings), "interior": interior, **more})


@pytest.fixture
def no_plan(monkeypatch):
    """The shell alone: no partitions in the way of the rays."""
    monkeypatch.setattr(interior, "plan_of", lambda *a, **k: None)


def _hit(mesh, origin, direction) -> float:
    """Distance along a ray to the nearest triangle of the mesh (inf: none)."""
    o, d = np.asarray(origin, float), np.asarray(direction, float)
    best = np.inf
    for a, b, c in mesh.tris:
        e1, e2 = b - a, c - a
        h = np.cross(d, e2)
        det = e1 @ h
        if abs(det) < 1e-12:
            continue
        s = o - a
        u = (s @ h) / det
        q = np.cross(s, e1)
        v = (d @ q) / det
        t = (e2 @ q) / det
        if u >= 0 and v >= 0 and u + v <= 1 and t > 1e-9:
            best = min(best, t)
    return best


def test_room_is_between_the_door_sill_and_the_eave():
    ins = interior.inside_of(house(), detail(WINDOW, DOOR))
    assert (ins.floor, ins.ceiling) == (0.6, 3.0)
    assert interior.inside_of(house(), detail(WINDOW, interior=False)) is None
    assert interior.inside_of({**house(), "eave_height_m": 6.0, "levels": 2}, detail(DOOR)).ceiling == pytest.approx(3.3)
    assert interior.inside_of({**house(), "eave_height_m": 4.6}, detail(DOOR)).ceiling == pytest.approx(3.6)  # one level, a high wall


def test_what_goes_through():
    ins = interior.inside_of(house(), detail(WINDOW, DOOR, HIGH))
    by = {o.id: ins.through(o) for o in detail(WINDOW, DOOR, HIGH, {**DOOR, "id": "g", "kind": "garage_door", "at_m": 8.0, "steps": 0}).openings}
    assert by == {"a": True, "d": True, "h": False, "g": True}


def test_leaves_are_the_doors_the_game_hangs():
    d = detail(WINDOW, DOOR, {**DOOR, "id": "g", "kind": "garage_door", "at_m": 8.0, "steps": 0, "sill_m": 0.0, "width_m": 1.5, "height_m": 2.4})
    ins = interior.inside_of(house(), d)
    by = {w["id"]: w for w in interior.leaves(house(), d, ins)}
    assert {i: w["kind"] for i, w in by.items()} == {"d": "hinged", "g": "garage"}
    assert by["d"]["pos"] == [6.475, interior.WALL_M / 2, 0.6] and by["d"]["yaw"] == 180.0  # in the middle of the south wall, out is south
    assert by["g"]["pos"][2] == 0.0 and by["g"]["width"] == 1.5


def test_a_garage_door_below_the_floor_has_the_floors_edge_in_it(no_plan):
    garage = {**DOOR, "id": "g", "kind": "garage_door", "at_m": 8.0, "steps": 0, "sill_m": 0.0, "width_m": 1.5, "height_m": 2.4}
    col = interior.collision(house(), detail(DOOR, garage))
    assert _hit(col, (8.7, -2.0, 0.3), (0, 1, 0)) == pytest.approx(2.0 + interior.WALL_M)  # the edge under the floor
    assert _hit(col, (8.7, -2.0, 1.5), (0, 1, 0)) > 7.0  # through the doorway


def test_collision_lets_a_body_in_at_the_door_and_stops_it_at_the_wall(no_plan):
    col = interior.collision(house(), detail(WINDOW, DOOR))
    door_mid, inside_y = 6.0 + 0.475, 3.0
    assert _hit(col, (door_mid, -2.0, 1.5), (0, 1, 0)) == pytest.approx(2.0 + 6.0 - interior.WALL_M)  # through the doorway, to the far wall's inner face
    assert _hit(col, (4.5, -2.0, 1.5), (0, 1, 0)) == pytest.approx(2.0)  # the wall beside it
    assert _hit(col, (4.5, inside_y, 1.5), (0, -1, 0)) == pytest.approx(inside_y - interior.WALL_M)  # its inner face, from the room
    assert _hit(col, (2.5, -2.0, 1.5), (0, 1, 0)) > 7.0  # through the window
    assert _hit(col, (5.0, inside_y, 2.0), (0, 0, -1)) == pytest.approx(2.0 - 0.6)  # the floor
    assert _hit(col, (5.0, inside_y, 2.0), (0, 0, 1)) == pytest.approx(1.0)  # the ceiling
    assert _hit(col, (5.0, inside_y, 9.0), (0, 0, -1)) == pytest.approx(9.0 - 4.0)  # the roof, where the plain massing ends
    assert _hit(col, (door_mid, -0.62, 3.0), (0, 0, -1)) == pytest.approx(3.0 - 0.3, abs=0.02)  # the ramp, half way up the steps



def test_visual_has_inner_faces_floor_and_two_sided_panes(no_plan):
    plain, tags0 = build_detail(house(), detail(WINDOW, DOOR, interior=False))
    mesh, tags = build_detail(house(), detail(WINDOW, DOOR))
    assert mesh.face_count > plain.face_count
    assert tags0[0].mesh.face_count == 2 and tags[0].mesh.face_count == 4
    assert _hit(mesh, (5.0, 3.0, 2.0), (0, -1, 0)) == pytest.approx(3.0 - interior.WALL_M)
    assert _hit(mesh, (6.475, 3.0, 1.5), (0, -1, 0)) == np.inf  # out through the open doorway
    zs = mesh.tris.reshape(-1, 3)[:, 2]
    assert np.isclose(zs, 0.6).any()


def test_room_ring_and_doorways():
    ins = interior.inside_of(house(), detail(DOOR))
    ring = interior.room_ring(house())
    assert min(x for x, _ in ring) == pytest.approx(0.15) and max(y for _, y in ring) == pytest.approx(5.85)
    (dw,) = interior.doorways(house(), detail(DOOR), ins)
    assert dw["at"] == [6.475, 0.0, 0.6] and dw["normal"] == [0.0, -1.0]


def test_refusals():
    with pytest.raises(DetailError, match="interior"):
        parse_detail({"id": "w1", "interior": "yes"})
    with pytest.raises(DetailError, match="between the floor"):
        interior.inside_of({**house(), "eave_height_m": 2.2}, detail(DOOR))
