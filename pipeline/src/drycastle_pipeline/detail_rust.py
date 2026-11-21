"""The Rust engine of the ``detail`` stage (``--engine rust``, ``DRYCASTLE_DETAIL_ENGINE=rust``).

Python stays the orchestrator: it reads the work files, the rules and ``edits.json`` exactly like :class:`detail.Village`, resolves
what the Python stage resolves (the detail rules of every ground type, the terrain colour of every unit, the recipe of every unit,
the water patches, the placed objects' clearance discs) and hands it over as one JSON document plus the terrain arrays to the extension
module ``drycastle_detail`` (``pipeline/rust/detail``, built with ``make detail-rust-build``). The module does the per-unit work on
all cores, merges it in a fixed order and writes ``detail.json``, ``detail_patches.npy``, ``detail_patches.json`` and
``detail_meadow.bin`` in the formats of the Python stage, so the export stage and the game read them unchanged. See
``docs/pipeline.md`` ("Rust engine").
"""

import json
import math
import os
import sys
import time
from pathlib import Path

import numpy as np

from drycastle_pipeline import detail as D
from drycastle_pipeline.assets import ASSETS_DIR, load_manifest
from drycastle_pipeline.config import map_paths
from drycastle_pipeline.detail_meshes import KINDS, TINTABLE
from drycastle_pipeline.edits import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline.ground import ground_types, ground_units, type_colours, water_kinds
from drycastle_pipeline.ground_attributes import attributes_of
from drycastle_pipeline.patches import plan_patches
from drycastle_pipeline.rules import DEFAULT_RULES, _merge, load_section
from drycastle_pipeline.terrain import load_terrain
from drycastle_pipeline.textures import load_textures

BUILD_HINT = "build it with `make detail-rust-build` (maturin develop, see docs/pipeline.md \"Rust engine\")"


class RustEngineMissing(RuntimeError):
    pass


def load_module():
    try:
        import drycastle_detail
    except ImportError as exc:
        raise RustEngineMissing(f"the Rust detail engine is not installed: {BUILD_HINT}") from exc
    return drycastle_detail


def _polys(geom) -> list[dict]:
    parts = list(geom.geoms) if hasattr(geom, "geoms") else [geom]
    return [{"exterior": [list(c) for c in p.exterior.coords], "holes": [[list(c) for c in r.coords] for r in p.interiors]}
            for p in parts if not p.is_empty]


def build_input(work_dir: Path, rules_path: Path, edits_path: Path) -> tuple[dict, np.ndarray, np.ndarray, tuple[int, int, bytes], list[str]]:
    """The input document of the engine, the terrain arrays, the dike texture (width, height, rgb bytes) and the warnings."""
    from drycastle_pipeline.scene import ParcelColours  # heavy imports, only for the stage

    cfg = load_section("detail", rules_path)
    tree_cfg = load_section("props", rules_path)["trees"]
    terrain, _ = load_terrain(work_dir)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    units = ground_units(parcels_doc)
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    props = json.loads((work_dir / "props.json").read_text())
    graph = json.loads((work_dir / "roads.json").read_text())
    objects_path = work_dir / "objects.json"
    objects = json.loads(objects_path.read_text())["objects"] if objects_path.is_file() else []
    types = ground_types(rules_path, edits_path)
    colours = ParcelColours(units, {**load_section("terrain", rules_path)["colours"], **type_colours(types)})

    # the unit table: recipe (None: no detail), terrain colour, whether the whole unit is the dike
    out_units = []
    cfgs: dict[str, dict] = {}
    seq = {p["id"]: i for i, p in enumerate(units)}  # the crop recipe walks the neighbours in the order of the work files
    for p in sorted(units, key=lambda u: u["id"]):
        recipe = D.area_type(p, types)
        out_units.append({
            "id": p["id"], "seq": seq[p["id"]], "kind": p["kind"], "recipe": recipe or "",
            "whole_dike": bool(p["id"] in D.dike_parcels() or p["kind"] == "dike"),
            "colour": colours._pick(p), "polygon": p["polygon"],
        })
        if recipe is None:
            continue
        for r in {recipe, "dike"}:
            attrs = attributes_of(types.get(p["kind"]))
            key = f"{p['kind']}|{r}"
            if attrs.get("detail") and attrs.get("recipe") == r and key not in cfgs:
                cfgs[key] = {**cfg, r: _merge(cfg[r], attrs["detail"])}

    sizes = {a["id"]: a.get("size_m", [1, 1, 1]) for a in load_manifest().get("assets", [])}
    discs = []
    for o in objects:
        sx, _, sz = sizes.get(o["asset"], [1.0, 1.0, 1.0])
        sx, sz, k = (o["size_m"][0], o["size_m"][1], 1.0) if o.get("size_m") else (sx, sz, float(o.get("scale", 1.0)))
        r = 0.5 * math.hypot(sx, sz) * k + float(cfg["object_clearance_m"])
        discs.append([o["position"][0], o["position"][1], r])

    plan = plan_patches(load_edits(edits_path), parcels_doc.get("origin_epsg5514"), terrain, types)
    water_patches = [q for p in plan.patches.values() if p.water_y is not None for q in _polys(p.polygon)]
    stands = tree_cfg.get("stands")
    swamp = D.swamp_zone(units, types, plan, terrain, float(stands["swamp"]["reach_m"])) if stands and "swamp" in stands else None

    lib = load_textures()
    tex_list = sorted(set(D.PATCH_TEXTURE.values()))
    image = (1, 1, b"\x00\x00\x00")
    if any(u["recipe"] == "dike" or u["whole_dike"] for u in out_units) or cfg["dike"].get("clips"):
        import matplotlib.image as mpimg

        img = (mpimg.imread(ASSETS_DIR / "drycastle-generated-textures" / "dike_gravel.png")[..., :3] * 255).round().astype(np.uint8)
        image = (img.shape[1], img.shape[0], img.tobytes())

    doc = {
        "chunk_m": float(parcels_doc["chunk_size_m"]),
        "cfg": cfg,
        "cfgs": cfgs,
        "canopy_m": float(tree_cfg["canopy_m"]),
        "units": out_units,
        "water_kinds": sorted(water_kinds(types)),
        "buildings": [{"exterior": b["footprint"]["exterior"], "holes": b["footprint"]["holes"]} for b in buildings],
        "fences": [[s["a"][0], s["a"][1], s["b"][0], s["b"][1]] for s in props["fences"]],
        "trees": [[t["x"], t["y"], float(t["scale"])] for t in props["trees"]],
        "roads": [{"id": e["id"], "highway": e["highway"], "width": float(e["width"]), "osm_id": e.get("osm_id") or "", "polyline": e["polyline"]}
                  for e in graph["edges"]],
        "objects": discs,
        "regions": water_patches,  # the engine calls the polygons it keeps detail out of "regions"
        "swamp": _polys(swamp) if swamp is not None else [],  # swamp ground: where the swamp stand of trees stands (detail.swamp_zone)
        "coverage": [list(c) for c in terrain.coverage.exterior.coords],
        "textures": {t: [lib[t].tile_w, lib[t].tile_h] for t in tex_list},
        "tex_list": tex_list,
        "kinds": list(KINDS),
        "tintable": sorted(TINTABLE),
        "terrain": {"chunk_m": terrain.chunk_m, "n": terrain.n, "i0": terrain.i0, "j0": terrain.j0,
                    "rows": int(terrain.heights.shape[0]), "cols": int(terrain.heights.shape[1])},
    }
    return doc, np.ascontiguousarray(terrain.heights, dtype="<f8"), np.ascontiguousarray(terrain.valid, dtype=np.uint8), image, list(plan.warnings)


def drape_patches(patches: list, terrain, lift_m: float, textures: list[str], threads: int = 0) -> tuple[np.ndarray, list[int]]:
    """The rows (the layout of ``detail_patches.npy``) of ``patches`` (:class:`detail.Patch`) draped on the terrain by the engine, on
    all cores, in patch order, and the triangle count of each patch. What :func:`detail.patch_meshes` does in Python (minutes for the
    crop fields of the village map): the same area at the same heights, cut into triangles its own way."""
    import shapely
    from shapely.geometry import MultiPolygon

    from drycastle_pipeline.patches import _polygons
    from drycastle_pipeline.textures import texture

    mod = load_module()
    jobs, blobs, at = [], [], 0
    for p in patches:
        wkb = shapely.to_wkb(MultiPolygon(_polygons(p.geom)))
        tex = None
        if p.texture is not None:
            t = texture(p.texture)
            tex = [t.tile_w, t.tile_h, textures.index(p.texture)]
        colour = p.colour or ([128, 128, 128] if p.texture is None else [255, 255, 255])  # a textured patch's colour is its tint
        jobs.append({"wkb": [at, len(wkb)], "colour": [int(c) for c in colour], "tex": tex, "angle": float(p.angle),
                     "origin": [float(p.origin[0]), float(p.origin[1])], "lift": float(p.lift or lift_m)})
        blobs.append(wkb)
        at += len(wkb)
    doc = {"terrain": {"chunk_m": terrain.chunk_m, "n": terrain.n, "i0": terrain.i0, "j0": terrain.j0,
                       "rows": int(terrain.heights.shape[0]), "cols": int(terrain.heights.shape[1])}, "jobs": jobs}
    rows, counts = mod.drape(json.dumps(doc), b"".join(blobs), np.ascontiguousarray(terrain.heights, dtype="<f8").tobytes(),
                             np.ascontiguousarray(terrain.valid, dtype=np.uint8).tobytes(), threads)
    return np.frombuffer(rows, dtype="<f8").reshape(-1, D._PATCH_ROW), list(counts)


def run_engine(
    work_dir: Path, rules_path: Path = DEFAULT_RULES, edits_path: Path = DEFAULT_EDITS,
    threads: int = 0, only: list[str] | None = None, write: bool = True, out_dir: Path | None = None,
) -> tuple[dict, list[str]]:
    """Runs the engine; returns its statistics and the warnings of the water patches."""
    mod = load_module()
    doc, heights, valid, image, warnings = build_input(work_dir, rules_path, edits_path)
    stats = json.loads(mod.run(json.dumps(doc), heights.tobytes(), valid.tobytes(), image[2], (image[0], image[1]),
                               str(out_dir or work_dir), threads, only, write))
    return stats, warnings


def run(force: bool = False, edits: str | None = None, work: str | None = None, **options: object) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    work_dir = Path(work) if work else map_paths().work
    threads = int(options.get("threads") or os.environ.get("DRYCASTLE_DETAIL_THREADS") or 0)
    t0 = time.time()
    try:
        stats, warnings = run_engine(work_dir, edits_path=edits_path, threads=threads)
    except RustEngineMissing as exc:
        print(f"detail: error: {exc}", file=sys.stderr)
        return 1
    except FileNotFoundError as exc:
        print(f"detail: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"detail: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    for w in warnings:
        print(f"detail: warning: {w}", file=sys.stderr)
    if any(stats["crops"].values()):
        print("detail: crops: " + ", ".join(f"{k}: {n}" for k, n in stats["crops"].items() if n), file=sys.stderr)
    for f in stats["failed"]:
        print(f"detail: warning: a unit failed and has no detail: {f}", file=sys.stderr)
    print(
        f"detail: patch triangles: {stats['patch_triangles']}, clutter: "
        + ", ".join(f"{k}: {n}" for k, n in stats["clutter"].items())
        + f" ({time.time() - t0:.1f} s, rust, {stats['threads']} threads)"
    )
    print(f"detail: meadow: {stats['meadow_cells']} cells; expected instances " + ", ".join(f"{k}: {n}" for k, n in stats["meadow_expected"].items()),
          file=sys.stderr)
    print("detail: patch triangles by area type: "
          + "; ".join(f"{a} {sum(l.values())} (" + ", ".join(f"{k} {n}" for k, n in sorted(l.items())) + ")"
                      for a, l in sorted(stats["patch_triangles_by_type"].items())), file=sys.stderr)
    print("detail: seconds by step: " + ", ".join(f"{k} {v:.1f}" for k, v in stats["timing_s"].items()), file=sys.stderr)
    return 0
