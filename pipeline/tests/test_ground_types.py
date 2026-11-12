"""Ground types of the user: overrides and additions in edits.json (``ground_types``), the merge with rules.yaml."""

import json

import pytest

from drycastle_pipeline.edits import Edits, EditsError, dump_text, load_edits, parse, validate
from drycastle_pipeline.ground import (
    ground_type_row,
    ground_type_source,
    ground_types,
    merge_ground_types,
    rules_ground_types,
)


def errs(types: dict) -> list[str]:
    return validate({"version": 1, "ground_types": types})


# --- the merge rule ---------------------------------------------------------------------------


def test_override_replaces_keys_and_merges_dicts() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"forest": {"osm": {"natural": ["scrub"]}, "colours": ["#112233"]}})
    assert out["forest"]["osm"] == {"landuse": ["forest"], "natural": ["scrub"]}  # the landuse list of the rules stays
    assert out["forest"]["colours"] == ["#112233"] and out["forest"]["recipe"] == "forest"
    assert rules["forest"]["osm"]["natural"] == ["wood"]  # the input is not changed
    out = merge_ground_types(rules, {"allotments": {"detail": {"furrow": {"probability": 0.5}}}})
    assert out["allotments"]["detail"]["furrow"]["probability"] == 0.5
    assert out["allotments"]["detail"]["furrow"]["min_area_m2"] == 300  # deeper keys stay


def test_new_types_come_last_and_deleted_types_go() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"my_dunes": {"colours": ["#d8c38a"]}, "orchard": {"deleted": True}})
    assert list(out)[-1] == "my_dunes" and "orchard" not in out
    assert [n for n in out if n != "my_dunes"] == [n for n in rules if n != "orchard"]
    edited = {"my_dunes": {}, "orchard": {"deleted": True}, "meadow": {"spawns": False}}
    assert [ground_type_source(n, rules, edited) for n in ("field", "meadow", "my_dunes", "orchard")] == [
        "rules", "overridden", "custom", "deleted",
    ]


def test_ground_types_reads_the_edits_file(tmp_path) -> None:
    path = tmp_path / "edits.json"
    path.write_text(dump_text(Edits(ground_types={"meadow": {"spawns": False}, "dunes": {"colours": ["#d8c38a"]}})))
    assert ground_types()["meadow"]["spawns"] is True  # without the edits path: the rules
    effective = ground_types(edits_path=path)
    assert effective["meadow"]["spawns"] is False and effective["dunes"]["colours"] == ["#d8c38a"]
    assert ground_types(edits_path=tmp_path / "missing.json") == ground_types()


def test_export_row_summarises_the_effective_type() -> None:
    rules = rules_ground_types()
    row = ground_type_row("forest", rules["forest"], rules["forest"])
    assert row["colour"] == "#2f5f38" and row["recipe"] == "forest" and row["source"] == "osm"
    assert row["rules"] == rules["forest"] and "custom" not in row
    assert ground_type_row("dunes", None, {"colours": ["#D8C38A"]})["custom"] is True


# --- validation -------------------------------------------------------------------------------


def test_a_full_override_and_a_custom_type_are_valid_and_round_trip(tmp_path) -> None:
    doc = {
        "version": 1,
        "parcels": {},
        "groups": {},
        "ground_types": {
            "meadow": {"spawns": True, "detail": {"furrow": {"probability": 0.0}}},
            "dunes": {
                "colours": ["#d8c38a", "#cfb97e"], "recipe": "field", "water": False,
                "osm": {"natural": ["sand"], "landuse": []}, "detail": {"tone_per_ha": 8},
            },
            "orchard": {"deleted": True},
            "village": {"recipe": None},
        },
    }
    assert validate(doc) == []
    path = tmp_path / "edits.json"
    path.write_text(dump_text(parse(doc)))
    assert json.loads(path.read_text())["ground_types"] == doc["ground_types"]
    assert dump_text(load_edits(path)) == path.read_text()
    assert '"ground_types"' not in dump_text(Edits())  # only written when there are some


@pytest.mark.parametrize(
    ("types", "message"),
    [
        ({"Meadow": {}}, "type names"),
        ({"1st": {}}, "type names"),
        ({"a" * 25: {}}, "at most 24"),
        ({"x": {"rule": "dike"}}, "rule types stay in rules.yaml"),
        ({"x": {"colour": "#ffffff"}}, "x.colour: unknown key"),
        ({"x": {"colours": []}}, "1 to 6"),
        ({"x": {"colours": ["#ffffff"] * 7}}, "1 to 6"),
        ({"x": {"colours": ["red"]}}, "#rrggbb"),
        ({"x": {"recipe": "garden"}}, "recipe: must be one of"),
        ({"x": {"detail": {"furrow": {}}}}, "needs a recipe"),
        ({"x": {"recipe": "village", "detail": {"furrow": {}}}}, "not a setting of detail.village"),
        ({"x": {"recipe": "field", "detail": 3}}, "detail: must be an object"),
        ({"x": {"trees": {"per_1000m2": 1}}}, "x.trees: unknown key"),
        ({"x": {"water": 1}}, "water: must be true or false"),
        ({"x": {"spawns": "no"}}, "spawns: must be true or false"),
        ({"x": {"osm": {"landcover": ["a"]}}}, "osm.landcover: unknown key"),
        ({"x": {"osm": {"landuse": "meadow"}}}, "list of OSM values"),
        ({"field": {"deleted": True}}, "cannot be deleted, it is the default type"),
        ({"dike": {"deleted": True}}, "cannot be deleted, it has a rule"),
        ({"x": {"deleted": True}}, "not a type of rules.yaml"),
        ({"orchard": {"deleted": True, "spawns": False}}, "no other keys"),
        ({"orchard": {"deleted": False}}, "must be true"),
        ("nope", "ground_types: must be an object"),
    ],
)
def test_bad_ground_types_are_refused(types, message) -> None:
    assert any(message in e for e in errs(types)), errs(types)


def test_detail_keys_are_checked_against_the_rules_type_recipe_when_overriding() -> None:
    assert errs({"meadow": {"detail": {"furrow": {"probability": 0.2}}}}) == []  # recipe field from rules.yaml
    assert any("not a setting" in e for e in errs({"meadow": {"detail": {"nonsense": 1}}}))
    assert any("needs a recipe" in e for e in errs({"meadow": {"recipe": None, "detail": {"furrow": {}}}}))


def test_invalid_file_raises_edits_error(tmp_path) -> None:
    path = tmp_path / "edits.json"
    path.write_text(json.dumps({"version": 1, "ground_types": {"Bad": {}}}))
    with pytest.raises(EditsError):
        ground_types(edits_path=path)
