from asset_catalog.search import text
from asset_catalog.search.spelling import Speller, distance


def test_name_words_split_underscores_camel_case_and_numbers():
    assert text.name_words("SM_Prop_WaterCooler_01") == ["prop", "water", "cooler"]
    assert text.name_words("water_cooler_01") == ["water", "cooler"]
    assert text.name_words("PCMonitor02") == ["pc", "monitor02"]
    assert text.name_words("Chair-Office_3") == ["chair", "office"]
    assert text.name_words("sm") == ["sm"]  # a lone prefix-looking stem is still a word


def test_name_key_keeps_the_number_and_folds_plurals():
    assert text.name_key("SM_Prop_WaterCooler_01") == "prop water cooler 1"
    assert text.name_key("SM_Prop_WaterCooler_01", with_number=False) == "prop water cooler"
    assert text.key_of(text.words("Water Coolers")) == "water cooler"


def test_words_split_hyphens_and_drop_apostrophes():
    assert text.words("Wall-mounted break-room") == ["wall", "mounted", "break", "room"]
    assert text.words("banker's box") == ["bankers", "box"]
    assert text.words("Caf" + chr(0xE9) + " table") == ["cafe", "table"]


def test_query_words_split_a_pasted_file_name():
    assert text.query_words("SM_Prop_WaterCooler_01") == ["prop", "water", "cooler", "01"]
    assert text.query_words("water cooler") == ["water", "cooler"]


def test_parse_query_drops_stopwords_filler_numbers_and_repeats():
    p = text.parse_query("Something small to put on a reception desk, desk 2")
    assert p.terms == ["small", "reception", "desk"]


def test_parse_query_neutralises_fts_syntax():
    p = text.parse_query('"chair" AND (desk OR NEAR/3 lamp) * col:x ^y -z')
    assert p.terms == ["chair", "desk", "lamp", "col"]
    assert all(t.isalnum() for t in p.terms)


def test_parse_query_negation_drops_the_next_word_only():
    assert text.parse_query("office chair without wheels").terms == ["office", "chair"]
    assert text.parse_query("no wheels chair").terms == ["chair"]


def test_adjacent_pairs_break_at_stopwords():
    assert text.parse_query("desk lamp for reception desk").adjacent_pairs() == [("desk", "lamp")]
    assert text.parse_query("chair with arms").adjacent_pairs() == []


def test_parse_query_caps_terms():
    assert len(text.parse_query(" ".join(f"w{i}x" for i in range(100)), max_terms=5).terms) == 5


def test_distance_handles_swaps_and_limits():
    assert distance("stapler", "stapler", 2) == 0
    assert distance("whiteborad", "whiteboard", 2) == 1  # swapped neighbours
    assert distance("abc", "xyzxyz", 1) == 2  # over the limit


def test_speller_prefers_near_then_frequent_and_skips_known_words():
    s = Speller({"stapler": 5, "staple": 20, "stable": 40, "printer": 9})
    assert s.suggest("stappler") == ["stapler", "staple"]
    assert s.suggest("printer") == []  # known
    assert s.suggest("cat") == []  # too short to guess
