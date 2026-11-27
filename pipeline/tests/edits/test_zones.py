"""Zones (ADR 0022): the catalog of kinds and the candidate points for the server, baked into zone_points.json.

The areas of a kind are the ground areas its rule zone (``where_ground``) names and the drawn zones of the edits; the points
lie inside their union, inside the terrain, clear of buildings and water."""

import json
import random

import numpy as np
import pytest
import shapely
from shapely.geometry import Point, box, mapping

from drycastle_pipeline.buildings.derive import derive_building, load_building_rules
from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.edits.zones import (
    FLOCKS,
    KIND_NAMES,
    KINDS,
    PLAYER_SPAWN,
    RULE_KEYS,
    WILDLIFE,
    ZonesError,
    build_zone_points,
    build_zone_polygons,
    check_ground_types,
    drawn_zones,
    kind_areas,
    kind_rows,
    point_count,
    rule_parts,
    rules,
    sample_points,
    zone_cuts,
)
from drycastle_pipeline.export.export import run_export
from tests.synthetic import make_terrain, make_workspace

RULE = {"m2_per_point": 100, "min_points": 10, "max_points": 50, "building_clearance_m": 10, "water_clearance_m": 5}
SECTION = {"bigfoot": RULE}
ORIGIN = (0.0, 0.0)


def ground(gid: str, kind: str, geom) -> dict:
    return {"id": gid, "kind": kind, "polygon": mapping(geom), "area": geom.area}


def zone(zid: str, kind: str, geom) -> dict:
    """An entry of edits.json ``zones`` for a box in a frame whose origin is (0, 0)."""
    return {zid: {"kind": kind, "polygon": [list(c) for c in geom.exterior.coords][:-1]}}


def drawn(*entries: dict) -> dict:
    merged: dict = {}
    for e in entries:
        merged.update(e)
    return drawn_zones(merged, ORIGIN)


def hut(geom) -> dict:
    return derive_building("w9", "P-0001", geom, {}, load_building_rules())


def inside(points: list, geom) -> bool:
    return all(shapely.contains_xy(geom, p[0], p[1]) for p in points)


# --- the catalog and the rules -----------------------------------------------------------------------


def test_the_catalog_is_the_event_kinds_the_player_kinds_and_every_species() -> None:
    assert KIND_NAMES == ("bigfoot", "scud", "player_spawn", "plantation", "cartel_drop", "cartel_hit", *WILDLIFE, *FLOCKS)
    assert {k.group for k in KINDS} == {"event", "player", "cartel", "wildlife"}
    assert all(k.label and k.tint.startswith("#") for k in KINDS)


def test_wildlife_species_are_species_of_the_game_data() -> None:
    """zones.WILDLIFE must name species of godot/shooter/data/animals.json (the server spawns them by id)."""
    import re

    from drycastle_pipeline.core.config import REPO_ROOT

    text = (REPO_ROOT / "godot" / "shooter" / "data" / "animals.json").read_text()
    ids = set(re.findall(r'"id"\s*:\s*"([^"]+)"', re.sub(r"(?m)^\s*//.*$", "", text)))
    assert [w for w in WILDLIFE if w not in ids] == []


def test_the_shipped_rules_cover_the_catalog_with_rule_zones() -> None:
    from drycastle_pipeline.ground.areas import rules_ground_types

    section = rules(load_section("zones"))
    assert list(section) == list(KIND_NAMES)
    assert len({json.dumps({k: v for k, v in r.items() if k != "where_ground"}, sort_keys=True) for k, r in section.items() if k in (*WILDLIFE, *FLOCKS)}) == 1  # one shared density rule
    types = rules_ground_types()
    check_ground_types(section, types)
    assert "where_ground" not in section["bigfoot"]  # drawn only
    assert "where_ground" not in section["scud"]  # drawn only; the server falls back to open ground when none is drawn
    assert section["scud"]["building_clearance_m"] >= 12  # a 12 m truck needs room
    for cartel in ("cartel_drop", "cartel_hit"):  # drawn only, exported as points (the Job picks one)
        assert "where_ground" not in section[cartel] and set(section[cartel]) == set(RULE_KEYS)
    assert section["cartel_drop"]["building_clearance_m"] >= 2.4 / 2  # the Cartel van stops at a drop point
    assert set(section["player_spawn"]["where_ground"]) == {"allotments", "orchard", "meadow", "village", "field"}
    assert section["stag"]["where_ground"] == ["forest"]
    assert set(section["deer"]["where_ground"]) == {"forest", "meadow", "field"}
    assert set(section["crow"]["where_ground"]) == {"forest", "meadow", "field"}
    assert all("water" not in r.get("where_ground", []) for r in section.values())


def test_kind_rows_are_the_catalog_in_order_with_the_rule_zone() -> None:
    rows = kind_rows(rules(load_section("zones")))
    assert [r["name"] for r in rows] == list(KIND_NAMES)
    by = {r["name"]: r for r in rows}
    assert by["bigfoot"]["where_ground"] is None and by["bigfoot"]["group"] == "event"
    assert by["scud"]["where_ground"] is None and by["scud"]["group"] == "event" and by["scud"]["label"] == "Scud truck"
    assert by["stag"]["where_ground"] == ["forest"] and by["stag"]["group"] == "wildlife"
    assert set(by["player_spawn"]) == {"name", "label", "group", "tint", "where_ground"}
    assert by["plantation"]["group"] == "player" and by["plantation"]["where_ground"] is None
    assert by["cartel_drop"]["group"] == by["cartel_hit"]["group"] == "cartel" and by["cartel_drop"]["where_ground"] is None


@pytest.mark.parametrize(
    "section, message",
    [
        ({"yeti": RULE}, "not a zone kind"),
        ({"bigfoot": {**RULE, "m2_per_point": 0}}, "m2_per_point"),
        ({"bigfoot": {**RULE, "min_points": 9, "max_points": 3}}, "min_points"),
        ({"bigfoot": {**RULE, "water_clearance_m": -1}}, "water_clearance_m"),
        ({"bigfoot": {k: v for k, v in RULE.items() if k != "min_points"}}, "exactly the keys"),
        ({"bigfoot": {**RULE, "min_tree_distance_m": 2}}, "exactly the keys"),  # no clearance from trees
        ({"bigfoot": {**RULE, "where_ground": []}}, "where_ground"),
        ({"bigfoot": {**RULE, "where_ground": ["forest", "forest"]}}, "where_ground"),
        ({"player_spawn": {**RULE, "where_ground": ["field"]}}, "exactly the keys"),  # no points: no density keys
        ({"plantation": RULE}, "exactly the keys"),  # polygons, not points: no density keys
        ({"bigfoot": 5}, "expected an object"),
    ],
)
def test_wrong_zone_rules_are_an_error_not_a_skip(section: dict, message: str) -> None:
    with pytest.raises(ZonesError, match=message):
        rules(section)


def test_a_rule_zone_must_name_ground_types_that_exist() -> None:
    section = rules({"bigfoot": {**RULE, "where_ground": ["forest", "swamp"]}})
    with pytest.raises(ZonesError, match="swamp"):
        check_ground_types(section, {"forest": {}})
    check_ground_types(section, {"forest": {}, "swamp": {}})


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


# --- which areas make a zone ------------------------------------------------------------------------


def test_drawn_zones_and_the_ground_of_a_rule_zone_are_the_areas_of_a_kind() -> None:
    doc = {
        "ground": [
            ground("A-0001", "forest", box(0, 0, 10, 10)),
            ground("A-0002", "meadow", box(20, 0, 30, 10)),
        ]
    }
    d = drawn(zone("Z-0002", "bigfoot", box(40, 0, 50, 10)), zone("Z-0001", "deer", box(60, 0, 70, 10)))
    got = kind_areas("deer", doc, d, ["forest"])
    assert [aid for aid, _ in got] == ["A-0001", "Z-0001"]  # the forest by rule, the drawn deer zone; sorted by id
    assert [aid for aid, _ in kind_areas("bigfoot", doc, d, None)] == ["Z-0002"]  # no rule zone: only what is drawn
    assert kind_areas("wolf", doc, d, None) == []
    assert kind_areas("wolf", {}, {}, ["forest"]) == []


def test_a_drawn_zone_is_given_in_the_absolute_frame() -> None:
    d = drawn_zones({"Z-0001": {"kind": "bigfoot", "polygon": [[1010, 2010], [1030, 2010], [1030, 2030], [1010, 2030]]}}, (1000, 2000))
    kind, poly = d["Z-0001"]
    assert kind == "bigfoot" and poly.equals(box(10, 10, 30, 30))


def test_player_spawn_zones_are_the_rule_areas_and_the_drawn_zones() -> None:
    doc = {"ground": [ground("A-0001", "forest", box(0, 0, 100, 100)), ground("A-0002", "village", box(200, 0, 300, 100))]}
    d = drawn(zone("Z-0001", PLAYER_SPAWN, box(50, 0, 150, 100)), zone("Z-0002", "deer", box(0, 0, 100, 100)))
    got = kind_areas(PLAYER_SPAWN, doc, d, ["village"])
    assert [(i, g.equals(r)) for (i, g), r in zip(got, [box(200, 0, 300, 100), box(50, 0, 150, 100)], strict=True)] == [("A-0002", True), ("Z-0001", True)]
    assert kind_areas(PLAYER_SPAWN, doc, drawn(zone("Z-0002", "deer", box(0, 0, 100, 100))), None) == []


def test_a_zone_over_forest_does_not_change_the_ground() -> None:
    """Zones are an overlay: the ground areas are the input and stay what they are."""
    doc = {"ground": [ground("A-0001", "forest", box(0, 0, 100, 100))]}
    before = json.dumps(doc, sort_keys=True)
    kind_areas("bigfoot", doc, drawn(zone("Z-0001", "bigfoot", box(20, 20, 60, 60))), None)
    kind_areas(PLAYER_SPAWN, doc, drawn(zone("Z-0001", PLAYER_SPAWN, box(20, 20, 60, 60))), None)
    assert json.dumps(doc, sort_keys=True) == before


def test_wildlife_species_are_baked_per_species() -> None:
    doc = {"ground": [ground("A-0001", "meadow", box(0, 0, 40, 40)), ground("A-0002", "wood", box(50, 0, 90, 40))]}
    section = rules({
        "deer": {**RULE, "where_ground": ["meadow"]},
        "fox": {**RULE, "where_ground": ["meadow", "wood"]},
        "bear": {**RULE, "where_ground": ["wood"]},
        "wolf": {**RULE, "where_ground": ["swamp"]},
    })
    out, _ = build_zone_points(doc, {}, [], make_terrain(), {"bodies": []}, section)
    got = {e["kind"]: e["areas"] for e in out["zones"]}
    assert got == {"deer": ["A-0001"], "bear": ["A-0002"], "fox": ["A-0001", "A-0002"]}  # wolf: no area, no entry; catalog order


# --- cuts: a rule zone that was moved, retyped or deleted ------------------------------------------


def cuts_of(kind: str, geom) -> dict:
    return zone_cuts({"ZC-0001": {"kind": kind, "polygon": [list(c) for c in geom.exterior.coords][:-1]}}, ORIGIN)


def test_a_cut_takes_its_area_out_of_the_rule_zone_of_its_kind_only() -> None:
    doc = {"ground": [ground("A-0001", "forest", box(0, 0, 10, 10)), ground("A-0002", "forest", box(20, 0, 30, 10))]}
    cuts = cuts_of("deer", box(0, 0, 10, 10))
    assert [aid for aid, _ in kind_areas("deer", doc, {}, ["forest"], cuts)] == ["A-0002"]
    assert [aid for aid, _ in kind_areas("boar", doc, {}, ["forest"], cuts)] == ["A-0001", "A-0002"]  # another kind: untouched
    assert [aid for aid, _ in kind_areas("deer", doc, {}, ["forest"], {})] == ["A-0001", "A-0002"]


def test_a_cut_is_geometry_so_a_rebuilt_area_leaves_no_sliver() -> None:
    cuts = cuts_of("deer", box(0, 0, 10, 10))  # cut when the area was 10 x 10
    rebuilt = {"ground": [ground("A-0777", "forest", box(0.2, -0.3, 10.4, 10.3))]}  # new id, slightly different
    assert kind_areas("deer", rebuilt, {}, ["forest"], cuts) == []
    bigger = {"ground": [ground("A-0778", "forest", box(0, 0, 20, 10))]}  # a real change: the rest stays
    (_, left), = kind_areas("deer", bigger, {}, ["forest"], cuts)
    assert left.area == pytest.approx(9.5 * 10, abs=0.5)  # x from 10.5 (the cut is grown by 0.5 m)


def test_a_cut_never_touches_a_drawn_zone() -> None:
    """The detached copy of a moved rule zone is a drawn zone over the very polygon that was cut."""
    doc = {"ground": [ground("A-0001", "forest", box(0, 0, 10, 10))]}
    d = drawn(zone("Z-0001", "deer", box(0, 0, 10, 10)))
    assert [aid for aid, _ in kind_areas("deer", doc, d, ["forest"], cuts_of("deer", box(0, 0, 10, 10)))] == ["Z-0001"]


def test_rule_parts_are_the_outlines_of_an_area() -> None:
    one = ground("A-0001", "forest", box(0, 0, 10, 10))
    assert [i for i, _ in rule_parts(one)] == ["A-0001"]
    two = ground("A-0002", "forest", box(0, 0, 10, 10).union(box(20, 0, 30, 10)))
    assert [i for i, _ in rule_parts(two)] == ["A-0002.1", "A-0002.2"]
    holed = ground("A-0003", "forest", box(0, 0, 10, 10).difference(box(4, 4, 6, 6)))
    assert rule_parts(holed)[0][1].equals(box(0, 0, 10, 10))  # holes are not part of the outline


def test_untouched_zone_points_are_the_same_with_cuts_of_other_kinds_and_places() -> None:
    doc = {"ground": [ground("A-0001", "bigfoot_forest", box(20, 120, 60, 160))]}
    section = rules({"bigfoot": {**RULE, "where_ground": ["bigfoot_forest"]}})
    args = (doc, drawn(zone("Z-0001", "bigfoot", box(140, 120, 190, 160))), [], make_terrain(), {"bodies": []}, section)
    base = build_zone_points(*args)
    assert build_zone_points(*args, {}) == base
    assert build_zone_points(*args, cuts_of("deer", box(20, 120, 60, 160))) == base  # another kind
    assert build_zone_points(*args, cuts_of("bigfoot", box(300, 300, 310, 310))) == base  # elsewhere
    cut, _ = build_zone_points(*args, cuts_of("bigfoot", box(20, 120, 60, 160)))
    assert cut["zones"][0]["areas"] == ["Z-0001"]


def test_player_spawn_rule_areas_lose_the_cut_and_a_moved_one_comes_back_through_its_drawn_zone() -> None:
    doc = {"ground": [ground("A-0001", "village", box(0, 0, 100, 100)), ground("A-0002", "village", box(200, 0, 300, 100))]}
    cuts = cuts_of(PLAYER_SPAWN, box(0, 0, 100, 100))
    assert [i for i, _ in kind_areas(PLAYER_SPAWN, doc, {}, ["village"], cuts)] == ["A-0002"]  # A-0001 cut away completely
    moved = drawn(zone("Z-0001", PLAYER_SPAWN, box(50, 0, 150, 100)))
    assert [i for i, _ in kind_areas(PLAYER_SPAWN, doc, moved, ["village"], cuts)] == ["A-0002", "Z-0001"]


# --- the points ---------------------------------------------------------------------------------


def bake(d: dict, buildings: list[dict] | None = None, water: list | None = None, rule: dict = RULE, doc: dict | None = None):
    water_doc = {"bodies": [{"id": "W", "polygon": mapping(g)} for g in (water or [])]}
    return build_zone_points(doc or {}, d, buildings or [], make_terrain(), water_doc, rules({"bigfoot": rule}))


def test_points_lie_inside_the_zone_the_terrain_and_clear_of_buildings_and_water() -> None:
    area = box(30, 30, 150, 120)
    building = hut(box(60, 55, 75, 70))
    lake = box(100, 80, 130, 110)
    doc, warnings = bake(drawn(zone("Z-0001", "bigfoot", area)), [building], [lake])
    assert warnings == []
    (entry,) = doc["zones"]
    assert (entry["kind"], entry["areas"]) == ("bigfoot", ["Z-0001"])
    assert doc["version"] == 2
    pts = entry["points"]
    assert len(pts) == 50  # the usable ground is far above 50 x 100 m2
    footprint = footprint_polygon(building["footprint"])
    assert all(shapely.contains_xy(area, x, y) for x, y, _ in pts)
    assert all(footprint.distance(Point(x, y)) >= 10 - 1e-6 for x, y, _ in pts)
    assert all(lake.distance(Point(x, y)) >= 5 - 1e-6 for x, y, _ in pts)
    terrain = make_terrain()
    assert all(shapely.contains_xy(terrain.coverage, x, y) for x, y, _ in pts)
    heights = terrain.surface(np.array([p[0] for p in pts]), np.array([p[1] for p in pts]))
    assert [p[2] for p in pts] == pytest.approx(list(heights), abs=1e-3)  # z is the terrain height there
    assert entry["area_m2"] == pytest.approx(
        area.difference(footprint.buffer(10)).difference(lake.buffer(5)).intersection(terrain.coverage).area, rel=0.01
    )


def test_a_zone_reaching_outside_the_terrain_gets_no_points_there() -> None:
    doc, _ = bake(drawn(zone("Z-0001", "bigfoot", box(-200, -200, 400, 400))))
    assert inside(doc["zones"][0]["points"], make_terrain().coverage)


def test_drawn_zones_and_rule_zone_ground_are_pooled() -> None:
    a, b = box(20, 120, 60, 160), box(140, 120, 190, 160)
    world = {"ground": [ground("A-0001", "bigfoot_forest", a)]}
    section = rules({"bigfoot": {**RULE, "where_ground": ["bigfoot_forest"]}})
    out, _ = build_zone_points(world, drawn(zone("Z-0001", "bigfoot", b)), [], make_terrain(), {"bodies": []}, section)
    (entry,) = out["zones"]
    assert entry["areas"] == ["A-0001", "Z-0001"]
    pts = entry["points"]
    assert inside(pts, a.union(b))
    assert any(shapely.contains_xy(a, x, y) for x, y, _ in pts) and any(shapely.contains_xy(b, x, y) for x, y, _ in pts)


def test_overlapping_zones_are_not_counted_twice() -> None:
    area = box(30, 120, 130, 180)  # 6000 m2: 60 by density
    rule = {**RULE, "max_points": 500}
    one, _ = bake(drawn(zone("Z-0001", "bigfoot", area)), rule=rule)
    two, _ = bake(drawn(zone("Z-0001", "bigfoot", area), zone("Z-0002", "bigfoot", area)), rule=rule)
    assert len(one["zones"][0]["points"]) == len(two["zones"][0]["points"]) == 60


def test_the_point_count_follows_the_density_rule() -> None:
    small, _ = bake(drawn(zone("Z-0001", "bigfoot", box(30, 120, 40, 130))))  # 100 m2: 1 by density
    big, _ = bake(drawn(zone("Z-0001", "bigfoot", box(30, 120, 80, 150))), rule={**RULE, "max_points": 500})  # 1500 m2
    assert len(small["zones"][0]["points"]) == RULE["min_points"]
    assert len(big["zones"][0]["points"]) == 15


def test_the_same_input_gives_the_same_points() -> None:
    d = drawn(zone("Z-0001", "bigfoot", box(30, 120, 150, 180)))
    assert bake(d)[0] == bake(d)[0]
    other = drawn(zone("Z-0001", "bigfoot", box(30, 121, 150, 180)))
    assert bake(d)[0]["zones"][0]["points"] != bake(other)[0]["zones"][0]["points"]


def test_a_kind_with_no_zones_is_not_in_the_file() -> None:
    assert bake({}, doc={"ground": [ground("A-0001", "forest", box(0, 0, 50, 50))]}) == (None, [])
    assert bake({}) == (None, [])


def test_a_zone_with_no_usable_ground_is_listed_with_no_points_and_a_warning() -> None:
    lake = box(20, 120, 80, 180)
    doc, warnings = bake(drawn(zone("Z-0001", "bigfoot", box(40, 140, 60, 160))), water=[lake])
    assert doc["zones"][0]["points"] == []  # the zone lies inside the lake
    assert doc["zones"][0]["areas"] == ["Z-0001"]
    assert len(warnings) == 1 and "bigfoot" in warnings[0]


def test_player_spawn_has_no_points_and_a_kind_without_a_rule_is_warned_about() -> None:
    d = drawn(zone("Z-0001", PLAYER_SPAWN, box(30, 120, 150, 180)), zone("Z-0002", "deer", box(30, 120, 150, 180)))
    got, warnings = build_zone_points({}, d, [], make_terrain(), {"bodies": []}, rules({"player_spawn": {}, "bigfoot": RULE}))
    assert got is None  # no bigfoot zone, spawn is not points, deer has no rule here
    assert any("Z-0002" in w and "deer" in w for w in warnings)


def test_the_cartel_kinds_bake_points_with_the_shipped_rules() -> None:
    """A drawn Drop point and Hit spot zone each give candidate points of their own kind (the Job picks one, CartelMap.cs)."""
    yard, field = box(30, 30, 60, 50), box(100, 30, 160, 90)
    d = drawn(zone("Z-0001", "cartel_drop", yard), zone("Z-0002", "cartel_hit", field))
    out, warnings = build_zone_points({}, d, [], make_terrain(), {"bodies": []}, rules(load_section("zones")))
    assert warnings == []
    by = {e["kind"]: e for e in out["zones"]}
    assert set(by) == {"cartel_drop", "cartel_hit"} and by["cartel_drop"]["areas"] == ["Z-0001"]
    assert len(by["cartel_drop"]["points"]) == 4  # 600 m2 at 150 m2 a point
    assert inside(by["cartel_drop"]["points"], yard) and inside(by["cartel_hit"]["points"], field)


# --- the export stage ---------------------------------------------------------------------------


def write_world(tmp_path, zones: dict | None, with_water: bool = True) -> dict:
    ws = make_workspace(tmp_path)
    doc = json.loads((ws["work"] / "parcels.json").read_text())
    doc["ground"] = [ground("A-0001", "water", box(100, 120, 130, 150))] if with_water else []
    (ws["work"] / "parcels.json").write_text(json.dumps(doc))
    ws["origin"] = doc["origin_epsg5514"]
    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"version": 1, **({"zones": zones} if zones else {})}))
    ws["edits"] = edits
    return ws


def absolute(ws, geom) -> list[list[float]]:
    ox, oy = ws["origin"]
    return [[x + ox, y + oy] for x, y in list(geom.exterior.coords)[:-1]]


def export(ws, out) -> dict:
    return run_export(ws["work"], out, ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])


def test_export_writes_the_points_and_lists_them_in_the_manifest(tmp_path) -> None:
    area = box(40, 110, 200, 190)
    ws = write_world(tmp_path, None)
    ws["edits"].write_text(json.dumps({"version": 1, "zones": {"Z-0001": {"kind": "bigfoot", "polygon": absolute(ws, area)}}}))
    manifest = export(ws, ws["out"])
    assert manifest["files"]["zone_points"] == "zone_points.json"
    doc = json.loads((ws["out"] / "zone_points.json").read_text())
    assert doc["version"] == 2
    (entry,) = doc["zones"]
    assert (entry["kind"], entry["areas"]) == ("bigfoot", ["Z-0001"])
    rule = load_section("zones")["bigfoot"]
    assert rule["min_points"] <= len(entry["points"]) <= rule["max_points"]
    lake = box(100, 120, 130, 150)
    assert all(shapely.contains_xy(area, x, y) for x, y, _ in entry["points"])
    assert all(lake.distance(Point(x, y)) >= rule["water_clearance_m"] - 1e-6 for x, y, _ in entry["points"])  # the water of the same build
    assert all(len(p) == 3 for p in entry["points"])


def test_export_lists_the_zone_kinds_for_the_editor_and_nothing_of_the_drawn_zones(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    export(ws, ws["out"])
    parcels = json.loads((ws["out"] / "parcels.json").read_text())
    assert [k["name"] for k in parcels["zone_kinds"]] == list(KIND_NAMES)
    assert "zones" not in parcels and "ground_layers" not in parcels
    assert all("spawns" not in t and "wildlife" not in t for t in parcels["ground_types"])


def test_export_is_byte_deterministic(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    ws["edits"].write_text(json.dumps({"version": 1, "zones": {"Z-0001": {"kind": "bigfoot", "polygon": absolute(ws, box(40, 110, 200, 190))}}}))
    export(ws, tmp_path / "a")
    export(ws, tmp_path / "b")
    assert (tmp_path / "a" / "zone_points.json").read_bytes() == (tmp_path / "b" / "zone_points.json").read_bytes()
    assert (tmp_path / "a" / "manifest.json").read_bytes() == (tmp_path / "b" / "manifest.json").read_bytes()


def test_a_map_without_a_zone_has_no_file_and_a_stale_one_goes(tmp_path) -> None:
    ws = write_world(tmp_path, None, with_water=False)
    out = ws["out"]
    out.mkdir()
    (out / "zone_points.json").write_text("{}")  # from an earlier build in which a zone had an area
    manifest = export(ws, out)
    assert "zone_points" not in manifest["files"]
    assert not (out / "zone_points.json").exists()


def test_the_export_refuses_a_broken_zones_section(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    rules_file = tmp_path / "rules.yaml"
    rules_file.write_text("zones:\n  yeti: {m2_per_point: 1}\n")
    with pytest.raises(ZonesError):
        run_export(ws["work"], tmp_path / "o", ws["raw"], rules_file, ws["overrides"], edits_path=ws["edits"])


def test_the_export_refuses_a_rule_zone_of_an_unknown_ground_type(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    rules_file = tmp_path / "rules.yaml"
    rules_file.write_text("zones:\n  fox:\n    where_ground: [swamp]\n")
    with pytest.raises(ZonesError, match="swamp"):
        run_export(ws["work"], tmp_path / "o", ws["raw"], rules_file, ws["overrides"], edits_path=ws["edits"])


def test_flock_kinds_are_the_flocks_of_the_wildlife_data() -> None:
    """zones.FLOCKS must name the flock kinds of godot/shooter/data/wildlife.json (the server starts a flock at its points)."""
    import re

    from drycastle_pipeline.core.config import REPO_ROOT

    text = (REPO_ROOT / "godot" / "shooter" / "data" / "wildlife.json").read_text()
    data = json.loads(re.sub(r"(?m)^\s*//.*$", "", text))
    assert sorted(f["id"] for f in data["flocks"]) == sorted(FLOCKS)


def test_ground_areas_are_clipped_to_the_terrain() -> None:
    from shapely.geometry import box

    from drycastle_pipeline.ground.areas import clip_areas

    def area(aid: str, g) -> dict:
        return {"id": aid, "kind": "field", "area": g.area, "polygon": g.__geo_interface__}

    inside, straddle, outside = area("A-0001", box(10, 10, 50, 50)), area("A-0002", box(80, 10, 180, 50)), area("A-0003", box(300, 0, 400, 50))
    out = clip_areas([inside, straddle, outside], box(0, 0, 100, 100))
    assert [a["id"] for a in out] == ["A-0001", "A-0002"]
    assert out[0] is inside  # untouched
    assert out[1]["area"] == 800.0 and max(c[0] for c in out[1]["polygon"]["coordinates"][0]) == 100.0


# --- the polygons of the plantation (zone_polygons.json) --------------------------------------------------


def test_the_shipped_rules_have_a_plantation_with_no_numbers_and_no_rule_zone() -> None:
    assert rules(load_section("zones"))["plantation"] == {}


def test_a_plantation_has_no_points_but_its_polygons_in_game_axes() -> None:
    d = drawn(zone("Z-0001", "plantation", box(30, 120, 150, 180)), zone("Z-0002", "deer", box(0, 0, 10, 10)))
    section = rules({"plantation": {}, "bigfoot": RULE})
    points, _ = build_zone_points({}, d, [], make_terrain(), {"bodies": []}, section)
    assert points is None  # no kind that has a zone has points
    doc = build_zone_polygons({}, d, section)
    assert doc is not None and doc["version"] == 1
    (entry,) = doc["zones"]
    assert (entry["kind"], entry["areas"]) == ("plantation", ["Z-0001"])
    (ring,) = entry["rings"]
    # x stays, z = -north: the box 30..150 east, 120..180 north is x 30..150, z -180..-120
    assert sorted({tuple(p) for p in ring}) == [(30, -180), (30, -120), (150, -180), (150, -120)]


def test_no_plantation_zone_means_no_polygons() -> None:
    section = rules({"plantation": {}})
    assert build_zone_polygons({}, drawn(zone("Z-0001", "deer", box(0, 0, 10, 10))), section) is None
    assert build_zone_polygons({}, {}, section) is None
    assert build_zone_polygons({}, drawn(zone("Z-0001", "plantation", box(0, 0, 10, 10))), rules({"bigfoot": RULE})) is None  # no rule for the kind


def test_a_cut_takes_its_part_out_of_the_polygons_of_a_rule_zone() -> None:
    doc = {"ground": [ground("A-0001", "field", box(0, 0, 100, 100))]}
    section = rules({"plantation": {"where_ground": ["field"]}})
    whole = build_zone_polygons(doc, {}, section)
    assert whole is not None and whole["zones"][0]["areas"] == ["A-0001"]
    cut = zone_cuts({"ZC-0001": {"kind": "plantation", "polygon": [list(c) for c in box(0, 0, 100, 100).exterior.coords][:-1]}}, ORIGIN)
    assert build_zone_polygons(doc, {}, section, cut) is None  # all cut away


def test_export_writes_the_plantation_polygons_and_lists_them_in_the_manifest(tmp_path) -> None:
    area = box(40, 110, 200, 190)
    ws = write_world(tmp_path, None)
    ws["edits"].write_text(json.dumps({"version": 1, "zones": {"Z-0001": {"kind": "plantation", "polygon": absolute(ws, area)}}}))
    manifest = export(ws, ws["out"])
    assert manifest["files"]["zone_polygons"] == "zone_polygons.json"
    assert "zone_points" not in manifest["files"]  # the kind has no points
    doc = json.loads((ws["out"] / "zone_polygons.json").read_text())
    (entry,) = doc["zones"]
    assert (entry["kind"], entry["areas"]) == ("plantation", ["Z-0001"])
    (ring,) = entry["rings"]
    xs, zs = [p[0] for p in ring], [p[1] for p in ring]
    assert (min(xs), max(xs)) == pytest.approx((40, 200), abs=0.01) and (min(zs), max(zs)) == pytest.approx((-190, -110), abs=0.01)


def test_a_map_without_a_plantation_has_no_polygon_file_and_a_stale_one_goes(tmp_path) -> None:
    ws = write_world(tmp_path, None)
    out = ws["out"]
    out.mkdir()
    (out / "zone_polygons.json").write_text("{}")
    manifest = export(ws, out)
    assert "zone_polygons" not in manifest["files"]
    assert not (out / "zone_polygons.json").exists()
