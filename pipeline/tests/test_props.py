import json

import numpy as np
import pytest
import yaml
from shapely.geometry import LineString, Point, box, shape

from suchohrad_pipeline.propmeshes import fence_mesh, tree_mesh
from suchohrad_pipeline.props import make_fences, make_spawns, make_trees, run_props, seeded
from suchohrad_pipeline.rules import load_section
from tests.synthetic import make_terrain, make_workspace

FENCE, TREE = load_section("props")["fences"], load_section("props")["trees"]
SPAWN, ROADS = load_section("spawns"), load_section("roads")


@pytest.fixture
def ws(tmp_path):
    work = make_workspace(tmp_path)["work"]
    return {
        "work": work,
        "parcels": json.loads((work / "parcels.json").read_text())["parcels"],
        "buildings": json.loads((work / "buildings.json").read_text())["buildings"],
        "graph": json.loads((work / "roads.json").read_text()),
        "terrain": make_terrain(),
    }


def building_poly(ws):
    from suchohrad_pipeline.buildings import footprint_polygon

    return footprint_polygon(ws["buildings"][0]["footprint"])


def fences(ws, cfg=FENCE):
    return make_fences(ws["parcels"], ws["buildings"], ws["graph"], ws["terrain"], cfg)


def test_seed_is_stable() -> None:
    assert seeded("P-0001:fence").random() == seeded("P-0001:fence").random()
    assert seeded("P-0001:fence").random() != seeded("P-0002:fence").random()


def test_fences_are_deterministic_low_and_clear_of_obstacles(ws) -> None:
    cfg = {**FENCE, "probability": 1.0}
    a, b = fences(ws, cfg), fences(ws, cfg)
    assert a and a == b
    bpoly = building_poly(ws).buffer(FENCE["building_clearance_m"] - 0.05)
    kinds = {p["id"]: p["kind"] for p in ws["parcels"]}
    for s in a:
        seg = LineString([s["a"], s["b"]])
        assert 0 < seg.length <= FENCE["section_m"] + 1e-6
        assert not seg.intersects(bpoly)
        assert kinds[s["parcel"]] in FENCE["kinds"]
        # fences stand on the ground
        assert s["za"] == pytest.approx(ws["terrain"].surface(*s["a"]), abs=2e-3)
    # field, forest and other parcels get none
    assert {s["parcel"] for s in a} <= {"P-0001", "P-0002"}
    # nothing is fenced twice: sections of the second parcel keep off the first one's fence
    first = [LineString([s["a"], s["b"]]) for s in a if s["parcel"] == "P-0001"]
    for s in a:
        if s["parcel"] == "P-0002":
            seg = LineString([s["a"], s["b"]])
            assert all(seg.distance(f) > 0.2 for f in first)


def test_fence_gate_gap_on_road_frontage(ws) -> None:
    on_road = lambda cfg: [
        s
        for s in fences(ws, {**FENCE, "probability": 1.0, **cfg})
        if s["parcel"] == "P-0001" and abs(s["a"][1] - 100) < 1e-6 and abs(s["b"][1] - 100) < 1e-6
    ]
    # the road clearance already keeps the carriageway free; use no clearance to see the gap
    with_gate = on_road({"road_clearance_m": -3.0, "gate_gap_m": 6})
    without = on_road({"road_clearance_m": -3.0, "gate_gap_m": 0})
    assert sum(1 for _ in without) > 0
    def covers(sections, x: float) -> bool:
        return any(min(s["a"][0], s["b"][0]) <= x <= max(s["a"][0], s["b"][0]) for s in sections)

    # the gate (6 m) is centred on the middle of the frontage; the rest stays fenced
    assert covers(without, 67.5) and not covers(with_gate, 67.5)
    assert covers(with_gate, 50) and covers(with_gate, 90)


def test_fence_probability_zero_gives_nothing(ws) -> None:
    assert fences(ws, {**FENCE, "probability": 0.0}) == []


def test_trees_deterministic_and_respect_rules(ws) -> None:
    osm = [
        {
            "properties": {"osm_id": "n1"},
            "geometry": {"type": "Point", "coordinates": [150, 150]},
        },
        {
            "properties": {"osm_id": "n2"},
            "geometry": {"type": "Point", "coordinates": [900, 900]},  # outside coverage
        },
    ]

    def make():
        return make_trees(ws["parcels"], ws["buildings"], ws["graph"], osm, ws["terrain"], TREE)

    a = make()
    assert a == make() and len(a) > 5
    assert [t["src"] for t in a if t["src"] == "osm"] == ["osm"]  # the far OSM tree is dropped
    polys = {p["id"]: shape(p["polygon"]) for p in ws["parcels"]}
    bpoly = building_poly(ws)
    road = LineString([[20, 106], [200, 106]])
    rule = [t for t in a if t["src"] == "rule"]
    assert {t["parcel"] for t in rule} <= {"P-0001", "P-0002", "P-0005", "P-0006"}  # not field
    for t in rule:
        pt = Point(t["x"], t["y"])
        assert polys[t["parcel"]].contains(pt)
        assert pt.distance(bpoly) >= TREE["building_clearance_m"] - 1e-6
        assert pt.distance(road) >= 2.5 + TREE["road_clearance_m"] - 1e-6
        assert t["z"] == pytest.approx(ws["terrain"].surface(t["x"], t["y"]), abs=2e-3)
    for i, t in enumerate(rule):
        for u in rule[:i]:
            if t["parcel"] == u["parcel"]:
                assert np.hypot(t["x"] - u["x"], t["y"] - u["y"]) >= TREE["min_spacing_m"] - 1e-6
    forest = [t for t in rule if t["parcel"] == "P-0005"]
    assert forest and all(t["colour"] in TREE["forest_canopy_colours"] for t in forest)
    # density config: no trees at density 0
    off = {**TREE, "density_per_1000m2": {}}
    assert [t for t in make_trees(ws["parcels"], ws["buildings"], ws["graph"], [], ws["terrain"], off)] == []


def test_prop_meshes_are_small_and_valid(ws) -> None:
    sections = fences(ws, {**FENCE, "probability": 1.0})
    vis = fence_mesh(sections, FENCE)
    col = fence_mesh(sections, FENCE, bottom=True)
    assert vis.face_count == 10 * len(sections) and col.face_count == 12 * len(sections)
    for shape_ in ("box", "cone"):
        tree = {"x": 50.0, "y": 50.0, "z": 120.0, "scale": 1.0, "rot": 0.3, "shape": shape_,
                "colour": "#4f8f45"}
        m = tree_mesh(tree, TREE)
        assert 0 < m.face_count <= 40 and m.colours.shape == (m.face_count, 3)
        flat = m.tris.reshape(-1, 3)
        assert flat[:, 2].min() >= 120 - 0.25 and flat[:, 2].max() < 120 + 8
        # closed solids are wound outwards: signed volume is positive per box-like piece
        v = np.einsum("ij,ij->i", m.tris[:, 0], np.cross(m.tris[:, 1], m.tris[:, 2])).sum() / 6
        assert v > 0


def test_spawn_constraints_and_determinism(ws) -> None:
    trees = make_trees(ws["parcels"], ws["buildings"], ws["graph"], [], ws["terrain"], TREE)

    def make(cfg=SPAWN):
        return make_spawns(
            ws["parcels"], ws["buildings"], ws["graph"], trees, ws["terrain"], cfg, ROADS, 250.0
        )

    a = make()
    assert a == make() and 0 < len(a) <= SPAWN["count"]
    assert [s["id"] for s in a] == [f"S-{i:03d}" for i in range(1, len(a) + 1)]
    bpoly = building_poly(ws)
    allowed = {"road", *SPAWN["ground_kinds"]}
    for s in a:
        pt = Point(s["x"], s["y"])
        assert s["kind"] in allowed
        assert pt.distance(bpoly) >= SPAWN["min_building_distance_m"] - 1e-6
        assert ws["terrain"].coverage.contains(pt)
        assert s["chunk"] == [0, 0]
        ground = float(ws["terrain"].surface(s["x"], s["y"]))
        lift = ROADS["lift_m"] if s["kind"] == "road" else 0.0
        assert s["z"] == pytest.approx(ground + lift, abs=2e-3)
        if s["kind"] != "road":
            parcel = next(p for p in ws["parcels"] if p["id"] == s["parcel"])
            assert parcel["kind"] == s["kind"] and shape(parcel["polygon"]).contains(pt)
        for t in trees:
            assert np.hypot(t["x"] - s["x"], t["y"] - s["y"]) >= SPAWN["min_tree_distance_m"] - 1e-6
    assert any(s["kind"] == "road" for s in a) and any(s["kind"] != "road" for s in a)
    assert len(make({**SPAWN, "count": 7})) == 7
    far = make({**SPAWN, "min_building_distance_m": 1000})
    assert far == []


def test_run_props_writes_files_deterministically(ws, tmp_path) -> None:
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"count": 12}}))
    counts = run_props(ws["work"], rules)
    first = {n: (ws["work"] / n).read_bytes() for n in ("props.json", "spawns.json")}
    assert counts["spawns"] == 12 and counts["fences"] > 0 and counts["trees"] > 0
    run_props(ws["work"], rules)
    assert first == {n: (ws["work"] / n).read_bytes() for n in first}
    doc = json.loads(first["spawns.json"])
    assert set(doc) == {"version", "axes", "chunk_size_m", "spawns"}
    assert box(0, 0, 250, 250).contains(Point(doc["spawns"][0]["x"], doc["spawns"][0]["y"]))


def test_area_overlay_only_applies_to_that_area(tmp_path) -> None:
    slice_props = load_section("props", area="slice")
    village = load_section("props", area="village")
    assert slice_props == load_section("props")
    assert slice_props["fences"]["collision"] is True and slice_props["trees"]["max_per_chunk"] == 0
    assert village["fences"]["collision"] is False and village["trees"]["max_per_chunk"] > 0
    # keys the overlay does not mention keep their value
    assert village["fences"]["height_m"] == slice_props["fences"]["height_m"]
    custom = tmp_path / "r.yaml"
    custom.write_text(yaml.safe_dump({"areas": {"village": {"spawns": {"count": 7}}}}))
    assert load_section("spawns", custom, "village")["count"] == 7
    assert load_section("spawns", custom, "slice")["count"] == load_section("spawns")["count"]


def test_cap_trees_is_per_chunk_stable_and_keeps_osm_trees() -> None:
    from suchohrad_pipeline.props import cap_trees

    trees = [{"src": "rule", "x": float(i), "y": 1.0} for i in range(40)]
    trees += [{"src": "rule", "x": 300.0 + i, "y": 1.0} for i in range(5)]
    trees += [{"src": "osm", "x": 2.5, "y": 2.0}]
    capped = cap_trees(trees, 250.0, 10)
    assert sum(1 for t in capped if t["x"] < 250 and t["src"] == "rule") == 10
    assert sum(1 for t in capped if t["x"] >= 300) == 5  # under the cap, untouched
    assert any(t["src"] == "osm" for t in capped)
    assert capped == cap_trees(trees, 250.0, 10)
    assert cap_trees(trees, 250.0, 0) == trees


def test_run_props_cap_via_rules(ws, tmp_path) -> None:
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"props": {"trees": {"max_per_chunk": 1}}}))
    run_props(ws["work"], rules)
    rule_trees = [t for t in json.loads((ws["work"] / "props.json").read_text())["trees"]
                  if t["src"] == "rule"]
    per_chunk = {}
    for t in rule_trees:
        per_chunk[(t["x"] // 250, t["y"] // 250)] = per_chunk.get((t["x"] // 250, t["y"] // 250), 0) + 1
    assert per_chunk and max(per_chunk.values()) <= 1
