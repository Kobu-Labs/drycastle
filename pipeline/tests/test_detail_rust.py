"""The Rust engine of the detail stage: same files, same look as the Python stage, deterministic for any thread count.

Skipped when the extension module is not built (``make detail-rust-build``)."""

import json

import numpy as np
import pytest
import yaml
from shapely.geometry import Point

pytest.importorskip("drycastle_detail", reason="build the Rust engine: make detail-rust-build")

from drycastle_pipeline import detail as D
from drycastle_pipeline import detail_rust as R
from drycastle_pipeline import meadow as M
from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.props import run_props
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
    return D.build_village_detail(work, edits_path=tmp / "no-edits.json")


def run_rust(ws, out, threads):
    work, tmp = ws
    out.mkdir(exist_ok=True)
    stats, _ = R.run_engine(work, area=None, edits_path=tmp / "no-edits.json", threads=threads, out_dir=out)
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
    D.write_detail(py_dir, py_result)
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
    _, tex = D.load_patches(py_dir)
    rows_rs, tex_rs = D.load_patches(out)
    assert tex == tex_rs and len(D.patches_by_chunk(rows_rs, tex_rs, 250.0)) > 0
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
    rows, _ = D.load_patches(tmp_path / "rs")
    tris = rows[:, :9].reshape(-1, 3, 3)
    cx, cy = tris[:, :, 0].mean(axis=1), tris[:, :, 1].mean(axis=1)
    assert not any(building.contains(Point(x, y)) for x, y in zip(cx, cy, strict=True))


def test_stage_runs_rust_only(monkeypatch) -> None:
    from drycastle_pipeline import detail_rust

    called = {}
    monkeypatch.setattr(detail_rust, "run", lambda **kw: called.update(kw) or 0)
    monkeypatch.setenv("DRYCASTLE_DETAIL_ENGINE", "python")  # the old switch is gone: it changes nothing
    assert D.run(area="slice") == 0
    assert called["area"] == "slice"
