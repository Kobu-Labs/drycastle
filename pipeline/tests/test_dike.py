"""The flood dike recipe: a gravel track over the road, flat grass, thick grass as clutter, all inside the parcel."""

import numpy as np
import pytest
from shapely.geometry import LineString, box
from shapely.ops import unary_union

from drycastle_pipeline.assets import ASSETS_DIR
from drycastle_pipeline.detail import ParcelContext, area_type, dike_parcels, patch_meshes
from drycastle_pipeline.dike import PATCH_RGB, dike_detail
from drycastle_pipeline.rules import load_section
from drycastle_pipeline.textures import texture

from .test_detail import flat_terrain


@pytest.fixture(scope="module")
def cfg() -> dict:
    return load_section("detail")


def dike() -> ParcelContext:
    road = LineString([(0, 15), (50, 15)])
    return ParcelContext(
        id="P-9998", kind="building_plot", polygon=box(0, 0, 50, 30), buildings=[], fences=[], trees=[],
        road=road.buffer(1.5), terrain=flat_terrain(), tree_cfg=load_section("props")["trees"],
        road_lines=[(road, 3.0)],
    )


def png(name: str) -> np.ndarray:
    import matplotlib.image as mpimg

    return (mpimg.imread(ASSETS_DIR / "drycastle-generated-textures" / name) * 255).round().astype(int)


def test_the_texture_is_in_the_library_and_tiles_both_ways() -> None:
    t = texture("dike_gravel")
    assert (t.tile_w, t.tile_h) == (8.0, 4.0) and not t.alpha and not t.tintable
    img = png("dike_gravel.png")[..., :3]
    assert img.shape[:2] == (256, 512)
    for edge_a, edge_b, inner_a, inner_b in ((img[:, 0], img[:, -1], img[:, 255], img[:, 256]), (img[0], img[-1], img[127], img[128])):
        assert np.abs(edge_a - edge_b).mean() < np.abs(inner_a - inner_b).mean() * 1.4 + 6  # the join is no worse than anywhere
    patch = np.any([np.abs(img - np.array(c)).sum(axis=-1) < 12 for c in PATCH_RGB], axis=0).mean()
    assert 0.03 < patch < 0.3  # some grass patches, mostly gravel


def test_the_dike_parcels_take_the_dike_recipe() -> None:
    assert "P-0073" in dike_parcels()
    assert area_type({"id": "P-0073", "kind": "building_plot"}) == "dike"
    assert area_type({"id": "P-0001", "kind": "building_plot"}) == "garden"


def test_track_is_narrow_lies_above_the_lawn_and_the_lawn_covers_the_road_beside_it(cfg) -> None:
    ctx, rc = dike(), cfg["dike"]
    res = dike_detail(ctx, cfg)
    track = [p for p in res.patches if p.layer == "dike_track"]
    assert track and all(p.texture == "dike_gravel" and p.lift == rc["track_lift_m"] for p in track)
    assert rc["track_lift_m"] > rc["border"]["lawn_lift_m"] > 0.05 + 0.003 * 5  # over the lawn, over the road ribbon
    union = unary_union([p.geom for p in track])
    assert union.within(ctx.polygon.buffer(1e-6))
    assert 1.5 * 50 < union.area < 3.2 * 50  # about 2.2 m wide over 50 m: well under the 3 m road
    lawn = unary_union([p.geom for p in res.patches if p.layer == "lawn"])
    inside_road = ctx.road.intersection(ctx.polygon.buffer(-0.5))
    assert union.union(lawn).buffer(0.02).contains(inside_road)  # no flat road colour shows beside the track
    assert sum(1 for p in track if abs(p.angle) < 1e-6 or abs(abs(p.angle) - np.pi) < 1e-6) == len(track)  # along a straight road
    assert len({p.origin for p in track}) > 3  # pieces start at different places of the tile


def test_grass_is_thick_off_the_track_taller_further_out_and_seeded(cfg) -> None:
    ctx = dike()
    res = dike_detail(ctx, cfg)
    assert {c["kind"] for c in res.clutter} <= {"tall_grass", "tuft", "straw"}
    assert len(res.clutter) > 0.5 * ctx.polygon.area  # about one per square metre
    track = unary_union([p.geom for p in res.patches if p.layer == "dike_track"])
    near = [c["scale"] for c in res.clutter if c["kind"] == "tall_grass" and abs(c["y"] - 15) < 4]
    far = [c["scale"] for c in res.clutter if c["kind"] == "tall_grass" and abs(c["y"] - 15) > 11]
    assert np.mean(far) > np.mean(near) * 1.15
    from shapely.geometry import Point

    bare = [c for c in res.clutter if c["kind"] != "tuft" and track.contains(Point(c["x"], c["y"]))]
    assert not bare  # no tall grass or straw on the gravel (tufts sit on its grass patches)
    assert dike_detail(ctx, cfg).clutter == res.clutter


def test_patch_meshes_lift_the_track_and_texture_it(cfg) -> None:
    ctx = dike()
    res = dike_detail(ctx, cfg)
    flat, tex = patch_meshes(res, ctx.terrain, cfg["lift_m"])
    assert set(tex) == {"dike_gravel"} and flat.face_count > 0
    assert np.allclose(tex["dike_gravel"].tris[..., 2], 100.0 + cfg["dike"]["track_lift_m"])
    assert np.allclose(flat.tris[..., 2], 100.0 + cfg["dike"]["border"]["lawn_lift_m"])  # over the neighbours' patches


def test_the_border_is_ragged_the_green_spills_over_it_and_the_grass_thins_out(cfg) -> None:
    from shapely.geometry import Point

    ctx = dike()
    res = dike_detail(ctx, cfg)
    lawn = unary_union([p.geom for p in res.patches if p.layer == "lawn"])
    outside, notch = lawn.difference(ctx.polygon), ctx.polygon.difference(lawn.buffer(0.01)).difference(ctx.road.buffer(0.5))
    assert outside.area > 20.0  # lawn beyond the parcel line (the hull's bulges and the loose blobs)
    assert lawn.length > 1.1 * ctx.polygon.length  # a frayed outline is longer than the straight one
    assert all(p.lift == cfg["dike"]["border"]["lawn_lift_m"] for p in res.patches if p.layer == "lawn")
    assert notch.area < ctx.polygon.area * 0.02  # green all the way to the parcel line
    # the grass thins across the border: dense well inside, gone well outside
    deep = [c for c in res.clutter if ctx.polygon.exterior.distance(Point(c["x"], c["y"])) > 4 and ctx.polygon.contains(Point(c["x"], c["y"])) and abs(c["y"] - 15) > 3]
    far = [c for c in res.clutter if not ctx.polygon.buffer(3.2).contains(Point(c["x"], c["y"]))]
    assert len(deep) > 200 and not far
