import pytest

from asset_catalog import index
from asset_catalog.search.keyword import KeywordParams, KeywordRetriever
from asset_catalog.search.types import Filters
from synthetic_records import make


def records():
    return [
        make("s/o/water_cooler_01", plain_name="office water cooler", aliases=["water dispenser"],
             description="A tall water cooler with a bottle on top.", settings=["break-room"]),
        make("s/o/chair_01", plain_name="swivel chair", aliases=["desk chair", "office chair"],
             description="Chair on five wheels.", kind="furniture"),
        make("s/o/shelf_01", plain_name="open shelf", aliases=["bookcase"], kind="furniture",
             description="Open shelving unit.", placements=["wall-mounted"]),
        make("s/o/whiteboard", plain_name="mobile whiteboard", aliases=["flip chart"],
             description="A whiteboard on a stand."),
        make("s/o/stapler", plain_name="desktop stapler", description="Red stapler for paper."),
        make("s/o/SM_Prop_Printer_02", plain_name="laser printer", description="Prints paper."),
    ]  # fmt: skip


@pytest.fixture
def kr(tmp_path):
    index.build(records(), "m", tmp_path)
    return KeywordRetriever(index.open_index("m", tmp_path))


def top(kr, query, filters=None, k=5) -> list[str]:
    return [h.id.split("/", 2)[2] for h in kr.retrieve(query, filters or Filters(), k)]


@pytest.mark.parametrize(
    "query",
    ['"', "'", "chair AND", "NOT chair", "chair NEAR/2 desk", "chair*", "col:chair", "(chair", "^chair",
     "chair -desk", "a OR b", '"water cooler" OR "', "\\", "chair\x00desk", "", "   ", "the of and"],
)  # fmt: skip
def test_hostile_or_empty_queries_never_raise(kr, query):
    kr.retrieve(query, Filters(), 5)


def test_operators_are_plain_words_not_syntax(kr):
    assert top(kr, "chair AND NOT desk NEAR printer")[0] == "chair_01"


def test_name_and_hyphen_variants_match_spoken_words(kr):
    assert top(kr, "water cooler")[0] == "water_cooler_01"
    assert top(kr, "SM_Prop_Printer_02")[0] == "SM_Prop_Printer_02"
    assert top(kr, "wall mounted shelf")[0] == "shelf_01"  # placement term "wall-mounted"
    assert top(kr, "break room")[0] == "water_cooler_01"


def test_alias_match_ranks_first_and_says_why(kr):
    hits = kr.retrieve("water dispenser", Filters(), 3)
    assert hits[0].id.endswith("water_cooler_01")
    assert "exact alias" in hits[0].why
    assert any(w.startswith("alias: water dispenser") for w in hits[0].why)


def test_plurals_and_natural_language(kr):
    assert top(kr, "I need some chairs to put in the office")[0] == "chair_01"


def test_misspelling_and_compounds_are_repaired(kr):
    assert top(kr, "stappler")[0] == "stapler"
    assert top(kr, "white board")[0] == "whiteboard"  # catalogue writes it as one word
    assert top(kr, "book case")[0] == "shelf_01"
    assert top(kr, "somthing to staple paper")[0] == "stapler"  # misspelt filler is dropped


def test_negated_word_is_not_searched(kr):
    assert top(kr, "chair without printer")[0] == "chair_01"
    assert "SM_Prop_Printer_02" not in top(kr, "chair without printer")


def test_words_that_appear_nowhere_return_nothing(kr):
    assert top(kr, "helicopter landing platform") == []
    assert top(kr, "chair helicopter landing platform zeppelin") == []


def test_filters_apply_before_the_cut_off(kr):
    only = Filters(kinds=["furniture"])
    assert top(kr, "office", only, k=1) == ["chair_01"]  # not empty because a prop outranks it
    assert set(top(kr, "chair shelf", only, k=5)) == {"chair_01", "shelf_01"}


def test_full_k_is_returned_when_that_many_match(tmp_path):
    many = [make(f"s/o/box_{i:02d}", plain_name="crate", description="a box") for i in range(30)]
    many.append(make("s/o/special", plain_name="special crate", kind="furniture"))
    index.build(many, "m", tmp_path)
    kr = KeywordRetriever(index.open_index("m", tmp_path))
    assert len(kr.retrieve("crate", Filters(), 10)) == 10
    assert len(kr.retrieve("crate", Filters(kinds=["furniture"]), 10)) == 1


def test_parameters_change_the_ranking(tmp_path):
    index.build(records(), "m", tmp_path)
    ix = index.open_index("m", tmp_path)
    weights = KeywordParams().field_weights | {
        "description": 50.0,
        "aliases": 0.1,
        "plain_name": 0.1,
    }
    ranked = KeywordRetriever(ix, KeywordParams(field_weights=weights, key_bonus={}))
    assert ranked.retrieve("chair", Filters(), 1)[0].id.endswith("chair_01")
    assert KeywordRetriever(ix, KeywordParams(fuzzy=False)).retrieve("stappler", Filters(), 3) == []
