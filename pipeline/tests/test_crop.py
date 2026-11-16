"""The crop recipe (#328): deterministic, soft or sharp edge, baulk between adjoining patches, artifacts on every crop."""

import numpy as np
import pytest
import shapely
from shapely.geometry import Polygon, box

from drycastle_pipeline import detail as D
from drycastle_pipeline.crop import ARTIFACT_KINDS, CROP_KINDS, PLANT_KINDS, crop_detail, plant_density
from drycastle_pipeline.ground import ground_types
from drycastle_pipeline.ground_attributes import attributes_of
from drycastle_pipeline.rules import _merge, load_section
from tests.test_detail import flat_terrain

CROPS = ("crop_grain", "crop_corn", "crop_potato", "crop_rapeseed", "crop_bare")


@pytest.fixture(scope="module")
def rules() -> dict:
    return load_section("detail")


@pytest.fixture(scope="module")
def types() -> dict:
    return ground_types()


def cfg_for(rules: dict, types: dict, kind: str) -> dict:
    return {**rules, "crop": _merge(rules["crop"], attributes_of(types[kind])["detail"])}


def field(kind: str, poly=None, pid="P-7001", neighbours=None) -> D.ParcelContext:
    return D.ParcelContext(
        id=pid, kind=kind, polygon=poly or box(0, 0, 60, 40), buildings=[], fences=[], trees=[], road=None,
        terrain=flat_terrain(), tree_cfg={}, crop_neighbours=neighbours,
    )


def test_every_crop_is_a_drawn_only_type_of_the_crop_recipe(types) -> None:
    for k in CROPS:
        a = attributes_of(types[k])
        assert a["recipe"] == "crop" and "osm" not in types[k] and "rule" not in types[k]
        assert D.area_type({"id": "A-1", "kind": k}, types) == "crop"


def test_same_patch_gives_identical_output(rules, types) -> None:
    c = cfg_for(rules, types, "crop_grain")
    a, b = crop_detail(field("crop_grain"), c), crop_detail(field("crop_grain"), c)
    assert a.crops == b.crops and a.crops
    assert [p.geom.wkb for p in a.patches] == [p.geom.wkb for p in b.patches]
    assert crop_detail(field("crop_grain", pid="P-7002"), c).crops != a.crops  # the seed is the patch


@pytest.mark.parametrize("kind", CROPS)
def test_every_crop_has_artifacts_and_only_its_own_kinds(rules, types, kind) -> None:
    res = crop_detail(field(kind), cfg_for(rules, types, kind))
    arts = [c for c in res.crops if c["kind"] in ARTIFACT_KINDS]
    assert 2 <= len(arts) <= 10
    allowed = {i["kind"] for i in attributes_of(types[kind])["detail"]["artifacts"]["items"]}
    assert {c["kind"] for c in arts} <= allowed
    assert {c["kind"] for c in res.crops} <= set(CROP_KINDS)
    plants = {c["kind"] for c in res.crops if c["kind"] in PLANT_KINDS}
    assert plants == ({"crop_wheat"} if kind == "crop_grain" else {"crop_corn"} if kind == "crop_corn"
                      else {"crop_potato"} if kind == "crop_potato" else {"crop_rapeseed"} if kind == "crop_rapeseed" else set())


def test_a_tiny_patch_has_no_artifacts(rules, types) -> None:
    res = crop_detail(field("crop_grain", box(0, 0, 8, 8)), cfg_for(rules, types, "crop_grain"))
    assert not [c for c in res.crops if c["kind"] in ARTIFACT_KINDS]


def test_instances_stay_inside_and_budget_is_sane(rules, types) -> None:
    poly = box(0, 0, 60, 40)
    for kind in CROPS:
        res = crop_detail(field(kind, poly), cfg_for(rules, types, kind))
        assert all(poly.buffer(1e-6).contains(shapely.Point(c["x"], c["y"])) for c in res.crops)
        assert len(res.crops) < 12_000  # 0.24 ha


def test_plants_thin_out_toward_a_feathered_edge_and_stop_dead_at_a_wall(rules, types) -> None:
    poly = box(0, 0, 80, 60)
    grain = crop_detail(field("crop_grain", poly), cfg_for(rules, types, "crop_grain"))
    d = np.array([min(c["x"], 80 - c["x"], c["y"], 60 - c["y"]) for c in grain.crops if c["kind"] == "crop_wheat"])
    per_m2 = lambda lo, hi: ((d >= lo) & (d < hi)).sum() / (perimeter_band(poly, lo, hi))  # noqa: E731
    assert per_m2(0.0, 1.0) < 0.35 * per_m2(5.0, 8.0)
    assert per_m2(1.5, 3.0) < per_m2(5.0, 8.0)
    corn_cfg = cfg_for(rules, types, "crop_corn")
    corn = crop_detail(field("crop_corn", poly), corn_cfg)
    dc = np.array([min(c["x"], 80 - c["x"], c["y"], 60 - c["y"]) for c in corn.crops if c["kind"] == "crop_corn"])
    margin = float(corn_cfg["crop"]["edge"]["plant_margin_m"])
    assert dc.min() >= margin - 0.15
    assert per_m2_of(dc, margin + 0.2, margin + 1.4, poly) > 0.7 * per_m2_of(dc, 6.0, 9.0, poly)  # abrupt: full right at the wall


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
    res = crop_detail(field("crop_grain"), cfg_for(rules, types, "crop_grain"))
    bands = [p for p in res.patches if p.layer.startswith("crop_edge_")]
    assert len(bands) >= 3
    sur = np.array(D.hex_to_rgb(rules["crop"]["surround"]))
    core = np.array(next(p for p in res.patches if p.layer == "crop_ground").colour)
    dist = [np.linalg.norm(np.array(b.colour) - sur) for b in bands]
    assert dist == sorted(dist) and dist[-1] < np.linalg.norm(core - sur)  # outer band nearest the surround
    walled = crop_detail(field("crop_corn"), cfg_for(rules, types, "crop_corn"))
    assert not [p for p in walled.patches if p.layer.startswith("crop_edge_")]


def test_headland_is_broken_not_a_clean_band(rules, types) -> None:
    res = crop_detail(field("crop_grain"), cfg_for(rules, types, "crop_grain"))
    head = next(p for p in res.patches if p.layer == "crop_headland")
    assert head.texture == "dirt" and len(D._polys(head.geom)) > 4
    assert head.geom.area < 0.6 * (box(0, 0, 60, 40).area - box(2, 2, 58, 38).area)


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
    res = crop_detail(field("crop_potato", box(0, 0, 60, 30)), cfg_for(rules, types, "crop_potato"))
    rows = next(p for p in res.patches if p.layer == "crop_rows")
    assert rows.texture == "soil_rows" and abs(abs(np.cos(rows.angle)) - 1) < 1e-6  # along the 60 m side
    assert {p.texture for p in crop_detail(field("crop_bare"), cfg_for(rules, types, "crop_bare")).patches if p.layer == "crop_rows"} == {"dirt"}


def test_patches_are_disjoint(rules, types) -> None:
    for kind in CROPS:
        res = crop_detail(field(kind), cfg_for(rules, types, kind))
        geoms = [p.geom for p in res.patches]
        for i, g in enumerate(geoms):
            for h in geoms[i + 1:]:
                assert g.intersection(h).area < 0.05


def test_crops_document_and_chunks(rules, types) -> None:
    res = crop_detail(field("crop_rapeseed"), cfg_for(rules, types, "crop_rapeseed"))
    det = D.VillageDetail(250.0, {}, np.zeros((0, 19)), [], {}, None, {(0, 0): {}})
    for c in res.crops:
        det.crops[(0, 0)].setdefault(c["kind"], []).append([c["x"], c["y"], c["z"], c["rot"], c["scale"]])
    doc = D.crops_document(det, rules | {"crop_kinds": rules["crop_kinds"]})
    assert set(doc["kinds"]) == set(CROP_KINDS) == set(rules["crop_kinds"])
    assert doc["kinds"]["crop_rapeseed"]["asset"] == "pack:drycastle-generated-models/rapeseed_plant"
    assert all(len(r) == 5 for rows in doc["chunks"]["0_0"].values() for r in rows)


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
    from drycastle_pipeline.props import run_props

    rules = tmp / "rules.yaml"
    import yaml

    rules.write_text(yaml.safe_dump({"spawns": {"count": 12}}))
    run_props(work, rules)
    return work, tmp


def test_village_builds_crop_patches_instances_and_a_baulk(ws) -> None:
    work, tmp = ws
    res = D.build_village_detail(work, edits_path=tmp / "no-edits.json")
    layers = res.stats["patch_triangles_by_type"]["crop"]
    assert layers.get("crop_baulk", 0) + layers.get("crop_baulk_dirt", 0) > 0 and layers["crop_headland"] > 0
    kinds = {k for rows in res.crops.values() for k in rows}
    assert {"crop_wheat", "crop_potato"} <= kinds and kinds & set(ARTIFACT_KINDS)
    doc = D.crops_document(res)
    assert doc["format_version"] == 1 and doc["chunks"] and set(doc["kinds"]) == set(CROP_KINDS)
    assert not any(k in res.clutter.get(key, {}) for key in res.clutter for k in CROP_KINDS)  # not clutter
    # only crop units, the same instances
    only = D.build_village_detail(work, edits_path=tmp / "no-edits.json", recipe="crop")
    assert only.crops == res.crops


def test_append_crops_after_the_engine(ws) -> None:
    work, tmp = ws
    out = work / "after-engine"
    out.mkdir(exist_ok=True)
    base = D.build_village_detail(work, edits_path=tmp / "no-edits.json", only={"P-0001"})  # stands in for the engine's files
    D.write_detail(out, base)
    n = len(np.load(out / "detail_patches.npy"))
    assert not (out / "detail_crops.json").exists()
    for name in ("parcels.json", "buildings.json", "roads.json", "props.json", "terrain.json", "terrain_heights.npy", "terrain_valid.npy"):
        if (work / name).is_file():
            (out / name).write_bytes((work / name).read_bytes())
    stats = D.append_crops(out, edits_path=tmp / "no-edits.json")
    assert stats["crops"]["crop_wheat"] > 0 and (out / "detail_crops.json").is_file()
    assert len(np.load(out / "detail_patches.npy")) > n
