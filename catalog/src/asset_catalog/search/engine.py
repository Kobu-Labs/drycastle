"""The search engine: parse, retrieve three ways, fuse, rerank, collapse variants, or abstain."""

from __future__ import annotations

import sys
import time
from collections.abc import Callable, Iterator
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from dataclasses import dataclass, field

from pydantic import BaseModel

from ..index import Index
from ..record import CatalogRecord
from . import fusion, parse, rerank
from .llm import LlmError
from .types import Filters, Hit, Retriever

# Called with the name of each stage as it starts: "parse", "retrieve", "rerank".
Progress = Callable[[str], None]

_FACETS = (
    "kinds", "categories", "materials", "settings", "placements", "functions", "styles",
    "condition",
)  # fmt: skip


@dataclass
class Params:
    """Every knob the evaluation set tunes. Defaults are the current best known setting."""

    parse_model: str | None = "claude-haiku-5-5"  # None: no query understanding
    parse_effort: str = "low"
    rerank_model: str | None = "claude-sonnet-5-5"  # None: fused order is final
    rerank_effort: str = "low"
    per_retriever: int = 40  # candidates taken from each retriever
    rerank_pool: int = 30  # fused candidates the reranker reads
    weights: dict[str, float] = field(default_factory=dict)  # retriever name -> RRF weight
    soft_weight: float = 0.5  # RRF weight of the soft-preference list
    min_grade: int = 1  # with rerank: results graded below this are dropped
    collapse_variants: bool = True


class Result(BaseModel):
    record: CatalogRecord
    grade: int | None = None  # the reranker's 0..3; None when rerank was off or skipped it
    score: float  # fused score
    why: list[str] = []
    variants: list[str] = []  # ids of sibling variants folded into this result


class Response(BaseModel):
    query: str
    text: str  # what was actually searched after parsing
    hard: Filters
    soft: Filters
    results: list[Result]
    abstained: bool  # nothing good enough was found
    notes: list[str] = []  # degradations: a stage that failed and was skipped
    # milliseconds per stage: parse, retrieve (holding the parallel per-retriever times and
    # fuse), rerank, total. A stage that did not run has no entry.
    timings: dict[str, float] = {}


def _soft_hits(index: Index, soft: Filters, allowed: set[str], k: int) -> list[Hit]:
    """Assets ranked by how many of the suggested facet terms they carry."""
    votes: dict[str, list[str]] = {}
    for name in _FACETS:
        for term in getattr(soft, name):
            for asset_id in index.filter_ids(Filters(**{name: [term]})) & allowed:
                votes.setdefault(asset_id, []).append(term)
    order = sorted(votes, key=lambda i: (-len(votes[i]), i))[:k]
    return [Hit(id=i, score=len(votes[i]), why=votes[i]) for i in order]


@contextmanager
def _timed(
    timings: dict[str, float], name: str, progress: Progress | None = None
) -> Iterator[None]:
    if progress:
        progress(name)
    start = time.perf_counter()
    try:
        yield
    finally:
        timings[name] = (time.perf_counter() - start) * 1000


def _constraints_text(f: Filters) -> str:
    return "; ".join(f"{k}={v}" for k, v in f.model_dump(exclude_defaults=True).items())


class Engine:
    def __init__(self, index: Index, retrievers: list[Retriever], params: Params | None = None):
        self.index = index
        self.retrievers = retrievers
        self.params = params or Params()

    def candidates(
        self, text: str, hard: Filters, soft: Filters, timings: dict[str, float] | None = None
    ) -> list[Hit]:
        """The fused, best-first candidate list before reranking. timings, when given, gets
        each retriever's milliseconds (they run in parallel) and "fuse"."""
        p = self.params
        timings = timings if timings is not None else {}

        def timed_retrieve(r: Retriever) -> list[Hit]:
            with _timed(timings, r.name):
                return r.retrieve(text, hard, p.per_retriever)

        with ThreadPoolExecutor(max(1, len(self.retrievers))) as pool:
            futures = {r.name: pool.submit(timed_retrieve, r) for r in self.retrievers}
            lists = {name: f.result() for name, f in futures.items()}
        weights = dict(p.weights)
        if not soft.is_empty():
            allowed = self.index.filter_ids(hard)
            lists["suggested"] = _soft_hits(self.index, soft, allowed, p.per_retriever)
            weights["suggested"] = p.soft_weight
        with _timed(timings, "fuse"):
            return fusion.rrf(lists, weights)

    def search(
        self,
        query: str,
        filters: Filters | None = None,
        k: int = 10,
        parsed: parse.Parsed | None = None,
        progress: Progress | None = None,
    ) -> Response:
        """filters: constraints the caller states outright; they add to any found in the text.
        parsed: an understanding of the query to use as it is, instead of asking the model."""
        p = self.params
        notes: list[str] = []
        timings: dict[str, float] = {}
        started = time.perf_counter()
        if parsed is None:
            parsed = parse.Parsed(text=query)
            if p.parse_model:
                try:
                    with _timed(timings, "parse", progress):
                        parsed = parse.parse(query, p.parse_model, p.parse_effort)
                except LlmError as e:
                    notes.append(f"query understanding skipped: {e}")
        hard = _merge(filters or Filters(), parsed.hard)
        with _timed(timings, "retrieve", progress):
            fused = self.candidates(parsed.text, hard, parsed.soft, timings)
        grades: dict[str, int] = {}
        if p.rerank_model and fused:
            pool = self.index.get_many([h.id for h in fused[: p.rerank_pool]])
            try:
                with _timed(timings, "rerank", progress):
                    grades = rerank.grade(
                        query, _constraints_text(hard), pool, p.rerank_model, p.rerank_effort
                    )
            except LlmError as e:
                notes.append(f"rerank skipped: {e}")
        ranked = _order(fused, grades, p.min_grade)
        results = self.results(ranked, grades, k)
        for n in notes:
            print(f"[search] {n}", file=sys.stderr)
        timings["total"] = (time.perf_counter() - started) * 1000
        return Response(
            query=query, text=parsed.text, hard=hard, soft=parsed.soft, results=results,
            abstained=not results, notes=notes, timings=timings,
        )  # fmt: skip

    def results(self, ranked: list[Hit], grades: dict[str, int], k: int) -> list[Result]:
        records = {r.id: r for r in self.index.get_many([h.id for h in ranked])}
        out: list[Result] = []
        by_group: dict[str, Result] = {}
        for h in ranked:
            rec = records.get(h.id)
            if rec is None:
                continue
            group = rec.variant_group if self.params.collapse_variants else None
            if group and group in by_group:
                by_group[group].variants.append(h.id)
                continue
            res = Result(record=rec, grade=grades.get(h.id), score=h.score, why=h.why)
            if group:
                by_group[group] = res
            out.append(res)
        return out[:k]


def _merge(a: Filters, b: Filters) -> Filters:
    """Both sets of constraints hold: lists from the caller win where both name a field."""
    merged = b.model_dump()
    for key, value in a.model_dump(exclude_defaults=True).items():
        merged[key] = value
    return Filters(**merged)


def _order(fused: list[Hit], grades: dict[str, int], min_grade: int) -> list[Hit]:
    """With grades: graded candidates by grade then fused rank, the rest dropped.
    Without grades (rerank off or failed): the fused order as it is."""
    if not grades:
        return fused
    rank = {h.id: i for i, h in enumerate(fused)}
    kept = [h for h in fused if grades.get(h.id, 0) >= min_grade]
    return sorted(kept, key=lambda h: (-grades[h.id], rank[h.id]))


RETRIEVERS = ("keyword", "text-embedding", "image-embedding")


def build_retrievers(
    describer: str, index: Index, use: tuple[str, ...] = RETRIEVERS
) -> dict[str, Retriever]:
    """The named retrievers over one index, by name. Models load on each one's first query."""
    from .keyword import KeywordRetriever
    from .vector import ImageVectorRetriever, TextVectorRetriever

    makers = {
        "keyword": lambda: KeywordRetriever(index),
        "text-embedding": lambda: TextVectorRetriever(describer, index.filter_ids),
        "image-embedding": lambda: ImageVectorRetriever(describer, index.filter_ids),
    }
    return {name: makers[name]() for name in use}


def open_engine(
    describer: str, params: Params | None = None, use: tuple[str, ...] = RETRIEVERS
) -> Engine:
    """The engine over one describing model's records, with the named retrievers."""
    from ..index import open_index

    index = open_index(describer)
    return Engine(index, list(build_retrievers(describer, index, use).values()), params)


def format_response(resp: Response, verbose: bool = False) -> str:
    lines = [f"searched: {resp.text!r}"]
    if not resp.hard.is_empty():
        lines.append(f"required: {_constraints_text(resp.hard)}")
    if not resp.soft.is_empty():
        lines.append(f"suggested: {_constraints_text(resp.soft)}")
    lines += [f"note: {n}" for n in resp.notes]
    if resp.abstained:
        lines.append("no good match")
    for n, r in enumerate(resp.results, 1):
        rec = r.record
        w, h, d = rec.size_m
        grade = "-" if r.grade is None else str(r.grade)
        lines.append(
            f"{n:2} [{grade}] {rec.plain_name}  ({rec.id})  {w:.2f}x{h:.2f}x{d:.2f} m"
            + (f"  +{len(r.variants)} variants" if r.variants else "")
        )
        if verbose:
            lines += [f"      {w_}" for w_ in r.why]
    return "\n".join(lines)
