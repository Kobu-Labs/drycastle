import json

import pytest
from shapely.geometry import LineString, Point, shape

from drycastle_pipeline.edits import (
    Edits,
    EditsError,
    dump_edits,
    dump_text,
    load_edits,
    parse,
    resolve_fence,
    validate,
)
from drycastle_pipeline.props import make_fences, run_props
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain, make_workspace

FENCE, TREE = load_section("props")["fences"], load_section("props")["trees"]
ORIGIN = SYNTHETIC_ORIGIN

FULL = {
    "version": 1,
    "parcels": {
        "P-0001": {
            "fence": {
                "mode": "custom",
                "type": "wire",
                "height_m": 1.4,
                "colour": "#223344",
                "gaps": [{"at": [ORIGIN[0] + 40, ORIGIN[1] + 70], "width_m": 3.0}],
            },
        },
        "P-0002": {"fence": {"mode": "none"}},
    },
    "objects": [  # written sorted by id
        {"id": "bench", "asset": "prim:box", "position": [1, 2], "z": 3},
        {
            "id": "car-1",
            "asset": "pack:cars/sedan",
            "position": [ORIGIN[0] + 50, ORIGIN[1] + 50],
            "rotation_deg": 90,
            "scale": 1.1,
            "colour": "#aa0000",
        },
    ],
    "groups": {
        "G-0001": {
            "parcels": ["P-0004", "P-0005"],
            "fence": {"mode": "auto"},
        },
    },
}


def errors_for(doc) -> list[str]:
    return validate(doc)


def with_parcel(entry: dict) -> dict:
    return {"version": 1, "parcels": {"P-0412": entry}}


def test_validator_accepts_full_document() -> None:
    assert validate(FULL) == []
    edits = parse(FULL)
    assert edits.parcels["P-0001"]["fence"]["type"] == "wire"
    assert edits.groups["G-0001"]["parcels"] == ["P-0004", "P-0005"]


@pytest.mark.parametrize(
    ("doc", "message"),
    [
        ([], "<root>: must be an object"),
        ({"version": 2}, "version: must be 1"),
        ({"version": True}, "version: must be 1"),
        ({"version": 1, "extra": 1}, "extra: unknown key"),
        (with_parcel({"fence": {"colr": "#000000"}}), "parcels.P-0412.fence.colr: unknown key"),
        (with_parcel({"fence": {"mode": "fancy"}}), "parcels.P-0412.fence.mode: must be one of"),
        (with_parcel({"fence": {"mode": "none", "gaps": []}}), "fence.gaps: not used with mode"),
        (with_parcel({"fence": {"mode": "auto", "type": "wire"}}), "fence.type: not used"),
        (with_parcel({"fence": {"type": "castle"}}), "parcels.P-0412.fence.type: must be one of"),
        (with_parcel({"fence": {"height_m": -1}}), "parcels.P-0412.fence.height_m: must be"),
        (
            with_parcel({"fence": {"thickness_m": True}}),
            "parcels.P-0412.fence.thickness_m: must be",
        ),
        (with_parcel({"fence": {"colour": "red"}}), "parcels.P-0412.fence.colour: must be"),
        (with_parcel({"fence": {"gaps": {}}}), "parcels.P-0412.fence.gaps: must be a list"),
        (
            with_parcel({"fence": {"gaps": [{"at": [1, 2, 3], "width_m": 2}]}}),
            "parcels.P-0412.fence.gaps.0.at: must be [x, y]",
        ),
        (
            with_parcel({"fence": {"gaps": [{"at": [1, 2], "width_m": 0}]}}),
            "parcels.P-0412.fence.gaps.0.width_m: must be",
        ),
        (with_parcel({"fence": {"gaps": [{"width_m": 2}]}}), "gaps.0.at: required"),
        (with_parcel({"trees": {"count": 1}}), "parcels.P-0412.trees: per-parcel tree settings were removed"),
        (with_parcel({"objects": {}}), "parcels.P-0412.objects: must be a list"),
        (
            with_parcel({"objects": [{"id": "a", "asset": "prim:box", "position": [1]}]}),
            "parcels.P-0412.objects.0.position: must be [x, y]",
        ),
        (
            with_parcel({"objects": [{"id": "a", "asset": "box", "position": [1, 2]}]}),
            "parcels.P-0412.objects.0.asset: must be",
        ),
        (
            with_parcel({"objects": [{"asset": "prim:box", "position": [1, 2]}]}),
            "parcels.P-0412.objects.0.id: required",
        ),
        (
            with_parcel(
                {
                    "objects": [
                        {"id": "a", "asset": "prim:box", "position": [1, 2]},
                        {"id": "a", "asset": "prim:box", "position": [3, 4]},
                    ]
                }
            ),
            "duplicate object id 'a'",
        ),
        (
            with_parcel(
                {"objects": [{"id": "a", "asset": "prim:box", "position": [1, 2], "scale": 0}]}
            ),
            "objects.0.scale: must be",
        ),
        (
            with_parcel(
                {"objects": [{"id": "a", "asset": "prim:box", "position": [1, 2], "spin": 1}]}
            ),
            "objects.0.spin: unknown key",
        ),
        (
            with_parcel(
                {"objects": [{"id": "a", "asset": "prim:box", "position": [1, 2], "colour": "x"}]}
            ),
            "objects.0.colour: must be",
        ),
        ({"version": 1, "parcels": {"Q-1": {}}}, "parcels.Q-1: parcel IDs look like"),
        (
            {"version": 1, "groups": {"G-1": {"parcels": ["P-0001"]}}},
            "groups.G-1: group IDs look like",
        ),
        ({"version": 1, "groups": {"G-0001": {"parcels": []}}}, "parcels: must be a non-empty"),
        ({"version": 1, "groups": {"G-0001": {}}}, "groups.G-0001.parcels: required"),
        (
            {"version": 1, "groups": {"G-0001": {"parcels": ["P-0001", "P-0001"]}}},
            "parcels.1: parcel P-0001 listed twice",
        ),
        (
            {"version": 1, "groups": {"G-0001": {"parcels": ["P-0001"], "name": "x"}}},
            "groups.G-0001.name: unknown key",
        ),
        (
            {
                "version": 1,
                "groups": {
                    "G-0001": {"parcels": ["P-0001", "P-0002"]},
                    "G-0002": {"parcels": ["P-0003", "P-0002"]},
                },
            },
            "groups.G-0002.parcels.1: parcel P-0002 is already in group G-0001",
        ),
        (
            {"version": 1, "groups": {"G-0001": {"parcels": ["P-0001"], "fence": {"mode": "x"}}}},
            "groups.G-0001.fence.mode: must be one of",
        ),
    ],
)
def test_validator_rejects_each_class_of_error(doc, message) -> None:
    errs = errors_for(doc)
    assert errs, "expected a validation error"
    assert any(message in e for e in errs), errs


def test_validator_reports_every_error_not_just_the_first() -> None:
    doc = {"version": 1, "parcels": {"P-0412": {"fence": {"mode": "x", "colr": 1}}}}
    assert len(validate(doc)) == 2


def test_parse_raises_with_all_errors() -> None:
    with pytest.raises(EditsError) as info:
        parse({"version": 1, "parcels": {"P-0412": {"trees": {}}}, "bogus": 1})
    assert len(info.value.errors) == 2


def test_missing_file_is_empty_edits(tmp_path) -> None:
    assert load_edits(tmp_path / "nope.json") == Edits()


def test_round_trip_is_byte_stable(tmp_path) -> None:
    # a hand-written file with unsorted keys and different spacing
    src = tmp_path / "hand.json"
    src.write_text(json.dumps(FULL, indent=4))
    first = tmp_path / "first.json"
    second = tmp_path / "second.json"
    dump_edits(load_edits(src), first)
    dump_edits(load_edits(first), second)
    assert first.read_bytes() == second.read_bytes()
    assert first.read_text() == dump_text(load_edits(first)) == dump_text(load_edits(src))
    assert first.read_text().endswith("}\n")
    assert json.loads(first.read_text()) == FULL
    assert not list(tmp_path.glob("*.tmp"))  # no temp files left behind


def test_dump_refuses_invalid_edits(tmp_path) -> None:
    target = tmp_path / "edits.json"
    with pytest.raises(EditsError):
        dump_edits(Edits(parcels={"P-0001": {"trees": {}}}), target)
    assert not target.exists()


def test_load_reports_bad_json(tmp_path) -> None:
    bad = tmp_path / "edits.json"
    bad.write_text("{not json")
    with pytest.raises(EditsError, match="not valid JSON"):
        load_edits(bad)


def test_shipped_edits_file_is_canonical_and_valid() -> None:
    from drycastle_pipeline.edits import DEFAULT_EDITS

    text = DEFAULT_EDITS.read_text()
    assert text == dump_text(load_edits(DEFAULT_EDITS))
    assert not [pid for pid, e in json.loads(text)["parcels"].items() if "objects" in e]  # the new shape


def test_resolve_fence_group_inheritance() -> None:
    group = {"mode": "custom", "type": "brick_wall", "gaps": [{"at": [1, 2], "width_m": 4}]}
    spec = resolve_fence(None, group)
    assert (spec.mode, spec.type, spec.height_m) == ("custom", "brick_wall", 1.8)
    assert spec.gaps == ((1.0, 2.0, 4.0),)
    # the member overrides the colour and keeps the group's other settings
    spec = resolve_fence({"colour": "#000000"}, group)
    assert spec.colour == "#000000" and spec.type == "brick_wall" and spec.gaps == ((1, 2, 4),)
    # the member's own gaps replace the group's
    assert resolve_fence({"gaps": []}, group).gaps == ()
    # the member may turn the fence off
    assert resolve_fence({"mode": "none"}, group).mode == "none"
    # no mode and no custom key: auto; custom keys without a mode: custom
    assert resolve_fence({}).mode == "auto"
    assert resolve_fence({"colour": "#111111"}).mode == "custom"


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


def fences(ws, specs=None, groups=None, cfg=None):
    return make_fences(
        ws["parcels"],
        ws["buildings"],
        ws["graph"],
        ws["terrain"],
        cfg or {**FENCE, "probability": 1.0},
        specs=specs,
        groups=groups,
    )


def on_line(sections, pid=None):
    return [s for s in sections if pid is None or s["parcel"] == pid]


def test_fence_none_gives_no_fence(ws) -> None:
    specs = {"P-0001": resolve_fence({"mode": "none"})}
    got = fences(ws, specs)
    assert on_line(got, "P-0001") == []
    assert on_line(got, "P-0002")  # other parcels unaffected


def test_fence_custom_ignores_probability_and_kind(ws) -> None:
    cfg = {**FENCE, "probability": 0.0}
    assert fences(ws, cfg=cfg) == []
    specs = {"P-0004": resolve_fence({"type": "brick_wall", "colour": "#101010"})}
    got = fences(ws, specs, cfg=cfg)  # P-0004 is a field: not a fenced kind by rule
    assert got and {s["parcel"] for s in got} == {"P-0004"}
    sec = got[0]
    assert sec["height_m"] == 1.8 and sec["thickness_m"] == 0.3 and sec["colour"] == "#101010"


def test_fence_auto_sections_keep_rule_style(ws) -> None:
    got = fences(ws)
    assert got and all("height_m" not in s and "colour" not in s for s in got)


def test_fence_gap_is_cut_at_the_given_point(ws) -> None:
    # west edge of P-0001 is x = 40, y from 40 to 100; gap 6 m centred on y = 70
    gap_at = (40.0, 70.0)
    base = {"mode": "custom", "gaps": []}
    open_ = on_line(fences(ws, {"P-0001": resolve_fence(base)}), "P-0001")
    gapped = on_line(
        fences(
            ws,
            {"P-0001": resolve_fence({**base, "gaps": [{"at": [40, 70], "width_m": 6}]})},
        ),
        "P-0001",
    )

    def west(sections):
        return [s for s in sections if s["a"][0] == 40 and s["b"][0] == 40]

    def mids(sections):
        return [(s["a"][1] + s["b"][1]) / 2 for s in sections]

    assert any(abs(m - gap_at[1]) < 1 for m in mids(west(open_)))
    assert not any(abs(m - gap_at[1]) < 2.5 for m in mids(west(gapped)))
    assert any(m > 75 for m in mids(west(gapped)))  # the rest of the edge stays fenced


def test_gap_positions_are_local_after_run(ws, tmp_path) -> None:
    edits = tmp_path / "edits.json"
    dump_edits(
        parse(
            {
                "version": 1,
                "parcels": {
                    "P-0001": {
                        "fence": {
                            "mode": "custom",
                            "gaps": [{"at": [ORIGIN[0] + 40, ORIGIN[1] + 70], "width_m": 6}],
                        }
                    }
                },
            }
        ),
        edits,
    )
    run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    doc = json.loads((ws["work"] / "props.json").read_text())
    west = [s for s in doc["fences"] if s["parcel"] == "P-0001" and s["a"][0] == 40]
    assert west and not any(abs((s["a"][1] + s["b"][1]) / 2 - 70) < 2.5 for s in west)


def off_shared_edge(section) -> bool:
    """True when the section's middle is clear of the shared edge x = 95 (corners may touch)."""
    mid = Point((section["a"][0] + section["b"][0]) / 2, (section["a"][1] + section["b"][1]) / 2)
    return mid.distance(LineString([(95, 40), (95, 100)])) > 0.2


def group_custom(members=("P-0001", "P-0002")):
    return {pid: resolve_fence(None, {"mode": "custom"}) for pid in members}


def test_group_perimeter_has_no_fence_on_shared_boundary(ws) -> None:
    got = fences(ws, group_custom(), groups={"G-0001": ["P-0001", "P-0002"]})
    assert all(off_shared_edge(s) for s in got)
    # the outer edge of both members is fenced, owned by the member it belongs to
    assert any(s["parcel"] == "P-0001" and s["a"][0] == 40 for s in got)
    assert any(s["parcel"] == "P-0002" and s["a"][0] == 140 for s in got)
    assert {s["parcel"] for s in got} == {"P-0001", "P-0002"}


def test_group_member_override_none_only_drops_its_own_edge(ws) -> None:
    specs = {
        "P-0001": resolve_fence(None, {"mode": "custom"}),
        "P-0002": resolve_fence({"mode": "none"}, {"mode": "custom"}),
    }
    got = fences(ws, specs, groups={"G-0001": ["P-0001", "P-0002"]})
    assert on_line(got, "P-0002") == []
    assert any(s["parcel"] == "P-0001" for s in got)


def test_group_auto_uses_one_seeded_roll_for_all_members(ws) -> None:
    groups = {"G-0001": ["P-0001", "P-0002"]}
    assert fences(ws, groups=groups, cfg={**FENCE, "probability": 0.0}) == []
    got = fences(ws, groups=groups)
    assert {s["parcel"] for s in got} == {"P-0001", "P-0002"}
    assert all(off_shared_edge(s) for s in got)


def write_edits(tmp_path, doc):
    path = tmp_path / "edits.json"
    dump_edits(parse(doc), path)
    return path


def read_objects(work):
    return json.loads((work / "objects.json").read_text())


def test_objects_json_content_and_order(ws, tmp_path, capsys) -> None:
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "objects": [
                {
                    "id": "z-bench",
                    "asset": "prim:slab",
                    "position": [ORIGIN[0] + 120, ORIGIN[1] + 60],
                    "colour": "#334455",
                },
                {
                    "id": "car-1",
                    "asset": "pack:kenney-car-kit/sedan",
                    "position": [ORIGIN[0] + 50.25, ORIGIN[1] + 50],
                    "z": 101.5,
                    "rotation_deg": 90,
                    "scale": 1.5,
                },
            ],
        },
    )
    run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    doc = read_objects(ws["work"])
    assert doc["format_version"] == 1
    assert [o["id"] for o in doc["objects"]] == ["car-1", "z-bench"]
    car, bench = doc["objects"]
    assert car == {
        "id": "car-1",
        "asset": "pack:kenney-car-kit/sedan",
        "position": [50.25, 50.0],
        "z": 101.5,
        "rotation_deg": 90.0,
        "scale": 1.5,
        "colour": None,
        "parcel": "P-0001",
        "chunk": [0, 0],
    }
    assert bench["parcel"] == "P-0002" and bench["colour"] == "#334455"
    assert bench["position"] == [120.0, 60.0] and bench["z"] is None
    assert capsys.readouterr().err == ""


def test_object_outside_every_parcel_is_placed_without_a_warning(ws, tmp_path, capsys) -> None:
    # an object belongs to no parcel: a pole on a road or on ground needs only its position
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "objects": [
                {"id": "lost", "asset": "prim:box", "position": [ORIGIN[0] + 900, ORIGIN[1]]}
            ],
        },
    )
    run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    (obj,) = read_objects(ws["work"])["objects"]
    assert obj["id"] == "lost" and obj["parcel"] is None
    assert "outside" not in capsys.readouterr().err


def test_object_parcel_is_the_one_containing_it(ws, tmp_path, capsys) -> None:
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "objects": [
                {"id": "moved", "asset": "prim:box", "position": [ORIGIN[0] + 120, ORIGIN[1] + 50]}
            ],
        },
    )
    run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    (obj,) = read_objects(ws["work"])["objects"]
    assert obj["parcel"] == "P-0002"
    assert "outside" not in capsys.readouterr().err


def test_objects_listed_under_a_parcel_are_still_read_and_moved_to_the_top_level(ws, tmp_path) -> None:
    # the old shape (a file from before objects belonged to no parcel) still loads
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "parcels": {
                "P-0002": {"objects": [{"id": "o-0002", "asset": "prim:box", "position": [1, 2]}]},
                "P-0001": {
                    "fence": {"mode": "none"},
                    "objects": [{"id": "o-0001", "asset": "prim:box", "position": [3, 4]}],
                },
            },
        },
    )
    loaded = load_edits(edits)
    assert [o["id"] for o in loaded.objects] == ["o-0001", "o-0002"]  # sorted by id
    assert loaded.parcels == {"P-0001": {"fence": {"mode": "none"}}}  # P-0002 held only objects
    doc = json.loads(dump_text(loaded))
    assert [o["id"] for o in doc["objects"]] == ["o-0001", "o-0002"]
    assert all("objects" not in e for e in doc["parcels"].values())  # written in the new shape


def test_object_ids_are_unique_across_the_top_level_and_old_shape() -> None:
    doc = {
        "version": 1,
        "objects": [{"id": "o-0001", "asset": "prim:box", "position": [1, 2]}],
        "parcels": {"P-0001": {"objects": [{"id": "o-0001", "asset": "prim:box", "position": [3, 4]}]}},
    }
    assert any("duplicate object id 'o-0001'" in e for e in validate(doc))


def test_stale_entries_warn_and_are_skipped(ws, tmp_path, capsys) -> None:
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "parcels": {
                "P-9999": {"fence": {"mode": "custom"}},
                "P-0001": {"fence": {"mode": "none"}},
            },
            "objects": [{"id": "ghost", "asset": "prim:box", "position": [1, 1]}],
            "groups": {
                "G-0001": {"parcels": ["P-0002", "P-9998"], "fence": {"mode": "custom"}},
                "G-0002": {"parcels": ["P-9997"]},
            },
        },
    )
    counts = run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    err = capsys.readouterr().err
    assert "parcels.P-9999: parcel not found; fence skipped" in err
    assert "groups.G-0001: parcel P-9998 not found; skipped" in err
    assert "groups.G-0002: no member parcels left; group skipped" in err
    # an object belongs to no parcel, so a dropped parcel loses none
    (ghost,) = read_objects(ws["work"])["objects"]
    assert (ghost["id"], ghost["parcel"]) == ("ghost", None)
    assert counts["objects"] == 1 and "outside every parcel" not in err
    props = json.loads((ws["work"] / "props.json").read_text())
    assert not [s for s in props["fences"] if s["parcel"] == "P-0001"]  # mode none applied


def test_run_props_applies_groups_end_to_end(ws, tmp_path) -> None:
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "parcels": {},
            "groups": {"G-0001": {"parcels": ["P-0001", "P-0002"], "fence": {"mode": "custom"}}},
        },
    )
    run_props(ws["work"], DEFAULT_RULES, edits_path=edits)
    props = json.loads((ws["work"] / "props.json").read_text())
    assert all(off_shared_edge(s) for s in props["fences"])
    assert any(t["src"] == "ortho-canopy" for t in props["trees"])  # the trees still come from the orthophoto


def test_empty_edits_leave_props_unchanged(ws, tmp_path) -> None:
    def props_bytes(edits_path):
        run_props(ws["work"], DEFAULT_RULES, edits_path=edits_path)
        return (ws["work"] / "props.json").read_bytes(), (ws["work"] / "spawns.json").read_bytes()

    no_file = props_bytes(tmp_path / "absent.json")
    empty = tmp_path / "empty.json"
    dump_edits(Edits(), empty)
    assert props_bytes(empty) == no_file
    assert read_objects(ws["work"]) == {"format_version": 1, "objects": []}


def test_missing_origin_fails_only_when_objects_or_gaps_need_it(ws, tmp_path) -> None:
    doc = json.loads((ws["work"] / "parcels.json").read_text())
    del doc["origin_epsg5514"]
    (ws["work"] / "parcels.json").write_text(json.dumps(doc))
    run_props(ws["work"], DEFAULT_RULES, edits_path=tmp_path / "absent.json")  # fine: no edits
    edits = write_edits(
        tmp_path,
        {
            "version": 1,
            "parcels": {
                "P-0001": {"objects": [{"id": "a", "asset": "prim:box", "position": [1, 1]}]}
            },
        },
    )
    with pytest.raises(ValueError, match="origin_epsg5514"):
        run_props(ws["work"], DEFAULT_RULES, edits_path=edits)


def test_parcel_geometry_fixture_is_as_described(ws) -> None:
    # the group and gap tests rely on these exact edges of the synthetic slice
    polys = {p["id"]: shape(p["polygon"]) for p in ws["parcels"]}
    assert polys["P-0001"].bounds == (40, 40, 95, 100)
    assert polys["P-0002"].bounds == (95, 40, 140, 100)


def test_custom_sections_carry_their_own_fence_style(ws) -> None:
    from drycastle_pipeline.propmeshes import fence_mesh
    from drycastle_pipeline.rules import load_section as load

    cfg = {**FENCE, "probability": 0.0}
    got = fences(ws, {"P-0001": resolve_fence({"type": "brick_wall"})}, cfg=cfg)
    assert got and all(s["height_m"] == 1.8 and s["type"] == "brick_wall" for s in got)
    assert all("column" not in s for s in got)  # only written when the edit names a column
    mesh = fence_mesh(got, load("props")["fences"])
    assert mesh.face_count == 10 * len(got)
    # the type's tint (not the rules colour) is on every triangle of its sections
    assert (mesh.colours == [0xFF, 0xFF, 0xFF]).all()
    got = fences(ws, {"P-0001": resolve_fence({"type": "picket", "column": "brick_red"})}, cfg=cfg)
    assert got and all(s["column"] == "brick_red" and s["colour"] == "#9a8064" for s in got)


def test_roundtrip_fixture_is_canonical():
    """tests/fixtures/edits_roundtrip.json is the file the Godot editor's C# writer must reproduce byte for
    byte (`make viewer-editor-smoke`); it must stay exactly what dump_text writes for its own content."""
    from pathlib import Path

    text = (Path(__file__).parent / "fixtures" / "edits_roundtrip.json").read_text(encoding="utf-8")
    assert dump_text(parse(json.loads(text))) == text


# --- cables ---


def _cable_doc(cables: dict) -> dict:
    return {"version": 1, "parcels": {}, "groups": {}, "cables": cables}


def test_cables_are_validated() -> None:
    assert validate(_cable_doc({"C-0001": {"from": "o-0001", "to": "o-0002"}})) == []
    assert validate(_cable_doc({"X1": {"from": "a", "to": "b"}})) == ["cables.X1: cable IDs look like C-0001"]
    assert validate(_cable_doc({"C-0001": {"from": "a"}})) == ["cables.C-0001.to: required"]
    assert validate(_cable_doc({"C-0001": {"from": "a", "to": 3}})) == [
        "cables.C-0001.to: must be the id of a placed object"
    ]
    assert validate(_cable_doc({"C-0001": {"from": "a", "to": "a"}})) == [
        "cables.C-0001: from and to must be two different poles"
    ]
    assert validate(_cable_doc({"C-0001": {"from": "a", "to": "b", "sag": 1}})) == ["cables.C-0001.sag: unknown key"]
    both = {"C-0001": {"from": "a", "to": "b"}, "C-0002": {"from": "b", "to": "a"}}
    assert validate(_cable_doc(both)) == ["cables.C-0002: a and b are already joined by C-0001"]


def test_cables_key_is_written_only_when_there_are_cables(tmp_path) -> None:
    assert '"cables"' not in dump_text(Edits())
    edits = Edits(cables={"C-0002": {"to": "o-0002", "from": "o-0001"}})
    text = dump_text(edits)
    assert text == (
        '{\n "cables": {\n  "C-0002": {\n   "from": "o-0001",\n   "to": "o-0002"\n  }\n },\n'
        ' "groups": {},\n "parcels": {},\n "version": 1\n}\n'
    )
    dump_edits(edits, tmp_path / "e.json")
    assert load_edits(tmp_path / "e.json") == edits


def test_plan_keeps_cables_between_placed_poles_only() -> None:
    from drycastle_pipeline.props import plan_edits

    square = {"type": "Polygon", "coordinates": [[[0, 0], [50, 0], [50, 50], [0, 50], [0, 0]]]}
    parcels = [{"id": "P-0001", "polygon": square}]

    def obj(oid: str, asset: str, x: float) -> dict:
        return {"id": oid, "asset": asset, "position": [x, 10.0]}

    edits = Edits(
        objects=[
            obj("o-0001", "prim:pole_lamp_1", 5), obj("o-0002", "prim:pole_plain_1", 30), obj("o-0003", "prim:slab", 40),
            {**obj("o-0004", "prim:connector_wall_2", 20), "z": 204.5, "rotation_deg": 90.0},
        ],
        cables={
            "C-0002": {"from": "o-0002", "to": "o-0001"},
            "C-0003": {"from": "o-0001", "to": "o-0003"},
            "C-0004": {"from": "o-0002", "to": "o-0009"},
            "C-0005": {"from": "o-0001", "to": "o-0004"},
        },
    )
    plan = plan_edits(edits, parcels, [0.0, 0.0], 100.0)
    assert plan.cables == [
        {"id": "C-0002", "from": "o-0002", "to": "o-0001"},
        {"id": "C-0005", "from": "o-0001", "to": "o-0004"},  # a pole to a house connector
    ]
    assert next(o for o in plan.objects if o["id"] == "o-0004")["z"] == 204.5
    assert "cables.C-0003: o-0003 is not a utility pole or a house connector; cable skipped" in plan.warnings
    assert "cables.C-0004: o-0009 is not placed; cable skipped" in plan.warnings


def test_objects_are_written_sorted_by_id() -> None:
    edits = Edits(
        objects=[
            {"id": "o-0010", "asset": "prim:box", "position": [1, 1]},
            {"id": "o-0002", "asset": "prim:box", "position": [2, 2]},
            {"id": "car-1", "asset": "prim:box", "position": [3, 3]},
        ]
    )
    doc = json.loads(dump_text(edits))
    assert [o["id"] for o in doc["objects"]] == ["car-1", "o-0002", "o-0010"]
    assert [o["id"] for o in edits.objects] == ["o-0010", "o-0002", "car-1"]  # the document in memory is not reordered
