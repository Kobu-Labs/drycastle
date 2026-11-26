"""The hidden voxel grid of a hollow house (buildings/voxels.py, docs/house-destruction.md Contract 1)."""

import numpy as np
import pytest

from drycastle_pipeline.buildings import interior
from drycastle_pipeline.buildings import voxels as vx
from drycastle_pipeline.buildings.floorplan import Plan, Wall
from drycastle_pipeline.buildings.openings import build_detail, parse_detail
from tests.buildings.test_building_detail import WINDOW, house

DOOR = {"id": "d", "kind": "door", "edge": 0, "at_m": 6.0, "width_m": 0.95, "height_m": 2.05, "sill_m": 0.6, "steps": 4}
HIGH = {**WINDOW, "id": "h", "edge": 1, "at_m": 2.5, "sill_m": 3.2, "height_m": 0.8}  # in the gable, above the ceiling
C = vx.CELL_M


@pytest.fixture(autouse=True)
def no_plan(monkeypatch):
    monkeypatch.setattr(interior, "plan_of", lambda *a, **k: None)


def grid(plan=False, **more):
    b = house()  # 10 x 6 m, eave 3, ridge 5, along x
    d = parse_detail({"id": "w1", "openings": [WINDOW, DOOR, HIGH], "interior": True, "overhang_m": 0.4, **more})
    ins = interior.inside_of(b, d)  # floor 0.6, ceiling 3.0
    return vx.voxelise("w1", b, d, ins, build_detail(b, d)[0], dz=10.0, plan=plan), ins


def at(g, x, y, z) -> int:
    """The material of the cell at map point (x, y, z): the box house has yaw 0."""
    i, j, k = (int(np.floor((v - o) / C)) for v, o in zip((x, y, z), g.origin, strict=True))
    return int(g.materials()[k, j, i])


def test_a_box_house_has_its_shell():
    g, ins = grid()
    assert g.yaw == 0.0 and g.cell == C and g.palette[0] == (vx.AIR, 0, 0, 0)
    assert g.origin[2] + 0 <= 10.0 - C  # the visual's foot and a cell of margin are inside
    z = 10.0
    assert at(g, 4.0, 0.1, z + 1.5) == vx.WALL  # the south wall, just behind the facade
    assert at(g, 4.0, -0.1, z + 1.5) == vx.AIR  # in front of it
    assert at(g, 4.0, 0.4, z + 1.5) == vx.AIR  # the room
    assert at(g, 4.0, 3.0, z + ins.floor - 0.1) == vx.FLOOR
    assert at(g, 4.0, 3.0, z + ins.floor - 0.1 - C) == vx.FOUNDATION
    assert at(g, 4.0, 3.0, z + ins.floor - 0.1 - 2 * C) == vx.AIR  # one layer only
    assert at(g, 4.0, 3.0, z + ins.ceiling + 0.2) == vx.CEILING
    assert at(g, 4.0, 3.0, z + 4.0) == vx.AIR  # the attic
    assert at(g, 4.0, 3.0, z + 4.7) == vx.ROOF  # the skin under the ridge (5.0)
    assert at(g, 4.0, 3.0, z + 5.05) == vx.AIR
    assert at(g, 4.0, -0.3, z + 3.0 - 0.2) == vx.ROOF  # the eave over the overhang
    assert at(g, 0.1, 3.0, z + 4.5) == vx.WALL  # the gable wall reaches up under the roof
    mats = g.materials()
    assert {vx.FOUNDATION, vx.FLOOR, vx.WALL, vx.CEILING, vx.ROOF} <= set(np.unique(mats).tolist())


def test_openings_that_go_through_are_air_and_the_others_wall():
    g, _ = grid()
    z = 10.0
    for depth in (0.1, 0.2):  # the whole wall
        assert at(g, 2.5, depth, z + 1.5) == vx.AIR  # the window (2.0..3.0, sill 0.9)
        assert at(g, 6.5, depth, z + 1.5) == vx.AIR  # the door (6.0..6.95, sill 0.6)
    assert at(g, 6.5, 0.1, z + 0.5) == vx.WALL  # under the door's sill
    assert at(g, 2.5, 0.1, z + 2.4) == vx.WALL  # over the window
    assert at(g, 9.9, 2.9, z + 3.6) == vx.WALL  # the gable window over the ceiling stays closed


def test_partitions_with_a_doorway():
    plan = Plan(0.0, "closed", [], [Wall((5.0, 0.3), (5.0, 5.7), [(2.0, 2.8, "door")])], [])
    g, ins = grid(plan=plan)
    z = 10.0
    assert at(g, 5.0, 1.0, z + 1.5) == vx.PARTITION  # 0.1 m thick: still a cell
    assert at(g, 5.0, 2.7, z + 1.5) == vx.AIR  # the doorway
    assert at(g, 5.0, 2.7, z + ins.floor + 2.1) == vx.PARTITION  # over it


def test_file_round_trip_and_layout():
    g, _ = grid()
    other = vx.HouseGrid("a0", (1.0, 2.0, 3.0), 0.5, C, [(0, 0, 0, 0), (vx.WALL, 1, 2, 3)], np.array([[[0, 1, 1]]], np.uint8))
    data = vx.encode([g, other])
    assert data[:4] == b"HVOX" and data[4:8] == bytes([1, 0, 2, 0])
    assert data[8:12] == bytes([2, 0]) + b"a0"  # sorted by id
    back = vx.decode(data)
    assert [h.id for h in back] == ["a0", "w1"]
    assert back[1].origin == g.origin and back[1].yaw == g.yaw and back[1].palette == g.palette
    assert np.array_equal(back[1].cells, g.cells) and np.array_equal(back[0].cells, other.cells)
    assert vx.encode(back) == data
    long_run = vx.HouseGrid("b", (0.0, 0.0, 0.0), 0.0, C, [(0, 0, 0, 0)], np.zeros((1, 1, 300), np.uint8))
    assert vx.encode([long_run]).endswith(bytes([3, 0, 0, 0, 0, 0xAC, 0x02]))  # one run of 300: varint ac 02


def test_same_input_same_bytes():
    assert vx.encode([grid()[0]]) == vx.encode([grid()[0]])
