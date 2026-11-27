"""``building-run`` must return what a full scene build makes of the target parcel: the same nodes, and the same triangles."""

import io
import json

import pytest
import trimesh
import yaml
from shapely.geometry import box, mapping

from drycastle_pipeline.buildings.derive import (
    build_buildings,
    load_building_rules,
    write_buildings,
)
from drycastle_pipeline.buildings.openings import load_details
from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.edits import building_run
from drycastle_pipeline.export import scene as scene_mod
from drycastle_pipeline.terrain.terrain import flatten_buildings, load_terrain, save_terrain
from tests.synthetic import make_workspace

WINDOW = {"id": "a", "kind": "window", "edge": 0, "at_m": 2.0, "width_m": 1.0, "height_m": 1.2, "sill_m": 0.9}
DOOR = {"id": "d", "kind": "door", "edge": 0, "at_m": 6.0, "width_m": 0.95, "height_m": 2.05, "sill_m": 0.3, "steps": 1}
HOUSE_YAML = {"id": "w2", "interior": True, "openings": [WINDOW, DOOR]}
PLAIN_YAML = {"id": "w1", "openings": [WINDOW]}  # a detail, but no room inside: the house stays one of the parcel's merged nodes


@pytest.fixture
def ws(tmp_path):
    """The synthetic map with w1 (P-0001, plain) and w2 (P-0002, a house with a detail), derived like the rules stage."""
    paths = make_workspace(tmp_path)
    work = paths["work"]
    parcels = json.loads((work / "parcels.json").read_text())
    by_id = {p["id"]: p for p in parcels["parcels"]}
    by_id["P-0001"]["buildings"], by_id["P-0002"]["buildings"] = ["w1"], ["w2"]
    (work / "parcels.json").write_text(json.dumps(parcels))
    feats = [
        {"type": "Feature", "properties": {"osm_id": "w1", "tags": {"roof:shape": "gabled"}}, "geometry": mapping(box(60, 55, 75, 70))},
        {"type": "Feature", "properties": {"osm_id": "w2", "tags": {}}, "geometry": mapping(box(100, 50, 112, 58))},
    ]
    (work / "normalised" / "buildings.geojson").write_text(json.dumps({"features": feats}))
    details = tmp_path / "details"
    details.mkdir()
    (details / "w2.yaml").write_text(yaml.safe_dump(HOUSE_YAML))
    overrides = tmp_path / "overrides.yaml"
    overrides.write_text("buildings:\n  w1: {wall_colour: '#102030'}\n")
    result = build_buildings(work, load_building_rules(), {"w1": {"wall_colour": "#102030"}})
    write_buildings(work, result)
    terrain, _ = load_terrain(work)
    save_terrain(work, terrain, flatten_buildings(terrain, result.buildings, 2.0, 6.0))
    edits = tmp_path / "edits.json"
    edits.write_text(json.dumps({"version": 1}))
    return {"work": work, "details": details, "overrides": overrides, "edits": edits, "tmp": tmp_path}


def full_scene(ws, monkeypatch):
    monkeypatch.setattr(scene_mod, "load_details", lambda: load_details(ws["details"]))
    return scene_mod.build_scene(ws["work"], DEFAULT_RULES, edits_path=ws["edits"])


def world(ws):
    return building_run.BuildingWorld(ws["work"], DEFAULT_RULES, ws["overrides"], ws["details"])


def share_of_full_scene(scene, parcel: str, bids: list[str]) -> dict[str, int]:
    """The nodes (and their triangle counts) of one parcel in a full scene: its merged nodes, its hollow houses', its window panes."""
    out = {}
    for items in scene.parcels.values():
        for pid, v, c in items:
            if pid == parcel:
                out |= {f"parcel_{pid}_visual": v.face_count, f"parcel_{pid}_collision": c.face_count}
    for named in scene.nodes.values():
        out |= {n: m.face_count for n, m in named.items() if any(n.startswith((f"house_{b}_", f"window_{b}_")) for b in bids)}
    return out


@pytest.mark.parametrize(("parcel", "bids", "hollow"), [("P-0001", ["w1"], False), ("P-0002", ["w2"], True)])
def test_fragment_has_the_nodes_of_a_full_scene(ws, monkeypatch, tmp_path, parcel, bids, hollow):
    if hollow:
        pytest.importorskip("drycastle_detail", reason="the floor plan engine: make detail-rust-build")
    expected = share_of_full_scene(full_scene(ws, monkeypatch), parcel, bids)
    glb = tmp_path / "b.glb"
    frag = world(ws).build([parcel], glb)

    assert frag["triangles"] == expected
    assert sorted(frag["nodes"]) == sorted(expected)
    assert set(expected) <= set(frag["replaces"])  # what the viewer hides covers what a full build has
    assert {f"parcel_{parcel}_visual", f"parcel_{parcel}_collision"} <= set(frag["replaces"])
    assert frag["buildings"] == {parcel: bids}
    assert any(n.startswith(f"house_{bids[0]}_") for n in frag["nodes"]) == hollow  # a hollow house is its own node, not in the parcel's
    assert (f"parcel_{parcel}_visual" in frag["nodes"]) != hollow
    if hollow:
        assert any(n.startswith(f"window_{bids[0]}_") for n in frag["nodes"])
    # the GLB carries exactly those nodes, in glTF axes (y up)
    nodes = trimesh.load(glb, force="scene")
    assert sorted(nodes.geometry) == sorted(expected)
    assert nodes.bounds[0][1] >= 0.0 and nodes.bounds[1][1] > 3.0


def test_the_house_stands_on_the_terrain_like_in_the_full_export(ws, monkeypatch, tmp_path):
    scene = full_scene(ws, monkeypatch)
    frag = world(ws).build(["P-0001"], tmp_path / "b.glb")
    (_, vis, _), = [it for items in scene.parcels.values() for it in items if it[0] == "P-0001"]
    got = trimesh.load(tmp_path / "b.glb", force="scene").geometry["parcel_P-0001_visual"]
    assert got.bounds[1][1] == pytest.approx(vis.tris[..., 2].max(), abs=1e-3)  # the roof top, y up
    assert frag["timing_s"]["total"] >= 0


def test_an_override_edit_changes_only_its_parcel(ws, tmp_path):
    w = world(ws)
    before = w.build(["P-0001"], tmp_path / "a.glb")
    ws["overrides"].write_text("buildings:\n  w1: {wall_colour: '#a0b0c0'}\n")
    after = w.build(["P-0001"], tmp_path / "b.glb")
    assert before["triangles"] == after["triangles"]
    assert (tmp_path / "a.glb").read_bytes() != (tmp_path / "b.glb").read_bytes()  # the colour
    base = ws["tmp"] / "base.yaml"
    base.write_text("buildings:\n  w1: {wall_colour: '#102030'}\n  P-0002: {roof: flat}\n")
    parcels, warnings = w.changed_parcels(base)  # w1 by its id, P-0002 by its parcel key, a stray key warns
    assert parcels == ["P-0001", "P-0002"] and warnings == []
    base.write_text("buildings:\n  w1: {wall_colour: '#a0b0c0'}\n  w999: {roof: flat}\n")
    assert w.changed_parcels(base) == ([], ["override w999 matches no parcel or building"])


def test_a_detail_file_edit_is_seen_and_a_broken_one_is_an_error(ws, tmp_path):
    w = world(ws)
    assert "window_w1_a" not in w.build(["P-0001"])["nodes"]
    (ws["details"] / "w1.yaml").write_text(yaml.safe_dump(PLAIN_YAML))
    assert "window_w1_a" in w.build(["P-0001"])["nodes"]
    (ws["details"] / "w1.yaml").write_text("id: w1\nfrobnicate: 1\n")
    with pytest.raises(Exception, match="unknown field"):
        w.build(["P-0001"])
    (ws["details"] / "w1.yaml").unlink()
    assert "window_w1_a" not in w.build(["P-0001"])["nodes"]


def test_unknown_targets_are_errors(ws):
    w = world(ws)
    with pytest.raises(building_run.TargetError):
        w.build(["P-9999"])
    with pytest.raises(building_run.TargetError):
        w.parcel_for("w404")


def test_the_worker_answers_json_lines_and_survives_a_bad_request(ws, tmp_path):
    ws_args = {"glb": str(tmp_path / "s.glb")}
    base = ws["tmp"] / "base.yaml"
    base.write_text("buildings: {}\n")
    lines = [
        {"id": 1, **ws_args, "parcels": ["P-0001"]},
        {"id": 2, **ws_args, "overrides": str(ws["overrides"]), "overrides_base": str(base)},  # w1's entry is new: P-0001
        {"id": 3, **ws_args, "files": ["w1.yaml"]},
        {"id": 4, **ws_args, "parcels": ["P-9999"]},
        {"id": 5, **ws_args, "files": ["nothing.yaml"]},
        {"op": "quit"},
    ]
    out = io.StringIO()
    code = building_run.serve(io.StringIO("".join(json.dumps(x) + "\n" for x in lines)), out, work=str(ws["work"]), world=world(ws))
    assert code == 0
    ready, *answers = [json.loads(x) for x in out.getvalue().splitlines()]
    assert ready["ready"] is True
    by = {a["id"]: a for a in answers}
    assert by[1]["ok"] and by[1]["fragment"]["parcels"] == ["P-0001"] and (tmp_path / "s.glb").is_file()
    assert by[2]["ok"] and by[2]["fragment"]["parcels"] == ["P-0001"]
    assert by[3]["ok"] and by[3]["fragment"]["parcels"] == ["P-0001"]
    assert not by[4]["ok"] and "P-9999" in by[4]["error"]
    assert by[5]["ok"] and by[5]["fragment"]["parcels"] == [] and "glb" not in by[5]["fragment"]  # nothing to rebuild
    assert any("nothing" in w for w in by[5]["fragment"]["warnings"])
