"""Hand-edited parcel outlines: the splice and the application (trimming, freed land, building guard)."""

import pytest
from shapely.geometry import Polygon, box

from drycastle_pipeline.parcels.outlines import apply_outlines, splice_outline

SQUARE = [(0.0, 0.0), (0.0, 100.0), (100.0, 100.0), (100.0, 0.0)]


def test_splice_cuts_a_corner_and_keeps_the_larger_part() -> None:
    ring = splice_outline(SQUARE, [(60.0, 100.0), (80.0, 70.0), (100.0, 40.0)])  # ends on the boundary, either way round
    assert Polygon(ring).area == pytest.approx(10000 - 0.5 * 40 * 60)  # the corner triangle is gone
    assert Polygon(ring).is_valid and (80.0, 70.0) in ring
    back = splice_outline(SQUARE, [(100.0, 40.0), (80.0, 70.0), (60.0, 100.0)])  # drawn the other way
    assert Polygon(back).area == pytest.approx(Polygon(ring).area)
    # ends a metre off the boundary are moved onto it
    near = splice_outline(SQUARE, [(60.0, 101.0), (80.0, 70.0), (101.0, 40.0)])
    assert Polygon(near).area == pytest.approx(Polygon(ring).area)
    with pytest.raises(ValueError):
        splice_outline(SQUARE, [(50.0, 100.0), (50.0, 100.0)])  # both ends at the same place


def test_apply_replaces_trims_the_neighbour_and_frees_the_rest() -> None:
    geoms = {"P-1": box(0, 0, 100, 100), "P-2": box(100, 0, 200, 100), "P-3": box(300, 0, 400, 100)}
    new = box(0, 0, 130, 50)  # takes 30 x 50 off P-2 and gives up the northern half of P-1
    out, freed, warnings = apply_outlines(geoms, {"P-1": new}, {"P-1": {}})
    assert not warnings
    assert out["P-1"].equals(new) and out["P-3"] is geoms["P-3"]
    assert out["P-2"].area == pytest.approx(10000 - 30 * 50) and not out["P-2"].intersects(new.buffer(-0.01))
    assert freed.area == pytest.approx(100 * 50) and freed.equals(box(0, 50, 100, 100))  # nothing else took it
    # the freed land is not freed where another outline takes it
    _, freed2, _ = apply_outlines(geoms, {"P-1": new, "P-3": box(0, 50, 100, 100)}, None)
    assert freed2.equals(box(300, 0, 400, 100))  # only P-3's own old land is left over


def test_apply_skips_an_outline_that_leaves_a_building_or_an_unknown_parcel() -> None:
    geoms = {"P-1": box(0, 0, 100, 100)}
    out, freed, warnings = apply_outlines(geoms, {"P-1": box(0, 0, 50, 100), "P-9": box(0, 0, 1, 1)}, {"P-1": {"w1": box(60, 10, 70, 20)}})
    assert out == geoms and freed.is_empty
    assert any("w1" in w for w in warnings) and any("P-9" in w for w in warnings)
