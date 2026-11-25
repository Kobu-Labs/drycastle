"""The catalogue's search service: one surface for the web page and, later, an MCP tool.

No HTTP in here. A Service holds one index and one set of loaded retrievers; the engines for
different stage combinations (which retrievers, query understanding on or off, rerank on or
off) are cheap views over those, so toggling a stage never reloads a model.
"""

from __future__ import annotations

import threading
import time
from collections.abc import Callable
from dataclasses import replace
from pathlib import Path

from pydantic import BaseModel

from . import config, vocab
from .index import FACET_NAMES, Index, open_index
from .record import CatalogRecord
from .search import engine, vector
from .search.engine import Engine, Params, Progress, Response, Result
from .search.parse import Parsed
from .search.types import Filters, Retriever
from .stages.measure import SIZE_BANDS

RETRIEVER_NAMES = engine.RETRIEVERS
# facet name (as the index and the vocabulary call it) -> the Filters field holding its terms
FILTER_FIELD = {
    "kind": "kinds", "category": "categories", "material": "materials", "setting": "settings",
    "placement": "placements", "function": "functions", "style": "styles",
    "condition": "condition", "colour": "colours", "pack": "packs", "size_class": "size_classes",
}  # fmt: skip
assert set(FILTER_FIELD) == set(FACET_NAMES)
BROWSE_LIMIT = 200
KEY_MISSING = (
    "ANTHROPIC_API_KEY is not set and not found in .env: query understanding and rerank were "
    "skipped. Export the key or put it in .env, then restart `catalog serve`."
)


class SearchResponse(Response):
    """A Response that also says how many assets passed the required constraints, so "nothing
    matched these filters" can be told apart from "nothing good enough was found"."""

    eligible: int = 0


class Similar(BaseModel):
    asset_id: str
    results: list[Result]
    timings: dict[str, float]


class Page(BaseModel):
    """Assets passing the filters, variant groups folded into one entry each, in id order."""

    total: int  # entries after folding
    assets: int  # assets passing the filters, before folding
    offset: int
    limit: int
    results: list[Result]


class Facets(BaseModel):
    total: int  # assets passing all the filters
    counts: dict[str, dict[str, int]]  # facet -> term -> assets
    # A facet's own selection does not narrow its own counts: they show what ticking another
    # term of that facet would add.


class AssetDetail(BaseModel):
    record: CatalogRecord
    siblings: list[CatalogRecord]


class Status(BaseModel):
    model: str
    assets: int
    retrievers: list[str]
    parse_model: str | None
    rerank_model: str | None
    llm_problem: str | None
    warmup_ms: dict[str, float]


class Service:
    def __init__(
        self,
        index: Index,
        retrievers: dict[str, Retriever],
        describer: str,
        params: Params | None = None,
        similar_fn: Callable[..., list] = vector.similar,
        data_dir: Path | None = None,
    ):
        self.index, self.retrievers, self.describer = index, retrievers, describer
        self.params = params or Params()
        self._similar = similar_fn
        self.data_dir = (data_dir or config.load().data_dir).resolve()
        self._engines: dict[tuple, Engine] = {}
        self._lock = threading.Lock()
        self.warmup_ms: dict[str, float] = {}

    @classmethod
    def open(cls, describer: str, params: Params | None = None) -> Service:
        index = open_index(describer)
        return cls(index, engine.build_retrievers(describer, index), describer, params)

    def warm(self) -> dict[str, float]:
        """Load every model and index now, with one real query each, so the first search is
        as fast as the second. Returns milliseconds per retriever."""
        for name, r in self.retrievers.items():
            start = time.perf_counter()
            r.retrieve("warm up", Filters(), 1)
            self.warmup_ms[name] = (time.perf_counter() - start) * 1000
        return self.warmup_ms

    # ------------------------------------------------------------------ search

    def _engine(self, use: tuple[str, ...], parse: bool, rerank: bool) -> Engine:
        key = (use, parse, rerank)
        with self._lock:
            if key not in self._engines:
                params = replace(
                    self.params,
                    parse_model=self.params.parse_model if parse else None,
                    rerank_model=self.params.rerank_model if rerank else None,
                )
                self._engines[key] = Engine(self.index, [self.retrievers[n] for n in use], params)
            return self._engines[key]

    def llm_problem(self) -> str | None:
        """Why the model-backed stages cannot run, or None."""
        try:
            config.api_key()
        except RuntimeError:
            return KEY_MISSING
        return None

    def search(
        self,
        query: str,
        filters: Filters | None = None,
        k: int = 30,
        use: list[str] | None = None,
        parse: bool = True,
        rerank: bool = True,
        understanding: Parsed | None = None,
        progress: Progress | None = None,
    ) -> SearchResponse:
        """The full search with the chosen stages. understanding: use this reading of the
        query (an earlier one, edited) instead of asking the model again."""
        query = query.strip()
        if not query:
            raise ValueError("The query is empty. Type something to search for, or use browse().")
        names = self._retrievers(use)
        problem = self.llm_problem()
        if problem:
            parse = rerank = False
        eng = self._engine(names, parse and understanding is None, rerank)
        resp = eng.search(query, filters, k, parsed=understanding, progress=progress)
        if problem and (self.params.parse_model or self.params.rerank_model):
            resp.notes.insert(0, problem)
        return SearchResponse(**resp.model_dump(), eligible=len(self.index.filter_ids(resp.hard)))

    def _retrievers(self, use: list[str] | None) -> tuple[str, ...]:
        wanted = RETRIEVER_NAMES if use is None else tuple(use)
        unknown = set(wanted) - set(self.retrievers)
        if unknown:
            raise ValueError(
                f"Unknown retriever {sorted(unknown)}; known: {list(self.retrievers)}."
            )
        names = tuple(n for n in RETRIEVER_NAMES if n in wanted)
        if not names:
            raise ValueError("Pick at least one retriever; with none there is nothing to search.")
        return names

    def similar(self, asset_id: str, k: int = 30, filters: Filters | None = None) -> Similar:
        """More like this: by pictures and descriptions, never the asset or its own variants."""
        start = time.perf_counter()
        self.index.get(asset_id)  # KeyError for an unknown asset
        own = [asset_id, *self.index.siblings(asset_id)]
        base = filters or Filters()
        wide = base.model_copy(update={"exclude_ids": [*base.exclude_ids, *own]})
        try:
            hits = self._similar(
                asset_id, k * 3, self.describer, wide, allowed_ids=self.index.filter_ids
            )
        except ValueError as e:  # the asset has no vectors yet
            raise ValueError(
                f"{asset_id} has no embeddings yet: run `catalog embed --model {self.describer}`."
            ) from e
        results = self._engine(RETRIEVER_NAMES[:1], False, False).results(hits, {}, k)
        return Similar(
            asset_id=asset_id,
            results=results,
            timings={"total": (time.perf_counter() - start) * 1000},
        )

    # ------------------------------------------------------------------ browsing

    def facets(self, filters: Filters | None = None) -> Facets:
        filters = filters or Filters()
        counts = self.index.facet_counts(filters)
        for facet, field in FILTER_FIELD.items():
            if getattr(filters, field):
                widened = filters.model_copy(update={field: []})
                counts[facet] = self.index.facet_counts(widened)[facet]
        return Facets(total=len(self.index.filter_ids(filters)), counts=counts)

    def browse(self, filters: Filters | None = None, offset: int = 0, limit: int = 48) -> Page:
        allowed = self.index.filter_ids(filters or Filters())
        groups = self.index.variant_groups()
        group_of = {a: g for g, members in groups.items() for a in members}
        seen: set[str] = set()
        entries: list[str] = []  # the first asset of each variant group, and every ungrouped one
        for asset_id in sorted(allowed):
            g = group_of.get(asset_id)
            if g is None or g not in seen:
                entries.append(asset_id)
                seen.add(g or "")
        limit = max(1, min(limit, BROWSE_LIMIT))
        offset = max(0, offset)
        results = [
            Result(
                record=rec,
                score=0.0,
                variants=[
                    a for a in groups.get(group_of.get(rec.id), []) if a != rec.id and a in allowed
                ],
            )
            for rec in self.index.get_many(entries[offset : offset + limit])
        ]
        return Page(
            total=len(entries), assets=len(allowed), offset=offset, limit=limit, results=results
        )

    def asset(self, asset_id: str) -> AssetDetail:
        """The record and its variant siblings; KeyError for an unknown id."""
        return AssetDetail(
            record=self.index.get(asset_id),
            siblings=self.index.get_many(self.index.siblings(asset_id)),
        )

    def image_path(self, asset_id: str, view: str) -> Path:
        """The picture file of a record. The path comes from the record and is checked to sit
        inside the data dir; nothing the caller types is joined into a path. KeyError for an
        unknown asset or view, FileNotFoundError when the file is missing."""
        rec = self.index.get(asset_id)
        rel = rec.contact_sheet if view == "contact_sheet" else rec.views.get(view)
        if not rel:
            raise KeyError(view)
        path = (self.data_dir / rel).resolve()
        if not path.is_relative_to(self.data_dir):
            raise KeyError(view)
        if not path.is_file():
            raise FileNotFoundError(path.name)
        return path

    # ------------------------------------------------------------------ about

    def vocabulary(self) -> dict[str, dict[str, str]]:
        """Definitions for tooltips: every facet's terms, the category paths, size classes."""
        v = vocab.load()
        lows = [0.0, *(upper for upper, _ in SIZE_BANDS[:-1])]
        size = {
            name: f"longest side {lo:g} to {hi:g} m"
            for lo, (hi, name) in zip(lows, SIZE_BANDS, strict=True)
        }
        size["terrain"] = f"longest side over {SIZE_BANDS[-1][0]:g} m"
        return {**v.facets, "category": v.categories, "size_class": size}

    def status(self) -> Status:
        return Status(
            model=self.describer,
            assets=self.index.size,
            retrievers=list(self.retrievers),
            parse_model=self.params.parse_model,
            rerank_model=self.params.rerank_model,
            llm_problem=self.llm_problem(),
            warmup_ms=self.warmup_ms,
        )
