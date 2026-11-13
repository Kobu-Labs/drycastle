"""The Street View follower: address -> S-JTSK position, view heading on the map grid, the files it writes."""

import json
import math

from drycastle_pipeline import sketchfile
from drycastle_pipeline import streetview_follow as sf

PANO = (
    "https://www.google.com/maps/@48.4024653,16.8607311,146a,75y,97.24h,54.44t/data=!3m7!1e1!3m5!1sCNVvw83Yn7gxmoWBmBRK5g"
    "!2e0?hl=sk&entry=ttu"
)


def test_parse_street_view_address():
    assert sf.parse_maps_url(PANO) == (48.4024653, 16.8607311, 97.24)


def test_parse_map_address_has_no_heading():
    assert sf.parse_maps_url("https://www.google.com/maps/@48.40,16.86,17z") == (48.40, 16.86, None)


def test_parse_ignores_other_pages():
    assert sf.parse_maps_url("https://example.com/maps/@48.40,16.86,17z") is None
    assert sf.parse_maps_url("https://www.google.com/search?q=lamp") is None
    assert sf.parse_maps_url("chrome://newtab/") is None


def test_position_doc_matches_the_sketch_projection():
    doc = sf.position_doc(48.4024653, 16.8607311, 97.24, seq=7)
    point, view = sf.to_sketch(48.4024653, 16.8607311, 97.24)
    assert [doc["x"], doc["y"]] == point
    assert doc["seq"] == 7
    assert doc["format_version"] == sf.POSITION_FORMAT_VERSION
    # the grid heading is the direction of the projected view line, clockwise from grid north
    dx, dy = view[1][0] - view[0][0], view[1][1] - view[0][1]
    assert math.isclose(doc["heading_grid"], math.degrees(math.atan2(dx, dy)) % 360, abs_tol=0.01)
    # the projection is turned a little against true north, but not by tens of degrees
    assert abs((doc["heading_grid"] - 97.24 + 180) % 360 - 180) < 10


def test_position_doc_without_heading():
    doc = sf.position_doc(48.4, 16.86, None, seq=1)
    assert doc["heading"] is None and doc["heading_grid"] is None


def test_write_position_is_atomic_json(tmp_path):
    path = tmp_path / "out" / "streetview-position.json"
    doc = sf.position_doc(48.4, 16.86, 10.0, seq=3)
    sf.write_position(path, doc)
    assert json.loads(path.read_text()) == doc
    assert not path.with_name(path.name + ".tmp").exists()


def test_update_sketch_moves_one_point_and_line(tmp_path):
    path = tmp_path / "sketch.json"
    sf.update_sketch(path, [1.0, 2.0], [[1.0, 2.0], [3.0, 4.0]])
    sf.update_sketch(path, [5.0, 6.0], [[5.0, 6.0], [7.0, 8.0]])
    doc = sketchfile.read(path)
    assert [(p["x"], p["y"]) for p in doc["points"]] == [(5.0, 6.0)]
    assert [ln["points"] for ln in doc["lines"]] == [[[5.0, 6.0], [7.0, 8.0]]]
    assert doc["version"] == 2


# --- points: stop and look, a permanent point is dropped -----------------------------------------


def view(lat: float, lon: float, heading: float | None = 90.0):
    return (lat, lon, heading)


def test_a_view_that_stays_put_drops_one_point_at_the_aim():
    d = sf.PointDropper(dwell_s=2.0, at="aim", ahead_m=8.0)
    v = view(48.4024653, 16.8607311, 97.24)
    assert d.feed(v, 0.0) is None  # just arrived: the wait starts
    assert d.feed(v, 1.0) is None  # not long enough yet
    spot = d.feed(v, 2.5)
    here, line = sf.to_sketch(*v)
    assert spot == line[1] and spot != here  # where you look, not where you stand
    assert d.feed(v, 9.0) is None  # one point per stop


def test_walking_or_turning_drops_nothing_and_a_new_stop_drops_another():
    d = sf.PointDropper(dwell_s=2.0, min_gap_m=1.5)
    a, b = view(48.4024653, 16.8607311, 90.0), view(48.4025653, 16.8607311, 90.0)
    t = 0.0
    for v in (a, b, view(48.4026653, 16.8607311, 90.0), view(48.4026653, 16.8607311, 100.0)):  # always moving
        assert d.feed(v, t) is None
        t += 1.0
    spot_b = d.feed(b, 10.0)  # stops on a new view (the wait starts)
    assert spot_b is None
    assert d.feed(b, 12.5) is not None
    assert d.feed(a, 13.0) is None and d.feed(a, 16.0) is not None  # another stop elsewhere: another point


def test_a_stop_within_the_min_gap_of_the_last_point_is_not_a_new_point():
    d = sf.PointDropper(dwell_s=1.0, min_gap_m=50.0)
    d.feed(view(48.4024653, 16.8607311), 0.0)
    assert d.feed(view(48.4024653, 16.8607311), 1.5) is not None
    d.feed(view(48.4024700, 16.8607311), 2.0)  # a few metres on
    assert d.feed(view(48.4024700, 16.8607311), 4.0) is None


def test_aim_needs_a_direction_but_camera_does_not():
    flat = view(48.4, 16.86, None)  # the plain map, no Street View
    aim = sf.PointDropper(dwell_s=1.0, at="aim")
    aim.feed(flat, 0.0)
    assert aim.feed(flat, 2.0) is None
    cam = sf.PointDropper(dwell_s=1.0, at="camera")
    cam.feed(flat, 0.0)
    assert cam.feed(flat, 2.0) == sf.to_sketch(*flat)[0]
    assert aim.feed(None, 3.0) is None  # the tab is not on Google Maps


def test_add_point_appends_permanent_points_to_a_sketch(tmp_path):
    path = tmp_path / "sketch.json"
    first = sf.add_point(path, [1.0, 2.0])
    second = sf.add_point(path, [3.0, 4.0])
    sf.update_sketch(path, [9.0, 9.0], [[9.0, 9.0], [10.0, 9.0]])  # the moving marker lives in another sketch
    doc = sketchfile.read(path)
    pts = [(p["id"], p["sketch"], p["x"], p["y"]) for p in doc["points"] if p["sketch"] == "POLES"]
    assert pts == [(first, "POLES", 1.0, 2.0), (second, "POLES", 3.0, 4.0)] and first != second
    assert doc["sketches"] == ["POLES", "STREETVIEW"]
