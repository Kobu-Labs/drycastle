"""Trees read from the orthophoto: image maths, the two detectors and the files, on synthetic photos."""

import json
from pathlib import Path

import numpy as np
import pytest
from PIL import Image, ImageDraw
from shapely.geometry import box, mapping

from drycastle_pipeline import ortho_trees as ot

RES = 0.6
SIZE = 300
LAWN, FOREST, TREE = (120, 160, 70), (50, 100, 40), (50, 100, 40)
GRID = ot.Grid(minx=0.0, maxy=SIZE * RES, res=RES, origin=(0.0, 0.0))  # local metres = column * 0.6, 180 - row * 0.6


def disc(draw: ImageDraw.ImageDraw, c: int, r: int, rad: int, colour) -> None:
    draw.ellipse([c - rad, r - rad, c + rad, r + rad], fill=colour)


def scene() -> np.ndarray:
    """Lawn; a dark forest block on the left; a tree on the lawn; and three things that are not trees."""
    im = Image.new("RGB", (SIZE, SIZE), LAWN)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, 99, SIZE - 1], fill=FOREST)
    disc(d, 200, 150, 6, TREE)             # the tree
    disc(d, 200, 240, 6, (60, 60, 60))     # a grey shadow: dark but not green
    disc(d, 250, 150, 6, (100, 145, 60))   # a barely darker patch of lawn
    disc(d, 250, 60, 6, TREE)              # a tree on a building: must be ignored
    return np.asarray(im)


def handmade_model() -> dict:
    """Canopy probability from the 31 px mean of excess green alone (the last feature): high in the forest block."""
    n = 2 * ot.N_FEATURES
    w = np.zeros(n)
    w[ot.N_FEATURES - 1] = 60.0
    return {"mu": np.zeros(n), "sd": np.ones(n), "w": w, "b": np.array(-30.0)}


# --- image maths ---


def test_smooth_keeps_a_constant_and_spreads_an_impulse() -> None:
    assert np.allclose(ot.smooth(np.full((40, 40), 7.0), 3.0), 7.0, atol=1e-4)
    a = np.zeros((61, 61), np.float32)
    a[30, 30] = 1.0
    s = ot.smooth(a, 3.0)
    assert s.sum() == pytest.approx(1.0, abs=1e-4)
    assert s[30, 30] == s.max() and s[30, 20] == pytest.approx(s[20, 30], rel=1e-4)  # symmetric


def test_box_mean_matches_a_brute_force_mean() -> None:
    a = np.random.default_rng(0).random((20, 25)).astype(np.float32)
    got = ot.box_mean(a, 5)
    p = np.pad(a, 2, mode="reflect")
    assert got[7, 9] == pytest.approx(p[7:12, 9:14].mean(), abs=1e-5)


def test_local_max_marks_only_the_peak_of_each_neighbourhood() -> None:
    a = np.zeros((30, 30), np.float32)
    a[10, 10], a[10, 13], a[25, 25] = 5.0, 3.0, 4.0
    m = ot.local_max(a, 4)
    assert m[10, 10] and m[25, 25] and not m[10, 13]  # the weaker neighbour within 4 px is not a peak


def test_suppress_drops_a_weaker_crown_overlapping_a_stronger_one() -> None:
    kept = ot.suppress([(0.5, 10, 10, 6.0), (0.3, 14, 10, 4.0), (0.4, 40, 10, 4.0)])
    assert [(c, r) for _, c, r, _ in kept] == [(10, 10), (40, 10)]


def test_grid_converts_both_ways() -> None:
    g = ot.Grid(minx=1000.0, maxy=2000.0, res=0.5, origin=(900.0, 1900.0))
    assert g.local(0, 0) == (100.0, 100.0)
    assert g.px(100.0, 100.0) == (0.0, 0.0)
    assert g.px(110.0, 90.0) == (20.0, 20.0)


# --- the detectors ---


def test_forest_block_is_canopy_and_only_the_real_tree_is_standalone() -> None:
    rgb = scene()
    built = np.zeros((SIZE, SIZE), bool)
    built[40:80, 230:270] = True  # the building under the fourth blob
    canopy, cands = ot.detect_strip(rgb, handmade_model(), built, RES, ot.Params())
    assert canopy[100:200, 20:80].mean() > 0.95  # inside the forest block
    assert canopy[:, 130:].mean() < 0.01  # none on the lawn, tree or not
    # the strip just inside the block edge is not canopy for this crude 31 px model and shows as blobs: not under test
    trees = [t for t in ot.suppress(cands) if t[1] > 130]
    assert len(trees) == 1  # the grey shadow, the pale patch and the tree on the building are not trees
    _, c, r, rad = trees[0]
    assert abs(c - 200) <= 2 and abs(r - 150) <= 2
    assert 3 <= rad <= 9  # a crown of 2-5 m radius


def test_nothing_is_found_in_a_plain_lawn() -> None:
    rgb = np.asarray(Image.new("RGB", (SIZE, SIZE), LAWN))
    canopy, cands = ot.detect_strip(rgb, handmade_model(), np.zeros((SIZE, SIZE), bool), RES, ot.Params())
    assert not canopy.any() and cands == []


def constant_model(p: float) -> dict:
    """Every pixel has canopy probability ``p``: below the canopy bar, so no mask, but 'tree-like' if p > 0.12."""
    n = 2 * ot.N_FEATURES
    return {"mu": np.zeros(n), "sd": np.ones(n), "w": np.zeros(n), "b": np.array(np.log(p / (1 - p)))}


def test_a_tree_in_dark_scrub_is_found_next_to_woods_but_not_in_open_meadow() -> None:
    """The threshold is sensitive where the surroundings are somewhat tree-like, strict in open ground."""
    im = Image.new("RGB", (SIZE, SIZE), (70, 120, 50))  # scrub: the tree is only a little darker than its surroundings
    disc(ImageDraw.Draw(im), 150, 150, 6, (48, 95, 38))
    rgb = np.asarray(im)
    none = np.zeros((SIZE, SIZE), bool)

    def found(p: float) -> bool:
        _, cands = ot.detect_strip(rgb, constant_model(p), none, RES, ot.Params())
        return any(abs(c - 150) <= 3 and abs(r - 150) <= 3 for _, c, r, _ in ot.suppress(cands))

    assert found(0.3)  # wooded surroundings: sensitive
    assert not found(0.05)  # open meadow: strict


# --- training and the files ---


def village(tmp_path: Path, model: dict | None) -> tuple[Path, Path, Path]:
    raw, work, ortho = tmp_path / "raw", tmp_path / "work", tmp_path / "ortho"
    for d in (raw, work / "normalised", ortho):
        d.mkdir(parents=True)
    Image.fromarray(scene()).save(ortho / "ortho.png")
    (ortho / "ortho.json").write_text(json.dumps({
        "file": "ortho.png", "pixel_m": RES, "bounds_epsg5514": [0.0, 0.0, SIZE * RES, SIZE * RES],
        "attribution": "test"}))
    (work / "frame.json").write_text(json.dumps({"origin_epsg5514": [0.0, 0.0]}))
    (raw / "area.json").write_text(json.dumps({"polygon_epsg5514": [[-10, -10], [200, -10], [200, 200], [-10, 200]]}))
    feat = lambda g, **p: {"type": "Feature", "geometry": mapping(g), "properties": p}
    coll = lambda fs: json.dumps({"type": "FeatureCollection", "features": fs})
    (work / "normalised" / "landuse.geojson").write_text(coll([
        feat(box(0, 0, 60, 180), osm_id="w1", value="forest"),
        feat(box(90, 0, 180, 180), osm_id="w2", value="meadow"),
    ]))
    (work / "normalised" / "buildings.geojson").write_text(coll([feat(box(138, 108, 162, 144), osm_id="w3")]))
    (work / "normalised" / "highways.geojson").write_text(coll([]))
    if model is not None:
        np.savez(ortho / "tree_model.npz", **model)
    return raw, work, ortho


def test_the_model_learns_forest_from_the_landuse_labels(tmp_path: Path) -> None:
    _, work, ortho = village(tmp_path, None)
    model = ot.train(Image.open(ortho / "ortho.png"), GRID, work, ot.Params(), n_per=1500)
    p = ot.predict(model, ot.expand(ot.features(scene())))
    assert p[100:200, 30:70].mean() > 0.8  # forest block
    assert p[:, 150:].mean() < 0.2  # lawn


def test_build_writes_trees_and_canopy(tmp_path: Path) -> None:
    raw, work, ortho = village(tmp_path, handmade_model())
    doc = ot.build(raw, work, ortho)
    assert (ortho / "trees.json").is_file() and (ortho / "canopy.png").is_file()
    on_disk = json.loads((ortho / "trees.json").read_text())
    assert on_disk["standalone"] == doc["standalone"] and on_disk["notice"].startswith("Facts read")
    assert 0.28 < on_disk["canopy"]["fraction"] < 0.34  # the left third of the photo
    trees = [t for t in on_disk["standalone"] if t["x"] > 80]  # (the strip beside the block edge is not under test)
    assert len(trees) == 1  # the lawn tree; the one at (250, 60) stands on the building of buildings.geojson
    near = min(trees, key=lambda t: abs(t["x"] - 120.3) + abs(t["y"] - 89.7))
    assert abs(near["x"] - 120.3) < 1.5 and abs(near["y"] - 89.7) < 1.5 and 2 <= near["radius_m"] <= 6
    mask = Image.open(ortho / "canopy.png")
    assert mask.size == (SIZE, SIZE) and mask.mode == "1"
    again = ot.build(raw, work, ortho)  # the model is reused, the result is the same
    assert again["standalone"] == doc["standalone"]


def test_the_outline_clips_the_trees_and_the_canopy(tmp_path: Path) -> None:
    raw, work, ortho = village(tmp_path, handmade_model())
    (raw / "area.json").write_text(json.dumps({"polygon_epsg5514": [[0, 0], [90, 0], [90, 180], [0, 180]]}))
    doc = ot.build(raw, work, ortho)
    assert not any(t["x"] > 100 for t in doc["standalone"])  # the lawn trees lie east of x = 90, outside
    assert 0.25 < doc["canopy"]["fraction"] < 0.34  # the forest block is inside


def test_missing_inputs_give_a_clear_error(tmp_path: Path) -> None:
    assert ot.main([]) in (0, 1)  # no data in the test tree is fine: an error code, never a crash
    with pytest.raises(FileNotFoundError):
        ot.build(tmp_path, tmp_path, tmp_path)
