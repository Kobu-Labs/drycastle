"""Ground types of the user: overrides and additions in edits.json (``ground_types``), the merge with rules.yaml.

A type is a named bundle of attributes (``attributes``, the catalog of ``ground_attributes.py``); ``osm`` and ``rule``
say where it takes land from."""

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
from drycastle_pipeline.ground_attributes import (
    ATTRIBUTES,
    GROUPS,
    accumulate,
    carries_layer,
    carries_terrain,
    default_tint,
    terrain_index,
)


def errs(types: dict) -> list[str]:
    return validate({"version": 1, "ground_types": types})


def attrs(**kw) -> dict:
    return {"attributes": kw}


def attrs_of(name: str, **kw) -> dict:
    return {name: attrs(**kw)}


# --- the catalog and the stack -----------------------------------------------------------------


def test_every_attribute_is_in_a_group_and_the_terrain_ones_are_the_appearance_and_water() -> None:
    groups = {g.key for g in GROUPS}
    assert all(a.group in groups for a in ATTRIBUTES.values())
    assert {k for k, a in ATTRIBUTES.items() if a.stacking == "terrain"} == {"colours", "recipe", "detail", "water"}
    assert {k for k, a in ATTRIBUTES.items() if a.stacking != "terrain"} == {"spawns", "event_spawn"}


def test_the_topmost_terrain_type_decides_and_the_other_attributes_add_up() -> None:
    forest = attrs(colours=["#2f5f38"], recipe="forest")
    meadow = attrs(colours=["#7aa860"], recipe="field", spawns=True)
    bigfoot = attrs(event_spawn=["bigfoot"])
    assert terrain_index([forest, bigfoot]) == 0  # an events-only type on top changes nothing
    assert terrain_index([forest, meadow, bigfoot]) == 1
    assert terrain_index([bigfoot]) is None and terrain_index([]) is None
    assert (carries_terrain(bigfoot), carries_layer(bigfoot), carries_terrain(meadow), carries_layer(meadow)) == (False, True, True, True)
    assert accumulate([forest, meadow, bigfoot]) == {"spawns": True, "event_spawn": ["bigfoot"]}  # meadow is shadowed, still counts
    assert accumulate([forest]) == {}
    assert default_tint({"event_spawn"}) == ATTRIBUTES["event_spawn"].tint and default_tint({"colours"}) is None


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
    out = merge_ground_types(rules, {"meadow": attrs(spawns=None, event_spawn=["bigfoot"])})
    assert "spawns" not in out["meadow"]["attributes"] and out["meadow"]["attributes"]["event_spawn"] == ["bigfoot"]
    assert rules["meadow"]["attributes"]["spawns"] is True
    out = merge_ground_types(rules, {"field": attrs(colours=None, recipe=None, spawns=None)})
    assert "attributes" not in out["field"]  # a type may carry nothing


def test_new_types_come_last_and_deleted_types_go() -> None:
    rules = rules_ground_types()
    out = merge_ground_types(rules, {"my_dunes": attrs(colours=["#d8c38a"]), "orchard": {"deleted": True}})
    assert list(out)[-1] == "my_dunes" and "orchard" not in out
    assert [n for n in out if n != "my_dunes"] == [n for n in rules if n != "orchard"]
    edited = {"my_dunes": {}, "orchard": {"deleted": True}, "meadow": attrs(spawns=None)}
    assert [ground_type_source(n, rules, edited) for n in ("field", "meadow", "my_dunes", "orchard")] == [
        "rules", "overridden", "custom", "deleted",
    ]


def test_ground_types_reads_the_edits_file(tmp_path) -> None:
    path = tmp_path / "edits.json"
    path.write_text(dump_text(Edits(ground_types={"meadow": attrs(spawns=None), "dunes": attrs(colours=["#d8c38a"])})))
    assert ground_types()["meadow"]["attributes"]["spawns"] is True  # without the edits path: the rules
    effective = ground_types(edits_path=path)
    assert "spawns" not in effective["meadow"]["attributes"] and effective["dunes"]["attributes"]["colours"] == ["#d8c38a"]
    assert ground_types(edits_path=tmp_path / "missing.json") == ground_types()


def test_export_row_summarises_the_effective_type() -> None:
    rules = rules_ground_types()
    row = ground_type_row("forest", rules["forest"], rules["forest"])
    assert row["colour"] == "#2f5f38" and row["recipe"] == "forest" and row["source"] == "osm"
    assert row["attributes"] == ["colours", "recipe"]
    assert row["rules"] == rules["forest"] and "custom" not in row
    assert ground_type_row("dunes", None, attrs(colours=["#D8C38A"]))["custom"] is True


# --- validation -------------------------------------------------------------------------------


def test_a_full_override_and_a_custom_type_are_valid_and_round_trip(tmp_path) -> None:
    doc = {
        "version": 1,
        "parcels": {},
        "groups": {},
        "ground_types": {
            "meadow": attrs(spawns=True, detail={"furrow": {"probability": 0.0}}, event_spawn=["bigfoot"]),
            "dunes": {
                **attrs(colours=["#d8c38a", "#cfb97e"], recipe="field", detail={"tone_per_ha": 8}),
                "osm": {"natural": ["sand"], "landuse": []},
            },
            "bigfoot_zone": attrs(event_spawn=["bigfoot"]),
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
        (attrs_of("x", spawns="no"), "spawns: must be true"),
        (attrs_of("x", event_spawn=[]), "non-empty list of events"),
        (attrs_of("x", event_spawn=["yeti"]), "non-empty list of events"),
        (attrs_of("x", event_spawn="bigfoot"), "non-empty list of events"),
        (attrs_of("x", event_spawn=["bigfoot", "bigfoot"]), "lists an event twice"),
        (attrs_of("x", spawns=None), "null removes an attribute of the type in rules.yaml"),  # a new type has nothing to remove
        (attrs_of("meadow", event_spawn=None), "meadow has no event_spawn there"),
        ({"x": {"osm": {"landcover": ["a"]}}}, "osm.landcover: unknown key"),
        ({"x": {"osm": {"landuse": "meadow"}}}, "list of OSM values"),
        ({"x": {"osm": {"landuse": ["scrub"]}, **attrs(event_spawn=["bigfoot"])}}, "needs appearance or water attributes"),
        (attrs_of("forest", colours=None, recipe=None), "needs appearance or water attributes"),  # all terrain taken from an OSM type
        ({"field": {"deleted": True}}, "cannot be deleted, it is the default type"),
        ({"dike": {"deleted": True}}, "cannot be deleted, it has a rule"),
        ({"x": {"deleted": True}}, "not a type of rules.yaml"),
        ({"orchard": {"deleted": True, **attrs(spawns=True)}}, "no other keys"),
        ({"orchard": {"deleted": False}}, "must be true"),
        ("nope", "ground_types: must be an object"),
    ],
)
def test_bad_ground_types_are_refused(types, message) -> None:
    assert any(message in e for e in errs(types)), errs(types)


def test_an_attribute_only_type_is_valid_and_so_is_one_on_top_of_osm_land_when_terrain_stays() -> None:
    assert errs({"bigfoot_zone": attrs(event_spawn=["bigfoot"])}) == []
    assert errs({"forest": attrs(event_spawn=["bigfoot"])}) == []  # a rules type with events added: terrain stays
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
    """GroundTypeEdits.cs repeats the catalog (the editor cannot import Python): the attributes, their groups and stacking, the events."""
    import re

    from drycastle_pipeline.config import REPO_ROOT
    from drycastle_pipeline.ground_attributes import EVENTS, RECIPES

    src = (REPO_ROOT / "godot" / "viewer" / "GroundTypeEdits.cs").read_text()
    rows = re.findall(r'new\("(\w+)", "(\w+)", (Terrain|Any|Union), ', src)
    assert {k: (g, s.lower()) for k, g, s in rows} == {k: (a.group, a.stacking) for k, a in ATTRIBUTES.items()}
    assert re.findall(r'new\("(\w+)", "[A-Z]\w+", "', src) == [g.key for g in GROUPS]
    assert re.search(r"Events = \{ ([^}]*) \}", src).group(1).replace('"', "").split(", ") == list(EVENTS)
    assert re.search(r"Recipes = \{ ([^}]*) \}", src).group(1).replace('"', "").split(", ") == list(RECIPES)
