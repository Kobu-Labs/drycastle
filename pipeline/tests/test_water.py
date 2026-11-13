"""water.json (version 2): natural beds from the outlines, the terrain carved to them, and the document."""

import base64
import json
import math

import numpy as np
import pytest
from shapely.geometry import Point, Polygon, box

from drycastle_pipeline.bed import DepthGrid, carve_terrain, depth_grid
from drycastle_pipeline.regions import plan_regions
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.water import (
    WATER_FORMAT_VERSION,
    plan_water,
    shore_surface,
    water_doc,
    water_rules,
)
from tests.synthetic import make_terrain, parcel, plane
from tests.test_regions import doc, region

RIVER = box(60, 20, 84, 190)  # 24 m wide, 170 m long, flows along y
POND = box(120, 60, 150, 90)


def rules() -> dict:
    return water_rules(DEFAULT_RULES)


def bend_band() -> Polygon:
    """A quarter-turn band of water: its outer bank is the big arc, its inner bank the small one."""
    def arc(r, a0, a1, n=120):
        return [(r * math.cos(a), r * math.sin(a)) for a in np.linspace(a0, a1, n)]

    return Polygon(arc(90, 0, math.pi / 2) + arc(60, math.pi / 2, 0)).buffer(0)


def wet_interior(grid: DepthGrid, geom: Polygon) -> np.ndarray:
    """Depth values of the grid cells whose centres lie inside the outline."""
    xs = grid.x0 + (np.arange(grid.cols) + 0.5) * grid.cell_m
    ys = grid.y0 + (np.arange(grid.rows) + 0.5) * grid.cell_m
    gx, gy = np.meshgrid(xs, ys)
    from shapely import contains_xy

    inside = contains_xy(geom, gx.ravel(), gy.ravel())
    return grid.depth.ravel()[inside]


# --- bed generation --------------------------------------------------------------------------


def test_bed_is_deterministic_and_seeded_by_the_body_id() -> None:
    r = rules()
    a = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    b = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    assert a.encode() == b.encode()
    c = depth_grid(RIVER, "P-0534", r["river"], True, r["bed"])
    assert a.encode() != c.encode()  # another body, another pattern of pools and riffles


def test_river_depths_stay_in_range_and_have_pools_and_riffles() -> None:
    r = rules()
    grid = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    d = wet_interior(grid, RIVER)
    assert d.min() >= r["bed"]["min_depth_m"] - 1e-9
    assert d.max() <= r["river"]["pool_depth_m"] + 0.01
    assert d.max() > 2.8  # pools
    assert d.min() < 1.0  # riffles, wadeable
    assert 1.5 < np.median(d) < 3.0  # mid-channel about 2.5 m


def test_depth_is_zero_outside_and_grows_from_the_bank() -> None:
    r = rules()
    grid = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    outside = grid.depth.copy()
    xs = grid.x0 + (np.arange(grid.cols) + 0.5) * grid.cell_m
    ys = grid.y0 + (np.arange(grid.rows) + 0.5) * grid.cell_m
    gx, gy = np.meshgrid(xs, ys)
    from shapely import contains_xy

    assert (outside[~contains_xy(RIVER, gx, gy)] == 0).all()
    # banks shelve: a cell next to the bank is shallower than one in the middle of the channel
    bank_cell = grid.depth_at(np.array([61.0]), np.array([100.0]))[0]
    centre = grid.depth_at(np.array([72.0]), np.array([100.0]))[0]
    assert bank_cell < centre


def test_outer_bend_is_deeper_and_steeper_than_inner() -> None:
    r = rules()
    band = bend_band()
    grid = depth_grid(band, "R-9001", r["river"], True, r["bed"])
    from shapely import contains_xy

    xs = grid.x0 + (np.arange(grid.cols) + 0.5) * grid.cell_m
    ys = grid.y0 + (np.arange(grid.rows) + 0.5) * grid.cell_m
    gx, gy = np.meshgrid(xs, ys)
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(band, gx, gy)
    dist = shapely_distance(gx, gy, band.exterior)
    radius = np.hypot(gx, gy)

    def strip(lo, hi, outer: bool) -> float:
        side = (radius > 75) if outer else (radius < 75)  # the outer arc has radius 90, the inner 60
        sel = inside & side & (dist >= lo) & (dist <= hi)
        return float(grid.depth.ravel()[sel].mean())

    assert strip(0.5, 2.0, outer=True) > strip(0.5, 2.0, outer=False) + 0.03  # steeper on the outside
    assert strip(4.0, 8.0, outer=True) > strip(4.0, 8.0, outer=False) + 0.2  # and deeper in the channel


def shapely_distance(x, y, geom):
    import shapely

    return shapely.distance(shapely.points(x, y), geom)


def test_ponds_are_a_shallow_bowl() -> None:
    r = rules()
    grid = depth_grid(POND, "P-0922", r["pond"], False, r["bed"])
    d = wet_interior(grid, POND)
    assert d.max() <= r["pond"]["max_depth_m"] * (1 + r["pond"]["pattern_gain"]) + 0.01
    assert d.max() > 1.2
    assert d.min() >= r["bed"]["min_depth_m"] - 1e-9


def test_max_depth_scales_the_bed_to_its_deepest_point() -> None:
    r = rules()
    grid = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"], max_depth_m=2.0)
    assert grid.max_depth() == pytest.approx(2.0, abs=0.01)
    deep = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    assert grid.max_depth() < deep.max_depth()


def test_depth_grid_round_trips_through_the_compact_encoding() -> None:
    r = rules()
    grid = depth_grid(RIVER, "P-0901", r["river"], True, r["bed"])
    back = DepthGrid.decode(grid.encode(), grid.cols, grid.rows)
    assert np.allclose(back, grid.depth, atol=0.005)


# --- surface, plan and document --------------------------------------------------------------


def test_surface_is_shoreline_median() -> None:
    terrain = make_terrain()
    surface = shore_surface(box(60, 60, 100, 90), terrain)
    assert abs(surface - plane(80, 75)[0]) < 0.3


def test_plan_keeps_only_water_parcels_sorted_by_id_and_reports_skips() -> None:
    terrain = make_terrain()
    parcels = [
        parcel("P-0009", "water", box(120, 120, 150, 140)),
        parcel("P-0002", "garden", box(20, 20, 40, 40)),
        parcel("P-0004", "water", box(60, 60, 100, 90)),
        parcel("P-0007", "water", box(300, 300, 320, 320)),  # outside the terrain coverage
    ]
    plan = plan_water(parcels, terrain, None, rules())
    assert [b.id for b in plan.bodies] == ["P-0004", "P-0009"]
    assert plan.skipped == ["P-0007"]


def test_water_doc_is_version_2_with_a_depth_grid_per_body() -> None:
    terrain = make_terrain()
    r = rules()
    plan = plan_water([parcel("P-0901", "water", RIVER), parcel("P-0922", "water", POND)], terrain, None, r)
    doc_v2 = water_doc(plan.bodies, r)
    assert doc_v2["version"] == WATER_FORMAT_VERSION == 2
    assert [b["id"] for b in doc_v2["bodies"]] == ["P-0901", "P-0922"]
    river = doc_v2["bodies"][0]
    assert river["bed_y"] == pytest.approx(river["surface_y"] - river["max_depth_m"], abs=0.001)
    assert river["flow"] == []
    g = river["depth"]
    assert g["cell_m"] == 2.0
    cm = np.frombuffer(base64.b64decode(g["cm"]), dtype="<u2")
    assert cm.size == g["cols"] * g["rows"]
    assert cm.max() / 100.0 == pytest.approx(river["max_depth_m"], abs=0.01)
    assert river["polygon"]["type"] == "Polygon"
    json.dumps(doc_v2)  # plain JSON


def test_region_water_bodies_get_their_max_depth_and_dry_levels_are_skipped() -> None:
    terrain = make_terrain()
    r = rules()
    mid = float(terrain.surface(np.array([70.0]), np.array([135.0]))[0])

    def regions(level: float, max_depth: float | None = None):
        entry = {**region(), "water_y": round(mid + level, 3)}
        if max_depth is not None:
            entry["max_depth"] = max_depth
        return plan_regions(_parse(doc(**{"R-0001": entry})), [OX, OY], terrain).regions

    plan = plan_water([], terrain, regions(1.0, max_depth=0.9), r)
    assert plan.skipped == [] and plan.bodies
    assert all(b.bed_y < b.surface_y for b in plan.bodies)
    assert plan.bodies[0].max_depth_m == pytest.approx(0.9, abs=0.01)
    assert "R-0001" in plan.wet
    dry = plan_water([], terrain, regions(-50.0), r)
    assert dry.bodies == [] and dry.skipped == ["R-0001"]


OX, OY = -500000.0, -1100000.0  # the synthetic origin (see tests/synthetic.py)


def _parse(d):
    from drycastle_pipeline.edits import parse

    return parse(d)


def test_max_depth_is_validated_only_with_water() -> None:
    from drycastle_pipeline.edits import validate

    assert validate(doc(**{"R-0001": {**region(), "water_y": 185.6, "max_depth": 2.0}})) == []
    assert validate(doc(**{"R-0001": {**region(), "water_y": 185.6, "max_depth": 0}}))
    assert validate(doc(**{"R-0001": {**region(), "water_y": 185.6, "max_depth": 25}}))
    assert validate(doc(**{"R-0001": {**region(), "max_depth": 2.0}}))  # no water_y: no depth


# --- carve -----------------------------------------------------------------------------------


def _carved(bodies_geoms, blend=4.0):
    terrain = make_terrain()
    r = rules()
    plan = plan_water([parcel(f"P-09{i:02d}", "water", g) for i, g in enumerate(bodies_geoms, 1)], terrain, None, r)
    carve_terrain(terrain, plan.bodies, blend)
    return terrain, plan.bodies


def test_carve_puts_the_bed_under_the_surface() -> None:
    terrain, bodies = _carved([RIVER])
    body = bodies[0]
    centre = Point(72, 100)
    # the mid channel sits at surface minus depth (4 m grid, bilinear: a few centimetres of slack)
    expected = body.surface_y - float(body.grid.depth_at(np.array([72.0]), np.array([100.0]))[0])
    assert float(terrain.surface(np.array([centre.x]), np.array([centre.y]))[0]) == pytest.approx(expected, abs=0.25)
    assert float(terrain.surface(np.array([72.0]), np.array([100.0]))[0]) < body.surface_y - 0.5


def test_carve_blends_the_shore_and_leaves_far_ground_alone() -> None:
    raw = make_terrain()
    terrain, bodies = _carved([RIVER], blend=4.0)
    surface = bodies[0].surface_y
    # far from the river nothing changes
    far_x, far_y = np.array([180.0]), np.array([40.0])
    assert terrain.surface(far_x, far_y)[0] == pytest.approx(raw.surface(far_x, far_y)[0], abs=1e-6)
    # the grid vertices on the outside of the bank (x = 56 is 4 m out, x = 52 is beyond the blend): the blend never
    # raises them, and a bank standing above the surface comes down to it at the rim
    xs, ys = raw.vertex_xy()
    outside = (xs < 60) & (xs > 50) & (np.abs(ys - 100) < 4) & raw.valid
    assert (terrain.heights[outside] <= raw.heights[outside] + 1e-6).all()
    x56 = (xs == 56) & (np.abs(ys - 100) < 4) & raw.valid
    assert np.allclose(terrain.heights[x56], raw.heights[x56])  # the blend ends 4 m out
    # the bank does not step: the ground either side of the outline is close
    edge_out = float(terrain.surface(np.array([58.0]), np.array([100.0]))[0])
    edge_in = float(terrain.surface(np.array([62.0]), np.array([100.0]))[0])
    assert abs(edge_out - edge_in) < 1.2
    assert float(terrain.surface(np.array([60.0]), np.array([100.0]))[0]) <= surface + 0.3


def test_carve_does_not_raise_low_ground_outside() -> None:
    raw = make_terrain()
    terrain, _ = _carved([RIVER])
    xs, ys = raw.vertex_xy()
    outside = (np.abs(xs - 72) > 20) & raw.valid
    assert (terrain.heights[outside] <= raw.heights[outside] + 1e-6).all()


# --- export ----------------------------------------------------------------------------------


def test_export_writes_water_json_v2_and_manifest_entry(tmp_path) -> None:
    from drycastle_pipeline.export import run_export
    from tests.synthetic import make_workspace

    ws = make_workspace(tmp_path)
    doc_p = json.loads((ws["work"] / "parcels.json").read_text())
    doc_p["parcels"].append(parcel("P-0099", "water", box(60, 60, 100, 90)))
    (ws["work"] / "parcels.json").write_text(json.dumps(doc_p))
    out = tmp_path / "out"
    m = run_export(ws["work"], out, ws["raw"], DEFAULT_RULES, ws["overrides"])
    assert m["files"]["water"] == "water.json"
    assert "water" in m["generated"]
    water = json.loads((out / "water.json").read_text())
    assert water["version"] == 2
    assert [b["id"] for b in water["bodies"]] == ["P-0099"]
    body = water["bodies"][0]
    assert body["bed_y"] < body["surface_y"]
    assert body["depth"]["cols"] * body["depth"]["rows"] * 2 == len(base64.b64decode(body["depth"]["cm"]))


def test_river_id_list_is_a_rules_setting() -> None:
    section = load_section("water", DEFAULT_RULES)
    assert "P-0901" in section["river_ids"] and "P-1596" in section["river_ids"]
