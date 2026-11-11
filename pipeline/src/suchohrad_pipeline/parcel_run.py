"""Per-parcel run: fences and trees of some parcels only, as a JSON fragment (for the editor).

``python -m suchohrad_pipeline parcel-run --area village --parcel P-0412 [--group G-0001]``
loads the work files, applies ``edits.json`` exactly like the props stage and regenerates only the
fences and trees of the target parcels. Nothing under ``data/`` is written. The result equals the
target parcels' share of the full props stage (``props.json`` / ``objects.json``):

* **Fences** are processed in parcel ID order and each parcel skips what neighbours with a lower ID
  already fenced (``done`` in :func:`props.make_fences`). So the run processes the targets plus,
  transitively, every lower-ID parcel whose boundary is within :data:`NEIGHBOUR_M` of one of them,
  and every member of a group a processed parcel belongs to (the perimeter needs all members). Only
  buildings, road edges and road parcels near that set are given to ``make_fences``.
* **Trees** of a parcel depend only on its own seed and the buildings, roads and placed objects
  around it (the whole parcel: a tree rejected or accepted anywhere shifts the seeded stream). The
  per-chunk cap (:func:`props.cap_trees`) also depends on the rule trees of every
  other parcel in the same chunk, so when the cap is on, those parcels' trees are generated too,
  for the chunks that contain target trees.
"""

import json
import struct
import sys
import time
from pathlib import Path

import shapely
from shapely.geometry import shape
from shapely.geometry.base import BaseGeometry

from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.config import DEFAULT_AREA, area_paths
from suchohrad_pipeline.edits import DEFAULT_EDITS, EditsError, load_edits
from suchohrad_pipeline.props import (
    EditPlan,
    cap_trees,
    chunk_of,
    make_fences,
    make_trees,
    plan_edits,
    unknown_asset_warnings,
)
from suchohrad_pipeline.roads import edge_lines
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section
from suchohrad_pipeline.terrain import load_terrain

FRAGMENT_FORMAT_VERSION = 1
# Parcels whose boundaries are closer than this can influence each other's fence de-duplication
# (``done`` is buffered by 0.3 m); the margin is generous.
NEIGHBOUR_M = 0.5
_ROAD_UNION_M = 1.0  # road parcels within this of a parcel matter for its gate gap (buffer 0.5)
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


def _tree_context(ctx: _Context, geoms: list[BaseGeometry], tcfg: dict) -> tuple:
    return ctx.around(
        geoms,
        float(tcfg["building_clearance_m"]) + _MARGIN_M,
        float(tcfg["road_clearance_m"]) + _MARGIN_M,
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
    chunk_m = float(parcels_doc["chunk_size_m"])
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
        float(fcfg["fences"]["road_clearance_m"]) + _MARGIN_M,
    )
    road_ids: set[str] = set()
    for g in closure_polys:
        road_ids |= {order[i] for i in _near(tree, g, _ROAD_UNION_M)}
    road_polys = [polys[pid] for pid in sorted(road_ids) if by_id[pid]["kind"] == "road"]
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
        road_polys=road_polys,
    )
    fences = [s for s in fences if s["parcel"] in target_set]

    # trees
    tcfg = fcfg["trees"]
    objects_xy = [(o["position"][0], o["position"][1]) for o in plan.objects]

    def trees_of(ids: set[str], geoms: list[BaseGeometry]) -> list[dict]:
        t_buildings, t_graph = _tree_context(ctx, geoms, tcfg)
        return make_trees(
            [by_id[pid] for pid in sorted(ids)],
            t_buildings,
            t_graph,
            [],
            terrain,
            tcfg,
            overrides=plan.tree_overrides,
            objects=objects_xy,
            only=ids,
            include_osm=False,
            water=plan.water,
        )

    trees = trees_of(target_set, [polys[pid] for pid in targets])
    limit = int(tcfg["max_per_chunk"])
    if limit > 0 and trees:
        chunks = {tuple(chunk_of(t["x"], t["y"], chunk_m)) for t in trees}
        rects = [
            shapely.box(cx * chunk_m, cy * chunk_m, (cx + 1) * chunk_m, (cy + 1) * chunk_m)
            for cx, cy in sorted(chunks)
        ]
        others: set[str] = set()
        for rect in rects:
            others |= {order[i] for i in tree.query(rect, predicate="intersects")}
        others -= target_set
        trees = trees + trees_of(others, [polys[pid] for pid in sorted(others)])
        trees = cap_trees(trees, chunk_m, limit)
    trees = [t for t in trees if t["parcel"] in target_set]

    objects = [
        o
        for o in plan.objects
        if o["parcel"] in target_set or plan.object_owner.get(o["id"]) in target_set
    ]
    return {"fences": fences, "trees": trees, "objects": objects}


def write_glb(path: Path, fragment: dict, fcfg: dict) -> None:
    """GLB with ``parcel_<ID>_props`` and ``parcel_<ID>_props_collision`` of the target parcels.

    Same mesh functions, glTF axes and node naming as the chunk export (z scale 1), and the same
    frame as the chunk files: add it at the map root. Parcels without fences or trees get no nodes.
    """
    from suchohrad_pipeline.export import build_chunk_scene, parcel_props_nodes
    from suchohrad_pipeline.propmeshes import parcel_props_meshes

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
