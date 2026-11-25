"""Vector retrievers: a query embedded by a local model, scored against every asset's vectors.

An asset has several vectors (descriptions, likely queries, picture views); its score is the
best of them, and Hit.why names that vector. Brute-force cosine in numpy: a few thousand rows.
Models load once per process, on the first query.
"""

from __future__ import annotations

import argparse
import threading
from collections import OrderedDict
from collections.abc import Callable
from dataclasses import dataclass
from functools import cache

import numpy as np

from .. import config
from ..config import Embeddings
from ..embedding import models
from ..embedding.space import IMAGE, TEXT, Row, Space
from ..stages import embed
from .types import Filters, Hit

# Filters -> the ids that pass them (None: no restriction). Supplied by the SQLite index.
AllowedIds = Callable[[Filters], set[str] | None]
_NO_ASSET_FILTER = Filters()


@dataclass
class VectorIndex:
    """The rows of one embedding space for one describer, grouped by asset."""

    ids: list[str]  # asset ids, one per asset
    starts: np.ndarray  # first row of each asset
    kinds: np.ndarray  # per row
    labels: list[str]  # per row
    matrix: np.ndarray  # (rows, dim), unit length

    @classmethod
    def build(cls, rows: list[Row], matrix: np.ndarray) -> VectorIndex:
        order = sorted(range(len(rows)), key=lambda i: rows[i].id)  # stable: keeps row order
        rows = [rows[i] for i in order]
        ids = list(dict.fromkeys(r.id for r in rows))
        first = {i: n for n, i in reversed(list(enumerate(r.id for r in rows)))}
        return cls(
            ids=ids,
            starts=np.array([first[i] for i in ids], dtype=np.intp),
            kinds=np.array([r.kind for r in rows]),
            labels=[r.label for r in rows],
            matrix=matrix[order],
        )

    @classmethod
    def load(cls, space: Space, describer: str) -> VectorIndex:
        index = space.read_index(describer)
        if index is None or not index.rows:
            raise FileNotFoundError(
                f"no embeddings for {describer!r} in {space.dir}: run catalog embed"
            )
        return cls.build(index.rows, space.matrix([r.key for r in index.rows]))

    def end(self, i: int) -> int:
        return int(self.starts[i + 1]) if i + 1 < len(self.ids) else len(self.kinds)

    def rank(
        self,
        q: np.ndarray,
        k: int,
        allowed: set[str] | None = None,
        kinds: frozenset[str] | None = None,
        bias: dict[str, float] | None = None,
    ) -> list[tuple[str, int, float]]:
        """Best k (asset id, best row, score): the asset's score is its best row's cosine."""
        sims = self.matrix @ q
        for kind, b in (bias or {}).items():
            sims[self.kinds == kind] += b
        if kinds is not None:
            sims[~np.isin(self.kinds, list(kinds))] = -np.inf
        best = np.maximum.reduceat(sims, self.starts)
        if allowed is not None:
            best[~np.fromiter((i in allowed for i in self.ids), bool, len(self.ids))] = -np.inf
        k = min(k, int(np.isfinite(best).sum()))
        if k <= 0:
            return []
        top = np.argpartition(-best, k - 1)[:k]
        top = top[np.argsort(-best[top], kind="stable")]
        return [
            (
                self.ids[i],
                int(self.starts[i]) + int(np.argmax(sims[self.starts[i] : self.end(i)])),
                float(best[i]),
            )
            for i in top
        ]


def why_text(kind: str, label: str) -> str:
    if kind == "what_it_is":
        return "description text"
    if kind == "what_it_is_for":
        return "usage text"
    if kind == "likely_query":
        return f"likely query: {label}"
    return label.replace("_", "-") + " view"


class VectorRetriever:
    """Scores a query vector against an index under the filters; the model is a callable."""

    def __init__(
        self,
        name: str,
        load_index: Callable[[], VectorIndex],
        encode: Callable[[str], np.ndarray],
        allowed_ids: AllowedIds | None = None,
        kinds: frozenset[str] | None = None,
        kind_bias: dict[str, float] | None = None,
    ):
        self.name = name
        self._load_index, self._encode = load_index, encode
        self.allowed_ids, self.kinds, self.kind_bias = allowed_ids, kinds, kind_bias
        self._index: VectorIndex | None = None
        self._cache: OrderedDict[str, np.ndarray] = OrderedDict()
        self._lock = threading.Lock()

    @property
    def index(self) -> VectorIndex:
        with self._lock:
            if self._index is None:
                self._index = self._load_index()
            return self._index

    def query_vector(self, query: str) -> np.ndarray:
        with self._lock:
            if query in self._cache:
                return self._cache[query]
        v = self._encode(query)
        with self._lock:
            self._cache[query] = v
            if len(self._cache) > 256:
                self._cache.popitem(last=False)
        return v

    def allowed(self, filters: Filters) -> set[str] | None:
        """Ids that pass the filters; None when nothing constrains."""
        rest = filters.model_copy(update={"exclude_ids": []})
        ids = None
        if not rest.is_empty():
            if self.allowed_ids is None:
                raise ValueError(f"{self.name}: filters given but no allowed_ids supplier")
            ids = self.allowed_ids(filters)
        if filters.exclude_ids:
            ids = set(self.index.ids if ids is None else ids) - set(filters.exclude_ids)
        return ids

    def retrieve(self, query: str, filters: Filters, k: int) -> list[Hit]:
        if not query.strip() or k <= 0:
            return []
        allowed = self.allowed(filters)
        if allowed is not None and not allowed:
            return []
        index = self.index
        ranked = index.rank(self.query_vector(query), k, allowed, self.kinds, self.kind_bias)
        return [
            Hit(id=i, score=s, why=[why_text(index.kinds[r], index.labels[r])])
            for i, r, s in ranked
        ]


def _embeddings(cfg: Embeddings | None) -> Embeddings:
    return cfg or config.load().embeddings


@cache
def _load(modality: str, describer: str, cfg: Embeddings, stamp: float) -> VectorIndex:
    space = embed.text_space(cfg.text) if modality == TEXT else embed.image_space(cfg.image)
    return VectorIndex.load(space, describer)


def load_index(modality: str, describer: str, cfg: Embeddings | None = None) -> VectorIndex:
    """The index, reloaded when embed rewrote it."""
    cfg = _embeddings(cfg)
    space = embed.text_space(cfg.text) if modality == TEXT else embed.image_space(cfg.image)
    path = space.index_path(describer)
    return _load(modality, describer, cfg, path.stat().st_mtime if path.exists() else 0.0)


class TextVectorRetriever(VectorRetriever):
    """Query against descriptions, usage text and likely queries."""

    def __init__(
        self,
        describer: str,
        allowed_ids: AllowedIds | None = None,
        cfg: Embeddings | None = None,
        kinds: frozenset[str] | None = None,
        kind_bias: dict[str, float] | None = None,
    ):
        cfg = _embeddings(cfg)
        super().__init__(
            "text-embedding",
            lambda: load_index(TEXT, describer, cfg),
            lambda q: models.text_embedder(cfg.text).queries([q])[0],
            allowed_ids,
            kinds,
            kind_bias if kind_bias is not None else {"likely_query": cfg.text.likely_query_bias},
        )


class ImageVectorRetriever(VectorRetriever):
    """Query text against the rendered views, in the picture model's shared space."""

    def __init__(
        self,
        describer: str,
        allowed_ids: AllowedIds | None = None,
        cfg: Embeddings | None = None,
        kinds: frozenset[str] | None = None,
        kind_bias: dict[str, float] | None = None,
    ):
        cfg = _embeddings(cfg)
        super().__init__(
            "image-embedding",
            lambda: load_index(IMAGE, describer, cfg),
            lambda q: models.image_embedder(cfg.image, "text").texts([q])[0],
            allowed_ids,
            kinds,
            kind_bias,
        )


def _centroid(index: VectorIndex, asset_id: str, kinds: set[str]) -> np.ndarray | None:
    i = index.ids.index(asset_id) if asset_id in index.ids else -1
    if i < 0:
        return None
    rows = [r for r in range(int(index.starts[i]), index.end(i)) if index.kinds[r] in kinds]
    if not rows:
        return None
    v = index.matrix[rows].mean(axis=0)
    return v / np.linalg.norm(v)


def _zscores(sims: np.ndarray) -> np.ndarray:
    ok = np.isfinite(sims)
    out = np.full_like(sims, -np.inf)
    out[ok] = (sims[ok] - sims[ok].mean()) / max(float(sims[ok].std()), 1e-9)
    return out


def similar(
    asset_id: str,
    k: int,
    describer: str,
    filters: Filters | None = None,
    allowed_ids: AllowedIds | None = None,
    cfg: Embeddings | None = None,
    text_weight: float = 0.5,
    image_weight: float = 0.5,
) -> list[Hit]:
    """More like this: assets whose pictures and descriptions are closest to this one's.

    Each side is the cosine between centroids of the asset's own vectors (descriptions;
    views), standardised over the candidates so the two are comparable, then blended.
    """
    cfg = _embeddings(cfg)
    sides = [
        (
            load_index(TEXT, describer, cfg),
            {"what_it_is", "what_it_is_for"},
            text_weight,
            "description",
        ),
        (load_index(IMAGE, describer, cfg), {"view"}, image_weight, "pictures"),
    ]
    first = sides[0][0]
    filt = filters or Filters()
    probe = VectorRetriever("similar", lambda: first, lambda q: np.zeros(1), allowed_ids)
    allowed = probe.allowed(filt)
    ids = first.ids
    total = np.zeros(len(ids))
    notes: list[np.ndarray] = []
    for index, kinds, weight, _ in sides:
        c = _centroid(index, asset_id, kinds)
        if c is None or weight <= 0:
            notes.append(np.full(len(ids), np.nan))
            continue
        sims = np.full(len(ids), -np.inf)
        by_id = {a: n for n, a in enumerate(index.ids)}
        cent = np.stack(
            [_centroid(index, a, kinds) if a in by_id else np.zeros_like(c) for a in ids]
        )
        sims = cent @ c
        notes.append(sims)
        total += weight * _zscores(sims)
    keep = np.ones(len(ids), bool)
    keep[ids.index(asset_id)] = False
    if allowed is not None:
        keep &= np.fromiter((i in allowed for i in ids), bool, len(ids))
    total[~keep] = -np.inf
    top = [i for i in np.argsort(-total, kind="stable")[:k] if np.isfinite(total[i])]
    return [
        Hit(
            id=ids[i],
            score=float(total[i]),
            why=[
                f"{name} similarity {n[i]:.2f}"
                for n, (_, _, _, name) in zip(notes, sides)
                if np.isfinite(n[i])
            ],
        )
        for i in top
    ]


def cli(args: argparse.Namespace) -> None:
    filters = Filters()
    for kind, retriever in (
        ("text", TextVectorRetriever(args.model)),
        ("image", ImageVectorRetriever(args.model)),
    ):
        print(f"-- {kind}")
        for h in retriever.retrieve(args.query, filters, args.k):
            print(f"{h.score:.3f}  {h.id}  ({h.why[0]})")
