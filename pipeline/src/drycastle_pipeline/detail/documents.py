"""The work files of the stage: the clutter and crops documents, ``write_detail`` and ``write_crops``."""

import json
from pathlib import Path

import numpy as np

from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.meshes import TINTABLE
from drycastle_pipeline.detail.model import (
    CLUTTER_KINDS,
    CROP_FORMAT_VERSION,
    DETAIL_FORMAT_VERSION,
    VillageDetail,
)


def clutter_document(detail: VillageDetail) -> dict:
    """The ``clutter.json`` document: one mesh per kind (``clutter_meshes.glb``), instances per chunk and kind."""
    chunks = {}
    for ix, iy in sorted(detail.clutter):
        by_kind = {k: rows for k, rows in sorted(detail.clutter[(ix, iy)].items()) if rows}
        if by_kind:
            chunks[f"{ix}_{iy}"] = by_kind
    return {
        "format_version": DETAIL_FORMAT_VERSION,
        "axes": "map metres: x east, y north, z up (ground height at the instance)",
        "chunk_size_m": detail.chunk_m,
        "kinds": {k: ({"mesh": k, "tint": True} if k in TINTABLE else {"mesh": k}) for k in CLUTTER_KINDS},
        "chunks": chunks,
    }


def crops_document(detail: VillageDetail, cfg: dict | None = None) -> dict:
    """The ``crops.json`` document: instances per chunk and kind (rows ``[x, y, z, rot, scale]``, like the clutter), and per kind what
    the game draws it with (``detail.crop_kinds``: ``role``, ``asset`` or ``synty`` names, ``cap`` per chunk)."""
    kinds = (cfg or load_section("detail", DEFAULT_RULES))["crop_kinds"]
    chunks = {}
    for ix, iy in sorted(detail.crops):
        by_kind = {k: rows for k, rows in sorted(detail.crops[(ix, iy)].items()) if rows}
        if by_kind:
            chunks[f"{ix}_{iy}"] = by_kind
    return {
        "format_version": CROP_FORMAT_VERSION,
        "axes": "map metres: x east, y north, z up (ground height at the instance); row: x y z rot(rad about up) scale",
        "chunk_size_m": detail.chunk_m,
        "kinds": {k: {key: v for key, v in spec.items() if key != "cap"} for k, spec in kinds.items()},
        "chunks": chunks,
    }


def write_crops(work_dir: Path, detail: VillageDetail) -> None:
    """``detail_crops.json`` (the export copies it as ``crops.json``); none when no crop instance exists."""
    path = work_dir / "detail_crops.json"
    doc = crops_document(detail)
    if doc["chunks"]:
        path.write_text(json.dumps(doc, separators=(",", ":")) + "\n")
    else:
        path.unlink(missing_ok=True)


def write_detail(work_dir: Path, detail: VillageDetail) -> None:
    write_crops(work_dir, detail)
    (work_dir / "detail.json").write_text(json.dumps(clutter_document(detail), separators=(",", ":")) + "\n")
    np.save(work_dir / "detail_patches.npy", detail.patches)
    if detail.meadow is not None and detail.meadow.chunks:
        (work_dir / "detail_meadow.bin").write_bytes(meadow_mod.encode(detail.meadow))
    else:
        (work_dir / "detail_meadow.bin").unlink(missing_ok=True)
    (work_dir / "detail_patches.json").write_text(
        json.dumps({"format_version": DETAIL_FORMAT_VERSION, "textures": detail.textures,
                    "row": "x0 y0 z0 x1 y1 z1 x2 y2 z2 r g b u0 v0 u1 v1 u2 v2 texture_index(-1: flat colour)"}, indent=1) + "\n"
    )
