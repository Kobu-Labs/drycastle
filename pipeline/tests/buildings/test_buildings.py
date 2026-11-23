import hashlib
import json
import math

import pytest
from shapely.affinity import rotate
from shapely.geometry import Polygon, box

from drycastle_pipeline.buildings import (
    BuildingRules,
    RulesError,
    build_buildings,
    derive_building,
    load_building_rules,
    load_overrides,
    roof_frame,
    seed_for,
)

RULES = load_building_rules()


def rect(w: float, h: float) -> Polygon:
    return box(0, 0, w, h)


def derive(poly, tags=None, ov=None, bid="w1", pid="P-0001"):
    return derive_building(bid, pid, poly, tags or {}, RULES, ov)


def test_seed_is_sha256_based_and_stable() -> None:
    expected = int.from_bytes(hashlib.sha256(b"P-0001:w1").digest()[:8], "big")
    assert seed_for("P-0001", "w1") == expected
    assert seed_for("P-0001", "w1") != seed_for("P-0002", "w1")


def test_two_runs_are_identical() -> None:
    poly = rect(10, 8)
    assert derive(poly) == derive(poly)
    assert json.dumps(derive(poly), sort_keys=True) == json.dumps(derive(poly), sort_keys=True)


def test_colours_come_from_palettes_and_differ_by_seed() -> None:
    walls = {tuple(derive(rect(10, 8), bid=f"w{i}")["wall_colour"]) for i in range(40)}
    assert len(walls) > 1
    palette = {tuple(int(c[i : i + 2], 16) for i in (1, 3, 5)) for c in RULES.walls}
    assert walls <= palette


def test_tiny_shed_is_flat_and_low() -> None:
    b = derive(rect(4, 5))
    assert b["roof"] == "flat"
    assert 2.4 <= b["height_m"] <= 3.6
    assert b["ridge_dir"] is None and b["roof_height_m"] == 0


def test_house_is_pitched_with_ridge_on_long_side() -> None:
    b = derive(rect(12, 8))
    assert b["roof"] in ("gabled", "hipped")
    assert 5.5 <= b["height_m"] <= 7.1
    assert b["ridge_dir"] % math.pi == pytest.approx(0.0, abs=1e-3)
    assert b["eave_height_m"] + b["roof_height_m"] == pytest.approx(b["height_m"], abs=0.02)


def test_ridge_follows_rotated_long_side() -> None:
    poly = rotate(rect(14, 8), 40, origin=(0, 0))
    angle, _, _, a, b = roof_frame(poly)
    assert angle == pytest.approx(math.radians(40), abs=1e-3)
    assert a == pytest.approx(7, abs=0.01) and b == pytest.approx(4, abs=0.01)


def test_large_building_is_tall_with_low_pitch() -> None:
    b = derive(rect(30, 20))
    assert 7 <= b["height_m"] <= 9
    assert b["roof_height_m"] <= 3.2


def test_l_shape_falls_back_to_flat() -> None:
    l_shape = Polygon([(0, 0), (12, 0), (12, 4), (4, 4), (4, 12), (0, 12)])
    assert l_shape.area / l_shape.minimum_rotated_rectangle.area < 0.8
    assert derive(l_shape, {"roof:shape": "gabled"})["roof"] == "flat"


def test_osm_height_levels_and_roof_win() -> None:
    b = derive(rect(12, 8), {"height": "9.5 m", "roof:shape": "hipped"})
    assert b["height_m"] == 9.5 and b["roof"] == "hipped"
    b = derive(rect(12, 8), {"building:levels": "2", "roof:shape": "flat"})
    assert b["roof"] == "flat" and b["height_m"] == 6.0 and b["levels"] == 2
    b = derive(rect(12, 8), {"building:levels": "2", "roof:shape": "gabled"})
    assert b["eave_height_m"] == pytest.approx(6.0, abs=0.02) and b["height_m"] > 6.5
    b = derive(rect(12, 8), {"roof:levels": "2", "roof:shape": "gabled"})
    assert b["roof_height_m"] == pytest.approx(3.0)


def test_building_type_class() -> None:
    b = derive(rect(5, 6), {"building": "garage"})
    assert b["roof"] == "flat" and b["class"] == "type:garage"


def test_overrides_apply_to_any_field() -> None:
    ov = {"height_m": 11, "roof": "hipped", "wall_colour": "#102030", "roof_colour": "#405060"}
    b = derive(rect(5, 6), {"height": "3"}, ov)
    assert b["height_m"] == 11 and b["roof"] == "hipped"
    assert b["wall_colour"] == [16, 32, 48] and b["roof_colour"] == [64, 80, 96]
    b = derive(rect(12, 8), None, {"ridge_dir": math.pi / 2, "roof": "gabled"})
    assert b["ridge_dir"] == pytest.approx(math.pi / 2, abs=1e-3)


def test_overrides_do_not_shift_other_random_choices() -> None:
    plain = derive(rect(12, 8))
    forced = derive(rect(12, 8), None, {"height_m": 7})
    assert plain["wall_colour"] == forced["wall_colour"]


def test_overrides_file_validation(tmp_path) -> None:
    f = tmp_path / "o.yaml"
    f.write_text("buildings:\n  P-0001:\n    colour: '#fff'\n")
    with pytest.raises(RulesError):
        load_overrides(f)
    f.write_text("# nothing\n")
    assert load_overrides(f) == {}


def test_hole_footprint_keeps_holes() -> None:
    poly = Polygon(box(0, 0, 20, 20).exterior.coords, [box(8, 8, 12, 12).exterior.coords])
    assert len(derive(poly)["footprint"]["holes"]) == 1


def test_build_buildings_parcel_then_osm_override(tmp_path) -> None:
    norm = tmp_path / "normalised"
    norm.mkdir()
    feat = {
        "type": "Feature",
        "properties": {"osm_id": "w7", "tags": {}},
        "geometry": rect(12, 8).__geo_interface__,
    }
    (norm / "buildings.geojson").write_text(json.dumps({"features": [feat]}))
    parcels = {"parcels": [{"id": "P-0003", "buildings": ["w7"]}], "unassigned_buildings": ["w9"]}
    (tmp_path / "parcels.json").write_text(json.dumps(parcels))
    overrides = {
        "P-0003": {"height_m": 5, "roof": "flat"},
        "w7": {"height_m": 4},
        "w404": {"levels": 2},
    }
    res = build_buildings(tmp_path, RULES, overrides)
    (b,) = res.buildings
    assert b["height_m"] == 4 and b["roof"] == "flat" and b["parcel"] == "P-0003"
    assert res.unassigned == ["w9"] and res.unused_overrides == ["w404"]


def test_missing_rules_file_gives_defaults(tmp_path) -> None:
    assert isinstance(load_building_rules(tmp_path / "missing.yaml"), BuildingRules)
