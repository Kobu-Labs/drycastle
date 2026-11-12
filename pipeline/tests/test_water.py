"""water.json: one body per water parcel, surface from the shoreline terrain, bed from the lowest terrain inside."""

from shapely.geometry import box

from drycastle_pipeline.water import WATER_FORMAT_VERSION, build_water, water_heights
from tests.synthetic import make_terrain, parcel, plane


def test_surface_is_shoreline_median_and_bed_is_lowest_inside() -> None:
    terrain = make_terrain()
    lake = box(60, 60, 100, 90)
    surface, bed = water_heights(lake, terrain)
    centre_h = plane(80, 75)[0]
    corner_h = plane(60, 60)[0]
    assert abs(surface - centre_h) < 0.3  # the shore averages out to about the centre of a plane
    assert abs(bed - corner_h) < 0.2      # lowest terrain inside is the low corner
    assert bed < surface


def test_build_water_keeps_only_water_parcels_sorted_by_id() -> None:
    terrain = make_terrain()
    parcels = [
        parcel("P-0009", "water", box(120, 120, 150, 140)),
        parcel("P-0002", "garden", box(20, 20, 40, 40)),
        parcel("P-0004", "water", box(60, 60, 100, 90)),
    ]
    doc, skipped = build_water(parcels, terrain)
    assert doc["version"] == WATER_FORMAT_VERSION == 1
    assert [b["id"] for b in doc["bodies"]] == ["P-0004", "P-0009"]
    assert skipped == []
    body = doc["bodies"][0]
    assert body["polygon"]["type"] == "Polygon"
    assert body["bed_y"] < body["surface_y"]


def test_water_parcel_outside_terrain_coverage_is_skipped() -> None:
    terrain = make_terrain()  # coverage is box(10, 10, 210, 200)
    doc, skipped = build_water([parcel("P-0007", "water", box(300, 300, 320, 320))], terrain)
    assert doc["bodies"] == []
    assert skipped == ["P-0007"]


def test_export_writes_water_json_and_manifest_entry(tmp_path) -> None:
    import json

    from drycastle_pipeline.export import run_export
    from drycastle_pipeline.rules import DEFAULT_RULES
    from tests.synthetic import make_workspace

    ws = make_workspace(tmp_path)
    doc = json.loads((ws["work"] / "parcels.json").read_text())
    doc["parcels"].append(parcel("P-0099", "water", box(60, 60, 100, 90)))
    (ws["work"] / "parcels.json").write_text(json.dumps(doc))
    out = tmp_path / "out"
    m = run_export(ws["work"], out, ws["raw"], DEFAULT_RULES, ws["overrides"])
    assert m["files"]["water"] == "water.json"
    assert "water" in m["generated"]
    water = json.loads((out / "water.json").read_text())
    assert [b["id"] for b in water["bodies"]] == ["P-0099"]
    assert water["bodies"][0]["bed_y"] < water["bodies"][0]["surface_y"]
