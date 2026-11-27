"""The building edit API (`edits/building_edit.py`): every op on a tmp overrides file and detail file, minimal diffs, atomic writes,
an invalid edit writes nothing, undo by snapshot, and the worker's op dispatch."""

import io
import json
import os
from pathlib import Path

import pytest
import yaml
from shapely.geometry import box, mapping

from drycastle_pipeline.buildings.derive import (
    build_buildings,
    load_building_rules,
    write_buildings,
)
from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.edits import building_edit as be
from drycastle_pipeline.edits import building_run
from tests.synthetic import make_workspace

HEAD = "# Manual corrections.\n# (a second comment line)\nkeep_traced_footprint: [w1, w2]\n\nbuildings:\n"
W1_BLOCK = '  # P-0001: a gabled house, measured.\n  w1:\n    roof: gabled\n    height_m: 7.0\n    roof_height_m: 3.0\n    wall_colour: "#102030"\n'
OTHER = '\n  # P-0009: somebody else.\n  w9:\n    roof: flat\n    height_m: 4.0\n'
OVERRIDES = HEAD + W1_BLOCK + OTHER
DETAIL = """# P-0002: house. Measured by hand.
# Wall top 3.00 m.
id: w2
source: "test"
overhang_m: 0.35
frame_colour: '#3a3a3c'
plinth: {height_m: 0.3, colour: '#6b5a50'}
eaves: {colour: '#3a3230', fascia_m: 0.18, gutter: false}
downpipes:
- {edge: 1, at_m: 0.2}
# the doors
interior: true
openings:
  - {id: w1, kind: window, edge: 0, at_m: 2.0, width_m: 1.0, height_m: 1.2, sill_m: 0.9}
  # a door
  - {id: d1, kind: door, edge: 0, at_m: 5.0, width_m: 0.95, height_m: 2.05, sill_m: 0.0, steps: 1}
"""


@pytest.fixture
def ws(tmp_path):
    """w1 (P-0001, gabled, an override entry, no detail), w2 (P-0002, a detail, no entry): the synthetic map, derived."""
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
    write_buildings(work, build_buildings(work, load_building_rules(), {}))
    details, photos = tmp_path / "details", tmp_path / "photos"
    details.mkdir()
    (details / "w2.yaml").write_text(DETAIL)
    overrides = tmp_path / "overrides.yaml"
    overrides.write_text(OVERRIDES)
    return {"work": work, "details": details, "photos": photos, "overrides": overrides}


@pytest.fixture
def world(ws):
    return building_run.BuildingWorld(ws["work"], DEFAULT_RULES, ws["overrides"], ws["details"], ws["photos"])


def edit(world, bid, change):
    return be.edit(world, bid, change)


def rows(ws, bid="w2"):
    return {o["id"]: o for o in yaml.safe_load((ws["details"] / f"{bid}.yaml").read_text())["openings"]}


# ---- state ----


def test_state_of_a_building_with_an_override_and_one_with_a_detail(world, ws):
    s1, s2 = be.state(world, "w1"), be.state(world, "w2")
    assert (s1["parcel"], s1["has_override"], s1["has_detail"], s1["detail"]) == ("P-0001", True, False, None)
    assert s1["override"] == {"roof": "gabled", "height_m": 7.0, "roof_height_m": 3.0, "wall_colour": "#102030"}
    assert s1["form"] == {"roof": "gabled", "wall_top_m": 4.0, "top_m": 7.0}
    assert (s2["has_override"], s2["has_detail"], s2["override"], s2["measured"]) == (False, True, {}, False)
    d = s2["detail"]
    assert (d["overhang_m"], d["plinth"], d["gutter"], d["interior"], d["corners"], d["bands"]) == (0.35, {"height_m": 0.3, "colour": "#6b5a50"}, False, True, False, False)
    assert d["surround_colour"] == "#f2f0ea" and d["ridge_caps"] is False
    assert [o["id"] for o in d["openings"]] == ["w1", "d1"]
    assert d["openings"][0]["confidence"] == "medium" and d["openings"][0]["sill_m"] == 0.9
    assert [e["i"] for e in s2["edges"]] == [0, 1, 2, 3]
    e0 = s2["edges"][0]  # (112, 50) -> (112, 58): the outside is east
    assert e0["length"] == pytest.approx(8.0) and e0["normal"] == pytest.approx([1.0, 0.0])
    assert s2["form"]["roof"] in ("flat", "gabled", "hipped") and "anthracite" in s2["palettes"]["roof"] and "white" in s2["palettes"]["wall"]
    assert isinstance(s2["base_z"], float)


def test_a_flat_roof_has_its_wall_top_at_the_top(world, ws):
    f = be.state(world, "w2")["form"]
    if f["roof"] == "flat":
        assert f["wall_top_m"] == f["top_m"]


def test_unknown_building_is_an_error(world):
    with pytest.raises(building_run.TargetError):
        be.state(world, "w404")


# ---- the form ----


def test_the_wall_top_keeps_the_ridge_and_the_ridge_keeps_the_wall_top(world, ws):
    out = edit(world, "w1", {"form": {"wall_top_m": 4.5}})
    assert out["state"]["override"] == {"roof": "gabled", "height_m": 7.0, "roof_height_m": 2.5, "wall_colour": "#102030"}
    assert out["state"]["form"] == {"roof": "gabled", "wall_top_m": 4.5, "top_m": 7.0}
    out = edit(world, "w1", {"form": {"top_m": 8.123}})
    assert out["state"]["override"]["height_m"] == 8.12 and out["state"]["override"]["roof_height_m"] == 3.62
    assert out["state"]["form"] == {"roof": "gabled", "wall_top_m": 4.5, "top_m": 8.12}
    out = edit(world, "w1", {"form": {"wall_top_m": 3.0, "top_m": 6.0}})
    assert (out["state"]["override"]["height_m"], out["state"]["override"]["roof_height_m"]) == (6.0, 3.0)


def test_a_ridge_too_close_to_the_wall_top_changes_nothing(world, ws):
    before = ws["overrides"].read_text()
    with pytest.raises(be.BuildingEditError, match="at least 0.3 m under the ridge"):
        edit(world, "w1", {"form": {"wall_top_m": 6.9}})
    assert ws["overrides"].read_text() == before


def test_flat_and_back(world, ws):
    out = edit(world, "w1", {"form": {"roof": "flat"}})
    assert out["state"]["override"] == {"roof": "flat", "height_m": 4.0, "wall_colour": "#102030"}  # the wall top is the height now
    assert out["state"]["form"] == {"roof": "flat", "wall_top_m": 4.0, "top_m": 4.0}
    out = edit(world, "w1", {"form": {"roof": "hipped"}})  # flat -> pitched: a 3 m rise
    assert out["state"]["override"]["roof"] == "hipped" and out["state"]["override"]["height_m"] == 7.0
    assert out["state"]["override"]["roof_height_m"] == 3.0
    out = edit(world, "w1", {"form": {"roof": "gabled", "top_m": 9.0}})
    assert out["state"]["form"] == {"roof": "gabled", "wall_top_m": 4.0, "top_m": 9.0}


def test_a_form_change_on_a_building_with_no_entry_adds_one(world, ws):
    out = edit(world, "w2", {"form": {"roof": "flat", "top_m": 3.2}})
    text = ws["overrides"].read_text()
    assert text.startswith(OVERRIDES.rstrip("\n"))  # the rest of the file is as it was
    assert text.endswith("\n  # P-0002: edited in the viewer\n  w2:\n    height_m: 3.2\n    roof: flat\n")
    assert out["before"]["override"] is None and out["after"]["override"].startswith("  # P-0002: edited in the viewer\n  w2:\n")


# ---- override fields, minimal diff ----


def test_an_override_field_changes_one_line_and_the_rest_is_byte_identical(world, ws):
    out = edit(world, "w1", {"override": {"wall_colour": "#a0b0c0", "levels": 2}})
    assert ws["overrides"].read_text() == OVERRIDES.replace('"#102030"', '"#a0b0c0"').replace("    wall_colour", "    wall_colour", 1).replace(
        '"#a0b0c0"\n', '"#a0b0c0"\n    levels: 2\n', 1
    )
    assert out["state"]["override"]["levels"] == 2
    edit(world, "w1", {"override": {"levels": None, "roof_fit": True}})
    assert "    levels" not in ws["overrides"].read_text() and "    roof_fit: True\n" in ws["overrides"].read_text()
    assert ws["overrides"].read_text().endswith(OTHER)  # a field with the same value as before: no line moved


def test_an_override_the_loader_refuses_writes_nothing(world, ws):
    before = ws["overrides"].read_text()
    for bad in ({"frobnicate": 1}, {"wall_colour": "red"}, {"roof": "round"}, {"roof_height_m": -1}):
        with pytest.raises(be.BuildingEditError):
            edit(world, "w1", {"override": bad})
    assert ws["overrides"].read_text() == before


def test_removing_every_field_removes_the_entry_with_its_comments(world, ws):
    edit(world, "w1", {"override": {k: None for k in ("roof", "height_m", "roof_height_m", "wall_colour")}})
    assert ws["overrides"].read_text() == HEAD + OTHER
    assert be.state(world, "w1")["has_override"] is False


def test_a_flow_style_entry_is_written_out_as_a_block(world, ws):
    ws["overrides"].write_text("buildings:\n  w1: {wall_colour: '#102030', roof: gabled}\n")
    edit(world, "w1", {"override": {"levels": 2}})
    assert ws["overrides"].read_text() == 'buildings:\n  w1:\n    wall_colour: "#102030"\n    roof: gabled\n    levels: 2\n'


# ---- the detail file ----


def chunks(text):
    return be.DetailText(text)


def test_the_detail_is_split_in_chunks_and_put_together_again_byte_for_byte():
    dt = chunks(DETAIL)
    assert [c.key for c in dt.chunks] == ["id", "source", "overhang_m", "frame_colour", "plinth", "eaves", "downpipes", "interior", "openings"]
    assert dt.head == ["# P-0002: house. Measured by hand.\n", "# Wall top 3.00 m.\n"]
    assert dt.chunk("interior").lead == ["# the doors\n"] and dt.text() == DETAIL
    assert chunks("id: x\nopenings: []").text() == "id: x\nopenings: []"  # no newline at the end: as it was


def test_a_detail_edit_rewrites_only_its_chunk(world, ws):
    out = edit(world, "w2", {"detail": {"overhang_m": 0.5, "ridge_caps": True, "roof_tiles": True}})
    new = (ws["details"] / "w2.yaml").read_text()
    assert new == DETAIL.replace("overhang_m: 0.35", "overhang_m: 0.5").replace("# the doors\ninterior", "ridge_caps: true\nroof_tiles: true\n# the doors\ninterior")
    assert out["state"]["detail"]["overhang_m"] == 0.5 and out["state"]["detail"]["ridge_caps"] is True
    assert out["before"]["detail"] == DETAIL and out["after"]["detail"] == new


def test_plinth_gutter_corners_bands_colours(world, ws):
    out = edit(world, "w2", {"detail": {"plinth_m": 0.6, "gutter": True, "frame_colour": "#112233", "interior": False}})
    d = out["state"]["detail"]
    assert d["plinth"] == {"height_m": 0.6, "colour": "#6b5a50"} and d["gutter"] is True and d["frame_colour"] == "#112233" and d["interior"] is False
    new = (ws["details"] / "w2.yaml").read_text()
    assert "plinth: {height_m: 0.6, colour: '#6b5a50'}\n" in new and "eaves: {colour: '#3a3230', fascia_m: 0.18, gutter: true}\n" in new
    assert "- {edge: 1, at_m: 0.2}\n" in new and "interior" not in new
    d = edit(world, "w2", {"detail": {"plinth_m": None, "corners": True, "bands": True, "surround_colour": "#abcdef"}})["state"]["detail"]
    assert d["plinth"] is None and d["corners"] is True and d["bands"] is True and d["surround_colour"] == "#abcdef"
    raw = yaml.safe_load((ws["details"] / "w2.yaml").read_text())
    wall = be.state(world, "w2")["form"]["wall_top_m"]
    assert raw["corners"] == {"width_m": 0.3, "top_m": round(wall - 0.18, 2)} and raw["bands"] == [{"top_m": wall, "height_m": 0.18}]
    d = edit(world, "w2", {"detail": {"corners": False, "bands": False, "gutter": False}})["state"]["detail"]
    assert (d["corners"], d["bands"], d["gutter"]) == (False, False, False)


def test_the_gutter_makes_an_eaves_block_when_there_is_none(world, ws):
    (ws["details"] / "w2.yaml").write_text(DETAIL.replace("eaves: {colour: '#3a3230', fascia_m: 0.18, gutter: false}\n", ""))
    edit(world, "w2", {"detail": {"gutter": True}})
    assert yaml.safe_load((ws["details"] / "w2.yaml").read_text())["eaves"] == {"colour": "#3a3230", "fascia_m": 0.18, "gutter": True}


def test_an_invalid_detail_edit_writes_nothing(world, ws):
    path = ws["details"] / "w2.yaml"
    for bad in ({"overhang_m": 3}, {"frame_colour": "blue"}, {"plinth_m": 9}, {"nonsense": 1}):
        with pytest.raises(be.BuildingEditError):
            edit(world, "w2", {"detail": bad})
    assert path.read_text() == DETAIL
    assert not [p for p in ws["details"].iterdir() if p.name != "w2.yaml"]  # no temp file either


def test_a_detail_is_made_when_an_opening_is_added_or_a_key_set(world, ws):
    assert not (ws["details"] / "w1.yaml").exists()
    edit(world, "w1", {"override": {"levels": 2}})
    assert not (ws["details"] / "w1.yaml").exists()  # an override edit makes none
    with pytest.raises(be.BuildingEditError, match="no detail file"):
        edit(world, "w1", {"opening": {"delete": "w1"}})
    out = edit(world, "w1", {"opening": {"add": {}}})
    text = (ws["details"] / "w1.yaml").read_text()
    assert text.startswith("id: w1\nsource: edited in the viewer\noverhang_m: 0.3\nopenings:\n  - {id: w1, kind: window, edge: ")
    assert out["before"]["detail"] is None and out["selected"] == "w1"
    (ws["details"] / "w1.yaml").unlink()
    edit(world, "w1", {"detail": {"overhang_m": 0.6}})
    assert (ws["details"] / "w1.yaml").read_text() == "id: w1\nsource: edited in the viewer\noverhang_m: 0.6\nopenings: []\n"


# ---- openings ----


def test_an_added_opening_gets_defaults_the_longest_edge_and_the_middle(world, ws):
    out = edit(world, "w2", {"opening": {"add": {}}})
    o = rows(ws)[out["selected"]]
    assert out["selected"] == "w2" and o["kind"] == "window" and o["edge"] in (1, 3)  # the 12 m walls
    assert (o["width_m"], o["height_m"], o["sill_m"], o["at_m"]) == (1.2, 1.3, 1.0, 5.4)
    out = edit(world, "w2", {"opening": {"add": {"kind": "door", "edge": 0}}})
    d = rows(ws)[out["selected"]]
    assert out["selected"] == "d2" and (d["width_m"], d["height_m"], d["sill_m"], d["at_m"]) == (1.0, 2.1, 0.0, 3.5)
    got = edit(world, "w2", {"opening": {"add": {"kind": "garage_door", "edge": 2}}})["selected"]
    g = rows(ws)[got]
    assert (g["id"], g["width_m"], g["height_m"]) == ("g1", 2.4, 2.1)
    new = (ws["details"] / "w2.yaml").read_text()
    assert "  # a door\n  - {id: d1, kind: door, edge: 0, at_m: 5.0," in new  # the rows and comments that were there stay
    assert "  - {id: w1, kind: window, edge: 0, at_m: 2.0, width_m: 1.0, height_m: 1.2, sill_m: 0.9}\n" in new
    assert new.startswith(DETAIL.split("openings:")[0])  # every chunk above the openings is untouched


def test_set_clamps_to_the_wall_and_leaves_the_other_rows_as_written(world, ws):
    out = edit(world, "w2", {"opening": {"id": "w1", "set": {"at_m": 99, "width_m": 1.5, "sill_m": 1.1}}})
    o = rows(ws)["w1"]
    assert (o["at_m"], o["width_m"], o["sill_m"], out["selected"]) == (6.5, 1.5, 1.1, "w1")  # edge 0 is 8 m
    edit(world, "w2", {"opening": {"id": "w1", "set": {"at_m": -3}}})
    assert rows(ws)["w1"]["at_m"] == 0.0
    new = (ws["details"] / "w2.yaml").read_text()
    assert DETAIL.split("\n")[-2] + "\n" in new  # d1's row, byte for byte
    with pytest.raises(be.BuildingEditError, match="edge 9 does not exist"):
        edit(world, "w2", {"opening": {"id": "w1", "set": {"edge": 9}}})


def test_changing_the_kind_drops_what_the_kind_cannot_have(world, ws):
    edit(world, "w2", {"opening": {"id": "d1", "set": {"kind": "window"}}})
    assert "steps" not in rows(ws)["d1"]
    edit(world, "w2", {"opening": {"id": "w1", "set": {"kind": "door", "sill_m": 0}}})
    assert rows(ws)["w1"]["kind"] == "door"


def test_delete_and_duplicate(world, ws):
    out = edit(world, "w2", {"opening": {"duplicate": "w1"}})
    c = rows(ws)[out["selected"]]
    assert out["selected"] == "w2" and c["at_m"] == 3.3 and c["edge"] == 0 and c["width_m"] == 1.0  # 2.0 + 1.0 + 0.3
    assert list(rows(ws)) == ["w1", "w2", "d1"]  # right after the original
    out = edit(world, "w2", {"opening": {"duplicate": "d1"}})  # 5.0 + 0.95 + 0.3 = 6.25, and it still fits on the 8 m wall
    assert rows(ws)[out["selected"]]["at_m"] == pytest.approx(6.25)
    edit(world, "w2", {"opening": {"id": "d1", "set": {"at_m": 7.0}}})
    out = edit(world, "w2", {"opening": {"duplicate": "d1"}})  # 7.0 + 1.25 + 0.95 > 8: to the left of it
    assert rows(ws)[out["selected"]]["at_m"] == pytest.approx(5.75) and out["selected"] == "d3"
    edit(world, "w2", {"opening": {"delete": "d3"}})
    edit(world, "w2", {"opening": {"delete": "d2"}})
    out = edit(world, "w2", {"opening": {"delete": "w2"}})
    assert out["selected"] is None and "w2" not in rows(ws)
    with pytest.raises(be.BuildingEditError, match="no opening nope"):
        edit(world, "w2", {"opening": {"delete": "nope"}})
    for oid in ("w1", "d1"):
        edit(world, "w2", {"opening": {"delete": oid}})
    assert (ws["details"] / "w2.yaml").read_text().endswith("interior: true\nopenings: []\n")


def test_an_opening_change_must_name_one_thing(world):
    with pytest.raises(be.BuildingEditError, match="one of set, add, delete, duplicate"):
        edit(world, "w2", {"opening": {"set": {}, "add": {}, "id": "w1"}})
    with pytest.raises(be.BuildingEditError, match="unknown change key"):
        edit(world, "w2", {"walls": 1})


def test_rows_written_in_block_style_are_made_again(world, ws):
    (ws["details"] / "w2.yaml").write_text("id: w2\nopenings:\n- id: w1\n  kind: window\n  edge: 0\n  at_m: 2.0\n  width_m: 1.0\n  height_m: 1.2\n")
    edit(world, "w2", {"opening": {"duplicate": "w1"}})
    assert (ws["details"] / "w2.yaml").read_text().endswith("openings:\n  - {id: w1, kind: window, edge: 0, at_m: 2.0, width_m: 1.0, height_m: 1.2}\n"
                                                           "  - {id: w2, kind: window, edge: 0, at_m: 3.3, width_m: 1.0, height_m: 1.2}\n")


# ---- snapshots ----


def test_snapshot_restore_and_reset_round_trip(world, ws):
    start = {"override": be.block_text(OVERRIDES, "w1"), "detail": None}
    assert start["override"] == W1_BLOCK
    snap = be.handle_op(world, {"op": "snapshot", "building": "w1"})["snapshot"]
    assert snap == start
    out = edit(world, "w1", {"form": {"roof": "flat"}, "opening": {"add": {}}, "detail": {"plinth_m": 0.4}})
    assert out["before"] == start and out["after"]["detail"] and out["after"] != start
    assert ws["overrides"].read_text() != OVERRIDES
    back = be.restore(world, "w1", start)
    assert back["after"] == start and back["before"] == out["after"]
    assert ws["overrides"].read_text() == OVERRIDES and not (ws["details"] / "w1.yaml").exists()  # byte for byte
    redo = be.restore(world, "w1", out["after"])  # redo
    assert redo["after"] == out["after"] and (ws["details"] / "w1.yaml").exists()
    # reset goes to what was measured
    with pytest.raises(be.BuildingEditError, match="nothing measured to go back to"):
        be.reset(world, "w1")
    (ws["photos"] / "w1").mkdir(parents=True)
    (ws["photos"] / "w1" / "measured.json").write_text(json.dumps(start))
    assert be.state(world, "w1")["measured"] is True
    got = be.reset(world, "w1")
    assert got["after"] == start and ws["overrides"].read_text() == OVERRIDES and not (ws["details"] / "w1.yaml").exists()


def test_restoring_a_missing_block_puts_it_back_at_the_end_and_none_removes_it_again(world, ws):
    snap = {"override": "  # P-0002: x\n  w2:\n    roof: flat\n    height_m: 3.5\n", "detail": DETAIL}
    be.restore(world, "w2", snap)
    assert ws["overrides"].read_text() == OVERRIDES + "\n" + snap["override"]
    assert be.state(world, "w2")["form"] == {"roof": "flat", "wall_top_m": 3.5, "top_m": 3.5}
    be.restore(world, "w2", {"override": None, "detail": None})
    assert ws["overrides"].read_text() == OVERRIDES and not (ws["details"] / "w2.yaml").exists()


def test_a_snapshot_that_does_not_load_is_refused_and_writes_nothing(world, ws):
    for bad in ({"override": "  w2:\n    frobnicate: 1\n", "detail": DETAIL}, {"override": None, "detail": "id: w2\nfrob: 1\n"}, {"override": None}):
        with pytest.raises(be.BuildingEditError):
            be.restore(world, "w2", bad)
    assert ws["overrides"].read_text() == OVERRIDES and (ws["details"] / "w2.yaml").read_text() == DETAIL


# ---- files ----


def test_a_write_is_atomic(tmp_path, monkeypatch):
    path = tmp_path / "f.yaml"
    path.write_text("old\n")
    seen = []
    real = os.replace

    def spy(src, dst):
        # same folder, the new text is complete, the file not yet changed
        seen.append((os.path.dirname(src) == str(tmp_path), Path(src).read_text(), path.read_text()))
        real(src, dst)

    monkeypatch.setattr(os, "replace", spy)
    be.atomic_write(path, "new\n")
    assert seen == [(True, "new\n", "old\n")] and path.read_text() == "new\n" and [p.name for p in tmp_path.iterdir()] == ["f.yaml"]
    monkeypatch.setattr(os, "replace", lambda *a: (_ for _ in ()).throw(OSError("disk full")))
    with pytest.raises(OSError):
        be.atomic_write(path, "newer\n")
    assert path.read_text() == "new\n" and [p.name for p in tmp_path.iterdir()] == ["f.yaml"]  # the old file stays, no temp left


def test_landmark_photo_writes_overrides_atomically(tmp_path, monkeypatch):
    from drycastle_pipeline.photos import landmark_photo as lp

    path = tmp_path / "o.yaml"
    path.write_text(OVERRIDES)
    written = []
    real = be.atomic_write
    monkeypatch.setattr(be, "atomic_write", lambda p, t: (written.append(p), real(p, t)))
    monkeypatch.setattr("tempfile.gettempdir", lambda: str(tmp_path))
    lp._upsert_override(path, "w5", "P-5: x", {"roof": "flat", "wall_colour": "#123456"}, False)
    assert written == [path] and path.read_text().endswith('  # P-5: x\n  w5:\n    roof: flat\n    wall_colour: "#123456"\n')


# ---- the worker ----


def test_the_worker_answers_ops_in_order_and_survives_an_invalid_one(world, ws, tmp_path):
    lines = [
        {"id": 1, "op": "state", "building": "w2"},
        {"id": 2, "op": "edit", "building": "w2", "change": {"opening": {"add": {}}}},
        {"id": 3, "op": "edit", "building": "w2", "change": {"detail": {"overhang_m": 5}}},
        {"id": 4, "op": "snapshot", "building": "w2"},
        {"id": 5, "op": "restore", "building": "w2", "snapshot": {"override": None, "detail": DETAIL}},
        {"id": 6, "op": "reset", "building": "w2"},
        {"id": 7, "op": "state", "building": "w404"},
        {"id": 8, "op": "bogus", "building": "w2"},
        {"id": 9, "glb": str(tmp_path / "s.glb"), "parcels": ["P-0001"]},  # a rebuild request still works
        {"op": "quit"},
    ]
    out = io.StringIO()
    code = building_run.serve(io.StringIO("".join(json.dumps(x) + "\n" for x in lines)), out, world=world)
    assert code == 0
    ready, *answers = [json.loads(x) for x in out.getvalue().splitlines()]
    by = {a["id"]: a for a in answers}
    assert ready["ready"] is True
    assert by[1]["ok"] and by[1]["result"]["building"] == "w2"
    assert by[2]["ok"] and by[2]["result"]["selected"] == "w2" and by[2]["result"]["before"]["detail"] == DETAIL
    assert not by[3]["ok"] and "overhang_m must be 0..2" in by[3]["error"]
    assert by[4]["ok"] and "id: w2" in by[4]["result"]["snapshot"]["detail"]
    assert by[5]["ok"] and (ws["details"] / "w2.yaml").read_text() == DETAIL
    assert not by[6]["ok"] and "nothing measured to go back to" in by[6]["error"]
    assert not by[7]["ok"] and "w404" in by[7]["error"]
    assert not by[8]["ok"] and "unknown op" in by[8]["error"]
    assert by[9]["ok"] and by[9]["fragment"]["parcels"] == ["P-0001"]
