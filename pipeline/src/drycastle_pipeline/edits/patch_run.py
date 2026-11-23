"""Per-patch run: the meshes of some ground patches only, as a GLB (for the editor's hot reload).

``python -m drycastle_pipeline patch-run --patch GP-0001 --glb p.glb`` loads the
work files, applies ``edits.json`` exactly like the export and rebuilds only the named patches
(none named: all). The GLB holds one node ``patch_<ID>`` per patch in the chunk files' frame (add
it at the map root); unlike the chunk files, a patch that spans chunks is one node here. A JSON
summary goes to stdout (or ``--out``), shaped like the ``parcel-run`` fragment::

    {"format_version": 1, "patches": [...], "replaces": [...], "glb": "...", "triangles": {...}}

``replaces`` lists the patches whose baked ``patch_<ID>`` nodes the viewer must hide: the named
ones, plus every patch that overlaps one of them (the higher ID wins, so a change shows in the
visible area of the others too). A named ID that is not in the edits (a deleted patch) is still
listed: its baked nodes have to go. Patches that overlapped a deleted patch are not known any
more; name them too. Nothing under ``data/`` is written.
"""

import json
import sys
import time
from pathlib import Path

from drycastle_pipeline.core.config import map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.edits.parcel_run import _EMPTY_GLB
from drycastle_pipeline.edits.schema import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline.ground.areas import ground_types, ground_units, water_kinds
from drycastle_pipeline.ground.patches import node_name, overlapping, patch_meshes, plan_patches
from drycastle_pipeline.terrain.bed import carve_terrain
from drycastle_pipeline.terrain.terrain import load_terrain
from drycastle_pipeline.terrain.water import plan_water, water_rules

PATCH_RUN_FORMAT_VERSION = 1


def write_glb(path: Path, meshes: dict) -> None:
    """GLB with one ``patch_<ID>`` node per mesh (same axes and frame as the chunk files)."""
    from drycastle_pipeline.export.gltf import build_chunk_scene

    path.parent.mkdir(parents=True, exist_ok=True)
    nodes = {node_name(pid): mesh for pid, mesh in sorted(meshes.items())}
    path.write_bytes(build_chunk_scene([], nodes).export(file_type="glb") if nodes else _EMPTY_GLB)


def run_patch_run(
    work_dir: Path,
    patches: list[str],
    edits_path: Path = DEFAULT_EDITS,
    rules_path: Path = DEFAULT_RULES,
    glb_path: Path | None = None,
) -> dict:
    t0 = time.perf_counter()
    terrain, _ = load_terrain(work_dir)  # carved below, so not memory-mapped
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    graph_path = work_dir / "roads.json"
    graph = json.loads(graph_path.read_text()) if graph_path.is_file() else None
    edits = load_edits(edits_path)
    plan = plan_patches(edits, parcels_doc.get("origin_epsg5514"), terrain, ground_types(rules_path, edits_path))
    for warning in plan.warnings:
        print(f"patch-run: warning: {warning}", file=sys.stderr)
    # the terrain the export draws on: carved to the water beds, after the water is worked out on the uncarved ground
    wrules = water_rules(rules_path)
    water = plan_water(ground_units(parcels_doc), terrain, plan.patches, wrules, water_kinds(ground_types(rules_path, edits_path)))
    carve_terrain(terrain, water.bodies, float(wrules["carve_blend_m"]))
    named = set(patches) if patches else set(plan.patches)
    for pid in sorted(named - set(plan.patches) - set(edits.ground_patches)):
        print(f"patch-run: warning: {pid} is not in the edits; its nodes are removed", file=sys.stderr)
    targets = overlapping(plan, named)
    t1 = time.perf_counter()
    meshes = patch_meshes(
        plan, terrain, graph, load_section("roads", rules_path), load_section("ground_patches", rules_path),
        only=targets, wet=water.wet,
    )
    glb = {}
    if glb_path is not None:
        write_glb(glb_path, meshes)
        glb = {"glb": str(glb_path.resolve())}
    t2 = time.perf_counter()
    return {
        "format_version": PATCH_RUN_FORMAT_VERSION,
        "patches": sorted(set(meshes)),
        "replaces": sorted(targets),
        "triangles": {pid: m.face_count for pid, m in sorted(meshes.items())},
        **glb,
        "timing_s": {
            "load": round(t1 - t0, 3),
            "compute": round(t2 - t1, 3),
            "total": round(t2 - t0, 3),
        },
    }


def run(
    patches: list[str] | None = None,
    edits: str | None = None,
    out: str | None = None,
    glb: str | None = None,
    work: str | None = None,
) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    try:
        summary = run_patch_run(
            Path(work) if work else map_paths().work,
            patches or [],
            edits_path,
            glb_path=Path(glb) if glb else None,
        )
    except FileNotFoundError as exc:
        print(
            f"patch-run: error: missing input {Path(str(exc.filename)).name}; run earlier stages",
            file=sys.stderr,
        )
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"patch-run: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    except ValueError as exc:
        print(f"patch-run: error: {exc}", file=sys.stderr)
        return 1
    text = json.dumps(summary, sort_keys=True, separators=(",", ":")) + "\n"
    if out:
        Path(out).write_text(text)
    else:
        sys.stdout.write(text)
    return 0
