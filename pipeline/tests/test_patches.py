"""Ground patches (ADR 0022): schema, canonical writer, stale entries, overlaps, draping, export and patch-run."""

import json
from types import SimpleNamespace

import numpy as np
import pytest
import shapely
import trimesh
from shapely.geometry import Polygon

from drycastle_pipeline import cli, patch_run
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
from drycastle_pipeline.ground import ground_types
from drycastle_pipeline.patches import (
    _terrain_triangles,
    drape,
    overlapping,
    patch_meshes,
    patch_rank,
    plan_patches,
    road_layers,
    visible_area,
)
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain, make_workspace

OX, OY = SYNTHETIC_ORIGIN
RCFG, ROADS = load_section("ground_patches"), load_section("roads")
TYPES = ground_types()
MEADOW_RGB = [0x7A, 0xA8, 0x60]  # the first colour of the meadow type


def sq(x0, y0, x1, y1):
    """A rectangle ring in absolute coordinates from local metres."""
    return [[OX + x0, OY + y0], [OX + x1, OY + y0], [OX + x1, OY + y1], [OX + x0, OY + y1]]


def patch(type="meadow", polygon=None):
    return {"type": type, "polygon": polygon or sq(50, 120, 90, 150)}


def doc(**patches):
    return {"version": 1, "ground_patches": patches}


# --- schema ---------------------------------------------------------------------------------

TRIANGLE = [[0, 0], [10, 0], [0, 10]]
BOWTIE = [[0, 0], [10, 10], [10, 0], [0, 10]]


def test_valid_patches() -> None:
    assert validate(doc(**{"GP-0001": patch(), "GP-12345": {"type": "forest", "polygon": TRIANGLE}})) == []
    closed = [*TRIANGLE, TRIANGLE[0]]  # a repeated closing point is tolerated
    assert validate(doc(**{"GP-0001": {"type": "field", "polygon": closed}})) == []
    assert validate({"version": 1}) == []


@pytest.mark.parametrize(
    ("patches", "message"),
    [
        ({"GP-1": patch()}, "ground patch IDs look like GP-0001"),
        ({"G-0001": patch()}, "ground patch IDs look like GP-0001"),
        ({"R-0001": patch()}, "ground patch IDs look like GP-0001"),
        ({"GP-0001": []}, "must be an object"),
        ({"GP-0001": {"polygon": TRIANGLE}}, "ground_patches.GP-0001.type: required"),
        ({"GP-0001": {"type": "swamp", "polygon": TRIANGLE}}, "must be a ground type"),
        ({"GP-0001": {"type": 5, "polygon": TRIANGLE}}, "must be a ground type"),
        ({"GP-0001": {"type": "meadow"}}, "ground_patches.GP-0001.polygon: required"),
        ({"GP-0001": {"type": "meadow", "polygon": "x"}}, "must be a list"),
        ({"GP-0001": {"type": "meadow", "polygon": TRIANGLE[:2]}}, "at least 3 points"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1, 1], [0, 0]]}}, "at least 3 points"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1], [3, 4]]}}, "polygon.1: must be [x, y]"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1, "a"], [3, 4]]}}, "polygon.1"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1, True], [3, 4]]}}, "polygon.1"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1, float("nan")], [3, 4]]}}, "polygon.1"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [1, float("inf")], [3, 4]]}}, "polygon.1"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [0, 0], [5, 0], [0, 5]]}}, "repeated"),
        ({"GP-0001": {"type": "meadow", "polygon": BOWTIE}}, "must not intersect"),
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [5, 0], [10, 0]]}}, "enclose an area"),
        # an edge that touches another edge's vertex
        ({"GP-0001": {"type": "meadow", "polygon": [[0, 0], [10, 0], [10, 10], [5, 0], [0, 10]]}}, "must not"),
        ({"GP-0001": {"type": "meadow", "polygon": TRIANGLE, "z": 1}}, "ground_patches.GP-0001.z: unknown key"),
        ({"GP-0001": {"type": "meadow", "polygon": TRIANGLE, "colour": "#112233"}}, "colour: unknown key"),  # the type has the colour
        ({"GP-0001": {"type": "meadow", "polygon": TRIANGLE, "ground": ["meadow"]}}, "ground: unknown key"),  # no stack
    ],
)
def test_invalid_patches(patches, message) -> None:
    errs = validate(doc(**patches))
    assert any(message in e for e in errs), errs


def test_the_removed_regions_key_is_refused() -> None:
    errs = validate({"version": 1, "regions": {"R-0001": {"colour": "#112233", "polygon": TRIANGLE}}})
    assert any("regions: unknown key" in e for e in errs), errs


def test_patches_must_be_an_object() -> None:
    assert any("ground_patches: must be an object" in e for e in validate({"version": 1, "ground_patches": []}))


def test_patch_errors_are_all_reported() -> None:
    errs = validate(doc(**{"GP-1": {"type": "swamp", "polygon": TRIANGLE[:2], "x": 1}}))
    assert len(errs) == 4


def test_a_patch_type_is_a_type_of_the_rules_or_of_the_edits_and_not_a_deleted_one() -> None:
    mine = {"my_dunes": {"attributes": {"colours": ["#d8c38a"]}}}
    d = {**doc(**{"GP-0001": patch("my_dunes")}), "ground_types": mine}
    assert validate(d) == []  # a type with only colours is valid: it is how a paint colour becomes a type
    assert any("must be a ground type" in e for e in validate(doc(**{"GP-0001": patch("my_dunes")})))
    gone = {**doc(**{"GP-0001": patch("meadow")}), "ground_types": {"meadow": {"deleted": True}}}
    assert any("GP-0001.type" in e for e in validate(gone))


def test_water_y_goes_with_a_water_type_and_only_with_one() -> None:
    assert validate(doc(**{"GP-0001": {**patch("water"), "water_y": 185.6}})) == []
    assert any("water_y: required" in e for e in validate(doc(**{"GP-0001": patch("water")})))
    assert any("water_y: only for a water type" in e for e in validate(doc(**{"GP-0001": {**patch("meadow"), "water_y": 1.0}})))
    for bad in ("high", True, float("nan"), None):
        assert validate(doc(**{"GP-0001": {**patch("water"), "water_y": bad}})), bad
    # a type of your own with the water attribute needs it too
    d = {**doc(**{"GP-0001": patch("pond")}), "ground_types": {"pond": {"attributes": {"colours": ["#336699"], "water": True}}}}
    assert any("water_y: required" in e for e in validate(d))
    # and the rules type loses it when the override removes the attribute
    d = {**doc(**{"GP-0001": {**patch("water"), "water_y": 1.0}}), "ground_types": {"water": {"attributes": {"water": None}}}}
    assert any("water_y: only for a water type" in e for e in validate(d))


# --- zones -------------------------------------------------------------------------------------


def zone(kind="bigfoot", polygon=None):
    return {"kind": kind, "polygon": polygon or sq(50, 120, 90, 150)}


@pytest.mark.parametrize(
    ("zones", "message"),
    [
        ({"Z-1": zone()}, "zone IDs look like Z-0001"),
        ({"GP-0001": zone()}, "zone IDs look like Z-0001"),
        ({"Z-0001": zone("yeti")}, "must be a zone kind"),
        ({"Z-0001": {"polygon": TRIANGLE}}, "zones.Z-0001.kind: required"),
        ({"Z-0001": {"kind": "deer"}}, "zones.Z-0001.polygon: required"),
        ({"Z-0001": zone("deer", BOWTIE)}, "must not intersect"),
        ({"Z-0001": {"kind": "deer", "polygon": TRIANGLE, "colour": "#112233"}}, "colour: unknown key"),
        ({"Z-0001": []}, "must be an object"),
    ],
)
def test_invalid_zones(zones, message) -> None:
    errs = validate({"version": 1, "zones": zones})
    assert any(message in e for e in errs), errs


@pytest.mark.parametrize(
    ("cuts", "message"),
    [
        ({"ZC-1": zone()}, "zone cut IDs look like ZC-0001"),
        ({"Z-0001": zone()}, "zone cut IDs look like ZC-0001"),
        ({"ZC-0001": zone("yeti")}, "zone_cuts.ZC-0001.kind: must be a zone kind"),
        ({"ZC-0001": {"polygon": TRIANGLE}}, "zone_cuts.ZC-0001.kind: required"),
        ({"ZC-0001": {"kind": "deer"}}, "zone_cuts.ZC-0001.polygon: required"),
        ({"ZC-0001": zone("deer", BOWTIE)}, "must not intersect"),
    ],
)
def test_invalid_zone_cuts(cuts, message) -> None:
    assert any(message in e for e in validate({"version": 1, "zone_cuts": cuts}))


def test_valid_zone_cuts_round_trip_and_are_only_written_when_there_are_any() -> None:
    doc = {"version": 1, "parcels": {}, "groups": {}, "zone_cuts": {"ZC-0001": zone("deer", TRIANGLE)}}
    assert validate(doc) == []
    assert json.loads(dump_text(parse(doc)))["zone_cuts"] == doc["zone_cuts"]
    assert '"zone_cuts"' not in dump_text(Edits())


def test_valid_zones_overlap_freely() -> None:
    zones = {"Z-0001": zone("bigfoot"), "Z-0002": zone("deer"), "Z-0003": zone("player_spawn"), "Z-0004": zone("deer")}
    assert validate({"version": 1, "zones": zones}) == []


# --- canonical writer -----------------------------------------------------------------------


def test_round_trip_is_byte_stable(tmp_path) -> None:
    src = tmp_path / "hand.json"
    hand = {**doc(**{"GP-0012": patch("forest", TRIANGLE), "GP-0002": patch()}), "zones": {"Z-0003": zone("deer", TRIANGLE)}}
    hand["parcels"] = {"P-0001": {"fence": {"mode": "none"}}}
    src.write_text(json.dumps(hand, indent=4))
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    dump_edits(load_edits(src), first)
    dump_edits(load_edits(first), second)
    assert first.read_bytes() == second.read_bytes()
    assert first.read_text() == dump_text(load_edits(src))
    written = json.loads(first.read_text())
    assert written["ground_patches"] == hand["ground_patches"] and written["zones"] == hand["zones"]
    text = first.read_text()
    assert text.index('"GP-0002"') < text.index('"GP-0012"')  # sorted keys


def test_files_without_patches_and_zones_keep_their_bytes() -> None:
    assert '"ground_patches"' not in dump_text(Edits()) and '"zones"' not in dump_text(Edits())
    assert dump_text(Edits()) == '{\n "groups": {},\n "parcels": {},\n "version": 1\n}\n'


def test_dump_refuses_invalid_patches(tmp_path) -> None:
    with pytest.raises(EditsError):
        dump_edits(Edits(ground_patches={"GP-0001": {"type": "meadow", "polygon": BOWTIE}}), tmp_path / "e.json")
    assert not (tmp_path / "e.json").exists()


def test_parse_keeps_patches_in_order_of_the_document() -> None:
    parsed = parse(doc(**{"GP-0001": patch(), "GP-0002": patch("forest")}))
    assert list(parsed.ground_patches) == ["GP-0001", "GP-0002"]


# --- planning and overlap -------------------------------------------------------------------


def plan_of(**patches):
    return plan_patches(parse(doc(**patches)), list(SYNTHETIC_ORIGIN), make_terrain(), TYPES)


def test_stale_patches_warn_and_are_skipped() -> None:
    plan = plan_of(
        **{
            "GP-0001": patch(),
            "GP-0002": patch(polygon=sq(1000, 1000, 1100, 1100)),  # off the terrain
            "GP-0003": patch(polygon=sq(-100, -100, 5, 5)),  # off the terrain
        }
    )
    assert list(plan.patches) == ["GP-0001"]
    assert len(plan.warnings) == 2
    assert all("outside the terrain coverage" in w for w in plan.warnings)
    assert {w.split(":")[0] for w in plan.warnings} == {"ground_patches.GP-0002", "ground_patches.GP-0003"}


def test_a_patch_is_in_the_first_colour_of_its_type() -> None:
    plan = plan_of(**{"GP-0001": patch("meadow"), "GP-0002": patch("forest", sq(120, 120, 150, 150))})
    assert plan.patches["GP-0001"].colour == MEADOW_RGB
    assert plan.patches["GP-0002"].colour == [0x3B, 0x7A, 0x3A]
    assert plan.patches["GP-0002"].type == "forest"


def test_a_patch_of_an_unknown_type_is_skipped_with_a_warning() -> None:
    plan = plan_patches(parse(doc(**{"GP-0001": patch()})), list(SYNTHETIC_ORIGIN), make_terrain(), {"forest": {}})
    assert not plan.patches and "unknown ground type" in plan.warnings[0]


def test_patch_partly_outside_the_coverage_is_clipped() -> None:
    plan = plan_of(**{"GP-0001": patch(polygon=sq(-50, 120, 90, 150))})
    assert plan.warnings == []
    assert plan.patches["GP-0001"].polygon.bounds[0] == pytest.approx(10)


def test_no_terrain_skips_with_a_warning_and_no_patches_need_no_origin() -> None:
    plan = plan_patches(parse(doc(**{"GP-0001": patch()})), list(SYNTHETIC_ORIGIN), None, TYPES)
    assert not plan.patches and "no terrain" in plan.warnings[0]
    assert plan_patches(Edits(), None, None, TYPES).patches == {}
    with pytest.raises(ValueError, match="origin"):
        plan_patches(parse(doc(**{"GP-0001": patch()})), None, make_terrain(), TYPES)


def test_higher_id_wins_where_patches_overlap() -> None:
    plan = plan_of(
        **{"GP-0001": patch(polygon=sq(50, 120, 90, 150)), "GP-0002": patch("forest", sq(70, 130, 110, 170))}
    )
    low, high = visible_area(plan, "GP-0001"), visible_area(plan, "GP-0002")
    assert high.equals(plan.patches["GP-0002"].polygon)
    assert low.area == pytest.approx(40 * 30 - 20 * 20)
    assert low.intersection(plan.patches["GP-0002"].polygon).area < 1e-9
    assert overlapping(plan, {"GP-0002"}) == {"GP-0001", "GP-0002"}
    assert overlapping(plan_of(**{"GP-0001": patch(), "GP-0002": patch(polygon=sq(120, 120, 150, 150))}), {"GP-0001"}) == {
        "GP-0001"
    }


def test_overlap_meshes_never_cover_the_same_ground() -> None:
    plan = plan_of(
        **{"GP-0001": patch(polygon=sq(50, 120, 90, 150)), "GP-0002": patch("forest", sq(70, 130, 110, 170))}
    )
    meshes = patch_meshes(plan, make_terrain(), None, ROADS, RCFG)
    shapes = {r: footprint(m) for r, m in meshes.items()}
    assert shapes["GP-0001"].intersection(shapes["GP-0002"]).area < 1e-6
    assert shapes["GP-0001"].area == pytest.approx(40 * 30 - 20 * 20, rel=1e-6)
    assert shapes["GP-0002"].area == pytest.approx(40 * 40, rel=1e-6)


def test_a_patch_fully_under_a_higher_one_has_no_mesh() -> None:
    plan = plan_of(**{"GP-0001": patch(polygon=sq(60, 125, 70, 135)), "GP-0002": patch(polygon=sq(50, 120, 90, 150))})
    assert list(patch_meshes(plan, make_terrain(), None, ROADS, RCFG)) == ["GP-0002"]


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


def test_patch_over_a_road_floats_above_the_ribbon() -> None:
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


# --- export and patch-run -------------------------------------------------------------------


def _water_level() -> float:
    return round(float(make_terrain().surface(np.array([70.0]), np.array([135.0]))[0]) + 1.0, 3)


EDITS = doc(
    **{
        "GP-0001": patch("meadow", sq(50, 120, 90, 150)),
        "GP-0002": patch("forest", sq(60, 95, 120, 118)),  # over the road
        "GP-0003": patch("meadow", sq(30, 20, 60, 40)),
        "GP-0004": patch("meadow", sq(2000, 2000, 2100, 2100)),  # stale
        "GP-0005": {**patch("water", sq(55, 125, 85, 145)), "water_y": _water_level()},
    }
)


@pytest.fixture
def ws(tmp_path):
    full = make_workspace(tmp_path)
    full["edits"] = tmp_path / "edits.json"
    full["edits"].write_text(json.dumps(EDITS))
    return full


def _nodes(manifest, out):
    names = {}
    for c in manifest["chunks"]:
        scene = trimesh.load(out / c["file"], force="scene")
        names[tuple(c["index"])] = set(scene.graph.nodes_geometry)
    return names


def test_export_bakes_the_water_patches_as_nodes_and_writes_patches_json_for_the_rest(ws, capsys) -> None:
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    assert "patches" in manifest["generated"]
    assert "GP-0004" in capsys.readouterr().err
    names = _nodes(manifest, ws["out"])
    # the other patches are ground (baked by the parcels stage), not nodes
    assert {n for ns in names.values() for n in ns if n.startswith("patch_")} == {"patch_GP-0005"}
    chunk = next(c for c in manifest["chunks"] if c["index"] == [0, 0])
    assert chunk["triangles"]["patch_GP-0005"] > 0
    # the minimap file: every patch with terrain under it but the water one, in ID order, in the colour of its type
    assert manifest["files"]["patches"] == "patches.json"
    pj = json.loads((ws["out"] / "patches.json").read_text())
    assert pj["version"] == 1
    assert [(p["id"], p["type"]) for p in pj["patches"]] == [("GP-0001", "meadow"), ("GP-0002", "forest"), ("GP-0003", "meadow")]
    assert pj["patches"][0]["colour"] == MEADOW_RGB and len(pj["patches"][0]["ring"]) >= 4
    # no effect on anything else: same chunk without patches has the same other nodes
    other = ws["out"].parent / "out2"
    plain = run_export(ws["work"], other, ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["raw"] / "none.json")
    assert "patches" not in plain["generated"] and "patches" not in plain["files"]
    assert not (other / "patches.json").exists()
    base = {k: v for k, v in plain["chunks"][0]["triangles"].items()}
    got = {k: v for k, v in chunk["triangles"].items() if not k.startswith("patch_")}
    # the ground of a water body is collided with at the full grid (the carved bed is what is waded over), not decimated
    assert got.pop("terrain_collision") >= base.pop("terrain_collision")
    assert got == base


def test_a_water_patch_is_a_water_body(ws) -> None:
    run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    bodies = json.loads((ws["out"] / "water.json").read_text())["bodies"]
    mine = [b for b in bodies if b["id"].startswith("GP-0005")]
    assert mine and all(b["surface_y"] == pytest.approx(_water_level()) and b["bed_y"] < b["surface_y"] for b in mine)


def test_export_is_byte_deterministic_with_patches(ws) -> None:
    run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    first = (ws["out"] / "chunks" / "chunk_0_0.glb").read_bytes()
    run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    assert (ws["out"] / "chunks" / "chunk_0_0.glb").read_bytes() == first


def test_patch_run_glb_has_one_patch_node_per_patch(ws, tmp_path, capsys) -> None:
    glb = tmp_path / "out" / "p.glb"
    res = patch_run.run_patch_run(ws["work"], ["GP-0003"], ws["edits"], DEFAULT_RULES, glb_path=glb)
    assert res["glb"] == str(glb) and res["replaces"] == ["GP-0003"] and res["patches"] == ["GP-0003"]
    scene = trimesh.load(glb, force="scene")
    assert sorted(scene.graph.nodes_geometry) == ["patch_GP-0003"]
    assert res["triangles"]["GP-0003"] == len(scene.geometry[scene.graph["patch_GP-0003"][1]].faces)


def test_patch_run_equals_the_exported_water_nodes(ws, tmp_path) -> None:
    manifest = run_export(ws["work"], ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"], edits_path=ws["edits"])
    baked = {}
    for c in manifest["chunks"]:
        scene = trimesh.load(ws["out"] / c["file"], force="scene")
        for n in scene.graph.nodes_geometry:
            if n.startswith("patch_"):
                baked[n] = scene.geometry[scene.graph[n][1]]
    glb = tmp_path / "all.glb"
    res = patch_run.run_patch_run(ws["work"], [], ws["edits"], DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["GP-0001", "GP-0002", "GP-0003", "GP-0005"]
    scene = trimesh.load(glb, force="scene")
    assert set(baked) <= set(scene.graph.nodes_geometry)
    for n, want in baked.items():
        got = scene.geometry[scene.graph[n][1]]
        assert len(got.faces) == len(want.faces)
        assert np.allclose(got.vertices, want.vertices)


def test_patch_run_replaces_overlapping_patches_and_deleted_ids(ws, tmp_path, capsys) -> None:
    edits = tmp_path / "ov.json"
    edits.write_text(json.dumps(doc(**{"GP-0001": patch(polygon=sq(50, 120, 90, 150)), "GP-0002": patch(polygon=sq(70, 130, 110, 170)), "GP-0003": patch(polygon=sq(30, 20, 60, 40))})))
    glb = tmp_path / "g.glb"
    res = patch_run.run_patch_run(ws["work"], ["GP-0002", "GP-0009"], edits, DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["GP-0001", "GP-0002", "GP-0009"]  # GP-0001 overlaps; GP-0009 was deleted
    assert sorted(trimesh.load(glb, force="scene").graph.nodes_geometry) == ["patch_GP-0001", "patch_GP-0002"]
    assert "GP-0009" in capsys.readouterr().err


def test_patch_run_without_patches_writes_a_valid_empty_glb(ws, tmp_path) -> None:
    glb = tmp_path / "e.glb"
    res = patch_run.run_patch_run(ws["work"], ["GP-0001"], tmp_path / "none.json", DEFAULT_RULES, glb_path=glb)
    assert res["replaces"] == ["GP-0001"] and res["patches"] == []
    assert not trimesh.load(glb, force="scene").graph.nodes_geometry


def test_cli_patch_run(ws, tmp_path, monkeypatch, capsys) -> None:
    monkeypatch.setattr(patch_run, "map_paths", lambda *_a, **_k: SimpleNamespace(work=ws["work"]))
    glb = tmp_path / "c.glb"
    assert cli.main(["patch-run", "--patch", "GP-0003", "--edits", str(ws["edits"]), "--glb", str(glb)]) == 0
    out = json.loads(capsys.readouterr().out)
    assert out["replaces"] == ["GP-0003"] and out["glb"] == str(glb)
    assert sorted(trimesh.load(glb, force="scene").graph.nodes_geometry) == ["patch_GP-0003"]
    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps(doc(**{"GP-1": patch()})))
    assert cli.main(["patch-run", "--edits", str(bad)]) == 1
    assert "GP-1" in capsys.readouterr().err
    monkeypatch.setattr(patch_run, "map_paths", lambda *_a, **_k: SimpleNamespace(work=tmp_path / "missing"))
    assert cli.main(["patch-run", "--edits", str(ws["edits"])]) == 1


# --- review fixes ---------------------------------------------------------------------------


def test_road_layers_do_not_depend_on_the_patch_bbox() -> None:
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
    plan = plan_of(**{"GP-9999": patch(polygon=sq(50, 120, 90, 150)), "GP-10000": patch("forest", sq(70, 130, 110, 170))})
    assert visible_area(plan, "GP-10000").equals(plan.patches["GP-10000"].polygon)
    assert visible_area(plan, "GP-9999").area == pytest.approx(40 * 30 - 20 * 20)
    assert patch_rank("GP-10000") > patch_rank("GP-9999")


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


# --- water patches ---------------------------------------------------------------------------


def _wet_setup(level_offset):
    terrain = make_terrain()
    mid = float(terrain.surface(np.array([70.0]), np.array([135.0]))[0])
    plan = plan_patches(
        parse(doc(**{"GP-0001": {**patch("water"), "water_y": round(mid + level_offset, 3)}})), [OX, OY], terrain, TYPES
    )
    return terrain, plan


def test_water_mesh_lies_on_the_bed_and_follows_the_shore() -> None:
    terrain, plan = _wet_setup(1.0)
    level = plan.patches["GP-0001"].water_y
    mesh = patch_meshes(plan, terrain, None, ROADS, RCFG)["GP-0001"]
    assert (np.cross(mesh.tris[:, 1] - mesh.tris[:, 0], mesh.tris[:, 2] - mesh.tris[:, 0])[:, 2] > 0).all()
    xy = mesh.tris[:, :, :2].reshape(-1, 2)
    assert (terrain.surface(xy[:, 0], xy[:, 1]) <= level + 0.4).all()  # nowhere on ground well above the level
    # painted on the ground (a hair above it), not hanging at the surface where the water shader would take it for the bed
    ground = terrain.surface(xy[:, 0], xy[:, 1])
    assert np.allclose(mesh.tris[:, :, 2].ravel(), ground + RCFG["lift_m"], atol=0.02)
    assert (mesh.tris[:, :, 2] < level - 0.1).any()


def test_water_patch_becomes_water_bodies_and_dry_levels_are_skipped() -> None:
    from drycastle_pipeline.water import plan_water, water_rules

    rules = water_rules()
    terrain, plan = _wet_setup(1.0)
    water = plan_water([], terrain, plan.patches, rules)
    assert water.skipped == [] and water.bodies
    assert all(b.bed_y < b.surface_y for b in water.bodies)
    terrain, plan = _wet_setup(-50.0)
    water = plan_water([], terrain, plan.patches, rules)
    assert water.bodies == [] and water.skipped == ["GP-0001"]
