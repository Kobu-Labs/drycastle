"""A building from captures without a model reading pixels (facade_rectify, facade_pipeline): the camera from lines,
the wall made flat, the candidates a model chooses between."""

import math

import numpy as np
import pytest

from drycastle_pipeline.photos import facade_pipeline as fp
from drycastle_pipeline.photos import facade_rectify as fr
from drycastle_pipeline.photos import streetview as sv
from drycastle_pipeline.photos.landmark_photo import project

CAM = {"x": 0.0, "y": -12.0, "z": 2.0, "yaw": 8.0, "pitch": -4.0, "f": 700.0, "w": 1400, "h": 1000}
A, B = (-5.0, 0.0), (5.0, 0.0)  # a wall 10 m long, 12 m north of the camera, its first corner on the left


def _segments_of_wall(cam: dict) -> np.ndarray:
    """What LSD would find on a wall with a grid of level and upright edges, as this camera sees it."""
    out = []
    for z in (0.0, 1.0, 2.2, 3.0):
        for s0 in (-5.0, -1.0, 2.0):
            out.append(np.r_[project(cam, np.array([[s0, 0.0, z], [s0 + 2.5, 0.0, z]]))].ravel())
    for s in (-5.0, -2.0, 1.0, 5.0):
        out.append(project(cam, np.array([[s, 0.0, 0.0], [s, 0.0, 3.0]])).ravel())
    for y0 in (0.0, 3.0):  # the next wall, going away from the camera
        out.append(project(cam, np.array([[5.0, y0, 3.0], [5.0, y0 + 3.0, 3.0]])).ravel())
    return np.array(out)


DIRS = [np.array([0, 0, 1.0]), np.array([1.0, 0, 0]), np.array([0, 1.0, 0])]


def test_the_heading_and_tilt_come_back_from_the_lines():
    segs = _segments_of_wall(CAM)
    wrong = {**CAM, "yaw": CAM["yaw"] + 3.4, "pitch": CAM["pitch"] - 1.2}
    got = fr.refine_rotation(wrong, segs, DIRS)
    assert got["yaw"] == pytest.approx(CAM["yaw"], abs=0.15)
    assert got["pitch"] == pytest.approx(CAM["pitch"], abs=0.15)


def test_segments_are_sorted_by_the_direction_they_run_along():
    segs = _segments_of_wall(CAM)
    kinds = fr.classify(CAM, segs, {"up": DIRS[0], "wall": DIRS[1], "next": DIRS[2]})
    assert kinds == ["wall"] * 12 + ["up"] * 4 + ["next"] * 2


def test_two_wall_ends_put_the_camera_back_in_its_place():
    cols = [float(project(CAM, np.array([[*c, CAM["z"]]]))[0, 0]) for c in (A, B)]
    moved = fp.move_camera({**CAM, "x": 1.7, "y": -10.4}, [(A, cols[0]), (B, cols[1])])
    assert (moved["x"], moved["y"]) == pytest.approx((CAM["x"], CAM["y"]), abs=0.02)
    assert moved["yaw"] == CAM["yaw"]  # the heading is not touched


def test_the_flat_wall_is_in_metres():
    h, size, length = fr.wall_homography(CAM, A, B)
    assert length == pytest.approx(10.0)
    assert size == (int((10 + 2 * fr.SIDE_M) * fr.PX_PER_M), int((fr.Z_RANGE[1] - fr.Z_RANGE[0]) * fr.PX_PER_M))
    for s, z in ((0.0, 0.0), (10.0, 0.0), (4.0, 2.5)):  # a point of the wall: its flat pixel maps to its photo pixel
        flat = np.array([fp._col(s), fp._row(z), 1.0])
        photo = h @ flat
        want = project(CAM, np.array([[A[0] + s, 0.0, z]]))[0]
        assert photo[:2] / photo[2] == pytest.approx(want, abs=0.5)


def test_candidates_closer_than_the_eye_can_tell_are_one():
    # uprights at the wall's own height, and one above the roof (a chimney) that must not count
    segs = np.array([[x, 450, x, 570] for x in (500.0, 506.0, 512.0, 540.0)] + [[100.0, 450, 100.0, 570], [530.0, 250, 530.0, 400]])
    kinds = ["up"] * 6
    cam = {**CAM, "yaw": 0.0, "pitch": 0.0}
    corner = (-2.83, 0.0)  # falls near column 535
    cands, where = fr.end_candidates(cam, segs, kinds, corner)
    assert where[0] == pytest.approx(535, abs=2)
    assert [round(c["x"]) for c in cands] == [506, 540]  # three lines 12 px apart are one; the far one and the chimney are out


def test_an_answer_is_a_letter_two_letters_or_none():
    cands = [{"letter": "A", "x": 100.0}, {"letter": "B", "x": 120.0}, {"letter": "C", "x": 180.0}]
    assert fp._pick_x(cands, "B") == 120.0
    assert fp._pick_x(cands, "b-c") == 150.0
    assert fp._pick_x(cands, "none") is None
    assert fp._pick_x(cands, "Z") is None


def _flat_wall(length: float):
    w, h = int((length + 2 * fr.SIDE_M) * fr.PX_PER_M), int((fr.Z_RANGE[1] - fr.Z_RANGE[0]) * fr.PX_PER_M)
    img = np.full((h, w, 3), (150, 170, 200), np.uint8)  # render
    img[: fp._row(3.4)] = (235, 200, 170)  # sky above the wall
    return img


def test_a_dark_window_and_a_mirroring_one_are_both_found():
    cv2 = pytest.importorskip("cv2")
    img = _flat_wall(10.0)
    cv2.rectangle(img, (fp._col(1.0), fp._row(2.4)), (fp._col(2.5), fp._row(1.0)), (40, 40, 45), -1)  # dark glass
    # glass that mirrors the sky: nearly the wall's colour, only its frame shows
    cv2.rectangle(img, (fp._col(6.0), fp._row(2.4)), (fp._col(7.8), fp._row(1.0)), (165, 180, 205), -1)
    cv2.rectangle(img, (fp._col(6.0), fp._row(2.4)), (fp._col(7.8), fp._row(1.0)), (30, 30, 30), 3)
    got = fp.find_rectangles(img, 10.0)
    assert len(got) == 2
    for r, (s0, s1) in zip(got, ((1.0, 2.5), (6.0, 7.8))):
        assert (r["s0"], r["s1"]) == pytest.approx((s0, s1), abs=0.08)
        assert (r["z0"], r["z1"]) == pytest.approx((1.0, 2.4), abs=0.08)


def test_a_pillar_in_front_does_not_stretch_the_window():
    cv2 = pytest.importorskip("cv2")
    img = _flat_wall(10.0)
    cv2.rectangle(img, (fp._col(4.0), fp._row(2.4)), (fp._col(6.0), fp._row(1.2)), (40, 40, 45), -1)
    cv2.rectangle(img, (fp._col(3.6), fp._row(1.6)), (fp._col(4.5), fp._row(-0.5)), (120, 120, 120), -1)  # fence pillar
    win = max(fp.find_boxes(img, 10.0), key=lambda r: r["z1"])
    assert (win["s0"], win["s1"]) == pytest.approx((4.0, 6.0), abs=0.08)
    assert (win["z0"], win["z1"]) == pytest.approx((1.2, 2.4), abs=0.08)


def test_the_wall_colour_is_the_commonest_not_the_middle_one():
    img = _flat_wall(10.0)
    part = img[fp._row(3.2) : fp._row(1.2), fp._col(0.3) : fp._col(6.5)]  # more than half is not wall: many colours
    part[:] = np.random.default_rng(1).integers(0, 255, part.shape, dtype=np.uint8)
    lab = fp.wall_colour_lab(img, 10.0)
    cv2 = pytest.importorskip("cv2")
    assert cv2.cvtColor(np.uint8([[lab]]), cv2.COLOR_LAB2BGR)[0, 0].astype(int) == pytest.approx([150, 170, 200], abs=6)


def test_the_ridge_is_where_the_roof_meets_the_sky():
    img = np.full((CAM["h"], CAM["w"], 3), (235, 200, 170), np.uint8)  # sky
    top = 6.5  # a roof whose top is level at 6.5 m, 5 m behind the wall
    row = int(project(CAM, np.array([[0.0, 5.0, top]]))[0, 1])
    img[row:] = (50, 50, 60)
    assert fp.ridge_height(img, CAM, (0.0, 5.0), 3.0) == pytest.approx(top, abs=0.08)


def test_tiles_cover_the_view_at_a_narrower_lens():
    plan = sv.tile_plan(354.0, 40.0, 10.0, 2)
    assert len(plan) == 4 and all(20 < t["fov"] < 26 for t in plan)
    fwd, right, up = sv.camera_axes(354.0, 10.0)
    half = math.tan(math.radians(20))
    for t, (cx, cy) in zip(plan, ((-0.5, 0.5), (0.5, 0.5), (-0.5, -0.5), (0.5, -0.5))):  # each aims at its cell's middle
        aim = fwd + right * cx * half + up * cy * half
        assert sv.camera_axes(t["heading"], t["pitch"])[0] == pytest.approx(aim / np.linalg.norm(aim), abs=1e-6)


def test_a_shaded_cream_wall_comes_out_light_and_warm():
    grey_brown = np.array([132.0, 131.0, 133.0])  # OpenCV Lab of a cream wall in shade
    got = fp.toned(grey_brown, fp.WALL_TONES["light"])
    assert got[0] == fp.WALL_TONES["light"]
    assert got[1] > 131.0 and got[2] > 133.0 + 4  # the hue is stronger, and warmer than the sky-lit sample
    assert fp.muted([40.0, 128.0, 110.0]) == pytest.approx([40.0, 128.0, 119.0])  # a dark blue-ish sample: half the hue


def test_the_overhang_is_where_the_roofs_corner_lies_past_the_houses():
    cv2 = pytest.importorskip("cv2")
    h, _, length = fr.wall_homography(CAM, A, B)
    reach, wall_top = 0.9, 3.2  # the roof's edge is level with the wall's top, 0.9 m out
    corner = np.array([B[0] + reach, B[1] - reach, wall_top])  # past the right end, towards the camera
    mid = np.array([0.0, -reach, wall_top])
    inv = np.linalg.inv(h)

    def flat(pt):
        return cv2.perspectiveTransform(project(CAM, pt[None]).reshape(1, 1, 2).astype(np.float32), inv)[0, 0]

    line = {"z": fp._z_of_row(flat(mid)[1]), "length_m": 11.0, "s0": -0.9, "s1": fp._s_of_col(flat(corner)[0])}
    got = fp.overhang_from_corner(CAM, h, line, B, (0.0, -1.0), (1.0, 0.0), line["s1"], length, A, B)
    assert got == pytest.approx(reach, abs=0.05)


def test_a_post_and_a_downpipe_are_found_on_the_flat_wall():
    cv2 = pytest.importorskip("cv2")
    img = _flat_wall(10.0)
    box = {"s0": 4.0, "s1": 6.0, "z0": 1.0, "z1": 2.4}
    cv2.rectangle(img, (fp._col(4.0), fp._row(2.4)), (fp._col(6.0), fp._row(1.0)), (200, 190, 170), -1)  # glass
    cv2.rectangle(img, (fp._col(4.7) - 3, fp._row(2.4)), (fp._col(4.7) + 3, fp._row(1.0)), (30, 30, 30), -1)  # a post at 0.35
    assert fp.window_posts(img, box) == pytest.approx([0.35], abs=0.03)
    cv2.rectangle(img, (fp._col(9.8) - 5, fp._row(3.4)), (fp._col(9.8) + 5, fp._row(0.0)), (40, 40, 50), -1)  # a pipe
    pipes = fp.find_downpipes(img, 10.0, 3.4, fp.wall_colour_lab(img, 10.0))
    assert [p["s"] for p in pipes] == pytest.approx([9.8], abs=0.05)


def test_walls_no_capture_saw_get_windows_like_the_measured_ones():
    b = {"id": "x", "footprint": {"exterior": [[0, 0], [12, 0], [12, 8], [0, 8]]}}
    like = [{"kind": "window", "width_m": 1.5, "height_m": 1.4, "sill_m": 0.9}]
    got = fp.guessed_openings(b, [], {0}, like)
    assert sorted({o["edge"] for o in got}) == [1, 2, 3] and all(o["confidence"] == "low" for o in got)
    on_long = [o for o in got if o["edge"] == 2]
    assert len(on_long) == 2 and on_long[0]["at_m"] == pytest.approx(3.0) and on_long[1]["at_m"] == pytest.approx(7.5)
    assert fp.guessed_openings(b, [], {0}, []) == []  # nothing measured: nothing to copy


def test_the_wall_is_the_colour_it_is_named_not_the_grey_it_photographs():
    assert set(fp.WALL_COLOURS) >= {"white", "cream", "peach", "brick"}
    for name, colour in fp.WALL_COLOURS.items():
        assert len(colour) == 7 and colour[0] == "#", name


def test_a_gabled_roof_covers_the_part_behind_its_eave_wall():
    from drycastle_pipeline.photos.landmark_tools import _edges

    # a 6 x 8 house with a 10 x 4 wing behind it: the roof belongs to the house, not to the long shape of both
    ring = [[0, 0], [6, 0], [6, 8], [10, 8], [10, 12], [0, 12]]
    edges = _edges({"footprint": {"exterior": ring, "holes": []}})
    front = next(e["i"] for e in edges if e["length"] == pytest.approx(6.0) and abs(e["normal"][1] + 1) < 1e-6)
    part = fp.main_part([list(map(float, p)) for p in ring], edges, front)
    assert part is not None and part["half"] == pytest.approx(6.0) and part["middle"] == pytest.approx((3.0, 6.0))
    square = [[0, 0], [6, 0], [6, 8], [0, 8]]
    edges = _edges({"footprint": {"exterior": square, "holes": []}})
    assert fp.main_part(square, edges, next(e["i"] for e in edges if abs(e["normal"][1] + 1) < 1e-6)) is None  # nothing to split off


def test_a_dormer_stands_back_on_the_roof_and_is_larger_than_it_shows():
    # camera 6 m from the wall at 2.5 m, roof rising 1 m per metre from a 3 m wall top; a dormer window 1.5 m behind the
    # wall, its foot at 4.5 m, shows on the wall's plane at 2.5 + 2 * 6 / 7.5 = 4.1 m
    k = 6 / 7.5
    box = {"s0": 2.0 + (3.0 - 2.0) * k, "s1": 2.0 + (4.0 - 2.0) * k, "z0": 2.5 + 2.0 * k, "z1": 2.5 + 3.0 * k}
    got = fp.dormer_from_box({"edge": 0, "forward": True, "length": 8.0, "box": box, "cam_z": 2.5, "cam_s": 2.0, "d": 6.0}, 3.0, 1.0)
    assert got["at_m"] == pytest.approx(3.5, abs=0.02) and got["window_m"] == pytest.approx([1.0, 1.0], abs=0.02)
    assert got["bottom_m"] == pytest.approx(4.4, abs=0.02)
    assert fp.dormer_from_box({"edge": 0, "forward": True, "length": 8.0, "box": {**box, "z0": 2.0}, "cam_z": 2.5, "cam_s": 2.0, "d": 6.0}, 3.0, 1.0) is None


def test_the_guide_says_which_walls_a_view_measures():
    from drycastle_pipeline.photos import capture_guide as cg

    b = {"id": "b", "footprint": {"exterior": [[0.0, 0.0], [8.0, 0.0], [8.0, 6.0], [0.0, 6.0]], "holes": []}}
    cam = {"x": 4.0, "y": -10.0, "z": 2.5, "yaw": 0.0, "pitch": 0.0, "f": 700.0, "w": 1400, "h": 900}  # square in front of the south wall
    walls = {w["edge"]: w for w in cg.wall_status(b, [], cam)}
    assert [w["status"] for w in walls.values()] == ["good"] and len(walls) == 1
    front = next(iter(walls.values()))
    assert front["off"] == 0 and front["quad"][0][0] < front["quad"][1][0]
    corners = cg.corners_to_mark(list(walls.values()))
    assert [c["side"] for c in corners] == ["left", "right"] and corners[0]["x"] < corners[1]["x"]
    steep = cg.wall_status(b, [], {**cam, "x": 20.0, "y": -6.0, "yaw": 290.0})
    assert {w["status"] for w in steep} <= {"poor", "no", "good"} and any(w["status"] != "good" for w in steep)
    near = cg.wall_status(b, [], {**cam, "y": -2.0})  # too close: the ends leave the picture
    assert near[0]["status"] == "no" and "out of view" in near[0]["reason"]


def test_marks_made_by_hand_give_the_wall_top_and_the_ridge():
    from drycastle_pipeline.photos.landmark_photo import project

    cam = {"x": 4.0, "y": -10.0, "z": 2.5, "yaw": 8.0, "pitch": 3.0, "f": 700.0, "w": 1400, "h": 900}
    a, c = (0.0, 0.0), (8.0, 0.0)
    line = project(cam, np.array([[*a, 3.2], [*c, 3.2]]))
    assert fp.wall_top_from_mark(cam, line.tolist(), a, c) == pytest.approx(3.2, abs=1e-6)
    ridge = project(cam, np.array([[6.0, 3.0, 6.4]]))[0]  # a point on a ridge running east-west through (4, 3)
    assert fp.ridge_from_mark(cam, ridge, (4.0, 3.0), 0.0) == pytest.approx(6.4, abs=1e-6)
    assert fp.ridge_from_mark({**cam, "x": -20.0, "y": 3.0, "yaw": 90.0}, (700, 300), (4.0, 3.0), 0.0) is None  # looking along it
    assert fp.long_axis([[0, 0], [8, 0], [8, 6], [0, 6]]) == pytest.approx(0.0, abs=1e-6)


def _wall_with_windows(at: list[float], frame: int = 6):
    """A grey flat wall 8 m long with white-framed dark windows 0.7 x 1.2 m, sill 1.0 m, each with a bar a quarter down."""
    rng = np.random.default_rng(3)
    flat = np.full((800, fp._col(8.0) + 150, 3), (150, 150, 150), np.uint8) + rng.integers(0, 6, (800, fp._col(8.0) + 150, 3), dtype=np.uint8)
    for s in at:
        x0, x1, y0, y1 = fp._col(s), fp._col(s + 0.7), fp._row(2.2), fp._row(1.0)
        flat[y0:y1, x0:x1] = (240, 240, 240)
        flat[y0 + frame : y1 - frame, x0 + frame : x1 - frame] = (60, 50, 45)
        bar = y0 + (y1 - y0) // 4
        flat[bar - 3 : bar + 3, x0:x1] = (240, 240, 240)
    return flat


def test_a_window_like_a_named_one_is_found_and_its_frame_and_bar_are_part_of_it():
    flat = _wall_with_windows([1.0, 3.5, 6.0])
    wall_lab = fp.wall_colour_lab(flat, 8.0)
    glass = {"s0": 1.06, "s1": 1.64, "z0": 1.06, "z1": 2.14, "number": 1}  # the finder gave the glass, not the frame
    whole = fp.grow_to_frame(flat, glass, wall_lab)
    assert whole["s0"] == pytest.approx(1.0, abs=0.03) and whole["s1"] == pytest.approx(1.7, abs=0.03)
    assert whole["z0"] == pytest.approx(1.0, abs=0.03) and whole["z1"] == pytest.approx(2.2, abs=0.03)
    twins = fp.find_twins(flat, [whole], 8.0)
    assert sorted(round(t["s0"], 1) for t in twins) == [3.5, 6.0]
    assert all(t["z0"] == pytest.approx(1.0, abs=0.05) for t in twins)
    assert fp.window_rails(flat, whole) == pytest.approx([0.75], abs=0.04)
    assert fp.find_twins(_wall_with_windows([1.0]), [whole], 8.0) == []  # a bare wall has none
