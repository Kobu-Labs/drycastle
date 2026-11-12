"""``parcel-run`` must return exactly the target parcels' share of the full props stage."""

import hashlib
import json
from types import SimpleNamespace

import pytest

from drycastle_pipeline import cli, parcel_run
from drycastle_pipeline.edits import EditsError
from drycastle_pipeline.props import run_props
from drycastle_pipeline.rules import DEFAULT_RULES
from tests.synthetic import SYNTHETIC_ORIGIN, make_workspace

OX, OY = SYNTHETIC_ORIGIN

NO_EDITS = {"version": 1}
EDITS = {
    "version": 1,
    "parcels": {
        "P-0001": {
            "fence": {
                "mode": "custom",
                "type": "wire",
                "gaps": [{"at": [OX + 40, OY + 70], "width_m": 3.0}],
            },
            "objects": [{"id": "car-1", "asset": "prim:box", "position": [OX + 50, OY + 50]}],
        },
        "P-0006": {"fence": {"mode": "none"}},
    },
    # P-0001 and P-0002 share a boundary: the group fence runs on the outer edge only.
    "groups": {"G-0001": {"parcels": ["P-0001", "P-0002"], "fence": {"mode": "custom"}}},
}
RULES_CAP = "props:\n  fences: {probability: 1.0}\n  trees: {max_per_chunk: 12}\n"
RULES_NOCAP = "props:\n  fences: {probability: 1.0}\n"


@pytest.fixture
def ws(tmp_path):
    return make_workspace(tmp_path)["work"]


def write(tmp_path, name, text):
    path = tmp_path / name
    path.write_text(text if isinstance(text, str) else json.dumps(text))
    return path


def full_run(work, edits_path, rules_path):
    run_props(work, rules_path, area="slice", edits_path=edits_path)
    props = json.loads((work / "props.json").read_text())
    objects = json.loads((work / "objects.json").read_text())["objects"]
    return props["fences"], props["trees"], objects


def fragment(work, edits_path, rules_path, parcels=(), groups=()):
    return parcel_run.run_parcel_run(
        work, "slice", list(parcels), list(groups), edits_path, rules_path
    )


CASES = [
    (["P-0001"], []),
    (["P-0002"], []),
    (["P-0003"], []),  # road
    (["P-0004"], []),  # field
    (["P-0005"], []),  # forest
    (["P-0006"], []),
    (["P-0001", "P-0005"], []),
    ([], ["G-0001"]),
]


@pytest.mark.parametrize("edits_doc", [NO_EDITS, EDITS], ids=["no-edits", "edits"])
@pytest.mark.parametrize("rules_text", [RULES_NOCAP, RULES_CAP], ids=["no-cap", "cap"])
@pytest.mark.parametrize(("parcels", "groups"), CASES)
def test_fragment_equals_share_of_full_run(ws, tmp_path, edits_doc, rules_text, parcels, groups):
    if groups and not edits_doc.get("groups"):
        pytest.skip("no groups without edits")
    edits_path = write(tmp_path, "edits.json", edits_doc)
    rules_path = write(tmp_path, "rules.yaml", rules_text)
    fences, trees, objects = full_run(ws, edits_path, rules_path)
    got = fragment(ws, edits_path, rules_path, parcels, groups)
    ids = set(got["parcels"])
    assert got["fences"] == [s for s in fences if s["parcel"] in ids]
    assert got["trees"] == [t for t in trees if t["parcel"] in ids]
    assert got["objects"] == [o for o in objects if o["parcel"] in ids]
    assert got["format_version"] == 1
    assert set(got["timing_s"]) == {"load", "compute", "total"}


def test_cases_are_not_vacuous(ws, tmp_path):
    """The fixtures produce fences, trees, a de-duplicated shared boundary and an active cap."""
    edits_path = write(tmp_path, "edits.json", NO_EDITS)
    capped = full_run(ws, edits_path, write(tmp_path, "cap.yaml", RULES_CAP))
    free = full_run(ws, edits_path, write(tmp_path, "free.yaml", RULES_NOCAP))
    assert capped[0] and free[1]
    assert len(capped[1]) < len(free[1])  # the cap dropped trees
    got = fragment(ws, edits_path, write(tmp_path, "cap.yaml", RULES_CAP), ["P-0005"])
    assert got["trees"] and len(got["trees"]) < len([t for t in free[1] if t["parcel"] == "P-0005"])


def test_group_member_pulls_in_the_whole_group(ws, tmp_path):
    edits_path = write(tmp_path, "edits.json", EDITS)
    rules_path = write(tmp_path, "rules.yaml", RULES_NOCAP)
    by_member = fragment(ws, edits_path, rules_path, ["P-0002"])
    by_group = fragment(ws, edits_path, rules_path, groups=["G-0001"])
    assert by_member["parcels"] == ["P-0001", "P-0002"]
    assert by_member["fences"] == by_group["fences"]
    assert {s["parcel"] for s in by_member["fences"]} == {"P-0001", "P-0002"}


def test_objects_listed_under_a_target_are_returned(ws, tmp_path):
    edits_path = write(tmp_path, "edits.json", EDITS)
    got = fragment(ws, edits_path, write(tmp_path, "r.yaml", RULES_NOCAP), ["P-0001"])
    assert [o["id"] for o in got["objects"]] == ["car-1"]


def test_unknown_targets_are_rejected(ws, tmp_path):
    edits_path = write(tmp_path, "edits.json", EDITS)
    rules_path = write(tmp_path, "r.yaml", RULES_NOCAP)
    with pytest.raises(parcel_run.TargetError, match="P-9999"):
        fragment(ws, edits_path, rules_path, ["P-9999"])
    with pytest.raises(parcel_run.TargetError, match="G-0042"):
        fragment(ws, edits_path, rules_path, groups=["G-0042"])


def test_cli_writes_json_and_leaves_work_files_alone(ws, tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(parcel_run, "area_paths", lambda area: SimpleNamespace(work=ws))
    edits_path = write(tmp_path, "edits.json", EDITS)

    def digests():
        return {
            p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in ws.iterdir() if p.is_file()
        }

    before = digests()
    argv = ["parcel-run", "--parcel", "P-0003", "--group", "G-0001", "--edits", str(edits_path)]
    assert cli.main(argv) == 0
    doc = json.loads(capsys.readouterr().out)
    assert doc["parcels"] == ["P-0001", "P-0002", "P-0003"]
    out = tmp_path / "frag.json"
    assert cli.main([*argv, "--out", str(out)]) == 0
    assert json.loads(out.read_text())["fences"] == doc["fences"]
    assert digests() == before


def test_cli_errors(ws, tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(parcel_run, "area_paths", lambda area: SimpleNamespace(work=ws))
    assert cli.main(["parcel-run"]) == 2
    assert cli.main(["parcel-run", "--parcel", "P-9999"]) == 1
    assert "P-9999" in capsys.readouterr().err
    bad = write(tmp_path, "bad.json", {"version": 2})
    assert cli.main(["parcel-run", "--parcel", "P-0001", "--edits", str(bad)]) == 1
    assert isinstance(EditsError([]), ValueError)


def test_default_rules_are_a_valid_rules_path():
    assert DEFAULT_RULES.is_file()


def test_glb_nodes_match_the_export_per_parcel_nodes(tmp_path):
    import numpy as np
    import trimesh

    from drycastle_pipeline.export import run_export

    full = make_workspace(tmp_path)
    work = full["work"]
    edits_path = write(tmp_path, "edits.json", EDITS)
    rules_path = write(tmp_path, "rules.yaml", RULES_NOCAP)
    run_props(work, rules_path, area="slice", edits_path=edits_path)
    manifest = run_export(work, tmp_path / "map", full["raw"], rules_path, full["overrides"])
    exported = {
        c["file"]: trimesh.load(tmp_path / "map" / c["file"], force="scene")
        for c in manifest["chunks"]
    }
    baked = {}  # node name -> trimesh geometry, over all chunk files
    for scene in exported.values():
        for name in scene.graph.nodes_geometry:
            baked[name] = scene.geometry[scene.graph[name][1]]

    targets = ["P-0001", "P-0002", "P-0003", "P-0004", "P-0005", "P-0006"]
    checked = 0
    for pid in targets:
        glb = tmp_path / f"{pid}.glb"
        frag = parcel_run.run_parcel_run(
            work, "slice", [pid], [], edits_path, rules_path, glb_path=glb
        )
        assert frag["glb"] == str(glb) and frag["replaces"] == frag["parcels"]
        scene = trimesh.load(glb, force="scene")
        names = sorted(scene.graph.nodes_geometry)
        # the flat props node, its collision and one "_props_tex_<texture>" node per texture used
        want = sorted(n for n in baked if any(
            n in (f"parcel_{p}_props", f"parcel_{p}_props_collision")
            or n.startswith(f"parcel_{p}_props_tex_") for p in frag["parcels"]))
        assert names == want, pid
        for name in names:
            got = scene.geometry[scene.graph[name][1]]
            assert len(got.faces) == len(baked[name].faces) > 0
            assert np.allclose(got.vertices, baked[name].vertices)
            checked += 1
    assert checked >= 4  # not vacuous: several parcels have fences and trees


def test_glb_of_a_parcel_without_props_still_names_it_in_replaces(ws, tmp_path):
    import trimesh

    # P-0003 is village ground: the rules plant no trees on it, and its fence is switched off
    edits_path = write(tmp_path, "e.json", {**NO_EDITS, "parcels": {"P-0003": {"fence": {"mode": "none"}}}})
    rules_path = write(tmp_path, "r.yaml", RULES_NOCAP)
    glb = tmp_path / "out" / "x.glb"
    frag = parcel_run.run_parcel_run(ws, "slice", ["P-0003"], [], edits_path, rules_path, glb_path=glb)
    assert frag["replaces"] == ["P-0003"] and frag["fences"] == [] and frag["trees"] == []
    assert not trimesh.load(glb, force="scene").graph.nodes_geometry


def test_cli_glb_option_and_group_expansion(ws, tmp_path, monkeypatch, capsys):
    import trimesh

    monkeypatch.setattr(parcel_run, "area_paths", lambda area: SimpleNamespace(work=ws))
    edits_path = write(tmp_path, "edits.json", EDITS)
    glb = tmp_path / "g.glb"
    argv = ["parcel-run", "--group", "G-0001", "--edits", str(edits_path), "--glb", str(glb)]
    assert cli.main(argv) == 0
    frag = json.loads(capsys.readouterr().out)
    assert frag["glb"] == str(glb) and frag["replaces"] == ["P-0001", "P-0002"]
    assert trimesh.load(glb, force="scene").graph.nodes_geometry
