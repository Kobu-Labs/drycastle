"""Reciprocal rank fusion: merge best-first lists whose scores are not comparable."""

from __future__ import annotations

from collections import defaultdict

from .types import Hit

RRF_K = 60  # the usual constant; larger flattens the advantage of the very top ranks


def rrf(
    lists: dict[str, list[Hit]], weights: dict[str, float] | None = None, k: int = RRF_K
) -> list[Hit]:
    """One list, best first. A hit's score is the weighted sum of 1 / (k + rank) over the lists
    that contain it, so an asset several retrievers agree on beats one a single retriever loves.
    """
    weights = weights or {}
    score: dict[str, float] = defaultdict(float)
    why: dict[str, list[str]] = defaultdict(list)
    for name, hits in lists.items():
        w = weights.get(name, 1.0)
        for rank, hit in enumerate(hits, start=1):
            score[hit.id] += w / (k + rank)
            why[hit.id].append(f"{name} #{rank}" + (f" ({'; '.join(hit.why)})" if hit.why else ""))
    order = sorted(score, key=lambda i: (-score[i], i))
    return [Hit(id=i, score=score[i], why=why[i]) for i in order]
