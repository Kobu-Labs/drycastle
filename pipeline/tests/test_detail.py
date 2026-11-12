"""Ground detail prototype: seeded and deterministic, and nothing lands where it must not."""

import copy

import numpy as np
import pytest
from shapely.geometry import LineString, Point, Polygon, box

from suchohrad_pipeline import detail_meshes
from suchohrad_pipeline.detail import (
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
from suchohrad_pipeline.props import seeded
from suchohrad_pipeline.rules import load_section
from suchohrad_pipeline.terrain import Terrain


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


def test_area_type_and_caps_and_patch_nodes(cfg) -> None:
    assert [area_type({"kind": k}) for k in ("garden", "building_plot", "other", "forest", "road", "water")] == [
        "garden", "garden", "field", "forest", "road", None]
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
        assert m.tris[:, :, 2].min() >= -1e-9 and m.tris[:, :, 2].max() < 0.5, name
