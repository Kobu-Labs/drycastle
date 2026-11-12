"""The map outline: an arbitrary polygon instead of the square around the centre."""

import json
from pathlib import Path

import numpy as np
import pytest
from pyproj import Transformer
from shapely.geometry import box

from drycastle_pipeline.config import ConfigError, load_slice_config
from drycastle_pipeline.frame import Frame
from drycastle_pipeline.geo import METRIC_CRS, bbox_of_polygon
from drycastle_pipeline.normalise import area_polygon, fetched_bounds, run_normalise
from drycastle_pipeline.terrain import clip_to_outline, grid_from_samples

RING = [[-589500, -1250500], [-588000, -1250500], [-588000, -1249000], [-589500, -1249500]]


def test_village_polygon_is_read_and_a_closing_point_is_dropped(tmp_path: Path) -> None:
    f = tmp_path / "s.yaml"
    f.write_text(f"osm_way: 1\nvillage:\n  margin_m: 10\n  polygon: {RING + [RING[0]]}\n")
    cfg = load_slice_config(f, "village")
    assert cfg.polygon == tuple(tuple(map(float, p)) for p in RING)
    assert cfg.margin_m == 10.0
    assert load_slice_config(f).polygon is None  # the slice never has one


@pytest.mark.parametrize(
    "polygon",
    ["[[0, 0], [1, 1]]", "[[0, 0], [10, 10], [10, 0], [0, 10]]", "[[0, 0, 1], [1, 1], [2, 0]]", "5"],
)
def test_bad_village_polygon_is_rejected(tmp_path: Path, polygon: str) -> None:
    f = tmp_path / "s.yaml"
    f.write_text(f"osm_way: 1\nvillage:\n  polygon: {polygon}\n")
    with pytest.raises(ConfigError):
        load_slice_config(f, "village")


def test_bbox_of_polygon_covers_the_polygon_plus_margin() -> None:
    bbox = bbox_of_polygon([tuple(p) for p in RING], 25)
    to_wgs = Transformer.from_crs("EPSG:5514", "EPSG:4326", always_xy=True)
    to_metric = Transformer.from_crs("EPSG:5514", METRIC_CRS, always_xy=True)
    for x, y in [(-589500 - 25, -1250500 - 25), (-588000 + 25, -1249000 + 25)]:
        lon, lat = to_wgs.transform(x, y)
        e, n = to_metric.transform(x, y)
        assert bbox.west <= lon <= bbox.east and bbox.south <= lat <= bbox.north
        assert bbox.min_e <= e <= bbox.max_e and bbox.min_n <= n <= bbox.max_n
    assert bbox.max_e - bbox.min_e > 1500 and bbox.max_n - bbox.min_n > 1500
    with pytest.raises(ValueError):
        bbox_of_polygon([tuple(p) for p in RING], -1)


def test_fetched_bounds_is_the_polygon_when_there_is_one(tmp_path: Path) -> None:
    raw = tmp_path / "raw"
    raw.mkdir()
    assert area_polygon(raw) is None
    (raw / "area.json").write_text(
        json.dumps({"bbox_wgs84": [16.85, 48.39, 16.87, 48.41], "polygon_epsg5514": RING})
    )
    bounds = fetched_bounds(raw, "village")
    assert bounds[0] == bounds[-1] and len(bounds) == len(RING) + 1
    assert [-589500.0, -1250500.0] in bounds


def test_normalise_keeps_only_what_touches_the_polygon(tmp_path: Path) -> None:
    lon, lat = 16.86, 48.40
    frame0 = Frame.at(lon, lat)
    ox, oy = frame0.origin_x, frame0.origin_y
    # polygon: a 200 m square 100..300 m east of the origin (absolute S-JTSK)
    ring = [[ox + 100, oy - 100], [ox + 300, oy - 100], [ox + 300, oy + 100], [ox + 100, oy + 100]]
    raw, work = tmp_path / "raw", tmp_path / "work"
    raw.mkdir()
    work.mkdir()
    (raw / "slice_centre.json").write_text(json.dumps({"lon": lon, "lat": lat}))
    (raw / "area.json").write_text(
        json.dumps({"bbox_wgs84": [0, 0, 1, 1], "polygon_epsg5514": ring})
    )
    back = Transformer.from_crs("EPSG:5514", "EPSG:4326", always_xy=True)

    def way(i: int, x: float, y: float) -> dict:  # 10 m square building at local (x, y)
        corners = ((0, 0), (10, 0), (10, 10), (0, 10), (0, 0))
        pts = [back.transform(ox + x + dx, oy + y + dy) for dx, dy in corners]
        return {
            "type": "way", "id": i, "tags": {"building": "yes"},
            "geometry": [{"lon": a, "lat": b} for a, b in pts],
        }

    # inside, west of it, east of it, straddling the east edge
    elements = [way(1, 150, 0), way(2, -50, 0), way(3, 400, 0), way(4, 295, 0)]
    (raw / "osm.json").write_text(json.dumps({"elements": elements}))
    (raw / "parcels.geojson").write_text(json.dumps({"features": []}))
    counts = run_normalise(raw, work, "village")
    assert counts["buildings"] == 2
    feats = json.loads((work / "normalised/buildings.geojson").read_text())["features"]
    assert {ft["properties"]["osm_id"] for ft in feats} == {"w1", "w4"}
    frame = json.loads((work / "frame.json").read_text())
    assert len(frame["area_polygon_epsg5514"]) == len(ring) + 1
    assert frame["area_polygon_epsg5514"][0] == [round(ox + 100, 2), round(oy - 100, 2)]


def test_square_area_has_no_outline(tmp_path: Path) -> None:
    raw, work = tmp_path / "raw", tmp_path / "work"
    raw.mkdir()
    work.mkdir()
    (raw / "slice_centre.json").write_text(json.dumps({"lon": 16.86, "lat": 48.40}))
    (raw / "area.json").write_text(json.dumps({"bbox_wgs84": [16.85, 48.39, 16.87, 48.41]}))
    (raw / "osm.json").write_text(json.dumps({"elements": []}))
    (raw / "parcels.geojson").write_text(json.dumps({"features": []}))
    run_normalise(raw, work, "village")
    assert json.loads((work / "frame.json").read_text())["area_polygon_epsg5514"] is None


def test_clip_to_outline_cuts_coverage_and_mesh_but_keeps_a_cell_margin() -> None:
    origin = (1000.0, 2000.0)
    coverage = box(-500, -500, 500, 500)
    outline = [[1000, 2000], [1200, 2000], [1200, 2100], [1000, 2100]]

    def sampler(x, y):
        return np.zeros(x.shape), np.ones(x.shape, bool)

    clipped, clipped_sampler = clip_to_outline(coverage, sampler, outline, origin, 4.0)
    assert clipped.bounds == (0.0, 0.0, 200.0, 100.0)
    t = grid_from_samples(250, 4, clipped, clipped_sampler)
    xs, ys = t.vertex_xy()
    assert t.valid[(xs >= 0) & (xs <= 200) & (ys >= 0) & (ys <= 100)].all()  # every outline vertex
    far = (xs > 200 + 6.5) | (ys > 100 + 6.5) | (xs < -6.5) | (ys < -6.5)
    assert not t.valid[far].any()
    assert t.valid.sum() < t.valid.size  # not the whole chunk-aligned grid


def test_clip_to_outline_outside_the_raster_is_an_error() -> None:
    with pytest.raises(ValueError):
        clip_to_outline(
            box(0, 0, 10, 10), lambda x, y: (x, x > 0),
            [[5000, 5000], [5100, 5000], [5100, 5100]], (0.0, 0.0), 4.0,
        )
