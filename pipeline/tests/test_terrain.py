import json

import numpy as np
import pytest
import rasterio
from pyproj import Transformer
from rasterio.transform import from_origin
from shapely.geometry import box

from drycastle_pipeline.buildings import derive_building, load_building_rules
from drycastle_pipeline.frame import Frame
from drycastle_pipeline.terrain import (
    build_terrain,
    cells_per_chunk,
    flatten_buildings,
    grid_from_samples,
    sample_dmr,
)
from tests.synthetic import make_terrain


def black(pts):
    return np.zeros((len(pts), 3), np.uint8)


def test_cells_per_chunk_is_even_and_close_to_step() -> None:
    assert cells_per_chunk(250, 4) == 62
    assert cells_per_chunk(250, 5) == 50
    assert cells_per_chunk(250, 1000) == 2


@pytest.fixture
def dmr(tmp_path):
    """A 100 x 100 m DMR tile (EPSG:3046) with z = 200 + 0.05 (E - E0) + 0.02 (N - N0)."""
    frame = Frame.at(16.86, 48.40)
    e0, n0 = Transformer.from_crs("EPSG:4326", "EPSG:3046", always_xy=True).transform(16.86, 48.40)
    e0, n0 = round(e0), round(n0)
    rows = cols = 100
    cx, cy = np.meshgrid(np.arange(cols) + 0.5, np.arange(rows) + 0.5)
    left, top = e0 - 50, n0 + 50
    z = 200 + 0.05 * (left + cx - e0) + 0.02 * (top - cy - n0)
    raw = tmp_path / "raw"
    raw.mkdir()
    with rasterio.open(
        raw / "dmr5.tif",
        "w",
        driver="GTiff",
        height=rows,
        width=cols,
        count=1,
        dtype="float32",
        crs="EPSG:3046",
        transform=from_origin(left, top, 1, 1),
        nodata=3.4e38,
    ) as dst:
        dst.write(z.astype("float32"), 1)
    work = tmp_path / "work"
    work.mkdir()
    (work / "frame.json").write_text(
        json.dumps({"origin_epsg5514": [frame.origin_x, frame.origin_y], "area_polygon_epsg5514": [[(frame.origin_x - 50), (frame.origin_y - 50)], [(frame.origin_x + 1100), (frame.origin_y - 50)], [(frame.origin_x + 1100), (frame.origin_y + 1100)], [(frame.origin_x - 50), (frame.origin_y + 1100)]]})
    )
    return {"raw": raw, "work": work, "frame": frame, "e0n0": (e0, n0)}


def _local_of(dmr, de: float, dn: float) -> tuple[float, float]:
    e0, n0 = dmr["e0n0"]
    sx, sy = Transformer.from_crs("EPSG:3046", "EPSG:5514", always_xy=True).transform(
        e0 + de, n0 + dn
    )
    return sx - dmr["frame"].origin_x, sy - dmr["frame"].origin_y


def test_dmr_sampling_reprojects_and_marks_coverage(dmr) -> None:
    x, y = _local_of(dmr, 10, -20)
    origin = (dmr["frame"].origin_x, dmr["frame"].origin_y)
    z, inside = sample_dmr(
        dmr["raw"] / "dmr5.tif", origin, np.array([x, x + 500.0]), np.array([y, y])
    )
    assert inside.tolist() == [True, False]
    assert z[0] == pytest.approx(200 + 0.05 * 10 + 0.02 * -20, abs=1e-3)


def test_build_terrain_flattens_under_building(dmr) -> None:
    cx, cy = _local_of(dmr, 0, 0)
    b = derive_building(
        "w1", "P-0001", box(cx - 6, cy - 4, cx + 6, cy + 4), {}, load_building_rules()
    )
    (dmr["work"] / "buildings.json").write_text(json.dumps({"buildings": [b]}))
    t, elev = build_terrain(dmr["work"], dmr["raw"])
    # the tile is a plane through the centre, so the footprint mean is about 200 m
    assert elev["w1"] == pytest.approx(200.0, abs=0.1)
    xs, ys = np.meshgrid(np.linspace(cx - 6, cx + 6, 7), np.linspace(cy - 4, cy + 4, 5))
    assert np.abs(t.surface(xs, ys) - elev["w1"]).max() < 1e-2
    # only the 100 m tile is covered
    assert 0 < t.valid.sum() < t.valid.size
    assert t.coverage.area == pytest.approx(99 * 99, rel=0.01)


def test_flatten_changes_only_near_building() -> None:
    t = make_terrain()
    before = t.heights.copy()
    b = derive_building("w1", "P-0001", box(100, 100, 112, 108), {}, load_building_rules())
    elev = flatten_buildings(t, [b], 2.0, 6.0)
    xs, ys = t.vertex_xy()
    changed = np.abs(t.heights - before) > 1e-6
    dx = np.maximum(np.maximum(100 - xs, xs - 112), 0)
    dy = np.maximum(np.maximum(100 - ys, ys - 108), 0)
    assert changed.any()
    assert not changed[np.hypot(dx, dy) > 6 + t.step].any()
    assert elev["w1"] == pytest.approx(100 + 0.05 * 106 + 0.02 * 104, abs=0.05)
    inside = (xs >= 100) & (xs <= 112) & (ys >= 100) & (ys <= 108)
    assert np.abs(t.heights[inside] - elev["w1"]).max() < 1e-3


def test_surface_matches_mesh_triangles_and_coverage_is_respected() -> None:
    t = make_terrain()
    assert t.chunks() == [(0, 0)]  # coverage lies inside chunk (0, 0) only
    m = t.chunk_mesh(0, 0, black)
    c = m.tris.mean(axis=1)
    assert np.abs(t.surface(c[:, 0], c[:, 1]) - c[:, 2]).max() < 1e-6
    # only cells with all four corners inside the coverage are meshed
    assert m.tris[..., 0].min() >= 10 and m.tris[..., 0].max() <= 210
    assert m.tris[..., 1].min() >= 10 and m.tris[..., 1].max() <= 200
    n = np.cross(m.tris[:, 1] - m.tris[:, 0], m.tris[:, 2] - m.tris[:, 0])
    assert (n[:, 2] > 0).all()  # counter-clockwise seen from above
    assert 0 < t.chunk_mesh(0, 0, black, dec=2).face_count < m.face_count
    assert t.chunk_mesh(5, 5, black).face_count == 0


def test_chunk_border_vertices_are_shared() -> None:
    t = grid_from_samples(
        250.0, 4.0, box(10, 10, 400, 100), lambda x, y: (100 + 0.05 * x, np.ones_like(x, bool))
    )
    assert t.chunks() == [(0, 0), (1, 0)]
    a, b = t.chunk_mesh(0, 0, black), t.chunk_mesh(1, 0, black)
    edge_a = {tuple(v) for v in a.tris.reshape(-1, 3) if abs(v[0] - 250) < 1e-9}
    edge_b = {tuple(v) for v in b.tris.reshape(-1, 3) if abs(v[0] - 250) < 1e-9}
    assert edge_a and edge_a == edge_b


def test_cut_holes_removes_the_hole_and_keeps_the_rest_on_the_same_plane() -> None:
    from shapely.geometry import Polygon

    from drycastle_pipeline.meshes import Mesh
    from drycastle_pipeline.terrain import cut_holes

    # one 4 m cell of two triangles on the plane z = 0.5 x + 1, facing up
    z = lambda x, y: 0.5 * x + 1.0
    sq = [(0, 0), (4, 0), (4, 4), (0, 4)]
    tris = np.array([[(*sq[a], z(*sq[a])) for a in t] for t in ((0, 1, 2), (0, 2, 3))], float)
    mesh = Mesh(tris, np.array([[10, 20, 30], [40, 50, 60]], np.uint8))
    hole = Polygon([(1.5, 1.5), (2.5, 1.5), (2.5, 2.5), (1.5, 2.5)])
    out = cut_holes(mesh, [hole])
    area = 0.0
    for t in out.tris:
        assert (np.cross(t[1] - t[0], t[2] - t[0])[2] > 0)  # still faces up
        assert t[:, 2] == pytest.approx(z(t[:, 0], t[:, 1]))  # same plane
        area += Polygon(t[:, :2]).area
        assert not Polygon(t[:, :2]).intersection(hole).area > 1e-9  # nothing inside the hole
    assert area == pytest.approx(16 - 1)
    assert cut_holes(mesh, []) is mesh
    far = Polygon([(10, 10), (11, 10), (11, 11), (10, 11)])
    assert cut_holes(mesh, [far]).face_count == 2
