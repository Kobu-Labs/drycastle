"""Command-line interface for the map pipeline."""

import argparse
from collections.abc import Sequence

from suchohrad_pipeline.config import AREAS, DEFAULT_AREA

# Heavy modules (matplotlib, rasterio, trimesh, ...) are imported only by the command that needs
# them, so that ``parcel-run`` starts fast. ``STAGE_NAMES`` must equal ``list(stages.STAGES)``
# (checked in tests).
STAGE_NAMES = ("fetch", "normalise", "parcels", "rules", "terrain", "roads", "props", "export")
RUN_ALL = "run-all"
PREVIEW = "preview"  # not a map stage: renders PNGs from the work files
PARCEL_RUN = "parcel-run"  # not a map stage: fences and trees of some parcels, as JSON
DEFAULT_EXAGGERATION = 3.0  # same as preview.DEFAULT_EXAGGERATION


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="suchohrad_pipeline",
        description="Generate the Suchohrad base map from source data.",
    )
    choices = [*STAGE_NAMES, RUN_ALL, PREVIEW, PARCEL_RUN]
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
        help="props, parcel-run: hand edits file (default: pipeline/config/edits.json)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="parcels: print counts by kind, building assignment and registry changes",
    )
    parser.add_argument(
        "--exaggeration",
        type=float,
        default=DEFAULT_EXAGGERATION,
        help="preview: multiply terrain heights by this factor in the isometric images",
    )
    parser.add_argument(
        "--parcel",
        action="append",
        default=[],
        metavar="ID",
        help="parcel-run: parcel to regenerate, e.g. P-0412 (repeatable)",
    )
    parser.add_argument(
        "--group",
        action="append",
        default=[],
        metavar="ID",
        help="parcel-run: property group to regenerate, e.g. G-0001 (repeatable)",
    )
    parser.add_argument(
        "--out",
        metavar="PATH",
        default=None,
        help="parcel-run: write the JSON fragment here instead of stdout",
    )
    parser.add_argument(
        "--glb",
        metavar="PATH",
        default=None,
        help="parcel-run: also write a GLB with the parcels' props nodes (chunk-file frame)",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.stage == PARCEL_RUN:
        from suchohrad_pipeline import parcel_run

        return parcel_run.run(
            parcels=args.parcel,
            groups=args.group,
            area=args.area,
            edits=args.edits,
            out=args.out,
            glb=args.glb,
        )
    from suchohrad_pipeline.stages import STAGES

    if args.stage == RUN_ALL:
        for run in STAGES.values():
            status = run(force=args.force, report=args.report, area=args.area, edits=args.edits)
            if status != 0:
                return status
        return 0
    if args.stage == PREVIEW:
        from suchohrad_pipeline import preview

        return preview.run(exaggeration=args.exaggeration, area=args.area)
    return STAGES[args.stage](
        force=args.force, report=args.report, area=args.area, edits=args.edits
    )
