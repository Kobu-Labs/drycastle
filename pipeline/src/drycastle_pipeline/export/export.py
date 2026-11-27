"""Export stage: ``data/village/work`` -> ``out/village/map/`` (chunk glTFs, parcels.json, manifest.json).

Output is byte-deterministic: no timestamps, sorted keys, sorted node order. glTF axes are Y-up:
glTF (x, y, z) = local (east, up, -north). parcels.json keeps local metres (x east, y north).
The glTF helpers are in ``gltf.py``, the small documents (trees.json, groups.json, ...) in ``docs.py``.
"""

import hashlib
import json
import sys
from pathlib import Path

from drycastle_pipeline.buildings import voxels
from drycastle_pipeline.buildings.derive import DEFAULT_OVERRIDES
from drycastle_pipeline.core.config import DEFAULT_RAW_DIR, REPO_ROOT, map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.edits.schema import DEFAULT_EDITS, GROUND_RECIPES, load_edits
from drycastle_pipeline.edits.zones import FILE_NAME as ZONE_POINTS_FILE
from drycastle_pipeline.edits.zones import POLYGONS_FILE_NAME as ZONE_POLYGONS_FILE
from drycastle_pipeline.edits.zones import (
    ZonesError,
    build_zone_points,
    build_zone_polygons,
    check_ground_types,
    drawn_zones,
    kind_rows,
    zone_cuts,
)
from drycastle_pipeline.edits.zones import rules as zone_rules
from drycastle_pipeline.export.docs import (
    GROUPS_FILE,
    build_groups,
    dump_doc,
    ground_objects_doc,
    trees_doc,
    write_clutter_meshes,
    write_graves,
    write_optional,
    write_primitives,
)
from drycastle_pipeline.export.gltf import (
    build_chunk_scene,
    chunk_name,
    export_glb,
    parcel_props_nodes,
)
from drycastle_pipeline.export.scene import build_scene
from drycastle_pipeline.ground.areas import (
    clip_areas,
    ground_type_row,
    ground_types,
    rules_ground_types,
)
from drycastle_pipeline.ground.patches import patch_rank
from drycastle_pipeline.parcels.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.terrain.water import water_doc, water_rules

DEFAULT_OUT_DIR = REPO_ROOT / "out" / "map"
# 0.2.0: fences and trees of a parcel moved from the chunk-level props / props_collision nodes into
# per-parcel nodes (props_layout "per_parcel"). Pre-1.0, a node-layout change bumps the minor part.
# 0.3.0: manifest lists objects.json (files.objects) and one GLB per primitive (prims).
# 0.4.0: parcels.json carries origin_epsg5514 (the local frame's origin in S-JTSK metres, so the editor can
# write absolute coordinates) and the manifest lists edit_defaults (the rule values the editor shows as defaults).
# 0.5.0: lamps.json (street lamp positions, files.lamps). Not in the chunk GLBs: the game builds poles and lights.
# 0.6.0: trees.json (files.trees): tree records and model numbers, for the trunk and crown collision solids.
# 0.7.0: textured nodes "<node>_tex_<texture id>" (UVs and a tint; drawn with that texture of the library in
# assets/manifest.json), first used for typed fences: "parcel_<ID>_props_tex_<id>". The manifest lists the ids used.
# (no bump) objects.json objects may carry "solid": false (no collision at all; the key is absent when true) and a "z" (absolute
# metres, else the terrain height). Older loaders ignore "solid" and keep the collision.
# (no bump) objects.json objects may carry "size_m": [length, width, height] metres, the exact final bounding box (the model is scaled
# non-uniformly to it; wins over "scale"; absent when not set). Older loaders ignore it and draw the model at "scale".
# (no bump) objects.json objects may carry "model": a GLB of the map (graves/<id>.glb) drawn in place of the asset's shared model, at its real size
# (not stretched by size_m): the generated graves (kits/graves.py). An older loader draws the asset stretched.
# (no bump) objects.json may carry "cables": the cables between placed utility poles (id, from, to). The game
# strings the wires itself from the poles and the wire seats in assets/manifest.json; older loaders ignore the key.
# 0.8.0: ground detail (detail stage): per chunk a "detail_patches" node (flat colours) and "detail_patches_tex_<id>" nodes
# (draped patches of dirt, gravel, leaf litter, beds, furrows, tones), and clutter.json (files.clutter: instances per chunk
# and kind) with clutter_meshes.glb (files.clutter_meshes: one tiny mesh per kind) for instanced rendering near the player.
# 0.9.0: meadow.bin (files.meadow): the density raster the game turns into near-player clutter on fields and in forests
# (detail/meadow.py). Optional: a loader that does not know it just draws the stored clutter.
# 0.10.0: clutter variety. clutter.json entries may carry a sixth number, the tint byte (128 = as drawn), and its kinds a
# "tint" flag; meadow.bin is format 2 (a tint flag per layer) and then 3 (a species-zone plane and kind mixes); more
# clutter kinds (grasses, wild and garden flowers) in clutter_meshes.glb. A loader that reads only 0.9.0 clutter would refuse
# meadow.bin's new format and draw no meadow.
# (no bump) crops.json (files.crops): instances of the crop fields (ground types crop_*): per chunk and kind rows [x, y, z, rot,
# scale] of plants (crop_wheat, ...) and artifacts (art_hay_bale_round, ...), and per kind what to draw it with (role, asset or
# synty names). Cosmetic: no collision. Only maps with a crop patch have it; a loader that does not know it draws none.
# (no bump) garage.json (files.garage): the bus garage: the doorway its doors hang in, the bay, where the bus stands.
# 0.11.0: forest trees (props.trees.forest): trees.json's model carries a "forest" block (tapered trunk, crown tiers) and each
# forest tree a "forest": 1 flag; the chunk meshes draw them with it. A loader that does not know it builds the old trunk and
# crown solids for them, which no longer match the drawn tree. Trees without the flag are drawn and built as before.
# 0.12.0: baked trees can be hidden one by one on the GPU. A props mesh with tree triangles (parcel_<ID>_props, the chunk-level
# props) carries a second UV set (TEXCOORD_1, two floats per vertex): for a tree's vertices the tree's base (trees.json x, y) as
# (POSITION x, POSITION z) of the same mesh, so MODEL_MATRIX * vec4(uv2.x, vertex.y, uv2.y, 1) is the base's world point; for all
# other vertices (fences) NO_TREE_UV2, a point no player is near. Meshes without trees have no TEXCOORD_1, and trees.json's model
# says "base_uv2": true. A loader that does not know it ignores the attribute and draws every tree; maps before 0.12.0 have none.
# 0.13.0: ground objects (props.ground_objects): ground_objects.json (files.ground_objects) lists the fallen logs, stumps, boulders, anthills, burrows and
# root plates of the forest ("kind", x, y, z, rot, scale, "len" for a log, chunk) and the model numbers of each kind; the game builds
# their solid colliders from it. Their low-poly meshes are baked into the same props meshes as the trees, with the object's base as
# TEXCOORD_1 (the model says "base_uv2": true). A loader that does not know the file draws them but walks and shoots through them.
# (no bump) urad.json (files.urad): the inside of the municipal office: entrance, rooms, worker seats, dictation desk.
# (no bump) church.json (files.church): the inside of the church: door, seats with routes, spots, anchors, rooms.
# (no bump) interiors.json (files.interiors): the houses with an inside (buildings/interior.py): room volume, floor, ceiling, doorways.
# (no bump) bunkers.json (files.bunkers): the rooms of the enterable bunkers, per floor a polygon and a height range.
# Only maps with a bunker have it; a loader that does not know it gives no shelter from blasts. A bunker may carry "intel", the
# wall of its command post the intel screen hangs on (ADR 0017); a loader that does not know it has no intel room.
# (no bump) zone_points.json (files.zone_points): candidate points per zone kind (rules.yaml zones: bigfoot, one kind per wild
# species) that the server picks from. Only maps where some zone has an area have it; a loader that does not know it, or a map
# without it, means "no points".
# (no bump) zone_polygons.json (files.zone_polygons): the outlines of the zones of the kinds the server asks "is a player inside" of
# (the plantation), rings in game axes [x, z]. Only maps with such a zone have it; a loader that does not know it, or a map without
# it, means "no such zone".
# (no bump) patches.json (files.patches): the ground patches of the edits in the colour of their type, for the minimap; the
# patch_<ID> nodes of the chunk files are the water patches only (the other patches are ground, baked into terrain and detail).
# (no bump) groups.json (files.groups): the property groups of the edits (group id -> parcel ids), for the game server's pets.
# Only maps with a group have it; a loader that does not know it, or a map without it, means "no groups".
# (no bump) fences.json (files.fences): every fence section (two end points with ground z, height_m, type, parcel, property group),
# for the game server's pets (the fence hop). A loader that does not know it, or a map without it, means "no fence data".
# (0.16.0 adds its destruction keys, see there.)
# (no bump) NORMAL on every drawn mesh of the chunk files, clutter_meshes.glb and prims (the face normal, flat shading);
# collision meshes have none. Maps built before this shade with one constant normal (lights cut off by heading).
# 0.15.0: house destruction (docs/house-destruction.md): every hollow house is its own house_<bid>_visual and house_<bid>_collision node
# of the chunk of its centroid, no longer merged into its parcel's nodes (a parcel of hollow houses only has none), and
# house_voxels.bin (files.house_voxels) holds their hidden voxel grids. A loader that registers only parcel_* colliders still
# collides with the houses but cannot swap them; a map without the file has no destruction. (A node-layout change: the minor part.)
# The manifest's file_sha256 names the sha256 of every file of `files`, so the build hash the join check compares (MapLoader.BuildHash)
# covers them too: a client with another house_voxels.bin is refused at join instead of never matching the server's houses.
# 0.16.0: destroyable fences (docs/fence-destruction.md). Every fences.json section gains kind (plain, picket, wire, wall),
# thickness_m and colour, a thin one also pieces, piece_m, piece0_m, uv and collider (the props_collision node that holds it), a
# wall one grid ("fence_<index>"); version stays 1 (the thieves' FenceBook ignores the keys). The panel of a thin section carries
# TEXCOORD_1 = (index + 0.5, (metres along from a - piece0_m) / piece_m) on every vertex in its textured node (picket, wire and the
# new library texture "plain", which draws the box of a section without a type: it was a flat box of the props mesh), so the game
# hides a broken piece by the floor of the second. A wall section (brick, stone, concrete types) leaves the props, props_tex_*
# and props_collision nodes: house_fence_<index:05d>_visual (flat parts), _visual_tex_<texture> and _collision (closed boxes of
# the wall and the piers it owns), in the chunk of its midpoint, and house_voxels.bin gets a grid "fence_<index:05d>" for it
# (materials 7 brick, 8 stone, 9 concrete). A loader that does not know them draws no walls and no pieces' texture coordinate.
MAP_VERSION_BASE = "0.16.0"
PROPS_LAYOUT = "per_parcel"
ARTIFACT_FORMAT_VERSION = 1
WORK_INPUTS = ("terrain.json", "terrain_heights.npy", "terrain_valid.npy", "roads.json",
               "props.json", "spawns.json", "lamps.json", "detail.json", "detail_patches.npy",
               "detail_patches.json", "detail_meadow.bin", "detail_crops.json")
NOT_GENERATED_NOTE = "Landmarks, navmesh and water surface meshes are not generated (water.json holds the water bodies). Textures are not in the map: textured nodes name a texture of the asset library."


INTERIOR_AXES = "points: local metres [x east, y north, z up] (x, y as parcels.json, z as terrain.json); yaw: compass degrees"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _on_terrain(areas: list[dict], terrain) -> list[dict]:
    """The ground areas limited to the terrain: nothing is defined off the playing area (no terrain: as they are)."""
    return areas if terrain is None else clip_areas(areas, terrain.coverage)


def _edit_defaults(rules_path: Path) -> dict:
    """Rule values the editor shows as the greyed defaults of fences and trees."""
    props = load_section("props", rules_path)
    fences = props["fences"]
    return {
        "fence": {
            "kinds": list(fences["kinds"]),
            "probability": fences["probability"],
            "height_m": fences["height_m"],
            "thickness_m": fences["thickness_m"],
            "colour": fences["colour"],
        },
    }


def _parcel_rows(parcels_doc: dict, buildings: dict) -> list[dict]:
    """The parcels.json rows: each parcel with the buildings of it the map has."""
    return [
        {
            "id": p["id"],
            "cadastral_ref": p["cadastral_ref"],
            "kind": p["kind"],
            "polygon": p["polygon"],
            "anchor": p["anchor"],
            "chunk": p["chunk"],
            "buildings": sorted(bid for bid in p["buildings"] if bid in buildings),
        }
        for p in sorted(parcels_doc["parcels"], key=lambda p: p["id"])
    ]


def _write_chunks(scene, out_dir: Path) -> list[dict]:
    """One GLB per chunk of the scene; returns the manifest's chunk entries."""
    chunk_entries = []
    for ix, iy in scene.chunks():
        items = sorted(scene.parcels.get((ix, iy), []), key=lambda t: t[0])
        extra = {
            **scene.nodes.get((ix, iy), {}),
            **parcel_props_nodes(scene.parcel_props.get((ix, iy), [])),
        }
        rel = f"chunks/{chunk_name(ix, iy)}.glb"
        data = export_glb(build_chunk_scene(items, extra))
        (out_dir / rel).write_bytes(data)
        triangles = {f"parcel_{pid}_{sfx}": m.face_count for pid, v, c in items
                     for sfx, m in (("visual", v), ("collision", c))}
        triangles.update({name: m.face_count for name, m in extra.items()})
        chunk_entries.append(
            {
                "index": [ix, iy],
                "file": rel,
                "parcels": sorted(pid for pid, _, _ in items),
                "triangles": dict(sorted(triangles.items())),
                "sha256": hashlib.sha256(data).hexdigest(),
            }
        )
    return chunk_entries


def _copy_work_file(
    work_dir: Path, out_dir: Path, src: str, dst: str, key: str, files: dict, *, clear_stale: bool = True
) -> bool:
    """Copy a work file verbatim (the game reads what the stage planned) and list it as ``files[key]``.

    Without the work file: the stale ``dst`` of an earlier run is removed when ``clear_stale``. Returns whether it was copied."""
    src_path = work_dir / src
    if src_path.is_file():
        (out_dir / dst).write_bytes(src_path.read_bytes())
        files[key] = dst
        return True
    if clear_stale:
        (out_dir / dst).unlink(missing_ok=True)
    return False


def _write_scene_docs(scene, out_dir: Path, rules_path: Path, chunk_m: float, files: dict) -> None:
    """roads.json, spawns.json, trees.json and ground_objects.json: the scene's baked lists."""
    if scene.graph is not None:
        dump_doc(out_dir / "roads.json", scene.graph)
        files["roads"] = "roads.json"
    if scene.spawns is not None:
        dump_doc(out_dir / "spawns.json", scene.spawns)
        files["spawns"] = "spawns.json"
    if scene.trees is not None:
        # tree collision solids (trunk and crown), built by the game from these records and the model numbers
        dump_doc(out_dir / "trees.json", trees_doc(scene.trees, load_section("props", rules_path)["trees"], chunk_m))
        files["trees"] = "trees.json"
    # solid ground objects of the forest (props stage): the game builds colliders from the records and the model numbers;
    # props.json from before this file existed: no objects
    objects = None if scene.ground_objects is None else ground_objects_doc(
        scene.ground_objects, load_section("props", rules_path)["ground_objects"], chunk_m)
    write_optional(out_dir, "ground_objects.json", objects, files, "ground_objects")


def _write_fences(work_dir: Path, out_dir: Path, scene, files: dict) -> None:
    """fences.json: the props stage's file with each section's destruction keys (kind, pieces, uv, collider, grid: ``scene.fence_docs``)
    added; no props stage file: none (a stale one is removed)."""
    path = work_dir / "fences.json"
    if not path.is_file():
        (out_dir / "fences.json").unlink(missing_ok=True)
        return
    doc = json.loads(path.read_text())
    if scene.fence_docs:  # the sections are the props.json ones, in the same order: the index is the position
        if len(doc["fences"]) != len(scene.props["fences"]):
            raise ValueError("work/fences.json and work/props.json disagree on the fence sections; rerun the props stage")
        doc["fences"] = [{**f, **scene.fence_docs.get(i, {})} for i, f in enumerate(doc["fences"])]
    (out_dir / "fences.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    files["fences"] = "fences.json"


def _copy_work_files(work_dir: Path, out_dir: Path, scene, files: dict) -> None:
    """The props and detail stages' files: fences (with the export's keys), lamps, clutter (with its meshes), crops, meadow."""
    # fence sections (props stage): the game server hops pets over them and breaks them (the export adds the keys it decides)
    _write_fences(work_dir, out_dir, scene, files)
    # street lamp positions (props stage): the game builds the lamps from them
    _copy_work_file(work_dir, out_dir, "lamps.json", "lamps.json", "lamps", files, clear_stale=False)
    # clutter instances (detail stage), with the one mesh per kind they are drawn with
    if _copy_work_file(work_dir, out_dir, "detail.json", "clutter.json", "clutter", files, clear_stale=False):
        files["clutter_meshes"] = write_clutter_meshes(out_dir / "clutter_meshes.glb")
    else:
        for stale in ("clutter.json", "clutter_meshes.glb"):
            (out_dir / stale).unlink(missing_ok=True)
    # crop plants and artifacts (detail stage, crop recipe): the game draws each kind with its own model
    _copy_work_file(work_dir, out_dir, "detail_crops.json", "crops.json", "crops", files)
    # density raster of the meadow clutter (detail stage): the game generates the instances from it
    _copy_work_file(work_dir, out_dir, "detail_meadow.bin", "meadow.bin", "meadow", files)


def _write_interior_docs(scene, out_dir: Path, files: dict) -> None:
    """windows.json, bunkers.json, church.json, urad.json and garage.json: what the building stages found; none when empty."""
    # tagged window panes (building_detail): node name, position, size, facade, breakable
    windows = {"version": 1, "windows": sorted(scene.windows, key=lambda w: w["node"])} if scene.windows else None
    write_optional(out_dir, "windows.json", windows, files, "windows")
    # the rooms of the enterable bunkers (bunker.bunker_rooms): the game shelters a player inside from a blast outside
    bunkers = {
        "version": 1,
        "axes": "polygon: local metres, x east, y north (as parcels.json); z0, z1: metres, as terrain.json",
        "bunkers": sorted(scene.bunkers, key=lambda b: b["id"]),
    } if scene.bunkers else None
    write_optional(out_dir, "bunkers.json", bunkers, files, "bunkers")
    # church_interior.church_info: seats and routes, spots, demon anchors, shelter rooms
    # office_interior.urad_info: entrance, rooms, worker seats, dictation desk
    # garage.garage_info: the doorway of its swinging doors, the bay, the parking place
    # buildings/interior.py: the houses that are hollow, each with its room (a shelter volume, as a bunker's), its doorways and the doors players open
    interiors = {"version": 1, "axes": INTERIOR_AXES, "houses": sorted(scene.interiors, key=lambda h: h["id"])} if scene.interiors else None
    write_optional(out_dir, "interiors.json", interiors, files, "interiors")
    for name, info in (("church", scene.church), ("urad", scene.urad), ("garage", scene.garage)):
        doc = {"version": 1, "axes": INTERIOR_AXES, **info} if info else None
        write_optional(out_dir, f"{name}.json", doc, files, name)


def _parcels_file(parcels_doc: dict, parcels_out: list[dict], scene, rules_path: Path, edits_path: Path) -> tuple[dict, dict]:
    """The ``parcels.json`` document and the zone rules (checked against the ground types) it was built with."""
    parcels_file = {
        "version": ARTIFACT_FORMAT_VERSION,
        "crs": parcels_doc["crs"],
        "axes": "x east, y north (local metres); glTF files use Y-up: x east, y up, z -north",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "parcels": parcels_out,
        # the ground areas by type (ground/areas.py: kind = the type): the editor's Ground types tool draws them to show what is where
        "ground_areas": [
            {"id": a["id"], "kind": a["kind"], "area": a["area"], "polygon": a["polygon"]}
            for a in sorted(_on_terrain(parcels_doc.get("ground", []), scene.terrain), key=lambda a: a["id"])
        ],
    }
    if "origin_epsg5514" in parcels_doc:  # always there from the parcels stage on
        parcels_file["origin_epsg5514"] = parcels_doc["origin_epsg5514"]
    # the ground types for the editor (ground types tool, the type of a patch): every type of the rules, with the
    # rules' own entry (`rules`; the editor lays the user's edits.json overrides over it, so the list stays live between
    # builds), then the types the user made (`custom`); name, first colour and a summary are those at this build
    rule_types = rules_ground_types(rules_path)
    effective = ground_types(rules_path, edits_path)
    parcels_file["ground_default"] = load_section("ground", rules_path)["default"]
    # the shipped settings of each detail recipe (without the clutter tables): what a ground type's `detail` is laid over
    detail_cfg = load_section("detail", rules_path)
    parcels_file["ground_recipes"] = {
        r: {k: v for k, v in detail_cfg[r].items() if k != "clutter"} for r in GROUND_RECIPES if isinstance(detail_cfg.get(r), dict)
    }
    parcels_file["ground_types"] = [
        ground_type_row(name, rule_types.get(name), effective.get(name) or rule_types.get(name) or {})
        for name in [*rule_types, *(n for n in effective if n not in rule_types)]
    ]
    zrules = zone_rules(load_section("zones", rules_path))
    check_ground_types(zrules, effective)
    # the zone kinds for the editor (zones tool): the catalog with the ground types of each rule zone; the drawn zones are in the edits
    parcels_file["zone_kinds"] = kind_rows(zrules)
    return parcels_file, zrules


def _write_water(scene, out_dir: Path, rules_path: Path, files: dict) -> dict | None:
    """``water.json`` and its warnings; no terrain: no water, and a water file from an earlier run would be stale. Returns the document."""
    water_file = out_dir / "water.json"
    if scene.water is None:
        if water_file.exists():
            water_file.unlink()
        return None
    for pid in scene.water.skipped:
        print(f"export: warning: water parcel/patch {pid} has no terrain under it (or none below its surface); no water body", file=sys.stderr)
    water = water_doc(scene.water.bodies, water_rules(rules_path))
    dump_doc(water_file, water)
    files["water"] = "water.json"
    return water


def _write_zone_docs(
    scene, parcels_doc: dict, buildings: dict, zrules: dict, out_dir: Path, rules_path: Path, edits_path: Path, files: dict
) -> None:
    """water.json, zone_points.json and zone_polygons.json (the last two only where some zone has an area to give)."""
    zone_edits = load_edits(edits_path)
    origin = parcels_doc.get("origin_epsg5514")
    drawn = drawn_zones(zone_edits.zones, origin) if origin else {}
    cuts = zone_cuts(zone_edits.zone_cuts, origin) if origin else {}
    water = _write_water(scene, out_dir, rules_path, files)
    points_doc = None
    if water is not None:
        # candidate points of the zone kinds, drawn clear of the buildings and of the water just built
        points_doc, points_warnings = build_zone_points(
            parcels_doc, drawn, list(buildings.values()), scene.terrain, water, zrules, cuts
        )
        for warning in points_warnings:
            print(f"export: warning: zone points: {warning}", file=sys.stderr)
    # no zone has an area (any more): no file, "no points"
    write_optional(out_dir, ZONE_POINTS_FILE, points_doc, files, "zone_points")
    # no plantation zone (any more): no file, the feature is off
    polygons_doc = build_zone_polygons(parcels_doc, drawn, zrules, cuts)
    write_optional(out_dir, ZONE_POLYGONS_FILE, polygons_doc, files, "zone_polygons")


def _write_patches(scene, out_dir: Path, files: dict) -> None:
    """``patches.json``: the ground patches for the minimap (a water patch is in water.json); no ground patch: no file."""
    ground_patches = [p for p in scene.patches.values() if p.water_y is None]
    doc = None
    if ground_patches:
        # local metres (x east, y north), drawn in ID order so the higher ID wins
        ordered = sorted(ground_patches, key=lambda p: patch_rank(p.id))
        doc = {"version": 1, "patches": [
            {"id": p.id, "type": p.type, "colour": list(p.colour),
             "ring": [[round(float(x), 2), round(float(y), 2)] for x, y in piece.exterior.coords]}
            for p in ordered
            for piece in getattr(p.polygon, "geoms", [p.polygon])
        ]}
    write_optional(out_dir, "patches.json", doc, files, "patches")


def _generated_parts(scene, files: dict, rules_path: Path) -> tuple[set[str], dict | None]:
    """The manifest's ``generated`` parts and its terrain grid info (None without terrain)."""
    generated = {"buildings"}
    if "lamps" in files:
        generated.add("lamps")
    if "clutter" in files:
        generated.add("detail")
    terrain_info = None
    if scene.terrain is not None:
        t = scene.terrain
        generated |= {"terrain", "water"}
        terrain_info = {
            "grid_step_m": round(t.step, 4),
            "cells_per_chunk": t.n,
            "collision_decimate": int(load_section("terrain", rules_path)["collision_decimate"]),
            "elevation": "metres above the DMR vertical datum, as in the source (not rebased)",
            "coverage": "meshed only where the DMR raster covers all four cell corners",
        }
        if scene.graph is not None:
            generated |= {"roads", "roads_graph"}
        if scene.props is not None:
            generated |= {"fences", "trees"}
            if "ground_objects" in files:
                generated.add("ground_objects")
        if scene.spawns is not None:
            generated |= {"spawns"}
    if any(name.startswith("patch_") for nodes in scene.nodes.values() for name in nodes):
        generated.add("patches")
    return generated, terrain_info


def _input_hashes(work_dir: Path, raw_manifest: dict, rules_path: Path, overrides_path: Path) -> dict:
    """The manifest's ``inputs``: the sha256 of every raw, work and config file the map was built from."""
    tree_overrides = overrides_path.parent / "tree_overrides.yaml"
    return {
        "raw": {f["file"]: f["sha256"] for f in raw_manifest.get("files", [])},
        "work/parcels.json": _sha256(work_dir / "parcels.json"),
        "work/buildings.json": _sha256(work_dir / "buildings.json"),
        **{
            f"work/{name}": _sha256(work_dir / name)
            for name in WORK_INPUTS
            if (work_dir / name).is_file()
        },
        "config/rules.yaml": _sha256(rules_path),
        "config/overrides.yaml": _sha256(overrides_path),
        **({"config/tree_overrides.yaml": _sha256(tree_overrides)} if tree_overrides.is_file() else {}),
    }


def run_export(
    work_dir: Path = DEFAULT_WORK_DIR,
    out_dir: Path = DEFAULT_OUT_DIR,
    raw_dir: Path = DEFAULT_RAW_DIR,
    rules_path: Path = DEFAULT_RULES,
    overrides_path: Path = DEFAULT_OVERRIDES,
    edits_path: Path = DEFAULT_EDITS,
) -> dict:
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    buildings = {
        b["id"]: b for b in json.loads((work_dir / "buildings.json").read_text())["buildings"]
    }
    raw_manifest = json.loads((raw_dir / "manifest.json").read_text())

    chunks_dir = out_dir / "chunks"
    chunks_dir.mkdir(parents=True, exist_ok=True)
    for old in chunks_dir.glob("chunk_*.glb"):
        old.unlink()

    scene = build_scene(work_dir, rules_path, edits_path=edits_path)
    for warning in scene.warnings:
        print(f"export: warning: {warning}", file=sys.stderr)
    parcels_out = _parcel_rows(parcels_doc, buildings)
    chunk_entries = _write_chunks(scene, out_dir)

    files: dict[str, str] = {}
    # placed objects (props stage), with the grave models made from them
    if _copy_work_file(work_dir, out_dir, "objects.json", "objects.json", "objects", files, clear_stale=False):
        write_graves(out_dir)
    prims = write_primitives(out_dir)
    _write_scene_docs(scene, out_dir, rules_path, float(parcels_doc["chunk_size_m"]), files)
    _copy_work_files(work_dir, out_dir, scene, files)
    _write_interior_docs(scene, out_dir, files)
    voxels.write_file(out_dir, scene.house_voxels, files)

    parcels_file, zrules = _parcels_file(parcels_doc, parcels_out, scene, rules_path, edits_path)
    dump_doc(out_dir / "parcels.json", parcels_file)
    _write_zone_docs(scene, parcels_doc, buildings, zrules, out_dir, rules_path, edits_path, files)
    groups_doc = build_groups(edits_path, (p["id"] for p in parcels_doc["parcels"]))
    write_optional(out_dir, GROUPS_FILE, groups_doc, files, "groups")  # no property group (any more): no file, "no pets"
    _write_patches(scene, out_dir, files)

    generated, terrain_info = _generated_parts(scene, files, rules_path)
    manifest = {
        "map_version": f"{MAP_VERSION_BASE}-village",
        "format_version": ARTIFACT_FORMAT_VERSION,
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "props_layout": PROPS_LAYOUT,
        "gltf_axes": "Y-up: x east, y up, z -north, metres, shared map-origin frame",
        "chunks": chunk_entries,
        "attribution": raw_manifest.get("attribution", []),
        "inputs": _input_hashes(work_dir, raw_manifest, rules_path, overrides_path),
        "generated": sorted(generated),
        "files": files,
        "file_sha256": {k: _sha256(out_dir / v) for k, v in sorted(files.items())},
        "edit_defaults": _edit_defaults(rules_path),
        "textures": sorted(
            {name.partition("_tex_")[2] for c in chunk_entries for name in c["triangles"] if "_tex_" in name}
        ),
        "prims": prims,
        "terrain": terrain_info,
        "not_generated": NOT_GENERATED_NOTE,
    }
    dump_doc(out_dir / "manifest.json", manifest)
    return manifest


def run(
    force: bool = False,
    out: str | None = None,
    work: str | None = None,
    edits: str | None = None,
    **_options: object,
) -> int:
    """``out`` and ``work`` override the map's output and work directories (experiments, tests)."""
    paths = map_paths()
    out_dir = Path(out) if out else paths.out
    try:
        manifest = run_export(Path(work) if work else paths.work, out_dir, paths.raw,
                              edits_path=Path(edits) if edits else DEFAULT_EDITS)
    except FileNotFoundError as exc:
        print(f"export: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except ZonesError as exc:
        print(f"export: error: {exc}", file=sys.stderr)
        return 1
    print(f"export: {len(manifest['chunks'])} chunk file(s) written to {out_dir}/")
    return 0
