"""Road parcels fitted to the OSM road: constant width, no holes, no overlaps."""

from shapely.geometry import LineString, Polygon, box
from shapely.ops import unary_union

from drycastle_pipeline.parcel_fit import fit_roads

ROAD = LineString([(50, 0), (50, 100)])
# the road parcel is offset 3 m west of the road and 10 m wide (ragged on purpose: a notch on its east side)
ROAD_PARCEL = Polygon([(42, 0), (52, 0), (52, 40), (49, 50), (52, 60), (52, 100), (42, 100)])
WEST = box(0, 0, 42, 100)
EAST = unary_union([box(52, 0, 100, 100), Polygon([(52, 40), (49, 50), (52, 60)])])  # fills the notch, so no gaps
GEOMS = {"P-1": ROAD_PARCEL, "P-2": WEST, "P-3": EAST}
CFG = {"rules": [{"parcels": ["P-1"], "ways": ["w1"], "half_width_m": 3.0}]}


def test_road_parcel_becomes_a_constant_width_strip_on_the_road() -> None:
    out = fit_roads(GEOMS, {"w1": ROAD}, CFG)
    assert out["P-1"].symmetric_difference(box(47, 0, 53, 100)).area < 0.5


def test_ground_is_conserved_and_parcels_do_not_overlap() -> None:
    out = fit_roads(GEOMS, {"w1": ROAD}, CFG)
    assert abs(unary_union(list(out.values())).area - unary_union(list(GEOMS.values())).area) < 0.5
    ids = list(out)
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            assert out[a].intersection(out[b]).area < 0.05


def test_given_up_ground_goes_to_the_neighbour_and_taken_ground_comes_from_one() -> None:
    out = fit_roads(GEOMS, {"w1": ROAD}, CFG)
    assert out["P-2"].area > WEST.area + 4 * 100  # got the 5 m between its edge and the strip, less the notch's ground
    assert out["P-3"].area < EAST.area  # the strip reaches to x = 53, 1 m into it


def test_parcels_without_a_rule_are_untouched() -> None:
    out = fit_roads(GEOMS, {"w1": ROAD}, {"rules": []})
    assert out == GEOMS


def test_two_parcels_along_one_road_split_where_they_meet() -> None:
    a, b = box(46, 0, 54, 50), box(46, 50, 54, 100)
    out = fit_roads({"A": a, "B": b}, {"w1": ROAD}, {"rules": [{"parcels": ["A", "B"], "ways": ["w1"]}]})
    assert out["A"].intersection(out["B"]).area < 0.05
    assert abs(out["A"].area - 6 * 50) < 1 and abs(out["B"].area - 6 * 50) < 1
