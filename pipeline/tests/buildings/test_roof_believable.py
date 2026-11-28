"""Roofs and their trim that stay believable: the fixes for what the geometry lint (buildings/lint_geometry.py) found on
the photo-built houses (docs/house-anomalies.md)."""

import math

import numpy as np
import pytest
from shapely.geometry import Polygon, box
from shapely.ops import unary_union

from drycastle_pipeline.buildings import interior
from drycastle_pipeline.buildings import lint_geometry as lint
from drycastle_pipeline.buildings import openings as op
from drycastle_pipeline.buildings import roof_fit as rf
from drycastle_pipeline.buildings.openings import build_detail, parse_detail
from drycastle_pipeline.core.mesh import Mesh
from tests.buildings.test_building_detail import WINDOW, house


def _house(ring, **more) -> dict:
    ring = list(Polygon(ring).exterior.coords)[:-1]
    return {**house(), "footprint": {"exterior": [list(p) for p in ring], "holes": []}, **more}


def _detail(*openings, **more):
    return parse_detail({"id": "w1", "openings": list(openings), **more})


@pytest.fixture(autouse=True)
def no_plan(monkeypatch):
    monkeypatch.setattr(interior, "plan_of", lambda *a, **k: None)
    rf._FITS.clear()


# a row house: 38 m along the street, 6.5 m deep, with a small block behind (w378900198, w1226408460)
ROW = list(unary_union([box(0, 0, 38, 6.5), box(28, 6.5, 36, 10)]).exterior.coords)[:-1]
# an L of two blocks (w378900347): a long one along the street and one behind its west end, each with its own roof
L_TWO = list(unary_union([box(0, 0, 12.4, 6.8), box(0, 6.8, 6.3, 18.5)]).exterior.coords)[:-1]
# a hipped T (w378920393): a long block and a lower one across its south end, out past it to the east only
HIP_T = list(unary_union([box(0, 0, 6.5, 18.5), box(0, 0, 10, 6)]).exterior.coords)[:-1]
# a narrow house with a narrow block beside it that runs on past its gable end (w378920518)
SIDE = list(unary_union([box(4, 0, 8, 20), box(0, -4, 4, 18)]).exterior.coords)[:-1]


def test_a_ridge_read_across_a_long_house_is_turned_along_it():
    fit = rf.fit(Polygon(ROW), "gabled", math.pi / 2, 1.0, 3.0)  # an eave read on the 6.5 m end wall
    assert abs(math.sin(fit.angle)) < 0.1  # the ridge runs along the 38 m house
    assert fit.half < 4.0


def test_a_lower_part_wider_across_its_ridge_than_long_runs_its_ridge_along_itself():
    # a 12.4 m block along the street behind a narrow house whose gable faces the street: square to the house's ridge it
    # would carry a 12.4 m gable on a block 6.8 m deep
    fit = rf.fit(Polygon(L_TWO), "gabled", math.pi / 2, 3.0, 3.0, lead=[[6.3, 18.5], [0, 18.5]])
    for p in fit.parts[1:]:
        if p.form == "gabled":
            across = p.half * 2
            assert across <= 8.0


def test_the_part_that_roofs_most_of_what_is_bare_goes_next():
    fit = rf.fit(Polygon(L_TWO), "gabled", 0.0, 3.0, 3.0)
    assert len(fit.parts) == 2  # the two blocks; no third roof over the corner between them
    assert lint.roof_parts_through({**_house(L_TWO), "roof": "gabled", "ridge_dir": 0.0, "roof_fit": True, "roof_height_m": 3.0}, 0.0) == []


def test_a_wing_out_of_a_hipped_house_stops_at_its_middle():
    b = _house(HIP_T, roof="hipped", ridge_dir=math.pi / 2, roof_fit=True, roof_height_m=2.5, height_m=5.5)
    assert lint.roof_parts_through(b, 0.3) == []
    fit = rf.fit_building(b, 0.3)
    wing = next(p for p in fit.parts[1:] if p.core.intersects(box(7, 0.5, 9.5, 5.5)))
    assert wing.core.bounds[0] > 1.0  # it no longer reaches the house's far (west) wall


def test_a_narrow_part_that_runs_past_the_gable_gets_its_own_gable():
    fit = rf.fit(Polygon(SIDE), "gabled", math.pi / 2, 1.5, 3.0, lead=[[8, 20], [4, 20]])
    assert [p.form for p in fit.parts][:2] == ["gabled", "gabled"]
    side = fit.parts[1]
    assert abs(math.cos(side.ridge - fit.angle)) > 0.99  # two ridges side by side
    assert fit.roof_z(2.0, 19.0) > 3.0 + 1.0  # no single slope falling from the house's eave there


def test_a_board_at_a_step_takes_the_height_of_its_own_roof():
    z = lambda x, y: 3.0 if x < 1.0 else 6.0  # (a higher part begins at x = 1)
    assert op._run_ends((0.0, 0.0), (1.0, 0.0), z) == pytest.approx((3.0, 3.0))


def _tris_of(mesh: Mesh, colour) -> np.ndarray:
    return np.asarray(mesh.tris)[np.all(np.asarray(mesh.colours) == np.asarray(colour, np.uint8), axis=1)]


def test_dormer_cheeks_stay_under_the_dormer_roof():
    dormer = {"id": "dm", "edge": 0, "at_m": 5.0, "width_m": 1.4, "bottom_m": 3.4, "height_m": 1.1, "rise_m": 0.3, "colour": "#e6dcbe"}
    b = {**house(), "roof_height_m": 3.0, "height_m": 6.0}
    mesh, _ = build_detail(b, _detail(WINDOW, dormers=[dormer], overhang_m=0.3))
    m2, _ = lint.exposed_walls({**b, "wall_colour": [230, 220, 190]}, mesh)
    assert m2 < lint._MIN_EXPOSED_M2
    wood = _tris_of(mesh, [230, 220, 190])
    n, _ = lint._normals(wood)
    cheeks = wood[(np.abs(n[:, 0]) > 0.99) & (np.abs(wood[:, :, 0].mean(axis=1) - 5.0) < 1.0)]  # square to the wall, at the dormer
    assert len(cheeks)
    assert cheeks[:, :, 2].max() <= 3.4 + 1.1 + 1e-6


def test_steps_are_not_in_the_plane_of_the_aprons_edge():
    door = {"id": "d", "kind": "door", "edge": 0, "at_m": 4.0, "width_m": 1.0, "height_m": 2.1, "sill_m": 0.0, "steps": 1}
    b = house()
    mesh, _ = build_detail(b, _detail(door, apron={"width_m": 0.4}))
    assert lint.ground_faces(b, mesh) == []


def test_no_canopy_under_an_eave_at_the_doors_head():
    door = {"id": "d", "kind": "door", "edge": 0, "at_m": 4.0, "width_m": 1.0, "height_m": 2.1, "sill_m": 0.0, "canopy_m": 0.7}
    low = {**house(), "eave_height_m": 2.4, "height_m": 4.4}
    mesh, _ = build_detail(low, _detail(door, overhang_m=0.3))
    assert lint.through_roof(low, mesh)[0] == 0
    high_mesh, _ = build_detail(house(), _detail(door, overhang_m=0.3))
    assert high_mesh.face_count > mesh.face_count  # under a 3 m eave it has its canopy


def test_a_surround_ends_under_the_wall_top():
    gable_window = {"id": "g", "kind": "window", "edge": 1, "at_m": 2.0, "width_m": 0.8, "height_m": 0.8, "sill_m": 3.4, "surround_m": 0.15}
    b = {**house(), "roof_height_m": 2.0}
    mesh, _ = build_detail(b, _detail(WINDOW, gable_window, surround_colour="#123456", overhang_m=0.3))
    band = _tris_of(mesh, [0x12, 0x34, 0x56])
    assert len(band)
    for t in band:
        y = t[:, 1].mean()
        assert t[:, 2].max() <= 3.0 + 2.0 * (1 - abs(y - 3.0) / 3.0) + 1e-6  # the gable's slope over edge 1


def test_a_steep_roof_over_an_attic_is_believed_and_a_too_steep_one_is_not():
    attic = {**_house([(0, 0), (13, 0), (13, 11.6), (0, 11.6)]), "eave_height_m": 3.6, "roof_height_m": 5.6, "height_m": 9.2}  # 44 degrees (w378920362)
    mesh, _ = build_detail(attic, _detail(WINDOW))
    assert lint.roof_rise(attic, mesh) is None
    steep = {**house(), "eave_height_m": 3.0, "roof_height_m": 4.2, "height_m": 7.2}  # 54 degrees, 1.4 times the walls (w378900345)
    mesh, _ = build_detail(steep, _detail(WINDOW))
    assert lint.roof_rise(steep, mesh).startswith("roof not believable")


def test_one_roof_too_wide_for_a_house_is_not_believed():
    wide = {**_house([(0, 0), (8, 0), (8, 24), (0, 24)]), "ridge_dir": 0.0, "eave_height_m": 3.0, "roof_height_m": 6.9, "height_m": 9.9}  # a gable 24 m wide, 30 degrees
    mesh, _ = build_detail(wide, _detail(WINDOW))
    assert "across" in lint.roof_rise(wide, mesh)


def test_trim_under_a_higher_roofs_overhang_does_not_pass_through_it():
    b = house()
    mesh, _ = build_detail(b, _detail(WINDOW, overhang_m=0.3))
    # a higher roof's overhang 1 m above a gutter that runs on the lower roof: it passes through neither
    roof_c = np.asarray(b["roof_colour"], np.uint8)
    over = np.array([[[2, 1, 6.5], [8, 1, 6.5], [8, 5, 6.5]], [[2, 1, 6.5], [8, 5, 6.5], [2, 5, 6.5]]], float)
    gutter = np.array([[[3, 2.95, 4.5], [7, 2.95, 4.5], [7, 2.95, 4.2]], [[3, 2.95, 4.5], [7, 2.95, 4.2], [3, 2.95, 4.2]]], float)
    gutter[:, :, 2] -= 0.5  # just above the lower roof's slope near the ridge, under the overhang
    both = Mesh.concat([mesh, Mesh(over, np.tile(roof_c, (2, 1))), Mesh(gutter, np.tile(np.array([90, 90, 90], np.uint8), (2, 1)))])
    assert lint.through_roof(b, both)[0] == 0
