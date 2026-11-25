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


def painted(mesh, colour):
    """The triangles of one colour (the tests give every new part its own colour) as (n, 3, 3)."""
    from drycastle_pipeline.core.colour import hex_to_rgb

    return mesh.tris[(mesh.colours == hex_to_rgb(colour)).all(1)]


def area(tris):
    return 0.5 * np.linalg.norm(np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]), axis=1).sum()


def with_parts(*openings, overhang=0.0, **parts):
    return parse_detail({"id": "w1", "overhang_m": overhang, "frame_colour": "#aabbcc", "openings": list(openings), **parts})


DOOR = {"id": "d", "kind": "door", "edge": 0, "at_m": 5.0, "width_m": 1.0, "height_m": 2.1}


def test_new_parts_absent_or_at_their_defaults_change_nothing():
    plain = {**WINDOW, "id": "d2", "kind": "door", "at_m": 6.0, "sill_m": 0.0}
    for roof in ("gabled", "hipped", "flat"):
        a, ta = build_detail(house(roof), detail(WINDOW, plain, overhang=0.4))
        explicit = {"frame_m": 0.0, "posts": [], "rails": [], "sill": False}
        b, tb = build_detail(house(roof), detail({**WINDOW, **explicit}, {**plain, **explicit}, overhang=0.4))
        assert np.array_equal(a.tris, b.tris) and np.array_equal(a.colours, b.colours)
        assert [t.meta for t in ta] == [t.meta for t in tb] and np.array_equal(ta[0].mesh.tris, tb[0].mesh.tris)


def test_plinth_stands_proud_and_a_door_cuts_through_it():
    mesh, _ = build_detail(house(), with_parts(DOOR, plinth={"height_m": 0.3, "colour": "#010203"}))
    band = painted(mesh, "#010203")
    assert band[..., 2].min() == 0.0 and band[..., 2].max() == pytest.approx(0.3)
    assert band[..., 1].min() == pytest.approx(-0.03) and band[..., 0].min() == pytest.approx(-0.03)  # 0.03 m proud
    front = band[(np.abs(band[..., 1] + 0.03) < 1e-9).all(axis=1)]  # edge 0, y = 0 and outward is -y
    assert area(front) == pytest.approx((10.06 - 1.0) * 0.3)  # the whole wall length (mitred corners), less the door
    assert not ((front[..., 0] > 5.01) & (front[..., 0] < 5.99)).all(axis=1).any()  # nothing across the doorway
    window = {**WINDOW, "sill_m": 0.9}  # sill above the band: no cut
    solid, _ = build_detail(house(), with_parts(window, plinth={"height_m": 0.3, "colour": "#010203"}))
    assert area(painted(solid, "#010203")) > area(band)


def test_window_frame_posts_rails_and_sill_are_shell_geometry_not_pane():
    bare, tags = build_detail(house(), with_parts(WINDOW))
    full = {**WINDOW, "frame_m": 0.06, "posts": [0.5], "rails": [0.6], "sill": True}
    mesh, tags2 = build_detail(house(), with_parts(full))
    assert tags2[0].meta == tags[0].meta and np.array_equal(tags2[0].mesh.tris, tags[0].mesh.tris)  # the pane is unchanged
    bars = painted(mesh, "#aabbcc")
    assert mesh.face_count - bare.face_count > 30
    # wall edge 0 is y = 0, outward -y; pane at 0.12 behind the wall, the bars 0.02 in front of it; sill 0.05 proud, 0.06 wider
    assert bars[..., 1].max() == pytest.approx(0.12) and bars[..., 1].min() == pytest.approx(-0.05)
    assert bars[..., 0].min() == pytest.approx(2.0 - 0.06) and bars[..., 0].max() == pytest.approx(3.0 + 0.06)
    assert bars[..., 2].min() == pytest.approx(0.9 - 0.05) and bars[..., 2].max() == pytest.approx(2.1)
    in_pane_plane = bars[(np.abs(bars[..., 1] - 0.10) < 1e-9).all(axis=1)]
    assert in_pane_plane[..., 0].min() == pytest.approx(2.0) and in_pane_plane[..., 2].max() == pytest.approx(2.1)
    centre = in_pane_plane[(in_pane_plane[..., 0] > 2.4).all(axis=1) & (in_pane_plane[..., 0] < 2.6).all(axis=1)]
    assert centre[..., 2].min() == pytest.approx(0.9 + 0.06) and centre[..., 2].max() == pytest.approx(2.1 - 0.06)  # the post
    door, _ = build_detail(house(), with_parts({**DOOR, "frame_m": 0.05}))
    assert door.face_count > build_detail(house(), with_parts(DOOR))[0].face_count


def test_eaves_close_the_rim_and_the_gutter_runs_along_the_level_edges_only():
    ea = {"colour": "#112233", "fascia_m": 0.2, "gutter": False}
    board, _ = build_detail(house(), with_parts(overhang=0.5, eaves=ea))
    fascia = painted(board, "#112233")
    # the roof's edge 0.5 m out has dropped by 0.5 * 2 / 3 below the eave, the board hangs 0.2 below it
    assert fascia[..., 2].min() == pytest.approx(3.0 - 1.0 / 3 - 0.2) and fascia[..., 2].max() == pytest.approx(5.0)  # to the gable peak
    assert fascia[..., 0].min() == pytest.approx(-0.5) and fascia[..., 1].min() == pytest.approx(-0.5)
    gutter, _ = build_detail(house(), with_parts(overhang=0.5, eaves={**ea, "gutter": True}))
    with_gutter = painted(gutter, "#112233")
    assert len(with_gutter) > len(fascia)
    assert with_gutter[..., 1].min() == pytest.approx(-0.62) and with_gutter[..., 1].max() == pytest.approx(6.62)  # 0.12 m out
    assert with_gutter[..., 0].min() == pytest.approx(-0.5)  # not along the sloping edges of the gable: nothing past x = -0.5
    assert with_gutter[..., 0].max() == pytest.approx(10.5)


def test_downpipe_runs_from_the_ground_to_the_underside_of_the_overhang():
    pipe = {"edge": 0, "at_m": 0.1, "colour": "#445566"}
    mesh, _ = build_detail(house(), with_parts(overhang=0.5, downpipes=[pipe]))
    tris = painted(mesh, "#445566")
    assert tris[..., 0].min() == pytest.approx(0.1) and tris[..., 0].max() == pytest.approx(0.19)
    assert tris[..., 1].min() == pytest.approx(-0.11) and tris[..., 1].max() == pytest.approx(-0.02)  # off the wall of edge 0
    assert tris[..., 2].min() == 0.0 and tris[..., 2].max() == pytest.approx(3.0 - 2 * 0.11 / 3 - 0.12)
    bare, _ = build_detail(house(), with_parts(downpipes=[{"edge": 0, "at_m": 0.1}]))
    assert painted(bare, "#4a4a4a")[..., 2].max() == pytest.approx(3.0)  # no overhang: up to the eave
    with pytest.raises(DetailError, match="does not fit"):
        build_detail(house(), with_parts(downpipes=[{"edge": 0, "at_m": 9.95}]))


def test_chimney_stands_on_the_roof_along_the_ridge():
    mesh, _ = build_detail(house(), with_parts(chimneys=[{"x": 5.0, "y": 3.0, "width_m": 0.8, "depth_m": 0.4, "top_m": 6.5, "colour": "#b0a090"}]))
    tris = painted(mesh, "#b0a090")
    # ridge_dir 0: the width runs along x. The ridge (y = 3) is 5 m high; the foot is 0.3 m below the lowest corner
    assert tris[..., 0].min() == pytest.approx(4.6) and tris[..., 0].max() == pytest.approx(5.4)
    assert tris[..., 1].min() == pytest.approx(2.8) and tris[..., 1].max() == pytest.approx(3.2)
    assert tris[..., 2].max() == 6.5 and tris[..., 2].min() == pytest.approx(5.0 - 2 * 0.2 / 3 - 0.3)
    default, _ = build_detail(house(), with_parts(chimneys=[{"x": 2.0, "y": 1.0, "top_m": 6.0}]))
    assert painted(default, "#e6dcbe").shape[0] > painted(build_detail(house(), with_parts())[0], "#e6dcbe").shape[0]  # wall colour
    with pytest.raises(DetailError, match="inside the footprint"):
        build_detail(house(), with_parts(chimneys=[{"x": 9.9, "y": 3.0, "top_m": 6.5}]))
    with pytest.raises(DetailError, match="above the roof"):
        build_detail(house(), with_parts(chimneys=[{"x": 5.0, "y": 3.0, "top_m": 4.0}]))


def test_new_parts_follow_a_roof_part_split():
    b = {
        "id": "w1", "footprint": {"exterior": [[0, 0], [12, 0], [12, 8], [6, 8], [6, 4], [0, 4]], "holes": []},
        "eave_height_m": 3.0, "roof_height_m": 2.0, "height_m": 5.0, "roof": "gabled", "ridge_dir": 1.5708,
        "wall_colour": [240, 240, 240], "roof_colour": [90, 90, 90], "roof_part": [[6, -1], [13, -1], [13, 9], [6, 9]], "rest_height_m": 2.5,
    }  # fmt: skip
    pipes = [{"edge": 0, "at_m": 0.1, "colour": "#445566"}, {"edge": 0, "at_m": 8.0, "colour": "#445566"}]  # extension, house
    d = with_parts(overhang=0.4, plinth={"colour": "#010203"}, eaves={"colour": "#112233"}, downpipes=pipes,
                   chimneys=[{"x": 9.0, "y": 4.0, "top_m": 6.5}])
    mesh, _ = build_detail(b, d)
    pipes_at = painted(mesh, "#445566")
    ext, house_pipe = pipes_at[(pipes_at[..., 0] < 1).all(axis=1)], pipes_at[(pipes_at[..., 0] > 7).all(axis=1)]
    assert ext[..., 2].max() == 2.5  # on the flat extension: up to its wall top
    assert house_pipe[..., 2].max() > 2.5  # on the house: under the overhang
    assert len(painted(mesh, "#010203")) > 0 and len(painted(mesh, "#112233")) > 0
    with pytest.raises(DetailError, match="chimney"):
        build_detail(b, with_parts(chimneys=[{"x": 6.0, "y": 4.0, "top_m": 6.5}]))  # on the cut between the parts' footprints


@pytest.mark.parametrize(
    "bad, match",
    [
        ({"plinth": {"height_m": 0.01}}, "height_m"),
        ({"plinth": {"height_m": 2.0}}, "height_m"),
        ({"plinth": {"colour": "nope"}}, "colour"),
        ({"plinth": {"bogus": 1}}, "unknown"),
        ({"eaves": {"fascia_m": 0.18}}, "overhang_m > 0"),
        ({"eaves": {"fascia_m": 0.9}, "overhang_m": 0.4}, "fascia_m"),
        ({"eaves": {"gutter": "yes"}, "overhang_m": 0.4}, "gutter"),
        ({"downpipes": [{"edge": 0}]}, "missing"),
        ({"downpipes": [{"edge": 0, "at_m": -1.0}]}, "at_m"),
        ({"chimneys": [{"x": 1, "y": 1, "top_m": 6, "width_m": 0.1}]}, "width_m"),
        ({"chimneys": [{"x": 1, "y": 1, "top_m": 6, "depth_m": 2.5}]}, "depth_m"),
        ({"chimneys": [{"x": 1, "y": 1}]}, "missing"),
        ({"openings": [{**WINDOW, "frame_m": 0.3}]}, "frame_m"),
        ({"openings": [{**WINDOW, "posts": [0.5]}]}, "frame_m > 0"),
        ({"openings": [{**WINDOW, "frame_m": 0.06, "posts": [1.0]}]}, "strictly between"),
        ({"openings": [{**WINDOW, "frame_m": 0.06, "rails": [0.0]}]}, "strictly between"),
        ({"openings": [{**WINDOW, "frame_m": 0.06, "posts": [0.02]}]}, "overlaps the frame"),
        ({"openings": [{**WINDOW, "kind": "door", "sill": True}]}, "windows only"),
        ({"openings": [{**WINDOW, "kind": "recess", "frame_m": 0.05}]}, "windows, doors"),
    ],
)
def test_new_keys_are_validated(bad, match):
    with pytest.raises(DetailError, match=match):
        parse_detail({"id": "w1", **bad})


def test_a_hipped_roof_overhangs_too_and_can_have_a_level_soffit():
    b = {**house(), "roof": "hipped"}
    mesh, _ = build_detail(b, detail(overhang=0.6))
    pts = mesh.tris.reshape(-1, 3)
    assert pts[:, 0].min() == pytest.approx(-0.6) and pts[:, 1].max() == pytest.approx(6.6)  # was cut off at the walls
    rim = pts[:, 2][np.isclose(pts[:, 0], -0.6)].max()
    boxed = parse_detail({"id": "w1", "overhang_m": 0.6, "eaves": {"fascia_m": 0.2, "level_soffit": True}, "openings": []})
    more, _ = build_detail(b, boxed)
    flat = [t for t in more.tris if np.allclose(t[:, 2], rim - 0.2)]  # a level board at the fascia's lower edge
    assert flat and all(np.cross(t[1] - t[0], t[2] - t[0])[2] < 0 for t in flat)  # seen from below


def test_the_gutter_can_be_another_colour_than_the_fascia():
    b = {**house(), "roof": "hipped"}
    d = parse_detail({"id": "w1", "overhang_m": 0.6, "eaves": {"colour": "#ece6d8", "gutter": True, "gutter_colour": "#39383f"}, "openings": []})
    mesh, _ = build_detail(b, d)
    colours = {tuple(int(v) for v in c) for c in mesh.colours}
    assert (0xEC, 0xE6, 0xD8) in colours and (0x39, 0x38, 0x3F) in colours
    with pytest.raises(DetailError):
        parse_detail({"id": "w1", "overhang_m": 0.6, "eaves": {"gutter_colour": "dark"}, "openings": []})


def test_a_window_can_have_a_surround_and_an_open_eave_its_rafter_tails():
    b = {**house(), "roof": "gabled"}
    plain, _ = build_detail(b, parse_detail({"id": "w1", "overhang_m": 0.3, "eaves": {"gutter": True}, "openings": [{**WINDOW}]}))
    rich = parse_detail({"id": "w1", "overhang_m": 0.3, "surround_colour": "#112233", "eaves": {"gutter": True, "rafters": True},
                         "openings": [{**WINDOW, "surround_m": 0.12}]})  # fmt: skip
    mesh, _ = build_detail(b, rich)
    assert len(mesh.tris) > len(plain.tris) + 20
    assert (0x11, 0x22, 0x33) in {tuple(int(v) for v in c) for c in mesh.colours}
    with pytest.raises(DetailError):
        parse_detail({"id": "w1", "overhang_m": 0.3, "eaves": {"rafters": True, "level_soffit": True}, "openings": []})
    with pytest.raises(DetailError):
        parse_detail({"id": "w1", "openings": [{**WINDOW, "surround_m": 0.9}]})



def colours_of(mesh):
    return {tuple(int(v) for v in c) for c in mesh.colours}


def test_trim_bands_corner_strips_and_a_gable_of_its_own_colour():
    b = {**house(), "roof": "gabled"}
    plain, _ = build_detail(b, parse_detail({"id": "w1", "openings": [{**WINDOW}]}))
    rich = parse_detail({"id": "w1", "trim_colour": "#112233", "gable_colour": "#445566", "bands": [{"top_m": b["eave_height_m"], "height_m": 0.18}],
                         "corners": {"width_m": 0.3, "top_m": 2.5}, "ridge_caps": True, "openings": [{**WINDOW, "lintel_m": 0.18}]})  # fmt: skip
    mesh, _ = build_detail(b, rich)
    assert {(0x11, 0x22, 0x33), (0x44, 0x55, 0x66)} <= colours_of(mesh)
    assert len(mesh.tris) > len(plain.tris) + 60
    gable = mesh.tris[(mesh.colours == (0x44, 0x55, 0x66)).all(axis=1)]
    assert gable[:, :, 2].min() >= b["eave_height_m"] - 1e-6  # only the triangle above the eave
    for bad in ({"bands": [{"top_m": 0.1, "height_m": 0.3}]}, {"corners": {"width_m": 2.0}}, {"ridge_caps": "yes"}, {"openings": [{**WINDOW, "panels": 2}]},
                {"openings": [{**WINDOW, "steps": 1}]}):  # fmt: skip
        with pytest.raises(DetailError):
            parse_detail({"id": "w1", "openings": [], **bad})


def test_a_door_can_have_panels_steps_and_a_canopy():
    b = house()
    plain, _ = build_detail(b, parse_detail({"id": "w1", "openings": [{**DOOR}]}))
    mesh, _ = build_detail(b, parse_detail({"id": "w1", "openings": [{**DOOR, "sill_m": 0.36, "panels": 3, "steps": 2, "canopy_m": 0.7}]}))
    assert len(mesh.tris) > len(plain.tris) + 60
    out_y = mesh.tris.reshape(-1, 3)[:, 1].min()
    assert out_y < plain.tris.reshape(-1, 3)[:, 1].min() - 0.5  # the steps and the canopy stand out in front of the wall


def test_skylights_chimney_cap_and_pipe_bends():
    b = {**house(), "roof": "gabled"}
    base = {"id": "w1", "overhang_m": 0.4, "eaves": {"gutter": True}, "openings": []}
    plain, _ = build_detail(b, parse_detail({**base, "downpipes": [{"edge": 0, "at_m": 0.2}]}))
    mesh, _ = build_detail(b, parse_detail({**base, "downpipes": [{"edge": 0, "at_m": 0.2, "bends": True}], "skylights": [{"edge": 0, "at_m": 4.0, "up_m": 0.8}]}))
    assert len(mesh.tris) == len(plain.tris) + 4 + 10 + 8 + 2  # two quads; the kick-out; the slanted piece; the pipe's top
    with pytest.raises(DetailError, match="skylight"):
        build_detail(b, parse_detail({**base, "skylights": [{"edge": 1, "at_m": 2.0, "up_m": 0.8}]}))  # a gable wall has no slope above it


def test_the_part_behind_the_house_can_have_a_single_slope_roof():
    from drycastle_pipeline.buildings.meshes import split_parts

    ring = [[0, 0], [10, 0], [10, 6], [6, 6], [6, 10], [0, 10]]
    b = {**house(), "footprint": {"exterior": ring, "holes": []}, "roof_part": [[0, 0], [10, 0], [10, 6], [0, 6]], "rest_height_m": 3.0, "rest_roof": "lean_to"}
    main, rest = split_parts(b)
    assert rest["roof"] == "lean_to" and rest["height_m"] == 3.0 and 2.0 < rest["eave_height_m"] < 3.0
    mesh, _ = build_detail(b, parse_detail({"id": "w1", "openings": []}))
    back = mesh.tris.reshape(-1, 3)
    far = back[(back[:, 1] > 9.9)][:, 2].max()
    assert abs(far - rest["eave_height_m"]) < 0.02  # low at the far wall, high against the house
    assert split_parts({**b, "rest_roof": "flat"})[1]["roof"] == "flat"
    # against a gable wall of a gabled house the wing carries the roof on: gabled, ridge the same way, lower
    side = {**b, "roof": "gabled", "ridge_dir": 0.0, "roof_height_m": 2.5, "footprint": {"exterior": [[0, 0], [10, 0], [10, 6], [0, 6], [0, 4], [-3, 4], [-3, 1], [0, 1]], "holes": []},
            "roof_part": [[0, 0], [10, 0], [10, 6], [0, 6]], "rest_roof": "auto"}  # fmt: skip
    wing = split_parts(side)[1]
    assert wing["roof"] == "gabled" and wing["ridge_dir"] == 0.0 and wing["height_m"] < side["eave_height_m"] + 2.5


def test_a_balcony_stands_out_of_its_wall():
    plain, _ = build_detail(house(), parse_detail({"id": "w1", "openings": []}))
    bal = {"edge": 3, "at_m": 0.5, "width_m": 5.0, "floor_m": 2.9}  # the gable wall at x = 0
    mesh, _ = build_detail(house(), parse_detail({"id": "w1", "openings": [], "balconies": [bal]}))
    assert mesh.face_count > plain.face_count
    assert mesh.tris[:, :, 0].min() == pytest.approx(-1.1)  # 1.1 m out of the wall
    with pytest.raises(DetailError):
        build_detail(house(), parse_detail({"id": "w1", "openings": [], "balconies": [{**bal, "width_m": 9.0}]}))
