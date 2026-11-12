"""Ground areas: which parcels stay parcels, and how the other land is cut by type."""

from shapely.geometry import LineString, box

from drycastle_pipeline.edits import validate
from drycastle_pipeline.regions import plan_regions
from shapely.ops import unary_union

from drycastle_pipeline.detail import RECIPES
from drycastle_pipeline.ground import (
    build_ground,
    dike_ground,
    drawn_ground,
    forest_kinds,
    ground_types,
    ground_units,
    kept_parcels,
    spawn_kinds,
    tree_density,
    type_colours,
    water_kinds,
)
from drycastle_pipeline.rules import load_section

CFG = {
    "min_area_m2": 1.0,
    "default": "field",
    "types": {
        "dike": {"rule": "dike"},
        "water": {"osm": {"natural": ["water"]}},
        "forest": {"osm": {"landuse": ["forest"], "natural": ["wood"]}},
        "village": {"osm": {"landuse": ["residential"]}},
        "field": {},
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
        assert spec.get("recipe") in RECIPES or spec.get("water")


def test_the_type_table_hands_out_trees_water_and_spawns() -> None:
    types = {
        "pond": {"water": True},
        "wood": {"trees": {"per_1000m2": 5, "forest": True}},
        "green": {"trees": {"per_1000m2": 0.5}, "spawns": True, "colours": ["#00ff00"]},
    }
    assert tree_density(types) == {"pond": 0.0, "wood": 5.0, "green": 0.5}
    assert (forest_kinds(types), water_kinds(types), spawn_kinds(types)) == ({"wood"}, {"pond"}, {"green"})
    assert type_colours(types) == {"green": ["#00ff00"]}


def test_village_overlay_changes_a_type_without_losing_the_rest() -> None:
    plain, village = ground_types(), ground_types(area="village")
    assert village["forest"]["trees"]["per_1000m2"] < plain["forest"]["trees"]["per_1000m2"]
    assert village["forest"]["trees"]["forest"] and village["forest"]["recipe"] == "forest"
    assert list(village) == list(plain)  # the order of the types is the order of the cut


# --- hand-drawn ground: regions with a ground type --------------------------------------------


def _region(x0, y0, x1, y1, **extra):
    return {"colour": "#112233", "polygon": [[x0, y0], [x1, y0], [x1, y1], [x0, y1]], **extra}


def test_region_ground_key_is_validated() -> None:
    def errs(**extra):
        return validate({"version": 1, "regions": {"R-0001": _region(0, 0, 10, 10, **extra)}})

    assert errs(ground="meadow") == []
    assert any("ground type name" in e for e in errs(ground="Meadow!"))
    assert any("ground type name" in e for e in errs(ground=3))
    assert any("not together with water_y" in e for e in errs(ground="meadow", water_y=100.0))


def test_drawn_ground_is_local_ordered_by_id_and_warns_about_unknown_types() -> None:
    regions = {
        "R-0001": _region(1000, 2000, 1010, 2010, ground="meadow"),
        "R-0002": _region(1000, 2000, 1005, 2005),  # paint: not ground
        "R-0003": _region(1005, 2000, 1020, 2010, ground="orchard"),
        "R-0004": _region(0, 0, 1, 1, ground="swamp"),
    }
    drawn, warnings = drawn_ground(regions, (1000.0, 2000.0), CFG["types"] | {"meadow": {}, "orchard": {}})
    assert [(k, p.bounds) for k, p in drawn] == [("orchard", (5, 0, 20, 10)), ("meadow", (0, 0, 10, 10))]  # higher ID first
    assert len(warnings) == 1 and "R-0004" in warnings[0] and "swamp" in warnings[0]


def test_drawn_ground_wins_over_the_rules_and_never_enters_a_parcel() -> None:
    cfg = {**CFG, "types": {**CFG["types"], "meadow": {}, "orchard": {}}}
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


def test_ground_regions_are_left_out_of_the_paint_plan_on_request() -> None:
    from drycastle_pipeline.edits import parse

    from .synthetic import SYNTHETIC_ORIGIN, make_terrain

    ox, oy = SYNTHETIC_ORIGIN
    edits = parse({"version": 1, "regions": {
        "R-0001": _region(ox + 50, oy + 120, ox + 90, oy + 150),
        "R-0002": _region(ox + 60, oy + 120, ox + 100, oy + 150, ground="meadow"),
    }})
    terrain = make_terrain()
    assert set(plan_regions(edits, list(SYNTHETIC_ORIGIN), terrain).regions) == {"R-0001", "R-0002"}  # hot reload preview
    assert set(plan_regions(edits, list(SYNTHETIC_ORIGIN), terrain, ground=False).regions) == {"R-0001"}
