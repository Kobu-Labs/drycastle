"""MCP server tools on the synthetic workspace (P-0001 building plot with a house, road to its north)."""

import asyncio
import json
import threading
import time

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from suchohrad_pipeline.edits import dump_text, load_edits, validate
from suchohrad_pipeline.mcp_server import (
    ParcelEditTools,
    Settings,
    build_server,
    object_polygon,
    parse_args,
)
from tests.synthetic import SYNTHETIC_ORIGIN, make_workspace

OX, OY = SYNTHETIC_ORIGIN
CAR = "pack:kenney-car-kit/sedan"


@pytest.fixture
def env(tmp_path):
    ws = make_workspace(tmp_path)
    settings = Settings(
        work_dir=ws["work"],
        edits_path=tmp_path / "edits.json",
        state_path=tmp_path / "editor-state.json",
    )
    return ParcelEditTools(settings), settings, tmp_path


@pytest.fixture
def tools(env):
    return env[0]


def canonical(settings) -> None:
    text = settings.edits_path.read_text()
    assert dump_text(load_edits(settings.edits_path)) == text
    assert validate(json.loads(text)) == []


def local(pos):
    return pos[0] - OX, pos[1] - OY


# --- read tools ---------------------------------------------------------------------------


def test_list_assets(tools):
    out = tools.list_assets()
    ids = {a["id"] for a in out["assets"]}
    assert CAR in ids and "prim:slab" in ids
    sedan = next(a for a in out["assets"] if a["id"] == CAR)
    assert set(sedan) == {"id", "category", "name", "size_m", "tags"} and len(sedan["size_m"]) == 3
    cars = tools.list_assets("car")["assets"]
    assert cars and all(a["category"] == "car" for a in cars)
    with pytest.raises(ToolError, match="unknown category"):
        tools.list_assets("spaceship")


def test_find_parcels_near_and_kind(tools):
    out = tools.find_parcels(near={"x": OX + 60, "y": OY + 60}, limit=3)
    assert out["parcels"][0]["id"] == "P-0001"
    assert out["parcels"][0]["distance_m"] == 0
    assert out["parcels"][0]["anchor"][0] > OX  # absolute S-JTSK
    around = tools.find_parcels(near={"parcel": "P-0001", "radius_m": 5})
    ids = {p["id"] for p in around["parcels"]}
    assert {"P-0001", "P-0002", "P-0003", "P-0006"} <= ids and "P-0004" not in ids
    assert [p["id"] for p in tools.find_parcels(kind="forest")["parcels"]] == ["P-0005"]
    assert tools.find_parcels(limit=2)["total_matching"] == 6
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.find_parcels(near={"parcel": "P-9999"})
    with pytest.raises(ToolError, match="unknown kind"):
        tools.find_parcels(kind="castle")
    with pytest.raises(ToolError, match="near needs"):
        tools.find_parcels(near={"x": 1})


def test_find_parcels_selected(env):
    tools, settings, _ = env
    with pytest.raises(ToolError, match="no editor state file"):
        tools.find_parcels(selected=True)
    state = {"format_version": 1, "selected": ["P-0002", "P-0001"], "camera": {"x": 1, "y": 2, "z": 3, "yaw_deg": 4}}
    settings.state_path.write_text(json.dumps(state))
    out = tools.find_parcels(selected=True)
    assert [p["id"] for p in out["parcels"]] == ["P-0001", "P-0002"]
    assert out["camera"]["yaw_deg"] == 4 and "warnings" not in out
    # stale file: still answers, with a warning
    old = time.time() - 3600
    import os

    os.utime(settings.state_path, (old, old))
    assert "min old" in tools.find_parcels(selected=True)["warnings"][0]
    # empty, foreign, broken
    settings.state_path.write_text(json.dumps({**state, "selected": []}))
    with pytest.raises(ToolError, match="nothing is selected"):
        tools.find_parcels(selected=True)
    settings.state_path.write_text(json.dumps({**state, "selected": ["P-4242"]}))
    with pytest.raises(ToolError, match="not in this map"):
        tools.find_parcels(selected=True)
    settings.state_path.write_text("{nope")
    with pytest.raises(ToolError, match="unreadable"):
        tools.find_parcels(selected=True)
    with pytest.raises(ToolError, match="not both"):
        tools.find_parcels(selected=True, near={"parcel": "P-0001"})


def test_get_parcel_front_edge_and_buildings(tools):
    p = tools.get_parcel("P-0001")
    assert p["kind"] == "building_plot" and p["area_m2"] == 3300.0
    front = p["front_edge"]
    # the road (P-0003) is north of the plot: front edge is its y=100 side, normal points north
    assert front["outward_normal"] == [0.0, 1.0]
    assert front["midpoint"] == [OX + 67.5, OY + 100.0]
    assert front["length_m"] == 55.0
    assert front["inward_axis"] == [0.0, -1.0] and front["right_axis"] == [-1.0, 0.0]
    assert [e["is_front"] for e in p["edges"]].count(True) == 1
    assert sorted(e["length_m"] for e in p["edges"]) == [55.0, 55.0, 60.0, 60.0]
    b = p["buildings"][0]
    assert b["id"] == "w1"
    assert b["footprint_centroid"] == [OX + 67.5, OY + 62.5]
    assert b["bounds"] == [OX + 60, OY + 55, OX + 75, OY + 70]
    assert p["polygon"]["bounds"] == [OX + 40, OY + 40, OX + 95, OY + 100]
    assert {n["id"] for n in p["neighbours"]} == {"P-0002", "P-0003"}
    assert p["effective_fence"]["mode"] == "auto"
    assert p["rule_defaults"]["trees"]["density_per_1000m2"] == 0.8
    assert p["rule_defaults"]["fence"]["kind_gets_fence"] is True
    assert p["edits"] == {"fence": None, "trees": None, "objects": [], "group_fence": None}
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.get_parcel("P-0777")


def test_get_parcel_shows_edits_and_group(tools):
    tools.set_fence("P-0001", height_m=1.5)
    gid = tools.create_group(["P-0002", "P-0005"], fence={"type": "wire"})["id"]
    assert tools.get_parcel("P-0001")["edits"]["fence"] == {"height_m": 1.5}
    g = tools.get_parcel("P-0002")
    assert g["group"] == gid and g["effective_fence"]["type"] == "wire"
    assert g["edits"]["group_fence"] == {"type": "wire"}
    assert tools.find_parcels(near={"parcel": "P-0001", "radius_m": 1})["parcels"][0]["has_edits"]


# --- fence and trees ----------------------------------------------------------------------


def test_set_fence_semantics(env):
    tools, settings, _ = env
    out = tools.set_fence("P-0001", type="brick_wall", colour="#112233")
    assert out["fence"] == {"colour": "#112233", "type": "brick_wall"}  # implies custom, no mode key
    assert tools.get_parcel("P-0001")["effective_fence"]["mode"] == "custom"
    canonical(settings)
    # merge into the existing entry
    out = tools.set_fence("P-0001", height_m=2)
    assert out["fence"] == {"colour": "#112233", "type": "brick_wall", "height_m": 2}
    # mode none clears custom keys
    assert tools.set_fence("P-0001", mode="none")["fence"] == {"mode": "none"}
    # custom keys after none imply custom again
    assert tools.set_fence("P-0001", height_m=1.1)["fence"] == {"height_m": 1.1}
    # gates: front shortcut
    out = tools.set_fence("P-0001", mode="custom", gaps=[{"at": "front", "width_m": 4}])
    assert out["fence"]["gaps"] == [{"at": [OX + 67.5, OY + 100.0], "width_m": 4.0}]
    canonical(settings)
    assert tools.reset_fence("P-0001")["changed"] is True
    assert load_edits(settings.edits_path).parcels == {}  # empty entry dropped
    assert tools.reset_fence("P-0001")["changed"] is False


def test_set_fence_errors(env):
    tools, settings, _ = env
    for kwargs, msg in [
        ({"mode": "auto", "height_m": 1}, "cannot be combined"),
        ({"mode": "none", "gaps": []}, "cannot be combined"),
        ({"mode": "wobbly"}, "mode must be one of"),
        ({"type": "hedge"}, "type must be one of"),
        ({"height_m": -1}, "must be > 0"),
        ({"colour": "red"}, "#rrggbb"),
        ({}, "nothing to set"),
        ({"gaps": "front"}, "list of"),
        ({"gaps": [{"at": [1], "width_m": 2}]}, "at must be"),
    ]:
        with pytest.raises(ToolError, match=msg):
            tools.set_fence("P-0001", **kwargs)
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.set_fence("P-4444", mode="none")
    with pytest.raises(ToolError, match="neither a parcel ID"):
        tools.set_fence("house", mode="none")
    with pytest.raises(ToolError, match="does not exist"):
        tools.set_fence("G-0009", mode="none")
    assert not settings.edits_path.exists()  # nothing was written on errors


def test_group_fence_and_member_override_warning(tools, env):
    gid = tools.create_group(["P-0001", "P-0002"])["id"]
    out = tools.set_fence(gid, mode="custom", type="wire")
    assert out["target"] == gid and out["warnings"] == []
    out = tools.set_fence("P-0001", mode="none")
    assert "override" in out["warnings"][0] and gid in out["warnings"][0]
    canonical(env[1])
    with pytest.raises(ToolError, match="single parcel"):
        tools.set_fence(gid, gaps=[{"at": "front", "width_m": 3}])


def test_set_trees(env):
    tools, settings, _ = env
    assert tools.set_trees("P-0001", density_per_1000m2=3)["trees"] == {"density_per_1000m2": 3}
    assert tools.set_trees("P-0001", count=5, seed_offset=2)["trees"] == {"count": 5, "seed_offset": 2}
    assert tools.set_trees("P-0001", seed_offset=3)["trees"] == {"count": 5, "seed_offset": 3}
    assert tools.set_trees("P-0001", density_per_1000m2=0)["trees"] == {
        "density_per_1000m2": 0, "seed_offset": 3,
    }
    canonical(settings)
    with pytest.raises(ToolError, match="exactly one"):
        tools.set_trees("P-0001", density_per_1000m2=1, count=2)
    with pytest.raises(ToolError, match="count must be"):
        tools.set_trees("P-0001", count=-1)
    with pytest.raises(ToolError, match=">= 0"):
        tools.set_trees("P-0001", density_per_1000m2=-1)
    with pytest.raises(ToolError, match="nothing to set"):
        tools.set_trees("P-0001")
    with pytest.raises(ToolError, match="seed_offset alone"):
        tools.set_trees("P-0002", seed_offset=1)
    assert tools.reset_trees("P-0001")["changed"] is True
    assert load_edits(settings.edits_path).parcels == {}


# --- objects ------------------------------------------------------------------------------


def test_place_object_anchors(tools, env):
    settings = env[1]
    got = {}
    for anchor in ("front", "back", "left", "right", "centre", "near_building"):
        out = tools.place_object("P-0001", CAR, anchor=anchor)
        got[anchor] = local(out["position"])
        assert out["rotation_deg"] == 180.0  # car front toward the road (north)
    assert got["front"] == (67.5, 98.5)
    assert got["back"] == (67.5, 41.5)
    # standing on the road edge looking south into the plot: left is east, right is west
    assert got["left"][0] > 90 and got["right"][0] < 45
    assert got["left"][1] == got["right"][1] == 70.0
    # between house (top y=70) and road edge: 2 m beyond the footprint
    assert got["near_building"] == (67.5, 72.0)
    assert got["centre"][1] == pytest.approx(70.0, abs=0.01)
    ids = [o["id"] for o in load_edits(settings.edits_path).parcels["P-0001"]["objects"]]
    assert ids == [f"o-000{i}" for i in range(1, 7)]
    canonical(settings)


def test_place_object_offsets_rotation_and_fields(tools, env):
    out = tools.place_object(
        "P-0001", "prim:slab", anchor="front", offset_m={"along": 5, "inward": 3},
        rotation_deg=-90, scale=2, colour="#aa0000",
    )
    assert local(out["position"]) == (62.5, 95.5)  # along > 0 = to the right = west
    assert out["rotation_deg"] == 270.0 and out["scale"] == 2.0
    assert out["entry"] == {
        "id": "o-0001", "asset": "prim:slab", "position": out["position"],
        "rotation_deg": 270.0, "scale": 2.0, "colour": "#aa0000",
    }
    assert "warnings" in out and not any("outside" in w for w in out["warnings"])
    canonical(env[1])


def test_place_object_facing_follows_the_road_side(tools):
    # P-0002 (garden) also has the road to its north; P-0004 (field) has it to its south
    assert tools.place_object("P-0002", "prim:slab")["rotation_deg"] == 180.0
    out = tools.place_object("P-0004", "prim:slab", anchor="front")
    assert out["rotation_deg"] == 0.0 and local(out["position"])[1] == pytest.approx(116.5)


def test_place_object_exact_position_and_warnings(tools):
    out = tools.place_object("P-0001", CAR, position=[OX + 50, OY + 50])
    assert out["position"] == [OX + 50, OY + 50] and out["warnings"] == []
    # overlap with the placed object
    out = tools.place_object("P-0001", CAR, position=[OX + 51, OY + 50.5])
    assert any("overlaps placed object o-0001" in w for w in out["warnings"])
    # inside the house
    out = tools.place_object("P-0001", CAR, position=[OX + 67, OY + 62])
    assert any("overlaps building w1" in w for w in out["warnings"])
    # outside the parcel: applied with a warning
    out = tools.place_object("P-0001", CAR, position=[OX + 150, OY + 150])
    assert any("outside P-0001" in w for w in out["warnings"])
    assert load_edits(tools.settings.edits_path).parcels["P-0001"]["objects"][-1]["position"] == [
        OX + 150, OY + 150,
    ]
    # inside another parcel is said so
    out = tools.place_object("P-0001", CAR, position=[OX + 120, OY + 60])
    assert any("inside P-0002" in w for w in out["warnings"])
    # small pack model at scale 1
    out = tools.place_object("P-0001", "pack:kenney-city-kit-suburban/tree-large", position=[OX + 80, OY + 50])
    assert any("small at scale 1" in w for w in out["warnings"])
    canonical(tools.settings)


def test_place_object_anchor_outside_is_moved_inside(tools):
    out = tools.place_object("P-0001", CAR, anchor="front", offset_m={"inward": -30})
    x, y = local(out["position"])
    assert 40 <= x <= 95 and 40 <= y <= 100
    assert any("moved" in w and "nearest interior point" in w for w in out["warnings"])


def test_place_object_errors(env):
    tools, settings, _ = env
    for kwargs, msg in [
        ({"parcel": "P-0001", "asset": "pack:nope/car"}, "unknown pack model"),
        ({"parcel": "P-0001", "asset": "car"}, 'must start with "pack:"'),
        ({"parcel": "P-0001", "asset": "prim:teapot"}, "unknown primitive"),
        ({"parcel": "P-9999", "asset": CAR}, "unknown parcel"),
        ({"parcel": "P-0001", "asset": CAR, "anchor": "above"}, "anchor must be one of"),
        ({"parcel": "P-0001", "asset": CAR, "scale": 0}, "scale must be > 0"),
        ({"parcel": "P-0001", "asset": CAR, "rotation_deg": "x"}, "rotation_deg must be a number"),
        ({"parcel": "P-0001", "asset": CAR, "colour": "blue"}, "#rrggbb"),
        ({"parcel": "P-0001", "asset": CAR, "position": [1]}, "position must be"),
        ({"parcel": "P-0001", "asset": CAR, "position": [OX, OY], "anchor": "front"}, "not both"),
        ({"parcel": "P-0001", "asset": CAR, "offset_m": {"sideways": 1}}, "offset_m must be"),
    ]:
        with pytest.raises(ToolError, match=msg):
            tools.place_object(**kwargs)
    assert not settings.edits_path.exists()


def test_remove_and_move_object(tools, env):
    settings = env[1]
    a = tools.place_object("P-0001", CAR, anchor="front")["id"]
    b = tools.place_object("P-0001", CAR, anchor="back")["id"]
    out = tools.move_object(a, anchor="left", offset_m={"inward": 1})
    assert out["entry"]["rotation_deg"] == 180.0 and out["parcel"] == "P-0001"
    assert local(out["entry"]["position"])[0] > 90
    out = tools.move_object(a, rotation_deg=45, scale=1.5)
    assert out["entry"]["rotation_deg"] == 45.0 and out["entry"]["scale"] == 1.5
    assert out["entry"]["position"] == tools.get_parcel("P-0001")["edits"]["objects"][0]["position"]
    out = tools.move_object(a, parcel="P-0002", anchor="centre")  # re-listed under another parcel
    assert out["parcel"] == "P-0002"
    parcels = load_edits(settings.edits_path).parcels
    assert [o["id"] for o in parcels["P-0001"]["objects"]] == [b]
    assert [o["id"] for o in parcels["P-0002"]["objects"]] == [a]
    canonical(settings)
    with pytest.raises(ToolError, match="nothing to change"):
        tools.move_object(a)
    with pytest.raises(ToolError, match="no placed object"):
        tools.move_object("o-0099", rotation_deg=1)
    tools.remove_object(a)
    tools.remove_object(b)
    assert load_edits(settings.edits_path).parcels == {}  # entries dropped when empty
    with pytest.raises(ToolError, match="no placed object 'o-0001'"):
        tools.remove_object("o-0001")
    # ids keep growing past the highest existing one
    assert tools.place_object("P-0001", CAR)["id"] == "o-0001"


def test_object_ids_continue_after_foreign_ids(tools, env):
    settings = env[1]
    settings.edits_path.write_text(
        json.dumps({"version": 1, "parcels": {"P-0003": {"objects": [
            {"id": "o-0007", "asset": "prim:box", "position": [1, 2]},
            {"id": "bench", "asset": "prim:box", "position": [1, 2]},
        ]}}, "groups": {}})
    )
    assert tools.place_object("P-0001", CAR)["id"] == "o-0008"


# --- groups -------------------------------------------------------------------------------


def test_groups(tools, env):
    settings = env[1]
    out = tools.create_group(["P-0002", "P-0001"], fence={"mode": "custom", "height_m": 1.5})
    assert out["id"] == "G-0001"
    assert out["entry"] == {"fence": {"height_m": 1.5, "mode": "custom"}, "parcels": ["P-0001", "P-0002"]}
    assert out["warnings"] == []
    assert tools.create_group(["P-0004", "P-0005"])["id"] == "G-0002"
    assert "not all touching" in tools.create_group(["P-0003", "P-0006"])["warnings"][0]
    canonical(settings)
    with pytest.raises(ToolError, match="already in a group: P-0001 \\(G-0001\\)"):
        tools.create_group(["P-0001", "P-0003"])
    with pytest.raises(ToolError, match="2 or more"):
        tools.create_group(["P-0001"])
    with pytest.raises(ToolError, match="2 or more different"):
        tools.create_group(["P-0003", "P-0003"])
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.create_group(["P-0001", "P-9000"])
    with pytest.raises(ToolError, match="cannot be combined"):
        tools.create_group(["P-0003", "P-0006"], fence={"mode": "auto", "height_m": 1})
    out = tools.dissolve_group("G-0001")
    assert out["summary"].startswith("dissolved G-0001")
    assert "G-0001" not in load_edits(settings.edits_path).groups
    assert tools.create_group(["P-0001", "P-0002"])["id"] == "G-0004"  # max + 1, ids never reused
    with pytest.raises(ToolError, match="does not exist"):
        tools.dissolve_group("G-0001")
    with pytest.raises(ToolError, match="not a group ID"):
        tools.dissolve_group("P-0001")


def test_merge_groups(tools, env):
    settings = env[1]
    a = tools.create_group(["P-0001", "P-0002"], fence={"mode": "custom", "type": "wire"})["id"]
    b = tools.create_group(["P-0004", "P-0005"])["id"]
    c = tools.create_group(["P-0003", "P-0006"], fence={"mode": "custom", "height_m": 2.0})["id"]
    tools.set_fence("P-0001", mode="custom", colour="#112233")  # parcel override stays put
    out = tools.merge_groups([a, b])
    assert out["id"] == "G-0004"
    assert out["entry"] == {"fence": {"mode": "custom", "type": "wire"},
                            "parcels": ["P-0001", "P-0002", "P-0004", "P-0005"]}
    edits = load_edits(settings.edits_path)
    assert sorted(edits.groups) == [c, "G-0004"]
    assert edits.parcels["P-0001"]["fence"]["colour"] == "#112233"
    canonical(settings)
    out = tools.merge_groups(["G-0004", c], fence={"mode": "none"})
    last = out["id"]
    assert out["entry"]["fence"] == {"mode": "none"}
    assert load_edits(settings.edits_path).groups[out["id"]]["parcels"] == [
        "P-0001", "P-0002", "P-0003", "P-0004", "P-0005", "P-0006"]
    with pytest.raises(ToolError, match="2 or more"):
        tools.merge_groups([last])
    with pytest.raises(ToolError, match="2 or more different"):
        tools.merge_groups([last, last])
    with pytest.raises(ToolError, match="no such group: G-0099"):
        tools.merge_groups([last, "G-0099"])
    with pytest.raises(ToolError, match="not a group ID"):
        tools.merge_groups([last, "P-0001"])


def test_merge_group_with_loose_parcels(tools, env):
    a = tools.create_group(["P-0001", "P-0002"], fence={"mode": "custom", "type": "wire"})["id"]
    other = tools.create_group(["P-0004", "P-0005"])["id"]
    out = tools.merge_groups([a], parcels=["P-0003"])
    assert out["entry"]["parcels"] == ["P-0001", "P-0002", "P-0003"]
    assert out["entry"]["fence"] == {"mode": "custom", "type": "wire"}
    assert a not in load_edits(env[1].edits_path).groups
    with pytest.raises(ToolError, match="already in another group: P-0004"):
        tools.merge_groups([out["id"]], parcels=["P-0004"])
    with pytest.raises(ToolError, match="2 or more"):
        tools.merge_groups([out["id"]])
    assert other in load_edits(env[1].edits_path).groups


def test_merge_groups_fence_conflict_warns(tools):
    a = tools.create_group(["P-0001", "P-0002"], fence={"mode": "custom", "type": "wire"})["id"]
    b = tools.create_group(["P-0004", "P-0005"], fence={"mode": "custom", "height_m": 2.0})["id"]
    out = tools.merge_groups([a, b])
    assert out["entry"]["fence"] == {"mode": "custom", "type": "wire"}
    assert "different fences" in out["warnings"][0]


# --- file handling ------------------------------------------------------------------------


def test_invalid_existing_file_is_not_overwritten(env):
    tools, settings, _ = env
    settings.edits_path.write_text('{"version": 1, "parcels": {"P-0001": {"fence": {"mode": "x"}}}}')
    before = settings.edits_path.read_text()
    with pytest.raises(ToolError, match="currently invalid"):
        tools.set_trees("P-0002", count=1)
    settings.edits_path.write_text("not json")
    with pytest.raises(ToolError, match="not valid JSON"):
        tools.set_trees("P-0002", count=1)
    assert settings.edits_path.read_text() == "not json" and before


def test_other_content_is_preserved_and_reloaded(tools, env):
    settings = env[1]
    tools.set_trees("P-0001", count=2)
    # another writer changes the file between two calls: it must be kept
    doc = json.loads(settings.edits_path.read_text())
    doc["parcels"]["P-0099"] = {"fence": {"mode": "none"}}  # unknown parcel: allowed, kept
    settings.edits_path.write_text(json.dumps(doc))
    tools.set_fence("P-0002", mode="none")
    parcels = load_edits(settings.edits_path).parcels
    assert parcels["P-0099"] == {"fence": {"mode": "none"}} and parcels["P-0001"]["trees"] == {"count": 2}


def test_concurrent_writers_do_not_lose_updates(tools, env):
    settings = env[1]
    errors: list[Exception] = []

    def work(parcel: str) -> None:
        try:
            for _ in range(8):
                tools.place_object(parcel, "prim:slab", anchor="centre")
        except Exception as exc:  # noqa: BLE001  # pragma: no cover - reported below
            errors.append(exc)

    threads = [threading.Thread(target=work, args=(p,)) for p in ("P-0001", "P-0002", "P-0004", "P-0005")]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert not errors
    edits = load_edits(settings.edits_path)
    ids = [o["id"] for e in edits.parcels.values() for o in e["objects"]]
    assert len(ids) == 32 and len(set(ids)) == 32
    canonical(settings)
    assert not list(settings.edits_path.parent.glob("edits.json.*.tmp"))


# --- regions ------------------------------------------------------------------------------

SQUARE = [[OX + 10, OY + 10], [OX + 20, OY + 10], [OX + 20, OY + 20], [OX + 10, OY + 20]]


def test_region_create_list_colour_delete(env):
    tools, settings, _ = env
    assert tools.list_regions() == {"count": 0, "total_matching": 0, "regions": []}
    out = tools.create_region("#8A7A5C", polygon=SQUARE)
    assert out["id"] == "R-0001" and out["area_m2"] == 100.0 and out["points"] == 4
    assert out["warnings"] == [] and out["polygon"] == SQUARE
    canonical(settings)
    assert load_edits(settings.edits_path).regions["R-0001"] == {"colour": "#8a7a5c", "polygon": SQUARE}
    second = tools.create_region("#112233", polygon=[*SQUARE[:3], SQUARE[0]])  # closing point is dropped
    assert second["id"] == "R-0002" and second["points"] == 3
    rows = tools.list_regions()["regions"]
    assert [r["id"] for r in rows] == ["R-0001", "R-0002"]
    assert rows[0]["bbox"] == [OX + 10, OY + 10, OX + 20, OY + 20] and rows[0]["centroid"] == [OX + 15, OY + 15]
    tools.set_region_colour("R-0001", "#ffffff")
    assert tools.list_regions()["regions"][0]["colour"] == "#ffffff"
    tools.delete_region("R-0001")
    canonical(settings)
    assert tools.create_region("#000000", polygon=SQUARE)["id"] == "R-0003"  # max + 1, no reuse of 0001
    tools.delete_region("R-0003")
    tools.delete_region("R-0002")
    assert "regions" not in json.loads(settings.edits_path.read_text())
    canonical(settings)


def test_region_parcel_relative_points(env):
    tools, settings, _ = env
    # P-0001's front edge is y=100 (road to the north): along grows west, inward grows south
    out = tools.create_region("#00ff00", parcel="P-0001", points=[[0, 1], [-5, 1], {"along": -5, "inward": 6}, [0, 6]])
    assert out["polygon"] == [
        [OX + 67.5, OY + 99.0], [OX + 72.5, OY + 99.0], [OX + 72.5, OY + 94.0], [OX + 67.5, OY + 94.0]
    ]
    assert out["area_m2"] == 25.0
    canonical(settings)
    near = tools.list_regions(near={"parcel": "P-0001", "radius_m": 1})
    assert [r["id"] for r in near["regions"]] == ["R-0001"] and near["regions"][0]["distance_m"] == 0
    assert tools.list_regions(near={"x": OX + 500, "y": OY + 500})["regions"] == []
    assert tools.list_regions(near={"x": OX + 70, "y": OY + 95, "radius_m": 3})["count"] == 1


def test_region_move_point(env):
    tools, settings, _ = env
    tools.create_region("#123456", polygon=SQUARE)
    out = tools.move_region_point("R-0001", 2, offset={"dx": 5, "dy": -2})
    assert out["position"] == [OX + 25, OY + 18] and out["old"] == [OX + 20, OY + 20]
    tools.move_region_point("R-0001", 0, position=[OX + 5, OY + 5])
    assert load_edits(settings.edits_path).regions["R-0001"]["polygon"][0] == [OX + 5, OY + 5]
    canonical(settings)


def test_region_errors_leave_file_untouched(env):
    tools, settings, _ = env
    with pytest.raises(ToolError, match="exactly one"):
        tools.create_region("#000000")
    with pytest.raises(ToolError, match="exactly one"):
        tools.create_region("#000000", polygon=SQUARE, points=[[0, 0]])
    with pytest.raises(ToolError, match="pass parcel"):
        tools.create_region("#000000", points=[[0, 0], [1, 0], [1, 1]])
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.create_region("#000000", parcel="P-9999", points=[[0, 0], [1, 0], [1, 1]])
    with pytest.raises(ToolError, match="#rrggbb"):
        tools.create_region("red", polygon=SQUARE)
    with pytest.raises(ToolError, match="at least 3"):
        tools.create_region("#000000", polygon=SQUARE[:2])
    with pytest.raises(ToolError, match=r"polygon\[1\]"):
        tools.create_region("#000000", polygon=[[1, 2], [1], [3, 4]])
    bow = [[OX, OY], [OX + 10, OY + 10], [OX + 10, OY], [OX, OY + 10]]
    with pytest.raises(ToolError, match="nothing was written"):
        tools.create_region("#000000", polygon=bow)
    assert not settings.edits_path.exists()
    tools.create_region("#123456", polygon=SQUARE)
    before = settings.edits_path.read_bytes()
    with pytest.raises(ToolError, match="nothing was written"):
        tools.move_region_point("R-0001", 2, position=[OX + 10, OY + 10])  # collapses the square
    with pytest.raises(ToolError, match="nothing was written"):
        tools.move_region_point("R-0001", 1, position=[OX + 10, OY + 20])  # bow tie via touch
    with pytest.raises(ToolError, match="out of range"):
        tools.move_region_point("R-0001", 4, offset={"dx": 1})
    with pytest.raises(ToolError, match="exactly one"):
        tools.move_region_point("R-0001", 0)
    with pytest.raises(ToolError, match="not a region ID"):
        tools.delete_region("P-0001")
    with pytest.raises(ToolError, match="does not exist"):
        tools.delete_region("R-0009")
    with pytest.raises(ToolError, match="does not exist"):
        tools.set_region_colour("R-0009", "#000000")
    with pytest.raises(ToolError, match="#rrggbb"):
        tools.set_region_colour("R-0001", "#12")
    assert settings.edits_path.read_bytes() == before


def test_region_overlap_warning_and_coexistence(env):
    tools, settings, _ = env
    tools.place_object("P-0001", CAR)
    tools.create_region("#111111", polygon=SQUARE)
    out = tools.create_region("#222222", polygon=[[OX + 15, OY + 15], [OX + 30, OY + 15], [OX + 30, OY + 30]])
    assert out["warnings"] and "R-0002 wins" in out["warnings"][0]
    tools.create_group(["P-0001", "P-0002"])
    edits = load_edits(settings.edits_path)
    assert len(edits.regions) == 2 and edits.groups  # other writers keep the regions
    canonical(settings)


def test_object_polygon_orientation():
    # a 2 x 4 m (x, z) car at rotation 0 spans x +-1 east, z +-2 i.e. north-south
    poly = object_polygon((10, 20), [2, 1, 4], 0, 1)
    assert poly.bounds == pytest.approx((9, 18, 11, 22))
    poly = object_polygon((10, 20), [2, 1, 4], 90, 1)
    assert poly.bounds == pytest.approx((8, 19, 12, 21))


def test_parse_args(tmp_path):
    s = parse_args(["--area", "village", "--edits", str(tmp_path / "e.json"), "--state", str(tmp_path / "s.json")])
    assert s.area == "village" and s.edits_path == tmp_path / "e.json" and s.work.parts[-2:] == ("village", "work")
    d = parse_args([])
    assert d.area == "slice" and d.edits_path.name == "edits.json" and d.state_path.name == "editor-state.json"


def test_missing_map_data_error(tmp_path):
    tools = ParcelEditTools(Settings(work_dir=tmp_path / "nothing", edits_path=tmp_path / "e.json"))
    with pytest.raises(ToolError, match="work files missing"):
        tools.get_parcel("P-0001")


# --- through the MCP SDK ------------------------------------------------------------------


def test_through_mcp_session(env):
    from mcp import Client

    _, settings, _ = env
    server = build_server(settings)

    async def run():
        async with Client(server) as client:
            names = {t.name for t in (await client.list_tools()).tools}
            assert names == {
                "list_assets", "find_parcels", "get_parcel", "set_fence", "reset_fence", "set_trees",
                "reset_trees", "place_object", "remove_object", "move_object", "create_group", "merge_groups",
                "dissolve_group", "create_region", "set_region_colour", "move_region_point",
                "delete_region", "list_regions",
            }
            tool = next(t for t in (await client.list_tools()).tools if t.name == "place_object")
            assert {"parcel", "asset", "anchor", "offset_m", "position"} <= set(tool.input_schema["properties"])
            assert tool.input_schema["required"] == ["parcel", "asset"]
            ok = await client.call_tool("place_object", {"parcel": "P-0001", "asset": CAR, "anchor": "front"})
            assert not ok.is_error
            payload = ok.structured_content or json.loads(ok.content[0].text)
            assert payload["id"] == "o-0001" and payload["rotation_deg"] == 180.0
            bad = await client.call_tool("place_object", {"parcel": "P-0001", "asset": "pack:x/y"})
            assert bad.is_error and "unknown pack model" in bad.content[0].text
            assert "Traceback" not in bad.content[0].text
            wrong = await client.call_tool("place_object", {"parcel": "P-0001"})  # missing argument
            assert wrong.is_error
            sel = await client.call_tool("find_parcels", {"selected": True})
            assert sel.is_error and "editor state" in sel.content[0].text

    asyncio.run(run())
    canonical(settings)


def test_validation_doc_includes_regions(env):
    from suchohrad_pipeline.mcp_server import _edits_doc

    tools, settings, _ = env
    tools.create_region("#111111", polygon=SQUARE)
    doc = _edits_doc(load_edits(settings.edits_path))
    assert "R-0001" in doc["regions"]


def test_region_overlap_winner_is_numeric(env):
    tools, settings, _ = env
    edits = load_edits(settings.edits_path)
    edits.regions["R-9999"] = {"colour": "#111111", "polygon": SQUARE}
    edits.regions["R-10000"] = {"colour": "#222222", "polygon": SQUARE}
    assert "R-10000 wins" in type(tools)._overlap_warnings(edits, "R-9999")[0]
