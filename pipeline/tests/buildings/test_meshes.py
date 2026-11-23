import numpy as np
import pytest
from shapely.affinity import rotate
from shapely.geometry import Polygon, box

from drycastle_pipeline.buildings import derive_building, load_building_rules
from drycastle_pipeline.export import to_trimesh
from drycastle_pipeline.meshes import building_mesh, collision_mesh

RULES = load_building_rules()


def entry(poly, roof, eave=3.0, rise=2.0, ridge=0.0):
    b = derive_building("w1", "P-0001", poly, {}, RULES, {"roof": "flat"})
    rise = rise if roof != "flat" else 0.0
    b.update(roof=roof, eave_height_m=eave, roof_height_m=rise, height_m=eave + rise)
    b["ridge_dir"] = None if roof == "flat" else ridge
    b["wall_colour"], b["roof_colour"] = [200, 190, 170], [160, 60, 40]
    return b


def volume(mesh) -> float:
    assert np.isfinite(mesh.tris).all()
    assert len(mesh.colours) == len(mesh.tris)
    vol = to_trimesh(mesh).volume
    assert vol > 0
    return vol


def test_flat_box_counts_and_volume() -> None:
    m = building_mesh(entry(box(0, 0, 10, 6), "flat"))
    assert m.face_count == 12  # 4 walls x2 + roof x2 + floor x2
    assert volume(m) == pytest.approx(10 * 6 * 3.0)


def test_gabled_box_counts_and_volume() -> None:
    m = building_mesh(entry(box(0, 0, 10, 6), "gabled"))
    assert m.face_count == 18  # walls 12 (gable ends split at the ridge), roof 4, floor 2
    assert volume(m) == pytest.approx(60 * 3.0 + 0.5 * 6 * 2.0 * 10)
    assert m.tris[:, :, 2].max() == pytest.approx(5.0)


def test_hipped_box_counts_and_volume() -> None:
    m = building_mesh(entry(box(0, 0, 10, 6), "hipped"))
    assert m.face_count == 16  # walls 8, roof 2 trapezoids + 2 triangles = 6, floor 2
    # hip roof volume W*L*h/2 - W^2*h/6
    assert volume(m) == pytest.approx(60 * 3.0 + 60 * 2.0 / 2 - 36 * 2.0 / 6)
    assert m.tris[:, :, 2].max() == pytest.approx(5.0)


def test_square_hipped_is_a_pyramid() -> None:
    m = building_mesh(entry(box(0, 0, 8, 8), "hipped"))
    assert m.face_count == 14  # walls 8, roof 4, floor 2
    assert volume(m) == pytest.approx(64 * 3.0 + 64 * 2.0 / 3)


def test_colours_are_flat_per_face() -> None:
    m = building_mesh(entry(box(0, 0, 10, 6), "gabled"))
    assert {tuple(c) for c in m.colours} == {(200, 190, 170), (160, 60, 40)}


def test_rotated_gabled_roof_is_valid() -> None:
    poly = rotate(box(0, 0, 12, 7), 33, origin=(0, 0))
    m = building_mesh(entry(poly, "gabled", ridge=float(np.radians(33))))
    assert m.face_count == 18
    assert volume(m) == pytest.approx(84 * 3.0 + 0.5 * 7 * 2.0 * 12, rel=1e-3)


def test_non_convex_and_holed_footprints() -> None:
    l_shape = Polygon([(0, 0), (12, 0), (12, 4), (4, 4), (4, 12), (0, 12)])
    assert volume(building_mesh(entry(l_shape, "flat"))) == pytest.approx(l_shape.area * 3.0)
    ring = Polygon(box(0, 0, 20, 20).exterior.coords, [box(8, 8, 12, 12).exterior.coords])
    assert volume(building_mesh(entry(ring, "flat"))) == pytest.approx(ring.area * 3.0)


def test_pitched_roof_on_irregular_footprint() -> None:
    poly = Polygon([(0, 0), (10, 0), (10.5, 6), (0.3, 6.2)])
    for roof in ("gabled", "hipped"):
        volume(building_mesh(entry(poly, roof)))


def test_collision_mesh_is_simple() -> None:
    c = collision_mesh(entry(box(0, 0, 10, 6), "gabled"))
    assert c.face_count == 12
    assert c.tris[:, :, 2].max() == pytest.approx(4.0)
    assert volume(c) == pytest.approx(60 * 4.0)
