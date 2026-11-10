import json
import shutil
from pathlib import Path

import numpy as np
import pytest
import trimesh
from shapely.geometry import box

from suchohrad_pipeline.buildings import derive_building, load_building_rules
from suchohrad_pipeline.export import run_export, to_gltf_axes
from suchohrad_pipeline.rules import DEFAULT_RULES

RULES = load_building_rules()


@pytest.fixture
def workspace(tmp_path: Path) -> dict[str, Path]:
    work, raw = tmp_path / "work", tmp_path / "raw"
    work.mkdir()
    raw.mkdir()
    b1 = derive_building("w1", "P-0001", box(10, 20, 22, 28), {"roof:shape": "gabled"}, RULES)
    b2 = derive_building("w2", "P-0002", box(300, 20, 306, 26), {}, RULES)
    parcel = {"kind": "building_plot", "anchor": [0, 0], "polygon": {"type": "Polygon"}}
    parcels = {
        "crs": "synthetic",
        "chunk_size_m": 250.0,
        "parcels": [
            {
                **parcel,
                "id": "P-0001",
                "cadastral_ref": "X.1",
                "chunk": [0, 0],
                "buildings": ["w1"],
            },
            {
                **parcel,
                "id": "P-0002",
                "cadastral_ref": "X.2",
                "chunk": [1, 0],
                "buildings": ["w2"],
            },
            {
                **parcel,
                "id": "P-0003",
                "cadastral_ref": "X.3",
                "chunk": [0, 0],
                "buildings": [],
                "kind": "garden",
            },
        ],
    }
    (work / "parcels.json").write_text(json.dumps(parcels))
    (work / "buildings.json").write_text(json.dumps({"buildings": [b1, b2]}))
    (raw / "manifest.json").write_text(
        json.dumps({"attribution": ["A", "B"], "files": [{"file": "osm.json", "sha256": "ab"}]})
    )
    overrides = tmp_path / "overrides.yaml"
    overrides.write_text("buildings: {}\n")
    return {"work": work, "raw": raw, "overrides": overrides, "out": tmp_path / "out"}


def export(ws, out=None):
    return run_export(ws["work"], out or ws["out"], ws["raw"], DEFAULT_RULES, ws["overrides"])


def test_axis_conversion() -> None:
    p = np.array([[[1.0, 2.0, 3.0]]])  # east 1, north 2, up 3
    assert to_gltf_axes(p)[0, 0].tolist() == [1.0, 3.0, -2.0]


def test_chunk_files_nodes_and_bounds(workspace) -> None:
    manifest = export(workspace)
    out = workspace["out"]
    assert [c["file"] for c in manifest["chunks"]] == [
        "chunks/chunk_0_0.glb",
        "chunks/chunk_1_0.glb",
    ]
    scene = trimesh.load(out / "chunks" / "chunk_0_0.glb", force="scene")
    assert sorted(scene.graph.nodes_geometry) == ["parcel_P-0001_collision", "parcel_P-0001_visual"]
    visual = scene.geometry[scene.graph["parcel_P-0001_visual"][1]]
    lo, hi = visual.bounds
    # footprint x 10..22 east, y 20..28 north -> glTF z = -north; roof makes y (up) > eave
    assert lo[0] == pytest.approx(10, abs=1e-3) and hi[0] == pytest.approx(22, abs=1e-3)
    assert lo[2] == pytest.approx(-28, abs=1e-3) and hi[2] == pytest.approx(-20, abs=1e-3)
    assert lo[1] == pytest.approx(0, abs=1e-3) and hi[1] > 3
    assert len(visual.visual.vertex_colors) == len(visual.vertices)


def test_parcels_and_manifest(workspace) -> None:
    export(workspace)
    out = workspace["out"]
    parcels = json.loads((out / "parcels.json").read_text())["parcels"]
    assert [p["id"] for p in parcels] == ["P-0001", "P-0002", "P-0003"]
    assert parcels[0]["buildings"] == ["w1"] and parcels[2]["buildings"] == []
    m = json.loads((out / "manifest.json").read_text())
    assert m["map_version"] == "0.1.0-slice"
    assert m["attribution"] == ["A", "B"] and m["inputs"]["raw"] == {"osm.json": "ab"}
    assert "not generated" in m["not_generated"]
    assert not any("time" in k or "date" in k for k in m)


def test_export_is_byte_deterministic(workspace, tmp_path) -> None:
    export(workspace)
    second = tmp_path / "out2"
    export(workspace, second)
    first = workspace["out"]
    files = sorted(p.relative_to(first) for p in first.rglob("*") if p.is_file())
    assert files and files == sorted(
        p.relative_to(second) for p in second.rglob("*") if p.is_file()
    )
    for rel in files:
        assert (first / rel).read_bytes() == (second / rel).read_bytes(), rel
    shutil.rmtree(second)


def test_village_area_manifest_and_lighter_props(tmp_path) -> None:
    from suchohrad_pipeline.props import run_props
    from tests.synthetic import make_workspace

    ws = make_workspace(tmp_path)
    args = (ws["work"], tmp_path / "v", ws["raw"], DEFAULT_RULES, ws["overrides"])

    def counts(area: str) -> dict:
        run_props(ws["work"], DEFAULT_RULES, area=area)
        return run_export(*args[:1], tmp_path / area, *args[2:], area=area)

    slice_m, village = counts("slice"), counts("village")
    assert village["map_version"] == "0.1.0-village"
    assert slice_m["map_version"] == "0.1.0-slice"
    assert any("props_collision" in c["triangles"] for c in slice_m["chunks"])
    assert not any("props_collision" in c["triangles"] for c in village["chunks"])
    # the village export is byte-deterministic too
    run_export(*args[:1], tmp_path / "again", *args[2:], area="village")
    files = [p for p in (tmp_path / "village").rglob("*") if p.is_file()]
    assert files
    for p in files:
        assert p.read_bytes() == (tmp_path / "again" / p.relative_to(tmp_path / "village")).read_bytes()
