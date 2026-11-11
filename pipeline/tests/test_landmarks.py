import numpy as np
import pytest
import yaml

from suchohrad_pipeline.buildings import RulesError, load_overrides
from suchohrad_pipeline.landmarks import MODELS, build_landmark

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
