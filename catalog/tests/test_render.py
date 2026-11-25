import json
import struct
from pathlib import Path

import numpy as np
import pytest

from asset_catalog.model import Asset
from asset_catalog.render import camera, gltf
from asset_catalog.render.scene import build_scene


def _cube_glb(path: Path, colors: bool = True) -> None:
    """A unit cube (12 triangles) without material; per-vertex red when colors."""
    v = np.array([[x, y, z] for x in (0, 1) for y in (0, 1) for z in (0, 1)], np.float32)
    idx = np.array(
        [
            0,
            2,
            1,
            1,
            2,
            3,
            4,
            5,
            6,
            5,
            7,
            6,
            0,
            1,
            4,
            1,
            5,
            4,
            2,
            6,
            3,
            3,
            6,
            7,
            0,
            4,
            2,
            2,
            4,
            6,
            1,
            3,
            5,
            3,
            7,
            5,
        ],
        np.uint16,
    )
    col = np.tile(np.array([0.8, 0.1, 0.1, 1.0], np.float32), (8, 1))
    blob = v.tobytes() + idx.tobytes() + (col.tobytes() if colors else b"")
    attrs = {"POSITION": 0}
    views = [
        {"buffer": 0, "byteOffset": 0, "byteLength": v.nbytes},
        {"buffer": 0, "byteOffset": v.nbytes, "byteLength": idx.nbytes},
    ]
    accs = [
        {"bufferView": 0, "componentType": 5126, "count": 8, "type": "VEC3"},
        {"bufferView": 1, "componentType": 5123, "count": len(idx), "type": "SCALAR"},
    ]
    if colors:
        views.append({"buffer": 0, "byteOffset": v.nbytes + idx.nbytes, "byteLength": col.nbytes})
        accs.append({"bufferView": 2, "componentType": 5126, "count": 8, "type": "VEC4"})
        attrs["COLOR_0"] = 2
    js = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0}],
        "meshes": [{"primitives": [{"attributes": attrs, "indices": 1}]}],
        "buffers": [{"byteLength": len(blob)}],
        "bufferViews": views,
        "accessors": accs,
    }
    j = json.dumps(js).encode()
    j += b" " * (-len(j) % 4)
    blob += b"\0" * (-len(blob) % 4)
    total = 12 + 8 + len(j) + 8 + len(blob)
    path.write_bytes(
        b"glTF"
        + struct.pack("<II", 2, total)
        + struct.pack("<II", len(j), 0x4E4F534A)
        + j
        + struct.pack("<II", len(blob), 0x004E4942)
        + blob
    )


def _asset(path: Path) -> Asset:
    return Asset(
        id="repo/t/cube",
        source="repo",
        pack="t",
        name="cube",
        path=str(path),
        media="model",
        content_hash="x",
        bytes=1,
    )


def test_nice_length_is_round_and_fits():
    assert camera.nice_length(0.37) == (0.2, "20 cm")
    assert camera.nice_length(7.0) == (5.0, "5 m")
    assert camera.nice_length(0.04)[1] == "2 cm"


def test_layout_keeps_thin_wall_inside_the_picture():
    wall = np.array([[0, 0, 0], [20, 3, 0.2]], float)
    lay = camera.make_layout("front", wall, 768)
    left, top, right, bottom = lay.model_box
    assert 0 < left < right < 768 and 0 < top < bottom < 768
    assert lay.bar_m * lay.px_per_m < 0.4 * 768


def test_human_only_for_human_scale():
    assert camera.wants_human(np.zeros(3), np.array([1.0, 1.0, 1.0]))
    assert not camera.wants_human(np.zeros(3), np.array([0.05, 0.05, 0.05]))
    assert not camera.wants_human(np.zeros(3), np.array([100.0, 5.0, 5.0]))


def test_vertex_colours_become_a_surface(tmp_path):
    p = tmp_path / "cube.glb"
    _cube_glb(p)
    sc = build_scene(_asset(p), gltf.Doc(p))
    assert sc.triangles == 12 and sc.placeholder_triangles == 0
    assert sc.batches[0].surface.vertex_color
    assert np.allclose(sc.hi - sc.lo, 1.0)


def test_cube_renders_red_not_blank(tmp_path):
    gl = pytest.importorskip("asset_catalog.render.gl")
    try:
        r = gl.Renderer(256)
    except Exception:  # noqa: BLE001 - no EGL on this machine
        pytest.skip("no EGL context")
    p = tmp_path / "cube.glb"
    _cube_glb(p)
    doc = gltf.Doc(p)
    sc = build_scene(_asset(p), doc)
    gpu = r.upload(sc, doc)
    lay = camera.make_layout("three_quarter", sc.points, 256)
    rgb, cov = r.lit_view(sc, gpu, lay)
    assert (cov > 0.5).mean() > 0.1
    m = rgb[cov > 0.99].astype(float)
    assert m[:, 0].mean() > 1.5 * m[:, 1].mean()  # red stays red under the lighting
    albedo, mask = r.albedo_view(sc, gpu, lay)
    assert mask.sum() > 1000 and abs(int(albedo[mask][:, 0].mean()) - 231) < 4  # 0.8 linear
