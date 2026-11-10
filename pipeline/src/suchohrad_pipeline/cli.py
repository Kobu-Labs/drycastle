"""Command-line interface for the map pipeline."""

import argparse
from collections.abc import Sequence

from suchohrad_pipeline import preview
from suchohrad_pipeline.config import AREAS, DEFAULT_AREA
from suchohrad_pipeline.stages import STAGES

RUN_ALL = "run-all"
PREVIEW = "preview"  # not a map stage: renders PNGs from the work files


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="suchohrad_pipeline",
        description="Generate the Suchohrad base map from source data.",
    )
    choices = [*STAGES, RUN_ALL, PREVIEW]
    parser.add_argument(
        "stage",
        choices=choices,
        metavar="stage",
        help="one of: " + ", ".join(choices),
    )
    parser.add_argument(
        "--area",
        choices=AREAS,
        default=DEFAULT_AREA,
        help="slice (default): data/raw, data/work, out/map; "
        "village: data/village/..., out/village/map",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="fetch: ignore the disk cache and download everything again",
    )
    parser.add_argument(
        "--edits",
        metavar="PATH",
        default=None,
        help="props: hand edits file (default: pipeline/config/edits.json)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="parcels: print counts by kind, building assignment and registry changes",
    )
    parser.add_argument(
        "--exaggeration",
        type=float,
        default=preview.DEFAULT_EXAGGERATION,
        help="preview: multiply terrain heights by this factor in the isometric images",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.stage == RUN_ALL:
        for run in STAGES.values():
            status = run(force=args.force, report=args.report, area=args.area, edits=args.edits)
            if status != 0:
                return status
        return 0
    if args.stage == PREVIEW:
        return preview.run(exaggeration=args.exaggeration, area=args.area)
    return STAGES[args.stage](
        force=args.force, report=args.report, area=args.area, edits=args.edits
    )
