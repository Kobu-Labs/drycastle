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
    ground_types,
    ground_units,
    kept_parcels,
    spawn_kinds,
    type_colours,
    water_kinds,
)
from drycastle_pipeline.rules import load_section

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
# a type that only adds attributes (no terrain): it can lie over any ground without changing it
BIGFOOT = {"attributes": {"event_spawn": ["bigfoot"]}}
SPAWN = {"attributes": {"spawns": True}}


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


def test_the_type_table_hands_out_water_spawns_and_colours() -> None:
    types = {
        "pond": {"attributes": {"water": True}},
        "wood": {},
        "green": {"attributes": {"spawns": True, "colours": ["#00ff00"]}},
    }
    assert (water_kinds(types), spawn_kinds(types)) == ({"pond"}, {"green"})
    assert type_colours(types) == {"green": ["#00ff00"]}


def test_ground_types_carry_no_tree_settings() -> None:
    assert all("trees" not in spec["attributes"] for spec in ground_types().values())  # every tree comes from the orthophoto


# --- hand-drawn ground: regions with a ground type --------------------------------------------


def _region(x0, y0, x1, y1, **extra):
    return {"colour": "#112233", "polygon": [[x0, y0], [x1, y0], [x1, y1], [x0, y1]], **extra}


def test_region_ground_key_is_validated() -> None:
    def errs(**extra):
        return validate({"version": 1, "regions": {"R-0001": _region(0, 0, 10, 10, **extra)}})

    assert errs(ground=["meadow"]) == []
    assert errs(ground=["forest", "bigfoot_zone"]) == []  # a stack, lowest first
    assert any("non-empty list" in e for e in errs(ground="meadow"))  # the old shape, one name
    assert any("non-empty list" in e for e in errs(ground=[]))
    assert any("ground type name" in e for e in errs(ground=["Meadow!"]))
    assert any("ground type name" in e for e in errs(ground=[3]))
    assert any("twice" in e for e in errs(ground=["meadow", "meadow"]))
    assert any("not together with water_y" in e for e in errs(ground=["meadow"], water_y=100.0))


def test_drawn_ground_is_local_ordered_by_id_and_warns_about_unknown_types() -> None:
    regions = {
        "R-0001": _region(1000, 2000, 1010, 2010, ground=["meadow"]),
        "R-0002": _region(1000, 2000, 1005, 2005),  # paint: not ground
        "R-0003": _region(1005, 2000, 1020, 2010, ground=["orchard"]),
        "R-0004": _region(0, 0, 1, 1, ground=["swamp"]),
    }
    types = CFG["types"] | {"meadow": _terrain(), "orchard": _terrain()}
    drawn, layers, warnings = drawn_ground(regions, (1000.0, 2000.0), types)
    assert [(k, p.bounds) for k, p in drawn] == [("orchard", (5, 0, 20, 10)), ("meadow", (0, 0, 10, 10))]  # higher ID first
    assert layers == []  # no type adds attributes
    assert len(warnings) == 1 and "R-0004" in warnings[0] and "swamp" in warnings[0]


def test_a_stack_has_the_terrain_of_its_topmost_terrain_type_and_the_layers_of_every_type() -> None:
    types = CFG["types"] | {"meadow": _terrain(spawns=True), "bigfoot_zone": BIGFOOT, "spawn_zone": SPAWN}
    regions = {
        "R-0001": _region(1000, 2000, 1010, 2010, ground=["forest", "meadow", "bigfoot_zone"]),  # events on top: meadow decides
        "R-0002": _region(1020, 2000, 1030, 2010, ground=["bigfoot_zone", "forest"]),  # forest on top of the events
        "R-0003": _region(1040, 2000, 1050, 2010, ground=["bigfoot_zone", "spawn_zone"]),  # no terrain type at all
    }
    drawn, layers, warnings = drawn_ground(regions, (1000.0, 2000.0), types)
    assert [(k, p.bounds) for k, p in drawn] == [("forest", (20, 0, 30, 10)), ("meadow", (0, 0, 10, 10))]
    assert [(rid, names) for rid, names, _ in layers] == [  # lowest region first; only the types that add up, not the deciding one
        ("R-0001", ["bigfoot_zone"]), ("R-0002", ["bigfoot_zone"]), ("R-0003", ["bigfoot_zone", "spawn_zone"]),
    ]
    assert layers[0][2].bounds == (0, 0, 10, 10) and warnings == []


def test_an_unknown_type_is_left_out_of_its_stack_and_a_stack_of_unknowns_is_skipped() -> None:
    types = CFG["types"] | {"bigfoot_zone": BIGFOOT}
    regions = {
        "R-0001": _region(0, 0, 10, 10, ground=["swamp", "forest", "bigfoot_zone"]),
        "R-0002": _region(20, 0, 30, 10, ground=["swamp"]),
    }
    drawn, layers, warnings = drawn_ground(regions, (0.0, 0.0), types)
    assert [k for k, _ in drawn] == ["forest"] and [rid for rid, _, _ in layers] == ["R-0001"]
    assert len(warnings) == 2 and all("swamp" in w for w in warnings)


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


def test_a_type_without_terrain_attributes_claims_no_land_and_an_events_layer_changes_no_ground() -> None:
    free = box(0, 0, 100, 100)
    landuse = [("landuse", "forest", box(0, 0, 50, 100)), ("landuse", "wetland", box(25, 0, 75, 100))]
    cfg = {**CFG, "types": {**CFG["types"], "wet": {"osm": {"landuse": ["wetland"]}, **BIGFOOT}}}
    assert build_ground(free, landuse, box(0, 0, 0, 0), cfg) == build_ground(free, landuse, box(0, 0, 0, 0), CFG)
    # an events-only stack on top of a forest region: the ground areas are exactly those of the forest region alone
    types = CFG["types"] | {"bigfoot_zone": BIGFOOT}
    region = _region(10, 10, 60, 60, ground=["forest"])
    stacked = _region(10, 10, 60, 60, ground=["forest", "bigfoot_zone"])
    plain, layers, _ = drawn_ground({"R-0001": region}, (0.0, 0.0), types)
    both, layers2, _ = drawn_ground({"R-0001": stacked}, (0.0, 0.0), types)
    assert layers == [] and len(layers2) == 1
    assert build_ground(free, landuse, box(0, 0, 0, 0), CFG, plain) == build_ground(free, landuse, box(0, 0, 0, 0), CFG, both)


def test_ground_regions_are_left_out_of_the_paint_plan_on_request() -> None:
    from drycastle_pipeline.edits import parse

    from .synthetic import SYNTHETIC_ORIGIN, make_terrain

    ox, oy = SYNTHETIC_ORIGIN
    edits = parse({"version": 1, "regions": {
        "R-0001": _region(ox + 50, oy + 120, ox + 90, oy + 150),
        "R-0002": _region(ox + 60, oy + 120, ox + 100, oy + 150, ground=["meadow"]),
        "R-0003": _region(ox + 60, oy + 120, ox + 100, oy + 150, ground=["bigfoot_zone"]),
    }})
    terrain = make_terrain()
    assert set(plan_regions(edits, list(SYNTHETIC_ORIGIN), terrain).regions) == {"R-0001", "R-0002", "R-0003"}  # hot reload preview
    assert set(plan_regions(edits, list(SYNTHETIC_ORIGIN), terrain, ground=False).regions) == {"R-0001"}
    types = {"meadow": _terrain(), "bigfoot_zone": BIGFOOT}  # a layer of attributes is no paint: the editor draws it as an overlay
    assert set(plan_regions(edits, list(SYNTHETIC_ORIGIN), terrain, types=types).regions) == {"R-0001", "R-0002"}
