"""Looking at a judged set: grade distribution and the queries a human should check first."""

from __future__ import annotations

from collections import Counter

from .io import Qrels
from .models import GOOD, QuerySet


def grade_distribution(qrels: Qrels) -> Counter[int]:
    return Counter(g for grades in qrels.values() for g in grades.values())


def suspects(qs: QuerySet, qrels: Qrels) -> dict[str, list[str]]:
    """Queries whose judgments contradict their intent; each is a draft mistake or a judge one."""
    out: dict[str, list[str]] = {"negative_with_good_answer": [], "no_good_answer": [],
                                 "unjudged": []}  # fmt: skip
    for q in qs.queries:
        grades = qrels.get(q.id, {})
        best = max(grades.values(), default=None)
        if best is None:
            out["unjudged"].append(q.id)
        elif q.style == "negative" and best >= GOOD:
            out["negative_with_good_answer"].append(q.id)
        elif q.style != "negative" and best < GOOD:
            out["no_good_answer"].append(q.id)
    return out


def format_summary(qs: QuerySet, qrels: Qrels) -> str:
    by_style = Counter(q.style for q in qs.queries)
    dist = grade_distribution(qrels)
    total = sum(dist.values())
    lines = [f"set {qs.name!r} (pack {qs.pack}): {len(qs.queries)} queries"]
    lines += [f"  {s:12} {n}" for s, n in by_style.items()]
    lines.append(f"judged pairs: {total}")
    lines += [f"  grade {g}: {dist[g]:4}  ({dist[g] / total:.0%})" for g in range(4) if total]
    for name, ids in suspects(qs, qrels).items():
        lines.append(f"{name}: {', '.join(ids) or 'none'}")
    return "\n".join(lines)
