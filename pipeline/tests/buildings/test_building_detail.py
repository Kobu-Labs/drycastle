import numpy as np
import pytest
from shapely.geometry import Polygon

from drycastle_pipeline.buildings.meshes import building_mesh
from drycastle_pipeline.buildings.openings import DetailError, build_detail, parse_detail


def house(roof="gabled"):
    ring = list(Polygon([(0, 0), (10, 0), (10, 6), (0, 6)]).exterior.coords)[:-1]
    return {
        "id": "w1", "footprint": {"exterior": [list(p) for p in ring], "holes": []},
        "eave_height_m": 3.0, "roof_height_m": 2.0, "height_m": 5.0, "roof": roof, "ridge_dir": 0.0,
        "wall_colour": [230, 220, 190], "roof_colour": [170, 70, 50],
    }


def detail(*openings, overhang=0.0):
    return parse_detail({"id": "w1", "overhang_m": overhang, "openings": list(openings)})


WINDOW = {"id": "a", "kind": "window", "edge": 0, "at_m": 2.0, "width_m": 1.0, "height_m": 1.2, "sill_m": 0.9}


def test_window_becomes_hole_and_tagged_pane():
    mesh, tags = build_detail(house(), detail(WINDOW))
    assert mesh.face_count > building_mesh(house()).face_count  # hole + reveals
    assert [t.node for t in tags] == ["window_w1_a"]
    assert tags[0].meta["breakable"] is True and tags[0].mesh.face_count == 2
    assert tags[0].meta["centre"][2] == pytest.approx(0.9 + 0.6)


def test_door_is_not_a_window_node():
    door = {**WINDOW, "id": "d", "kind": "door", "sill_m": 0.0, "height_m": 2.1}
    _, tags = build_detail(house(), detail(door))
    assert tags == []


def test_overhang_grows_the_roof_past_the_walls():
    mesh, _ = build_detail(house(), detail(overhang=0.5))
    pts = mesh.tris.reshape(-1, 3)
    assert pts[:, 0].min() == pytest.approx(-0.5) and pts[:, 1].max() == pytest.approx(6.5)
    assert np.isfinite(pts).all()


def test_opening_must_fit_the_wall():
    with pytest.raises(DetailError, match="does not fit"):
        build_detail(house(), detail({**WINDOW, "at_m": 9.5}))
    with pytest.raises(DetailError, match="edge"):
        build_detail(house(), detail({**WINDOW, "edge": 9}))


def test_validation():
    with pytest.raises(DetailError, match="kind"):
        detail({**WINDOW, "kind": "hatch"})
    with pytest.raises(DetailError, match="duplicate"):
        detail(WINDOW, WINDOW)
    with pytest.raises(DetailError, match="unknown"):
        detail({**WINDOW, "bogus": 1})


def test_nested_opening_in_a_recess():
    bay = {"id": "bay", "kind": "recess", "edge": 0, "at_m": 1.0, "width_m": 5.0, "height_m": 2.6, "recess_m": 0.6}
    door = {"id": "dr", "kind": "door", "edge": 0, "at_m": 2.0, "width_m": 0.9, "height_m": 2.1, "recess_m": 0.08, "parent": "bay"}
    win = {**WINDOW, "id": "w", "at_m": 4.0, "parent": "bay"}
    mesh, tags = build_detail(house(), detail(bay, door, win))
    assert [t.node for t in tags] == ["window_w1_w"]
    # wall edge 0 is y = 0 and outward is -y: the pane sits 0.6 (bay) + 0.12 (default window recess) behind the wall plane
    assert tags[0].meta["centre"][1] == pytest.approx(0.72)
    assert np.isfinite(mesh.tris).all()


def test_child_must_lie_inside_parent_recess():
    bay = {"id": "bay", "kind": "recess", "edge": 0, "at_m": 1.0, "width_m": 2.0, "height_m": 2.6, "recess_m": 0.5}
    with pytest.raises(DetailError, match="inside"):
        detail(bay, {**WINDOW, "at_m": 2.5, "parent": "bay"})
    with pytest.raises(DetailError, match="recess"):
        detail({**WINDOW, "parent": "nope"})


def test_a_roof_part_splits_the_building_and_keeps_its_openings():
    from drycastle_pipeline.buildings.meshes import building_mesh, collision_mesh, split_parts
    from drycastle_pipeline.buildings.openings import build_detail, parse_detail

    # an L: a 6 x 8 house in the east, a 6 x 4 extension in the south-west; the roof belongs to the house
    b = {
        "id": "w1", "footprint": {"exterior": [[0, 0], [12, 0], [12, 8], [6, 8], [6, 4], [0, 4]], "holes": []},
        "eave_height_m": 3.0, "roof_height_m": 2.0, "height_m": 5.0, "roof": "gabled", "ridge_dir": 1.5708,
        "wall_colour": [240, 240, 240], "roof_colour": [90, 90, 90], "roof_part": [[6, -1], [13, -1], [13, 9], [6, 9]], "rest_height_m": 2.5,
    }  # fmt: skip
    main, rest = split_parts(b)
    assert main["roof"] == "gabled" and rest["roof"] == "flat" and rest["height_m"] == 2.5 and "roof_part" not in main
    assert split_parts({k: v for k, v in b.items() if k != "roof_part"})[0]["roof"] == "gabled"
    plain, col = building_mesh(b), collision_mesh(b)
    assert plain.tris[..., 2].max() == pytest.approx(5.0) and col.face_count > 0
    # the ridge is over the house (x = 9), not over the middle of the whole footprint (x = 6)
    top = plain.tris.reshape(-1, 3)
    assert top[top[:, 2] > 4.99][:, 0] == pytest.approx(9.0, abs=0.03)
    assert top[(top[:, 0] < 5.9)][:, 2].max() == pytest.approx(2.5)  # the extension is a flat block
    lean = {**b, "roof": "lean_to", "ridge_dir": 1.5708, "rest_height_m": 5.0}  # one slope rising to the west, flat behind
    lt = building_mesh(lean).tris.reshape(-1, 3)
    assert lt[:, 2].max() == pytest.approx(5.0) and lt[lt[:, 0] > 11.9][:, 2].max() == pytest.approx(3.0, abs=0.05)
    assert lt[(lt[:, 0] > 5.9) & (lt[:, 0] < 6.1)][:, 2].max() == pytest.approx(5.0, abs=0.15)  # the top of the slope meets the flat part
    d = parse_detail({"id": "w1", "openings": [
        {"id": "a", "kind": "window", "edge": 0, "at_m": 8.0, "width_m": 1.2, "height_m": 1.2, "sill_m": 0.9},  # on the house
        {"id": "b", "kind": "door", "edge": 0, "at_m": 2.0, "width_m": 1.0, "height_m": 2.0},  # on the extension
    ]})  # fmt: skip
    _, tags = build_detail(b, d)
    assert [t.meta["id"] for t in tags] == ["a"] and tags[0].meta["centre"][0] == pytest.approx(8.6, abs=0.01)
    across = parse_detail({"id": "w1", "openings": [{"id": "x", "kind": "window", "edge": 0, "at_m": 5.5, "width_m": 1.2, "height_m": 1.2, "sill_m": 0.9}]})
    with pytest.raises(DetailError):
        build_detail(b, across)
