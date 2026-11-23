from pathlib import Path

import pytest

from drycastle_pipeline.config import ConfigError, load_map_config, map_paths
from drycastle_pipeline.geo import centroid_of_geometry

def test_centroid_of_closed_way() -> None:
    pts = [(0.0, 0.0), (2.0, 0.0), (2.0, 2.0), (0.0, 2.0), (0.0, 0.0)]
    assert centroid_of_geometry(pts) == pytest.approx((1.0, 1.0))


def test_centroid_of_open_way_and_empty() -> None:
    assert centroid_of_geometry([(0.0, 0.0), (2.0, 0.0)]) == pytest.approx((1.0, 0.0))
    with pytest.raises(ValueError):
        centroid_of_geometry([])


RING_YAML = "[[-589500, -1250500], [-588000, -1250500], [-588000, -1249000]]"


def test_config_reads_osm_way_margin_and_polygon(tmp_path: Path) -> None:
    f = tmp_path / "m.yaml"
    f.write_text(f"osm_way: 42\nmargin_m: 5\npolygon: {RING_YAML}\n")
    cfg = load_map_config(f)
    assert (cfg.osm_way, cfg.margin_m) == (42, 5.0)
    assert cfg.polygon[0] == (-589500.0, -1250500.0)


def test_config_margin_has_a_default(tmp_path: Path) -> None:
    f = tmp_path / "m.yaml"
    f.write_text(f"osm_way: 42\npolygon: {RING_YAML}\n")
    assert load_map_config(f).margin_m > 0


def test_missing_polygon_is_a_config_error(tmp_path: Path) -> None:
    f = tmp_path / "m.yaml"
    f.write_text("osm_way: 42\nmargin_m: 5\n")
    with pytest.raises(ConfigError, match="polygon"):
        load_map_config(f)


def test_config_errors(tmp_path: Path) -> None:
    f = tmp_path / "m.yaml"
    f.write_text(f"polygon: {RING_YAML}\n")
    with pytest.raises(ConfigError):
        load_map_config(f)  # no osm_way
    with pytest.raises(ConfigError):
        load_map_config(tmp_path / "missing.yaml")


def test_map_paths_are_the_village_layout(tmp_path: Path) -> None:
    p = map_paths(tmp_path)
    assert (p.raw, p.work, p.out, p.preview) == (
        tmp_path / "data/village/raw", tmp_path / "data/village/work",
        tmp_path / "out/village/map", tmp_path / "out/village/preview",
    )
