import json
import struct

import numpy as np
import pytest

from drycastle_pipeline.assets import ASSETS_DIR, DEFAULT_MANIFEST
from drycastle_pipeline.edits import COLUMN_TYPES, FENCE_TYPES, parse
from drycastle_pipeline.export import build_chunk_scene, parcel_props_nodes
from drycastle_pipeline.fencestyles import COLUMN_STYLES, FENCE_STYLES, styled_fence_meshes
from drycastle_pipeline.propmeshes import fence_meshes, parcel_props_meshes
from drycastle_pipeline.rules import load_section
from drycastle_pipeline.textures import (
    flat_mesh,
    load_textures,
    split_tex_node,
    tex_node_name,
    texture,
)

PROPS = load_section("props")
FENCE = PROPS["fences"]


def run(ftype: str, n: int = 2, slope: float = 0.0, **extra) -> list[dict]:
    """``n`` connected 3 m sections along x, rising ``slope`` metres per section."""
    style = {"type": ftype, **{k: v for k, v in FENCE_TYPES[ftype].items()}, **extra}
    return [
        {"parcel": "P-0001", "a": [3.0 * i, 0.0], "b": [3.0 * (i + 1), 0.0],
         "za": slope * i, "zb": slope * (i + 1), **style}
        for i in range(n)
    ]


# --- the texture library ---------------------------------------------------------------------


def test_every_library_texture_has_its_file_with_the_listed_size() -> None:
    doc = json.loads(DEFAULT_MANIFEST.read_text())
    assert {t["id"] for t in doc["textures"]} == set(load_textures())
    assert any(p["name"] == "drycastle-generated-textures" for p in doc["packs"])
    for t in doc["textures"]:
        data = (ASSETS_DIR / t["file"]).read_bytes()
        assert data[:8] == b"\x89PNG\r\n\x1a\n", t["id"]
        w, h, depth, colour_type = struct.unpack(">IIBB", data[16:26])
        assert [w, h] == t["size_px"] and (depth, colour_type) == (8, 6), t["id"]  # RGBA8
        assert w & (w - 1) == 0 and h & (h - 1) == 0, t["id"]  # powers of two: mipmaps, repeat
        assert t["licence"] == "CC0" and t["tile_m"][0] > 0 and t["tile_m"][1] >= 0
        # an id is part of a node name: lower case, digits and underscores only, and no "_tex_"
        assert t["id"].replace("_", "").isalnum() and t["id"] == t["id"].lower()
        assert "_tex_" not in t["id"] and not t["id"].endswith("_collision")


def test_texture_uv_repeats_from_the_top_or_stretches_once() -> None:
    brick = texture("brick_red")  # 1.0 x 0.6 m
    assert brick.uv(2.5, 1.8, 1.8) == (2.5, 0.0)  # the top of the wall is the top of a course
    assert brick.uv(0.0, 0.0, 1.8)[1] == pytest.approx(-3.0)
    picket = texture("picket")  # stretched over the height
    lo, hi = picket.uv(0.0, 0.0, 1.0)[1], picket.uv(0.0, 1.0, 1.0)[1]
    assert 0 < lo < 0.01 and 0.99 < hi < 1 and picket.uv(1.0, 0.5, 1.0)[0] == 2.0
    assert picket.alpha and picket.tintable and not brick.alpha and not brick.tintable


def test_tex_node_names_split_back() -> None:
    name = tex_node_name("parcel_P-0001_props", "brick_red")
    assert name == "parcel_P-0001_props_tex_brick_red"
    assert split_tex_node(name) == ("parcel_P-0001_props", "brick_red")
    assert split_tex_node("parcel_P-0001_props") == ("parcel_P-0001_props", None)
    with pytest.raises(KeyError):
        texture("no_such_texture")


# --- styles ----------------------------------------------------------------------------------


def test_style_tables_cover_the_schema() -> None:
    assert set(FENCE_STYLES) == set(FENCE_TYPES)
    assert set(COLUMN_STYLES) | {"none"} == set(COLUMN_TYPES)
    lib = load_textures()
    for name, style in FENCE_STYLES.items():
        assert style.texture in lib and style.column in COLUMN_STYLES, name
        assert style.panel in ("sheet", "wall")
        assert lib[style.texture].alpha == (style.panel == "sheet"), name
    assert all(c.texture is None or c.texture in lib for c in COLUMN_STYLES.values())
    assert FENCE_STYLES["brick_wall"] is FENCE_STYLES["brick_red"]


@pytest.mark.parametrize("ftype", sorted(FENCE_TYPES))
def test_every_type_builds_its_texture_and_flat_parts(ftype: str) -> None:
    flat, textured = styled_fence_meshes(run(ftype), FENCE)
    style = FENCE_STYLES[ftype]
    assert style.texture in textured and flat.face_count > 0  # posts or caps
    for tid, mesh in textured.items():
        assert mesh.tris.shape == (mesh.face_count, 3, 3) and mesh.uvs.shape == (mesh.face_count, 3, 2)
        assert np.isfinite(mesh.tris).all() and np.isfinite(mesh.uvs).all()
        want = FENCE_TYPES[ftype]["colour"]
        tint = [int(want[i : i + 2], 16) for i in (1, 3, 5)]
        assert (mesh.colours == tint).all(), tid
    height = FENCE_TYPES[ftype]["height_m"]
    top = max(float(m.tris[..., 2].max()) for m in textured.values())
    assert height <= top <= height + 0.5
    assert min(float(m.tris[..., 2].min()) for m in textured.values()) < 0  # sunk into the ground


def test_untyped_sections_stay_plain_boxes() -> None:
    plain = [{k: v for k, v in s.items() if k != "type"} for s in run("picket")]
    styled_flat, styled_textured = styled_fence_meshes(plain, FENCE)
    assert styled_flat.face_count == 0 and styled_textured == {}
    flat, textured = fence_meshes(plain, FENCE)
    assert flat.face_count == 10 * len(plain) and textured == {}
    # the rules can give every rule fence a type
    flat, textured = fence_meshes(plain, {**FENCE, "type": "wire"})
    assert set(textured) == {"wire"} and flat.face_count > 0


def test_sheet_is_two_sided_and_follows_the_slope() -> None:
    _, textured = styled_fence_meshes(run("picket", n=1, slope=0.6, column="none"), FENCE)
    mesh = textured["picket"]
    assert mesh.face_count == 4  # one quad, both sides
    normals = np.cross(mesh.tris[:, 1] - mesh.tris[:, 0], mesh.tris[:, 2] - mesh.tris[:, 0])
    assert (normals[:, 1] > 0).sum() == 2 and (normals[:, 1] < 0).sum() == 2
    z = mesh.tris[..., 2]
    assert z.max() == pytest.approx(0.6 + 1.0) and z.min() == pytest.approx(-0.1)
    # 3 m of a 0.5 m tile: six repeats; the height is one stretch
    assert np.ptp(mesh.uvs[..., 0]) == pytest.approx(6.0) and np.ptp(mesh.uvs[..., 1]) < 1


def test_wall_tops_are_level_and_the_pattern_runs_on_through_a_run() -> None:
    sections = run("brick_red", n=2, slope=0.5, column="none")
    flat, textured = styled_fence_meshes(sections, FENCE)
    mesh = textured["brick_red"]
    side = mesh.tris[np.isclose(mesh.tris[..., 1], -0.15).all(axis=1)]  # the face at y = -thickness / 2
    for x0, top in ((0.0, 0.5 + 1.8), (3.0, 1.0 + 1.8)):
        part = side[(side[..., 0] >= x0 - 1e-9).all(axis=1) & (side[..., 0] <= x0 + 3 + 1e-9).all(axis=1)]
        assert part[..., 2].max() == pytest.approx(top)  # stepped: level over the higher end
    uv = mesh.uvs[np.isclose(mesh.tris[..., 1], -0.15).all(axis=1)]
    assert uv[..., 0].min() == pytest.approx(0.0) and uv[..., 0].max() == pytest.approx(6.0)  # 6 m of a 1 m tile
    assert uv[..., 1].max() == pytest.approx(0.0)  # anchored at the top
    assert flat.face_count == 12 * len(sections)  # one coping box per section, no columns


def test_columns_stand_at_joints_and_can_be_swapped() -> None:
    own = styled_fence_meshes(run("picket", n=2), FENCE)
    # 3 m sections, a post every 2 m at most: both ends, the joint and one inside each section
    assert own[0].face_count == 5 * 10 and set(own[1]) == {"picket"}
    flat, textured = styled_fence_meshes(run("picket", n=2, column="brick_red"), FENCE)
    assert set(textured) == {"picket", "brick_red"}
    piers = textured["brick_red"]
    assert piers.face_count == 5 * 8 and flat.face_count == 5 * 12  # four textured sides, a cap box each
    assert (piers.colours == 255).all()  # another material than the panel: its own tint, not the fence's
    assert piers.tris[..., 2].max() > 1.0  # stands above the 1.0 m pickets
    none = styled_fence_meshes(run("picket", n=2, column="none"), FENCE)
    assert none[0].face_count == 0 and set(none[1]) == {"picket"}
    # a closed ring does not get two columns at the same point
    ring = [
        {**run("stone_wall", n=1)[0], "a": a, "b": b}
        for a, b in (([0, 0], [3, 0]), ([3, 0], [3, 3]), ([3, 3], [0, 3]), ([0, 3], [0, 0]))
    ]
    assert styled_fence_meshes(ring, FENCE)[1]["stone"].face_count == 4 * 4 + 4 * 8


def test_concrete_is_painted_by_its_colour() -> None:
    _, textured = styled_fence_meshes(run("concrete", colour="#3a7d44"), FENCE)
    assert set(textured) == {"concrete"} and (textured["concrete"].colours == [0x3A, 0x7D, 0x44]).all()
    # previews show the paint, slightly greyed by the texture
    preview = flat_mesh(textured).colours[0]
    assert preview[1] > preview[0] and preview[1] > preview[2]


# --- export ----------------------------------------------------------------------------------


def test_textured_nodes_carry_uvs_and_tint_but_no_image() -> None:
    import trimesh

    sections = run("brick_white", colour="#ffeedd")
    visual, collision, textured = parcel_props_meshes(sections, [], PROPS)
    assert visual.face_count and collision.face_count == 12 * len(sections) and textured
    nodes = parcel_props_nodes([("P-0001", visual, collision, textured)])
    assert sorted(nodes) == [
        "parcel_P-0001_props", "parcel_P-0001_props_collision", "parcel_P-0001_props_tex_brick_white",
    ]
    data = build_chunk_scene([], nodes).export(file_type="glb")
    length = struct.unpack("<I", data[12:16])[0]
    doc = json.loads(data[20 : 20 + length])
    assert "images" not in doc and "textures" not in doc
    by_name = {m["name"]: m["primitives"][0] for m in doc["meshes"]}
    tex = by_name["parcel_P-0001_props_tex_brick_white"]
    assert {"POSITION", "TEXCOORD_0", "COLOR_0"} <= set(tex["attributes"])
    assert doc["materials"][tex["material"]]["name"] == "tex:brick_white"
    assert "TEXCOORD_0" not in by_name["parcel_P-0001_props"]["attributes"]
    # v is flipped for glTF: the top of the wall (pipeline v = 0) is at glTF v = 1 (= 0 when repeated)
    loaded = trimesh.load(trimesh.util.wrap_as_stream(data), file_type="glb", force="scene")
    geom = loaded.geometry["parcel_P-0001_props_tex_brick_white"]
    assert len(geom.faces) == textured["brick_white"].face_count
    assert geom.visual.uv[:, 1].max() == pytest.approx(0.0)


# --- schema ----------------------------------------------------------------------------------


def edits_with(fence: dict) -> dict:
    return {"version": 1, "parcels": {"P-0001": {"fence": fence}}, "groups": {}}


def test_new_types_and_column_validate() -> None:
    for ftype in FENCE_TYPES:
        parse(edits_with({"type": ftype}))
    for column in COLUMN_TYPES:
        parse(edits_with({"type": "wire", "column": column}))
    for bad, message in (
        ({"type": "wire", "column": "marble"}, "fence.column: must be one of"),
        ({"mode": "auto", "column": "stone"}, "fence.column: not used"),
        ({"type": "brick"}, "fence.type: must be one of"),
    ):
        with pytest.raises(ValueError, match=message):
            parse(edits_with(bad))
