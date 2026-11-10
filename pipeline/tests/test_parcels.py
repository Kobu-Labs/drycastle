"""Registry, building assignment and kind rules on synthetic geometry (no network, no real data)."""

import json
from pathlib import Path

import pytest
from shapely.geometry import LineString, Point, box

from suchohrad_pipeline.normalise import parse_osm
from suchohrad_pipeline.parcels import Landuse, assign_buildings, build_parcels, classify_parcels
from suchohrad_pipeline.registry import load_registry, match_parcels, save_registry
from suchohrad_pipeline.rules import ParcelRules, load_rules

# Synthetic cadastre: three 100 x 100 m squares in a row, in EPSG:5514-like metres.
BASE = {
    "100/1": box(0, 0, 100, 100),
    "100/2": box(100, 0, 200, 100),
    "100/3": box(200, 0, 300, 100),
}


def ids_of(result) -> dict[str, str]:
    return result.ids


def test_first_run_allocates_sequential_ids_by_cadastral_reference() -> None:
    res = match_parcels({"parcels": []}, dict(reversed(list(BASE.items()))))
    assert res.ids == {"100/1": "P-0001", "100/2": "P-0002", "100/3": "P-0003"}
    assert res.new == ["P-0001", "P-0002", "P-0003"]


def test_two_identical_runs_are_idempotent_and_byte_identical(tmp_path: Path) -> None:
    path = tmp_path / "reg.json"
    first = match_parcels(load_registry(path), BASE)
    save_registry(first.registry, path)
    bytes_1 = path.read_bytes()
    second = match_parcels(load_registry(path), BASE)
    save_registry(second.registry, path)
    assert path.read_bytes() == bytes_1
    assert second.ids == first.ids
    assert not (second.new or second.retired or second.renumbered)


def test_renumbered_parcel_keeps_id_and_records_history() -> None:
    first = match_parcels({"parcels": []}, BASE)
    changed = {**BASE}
    changed["200/9"] = changed.pop("100/2")  # same outline, new number
    res = match_parcels(first.registry, changed)
    assert res.ids["200/9"] == first.ids["100/2"]
    assert res.renumbered == ["P-0002"]
    entry = next(e for e in res.registry["parcels"] if e["id"] == "P-0002")
    assert entry["cadastral_ref"] == "200/9"
    assert entry["previous_refs"] == ["100/2"]
    assert entry["status"] == "active"
    assert not res.new and not res.retired


def test_renumbered_with_slightly_changed_outline_still_matches() -> None:
    first = match_parcels({"parcels": []}, BASE)
    changed = {"100/1": BASE["100/1"], "100/3": BASE["100/3"], "9/9": box(102, 0, 200, 100)}
    res = match_parcels(first.registry, changed)
    assert res.ids["9/9"] == "P-0002"


def test_low_overlap_is_not_a_match() -> None:
    first = match_parcels({"parcels": []}, BASE)
    changed = {"100/1": BASE["100/1"], "100/3": BASE["100/3"], "9/9": box(190, 0, 290, 100)}
    res = match_parcels(first.registry, changed)
    # 9/9 overlaps 100/3 (an exact match keeps P-0003) and only barely the old 100/2
    assert res.ids["9/9"] == "P-0004"
    assert res.retired == ["P-0002"]


def test_removed_parcel_is_retired_and_its_id_never_reused() -> None:
    first = match_parcels({"parcels": []}, BASE)
    fewer = {k: v for k, v in BASE.items() if k != "100/3"}
    second = match_parcels(first.registry, fewer)
    assert second.retired == ["P-0003"]
    retired = next(e for e in second.registry["parcels"] if e["id"] == "P-0003")
    assert retired["status"] == "retired"
    assert retired["cadastral_ref"] == "100/3"

    # a brand new, unrelated parcel must not take the retired ID
    more = {**fewer, "500/1": box(1000, 1000, 1050, 1050)}
    third = match_parcels(second.registry, more)
    assert third.ids["500/1"] == "P-0004"
    assert any(e["id"] == "P-0003" and e["status"] == "retired" for e in third.registry["parcels"])
    assert not third.retired  # already retired earlier


def test_new_parcel_gets_next_id_after_highest_ever_used() -> None:
    first = match_parcels({"parcels": []}, BASE)
    grown = {**BASE, "7/1": box(0, 500, 10, 510), "7/0": box(0, 600, 10, 610)}
    res = match_parcels(first.registry, grown)
    assert res.ids["7/0"] == "P-0004"
    assert res.ids["7/1"] == "P-0005"
    assert [p["id"] for p in res.registry["parcels"]] == [f"P-000{i}" for i in range(1, 6)]


def test_retired_entry_is_revived_by_exact_reference() -> None:
    first = match_parcels({"parcels": []}, BASE)
    gone = match_parcels(first.registry, {"100/1": BASE["100/1"]})
    back = match_parcels(gone.registry, BASE)
    assert back.ids["100/3"] == "P-0003"
    assert back.revived == ["P-0002", "P-0003"]


def test_ids_grow_beyond_four_digits() -> None:
    reg = {
        "parcels": [
            {
                "id": "P-9999",
                "cadastral_ref": "x",
                "status": "retired",
                "previous_refs": [],
                "geometry": {
                    "area": 1.0,
                    "centroid": [0, 0],
                    "outline": [[[0, 0], [1, 0], [1, 1], [0, 0]]],
                },
            }
        ]
    }
    res = match_parcels(reg, {"a": box(5, 5, 6, 6)})
    assert res.ids["a"] == "P-10000"


# --- building assignment -------------------------------------------------------------------


def test_building_assignment_inside_straddling_and_outside() -> None:
    parcels = [box(0, 0, 100, 100), box(100, 0, 200, 100)]
    buildings = {
        "inside": box(10, 10, 20, 20),
        "straddle": box(90, 10, 130, 30),  # 10 m wide in parcel 0, 30 m in parcel 1
        "near": box(205, 10, 210, 20),  # 5 m off parcel 1
        "far": box(500, 500, 510, 510),
    }
    res = assign_buildings(parcels, buildings, max_distance_m=10)
    assert res.by_parcel == {0: ["inside"], 1: ["near", "straddle"]}
    assert res.unassigned == ["far"]


def test_building_beyond_max_distance_is_unassigned() -> None:
    res = assign_buildings([box(0, 0, 10, 10)], {"b": box(30, 0, 35, 5)}, max_distance_m=10)
    assert res.unassigned == ["b"]


# --- kind rules ----------------------------------------------------------------------------


def kind_of(parcel, *, building=False, highways=(), landuse=(), buildings=(), rules=None):
    rules = rules or ParcelRules(
        landuse_kinds={"forest": "forest", "meadow": "field"}, natural_kinds={"water": "water"}
    )
    return classify_parcels(
        [parcel], [building], list(highways), list(landuse), list(buildings), rules
    )[0]


def test_kind_building_plot() -> None:
    assert kind_of(box(0, 0, 30, 30), building=True) == "building_plot"


def test_kind_road_from_centreline_buffer() -> None:
    strip = box(0, 0, 200, 6)
    line = LineString([(0, 3), (200, 3)])
    assert kind_of(strip, highways=[line]) == "road"
    assert kind_of(box(0, 50, 200, 56), highways=[line]) == "other"


def test_kind_road_buffer_width_is_configurable() -> None:
    strip = box(0, 0, 200, 20)  # 10 m wide strip beside the line
    line = LineString([(0, 0), (200, 0)])
    narrow = ParcelRules(road_buffer_width_m=4)
    wide = ParcelRules(road_buffer_width_m=40)
    assert kind_of(strip, highways=[line], rules=narrow) == "other"
    assert kind_of(strip, highways=[line], rules=wide) == "road"


def test_kind_forest_field_water() -> None:
    p = box(0, 0, 200, 200)
    cover = box(-10, -10, 300, 300)
    assert kind_of(p, landuse=[Landuse("landuse", "forest", cover)]) == "forest"
    assert kind_of(p, landuse=[Landuse("landuse", "meadow", cover)]) == "field"
    assert kind_of(p, landuse=[Landuse("natural", "water", cover)]) == "water"
    assert kind_of(p, landuse=[Landuse("landuse", "forest", box(0, 0, 50, 50))]) == "other"


def test_kind_garden_needs_small_parcel_in_built_up_area() -> None:
    house = box(0, 0, 10, 10)
    small_near = box(15, 0, 45, 30)
    assert kind_of(small_near, buildings=[house]) == "garden"
    assert kind_of(box(500, 0, 530, 30), buildings=[house]) == "other"  # not built-up
    assert kind_of(box(15, 0, 215, 200), buildings=[house]) == "other"  # too big
    residential = Landuse("landuse", "residential", box(-500, -500, 1000, 1000))
    assert kind_of(box(500, 0, 530, 30), landuse=[residential]) == "garden"


def test_rules_yaml_loads_and_defines_kinds() -> None:
    rules = load_rules()
    assert rules.landuse_kinds["forest"] == "forest"
    assert rules.natural_kinds["water"] == "water"
    assert rules.chunk_size_m == 250


# --- normalise: OSM parsing ----------------------------------------------------------------


def _ring(x0, y0, x1, y1):
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0)]
    return [{"lon": x, "lat": y} for x, y in pts]


def test_parse_osm_builds_polygons_lines_and_multipolygons() -> None:
    raw = {
        "elements": [
            {
                "type": "way",
                "id": 1,
                "tags": {"building": "yes"},
                "geometry": _ring(17, 48, 17.001, 48.001),
            },
            {
                "type": "way",
                "id": 2,
                "tags": {"highway": "residential"},
                "geometry": [{"lon": 17, "lat": 48}, {"lon": 17.001, "lat": 48}],
            },
            {
                "type": "relation",
                "id": 3,
                "tags": {"landuse": "forest"},
                "members": [
                    {"type": "way", "role": "outer", "geometry": _ring(17, 48, 17.01, 48.01)},
                    {
                        "type": "way",
                        "role": "inner",
                        "geometry": _ring(17.002, 48.002, 17.004, 48.004),
                    },
                ],
            },
        ]
    }
    out = parse_osm(raw)
    assert [f.osm_id for f in out["buildings"]] == ["w1"]
    assert out["highways"][0].geom.geom_type == "LineString"
    forest = out["landuse"][0]
    assert forest.osm_id == "r3"
    assert forest.geom.area == pytest.approx(0.01 * 0.01 - 0.002 * 0.002)


# --- whole stage ---------------------------------------------------------------------------


def _fc(features):
    return {"type": "FeatureCollection", "features": features}


def _feat(geom, **props):
    return {"type": "Feature", "properties": props, "geometry": geom.__geo_interface__}


def _write_normalised(work: Path) -> None:
    norm = work / "normalised"
    norm.mkdir(parents=True)
    parcels = {"100/1": box(0, 0, 100, 100), "100/2": box(100, 0, 200, 100)}
    (norm / "parcels.geojson").write_text(
        json.dumps(_fc([_feat(g, cadastral_ref=r) for r, g in parcels.items()]))
    )
    (norm / "buildings.geojson").write_text(
        json.dumps(
            _fc(
                [
                    _feat(box(10, 10, 20, 20), osm_id="w1", tags={}),
                    _feat(box(900, 0, 910, 5), osm_id="w2", tags={}),
                ]
            )
        )
    )
    (norm / "highways.geojson").write_text(json.dumps(_fc([])))
    (norm / "landuse.geojson").write_text(json.dumps(_fc([])))
    (work / "frame.json").write_text(
        json.dumps({"crs": "test", "origin_epsg5514": [-500000.0, -1200000.0]})
    )


def test_stage_writes_parcels_json_and_is_idempotent(tmp_path: Path) -> None:
    work = tmp_path / "work"
    _write_normalised(work)
    registry = tmp_path / "registry" / "parcels.registry.json"
    rules = ParcelRules(chunk_size_m=50)

    res = build_parcels(work, registry, rules)
    assert res.match.new == ["P-0001", "P-0002"]
    assert res.unassigned == ["w2"]
    doc = json.loads((work / "parcels.json").read_text())
    p1, p2 = doc["parcels"]
    assert (p1["id"], p1["cadastral_ref"], p1["kind"], p1["buildings"]) == (
        "P-0001",
        "100/1",
        "building_plot",
        ["w1"],
    )
    assert p2["buildings"] == [] and p1["area"] == 10000
    assert p1["chunk"] == [int(p1["anchor"][0] // 50), int(p1["anchor"][1] // 50)]
    assert box(0, 0, 100, 100).contains(Point(*p1["anchor"]))

    before = (registry.read_bytes(), (work / "parcels.json").read_bytes())
    again = build_parcels(work, registry, rules)
    assert (registry.read_bytes(), (work / "parcels.json").read_bytes()) == before
    assert not again.match.new


# --- area-aware registry: slice and village share one registry --------------------------------

# Slice box covers the first two parcels, the village box all of them (plus extra parcels).
SLICE_BOX = box(-10, -10, 210, 110)
VILLAGE_BOX = box(-10, -10, 1100, 1100)
VILLAGE = {**BASE, "200/1": box(500, 0, 600, 100), "200/2": box(600, 0, 700, 100)}
SLICE = {k: v for k, v in VILLAGE.items() if SLICE_BOX.covers(v)}


def _run(registry: dict, current: dict, bounds):
    res = match_parcels(registry, current, 0.5, bounds)
    return res


def test_village_after_slice_keeps_ids_and_appends_new_ones() -> None:
    s = _run({"parcels": []}, SLICE, SLICE_BOX)
    assert s.ids == {"100/1": "P-0001", "100/2": "P-0002"}
    v = _run(s.registry, VILLAGE, VILLAGE_BOX)
    assert v.ids["100/1"] == "P-0001" and v.ids["100/2"] == "P-0002"
    assert v.ids["100/3"] == "P-0003"
    assert v.ids["200/1"] == "P-0004" and v.ids["200/2"] == "P-0005"
    assert not v.retired


def test_slice_after_village_does_not_retire_village_only_parcels() -> None:
    v = _run({"parcels": []}, VILLAGE, VILLAGE_BOX)
    s = _run(v.registry, SLICE, SLICE_BOX)
    assert not s.retired and not s.new
    assert all(e["status"] == "active" for e in s.registry["parcels"])
    assert s.registry == v.registry  # outside entries untouched, inside ones unchanged


def test_unmatched_entry_inside_area_is_retired_outside_is_not() -> None:
    v = _run({"parcels": []}, VILLAGE, VILLAGE_BOX)
    gone_inside = {k: p for k, p in SLICE.items() if k != "100/2"}
    s = _run(v.registry, gone_inside, SLICE_BOX)
    assert s.retired == [v.ids["100/2"]]
    status = {e["id"]: e["status"] for e in s.registry["parcels"]}
    assert status[v.ids["100/2"]] == "retired"
    assert status[v.ids["200/1"]] == "active" and status[v.ids["100/3"]] == "active"


def test_without_bounds_every_unclaimed_entry_is_retired() -> None:
    v = _run({"parcels": []}, VILLAGE, None)
    s = _run(v.registry, SLICE, None)
    assert len(s.retired) == 3


def test_both_orders_are_idempotent_and_stable(tmp_path: Path) -> None:
    path = tmp_path / "reg.json"
    sequence = [(SLICE, SLICE_BOX), (VILLAGE, VILLAGE_BOX), (SLICE, SLICE_BOX),
                (VILLAGE, VILLAGE_BOX), (VILLAGE, VILLAGE_BOX), (SLICE, SLICE_BOX)]
    seen = []
    for current, bounds in sequence:
        res = _run(load_registry(path), current, bounds)
        save_registry(res.registry, path)
        seen.append(path.read_bytes())
    assert seen[1] == seen[2] == seen[3] == seen[4] == seen[5]  # nothing changes after village
    first_ids = {e["cadastral_ref"]: e["id"] for e in json.loads(seen[0])["parcels"]}
    final_ids = {e["cadastral_ref"]: e["id"] for e in json.loads(seen[-1])["parcels"]}
    assert all(final_ids[r] == i for r, i in first_ids.items())
