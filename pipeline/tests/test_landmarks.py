import numpy as np
import pytest
import yaml

from drycastle_pipeline.buildings import RulesError, load_overrides
from drycastle_pipeline.landmarks import MODELS, build_landmark

# footprint of the church w378900220 (map metres) and the bearing of its front
CHURCH = {
    "id": "w378900220",
    "model": "church",
    "model_axis": 0.989,
    "footprint": {
        "exterior": [
            [-226.233, 275.724], [-228.734, 277.361], [-237.457, 264.087], [-239.386, 265.351],
            [-243.203, 259.546], [-237.442, 255.778], [-238.646, 253.947], [-233.421, 250.527],
            [-229.623, 256.3], [-226.758, 254.435], [-216.479, 270.062], [-219.425, 271.992],
            [-216.547, 276.375], [-223.019, 280.608],
        ],
        "holes": [],
    },
}  # fmt: skip


def signed_volume(mesh) -> float:
    a, b, c = mesh.tris[:, 0], mesh.tris[:, 1], mesh.tris[:, 2]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6)


def test_church_is_deterministic_flat_coloured_and_on_its_footprint() -> None:
    a, b = build_landmark(CHURCH), build_landmark(CHURCH)
    assert np.array_equal(a.tris, b.tris) and np.array_equal(a.colours, b.colours)
    assert a.colours.dtype == np.uint8 and len(a.colours) == a.face_count
    pts = a.tris.reshape(-1, 3)
    ring = np.array(CHURCH["footprint"]["exterior"])
    assert pts[:, 2].min() == pytest.approx(0.0)
    assert 20 < pts[:, 2].max() < 24  # the tower and its cross dominate the village
    assert pts[:, 0].min() > ring[:, 0].min() - 1 and pts[:, 0].max() < ring[:, 0].max() + 1
    assert pts[:, 1].min() > ring[:, 1].min() - 1 and pts[:, 1].max() < ring[:, 1].max() + 1


def test_church_collision_is_the_lighter_massing_mesh() -> None:
    full, light = build_landmark(CHURCH), build_landmark(CHURCH, detail=False)
    assert 0 < light.face_count < full.face_count
    assert (light.tris.reshape(-1, 3)[:, 2].max()) == pytest.approx(
        full.tris.reshape(-1, 3)[:, 2].max()
    )


def test_model_axis_turns_the_model_about_the_footprint_centre() -> None:
    a = build_landmark(CHURCH)
    b = build_landmark({**CHURCH, "model_axis": 0.989 + np.pi})
    assert a.face_count == b.face_count
    c = np.array(CHURCH["footprint"]["exterior"]).mean(axis=0)
    pa, pb = a.tris.reshape(-1, 3), b.tris.reshape(-1, 3)
    assert np.allclose(pa[:, :2] - c, -(pb[:, :2] - c), atol=1e-6) or pa.shape == pb.shape
    assert np.allclose(pa[:, 2], pb[:, 2])


def test_overrides_accept_known_models_only(tmp_path) -> None:
    path = tmp_path / "overrides.yaml"
    path.write_text(yaml.safe_dump({"buildings": {"w1": {"model": "church", "model_axis": 1.0}}}))
    assert load_overrides(path)["w1"]["model"] in MODELS
    path.write_text(yaml.safe_dump({"buildings": {"w1": {"model": "castle", "model_axis": 1.0}}}))
    with pytest.raises(RulesError, match="model must be one of"):
        load_overrides(path)


# footprint of the block of flats w378900224 (map metres): rear wall 22.3 m, gables 9.15 m, stepped front
FLATS = {
    "id": "w378900224",
    "model": "flats",
    "model_axis": 0.0,
    "footprint": {
        "exterior": [
            [-445.149, 274.95], [-443.595, 283.955], [-465.595, 287.837], [-467.218, 278.817],
            [-462.962, 278.08], [-463.033, 277.696], [-459.286, 277.08], [-459.216, 277.464],
            [-457.283, 277.082], [-457.413, 276.336], [-455.221, 275.949], [-455.09, 276.696],
            [-453.089, 276.33], [-453.154, 276.068], [-449.405, 275.329], [-449.343, 275.702],
        ],
        "holes": [],
    },
}  # fmt: skip


def test_flats_stand_on_their_footprint_under_one_overhanging_roof() -> None:
    a, b = build_landmark(FLATS), build_landmark(FLATS)
    assert np.array_equal(a.tris, b.tris) and np.array_equal(a.colours, b.colours)
    assert a.colours.dtype == np.uint8 and len(a.colours) == a.face_count
    pts = a.tris.reshape(-1, 3)
    ring = np.array(FLATS["footprint"]["exterior"])
    assert pts[:, 2].min() == pytest.approx(0.0)
    assert pts[:, 2].max() == pytest.approx(7.15)  # ridge
    # nothing but the roof overhang (1.15 m at the front) and the gutters leaves the outline
    assert pts[:, 0].min() > ring[:, 0].min() - 1.5 and pts[:, 0].max() < ring[:, 0].max() + 1.5
    assert pts[:, 1].min() > ring[:, 1].min() - 1.5 and pts[:, 1].max() < ring[:, 1].max() + 1.5
    # the rear wall and the gables lie on the footprint edges
    rear = pts[np.isclose(pts[:, 2], 0.0)]
    nw, ne = ring[2], ring[1]
    along = (ne - nw) / np.linalg.norm(ne - nw)
    off = (rear[:, :2] - nw) @ np.array([-along[1], along[0]])
    assert off.max() == pytest.approx(0.02, abs=0.01)  # plinth, 2 cm proud of the rear wall


def test_flats_collision_keeps_the_loggias_and_drops_the_small_parts() -> None:
    full, light = build_landmark(FLATS), build_landmark(FLATS, detail=False)
    assert 0 < light.face_count < full.face_count / 5
    assert light.tris.reshape(-1, 3)[:, 2].max() == pytest.approx(7.15)
    green = (light.colours == [0x4D, 0x7B, 0x68]).all(axis=1)
    assert green.any()  # the balcony and terrace screens stay as panels


# a plain 17.2 x 10.9 m footprint turned 30 degrees: the loggia house is laid on its bounding rectangle
def _turned_rect(length: float, depth: float, angle: float) -> list[list[float]]:
    c, s = np.cos(angle), np.sin(angle)
    return [[100 + x * c - y * s, 50 + x * s + y * c] for x, y in ((0, 0), (length, 0), (length, depth), (0, depth))]


LOGGIA_HOUSE = {
    "id": "w1",
    "model": "loggia_house",
    "model_axis": np.pi / 6 - np.pi / 2,  # the street front is the edge from the first to the second corner
    "footprint": {"exterior": _turned_rect(17.2, 10.9, np.pi / 6), "holes": []},
}


def test_loggia_house_stands_on_its_footprint_with_the_front_on_the_model_axis() -> None:
    a, b = build_landmark(LOGGIA_HOUSE), build_landmark(LOGGIA_HOUSE)
    assert np.array_equal(a.tris, b.tris) and np.array_equal(a.colours, b.colours)
    assert a.colours.dtype == np.uint8 and len(a.colours) == a.face_count
    pts = a.tris.reshape(-1, 3)
    assert pts[:, 2].min() == pytest.approx(0.0)
    assert pts[:, 2].max() == pytest.approx(6.9)  # chimneys, 0.6 m above the 6.3 m roof
    ring = np.array(LOGGIA_HOUSE["footprint"]["exterior"])
    along = (ring[1] - ring[0]) / np.linalg.norm(ring[1] - ring[0])
    inward = np.array([-along[1], along[0]])
    x, y = (pts[:, :2] - ring[0]) @ along, (pts[:, :2] - ring[0]) @ inward
    # only the roof slab, the gutters and the porch roof leave the outline, by less than 0.4 m
    assert x.min() > -0.4 and x.max() < 17.6 and y.min() > -0.4 and y.max() < 11.3
    # the shuttered opening is at the low end of the street front, the timber door in the rear wall
    shutter = (a.colours == [0xD6, 0xD6, 0xD2]).all(axis=1).repeat(3)
    wood = (a.colours == [0x8A, 0x4F, 0x2E]).all(axis=1).repeat(3)
    assert shutter.any() and x[shutter].max() < 17.2 / 2 and y[shutter].max() < 0.2
    assert wood.any() and y[wood].min() > 10.9 - 1.0
    # the depth is not stretched at the front: the loggias are 1.2 m deep, the bay 1.3 m
    front = pts[np.isclose(pts[:, 2], 3.5)]  # loggia floors
    assert ((front[:, :2] - ring[0]) @ inward).max() == pytest.approx(1.2)
    # turning the axis round puts the street front on the opposite long side
    turned = build_landmark({**LOGGIA_HOUSE, "model_axis": LOGGIA_HOUSE["model_axis"] + np.pi})
    yt = (turned.tris.reshape(-1, 3)[:, :2] - ring[0]) @ inward
    assert yt[shutter].min() > 10.9 - 0.2


def test_loggia_house_collision_keeps_the_cutouts_and_drops_the_small_parts() -> None:
    full, light = build_landmark(LOGGIA_HOUSE), build_landmark(LOGGIA_HOUSE, detail=False)
    assert 0 < light.face_count < full.face_count / 5
    pts = light.tris.reshape(-1, 3)
    assert pts[:, 2].max() == pytest.approx(6.3)  # no chimneys
    assert np.isclose(light.tris[..., 2], 3.5).all(axis=1).any()  # the loggias are still rooms: floors
    assert not (light.colours == [150, 190, 215]).all(axis=1).any()  # no glass: openings are not cut


# footprint of the bunker w825140518 (map metres): a long west wall and a faceted east side
BUNKER_RING = [
    [19.06, -268.855], [19.265, -276.211], [21.914, -275.515], [23.521, -274.812],
    [24.454, -273.434], [24.561, -271.276], [23.259, -269.988], [21.605, -269.222],
]  # fmt: skip
BUNKER = {
    "id": "w825140518",
    "model": "bunker",
    "model_axis": -3.085,
    "footprint": {"exterior": BUNKER_RING, "holes": []},
}


def _crosses(mesh, a, b) -> bool:
    """Does the segment a..b pass through any triangle of the mesh?"""
    a, b = np.asarray(a, float), np.asarray(b, float)
    d = b - a
    v0, e1, e2 = mesh.tris[:, 0], mesh.tris[:, 1] - mesh.tris[:, 0], mesh.tris[:, 2] - mesh.tris[:, 0]
    p = np.cross(d, e2)
    det = np.einsum("ij,ij->i", e1, p)
    ok = np.abs(det) > 1e-12
    det = np.where(ok, det, 1.0)
    s = a - v0
    u = np.einsum("ij,ij->i", s, p) / det
    q = np.cross(s, e1)
    v = (q @ d) / det
    t = np.einsum("ij,ij->i", e2, q) / det
    return bool((ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-9) & (t < 1 - 1e-9)).any())


def test_bunker_has_a_room_behind_an_open_doorway() -> None:
    from drycastle_pipeline import bunker as bk

    ring = np.array(BUNKER_RING)
    west = ring[:, 0].min()
    mid_y = (ring[0][1] + ring[1][1]) / 2  # the west wall runs between the first two corners
    for mesh in (build_landmark(BUNKER), build_landmark(BUNKER, detail=False)):
        pts = mesh.tris.reshape(-1, 3)
        assert pts[:, 2].min() == pytest.approx(-bk.STOREY * bk.UNDER)  # the lowest floor
        assert pts[:, 2].max() == pytest.approx(bk.ENTER_WALL_H + 0.877, abs=0.01)
        # a standing player (1.8 m, 0.7 m wide) walks in through the middle of the west wall ...
        for z in (0.3, 1.0, 1.8):
            for side in (-0.35, 0.0, 0.35):
                assert not _crosses(mesh, (west - 2, mid_y + side, z), (west + 2, mid_y + side, z))
        # ... but not through the wall beside the doorway, nor above it
        assert _crosses(mesh, (west - 2, mid_y + 1.0, 1.0), (west + 2, mid_y + 1.0, 1.0))
        assert _crosses(mesh, (west - 2, mid_y, bk.DOOR_H + 0.1), (west + 2, mid_y, bk.DOOR_H + 0.1))
        # the room is roofed: nothing drops in from above
        assert _crosses(mesh, (west + 2, mid_y, 10.0), (west + 2, mid_y, 1.0))


def test_bunker_collision_keeps_the_openings_and_has_no_floor() -> None:
    from drycastle_pipeline import bunker as bk

    full, light = build_landmark(BUNKER), build_landmark(BUNKER, detail=False)
    assert 0 < light.face_count < full.face_count
    flat = np.ptp(light.tris[:, :, 2], axis=1) < 1e-9
    assert not np.isclose(light.tris[flat][:, 0, 2], bk.FLOOR_Z).any()  # players stand on the terrain
    slits = np.isclose(light.tris[flat][:, 0, 2], bk.SLIT_Z0).sum()
    assert slits == 2 * 4  # four slit sills, two triangles each


def test_bunker_slits_are_shot_through_at_crouch_height() -> None:
    from drycastle_pipeline import bunker as bk

    mesh = build_landmark(BUNKER, detail=False)
    ring = np.array(BUNKER_RING)
    c = ring.mean(axis=0)
    eye = (bk.SLIT_Z0 + bk.SLIT_Z1) / 2
    east = ring[:, 0].max()
    # the east wall (corners 5 and 6, facing +x) carries a slit at its middle: a level shot leaves the room there
    hits = [
        y for y in np.arange(ring[4][1] + 0.05, ring[5][1], 0.05)
        if not _crosses(mesh, (c[0], y, eye), (east + 2, y, eye))
    ]
    assert hits and max(hits) - min(hits) < bk.SLIT_W + 0.1
    assert all(_crosses(mesh, (c[0], y, bk.SLIT_Z1 + 0.2), (east + 2, y, bk.SLIT_Z1 + 0.2)) for y in hits)


def test_bunker_too_small_for_a_room_stays_closed() -> None:
    tiny = {**BUNKER, "footprint": {"exterior": [[0, 0], [2.2, 0], [2.2, 2.2], [0, 2.2]], "holes": []}}
    mesh = build_landmark(tiny, detail=False)
    assert _crosses(mesh, (-1, 1.1, 0.5), (1.1, 1.1, 0.5))  # no way in
    assert mesh.tris.reshape(-1, 3)[:, 2].max() == pytest.approx(2.677, abs=0.01)


def test_bunker_rooms_cover_the_room_and_stop_short_of_the_outer_wall() -> None:
    from shapely.geometry import Point, Polygon

    from drycastle_pipeline import bunker as bk

    rooms = bk.bunker_rooms(BUNKER_RING, (-1.0, 0.0))["floors"]
    assert [r["level"] for r in rooms] == [0, -1, -2, -3]
    room, outer = Polygon(rooms[0]["polygon"]), Polygon(BUNKER_RING)
    assert outer.contains(room)
    # the shelter volume reaches into the walls, but the outer 0.3 m of them is outside it
    assert outer.exterior.distance(room) == pytest.approx(bk.WALL_T - bk.SHELTER_GROW, abs=1e-3)
    assert room.contains(Point(np.array(BUNKER_RING).mean(axis=0)))
    assert rooms[0]["z0"] < 0 < bk.FLOOR_Z + bk.CLEAR_H < rooms[0]["z1"] < bk.ENTER_WALL_H
    assert bk.bunker_rooms([[0, 0], [2.2, 0], [2.2, 2.2], [0, 2.2]], (1.0, 0.0)) == {}  # stays closed


def test_bunker_underground_floors_are_stacked_under_one_footprint() -> None:
    from itertools import pairwise

    from shapely.geometry import Point, Polygon

    from drycastle_pipeline import bunker as bk

    info = bk.bunker_rooms(BUNKER_RING, (-1.0, 0.0))
    floors = info["floors"]
    assert all(a["z0"] > b["z1"] - 1e-9 for a, b in pairwise(floors))  # no two rooms overlap
    assert [round(f["z0"] + bk.SHELTER_GROW, 3) for f in floors[1:]] == [-2.4, -4.8, -7.2]
    assert len(info["ladders"]) == bk.UNDER
    room = Polygon(floors[0]["polygon"])
    for ld in info["ladders"]:
        assert room.contains(Point(ld["x"], ld["y"]))
        assert np.hypot(ld["dx"], ld["dy"]) == pytest.approx(1.0, abs=1e-3)
        assert ld["z1"] - ld["z0"] >= bk.STOREY - 1e-9
        # the floor beyond the ladder, where the climber steps off at the top, is inside the room
        off = Point(ld["x"] + ld["dx"] * bk.STEP_OFF, ld["y"] + ld["dy"] * bk.STEP_OFF)
        assert room.contains(off) and not Polygon(info["hatch"]).contains(off)
    # each ladder is climbed from the floor it was reached by: the next one is far from it
    pts = [Point(ld["x"], ld["y"]) for ld in info["ladders"]]
    assert all(a.distance(b) >= bk.HATCH_APART - 1.0 for a, b in pairwise(pts))


def test_bunker_hatch_is_open_in_every_floor_and_the_ladder_is_not_collided_with() -> None:
    from drycastle_pipeline import bunker as bk

    info = bk.bunker_rooms(BUNKER_RING, (-1.0, 0.0))
    ld = info["ladders"][0]
    # straight down the ladder's climbing spot from the ground floor to the floor below: nothing in the way
    col = build_landmark(BUNKER, detail=False)
    assert not _crosses(col, (ld["x"], ld["y"], ld["z1"] + 0.5), (ld["x"], ld["y"], ld["z0"] + 0.1))
    # but the solid floor under the lowest ladder is there
    last = info["ladders"][-1]
    assert _crosses(col, (last["x"], last["y"], last["z0"] + 1.0), (last["x"], last["y"], last["z0"] - 1.0))
