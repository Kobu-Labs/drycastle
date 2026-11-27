"""§facade_pipeline auto§: arguments, the stage order with the stage functions replaced, §--redo§, the missing key, the RESULT line, own
photos (side -> viewpoint, sketch point, the sheet the model writes), all with a fake client: no API, no map, no photo is measured."""

import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from drycastle_pipeline.edits import building_edit as be
from drycastle_pipeline.edits import sketchfile
from drycastle_pipeline.photos import auto_build as ab
from drycastle_pipeline.photos import facade_pipeline as fp
from drycastle_pipeline.photos import facade_reader as fr
from drycastle_pipeline.photos import sheet_reader as sr

BID = "w5"
BLOCK = "  # P-0005: house, from Street View captures.\n  w5:\n    roof: gabled\n    height_m: 7.5\n"
DETAIL = "id: w5\nsource: test\nopenings:\n  - {id: w1, kind: window, edge: 0, at_m: 1.0, width_m: 1.0, height_m: 1.2, sill_m: 0.9}\n"
EXISTING = "buildings:\n  # P-0009: x\n  w9:\n    roof: flat\n"
RESULT_JSON = {"invented": ["windows on the back"], "building": BID, "roof": "gabled", "eave_m": 3.2, "top_m": 7.5, "problems": ["one wall unseen"],
               "detail": {"openings": [{"id": "w1"}, {"id": "w2"}]}}  # fmt: skip


@pytest.fixture
def env(tmp_path, monkeypatch):
    monkeypatch.setattr(ab, "PHOTOS_DIR", tmp_path / "photos")
    monkeypatch.setattr(ab, "OVERRIDES_PATH", tmp_path / "overrides.yaml")
    monkeypatch.setattr(ab, "DETAILS_DIR", tmp_path / "details")
    monkeypatch.setattr(ab, "load_dotenv", lambda: None)
    monkeypatch.setattr(ab, "api_state", lambda: (True, True))
    (tmp_path / "overrides.yaml").write_text(EXISTING)
    (tmp_path / "details").mkdir()
    return SimpleNamespace(tmp=tmp_path, run=tmp_path / "photos" / BID / "run", own=tmp_path / "photos" / BID / "own_run")


class Stages:
    """facade_pipeline's three stage functions, made up: they leave the files the real ones leave."""

    def __init__(self, env, hand_marked=False):
        self.env, self.hand_marked, self.calls = env, hand_marked, []

    def stage_ends(self, building, out, use_marks=True, captures=None):
        self.calls.append(("ends", captures))
        out.mkdir(parents=True, exist_ok=True)
        (out / "state.json").write_text(json.dumps({"building": building, "parcel": "P-0005"}))
        (out / ("ends.json" if self.hand_marked else "ends_ask.md")).write_text("{}\n" if self.hand_marked else "ask")
        print("capture 0: 40 straight segments")

    def stage_walls(self, out):
        self.calls.append(("walls",))
        (out / "walls_ask.md").write_text("ask")

    def stage_apply(self, out, dry, replace, guess=True):
        self.calls.append(("apply", dry, replace))
        path = self.env.tmp / "overrides.yaml"
        if BLOCK.splitlines()[1] in path.read_text() and not replace:
            raise SystemExit("w5 already has an entry in overrides.yaml: look at it, then pass --replace to overwrite it")
        path.write_text(be.with_block(path.read_text(), BID, BLOCK))
        (self.env.tmp / "details" / f"{BID}.yaml").write_text(DETAIL)
        (out / "result.json").write_text(json.dumps(RESULT_JSON))
        for name in ("overlay_0.png", "overlay_2.png", "compare_0.png", "render_0.png", "render_1.png"):
            (out / name).write_bytes(b"png")
        print("PROBLEM: one wall unseen")


@pytest.fixture
def reads(monkeypatch):
    """fr.read_one made up: §answers§ maps a stage to its replies, a text is written as the answer, None is a reply that failed the template."""
    calls, answers = [], {"ends": ['{"c0_e0_left": "a"}'], "walls": ['{"what": "house"}']}

    def read_one(client, out, again, stage=None):
        calls.append(stage)
        reply = answers[stage].pop(0) if len(answers[stage]) > 1 else answers[stage][0]
        if reply is None:
            print(f"PROBLEM: {out}: a \"?\" was left unanswered: not written")
            return {"in": 1, "out": 1, "problem": 'a "?" was left unanswered'}
        (out / fr.STAGES[0 if stage == "ends" else 1][1]).write_text(reply)
        return {"in": 1, "out": 1}

    monkeypatch.setattr(fr, "read_one", read_one)
    return SimpleNamespace(calls=calls, answers=answers)


def auto(capsys, env, stages, **kw):
    code = ab.run(ab.namespace(building=BID, **kw), stages, client_of=lambda: object())
    lines = capsys.readouterr().out.splitlines()
    return code, lines


def result_of(lines):
    (line,) = [ln for ln in lines if ln.startswith("RESULT: ")]
    return json.loads(line[len("RESULT: ") :])


# ---- arguments ----


def test_the_command_is_part_of_facade_pipeline():
    p = fp.argparse.ArgumentParser()
    ab.add_arguments(p)
    a = p.parse_args(["w5", "--captures", "0,2", "--redo", "ends", "--cam-height", "1.4"])
    assert (a.building, a.captures, a.redo, a.cam_height, a.check) == ("w5", "0,2", "ends", 1.4, False)
    assert p.parse_args(["--check"]).building is None


def test_each_photo_takes_the_locator_after_it():
    p = fp.argparse.ArgumentParser()
    ab.add_arguments(p)
    a = p.parse_args(["w5", "--photo", "a.png", "--side", "ne", "--photo", "b.png", "--point", "b", "--photo", "c.png", "--view", "1.5,-2"])
    got = ab.parse_photos(a.photo_seq)
    assert [(g.path.name, g.kind, g.value) for g in got] == [("a.png", "side", "NE"), ("b.png", "point", "B"), ("c.png", "view", (1.5, -2.0))]
    assert ab.parse_photos(None) == []


@pytest.mark.parametrize(
    ("seq", "message"),
    [
        ([("side", "N")], "before any --photo"),
        ([("photo", "a.png")], "say where it was taken"),
        ([("photo", "a.png"), ("side", "N"), ("point", "A")], "one of --side, --point, --view"),
        ([("photo", "a.png"), ("side", "up")], "--side must be one of"),
        ([("photo", "a.png"), ("view", "x,y")], "X,Y in metres"),
        ([item for i in range(4) for item in (("photo", f"{i}.png"), ("side", "N"))], "at most 3 photos"),
    ],
)
def test_bad_photo_arguments_are_errors(seq, message):
    with pytest.raises(ab.AutoError, match=message):
        ab.parse_photos(seq)


def test_capture_numbers():
    assert ab.parse_captures("0, 2") == [0, 2] and ab.parse_captures(None) is None
    for bad in ("a", "", "-1", ","):
        with pytest.raises(ab.AutoError, match="capture numbers"):
            ab.parse_captures(bad)


def test_check_needs_no_building(capsys, monkeypatch):
    monkeypatch.setattr(ab, "load_dotenv", lambda: None)
    monkeypatch.setattr(ab, "api_state", lambda: (True, False))
    assert ab.run(ab.namespace(check=True)) == 0
    assert capsys.readouterr().out == 'CHECK: {"api_key": true, "reader": false}\n'


def test_api_state_reads_the_environment(monkeypatch):
    monkeypatch.setenv("ANTHROPIC_API_KEY", "k")
    assert ab.api_state()[0] is True
    monkeypatch.delenv("ANTHROPIC_API_KEY")
    assert ab.api_state()[0] is False


# ---- the capture chain ----


def test_the_stages_run_in_order_and_the_result_line_has_everything(env, reads, capsys):
    stages = Stages(env)
    code, lines = auto(capsys, env, stages)
    assert code == 0
    assert [ln for ln in lines if ln.startswith("STAGE: ")] == ["STAGE: ends 1/5", "STAGE: read ends 2/5", "STAGE: walls 3/5", "STAGE: read walls 4/5", "STAGE: apply 5/5"]
    assert stages.calls == [("ends", None), ("walls",), ("apply", False, False)] and reads.calls == ["ends", "walls"]
    assert "capture 0: 40 straight segments" in lines and "PROBLEM: one wall unseen" in lines  # the stages' own lines pass through
    assert lines[-1].startswith("RESULT: ")
    r = result_of(lines)
    run = str(env.run.resolve())
    assert r == {
        "building": BID, "parcel": "P-0005", "method": "captures", "run": run,
        "overlays": [f"{run}/overlay_0.png", f"{run}/overlay_2.png"], "compares": [f"{run}/compare_0.png"],
        "renders": [f"{run}/render_0.png", f"{run}/render_1.png"], "result_json": f"{run}/result.json",
        "problems": ["one wall unseen"], "invented": ["windows on the back"], "roof": "gabled", "eave_m": 3.2, "top_m": 7.5,
        "openings": 2, "can_redo_ends": True, "replaced": False,
    }  # fmt: skip


def test_measured_json_is_what_the_build_wrote(env, reads, capsys):
    auto(capsys, env, Stages(env))
    assert json.loads((env.tmp / "photos" / BID / "measured.json").read_text()) == {"override": BLOCK, "detail": DETAIL}
    assert (env.tmp / "overrides.yaml").read_text() == EXISTING + "\n" + BLOCK  # the rest of the file as it was


def test_selected_captures_go_to_the_ends_stage(env, reads, capsys):
    stages = Stages(env)
    auto(capsys, env, stages, captures="0,2")
    assert stages.calls[0] == ("ends", [0, 2])


def test_a_fresh_run_does_not_reuse_the_answers_of_an_earlier_one(env, reads, capsys):
    env.run.mkdir(parents=True)
    for name in ("ends.json", "names.json", "walls_ask.md", "overlay_9.png", "result.json"):
        (env.run / name).write_text("old")
    stages = Stages(env)
    code, _ = auto(capsys, env, stages)
    assert code == 0 and reads.calls == ["ends", "walls"]  # both asked again
    assert (env.run / "ends.json").read_text() == '{"c0_e0_left": "a"}' and not (env.run / "overlay_9.png").exists()


def test_every_end_marked_by_hand_asks_nothing_for_the_ends(env, reads, capsys):
    code, lines = auto(capsys, env, Stages(env, hand_marked=True))
    assert code == 0 and reads.calls == ["walls"]
    assert "STAGE: read ends 2/5" in lines and "every wall end was marked by hand: nothing to ask" in lines
    assert result_of(lines)["can_redo_ends"] is False


def test_an_existing_entry_needs_replace(env, reads, capsys):
    (env.tmp / "overrides.yaml").write_text(EXISTING + "\n" + BLOCK)
    stages = Stages(env)
    code, lines = auto(capsys, env, stages)
    assert code == 1 and "already has an entry" in lines[-1] and lines[-1].startswith("ERROR: ")
    assert stages.calls == [] and reads.calls == []  # nothing spent, nothing overwritten
    code, lines = auto(capsys, env, stages, replace=True)
    assert code == 0 and stages.calls[-1] == ("apply", False, True) and result_of(lines)["replaced"] is True


def test_what_a_stage_says_with_systemexit_is_an_error_line(env, reads, capsys):
    stages = Stages(env)
    stages.stage_ends = lambda *a, **k: (_ for _ in ()).throw(SystemExit(f"{BID} has no captures in /x:\n take them"))
    code, lines = auto(capsys, env, stages)
    assert code == 1 and lines[-1] == f"ERROR: {BID} has no captures in /x: take them" and not any(ln.startswith("RESULT") for ln in lines)


def test_another_failure_is_an_error_line_too(env, reads, capsys):
    stages = Stages(env)
    stages.stage_walls = lambda out: 1 / 0
    code, lines = auto(capsys, env, stages)
    assert code == 1 and lines[-1] == "ERROR: ZeroDivisionError: division by zero"


def test_no_key_or_no_package_is_exit_2_before_any_stage(env, reads, capsys, monkeypatch):
    stages = Stages(env)
    for state in ((False, True), (True, False)):
        monkeypatch.setattr(ab, "api_state", lambda state=state: state)
        code, lines = auto(capsys, env, stages)
        assert code == 2 and len(lines) == 1 and lines[0].startswith("ERROR: ")
        assert "uv sync --extra reader" in lines[0] and ".env" in lines[0] and ("ANTHROPIC_API_KEY is not set" in lines[0]) == (not state[0])
    assert stages.calls == []


def test_an_answer_that_fails_the_template_is_asked_once_more(env, reads, capsys):
    reads.answers["ends"] = [None, '{"c0_e0_left": "a"}']
    stages = Stages(env)
    code, lines = auto(capsys, env, stages)
    assert code == 0 and reads.calls == ["ends", "ends", "walls"]
    reads.calls.clear()
    reads.answers["walls"] = [None]
    code, lines = auto(capsys, env, stages, replace=True)
    assert code == 1 and reads.calls == ["ends", "walls", "walls"]  # the second try of the second question was the last
    assert lines[-1] == 'ERROR: the model\'s answer was not usable: a "?" was left unanswered; try again'
    assert ("apply", False, True) not in stages.calls


# ---- --redo ----


def built_run(env, hand_marked=False):
    env.run.mkdir(parents=True)
    (env.run / "state.json").write_text(json.dumps({"building": BID, "parcel": "P-0005"}))
    (env.run / "ends.json").write_text('{"c0_e0_left": "a"}')
    (env.run / "names.json").write_text('{"what": "house"}')
    (env.run / "walls_ask.md").write_text("ask")
    if not hand_marked:
        (env.run / "ends_ask.md").write_text("ask")
    (env.tmp / "overrides.yaml").write_text(EXISTING + "\n" + BLOCK)


def test_redo_ends_asks_the_ends_again_then_walls_then_applies_with_replace(env, reads, capsys):
    built_run(env)
    stages = Stages(env)
    code, lines = auto(capsys, env, stages, redo="ends")
    assert code == 0
    assert [ln for ln in lines if ln.startswith("STAGE: ")] == ["STAGE: read ends 1/4", "STAGE: walls 2/4", "STAGE: read walls 3/4", "STAGE: apply 4/4"]
    assert reads.calls == ["ends", "walls"] and stages.calls == [("walls",), ("apply", False, True)]
    assert (env.run / "ends.json").read_text() == '{"c0_e0_left": "a"}' and (env.run / "names.json").read_text() == '{"what": "house"}'
    assert result_of(lines)["replaced"] is True


def test_redo_walls_asks_only_the_walls(env, reads, capsys):
    built_run(env)
    stages = Stages(env)
    code, lines = auto(capsys, env, stages, redo="walls")
    assert code == 0 and reads.calls == ["walls"] and stages.calls == [("apply", False, True)]
    assert [ln for ln in lines if ln.startswith("STAGE: ")] == ["STAGE: read walls 1/2", "STAGE: apply 2/2"]
    (env.run / "walls_ask.md").unlink()  # without its question the walls stage is run first
    stages.calls.clear()
    auto(capsys, env, stages, redo="walls")
    assert stages.calls == [("walls",), ("apply", False, True)]


def test_redo_ends_of_a_run_whose_ends_were_marked_by_hand_is_an_error(env, reads, capsys):
    built_run(env, hand_marked=True)
    code, lines = auto(capsys, env, Stages(env), redo="ends")
    assert code == 1 and lines == ["ERROR: the wall ends were marked by hand: nothing to ask again"] and reads.calls == []
    (env.run / "state.json").unlink()
    code, lines = auto(capsys, env, Stages(env), redo="walls")
    assert code == 1 and "no saved run" in lines[0]


def test_redo_is_for_captures_only(env, reads, capsys):
    code, lines = auto(capsys, env, Stages(env), redo="ends", photo_seq=[("photo", "a.png"), ("side", "N")])
    assert code == 1 and "not on photos of your own" in lines[0]
    code, lines = auto(capsys, env, Stages(env), redo="ends", captures="0")
    assert code == 1 and "--captures starts a fresh run" in lines[0]


# ---- facade_reader and stage_ends ----


class FakeClient:
    """client.beta.messages.create: returns the next reply."""

    def __init__(self, *replies, stop="end_turn"):
        self.replies, self.stop, self.sent = list(replies), stop, []
        self.beta = SimpleNamespace(messages=SimpleNamespace(create=self._create))

    def _create(self, **kw):
        self.sent.append(kw)
        reply = self.replies.pop(0) if len(self.replies) > 1 else self.replies[0]
        return SimpleNamespace(content=[SimpleNamespace(type="text", text=reply)], usage=SimpleNamespace(input_tokens=5, output_tokens=7), stop_reason=self.stop)


def test_read_one_can_ask_a_named_stage_again(tmp_path, capsys):
    (tmp_path / "ends_ask.md").write_text('# q\n\n{\n "c0_e0_left": "?"\n}')
    (tmp_path / "ends.json").write_text('{"c0_e0_left": "old"}')
    client = FakeClient('Here: {"c0_e0_left": "b"}')
    assert fr.read_one(client, tmp_path, False) is None  # the ends are answered, the walls have no ask file: nothing open
    assert client.sent == []
    assert fr.read_one(client, tmp_path, False, stage="ends") == {"in": 5, "out": 7}
    assert json.loads((tmp_path / "ends.json").read_text()) == {"c0_e0_left": "b"}  # overwritten
    assert fr.read_one(client, tmp_path, False, stage="walls") is None  # no walls_ask.md
    assert len(client.sent) == 1 and client.sent[0]["model"] == fr.MODEL
    bad = FakeClient('{"c0_e0_left": "?"}')
    got = fr.read_one(bad, tmp_path, False, stage="ends")
    assert got["problem"] == 'a "?" was left unanswered' and json.loads((tmp_path / "ends.json").read_text()) == {"c0_e0_left": "b"}  # nothing written
    assert fr.read_one(FakeClient("{}", stop="max_tokens"), tmp_path, False, stage="ends")["problem"] == "the model stopped with max_tokens"
    capsys.readouterr()


def test_read_one_without_a_stage_is_as_before(tmp_path):
    (tmp_path / "ends_ask.md").write_text('# q\n\n{\n "k": "?"\n}')
    assert fr.read_one(FakeClient('{"k": "a"}'), tmp_path, False) == {"in": 5, "out": 7}
    assert (tmp_path / "ends.json").exists() and fr.read_one(FakeClient("{}"), tmp_path, False) is None  # answered, walls not asked yet


def test_stage_ends_takes_the_captures_it_is_given(tmp_path, monkeypatch):
    from drycastle_pipeline.photos import landmark_tools as lt
    from drycastle_pipeline.photos import photo_capture as pc

    work, photos = tmp_path / "work", tmp_path / "photos"
    work.mkdir()
    ring = [[0, 0], [8, 0], [8, 6], [0, 6]]
    (work / "buildings.json").write_text(json.dumps({"buildings": [{"id": BID, "parcel": "P-5", "footprint": {"exterior": ring, "holes": []}}]}))
    (photos / BID).mkdir(parents=True)
    for n in range(4):
        (photos / BID / f"{n}.json").write_text("{}")
    monkeypatch.setattr(lt, "WORK", work)
    monkeypatch.setattr(pc, "PHOTOS_DIR", photos)
    with pytest.raises(SystemExit, match=r"w5 has no capture 5 \(it has: 0, 1, 2, 3\)"):
        fp.stage_ends(BID, tmp_path / "run", captures=[0, 5])
    with pytest.raises(SystemExit, match="at most 3 captures"):
        fp.stage_ends(BID, tmp_path / "run", captures=[0, 1, 2, 3])


def test_the_colour_lists_are_the_same_objects_in_both_modules():
    from drycastle_pipeline.photos import palettes

    for name in ("WALL_COLOURS", "ROOF_COLOURS", "FRAME_COLOURS", "SURROUND_COLOURS"):
        assert getattr(fp, name) is getattr(palettes, name)


# ---- photos of your own: pure helpers ----

RING = [(0.0, 0.0), (10.0, 0.0), (10.0, 6.0), (0.0, 6.0)]  # counter-clockwise: edge 0 faces south, 1 east, 2 north, 3 west
HOUSE = {"id": BID, "footprint": {"exterior": RING, "holes": []}}


@pytest.mark.parametrize(("side", "view"), [("S", (5.0, -10.0)), ("E", (20.0, 3.0)), ("N", (5.0, 16.0)), ("W", (-10.0, 3.0)), ("e", (20.0, 3.0))])
def test_a_compass_side_is_the_middle_of_that_wall_ten_metres_out(side, view):
    assert sr.view_from_side(HOUSE, side) == pytest.approx(view)


def test_a_wall_shared_with_a_neighbour_is_not_the_one_photographed():
    neighbour = [(10.0, 0.0), (20.0, 0.0), (20.0, 6.0), (10.0, 6.0)]  # stands against the east wall
    got = sr.view_from_side(HOUSE, "E", [neighbour])
    assert got != pytest.approx((20.0, 3.0)) and got in [pytest.approx((5.0, -10.0)), pytest.approx((5.0, 16.0))]
    apart = [(30.0, 0.0), (40.0, 0.0), (40.0, 6.0), (30.0, 6.0)]
    assert sr.view_from_side(HOUSE, "E", [apart]) == pytest.approx((20.0, 3.0))


def test_short_walls_are_no_candidates_and_a_bad_side_is_an_error():
    sliver = {"id": "w7", "footprint": {"exterior": [(0, 0), (10, 0), (10, 1), (0, 1)], "holes": []}}
    assert sr.view_from_side(sliver, "E") != pytest.approx((20.0, 0.5))  # the 1 m wall is not taken for east
    with pytest.raises(sr.SheetError, match="side must be one of"):
        sr.view_from_side(HOUSE, "up")


def test_a_sketch_point_is_looked_up_in_the_sketch_file(tmp_path):
    path = tmp_path / "sketch.json"
    doc = sketchfile.empty_doc()
    doc["points"] = [{"id": "A", "x": -589000.5, "y": -1250100.0, "sketch": "DEFAULT"}, {"id": "B", "x": 3.0, "y": 4.0, "sketch": "DEFAULT"}]
    sketchfile.write(path, doc)
    local = lambda xy: (xy[0] + 589000.0, xy[1] + 1250000.0)
    assert sr.view_from_point("a", path, local) == pytest.approx((-0.5, -100.0))
    assert sr.view_from_point("B", path, local) == pytest.approx((589003.0, 1250004.0))
    with pytest.raises(sr.SheetError, match=r"point C is not in .* \(it has: A, B\)"):
        sr.view_from_point("C", path, local)
    with pytest.raises(sr.SheetError, match="no sketch file"):
        sr.view_from_point("A", tmp_path / "none.json", local)
    (tmp_path / "bad.json").write_text("{not json")
    with pytest.raises(sr.SheetError, match="unreadable"):
        sr.view_from_point("A", tmp_path / "bad.json", local)


def test_a_view_is_taken_as_it_is():
    assert sr.resolve_view(sr.PhotoSpec(Path("a.png"), "view", (1.5, -2.0)), HOUSE) == (1.5, -2.0)


POINTS = ["c0:ground:100,500", "c0:eave:100,200", "c1:eave:600,210", "c1:ground:600,505", "c2:eave:900,300"]
GOOD = {
    "what": "house, cream render", "photos": [{"points": POINTS, "profile": {"edge": 1, "px": [[10, 20], [30, 40]]}, "openings": [{"edge": 0, "kind": "window", "px": [[1, 2], [3, 4]]}]}],
    "colours": {"wall": [800, 560, 0], "roof": "#a05030", "frame": "#e6e8e8"}, "overhang_m": 0.3, "not_modelled": "gate", "roof": "gabled", "building": "w999", "top_m": 99,
}  # fmt: skip


def test_the_sheet_is_checked_and_cut_to_what_the_model_may_say():
    sheet = sr.validate_sheet(GOOD, 1)
    assert "building" not in sheet and "top_m" not in sheet and sheet["photos"][0]["profile"]["edge"] == 1
    assert sheet["photos"][0]["points"] == POINTS and sheet["roof"] == "gabled"
    assert sr.validate_sheet({**GOOD, "photos": [{"points": POINTS}]}, 1)["photos"][0]["openings"] == []


@pytest.mark.parametrize(
    ("change", "message"),
    [
        ({"photos": []}, "list of 1 entries"),
        ({"photos": [{"points": POINTS[:4], "openings": []}]}, "5 or more points"),
        ({"photos": [{"points": "c0:ground:1,1", "openings": []}]}, "list of WHERE:LEVEL:X,Y"),
        ({"photos": [{"points": [*POINTS[:4], "c0 ground 5"], "openings": []}]}, "not WHERE:LEVEL:X,Y"),
        ({"photos": [{"points": POINTS, "openings": {}}]}, "openings must be a list"),
        ({"photos": [{"points": POINTS, "openings": [{"edge": 0, "px": [[1, 2]]}]}]}, "openings must be a list"),
        ({"photos": [{"points": POINTS, "profile": {"edge": 1}, "openings": []}]}, "profile must be"),
        ({"colours": None}, "colours is missing"),
        ({"overhang_m": "wide"}, "overhang_m must be a number"),
    ],
)
def test_a_sheet_of_the_wrong_shape_is_refused(change, message):
    with pytest.raises(sr.SheetError, match=message):
        sr.validate_sheet({**GOOD, **change}, 1)
    with pytest.raises(sr.SheetError, match="not a JSON object"):
        sr.validate_sheet([], 1)


def test_the_program_adds_what_only_it_knows():
    sheet = sr.inject(sr.validate_sheet(GOOD, 1), BID, [{"photo": "/a/1.png", "view": [1.0, 2.0], "cam_height": 1.6}], "photo of your own (1.png)")
    assert list(sheet)[:2] == ["building", "what"] and sheet["building"] == BID and sheet["source"] == "photo of your own (1.png)"
    assert sheet["photos"][0] == {"photo": "/a/1.png", "view": [1.0, 2.0], "cam_height": 1.6, **sr.validate_sheet(GOOD, 1)["photos"][0]}
    assert sr.inject({**sr.validate_sheet(GOOD, 1), "source": "mine"}, BID, [{"photo": "p", "view": [0, 0], "cam_height": 1}], "x")["source"] == "mine"


def test_the_prompt_is_made_of_the_skill_file():
    md = sr.SKILL_SHEET.read_text()
    text = sr.sheet_prompt(md, 2)
    assert "**`points`**" in text and "**`profile`**" in text and "**`openings`**" in text and "**`colours`**" in text
    assert "Which wall is which" in text and "exactly 2 entries" in text
    assert "landmark_photo apply" not in text  # the commands of the guide are not in what the model reads
    with pytest.raises(sr.SheetError, match="no section 2"):
        sr.rules_text("## 1. Prep\nIt prints, per photo\n")


def test_read_sheet_asks_once_more_when_the_reply_is_no_good(tmp_path, capsys):
    grid = tmp_path / "grid_0.png"
    grid.write_bytes(b"\x89PNG")
    client = FakeClient("not json at all", json.dumps(GOOD))
    sheet = sr.read_sheet(client, sr.SKILL_SHEET.read_text(), ["footprint text"], [grid])
    assert sheet["photos"][0]["points"] == POINTS and len(client.sent) == 2
    blocks = client.sent[0]["messages"][0]["content"]
    assert any(b["type"] == "image" for b in blocks) and any("footprint text" in b.get("text", "") for b in blocks)
    with pytest.raises(sr.SheetError, match="the model's answer was not usable: .*; try again"):
        sr.read_sheet(FakeClient('{"what": "x"}'), sr.SKILL_SHEET.read_text(), ["t"], [grid])


# ---- photos of your own: the run ----


def own_fakes(monkeypatch, env, sheet=GOOD, summary=True):
    from drycastle_pipeline.photos import landmark_photo as lp

    photo = env.tmp / "mine.png"
    photo.write_bytes(b"png")
    monkeypatch.setattr(sr, "neighbours", lambda bid: ({**HOUSE, "parcel": "P-0005"}, []))

    def prep(bid, n, photo, view, out):
        out.mkdir(parents=True, exist_ok=True)
        (out / f"grid_{n}.png").write_bytes(b"png")
        return f"footprint seen from {view}", out / f"grid_{n}.png"

    applied = []

    def apply(sheet_path, out, replace, dry):
        applied.append((json.loads(sheet_path.read_text()), replace, dry))
        (out / "overlay_0.png").write_bytes(b"png")
        (env.tmp / "overrides.yaml").write_text(be.with_block((env.tmp / "overrides.yaml").read_text(), BID, BLOCK))
        (env.tmp / "details" / f"{BID}.yaml").write_text(DETAIL)
        print(f"\n{BID}: gabled roof, eave 3.20 m, top 7.50 m, 1 opening(s), colours #ecdcb2 / #a05030")
        print("PROBLEM: the photos disagree on the eave")
        print("written: config/buildings/w5.yaml and its entry in config/overrides.yaml")

    monkeypatch.setattr(sr, "prep_photo", prep)
    monkeypatch.setattr(lp, "apply", apply)
    return photo, applied


def test_own_photos_go_prep_read_sheet_apply(env, monkeypatch, capsys):
    photo, applied = own_fakes(monkeypatch, env)
    client = FakeClient(json.dumps(GOOD))
    a = ab.namespace(building=BID, cam_height=1.4, photo_seq=[("photo", str(photo)), ("side", "S")])
    assert ab.run(a, SimpleNamespace(), client_of=lambda: client) == 0
    lines = capsys.readouterr().out.splitlines()
    assert [ln for ln in lines if ln.startswith("STAGE: ")] == ["STAGE: prep 1/3", "STAGE: read sheet 2/3", "STAGE: apply 3/3"]
    (sheet, replace, dry), = applied
    assert (replace, dry) == (False, False)
    assert sheet["building"] == BID and "top_m" not in sheet and sheet["source"].startswith("photo of your own")
    assert sheet["photos"][0]["photo"] == str(photo.resolve()) and sheet["photos"][0]["cam_height"] == 1.4
    assert sheet["photos"][0]["view"] == pytest.approx([5.0, -10.0])  # south of the house, 10 m
    assert json.loads((env.own / "sheet.json").read_text()) == sheet
    r = result_of(lines)
    own = str(env.own.resolve())
    assert r["method"] == "own" and r["run"] == own and r["overlays"] == [f"{own}/overlay_0.png"] and r["compares"] == [] and r["renders"] == []
    assert (r["problems"], r["invented"], r["roof"], r["eave_m"], r["top_m"], r["openings"]) == (["the photos disagree on the eave"], [], "gabled", 3.2, 7.5, 1)
    assert (r["can_redo_ends"], r["result_json"], r["parcel"], r["replaced"]) == (False, None, "P-0005", False)
    assert "PROBLEM: the photos disagree on the eave" in lines  # still printed
    assert json.loads((env.tmp / "photos" / BID / "measured.json").read_text()) == {"override": BLOCK, "detail": DETAIL}


def test_own_photos_with_a_bad_reply_or_a_missing_file_are_errors(env, monkeypatch, capsys):
    photo, applied = own_fakes(monkeypatch, env)
    a = ab.namespace(building=BID, photo_seq=[("photo", str(photo)), ("side", "S")])
    assert ab.run(a, SimpleNamespace(), client_of=lambda: FakeClient('{"what": "x"}')) == 1
    out = capsys.readouterr().out.splitlines()
    assert out[-1].startswith("ERROR: the model's answer was not usable") and applied == []
    a = ab.namespace(building=BID, photo_seq=[("photo", str(env.tmp / "nope.png")), ("side", "S")])
    assert ab.run(a, SimpleNamespace(), client_of=lambda: FakeClient("{}")) == 1
    assert "does not exist" in capsys.readouterr().out
    a = ab.namespace(building=BID, photo_seq=[("photo", str(photo)), ("point", "A")])
    monkeypatch.setattr(sr, "view_from_point", lambda *x, **k: (_ for _ in ()).throw(sr.SheetError("sketch point A is not in /x")))
    assert ab.run(a, SimpleNamespace(), client_of=lambda: FakeClient("{}")) == 1
    assert capsys.readouterr().out.splitlines()[-1] == "ERROR: sketch point A is not in /x"
