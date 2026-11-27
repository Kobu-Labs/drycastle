"""Destroyable fences (docs/fence-destruction.md, Contracts 1 and 2): pieces, TEXCOORD_1, wall nodes and grids, fences.json keys."""

import json
import struct

import numpy as np
import pytest
import yaml

from drycastle_pipeline.buildings import voxels
from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.edits.schema import FENCE_TYPES
from drycastle_pipeline.export.export import run_export
from drycastle_pipeline.export.gltf import build_chunk_scene, export_glb
from drycastle_pipeline.props.fencestyles import section_docs, styled_fence_meshes, wall_parts
from drycastle_pipeline.props.fencewalls import wall_grid, wall_nodes
from drycastle_pipeline.props.props import run_props
from tests.synthetic import make_workspace

FENCE = load_section("props")["fences"]
PITCH = 0.125


def run(ftype: str | None, n: int = 2, first: int = 0, length: float = 3.0, slope: float = 0.0, **extra) -> list[dict]:
    """``n`` connected sections along x, with fences.json indices from ``first``."""
    style = {"type": ftype, **FENCE_TYPES[ftype]} if ftype else {}
    return [
        {"parcel": "P-0001", "index": first + i, "a": [length * i, 0.0], "b": [length * (i + 1), 0.0],
         "za": slope * i, "zb": slope * (i + 1), **style, **extra}
        for i in range(n)
    ]


def test_picket_piece_edges_sit_in_the_middle_of_the_gaps_between_boards() -> None:
    docs = section_docs(run("picket", n=3, length=3.3, column="none"), FENCE)
    along = 0.0
    for i in range(3):
        d = docs[i]
        assert d["kind"] == "picket" and d["piece_m"] == pytest.approx(PITCH)
        assert -PITCH < d["piece0_m"] <= 0 and d["uv"][0] == pytest.approx(along / 0.5) and d["uv"][1] == 2.0
        assert d["pieces"] * PITCH + d["piece0_m"] >= 3.3 - 1e-6 > (d["pieces"] - 1) * PITCH + d["piece0_m"]
        # a board is 0.7 of its pitch from the pitch start, the gap the rest: the edge is at 0.85 of a pitch
        edge = (along + d["piece0_m"]) / PITCH
        assert edge - np.floor(edge) == pytest.approx(0.85, abs=1e-4)
        along += 3.3


def test_wire_and_plain_pieces_are_uniform_from_the_start() -> None:
    wire = section_docs(run("wire", n=1, length=2.2), FENCE)[0]
    assert (wire["pieces"], wire["piece0_m"]) == (5, 0.0) and wire["piece_m"] == pytest.approx(2.2 / 5, abs=1e-6)
    plain = section_docs(run(None, n=1, length=4.0), FENCE)[0]
    assert (plain["kind"], plain["pieces"], plain["piece_m"], plain["piece0_m"]) == ("plain", 16, 0.25, 0.0)
    assert plain["thickness_m"] == FENCE["thickness_m"] and plain["colour"] == FENCE["colour"]
    assert section_docs(run("brick_red", n=1), FENCE)[0]["kind"] == "wall" and "pieces" not in section_docs(run("brick_red", n=1), FENCE)[0]


def test_texcoord_1_is_the_section_and_the_piece_coordinate_on_every_vertex() -> None:
    sections = run("picket", n=2, first=7, column="none")
    docs = section_docs(sections, FENCE)
    _, textured = styled_fence_meshes(sections, FENCE)
    mesh = textured["picket"]
    assert mesh.uv2.shape == mesh.uvs.shape
    for i, x0 in ((7, 0.0), (8, 3.0)):
        sel = np.isclose(mesh.tris[..., 0], x0).any(axis=1) & (mesh.tris[..., 0] >= x0 - 1e-9).all(axis=1) & (mesh.tris[..., 0] <= x0 + 3 + 1e-9).all(axis=1)
        uv2, pos = mesh.uv2[sel], mesh.tris[sel]
        assert (uv2[..., 0] == i + 0.5).all()
        d = docs[i]
        along = pos[..., 0] - x0
        assert np.allclose(uv2[..., 1], (along - d["piece0_m"]) / d["piece_m"])
        assert d["uv"][0] == pytest.approx(mesh.uvs[sel][..., 0].min(), abs=1e-6)


def test_plain_box_vertices_carry_the_piece_coordinate_too_and_the_glb_has_texcoord_1() -> None:
    sections = run(None, n=1, first=3, length=2.0)
    _, textured = styled_fence_meshes(sections, FENCE)
    mesh = textured["plain"]
    along = mesh.tris[..., 0]
    assert (mesh.uv2[..., 0] == 3.5).all() and np.allclose(mesh.uv2[..., 1], along / 0.25)
    data = export_glb(build_chunk_scene([], {"props_tex_plain": mesh}))
    n = struct.unpack("<I", data[12:16])[0]
    attrs = json.loads(data[20 : 20 + n])["meshes"][0]["primitives"][0]["attributes"]
    assert "TEXCOORD_1" in attrs and "TEXCOORD_0" in attrs


def test_a_section_without_an_index_has_no_second_uv_set() -> None:
    sections = [{k: v for k, v in s.items() if k != "index"} for s in run("picket", n=1)]
    assert styled_fence_meshes(sections, FENCE)[1]["picket"].uv2 is None


def test_wall_sections_are_apart_with_their_nodes_collision_and_piers() -> None:
    sections = run("brick_red", n=2, first=5, slope=0.5)
    parts = wall_parts(sections, FENCE)
    assert sorted(parts) == [5, 6]
    # the pier at the joint is made once, and belongs to the lower index: 5 has both its ends, 6 only its far end
    assert [len(parts[i].spec.piers) for i in (5, 6)] == [2, 1]
    for i in (5, 6):
        names = sorted(wall_nodes(parts[i]))
        base = f"house_fence_{i:05d}"
        assert names == [f"{base}_collision", f"{base}_visual", f"{base}_visual_tex_brick_red"]
        coll = wall_nodes(parts[i])[f"{base}_collision"]
        assert coll.face_count == 12 * (1 + len(parts[i].spec.piers))
        assert parts[i].flat.face_count > 0  # caps
    # all together in the shared mesh a wall is still drawn (previews): same textures, no apart nodes
    flat, textured = styled_fence_meshes(sections, FENCE)
    assert set(textured) == {"brick_red"} and flat.face_count > 0


def test_wall_grid_is_a_thin_wall_on_a_foundation_with_the_wall_material() -> None:
    sections = run("brick_red", n=1, first=12, slope=0.5)  # 3 m, 1.8 m high, rises 0.5 m
    grid = wall_grid(wall_parts(sections, FENCE)[12].spec)
    assert grid.id == "fence_00012" and grid.cell == 0.25 and grid.yaw == 0.0
    mats = grid.materials()
    nz, ny, nx = mats.shape
    assert (nx, ny) == (14, 1)  # 12 cells of wall and a cell of each pier; the 0.08 m wall and the 0.44 m piers are one cell thick
    column = mats[:, 0, nx // 2]  # mid-wall: foundation on the bottom layer, then brick, no gap
    solid = np.flatnonzero(column)
    assert solid[0] == 0 and (np.diff(solid) == 1).all() and column[0] == voxels.FOUNDATION and column[solid[-1]] == voxels.BRICK
    # the ground line rises from 0 to 0.5 m: foundation up to it, the wall column at x = 1.5 m (ground 0.25 m) has one layer of it
    assert (column == voxels.FOUNDATION).sum() == 1
    assert (mats[:, 0, nx - 1] == voxels.FOUNDATION).sum() == 2  # the high end: 0.5 m of foundation
    assert grid.origin[2] <= 0 and grid.origin[2] + nz * 0.25 >= 0.5 + 1.8  # from the foot to the top of the wall
    assert mats[-1, 0, 0] == voxels.BRICK and mats[-1, 0, nx // 2] == voxels.AIR  # the piers stand higher than the wall
    assert set(np.unique(mats)) == {voxels.AIR, voxels.FOUNDATION, voxels.BRICK}
    # a stone pier is 0.5 m: three rows across, the middle one the wall
    stone = wall_grid(wall_parts(run("stone_wall", n=1, length=2.0), FENCE)[0].spec).materials()
    assert stone.shape[1] == 3 and (stone[:, 0, stone.shape[2] // 2] == voxels.AIR).all() and (stone[:, 1, stone.shape[2] // 2] != voxels.AIR).any()
    # same input, same bytes, and the file round-trips
    again = wall_grid(wall_parts(sections, FENCE)[12].spec)
    assert voxels.encode([grid]) == voxels.encode([again])
    back = voxels.decode(voxels.encode([grid]))[0]
    assert back.id == grid.id and (back.cells == grid.cells).all()


def test_wall_grid_materials_follow_the_type_and_the_pier() -> None:
    for ftype, mat in (("stone_wall", voxels.STONE), ("concrete", voxels.CONCRETE), ("brick_white", voxels.BRICK)):
        sections = run(ftype, n=1, length=2.0)
        mats = wall_grid(wall_parts(sections, FENCE)[0].spec).materials()
        assert mat in set(np.unique(mats)) and set(np.unique(mats)) <= {voxels.AIR, voxels.FOUNDATION, mat}
    # a wire wall on a brick pier would be a mix; a brick wall on a stone pier is brick wall, stone pier
    mats = wall_grid(wall_parts(run("brick_red", n=1, length=2.0, column="stone"), FENCE)[0].spec).materials()
    assert {voxels.BRICK, voxels.STONE} <= set(np.unique(mats))


# --- the export: fences.json keys, nodes and grids of a real (synthetic) run -----------------------


def _export(tmp_path, fence_rules: dict):
    ws = make_workspace(tmp_path)
    rules = tmp_path / "rules.yaml"
    rules.write_text(yaml.safe_dump({"props": {"fences": {"probability": 1.0, **fence_rules}}}))
    no_edits = tmp_path / "no_edits.json"
    run_props(ws["work"], rules, no_edits)
    manifest = run_export(ws["work"], ws["out"], ws["raw"], rules, ws["overrides"], edits_path=no_edits)
    return ws, manifest, json.loads((ws["out"] / "fences.json").read_text())["fences"]


def test_export_of_thin_fences_names_the_collision_node_and_the_texture_node(tmp_path) -> None:
    _, manifest, fences = _export(tmp_path, {"type": "picket", "collision": True})
    tris = manifest["chunks"][0]["triangles"]
    assert fences and manifest["map_version"].startswith("0.16.0")
    for f in fences:
        assert f["kind"] == "picket" and 1 <= f["pieces"] <= 64 and f["collider"] == f"parcel_{f['parcel']}_props_collision"
        assert f["collider"] in tris and f"parcel_{f['parcel']}_props_tex_picket" in tris
        assert f["thickness_m"] == FENCE["thickness_m"] and f["colour"].startswith("#") and "grid" not in f
    assert not any(n.startswith("house_fence_") for n in tris)


def test_export_of_wall_fences_makes_house_fence_nodes_and_one_grid_each(tmp_path) -> None:
    ws, manifest, fences = _export(tmp_path, {"type": "brick_red"})
    tris = manifest["chunks"][0]["triangles"]
    assert fences and all(f["kind"] == "wall" and f["grid"] == f"fence_{i:05d}" and "pieces" not in f for i, f in enumerate(fences))
    for i in range(len(fences)):
        base = f"house_fence_{i:05d}"
        assert {f"{base}_visual", f"{base}_collision", f"{base}_visual_tex_brick_red"} <= set(tris)
    assert not any(n.endswith(("_props_collision", "_props_tex_brick_red")) for n in tris)  # out of the props
    grids = voxels.decode((ws["out"] / voxels.FILE_NAME).read_bytes())
    assert [g.id for g in grids if g.id.startswith("fence_")] == [f["grid"] for f in fences]  # sorted by id, one each
    assert manifest["files"]["house_voxels"] == voxels.FILE_NAME
