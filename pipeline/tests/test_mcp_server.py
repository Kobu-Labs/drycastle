"""MCP server tools on the synthetic workspace (P-0001 building plot with a house, road to its north)."""

import asyncio
import json
import threading
import time

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.edits import dump_text, load_edits, validate
from drycastle_pipeline.mcp_server import (
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
    # a mark off the map alone: answered, with a warning about the area
    assert "another area" in tools.get_sketch("B")["warnings"][0]
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
    assert p["rule_defaults"]["trees"]["density_per_1000m2"] == 0.8
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
                "list_assets", "find_parcels", "get_parcel", "set_fence", "reset_fence",
                "place_object", "remove_object", "move_object", "create_group", "merge_groups",
                "dissolve_group", "create_region", "set_region_colour", "set_region_water", "set_region_ground", "list_ground_types", "set_ground_type", "reset_ground_type", "delete_ground_type", "move_region_point",
                "delete_region", "list_regions", "connect_poles", "disconnect_poles", "list_cables", "get_sketch", "create_sketch", "sketch_add_point", "sketch_add_line", "sketch_add_road", "sketch_remove",
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
    from drycastle_pipeline.mcp_server import _edits_doc

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
    a = tools.place_object("P-0001", POLE, anchor="front")["id"]
    b = tools.place_object("P-0001", POLE, anchor="centre")["id"]
    c = tools.place_object("P-0001", "prim:pole_plain_1", anchor="back")["id"]
    car = tools.place_object("P-0001", CAR, anchor="left")["id"]
    out = tools.connect_poles([a, b, c])
    assert [(x["id"], x["from"], x["to"]) for x in out["cables"]] == [("C-0001", a, b), ("C-0002", b, c)]
    assert all(x["length_m"] > 0 for x in out["cables"]) and out["warnings"] == []
    canonical(settings)
    assert load_edits(settings.edits_path).cables == {"C-0001": {"from": a, "to": b}, "C-0002": {"from": b, "to": c}}
    again = tools.connect_poles([b, a])  # the same pair the other way round
    assert again["cables"] == [] and "already joined by C-0001" in again["warnings"][0]
    with pytest.raises(ToolError, match="not a utility pole or a house connector"):
        tools.connect_poles([a, car])
    wall = tools.place_object("P-0001", "prim:connector_wall_2", anchor="right")["id"]
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
    ids = [tools.place_object("P-0001", POLE, anchor=a)["id"] for a in ("front", "centre", "back")]
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


def test_region_ground_types(env):
    tools, settings, _ = env
    types = {t["type"]: t for t in tools.list_ground_types()["types"]}
    assert {"field", "forest", "meadow", "village", "water"} <= set(types)
    assert types["water"]["water"] and types["forest"]["trees_per_1000m2"] > 0 and types["meadow"]["recipe"] == "field"
    out = tools.create_region(polygon=SQUARE, ground="meadow")  # no colour: the type's first shade tints the preview
    assert (out["ground"], out["colour"]) == ("meadow", types["meadow"]["colours"][0].lower())
    assert load_edits(settings.edits_path).regions["R-0001"]["ground"] == "meadow"
    assert tools.list_regions()["regions"][0]["ground"] == "meadow"
    tools.set_region_ground("R-0001", "orchard")
    assert load_edits(settings.edits_path).regions["R-0001"]["ground"] == "orchard"
    tools.set_region_ground("R-0001", None)
    assert "ground" not in load_edits(settings.edits_path).regions["R-0001"]
    canonical(settings)
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.set_region_ground("R-0001", "swamp")
    with pytest.raises(ToolError, match="colour is required"):
        tools.create_region(polygon=SQUARE)
    with pytest.raises(ToolError, match="not both"):
        tools.create_region(polygon=SQUARE, ground="meadow", water_y=100.0)
    tools.set_region_water("R-0001", 100.0)
    with pytest.raises(ToolError, match="body of water"):
        tools.set_region_ground("R-0001", "meadow")


def test_ground_type_tools(env):
    tools, settings, _ = env
    rows = {t["type"]: t for t in tools.list_ground_types()["types"]}
    assert rows["meadow"]["source"] == "rules" and rows["meadow"]["trees_per_1000m2"] == 0.1 and rows["forest"]["forest_trees"]
    out = tools.set_ground_type("meadow", trees_per_1000m2=2.5, spawns=False, detail={"tone_per_ha": 9})
    assert "overridden" in out["summary"] and "make map" in out["summary"]
    assert out["type"]["source"] == "overridden" and out["type"]["trees_per_1000m2"] == 2.5 and out["type"]["spawns"] is False
    assert out["type"]["detail"]["furrow"]["probability"] == 0.0 and out["type"]["detail"]["tone_per_ha"] == 9  # merged
    assert load_edits(settings.edits_path).ground_types["meadow"] == {"trees": {"per_1000m2": 2.5}, "spawns": False, "detail": {"tone_per_ha": 9}}
    canonical(settings)

    out = tools.set_ground_type("dunes", colours=["#d8c38a"], recipe="field", osm={"natural": ["sand"]})
    assert "created" in out["summary"] and out["type"]["source"] == "custom"
    assert [r["type"] for r in tools.list_ground_types()["types"]][-1] == "dunes"
    assert tools.create_region(polygon=SQUARE, ground="dunes")["ground"] == "dunes"  # a custom type is usable
    assert "updated" in tools.set_ground_type("dunes", recipe="none")["summary"]
    assert load_edits(settings.edits_path).ground_types["dunes"]["recipe"] is None

    with pytest.raises(ToolError, match="at least one"):
        tools.set_ground_type("meadow")
    with pytest.raises(ToolError, match="1 to 6"):
        tools.set_ground_type("meadow", colours=[])
    with pytest.raises(ToolError, match="type names"):
        tools.set_ground_type("Bad Name", spawns=True)
    with pytest.raises(ToolError, match="0 to 50"):
        tools.set_ground_type("meadow", trees_per_1000m2=60)
    assert "Bad Name" not in settings.edits_path.read_text()  # nothing was written

    out = tools.reset_ground_type("meadow")
    assert out["type"]["source"] == "rules" and "meadow" not in load_edits(settings.edits_path).ground_types
    with pytest.raises(ToolError, match="no override"):
        tools.reset_ground_type("meadow")
    with pytest.raises(ToolError, match="delete_ground_type"):
        tools.reset_ground_type("dunes")
    canonical(settings)


def test_delete_ground_type(env):
    tools, settings, _ = env
    for protected, why in (("field", "default"), ("dike", "rule")):
        with pytest.raises(ToolError, match=why):
            tools.delete_ground_type(protected)
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.delete_ground_type("swamp")
    tools.set_ground_type("dunes", colours=["#d8c38a"])
    assert tools.delete_ground_type("dunes")["type"] == "dunes"
    assert load_edits(settings.edits_path).ground_types == {}  # a custom type is just removed

    tools.create_region(polygon=SQUARE, ground="orchard")
    out = tools.delete_ground_type("orchard")
    assert "reset_ground_type" in out["summary"] and "R-0001" in out["warnings"][0]
    assert load_edits(settings.edits_path).ground_types == {"orchard": {"deleted": True}}
    listing = tools.list_ground_types()
    assert "orchard" not in {t["type"] for t in listing["types"]} and listing["deleted"] == ["orchard"]
    with pytest.raises(ToolError, match="unknown ground type"):
        tools.set_region_ground("R-0001", "orchard")
    with pytest.raises(ToolError, match="already deleted"):
        tools.delete_ground_type("orchard")
    with pytest.raises(ToolError, match="reset_ground_type it first"):
        tools.set_ground_type("orchard", spawns=True)
    assert "it was deleted" in tools.reset_ground_type("orchard")["summary"]
    assert load_edits(settings.edits_path).ground_types == {}
    assert '"ground_types"' not in settings.edits_path.read_text()
