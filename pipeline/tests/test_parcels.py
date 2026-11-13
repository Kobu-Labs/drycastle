"""Registry, building assignment and the parcels stage on synthetic geometry (no network, no real data)."""

import json
from pathlib import Path

import pytest
from shapely.geometry import Point, box, shape

from drycastle_pipeline.normalise import parse_osm
from drycastle_pipeline.parcels import assign_buildings, build_parcels
from drycastle_pipeline.registry import load_registry, match_parcels, save_registry
from drycastle_pipeline.rules import ParcelRules, load_rules

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


def test_rules_yaml_loads() -> None:
    assert load_rules().chunk_size_m == 250


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
        json.dumps({"crs": "test", "origin_epsg5514": [-500000.0, -1200000.0], "area_polygon_epsg5514": [[-500050.0, -1200050.0], [-498900.0, -1200050.0], [-498900.0, -1198900.0], [-500050.0, -1198900.0]]})
    )


def _edits(tmp_path: Path, groups: dict) -> Path:
    path = tmp_path / "edits.json"
    path.write_text(json.dumps({"version": 1, "groups": {g: {"parcels": m} for g, m in groups.items()}}))
    return path


def test_stage_keeps_properties_and_turns_the_rest_into_ground(tmp_path: Path) -> None:
    work = tmp_path / "work"
    _write_normalised(work)
    registry = tmp_path / "registry" / "parcels.registry.json"
    rules = ParcelRules(chunk_size_m=50)

    res = build_parcels(work, registry, rules, edits_path=_edits(tmp_path, {}))
    assert res.match.new == ["P-0001", "P-0002"]  # every cadastral parcel keeps its registry entry
    assert res.unassigned == ["w2"]
    assert (res.parcel_count, res.dropped, dict(res.ground)) == (1, 1, {"field": 1})
    doc = json.loads((work / "parcels.json").read_text())
    (p1,) = doc["parcels"]
    assert (p1["id"], p1["cadastral_ref"], p1["kind"], p1["buildings"]) == (
        "P-0001",
        "100/1",
        "building_plot",
        ["w1"],
    )
    assert p1["area"] == 10000
    assert p1["chunk"] == [int(p1["anchor"][0] // 50), int(p1["anchor"][1] // 50)]
    assert box(0, 0, 100, 100).contains(Point(*p1["anchor"]))
    (g1,) = doc["ground"]  # the parcel without a building is ground of the default type
    assert (g1["id"], g1["kind"], g1["buildings"]) == ("A-0001", "field", [])
    assert shape(g1["polygon"]).equals(box(100, 0, 200, 100))

    before = (registry.read_bytes(), (work / "parcels.json").read_bytes())
    again = build_parcels(work, registry, rules, edits_path=_edits(tmp_path, {}))
    assert (registry.read_bytes(), (work / "parcels.json").read_bytes()) == before
    assert not again.match.new


def test_stage_keeps_a_parcel_grouped_with_a_building_plot_as_garden(tmp_path: Path) -> None:
    work = tmp_path / "work"
    _write_normalised(work)
    registry = tmp_path / "registry" / "parcels.registry.json"
    res = build_parcels(work, registry, ParcelRules(), edits_path=_edits(tmp_path, {"G-0001": ["P-0001", "P-0002"]}))
    doc = json.loads((work / "parcels.json").read_text())
    assert [(p["id"], p["kind"]) for p in doc["parcels"]] == [("P-0001", "building_plot"), ("P-0002", "garden")]
    assert doc["ground"] == [] and res.dropped == 0


# --- registry: partial and full coverage share one registry ---------------------------------------

# The partial box covers the first two parcels, the full box all of them (plus extra parcels).
PART_BOX = box(-10, -10, 210, 110)
FULL_BOX = box(-10, -10, 1100, 1100)
FULL = {**BASE, "200/1": box(500, 0, 600, 100), "200/2": box(600, 0, 700, 100)}
PART = {k: v for k, v in FULL.items() if PART_BOX.covers(v)}


def _run(registry: dict, current: dict, bounds):
    res = match_parcels(registry, current, 0.5, bounds)
    return res


def test_full_after_partial_keeps_ids_and_appends_new_ones() -> None:
    s = _run({"parcels": []}, PART, PART_BOX)
    assert s.ids == {"100/1": "P-0001", "100/2": "P-0002"}
    v = _run(s.registry, FULL, FULL_BOX)
    assert v.ids["100/1"] == "P-0001" and v.ids["100/2"] == "P-0002"
    assert v.ids["100/3"] == "P-0003"
    assert v.ids["200/1"] == "P-0004" and v.ids["200/2"] == "P-0005"
    assert not v.retired


def test_partial_after_full_does_not_retire_full_only_parcels() -> None:
    v = _run({"parcels": []}, FULL, FULL_BOX)
    s = _run(v.registry, PART, PART_BOX)
    assert not s.retired and not s.new
    assert all(e["status"] == "active" for e in s.registry["parcels"])
    assert s.registry == v.registry  # outside entries untouched, inside ones unchanged


def test_unmatched_entry_inside_bounds_is_retired_outside_is_not() -> None:
    v = _run({"parcels": []}, FULL, FULL_BOX)
    gone_inside = {k: p for k, p in PART.items() if k != "100/2"}
    s = _run(v.registry, gone_inside, PART_BOX)
    assert s.retired == [v.ids["100/2"]]
    status = {e["id"]: e["status"] for e in s.registry["parcels"]}
    assert status[v.ids["100/2"]] == "retired"
    assert status[v.ids["200/1"]] == "active" and status[v.ids["100/3"]] == "active"


def test_without_bounds_every_unclaimed_entry_is_retired() -> None:
    v = _run({"parcels": []}, FULL, None)
    s = _run(v.registry, PART, None)
    assert len(s.retired) == 3


def test_both_orders_are_idempotent_and_stable(tmp_path: Path) -> None:
    path = tmp_path / "reg.json"
    sequence = [(PART, PART_BOX), (FULL, FULL_BOX), (PART, PART_BOX),
                (FULL, FULL_BOX), (FULL, FULL_BOX), (PART, PART_BOX)]
    seen = []
    for current, bounds in sequence:
        res = _run(load_registry(path), current, bounds)
        save_registry(res.registry, path)
        seen.append(path.read_bytes())
    assert seen[1] == seen[2] == seen[3] == seen[4] == seen[5]  # nothing changes after village
    first_ids = {e["cadastral_ref"]: e["id"] for e in json.loads(seen[0])["parcels"]}
    final_ids = {e["cadastral_ref"]: e["id"] for e in json.loads(seen[-1])["parcels"]}
    assert all(final_ids[r] == i for r, i in first_ids.items())


def test_stage_turns_a_region_with_a_ground_type_into_ground(tmp_path: Path, capsys) -> None:
    work = tmp_path / "work"
    _write_normalised(work)
    ox, oy = -500000.0, -1200000.0

    def ring(x0, y0, x1, y1):
        return [[ox + x0, oy + y0], [ox + x1, oy + y0], [ox + x1, oy + y1], [ox + x0, oy + y1]]

    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"version": 1, "regions": {
        "R-0001": {"colour": "#112233", "polygon": ring(50, 0, 150, 50), "ground": ["meadow"]},  # half over the building plot
        "R-0002": {"colour": "#112233", "polygon": ring(150, 0, 200, 50), "ground": ["swamp"]},
        "R-0003": {"colour": "#112233", "polygon": ring(150, 50, 200, 100)},  # paint only
    }}))
    build_parcels(work, tmp_path / "registry" / "parcels.registry.json", ParcelRules(), edits_path=edits)
    doc = json.loads((work / "parcels.json").read_text())
    got = sorted((g["kind"], g["area"]) for g in doc["ground"])
    assert got == [("field", 7500.0), ("meadow", 2500.0)]  # the meadow stops at the parcel line
    meadow = next(g for g in doc["ground"] if g["kind"] == "meadow")
    assert shape(meadow["polygon"]).equals(box(100, 0, 150, 50))
    assert "R-0002" in capsys.readouterr().err  # unknown type: warned about and skipped
    assert "ground_layers" not in doc  # no type adds attributes: the key is only there when something does


def test_stage_keeps_the_attributes_that_add_up_as_layers_and_leaves_the_ground_alone(tmp_path: Path) -> None:
    ox, oy = -500000.0, -1200000.0

    def ring(x0, y0, x1, y1):
        return [[ox + x0, oy + y0], [ox + x1, oy + y0], [ox + x1, oy + y1], [ox + x0, oy + y1]]

    def build(name: str, regions: dict) -> dict:
        work = tmp_path / name / "work"
        _write_normalised(work)
        edits = tmp_path / name / "edits.json"
        edits.write_text(json.dumps({"version": 1, "ground_types": {"bigfoot_zone": {"attributes": {"event_spawn": ["bigfoot"]}}}, "regions": regions}))
        build_parcels(work, tmp_path / name / "registry" / "parcels.registry.json", ParcelRules(), edits_path=edits)
        return json.loads((work / "parcels.json").read_text())

    meadow = {"colour": "#112233", "polygon": ring(50, 0, 150, 50), "ground": ["meadow"]}
    plain = build("plain", {"R-0001": meadow})
    stacked = build("stacked", {
        "R-0001": {**meadow, "ground": ["meadow", "bigfoot_zone"]},
        "R-0002": {"colour": "#aa00ff", "polygon": ring(150, 0, 200, 50), "ground": ["bigfoot_zone"]},  # only the event, on the field
    })
    assert stacked["ground"] == plain["ground"]  # the layers change no ground area
    assert [(layer["id"], layer["region"], layer["types"]) for layer in stacked["ground_layers"]] == [
        ("L-0001", "R-0001", ["bigfoot_zone"]), ("L-0002", "R-0002", ["bigfoot_zone"]),
    ]
    assert shape(stacked["ground_layers"][1]["polygon"]).equals(box(150, 0, 200, 50))
    assert "ground_layers" not in plain


def test_a_carved_parcel_is_its_building_outline_and_the_rest_is_ground(tmp_path: Path) -> None:
    work = tmp_path / "work"
    _write_normalised(work)  # P-0001 = 100 x 100 m with a 10 x 10 m building, P-0002 beside it, no building
    rules = tmp_path / "rules.yaml"
    rules.write_text("ground:\n  carve:\n    P-0001: {margin_m: 2}\n    P-0002: {}\n")  # P-0002 has no building
    build_parcels(work, tmp_path / "registry" / "parcels.registry.json", ParcelRules(), rules_path=rules,
                  edits_path=_edits(tmp_path, {}))
    doc = json.loads((work / "parcels.json").read_text())
    (p1,) = doc["parcels"]
    assert p1["id"] == "P-0001" and p1["kind"] == "building_plot" and p1["buildings"] == ["w1"]
    assert abs(p1["area"] - 14 * 14) < 1.0 and box(10, 10, 20, 20).within(shape(p1["polygon"]))
    (g1,) = doc["ground"]  # the rest of P-0001 and all of P-0002 are one field, with no hole and no overlap
    assert abs(g1["area"] - (20000 - p1["area"])) < 0.1
    assert shape(g1["polygon"]).intersection(shape(p1["polygon"])).area < 1e-6
