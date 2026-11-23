"""Ground objects (logs, stumps, boulders, root plates): placement in the forest, the drawn meshes, and the golden file that
the C# collider test (ForestTreeColliderTests' sibling GroundObjectColliderTests) checks the solids against.

Change the model or the rules: regenerate with ``UPDATE_GROUND_PARITY=1 uv run pytest tests/test_ground_objects.py``.
"""

import json
import math
import os
from pathlib import Path

import numpy as np
import pytest
from shapely.geometry import Point, box, mapping, shape
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.config import REPO_ROOT
from drycastle_pipeline.export import NO_TREE_UV2, ground_model, ground_objects_doc, to_trimesh
from drycastle_pipeline.propmeshes import ground_object_mesh, ground_objects_mesh
from drycastle_pipeline.props import make_trees, plan_ground_objects, road_zone
from drycastle_pipeline.rules import load_section
from drycastle_pipeline.terrain import grid_from_samples
from tests.synthetic import COVERAGE, make_terrain, make_workspace, write_ortho

PROPS = load_section("props")
TREE = PROPS["trees"]
CFG = PROPS["ground_objects"]
GOLDEN = REPO_ROOT / "godot" / "ShooterCore.Tests" / "fixtures" / "ground_object_parity.json"
FOREST = box(5, 5, 245, 245)  # a ground area of type forest over most of the synthetic terrain; the canopy covers the same


def forest_map(tmp_path: Path, kind: str = "forest"):
    ws = make_workspace(tmp_path)
    work = ws["work"]
    parcels_doc = json.loads((work / "parcels.json").read_text())
    parcels_doc["origin_epsg5514"] = [0.0, 0.0]
    parcels_doc["ground"] = [{"id": "G-0001", "kind": kind, "area": FOREST.area, "polygon": mapping(FOREST)}]
    for p in parcels_doc["parcels"]:
        if p["kind"] == "forest" and kind != "forest":
            p["kind"] = kind
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    graph = json.loads((work / "roads.json").read_text())
    write_ortho(tmp_path / "ortho", (0.0, 0.0), (10, 10, 240, 240), [])
    terrain = make_terrain()
    tree_cfg = {**TREE, "ortho_dir": str(tmp_path / "ortho")}
    trees = make_trees(buildings, graph, terrain, tree_cfg, origin=(0.0, 0.0))
    return parcels_doc, buildings, graph, terrain, trees, tree_cfg


def plan(tmp_path: Path, kind: str = "forest", cfg: dict = CFG, terrain=None, wet=None):
    parcels_doc, buildings, graph, flat, trees, tree_cfg = forest_map(tmp_path, kind)
    terrain = terrain if terrain is not None else flat
    objs = plan_ground_objects(parcels_doc, buildings, graph, terrain, trees, tree_cfg, cfg, wet=wet)
    return objs, (parcels_doc, buildings, graph, terrain, trees)


def footprint_points(o: dict) -> list[tuple[float, float]]:
    """Plan points of an object: its centre, and along a log's length."""
    if o["kind"] != "log":
        return [(o["x"], o["y"])]
    u = (math.cos(o["rot"]), math.sin(o["rot"]))
    return [(o["x"] + u[0] * t, o["y"] + u[1] * t) for t in np.linspace(-o["len"] / 2, o["len"] / 2, 9)]


def test_forest_gets_clumped_solid_objects_at_the_rule_densities(tmp_path: Path) -> None:
    objs, _ = plan(tmp_path)
    again, _ = plan(tmp_path / "again")
    assert objs == again  # deterministic
    kinds = {k: [o for o in objs if o["kind"] == k] for k in ("log", "stump", "boulder", "rootplate", "anthill", "burrow")}
    ha = 230 * 230 / 10000.0
    for kind in ("log", "stump", "boulder"):
        want = CFG[kind]["per_ha"] * ha
        assert 0.4 * want <= len(kinds[kind]) <= 1.3 * want, (kind, len(kinds[kind]), want)
    assert 0 < len(kinds["rootplate"]) <= 0.45 * len(kinds["log"])
    lo, hi = CFG["log"]["length_m"]
    assert all(lo <= o["len"] <= hi for o in kinds["log"])
    assert all("len" not in o for o in kinds["stump"] + kinds["boulder"] + kinds["rootplate"])
    # clumps: the nearest neighbour of a boulder is nearer than it would be in an even spread of the same count
    pts = np.array([(o["x"], o["y"]) for o in kinds["boulder"]])
    nearest = np.array([np.sort(np.hypot(*(pts - p).T))[1] for p in pts])
    even = 0.5 / math.sqrt(len(pts) / (230 * 230))  # mean nearest-neighbour distance of a random spread
    assert nearest.mean() < 0.85 * even


def test_objects_keep_off_roads_buildings_trunks_and_other_ground(tmp_path: Path) -> None:
    objs, (parcels_doc, buildings, graph, terrain, trees) = plan(tmp_path)
    assert len(objs) > 50
    bzone = unary_union([footprint_polygon(b["footprint"]) for b in buildings]).buffer(CFG["building_clearance_m"])
    rzone = road_zone(graph, CFG["road_clearance_m"])
    plots = unary_union([shape(p["polygon"]) for p in parcels_doc["parcels"] if p["kind"] in CFG["avoid_parcel_kinds"]])
    off = unary_union([bzone, rzone, plots])
    for o in objs:
        for x, y in footprint_points(o):
            assert 10 <= x < 240 and 10 <= y < 240  # on the canopy
            assert not off.contains(Point(x, y)), o
    trunk = np.array([(t["x"], t["y"]) for t in trees])
    for o in objs:
        if o["kind"] == "rootplate":
            continue
        for x, y in footprint_points(o):
            assert np.hypot(trunk[:, 0] - x, trunk[:, 1] - y).min() > 0.4, o  # clear of every trunk foot


def test_a_root_plate_stands_at_the_thick_end_of_a_log(tmp_path: Path) -> None:
    objs, _ = plan(tmp_path)
    plates = [o for o in objs if o["kind"] == "rootplate"]
    logs = [o for o in objs if o["kind"] == "log"]
    assert plates
    for p in plates:
        # the plate's heading is its log's; the log is the one whose -u end is next to the plate
        near = [lg for lg in logs if lg["rot"] == p["rot"]]
        assert near
        d = min(math.hypot(p["x"] - (lg["x"] - math.cos(lg["rot"]) * lg["len"] / 2), p["y"] - (lg["y"] - math.sin(lg["rot"]) * lg["len"] / 2)) for lg in near)
        assert d < 0.5


def test_only_forest_ground_gets_objects(tmp_path: Path) -> None:
    objs, _ = plan(tmp_path, kind="field")
    assert objs == []


def test_ground_follows_the_terrain(tmp_path: Path) -> None:
    objs, (_, _, _, terrain, _) = plan(tmp_path)
    z = terrain.surface(np.array([o["x"] for o in objs]), np.array([o["y"] for o in objs]))
    assert np.allclose([o["z"] for o in objs], z, atol=1e-3)


SAMPLES = {
    "log": {"kind": "log", "x": 40.5, "y": -12.25, "z": 130.0, "rot": 0.7, "scale": 1.0, "len": 6.0},
    "log_thick": {"kind": "log", "x": 40.5, "y": -12.25, "z": 130.0, "rot": 2.9, "scale": 1.33, "len": 8.5},
    "stump": {"kind": "stump", "x": 12.5, "y": 3.25, "z": 128.5, "rot": 0.4, "scale": 1.0},
    "stump_tall": {"kind": "stump", "x": 12.5, "y": 3.25, "z": 128.5, "rot": 1.0, "scale": 1.33},
    "boulder": {"kind": "boulder", "x": -5.5, "y": 20.0, "z": 131.2, "rot": 0.3, "scale": 1.0},
    "boulder_big": {"kind": "boulder", "x": -15.5, "y": 2.0, "z": 131.2, "rot": 2.2, "scale": 2.2},
    "rootplate": {"kind": "rootplate", "x": 7.75, "y": 7.5, "z": 129.0, "rot": 1.9, "scale": 1.0},
    "anthill": {"kind": "anthill", "x": 3.5, "y": -8.25, "z": 127.5, "rot": 0.9, "scale": 1.0},
    "anthill_big": {"kind": "anthill", "x": 3.5, "y": -8.25, "z": 127.5, "rot": 2.4, "scale": 1.33},
    "burrow": {"kind": "burrow", "x": -20.5, "y": 6.0, "z": 126.0, "rot": 0.3, "scale": 1.0},
    "burrow_big": {"kind": "burrow", "x": -20.5, "y": 6.0, "z": 126.0, "rot": 3.9, "scale": 1.23},
}


def bbox(mesh) -> list[list[float]]:
    pts = mesh.tris.reshape(-1, 3)
    return [np.round(pts.min(axis=0), 3).tolist(), np.round(pts.max(axis=0), 3).tolist()]


def expected() -> dict:
    return {
        "note": "Bounding boxes (map metres) of the drawn ground objects (propmeshes.ground_object_mesh), checked against the collision "
        "solids by GroundObjectColliderTests. Regenerate: UPDATE_GROUND_PARITY=1 uv run pytest tests/test_ground_objects.py (in pipeline/).",
        "model": ground_model(CFG),
        "objects": [{"name": n, "object": o, "bbox": bbox(ground_object_mesh(o, CFG))} for n, o in SAMPLES.items()],
    }


def test_golden_matches_the_mesh() -> None:
    want = expected()
    if os.environ.get("UPDATE_GROUND_PARITY"):
        GOLDEN.write_text(json.dumps(want, indent=1) + "\n")
    got = json.loads(GOLDEN.read_text())
    assert got["model"] == json.loads(json.dumps(want["model"]))
    assert [g["name"] for g in got["objects"]] == list(SAMPLES)
    for g, w in zip(got["objects"], want["objects"]):
        assert g["object"] == w["object"]
        assert np.allclose(g["bbox"], w["bbox"], atol=1e-3), g["name"]


def test_meshes_are_cheap_sit_on_the_ground_and_have_the_sizes_of_the_rules() -> None:
    for name, o in SAMPLES.items():
        m = ground_object_mesh(o, CFG)
        assert 0 < m.face_count <= 40, name
        lo = bbox(m)[0]
        assert lo[2] <= o["z"] + 1e-6 and lo[2] >= o["z"] - 0.3, name  # foot just under the ground, nothing floats
        assert ground_object_mesh(o, CFG).tris.tolist() == m.tris.tolist()  # seeded by position
    assert bbox(ground_object_mesh(SAMPLES["rootplate"], CFG))[1][2] - 129.0 == pytest.approx(1.9, abs=0.25)  # about 2 m tall
    log = bbox(ground_object_mesh({**SAMPLES["log"], "rot": 0.0}, CFG))
    assert log[1][0] - log[0][0] == pytest.approx(6.0, abs=1e-3)  # `len` is the length


def test_baked_objects_carry_their_own_base_in_uv2() -> None:
    objs = [SAMPLES["log"], SAMPLES["boulder"]]
    mesh = ground_objects_mesh(objs, CFG)
    tm = to_trimesh(mesh)
    uv2 = tm.vertex_attributes["_UV2"]
    n = [ground_object_mesh(o, CFG).face_count * 3 for o in objs]
    assert len(uv2) == sum(n)
    first = np.round(np.array([objs[0]["x"], -objs[0]["y"]]), 4)
    second = np.round(np.array([objs[1]["x"], -objs[1]["y"]]), 4)  # (POSITION x, POSITION z) of the base
    assert (uv2[: n[0]] == first).all() and (uv2[n[0] :] == second).all()
    assert (uv2 != NO_TREE_UV2).all()


def test_ground_objects_json_has_the_contract_shape() -> None:
    objs = [{**SAMPLES["log"]}, {**SAMPLES["stump"]}]
    doc = ground_objects_doc(objs, CFG, 128.0)
    assert list(doc) == ["format_version", "axes", "chunk_size_m", "model", "objects"] or set(doc) == {"format_version", "axes", "chunk_size_m", "model", "objects"}
    assert doc["format_version"] == 1 and doc["model"]["base_uv2"] is True
    assert set(doc["model"]["kinds"]) == {"log", "stump", "boulder", "rootplate", "anthill", "burrow"}
    assert doc["objects"][0] == {**SAMPLES["log"], "chunk": [0, -1]}
    assert "len" not in doc["objects"][1]


def dense(**kw) -> dict:
    """The rules with enough anthills and burrows to count (the village numbers are about one per hectare)."""
    cfg = json.loads(json.dumps(CFG))
    cfg["anthill"]["per_ha"], cfg["burrow"]["candidates_per_ha"] = 30, 200
    for k, v in kw.items():
        cfg[k].update(v)
    return cfg


def test_anthills_stand_at_the_feet_of_pines_and_mixed_trees_and_never_in_swamp(tmp_path: Path) -> None:
    objs, (_, _, _, _, trees) = plan(tmp_path, cfg=dense())
    hills = [o for o in objs if o["kind"] == "anthill"]
    assert len(hills) > 30
    lo, hi = CFG["anthill"]["size_m"]
    assert all(lo - 2e-3 <= o["scale"] * CFG["model"]["anthill"]["width_m"] <= hi + 2e-3 for o in hills)
    stand = {(t["x"], t["y"]): t.get("stand") for t in trees}
    xy = np.array(list(stand))
    for o in hills:
        d = np.hypot(xy[:, 0] - o["x"], xy[:, 1] - o["y"])
        feet = [stand[tuple(p)] for p in xy[d < 0.3 + 0.8 * 1.2 + 0.8 + 0.3]]  # a trunk within the widest reach
        assert set(feet) & set(CFG["anthill"]["stands"]), o
    # a pond in the middle: the zone round it is swamp ground (the stand's reach), and no anthill stands in it
    pond = box(110, 110, 130, 130)
    reach = TREE["stands"]["swamp"]["reach_m"]
    wet, _ = plan(tmp_path / "wet", cfg=dense(), wet=pond)
    zone = pond.buffer(reach)
    assert not any(zone.contains(Point(o["x"], o["y"])) for o in wet if o["kind"] == "anthill")
    assert sum(o["kind"] == "anthill" for o in wet) > 0.5 * len(hills)  # elsewhere there are still as many


def test_burrows_gather_at_the_forest_edge_and_on_slopes_and_face_downhill(tmp_path: Path) -> None:
    objs, _ = plan(tmp_path, cfg=dense())
    burrows = [o for o in objs if o["kind"] == "burrow"]
    assert len(burrows) > 60
    lo, hi = CFG["burrow"]["size_m"]
    assert all(lo - 2e-3 <= o["scale"] * CFG["model"]["burrow"]["width_m"] <= hi + 2e-3 for o in burrows)
    edge = CFG["burrow"]["edge_m"]
    near = np.mean([min(o["x"] - 10, 240 - o["x"], o["y"] - 10, 240 - o["y"]) < edge for o in burrows])
    assert near > 0.38  # the edge band is a fifth of the canopy; weights 0.65 against 0.15 make about half
    # a ground that climbs in the west half only (a plane z = 100 + 0.2 x there): burrows prefer it, and look down it
    def climb(x, y):
        x, y = np.asarray(x, float), np.asarray(y, float)
        inside = (x >= 10) & (x <= 210) & (y >= 10) & (y <= 200)
        return 100 + 0.2 * np.minimum(x, 125.0) + 0.02 * y, inside

    steep = grid_from_samples(250.0, 4.0, COVERAGE, climb)
    objs, _ = plan(tmp_path / "steep", cfg=dense(), terrain=steep)
    west = [o for o in objs if o["kind"] == "burrow" and 20 < o["x"] < 115]
    east = [o for o in objs if o["kind"] == "burrow" and 135 < o["x"] < 210]
    assert len(west) > 1.8 * len(east)
    assert np.mean([math.cos(o["rot"]) for o in west]) < -0.85  # the hole faces -x, down the slope


def test_new_kinds_leave_the_rest_of_the_forest_as_it_was(tmp_path: Path) -> None:
    objs, _ = plan(tmp_path)
    old = json.loads(json.dumps(CFG))
    old["anthill"]["per_ha"], old["burrow"]["candidates_per_ha"] = 0, 0
    plain, _ = plan(tmp_path / "old", cfg=old)
    assert [o for o in objs if o["kind"] not in ("anthill", "burrow")] == plain
