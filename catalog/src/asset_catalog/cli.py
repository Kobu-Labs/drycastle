"""catalog <command>. Each stage registers its own subcommand in COMMANDS."""

from __future__ import annotations

import argparse
import collections

from . import index, pilot_report
from .embedding import sanity
from .evalset import commands as evalset_commands
from .sample import pilot_sample
from .search import keyword, vector
from .stages import describe, describe_pilot, embed, ingest, measure, render


def _ingest(args: argparse.Namespace) -> None:
    assets = ingest.scan()
    path = ingest.write_index(assets)
    by = collections.Counter((a.source, a.media) for a in assets)
    for (source, media), n in sorted(by.items()):
        print(f"{source:14} {media:6} {n}")
    groups = {a.variant_group for a in assets if a.variant_group}
    print(f"{len(assets)} assets, {len(groups)} variant groups -> {path}")


def _measure(args: argparse.Namespace) -> None:
    assets = [a for a in ingest.read_index() if a.media == "model"]
    if args.only:
        assets = [a for a in assets if args.only in a.id]
    if args.limit:
        assets = assets[: args.limit]
    print(measure.run(assets, workers=args.workers, force=args.force))


def _render(args: argparse.Namespace) -> None:
    assets = [a for a in ingest.read_index() if a.media == "model"]
    if args.only:
        assets = [a for a in assets if args.only in a.id]
    if args.limit:
        assets = assets[: args.limit]
    print(render.run(assets, workers=args.workers, force=args.force))


def _describe(args: argparse.Namespace) -> None:
    everything = ingest.read_index()
    assets = [a for a in everything if describe.describable(a)]
    if args.only:
        assets = [a for a in assets if args.only in a.id]
    if args.limit:
        assets = assets[: args.limit]
    print(describe.run(assets, everything, args.model, args.effort, args.threads, args.force))


def _eval_run(args: argparse.Namespace) -> None:
    from .evalset import run

    run.cli(args)


def _search(args: argparse.Namespace) -> None:
    from .search import engine
    from .search.types import Filters

    params = engine.Params()
    if args.no_parse:
        params.parse_model = None
    if args.no_rerank:
        params.rerank_model = None
    use = tuple(args.use.split(",")) if args.use else engine.RETRIEVERS
    eng = engine.open_engine(args.model, params, use)
    filters = Filters(packs=[args.pack] if args.pack else [])
    print(engine.format_response(eng.search(args.query, filters, args.k), args.verbose))


def _serve(args: argparse.Namespace) -> None:
    from . import server

    server.serve(args.model, args.port)


def _pilot(args: argparse.Namespace) -> None:
    everything = ingest.read_index()
    assets = pilot_sample(everything, n=args.n)
    if args.limit:
        # every k-th, so a small run still spans the packs
        assets = assets[:: max(1, len(assets) // args.limit)][: args.limit]
    print(
        describe_pilot.run(
            assets, everything, args.model, args.run, args.effort, args.threads, args.force
        )
    )


def main() -> None:
    parser = argparse.ArgumentParser(prog="catalog")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("ingest", help="scan source roots, write assets.jsonl").set_defaults(fn=_ingest)
    p = sub.add_parser("measure", help="geometry facts: size, pivot, flat sides, rig")
    p.add_argument("--only", help="asset id substring")
    p.add_argument("--limit", type=int, help="first N assets")
    p.add_argument("--workers", type=int, default=1)
    p.add_argument("--force", action="store_true", help="ignore the cache")
    p.set_defaults(fn=_measure)
    p = sub.add_parser("render", help="four views + contact sheet per model, dominant colours")
    p.add_argument("--only", help="asset id substring")
    p.add_argument("--limit", type=int, help="first N assets")
    p.add_argument("--workers", type=int, default=1)
    p.add_argument("--force", action="store_true", help="ignore the cache")
    p.set_defaults(fn=_render)
    p = sub.add_parser("describe", help="write records, constrained to the vocabularies")
    p.add_argument("--model", required=True)
    p.add_argument("--effort", default="high")
    p.add_argument("--only", help="asset id substring")
    p.add_argument("--limit", type=int, help="first N assets")
    p.add_argument("--threads", type=int, default=4)
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=_describe)
    p = sub.add_parser("eval-run", help="score search setups against a query set")
    p.add_argument("--set", required=True)
    p.add_argument("--model", default="claude-sonnet-5-5", help="describing model of the records")
    p.add_argument("--no-judge", action="store_true", help="do not judge new results")
    p.set_defaults(fn=_eval_run)
    p = sub.add_parser("search", help="the full search: parse, retrieve, fuse, rerank")
    p.add_argument("query")
    p.add_argument("--model", default="claude-sonnet-5-5", help="describing model of the records")
    p.add_argument("-k", type=int, default=10)
    p.add_argument("--pack")
    p.add_argument("--use", help="comma list: keyword,text-embedding,image-embedding")
    p.add_argument("--no-parse", action="store_true")
    p.add_argument("--no-rerank", action="store_true")
    p.add_argument("-v", "--verbose", action="store_true", help="show why each result matched")
    p.set_defaults(fn=_search)
    p = sub.add_parser("serve", help="the search page and JSON API on 127.0.0.1")
    p.add_argument("--model", default="claude-sonnet-5-5", help="describing model of the records")
    p.add_argument("--port", type=int, default=8765)
    p.set_defaults(fn=_serve)
    p = sub.add_parser("pilot", help="open-vocabulary describe on the pilot sample")
    p.add_argument("--model", required=True)
    p.add_argument("--run", type=int, default=1, help="run number, for repeat agreement")
    p.add_argument("--effort", default="medium")
    p.add_argument("--n", type=int, default=300, help="pilot sample size")
    p.add_argument("--limit", type=int, help="only N of the sample, spread evenly")
    p.add_argument("--threads", type=int, default=8)
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=_pilot)
    p = sub.add_parser("pilot-report", help="compare the pilot models: agreement, judging, HTML")
    p.add_argument("--judge", default=pilot_report.JUDGE)
    p.add_argument("--seed", type=int, default=1, help="shuffle seed for the blind A/B/C order")
    p.add_argument("--threads", type=int, default=4)
    p.add_argument("--force", action="store_true", help="redo the judge calls")
    p.set_defaults(fn=pilot_report.cli)
    p = sub.add_parser("index", help="build the search index and JSONL records")
    p.add_argument("--model", required=True, help="describing model whose records to index")
    p.set_defaults(fn=index.cli)
    p = sub.add_parser("keyword", help="keyword search, for trying queries")
    p.add_argument("query")
    p.add_argument("--model", required=True)
    p.add_argument("-k", type=int, default=10)
    p.add_argument("--pack", action="append")
    p.add_argument("--category", action="append")
    p.set_defaults(fn=keyword.cli)
    p = sub.add_parser("embed", help="text and picture vectors for the records")
    p.add_argument("--model", required=True, help="describing model whose records to embed")
    p.add_argument("--only", help="asset id substring")
    p.add_argument("--limit", type=int, help="first N records")
    p.add_argument("--part", choices=["all", "text", "image"], default="all")
    p.add_argument("--force", action="store_true", help="re-embed even what is cached")
    p.set_defaults(fn=embed.cli)
    p = sub.add_parser("embed-sanity", help="recall of each asset for its own likely queries")
    p.add_argument("--model", required=True, help="describing model whose records to check")
    p.set_defaults(fn=sanity.cli)
    p = sub.add_parser("vector", help="text and picture vector search, for trying queries")
    p.add_argument("query")
    p.add_argument("--model", required=True)
    p.add_argument("-k", type=int, default=10)
    p.set_defaults(fn=vector.cli)
    evalset_commands.add_commands(sub)
    args = parser.parse_args()
    args.fn(args)
