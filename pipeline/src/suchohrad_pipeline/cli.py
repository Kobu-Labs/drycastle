"""Command-line interface for the map pipeline."""

import argparse
from collections.abc import Sequence

from suchohrad_pipeline.stages import STAGES

RUN_ALL = "run-all"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="suchohrad_pipeline",
        description="Generate the Suchohrad base map from source data.",
    )
    choices = [*STAGES, RUN_ALL]
    parser.add_argument(
        "stage",
        choices=choices,
        metavar="stage",
        help="one of: " + ", ".join(choices),
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="fetch: ignore the disk cache and download everything again",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.stage == RUN_ALL:
        for run in STAGES.values():
            status = run(force=args.force)
            if status != 0:
                return status
        return 0
    return STAGES[args.stage](force=args.force)
