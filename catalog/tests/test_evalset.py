"""Evaluation set: metrics on hand-made rankings, judgment precedence, closed schemas."""

import pytest

from asset_catalog import vocab
from asset_catalog.evalset import draft, judge, metrics
from asset_catalog.evalset import io as evalio
from asset_catalog.evalset.models import Judgment, Override, Query, QuerySet
from asset_catalog.search.types import Filters


def q(qid: str, style: str = "keyword", text: str = "chair", **kw) -> Query:
    return Query(id=qid, text=text, style=style, **kw)


GRADES = {"a": 3, "b": 2, "c": 1, "d": 0, "e": 2}  # f..: unjudged


def test_perfect_and_reversed_ranking():
    perfect = metrics.score_query(q("1"), ["a", "b", "e", "c", "d"], GRADES, gain="linear")
    assert perfect.ndcg == pytest.approx(1.0)
    assert perfect.recall == 1.0 and perfect.rr == 1.0 and perfect.precision5 == 0.8
    worse = metrics.score_query(q("1"), ["d", "c", "b", "a", "e"], GRADES, gain="linear")
    assert 0 < worse.ndcg < 1 and worse.rr == pytest.approx(1 / 3)


def test_unjudged_counts_as_zero_and_is_reported():
    s = metrics.score_query(q("1"), ["x", "a", "y", "b"], GRADES, k=4)
    assert s.unjudged == 2 and s.unjudged_ids == ["x", "y"]
    assert s.recall == pytest.approx(2 / 3)  # a, b of a, b, e
    assert s.rr == 0.5


def test_no_right_answer_leaves_recall_and_mrr_undefined():
    s = metrics.score_query(q("1"), ["c"], {"c": 1})
    assert s.recall is None and s.rr is None and s.ndcg is not None and s.relevant == 0


def test_negative_query_scores_abstention_only():
    abstain = metrics.score_query(q("n", "negative"), [], {"x": 0})
    assert abstain.abstained and abstain.ndcg is None and abstain.precision5 is None
    assert not metrics.score_query(q("n", "negative"), ["x"], {"x": 0}).abstained


def test_variant_groups_are_scored_as_one():
    group_of = {"chair_1": "chair", "chair_2": "chair", "chair_3": "chair"}
    grades = {"chair_1": 3, "chair_2": 2, "chair_3": 3, "lamp": 2}
    ranked = ["chair_2", "chair_1", "chair_3", "lamp"]
    flat = metrics.score_query(q("1"), ranked, grades, gain="linear")
    grouped = metrics.score_query(q("1"), ranked, grades, gain="linear", group_of=group_of)
    assert grouped.returned == 2 and grouped.ndcg == pytest.approx(1.0)
    assert flat.ndcg < 1.0


def test_summary_by_style_and_false_abstain():
    qs = QuerySet(name="t", pack="s/p", queries=[q("1"), q("2"), q("3", "negative")])
    qrels = {"1": {"a": 3}, "2": {"b": 3}, "3": {"z": 0}}
    answers = {"1": ["a"], "2": [], "3": []}
    r = metrics.evaluate(qs, lambda x: answers[x.id], qrels)
    assert r.per_style["keyword"].false_abstain_rate == 0.5
    assert r.per_style["negative"].abstain_rate == 1.0
    assert r.overall.unjudged_share == 0.0
    assert metrics.unjudged_pool(r) == {}
    assert "keyword" in metrics.format_report(r)


def test_overrides_win_and_stale_judgments_are_ignored(tmp_path):
    qs = QuerySet(name="t", pack="s/p", queries=[q("1"), q("2", text="sofa")])
    store = evalio.JudgmentStore("t", tmp_path)
    mk = lambda qid, a, g, digest: Judgment(
        query_id=qid, asset_id=a, grade=g, judged_by="model", judge="m", query_digest=digest
    )
    store.add([mk("1", "a", 1, qs.get("1").digest()), mk("2", "a", 3, "stale")])
    store.add([mk("1", "a", 2, qs.get("1").digest())])  # later line wins
    qrels = evalio.load_qrels(qs, store, [])
    assert qrels == {"1": {"a": 2}, "2": {}}
    qrels = evalio.load_qrels(qs, store, [Override(query="1", asset="a", grade=0, by="me")])
    assert qrels["1"]["a"] == 0
    with pytest.raises(ValueError):
        evalio.load_qrels(qs, store, [Override(query="9", asset="a", grade=1)])


def test_judge_pending_skips_fresh_pairs(tmp_path):
    query = q("1")
    store = evalio.JudgmentStore("t", tmp_path)
    j = judge.Judge.__new__(judge.Judge)  # no client needed to compute the pending set
    j.set_name, j.store = "nonexistent-set", store
    store.add([
        Judgment(query_id="1", asset_id="a", grade=3, judged_by="model", judge="m",
                 query_digest=query.digest(), rubric_version=judge.RUBRIC_VERSION),
    ])  # fmt: skip
    assert j.pending({"1": query}, {"1": ["a", "b", "b"]}) == {"1": ["b"]}


def test_resolved_queries_carry_the_pack_and_keep_their_digest():
    qs = QuerySet(name="t", pack="s/p", queries=[q("1", filters=Filters(max_height_m=1))])
    (r,) = qs.resolved()
    assert r.filters.packs == ["s/p"] and r.filters.max_height_m == 1
    assert r.digest() == qs.queries[0].digest()


def test_yaml_roundtrip_leaves_defaults_out(tmp_path):
    qs = QuerySet(
        name="t",
        pack="s/p",
        queries=[q("1"), q("2", "constraint", filters=Filters(max_height_m=1))],
    )
    path = evalio.save_queryset(qs, tmp_path / "t.yaml")
    assert "kinds" not in path.read_text()
    assert evalio.load_queryset("t", path) == qs
    with pytest.raises(FileExistsError):
        evalio.save_queryset(qs, path)


def test_schemas_are_closed():
    v = vocab.load()
    for style in draft.ORDER:
        s = draft.schema(style, v)
        item = s["properties"]["queries"]["items"]
        assert item["additionalProperties"] is False and item["required"] == list(
            item["properties"]
        )
    f = draft.filters_schema(v)
    assert f["additionalProperties"] is False and f["required"] == list(f["properties"])
    assert draft.style_counts(62) == {
        "keyword": 13,
        "descriptive": 13,
        "need": 12,
        "constraint": 12,
        "negative": 12,
    }


def test_numeric_bounds():
    f = Filters(max_height_m=1.0, min_width_m=0.4)
    assert draft.satisfies_bounds(f, (0.5, 0.9, 0.3))
    assert not draft.satisfies_bounds(f, (0.5, 1.1, 0.3))
    assert not draft.satisfies_bounds(f, (0.3, 0.9, 0.3))
