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
