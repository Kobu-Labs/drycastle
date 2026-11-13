"""Runtime attributes (issue #263): candidate points for the server, baked into attribute_points.json.

The areas that carry an attribute come from the ground areas of a type that ticks it and from the attribute layers stacked
over ground (ADR 0015); the points lie inside their union, inside the terrain, clear of buildings and water."""

import json
import random

import numpy as np
import pytest
import shapely
from shapely.geometry import Point, box, mapping

from drycastle_pipeline.attribute_points import (
    AttributePointsError,
    allowlist,
    build_attribute_points,
    carrying_areas,
    point_count,
    sample_points,
)
from drycastle_pipeline.buildings import derive_building, footprint_polygon, load_building_rules
from drycastle_pipeline.export import run_export
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from tests.synthetic import make_terrain, make_workspace

BIGFOOT = {"attributes": {"event_spawn": ["bigfoot"]}}
FOREST = {"attributes": {"colours": ["#2f5f38"], "recipe": "forest"}}
WATER = {"attributes": {"colours": ["#6b7a6a"], "water": True}}
RULE = {"m2_per_point": 100, "min_points": 10, "max_points": 50, "building_clearance_m": 10, "water_clearance_m": 5}
SECTION = {"event_spawn": {"bigfoot": RULE}}
TYPES = {"forest": FOREST, "water": WATER, "bigfoot_zone": BIGFOOT, "bigfoot_forest": {"attributes": {**FOREST["attributes"], "event_spawn": ["bigfoot"]}}}


def ground(gid: str, kind: str, geom) -> dict:
    return {"id": gid, "kind": kind, "polygon": mapping(geom), "area": geom.area}


def layer(lid: str, types: list[str], geom) -> dict:
    return {"id": lid, "region": "R-0001", "types": types, "polygon": mapping(geom)}


def hut(geom) -> dict:
    return derive_building("w9", "P-0001", geom, {}, load_building_rules())


def inside(points: list, geom) -> bool:
    return all(shapely.contains_xy(geom, p[0], p[1]) for p in points)


# --- the allowlist and the density rule -----------------------------------------------------------


def test_the_shipped_allowlist_is_the_bigfoot_event_spawn_only() -> None:
    entries = allowlist(load_section("runtime_points"))
    assert [(a, v) for a, v, _ in entries] == [("event_spawn", "bigfoot")]


@pytest.mark.parametrize(
    "section, message",
    [
        ({"spawns": {"x": RULE}}, "not a list attribute"),  # a flag of the catalog is not on the allowlist
        ({"water": {"x": RULE}}, "not a list attribute"),
        ({"nonsense": {}}, "not a list attribute"),
        ({"event_spawn": {"yeti": RULE}}, "unknown value"),
        ({"event_spawn": {"bigfoot": {**RULE, "m2_per_point": 0}}}, "m2_per_point"),
        ({"event_spawn": {"bigfoot": {**RULE, "min_points": 9, "max_points": 3}}}, "min_points"),
        ({"event_spawn": {"bigfoot": {**RULE, "water_clearance_m": -1}}}, "water_clearance_m"),
        ({"event_spawn": {"bigfoot": {k: v for k, v in RULE.items() if k != "min_points"}}}, "exactly the keys"),
        ({"event_spawn": {"bigfoot": {**RULE, "min_tree_distance_m": 2}}}, "exactly the keys"),  # no clearance from trees
    ],
)
def test_a_wrong_allowlist_is_an_error_not_a_skip(section: dict, message: str) -> None:
    with pytest.raises(AttributePointsError, match=message):
        allowlist(section)


def test_the_count_is_a_density_between_a_minimum_and_a_maximum() -> None:
    assert point_count(0, RULE) == 0  # no usable ground: nothing to place, not even the minimum
    assert point_count(-5, RULE) == 0
    assert point_count(300, RULE) == 10  # 3 by density, lifted to the minimum
    assert point_count(2000, RULE) == 20
    assert point_count(1e6, RULE) == 50  # capped


# --- sampling ---------------------------------------------------------------------------------


def test_sampling_stays_inside_pieces_and_holes_and_is_seeded() -> None:
    region = box(0, 0, 100, 100).difference(box(40, 40, 60, 60)).union(box(200, 0, 230, 30))
    a = sample_points(region, 80, random.Random(7))
    assert len(a) == 80
    assert all(shapely.contains_xy(region, x, y) for x, y in a)
    assert a == sample_points(region, 80, random.Random(7))
    assert a != sample_points(region, 80, random.Random(8))
    assert sample_points(region, 0, random.Random(7)) == []
    assert sample_points(shapely.Polygon(), 5, random.Random(7)) == []


def test_sampling_is_spread_by_area() -> None:
    big, small = box(0, 0, 90, 10), box(200, 0, 210, 10)  # 900 m2 against 100 m2
    pts = sample_points(big.union(small), 1000, random.Random(1))
    share = sum(1 for x, _ in pts if x > 100) / len(pts)
    assert 0.06 < share < 0.14


# --- which areas carry an attribute ------------------------------------------------------------


def test_ground_areas_of_a_type_and_stacked_layers_both_carry_it() -> None:
    doc = {
        "ground": [
            ground("A-0001", "forest", box(0, 0, 10, 10)),
            ground("A-0002", "bigfoot_forest", box(20, 0, 30, 10)),  # a type that is forest and carries the event itself
        ],
        "ground_layers": [
            layer("L-0001", ["bigfoot_zone"], box(40, 0, 50, 10)),
            layer("L-0002", ["something_else"], box(60, 0, 70, 10)),  # a type the table does not know: carries nothing
        ],
    }
    got = carrying_areas(doc, TYPES, "event_spawn", "bigfoot")
    assert [aid for aid, _ in got] == ["A-0002", "L-0001"]
    assert carrying_areas(doc, TYPES, "event_spawn", "nothing") == []
    assert carrying_areas({}, TYPES, "event_spawn", "bigfoot") == []


# --- the points ---------------------------------------------------------------------------------


def bake(doc: dict, buildings: list[dict] | None = None, water: list | None = None, rule: dict = RULE):
    water_doc = {"bodies": [{"id": "W", "polygon": mapping(g)} for g in (water or [])]}
    return build_attribute_points(doc, buildings or [], make_terrain(), water_doc, TYPES, {"event_spawn": {"bigfoot": rule}})


def test_points_lie_inside_the_layer_the_terrain_and_clear_of_buildings_and_water() -> None:
    zone = box(30, 30, 150, 120)
    building = hut(box(60, 55, 75, 70))
    lake = box(100, 80, 130, 110)
    doc, warnings = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], zone)]}, [building], [lake])
    assert warnings == []
    (entry,) = doc["attributes"]
    assert (entry["attribute"], entry["value"], entry["areas"]) == ("event_spawn", "bigfoot", ["L-0001"])
    pts = entry["points"]
    assert len(pts) == 50  # the usable ground is far above 50 x 100 m2
    footprint = footprint_polygon(building["footprint"])
    assert all(shapely.contains_xy(zone, x, y) for x, y, _ in pts)
    assert all(footprint.distance(Point(x, y)) >= 10 - 1e-6 for x, y, _ in pts)
    assert all(lake.distance(Point(x, y)) >= 5 - 1e-6 for x, y, _ in pts)
    terrain = make_terrain()
    assert all(shapely.contains_xy(terrain.coverage, x, y) for x, y, _ in pts)
    heights = terrain.surface(np.array([p[0] for p in pts]), np.array([p[1] for p in pts]))
    assert [p[2] for p in pts] == pytest.approx(list(heights), abs=1e-3)  # z is the terrain height there
    assert entry["area_m2"] == pytest.approx(
        zone.difference(footprint.buffer(10)).difference(lake.buffer(5)).intersection(terrain.coverage).area, rel=0.01
    )


def test_a_layer_reaching_outside_the_terrain_gets_no_points_there() -> None:
    doc, _ = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(-200, -200, 400, 400))]})
    assert inside(doc["attributes"][0]["points"], make_terrain().coverage)


def test_the_areas_of_every_carrier_are_pooled_across_stacked_types() -> None:
    a, b = box(20, 120, 60, 160), box(140, 120, 190, 160)
    doc, _ = bake({"ground": [ground("A-0001", "bigfoot_forest", a)], "ground_layers": [layer("L-0001", ["bigfoot_zone"], b)]})
    (entry,) = doc["attributes"]
    assert entry["areas"] == ["A-0001", "L-0001"]
    pts = entry["points"]
    assert inside(pts, a.union(b))
    assert any(shapely.contains_xy(a, x, y) for x, y, _ in pts) and any(shapely.contains_xy(b, x, y) for x, y, _ in pts)


def test_overlapping_areas_are_not_counted_twice() -> None:
    zone = box(30, 120, 130, 180)  # 6000 m2: 60 by density, capped at 50 (RULE max)
    rule = {**RULE, "max_points": 500}
    one, _ = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], zone)]}, rule=rule)
    two, _ = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], zone), layer("L-0002", ["bigfoot_zone"], zone)]}, rule=rule)
    assert len(one["attributes"][0]["points"]) == len(two["attributes"][0]["points"]) == 60


def test_the_point_count_follows_the_density_rule() -> None:
    small, _ = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(30, 120, 40, 130))]})  # 100 m2: 1 by density
    big, _ = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(30, 120, 80, 150))]}, rule={**RULE, "max_points": 500})  # 1500 m2
    assert len(small["attributes"][0]["points"]) == RULE["min_points"]
    assert len(big["attributes"][0]["points"]) == 15


def test_the_same_input_gives_the_same_points() -> None:
    doc = {"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(30, 120, 150, 180))]}
    assert bake(doc)[0] == bake(doc)[0]
    other = {"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(30, 121, 150, 180))]}
    assert bake(doc)[0]["attributes"][0]["points"] != bake(other)[0]["attributes"][0]["points"]


def test_an_attribute_with_no_areas_is_not_in_the_file() -> None:
    assert bake({"ground": [ground("A-0001", "forest", box(0, 0, 50, 50))]}) == (None, [])
    assert bake({}) == (None, [])


def test_an_area_with_no_usable_ground_is_listed_with_no_points_and_a_warning() -> None:
    lake = box(20, 120, 80, 180)
    doc, warnings = bake({"ground_layers": [layer("L-0001", ["bigfoot_zone"], box(40, 140, 60, 160))]}, water=[lake])
    assert doc["attributes"][0]["points"] == []  # the layer lies inside the lake
    assert doc["attributes"][0]["areas"] == ["L-0001"]
    assert len(warnings) == 1 and "event_spawn: bigfoot" in warnings[0]


def test_an_attribute_off_the_allowlist_is_not_exported() -> None:
    types = {**TYPES, "spawny": {"attributes": {"spawns": True}}}
    doc = {"ground_layers": [layer("L-0001", ["spawny", "bigfoot_zone"], box(30, 120, 150, 180))]}
    got, _ = build_attribute_points(doc, [], make_terrain(), {"bodies": []}, types, {})
    assert got is None  # an empty allowlist exports nothing, whatever the areas carry
    got, _ = build_attribute_points(doc, [], make_terrain(), {"bodies": []}, types, SECTION)
    assert [e["attribute"] for e in got["attributes"]] == ["event_spawn"]  # and `spawns` is not there


# --- the export stage ---------------------------------------------------------------------------


def write_world(tmp_path, layers: list[dict] | None, with_water: bool = True) -> dict:
    ws = make_workspace(tmp_path)
    doc = json.loads((ws["work"] / "parcels.json").read_text())
    doc["ground"] = [ground("A-0001", "water", box(100, 120, 130, 150))] if with_water else []
    if layers is not None:
        doc["ground_layers"] = layers
    (ws["work"] / "parcels.json").write_text(json.dumps(doc))
    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"version": 1, "ground_types": {"bigfoot_zone": BIGFOOT}}))
    ws["edits"] = edits
    return ws


def export(ws, out) -> dict:
    return run_export(ws["work"], out, ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])


def test_export_writes_the_points_and_lists_them_in_the_manifest(tmp_path) -> None:
    zone = box(40, 110, 200, 190)
    ws = write_world(tmp_path, [layer("L-0001", ["bigfoot_zone"], zone)])
    manifest = export(ws, ws["out"])
    assert manifest["files"]["attribute_points"] == "attribute_points.json"
    doc = json.loads((ws["out"] / "attribute_points.json").read_text())
    assert doc["version"] == 1
    (entry,) = doc["attributes"]
    assert (entry["attribute"], entry["value"], entry["areas"]) == ("event_spawn", "bigfoot", ["L-0001"])
    rule = load_section("runtime_points")["event_spawn"]["bigfoot"]
    assert rule["min_points"] <= len(entry["points"]) <= rule["max_points"]
    lake = box(100, 120, 130, 150)
    assert all(shapely.contains_xy(zone, x, y) for x, y, _ in entry["points"])
    assert all(lake.distance(Point(x, y)) >= rule["water_clearance_m"] - 1e-6 for x, y, _ in entry["points"])  # the water of the same build
    assert all(len(p) == 3 for p in entry["points"])


def test_export_is_byte_deterministic_and_the_points_follow_the_edits(tmp_path) -> None:
    layers = [layer("L-0001", ["bigfoot_zone"], box(40, 110, 200, 190))]
    ws = write_world(tmp_path, layers)
    export(ws, tmp_path / "a")
    export(ws, tmp_path / "b")
    assert (tmp_path / "a" / "attribute_points.json").read_bytes() == (tmp_path / "b" / "attribute_points.json").read_bytes()
    assert (tmp_path / "a" / "manifest.json").read_bytes() == (tmp_path / "b" / "manifest.json").read_bytes()


def test_a_map_without_an_area_that_carries_one_has_no_file_and_a_stale_one_goes(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    out = ws["out"]
    out.mkdir()
    (out / "attribute_points.json").write_text("{}")  # from an earlier build in which an area carried it
    manifest = export(ws, out)
    assert "attribute_points" not in manifest["files"]
    assert not (out / "attribute_points.json").exists()


def test_the_export_refuses_a_broken_allowlist(tmp_path) -> None:
    ws = write_world(tmp_path, [layer("L-0001", ["bigfoot_zone"], box(40, 110, 200, 190))])
    rules = tmp_path / "rules.yaml"
    rules.write_text("runtime_points:\n  spawns: {x: {}}\n")
    with pytest.raises(AttributePointsError):
        run_export(ws["work"], tmp_path / "o", ws["raw"], rules, ws["overrides"], edits_path=ws["edits"])

