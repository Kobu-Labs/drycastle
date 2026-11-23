"""Fitting a camera to a photo on a known footprint (landmark_photo): a made-up camera is recovered from its own picture."""

import numpy as np
import pytest

from drycastle_pipeline.photos import landmark_photo as lp

RING = [(0.0, 0.0), (8.0, 0.0), (8.0, 6.0), (0.0, 6.0)]  # counter-clockwise; edge 0 faces south
TRUE = {"x": -5.0, "y": -9.0, "z": 2.4, "yaw": 38.0, "pitch": 4.0, "f": 620.0, "w": 900, "h": 700}
EAVE, TOP = 2.7, 5.4


def _points(noise=0.0):
    world = [  # the south wall and the west gable end, as a photo from the south-west corner shows them
        ((0.0, 0.0), "ground", 0.0), ((0.0, 0.0), "eave", EAVE), ((8.0, 0.0), "eave", EAVE), ((8.0, 0.0), "ground", 0.0),
        ((0.0, 6.0), "eave", EAVE), (((0.0, 6.0), (0.0, 0.0)), "top", TOP),  # the ridge end, 40% along edge 3
    ]  # fmt: skip
    at = [(0.0, 6.0 - 6.0 * 0.4) if isinstance(xy[0], tuple) else xy for xy, _, _ in world]
    px = lp.project(TRUE, np.array([[xy[0], xy[1], z] for xy, (_, _, z) in zip(at, world)]))
    rng = np.random.default_rng(1)
    return [(xy, lvl, tuple(p + rng.normal(0, noise, 2))) for (xy, lvl, _), p in zip(world, px)]


def test_the_camera_and_the_heights_are_recovered_from_a_rough_viewpoint():
    cam, eave, top, errs, ridge_at = lp.solve(_points(), (900, 700), view=(-3.5, -10.5), cam_height=2.5)
    assert errs.max() < 1.0 and ridge_at == pytest.approx(0.4, abs=0.05)
    assert eave == pytest.approx(EAVE, abs=0.1) and top == pytest.approx(TOP, abs=0.15)
    assert (cam["x"], cam["y"]) == pytest.approx((TRUE["x"], TRUE["y"]), abs=1.0)


def test_points_read_a_few_pixels_off_still_give_the_heights():
    _, eave, top, errs, _ = lp.solve(_points(noise=4.0), (900, 700), view=(-3.5, -10.5), cam_height=2.5)
    assert errs.max() < 15
    assert eave == pytest.approx(EAVE, abs=0.3) and top == pytest.approx(TOP, abs=0.45)


def test_a_pixel_on_a_wall_measures_back_to_metres(tmp_path, monkeypatch):
    monkeypatch.setattr(lp, "_buildings", lambda ids: [{"id": "w1", "footprint": {"exterior": RING}}])
    cam = tmp_path / "cam.json"
    cam.write_text(__import__("json").dumps({**TRUE, "building": "w1"}))
    px = lp.project(TRUE, np.array([[3.0, 0.0, 0.9], [4.4, 0.0, 2.2]]))  # a window on edge 0
    (s0, z0), (s1, z1) = lp.measure(cam, 0, [tuple(p) for p in px])
    assert (s0, z0, s1, z1) == pytest.approx((3.0, 0.9, 4.4, 2.2), abs=0.01)


def test_a_colour_is_corrected_by_a_white_surface(tmp_path):
    from PIL import Image

    img = np.zeros((40, 40, 3), np.uint8)
    img[:, :20] = (121, 121, 121)  # a white wall in shade
    img[:, 20:] = (75, 93, 83)  # a green wall in the same shade
    path = tmp_path / "p.png"
    Image.fromarray(img).save(path)
    assert lp.sample(path, (30, 20), None) == [75, 93, 83]
    assert lp.sample(path, (30, 20), (10, 20)) == [150, 186, 166]


def test_a_known_camera_needs_only_three_points_and_a_poor_viewpoint():
    pts = [p for p in _points() if p[1] != "ground"][:3]  # two eave corners of the south wall and one of the gable end
    pts.append(_points()[-1])  # and the ridge end, so the ridge height is asked for
    pose = {"heading": TRUE["yaw"] + 2.0, "pitch": TRUE["pitch"] - 1.0, "fov": 2 * np.degrees(np.arctan(450 / TRUE["f"]))}
    _, eave, top, errs, _ = lp.solve(pts[:3], (900, 700), view=(-4.0, -10.0), cam_height=2.5, pose=pose)
    assert errs.max() < 3 and eave == pytest.approx(EAVE, abs=0.25)
    _, eave, top, errs, _ = lp.solve(pts, (900, 700), view=(-4.0, -10.0), cam_height=2.5, pose=pose)
    assert eave == pytest.approx(EAVE, abs=0.25) and top == pytest.approx(TOP, abs=0.4)
