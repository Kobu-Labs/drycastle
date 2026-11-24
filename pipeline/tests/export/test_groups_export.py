"""The export writes the property groups of the edits into groups.json (the game server's pens come from it)."""

import json

import pytest

from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.export.docs import build_groups
from drycastle_pipeline.export.export import run_export
from tests.synthetic import make_workspace


def write_edits(path, groups: dict):
    path.write_text(json.dumps({"version": 1, "groups": groups}))
    return path


def test_build_groups_keeps_the_parcels_of_the_map_in_the_shape_the_game_reads(tmp_path) -> None:
    edits = write_edits(tmp_path / "e.json", {
        "G-0002": {"parcels": ["P-0009", "P-0003"], "fence": {"type": "picket"}},
        "G-0003": {"parcels": ["P-0099"]},  # no parcel of this map
        "G-0001": {"parcels": ["P-0001", "P-0002"]},
    })
    doc = build_groups(edits, ["P-0001", "P-0002", "P-0003", "P-0009"])
    assert doc == {"version": 1, "groups": {"G-0001": {"parcels": ["P-0001", "P-0002"]}, "G-0002": {"parcels": ["P-0003", "P-0009"]}}}


def test_build_groups_is_none_without_groups(tmp_path) -> None:
    assert build_groups(tmp_path / "missing.json", ["P-0001"]) is None
    assert build_groups(write_edits(tmp_path / "e.json", {}), ["P-0001"]) is None


@pytest.fixture
def ws(tmp_path):
    return make_workspace(tmp_path)


def test_export_writes_groups_json_and_lists_it(ws, tmp_path) -> None:
    edits = write_edits(tmp_path / "edits.json", {"G-0001": {"parcels": ["P-0001"]}})
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=edits)
    assert manifest["files"]["groups"] == "groups.json"
    assert json.loads((ws["out"] / "groups.json").read_text()) == {"version": 1, "groups": {"G-0001": {"parcels": ["P-0001"]}}}


def test_export_without_groups_has_no_file_and_deletes_a_stale_one(ws, tmp_path) -> None:
    ws["out"].mkdir(parents=True, exist_ok=True)
    (ws["out"] / "groups.json").write_text("{}")
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=tmp_path / "none.json")
    assert "groups" not in manifest["files"]
    assert not (ws["out"] / "groups.json").exists()
