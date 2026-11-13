"""Per-parcel run: fences and objects of some parcels only, as a JSON fragment (for the editor).

``python -m drycastle_pipeline parcel-run --area village --parcel P-0412 [--group G-0001]``
loads the work files, applies ``edits.json`` exactly like the props stage and regenerates only the
fences and objects of the target parcels. Nothing under ``data/`` is written. The result equals the
target parcels' share of the full props stage (``props.json`` / ``objects.json``):

* **Fences** are processed in parcel ID order and each parcel skips what neighbours with a lower ID
  already fenced (``done`` in :func:`props.make_fences`). So the run processes the targets plus,
  transitively, every lower-ID parcel whose boundary is within :data:`NEIGHBOUR_M` of one of them,
  and every member of a group a processed parcel belongs to (the perimeter needs all members). Only
  buildings and road edges near that set are given to ``make_fences``.
* **Trees** are not part of a run: they come from the orthophoto (:func:`props.ortho_trees`), not from
  parcels, so the fragment's ``trees`` is empty and a full props run places them (and keeps them clear of
  placed objects).
"""

import json
import struct
import sys
import time
from pathlib import Path

import shapely
from shapely.geometry import shape
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline.props import (
    EditPlan,
    make_fences,
    plan_edits,
    unknown_asset_warnings,
)
from drycastle_pipeline.roads import edge_lines
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import load_terrain

FRAGMENT_FORMAT_VERSION = 1
# Parcels whose boundaries are closer than this can influence each other's fence de-duplication
# (``done`` is buffered by 0.3 m); the margin is generous.
NEIGHBOUR_M = 0.5
_MARGIN_M = 1.0


def _empty_glb() -> bytes:
    """A valid GLB with one empty scene (trimesh refuses to export a scene without geometry)."""
    body = b'{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}]}'
    body += b" " * (-len(body) % 4)
    return b"glTF" + struct.pack("<II", 2, 20 + len(body)) + struct.pack("<I", len(body)) + b"JSON" + body


_EMPTY_GLB = _empty_glb()


class TargetError(ValueError):
    """A requested parcel or group does not exist."""


def _near(tree: shapely.STRtree, geom: BaseGeometry, distance: float) -> set[int]:
    return {int(i) for i in tree.query(geom, predicate="dwithin", distance=distance)}


def expand_targets(
    plan: EditPlan, known: set[str], parcels: list[str], groups: list[str]
) -> list[str]:
    """Target parcel IDs: the given parcels and group members; a group member pulls in its group."""
    out: set[str] = set()
    for gid in groups:
        if gid not in plan.groups:
            raise TargetError(f"group {gid} not found in the edits (or has no member parcels)")
        out.update(plan.groups[gid])
    for pid in parcels:
        if pid not in known:
            raise TargetError(f"parcel {pid} not found")
        out.add(pid)
    member_group = {pid: gid for gid, pids in plan.groups.items() for pid in pids}
    for pid in list(out):
        if pid in member_group:
            out.update(plan.groups[member_group[pid]])
    return sorted(out)


def fence_closure(
    plan: EditPlan,
    ids: list[str],
    polys: dict[str, BaseGeometry],
    tree: shapely.STRtree,
    order: list[str],
) -> list[str]:
    """Parcels whose fences can influence the fences of ``ids`` (including ``ids`` themselves)."""
    member_group = {pid: gid for gid, pids in plan.groups.items() for pid in pids}
    seen = set(ids)
    queue = list(ids)
    while queue:
        pid = queue.pop()
        new = [order[i] for i in _near(tree, polys[pid], NEIGHBOUR_M)]
        for other in new:
            if other < pid and other not in seen:
                seen.add(other)
                queue.append(other)
                gid = member_group.get(other)
                for q in plan.groups[gid] if gid else ():
                    if q not in seen:
                        seen.add(q)
                        queue.append(q)
    return sorted(seen)


class _Context:
    """Buildings and road edges of the work files, indexed for neighbourhood queries."""

    def __init__(self, buildings: list[dict], graph: dict) -> None:
        self.buildings = buildings
        self.btree = shapely.STRtree([footprint_polygon(b["footprint"]) for b in buildings])
        self.graph = graph
        self.edges = [e for e, _ in edge_lines(graph)]
        lines = [ln for _, ln in edge_lines(graph)]
        self.etree = shapely.STRtree(lines)
        self.max_half = max((e["width"] / 2 for e in self.edges), default=0.0)

    def around(self, geoms: list[BaseGeometry], building_m: float, road_m: float) -> tuple:
        """Buildings within ``building_m`` and road edges within ``road_m`` of any of ``geoms``."""
        bi: set[int] = set()
        ei: set[int] = set()
        for g in geoms:
            bi |= _near(self.btree, g, building_m)
            ei |= _near(self.etree, g, road_m + self.max_half)
        return (
            [self.buildings[i] for i in sorted(bi)],
            {**self.graph, "edges": [self.edges[i] for i in sorted(ei)]},
        )


def compute_fragment(
    parcels_doc: dict,
    buildings: list[dict],
    graph: dict,
    terrain,
    fcfg: dict,
    plan: EditPlan,
    targets: list[str],
    context: _Context | None = None,
) -> dict:
    """Fences, trees and objects of ``targets`` (already expanded); see the module docstring."""
    parcels = parcels_doc["parcels"]
    order = sorted(p["id"] for p in parcels)
    by_id = {p["id"]: p for p in parcels}
    polys = {pid: shape(by_id[pid]["polygon"]) for pid in order}
    tree = shapely.STRtree([polys[pid] for pid in order])
    ctx = context or _Context(buildings, graph)
    target_set = set(targets)

    # fences: targets plus everything that can influence them
    closure = fence_closure(plan, targets, polys, tree, order)
    closure_polys = [polys[pid] for pid in closure]
    f_buildings, f_graph = ctx.around(
        closure_polys,
        max(
            float(fcfg["fences"]["garden_near_building_m"]),
            float(fcfg["fences"]["building_clearance_m"]),
        )
        + _MARGIN_M,
        max(float(fcfg["fences"]["road_clearance_m"]), float(fcfg["fences"]["gate_road_within_m"])) + _MARGIN_M,
    )
    in_closure = set(closure)
    groups = {gid: pids for gid, pids in plan.groups.items() if in_closure.intersection(pids)}
    fences = make_fences(
        [by_id[pid] for pid in closure],
        f_buildings,
        f_graph,
        terrain,
        fcfg["fences"],
        specs=plan.specs,
        groups=groups,
    )
    fences = [s for s in fences if s["parcel"] in target_set]

    trees: list[dict] = []  # trees come from the orthophoto only: not per parcel, a full props run places them

    objects = [o for o in plan.objects if o["parcel"] in target_set]
    return {"fences": fences, "trees": trees, "objects": objects}


def write_glb(path: Path, fragment: dict, fcfg: dict) -> None:
    """GLB with ``parcel_<ID>_props`` and ``parcel_<ID>_props_collision`` of the target parcels.

    Same mesh functions, glTF axes and node naming as the chunk export (z scale 1), and the same
    frame as the chunk files: add it at the map root. Parcels without fences or trees get no nodes.
    """
    from drycastle_pipeline.export import build_chunk_scene, parcel_props_nodes
    from drycastle_pipeline.propmeshes import parcel_props_meshes

    by_f: dict[str, list[dict]] = {}
    by_t: dict[str, list[dict]] = {}
    for s in fragment["fences"]:
        by_f.setdefault(s["parcel"], []).append(s)
    for t in fragment["trees"]:
        by_t.setdefault(t["parcel"], []).append(t)
    items = [
        (pid, *parcel_props_meshes(by_f.get(pid, []), by_t.get(pid, []), fcfg))
        for pid in sorted(set(by_f) | set(by_t))
    ]
    path.parent.mkdir(parents=True, exist_ok=True)
    nodes = parcel_props_nodes(items)
    path.write_bytes(
        build_chunk_scene([], nodes).export(file_type="glb") if nodes else _EMPTY_GLB
    )


def run_parcel_run(
    work_dir: Path,
    area: str,
    parcels: list[str],
    groups: list[str],
    edits_path: Path = DEFAULT_EDITS,
    rules_path: Path = DEFAULT_RULES,
    glb_path: Path | None = None,
) -> dict:
    t0 = time.perf_counter()
    terrain, _ = load_terrain(work_dir, mmap=True)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    graph = json.loads((work_dir / "roads.json").read_text())
    fcfg = load_section("props", rules_path, area)
    plan = plan_edits(
        load_edits(edits_path),
        parcels_doc["parcels"],
        parcels_doc.get("origin_epsg5514"),
        float(parcels_doc["chunk_size_m"]),
    )
    for warning in plan.warnings + unknown_asset_warnings(plan.objects):
        print(f"parcel-run: warning: {warning}", file=sys.stderr)
    targets = expand_targets(plan, {p["id"] for p in parcels_doc["parcels"]}, parcels, groups)
    t1 = time.perf_counter()
    fragment = compute_fragment(parcels_doc, buildings, graph, terrain, fcfg, plan, targets)
    glb = {}
    if glb_path is not None:
        write_glb(glb_path, fragment, fcfg)
        # "replaces" lets the viewer hide the baked nodes even when the new result is empty
        glb = {"glb": str(glb_path.resolve()), "replaces": targets}
    t2 = time.perf_counter()
    return {
        "format_version": FRAGMENT_FORMAT_VERSION,
        "parcels": targets,
        **fragment,
        **glb,
        "timing_s": {
            "load": round(t1 - t0, 3),
            "compute": round(t2 - t1, 3),
            "total": round(t2 - t0, 3),
        },
    }


def run(
    parcels: list[str],
    groups: list[str] | None = None,
    area: str = DEFAULT_AREA,
    edits: str | None = None,
    out: str | None = None,
    glb: str | None = None,
    work: str | None = None,
) -> int:
    groups = groups or []
    if not parcels and not groups:
        print("parcel-run: error: give at least one --parcel or --group", file=sys.stderr)
        return 2
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    try:
        fragment = run_parcel_run(
            Path(work) if work else area_paths(area).work, area, parcels, groups, edits_path,
            glb_path=Path(glb) if glb else None,
        )
    except FileNotFoundError as exc:
        print(
            f"parcel-run: error: missing input {Path(str(exc.filename)).name}; run earlier stages",
            file=sys.stderr,
        )
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"parcel-run: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    except (TargetError, ValueError) as exc:
        print(f"parcel-run: error: {exc}", file=sys.stderr)
        return 1
    text = json.dumps(fragment, sort_keys=True, separators=(",", ":")) + "\n"
    if out:
        Path(out).write_text(text)
    else:
        sys.stdout.write(text)
    return 0
