"""Score search configurations against a query set, widening the judged pool as needed.

A configuration is which retrievers run and whether query understanding and reranking are on.
Each is run over every query; any top result nobody has judged yet is sent to the judge, and
only then are the scores computed, so no configuration is penalised for finding something
the pool had missed.
"""

from __future__ import annotations

import argparse
import json
import sys
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, replace

from .. import config
from ..search import engine, llm
from ..search.types import Filters, Hit, Retriever
from . import io as evalio
from . import metrics
from .judge import Judge
from .models import Query

K = 10
KW, TXT, IMG = engine.RETRIEVERS


@dataclass(frozen=True)
class Setup:
    name: str
    use: tuple[str, ...]
    parse: bool = False
    rerank: bool = False


SETUPS = [
    Setup("keyword", (KW,)),
    Setup("text", (TXT,)),
    Setup("image", (IMG,)),
    Setup("keyword+text", (KW, TXT)),
    Setup("all three", (KW, TXT, IMG)),
    Setup("all three +parse", (KW, TXT, IMG), parse=True),
    Setup("all three +rerank", (KW, TXT, IMG), rerank=True),
    Setup("keyword+text +parse +rerank", (KW, TXT), parse=True, rerank=True),
    Setup("FULL: all three +parse +rerank", (KW, TXT, IMG), parse=True, rerank=True),
]


class Memo:
    """A retriever that remembers its answers: setups share retrievers, so each runs once."""

    def __init__(self, inner: Retriever):
        self.inner, self.name = inner, inner.name
        self._seen: dict[str, list[Hit]] = {}

    def retrieve(self, query: str, filters: Filters, k: int) -> list[Hit]:
        key = json.dumps([query, filters.model_dump(exclude_defaults=True), k], sort_keys=True)
        if key not in self._seen:
            self._seen[key] = self.inner.retrieve(query, filters, k)
        return self._seen[key]


def run(set_name: str, describer: str, base: engine.Params, judge: bool = True) -> str:
    qs = evalio.load_queryset(set_name)
    queries = qs.resolved()
    llm.CACHE_DIR = config.load().data_dir / "evalset" / "llm-cache"
    full = engine.open_engine(describer, base)
    shared = {r.name: Memo(r) for r in full.retrievers}
    group_of = {a: g for g, ids in full.index.variant_groups().items() for a in ids}

    rankings: dict[str, dict[str, list[str]]] = {}
    for s in SETUPS:
        params = replace(
            base,
            parse_model=base.parse_model if s.parse else None,
            rerank_model=base.rerank_model if s.rerank else None,
            collapse_variants=False,  # metrics group variants themselves
        )
        eng = engine.Engine(full.index, [shared[n] for n in s.use], params)
        # the model calls dominate; the organisation allows only a few at once
        with ThreadPoolExecutor(4 if s.parse or s.rerank else 1) as pool:
            ranked = list(pool.map(lambda q, eng=eng: _ranked(eng, q), queries))
        rankings[s.name] = {q.id: r for q, r in zip(queries, ranked, strict=True)}
        print(f"[eval] ran {s.name}", file=sys.stderr)

    if judge:
        # scores are per variant group, so judge the first asset of each of the top K groups
        pool = {
            q.id: [a for r in rankings.values() for a in _top_groups(r[q.id], group_of)]
            for q in queries
        }
        counts = Judge(set_name).judge_pool({q.id: q for q in qs.queries}, pool)
        print(f"[eval] judging: {counts}", file=sys.stderr)
    qrels = evalio.load_qrels(qs)

    out = []
    reports = {}
    for s in SETUPS:
        ranks = rankings[s.name]
        reports[s.name] = metrics.evaluate(
            qs, lambda q, ranks=ranks: ranks[q.id], qrels, k=K, group_of=group_of, queries=queries
        )
    out.append(_table(reports))
    out.append("\n== FULL, by query style\n" + metrics.format_report(reports[SETUPS[-1].name]))
    evalio.write_json(
        evalio.data_root() / "runs" / f"{set_name}--{describer}.json",
        {name: r.model_dump() for name, r in reports.items()},
    )
    return "\n".join(out)


def _top_groups(ranked: list[str], group_of: dict[str, str]) -> list[str]:
    seen: dict[str, str] = {}
    for a in ranked:
        seen.setdefault(group_of.get(a, a), a)
        if len(seen) == K:
            break
    return list(seen.values())


def _ranked(eng: engine.Engine, q: Query) -> list[str]:
    return [r.record.id for r in eng.search(q.text, q.filters, k=K * 4).results]


def _f(v: float | None) -> str:
    return "   -  " if v is None else f"{v:6.3f}"


def _table(reports: dict[str, metrics.Report]) -> str:
    head = f"{'setup':36}{'nDCG@10':>8}{'R@10':>7}{'MRR':>7}{'P@5':>7}{'abstain':>8}{'f.abst':>7}"
    rows = [head + f"{'unjudged':>9}"]
    for name, r in reports.items():
        o = r.overall
        rows.append(
            f"{name:36}{_f(o.ndcg):>8}{_f(o.recall):>7}{_f(o.mrr):>7}{_f(o.precision5):>7}"
            f"{_f(o.abstain_rate):>8}{_f(o.false_abstain_rate):>7}{_f(o.unjudged_share):>9}"
        )
    rows.append(
        "nDCG/R/MRR/P: over the 48 queries that have an answer. abstain: share of the 12 "
        "no-answer queries where nothing was returned (higher is better). f.abst: share of "
        "answerable queries where nothing was returned (lower is better)."
    )
    return "\n".join(rows)


def cli(args: argparse.Namespace) -> None:
    base = engine.Params()
    print(run(args.set, args.model, base, judge=not args.no_judge))
