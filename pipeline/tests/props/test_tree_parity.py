"""The drawn tree (propmeshes.tree_mesh), trees.json's model block (export/docs.py forest_model) and the collision solids
(godot/ShooterCore/Colliders/PropColliders.cs) describe one tree.

The golden file godot/ShooterCore.Tests/fixtures/tree_parity.json holds, per sample tree, the vertices of each drawn piece
(trunk, crown tiers). This test checks the mesh against it; the C# test PropCollidersTests checks the solids' points against the
same file. Change the tree model or the rules: regenerate with ``UPDATE_TREE_PARITY=1 uv run pytest tests/test_tree_parity.py``.
"""

import json
import os

import numpy as np
import pytest

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.config import REPO_ROOT
from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.export.docs import forest_model
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.props.propmeshes import is_forest, tree_mesh
from drycastle_pipeline.props.stands import age_class

TREE = load_section("props")["trees"]
GOLDEN = REPO_ROOT / "godot" / "ShooterCore.Tests" / "fixtures" / "tree_parity.json"

BASE = {"x": 12.5, "y": -3.25, "z": 130.0, "colour": "#4f8f45"}
TREES = {
    "forest_cone": {**BASE, "scale": 2.1, "rot": 0.4, "shape": "cone", "forest": True},
    "forest_box": {**BASE, "scale": 2.6, "rot": 1.1, "shape": "box", "forest": True},
    "standalone_box": {**BASE, "scale": 1.1, "rot": 0.7, "shape": "box", "forest": False},
    "standalone_cone": {**BASE, "scale": 0.9, "rot": 0.2, "shape": "cone", "forest": False},
}
# triangles per drawn piece, in drawing order: trunk, then the crown tiers (box 10 or 12, pyramid 6)
PIECES = {
    "forest_cone": [10, 6, 6, 6],
    "forest_box": [10, 12, 10],
    "standalone_box": [10, 12, 10],
    "standalone_cone": [10, 6, 6],
}


def group_points(mesh, pieces: list[int]) -> list[list[list[float]]]:
    """Per piece the sorted distinct vertices of its triangles (a pyramid has 5, a box or taper 8)."""
    out, i = [], 0
    for n in pieces:
        pts = {tuple(np.round(v, 4)) for tri in mesh.tris[i : i + n] for v in tri}
        out.append([list(p) for p in sorted(pts)])
        i += n
    assert i == mesh.face_count
    return out


def expected() -> dict:
    return {
        "note": "Drawn pieces of sample trees (propmeshes.tree_mesh), checked against the collision solids by PropCollidersTests. "
        "Regenerate: UPDATE_TREE_PARITY=1 uv run pytest tests/test_tree_parity.py (in pipeline/).",
        "model": {
            "trunk_width_m": TREE["trunk_width_m"],
            "trunk_height_m": TREE["trunk_height_m"],
            "canopy_m": TREE["canopy_m"],
            "forest": forest_model(TREE),
        },
        "trees": [
            {"name": n, "tree": t, "pieces": PIECES[n], "points": group_points(tree_mesh(t, TREE), PIECES[n])}
            for n, t in TREES.items()
        ],
    }


def test_golden_matches_the_mesh() -> None:
    want = expected()
    if os.environ.get("UPDATE_TREE_PARITY"):
        GOLDEN.write_text(json.dumps(want, indent=1) + "\n")
    got = json.loads(GOLDEN.read_text())
    assert got["model"] == json.loads(json.dumps(want["model"]))  # the rules' numbers, as trees.json carries them
    assert [t["name"] for t in got["trees"]] == list(TREES)
    for g, w in zip(got["trees"], want["trees"]):
        assert g["tree"] == w["tree"] and g["pieces"] == w["pieces"]
        assert np.allclose(np.concatenate([np.array(p) for p in g["points"]]), np.concatenate([np.array(p) for p in w["points"]]), atol=1e-3), g["name"]


def test_forest_trees_are_tall_with_a_clear_trunk_and_stay_cheap() -> None:
    for name in ("forest_cone", "forest_box"):
        t = TREES[name]
        m = tree_mesh(t, TREE)
        assert m.face_count <= 40
        z = m.tris.reshape(-1, 3)[:, 2] - t["z"]
        s = t["scale"]
        f = TREE["forest"]
        crown_base = min(tier[0] for tier in f["cone_tiers" if name == "forest_cone" else "box_tiers"]) * s
        assert 4.0 <= crown_base <= 6.5 and 15 <= z.max() <= 28
        # nothing but the trunk below the crown base: the trunk is the first piece
        below = m.tris[10:].reshape(-1, 3)[:, 2] - t["z"]
        assert below.min() >= crown_base - 1e-6


def test_standalone_tree_is_unchanged_by_the_forest_model() -> None:
    t = TREES["standalone_box"]
    without = {k: v for k, v in TREE.items() if k != "forest"}
    assert np.array_equal(tree_mesh(t, TREE).tris, tree_mesh(t, without).tris)
    # a record without the flag (older props.json) is a standalone tree, unless its src says it is a forest tree
    old = {k: v for k, v in t.items() if k != "forest"}
    assert not is_forest(old) and is_forest({**old, "src": "ortho-canopy"})
    assert np.array_equal(tree_mesh(old, TREE).tris, tree_mesh(t, TREE).tris)


def test_forest_tree_without_the_forest_model_falls_back_to_the_plain_tree() -> None:
    without = {k: v for k, v in TREE.items() if k != "forest"}
    t = TREES["forest_cone"]
    assert tree_mesh(t, without).face_count == 22


def test_age_classes_follow_their_shares_and_are_stable() -> None:
    ages = [(a["name"], a["share"], a["scale"]) for a in TREE["ortho"]["age_classes"]]
    draws = [age_class(ages, seeded(f"t:{i}")) for i in range(4000)]
    assert draws == [age_class(ages, seeded(f"t:{i}")) for i in range(4000)]
    total = sum(s for _, s, _ in ages)
    for name, share, factor in ages:
        got = sum(1 for n, _ in draws if n == name) / len(draws)
        assert got == pytest.approx(share / total, abs=0.03)
        assert {f for n, f in draws if n == name} == {factor}
    assert age_class([], seeded("x")) == ("", 1.0)


def test_forest_trunk_colour_is_its_own_and_standalone_trees_keep_the_global_one() -> None:
    f, s = TREES["forest_box"], TREES["standalone_box"]
    trunk = lambda m: tuple(m.colours[0])  # noqa: E731 - the trunk is the first piece
    assert trunk(tree_mesh(f, TREE)) == tuple(hex_to_rgb(TREE["forest"]["trunk_colour"]))
    assert trunk(tree_mesh(s, TREE)) == tuple(hex_to_rgb(TREE["trunk_colour"]))
    # unset: the forest tree falls back to the global trunk colour
    plain = {**TREE, "forest": {k: v for k, v in TREE["forest"].items() if k != "trunk_colour"}}
    assert trunk(tree_mesh(f, plain)) == tuple(hex_to_rgb(TREE["trunk_colour"]))
