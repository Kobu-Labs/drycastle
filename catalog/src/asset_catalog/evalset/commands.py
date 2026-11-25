"""CLI: catalog eval-draft, eval-judge, eval-summary, eval-review."""

from __future__ import annotations

import argparse
import subprocess
import sys
from collections import Counter
from pathlib import Path

from ..stages import ingest
from . import draft as drafting
from . import io as evalio
from . import llm, review
from .judge import BATCH, THREADS, Judge
from .summary import format_summary


def _money(kind: str) -> str:
    t = llm.usage_totals().get(kind)
    if not t:
        return f"{kind}: no requests logged"
    return (f"{kind}: {t['requests']:.0f} requests, {t['in']:.0f} in + {t['cached']:.0f} cached in, "
            f"{t['out']:.0f} out tokens, USD {t['usd']:.2f}")  # fmt: skip


def eval_draft(args: argparse.Namespace) -> None:
    set_name = args.set or args.pack.rsplit("/", 1)[-1]
    path = args.out or evalio.queryset_path(set_name)
    if path.exists() and not args.force:
        sys.exit(f"{path} exists and may hold human edits; use --force to replace it")
    d = drafting.draft(
        args.pack, args.n, set_name, group_size=args.group_size, px=args.px, effort=args.effort
    )
    evalio.save_queryset(d.queries, path, overwrite=True)
    evalio.write_json(evalio.pool_path(set_name), d.pool)
    evalio.write_json(
        evalio.data_root() / "drafts" / f"{set_name}.{evalio.now()}.json",
        {"raw": d.raw, "issues": d.issues},
    )
    pack_ids = [a.id for a in ingest.read_index() if f"{a.source}/{a.pack}" == args.pack]
    by_style = Counter(q.style for q in d.queries.queries)
    print(f"{len(d.queries.queries)} queries {dict(by_style)} -> {path}")
    print(f"candidate pool -> {evalio.pool_path(set_name)}")
    print(f"pack coverage by non-negative candidates: "
          f"{drafting.coverage(d.queries, d.pool, pack_ids):.0%}")  # fmt: skip
    for issue in d.issues:
        print("ISSUE", issue)
    print(_money("draft"))


def eval_nominate(args: argparse.Namespace) -> None:
    qs = evalio.load_queryset(args.set)
    pool = evalio.pool_path(args.set)
    have = evalio.read_candidates(str(pool)) if pool.exists() else {}
    new = drafting.nominate(qs, effort=args.effort)
    merged = {
        q.id: list(dict.fromkeys([*have.get(q.id, []), *new.get(q.id, [])])) for q in qs.queries
    }
    evalio.write_json(pool, merged)
    print(
        f"pool: {sum(map(len, have.values()))} -> {sum(map(len, merged.values()))} pairs -> {pool}"
    )
    print(_money("nominate"))


def eval_judge(args: argparse.Namespace) -> None:
    qs = evalio.load_queryset(args.set)
    src = args.candidates or str(evalio.pool_path(args.set))
    pool = evalio.read_candidates(src)
    queries = {q.id: q for q in qs.resolved()}
    if unknown := sorted(set(pool) - set(queries)):
        sys.exit(f"candidates name unknown queries: {unknown[:5]}")
    judge = Judge(args.set, effort=args.effort, batch=args.batch, threads=args.threads, px=args.px)
    print(judge.judge_pool(queries, pool, force=args.force))
    print(format_summary(qs, evalio.load_qrels(qs)))
    print(_money("judge"))


def eval_summary(args: argparse.Namespace) -> None:
    qs = evalio.load_queryset(args.set)
    print(format_summary(qs, evalio.load_qrels(qs)))
    for kind in llm.usage_totals():
        print(_money(kind))


def eval_review(args: argparse.Namespace) -> None:
    qs = evalio.load_queryset(args.set)
    judgments = evalio.effective_judgments(qs, evalio.JudgmentStore(args.set))
    out = review.write_page(qs, judgments)
    print(out)
    if args.open:
        subprocess.run(["xdg-open", str(out)], check=False)


def add_commands(sub: argparse._SubParsersAction) -> None:
    p = sub.add_parser("eval-draft", help="draft evaluation queries for a pack with Opus")
    p.add_argument("--pack", required=True, help="<source>/<pack>, e.g. synty/office")
    p.add_argument("--n", type=int, default=60, help="total queries, split over the styles")
    p.add_argument("--set", help="set name (default: the pack's name)")
    p.add_argument("--group-size", type=int, default=drafting.GROUP_SIZE)
    p.add_argument("--px", type=int, default=llm.THUMB_PX, help="thumbnail size the model sees")
    p.add_argument("--effort", default="high")
    p.add_argument("--out", type=Path, help="query file to write (default catalog/evalset/)")
    p.add_argument("--force", action="store_true", help="replace an existing query file")
    p.set_defaults(fn=eval_draft)
    p = sub.add_parser("eval-nominate", help="widen the judging pool with a second Opus pass")
    p.add_argument("--set", required=True)
    p.add_argument("--effort", default="high")
    p.set_defaults(fn=eval_nominate)
    p = sub.add_parser("eval-judge", help="grade candidate assets per query with Opus (cached)")
    p.add_argument("--set", required=True)
    p.add_argument("--candidates", help="JSON/YAML query id -> asset ids, or - for stdin")
    p.add_argument("--batch", type=int, default=BATCH, help="candidates per request")
    p.add_argument("--threads", type=int, default=THREADS)
    p.add_argument("--px", type=int, default=llm.THUMB_PX)
    p.add_argument("--effort", default="high")
    p.add_argument("--force", action="store_true", help="rejudge pairs already judged")
    p.set_defaults(fn=eval_judge)
    p = sub.add_parser("eval-summary", help="grade distribution and queries to check")
    p.add_argument("--set", required=True)
    p.set_defaults(fn=eval_summary)
    p = sub.add_parser("eval-review", help="HTML page of queries with judged assets")
    p.add_argument("--set", required=True)
    p.add_argument("--open", action="store_true", help="open the page with xdg-open")
    p.set_defaults(fn=eval_review)
