import json

import numpy as np
import pytest
import trimesh

from drycastle_pipeline.export import run_export
from drycastle_pipeline.props import run_props
from drycastle_pipeline.rules import DEFAULT_RULES
from tests.synthetic import make_workspace


@pytest.fixture
def ws(tmp_path):
    ws = make_workspace(tmp_path)
    run_props(ws["work"], DEFAULT_RULES)
    return ws


def export(ws, out=None):
    # no edits file: the synthetic world must not pick up the groups and edits of the repo's own pipeline/config/edits.json
    return run_export(ws["work"], out or ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["out"].parent / "no_edits.json")


def test_chunk_glb_has_all_node_groups(ws) -> None:
    manifest = export(ws)
    assert [c["file"] for c in manifest["chunks"]] == ["chunks/chunk_0_0.glb"]
    scene = trimesh.load(ws["out"] / "chunks" / "chunk_0_0.glb", force="scene")
    names = sorted(scene.graph.nodes_geometry)
    assert {
        "parcel_P-0001_collision",
        "parcel_P-0001_visual",
        "roads",
        "terrain",
        "terrain_collision",
    } <= set(names)
    assert any(n.startswith("parcel_") and n.endswith("_props") for n in names)
    tris = manifest["chunks"][0]["triangles"]
    for name in names:
        geom = scene.geometry[scene.graph[name][1]]
        assert len(geom.faces) == tris[name] > 0
    # collision terrain is coarser than the visual terrain
    assert tris["terrain_collision"] < tris["terrain"]
    # terrain is y-up glTF: heights 100..~120 m, z = -north
    t = scene.geometry[scene.graph["terrain"][1]]
    assert t.bounds[0][1] > 100 and t.bounds[1][1] < 125
    assert t.bounds[0][2] >= -200 and t.bounds[1][2] <= -10


def test_buildings_sit_on_flattened_ground(ws) -> None:
    export(ws)
    scene = trimesh.load(ws["out"] / "chunks" / "chunk_0_0.glb", force="scene")
    elev = json.loads((ws["work"] / "terrain.json").read_text())["building_elevation"]["w1"]
    lo = scene.geometry[scene.graph["parcel_P-0001_visual"][1]].bounds[0]
    assert lo[1] == pytest.approx(elev, abs=1e-3)
    terrain = scene.geometry[scene.graph["terrain"][1]]
    # terrain vertices inside the footprint (x 60..75, north 55..70 -> z -70..-55) are at elev
    v = terrain.vertices
    inside = (v[:, 0] > 60) & (v[:, 0] < 75) & (v[:, 2] < -55) & (v[:, 2] > -70)
    assert inside.any() and np.abs(v[inside, 1] - elev).max() < 0.02


def test_roads_json_spawns_json_and_manifest(ws) -> None:
    manifest = export(ws)
    out = ws["out"]
    roads = json.loads((out / "roads.json").read_text())
    assert {"nodes", "edges"} <= set(roads) and len(roads["edges"]) == 3
    spawns = json.loads((out / "spawns.json").read_text())["spawns"]
    assert spawns and all({"id", "x", "y", "z", "kind", "chunk"} <= set(s) for s in spawns)
    assert manifest["files"] == {
        "objects": "objects.json",
        "roads": "roads.json",
        "spawns": "spawns.json",
        "water": "water.json",
        "fences": "fences.json",
        "lamps": "lamps.json",
        "trees": "trees.json",
        "ground_objects": "ground_objects.json",
    }
    assert {"terrain", "roads", "fences", "trees", "spawns", "buildings", "water"} <= set(
        manifest["generated"]
    )
    assert manifest["terrain"]["cells_per_chunk"] % 2 == 0
    for name in ("terrain.json", "terrain_heights.npy", "roads.json", "props.json", "spawns.json"):
        assert len(manifest["inputs"][f"work/{name}"]) == 64
    assert not any("time" in k or "date" in k for k in manifest)


def test_fences_json_is_copied_from_the_props_stage_and_a_stale_one_goes(ws) -> None:
    manifest = export(ws)
    assert manifest["files"]["fences"] == "fences.json"
    assert (ws["out"] / "fences.json").read_bytes() == (ws["work"] / "fences.json").read_bytes()
    (ws["work"] / "fences.json").unlink()
    manifest = export(ws)
    assert "fences" not in manifest["files"] and not (ws["out"] / "fences.json").exists()


def test_export_without_optional_stages_still_works(tmp_path) -> None:
    ws = make_workspace(tmp_path)
    for name in ("terrain.json", "terrain_heights.npy", "terrain_valid.npy", "roads.json"):
        (ws["work"] / name).unlink()
    manifest = export(ws)
    assert manifest["generated"] == ["buildings"] and manifest["files"] == {}
    scene = trimesh.load(ws["out"] / "chunks" / "chunk_0_0.glb", force="scene")
    assert sorted(scene.graph.nodes_geometry) == ["parcel_P-0001_collision", "parcel_P-0001_visual"]


def test_export_is_byte_deterministic_across_runs(ws, tmp_path) -> None:
    export(ws)
    second = tmp_path / "out2"
    export(ws, second)
    first = ws["out"]
    files = sorted(p.relative_to(first) for p in first.rglob("*") if p.is_file())
    assert "roads.json" in map(str, files) and "chunks/chunk_0_0.glb" in map(str, files)
    assert files == sorted(p.relative_to(second) for p in second.rglob("*") if p.is_file())
    for rel in files:
        assert (first / rel).read_bytes() == (second / rel).read_bytes(), rel


def test_props_are_per_parcel_nodes_with_unchanged_totals(ws) -> None:
    from drycastle_pipeline.propmeshes import fence_meshes, ground_objects_mesh, trees_mesh
    from drycastle_pipeline.rules import load_section

    manifest = export(ws)
    assert manifest["props_layout"] == "per_parcel"
    tris = manifest["chunks"][0]["triangles"]
    props = json.loads((ws["work"] / "props.json").read_text())
    pcfg = load_section("props", DEFAULT_RULES)
    ids = sorted({x["parcel"] for x in props["fences"] + props["trees"] if x.get("parcel")})
    assert ids and all(f"parcel_{i}_props" in tris for i in ids)
    # triangle totals equal what the merged chunk-level nodes held before
    # (typed fences add flat posts and caps to the visual; their textured parts are separate nodes)
    want_visual = fence_meshes(props["fences"], pcfg["fences"])[0].face_count + trees_mesh(
        props["trees"], pcfg["trees"]
    ).face_count + ground_objects_mesh(props["ground_objects"], pcfg["ground_objects"]).face_count  # no parcel: chunk level
    got_visual = sum(v for n, v in tris.items() if n == "props" or n.endswith("_props"))
    got_collision = sum(v for n, v in tris.items() if n.endswith("props_collision"))
    assert got_visual == want_visual > 0
    assert got_collision == 0  # the map ships with fence collision off
    # fences always have a parcel; OSM trees (no parcel) stay in the chunk-level node
    assert all(f["parcel"] for f in props["fences"])
    osm = [t for t in props["trees"] if not t.get("parcel")]
    assert ("props" in tris) == bool(osm)
    scene = trimesh.load(ws["out"] / "chunks" / "chunk_0_0.glb", force="scene")
    for name in tris:
        if name.endswith(("_props", "_props_collision")):
            assert len(scene.geometry[scene.graph[name][1]].faces) == tris[name]


def test_baked_trees_carry_their_base_as_second_uv_set(ws) -> None:
    import struct

    export(ws)
    assert json.loads((ws["out"] / "trees.json").read_text())["model"]["base_uv2"] is True
    data = (ws["out"] / "chunks" / "chunk_0_0.glb").read_bytes()
    doc = json.loads(data[20 : 20 + struct.unpack_from("<I", data, 12)[0]])
    attrs = {m["name"]: m["primitives"][0]["attributes"] for m in doc["meshes"]}
    trees = json.loads((ws["work"] / "props.json").read_text())["trees"]
    # a tree's parcel props mesh (or the chunk-level one, for trees without a parcel) has TEXCOORD_1
    with_trees = {f"parcel_{t['parcel']}_props" if t.get("parcel") else "props" for t in trees}
    assert with_trees
    for name in with_trees:
        assert "TEXCOORD_1" in attrs[name], name
    assert not any("_UV2" in a for a in attrs.values())


def test_forest_colour_fades_into_its_neighbour_over_the_blend_band() -> None:
    from drycastle_pipeline.scene import ParcelColours

    sq = lambda x0, x1: {"type": "Polygon", "coordinates": [[[x0, 0], [x1, 0], [x1, 100], [x0, 100], [x0, 0]]]}
    units = [{"id": "A-1", "kind": "forest", "polygon": sq(0, 100)}, {"id": "A-2", "kind": "field", "polygon": sq(100, 200)}]
    palette = {"forest": ["#204020"], "field": ["#c0c080"], "other": ["#808080"]}
    pts = np.array([[50.0, 50.0], [94.0, 50.0], [100.5, 50.0], [106.0, 50.0], [150.0, 50.0]])
    flat = ParcelColours(units, palette)(pts)
    soft = ParcelColours(units, palette, {"kinds": ["forest"], "radius_m": 12})(pts)
    assert (flat[:2] == flat[0]).all() and (flat[3:] == flat[4]).all()  # a line without the blend
    assert (soft[0] == flat[0]).all() and (soft[4] == flat[4]).all()  # far from the outline: untouched
    red = soft[:, 0].astype(int)
    assert red[0] < red[1] < red[2] < red[3] < red[4]  # a ramp across the outline


def test_swamp_ground_is_terrain_colour_and_fades_at_its_outline() -> None:
    from shapely.geometry import box

    from drycastle_pipeline.scene import ParcelColours

    sq = lambda x0, x1: {"type": "Polygon", "coordinates": [[[x0, 0], [x1, 0], [x1, 100], [x0, 100], [x0, 0]]]}
    units = [{"id": "A-1", "kind": "forest", "polygon": sq(0, 200)}, {"id": "A-2", "kind": "road", "polygon": sq(200, 300)}]
    palette = {"forest": ["#30a030"], "road": ["#808080"], "other": ["#808080"]}
    swamp = {"zone": box(-10, -10, 100, 310).union(box(240, -10, 310, 110)), "kinds": ["forest"], "colours": ["#402010"]}
    pts = np.array([[50.0, 50.0], [95.0, 50.0], [100.5, 50.0], [106.0, 50.0], [150.0, 50.0], [270.0, 50.0]])
    col = ParcelColours(units, palette, {"kinds": [], "radius_m": 12}, swamp)(pts)
    assert col[0].tolist() == [0x40, 0x20, 0x10] and col[4].tolist() == [0x30, 0xA0, 0x30]  # wet inside the zone, plain forest outside
    green = col[:5, 1].astype(int)
    assert green[0] < green[1] < green[2] < green[3] < green[4]  # a ramp across the zone's outline
    assert col[5].tolist() == [0x80, 0x80, 0x80]  # ground of another kind in the zone keeps its colour
