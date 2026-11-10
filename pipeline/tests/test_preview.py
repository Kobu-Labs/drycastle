from shapely.geometry import box

from suchohrad_pipeline.buildings import derive_building, load_building_rules
from suchohrad_pipeline.preview import densest_cluster, run_preview
from suchohrad_pipeline.props import run_props
from suchohrad_pipeline.rules import DEFAULT_RULES
from tests.synthetic import make_workspace


def test_densest_cluster_picks_the_crowded_spot() -> None:
    rules = load_building_rules()
    lone = derive_building("w0", "P-1", box(500, 500, 506, 506), {}, rules)
    crowd = [
        derive_building(f"w{i}", "P-1", box(10 * i, 0, 10 * i + 6, 6), {}, rules)
        for i in range(1, 5)
    ]
    cx, cy = densest_cluster([lone, *crowd])
    assert 0 < cx < 60 and -1 < cy < 10


def test_preview_writes_three_images(tmp_path) -> None:
    ws = make_workspace(tmp_path)
    run_props(ws["work"], DEFAULT_RULES)
    paths = run_preview(ws["work"], tmp_path / "preview", DEFAULT_RULES, exaggeration=2.0)
    assert [p.name for p in paths] == ["topdown.png", "iso.png", "iso_close.png"]
    assert all(p.stat().st_size > 5000 for p in paths)
