"""Forest stands and the edge mantle (props.Stands, props.ortho_trees): determinism, shares, the edge flag, swamp near water."""

import json
import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from PIL import Image
from shapely.geometry import box

from drycastle_pipeline.props import Stands, make_trees
from drycastle_pipeline.rules import load_section

TREE = load_section("props")["trees"]
SIZE = 600  # the synthetic map: 600 x 600 m, 1 m per pixel, canopy from 50 to 550
CANOPY = (50, 550)
CFG_STANDS = {**TREE["stands"], "cell_m": 40}  # small cells: several stands in 500 m
WET = box(280, 100, 300, 500)  # a stream through the forest


def forest(tmp_path: Path, wet=None) -> list[dict]:
    mask = np.zeros((SIZE, SIZE), np.uint8)
    mask[SIZE - CANOPY[1] : SIZE - CANOPY[0], CANOPY[0] : CANOPY[1]] = 255  # row 0 is the north edge
    ortho = tmp_path / "ortho"
    ortho.mkdir(exist_ok=True)
    Image.fromarray(mask).save(ortho / "canopy.png")
    (ortho / "trees.json").write_text(json.dumps({
        "canopy": {"file": "canopy.png", "pixel_m": 1.0, "bounds_epsg5514": [0, 0, SIZE, SIZE]},
        "standalone": [],
    }))
    terrain = SimpleNamespace(coverage=box(0, 0, SIZE, SIZE), surface=lambda x, y: np.zeros(len(x)))
    cfg = {**TREE, "ortho_dir": str(ortho), "stands": CFG_STANDS,
           "ortho": {**TREE["ortho"], "canopy_per_1000m2": 8.0}}  # sparser than the village: a test of the stands
    return make_trees([], {"edges": [], "nodes": []}, terrain, cfg, origin=(0.0, 0.0), wet=wet)


def edge_distance_m(t: dict) -> float:
    return min(t["x"] - CANOPY[0], CANOPY[1] - t["x"], t["y"] - CANOPY[0], CANOPY[1] - t["y"])


def wet_distance_m(t: dict) -> float:
    return math.hypot(max(WET.bounds[0] - t["x"], 0, t["x"] - WET.bounds[2]), max(WET.bounds[1] - t["y"], 0, t["y"] - WET.bounds[3]))


def test_stands_are_deterministic_and_every_forest_tree_has_one(tmp_path) -> None:
    a, b = forest(tmp_path), forest(tmp_path)
    assert a and a == b
    assert all(t["stand"] in ("pine", "birch", "mixed", "thicket") for t in a)  # no water: no swamp


def test_stand_shares_follow_the_rules() -> None:
    stands = Stands({**TREE["stands"], "cell_m": 110}, None)
    xs = np.random.default_rng(1).random((20000, 2)) * 6000  # about 3000 cells
    got = [stands.noise(float(x), float(y)) for x, y in xs]
    for name, spec in TREE["stands"]["types"].items():
        assert abs(got.count(name) / len(got) - spec["share"]) < 0.04, name


def test_stands_shape_the_mix(tmp_path) -> None:
    trees = [t for t in forest(tmp_path) if not t.get("edge")]  # the edge mantle has its own mix and scale

    def of(name):
        return [t for t in trees if t["stand"] == name]

    def cone(ts):
        return sum(t["shape"] == "cone" for t in ts) / len(ts)

    def mean_scale(ts):
        return sum(t["scale"] for t in ts) / len(ts)

    assert cone(of("pine")) > 0.88 and cone(of("birch")) < 0.2 and 0.5 < cone(of("mixed")) < 0.7
    assert mean_scale(of("thicket")) < 0.6 * mean_scale(of("mixed")) < mean_scale(of("pine"))


def test_thicket_is_denser_and_keeps_the_scaled_spacing(tmp_path) -> None:
    trees = forest(tmp_path)
    stands = Stands(CFG_STANDS, None)

    def area(name):  # m2 of the canopy (50..550) in that stand, sampled every 5 m
        return 25 * sum(stands.noise(x, y) == name for x in range(50, 550, 5) for y in range(50, 550, 5))

    def density(name):
        return sum(t["stand"] == name for t in trees) / area(name)

    assert density("thicket") > 1.3 * density("mixed")
    pts = np.array([(t["x"], t["y"]) for t in trees if t["stand"] == "thicket"])
    d = np.hypot(pts[:, None, 0] - pts[None, :, 0], pts[:, None, 1] - pts[None, :, 1]) + np.eye(len(pts)) * 1e9
    spec = TREE["stands"]["types"]["thicket"]
    assert d.min() >= TREE["ortho"]["canopy_min_spacing_m"] * spec["spacing_scale"] - 1e-3


def test_edge_flag_only_near_the_canopy_boundary(tmp_path) -> None:
    trees = forest(tmp_path)
    edge_m = TREE["stands"]["edge"]["edge_m"]
    assert any(t.get("edge") for t in trees)
    for t in trees:
        if t.get("edge"):
            assert edge_distance_m(t) < edge_m + 1.5  # a pixel of slack
        else:
            assert edge_distance_m(t) > edge_m - 1.5, t
    # smaller toward the boundary
    rim = [t["scale"] for t in trees if t.get("edge") and t["stand"] == "mixed" and edge_distance_m(t) < 3]
    inner = [t["scale"] for t in trees if not t.get("edge") and t["stand"] == "mixed"]
    assert rim and sum(rim) / len(rim) < 0.7 * sum(inner) / len(inner)


def test_swamp_only_near_water(tmp_path) -> None:
    reach = TREE["stands"]["swamp"]["reach_m"]
    trees = forest(tmp_path, wet=WET)
    swamp = [t for t in trees if t["stand"] == "swamp"]
    assert swamp
    for t in trees:
        assert (t["stand"] == "swamp") == (wet_distance_m(t) <= reach), t
    assert sum(t["shape"] == "box" for t in swamp) / len(swamp) > 0.7
    dry = [t for t in forest(tmp_path) if wet_distance_m(t) <= reach]
    assert len(swamp) < 0.7 * len(dry)  # sparse
