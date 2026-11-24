"""The crop recipe (#328): deterministic, soft or sharp edge, baulk between adjoining patches, artifacts on every crop."""

import numpy as np
import pytest
import shapely
from shapely.geometry import box

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.geometry import polygons
from drycastle_pipeline.core.rules import _merge, load_section
from drycastle_pipeline.detail.build import build_village_detail
from drycastle_pipeline.detail.crop import (
    ARTIFACT_KINDS,
    CROP_KINDS,
    PLANT_KINDS,
    _row_frame,
    crop_detail,
    crop_plan,
    plant_density,
)
from drycastle_pipeline.detail.documents import crops_document
from drycastle_pipeline.detail.model import ParcelContext, VillageDetail
from drycastle_pipeline.detail.recipes import area_type
from drycastle_pipeline.detail.tools import _union
from drycastle_pipeline.ground.areas import ground_types
from drycastle_pipeline.ground.attributes import attributes_of
from tests.detail.test_detail import flat_terrain

CROPS = ("crop_grain", "crop_corn", "crop_potato", "crop_rapeseed", "crop_bare")


@pytest.fixture(scope="module")
def rules() -> dict:
    return load_section("detail")


@pytest.fixture(scope="module")
def types() -> dict:
    return ground_types()


def cfg_for(rules: dict, types: dict, kind: str, *over: dict) -> dict:
    crop = _merge(rules["crop"], attributes_of(types[kind])["detail"])
    for o in over:
        crop = _merge(crop, o)
    return {**rules, "crop": crop}


# the per-patch variety switched off or pinned, to test one thing at a time
QUIET = {"stubble": {"probability": 0}, "fallow": {"probability": 0}, "artifacts": {"extras": [], "kinds_per_patch": [9, 9]}}
STRAIGHT = {"variety": {"row_angle": {"cross_share": 0, "free_share": 0, "jitter_deg": 0}}}
ALWAYS_FALLOW = {"fallow": {"probability": 1.0}}
ALWAYS_STUBBLE = {"stubble": {"probability": 1.0}, "fallow": {"probability": 0}}


def field(kind: str, poly=None, pid="P-7001", neighbours=None) -> ParcelContext:
    return ParcelContext(
        id=pid, kind=kind, polygon=poly or box(0, 0, 60, 40), buildings=[], fences=[], trees=[], road=None,
        terrain=flat_terrain(), tree_cfg={}, crop_neighbours=neighbours,
    )


def test_every_crop_is_a_drawn_only_type_of_the_crop_recipe(types) -> None:
    for k in CROPS:
        a = attributes_of(types[k])
        assert a["recipe"] == "crop" and "osm" not in types[k] and "rule" not in types[k]
        assert area_type({"id": "A-1", "kind": k}, types) == "crop"


def test_same_patch_gives_identical_output(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", {"fallow": {"probability": 0.5}, "stubble": {"probability": 0.5}})
    a, b = crop_detail(field("crop_grain"), c), crop_detail(field("crop_grain"), c)
    assert a.crops == b.crops and a.crops
    assert [p.geom.wkb for p in a.patches] == [p.geom.wkb for p in b.patches]
    assert crop_detail(field("crop_grain", pid="P-7002"), c).crops != a.crops  # the seed is the patch


@pytest.mark.parametrize("kind", CROPS)
def test_every_crop_has_artifacts_and_only_its_own_kinds(rules, types, kind) -> None:
    res = crop_detail(field(kind), cfg_for(rules, types, kind, QUIET))
    arts = [c for c in res.crops if c["kind"] in ARTIFACT_KINDS]
    assert 2 <= len(arts) <= 10
    allowed = {i["kind"] for i in attributes_of(types[kind])["detail"]["artifacts"]["items"]}
    assert {c["kind"] for c in arts} <= allowed
    assert {c["kind"] for c in res.crops} <= set(CROP_KINDS)
    plants = {c["kind"] for c in res.crops if c["kind"] in PLANT_KINDS}
    assert plants == ({"crop_wheat"} if kind == "crop_grain" else {"crop_corn"} if kind == "crop_corn"
                      else {"crop_potato"} if kind == "crop_potato" else {"crop_rapeseed"} if kind == "crop_rapeseed" else set())


def test_a_tiny_patch_has_no_artifacts(rules, types) -> None:
    res = crop_detail(field("crop_grain", box(0, 0, 8, 8)), cfg_for(rules, types, "crop_grain", ALWAYS_STUBBLE))
    assert not [c for c in res.crops if c["kind"] in ARTIFACT_KINDS]


def test_instances_stay_inside_and_budget_is_sane(rules, types) -> None:
    poly = box(0, 0, 60, 40)
    for kind in CROPS:
        res = crop_detail(field(kind, poly), cfg_for(rules, types, kind, {"fallow": {"probability": 0.5}, "stubble": {"probability": 0.5}}))
        assert all(poly.buffer(1e-6).contains(shapely.Point(c["x"], c["y"])) for c in res.crops)
        assert len(res.crops) < 12_000  # 0.24 ha


def test_plants_thin_out_toward_a_feathered_edge_and_stop_dead_at_a_wall(rules, types) -> None:
    poly = box(0, 0, 80, 60)
    grain = crop_detail(field("crop_grain", poly), cfg_for(rules, types, "crop_grain", QUIET, STRAIGHT, {"tramlines": {"enabled": False}, "spots": {"wet": {"per_ha": 0}, "bare": {"per_ha": 0}, "weeds": {"per_ha": 0}}}))
    d = np.array([min(c["x"], 80 - c["x"], c["y"], 60 - c["y"]) for c in grain.crops if c["kind"] == "crop_wheat"])
    per_m2 = lambda lo, hi: ((d >= lo) & (d < hi)).sum() / (perimeter_band(poly, lo, hi))  # noqa: E731
    assert per_m2(0.0, 1.0) < 0.35 * per_m2(12.0, 16.0)
    assert per_m2(1.5, 3.0) < per_m2(12.0, 16.0)
    assert per_m2(4.0, 6.0) < 0.9 * per_m2(12.0, 16.0)  # the thinning runs over the wide zone, not 3 m
    corn_cfg = cfg_for(rules, types, "crop_corn", QUIET)
    corn = crop_detail(field("crop_corn", poly), corn_cfg)
    dc = np.array([min(c["x"], 80 - c["x"], c["y"], 60 - c["y"]) for c in corn.crops if c["kind"] == "crop_corn"])
    margin = float(corn_cfg["crop"]["edge"]["plant_margin_m"])
    assert dc.min() >= margin - 0.15
    assert per_m2_of(dc, margin + 0.2, margin + 1.4, poly) > 0.7 * per_m2_of(dc, 8.0, 11.0, poly)  # abrupt: full right at the wall


def perimeter_band(poly, lo, hi) -> float:
    return poly.buffer(-lo).area - poly.buffer(-hi).area


def per_m2_of(d, lo, hi, poly) -> float:
    return ((d >= lo) & (d < hi)).sum() / perimeter_band(poly, lo, hi)


def test_plant_density_curve(rules) -> None:
    e = {"plant_margin_m": 0.5, "plant_falloff_m": 3.0}
    d = np.array([0.0, 0.5, 2.0, 3.5, 9.0])
    p = plant_density(d, e)
    assert p[0] == p[1] == 0 and 0 < p[2] < 1 and p[3] == 1 and p[4] == 1
    assert list(plant_density(d, {"plant_margin_m": 0.5, "plant_falloff_m": 0.0})) == [0, 1, 1, 1, 1]


def test_feathered_edge_is_stepped_bands_into_the_surround(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET)
    res = crop_detail(field("crop_grain"), c)
    bands = [p for p in res.patches if p.layer.startswith("crop_edge_")]
    assert len({p.layer for p in bands}) >= 8  # 8 or more bands: a gradient, not stripes
    sur = np.array(hex_to_rgb(rules["crop"]["surround"]))  # nothing beside the patch: the fallback colour
    core = np.array(next(p for p in res.patches if p.layer == "crop_ground").colour)
    by = {}
    for p in bands:
        by.setdefault(int(p.layer.rsplit("_", 1)[1]), set()).add(tuple(p.colour))
    ks = sorted(by)
    dist = [np.mean([np.linalg.norm(np.array(col) - sur) for col in by[k]]) for k in ks]
    assert dist == sorted(dist) and dist[-1] < np.linalg.norm(core - sur)  # outer band nearest the surround
    wide = max(c["crop"]["edge"]["feather_m"], 6.0)
    deep = max(p.geom.distance(shapely.Point(30, 20)) for p in bands)
    assert box(0, 0, 60, 40).exterior.distance(shapely.Point(30, 20)) > 0 and wide >= 6.0 and deep >= 0
    # the bands reach several metres in
    depth = max(min(x, 60 - x, y, 40 - y) for p in bands for x, y in shapely.get_coordinates(p.geom))
    assert depth >= 6.0
    # the bands interleave: their edges wander, so the outer band is broken up and reaches deeper than its nominal width
    assert len(polygons(next(p for p in bands if p.layer == "crop_edge_5").geom)) >= 1
    # corn keeps its plant wall but the ground blends too
    walled = crop_detail(field("crop_corn"), cfg_for(rules, types, "crop_corn", QUIET))
    assert len({p.layer for p in walled.patches if p.layer.startswith("crop_edge_")}) >= 8


def around(*units) -> list:
    """(id, polygon, colour, is_crop) of the ground beside a patch."""
    return [(pid, g, col, crop) for pid, g, col, crop in units]


def test_the_blend_takes_the_colour_of_the_ground_that_really_lies_outside(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET)
    poly = box(0, 0, 60, 40)
    ctx = field("crop_grain", poly)
    ctx.ground_around = around(("A-1", box(-30, -5, 0, 45), [30, 60, 160], False), ("A-2", box(60, -5, 90, 45), [200, 40, 40], False))
    res = crop_detail(ctx, c)
    outer = [p for p in res.patches if p.layer == "crop_edge_1"]
    assert outer
    left = [p for p in outer if p.geom.centroid.x < 20 and 12 < p.geom.centroid.y < 28]
    right = [p for p in outer if p.geom.centroid.x > 40 and 12 < p.geom.centroid.y < 28]
    assert left and right
    near = lambda col, ref: np.linalg.norm(np.array(col) - np.array(ref))  # noqa: E731
    # the first band is nearly the neighbour's colour, each side its own; the surround colour is not used
    assert min(near(p.colour, [30, 60, 160]) for p in left) < 45 and min(near(p.colour, [200, 40, 40]) for p in right) < 45
    sur = hex_to_rgb(rules["crop"]["surround"])
    assert all(near(p.colour, sur) > 60 for p in left + right)
    # and a stretch with another neighbour: the colours differ along the edge
    assert near(left[0].colour, right[0].colour) > 100
    # the last band is next to the field colour
    inner = [p for p in res.patches if p.layer.startswith("crop_edge_") and int(p.layer.rsplit("_", 1)[1]) >= 8]
    core = np.array(next(p for p in res.patches if p.layer == "crop_ground").colour)
    assert all(near(p.colour, core) < near(left[0].colour, core) for p in inner)


def test_two_neighbours_blend_along_the_edge(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET)
    ctx = field("crop_grain", box(0, 0, 60, 40))
    ctx.ground_around = around(("A-1", box(-30, -5, 30, 0), [30, 60, 160], False), ("A-2", box(30, -5, 90, 0), [200, 40, 40], False))
    res = crop_detail(ctx, c)
    bottom = sorted((p for p in res.patches if p.layer == "crop_edge_1" and p.geom.centroid.y < 4), key=lambda p: p.geom.centroid.x)
    assert len({tuple(p.colour) for p in bottom}) >= 3  # more than two colours: stepping from one neighbour to the other
    reds = [p.colour[0] for p in bottom]
    assert reds[0] < reds[-1]


def test_beside_a_crop_patch_the_blend_is_short(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET)
    left, right = box(0, 0, 40, 40), box(40, 0, 80, 40)
    ctx = field("crop_grain", left, "P-7001", [("P-7002", right)])
    ctx.ground_around = around(("P-7002", right, [110, 80, 55], True))
    res = crop_detail(ctx, c)
    pts = [(x, y) for p in res.patches if p.layer.startswith("crop_edge_") for x, y in shapely.get_coordinates(p.geom) if 10 < y < 30]
    toward_nb = max(40 - x for x, _ in pts if x > 20)  # how deep the bands reach from the shared edge
    toward_out = max(x for x, _ in pts if x < 20)  # ... from the open edge
    assert toward_nb <= float(c["crop"]["baulk"]["blend_m"]) + 1.5 and toward_out >= 0.7 * float(c["crop"]["edge"]["feather_m"])
    # the bands beside the neighbour head for ITS colour
    sh = [p for p in res.patches if p.layer == "crop_edge_1" and p.geom.centroid.x > 30]
    assert sh and min(np.linalg.norm(np.array(p.colour) - [110, 80, 55]) for p in sh) < 40
    assert [p for p in res.patches if p.layer in ("crop_baulk", "crop_baulk_dirt")]  # the baulk stays


def test_a_road_beside_the_field_gives_the_verge_colour(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET)
    ctx = field("crop_grain", box(0, 0, 60, 40))
    ctx.road = box(-20, -6, 80, -1)
    res = crop_detail(ctx, c)
    verge = np.array(hex_to_rgb(rules["crop"]["edge"]["neighbour"]["road"]))
    low = [p for p in res.patches if p.layer == "crop_edge_1" and p.geom.centroid.y < 5 and 15 < p.geom.centroid.x < 45]
    assert low and min(np.linalg.norm(np.array(p.colour) - verge) for p in low) < 40


def test_headland_is_broken_not_a_clean_band(rules, types) -> None:
    res = crop_detail(field("crop_grain"), cfg_for(rules, types, "crop_grain", QUIET))
    head = next(p for p in res.patches if p.layer == "crop_headland")
    assert head.texture == "dirt" and len(polygons(head.geom)) > 4
    assert head.geom.area < 0.55 * (box(0, 0, 60, 40).area - box(2, 2, 58, 38).area)


def test_baulk_between_two_adjoining_patches_and_none_without(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain")
    left, right = box(0, 0, 40, 40), box(40, 0, 80, 40)
    a = crop_detail(field("crop_grain", left, "P-7001", [("P-7002", right)]), c)
    b = crop_detail(field("crop_corn", right, "P-7002", [("P-7001", left)]), cfg_for(rules, types, "crop_corn"))
    alone = crop_detail(field("crop_grain", left, "P-7001"), c)
    layer = lambda r: [p for p in r.patches if p.layer in ("crop_baulk", "crop_baulk_dirt")]  # noqa: E731
    assert layer(a) and layer(b) and not layer(alone)
    strip_a, strip_b = layer(a)[0].geom, layer(b)[0].geom
    assert 0.2 < 40 - strip_a.bounds[0] < 1.2 and 0.2 < strip_b.bounds[2] - 40 < 1.2  # half each side of x = 40
    total = strip_a.area + strip_b.area
    assert 0.5 * 40 * 0.9 < total < 40 * 1.1  # 0.5 to 1 m wide along 40 m
    assert not strip_a.intersects(left.difference(strip_a.buffer(0.01)).intersection(box(0, 0, 38, 40)).buffer(-0.01))
    # no plant on the baulk
    assert not any(strip_a.buffer(-0.01).contains(shapely.Point(p["x"], p["y"])) for p in a.crops)
    # patches that merely stand apart get none
    far = crop_detail(field("crop_grain", left, "P-7001", [("P-7003", box(60, 0, 100, 40))]), c)
    assert not layer(far)


def test_textured_grounds_turn_with_the_rows(rules, types) -> None:
    res = crop_detail(field("crop_potato", box(0, 0, 60, 30)), cfg_for(rules, types, "crop_potato", STRAIGHT))
    rows = next(p for p in res.patches if p.layer == "crop_rows")
    assert rows.texture == "soil_rows" and abs(abs(np.cos(rows.angle)) - 1) < 1e-6  # along the 60 m side
    assert {p.texture for p in crop_detail(field("crop_bare"), cfg_for(rules, types, "crop_bare", STRAIGHT)).patches if p.layer == "crop_rows"} == {"dirt"}


def test_patches_are_disjoint(rules, types) -> None:
    for kind in CROPS:
        res = crop_detail(field(kind), cfg_for(rules, types, kind, {"fallow": {"probability": 0.5}, "stubble": {"probability": 0.5}}))
        geoms = [p.geom for p in res.patches]
        for i, g in enumerate(geoms):
            for h in geoms[i + 1:]:
                assert g.intersection(h).area < 0.05


def test_crops_document_and_chunks(rules, types) -> None:
    res = crop_detail(field("crop_rapeseed"), cfg_for(rules, types, "crop_rapeseed"))
    det = VillageDetail(250.0, {}, np.zeros((0, 19)), [], {}, None, {(0, 0): {}})
    for c in res.crops:
        det.crops[(0, 0)].setdefault(c["kind"], []).append([c["x"], c["y"], c["z"], c["rot"], c["scale"]])
    doc = crops_document(det, rules | {"crop_kinds": rules["crop_kinds"]})
    assert set(doc["kinds"]) == set(CROP_KINDS) == set(rules["crop_kinds"])
    assert doc["kinds"]["crop_rapeseed"]["asset"] == "pack:drycastle-generated-models/rapeseed_plant"
    assert all(len(r) == 5 for rows in doc["chunks"]["0_0"].values() for r in rows)


# --- variety ---------------------------------------------------------------------------------

BIG = box(-60, -35, 60, 35)
NO_SPOTS = {"spots": {"wet": {"per_ha": 0}, "bare": {"per_ha": 0}, "weeds": {"per_ha": 0}}}


def plants_of(res) -> list[dict]:
    return [c for c in res.crops if c["kind"] in PLANT_KINDS]


def test_every_patch_has_its_own_tone_plant_height_and_row_angle(rules, types) -> None:
    c = cfg_for(rules, types, "crop_corn", QUIET, NO_SPOTS)
    plans = [crop_plan(field("crop_corn", pid=f"P-{7100 + i}"), c["crop"]) for i in range(40)]
    assert len({round(p.angle, 2) for p in plans}) > 20 and len({round(p.tone[0], 3) for p in plans}) > 20
    assert all(0.93 <= p.tone[0] <= 1.07 and 0.85 <= p.plant_mult <= 1.15 for p in plans)
    long_angle = _row_frame(box(0, 0, 60, 40))[0]
    off_long = [p for p in plans if abs(((p.angle - long_angle) + np.pi / 2) % np.pi - np.pi / 2) > 0.3]
    assert 5 <= len(off_long) <= 30  # not always the long side, but mostly
    # the plants of two patches stand as tall as their plan says, the ground has the shifted tone
    lo, hi = sorted(plans, key=lambda p: p.plant_mult)[0], sorted(plans, key=lambda p: p.plant_mult)[-1]
    heights = []
    for pl in (lo, hi):
        pid = f"P-{7100 + plans.index(pl)}"
        res = crop_detail(field("crop_corn", pid=pid), c)
        heights.append(np.mean([x["scale"] for x in plants_of(res)]))
        base = np.array(hex_to_rgb(c["crop"]["ground_colours"][0]))
        got = np.array(next(q for q in res.patches if q.layer == "crop_ground").colour)
        assert np.all(np.abs(got - np.clip(base * pl.tone[0], 0, 255)) <= 0.02 * 255 + 2)
    assert heights[1] / heights[0] > 1.1 * 0.9 * (hi.plant_mult / lo.plant_mult) / 1.1
    assert hi.plant_mult > lo.plant_mult and heights[1] > heights[0]


def test_variety_is_seeded_by_the_patch(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", {"fallow": {"probability": 0.5}, "stubble": {"probability": 0.5}})
    for pid in ("P-7201", "P-7202", "P-7203"):
        a, b = crop_detail(field("crop_grain", pid=pid), c), crop_detail(field("crop_grain", pid=pid), c)
        assert a.crops == b.crops and [(p.layer, p.colour, p.geom.wkb) for p in a.patches] == [(p.layer, p.colour, p.geom.wkb) for p in b.patches]


def test_tramlines_are_pairs_of_tracks_18_to_24_m_apart_without_plants(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", QUIET, STRAIGHT, NO_SPOTS)
    seen = set()
    for pid in ("P-7301", "P-7302", "P-7303", "P-7304"):
        ctx = field("crop_grain", BIG, pid)
        plan = crop_plan(ctx, c["crop"])
        res = crop_detail(ctx, c)
        tram = next(p for p in res.patches if p.layer == "crop_tramline")
        assert sum(tram.colour) < sum(next(p for p in res.patches if p.layer == "crop_ground").colour)  # darker
        ys = sorted(round(g.centroid.y, 2) for g in polygons(tram.geom))  # the rows run along x: a track is a strip of constant y
        assert len(ys) >= 6  # three pairs in 70 m
        pairs = [(a, b) for a, b in zip(ys[0::2], ys[1::2], strict=True)]
        assert all(abs((b - a) - 1.8) < 0.15 for a, b in pairs)
        mids = [(a + b) / 2 for a, b in pairs]
        assert all(abs((m2 - m1) - plan.tram_spacing) < 0.3 for m1, m2 in zip(mids, mids[1:], strict=False))
        assert 18.0 <= plan.tram_spacing <= 24.0
        seen.add(round(plan.tram_spacing, 1))
        assert all(abs(g.bounds[3] - g.bounds[1] - 0.5) < 0.05 for g in polygons(tram.geom))
        assert not any(tram.geom.buffer(-0.01).contains(shapely.Point(x["x"], x["y"])) for x in plants_of(res))
        assert tram.geom.bounds[0] > -60 + 0.5 and tram.geom.bounds[2] < 60 - 0.5  # stops short of the headland
    assert len(seen) > 1  # every patch its own spacing
    for kind in ("crop_corn", "crop_potato", "crop_bare"):  # only grain and rapeseed
        assert not [p for p in crop_detail(field(kind, BIG), cfg_for(rules, types, kind, QUIET)).patches if p.layer == "crop_tramline"]
    assert [p for p in crop_detail(field("crop_rapeseed", BIG), cfg_for(rules, types, "crop_rapeseed", QUIET)).patches if p.layer == "crop_tramline"]


def test_dirt_strips_along_one_or_two_edges(rules, types) -> None:
    strip = {"tramlines": {"edge_strip": {"probability": 1.0, "edges": [2, 2], "min_edge_m": 10.0}}}
    c = cfg_for(rules, types, "crop_grain", QUIET, STRAIGHT, NO_SPOTS, strip)
    res = crop_detail(field("crop_grain", BIG, "P-7311"), c)
    ds = next(p for p in res.patches if p.layer == "crop_dirtstrip")
    assert ds.texture == "dirt" and ds.geom.area > 60
    assert len(polygons(ds.geom)) >= 1 and not any(ds.geom.buffer(-0.01).contains(shapely.Point(x["x"], x["y"])) for x in plants_of(res))
    none = cfg_for(rules, types, "crop_grain", QUIET, {"tramlines": {"edge_strip": {"probability": 0.0}}})
    assert not [p for p in crop_detail(field("crop_grain", BIG, "P-7311"), none).patches if p.layer == "crop_dirtstrip"]


def test_a_stubble_stripe_has_straw_ground_bales_and_no_plants(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", ALWAYS_STUBBLE, STRAIGHT, NO_SPOTS, {"artifacts": {"items": [], "extras": []}})  # only the bales of the stripe
    for pid in ("P-7401", "P-7402", "P-7403"):
        ctx = field("crop_grain", BIG, pid)
        assert crop_plan(ctx, c["crop"]).stubble
        res = crop_detail(ctx, c)
        st = next(p for p in res.patches if p.layer == "crop_stubble")
        straw = np.array(hex_to_rgb(c["crop"]["stubble"]["colours"][0]))
        assert np.linalg.norm(np.array(st.colour) - straw) < 40
        share = sum(p.geom.area for p in res.patches if p.layer in ("crop_stubble", "crop_stubble_rows")) / BIG.area  # without its border zone
        assert 0.1 < share < 0.6
        bales = [x for x in res.crops if x["kind"] in ("art_hay_bale_round", "art_hay_bale_square")]
        assert len(bales) >= 4 and len({b["kind"] for b in bales}) == 1  # one kind per patch, lined
        whole = _union([p.geom for p in res.patches if p.layer in ("crop_stubble", "crop_stubble_rows") or p.layer.startswith("crop_edge_")])  # the border zone of a stripe is straw-mixed
        assert all(whole.buffer(0.05).contains(shapely.Point(b["x"], b["y"])) for b in bales)
        assert not any(st.geom.buffer(-0.05).contains(shapely.Point(x["x"], x["y"])) for x in plants_of(res))
        assert plants_of(res)  # the rest is still planted
        ys = sorted(round(b["y"]) for b in bales)
        assert max(ys) - min(ys) <= 8  # a line or two, not scattered
    other = crop_detail(field("crop_grain", BIG, "P-7401"), cfg_for(rules, types, "crop_grain", {"stubble": {"probability": 0.0}, "fallow": {"probability": 0}}))
    assert not [p for p in other.patches if p.layer == "crop_stubble"]
    for kind in ("crop_corn", "crop_potato", "crop_rapeseed"):  # grain only (the type's config)
        assert not any(crop_plan(field(kind, BIG, f"P-{7410 + i}"), cfg_for(rules, types, kind)["crop"]).stubble for i in range(30))
    share = np.mean([crop_plan(field("crop_grain", BIG, f"P-{7500 + i}"), cfg_for(rules, types, "crop_grain")["crop"]).stubble for i in range(300)])
    assert 0.1 < share < 0.3  # grain: a share of the big patches


def test_stubble_bands_blend_to_straw_not_to_the_crop_colour(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", ALWAYS_STUBBLE, STRAIGHT, NO_SPOTS, {"tramlines": {"enabled": False}})
    ctx = field("crop_grain", BIG, "P-7401")
    ctx.ground_around = around(("A", box(-90, -45, 90, 45).difference(BIG), [100, 130, 90], False))
    res = crop_detail(ctx, c)
    straw = np.array(c["crop"]["stubble"]["colours"][0] and hex_to_rgb(c["crop"]["stubble"]["colours"][0]))
    base = np.array(hex_to_rgb(c["crop"]["ground_colours"][0]))
    deep = [np.array(p.colour) for p in res.patches if p.layer in ("crop_edge_7", "crop_edge_8")]
    to_straw = [x for x in deep if np.linalg.norm(x - straw) < np.linalg.norm(x - base)]
    to_base = [x for x in deep if np.linalg.norm(x - base) < np.linalg.norm(x - straw)]
    assert to_straw and to_base  # where the stripe is, the zone ends in straw; elsewhere in the crop's ground


def test_fallow_is_weedy_grass_without_crop_plants_and_keeps_its_type(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", ALWAYS_FALLOW)
    ctx = field("crop_grain", box(0, 0, 60, 40), "P-7501")
    assert ctx.kind == "crop_grain" and crop_plan(ctx, c["crop"]).fallow
    res = crop_detail(ctx, c)
    assert not plants_of(res)
    ground = next(p for p in res.patches if p.layer == "crop_ground").colour
    assert ground[1] > ground[0] and ground[1] > ground[2]  # grass-green, not wheat-gold
    layers = {p.layer for p in res.patches}
    assert {"crop_tone", "crop_tone_2"} <= layers and any(x.startswith("crop_flower_") for x in layers)
    assert not layers & {"crop_tramline", "crop_stubble"}
    assert len([x for x in res.crops if x["kind"] in ARTIFACT_KINDS]) <= int(c["crop"]["fallow"]["max_artifacts"])
    flowers = _union([p.geom for p in res.patches if p.layer.startswith("crop_flower_")])
    assert 0 < len(polygons(flowers)) < 400 and flowers.area < 0.02 * 2400
    # a share of the patches (config), whatever the type
    share = np.mean([crop_plan(field("crop_grain", pid=f"P-{8000 + i}"), cfg_for(rules, types, "crop_grain")["crop"]).fallow for i in range(400)])
    assert 0.09 < share < 0.22 and abs(rules["crop"]["fallow"]["probability"] - 0.15) < 1e-9
    assert not crop_plan(field("crop_bare"), cfg_for(rules, types, "crop_bare", ALWAYS_FALLOW, {"fallow": {"probability": 0.0}})["crop"]).fallow


def test_spots_wet_bare_and_weedy(rules, types) -> None:
    spots = {"spots": {"wet": {"per_ha": 60}, "bare": {"per_ha": 60}, "weeds": {"per_ha": 60}}}
    c = cfg_for(rules, types, "crop_corn", QUIET, STRAIGHT, spots, {"tramlines": {"enabled": False}})
    res = crop_detail(field("crop_corn", BIG, "P-7601"), c)
    by = {p.layer: p for p in res.patches}
    assert {"crop_wet", "crop_bare_spot", "crop_weeds"} <= set(by)
    assert sum(by["crop_wet"].colour) < sum(by["crop_ground"].colour)  # wet = darker
    assert not any(by["crop_bare_spot"].geom.buffer(-0.05).contains(shapely.Point(x["x"], x["y"])) for x in plants_of(res))
    inside = [x["scale"] for x in plants_of(res) if by["crop_weeds"].geom.contains(shapely.Point(x["x"], x["y"]))]
    outside = [x["scale"] for x in plants_of(res) if not by["crop_weeds"].geom.buffer(0.5).contains(shapely.Point(x["x"], x["y"]))]
    assert inside and np.mean(inside) < 0.55 * np.mean(outside)
    # clumped: where the noise is high, not an even scatter; none when switched off
    off = cfg_for(rules, types, "crop_corn", QUIET, NO_SPOTS)
    assert not {p.layer for p in crop_detail(field("crop_corn", BIG, "P-7601"), off).patches} & {"crop_wet", "crop_bare_spot", "crop_weeds"}
    again = crop_detail(field("crop_corn", BIG, "P-7601"), c)
    assert [p.geom.wkb for p in again.patches] == [p.geom.wkb for p in res.patches]


def test_each_patch_picks_one_or_two_artifact_kinds_and_sometimes_a_lone_extra(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain", {"fallow": {"probability": 0}, "stubble": {"probability": 0}})
    extra_kinds = {e["kind"] for e in c["crop"]["artifacts"]["extras"]}
    base_kinds = {i["kind"] for i in c["crop"]["artifacts"]["items"]}
    sets, extras_seen, biggest = set(), set(), 0
    for i in range(60):
        res = crop_detail(field("crop_grain", pid=f"P-{7700 + i}"), c)
        arts = [x["kind"] for x in res.crops if x["kind"] in ARTIFACT_KINDS]
        biggest = max(biggest, len(arts))
        sets.add(frozenset(set(arts) - extra_kinds))
        extras_seen |= set(arts) & extra_kinds - base_kinds
        assert len(set(arts) & base_kinds) <= 2 or (set(arts) & extra_kinds & base_kinds)
        assert len(arts) <= int(c["crop"]["artifacts"]["max"]) + 2
    assert len(sets) >= 4  # not the same set everywhere
    assert "art_tractor" in extras_seen
    sure = cfg_for(rules, types, "crop_rapeseed", QUIET, {"artifacts": {"kinds_per_patch": [1, 1], "extras": [
        {"kind": "art_sprinkler", "probability": 1.0, "place": "edge", "group": [4, 4], "scale": [1.0, 1.0], "radius_m": 0.8, "sep_m": 5.0, "line": True}]}})
    res = crop_detail(field("crop_rapeseed", BIG, "P-7701"), sure)
    sp = [x for x in res.crops if x["kind"] == "art_sprinkler"]
    assert len(sp) >= 2  # a line of them


def test_triangle_budget_of_the_wide_blend(rules, types) -> None:
    from drycastle_pipeline.detail.patches import patch_meshes

    c = cfg_for(rules, types, "crop_grain")
    ctx = field("crop_grain", box(-50, -30, 50, 30))
    ctx.ground_around = around(("A", box(-80, -60, 0, 60), [114, 153, 107], False), ("B", box(0, -60, 80, -30), [140, 120, 90], False))
    tally: dict = {}
    patch_meshes(crop_detail(ctx, c), ctx.terrain, 0.02, tally)
    assert sum(tally.values()) < 15_000  # a 0.6 ha field with two neighbours


# --- the stage -------------------------------------------------------------------------------


@pytest.fixture(scope="module")
def ws(tmp_path_factory):
    import json

    from tests.synthetic import make_workspace, parcel

    tmp = tmp_path_factory.mktemp("crop-ws")
    work = make_workspace(tmp)["work"]
    doc = json.loads((work / "parcels.json").read_text())
    doc["parcels"] = [p for p in doc["parcels"] if p["id"] != "P-0004"] + [
        parcel("P-0004", "crop_grain", box(40, 115, 120, 190)), parcel("P-0007", "crop_potato", box(120, 115, 200, 190)),
    ]
    (work / "parcels.json").write_text(json.dumps(doc))
    from drycastle_pipeline.props.props import run_props

    rules = tmp / "rules.yaml"
    import yaml

    rules.write_text(yaml.safe_dump({"spawns": {"count": 12}}))
    run_props(work, rules)
    return work, tmp


def test_village_builds_crop_patches_instances_and_a_baulk(ws) -> None:
    work, tmp = ws
    res = build_village_detail(work, edits_path=tmp / "no-edits.json")
    layers = res.stats["patch_triangles_by_type"]["crop"]
    assert layers.get("crop_baulk", 0) + layers.get("crop_baulk_dirt", 0) > 0 and layers["crop_headland"] > 0
    kinds = {k for rows in res.crops.values() for k in rows}
    assert {"crop_wheat", "crop_potato"} <= kinds and kinds & set(ARTIFACT_KINDS)
    doc = crops_document(res)
    assert doc["format_version"] == 1 and doc["chunks"] and set(doc["kinds"]) == set(CROP_KINDS)
    assert not any(k in res.clutter.get(key, {}) for key in res.clutter for k in CROP_KINDS)  # not clutter
    # only crop units, the same instances
    only = build_village_detail(work, edits_path=tmp / "no-edits.json", recipe="crop")
    assert only.crops == res.crops


def test_crop_units_in_processes_give_the_same_village(ws) -> None:
    work, tmp = ws
    one = build_village_detail(work, edits_path=tmp / "no-edits.json", recipe="crop")
    many = build_village_detail(work, edits_path=tmp / "no-edits.json", recipe="crop", workers=3)
    assert many.crops == one.crops and many.clutter == one.clutter
    assert many.patches.tobytes() == one.patches.tobytes() and len(one.patches) > 0
    assert many.stats["patch_triangles_by_type"] == one.stats["patch_triangles_by_type"]
