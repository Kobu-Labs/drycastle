"""The crop recipe in the Rust engine against its Python twin (crop.py): same seeds, same instances (``crops.json`` rows), the same
ground by area and colour. The engine runs the same draws in the same order on the same GEOS, so the instances are identical; the
ground patches are the same shapes, draped the engine's way (the area per layer and colour agrees, the triangles may be cut
differently where a patch touches a terrain cell border).

Skipped when the extension module is not built (``make detail-rust-build``)."""

import json
from collections import defaultdict

import numpy as np
import pytest
import yaml
from shapely.geometry import box

pytest.importorskip("drycastle_detail", reason="build the Rust engine: make detail-rust-build")

from drycastle_pipeline import detail as D
from drycastle_pipeline import detail_rust as R
from drycastle_pipeline.props import run_props
from tests.synthetic import make_workspace, parcel


@pytest.fixture(scope="module")
def ws(tmp_path_factory):
    tmp = tmp_path_factory.mktemp("crop-rust-ws")
    work = make_workspace(tmp)["work"]
    doc = json.loads((work / "parcels.json").read_text())
    doc["parcels"] = [p for p in doc["parcels"] if p["id"] != "P-0004"] + [
        parcel("P-0004", "crop_grain", box(40, 115, 120, 190)), parcel("P-0007", "crop_potato", box(120, 115, 200, 190)),
        parcel("P-0008", "crop_corn", box(40, 190, 120, 260)),
    ]
    (work / "parcels.json").write_text(json.dumps(doc))
    rules = tmp / "rules.yaml"
    rules.write_text(yaml.safe_dump({"spawns": {"count": 12}}))
    run_props(work, rules)
    return work, tmp


@pytest.fixture(scope="module")
def python_crops(ws):
    work, tmp = ws
    return D.build_village_detail(work, edits_path=tmp / "no-edits.json", recipe="crop")


def run_rust(ws, out, threads):
    work, tmp = ws
    out.mkdir(exist_ok=True)
    stats, _ = R.run_engine(work, edits_path=tmp / "no-edits.json", threads=threads, out_dir=out)
    return stats


def _by_layer(rows: np.ndarray) -> dict:
    t = rows[:, :9].reshape(-1, 3, 3)
    area = np.abs((t[:, 1, 0] - t[:, 0, 0]) * (t[:, 2, 1] - t[:, 0, 1]) - (t[:, 2, 0] - t[:, 0, 0]) * (t[:, 1, 1] - t[:, 0, 1])) / 2
    out: dict = defaultdict(float)
    for r, a in zip(rows, area, strict=True):
        out[(tuple(r[9:12]), int(r[18]))] += a
    return out


def test_instances_are_the_python_twins(ws, python_crops, tmp_path) -> None:
    run_rust(ws, tmp_path / "rs", 2)
    rs = json.loads((tmp_path / "rs" / "detail_crops.json").read_text())
    py = D.crops_document(python_crops)
    assert rs["kinds"] == py["kinds"] and rs["chunk_size_m"] == py["chunk_size_m"] and rs["axes"] == py["axes"]
    assert rs["chunks"] and rs["chunks"] == py["chunks"]  # every row of every chunk and kind, in order


def test_ground_patches_cover_the_same_ground(ws, python_crops, tmp_path) -> None:
    stats = run_rust(ws, tmp_path / "rs", 2)
    rows, tex = D.load_patches(tmp_path / "rs")
    n = sum(stats["patch_triangles_by_type"]["crop"].values())
    rs = rows[len(rows) - n:]  # the crop patches come last in the file
    assert tex == python_crops.textures and n > 0
    a, b = _by_layer(rs), _by_layer(python_crops.patches)
    assert set(a) == set(b)
    for k in a:
        assert a[k] == pytest.approx(b[k], rel=2e-3, abs=0.05), k
    # the layers of the Python build exist, with the area the Python build gave them
    py_layers = python_crops.stats["patch_triangles_by_type"]["crop"]
    assert set(stats["patch_triangles_by_type"]["crop"]) == set(py_layers) - {"total"}


def test_engine_with_crops_is_identical_for_any_thread_count(ws, tmp_path) -> None:
    run_rust(ws, tmp_path / "t1", 1)
    run_rust(ws, tmp_path / "t4", 4)
    for name in ("detail.json", "detail_crops.json", "detail_patches.npy", "detail_patches.json"):
        assert (tmp_path / "t1" / name).read_bytes() == (tmp_path / "t4" / name).read_bytes(), name
