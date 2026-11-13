"""Per-region run: the meshes of some regions only, as a GLB (for the editor's hot reload).

``python -m drycastle_pipeline region-run --area village --region R-0001 --glb r.glb`` loads the
work files, applies ``edits.json`` exactly like the export and rebuilds only the named regions
(none named: all). The GLB holds one node ``region_<ID>`` per region in the chunk files' frame (add
it at the map root); unlike the chunk files, a region that spans chunks is one node here. A JSON
summary goes to stdout (or ``--out``), shaped like the ``parcel-run`` fragment::

    {"format_version": 1, "regions": [...], "replaces": [...], "glb": "...", "triangles": {...}}

``replaces`` lists the regions whose baked ``region_<ID>`` nodes the viewer must hide: the named
ones, plus every region that overlaps one of them (the higher ID wins, so a change shows in the
visible area of the others too). A named ID that is not in the edits (a deleted region) is still
listed: its baked nodes have to go. Regions that overlapped a deleted region are not known any
more; name them too. Nothing under ``data/`` is written.
"""

import json
import sys
import time
from pathlib import Path

from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline.ground import ground_types
from drycastle_pipeline.parcel_run import _EMPTY_GLB
from drycastle_pipeline.regions import node_name, overlapping, plan_regions, region_meshes
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import load_terrain

REGION_RUN_FORMAT_VERSION = 1


def write_glb(path: Path, meshes: dict) -> None:
    """GLB with one ``region_<ID>`` node per mesh (same axes and frame as the chunk files)."""
    from drycastle_pipeline.export import build_chunk_scene

    path.parent.mkdir(parents=True, exist_ok=True)
    nodes = {node_name(rid): mesh for rid, mesh in sorted(meshes.items())}
    path.write_bytes(build_chunk_scene([], nodes).export(file_type="glb") if nodes else _EMPTY_GLB)


def run_region_run(
    work_dir: Path,
    regions: list[str],
    edits_path: Path = DEFAULT_EDITS,
    rules_path: Path = DEFAULT_RULES,
    glb_path: Path | None = None,
    area: str | None = None,
) -> dict:
    t0 = time.perf_counter()
    terrain, _ = load_terrain(work_dir, mmap=True)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    graph_path = work_dir / "roads.json"
    graph = json.loads(graph_path.read_text()) if graph_path.is_file() else None
    edits = load_edits(edits_path)
    plan = plan_regions(edits, parcels_doc.get("origin_epsg5514"), terrain, types=ground_types(rules_path, area, edits_path))
    for warning in plan.warnings:
        print(f"region-run: warning: {warning}", file=sys.stderr)
    named = set(regions) if regions else set(plan.regions)
    for rid in sorted(named - set(plan.regions) - set(edits.regions)):  # a region of a ground layer is in the edits but has no mesh
        print(f"region-run: warning: {rid} is not in the edits; its nodes are removed", file=sys.stderr)
    targets = overlapping(plan, named)
    t1 = time.perf_counter()
    meshes = region_meshes(
        plan, terrain, graph, load_section("roads", rules_path), load_section("regions", rules_path),
        only=targets,
    )
    glb = {}
    if glb_path is not None:
        write_glb(glb_path, meshes)
        glb = {"glb": str(glb_path.resolve())}
    t2 = time.perf_counter()
    return {
        "format_version": REGION_RUN_FORMAT_VERSION,
        "regions": sorted(set(meshes)),
        "replaces": sorted(targets),
        "triangles": {rid: m.face_count for rid, m in sorted(meshes.items())},
        **glb,
        "timing_s": {
            "load": round(t1 - t0, 3),
            "compute": round(t2 - t1, 3),
            "total": round(t2 - t0, 3),
        },
    }


def run(
    regions: list[str] | None = None,
    area: str = DEFAULT_AREA,
    edits: str | None = None,
    out: str | None = None,
    glb: str | None = None,
    work: str | None = None,
) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    try:
        summary = run_region_run(
            Path(work) if work else area_paths(area).work,
            regions or [],
            edits_path,
            glb_path=Path(glb) if glb else None,
            area=area,
        )
    except FileNotFoundError as exc:
        print(
            f"region-run: error: missing input {Path(str(exc.filename)).name}; run earlier stages",
            file=sys.stderr,
        )
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"region-run: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    except ValueError as exc:
        print(f"region-run: error: {exc}", file=sys.stderr)
        return 1
    text = json.dumps(summary, sort_keys=True, separators=(",", ":")) + "\n"
    if out:
        Path(out).write_text(text)
    else:
        sys.stdout.write(text)
    return 0
