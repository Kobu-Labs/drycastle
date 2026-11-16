"""Ground detail prototype: seeded and deterministic, and nothing lands where it must not."""

import copy
import math

import numpy as np
import pytest
import shapely
from shapely.geometry import LineString, Point, Polygon, box
from shapely.ops import unary_union

from drycastle_pipeline import detail_meshes
from drycastle_pipeline.detail import (
    _hollow,
    MOSS_SALT,
    PATCH_PRIORITY,
    ParcelContext,
    area_type,
    cap_clutter,
    field_detail,
    forest_detail,
    free_area,
    garden_detail,
    patch_meshes,
    patch_rows,
    patches_by_chunk,
    poisson_disc,
    thin,
)
from drycastle_pipeline.meadow import clump
from drycastle_pipeline.props import seeded
from drycastle_pipeline.rules import load_section
from drycastle_pipeline.terrain import Terrain


def flat_terrain() -> Terrain:
    n = 8
    return Terrain(
        chunk_m=64.0, n=n, i0=-8, j0=-8,
        heights=np.full((17, 17), 100.0), valid=np.ones((17, 17), bool),
        coverage=box(-64, -64, 64, 64),
    )


def yard() -> ParcelContext:
    return ParcelContext(
        id="P-9999", kind="building_plot",
        polygon=box(0, 0, 30, 24),
        buildings=[box(10, 8, 18, 14)],
        fences=[LineString([(0, 0), (30, 0)]), LineString([(30, 0), (30, 24)])],
        trees=[(5.0, 18.0, 1.0)],
        road=box(-5, 24.5, 35, 30),
        terrain=flat_terrain(),
        tree_cfg=load_section("props")["trees"],
    )


@pytest.fixture(scope="module")
def cfg() -> dict:
    return load_section("detail")


def test_poisson_disc_keeps_distance_and_stays_inside() -> None:
    area = box(0, 0, 10, 6).difference(box(4, 2, 6, 4))
    pts = poisson_disc(seeded("t:pd"), area, 0.8)
    assert len(pts) > 30
    assert all(area.contains(Point(p)) for p in pts)
    d = np.linalg.norm(np.array(pts)[:, None] - np.array(pts)[None], axis=2) + np.eye(len(pts)) * 9
    assert d.min() >= 0.8 - 1e-9
    kept = thin(seeded("t:thin"), pts, lambda xs, ys: np.full(len(xs), 0.5))
    assert 0 < len(kept) < len(pts)


def test_same_parcel_gives_identical_output(cfg) -> None:
    a, b = garden_detail(yard(), cfg), garden_detail(yard(), cfg)
    assert a.clutter == b.clutter and a.clutter
    assert [p.geom.wkb for p in a.patches] == [p.geom.wkb for p in b.patches]
    other = yard()
    other.id = "P-9998"
    assert garden_detail(other, cfg).clutter != a.clutter  # the seed is the parcel


def test_nothing_on_buildings_fences_trunks_or_the_road(cfg) -> None:
    ctx = yard()
    res = garden_detail(ctx, cfg)
    assert sum(res.counts().values()) > 60
    solid = free_area(ctx, cfg)
    for c in res.clutter:
        assert solid.buffer(1e-6).contains(Point(c["x"], c["y"])), c
    for p in res.patches:
        assert p.geom.difference(solid.buffer(1e-6)).area < 1e-6
        assert p.geom.intersection(box(10, 8, 18, 14)).area < 1e-6


def test_patches_do_not_overlap_and_drape(cfg) -> None:
    ctx = yard()
    res = garden_detail(ctx, cfg)
    assert {p.layer for p in res.patches} <= set(PATCH_PRIORITY)
    for i, a in enumerate(res.patches):
        for b in res.patches[i + 1:]:
            assert a.geom.intersection(b.geom).area < 1e-6
    flat, tex = patch_meshes(res, ctx.terrain, cfg["lift_m"])
    assert flat.face_count and tex
    assert np.allclose(flat.tris[:, :, 2], 100.0 + cfg["lift_m"])
    for m in tex.values():
        assert m.uvs.shape == (m.face_count, 3, 2)


def test_gravel_path_needs_a_gate(cfg) -> None:
    ctx = yard()
    ctx.fences = [LineString([(0, 0), (30, 0), (30, 24), (0, 24), (0, 0)])]  # fenced all round, no gap
    assert not any(p.layer == "gravel" for p in garden_detail(ctx, cfg).patches)
    ctx.fences = [LineString([(0, 0), (30, 0), (30, 24), (22, 24)]), LineString([(8, 24), (0, 24), (0, 0)])]  # a gate gap
    assert any(p.layer == "gravel" for p in garden_detail(ctx, cfg).patches)


def test_field_and_forest_keep_off_blocked_ground(cfg) -> None:
    for recipe, kind in ((field_detail, "other"), (forest_detail, "forest")):
        ctx = yard()
        ctx.kind, ctx.polygon, ctx.fences, ctx.buildings, ctx.road = kind, box(0, 0, 60, 60), [], [], None
        ctx.trees = [(20.0, 20.0, 1.0), (40.0, 30.0, 1.0)]
        ctx.blocked = box(10, 10, 30, 30)  # a placed object or a region
        res = recipe(ctx, cfg)
        assert sum(res.counts().values()) > 20
        assert all(not ctx.blocked.contains(Point(c["x"], c["y"])) for c in res.clutter)
        assert all(p.geom.intersection(ctx.blocked).area < 1e-6 for p in res.patches)
        assert recipe(ctx, cfg).clutter == res.clutter  # seeded


def forest_with_road() -> ParcelContext:
    ctx = yard()
    ctx.kind, ctx.polygon, ctx.fences, ctx.buildings = "forest", box(0, 0, 90, 90), [], []
    ctx.road = box(-5, -6, 95, -1)
    rng = np.random.default_rng(3)
    ctx.trees = [(float(x), float(y), 2.0) for x, y in rng.uniform(2, 88, (200, 2))]
    return ctx


def test_forest_moss_is_a_textured_layer_between_verge_and_litter(cfg) -> None:
    assert PATCH_PRIORITY.index("verge_dirt") < PATCH_PRIORITY.index("moss") < PATCH_PRIORITY.index("litter")
    ctx = forest_with_road()
    res = forest_detail(ctx, cfg)
    moss = [p for p in res.patches if p.layer == "moss"]
    assert moss and all(p.texture == "moss" for p in moss)
    assert any(p.layer == "litter" for p in res.patches)
    for p in res.patches:  # still disjoint
        if p.layer != "moss":
            assert p.geom.intersection(moss[0].geom).area < 1e-6
    off = copy.deepcopy(cfg)
    off["forest"]["moss"]["tree"]["keep"] = off["forest"]["moss"]["interior"]["keep"] = 0.0
    assert not any(p.layer == "moss" for p in forest_detail(ctx, off).patches)


def test_forest_moss_keeps_off_the_road_edge_and_clumps_on_noise(cfg) -> None:
    ctx = forest_with_road()
    mc = cfg["forest"]["moss"]
    moss = forest_detail(ctx, cfg).patches
    geom = unary_union([p.geom for p in moss if p.layer == "moss"])
    assert geom.distance(ctx.road) >= float(mc["edge_m"]) - 1e-6
    # thick where the clump noise is high, thin where it is low
    xs, ys = np.meshgrid(np.arange(4.5, 90, 3.0), np.arange(6.0, 90, 3.0))
    f = clump(xs.ravel(), ys.ravel(), float(mc["clump"]), float(mc["clump_m"]), MOSS_SALT)
    covered = shapely.contains_xy(geom, xs.ravel(), ys.ravel())
    thick, thin = covered[f > 1.2].mean(), covered[f < 0.5].mean()  # relative: the amounts are rules.yaml's to tune
    assert thick > 0.04 and thin < 0.4 * thick


def swamp_forest(cfg: dict, zone=None) -> tuple[ParcelContext, object]:
    ctx = forest_with_road()
    ctx.swamp = zone if zone is not None else box(-5, -5, 95, 45)  # the south half of the forest is within reach of water
    return ctx, forest_detail(ctx, cfg)


def test_swamp_ground_has_a_wet_floor_and_less_litter(cfg) -> None:
    for name in ("swamp", "mud", "puddle"):
        assert PATCH_PRIORITY.index("puddle") < PATCH_PRIORITY.index("mud") < PATCH_PRIORITY.index("moss") and name in PATCH_PRIORITY
    assert PATCH_PRIORITY.index("tone_b") < PATCH_PRIORITY.index("swamp") < PATCH_PRIORITY.index("lawn")
    ctx, res = swamp_forest(cfg)
    layer = lambda n: unary_union([p.geom for p in res.patches if p.layer == n] or [Polygon()])
    zone, free = ctx.swamp, free_area(ctx, cfg, trunks=False)
    base = layer("swamp")
    assert base.area > 0.3 * zone.intersection(free).area and base.difference(zone.buffer(1e-6)).area < 1e-6
    assert all(p.colour is not None and p.texture is None for p in res.patches if p.layer == "swamp")
    assert {p.texture for p in res.patches if p.layer in ("mud", "puddle")} == {"mud", "puddle"}
    # the wet floor covers the zone: no tone patch, and almost no bare ground (everything in it is some patch)
    assert not any(p.layer in ("tone_a", "tone_b") and p.geom.intersects(zone.buffer(-0.5)) for p in res.patches)
    covered = unary_union([p.geom for p in res.patches]).intersection(zone)
    assert covered.area > 0.98 * zone.intersection(free).area
    # moss stays, litter is cut to its factor per tree
    assert layer("moss").intersection(zone).area > 0
    plain = forest_detail(forest_with_road(), cfg)
    lit = lambda r, g: unary_union([p.geom for p in r.patches if p.layer == "litter"]).intersection(g).area
    north = box(-5, 45, 95, 95)
    assert lit(res, zone) < 0.5 * lit(plain, zone) and lit(res, north) == pytest.approx(lit(plain, north), rel=0.6)  # few blobs at the tuned keep: loose
    # the puddles are small pools of 1 to 4 m, mud patches are the large ones
    pools = [g for p in res.patches if p.layer == "puddle" for g in getattr(p.geom, "geoms", [p.geom])]
    assert pools and all(math.dist(g.bounds[:2], g.bounds[2:]) < 12 for g in pools)
    for i, a in enumerate(res.patches):
        for b in res.patches[i + 1:]:
            assert a.geom.intersection(b.geom).area < 1e-6  # still disjoint
    assert [p.geom.wkb for p in forest_detail(ctx, cfg).patches] == [p.geom.wkb for p in res.patches]  # seeded


def test_a_meadow_in_reach_of_water_is_swampy_too(cfg) -> None:
    ctx = yard()
    ctx.kind, ctx.polygon, ctx.fences, ctx.buildings, ctx.road, ctx.trees = "meadow", box(0, 0, 90, 90), [], [], None, []
    assert not any(p.layer in ("swamp", "mud", "puddle") for p in field_detail(ctx, cfg).patches)
    ctx.swamp = box(-5, -5, 50, 95)
    got = {p.layer for p in field_detail(ctx, cfg).patches}
    assert {"swamp", "mud", "puddle"} <= got and not {"tone_a", "tone_b"} & {
        p.layer for p in field_detail(ctx, cfg).patches if p.geom.intersects(ctx.swamp.buffer(-0.5))}


def test_plain_forest_has_a_few_mud_patches_beside_the_tracks_and_a_third_with_puddles(cfg) -> None:
    ctx = forest_with_road()
    ctx.polygon, ctx.trees = box(0, 0, 200, 100), []
    ctx.tracks = [("w1", LineString([(0, 50), (200, 50)]), 3.0)]
    res = forest_detail(ctx, cfg)
    mud = unary_union([p.geom for p in res.patches if p.layer == "mud"])
    puddle = unary_union([p.geom for p in res.patches if p.layer == "puddle"])
    n = len(getattr(mud, "geoms", [mud]))
    assert 2 <= n / 2.0 <= 12  # per hectare (the parcel is 2 ha): a few
    assert 0.1 * n <= len(getattr(puddle, "geoms", [puddle])) <= 0.7 * n and mud.area < 0.01 * ctx.polygon.area
    beside = [g for g in getattr(mud, "geoms", [mud]) if g.distance(ctx.tracks[0][1]) < 3.5]
    assert len(beside) >= 2  # along the edge of the track
    ctx.tracks = []
    off = copy.deepcopy(cfg)
    off["forest"]["mud"]["damp"]["keep"] = 0.0
    assert not any(p.layer in ("mud", "puddle") for p in forest_detail(ctx, off).patches)
    ctx.swamp = box(-5, -5, 205, 105)  # all swamp: no plain-forest mud, the swamp's own instead
    assert unary_union([p.geom for p in forest_detail(ctx, off).patches if p.layer == "mud"]).area > 0.1 * ctx.polygon.area


def test_hollow_is_the_dip_below_the_ring() -> None:
    n = 9
    h = np.array([[0.002 * ((c * 4.0 - 16) ** 2 + (r * 4.0 - 16) ** 2) for c in range(n)] for r in range(n)])
    bowl = Terrain(chunk_m=32.0, n=8, i0=-4, j0=-4, heights=h, valid=np.ones((n, n), bool), coverage=box(-16, -16, 16, 16))
    hol = {"ring_m": 6.0, "depth_m": 0.25}
    # the same number as the Rust test in wet.rs
    assert _hollow(bowl, np.array([1.0]), np.array([2.0]), hol)[0] == pytest.approx(0.2795290039756342, abs=1e-9)
    flat = Terrain(chunk_m=32.0, n=8, i0=-4, j0=-4, heights=np.full((n, n), 100.0), valid=np.ones((n, n), bool), coverage=box(-16, -16, 16, 16))
    assert _hollow(flat, np.array([1.0]), np.array([2.0]), hol)[0] == 0.0


def test_area_type_and_caps_and_patch_nodes(cfg) -> None:
    assert [area_type({"kind": k}) for k in ("garden", "building_plot", "field", "forest", "village", "dike", "water")] == [
        "garden", "garden", "field", "forest", "village", "dike", None]
    chunks = {(0, 0): {"pebble": [[float(i), 1.0, 0.0, 0.0, 1.0] for i in range(50)]}}
    cap_clutter(chunks, {"pebble": 10})
    again = {(0, 0): {"pebble": [[float(i), 1.0, 0.0, 0.0, 1.0] for i in range(50)]}}
    cap_clutter(again, {"pebble": 10})
    assert len(chunks[(0, 0)]["pebble"]) == 10 and chunks == again
    res = garden_detail(yard(), cfg)
    flat, tex = patch_meshes(res, yard().terrain, cfg["lift_m"])
    ids = ["dirt", "gravel", "leaf_litter", "soil_rows"]
    nodes = patches_by_chunk(patch_rows(flat, tex, ids), ids, 250.0)
    assert set(nodes) == {(0, 0)}
    assert "detail_patches" in nodes[(0, 0)]
    assert sum(m.face_count for m in nodes[(0, 0)].values()) == flat.face_count + sum(m.face_count for m in tex.values())


def test_clutter_meshes_are_tiny_and_stand_on_the_ground() -> None:
    meshes = detail_meshes.clutter_meshes()
    assert list(meshes) == list(detail_meshes.KINDS)
    for name, m in meshes.items():
        assert 3 <= m.face_count <= 30, name
        assert m.tris[:, :, 2].min() >= -1e-9 and m.tris[:, :, 2].max() < 1.3, name


def test_stored_grass_carries_a_tint_and_flowers_do_not(cfg: dict) -> None:
    res = garden_detail(yard(), cfg)
    grass = [c for c in res.clutter if c["kind"] in detail_meshes.TINTABLE]
    assert grass and all(0 <= c["tint"] <= 255 for c in grass)
    assert all("tint" not in c for c in res.clutter if c["kind"] not in detail_meshes.TINTABLE)
    assert len({c["tint"] for c in grass}) > 5  # not one flat value
    assert detail_meshes.tint_rgb(128) == (1.0, 1.0, 1.0)


def test_a_yard_has_garden_flowers_only_and_tall_grass_by_the_fence(cfg: dict) -> None:
    res = garden_detail(yard(), cfg)
    kinds = {c["kind"] for c in res.clutter}
    assert not kinds & set(detail_meshes.WILD_FLOWERS)  # the wild species stay on meadows and verges
    assert kinds & set(detail_meshes.GARDEN_FLOWERS)
    tall = [c for c in res.clutter if c["kind"] == "tall_grass"]
    lawn = [c for c in res.clutter if c["kind"] == "tuft"]
    d = lambda c: min(Point(c["x"], c["y"]).distance(f) for f in yard().fences)  # noqa: E731
    assert np.mean([d(c) for c in tall]) < np.mean([d(c) for c in lawn])


def test_garden_paths_and_beds_never_lead_to_a_neighbours_building() -> None:
    from shapely.geometry import box
    from drycastle_pipeline.detail import _own_buildings

    class Ctx:  # only what _own_buildings reads
        polygon = box(0, 0, 20, 6)
        buildings = [box(25, 0, 30, 5), box(10, 1, 14, 4)]

    assert _own_buildings(Ctx()) == [Ctx.buildings[1]]
