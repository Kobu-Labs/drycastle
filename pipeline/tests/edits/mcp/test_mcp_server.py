"""MCP server tools on the synthetic workspace (P-0001 building plot with a house, road to its north)."""

import asyncio
import json
import threading
import time

import pytest
from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import Polygon

from drycastle_pipeline.edits.mcp.objects import object_polygon
from drycastle_pipeline.edits.mcp.server import build_server, parse_args
from drycastle_pipeline.edits.mcp.settings import Settings
from drycastle_pipeline.edits.mcp.tools import ParcelEditTools
from drycastle_pipeline.edits.schema import dump_text, load_edits, validate
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
        sketch_path=tmp_path / "sketch.json",
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
    assert [p["id"] for p in tools.find_parcels(kind="garden")["parcels"]] == ["P-0002"]
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


def test_get_sketch(env):
    tools, settings, _ = env
    with pytest.raises(ToolError, match="no editor state file"):
        tools.get_sketch()
    state = {"format_version": 1, "selected": [], "camera": {"x": 1, "y": 2, "z": 3, "yaw_deg": 4}}
    settings.state_path.write_text(json.dumps(state))
    with pytest.raises(ToolError, match="no sketch file at"):
        tools.get_sketch()
    empty = {"lines": [], "next_line": 0, "next_point": 0, "points": []}
    settings.state_path.write_text(json.dumps({**state, "sketch": empty}))
    with pytest.raises(ToolError, match="nothing is sketched"):
        tools.get_sketch()

    def absolute(pts):
        return [[OX + x, OY + y] for x, y in pts]

    # as the viewer writes it: A on P-0001, B off the map, L1 across P-0001 and P-0002, the area L2 on their border
    sketch = {
        "lines": [
            {"closed": False, "id": "L1", "points": absolute([(50, 60), (120, 60)])},
            {"closed": True, "id": "L2", "points": absolute([(90, 50), (100, 50), (100, 60), (90, 60)])},
        ],
        "next_line": 2,
        "next_point": 2,
        "points": [{"id": "A", "x": OX + 50, "y": OY + 50}, {"id": "B", "x": OX + 300, "y": OY + 300}],
    }
    settings.state_path.write_text(json.dumps({**state, "sketch": sketch}))
    out = tools.get_sketch()
    assert out["count"] == 4 and "warnings" not in out
    a, b = out["points"]
    assert a == {"id": "A", "sketch": "DEFAULT", "position": [OX + 50, OY + 50], "parcel": "P-0001", "kind": "building_plot"}
    assert b["parcel"] is None and b["kind"] is None
    l1, l2 = out["lines"]
    assert l1["closed"] is False and l1["length_m"] == 70.0 and "area_m2" not in l1
    assert l1["points"] == absolute([(50, 60), (120, 60)])
    assert l1["bbox"] == [OX + 50, OY + 60, OX + 120, OY + 60]
    assert l1["parcels"] == [
        {"id": "P-0001", "kind": "building_plot", "length_m": 45.0},
        {"id": "P-0002", "kind": "garden", "length_m": 25.0},
    ]
    assert l2["closed"] is True and l2["area_m2"] == 100.0 and l2["length_m"] == 40.0
    assert l2["centroid"] == [OX + 95, OY + 55]
    assert l2["parcels"] == [
        {"id": "P-0001", "kind": "building_plot", "area_m2": 50.0, "share_of_parcel": 0.015},
        {"id": "P-0002", "kind": "garden", "area_m2": 50.0, "share_of_parcel": 0.019},
    ]
    # one mark by name
    one = tools.get_sketch("L2")
    assert one["count"] == 1 and one["points"] == [] and one["lines"][0]["id"] == "L2"
    with pytest.raises(ToolError, match="has no 'L9'; marks: A, B, L1, L2; sketches: DEFAULT"):
        tools.get_sketch("L9")
    # a mark off the map alone: answered, with a warning about the parcels
    assert "no mark lies on a parcel" in tools.get_sketch("B")["warnings"][0]
    # a self-crossing area is returned without parcels, and said so
    bow = {"closed": True, "id": "L1", "points": absolute([(50, 50), (60, 60), (60, 50), (50, 60)])}
    settings.state_path.write_text(json.dumps({**state, "sketch": {**sketch, "lines": [bow]}}))
    crossed = tools.get_sketch("L1")
    assert crossed["lines"][0]["parcels"] == [] and "crosses itself" in crossed["warnings"][0]
    # stale and broken files
    old = time.time() - 3600
    import os

    os.utime(settings.state_path, (old, old))
    assert "this sketch could be outdated" in tools.get_sketch("A")["warnings"][0]
    settings.state_path.write_text(json.dumps({**state, "sketch": {"points": [{"id": "A"}]}}))
    with pytest.raises(ToolError, match="unreadable"):
        tools.get_sketch()
    # the selection still reads a state file that carries a sketch
    settings.state_path.write_text(json.dumps({**state, "selected": ["P-0001"], "sketch": sketch}))
    assert [p["id"] for p in tools.find_parcels(selected=True)["parcels"]] == ["P-0001"]


def test_sketch_tools(env):
    tools, settings, tmp = env
    path = settings.sketch_file
    assert path == tmp / "sketch.json"
    # the first mark makes the sketch; with one sketch the name may be left out
    assert tools.sketch_add_point(OX + 50, OY + 50, "ROAD") == {"id": "A", "sketch": "ROAD", "position": [OX + 50, OY + 50]}
    assert tools.sketch_add_point(OX + 60, OY + 50)["sketch"] == "ROAD"
    line = tools.sketch_add_line([[OX + 50, OY + 60], [OX + 120, OY + 60]], sketch="road")  # names match ignoring case
    assert line == {"id": "L1", "sketch": "ROAD", "closed": False, "corners": 2}
    tools.sketch_add_line([[OX + 90, OY + 50], [OX + 100, OY + 50], [OX + 100, OY + 60]], closed=True, sketch="PLAN")
    with pytest.raises(ToolError, match="name the sketch: there are several"):
        tools.sketch_add_point(OX + 1, OY + 1)
    with pytest.raises(ToolError, match="must not cross itself"):
        tools.sketch_add_line([[OX, OY], [OX + 10, OY + 10], [OX + 10, OY], [OX, OY + 10]], closed=True, sketch="PLAN")
    with pytest.raises(ToolError, match="not a sketch name"):
        tools.sketch_add_point(1, 1, "has space")
    doc = json.loads(path.read_text())
    assert doc["version"] == 4 and doc["sketches"] == ["ROAD", "PLAN"] and doc["format_version"] == 1
    # reading: marks carry their sketch, a sketch name returns its marks
    out = tools.get_sketch()
    assert [s["name"] for s in out["sketches"]] == ["ROAD", "PLAN"] and out["count"] == 4
    assert out["sketches"][0] == {"name": "ROAD", "points": 2, "lines": 1}
    road = tools.get_sketch("ROAD")
    assert [p["id"] for p in road["points"]] == ["A", "B"] and [ln["id"] for ln in road["lines"]] == ["L1"]
    assert tools.get_sketch("L2")["lines"][0]["sketch"] == "PLAN"
    # removing a mark, then a whole sketch; ids start again once nothing is left
    assert tools.sketch_remove("B") == {"removed": "B"}
    assert tools.sketch_remove("plan") == {"removed_sketch": "PLAN", "removed_marks": ["L2"]}
    with pytest.raises(ToolError, match="no mark or sketch"):
        tools.sketch_remove("PLAN")
    tools.sketch_remove("A")
    tools.sketch_remove("L1")
    assert tools.create_sketch("EMPTY") == {"sketch": "EMPTY"}
    with pytest.raises(ToolError, match="exists already"):
        tools.create_sketch("empty")
    assert tools.get_sketch()["count"] == 0
    assert tools.sketch_add_point(OX + 5, OY + 5, "EMPTY")["id"] == "A"


def test_sketch_add_road(env):
    tools, settings, _ = env
    graph = json.loads((settings.work / "roads.json").read_text())
    edge = graph["edges"][0]
    out = tools.sketch_add_road(road=edge["id"], sketch="ROAD")
    assert out["sketch"] == "ROAD" and out["road"]["id"] == edge["id"] and out["lines"]
    line = tools.get_sketch(out["lines"][0])["lines"][0]
    assert line["points"][0] == [OX + edge["polyline"][0][0], OY + edge["polyline"][0][1]]
    # the way an edge belongs to, by OSM id; a spot near a road; a sketch point as the spot
    way = tools.sketch_add_road(road=edge["osm_id"], sketch="ROAD")  # w10: two edges, chained into one line
    assert len(way["lines"]) == 1 and way["length_m"] == 180.0
    assert len(tools.get_sketch(way["lines"][0])["lines"][0]["points"]) == 3
    mid = [60, 106]
    near = tools.sketch_add_road(near=[OX + mid[0], OY + mid[1] + 1], sketch="NEAR")
    assert near["road"]["id"] == edge["id"]
    pid = tools.sketch_add_point(OX + mid[0], OY + mid[1], "NEAR")["id"]
    assert tools.sketch_add_road(near=pid, sketch="NEAR")["road"]["id"] == edge["id"]
    with pytest.raises(ToolError, match="exactly one of"):
        tools.sketch_add_road(sketch="ROAD")
    with pytest.raises(ToolError, match="no road 'E-9999'"):
        tools.sketch_add_road(road="E-9999", sketch="ROAD")
    with pytest.raises(ToolError, match="within 30 m"):
        tools.sketch_add_road(near=[OX + 5000, OY + 5000], sketch="ROAD")


def test_sketch_migrates_legacy_state_sketch(env):
    tools, settings, _ = env
    legacy = {"format_version": 1, "selected": [], "sketch": {
        "lines": [], "next_line": 0, "next_point": 1, "points": [{"id": "A", "x": OX + 50, "y": OY + 50}]}}
    settings.state_path.write_text(json.dumps(legacy))
    assert tools.get_sketch()["points"][0]["sketch"] == "DEFAULT"
    assert tools.sketch_add_point(OX + 1, OY + 1)["id"] == "B"  # joins DEFAULT, continues the names
    assert json.loads(settings.sketch_file.read_text())["sketches"] == ["DEFAULT"]


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
    assert "trees" not in p["rule_defaults"]
    assert p["rule_defaults"]["fence"]["kind_gets_fence"] is True
    assert p["edits"] == {"fence": None, "objects": [], "group_fence": None}
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


# --- fence --------------------------------------------------------------------------------


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


# --- file handling ------------------------------------------------------------------------


def test_invalid_existing_file_is_not_overwritten(env):
    tools, settings, _ = env
    settings.edits_path.write_text('{"version": 1, "parcels": {"P-0001": {"fence": {"mode": "x"}}}}')
    before = settings.edits_path.read_text()
    with pytest.raises(ToolError, match="currently invalid"):
        tools.set_fence("P-0002", mode="none")
    settings.edits_path.write_text("not json")
    with pytest.raises(ToolError, match="not valid JSON"):
        tools.set_fence("P-0002", mode="none")
    assert settings.edits_path.read_text() == "not json" and before


def test_other_content_is_preserved_and_reloaded(tools, env):
    settings = env[1]
    tools.set_fence("P-0001", mode="none")
    # another writer changes the file between two calls: it must be kept
    doc = json.loads(settings.edits_path.read_text())
    doc["parcels"]["P-0099"] = {"fence": {"mode": "none"}}  # unknown parcel: allowed, kept
    settings.edits_path.write_text(json.dumps(doc))
    tools.set_fence("P-0002", mode="none")
    parcels = load_edits(settings.edits_path).parcels
    assert parcels["P-0099"] == {"fence": {"mode": "none"}} and parcels["P-0001"] == {"fence": {"mode": "none"}}


def test_concurrent_writers_do_not_lose_updates(tools, env):
    settings = env[1]
    errors: list[Exception] = []

    def work(parcel: str) -> None:
        try:
            for _ in range(8):
                tools.place_object("prim:slab", parcel, anchor="centre")
        except Exception as exc:  # noqa: BLE001  # pragma: no cover - reported below
            errors.append(exc)

    threads = [threading.Thread(target=work, args=(p,)) for p in ("P-0001", "P-0002", "P-0004", "P-0005")]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert not errors
    edits = load_edits(settings.edits_path)
    ids = [o["id"] for o in edits.objects]
    assert len(ids) == 32 and len(set(ids)) == 32
    canonical(settings)
    assert not list(settings.edits_path.parent.glob("edits.json.*.tmp"))


# --- ground patches and zones -------------------------------------------------------------

SQUARE = [[OX + 10, OY + 10], [OX + 20, OY + 10], [OX + 20, OY + 20], [OX + 10, OY + 20]]


def test_patch_create_list_retype_delete(env):
    tools, settings, _ = env
    assert tools.list_ground_patches() == {"count": 0, "total_matching": 0, "ground_patches": []}
    out = tools.create_ground_patch("meadow", polygon=SQUARE)
    assert out["id"] == "GP-0001" and out["area_m2"] == 100.0 and out["points"] == 4 and out["type"] == "meadow"
    assert out["warnings"] == [] and out["polygon"] == SQUARE
    canonical(settings)
    assert load_edits(settings.edits_path).ground_patches["GP-0001"] == {"type": "meadow", "polygon": SQUARE}
    second = tools.create_ground_patch("forest", polygon=[*SQUARE[:3], SQUARE[0]])  # closing point is dropped
    assert second["id"] == "GP-0002" and second["points"] == 3
    rows = tools.list_ground_patches()["ground_patches"]
    assert [r["id"] for r in rows] == ["GP-0001", "GP-0002"]
    assert rows[0]["bbox"] == [OX + 10, OY + 10, OX + 20, OY + 20] and rows[0]["centroid"] == [OX + 15, OY + 15]
    tools.set_patch_type("GP-0001", "orchard")
    assert tools.list_ground_patches()["ground_patches"][0]["type"] == "orchard"
    tools.delete_ground_patch("GP-0001")
    canonical(settings)
    assert tools.create_ground_patch("field", polygon=SQUARE)["id"] == "GP-0003"  # max + 1, no reuse of 0001
    tools.delete_ground_patch("GP-0003")
    tools.delete_ground_patch("GP-0002")
    assert "ground_patches" not in json.loads(settings.edits_path.read_text())
    canonical(settings)


def test_patch_parcel_relative_points(env):
    tools, settings, _ = env
    # P-0001's front edge is y=100 (road to the north): along grows west, inward grows south
    out = tools.create_ground_patch("meadow", parcel="P-0001", points=[[0, 1], [-5, 1], {"along": -5, "inward": 6}, [0, 6]])
    assert out["polygon"] == [
        [OX + 67.5, OY + 99.0], [OX + 72.5, OY + 99.0], [OX + 72.5, OY + 94.0], [OX + 67.5, OY + 94.0]
    ]
    assert out["area_m2"] == 25.0
    canonical(settings)
    near = tools.list_ground_patches(near={"parcel": "P-0001", "radius_m": 1})
    assert [r["id"] for r in near["ground_patches"]] == ["GP-0001"] and near["ground_patches"][0]["distance_m"] == 0
    assert tools.list_ground_patches(near={"x": OX + 500, "y": OY + 500})["ground_patches"] == []
    assert tools.list_ground_patches(near={"x": OX + 70, "y": OY + 95, "radius_m": 3})["count"] == 1


def test_patch_move_corner(env):
    tools, settings, _ = env
    tools.create_ground_patch("meadow", polygon=SQUARE)
    out = tools.move_patch_corner("GP-0001", 2, offset={"dx": 5, "dy": -2})
    assert out["position"] == [OX + 25, OY + 18] and out["old"] == [OX + 20, OY + 20]
    tools.move_patch_corner("GP-0001", 0, position=[OX + 5, OY + 5])
    assert load_edits(settings.edits_path).ground_patches["GP-0001"]["polygon"][0] == [OX + 5, OY + 5]
    canonical(settings)


def test_patch_errors_leave_file_untouched(env):
    tools, settings, _ = env
    with pytest.raises(ToolError, match="exactly one"):
        tools.create_ground_patch("meadow")
    with pytest.raises(ToolError, match="exactly one"):
        tools.create_ground_patch("meadow", polygon=SQUARE, points=[[0, 0]])
    with pytest.raises(ToolError, match="pass parcel"):
        tools.create_ground_patch("meadow", points=[[0, 0], [1, 0], [1, 1]])
    with pytest.raises(ToolError, match="unknown parcel"):
        tools.create_ground_patch("meadow", parcel="P-9999", points=[[0, 0], [1, 0], [1, 1]])
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.create_ground_patch("swamp", polygon=SQUARE)
    with pytest.raises(ToolError, match="at least 3"):
        tools.create_ground_patch("meadow", polygon=SQUARE[:2])
    with pytest.raises(ToolError, match=r"polygon\[1\]"):
        tools.create_ground_patch("meadow", polygon=[[1, 2], [1], [3, 4]])
    bow = [[OX, OY], [OX + 10, OY + 10], [OX + 10, OY], [OX, OY + 10]]
    with pytest.raises(ToolError, match="nothing was written"):
        tools.create_ground_patch("meadow", polygon=bow)
    with pytest.raises(ToolError, match="water type"):
        tools.create_ground_patch("water", polygon=SQUARE)  # a water type needs its level
    with pytest.raises(ToolError, match="only goes with a water type"):
        tools.create_ground_patch("meadow", polygon=SQUARE, water_y=100.0)
    assert not settings.edits_path.exists()
    tools.create_ground_patch("meadow", polygon=SQUARE)
    before = settings.edits_path.read_bytes()
    with pytest.raises(ToolError, match="nothing was written"):
        tools.move_patch_corner("GP-0001", 2, position=[OX + 10, OY + 10])  # collapses the patch
    with pytest.raises(ToolError, match="nothing was written"):
        tools.move_patch_corner("GP-0001", 1, position=[OX + 10, OY + 20])  # bow tie via touch
    with pytest.raises(ToolError, match="out of range"):
        tools.move_patch_corner("GP-0001", 4, offset={"dx": 1})
    with pytest.raises(ToolError, match="exactly one"):
        tools.move_patch_corner("GP-0001", 0)
    with pytest.raises(ToolError, match="not a ground patch ID"):
        tools.delete_ground_patch("P-0001")
    with pytest.raises(ToolError, match="not a ground patch ID"):
        tools.delete_ground_patch("R-0001")
    with pytest.raises(ToolError, match="does not exist"):
        tools.delete_ground_patch("GP-0009")
    with pytest.raises(ToolError, match="does not exist"):
        tools.set_patch_type("GP-0009", "forest")
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.set_patch_type("GP-0001", "swamp")
    with pytest.raises(ToolError, match="water type"):
        tools.set_patch_type("GP-0001", "water")  # needs a level
    with pytest.raises(ToolError, match="not a water type"):
        tools.set_patch_water("GP-0001", 100.0)
    assert settings.edits_path.read_bytes() == before


def test_a_water_patch_has_a_level(env):
    tools, settings, _ = env
    out = tools.create_ground_patch("water", polygon=SQUARE, water_y=186.4)
    assert out["water_y"] == 186.4 and load_edits(settings.edits_path).ground_patches["GP-0001"]["water_y"] == 186.4
    tools.set_patch_water("GP-0001", 187.0)
    assert tools.list_ground_patches()["ground_patches"][0]["water_y"] == 187.0
    tools.set_patch_type("GP-0001", "meadow")  # no longer water: the level goes
    assert load_edits(settings.edits_path).ground_patches["GP-0001"] == {"type": "meadow", "polygon": SQUARE}
    tools.set_patch_type("GP-0001", "water", water_y=100.0)
    assert load_edits(settings.edits_path).ground_patches["GP-0001"]["water_y"] == 100.0
    canonical(settings)


def test_patch_overlap_warning_and_coexistence(env):
    tools, settings, _ = env
    tools.place_object(CAR, "P-0001")
    tools.create_ground_patch("meadow", polygon=SQUARE)
    out = tools.create_ground_patch("forest", polygon=[[OX + 15, OY + 15], [OX + 30, OY + 15], [OX + 30, OY + 30]])
    assert out["warnings"] and "GP-0002 wins" in out["warnings"][0]
    tools.create_group(["P-0001", "P-0002"])
    edits = load_edits(settings.edits_path)
    assert len(edits.ground_patches) == 2 and edits.groups  # other writers keep the patches
    canonical(settings)


def test_zone_create_list_kind_delete(env):
    tools, settings, _ = env
    assert tools.list_zones() == {"count": 0, "total_matching": 0, "zones": []}
    out = tools.create_zone("bigfoot", polygon=SQUARE)
    assert out["id"] == "Z-0001" and out["area_m2"] == 100.0 and out["kind"] == "bigfoot"
    canonical(settings)
    assert load_edits(settings.edits_path).zones["Z-0001"] == {"kind": "bigfoot", "polygon": SQUARE}
    tools.create_zone("deer", polygon=SQUARE)  # zones overlap freely: no warning
    assert tools.create_zone("player_spawn", parcel="P-0001", points=[[0, 1], [-5, 1], [-5, 6], [0, 6]])["id"] == "Z-0003"
    assert [r["id"] for r in tools.list_zones(kind="deer")["zones"]] == ["Z-0002"]
    assert [r["id"] for r in tools.list_zones(near={"parcel": "P-0001", "radius_m": 1})["zones"]] == ["Z-0003"]
    tools.set_zone_kind("Z-0002", "wolf")
    assert load_edits(settings.edits_path).zones["Z-0002"]["kind"] == "wolf"
    moved = tools.move_zone_corner("Z-0001", 2, offset={"dx": 5, "dy": -2})
    assert moved["position"] == [OX + 25, OY + 18]
    for zid in ("Z-0001", "Z-0002", "Z-0003"):
        tools.delete_zone(zid)
    assert "zones" not in json.loads(settings.edits_path.read_text())
    canonical(settings)


def test_zone_errors_leave_file_untouched(env):
    tools, settings, _ = env
    with pytest.raises(ToolError, match="unknown zone kind"):
        tools.create_zone("yeti", polygon=SQUARE)
    with pytest.raises(ToolError, match="exactly one"):
        tools.create_zone("bigfoot")
    with pytest.raises(ToolError, match="at least 3"):
        tools.create_zone("bigfoot", polygon=SQUARE[:2])
    bow = [[OX, OY], [OX + 10, OY + 10], [OX + 10, OY], [OX, OY + 10]]
    with pytest.raises(ToolError, match="nothing was written"):
        tools.create_zone("bigfoot", polygon=bow)
    assert not settings.edits_path.exists()
    tools.create_zone("bigfoot", polygon=SQUARE)
    before = settings.edits_path.read_bytes()
    with pytest.raises(ToolError, match="not a zone ID"):
        tools.delete_zone("GP-0001")
    with pytest.raises(ToolError, match="does not exist"):
        tools.delete_zone("Z-0009")
    with pytest.raises(ToolError, match="unknown zone kind"):
        tools.set_zone_kind("Z-0001", "yeti")
    with pytest.raises(ToolError, match="nothing was written"):
        tools.move_zone_corner("Z-0001", 2, position=[OX + 10, OY + 10])
    assert settings.edits_path.read_bytes() == before


def test_zones_by_rule_are_zones_too(env):
    from shapely.geometry import box, mapping

    tools, settings, _ = env
    parcels = settings.work_dir / "parcels.json"
    doc = json.loads(parcels.read_text())
    doc["ground"] = [{"id": "A-0001", "kind": "forest", "polygon": mapping(box(0, 0, 40, 40)), "area": 1600.0}]
    parcels.write_text(json.dumps(doc))
    assert [r["id"] for r in tools.list_zones(kind="stag")["zones"]] == ["A-0001"]
    kinds = {r["kind"] for r in tools.list_zones()["zones"] if r["id"] == "A-0001"}
    assert {"stag", "boar", "wolf"} <= kinds  # one area is a zone of every kind whose rule names forest
    assert {k["name"]: k["zones"] for k in tools.list_zone_kinds()["kinds"]}["stag"] == 1
    with pytest.raises(ToolError, match="several kinds"):
        tools.delete_zone("A-0001")
    tools.delete_zone("A-0001", kind="stag")  # a plain delete: a cut, no zone
    edits = load_edits(settings.edits_path)
    assert edits.zones == {} and edits.zone_cuts["ZC-0001"]["kind"] == "stag"
    assert tools.list_zones(kind="stag")["zones"] == [] and [r["id"] for r in tools.list_zones(kind="boar")["zones"]] == ["A-0001"]
    out = tools.set_zone_kind("A-0001", "wolf", from_kind="boar")  # retype: a drawn zone of the new kind and a cut of the old
    assert out["id"] == "Z-0001"
    edits = load_edits(settings.edits_path)
    assert edits.zones["Z-0001"]["kind"] == "wolf" and edits.zone_cuts["ZC-0002"]["kind"] == "boar"
    assert tools.list_zones(kind="boar")["zones"] == []
    tools.move_zone_corner("Z-0001", 0, offset={"dx": -3, "dy": 0})
    moved = tools.move_zone_corner("A-0001", 0, offset={"dx": 1, "dy": 0}, kind="fox")  # a rule zone moves: detached first
    assert moved["id"] == "Z-0002" and load_edits(settings.edits_path).zone_cuts["ZC-0003"]["kind"] == "fox"
    canonical(settings)


def test_list_zone_kinds_is_the_catalog_with_its_rules(env):
    from drycastle_pipeline.edits.zones import KIND_NAMES

    tools, settings, _ = env
    tools.create_zone("bigfoot", polygon=SQUARE)
    out = tools.list_zone_kinds()
    rows = {k["name"]: k for k in out["kinds"]}
    assert list(rows) == list(KIND_NAMES)
    assert rows["bigfoot"]["where_ground"] is None and rows["bigfoot"]["zones"] == 1
    assert rows["bigfoot"]["points"]["min_points"] == 30
    assert rows["stag"]["where_ground"] == ["forest"] and rows["stag"]["group"] == "wildlife"
    assert "props stage" in rows["player_spawn"]["points"]
    assert "zone_polygons.json" in rows["plantation"]["points"]


def test_object_polygon_orientation():
    # a 2 x 4 m (x, z) car at rotation 0 spans x +-1 east, z +-2 i.e. north-south
    poly = object_polygon((10, 20), [2, 1, 4], 0, 1)
    assert poly.bounds == pytest.approx((9, 18, 11, 22))
    poly = object_polygon((10, 20), [2, 1, 4], 90, 1)
    assert poly.bounds == pytest.approx((8, 19, 12, 21))


def test_object_polygon_fixed_size():
    # size_m [length, width, height]: length along x, width along map y; wins over scale and the manifest size
    poly = object_polygon((10, 20), [1.0, 0.1, 1.0], 0, 3, [1.52, 2.66, 0.12])
    assert poly.bounds == pytest.approx((10 - 0.76, 20 - 1.33, 10 + 0.76, 20 + 1.33))


def test_place_and_move_object_size_m(tools, env):
    placed = tools.place_object("prim:slab", "P-0001", anchor="centre", size_m=[1.52, 2.66, 0.12])
    assert placed["entry"]["size_m"] == [1.52, 2.66, 0.12] and "scale" not in placed["entry"]
    oid = placed["id"]
    with pytest.raises(ToolError, match="not both"):
        tools.place_object("prim:slab", "P-0001", size_m=[1, 1, 1], scale=2)
    for bad in ([1, 2], [1, 0, 1], [1, 1, "x"]):
        with pytest.raises(ToolError):
            tools.place_object("prim:slab", "P-0001", size_m=bad)
    moved = tools.move_object(oid, rotation_deg=90)
    assert moved["entry"]["size_m"] == [1.52, 2.66, 0.12]
    assert "size_m" not in tools.move_object(oid, scale=2)["entry"]
    again = tools.move_object(oid, size_m=[2, 1, 0.1])["entry"]
    assert again["size_m"] == [2, 1, 0.1] and "scale" not in again


def test_parse_args(tmp_path):
    s = parse_args(["--edits", str(tmp_path / "e.json"), "--state", str(tmp_path / "s.json")])
    assert s.edits_path == tmp_path / "e.json" and s.work.parts[-2:] == ("village", "work")
    d = parse_args([])
    assert d.edits_path.name == "edits.json" and d.state_path.name == "editor-state.json"


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
                "list_assets", "find_parcels", "get_parcel", "set_fence", "reset_fence", "set_parcel_ground_type", "set_parcel_outline", "reset_parcel_outline",
                "place_object", "remove_object", "move_object", "create_group", "merge_groups",
                "dissolve_group", "create_ground_patch", "set_patch_type", "set_patch_water", "move_patch_corner", "delete_ground_patch", "list_ground_patches",
                "create_zone", "set_zone_kind", "move_zone_corner", "delete_zone", "list_zones", "list_zone_kinds", "create_path", "list_paths", "delete_path",
                "list_ground_types", "set_ground_type", "reset_ground_type", "delete_ground_type", "connect_poles", "disconnect_poles", "list_cables", "get_sketch", "create_sketch", "sketch_add_point", "sketch_add_line", "sketch_add_road", "sketch_remove",
            }
            tool = next(t for t in (await client.list_tools()).tools if t.name == "place_object")
            assert {"parcel", "asset", "anchor", "offset_m", "position"} <= set(tool.input_schema["properties"])
            assert tool.input_schema["required"] == ["asset"]  # an object needs no parcel
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


def test_validation_doc_includes_patches_and_zones(env):
    from drycastle_pipeline.edits.store import edits_doc

    tools, settings, _ = env
    tools.create_ground_patch("meadow", polygon=SQUARE)
    tools.create_zone("deer", polygon=SQUARE)
    doc = edits_doc(load_edits(settings.edits_path))
    assert "GP-0001" in doc["ground_patches"] and "Z-0001" in doc["zones"]


def test_patch_overlap_winner_is_numeric(env):
    tools, settings, _ = env
    edits = load_edits(settings.edits_path)
    edits.ground_patches["GP-9999"] = {"type": "meadow", "polygon": SQUARE}
    edits.ground_patches["GP-10000"] = {"type": "forest", "polygon": SQUARE}
    assert "GP-10000 wins" in type(tools)._overlap_warnings(edits, "GP-9999")[0]


def test_set_fence_takes_the_textured_types_and_a_column(tools) -> None:
    out = tools.set_fence("P-0001", type="picket", column="brick_red", colour="#f2efe6")
    assert out["fence"] == {"colour": "#f2efe6", "column": "brick_red", "type": "picket"}
    eff = tools.get_parcel("P-0001")["effective_fence"]
    assert (eff["type"], eff["column"]) == ("picket", "brick_red")
    tools.set_fence("P-0001", type="stone_wall")
    tools.reset_fence("P-0001")
    tools.set_fence("P-0001", type="concrete")
    eff = tools.get_parcel("P-0001")
    assert eff["effective_fence"]["column"] == "concrete"  # the type's own
    assert "stone" in eff["rule_defaults"]["fence"]["column_types"]
    with pytest.raises(ToolError, match="column must be one of"):
        tools.set_fence("P-0001", column="marble")
    with pytest.raises(ToolError, match="cannot be combined"):
        tools.set_fence("P-0001", mode="none", column="stone")


# --- cables between utility poles ---

POLE = "prim:pole_lamp_1"


def test_connect_poles_strings_a_chain_and_refuses_what_is_not_a_pole(tools, env):
    settings = env[1]
    a = tools.place_object(POLE, "P-0001", anchor="front")["id"]
    b = tools.place_object(POLE, "P-0001", anchor="centre")["id"]
    c = tools.place_object("prim:pole_plain_1", "P-0001", anchor="back")["id"]
    car = tools.place_object(CAR, "P-0001", anchor="left")["id"]
    out = tools.connect_poles([a, b, c])
    assert [(x["id"], x["from"], x["to"]) for x in out["cables"]] == [("C-0001", a, b), ("C-0002", b, c)]
    assert all(x["length_m"] > 0 for x in out["cables"]) and out["warnings"] == []
    canonical(settings)
    assert load_edits(settings.edits_path).cables == {"C-0001": {"from": a, "to": b}, "C-0002": {"from": b, "to": c}}
    again = tools.connect_poles([b, a])  # the same pair the other way round
    assert again["cables"] == [] and "already joined by C-0001" in again["warnings"][0]
    with pytest.raises(ToolError, match="not a utility pole or a house connector"):
        tools.connect_poles([a, car])
    wall = tools.place_object("prim:connector_wall_2", "P-0001", anchor="right")["id"]
    house = tools.connect_poles([c, wall])  # a cable can end on a house connector
    assert [(x["from"], x["to"]) for x in house["cables"]] == [(c, wall)]
    assert [x["id"] for x in tools.list_cables()["connectors"]] == [wall]
    tools.disconnect_poles(poles=[c, wall])
    tools.remove_object(wall)
    with pytest.raises(ToolError, match="no placed object"):
        tools.connect_poles([a, "o-9999"])
    with pytest.raises(ToolError, match="two or more"):
        tools.connect_poles([a])
    listed = tools.list_cables()
    assert [x["id"] for x in listed["cables"]] == ["C-0001", "C-0002"]
    assert {p["id"]: p["cables"] for p in listed["poles"]} == {a: 1, b: 2, c: 1}
    assert [x["id"] for x in tools.list_cables(pole=c)["cables"]] == ["C-0002"]


def test_disconnect_poles_and_removing_a_pole_takes_its_cables(tools, env):
    settings = env[1]
    ids = [tools.place_object(POLE, "P-0001", anchor=a)["id"] for a in ("front", "centre", "back")]
    tools.connect_poles(ids)
    with pytest.raises(ToolError, match="either cable"):
        tools.disconnect_poles()
    with pytest.raises(ToolError, match="not joined"):
        tools.disconnect_poles(poles=[ids[0], ids[2]])
    assert tools.disconnect_poles(poles=[ids[1], ids[0]])["id"] == "C-0001"
    assert list(load_edits(settings.edits_path).cables) == ["C-0002"]
    out = tools.remove_object(ids[2])
    assert out["cables_removed"] == ["C-0002"] and "C-0002" in out["summary"]
    assert load_edits(settings.edits_path).cables == {}
    assert '"cables"' not in settings.edits_path.read_text()  # the key goes when the last cable does
    canonical(settings)


def test_ground_types_are_terrain_only(env):
    tools, settings, _ = env
    types = {t["type"]: t for t in tools.list_ground_types()["types"]}
    assert {"field", "forest", "meadow", "village", "water"} <= set(types)
    assert types["water"]["attributes"]["water"] and types["meadow"]["attributes"]["recipe"] == "field"
    assert all(not ({"spawns", "event_spawn", "wildlife"} & set(t["attributes"])) for t in types.values())
    catalog = tools.list_ground_types()["catalog"]
    assert [g["group"] for g in catalog["groups"]] == ["appearance", "water"]
    assert {a["attribute"] for g in catalog["groups"] for a in g["attributes"]} == {"colours", "recipe", "detail", "water"}
    assert all("stacking" not in a for g in catalog["groups"] for a in g["attributes"])
    for gone in ({"spawns": True}, {"event_spawn": ["bigfoot"]}, {"wildlife": ["deer"]}):
        with pytest.raises(ToolError, match="unknown attribute"):
            tools.set_ground_type("herd", attributes=gone)
    assert not settings.edits_path.exists()


def test_a_paint_colour_is_a_ground_type_of_your_own(env):
    tools, settings, _ = env
    out = tools.set_ground_type("paint_e2b92c", attributes={"colours": ["#e2b92c"]})
    assert out["type"]["attributes"] == {"colours": ["#e2b92c"]} and out["type"]["from"] == "hand-drawn or default only"
    assert tools.create_ground_patch("paint_e2b92c", polygon=SQUARE)["type"] == "paint_e2b92c"
    canonical(settings)


def test_ground_type_tools(env):
    tools, settings, _ = env
    rows = {t["type"]: t for t in tools.list_ground_types()["types"]}
    assert rows["meadow"]["source"] == "rules" and rows["meadow"]["attributes"]["recipe"] == "field" and rows["forest"]["osm"]["landuse"] == ["forest"]
    out = tools.set_ground_type("meadow", attributes={"colours": ["#112233"], "detail": {"tone_per_ha": 9}})
    assert "overridden" in out["summary"] and "make map" in out["summary"]
    attrs = out["type"]["attributes"]
    assert out["type"]["source"] == "overridden" and attrs["colours"] == ["#112233"] and attrs["recipe"] == "field"
    assert attrs["detail"]["furrow"]["probability"] == 0.0 and attrs["detail"]["tone_per_ha"] == 9  # merged
    assert load_edits(settings.edits_path).ground_types["meadow"] == {"attributes": {"colours": ["#112233"], "detail": {"tone_per_ha": 9}}}
    canonical(settings)

    out = tools.set_ground_type("dunes", attributes={"colours": ["#d8c38a"], "recipe": "field"}, osm={"natural": ["sand"]})
    assert "created" in out["summary"] and out["type"]["source"] == "custom"
    assert [r["type"] for r in tools.list_ground_types()["types"]][-1] == "dunes"
    assert tools.create_ground_patch("dunes", polygon=SQUARE)["type"] == "dunes"  # a custom type is usable
    assert "updated" in tools.set_ground_type("dunes", attributes={"recipe": None})["summary"]  # a custom type just drops it
    assert load_edits(settings.edits_path).ground_types["dunes"]["attributes"] == {"colours": ["#d8c38a"]}

    with pytest.raises(ToolError, match="give attributes"):
        tools.set_ground_type("meadow")
    with pytest.raises(ToolError, match="1 to 6"):
        tools.set_ground_type("meadow", attributes={"colours": []})
    with pytest.raises(ToolError, match="type names"):
        tools.set_ground_type("Bad Name", attributes={"colours": ["#ffffff"]})
    with pytest.raises(ToolError, match="#rrggbb"):
        tools.set_ground_type("meadow", attributes={"colours": ["red"]})
    with pytest.raises(ToolError, match="unknown attribute"):
        tools.set_ground_type("meadow", attributes={"trees": {}})
    assert "Bad Name" not in settings.edits_path.read_text()  # nothing was written

    out = tools.reset_ground_type("meadow")
    assert out["type"]["source"] == "rules" and "meadow" not in load_edits(settings.edits_path).ground_types
    with pytest.raises(ToolError, match="no override"):
        tools.reset_ground_type("meadow")
    with pytest.raises(ToolError, match="delete_ground_type"):
        tools.reset_ground_type("dunes")
    canonical(settings)


def test_a_type_that_makes_a_patch_water_needs_the_patch_to_have_a_level(env):
    tools, settings, _ = env
    tools.create_ground_patch("meadow", polygon=SQUARE)
    before = settings.edits_path.read_bytes()
    with pytest.raises(ToolError, match="water_y"):
        tools.set_ground_type("meadow", attributes={"water": True})  # the patch has no level
    assert settings.edits_path.read_bytes() == before


def test_delete_ground_type(env):
    tools, settings, _ = env
    for protected, why in (("field", "default"), ("dike", "rule")):
        with pytest.raises(ToolError, match=why):
            tools.delete_ground_type(protected)
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.delete_ground_type("swamp")
    tools.set_ground_type("dunes", attributes={"colours": ["#d8c38a"]})
    assert tools.delete_ground_type("dunes")["type"] == "dunes"
    assert load_edits(settings.edits_path).ground_types == {}  # a custom type is just removed

    tools.create_ground_patch("orchard", polygon=SQUARE)
    with pytest.raises(ToolError, match="GP-0001"):  # a patch of the type: delete or retype it first
        tools.delete_ground_type("orchard")
    assert load_edits(settings.edits_path).ground_types == {}
    tools.delete_ground_patch("GP-0001")
    out = tools.delete_ground_type("orchard")
    assert "reset_ground_type" in out["summary"]
    assert load_edits(settings.edits_path).ground_types == {"orchard": {"deleted": True}}
    listing = tools.list_ground_types()
    assert "orchard" not in {t["type"] for t in listing["types"]} and listing["deleted"] == ["orchard"]
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.create_ground_patch("orchard", polygon=SQUARE)
    with pytest.raises(ToolError, match="already deleted"):
        tools.delete_ground_type("orchard")
    with pytest.raises(ToolError, match="reset_ground_type it first"):
        tools.set_ground_type("orchard", attributes={"colours": ["#ffffff"]})
    assert "it was deleted" in tools.reset_ground_type("orchard")["summary"]
    assert load_edits(settings.edits_path).ground_types == {}
    assert '"ground_types"' not in settings.edits_path.read_text()


# --- objects belong to no parcel ----------------------------------------------------------


def test_a_pole_needs_no_parcel(env):
    tools, settings, _ = env
    far = [OX + 900, OY + 900]  # on no parcel at all: ground
    placed = tools.place_object("prim:pole_lamp_1", position=far)
    assert placed["position"] == far and placed["rotation_deg"] == 0.0 and placed["warnings"] == []
    assert "parcel" not in placed
    canonical(settings)
    edits = load_edits(settings.edits_path)
    assert [o["id"] for o in edits.objects] == [placed["id"]]
    assert not any("objects" in e for e in edits.parcels.values())  # nothing is listed under a parcel

    moved = tools.move_object(placed["id"], position=[far[0] + 5, far[1]], rotation_deg=90)
    assert moved["entry"]["position"] == [far[0] + 5, far[1]] and moved["entry"]["rotation_deg"] == 90.0
    assert [o["id"] for o in load_edits(settings.edits_path).objects] == [placed["id"]]

    assert "removed" in tools.remove_object(placed["id"])["summary"]
    assert load_edits(settings.edits_path).objects == []
    assert '"objects"' not in settings.edits_path.read_text()  # only written when there are some


def test_object_z_and_solid_round_trip(env):
    tools, settings, _ = env
    far = [OX + 900, OY + 900]
    plain = tools.place_object("prim:slab", position=far)["entry"]
    assert "z" not in plain and "solid" not in plain  # defaults are not written

    placed = tools.place_object("prim:slab", position=far, z=212.5, solid=False)
    assert placed["entry"]["z"] == 212.5 and placed["entry"]["solid"] is False
    oid = placed["id"]
    on_disk = next(o for o in load_edits(settings.edits_path).objects if o["id"] == oid)
    assert on_disk["z"] == 212.5 and on_disk["solid"] is False
    canonical(settings)

    assert tools.move_object(oid, z=213.0)["entry"]["z"] == 213.0
    cleared = tools.move_object(oid, clear_z=True)["entry"]
    assert "z" not in cleared and cleared["solid"] is False  # solid kept
    back = tools.move_object(oid, solid=True)["entry"]
    assert "solid" not in back  # true is the default: the key goes away
    assert tools.place_object("prim:slab", position=far, solid=True)["entry"].get("solid") is None
    canonical(settings)


def test_object_z_and_solid_are_validated(env):
    tools, settings, _ = env
    far = [OX + 900, OY + 900]
    oid = tools.place_object("prim:slab", position=far)["id"]
    for bad in (float("nan"), float("inf"), "3", True):
        with pytest.raises(ToolError, match="z must be a number"):
            tools.place_object("prim:slab", position=far, z=bad)
        with pytest.raises(ToolError, match="z must be a number"):
            tools.move_object(oid, z=bad)
    with pytest.raises(ToolError, match="solid must be true or false"):
        tools.place_object("prim:slab", position=far, solid="no")
    with pytest.raises(ToolError, match="solid must be true or false"):
        tools.move_object(oid, solid=0)
    with pytest.raises(ToolError, match="not both"):
        tools.move_object(oid, z=1.0, clear_z=True)
    with pytest.raises(ToolError, match="nothing to change"):
        tools.move_object(oid)
    assert [o["id"] for o in load_edits(settings.edits_path).objects] == [oid]  # nothing was written by the refusals


def test_an_anchor_needs_a_parcel_but_an_exact_position_does_not(env):
    tools, _, _ = env
    with pytest.raises(ToolError, match="exact `position`"):
        tools.place_object("prim:pole_lamp_1", anchor="front")
    with pytest.raises(ToolError, match="exact `position`"):
        tools.place_object("prim:pole_lamp_1")


def test_get_parcel_lists_the_objects_standing_in_it(env):
    tools, _, _ = env
    inside = tools.place_object(CAR, "P-0001", anchor="centre")
    tools.place_object("prim:pole_lamp_1", position=[OX + 900, OY + 900])
    assert [o["id"] for o in tools.get_parcel("P-0001")["edits"]["objects"]] == [inside["id"]]
    assert tools.get_parcel("P-0002")["edits"]["objects"] == []


def test_set_parcel_ground_type(env):
    tools, settings, _ = env
    tools.set_ground_type("graveyard", attributes={"colours": ["#9aa38f", "#939c88"]})
    assert tools.set_parcel_ground_type("P-0001", "graveyard")["ground_type"] == "graveyard"
    assert load_edits(settings.edits_path).parcels == {"P-0001": {"ground_type": "graveyard"}}
    canonical(settings)
    with pytest.raises(ToolError, match="P-0001"):  # a type in use stays
        tools.delete_ground_type("graveyard")
    with pytest.raises(ToolError):
        tools.set_parcel_ground_type("P-0001", "no_such_type")
    assert tools.set_parcel_ground_type("P-0001", None)["changed"] is True
    assert load_edits(settings.edits_path).parcels == {}  # emptied entry dropped
    assert tools.set_parcel_ground_type("P-0001", None)["changed"] is False


def test_parcel_outline_tools(env):
    tools, settings, _ = env
    # P-0001 is box(40,40,95,100) with a house at (60..75, 55..70); a line cutting its north-east corner
    abs_ = lambda x, y: [OX + x, OY + y]
    settings.sketch_path.write_text(json.dumps({"lines": [
        {"id": "L1", "points": [abs_(80, 100), abs_(95, 85)]},
        {"id": "L2", "points": [abs_(50, 100), abs_(50, 60), abs_(95, 60)]},  # cuts the house off
    ]}))
    out = tools.set_parcel_outline("P-0001", sketch_line="L1")
    assert out["old_area_m2"] == 3300.0 and out["new_area_m2"] == pytest.approx(3300 - 112.5) and out["trimmed"] == {}
    stored = load_edits(settings.edits_path).parcels["P-0001"]["outline"]
    assert Polygon([local(p) for p in stored]).area == pytest.approx(3300 - 112.5) and abs_(80, 100) in stored
    canonical(settings)
    # a line that would leave the house outside, and a bow-tie, are refused and change nothing
    before = settings.edits_path.read_text()
    with pytest.raises(ToolError, match="leaves w1 outside"):
        tools.set_parcel_outline("P-0001", sketch_line="L2")
    with pytest.raises(ToolError, match="refused"):
        tools.set_parcel_outline("P-0001", points=[abs_(40, 40), abs_(95, 100), abs_(95, 40), abs_(40, 100)])
    with pytest.raises(ToolError, match="exactly one"):
        tools.set_parcel_outline("P-0001")
    assert settings.edits_path.read_text() == before
    # the full outline, reaching 5 m over the unedited neighbour P-0002: it is cut back
    full = [abs_(40, 40), abs_(100, 40), abs_(100, 100), abs_(40, 100)]
    out = tools.set_parcel_outline("P-0001", points=full)
    assert out["trimmed"] == {"P-0002": 300.0}
    assert tools.reset_parcel_outline("P-0001")["changed"] is True
    assert load_edits(settings.edits_path).parcels == {}
    assert tools.reset_parcel_outline("P-0001")["changed"] is False


def test_path_tools(env):
    tools, settings, _ = env
    out = tools.create_path(points=[[0.0, 0.0], [10.0, 0.0]], width_m=1.2)
    assert out["id"] == "PA-0001" and load_edits(settings.edits_path).paths == {"PA-0001": {"points": [[0.0, 0.0], [10.0, 0.0]], "width_m": 1.2}}
    canonical(settings)
    assert tools.list_paths()["count"] == 1
    with pytest.raises(ToolError):
        tools.create_path(points=[[0.0, 0.0]])
    with pytest.raises(ToolError):
        tools.create_path()
    tools.delete_path("PA-0001")
    assert load_edits(settings.edits_path).paths == {}
    with pytest.raises(ToolError):
        tools.delete_path("PA-0001")
