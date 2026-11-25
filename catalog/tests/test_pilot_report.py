from asset_catalog import pilot_report as pr
from asset_catalog.stages.describe_pilot import PilotRecord


def _rec(**kw) -> PilotRecord:
    base = {
        "plain_name": "x", "kind": "prop", "category_path": ["a", "b"],
        "secondary_categories": [], "description": "", "usage": "", "aliases": [],
        "materials": [], "colours": [], "typical_settings": [], "placements": [],
        "functions": [], "styles": [], "condition": [], "art_style": "", "real_front": "front",
        "modular": False, "snaps_with": [], "interactive_parts": [], "distinguishing": "",
        "likely_queries": [], "expected_size_m": [1, 2], "doubts": [],
    }  # fmt: skip
    return PilotRecord(**(base | kw))


def test_agreement_metrics():
    a = _rec(kind="Prop", category_path=["A", "b", "c"], materials=["Wood", "metal"])
    b = _rec(kind="prop", category_path=["a", "b", "d"], materials=["wood", "glass"])
    m = pr.record_agreement(a, b)
    assert m["kind"] == 1.0 and m["category_path[0]"] == 1.0
    assert m["category_path exact"] == 0.0 and m["category_path prefix depth"] == 2.0
    assert abs(m["materials"] - 1 / 3) < 1e-9
    assert m["colours"] == 1.0  # both empty agree


def test_order_is_deterministic_and_varies():
    models = pr.CANDIDATES
    assert pr.assign_order("x/y/z", models, 1) == pr.assign_order("x/y/z", models, 1)
    orders = {tuple(pr.assign_order(f"a/{i}", models, 1)) for i in range(40)}
    assert len(orders) > 1 and all(sorted(o) == sorted(models) for o in orders)


def test_schema_is_closed():
    s = pr.judge_schema()
    assert s["additionalProperties"] is False and s["required"] == list(s["properties"])
    assert s["properties"]["A"]["additionalProperties"] is False
