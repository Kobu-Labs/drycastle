import numpy as np
import pytest
import shapely
from shapely.geometry import Point

from drycastle_pipeline.roads import (
    build_graph,
    is_unpaved,
    resample,
    ribbon_footprint,
    ribbon_mesh,
    ribbon_sides,
    road_colour,
    road_mesh,
    road_width,
)
from drycastle_pipeline.rules import load_section
from tests.synthetic import line_feature, make_terrain

CFG = load_section("roads")


def test_width_rules() -> None:
    assert road_width({}, "residential", CFG) == 5.0
    assert road_width({"width": "7.5"}, "residential", CFG) == 7.5
    assert road_width({"width": "6,5 m"}, "residential", CFG) == 6.5
    assert road_width({"lanes": "4"}, "residential", CFG) == 12.0
    assert road_width({"lanes": "x"}, "tertiary", CFG) == 6.0
    assert road_width({}, "steps", CFG) is None  # not a built class


def test_surface_and_colour() -> None:
    assert is_unpaved("track", None, CFG) and not is_unpaved("residential", None, CFG)
    assert is_unpaved("residential", "gravel", CFG) and not is_unpaved("track", "asphalt", CFG)
    assert road_colour("residential", "asphalt", CFG) != road_colour("track", None, CFG)


FEATURES = [
    line_feature("w1", "residential", [[0, 0], [50, 0], [100, 0]], surface="asphalt"),
    line_feature("w2", "track", [[50, 0], [50, 60]]),  # T junction at a shared vertex
    line_feature("w3", "residential", [[100, 40], [100, 0]], oneway="-1"),
    line_feature("w4", "steps", [[0, 10], [5, 10]]),  # skipped
]


def test_graph_schema_and_topology() -> None:
    g = build_graph(FEATURES, CFG)
    assert set(g) == {"version", "axes", "nodes", "edges"}
    for n in g["nodes"]:
        assert set(n) == {"id", "x", "y"}
    ids = {n["id"] for n in g["nodes"]}
    for e in g["edges"]:
        assert set(e) == {
            "id", "osm_id", "from", "to", "polyline", "highway", "width", "oneway", "surface",
            "length_m",
        }
        assert e["from"] in ids and e["to"] in ids and len(e["polyline"]) >= 2
        assert all(len(p) == 2 for p in e["polyline"])
    # w1 is split at the T junction; w2 and w3 are single edges; steps are skipped
    assert [e["osm_id"] for e in g["edges"]] == ["w1", "w1", "w2", "w3"]
    assert len(g["nodes"]) == 5  # (0,0), (50,0), (100,0), (50,60), (100,40)
    w1a, w1b, w2, w3 = g["edges"]
    assert w1a["to"] == w1b["from"] == w2["from"]
    assert w1a["surface"] == "asphalt" and w2["surface"] is None
    assert w2["highway"] == "track" and w2["width"] == 3.0
    assert w3["oneway"] is True and w3["polyline"][0] == [100, 0]  # oneway=-1 reversed
    assert w1a["length_m"] == 50.0
    assert build_graph(FEATURES, CFG) == g  # deterministic


def test_resample_and_ribbon_width() -> None:
    pts = resample(np.array([[0.0, 0.0], [10.0, 0.0]]), 2.0)
    assert len(pts) == 6
    left, right = ribbon_sides(np.array([[0.0, 0.0], [10.0, 0.0]]), 6.0)
    assert left[:, 1].tolist() == [3.0, 3.0] and right[:, 1].tolist() == [-3.0, -3.0]
    # a right angle bend: the mitred side stays within the miter limit
    bend = np.array([[0.0, 0.0], [10.0, 0.0], [10.0, 10.0]])
    left, right = ribbon_sides(bend, 4.0)
    assert np.linalg.norm(left[1] - bend[1]) <= 2.0 / 0.5 + 1e-9


def test_ribbon_is_valid_and_draped() -> None:
    t = make_terrain()
    pts = np.array([[30.0, 50.0], [90.0, 70.0], [150.0, 70.0]])
    lift = 0.05
    m = ribbon_mesh(pts, 5.0, t, lift, [70, 70, 70], 2.0)
    assert m.face_count > 0 and m.tris.shape[1:] == (3, 3)
    n = np.cross(m.tris[:, 1] - m.tris[:, 0], m.tris[:, 2] - m.tris[:, 0])
    area = np.linalg.norm(n, axis=1) / 2
    assert (area > 1e-6).all()  # no degenerate triangles
    assert (n[:, 2] > 0).all()  # facing up
    flat = m.tris.reshape(-1, 3)
    ground = t.surface(flat[:, 0], flat[:, 1])
    # every vertex floats exactly `lift` above the terrain surface
    assert np.abs(flat[:, 2] - ground - lift).max() < 1e-6
    # the strip is as wide as asked (perpendicular to the first, straight segment)
    d = np.array([60.0, 20.0]) / np.hypot(60.0, 20.0)
    start = flat[np.hypot(flat[:, 0] - 30.0, flat[:, 1] - 50.0) < 2.6]
    across = (start[:, :2] - [30.0, 50.0]) @ np.array([-d[1], d[0]])
    assert across.max() - across.min() == pytest.approx(5.0, abs=1e-6)


def test_road_mesh_is_clipped_to_terrain_coverage() -> None:
    t = make_terrain()
    g = build_graph(
        [line_feature("w1", "residential", [[-100, 50], [120, 50]])], CFG
    )  # starts far outside the coverage box (x >= 10)
    m = road_mesh(g, t, CFG)
    assert m.face_count > 0
    assert m.tris[..., 0].min() > 9.0  # everything left of the coverage is dropped
    # higher class lies a hair above the lower one at overlaps
    g2 = build_graph(
        [
            line_feature("a", "track", [[20, 50], [100, 50]]),
            line_feature("b", "residential", [[20, 50], [100, 50]]),
        ],
        CFG,
    )
    m2 = road_mesh(g2, t, CFG)
    zs = sorted({round(float(z), 4) for z in (m2.tris[..., 2] - t.surface(m2.tris[..., 0], m2.tris[..., 1])).ravel()})
    assert len(zs) == 2 and zs[1] > zs[0]


def _footprint_by_colour(m, colour):
    sel = m.tris[(m.colours == colour).all(axis=1)]
    polys = shapely.polygons(sel[:, :, :2])
    return shapely.union_all(polys[shapely.area(polys) > 1e-12])


def test_same_class_roads_of_different_colour_never_overlap() -> None:
    """Asphalt and gravel residential roads meeting at a node share one height: where they
    overlapped, nothing decided which was on top and the two colours flickered."""
    t = make_terrain()
    g = build_graph(
        [
            line_feature("a", "residential", [[30, 50], [60, 50]], surface="asphalt"),
            line_feature("b", "residential", [[60, 50], [100, 70]], surface="asphalt"),
            line_feature("c", "residential", [[60, 50], [100, 25]], surface="gravel"),
        ],
        CFG,
    )
    m = road_mesh(g, t, CFG)
    asphalt = _footprint_by_colour(m, road_colour("residential", "asphalt", CFG))
    gravel = _footprint_by_colour(m, road_colour("residential", "gravel", CFG))
    assert gravel.area > 100 and asphalt.area > 100
    assert asphalt.intersection(gravel).area < 1e-6
    # nothing is lost: the three plain ribbons covered the same ground
    plain = shapely.union_all(
        [ribbon_footprint(np.array(e["polyline"]), e["width"], 2.0) for e in g["edges"]]
    )
    assert plain.difference(asphalt.union(gravel)).area < 0.5
    assert asphalt.union(gravel).contains(Point(60, 50))
