"""Search metrics against the judged set.

A system under test is any function Query -> ranked asset ids (best first). Run it on
QuerySet.resolved() so the pack restriction is in the filters. Grades come from io.load_qrels
(model judgments, human overrides on top). Assets nobody judged count as grade 0, and the
report says how many results that was: a high share means the pool needs extending
(unjudged_pool() lists what to judge next).

Variant collapsing: pass group_of (asset id -> variant group) and everything is scored on
groups. A group's grade is the best grade of any judged member, a result list is reduced to its
first member per group, and the ideal ranking is built from groups. A system that collapses
variants and one that does not are then scored on the same footing.
"""

from __future__ import annotations

import math
from collections.abc import Callable, Iterable
from typing import Literal

from pydantic import BaseModel

from .io import Qrels
from .models import ACCEPTABLE, GOOD, STYLES, Query, QuerySet

RankFn = Callable[[Query], list[str]]
Gain = Literal["linear", "exponential"]


class QueryScore(BaseModel):
    query_id: str
    style: str
    returned: int  # results the system returned, before cutting at k
    relevant: int  # judged right answers (grade >= GOOD) known for the query
    ndcg: float | None = None  # None: undefined (no judged asset has a grade above 0)
    recall: float | None = None  # None: no right answer is known
    rr: float | None = None
    precision5: float | None = None  # None for negative queries
    abstained: bool = False
    unjudged: int = 0  # of the top-k results, how many nobody judged
    topk: int = 0  # how many results were in the top k
    unjudged_ids: list[str] = []


class StyleSummary(BaseModel):
    queries: int
    ndcg: float | None
    recall: float | None
    mrr: float | None
    precision5: float | None
    abstain_rate: float | None  # negative queries only: nothing returned, which is right
    false_abstain_rate: float | None  # queries with a known right answer: nothing returned
    unjudged_share: float | None  # unjudged results over all top-k results


class Report(BaseModel):
    k: int
    gain: str
    grouped: bool
    per_query: list[QueryScore]
    per_style: dict[str, StyleSummary]
    overall: StyleSummary


def gain_of(grade: int, gain: Gain) -> float:
    return float(grade) if gain == "linear" else float(2**grade - 1)


def dcg(grades: list[int], gain: Gain) -> float:
    return sum(gain_of(g, gain) / math.log2(i + 2) for i, g in enumerate(grades))


def collapse(ranked: list[str], group_of: dict[str, str] | None) -> list[str]:
    """Keys of the ranking: the asset ids, or first-seen variant groups when group_of is given."""
    if group_of is None:
        return list(dict.fromkeys(ranked))
    return list(dict.fromkeys(group_of.get(a, a) for a in ranked))


def group_grades(grades: dict[str, int], group_of: dict[str, str] | None) -> dict[str, int]:
    if group_of is None:
        return dict(grades)
    out: dict[str, int] = {}
    for a, g in grades.items():
        key = group_of.get(a, a)
        out[key] = max(out.get(key, 0), g)
    return out


def score_query(
    query: Query,
    ranked: list[str],
    grades: dict[str, int],
    *,
    k: int = 10,
    gain: Gain = "exponential",
    group_of: dict[str, str] | None = None,
) -> QueryScore:
    keys = collapse(ranked, group_of)
    judged = group_grades(grades, group_of)
    top = keys[:k]
    got = [judged.get(a, 0) for a in top]
    right = {a for a, g in judged.items() if g >= GOOD}
    score = QueryScore(query_id=query.id, style=query.style, returned=len(keys),
                       relevant=len(right), abstained=not keys, topk=len(top))  # fmt: skip
    score.unjudged_ids = [a for a in top if a not in judged]
    score.unjudged = len(score.unjudged_ids)
    if query.style == "negative":
        return score
    ideal = dcg(sorted(judged.values(), reverse=True)[:k], gain)
    if ideal > 0:
        score.ndcg = dcg(got, gain) / ideal
    if right:
        score.recall = len(right & set(top)) / len(right)
        first = next((i for i, a in enumerate(keys, 1) if judged.get(a, 0) >= GOOD), None)
        score.rr = 1 / first if first else 0.0
    score.precision5 = sum(g >= ACCEPTABLE for g in got[:5]) / 5
    return score


def _mean(values: Iterable[float | None]) -> float | None:
    vals = [v for v in values if v is not None]
    return sum(vals) / len(vals) if vals else None


def summarize(scores: list[QueryScore]) -> StyleSummary:
    neg = [s for s in scores if s.style == "negative"]
    pos = [s for s in scores if s.style != "negative" and s.relevant > 0]
    topk = sum(s.topk for s in scores)
    return StyleSummary(
        queries=len(scores),
        ndcg=_mean(s.ndcg for s in scores),
        recall=_mean(s.recall for s in scores),
        mrr=_mean(s.rr for s in scores),
        precision5=_mean(s.precision5 for s in scores),
        abstain_rate=_mean(float(s.abstained) for s in neg),
        false_abstain_rate=_mean(float(s.abstained) for s in pos),
        unjudged_share=sum(s.unjudged for s in scores) / topk if topk else None,
    )


def evaluate(
    qs: QuerySet,
    rank: RankFn,
    qrels: Qrels,
    *,
    k: int = 10,
    gain: Gain = "exponential",
    group_of: dict[str, str] | None = None,
    queries: list[Query] | None = None,
) -> Report:
    """Run rank on every query (the set's resolved ones unless given) and score it."""
    scores = [
        score_query(q, rank(q), qrels.get(q.id, {}), k=k, gain=gain, group_of=group_of)
        for q in (queries if queries is not None else qs.resolved())
    ]
    per_style = {s: summarize([x for x in scores if x.style == s]) for s in STYLES
                 if any(x.style == s for x in scores)}  # fmt: skip
    return Report(k=k, gain=gain, grouped=group_of is not None, per_query=scores,
                  per_style=per_style, overall=summarize(scores))  # fmt: skip


def unjudged_pool(report: Report) -> dict[str, list[str]]:
    """The top-k results nobody judged, per query: feed to Judge.judge_pool to extend the pool."""
    return {s.query_id: s.unjudged_ids for s in report.per_query if s.unjudged_ids}


def _fmt(v: float | None) -> str:
    return "  -  " if v is None else f"{v:.3f}"


def format_report(r: Report) -> str:
    head = (f"{'style':12}{'n':>4}{'nDCG@' + str(r.k):>10}{'R@' + str(r.k):>8}{'MRR':>8}"
            f"{'P@5':>8}{'abstain':>9}{'f.abst':>8}{'unjudged':>10}")  # fmt: skip
    rows = [head]
    for name, s in [*r.per_style.items(), ("ALL", r.overall)]:
        rows.append(
            f"{name:12}{s.queries:>4}{_fmt(s.ndcg):>10}{_fmt(s.recall):>8}{_fmt(s.mrr):>8}"
            f"{_fmt(s.precision5):>8}{_fmt(s.abstain_rate):>9}{_fmt(s.false_abstain_rate):>8}"
            f"{_fmt(s.unjudged_share):>10}"
        )
    note = f"gain={r.gain}, scored on {'variant groups' if r.grouped else 'assets'}"
    return "\n".join(rows) + "\n" + note
