from pathlib import Path

import pytest

from suchohrad_pipeline.config import ConfigError, load_slice_config
from suchohrad_pipeline.geo import centroid_of_geometry, compute_bbox

# Synthetic point, not a real location of interest.
LON, LAT = 17.0, 48.5


def test_bbox_is_square_of_radius_plus_margin() -> None:
    bbox = compute_bbox(LON, LAT, 100, 25)
    assert bbox.max_e - bbox.min_e == pytest.approx(250)
    assert bbox.max_n - bbox.min_n == pytest.approx(250)


def test_bbox_wgs84_contains_centre_and_is_ordered() -> None:
    bbox = compute_bbox(LON, LAT, 100)
    assert bbox.west < LON < bbox.east
    assert bbox.south < LAT < bbox.north
    # 200 m is roughly 0.0018 deg latitude
    assert bbox.north - bbox.south == pytest.approx(200 / 111_200, rel=0.08)


def test_margin_enlarges_bbox() -> None:
    small = compute_bbox(LON, LAT, 100, 0)
    big = compute_bbox(LON, LAT, 100, 50)
    assert big.west < small.west and big.east > small.east
    assert big.south < small.south and big.north > small.north


def test_bbox_rejects_bad_input() -> None:
    with pytest.raises(ValueError):
        compute_bbox(LON, LAT, 0)
    with pytest.raises(ValueError):
        compute_bbox(LON, LAT, 10, -1)


def test_formatted_axis_order() -> None:
    bbox = compute_bbox(LON, LAT, 100)
    s, w, n, e = (float(v) for v in bbox.overpass.split(","))
    assert (s, w, n, e) == pytest.approx((bbox.south, bbox.west, bbox.north, bbox.east))
    assert bbox.wfs.endswith(",urn:ogc:def:crs:EPSG::4326")


def test_centroid_of_closed_way() -> None:
    pts = [(0.0, 0.0), (2.0, 0.0), (2.0, 2.0), (0.0, 2.0), (0.0, 0.0)]
    assert centroid_of_geometry(pts) == pytest.approx((1.0, 1.0))


def test_centroid_of_open_way_and_empty() -> None:
    assert centroid_of_geometry([(0.0, 0.0), (2.0, 0.0)]) == pytest.approx((1.0, 0.0))
    with pytest.raises(ValueError):
        centroid_of_geometry([])


def test_config_defaults(tmp_path: Path) -> None:
    f = tmp_path / "s.yaml"
    f.write_text("osm_way: 42\n")
    cfg = load_slice_config(f)
    assert (cfg.osm_way, cfg.radius_m) == (42, 100.0)
    assert cfg.margin_m > 0


def test_config_overrides_and_errors(tmp_path: Path) -> None:
    f = tmp_path / "s.yaml"
    f.write_text("osm_way: 42\nradius_m: 50\nmargin_m: 5\n")
    cfg = load_slice_config(f)
    assert (cfg.radius_m, cfg.margin_m) == (50.0, 5.0)
    f.write_text("radius_m: 50\n")
    with pytest.raises(ConfigError):
        load_slice_config(f)
    with pytest.raises(ConfigError):
        load_slice_config(tmp_path / "missing.yaml")


def test_village_section_defaults_and_overrides(tmp_path: Path) -> None:
    f = tmp_path / "s.yaml"
    f.write_text("osm_way: 42\nradius_m: 50\nmargin_m: 5\n")
    cfg = load_slice_config(f, "village")
    assert (cfg.osm_way, cfg.radius_m, cfg.margin_m) == (42, 700.0, 5.0)
    f.write_text("osm_way: 42\nradius_m: 50\nvillage:\n  radius_m: 900\n  margin_m: 40\n")
    cfg = load_slice_config(f, "village")
    assert (cfg.radius_m, cfg.margin_m) == (900.0, 40.0)
    assert load_slice_config(f).radius_m == 50.0  # the slice is unaffected
    f.write_text("osm_way: 42\nvillage:\n  radius_m: -1\n")
    with pytest.raises(ConfigError):
        load_slice_config(f, "village")
    with pytest.raises(ConfigError):
        load_slice_config(f, "moon")


def test_area_paths_keep_slice_layout_and_separate_village(tmp_path: Path) -> None:
    from suchohrad_pipeline.config import area_paths

    s, v = area_paths("slice", tmp_path), area_paths("village", tmp_path)
    assert (s.raw, s.work, s.out) == (
        tmp_path / "data/raw", tmp_path / "data/work", tmp_path / "out/map")
    assert (v.raw, v.work, v.out) == (
        tmp_path / "data/village/raw", tmp_path / "data/village/work",
        tmp_path / "out/village/map")
    with pytest.raises(ConfigError):
        area_paths("moon")
