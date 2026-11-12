"""Regions: schema, canonical writer, stale entries, overlaps, draping, export and region-run."""

import json
from types import SimpleNamespace

import numpy as np
import pytest
import shapely
import trimesh
from shapely.geometry import Polygon

from drycastle_pipeline import cli, region_run
from drycastle_pipeline.edits import (
    Edits,
    EditsError,
    dump_edits,
    dump_text,
    load_edits,
    parse,
    validate,
)
from drycastle_pipeline.export import run_export
from drycastle_pipeline.regions import (
    _terrain_triangles,
    drape,
    overlapping,
    plan_regions,
    region_meshes,
    region_rank,
    road_layers,
    visible_area,
)
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain, make_workspace

OX, OY = SYNTHETIC_ORIGIN
RCFG, ROADS = load_section("regions"), load_section("roads")


def sq(x0, y0, x1, y1):
    """A rectangle ring in absolute coordinates from local metres."""
    return [[OX + x0, OY + y0], [OX + x1, OY + y0], [OX + x1, OY + y1], [OX + x0, OY + y1]]


def region(colour="#336699", polygon=None):
    return {"colour": colour, "polygon": polygon or sq(50, 120, 90, 150)}


def doc(**regions):
    return {"version": 1, "regions": regions}


# --- schema ---------------------------------------------------------------------------------

TRIANGLE = [[0, 0], [10, 0], [0, 10]]
BOWTIE = [[0, 0], [10, 10], [10, 0], [0, 10]]


def test_valid_regions() -> None:
    assert validate(doc(**{"R-0001": region(), "R-12345": {"colour": "#AbCdEf", "polygon": TRIANGLE}})) == []
    closed = [*TRIANGLE, TRIANGLE[0]]  # a repeated closing point is tolerated
    assert validate(doc(**{"R-0001": {"colour": "#000000", "polygon": closed}})) == []
    assert validate({"version": 1}) == []


@pytest.mark.parametrize(
    ("regions", "message"),
    [
        ({"R-1": region()}, "region IDs look like R-0001"),
        ({"P-0001": region()}, "region IDs look like R-0001"),
        ({"R-0001": []}, "must be an object"),
        ({"R-0001": {"polygon": TRIANGLE}}, "regions.R-0001.colour: required"),
        ({"R-0001": {"colour": "#fff", "polygon": TRIANGLE}}, "#rrggbb"),
        ({"R-0001": {"colour": 5, "polygon": TRIANGLE}}, "#rrggbb"),
        ({"R-0001": {"colour": "#112233"}}, "regions.R-0001.polygon: required"),
        ({"R-0001": {"colour": "#112233", "polygon": "x"}}, "must be a list"),
        ({"R-0001": {"colour": "#112233", "polygon": TRIANGLE[:2]}}, "at least 3 points"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1, 1], [0, 0]]}}, "at least 3 points"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1], [3, 4]]}}, "polygon.1: must be [x, y]"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1, "a"], [3, 4]]}}, "polygon.1"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1, True], [3, 4]]}}, "polygon.1"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1, float("nan")], [3, 4]]}}, "polygon.1"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [1, float("inf")], [3, 4]]}}, "polygon.1"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [0, 0], [5, 0], [0, 5]]}}, "repeated"),
        ({"R-0001": {"colour": "#112233", "polygon": BOWTIE}}, "must not intersect"),
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [5, 0], [10, 0]]}}, "enclose an area"),
        # an edge that touches another edge's vertex
        ({"R-0001": {"colour": "#112233", "polygon": [[0, 0], [10, 0], [10, 10], [5, 0], [0, 10]]}}, "must not"),
        ({"R-0001": {"colour": "#112233", "polygon": TRIANGLE, "z": 1}}, "regions.R-0001.z: unknown key"),
    ],
)
def test_invalid_regions(regions, message) -> None:
    errs = validate(doc(**regions))
    assert any(message in e for e in errs), errs


def test_regions_must_be_an_object() -> None:
    assert any("regions: must be an object" in e for e in validate({"version": 1, "regions": []}))


def test_region_errors_are_all_reported() -> None:
    errs = validate(doc(**{"R-1": {"colour": "red", "polygon": TRIANGLE[:2], "x": 1}}))
    assert len(errs) == 4


# --- canonical writer -----------------------------------------------------------------------


def test_round_trip_is_byte_stable(tmp_path) -> None:
    src = tmp_path / "hand.json"
    hand = doc(**{"R-0012": region("#AA0000", TRIANGLE), "R-0002": region()})
    hand["parcels"] = {"P-0001": {"fence": {"mode": "none"}}}
    src.write_text(json.dumps(hand, indent=4))
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    dump_edits(load_edits(src), first)
    dump_edits(load_edits(first), second)
    assert first.read_bytes() == second.read_bytes()
    assert first.read_text() == dump_text(load_edits(src))
    assert json.loads(first.read_text())["regions"] == hand["regions"]
    text = first.read_text()
    assert text.index('"R-0002"') < text.index('"R-0012"')  # sorted keys
    assert '"colour": "#AA0000"' in text  # written as given


def test_files_without_regions_keep_their_bytes() -> None:
    assert '"regions"' not in dump_text(Edits())
    assert dump_text(Edits()) == '{\n "groups": {},\n "parcels": {},\n "version": 1\n}\n'


def test_dump_refuses_invalid_regions(tmp_path) -> None:
    with pytest.raises(EditsError):
        dump_edits(Edits(regions={"R-0001": {"colour": "#112233", "polygon": BOWTIE}}), tmp_path / "e.json")
    assert not (tmp_path / "e.json").exists()


def test_parse_keeps_regions_in_order_of_the_document() -> None:
    parsed = parse(doc(**{"R-0001": region(), "R-0002": region("#ffffff")}))
    assert list(parsed.regions) == ["R-0001", "R-0002"]


# --- planning and overlap -------------------------------------------------------------------


def plan_of(**regions):
    return plan_regions(parse(doc(**regions)), list(SYNTHETIC_ORIGIN), make_terrain())


def test_stale_regions_warn_and_are_skipped() -> None:
    plan = plan_of(
        **{
            "R-0001": region(),
            "R-0002": region(polygon=sq(1000, 1000, 1100, 1100)),  # off the terrain
            "R-0003": region(polygon=sq(-100, -100, 5, 5)),  # off the terrain
        }
    )
    assert list(plan.regions) == ["R-0001"]
    assert len(plan.warnings) == 2
    assert all("outside the terrain coverage" in w for w in plan.warnings)
    assert {w.split(":")[0] for w in plan.warnings} == {"regions.R-0002", "regions.R-0003"}


def test_region_partly_outside_the_coverage_is_clipped() -> None:
    plan = plan_of(**{"R-0001": region(polygon=sq(-50, 120, 90, 150))})
    assert plan.warnings == []
    assert plan.regions["R-0001"].polygon.bounds[0] == pytest.approx(10)


def test_no_terrain_skips_with_a_warning_and_no_regions_need_no_origin() -> None:
    plan = plan_regions(parse(doc(**{"R-0001": region()})), list(SYNTHETIC_ORIGIN), None)
    assert not plan.regions and "no terrain" in plan.warnings[0]
    assert plan_regions(Edits(), None, None).regions == {}
    with pytest.raises(ValueError, match="origin"):
        plan_regions(parse(doc(**{"R-0001": region()})), None, make_terrain())


def test_higher_id_wins_where_regions_overlap() -> None:
    plan = plan_of(
        **{"R-0001": region(polygon=sq(50, 120, 90, 150)), "R-0002": region("#ff0000", sq(70, 130, 110, 170))}
    )
    low, high = visible_area(plan, "R-0001"), visible_area(plan, "R-0002")
    assert high.equals(plan.regions["R-0002"].polygon)
    assert low.area == pytest.approx(40 * 30 - 20 * 20)
    assert low.intersection(plan.regions["R-0002"].polygon).area < 1e-9
    assert overlapping(plan, {"R-0002"}) == {"R-0001", "R-0002"}
    assert overlapping(plan_of(**{"R-0001": region(), "R-0002": region(polygon=sq(120, 120, 150, 150))}), {"R-0001"}) == {
        "R-0001"
    }


def test_overlap_meshes_never_cover_the_same_ground() -> None:
    plan = plan_of(
        **{"R-0001": region(polygon=sq(50, 120, 90, 150)), "R-0002": region("#ff0000", sq(70, 130, 110, 170))}
    )
    meshes = region_meshes(plan, make_terrain(), None, ROADS, RCFG)
    shapes = {r: footprint(m) for r, m in meshes.items()}
    assert shapes["R-0001"].intersection(shapes["R-0002"]).area < 1e-6
    assert shapes["R-0001"].area == pytest.approx(40 * 30 - 20 * 20, rel=1e-6)
    assert shapes["R-0002"].area == pytest.approx(40 * 40, rel=1e-6)


def test_a_region_fully_under_a_higher_one_has_no_mesh() -> None:
    plan = plan_of(**{"R-0001": region(polygon=sq(60, 125, 70, 135)), "R-0002": region(polygon=sq(50, 120, 90, 150))})
    assert list(region_meshes(plan, make_terrain(), None, ROADS, RCFG)) == ["R-0002"]


# --- draping --------------------------------------------------------------------------------


def footprint(mesh):
    from shapely import polygons, union_all

    return union_all(polygons(mesh.tris[:, :, :2]).tolist())


def test_mesh_covers_exactly_the_polygon_and_faces_up() -> None:
    t = make_terrain()
    ring = [(52, 123), (88, 121), (91, 140), (70, 149), (55, 138)]
    mesh = drape(Polygon(ring), [1, 2, 3], t, [], 0.03, 0.02)
    assert footprint(mesh).symmetric_difference(Polygon(ring)).area < 1e-6
    a, b, c = mesh.tris[:, 0], mesh.tris[:, 1], mesh.tris[:, 2]
    assert (np.cross(b - a, c - a)[:, 2] > 0).all()
    assert (mesh.colours == [1, 2, 3]).all()
    assert mesh.tris[:, :, :2].min() >= 50 - 1e-9


def test_mesh_lies_on_the_terrain_and_follows_a_kink() -> None:
    t = make_terrain()
    t.heights[:, :] += np.where(np.arange(t.heights.shape[1]) * t.step > 80, 3.0, 0.0)  # a step up
    big = Polygon([(40, 120), (140, 120), (140, 160), (40, 160)])
    mesh = drape(big, [0, 0, 0], t, [], RCFG["lift_m"], RCFG["road_lift_m"])
    v = mesh.tris.reshape(-1, 3)
    assert np.allclose(v[:, 2] - t.surface(v[:, 0], v[:, 1]), RCFG["lift_m"], atol=1e-9)
    # every terrain cell it touches is cut at the grid, so the mesh has more than 2 triangles per 1600 m2
    assert mesh.face_count >= 2 * (100 // 4) * (40 // 4)
    # the centroid of each triangle is within the lift of the terrain surface (no cutting corners)
    c = mesh.tris.mean(axis=1)
    assert np.abs(c[:, 2] - t.surface(c[:, 0], c[:, 1]) - RCFG["lift_m"]).max() < 1e-9


def test_region_over_a_road_floats_above_the_ribbon() -> None:
    from drycastle_pipeline.roads import road_mesh

    ws = make_workspace_graph()
    t = make_terrain()
    ribbons = road_mesh(ws, t, ROADS)
    poly = Polygon([(60, 95), (100, 95), (100, 118), (60, 118)])  # across the residential road y=106
    layers = road_layers(ws, t, ROADS, poly.bounds)
    assert layers and layers[0][1] > ROADS["lift_m"]
    mesh = drape(poly, [9, 9, 9], t, layers, RCFG["lift_m"], RCFG["road_lift_m"])
    assert footprint(mesh).symmetric_difference(poly).area < 1e-6
    c = mesh.tris.mean(axis=1)
    rise = c[:, 2] - t.surface(c[:, 0], c[:, 1])
    on_road = np.abs(c[:, 1] - 106) < 1.5  # inside the 5 m ribbon
    assert on_road.any() and (rise[on_road] > ROADS["lift_m"] + RCFG["road_lift_m"] - 1e-6).all()
    assert np.allclose(rise[np.abs(c[:, 1] - 106) > 3.5], RCFG["lift_m"], atol=1e-9)
    # and above the ribbon surface itself, which is what the player sees
    rc = ribbons.tris.mean(axis=1)
    ribbon_rise = rc[:, 2] - t.surface(rc[:, 0], rc[:, 1])
    centre = (np.abs(rc[:, 0] - 80) < 15) & (np.abs(rc[:, 1] - 106) < 3)
    assert centre.any() and rise[on_road].min() > ribbon_rise[centre].max() + 0.01


def make_workspace_graph():
    import tempfile
    from pathlib import Path

    with tempfile.TemporaryDirectory() as tmp:
        return json.loads((make_workspace(Path(tmp))["work"] / "roads.json").read_text())


# --- export and region-run ------------------------------------------------------------------

EDITS = doc(
    **{
        "R-0001": region("#336699", sq(50, 120, 90, 150)),
        "R-0002": region("#ff0000", sq(60, 95, 120, 118)),  # over the road
        "R-0003": region("#00ff00", sq(30, 20, 60, 40)),
        "R-0004": region("#112233", sq(2000, 2000, 2100, 2100)),  # stale
    }
)


@pytest.fixture
def ws(tmp_path):
    full = make_workspace(tmp_path)
    full["edits"] = tmp_path / "edits.json"
    full["edits"].write_text(json.dumps(EDITS))
    return full


def test_export_writes_region_nodes_into_the_chunks_they_touch(ws, capsys) -> None:
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    assert "regions" in manifest["generated"]
    assert "R-0004" in capsys.readouterr().err
    names = {}
    for c in manifest["chunks"]:
        scene = trimesh.load(ws["out"] / c["file"], force="scene")
        names[tuple(c["index"])] = set(scene.graph.nodes_geometry)
    assert {n for ns in names.values() for n in ns if n.startswith("region_")} == {
        "region_R-0001",
        "region_R-0002",
        "region_R-0003",
    }
    assert {n for n in names[(0, 0)] if n.startswith("region_")} == {"region_R-0001", "region_R-0002", "region_R-0003"}
    chunk = next(c for c in manifest["chunks"] if c["index"] == [0, 0])
    assert chunk["triangles"]["region_R-0001"] > 0
    # no effect on anything else: same chunk without regions has the same non-region nodes
    other = ws["out"].parent / "out2"
    plain = run_export(ws["work"], other, ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["raw"] / "none.json")
    assert "regions" not in plain["generated"]
    base = {k: v for k, v in plain["chunks"][0]["triangles"].items()}
    assert {k: v for k, v in chunk["triangles"].items() if not k.startswith("region_")} == base


def test_export_is_byte_deterministic_with_regions(ws) -> None:
    run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    first = (ws["out"] / "chunks" / "chunk_0_0.glb").read_bytes()
    run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    assert (ws["out"] / "chunks" / "chunk_0_0.glb").read_bytes() == first


def test_region_run_glb_has_one_region_node_per_region(ws, tmp_path, capsys) -> None:
    glb = tmp_path / "out" / "r.glb"
    res = region_run.run_region_run(ws["work"], ["R-0001"], ws["edits"], DEFAULT_RULES, glb_path=glb)
    assert res["glb"] == str(glb) and res["replaces"] == ["R-0001"] and res["regions"] == ["R-0001"]
    scene = trimesh.load(glb, force="scene")
    assert sorted(scene.graph.nodes_geometry) == ["region_R-0001"]
    assert res["triangles"]["R-0001"] == len(scene.geometry[scene.graph["region_R-0001"][1]].faces)


def test_region_run_equals_the_exported_nodes(ws, tmp_path) -> None:
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    baked = {}
    for c in manifest["chunks"]:
        scene = trimesh.load(ws["out"] / c["file"], force="scene")
        for n in scene.graph.nodes_geometry:
            if n.startswith("region_"):
                baked[n] = scene.geometry[scene.graph[n][1]]
    glb = tmp_path / "all.glb"
    res = region_run.run_region_run(ws["work"], [], ws["edits"], DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["R-0001", "R-0002", "R-0003"]
    scene = trimesh.load(glb, force="scene")
    assert sorted(scene.graph.nodes_geometry) == sorted(baked)  # one chunk here: same names
    for n, want in baked.items():
        got = scene.geometry[scene.graph[n][1]]
        assert len(got.faces) == len(want.faces)
        assert np.allclose(got.vertices, want.vertices)


def test_region_run_replaces_overlapping_regions_and_deleted_ids(ws, tmp_path, capsys) -> None:
    edits = tmp_path / "ov.json"
    edits.write_text(json.dumps(doc(**{"R-0001": region(polygon=sq(50, 120, 90, 150)), "R-0002": region(polygon=sq(70, 130, 110, 170)), "R-0003": region(polygon=sq(30, 20, 60, 40))})))
    glb = tmp_path / "g.glb"
    res = region_run.run_region_run(ws["work"], ["R-0002", "R-0009"], edits, DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["R-0001", "R-0002", "R-0009"]  # R-0001 overlaps; R-0009 was deleted
    assert sorted(trimesh.load(glb, force="scene").graph.nodes_geometry) == ["region_R-0001", "region_R-0002"]
    assert "R-0009" in capsys.readouterr().err


def test_region_run_without_regions_writes_a_valid_empty_glb(ws, tmp_path) -> None:
    glb = tmp_path / "e.glb"
    res = region_run.run_region_run(ws["work"], ["R-0001"], tmp_path / "none.json", DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["R-0001"] and res["regions"] == []
    assert not trimesh.load(glb, force="scene").graph.nodes_geometry


def test_cli_region_run(ws, tmp_path, monkeypatch, capsys) -> None:
    monkeypatch.setattr(region_run, "area_paths", lambda area: SimpleNamespace(work=ws["work"]))
    glb = tmp_path / "c.glb"
    assert cli.main(["region-run", "--region", "R-0003", "--edits", str(ws["edits"]), "--glb", str(glb)]) == 0
    out = json.loads(capsys.readouterr().out)
    assert out["replaces"] == ["R-0003"] and out["glb"] == str(glb)
    assert sorted(trimesh.load(glb, force="scene").graph.nodes_geometry) == ["region_R-0003"]
    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps(doc(**{"R-1": region()})))
    assert cli.main(["region-run", "--edits", str(bad)]) == 1
    assert "R-1" in capsys.readouterr().err
    monkeypatch.setattr(region_run, "area_paths", lambda area: SimpleNamespace(work=tmp_path / "missing"))
    assert cli.main(["region-run", "--edits", str(ws["edits"])]) == 1


# --- review fixes ---------------------------------------------------------------------------


def test_road_layers_do_not_depend_on_the_region_bbox() -> None:
    def edge(eid, a, b, pts, width, highway):
        return {"id": eid, "from": a, "to": b, "polyline": pts, "width": width,
                "highway": highway, "surface": "asphalt", "length_m": 100.0, "oneway": False, "osm_id": eid}

    nodes = [{"id": f"N-{k}", "x": x, "y": y} for k, (x, y) in enumerate([(20, 100), (160, 100), (20, 30)], 1)]
    graph = {
        "nodes": nodes,
        "edges": [
            edge("E-1", "N-1", "N-2", [[20, 100], [160, 100]], 3.0, "residential"),  # narrow, in the bbox
            edge("E-2", "N-1", "N-3", [[20, 100], [20, 30]], 10.0, "residential"),  # wide arm, outside it
        ],
    }
    layers = road_layers(graph, make_terrain(), ROADS, (100, 90, 130, 110))
    outline = shapely.union_all([g for g, _ in layers])
    # the junction patch has the widest arm's radius (5 m), not the narrow arm's
    assert outline.contains(shapely.Point(22, 104))


def test_higher_id_is_numeric_for_five_digit_ids() -> None:
    plan = plan_of(**{"R-9999": region(polygon=sq(50, 120, 90, 150)), "R-10000": region("#ff0000", sq(70, 130, 110, 170))})
    assert visible_area(plan, "R-10000").equals(plan.regions["R-10000"].polygon)
    assert visible_area(plan, "R-9999").area == pytest.approx(40 * 30 - 20 * 20)
    assert region_rank("R-10000") > region_rank("R-9999")


def test_terrain_triangles_skip_cells_chunk_mesh_does_not_mesh() -> None:
    t = make_terrain()
    poly = Polygon([(60, 60), (80, 60), (80, 80), (60, 80)])
    full = len(_terrain_triangles(t, poly))
    i = j = int(60 // t.step) + 1
    t.valid[j, i] = False  # a corner shared by four cells
    assert len(_terrain_triangles(t, poly)) == full - 8
    mesh = drape(poly, [1, 2, 3], t, [], RCFG["lift_m"], RCFG["road_lift_m"])
    vx = i * t.step
    hole = Polygon([(vx - t.step + 0.1, vx - t.step + 0.1), (vx + t.step - 0.1, vx - t.step + 0.1),
                    (vx + t.step - 0.1, vx + t.step - 0.1), (vx - t.step + 0.1, vx + t.step - 0.1)])
    assert footprint(mesh).intersection(hole).area < 1e-6


# --- water regions ---------------------------------------------------------------------------


def _wet_setup(level_offset):
    terrain = make_terrain()
    mid = float(terrain.surface(np.array([70.0]), np.array([135.0]))[0])
    plan = plan_regions(parse(doc(**{"R-0001": {**region(), "water_y": round(mid + level_offset, 3)}})), [OX, OY], terrain)
    return terrain, plan


def test_water_y_is_validated() -> None:
    assert validate(doc(**{"R-0001": {**region(), "water_y": 185.6}})) == []
    for bad in ("high", True, float("nan"), None):
        assert validate(doc(**{"R-0001": {**region(), "water_y": bad}})), bad


def test_water_mesh_is_flat_under_the_level_and_follows_the_shore() -> None:
    terrain, plan = _wet_setup(1.0)
    level = plan.regions["R-0001"].water_y
    mesh = region_meshes(plan, terrain, None, ROADS, RCFG)["R-0001"]
    assert np.allclose(mesh.tris[:, :, 2], level - 0.02)
    assert (np.cross(mesh.tris[:, 1] - mesh.tris[:, 0], mesh.tris[:, 2] - mesh.tris[:, 0])[:, 2] > 0).all()
    xy = mesh.tris[:, :, :2].reshape(-1, 2)
    assert (terrain.surface(xy[:, 0], xy[:, 1]) <= level + 0.4).all()  # nowhere on ground well above the level


def test_water_region_becomes_water_bodies_and_dry_levels_are_skipped() -> None:
    from drycastle_pipeline.water import build_water

    terrain, plan = _wet_setup(1.0)
    docw, skipped = build_water([], terrain, plan.regions)
    assert skipped == [] and docw["bodies"]
    assert all(b["bed_y"] < b["surface_y"] for b in docw["bodies"])
    terrain, plan = _wet_setup(-50.0)
    docw, skipped = build_water([], terrain, plan.regions)
    assert docw["bodies"] == [] and skipped == ["R-0001"]
