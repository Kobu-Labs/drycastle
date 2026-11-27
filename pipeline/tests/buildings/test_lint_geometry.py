"""The geometry lint of a built house (buildings/lint_geometry.py) and the fixes for what it finds."""

import math

import numpy as np
import pytest
from shapely.affinity import rotate
from shapely.geometry import Polygon, box
from shapely.ops import unary_union

from drycastle_pipeline.buildings import interior
from drycastle_pipeline.buildings import lint_geometry as lint
from drycastle_pipeline.buildings import roof_fit as rf
from drycastle_pipeline.buildings.openings import THRESHOLD_M, build_detail, parse_detail
from drycastle_pipeline.core.mesh import Mesh
from tests.buildings.test_building_detail import WINDOW, house


def _house(ring, **more) -> dict:
    ring = list(Polygon(ring).exterior.coords)[:-1]
    return {**house(), "footprint": {"exterior": [list(p) for p in ring], "holes": []}, **more}


def _detail(*openings, **more):
    return parse_detail({"id": "w1", "openings": list(openings), **more})


@pytest.fixture
def no_plan(monkeypatch):
    monkeypatch.setattr(interior, "plan_of", lambda *a, **k: None)


# a T: a house turned 40 degrees, a long wing running into it square to the street and out past its ridge
_MAIN = rotate(box(-4.0, -3.0, 4.0, 3.0), 40.0, origin=(0, 0))
T_RING = list(unary_union([_MAIN, box(-20.0, -2.0, 3.0, 2.0)]).simplify(0.01).exterior.coords)[:-1]
# a house with a narrow part beside its eave wall: the part gets a single slope that ends 2.4 m up
LEAN_RING = [(0, 0), (10, 0), (10, 6), (0, 6)]
LEAN_RING = [(0, 0), (2, 0), (2, -2.5), (7, -2.5), (7, 0), (10, 0), (10, 6), (0, 6)]


def test_a_plain_house_has_no_fault():
    b = house()
    mesh, tags = build_detail(b, _detail(WINDOW, overhang_m=0.3))
    assert lint.check(b, mesh, tags) == []


def test_a_wall_standing_above_the_roof_is_found():
    b = house()
    mesh, tags = build_detail(b, _detail(WINDOW))
    fin = np.array([[[3.0, 3.0, 3.0], [7.0, 3.0, 3.0], [5.0, 3.0, 6.5]], [[3.0, 3.0, 3.0], [5.0, 3.0, 6.5], [7.0, 3.0, 3.0]]])  # a gable across the roof's ridge, 1.5 m above it
    bad = Mesh.concat([mesh, Mesh(fin, np.tile(np.array(b["wall_colour"], np.uint8), (2, 1)))])
    found = lint.check(b, bad, tags)
    assert any(line.startswith("wall above the roof") for line in found)


def test_an_unbelievable_roof_is_found():
    b = {**house(), "roof_height_m": 7.0, "height_m": 10.0}  # 7 m of roof over 3 m walls, on a house 6 m across
    mesh, tags = build_detail(b, _detail(WINDOW))
    assert any(line.startswith("roof not believable") for line in lint.check(b, mesh, tags))
    assert lint.roof_rise(house(), build_detail(house(), _detail(WINDOW))[0]) is None


def test_a_gable_wall_far_wider_than_the_house_is_deep_is_not_believed():
    assert rf.gable_believable(9.0, 12.0) and rf.gable_believable(14.0, 11.0)
    assert not rf.gable_believable(22.6, 6.8)  # w378900292: a gable read on the long side


def test_a_doorway_on_the_ground_has_a_threshold_off_the_terrain(no_plan):
    door = {"id": "g", "kind": "garage_door", "edge": 0, "at_m": 2.0, "width_m": 3.0, "height_m": 2.1, "sill_m": 0.0}
    b = house()
    for inside in (False, True):
        mesh, tags = build_detail(b, _detail(door, interior=inside, plinth={"height_m": 0.3}, apron={"width_m": 0.8}))
        assert lint.ground_faces(b, mesh) == []
        n, area = lint._normals(mesh.tris)
        up = (n[:, 2] > 0.999) & (area > 1e-4) & (np.abs(mesh.tris[:, :, 2] - THRESHOLD_M).max(axis=1) < 1e-6)
        assert up.any()  # the doorway's floor, a little above the ground


def test_a_face_on_the_ground_is_found():
    b = house()
    mesh, tags = build_detail(b, _detail(WINDOW))
    on = Mesh(np.array([[[1.0, -1.0, 0.0], [3.0, -1.0, 0.0], [3.0, -0.2, 0.0]]]), np.array([[200, 200, 200]], np.uint8))
    assert lint.ground_faces(b, Mesh.concat([mesh, on]))


def test_a_wing_running_into_a_house_at_an_angle_stops_at_its_ridge():
    poly = Polygon(T_RING)
    fit = rf.fit(poly, "gabled", math.radians(40.0), 1.4, 3.5, 0.4)
    assert len(fit.parts) >= 2
    b = _house(T_RING, roof="gabled", ridge_dir=math.radians(40.0), roof_height_m=1.4, eave_height_m=3.5, height_m=4.9, roof_fit=True)
    assert lint.roof_parts_through(b, 0.4) == []
    mesh, tags = build_detail(b, _detail(overhang_m=0.4))
    assert lint.check(b, mesh, tags) == []


def test_without_the_stop_the_wing_comes_up_through_the_far_slope(monkeypatch):
    monkeypatch.setattr(rf, "_stop_at_ridges", lambda parts: None)
    rf._FITS.clear()
    b = _house(T_RING, roof="gabled", ridge_dir=math.radians(40.0), roof_height_m=1.4, eave_height_m=3.5, height_m=4.9, roof_fit=True)
    try:
        assert any("comes up through" in line for line in lint.roof_parts_through(b, 0.4))
    finally:
        rf._FITS.clear()


def test_the_inside_stays_under_a_low_single_slope(no_plan):
    b = _house(LEAN_RING, roof="gabled", ridge_dir=0.0, roof_fit=True)
    fit = rf.fit(Polygon(LEAN_RING), "gabled", 0.0, 2.0, 3.0)
    assert [p.form for p in fit.parts] == ["gabled", "lean_to"]
    door = {"id": "d", "kind": "door", "edge": 2, "at_m": 1.5, "width_m": 0.95, "height_m": 2.0, "sill_m": 0.1}
    mesh, tags = build_detail(b, _detail(door, interior=True))
    inside = np.all(np.isin(mesh.colours, [interior.INNER_WALL, interior.CEILING]).all(axis=1, keepdims=True), axis=1)
    under = mesh.tris[inside]
    pts = under.reshape(-1, 3)
    lean = pts[pts[:, 1] < -0.1]
    assert len(lean) and all(z <= fit.roof_z(x, y) - 0.05 for x, y, z in lean)  # nothing of the inside above the single slope
    assert lint.through_roof(b, mesh)[0] == 0
