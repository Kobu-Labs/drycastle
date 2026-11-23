"""Ground areas: which parcels stay parcels, and how the other land is cut by type."""

from shapely.geometry import LineString, box
from shapely.ops import unary_union

from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.detail.recipes import RECIPES
from drycastle_pipeline.ground.areas import (
    build_ground,
    dike_ground,
    drawn_ground,
    ground_types,
    ground_units,
    kept_parcels,
    type_colours,
    water_kinds,
)
from drycastle_pipeline.ground.patches import plan_patches


def _terrain(**attrs):
    return {"attributes": {"colours": ["#808080"], **attrs}}


CFG = {
    "min_area_m2": 1.0,
    "default": "field",
    "types": {
        "dike": {"rule": "dike", **_terrain()},
        "water": {"osm": {"natural": ["water"]}, **_terrain(water=True)},
        "forest": {"osm": {"landuse": ["forest"], "natural": ["wood"]}, **_terrain()},
        "village": {"osm": {"landuse": ["residential"]}, **_terrain()},
        "field": _terrain(),
    },
}


def test_a_parcel_is_kept_with_a_building_or_in_a_group_that_has_one() -> None:
    groups = {"G-1": ["P-1", "P-2"], "G-2": ["P-3", "P-4"]}  # G-2 has no building
    assert kept_parcels({"P-1", "P-9"}, groups) == {"P-1", "P-2", "P-9"}


def test_ground_is_cut_by_type_in_order_and_the_rest_is_the_default() -> None:
    free = box(0, 0, 100, 100)
    landuse = [
        ("landuse", "forest", box(0, 0, 50, 100)),
        ("natural", "water", box(40, 0, 60, 100)),  # water is above forest in the order: it wins the overlap
        ("landuse", "residential", box(90, 0, 200, 100)),  # only the part inside the free land counts
        ("landuse", "cemetery", box(60, 0, 70, 100)),  # not a source of any type
    ]
    out = build_ground(free, landuse, box(0, 0, 0, 0), CFG)
    assert [(k, p.area) for k, p in out] == [("water", 2000), ("forest", 4000), ("village", 1000), ("field", 3000)]
    assert unary_union([p for _, p in out]).equals(free)  # no holes, no overlaps
    assert sum(p.area for _, p in out) == free.area


def test_ground_pieces_under_the_minimum_are_dropped_and_parts_are_separate_areas() -> None:
    free = unary_union([box(0, 0, 10, 10), box(20, 0, 30, 10), box(50, 0, 50.5, 1)])
    out = build_ground(free, [], box(0, 0, 0, 0), CFG)
    assert [(k, p.bounds) for k, p in out] == [("field", (0, 0, 10, 10)), ("field", (20, 0, 30, 10))]


def test_dike_ground_is_the_listed_parcels_and_the_clipped_part_of_the_clip_parcels() -> None:
    geoms = {"P-1": box(0, 0, 10, 10), "P-2": box(10, 0, 110, 100)}
    ways = {"w1": LineString([(10, 50), (110, 50)])}
    cfg = {"parcels": ["P-1", "P-7"], "clips": {"P-2": {"osm_id": "w1", "within_m": 5}, "P-8": {"osm_id": "w1", "within_m": 5}}}
    dike = dike_ground(geoms, ways, cfg)
    assert dike.area == 100 + 100 * 10
    out = build_ground(unary_union(list(geoms.values())), [], dike, CFG)
    assert [(k, round(p.area)) for k, p in out] == [("dike", 1000), ("dike", 100), ("field", 4500), ("field", 4500)] or sorted(
        (k, round(p.area)) for k, p in out
    ) == [("dike", 100), ("dike", 1000), ("field", 4500), ("field", 4500)]


def test_ground_units_are_the_parcels_and_the_ground_areas() -> None:
    doc = {"parcels": [{"id": "P-0001"}], "ground": [{"id": "A-0001"}]}
    assert [u["id"] for u in ground_units(doc)] == ["P-0001", "A-0001"]
    assert ground_units({"parcels": []}) == []


def test_shipped_ground_types_have_a_colour_and_the_default_is_a_type() -> None:
    cfg = load_section("ground")
    types = ground_types()
    assert cfg["default"] in types and list(types) == list(cfg["types"])
    assert set(type_colours(types)) == set(types)
    for spec in types.values():  # a type has a recipe or is water
        attrs = spec["attributes"]
        assert attrs.get("recipe") in RECIPES or attrs.get("water")


def test_the_type_table_hands_out_water_and_colours() -> None:
    types = {
        "pond": {"attributes": {"water": True}},
        "wood": {},
        "green": {"attributes": {"colours": ["#00ff00"]}},
    }
    assert water_kinds(types) == {"pond"}
    assert type_colours(types) == {"green": ["#00ff00"]}


def test_ground_types_carry_no_tree_settings() -> None:
    assert all("trees" not in spec["attributes"] for spec in ground_types().values())  # every tree comes from the orthophoto


# --- hand-drawn ground: ground patches -------------------------------------------------------


def _patch(type, x0, y0, x1, y1, **extra):
    return {"type": type, "polygon": [[x0, y0], [x1, y0], [x1, y1], [x0, y1]], **extra}


def test_drawn_ground_is_local_ordered_by_id_and_warns_about_unknown_types() -> None:
    patches = {
        "GP-0001": _patch("meadow", 1000, 2000, 1010, 2010),
        "GP-0003": _patch("orchard", 1005, 2000, 1020, 2010),
        "GP-0004": _patch("swamp", 0, 0, 1, 1),
    }
    types = CFG["types"] | {"meadow": _terrain(), "orchard": _terrain()}
    drawn, warnings = drawn_ground(patches, (1000.0, 2000.0), types)
    assert [(k, p.bounds) for k, p in drawn] == [("orchard", (5, 0, 20, 10)), ("meadow", (0, 0, 10, 10))]  # higher ID first
    assert len(warnings) == 1 and "GP-0004" in warnings[0] and "swamp" in warnings[0]


def test_a_patch_of_a_water_type_is_a_body_of_water_and_no_ground_area() -> None:
    patches = {"GP-0001": _patch("water", 0, 0, 10, 10, water_y=185.0), "GP-0002": _patch("forest", 20, 0, 30, 10)}
    drawn, warnings = drawn_ground(patches, (0.0, 0.0), CFG["types"])
    assert [k for k, _ in drawn] == ["forest"] and warnings == []


def test_drawn_ground_wins_over_the_rules_and_never_enters_a_parcel() -> None:
    cfg = {**CFG, "types": {**CFG["types"], "meadow": _terrain(), "orchard": _terrain()}}
    free = box(0, 0, 100, 100)
    parcels = box(100, 0, 150, 100)  # a property beside the free land
    landuse = [("landuse", "forest", box(0, 0, 100, 100))]
    drawn = [("orchard", box(40, 0, 60, 100)), ("meadow", box(50, 40, 120, 60))]  # the first wins the overlap
    out = build_ground(free, landuse, box(0, 0, 0, 0), cfg, drawn, parcels)
    area = {}
    for k, p in out:
        area[k] = area.get(k, 0) + p.area
    assert area == {"orchard": 2000, "meadow": 40 * 20, "forest": 10000 - 2000 - 800}
    assert all(not p.intersection(parcels).area for _, p in out)
    # a drawn area may reach beyond the cadastre's land
    out = build_ground(free, [], box(0, 0, 0, 0), cfg, [("meadow", box(90, 90, 110, 130))], parcels)
    assert sorted((k, p.area) for k, p in out) == [("field", 9900), ("meadow", 700)]  # 20 x 40 minus the corner inside the parcel


def test_a_type_without_attributes_claims_no_land() -> None:
    free = box(0, 0, 100, 100)
    landuse = [("landuse", "forest", box(0, 0, 50, 100)), ("landuse", "wetland", box(25, 0, 75, 100))]
    cfg = {**CFG, "types": {**CFG["types"], "wet": {"osm": {"landuse": ["wetland"]}}}}
    assert build_ground(free, landuse, box(0, 0, 0, 0), cfg) == build_ground(free, landuse, box(0, 0, 0, 0), CFG)


def test_the_plan_has_every_patch_in_the_colour_of_its_type_for_the_preview() -> None:
    from drycastle_pipeline.edits.schema import parse
    from tests.synthetic import SYNTHETIC_ORIGIN, make_terrain

    ox, oy = SYNTHETIC_ORIGIN
    edits = parse({"version": 1, "ground_patches": {
        "GP-0001": _patch("meadow", ox + 50, oy + 120, ox + 90, oy + 150),
        "GP-0002": _patch("forest", ox + 60, oy + 120, ox + 100, oy + 150),
    }})
    plan = plan_patches(edits, list(SYNTHETIC_ORIGIN), make_terrain(), ground_types())
    assert {p.id: p.type for p in plan.patches.values()} == {"GP-0001": "meadow", "GP-0002": "forest"}
