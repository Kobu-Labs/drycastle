"""Ground types of the user: overrides and additions in edits.json (``ground_types``), the merge with rules.yaml.

A type is a named bundle of terrain attributes (``attributes``, the catalog of ``ground/attributes.py``); ``osm`` and ``rule``
say where it takes land from. It carries nothing a game system reads (ADR 0022)."""

import json

import pytest

from drycastle_pipeline.edits.schema import (
    Edits,
    EditsError,
    dump_text,
    load_edits,
    parse,
    validate,
)
from drycastle_pipeline.ground.areas import (
    ground_type_row,
    ground_type_source,
    ground_types,
    merge_ground_types,
    rules_ground_types,
)
from drycastle_pipeline.ground.attributes import ATTRIBUTES, GROUPS, TERRAIN_KEYS, carries_terrain


def errs(types: dict) -> list[str]:
    return validate({"version": 1, "ground_types": types})


def attrs(**kw) -> dict:
    return {"attributes": kw}


def attrs_of(name: str, **kw) -> dict:
    return {name: attrs(**kw)}


# --- the catalog ------------------------------------------------------------------------------


def test_every_attribute_is_in_a_group_and_all_of_them_are_terrain() -> None:
    groups = {g.key for g in GROUPS}
    assert all(a.group in groups for a in ATTRIBUTES.values())
    assert set(ATTRIBUTES) == set(TERRAIN_KEYS) == {"colours", "recipe", "detail", "water"}  # nothing a game system reads


def test_a_type_carries_terrain_when_it_has_any_attribute() -> None:
    assert carries_terrain(attrs(colours=["#2f5f38"])) and carries_terrain(attrs(water=True))
    assert not carries_terrain({}) and not carries_terrain(None) and not carries_terrain(attrs())


# --- the merge rule ---------------------------------------------------------------------------


def test_override_replaces_keys_and_merges_dicts() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"forest": {"osm": {"natural": ["scrub"]}, **attrs(colours=["#112233"])}})
    assert out["forest"]["osm"] == {"landuse": ["forest"], "natural": ["scrub"]}  # the landuse list of the rules stays
    assert out["forest"]["attributes"]["colours"] == ["#112233"] and out["forest"]["attributes"]["recipe"] == "forest"
    assert rules["forest"]["osm"]["natural"] == ["wood"]  # the input is not changed
    out = merge_ground_types(rules, {"allotments": attrs(detail={"furrow": {"probability": 0.5}})})
    detail = out["allotments"]["attributes"]["detail"]
    assert detail["furrow"]["probability"] == 0.5
    assert detail["furrow"]["min_area_m2"] == 300  # deeper keys stay


def test_a_null_attribute_is_removed_and_a_new_one_is_added() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"meadow": attrs(recipe=None, water=True)})
    assert "recipe" not in out["meadow"]["attributes"] and out["meadow"]["attributes"]["water"] is True
    assert rules["meadow"]["attributes"]["recipe"] == "field"
    out = merge_ground_types(rules, {"field": attrs(colours=None, recipe=None)})
    assert "attributes" not in out["field"]  # a type may carry nothing


def test_new_types_come_last_and_deleted_types_go() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"my_dunes": attrs(colours=["#d8c38a"]), "orchard": {"deleted": True}})
    assert list(out)[-1] == "my_dunes" and "orchard" not in out
    assert [n for n in out if n != "my_dunes"] == [n for n in rules if n != "orchard"]
    edited = {"my_dunes": {}, "orchard": {"deleted": True}, "meadow": attrs(recipe=None)}
    assert [ground_type_source(n, rules, edited) for n in ("field", "meadow", "my_dunes", "orchard")] == [
        "rules", "overridden", "custom", "deleted",
    ]


def test_ground_types_reads_the_edits_file(tmp_path) -> None:
    path = tmp_path / "edits.json"
    path.write_text(dump_text(Edits(ground_types={"meadow": attrs(recipe=None), "dunes": attrs(colours=["#d8c38a"])})))
    assert ground_types()["meadow"]["attributes"]["recipe"] == "field"  # without the edits path: the rules
    effective = ground_types(edits_path=path)
    assert "recipe" not in effective["meadow"]["attributes"] and effective["dunes"]["attributes"]["colours"] == ["#d8c38a"]
    assert ground_types(edits_path=tmp_path / "missing.json") == ground_types()


def test_export_row_summarises_the_effective_type() -> None:
    rules = rules_ground_types()
    row = ground_type_row("forest", rules["forest"], rules["forest"])
    assert row["colour"] == "#3b7a3a" and row["recipe"] == "forest" and row["source"] == "osm"
    assert row["attributes"] == ["colours", "recipe"] and "spawns" not in row
    assert row["rules"] == rules["forest"] and "custom" not in row
    assert ground_type_row("dunes", None, attrs(colours=["#D8C38A"]))["custom"] is True


# --- validation -------------------------------------------------------------------------------


def test_a_full_override_and_a_custom_type_are_valid_and_round_trip(tmp_path) -> None:
    doc = {
        "version": 1,
        "parcels": {},
        "groups": {},
        "ground_types": {
            "meadow": attrs(water=True, detail={"furrow": {"probability": 0.0}}),
            "dunes": {
                **attrs(colours=["#d8c38a", "#cfb97e"], recipe="field", detail={"tone_per_ha": 8}),
                "osm": {"natural": ["sand"], "landuse": []},
            },
            "paint_e2b92c": attrs(colours=["#e2b92c"]),  # a colour only: how a paint colour becomes a type
            "orchard": {"deleted": True},
            "village": attrs(recipe=None),
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
        ({"x": {"colours": ["#ffffff"]}}, "x.colours: unknown key"),  # the old flat shape
        ({"x": {"attributes": {"colour": "#ffffff"}}}, "x.attributes.colour: unknown attribute"),
        ({"x": {"attributes": {"trees": {"per_1000m2": 1}}}}, "x.attributes.trees: unknown attribute"),
        ({"x": {"attributes": 3}}, "attributes: must be an object"),
        (attrs_of("x", colours=[]), "1 to 6"),
        (attrs_of("x", colours=["#ffffff"] * 7), "1 to 6"),
        (attrs_of("x", colours=["red"]), "#rrggbb"),
        (attrs_of("x", recipe="garden"), "recipe: must be one of"),
        (attrs_of("x", detail={"furrow": {}}), "needs a recipe"),
        (attrs_of("x", recipe="village", detail={"furrow": {}}), "not a setting of detail.village"),
        (attrs_of("x", recipe="field", detail=3), "detail: must be an object"),
        (attrs_of("x", water=1), "water: must be true"),
        (attrs_of("x", water=False), "water: must be true"),
        # what an area means in play is a zone: the game attributes are gone, not renamed
        (attrs_of("x", spawns=True), "x.attributes.spawns: unknown attribute"),
        (attrs_of("x", event_spawn=["bigfoot"]), "x.attributes.event_spawn: unknown attribute"),
        (attrs_of("x", wildlife=["deer"]), "x.attributes.wildlife: unknown attribute"),
        (attrs_of("forest", wildlife=["wolf"]), "forest.attributes.wildlife: unknown attribute"),
        (attrs_of("x", water=None), "null removes an attribute of the type in rules.yaml"),  # a new type has nothing to remove
        (attrs_of("meadow", water=None), "meadow has no water there"),
        ({"x": {"osm": {"landcover": ["a"]}}}, "osm.landcover: unknown key"),
        ({"x": {"osm": {"landuse": "meadow"}}}, "list of OSM values"),
        ({"x": {"osm": {"landuse": ["scrub"]}, "attributes": {}}}, "needs appearance or water attributes"),
        (attrs_of("forest", colours=None, recipe=None), "needs appearance or water attributes"),  # all terrain taken from an OSM type
        ({"field": {"deleted": True}}, "cannot be deleted, it is the default type"),
        ({"dike": {"deleted": True}}, "cannot be deleted, it has a rule"),
        ({"x": {"deleted": True}}, "not a type of rules.yaml"),
        ({"orchard": {"deleted": True, **attrs(water=True)}}, "no other keys"),
        ({"orchard": {"deleted": False}}, "must be true"),
        ("nope", "ground_types: must be an object"),
    ],
)
def test_bad_ground_types_are_refused(types, message) -> None:
    assert any(message in e for e in errs(types)), errs(types)


def test_a_colour_only_type_is_valid_and_a_terrain_override_of_a_rules_type_too() -> None:
    assert errs({"paint_e2b92c": attrs(colours=["#e2b92c"])}) == []  # no recipe: no detail, only the colour
    assert errs({"forest": attrs(colours=["#112233"])}) == []
    assert errs({"forest": attrs(colours=None)}) == []  # recipe is still terrain


def test_detail_keys_are_checked_against_the_rules_type_recipe_when_overriding() -> None:
    assert errs({"meadow": attrs(detail={"furrow": {"probability": 0.2}})}) == []  # recipe field from rules.yaml
    assert any("not a setting" in e for e in errs({"meadow": attrs(detail={"nonsense": 1})}))
    assert any("needs a recipe" in e for e in errs({"meadow": attrs(recipe=None, detail={"furrow": {}})}))


def test_invalid_file_raises_edits_error(tmp_path) -> None:
    path = tmp_path / "edits.json"
    path.write_text(json.dumps({"version": 1, "ground_types": {"Bad": {}}}))
    with pytest.raises(EditsError):
        ground_types(edits_path=path)


def test_the_editor_mirrors_the_attribute_catalog() -> None:
    """GroundTypeEdits.cs repeats the catalog (the editor cannot import Python): the attributes and the recipes."""
    import re

    from drycastle_pipeline.core.config import REPO_ROOT
    from drycastle_pipeline.ground.attributes import RECIPES

    src = (REPO_ROOT / "godot" / "viewer" / "Edits" / "GroundTypeEdits.cs").read_text()
    assert all(f'"{k}"' in src for k in ATTRIBUTES)
    assert not any(f'"{k}"' in src for k in ("spawns", "event_spawn", "wildlife"))  # ground types are terrain only
    assert re.search(r"Recipes = \{ ([^}]*) \}", src).group(1).replace('"', "").split(", ") == list(RECIPES)
