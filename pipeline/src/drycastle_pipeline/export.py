"""Export stage: ``data/village/work`` -> ``out/village/map/`` (chunk glTFs, parcels.json, manifest.json).

Output is byte-deterministic: no timestamps, sorted keys, sorted node order. glTF axes are Y-up:
glTF (x, y, z) = local (east, up, -north). parcels.json keeps local metres (x east, y north).
"""

import hashlib
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
import trimesh

from drycastle_pipeline.buildings import DEFAULT_OVERRIDES
from drycastle_pipeline.config import DEFAULT_RAW_DIR, REPO_ROOT, map_paths
from drycastle_pipeline.detail_meshes import clutter_meshes
from drycastle_pipeline.edits import DEFAULT_EDITS, GROUND_RECIPES, load_edits
from drycastle_pipeline.ground import (
    clip_areas,
    ground_type_row,
    ground_types,
    ground_units,
    rules_ground_types,
    water_kinds,
)
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.patches import patch_rank
from drycastle_pipeline.primitives import PRIMITIVES, build_primitive
from drycastle_pipeline.propmeshes import GROUND_BAKED_ONLY, is_forest
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.scene import build_scene
from drycastle_pipeline.textures import TexturedMesh, tex_node_name
from drycastle_pipeline.water import water_doc, water_rules
from drycastle_pipeline.zones import FILE_NAME as ZONE_POINTS_FILE
from drycastle_pipeline.zones import POLYGONS_FILE_NAME as ZONE_POLYGONS_FILE
from drycastle_pipeline.zones import (
    ZonesError,
    build_zone_points,
    build_zone_polygons,
    check_ground_types,
    drawn_zones,
    kind_rows,
    zone_cuts,
)
from drycastle_pipeline.zones import rules as zone_rules

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
# (no bump) NORMAL on every drawn mesh of the chunk files, clutter_meshes.glb and prims (the face normal, flat shading);
# collision meshes have none. Maps built before this shade with one constant normal (lights cut off by heading).
MAP_VERSION_BASE = "0.14.0"
NO_TREE_UV2 = 1.0e6  # TEXCOORD_1 of a vertex that is no tree
BASE_UV2_ATTRIBUTE = "_UV2"  # trimesh writes custom attributes with a leading underscore; export_glb renames it
PROPS_LAYOUT = "per_parcel"
ARTIFACT_FORMAT_VERSION = 1
TREES_FORMAT_VERSION = 1
GROUND_OBJECTS_FORMAT_VERSION = 1
TREES_AXES = "map metres: x east, y north, z up (ground height at the trunk foot)"
WORK_INPUTS = ("terrain.json", "terrain_heights.npy", "terrain_valid.npy", "roads.json",
               "props.json", "spawns.json", "lamps.json", "detail.json", "detail_patches.npy",
               "detail_patches.json", "detail_meadow.bin", "detail_crops.json")
NOT_GENERATED_NOTE = "Landmarks, navmesh and water surface meshes are not generated (water.json holds the water bodies). Textures are not in the map: textured nodes name a texture of the asset library."


def to_gltf_axes(tris: np.ndarray) -> np.ndarray:
    """Local (x east, y north, z up) -> glTF (x east, y up, z -north); a proper rotation."""
    out = np.empty_like(tris, dtype=np.float64)
    out[..., 0] = tris[..., 0]
    out[..., 1] = tris[..., 2]
    out[..., 2] = -tris[..., 1]
    return np.round(out, 4)


def flat_normals(verts: np.ndarray) -> np.ndarray:
    """One unit normal per vertex of unshared triangles (``verts`` is (3n, 3)): the face normal of its
    triangle (counter-clockwise front). A degenerate triangle gets straight up.

    Without NORMAL the game shades every map surface with one constant normal, so a lamp or a
    headlight lights the ground only on one side of itself.
    """
    t = verts.reshape(-1, 3, 3)
    n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    length = np.linalg.norm(n, axis=1, keepdims=True)
    n = np.where(length > 1e-12, n / np.maximum(length, 1e-12), np.array([0.0, 1.0, 0.0]))
    return np.repeat(n, 3, axis=0).astype(np.float32)


def to_trimesh(mesh: Mesh, normals: bool = True) -> trimesh.Trimesh:
    """Flat-shaded trimesh: unshared vertices, one RGBA vertex colour and one normal per triangle.

    ``normals=False`` for a collision mesh (never drawn): no NORMAL attribute, a smaller file.
    """
    verts = to_gltf_axes(mesh.tris).reshape(-1, 3)
    faces = np.arange(len(verts)).reshape(-1, 3)
    rgba = np.concatenate([mesh.colours, np.full((len(mesh.colours), 1), 255, np.uint8)], axis=1)
    tm = trimesh.Trimesh(
        vertices=verts,
        faces=faces,
        vertex_colors=np.repeat(rgba, 3, axis=0),
        vertex_normals=flat_normals(verts) if normals and len(verts) else None,
        process=False,
    )
    if mesh.bases is not None and not np.isnan(mesh.bases).all():
        # (POSITION x, POSITION z) of the tree base: glTF axes of (x, y, z) are (x, z, -y); same rounding as the positions
        uv2 = np.round(np.stack([mesh.bases[:, 0], -mesh.bases[:, 1]], axis=1), 4)
        uv2[np.isnan(uv2).any(axis=1)] = NO_TREE_UV2
        tm.vertex_attributes[BASE_UV2_ATTRIBUTE] = np.repeat(uv2, 3, axis=0).astype(np.float32)
    return tm


def to_trimesh_textured(mesh: TexturedMesh, tid: str) -> trimesh.Trimesh:
    """Flat-shaded trimesh with UVs (TEXCOORD_0) and the tint as vertex colour (COLOR_0).

    The material only carries the name ``tex:<id>``; no image is embedded (see ``textures.py``).
    """
    verts = to_gltf_axes(mesh.tris).reshape(-1, 3)
    rgba = np.concatenate([mesh.colours, np.full((len(mesh.colours), 1), 255, np.uint8)], axis=1)
    visual = trimesh.visual.TextureVisuals(
        uv=mesh.uvs.reshape(-1, 2),
        material=trimesh.visual.material.PBRMaterial(
            name=f"tex:{tid}", roughnessFactor=1.0, metallicFactor=0.0
        ),
    )
    visual.vertex_attributes["color"] = np.repeat(rgba, 3, axis=0)
    return trimesh.Trimesh(
        vertices=verts, faces=np.arange(len(verts)).reshape(-1, 3), visual=visual,
        vertex_normals=flat_normals(verts) if len(verts) else None, process=False,
    )


def node_trimesh(name: str, mesh: Mesh | TexturedMesh) -> trimesh.Trimesh:
    if isinstance(mesh, TexturedMesh):
        return to_trimesh_textured(mesh, name.partition("_tex_")[2])
    return to_trimesh(mesh, normals=not name.endswith("collision"))


def export_glb(scene: trimesh.Scene) -> bytes:
    """``scene`` as GLB bytes, with the base attribute (``_UV2``, see ``to_trimesh``) renamed to ``TEXCOORD_1``.

    trimesh can only write TEXCOORD_0 itself. Every GLB that may carry tree meshes goes through here.
    """
    data = scene.export(file_type="glb")
    jlen, jtype = struct.unpack_from("<II", data, 12)
    assert jtype == 0x4E4F534A  # "JSON"
    doc = json.loads(data[20 : 20 + jlen])
    found = False
    for mesh in doc.get("meshes", []):
        for prim in mesh["primitives"]:
            if BASE_UV2_ATTRIBUTE in prim["attributes"]:
                prim["attributes"]["TEXCOORD_1"] = prim["attributes"].pop(BASE_UV2_ATTRIBUTE)
                found = True
    if not found:
        return data
    blob = json.dumps(doc, separators=(",", ":")).encode()
    blob += b" " * (-len(blob) % 4)
    rest = data[20 + jlen :]
    return struct.pack("<4sIIII", b"glTF", 2, 20 + len(blob) + len(rest), len(blob), jtype) + blob + rest


def parcel_props_nodes(
    items: list[tuple[str, Mesh, Mesh, dict[str, TexturedMesh]]],
) -> dict[str, Mesh | TexturedMesh]:
    """``parcel_<ID>_props`` (visual), ``parcel_<ID>_props_collision`` and one
    ``parcel_<ID>_props_tex_<texture id>`` per texture used; empty ones omitted."""
    out: dict[str, Mesh | TexturedMesh] = {}
    for pid, visual, collision, textured in sorted(items, key=lambda t: t[0]):
        for suffix, mesh in (("props", visual), ("props_collision", collision)):
            if mesh.face_count:
                out[f"parcel_{pid}_{suffix}"] = mesh
        for tid, mesh in sorted(textured.items()):
            if mesh.face_count:
                out[tex_node_name(f"parcel_{pid}_props", tid)] = mesh
    return out


def chunk_name(ix: int, iy: int) -> str:
    return f"chunk_{ix}_{iy}"


def build_chunk_scene(
    parcel_meshes: list[tuple[str, Mesh, Mesh]],
    extra: dict[str, Mesh | TexturedMesh] | None = None,
) -> trimesh.Scene:
    """Nodes of one chunk: parcel nodes plus terrain, terrain_collision, roads, props, ..."""
    scene = trimesh.Scene()
    for pid, visual, collision in sorted(parcel_meshes, key=lambda t: t[0]):
        for suffix, mesh in (("visual", visual), ("collision", collision)):
            name = f"parcel_{pid}_{suffix}"
            scene.add_geometry(to_trimesh(mesh, normals=suffix == "visual"), node_name=name, geom_name=name)
    for name, mesh in sorted((extra or {}).items()):
        scene.add_geometry(node_trimesh(name, mesh), node_name=name, geom_name=name)
    return scene


def write_clutter_meshes(path: Path) -> str:
    """``clutter_meshes.glb``: one node per clutter kind, flat colours, glTF axes (Y up), at scale 1 on y = 0."""
    scene = trimesh.Scene()
    for kind, mesh in clutter_meshes().items():
        scene.add_geometry(to_trimesh(mesh), node_name=kind, geom_name=kind)
    path.write_bytes(scene.export(file_type="glb"))
    return path.name


def write_primitives(out_dir: Path) -> dict[str, dict]:
    """``prims/<name>.glb`` for every ``prim:<name>``: flat colours, glTF axes like the chunk files.

    The loader instances these like any glTF and never builds meshes itself. Returns the manifest
    entries ``{name: {"file": ..., "sha256": ...}}``.
    """
    prims_dir = out_dir / "prims"
    prims_dir.mkdir(parents=True, exist_ok=True)
    for old in prims_dir.glob("*.glb"):
        old.unlink()
    entries = {}
    for name in sorted(PRIMITIVES):
        scene = build_chunk_scene([], {name: build_primitive(name)})
        data = scene.export(file_type="glb")
        (prims_dir / f"{name}.glb").write_bytes(data)
        entries[name] = {"file": f"prims/{name}.glb", "sha256": hashlib.sha256(data).hexdigest()}
    return entries


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _on_terrain(areas: list[dict], terrain) -> list[dict]:
    """The ground areas limited to the terrain: nothing is defined off the playing area (no terrain: as they are)."""
    return areas if terrain is None else clip_areas(areas, terrain.coverage)


def forest_fields(tree: dict) -> dict:
    """A forest tree's trees.json fields: the flag, its stand (props.Stands; mixed when the props.json has none) and, where true, ``edge``."""
    if not is_forest(tree):
        return {}
    return {"forest": 1, "stand": tree.get("stand", "mixed"), **({"edge": True} if tree.get("edge") else {})}


def forest_model(tree_cfg: dict) -> dict:
    """The forest tree numbers of trees.json's model block (metres at scale 1; the same ones ``tree_mesh`` draws)."""
    f = tree_cfg["forest"]
    return {
        "trunk_width_m": float(f["trunk_width_m"]),
        "trunk_height_m": float(f["trunk_height_m"]),
        "trunk_top_ratio": float(f["trunk_top_ratio"]),
        "cone_tiers": [[float(v) for v in t] for t in f["cone_tiers"]],
        "box_tiers": [[float(v) for v in t] for t in f["box_tiers"]],
    }


def ground_model(cfg: dict) -> dict:
    """The ground object numbers of ground_objects.json's model block (metres at scale 1; the same ones ``ground_object_mesh`` draws)."""
    return {kind: {k: float(v) for k, v in cfg["model"][kind].items()} for kind in ("log", "stump", "boulder", "rootplate", "anthill", "burrow")}


def ground_objects_doc(objects: list[dict], cfg: dict, chunk_m: float) -> dict:
    """The ``ground_objects.json`` document. ``rot`` is the yaw as for trees: u = (cos rot, sin rot). A log lies along u (thick end at -u,
    ``len`` metres long, its midpoint at x, y); a root plate's flat faces are across u (a log's plate stands at its -u end)."""
    return {
        "format_version": GROUND_OBJECTS_FORMAT_VERSION,
        "axes": TREES_AXES,
        "chunk_size_m": chunk_m,
        "model": {"base_uv2": True, "baked_only": sorted(GROUND_BAKED_ONLY), "kinds": ground_model(cfg)},
        "objects": [
            {
                "kind": o["kind"], "x": o["x"], "y": o["y"], "z": round(o["z"], 3), "rot": o["rot"], "scale": o["scale"],
                **({"len": o["len"]} if o["kind"] == "log" else {}),
                "chunk": [int(math.floor(o["x"] / chunk_m)), int(math.floor(o["y"] / chunk_m))],
            }
            for o in objects
        ],
    }


def _dump(path: Path, doc: dict) -> None:
    # compact: the map loader parses these at every start, and indentation was most of trees.json (1.8 MB)
    path.write_text(json.dumps(doc, separators=(",", ":"), sort_keys=True) + "\n")


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


GROUPS_FILE = "groups.json"


def build_groups(edits_path: Path, parcel_ids) -> dict | None:
    """The ``groups.json`` document (``{"version": 1, "groups": {"G-0001": {"parcels": [...]}}}``) or None when no group has a parcel of the map.

    The shape is the ``groups`` section of the edits file, which the game's ``PropertyGroups.Parse`` reads; only the parcels the map has are kept."""
    known = set(parcel_ids)
    groups = {}
    for gid, entry in sorted(load_edits(edits_path).groups.items()):
        members = sorted({pid for pid in entry.get("parcels", []) if pid in known})
        if members:
            groups[gid] = {"parcels": members}
    return {"version": 1, "groups": groups} if groups else None


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
    parcels_out = []
    for p in sorted(parcels_doc["parcels"], key=lambda p: p["id"]):
        parcels_out.append(
            {
                "id": p["id"],
                "cadastral_ref": p["cadastral_ref"],
                "kind": p["kind"],
                "polygon": p["polygon"],
                "anchor": p["anchor"],
                "chunk": p["chunk"],
                "buildings": sorted(bid for bid in p["buildings"] if bid in buildings),
            }
        )

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

    files = {}
    objects_src = work_dir / "objects.json"
    if objects_src.is_file():
        # placed objects (props stage); copied verbatim so the loader reads what the build planned
        (out_dir / "objects.json").write_bytes(objects_src.read_bytes())
        files["objects"] = "objects.json"
    prims = write_primitives(out_dir)
    if scene.graph is not None:
        _dump(out_dir / "roads.json", scene.graph)
        files["roads"] = "roads.json"
    if scene.spawns is not None:
        _dump(out_dir / "spawns.json", scene.spawns)
        files["spawns"] = "spawns.json"
    if scene.trees is not None:
        # tree collision solids (trunk and crown), built by the game from these records and the model numbers
        tree_cfg = load_section("props", rules_path)["trees"]
        chunk_m = float(parcels_doc["chunk_size_m"])
        _dump(out_dir / "trees.json", {
            "format_version": TREES_FORMAT_VERSION,
            "axes": TREES_AXES,
            "chunk_size_m": chunk_m,
            "model": {
                "trunk_width_m": float(tree_cfg["trunk_width_m"]),
                "trunk_height_m": float(tree_cfg["trunk_height_m"]),
                "canopy_m": float(tree_cfg["canopy_m"]),
                "base_uv2": True,  # the chunk props meshes carry each tree's base as TEXCOORD_1 (map 0.12.0)
                **({"forest": forest_model(tree_cfg)} if "forest" in tree_cfg else {}),
            },
            "trees": [
                {
                    "x": t["x"], "y": t["y"], "z": round(t["z"], 3), "scale": t["scale"], "rot": t["rot"],
                    "shape": t["shape"],
                    **({"colour": t["colour"]} if t.get("colour") else {}),  # the canopy colour: the game's detailed tree takes it, so the swap keeps the green
                    **forest_fields(t),
                    "chunk": [int(math.floor(t["x"] / chunk_m)), int(math.floor(t["y"] / chunk_m))],
                }
                for t in scene.trees
            ],
        })
        files["trees"] = "trees.json"
    ground_file = out_dir / "ground_objects.json"
    if scene.ground_objects is not None:
        # solid ground objects of the forest (props stage): the game builds colliders from the records and the model numbers
        _dump(ground_file, ground_objects_doc(
            scene.ground_objects, load_section("props", rules_path)["ground_objects"], float(parcels_doc["chunk_size_m"])))
        files["ground_objects"] = "ground_objects.json"
    else:
        ground_file.unlink(missing_ok=True)  # props.json from before this file existed: no objects
    fences_src = work_dir / "fences.json"
    if fences_src.is_file():
        # fence sections (props stage); copied verbatim, the game server hops pets over them
        (out_dir / "fences.json").write_bytes(fences_src.read_bytes())
        files["fences"] = "fences.json"
    else:
        (out_dir / "fences.json").unlink(missing_ok=True)
    lamps_src = work_dir / "lamps.json"
    if lamps_src.is_file():
        # street lamp positions (props stage); copied verbatim, the game builds the lamps from them
        (out_dir / "lamps.json").write_bytes(lamps_src.read_bytes())
        files["lamps"] = "lamps.json"
    clutter_src = work_dir / "detail.json"
    if clutter_src.is_file():
        # clutter instances (detail stage); copied verbatim, with the one mesh per kind they are drawn with
        (out_dir / "clutter.json").write_bytes(clutter_src.read_bytes())
        files["clutter"] = "clutter.json"
        files["clutter_meshes"] = write_clutter_meshes(out_dir / "clutter_meshes.glb")
    else:
        for stale in ("clutter.json", "clutter_meshes.glb"):
            (out_dir / stale).unlink(missing_ok=True)
    crops_src = work_dir / "detail_crops.json"
    if crops_src.is_file():
        # crop plants and artifacts (detail stage, crop recipe); copied verbatim, the game draws each kind with its own model
        (out_dir / "crops.json").write_bytes(crops_src.read_bytes())
        files["crops"] = "crops.json"
    else:
        (out_dir / "crops.json").unlink(missing_ok=True)
    meadow_src = work_dir / "detail_meadow.bin"
    if meadow_src.is_file():
        # density raster of the meadow clutter (detail stage); copied verbatim, the game generates the instances from it
        (out_dir / "meadow.bin").write_bytes(meadow_src.read_bytes())
        files["meadow"] = "meadow.bin"
    else:
        (out_dir / "meadow.bin").unlink(missing_ok=True)
    stale_windows = out_dir / "windows.json"
    stale_windows.unlink(missing_ok=True)
    if scene.windows:
        # tagged window panes (building_detail): node name, position, size, facade, breakable
        _dump(stale_windows, {"version": 1, "windows": sorted(scene.windows, key=lambda w: w["node"])})
        files["windows"] = "windows.json"
    stale_bunkers = out_dir / "bunkers.json"
    stale_bunkers.unlink(missing_ok=True)
    if scene.bunkers:
        # the rooms of the enterable bunkers (bunker.bunker_rooms): the game shelters a player inside from a blast outside
        _dump(stale_bunkers, {
            "version": 1,
            "axes": "polygon: local metres, x east, y north (as parcels.json); z0, z1: metres, as terrain.json",
            "bunkers": sorted(scene.bunkers, key=lambda b: b["id"]),
        })
        files["bunkers"] = "bunkers.json"
    stale_church = out_dir / "church.json"
    stale_church.unlink(missing_ok=True)
    if scene.church:
        # the inside of the church (church_interior.church_info): seats and routes, spots, demon anchors, shelter rooms
        _dump(stale_church, {
            "version": 1,
            "axes": "points: local metres [x east, y north, z up] (x, y as parcels.json, z as terrain.json); yaw: compass degrees",
            **scene.church,
        })
        files["church"] = "church.json"
    stale_urad = out_dir / "urad.json"
    stale_urad.unlink(missing_ok=True)
    if scene.urad:
        # the inside of the municipal office (office_interior.urad_info): entrance, rooms, worker seats, dictation desk
        _dump(stale_urad, {
            "version": 1,
            "axes": "points: local metres [x east, y north, z up] (x, y as parcels.json, z as terrain.json); yaw: compass degrees",
            **scene.urad,
        })
        files["urad"] = "urad.json"
    stale_garage = out_dir / "garage.json"
    stale_garage.unlink(missing_ok=True)
    if scene.garage:
        # the bus garage (garage.garage_info): the doorway of its swinging doors, the bay, the parking place
        _dump(stale_garage, {
            "version": 1,
            "axes": "points: local metres [x east, y north, z up] (x, y as parcels.json, z as terrain.json); yaw: compass degrees",
            **scene.garage,
        })
        files["garage"] = "garage.json"

    parcels_file = {
        "version": ARTIFACT_FORMAT_VERSION,
        "crs": parcels_doc["crs"],
        "axes": "x east, y north (local metres); glTF files use Y-up: x east, y up, z -north",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "parcels": parcels_out,
        # the ground areas by type (ground.py: kind = the type): the editor's Ground types tool draws them to show what is where
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
    _dump(out_dir / "parcels.json", parcels_file)
    water_file = out_dir / "water.json"
    zone_edits = load_edits(edits_path)
    origin = parcels_doc.get("origin_epsg5514")
    drawn = drawn_zones(zone_edits.zones, origin) if origin else {}
    cuts = zone_cuts(zone_edits.zone_cuts, origin) if origin else {}
    if scene.water is not None:
        for pid in scene.water.skipped:
            print(f"export: warning: water parcel/patch {pid} has no terrain under it (or none below its surface); no water body", file=sys.stderr)
        water = water_doc(scene.water.bodies, water_rules(rules_path))
        _dump(water_file, water)
        files["water"] = "water.json"
        # candidate points of the zone kinds, drawn clear of the buildings and of the water just built
        points_doc, points_warnings = build_zone_points(
            parcels_doc, drawn, list(buildings.values()), scene.terrain, water, zrules, cuts
        )
        for warning in points_warnings:
            print(f"export: warning: zone points: {warning}", file=sys.stderr)
    else:
        points_doc = None
        if water_file.exists():
            water_file.unlink()  # no terrain: a water file from an earlier run would be stale
    points_file = out_dir / ZONE_POINTS_FILE
    if points_doc is not None:
        _dump(points_file, points_doc)
        files["zone_points"] = ZONE_POINTS_FILE
    else:
        points_file.unlink(missing_ok=True)  # no zone has an area (any more): no file, "no points"
    polygons_doc = build_zone_polygons(parcels_doc, drawn, zrules, cuts)
    polygons_file = out_dir / ZONE_POLYGONS_FILE
    if polygons_doc is not None:
        _dump(polygons_file, polygons_doc)
        files["zone_polygons"] = ZONE_POLYGONS_FILE
    else:
        polygons_file.unlink(missing_ok=True)  # no plantation zone (any more): no file, the feature is off
    groups_doc = build_groups(edits_path, (p["id"] for p in parcels_doc["parcels"]))
    groups_file = out_dir / GROUPS_FILE
    if groups_doc is not None:
        _dump(groups_file, groups_doc)
        files["groups"] = GROUPS_FILE
    else:
        groups_file.unlink(missing_ok=True)  # no property group (any more): no file, "no pets"
    patches_file = out_dir / "patches.json"
    ground_patches = [p for p in scene.patches.values() if p.water_y is None]  # a water patch is in water.json
    if ground_patches:
        # ground patches for the minimap: local metres (x east, y north), drawn in ID order so the higher ID wins
        ordered = sorted(ground_patches, key=lambda p: patch_rank(p.id))
        _dump(patches_file, {"version": 1, "patches": [
            {"id": p.id, "type": p.type, "colour": list(p.colour),
             "ring": [[round(float(x), 2), round(float(y), 2)] for x, y in piece.exterior.coords]}
            for p in ordered
            for piece in getattr(p.polygon, "geoms", [p.polygon])
        ]})
        files["patches"] = "patches.json"
    elif patches_file.exists():
        patches_file.unlink()
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
    inputs = {
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
        **({"config/tree_overrides.yaml": _sha256(tree_overrides)} if (tree_overrides := overrides_path.parent / "tree_overrides.yaml").is_file() else {}),
    }
    manifest = {
        "map_version": f"{MAP_VERSION_BASE}-village",
        "format_version": ARTIFACT_FORMAT_VERSION,
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "props_layout": PROPS_LAYOUT,
        "gltf_axes": "Y-up: x east, y up, z -north, metres, shared map-origin frame",
        "chunks": chunk_entries,
        "attribution": raw_manifest.get("attribution", []),
        "inputs": inputs,
        "generated": sorted(generated),
        "files": files,
        "edit_defaults": _edit_defaults(rules_path),
        "textures": sorted(
            {name.partition("_tex_")[2] for c in chunk_entries for name in c["triangles"] if "_tex_" in name}
        ),
        "prims": prims,
        "terrain": terrain_info,
        "not_generated": NOT_GENERATED_NOTE,
    }
    _dump(out_dir / "manifest.json", manifest)
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
