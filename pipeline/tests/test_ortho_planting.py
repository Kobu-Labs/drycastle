"""Every tree comes from the orthophoto detector (props.ortho_trees); there is no other tree source."""

import json
import math
import shutil
from pathlib import Path

import numpy as np
import pytest
import shapely
import yaml
from PIL import Image
from shapely.geometry import box
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.props import make_trees, road_zone, run_props
from drycastle_pipeline.rules import load_section
from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain, make_workspace, write_ortho

TREE = load_section("props")["trees"]
OCFG = TREE["ortho"]
RES = 1.0  # metres per pixel of the synthetic canopy mask, 250 x 250 px covering local (0, 0)..(250, 250)


def cfg_for(folder: Path) -> dict:
    return {**TREE, "ortho_dir": str(folder)}


def test_trees_come_only_from_the_detector(tmp_path: Path) -> None:
    ws = make_workspace(tmp_path)
    work = ws["work"]
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    graph = json.loads((work / "roads.json").read_text())
    standalone = [
        {"x": 150.0, "y": 150.0, "radius_m": 3.0, "score": 0.3},   # a normal crown
        {"x": 160.0, "y": 60.0, "radius_m": 20.0, "score": 0.3},   # absurdly big: scale is capped
        {"x": 120.0, "y": 60.0, "radius_m": 0.5, "score": 0.3},    # tiny: scale has a floor
        {"x": 500.0, "y": 500.0, "radius_m": 3.0, "score": 0.3},   # outside the terrain: dropped
    ]
    write_ortho(tmp_path / "ortho", (0.0, 0.0), (30, 40, 90, 100), standalone)
    terrain = make_terrain()

    def make():
        return make_trees(buildings, graph, terrain, cfg_for(tmp_path / "ortho"), origin=(0.0, 0.0))

    trees = make()
    assert trees == make()  # deterministic
    assert {t["src"] for t in trees} == {"ortho", "ortho-canopy"}
    assert all(t["parcel"] is None for t in trees)

    alone = [t for t in trees if t["src"] == "ortho"]
    assert [(t["x"], t["y"]) for t in alone] == [(150.0, 150.0), (160.0, 60.0), (120.0, 60.0)]
    assert [t["scale"] for t in alone] == [pytest.approx(2 * 3.0 / TREE["canopy_m"], abs=1e-3), 3.0, 0.8]
    assert all(t["shape"] == "box" and t["colour"] in TREE["canopy_colours"] for t in alone)

    canopy = [t for t in trees if t["src"] == "ortho-canopy"]
    assert canopy and all(30 <= t["x"] < 90 and 40 <= t["y"] < 100 for t in canopy)  # inside the mask only
    lo, hi = OCFG["canopy_crown_scale"]
    assert all(lo <= t["scale"] <= hi for t in canopy)
    assert all(t["colour"] in TREE["forest_canopy_colours"] for t in canopy)
    expected = OCFG["canopy_per_1000m2"] * 60 * 60 / 1000.0  # 90 trees on the 60 x 60 m of mask
    assert 0.75 * expected <= len(canopy) <= 1.05 * expected
    gap = OCFG["canopy_min_spacing_m"]
    pts = [(t["x"], t["y"]) for t in trees]
    assert all(math.hypot(a[0] - b[0], a[1] - b[1]) >= gap - 1e-6
               for i, a in enumerate([(t["x"], t["y"]) for t in canopy]) for b in pts[: len(alone) + i])


def test_the_forest_keeps_off_buildings_and_roads(tmp_path: Path) -> None:
    ws = make_workspace(tmp_path)
    work = ws["work"]
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    graph = json.loads((work / "roads.json").read_text())
    write_ortho(tmp_path / "ortho", (0.0, 0.0), (10, 10, 210, 200), [])  # the whole terrain is "forest"
    trees = make_trees(buildings, graph, make_terrain(), cfg_for(tmp_path / "ortho"), origin=(0.0, 0.0))
    assert len(trees) > 100
    bzone = unary_union([footprint_polygon(b["footprint"]) for b in buildings]).buffer(TREE["building_clearance_m"])
    rzone = road_zone(graph, TREE["road_clearance_m"])
    xs, ys = np.array([t["x"] for t in trees]), np.array([t["y"] for t in trees])
    assert not shapely.contains_xy(bzone, xs, ys).any()
    assert not shapely.contains_xy(rzone, xs, ys).any()
    assert shapely.contains_xy(box(10, 10, 210, 200), xs, ys).all()  # and inside the terrain


def test_trees_keep_clear_of_placed_objects_and_out_of_water(tmp_path: Path) -> None:
    ws = make_workspace(tmp_path)
    work = ws["work"]
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    graph = json.loads((work / "roads.json").read_text())
    write_ortho(tmp_path / "ortho", (0.0, 0.0), (10, 10, 210, 200), [])
    objects = [(60.0, 130.0), (120.0, 150.0)]
    pond = box(150, 120, 190, 180)
    trees = make_trees(buildings, graph, make_terrain(), cfg_for(tmp_path / "ortho"), objects=objects, water=pond, origin=(0.0, 0.0))
    assert len(trees) > 100
    for t in trees:
        assert not pond.contains(shapely.Point(t["x"], t["y"]))
        for ox, oy in objects:
            assert math.hypot(t["x"] - ox, t["y"] - oy) >= TREE["object_clearance_m"] - 1e-6


def test_run_props_plants_from_the_detector_and_an_area_without_ortho_data_has_no_trees(tmp_path: Path, capsys) -> None:
    ws = make_workspace(tmp_path)
    work = ws["work"]
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"count": 5}}))
    counts = run_props(work, rules)  # the workspace has an ortho folder beside its work dir
    props = json.loads((work / "props.json").read_text())["trees"]
    assert counts["trees"] == len(props) > 0
    assert {t["src"] for t in props} == {"ortho", "ortho-canopy"} and all(t["parcel"] is None for t in props)
    assert sum(t["src"] == "ortho" for t in props) == 2
    shutil.rmtree(tmp_path / "ortho")
    assert run_props(work, rules)["trees"] == 0
    assert "no orthophoto trees" in capsys.readouterr().err
