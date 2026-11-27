import json

import numpy as np
import pytest
import yaml
from shapely.geometry import LineString, Point, box

from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.props.fences import fences_doc, make_fences
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.props.propmeshes import fence_mesh, tree_mesh
from drycastle_pipeline.props.props import run_props
from drycastle_pipeline.props.spawns import make_spawns
from drycastle_pipeline.props.trees import make_trees
from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain, make_workspace

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
    from drycastle_pipeline.buildings.footprint import footprint_polygon

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


def spawns_of(ws, zones, trees=(), water=None, cfg=SPAWN):
    return make_spawns(zones, ws["buildings"], ws["graph"], list(trees), ws["terrain"], cfg, 250.0, water=water)


def test_spawns_are_inside_the_zones_and_clear_of_obstacles(ws) -> None:
    trees = make_trees(ws["buildings"], ws["graph"], ws["terrain"], {**TREE, "ortho_dir": str(ws["work"].parent / "ortho")},
                       origin=SYNTHETIC_ORIGIN)
    assert trees
    zones = [("Z-0001", box(20, 20, 120, 90)), ("Z-0002", box(150, 150, 230, 230))]
    water = box(60, 40, 90, 70)
    a = spawns_of(ws, zones, trees, water)
    assert a == spawns_of(ws, zones, trees, water) and 0 < len(a)
    assert [s["id"] for s in a] == [f"S-{i:03d}" for i in range(1, len(a) + 1)]
    bpoly = building_poly(ws)
    by_id = dict(zones)
    for s in a:
        pt = Point(s["x"], s["y"])
        assert s["kind"] == "zone" and by_id[s["zone"]].contains(pt)
        assert pt.distance(bpoly) >= SPAWN["min_building_distance_m"] - 1e-6
        assert pt.distance(water) >= SPAWN["water_clearance_m"] - 1e-6
        assert ws["terrain"].coverage.contains(pt)
        assert s["chunk"] == [0, 0]
        assert s["z"] == pytest.approx(float(ws["terrain"].surface(s["x"], s["y"])), abs=2e-3)
        for t in trees:
            assert np.hypot(t["x"] - s["x"], t["y"] - s["y"]) >= SPAWN["min_tree_distance_m"] - 1e-6
    assert {s["zone"] for s in a} == {"Z-0001", "Z-0002"}


def test_points_follow_area_with_no_minimum_or_maximum(ws) -> None:
    big, tiny = box(150, 150, 240, 240), box(200, 20, 204, 24)  # 4 x 4 m
    got = spawns_of(ws, [("big", big), ("tiny", tiny)])
    n = lambda zid: sum(s["zone"] == zid for s in got)
    assert n("tiny") == 1  # ceil: a zone with usable ground always has a point, whatever its size
    assert n("big") >= 6  # and a bigger one has more (only 60 x 50 m of it is inside the terrain)
    off_terrain = box(900, 900, 950, 950)
    assert spawns_of(ws, [("off", off_terrain)]) == []  # a zone with no usable ground: nothing, not a random spawn
    assert spawns_of(ws, [("z", big)], cfg={**SPAWN, "min_building_distance_m": 1000}) == []
    dense = spawns_of(ws, [("z", big)], cfg={**SPAWN, "m2_per_point": 100})
    sparse = spawns_of(ws, [("z", big)], cfg={**SPAWN, "m2_per_point": 1000})
    assert len(dense) > 3 * len(sparse) > 0


def test_no_zone_means_random_points_off_the_roads(ws) -> None:
    from drycastle_pipeline.props.helpers import road_zone

    a = spawns_of(ws, [])
    assert a and a == spawns_of(ws, []) and all(s["kind"] == "random" and s["zone"] == "" for s in a)
    rzone = road_zone(ws["graph"], 0.0)
    assert all(not rzone.contains(Point(s["x"], s["y"])) for s in a)


def test_run_props_takes_spawns_only_from_the_player_spawn_zones(ws, tmp_path) -> None:
    ox, oy = SYNTHETIC_ORIGIN
    edits = tmp_path / "edits.json"
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"m2_per_point": 200}, "zones": {"player_spawn": {"where_ground": ["meadow"]}}}))

    def run(zones):
        edits.write_text(json.dumps({"version": 1, "zones": zones}))
        run_props(ws["work"], rules, edits)
        return json.loads((ws["work"] / "spawns.json").read_text())["spawns"]

    zone = {"kind": "player_spawn", "polygon": [[ox + 120, oy + 120], [ox + 200, oy + 120], [ox + 200, oy + 190], [ox + 120, oy + 190]]}
    got = run({"Z-0001": zone, "Z-0002": {"kind": "deer", "polygon": zone["polygon"]}})
    assert got and all(s["zone"] == "Z-0001" and box(120, 120, 200, 190).contains(Point(s["x"], s["y"])) for s in got)
    assert all(s["kind"] == "random" for s in run({}))  # no zone at all: random points


def test_run_props_writes_files_deterministically(ws, tmp_path) -> None:
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"m2_per_point": 400}}))
    none = tmp_path / "no_edits.json"  # not the repo's own zones
    counts = run_props(ws["work"], rules, none)
    first = {n: (ws["work"] / n).read_bytes() for n in ("props.json", "spawns.json")}
    assert counts["spawns"] > 0 and counts["fences"] > 0 and counts["trees"] > 0
    run_props(ws["work"], rules, none)
    assert first == {n: (ws["work"] / n).read_bytes() for n in first}
    doc = json.loads(first["spawns.json"])
    assert set(doc) == {"version", "axes", "chunk_size_m", "spawns"}
    assert box(0, 0, 250, 250).contains(Point(doc["spawns"][0]["x"], doc["spawns"][0]["y"]))


def test_map_props_defaults_are_the_village_values() -> None:
    props = load_section("props")
    assert props["fences"]["collision"] is False and props["trees"]["max_per_chunk"] == 0


def test_custom_rules_file_is_merged_over_the_shipped_defaults(tmp_path) -> None:
    custom = tmp_path / "r.yaml"
    custom.write_text(yaml.safe_dump({"spawns": {"m2_per_point": 7}}))
    assert load_section("spawns", custom)["m2_per_point"] == 7
    assert load_section("spawns", custom).keys() == load_section("spawns").keys()


def test_cap_trees_is_per_chunk_stable_and_keeps_detected_trees() -> None:
    from drycastle_pipeline.props.trees import cap_trees

    trees = [{"src": "ortho-canopy", "x": float(i), "y": 1.0} for i in range(40)]
    trees += [{"src": "ortho-canopy", "x": 300.0 + i, "y": 1.0} for i in range(5)]
    trees += [{"src": "ortho", "x": 2.5, "y": 2.0}]
    capped = cap_trees(trees, 250.0, 10)
    assert sum(1 for t in capped if t["x"] < 250 and t["src"] == "ortho-canopy") == 10
    assert sum(1 for t in capped if t["x"] >= 300) == 5  # under the cap, untouched
    assert any(t["src"] == "ortho" for t in capped)
    assert capped == cap_trees(trees, 250.0, 10)
    assert cap_trees(trees, 250.0, 0) == trees


def test_run_props_cap_via_rules(ws, tmp_path) -> None:
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"props": {"trees": {"max_per_chunk": 1}}}))
    run_props(ws["work"], rules)
    rule_trees = [t for t in json.loads((ws["work"] / "props.json").read_text())["trees"]
                  if t["src"] == "ortho-canopy"]
    per_chunk = {}
    for t in rule_trees:
        per_chunk[(t["x"] // 250, t["y"] // 250)] = per_chunk.get((t["x"] // 250, t["y"] // 250), 0) + 1
    assert per_chunk and max(per_chunk.values()) <= 1


def test_fences_doc_has_the_sections_with_effective_height_type_and_group() -> None:
    plain = {"parcel": "P-0001", "a": [0.0, 0.0], "b": [4.0, 0.0], "za": 100.0, "zb": 100.5}
    custom = {"parcel": "P-0002", "a": [4.0, 0.0], "b": [8.0, 0.0], "za": 100.5, "zb": 101.0, "type": "brick_red", "height_m": 1.8}
    doc = fences_doc([plain, custom], {"G-0001": ["P-0002", "P-0003"]}, FENCE)
    assert set(doc) == {"version", "axes", "fences"} and doc["version"] == 1
    p, c = doc["fences"]
    assert p == {"parcel": "P-0001", "group": None, "a": [0.0, 0.0], "b": [4.0, 0.0], "za": 100.0, "zb": 100.5,
                 "height_m": FENCE["height_m"], "type": FENCE.get("type")}
    assert c["group"] == "G-0001" and c["type"] == "brick_red" and c["height_m"] == 1.8 and c["zb"] == 101.0


def test_run_props_writes_fences_json_for_every_section_deterministically(ws, tmp_path) -> None:
    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"version": 1, "groups": {"G-0001": {"parcels": ["P-0001", "P-0002"]}}}))
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"props": {"fences": {"probability": 1.0}}}))
    counts = run_props(ws["work"], rules, edits)
    first = (ws["work"] / "fences.json").read_bytes()
    run_props(ws["work"], rules, edits)
    assert first == (ws["work"] / "fences.json").read_bytes()
    doc = json.loads(first)
    sections = json.loads((ws["work"] / "props.json").read_text())["fences"]
    assert len(doc["fences"]) == len(sections) == counts["fences"] > 0
    assert {f["parcel"] for f in doc["fences"]} <= {"P-0001", "P-0002"}
    assert all(f["group"] == "G-0001" for f in doc["fences"])  # both parcels are in the group
    for f, s in zip(doc["fences"], sections, strict=True):
        assert (f["a"], f["b"], f["za"], f["zb"]) == (s["a"], s["b"], s["za"], s["zb"])
        assert f["height_m"] == pytest.approx(FENCE["height_m"])


def test_run_props_spawns_skip_a_cut_of_the_player_spawn_rule_zone(ws, tmp_path) -> None:
    from shapely.geometry import mapping

    parcels_file = ws["work"] / "parcels.json"
    doc = json.loads(parcels_file.read_text())
    doc["ground"] = [{"id": "A-0001", "kind": "meadow", "polygon": mapping(box(120, 120, 200, 190)), "area": 80 * 70}]
    parcels_file.write_text(json.dumps(doc))
    ox, oy = SYNTHETIC_ORIGIN
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({
        "spawns": {"m2_per_point": 400},
        "zones": {"player_spawn": {"where_ground": ["meadow"]}},
    }))
    edits = tmp_path / "edits.json"

    def meadow(cuts):
        edits.write_text(json.dumps({"version": 1, **({"zone_cuts": cuts} if cuts else {})}))
        run_props(ws["work"], rules, edits)
        return [s for s in json.loads((ws["work"] / "spawns.json").read_text())["spawns"] if s["kind"] == "zone"]

    assert meadow(None)  # the rule zone: every meadow area
    half = [[ox + 100, oy + 100], [ox + 160, oy + 100], [ox + 160, oy + 200], [ox + 100, oy + 200]]
    left = meadow({"ZC-0001": {"kind": "player_spawn", "polygon": half}})
    assert left and all(s["x"] > 160 for s in left)  # the cut part is gone, the rest stays
    whole = [[ox + 100, oy + 100], [ox + 210, oy + 100], [ox + 210, oy + 200], [ox + 100, oy + 200]]
    assert meadow({"ZC-0001": {"kind": "player_spawn", "polygon": whole}}) == []  # all cut away: no spawn, not a random one


# --- per-tree base attribute (map 0.12.0) -----------------------------------------------------


def glb_attributes(data: bytes, node: str) -> tuple[dict, dict]:
    """The primitive of mesh ``node`` of a GLB and its POSITION / TEXCOORD_1 read as float arrays."""
    import struct

    jlen = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20 : 20 + jlen])
    blen = struct.unpack_from("<I", data, 20 + jlen)[0]
    blob = data[28 + jlen : 28 + jlen + blen]
    prim = next(m["primitives"][0] for m in doc["meshes"] if m["name"] == node)
    out = {}
    for name in ("POSITION", "TEXCOORD_1"):
        if name in prim["attributes"]:
            acc = doc["accessors"][prim["attributes"][name]]
            view = doc["bufferViews"][acc["bufferView"]]
            width = 3 if acc["type"] == "VEC3" else 2
            start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
            out[name] = np.frombuffer(blob, "<f4", acc["count"] * width, start).reshape(-1, width)
    return prim, out


def test_tree_vertices_carry_their_base_and_fences_the_sentinel() -> None:
    import trimesh

    from drycastle_pipeline.export.gltf import (
        NO_TREE_UV2,
        build_chunk_scene,
        export_glb,
        parcel_props_nodes,
    )
    from drycastle_pipeline.props.propmeshes import parcel_props_meshes, trees_mesh

    cfg = load_section("props")
    trees = [
        {"x": 1234.5, "y": 678.25, "z": 3.0, "scale": 1.0, "rot": 0.3, "shape": "cone", "colour": "#2f6b2f", "parcel": "P-1"},
        {"x": -20.75, "y": 910.5, "z": 5.5, "scale": 1.4, "rot": 1.1, "shape": "box", "colour": "#3f7b3f", "parcel": "P-1", "forest": 1},
    ]
    fences = [{"parcel": "P-1", "a": [10.0, 10.0], "b": [13.0, 10.0], "za": 0.0, "zb": 0.0, "type": "picket"}]  # its posts are flat
    visual, collision, textured = parcel_props_meshes(fences, trees, cfg)
    fence_only = parcel_props_meshes(fences, [], cfg)[0]
    n_tree = [trees_mesh([t], cfg["trees"]).face_count for t in trees]
    assert visual.face_count == fence_only.face_count + sum(n_tree) and min(n_tree) > 0 and fence_only.face_count > 0

    data = export_glb(build_chunk_scene([], parcel_props_nodes([("P-1", visual, collision, textured)])))
    prim, attrs = glb_attributes(data, "parcel_P-1_props")
    pos, uv2 = attrs["POSITION"], attrs["TEXCOORD_1"]
    assert len(pos) == len(uv2) == 3 * visual.face_count  # one vertex per triangle corner

    # triangle order: fence sections, then tree 1, then tree 2
    ends = np.cumsum([fence_only.face_count, *n_tree]) * 3
    assert (uv2[: ends[0]] == NO_TREE_UV2).all()
    for t, lo, hi in zip(trees, ends[:-1], ends[1:]):
        # (POSITION x, POSITION z) of the base: the glTF axes of (x, y, z) are (x, z, -y)
        assert np.allclose(uv2[lo:hi], [t["x"], -t["y"]])
        p = pos[lo:hi]
        assert p[:, 0].min() < t["x"] < p[:, 0].max() and p[:, 2].min() < -t["y"] < p[:, 2].max()
        assert p[:, 1].min() == pytest.approx(t["z"] - 0.2, abs=1e-3)  # the trunk starts just below the base

    # no trees, no second UV set; and the renamed file still loads like any other
    bare = export_glb(build_chunk_scene([], parcel_props_nodes([("P-1", fence_only, collision, textured)])))
    assert "TEXCOORD_1" not in glb_attributes(bare, "parcel_P-1_props")[0]["attributes"]
    loaded = trimesh.load(trimesh.util.wrap_as_stream(data), file_type="glb", force="scene")
    assert len(loaded.geometry["parcel_P-1_props"].faces) == visual.face_count
