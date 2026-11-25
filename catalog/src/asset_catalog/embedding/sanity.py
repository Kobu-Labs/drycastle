"""Sanity check without an evaluation set: do assets come back for their own words?

Text: each likely query as the query, the source asset must rank high. Optimistic when
likely-query vectors are indexed (the query finds itself), so it is also run without them and
with only the query's own vector held out. Pictures: plain_name as the query against the views.
Variants of one design are one answer (the search collapses them), so recall is shown for the
exact asset and for its variant group.
"""

from __future__ import annotations

import argparse

import numpy as np

from .. import config, record
from ..config import Embeddings
from ..search import vector
from ..stages import ingest
from .models import image_embedder, text_embedder
from .space import IMAGE, TEXT

CUTOFFS = (1, 5, 10)


def asset_scores(index: vector.VectorIndex, queries: np.ndarray, row_ok: np.ndarray) -> np.ndarray:
    """(queries, assets) best cosine per asset over the rows allowed by row_ok (queries, rows)."""
    sims = np.where(row_ok, queries @ index.matrix.T, -np.inf)
    return np.maximum.reduceat(sims, index.starts, axis=1)


def ranks(scores: np.ndarray, source: np.ndarray, groups: np.ndarray | None = None) -> np.ndarray:
    """1-based rank of each query's source asset; ties count against it.

    With groups (one id per asset), assets of a group are one candidate scoring their best.
    """
    if groups is None:
        own = scores[np.arange(len(source)), source][:, None]
        return 1 + (scores > own).sum(axis=1)
    out = np.empty(len(source), int)
    for n, s in enumerate(source):
        best = {}
        for g, v in zip(groups, scores[n], strict=True):
            best[g] = max(v, best.get(g, -np.inf))
        out[n] = 1 + sum(v > best[groups[s]] for v in best.values())
    return out


def recall(rank: np.ndarray) -> dict[int, float]:
    return {k: float((rank <= k).mean()) for k in CUTOFFS}


def fmt(name: str, rk: np.ndarray, grouped: np.ndarray) -> str:
    cells = lambda r: " ".join(f"{v:5.3f}" for v in recall(r).values())
    return f"{name:34} n={len(rk):4}  exact {cells(rk)}   variants-as-one {cells(grouped)}"


def _by_id(describer: str) -> dict[str, record.CatalogRecord]:
    return {r.id: r for r in record.load_all(ingest.read_index(), describer)}


def text_report(describer: str, cfg: Embeddings) -> list[str]:
    byid = _by_id(describer)
    index = vector.load_index(TEXT, describer, cfg)
    groups = np.array([byid[a].variant_group or a for a in index.ids])
    rows = [
        (r, i)
        for i, (r, k) in enumerate(zip(index.labels, index.kinds, strict=True))
        if k == "likely_query"
    ]
    owner = np.searchsorted(index.starts, [i for _, i in rows], side="right") - 1
    q = text_embedder(cfg.text).queries([lab for lab, _ in rows])  # as a searcher would ask
    src = owner
    kinds = index.kinds
    out = [
        f"text vectors {index.matrix.shape}, {len(q)} likely queries over {len(index.ids)} assets"
    ]
    ok_all = np.ones((len(q), len(kinds)), bool)
    ok_doc = np.broadcast_to(kinds != "likely_query", (len(q), len(kinds)))
    ok_loo = ok_all.copy()
    ok_loo[np.arange(len(q)), [i for _, i in rows]] = False
    words = np.array([lab.lower() for lab in index.labels])
    ok_words = words[None, :] != np.array([lab.lower() for lab, _ in rows])[:, None]
    for name, ok in (
        ("all vectors (optimistic)", ok_all),
        ("descriptions + usage only", ok_doc),
        ("all, the query's own row held out", ok_loo),
        ("all, every row with this wording held out", ok_words),
        ("what_it_is only", np.broadcast_to(kinds == "what_it_is", ok_all.shape)),
        ("what_it_is_for only", np.broadcast_to(kinds == "what_it_is_for", ok_all.shape)),
    ):
        s = asset_scores(index, q, ok)
        out.append(fmt(name, ranks(s, src), ranks(s, src, groups)))
    return out


def image_report(describer: str, cfg: Embeddings) -> list[str]:
    byid = _by_id(describer)
    index = vector.load_index(IMAGE, describer, cfg)
    ids = index.ids
    q = image_embedder(cfg.image, "text").texts([byid[a].plain_name for a in ids])
    src = np.arange(len(ids))
    groups = np.array([byid[a].variant_group or a for a in ids])
    labels = np.array(index.labels)
    out = [f"picture vectors {index.matrix.shape}, plain_name queries over {len(ids)} assets"]
    views = sorted(set(labels))
    variants = [("all views, max", views)] + [(f"{v} only", [v]) for v in views]
    for name, vs in variants:
        ok = np.broadcast_to(np.isin(labels, vs), (len(q), len(labels)))
        s = asset_scores(index, q, ok)
        out.append(fmt(name, ranks(s, src), ranks(s, src, groups)))
    sims = q @ index.matrix.T
    mean = np.stack(
        [sims[:, a : index.end(i)].mean(axis=1) for i, a in enumerate(index.starts)], axis=1
    )
    out.append(fmt("all views, mean", ranks(mean, src), ranks(mean, src, groups)))
    return out


def cli(args: argparse.Namespace) -> None:
    cfg = config.load().embeddings
    for line in text_report(args.model, cfg) + image_report(args.model, cfg):
        print(line)
