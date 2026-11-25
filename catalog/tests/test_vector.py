"""Vector retrieval on tiny synthetic vectors: max-sim, filters, kinds, similar, caching."""

from types import SimpleNamespace

import numpy as np
import pytest

from asset_catalog.config import Embeddings, TextEmbedding
from asset_catalog.embedding import images
from asset_catalog.embedding.space import Index, Row, Space
from asset_catalog.search import vector
from asset_catalog.search.types import Filters
from asset_catalog.stages import embed


def unit(*v: float) -> np.ndarray:
    a = np.array(v, np.float32)
    return a / np.linalg.norm(a)


def rows() -> tuple[list[Row], np.ndarray]:
    # deliberately unsorted: the index must group rows by asset itself
    spec = [
        ("b", "what_it_is", "", unit(0, 1, 0)),
        ("a", "what_it_is", "", unit(1, 0, 0)),
        ("a", "likely_query", "something to sit on", unit(0, 0, 1)),
        ("c", "what_it_is", "", unit(1, 1, 0)),
        ("b", "likely_query", "a lamp", unit(0, 1, 1)),
    ]
    rs = [Row(id=i, kind=k, label=lab, key=f"k{n}") for n, (i, k, lab, _) in enumerate(spec)]
    return rs, np.stack([v for *_, v in spec])


def retriever(allowed_ids=None, **kw) -> vector.VectorRetriever:
    rs, m = rows()
    return vector.VectorRetriever(
        "t", lambda: vector.VectorIndex.build(rs, m), lambda q: unit(*map(float, q.split())),
        allowed_ids, **kw,
    )  # fmt: skip


def test_asset_score_is_its_best_vector_and_why_names_it():
    hits = retriever().retrieve("0 0 1", Filters(), 3)
    assert [h.id for h in hits] == ["a", "b", "c"]
    assert hits[0].score == pytest.approx(1.0)
    assert hits[0].why == ["likely query: something to sit on"]
    assert retriever().retrieve("1 0 0", Filters(), 1)[0].why == ["description text"]


def test_k_larger_than_corpus_and_blank_query():
    assert len(retriever().retrieve("1 0 0", Filters(), 50)) == 3
    assert retriever().retrieve("  ", Filters(), 5) == []


def test_filters_restrict_to_allowed_ids():
    r = retriever(allowed_ids=lambda f: {"b", "c"})
    assert [h.id for h in r.retrieve("1 0 0", Filters(kinds=["x"]), 5)] == ["c", "b"]
    assert retriever(allowed_ids=lambda f: set()).retrieve("1 0 0", Filters(kinds=["x"]), 5) == []


def test_filters_without_a_supplier_are_an_error_not_ignored():
    with pytest.raises(ValueError):
        retriever().retrieve("1 0 0", Filters(kinds=["prop"]), 5)


def test_exclude_ids_needs_no_supplier_and_combines_with_one():
    assert [h.id for h in retriever().retrieve("1 0 0", Filters(exclude_ids=["a"]), 5)] == [
        "c",
        "b",
    ]
    r = retriever(allowed_ids=lambda f: {"a", "b"})
    assert [h.id for h in r.retrieve("1 0 0", Filters(kinds=["x"], exclude_ids=["a"]), 5)] == ["b"]


def test_kinds_restriction_and_bias():
    only_queries = retriever(kinds=frozenset({"likely_query"}))
    assert [h.id for h in only_queries.retrieve("0 0 1", Filters(), 3)] == ["a", "b"]  # c has none
    no_queries = retriever(kinds=frozenset({"what_it_is"}))
    assert no_queries.retrieve("0 1 1", Filters(), 1)[0].score == pytest.approx(0.7071, abs=1e-3)
    biased = retriever(kind_bias={"likely_query": -2.0})
    assert biased.retrieve("0 0 1", Filters(), 1)[0].score == pytest.approx(0.0)  # a's 1.0 - 2


def test_query_vector_is_computed_once_per_query():
    calls = []
    rs, m = rows()
    r = vector.VectorRetriever(
        "t", lambda: vector.VectorIndex.build(rs, m), lambda q: calls.append(q) or unit(1, 0, 0)
    )
    r.retrieve("x", Filters(), 1)
    r.retrieve("x", Filters(), 2)
    assert calls == ["x"]


def test_similar_blends_standardised_sides_and_skips_itself(monkeypatch):
    rs = [Row(id=i, kind="what_it_is", label="", key=i) for i in "abc"]
    text = np.stack([unit(1, 0), unit(1, 0.1), unit(0, 1)])
    views = [Row(id=i, kind="view", label="front", key=i) for i in "abc"]
    pics = np.stack([unit(1, 0), unit(0, 1), unit(1, 0.2)])
    idx = {
        "text": vector.VectorIndex.build(rs, text),
        "image": vector.VectorIndex.build(views, pics),
    }
    monkeypatch.setattr(vector, "load_index", lambda modality, describer, cfg=None: idx[modality])
    hits = vector.similar("a", 5, "m", cfg=Embeddings())
    assert [h.id for h in hits] == ["c", "b"] or [h.id for h in hits] == ["b", "c"]
    assert "a" not in [h.id for h in hits]
    only_pictures = vector.similar("a", 5, "m", cfg=Embeddings(), text_weight=0)
    assert only_pictures[0].id == "c"
    assert (
        vector.similar(
            "a", 5, "m", cfg=Embeddings(), allowed_ids=lambda f: {"b"}, filters=Filters(kinds=["x"])
        )[0].id
        == "b"
    )


def test_space_roundtrip_and_prune(tmp_path):
    s = Space("text", "org/model", "t1", data_dir=tmp_path)
    s.add(["k1", "k2"], np.array([[1, 0], [0, 1]], np.float32))
    rs = [Row(id="a", kind="what_it_is", label="", key="k1")]
    s.save(Index(describer="m", rows=rs))
    again = Space("text", "org/model", "t1", data_dir=tmp_path)
    assert again.has("k1") and not again.has("k2")  # k2 unused by any index: dropped
    assert again.read_index("m").rows == rs
    assert again.matrix(["k1"]).tolist() == [[1.0, 0.0]]


def test_merge_rows_keeps_other_assets_only_when_partial():
    old = Index(
        describer="m",
        rows=[Row(id="a", kind="v", label="", key="1"), Row(id="b", kind="v", label="", key="2")],
    )
    new = [Row(id="b", kind="v", label="", key="3")]
    assert [r.key for r in embed.merge_rows(old, new, {"b"}, True)] == ["1", "3"]
    assert [r.key for r in embed.merge_rows(old, new, {"b"}, False)] == ["3"]


def test_text_items_dedupe_likely_queries_and_use_the_query_prompt():
    rec = SimpleNamespace(
        id="a",
        likely_queries=["Chair", "chair ", "desk lamp"],
        what_it_is=lambda: "d",
        what_it_is_for=lambda: "u",
    )
    items = embed.text_items(rec, Embeddings())
    assert [i.row.kind for i in items] == [
        "what_it_is",
        "what_it_is_for",
        "likely_query",
        "likely_query",
    ]
    assert [i.is_query for i in items] == [False, False, False, False]  # stored as documents
    asked = Embeddings(text=TextEmbedding(likely_query_prompt="query", query_prompt="Q:"))
    queries = embed.text_items(rec, asked)
    assert [i.is_query for i in queries] == [False, False, True, True]
    assert queries[2].row.key != items[2].row.key  # the prompt is part of the cache key
    assert not embed.text_items(rec, Embeddings(likely_queries=False))[2:]


def test_erase_and_crop_remove_overlays_and_centre_the_model():
    a = np.full((768, 768, 3), 218, np.uint8)
    a[10:40, 28:90] = 58  # title
    a[730:740, 28:200] = 58  # scale bar
    a[300:400, 500:560] = (200, 30, 30)  # the model
    from PIL import Image

    cleaned = images.erase_overlays(Image.fromarray(a), None, "three_quarter")
    assert (cleaned == 58).sum() == 0
    cropped = images.crop_to_model(cleaned, 0.1)
    assert cropped.shape[0] == cropped.shape[1] == round(100 * 1.2)
    assert tuple(cropped[cropped.shape[0] // 2, cropped.shape[1] // 2]) == (200, 30, 30)
