"""The roof fitted to the whole footprint (roof_fit)."""

import pytest
from shapely.geometry import Polygon

from drycastle_pipeline.buildings import roof_fit as rf
from drycastle_pipeline.buildings.openings import build_detail, parse_detail
from drycastle_pipeline.buildings.meshes import building_mesh

L_SHAPE = [(0, 0), (12, 0), (12, 6), (5, 6), (5, 11), (0, 11)]
INLINE = [(0, 0), (10, 0), (10, 1.5), (14, 1.5), (14, 5.5), (10, 5.5), (10, 7), (0, 7)]
BAY = [(0, 0), (4, 0), (4.4, -0.3), (5.4, -1.2), (7, -1.2), (8, -0.3), (8.4, 0), (12, 0), (12, 7), (0, 7)]


def _building(ring, roof, **extra):
    return {"id": "t", "footprint": {"exterior": [list(p) for p in ring], "holes": []}, "eave_height_m": 3.0, "roof_height_m": 2.0, "height_m": 5.0,
            "roof": roof, "ridge_dir": 0.0, "wall_colour": [230, 215, 180], "roof_colour": [150, 70, 50], "roof_fit": True, **extra}  # fmt: skip


@pytest.mark.parametrize("roof", ["hipped", "gabled"])
@pytest.mark.parametrize("ring", [L_SHAPE, INLINE, BAY])
def test_the_roof_covers_the_whole_footprint_and_its_overhang(ring, roof):
    poly = Polygon(ring)
    fit = rf.fit(poly, roof, 0.0, 2.0, 3.0, 0.4)
    assert fit.outline.buffer(0.01).covers(poly.buffer(0.1))  # every wall is under the roof
    assert fit.outline.area == pytest.approx(poly.buffer(0.4, join_style=2, mitre_limit=3.0).area, rel=0.03)
    assert sum(g.area for g, _ in fit.pieces) == pytest.approx(fit.outline.area, abs=0.05)  # no hole in it


def test_an_l_shaped_house_has_two_parts_that_meet_at_the_same_slope():
    fit = rf.fit(Polygon(L_SHAPE), "hipped", None, 2.0, 3.0)
    assert [(round(p.rect.w), round(p.rect.d)) for p in fit.parts] == [(12, 6), (11, 5)]
    assert fit.half == pytest.approx(3.0, abs=0.05)
    assert fit.top(6, 3) == pytest.approx(5.0, abs=0.05)  # the main ridge
    assert fit.top(2.5, 8) == pytest.approx(3.0 + 2.5 * 2 / 3, abs=0.05)  # the wing's ridge: lower, the same slope
    assert fit.top(0, 5.5) == pytest.approx(3.0, abs=0.03)  # a hipped roof's walls all end at the eave


def test_a_wing_out_of_an_eave_wall_of_a_gabled_house_ends_in_a_gable():
    fit = rf.fit(Polygon(L_SHAPE), "gabled", 0.0, 2.0, 3.0)
    assert [p.form for p in fit.parts] == ["gabled", "gabled"]
    assert fit.parts[1].ridge == pytest.approx(1.5708, abs=0.01)
    assert fit.top(2.5, 11) > 4.5  # the middle of the wing's end wall
    assert fit.top(0, 11) == pytest.approx(3.0, abs=0.03)
    assert fit.top(6, -0.0) == pytest.approx(3.0, abs=0.03)  # the house's eave


def test_the_roof_reaches_the_rim_of_a_house_whose_walls_are_not_square():
    ring = [(0, 0), (12, 0.5), (11.6, 7.2), (0.3, 6.6)]  # no two walls parallel
    fit = rf.fit(Polygon(ring), "hipped", None, 2.0, 3.0, 0.5)
    assert len(fit.parts) == 1
    rim = [fit.roof_z(x, y) for x, y in fit.outline.exterior.coords]
    assert max(rim) - min(rim) < 0.05  # a level eave along every wall
    assert max(rim) < 3.0  # below the wall's top by the overhang's fall, all round


def test_a_wing_in_line_carries_the_roof_on_lower():
    fit = rf.fit(Polygon(INLINE), "gabled", 0.0, 2.0, 3.0)
    assert fit.top(14, 3.5) == pytest.approx(3.0 + 2.0 / 3.5 * 2.0, abs=0.05)  # the wing's gable, the house's slope
    assert fit.top(12, 1.5) == pytest.approx(3.0, abs=0.03)


def test_a_bay_gets_a_small_hipped_roof_of_its_own():
    fit = rf.fit(Polygon(BAY), "hipped", None, 2.0, 3.0)
    assert len(fit.parts) == 2 and fit.parts[1].form == "hipped"
    assert fit.roof_z(6.2, 0.0) > 3.3  # over the wall the bay stands on, its roof is above the main eave
    assert fit.top(6.2, -1.2) == pytest.approx(3.0, abs=0.03)


@pytest.mark.parametrize("roof", ["hipped", "gabled"])
def test_the_generator_builds_a_fitted_roof(roof):
    b = _building(L_SHAPE, roof)
    d = parse_detail({"id": "t", "overhang_m": 0.4, "eaves": {"colour": "#444444", "fascia_m": 0.18, "gutter": True}, "ridge_caps": True, "openings": []}, "t")
    mesh, _ = build_detail(b, d)
    assert mesh.tris[:, :, 2].max() == pytest.approx(5.0, abs=0.15)
    plain = building_mesh(b)
    assert plain.tris[:, :, 2].max() == pytest.approx(5.0, abs=0.01)
    assert len(plain.tris) > len(building_mesh({**b, "roof_fit": False}).tris)


GARAGE = [(0, 0), (8, 0), (8, 10), (0, 10), (-3, 10), (-3, 1.5), (0, 1.5)]  # a house 8 m wide, a garage 3 m wide beside it, set back


def test_the_main_roof_stands_on_the_gable_wall_that_was_seen():
    fit = rf.fit(Polygon(GARAGE), "gabled", None, 4.0, 3.5, lead=[[0, 0], [8, 0]])
    assert fit.half == pytest.approx(4.0, abs=0.1)  # half the gable wall, not half the whole frontage
    assert fit.top(4, 0) == pytest.approx(7.5, abs=0.1)  # the gable's peak over the wall's middle
    assert fit.top(0, 5) == pytest.approx(3.5, abs=0.05)


def test_a_narrow_part_beside_an_eave_wall_gets_a_single_slope_down_from_the_eave():
    fit = rf.fit(Polygon(GARAGE), "gabled", None, 4.0, 3.5, lead=[[0, 0], [8, 0]])
    assert [p.form for p in fit.parts] == ["gabled", "lean_to"]
    assert fit.top(-3, 5) == pytest.approx(2.4, abs=0.05)  # its low wall
    assert 2.4 < fit.top(-1.5, 1.5) < 3.5  # its front wall slopes
    assert fit.roof_z(-1.5, 5) == pytest.approx((2.4 + 3.5) / 2, abs=0.05)
