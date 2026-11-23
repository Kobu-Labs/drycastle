"""The detail of the whole village (:func:`build_village_detail`) and of one parcel (:func:`run_parcel`)."""

import hashlib
import math
import sys
import time
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import Point, shape

from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.model import (
    _PATCH_ROW,
    CLUTTER_KINDS,
    PATCH_TEXTURE,
    DetailResult,
    ParcelContext,
    Patch,
    VillageDetail,
)
from drycastle_pipeline.detail.parts import free_area
from drycastle_pipeline.detail.patches import _draped_rows, patch_meshes, patch_rows
from drycastle_pipeline.detail.recipes import RECIPES, area_type
from drycastle_pipeline.detail.village import Village
from drycastle_pipeline.edits.schema import DEFAULT_EDITS
from drycastle_pipeline.props.helpers import chunk_of


def cap_clutter(chunks: dict, caps: dict[str, int]) -> dict:
    """At most ``caps[kind]`` instances of a kind per chunk (0 or no entry = no cap). Which ones go is decided
    by a hash of the position, so it is stable and spread over the chunk."""
    for kinds in chunks.values():
        for kind, rows in kinds.items():
            limit = int(caps.get(kind, 0))
            if limit > 0 and len(rows) > limit:
                rows.sort(key=lambda r: hashlib.sha256(f"{r[0]}:{r[1]}:{kind}:cap".encode()).hexdigest())
                del rows[limit:]
                rows.sort()
    return chunks


def _unit_results(v: "Village", parcel: dict, atype: str) -> list[tuple]:
    """``(context, recipe, rules, result)`` of every context of one unit."""
    out = []
    for ctx in v.contexts(parcel):
        kind = "dike" if ctx.dike else atype
        cfg = v.cfg_for(parcel, kind)
        out.append((ctx, kind, cfg, RECIPES[kind](ctx, cfg)))
    return out


_FORKED: tuple | None = None  # (village, units) of :func:`_units_in_processes`, read by its forked workers


def _forked_unit(k: int) -> list[tuple]:
    v, todo = _FORKED
    _, parcel, atype = todo[k]
    return [(None, kind, None, res) for _, kind, _, res in _unit_results(v, parcel, atype)]  # the result alone crosses back


def _units_in_processes(v: "Village", todo: list[tuple], workers: int) -> list[list[tuple]]:
    """:func:`_unit_results` of every unit of ``todo``, in its order, from ``workers`` forked processes (they share the village
    read-only; the biggest units go first so no process is left with them at the end)."""
    import multiprocessing

    global _FORKED
    for _ in v.contexts(todo[0][1]):  # the village's lazy tables are built once here, not once per process
        pass
    _FORKED = (v, todo)
    try:
        order = sorted(range(len(todo)), key=lambda k: -shape(todo[k][1]["polygon"]).area)
        with multiprocessing.get_context("fork").Pool(min(workers, len(todo))) as pool:
            got = pool.map(_forked_unit, order, chunksize=1)
    finally:
        _FORKED = None
    out: list[list[tuple]] = [[] for _ in todo]
    for k, r in zip(order, got, strict=True):
        out[k] = r
    return out


def build_village_detail(
    work_dir: Path, rules_path: Path = DEFAULT_RULES, edits_path: Path = DEFAULT_EDITS,
    only: set[str] | None = None, log=None, recipe: str | None = None, drape_patches=None, workers: int = 0,
) -> VillageDetail:
    """The detail of every parcel of the map (``only``: just these parcel IDs; tests, previews; ``recipe``: just the units of
    this recipe). ``drape_patches`` (:func:`detail_rust.drape_patches`): the patches
    of all units are draped by it in one call at the end, not one by one in Python (:func:`patch_meshes`); the rows keep the
    order of the Python build (per unit: the flat patches, then the textured ones by texture id). ``workers`` > 1: the recipes
    of the units run in that many processes (every recipe is seeded by its unit, so the result is the same)."""
    v = Village(work_dir, rules_path, edits_path)
    for w in v.warnings:
        print(f"detail: warning: {w}", file=sys.stderr)
    lift = float(v.cfg["lift_m"])
    textures = sorted(set(PATCH_TEXTURE.values()))
    chunks: dict[tuple[int, int], dict[str, list[list[float]]]] = {}
    crops: dict[tuple[int, int], dict[str, list[list[float]]]] = {}
    rows = []
    pending: list[tuple[str, list[Patch]]] = []  # with ``drape_patches``: (recipe, patches) of every unit, draped after the loop
    by_type: dict[str, dict[str, int]] = {}
    mcfg = v.cfg["meadow"]
    meadow = meadow_mod.new_meadow(mcfg, v.chunk_m)
    stored = set(mcfg["stored_kinds"])
    meadow_cells = 0
    t0 = time.time()
    todo = []
    for n, parcel in enumerate(sorted(v.parcels, key=lambda p: p["id"])):
        atype = area_type(parcel, v.types)
        if atype is None or (only is not None and parcel["id"] not in only) or (recipe is not None and atype != recipe):
            continue
        todo.append((n, parcel, atype))
    # not for the meadow recipes: they are added to the one meadow from their context, here
    forked = workers > 1 and len(todo) > 1 and recipe is not None and recipe not in meadow_mod.MEADOW_TYPES
    built = _units_in_processes(v, todo, workers) if forked else None
    for k, (n, parcel, atype) in enumerate(todo):
        for ctx, kind, cfg, res in built[k] if built is not None else _unit_results(v, parcel, atype):
            if kind in meadow_mod.MEADOW_TYPES:
                # the game generates the dense kinds from the raster; the recipe's own instances stay only for the rest
                res.clutter = [c for c in res.clutter if c["kind"] in stored]
                band = float(cfg[kind].get("edge_band_m", 0.0))
                meadow_cells += meadow_mod.add_parcel(meadow, ctx, res, kind, free_area(ctx, cfg, trunks=False), mcfg, band)
            if drape_patches is not None:
                pending.append((kind, res.patches))
            else:
                flat, tex = patch_meshes(res, v.terrain, lift, by_type.setdefault(kind, {}))
                rows.append(patch_rows(flat, tex, textures))
            for c in res.crops:
                key = tuple(chunk_of(c["x"], c["y"], v.chunk_m))
                crops.setdefault(key, {}).setdefault(c["kind"], []).append(
                    [round(c["x"], 2), round(c["y"], 2), round(c["z"], 2), round(c["rot"], 2), round(c["scale"], 2)])
            for c in res.clutter:
                key = tuple(chunk_of(c["x"], c["y"], v.chunk_m))
                row = [round(c["x"], 2), round(c["y"], 2), round(c["z"], 2), round(c["rot"], 2), round(c["scale"], 2)]
                if "tint" in c:
                    row.append(c["tint"])
                chunks.setdefault(key, {}).setdefault(c["kind"], []).append(row)
        if log and n % 200 == 0:
            log(f"{n}/{len(v.parcels)} parcels, {time.time() - t0:.0f} s")
    if pending:
        rows.append(_draped_rows(pending, drape_patches, v.terrain, lift, textures, by_type))
    count = lambda: {k: sum(len(kinds.get(k, [])) for kinds in chunks.values()) for k in CLUTTER_KINDS}  # noqa: E731
    before = count()
    cap_clutter(chunks, v.cfg["caps_per_chunk"])
    cap_clutter(crops, {k: s.get("cap", 0) for k, s in v.cfg["crop_kinds"].items()})
    patches = np.concatenate(rows) if rows else np.zeros((0, _PATCH_ROW))
    stats = {
        "clutter_before_caps": before,
        "clutter": count(),
        "max_per_chunk": {k: max((len(kinds.get(k, [])) for kinds in chunks.values()), default=0) for k in CLUTTER_KINDS},
        "patch_triangles": len(patches),
        "patch_triangles_by_type": {a: dict(sorted(l.items())) | {"total": sum(l.values())} for a, l in sorted(by_type.items())},
        "meadow_cells": meadow_cells,
        "meadow_expected": {k: round(x) for k, x in meadow.expected().items()},
        "meadow_busiest": meadow.busiest({"tuft": 70, "fern": 70, "flower": 55, "pebble": 40, "litter": 40, "floor": 60}),
        "seconds": round(time.time() - t0, 1),
    }
    stats["crops"] = {k: sum(len(kinds.get(k, [])) for kinds in crops.values()) for k in v.cfg["crop_kinds"]}
    return VillageDetail(v.chunk_m, chunks, patches, textures, stats, meadow, crops)


def run_parcel(
    parcel_id: str,
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    edits_path: Path = DEFAULT_EDITS,
    with_meadow: bool = False,
    meadow_window: float = 56.0,
) -> tuple[ParcelContext, DetailResult]:
    """The detail of one parcel, with the same inputs and recipe as the stage. ``with_meadow`` (field and forest
    only) swaps the recipe's dense kinds for what the game would generate from the raster of this parcel (the
    instances of :func:`meadow.generate`, minus those within a trunk's reach), for the preview, within a
    ``meadow_window`` metre square round the parcel's representative point (a forest is too big to draw)."""
    v = Village(work_dir, rules_path, edits_path)
    parcel = next((p for p in v.parcels if p["id"] == parcel_id), None)
    if parcel is None:
        raise ValueError(f"no parcel {parcel_id}")
    atype = area_type(parcel, v.types)
    if atype is None:
        raise ValueError(f"parcel {parcel_id} ({parcel['kind']}) has no ground detail")
    ctx = next(v.contexts(parcel))
    atype = "dike" if ctx.dike else atype
    res = RECIPES[atype](ctx, v.cfg_for(parcel, atype))
    if with_meadow and atype in meadow_mod.MEADOW_TYPES:
        mcfg = v.cfg["meadow"]
        m = meadow_mod.new_meadow(mcfg, v.chunk_m)
        meadow_mod.add_parcel(m, ctx, res, atype, free_area(ctx, v.cfg, trunks=False), mcfg, float(v.cfg[atype].get("edge_band_m", 0.0)))
        res.clutter = [c for c in res.clutter if c["kind"] in set(mcfg["stored_kinds"])]
        rp = ctx.polygon.representative_point()
        h = meadow_window / 2
        x0, y0, x1, y1 = max(rp.x - h, ctx.polygon.bounds[0]), max(rp.y - h, ctx.polygon.bounds[1]), min(rp.x + h, ctx.polygon.bounds[2]), min(rp.y + h, ctx.polygon.bounds[3])
        cell = m.cell_m
        trunks = shapely.points([(x, y) for x, y, _ in ctx.trees]) if ctx.trees else None
        for c in meadow_mod.generate(m, math.floor(x0 / cell), math.ceil(x1 / cell), math.floor(y0 / cell), math.ceil(y1 / cell)):
            if trunks is not None and len(shapely.STRtree(trunks).query(Point(c["x"], c["y"]), predicate="dwithin", distance=m.trunk_reach_m)):
                continue
            res.clutter.append({**c, "z": 0.0})
    return ctx, res
