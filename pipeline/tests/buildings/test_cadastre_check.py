"""Footprints against the cadastre's plots: what is found, and what the footprint becomes."""

from shapely.geometry import Polygon, box, mapping

from drycastle_pipeline.buildings.cadastre_check import Plots, judge


def _plot(pid: str, geom, kind: str = "building_plot") -> dict:
    return {"id": pid, "kind": kind, "polygon": mapping(geom)}


def test_a_house_plot_replaces_a_footprint_traced_beside_it_and_a_matching_one_stays():
    plots = Plots([_plot("P-1", box(0, 0, 10, 8))])
    finding, got = judge(box(1.5, 1, 11.5, 9), "P-1", plots)
    assert finding["finding"] == "replace" and got.equals(box(0, 0, 10, 8))
    finding, got = judge(box(0.1, 0, 10.1, 8), "P-1", plots)
    assert finding["finding"] == "match" and got.equals(box(0.1, 0, 10.1, 8))


def test_a_plot_of_another_size_is_not_taken_for_the_house():
    finding, got = judge(box(0, 0, 20, 10), "P-1", Plots([_plot("P-1", box(0, 0, 10, 8))]))
    assert finding["finding"] == "look" and got.area == 200


def test_in_a_yard_plot_a_footprint_is_slid_inside_or_cut_at_the_neighbours_plot():
    yard, next_door = box(0, 0, 30, 30), box(30, 0, 60, 30)
    plots = Plots([_plot("P-1", yard), _plot("P-2", next_door)])
    assert judge(box(5, 5, 15, 13), "P-1", plots)[0]["finding"] == "ok"
    finding, got = judge(box(-1.5, 5, 8.5, 13), "P-1", Plots([_plot("P-1", yard)]))
    assert finding["finding"] == "shift" and got.within(yard.buffer(0.3))
    finding, got = judge(box(18, 5, 36, 13), "P-1", plots)  # 6 m onto the neighbour: more than a slide
    assert finding["finding"] == "clip" and isinstance(got, Polygon) and got.intersection(next_door).area < 0.5 and got.area > 90
