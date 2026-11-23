"""Props stage: fences, trees (from the orthophoto only) and spawn candidates -> ``data/village/work/props.json`` and ``spawns.json``.

Hand edits (``config/edits.json``, see ``edits/schema.py``) are applied here too: fence modes and groups,
and placed objects, which are written to ``objects.json``.

Everything random is drawn from ``random.Random`` seeded with sha256 of ``<parcel id>:<purpose>``
(never Python's ``hash()``), so identical input gives identical output. Only places inside the
terrain coverage are used.
"""

import json
import sys
from pathlib import Path

from drycastle_pipeline.core.config import map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.edits import zones as zone_kinds
from drycastle_pipeline.edits.schema import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline.ground.areas import ground_types, ground_units, water_kinds
from drycastle_pipeline.parcels.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.props.edit_plan import plan_edits, unknown_asset_warnings, water_zone
from drycastle_pipeline.props.fences import fences_doc, make_fences
from drycastle_pipeline.props.spawns import make_spawns
from drycastle_pipeline.props.stands import wet_zone
from drycastle_pipeline.props.trees import (
    DEFAULT_TREE_OVERRIDES,
    cap_trees,
    load_tree_overrides,
    make_trees,
    plan_ground_objects,
)
from drycastle_pipeline.roads.lamps import make_lamps, read_osm_lamps, write_lamps
from drycastle_pipeline.terrain.terrain import load_terrain

PROPS_FORMAT_VERSION = 1
SPAWNS_FORMAT_VERSION = 1
OBJECTS_FORMAT_VERSION = 1


def run_props(
    work_dir: Path = DEFAULT_WORK_DIR,
    rules_path: Path = DEFAULT_RULES,
    edits_path: Path = DEFAULT_EDITS,
    tree_overrides_path: Path | None = DEFAULT_TREE_OVERRIDES,
) -> dict:
    terrain, _ = load_terrain(work_dir)
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    parcels = parcels_doc["parcels"]
    buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
    graph = json.loads((work_dir / "roads.json").read_text())
    fcfg = load_section("props", rules_path)
    scfg = load_section("spawns", rules_path)
    zrules = zone_kinds.rules(load_section("zones", rules_path))
    types = ground_types(rules_path, edits_path)
    zone_kinds.check_ground_types(zrules, types)
    spawn_where = zrules.get(zone_kinds.PLAYER_SPAWN, {}).get("where_ground")
    chunk_m = float(parcels_doc["chunk_size_m"])
    edits = load_edits(edits_path)
    plan = plan_edits(edits, parcels, parcels_doc.get("origin_epsg5514"), chunk_m)
    origin = parcels_doc.get("origin_epsg5514")
    cuts = zone_kinds.zone_cuts(edits.zone_cuts, origin) if edits.zone_cuts and origin else {}
    drawn = zone_kinds.drawn_zones(edits.zones, origin) if edits.zones and origin else {}
    spawn_zones = zone_kinds.kind_areas(zone_kinds.PLAYER_SPAWN, parcels_doc, drawn, spawn_where, cuts)
    for warning in plan.warnings + unknown_asset_warnings(plan.objects):
        print(f"props: warning: {warning}", file=sys.stderr)
    fences = make_fences(
        parcels, buildings, graph, terrain, fcfg["fences"], specs=plan.specs, groups=plan.groups
    )
    tree_cfg = {**fcfg["trees"], "ortho_dir": str(work_dir.parent / "ortho")}
    if not (Path(tree_cfg["ortho_dir"]) / "trees.json").is_file():
        print(f"props: warning: no orthophoto trees in {tree_cfg['ortho_dir']} (run make ortho-trees); the map has no trees",
              file=sys.stderr)
    elif not parcels_doc.get("origin_epsg5514"):
        print("props: warning: parcels.json has no origin_epsg5514, so the orthophoto trees cannot be placed; the map has no trees",
              file=sys.stderr)
    water = water_zone(plan, terrain)
    wet = wet_zone(ground_units(parcels_doc), water_kinds(types), water)
    trees = make_trees(
        buildings,
        graph,
        terrain,
        tree_cfg,
        objects=[(o["position"][0], o["position"][1]) for o in plan.objects],
        water=water,
        origin=tuple(parcels_doc["origin_epsg5514"]) if parcels_doc.get("origin_epsg5514") else None,
        removed=load_tree_overrides(tree_overrides_path, tuple(origin) if origin else None),
        wet=wet,
    )
    trees = cap_trees(trees, chunk_m, int(fcfg["trees"]["max_per_chunk"]))
    ground_objects = plan_ground_objects(
        parcels_doc, buildings, graph, terrain, trees, tree_cfg, fcfg["ground_objects"],
        objects=[(o["position"][0], o["position"][1]) for o in plan.objects], water=water, wet=wet,
    )
    spawns = make_spawns(
        spawn_zones,
        buildings,
        graph,
        trees,
        terrain,
        scfg,
        chunk_m,
        water=wet,  # the water ground areas and the wet part of the water patches
    )
    if spawn_zones and not spawns:
        print("props: warning: there are player_spawn zones but no usable ground in them; no spawn points", file=sys.stderr)
    objects_doc = {"format_version": OBJECTS_FORMAT_VERSION, "objects": plan.objects}
    if plan.cables:  # the wires between placed utility poles; the game strings them (docs/utility-poles.md)
        objects_doc["cables"] = plan.cables
    (work_dir / "objects.json").write_text(json.dumps(objects_doc, indent=1, sort_keys=True) + "\n")
    props = {"version": PROPS_FORMAT_VERSION, "fences": fences, "trees": trees, "ground_objects": ground_objects}
    (work_dir / "fences.json").write_text(json.dumps(fences_doc(fences, plan.groups, fcfg["fences"]), indent=1, sort_keys=True) + "\n")
    spawn_doc = {
        "version": SPAWNS_FORMAT_VERSION,
        "axes": "local metres, x east, y north, z up (elevation)",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "spawns": spawns,
    }
    for name, doc in (("props.json", props), ("spawns.json", spawn_doc)):
        (work_dir / name).write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    lamps_cfg = load_section("lamps", rules_path)
    lamps = make_lamps(
        parcels,
        buildings,
        graph,
        read_osm_lamps(work_dir),
        terrain,
        lamps_cfg,
        float(parcels_doc["chunk_size_m"]),
    )
    write_lamps(work_dir, lamps, float(parcels_doc["chunk_size_m"]), float(lamps_cfg["spacing_m"]))
    return {
        "fences": len(fences),
        "trees": len(trees),
        "ground_objects": len(ground_objects),
        "spawns": len(spawns),
        "objects": len(plan.objects),
        "lamps": len(lamps),
    }


def run(
    force: bool = False,
    edits: str | None = None,
    **_options: object,
) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    try:
        counts = run_props(map_paths().work, edits_path=edits_path)
    except FileNotFoundError as exc:
        print(f"props: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"props: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    except ValueError as exc:
        print(f"props: error: {exc}", file=sys.stderr)
        return 1
    print("props: " + ", ".join(f"{k}: {v}" for k, v in counts.items()))
    return 0
