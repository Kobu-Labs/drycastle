"""The NAME (MODULE): the mesh builds on its footprint."""

import pytest

from drycastle_pipeline.buildings.landmarks import MODULE as m
from drycastle_pipeline.buildings.landmarks.catalog import build_landmark


def _building():
    return {"id": m.BUILDING, "model": m.MODEL, "footprint": {"exterior": [list(p) for p in m.RING], "holes": []}}


def test_meshes_build_and_are_fixed():
    vis, col = build_landmark(_building(), True), build_landmark(_building(), False)
    assert vis.face_count > col.face_count >= 12
    assert vis.tris[..., 2].min() <= 0.0 and 2.0 < vis.tris[..., 2].max() < 15.0  # stands on the base, a sane height
    assert abs(vis.tris[..., 2].max() - col.tris[..., 2].max()) < 1.0  # the collision is the same massing
    assert build_landmark(_building(), True).tris.tobytes() == vis.tris.tobytes()  # deterministic


def test_a_changed_footprint_is_refused():
    with pytest.raises(ValueError):
        m.building_mesh({"id": m.BUILDING, "footprint": {"exterior": [[0, 0], [1, 0], [1, 1]], "holes": []}})
