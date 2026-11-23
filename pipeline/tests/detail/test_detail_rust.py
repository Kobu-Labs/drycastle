"""The Rust engine of the detail stage: same files, same look as the Python stage, deterministic for any thread count.

Skipped when the extension module is not built (``make detail-rust-build``)."""

import json
import shutil

import numpy as np
import pytest
import yaml
from shapely.geometry import Point, box, mapping

pytest.importorskip("drycastle_detail", reason="build the Rust engine: make detail-rust-build")

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.detail import detail as D
from drycastle_pipeline.detail import meadow as M
from drycastle_pipeline.detail import rust as R
from drycastle_pipeline.detail.build import build_village_detail
from drycastle_pipeline.detail.documents import write_detail
from drycastle_pipeline.detail.model import PATCH_TEXTURE, DetailResult, Patch
from drycastle_pipeline.detail.patches import (
    _draped_rows,
    load_patches,
    patch_meshes,
    patch_rows,
    patches_by_chunk,
)
from drycastle_pipeline.props.props import run_props
from tests.synthetic import make_workspace

FILES = ("detail.json", "detail_patches.npy", "detail_patches.json", "detail_meadow.bin")


@pytest.fixture(scope="module")
def ws(tmp_path_factory):
    tmp = tmp_path_factory.mktemp("ws")
    work = make_workspace(tmp)["work"]
    rules = tmp / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"count": 12}}))
    run_props(work, rules)
    return work, tmp


@pytest.fixture(scope="module")
def py_result(ws):
    work, tmp = ws
    return build_village_detail(work, edits_path=tmp / "no-edits.json")


def run_rust(ws, out, threads):
    work, tmp = ws
    out.mkdir(exist_ok=True)
    stats, _ = R.run_engine(work, edits_path=tmp / "no-edits.json", threads=threads, out_dir=out)
    return stats


def test_engine_is_identical_for_any_thread_count(ws, tmp_path) -> None:
    a, b = tmp_path / "t1", tmp_path / "t4"
    run_rust(ws, a, 1)
    run_rust(ws, b, 4)
    for name in FILES:
        assert (a / name).read_bytes() == (b / name).read_bytes(), name


def test_files_have_the_formats_of_the_python_stage(ws, py_result, tmp_path) -> None:
    py_dir = tmp_path / "py"
    py_dir.mkdir()
    write_detail(py_dir, py_result)
    out = tmp_path / "rs"
    run_rust(ws, out, 2)
    assert (py_dir / "detail_patches.json").read_text() == (out / "detail_patches.json").read_text()
    a, b = np.load(py_dir / "detail_patches.npy"), np.load(out / "detail_patches.npy")
    assert a.dtype == b.dtype == np.float64 and a.shape[1] == b.shape[1] == 19
    da, db = json.loads((py_dir / "detail.json").read_text()), json.loads((out / "detail.json").read_text())
    assert list(da) == list(db) and da["kinds"] == db["kinds"] and da["axes"] == db["axes"]
    assert da["chunk_size_m"] == db["chunk_size_m"]
    rows = [r for kinds in db["chunks"].values() for rs in kinds.values() for r in rs]
    assert rows and all(len(r) in (5, 6) for r in rows)
    _, tex = load_patches(py_dir)
    rows_rs, tex_rs = load_patches(out)
    assert tex == tex_rs and len(patches_by_chunk(rows_rs, tex_rs, 250.0)) > 0
    ma, mb = M.decode((py_dir / "detail_meadow.bin").read_bytes()), M.decode((out / "detail_meadow.bin").read_bytes())
    assert [x.name for x in ma.layers] == [x.name for x in mb.layers] and ma.cell_m == mb.cell_m
    assert all(x.mixes == y.mixes for x, y in zip(ma.layers, mb.layers, strict=True))


def test_counts_match_the_python_stage(ws, py_result, tmp_path) -> None:
    stats = run_rust(ws, tmp_path / "rs", 2)
    py = py_result.stats
    # a few units, so loose bounds: the same recipes with other random draws (a furrow strip alone is 17 % of the triangles here)
    assert stats["patch_triangles"] == pytest.approx(py["patch_triangles"], rel=0.4)
    for recipe in ("garden", "village"):
        assert sum(stats["patch_triangles_by_type"][recipe].values()) == pytest.approx(py["patch_triangles_by_type"][recipe]["total"], rel=0.25)
    assert sum(stats["clutter"].values()) == pytest.approx(sum(py["clutter"].values()), rel=0.25)
    # the raster follows the free ground (the same in both) except under a furrow strip, which only one of the two drew here
    assert stats["meadow_cells"] == pytest.approx(py["meadow_cells"], rel=0.4)
    assert not stats["failed"]


def test_moss_layer_matches_the_python_stage(ws, py_result, tmp_path) -> None:
    # the same recipe with other random draws: the moss triangles agree in order of magnitude
    py_dir, out = tmp_path / "py", tmp_path / "rs"
    py_dir.mkdir()
    write_detail(py_dir, py_result)
    run_rust(ws, out, 2)
    counts = []
    for d in (py_dir, out):
        rows, tex = load_patches(d)
        assert "moss" in tex
        counts.append(int((rows[:, 18] == tex.index("moss")).sum()))
    assert counts[0] > 50 and counts[1] == pytest.approx(counts[0], rel=0.6), counts


def test_nothing_on_buildings_and_clutter_stands_on_the_ground(ws, tmp_path) -> None:
    work, _ = ws
    run_rust(ws, tmp_path / "rs", 2)
    doc = json.loads((tmp_path / "rs" / "detail.json").read_text())
    building = footprint_polygon(json.loads((work / "buildings.json").read_text())["buildings"][0]["footprint"])
    n = 0
    for kinds in doc["chunks"].values():
        for rows in kinds.values():
            for x, y, z, rot, scale, *_ in rows:
                n += 1
                assert not building.contains(Point(x, y))
                assert 99.0 < z < 125.0 and scale > 0
    assert n > 100
    rows, _ = load_patches(tmp_path / "rs")
    tris = rows[:, :9].reshape(-1, 3, 3)
    cx, cy = tris[:, :, 0].mean(axis=1), tris[:, :, 1].mean(axis=1)
    assert not any(building.contains(Point(x, y)) for x, y in zip(cx, cy, strict=True))


def test_stage_runs_rust_only(monkeypatch) -> None:
    from drycastle_pipeline.detail import rust as detail_rust

    called = {}
    monkeypatch.setattr(detail_rust, "run", lambda **kw: called.update(kw, ran=True) or 0)
    monkeypatch.setenv("DRYCASTLE_DETAIL_ENGINE", "python")  # the old switch is gone: it changes nothing
    assert D.run() == 0
    assert called["ran"]


@pytest.fixture(scope="module")
def wet_ws(ws, tmp_path_factory):
    """The workspace with a pond in the forest and one in a meadow (P-0006 turned meadow): their surroundings are swamp ground."""
    work, tmp = ws
    wet = tmp_path_factory.mktemp("wet") / "work"
    shutil.copytree(work, wet)
    doc = json.loads((wet / "parcels.json").read_text())
    for p in doc["parcels"]:
        if p["id"] == "P-0006":
            p["kind"] = "meadow"
    doc["ground"] = [{"id": f"G-000{i}", "kind": "water", "area": g.area, "polygon": mapping(g)}
                     for i, g in enumerate((box(150, 60, 160, 75), box(28, 50, 34, 60)), 1)]
    (wet / "parcels.json").write_text(json.dumps(doc))
    return wet, tmp


def test_swamp_ground_matches_the_python_stage(wet_ws, tmp_path) -> None:
    work, tmp = wet_ws
    edits = tmp / "no-edits.json"
    py = build_village_detail(work, edits_path=edits)
    stats, _ = R.run_engine(work, edits_path=edits, threads=2, out_dir=tmp_path)
    assert not stats["failed"]
    for recipe in ("forest", "field"):  # the forest and the meadow (a field recipe) both take the wet floor
        want, got = py.stats["patch_triangles_by_type"][recipe], stats["patch_triangles_by_type"][recipe]
        assert "swamp" not in want and "swamp" not in got, (recipe, want, got)  # the base is the terrain's colour
    f, g = py.stats["patch_triangles_by_type"]["forest"], stats["patch_triangles_by_type"]["forest"]
    for layer in ("mud", "puddle"):
        assert f[layer] > 20 and g[layer] == pytest.approx(f[layer], rel=0.8), (layer, f, g)
    assert g["litter"] > 0
    # the meadow raster: the cells within reach of water have the swamp mix, in the same places
    py_dir = tmp_path / "py"
    py_dir.mkdir()
    write_detail(py_dir, py)
    cells = []
    for d in (py_dir, tmp_path):
        m = M.decode((d / "detail_meadow.bin").read_bytes())
        z = np.concatenate([a[-1].ravel() for a in m.chunks.values()])
        cells.append(int(((z >> 4) == 9).sum()))
    assert cells[0] > 100 and cells[1] == pytest.approx(cells[0], rel=0.1), cells


def test_a_tiled_forest_gets_the_wet_floor(wet_ws, tmp_path) -> None:
    # a forest longer than a tile (TILE_ABOVE_M) is cut into tiles, which each draw the wet floor from the position alone
    work, tmp = wet_ws
    doc = json.loads((work / "parcels.json").read_text())
    for p in doc["parcels"]:
        if p["id"] == "P-0005":
            p["polygon"] = mapping(box(100, 20, 260, 190))
    (work / "parcels.json").write_text(json.dumps(doc))
    stats, _ = R.run_engine(work, edits_path=tmp / "no-edits.json", threads=2, only=["P-0005"], out_dir=tmp_path)
    assert stats["items"] > stats["parts"] and not stats["failed"]  # tiled
    forest = stats["patch_triangles_by_type"]["forest"]
    assert forest["mud"] > 20


def _tri_area(rows: np.ndarray) -> np.ndarray:
    return np.abs((rows[:, 3] - rows[:, 0]) * (rows[:, 7] - rows[:, 1]) - (rows[:, 6] - rows[:, 0]) * (rows[:, 4] - rows[:, 1])) / 2


def test_drape_patches_matches_the_python_drape(ws) -> None:
    """The engine's drape for patches built in Python (the crop fields): the same area per patch, on the terrain, with the colours,
    texture coordinates and row order of the Python build; the triangles themselves are cut differently."""
    from drycastle_pipeline.core.textures import texture
    from drycastle_pipeline.terrain.terrain import load_terrain

    work, _ = ws
    terrain, _ = load_terrain(work)
    c = terrain.coverage.centroid
    hole = Point(c.x + 6, c.y + 4).buffer(5)
    patches = [
        Patch("crop_ground", Point(c.x, c.y).buffer(60).difference(hole), colour=[90, 70, 40]),
        Patch("dirt", box(c.x - 80, c.y - 9, c.x - 30, c.y + 31), colour=[200, 180, 160], texture="dirt", angle=0.4, origin=(c.x, c.y)),
        Patch("gravel", hole, texture="gravel", lift=0.2),
        Patch("lawn", box(c.x + 1e6, c.y, c.x + 1e6 + 5, c.y + 5), colour=[1, 2, 3]),  # off the terrain: no rows
    ]
    textures = sorted(set(PATCH_TEXTURE.values()))
    lift = 0.03
    res = DetailResult("P-test", patches=patches)
    tally_py: dict = {}
    py = patch_rows(*patch_meshes(res, terrain, lift, tally_py), textures)
    by_type: dict = {}
    rs = _draped_rows([("crop", patches)], R.drape_patches, terrain, lift, textures, by_type)
    assert set(by_type["crop"]) == set(tally_py) == {"crop_ground", "dirt", "gravel"}
    assert rs.shape[1] == py.shape[1] == 19 and len(rs) == sum(by_type["crop"].values())
    # the same blocks in the same order: flat first, then the textures by id
    assert [int(t) for t in rs[np.r_[True, rs[1:, 18] != rs[:-1, 18]], 18]] == [int(t) for t in py[np.r_[True, py[1:, 18] != py[:-1, 18]], 18]]
    for tid, p in ((-1, patches[0]), (textures.index("dirt"), patches[1]), (textures.index("gravel"), patches[2])):
        a, b = rs[rs[:, 18] == tid], py[py[:, 18] == tid]
        covered = p.geom.intersection(terrain.coverage).area
        assert abs(_tri_area(a).sum() - _tri_area(b).sum()) < 0.01 * covered and abs(_tri_area(a).sum() - covered) < 0.01 * covered
        assert {tuple(r) for r in a[:, 9:12].tolist()} == {tuple(r) for r in b[:, 9:12].tolist()}
        xy = a[:, :9].reshape(-1, 3, 3)
        ground = terrain.surface(xy[:, :, 0].ravel(), xy[:, :, 1].ravel()).reshape(-1, 3)
        assert np.allclose(xy[:, :, 2], ground + (p.lift or lift), atol=1e-9)
        e1, e2 = xy[:, 1] - xy[:, 0], xy[:, 2] - xy[:, 0]
        assert (e1[:, 0] * e2[:, 1] - e1[:, 1] * e2[:, 0] > 0).all()  # counter-clockwise from above
        if tid >= 0:
            t = texture(p.texture)
            cs, sn = np.cos(-p.angle), np.sin(-p.angle)
            dx, dy = xy[:, :, 0] - p.origin[0], xy[:, :, 1] - p.origin[1]
            uv = np.stack([(dx * cs - dy * sn) / t.tile_w, (dx * sn + dy * cs) / t.tile_h], axis=2).reshape(-1, 6)
            assert np.allclose(a[:, 12:18], uv, atol=1e-9)
    one, _ = R.drape_patches(patches, terrain, lift, textures, threads=1)
    many, _ = R.drape_patches(patches, terrain, lift, textures, threads=4)
    assert one.tobytes() == many.tobytes()
