import json

import numpy as np
import pytest
import trimesh

from suchohrad_pipeline.export import run_export
from suchohrad_pipeline.props import run_props
from suchohrad_pipeline.rules import DEFAULT_RULES
from tests.synthetic import make_workspace


@pytest.fixture
def ws(tmp_path):
    ws = make_workspace(tmp_path)
    run_props(ws["work"], DEFAULT_RULES)
    return ws


def export(ws, out=None):
    return run_export(ws["work"], out or ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"])


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
    assert any(n.endswith("_props_collision") for n in names)
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
    assert manifest["files"] == {"roads": "roads.json", "spawns": "spawns.json"}
    assert {"terrain", "roads", "fences", "trees", "spawns", "buildings"} <= set(
        manifest["generated"]
    )
    assert manifest["terrain"]["cells_per_chunk"] % 2 == 0
    for name in ("terrain.json", "terrain_heights.npy", "roads.json", "props.json", "spawns.json"):
        assert len(manifest["inputs"][f"work/{name}"]) == 64
    assert not any("time" in k or "date" in k for k in manifest)


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
    from suchohrad_pipeline.propmeshes import fence_mesh, trees_mesh
    from suchohrad_pipeline.rules import load_section

    manifest = export(ws)
    assert manifest["props_layout"] == "per_parcel"
    tris = manifest["chunks"][0]["triangles"]
    props = json.loads((ws["work"] / "props.json").read_text())
    pcfg = load_section("props", DEFAULT_RULES, "slice")
    ids = sorted({x["parcel"] for x in props["fences"] + props["trees"] if x.get("parcel")})
    assert ids and all(f"parcel_{i}_props" in tris for i in ids)
    # triangle totals equal what the merged chunk-level nodes held before
    want_visual = fence_mesh(props["fences"], pcfg["fences"]).face_count + trees_mesh(
        props["trees"], pcfg["trees"]
    ).face_count
    want_collision = fence_mesh(props["fences"], pcfg["fences"], True).face_count
    got_visual = sum(v for n, v in tris.items() if n == "props" or n.endswith("_props"))
    got_collision = sum(v for n, v in tris.items() if n.endswith("props_collision"))
    assert got_visual == want_visual > 0
    assert got_collision == want_collision > 0
    # fences always have a parcel; OSM trees (no parcel) stay in the chunk-level node
    assert all(f["parcel"] for f in props["fences"])
    osm = [t for t in props["trees"] if not t.get("parcel")]
    assert ("props" in tris) == bool(osm)
    scene = trimesh.load(ws["out"] / "chunks" / "chunk_0_0.glb", force="scene")
    for name in tris:
        if name.endswith("_props") or name.endswith("_props_collision"):
            assert len(scene.geometry[scene.graph[name][1]].faces) == tris[name]
