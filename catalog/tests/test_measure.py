"""measure stage on tiny GLBs built here; no private assets."""

from __future__ import annotations

import json
import struct
from pathlib import Path

import numpy as np
import pytest

from asset_catalog.model import Asset
from asset_catalog.stages import measure

_CUBE_IDX = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
             3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5]  # fmt: skip


def box(x0, y0, z0, x1, y1, z1) -> tuple[np.ndarray, list[int]]:
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
         (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]  # fmt: skip
    return np.array(v, dtype="<f4"), _CUBE_IDX


def merge(boxes) -> tuple[np.ndarray, list[int]]:
    pos, idx = np.zeros((0, 3), dtype="<f4"), []
    for p, i in boxes:
        idx += [k + len(pos) for k in i]
        pos = np.concatenate([pos, p])
    return pos, idx


def make_glb(path: Path, meshes, nodes, **extra) -> None:
    """meshes: [(positions (n,3), indices)]; nodes: gltf node dicts (node 0 is the scene root)."""
    blob, views, accs, prims = b"", [], [], []
    for pos, idx in meshes:
        for data, kind, comp, count, bounds in (
            (pos.astype("<f4").tobytes(), "VEC3", 5126, len(pos), (pos.min(0), pos.max(0))),
            (np.array(idx, dtype="<u2").tobytes(), "SCALAR", 5123, len(idx), None),
        ):
            views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
            acc = {
                "bufferView": len(views) - 1,
                "componentType": comp,
                "count": count,
                "type": kind,
            }
            if bounds:
                acc["min"], acc["max"] = bounds[0].tolist(), bounds[1].tolist()
            accs.append(acc)
            blob += data + b"\0" * (-len(data) % 4)
        prims.append(
            {"primitives": [{"attributes": {"POSITION": len(accs) - 2}, "indices": len(accs) - 1}]}
        )
    doc = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": nodes,
        "meshes": prims,
        "accessors": accs,
        "bufferViews": views,
        "buffers": [{"byteLength": len(blob)}],
        **extra,
    }
    js = json.dumps(doc).encode()
    js += b" " * (-len(js) % 4)
    body = struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942)
    path.write_bytes(b"glTF" + struct.pack("<II", 2, 12 + len(body) + len(blob)) + body + blob)


def measured(tmp_path: Path, meshes, nodes, **extra) -> measure.Measure:
    p = tmp_path / "a.glb"
    make_glb(p, meshes, nodes, **extra)
    asset = Asset(
        id="t/t/a",
        source="t",
        pack="t",
        name="a",
        path=str(p),
        media="model",
        content_hash="x",
        bytes=p.stat().st_size,
    )
    return measure.compute(asset)


def test_box_on_the_ground(tmp_path):
    m = measured(
        tmp_path,
        [box(-0.5, 0, -0.5, 0.5, 2, 0.5)],
        [{"children": [1]}, {"mesh": 0, "translation": [0, 0, 0]}],
    )
    g = m.geometry
    assert g.size_m == [1.0, 2.0, 1.0]
    assert g.size_class == "furniture"
    assert g.pivot.kind == "base-centre"
    assert g.form == "block"
    assert set(g.flat_faces) == {"-x", "+x", "-y", "+y", "-z", "+z"}
    assert g.symmetric == ["x", "z"]
    assert (m.tris, m.verts, m.mesh_count, m.has_rig) == (12, 8, 1, False)


def test_node_transform_is_applied(tmp_path):
    # 2 x 1 x 0.02 panel, turned 90 degrees about X (quaternion) and lifted 3 m
    s = np.sqrt(0.5)
    m = measured(
        tmp_path,
        [box(-1, -0.5, -0.01, 1, 0.5, 0.01)],
        [{"children": [1]}, {"mesh": 0, "rotation": [s, 0, 0, s], "translation": [0, 3, 0]}],
    )
    g = m.geometry
    assert g.size_m == pytest.approx([2.0, 0.02, 1.0], abs=1e-4)
    assert g.min_m[1] == pytest.approx(2.99, abs=1e-4)
    assert (g.form, g.form_axis) == ("plate", "y")
    assert g.pivot.kind == "other"


def test_table_top_is_flat_but_underside_is_not(tmp_path):
    top = box(-0.5, 0.7, -0.5, 0.5, 0.75, 0.5)
    legs = [box(x, 0, z, x + 0.05, 0.7, z + 0.05) for x in (-0.5, 0.45) for z in (-0.5, 0.45)]
    m = measured(tmp_path, [merge([top, *legs])], [{"children": [1]}, {"mesh": 0}])
    flat = m.geometry.flat
    assert flat["+y"] > 0.95
    assert flat["-y"] < 0.1  # four leg ends
    assert m.geometry.flat_faces == ["+y"]


def test_placeholder_mesh_has_no_geometry_and_rig_is_read(tmp_path):
    tiny = box(0, 0, 0, 0.0001, 0.0001, 0.0001)
    nodes = [{"children": [1, 2]}, {"mesh": 0}, {"name": "hip"}]
    anim = [{"name": "walk", "samplers": [{"input": 0, "output": 0}], "channels": []}]
    m = measured(tmp_path, [tiny], nodes, skins=[{"joints": [2]}], animations=anim)
    assert m.geometry is None
    assert m.has_rig and m.joints == 1
    assert m.animations[0].name == "walk"
    assert m.animations[0].duration_s == pytest.approx(0.0001)  # input accessor max
    assert "placeholder" in m.notes[0]


def test_hidden_and_collider_nodes_are_not_counted(tmp_path):
    hidden = {"mesh": 1, "extensions": {"KHR_node_visibility": {"visible": False}}}
    collider = {"extensions": {"OMI_physics_body": {"motion": {"type": "static"}}}, "children": [4]}
    nodes = [
        {"children": [1, 2, 3]},
        {"mesh": 0},
        hidden,
        collider,
        {"extensions": {"OMI_physics_body": {"collider": {"shape": 0}}}},
    ]
    m = measured(tmp_path, [box(0, 0, 0, 1, 1, 1), box(0, 0, 0, 9, 9, 9)], nodes)
    assert m.geometry.size_m == [1.0, 1.0, 1.0]
    assert m.mesh_count == 1
    assert m.node_count == 2  # root + mesh node


@pytest.mark.parametrize(
    ("longest", "name"),
    [
        (0.1, "hand"),
        (0.5, "tabletop"),
        (1.0, "furniture"),
        (4.5, "vehicle-scale"),
        (10, "room"),
        (15, "building"),
        (100, "terrain"),
    ],
)
def test_size_class(longest, name):
    assert measure.size_class(longest) == name


@pytest.mark.parametrize(
    ("size", "expected"),
    [
        ((2, 1, 0.05), ("plate", "z")),
        ((0.2, 4, 0.2), ("pole", "y")),
        ((0.7, 7.5, 0.16), ("strip", "y")),
        ((0.5, 1, 0.5), ("block", "y")),
    ],
)
def test_form(size, expected):
    assert measure.form_of(np.array(size, dtype=float)) == expected


def test_pivot_kinds():
    lo, hi = np.array([-1, 0, -1.0]), np.array([1, 2, 1.0])
    assert measure.pivot_of(lo, hi).kind == "base-centre"
    assert measure.pivot_of(np.array([0, 0, -1.0]), hi).kind == "base-edge"
    assert measure.pivot_of(np.array([0, 0, 0.0]), hi).kind == "base-corner"
    mid = np.array([1, 1, 1.0])
    assert measure.pivot_of(-mid, mid).kind == "centre"
    assert measure.pivot_of(np.array([-1, -2, -1.0]), np.array([1, 0, 1.0])).kind == "top-centre"
    assert measure.pivot_of(np.array([-1, -0.7, -1.0]), hi).kind == "other"
