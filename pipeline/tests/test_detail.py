"""Ground detail prototype: seeded and deterministic, and nothing lands where it must not."""

import copy

import numpy as np
import pytest
from shapely.geometry import LineString, Point, Polygon, box

from suchohrad_pipeline.detail import (
    PATCH_PRIORITY,
    ParcelContext,
    free_area,
    garden_detail,
    patch_meshes,
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
    assert sum(res.counts().values()) > 200
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
