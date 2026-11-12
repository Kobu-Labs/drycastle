"""Street lamps: OSM street_lamp nodes, rule lamps along roads, lamps.json and its export."""

import json
from itertools import pairwise

import pytest
import yaml
from shapely.geometry import Point, shape
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.export import run_export
from drycastle_pipeline.lamps import make_lamps
from drycastle_pipeline.normalise import parse_osm
from drycastle_pipeline.props import run_props
from drycastle_pipeline.roads import edge_lines
from drycastle_pipeline.rules import load_section
from tests.synthetic import make_terrain, make_workspace

LAMPS = load_section("lamps")


@pytest.fixture
def ws(tmp_path):
    paths = make_workspace(tmp_path)
    work = paths["work"]
    return {
        "work": work,
        "raw": paths["raw"],
        "overrides": paths["overrides"],
        "out": paths["out"],
        "parcels": json.loads((work / "parcels.json").read_text())["parcels"],
        "buildings": json.loads((work / "buildings.json").read_text())["buildings"],
        "graph": json.loads((work / "roads.json").read_text()),
        "terrain": make_terrain(),
    }


def osm_lamp(osm_id: str, x: float, y: float) -> dict:
    return {
        "type": "Feature",
        "properties": {"osm_id": osm_id},
        "geometry": {"type": "Point", "coordinates": [x, y]},
    }


def lamps_for(ws, osm=()):
    return make_lamps(
        ws["parcels"], ws["buildings"], ws["graph"], list(osm), ws["terrain"], LAMPS, 250.0
    )


def road_centre(ws):
    """Centrelines of the residential edges (the road is split at the track junction) and its width."""
    edges = [(e, ln) for e, ln in edge_lines(ws["graph"]) if e["highway"] == "residential"]
    return unary_union([ln for _, ln in edges]), float(edges[0][0]["width"])


def test_rule_lamps_sit_beside_the_road_and_respect_parcels(ws) -> None:
    lamps = lamps_for(ws)
    rules = [lp for lp in lamps if lp["source"] == "rule"]
    assert len(rules) >= 5
    line, width = road_centre(ws)
    avoid = [shape(p["polygon"]) for p in ws["parcels"] if p["kind"] in LAMPS["avoid_kinds"]]
    building_zone = unary_union([footprint_polygon(b["footprint"]) for b in ws["buildings"]])
    for lp in rules:
        p = Point(lp["x"], lp["y"])
        assert lp["highway"] == "residential" and lp["parcel"] == "P-0003"
        assert line.distance(p) == pytest.approx(width / 2 + LAMPS["road_offset_m"], abs=0.01)
        assert not any(a.contains(p) for a in avoid)
        assert building_zone.distance(p) >= LAMPS["building_clearance_m"]
        assert ws["terrain"].coverage.contains(p)
    # staggered: both sides of the road are used
    sides = {round((lp["y"] - 106) / abs(lp["y"] - 106)) for lp in rules}
    assert sides == {1, -1}


def test_rule_lamps_are_spaced_and_deterministic(ws) -> None:
    rules = [lp for lp in lamps_for(ws) if lp["source"] == "rule"]
    xs = sorted(lp["x"] for lp in rules)
    gaps = [b - a for a, b in pairwise(xs)]
    assert all(g >= LAMPS["min_lamp_distance_m"] for g in gaps)
    assert lamps_for(ws) == lamps_for(ws)


def test_osm_lamps_kept_unless_under_a_building_or_off_coverage(ws) -> None:
    osm = [
        osm_lamp("n1", 120.0, 60.0),  # inside the garden: kept (the parcel rule is for rule lamps only)
        osm_lamp("n2", 67.0, 62.0),  # inside the building footprint: dropped
        osm_lamp("n3", 500.0, 500.0),  # outside the terrain coverage: dropped
    ]
    lamps = lamps_for(ws, osm)
    kept = [lp for lp in lamps if lp["source"] == "osm"]
    assert [lp["osm_id"] for lp in kept] == ["n1"]
    assert kept[0]["x"] == 120.0 and kept[0]["parcel"] == "P-0002"


def test_lamps_can_be_switched_off_and_the_village_has_them_off(ws) -> None:
    assert lamps_for(ws, [osm_lamp("n1", 120.0, 60.0)])  # on by default
    off = make_lamps(
        ws["parcels"], ws["buildings"], ws["graph"], [osm_lamp("n1", 120.0, 60.0)], ws["terrain"],
        {**LAMPS, "enabled": False}, 250.0,
    )
    assert off == []  # rule lamps and OSM lamps alike
    assert load_section("lamps")["enabled"] is True
    assert load_section("lamps", area="village")["enabled"] is False


def test_rule_lamps_yield_to_nearby_osm_lamps(ws) -> None:
    # an OSM lamp right where the first rule lamp would stand
    probe = next(lp for lp in lamps_for(ws) if lp["source"] == "rule")
    osm = [osm_lamp("n9", probe["x"] + 1.0, probe["y"])]
    rules = [lp for lp in lamps_for(ws, osm) if lp["source"] == "rule"]
    assert rules and all(
        Point(lp["x"], lp["y"]).distance(Point(probe["x"] + 1.0, probe["y"])) >= LAMPS["min_lamp_distance_m"]
        for lp in rules
    )


def test_lamp_records_have_the_fields_the_game_reads(ws) -> None:
    lamps = lamps_for(ws, [osm_lamp("n1", 120.0, 60.0)])
    assert [lp["id"] for lp in lamps] == [f"L-{i:04d}" for i in range(1, len(lamps) + 1)]
    for lp in lamps:
        assert set(lp) == {"id", "x", "y", "z", "source", "osm_id", "highway", "parcel", "chunk"}
        assert lp["chunk"] == [int(lp["x"] // 250), int(lp["y"] // 250)]


def test_run_props_writes_lamps_json_and_export_copies_it(ws) -> None:
    rules = ws["work"].parent / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"count": 4}}))
    counts = run_props(ws["work"], rules)
    assert counts["lamps"] > 0
    doc = json.loads((ws["work"] / "lamps.json").read_text())
    assert doc["spacing_m"] == LAMPS["spacing_m"] and len(doc["lamps"]) == counts["lamps"]

    run_export(ws["work"], ws["out"], ws["raw"], overrides_path=ws["overrides"])
    manifest = json.loads((ws["out"] / "manifest.json").read_text())
    assert manifest["files"]["lamps"] == "lamps.json"
    assert "lamps" in manifest["generated"]
    assert "work/lamps.json" in manifest["inputs"]
    assert (ws["out"] / "lamps.json").read_bytes() == (ws["work"] / "lamps.json").read_bytes()


def test_parse_osm_collects_street_lamp_nodes_only() -> None:
    raw = {
        "elements": [
            {"type": "node", "id": 1, "lon": 17.0, "lat": 48.0, "tags": {"highway": "street_lamp"}},
            {"type": "node", "id": 2, "lon": 17.0, "lat": 48.0, "tags": {"man_made": "street_lamp"}},
            {"type": "node", "id": 3, "lon": 17.0, "lat": 48.0, "tags": {"highway": "bus_stop"}},
            {"type": "node", "id": 4, "lon": 17.0, "lat": 48.0, "tags": {"natural": "tree"}},
        ]
    }
    osm = parse_osm(raw)
    assert [f.osm_id for f in osm["street_lamps"]] == ["n1", "n2"]
    assert [f.osm_id for f in osm["trees"]] == ["n4"]
    assert osm["highways"] == []
