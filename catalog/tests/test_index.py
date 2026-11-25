import json

import pytest

from asset_catalog import index
from asset_catalog.search.types import Filters
from synthetic_records import make


def records():
    return [
        make("s/office/chair_01", kind="furniture", category="furniture/seating/chair",
             secondary_categories=["prop/office/seat"], materials=["metal", "fabric"],
             settings=["office"], placements=["floor-standing"], colours=["dark grey", "black"],
             variant_group="s/office/chair", size_m=(0.6, 1.0, 0.6), size_class="furniture"),
        make("s/office/chair_02", kind="furniture", category="furniture/seating/chair",
             materials=["metal"], settings=["office", "reception"], colours=["light grey"],
             variant_group="s/office/chair", size_m=(0.6, 1.2, 0.6), size_class="furniture"),
        make("s/office/desk_lamp", category="prop/lighting/lamp", materials=["metal"],
             settings=["office"], placements=["on-surface"], colours=["red"],
             size_m=(0.2, 0.4, 0.2), longest_m=0.4),
        make("s/office/clock", category="prop/decor/clock", materials=["plastic"],
             placements=["wall-mounted"], size_m=(0.3, 0.3, 0.05), longest_m=0.3),
        make("s/lobby/vase", category="prop/decor/vase", settings=["reception"], has_rig=True,
             size_m=(0.2, 0.3, 0.2), longest_m=0.3),
        make("t/lobby/bench", kind="furniture", category="furniture/seating/bench",
             settings=["reception"], size_class="furniture", size_m=(1.5, 0.5, 0.4), longest_m=1.5),
    ]  # fmt: skip


@pytest.fixture
def ix(tmp_path):
    index.build(records(), "m", tmp_path)
    return index.open_index("m", tmp_path)


def ids(ix, **kw) -> set[str]:
    return {i.split("/", 2)[2] for i in ix.filter_ids(Filters(**kw))}


def test_empty_filters_match_everything(ix):
    assert len(ix.filter_ids(Filters())) == 6
    assert len(ix.all_ids()) == 6


def test_or_within_a_field_and_across_fields(ix):
    assert ids(ix, settings=["office"]) == {"chair_01", "chair_02", "desk_lamp"}
    assert ids(ix, settings=["office", "reception"]) == {
        "chair_01", "chair_02", "desk_lamp", "vase", "bench"
    }  # fmt: skip
    assert ids(ix, settings=["reception"], materials=["metal"]) == {"chair_02"}
    assert ids(ix, kinds=["furniture"], settings=["lobby-nope"]) == set()


def test_category_matches_leaf_or_any_prefix_primary_or_secondary(ix):
    assert ids(ix, categories=["furniture"]) == {"chair_01", "chair_02", "bench"}
    assert ids(ix, categories=["furniture/seating"]) == {"chair_01", "chair_02", "bench"}
    assert ids(ix, categories=["furniture/seating/chair/"]) == {"chair_01", "chair_02"}
    assert ids(ix, categories=["prop/office"]) == {"chair_01"}  # secondary category
    assert ids(ix, categories=["prop/lighting/lamp", "prop/decor/vase"]) == {"desk_lamp", "vase"}
    assert ids(ix, categories=["furn"]) == set()  # not a path prefix


def test_packs_match_source_pack_or_bare_pack(ix):
    assert ids(ix, packs=["s/office"]) == {"chair_01", "chair_02", "desk_lamp", "clock"}
    assert ids(ix, packs=["lobby"]) == {"vase", "bench"}
    assert ids(ix, packs=["s/lobby"]) == {"vase"}
    assert ids(ix, packs=["office", "t/lobby"]) == {"chair_01", "chair_02", "desk_lamp", "clock", "bench"}  # fmt: skip


def test_colour_filter_takes_shades_of_a_stated_colour(ix):
    assert ids(ix, colours=["grey"]) == {"chair_01", "chair_02"}
    assert ids(ix, colours=["dark grey"]) == {"chair_01"}
    assert ids(ix, colours=["red", "black"]) == {"desk_lamp", "chair_01"}


def test_size_rig_and_exclusion(ix):
    assert ids(ix, min_height_m=1.1) == {"chair_02"}
    assert ids(ix, max_longest_m=0.35, min_width_m=0.25) == {"clock"}
    assert ids(ix, rigged=True) == {"vase"}
    assert ids(ix, rigged=False, size_classes=["furniture"]) == {"chair_01", "chair_02", "bench"}
    assert "chair_01" not in ids(ix, exclude_ids=["s/office/chair_01"])


def test_facet_counts_follow_the_filters(ix):
    counts = ix.facet_counts(Filters(kinds=["furniture"]))
    assert counts["category"]["furniture"] == 3
    assert counts["category"]["furniture/seating/chair"] == 2
    assert counts["category"]["prop/office"] == 1  # secondary category counts too
    assert counts["setting"] == {"reception": 2, "office": 2}
    assert counts["pack"]["s/office"] == 2
    assert ix.facet_counts()["kind"] == {"prop": 3, "furniture": 3}


def test_get_siblings_and_variant_groups(ix):
    assert ix.get("s/office/clock").plain_name == "clock"
    with pytest.raises(KeyError):
        ix.get("nope")
    got = ix.get_many(["s/office/clock", "missing", "s/office/chair_01"])
    assert [r.id for r in got] == ["s/office/clock", "s/office/chair_01"]
    assert ix.siblings("s/office/chair_01") == ["s/office/chair_02"]
    assert ix.siblings("s/office/clock") == []
    assert ix.variant_groups() == {"s/office/chair": ["s/office/chair_01", "s/office/chair_02"]}


def test_jsonl_is_per_pack_sorted_and_stale_files_go(tmp_path):
    recs = records()
    index.build(recs, "m", tmp_path)
    root = index.records_dir("m", tmp_path)
    lines = (root / "s" / "office.jsonl").read_text().splitlines()
    assert [json.loads(x)["id"] for x in lines] == sorted(json.loads(x)["id"] for x in lines)
    assert {p.relative_to(root).as_posix() for p in root.rglob("*.jsonl")} == {
        "s/office.jsonl", "s/lobby.jsonl", "t/lobby.jsonl"
    }  # fmt: skip
    index.build([r for r in recs if r.source == "s"], "m", tmp_path)
    assert not (root / "t" / "lobby.jsonl").exists()
    assert len(index.open_index("m", tmp_path).all_ids()) == 5
    assert not list(index.index_path("m", tmp_path).parent.glob("*.tmp"))


def test_missing_index_says_how_to_build_it(tmp_path):
    with pytest.raises(FileNotFoundError, match="catalog index"):
        index.open_index("nope", tmp_path)
