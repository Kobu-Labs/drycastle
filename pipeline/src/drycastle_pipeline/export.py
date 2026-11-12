"""Export stage: ``data/work`` -> ``out/map/`` (chunk glTFs, parcels.json, manifest.json).

Output is byte-deterministic: no timestamps, sorted keys, sorted node order. glTF axes are Y-up:
glTF (x, y, z) = local (east, up, -north). parcels.json keeps local metres (x east, y north).
"""

import hashlib
import json
import math
import sys
from pathlib import Path

import numpy as np
import trimesh

from drycastle_pipeline.buildings import DEFAULT_OVERRIDES
from drycastle_pipeline.config import DEFAULT_AREA, DEFAULT_RAW_DIR, REPO_ROOT, area_paths
from drycastle_pipeline.edits import DEFAULT_EDITS
from drycastle_pipeline.detail_meshes import clutter_meshes
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.primitives import PRIMITIVES, build_primitive
from drycastle_pipeline.regions import region_rank
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.scene import build_scene
from drycastle_pipeline.textures import TexturedMesh, tex_node_name
from drycastle_pipeline.ground import ground_types, ground_units, water_kinds
from drycastle_pipeline.water import build_water

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
# (no bump) bunkers.json (files.bunkers): the rooms of the enterable bunkers, per floor a polygon and a height range.
# Only maps with a bunker have it; a loader that does not know it gives no shelter from blasts.
MAP_VERSION_BASE = "0.10.0"
PROPS_LAYOUT = "per_parcel"
ARTIFACT_FORMAT_VERSION = 1
TREES_FORMAT_VERSION = 1
WORK_INPUTS = ("terrain.json", "terrain_heights.npy", "terrain_valid.npy", "roads.json",
               "props.json", "spawns.json", "lamps.json", "detail.json", "detail_patches.npy",
               "detail_patches.json", "detail_meadow.bin")
NOT_GENERATED_NOTE = "Landmarks, navmesh and water surface meshes are not generated (water.json holds the water bodies). Textures are not in the map: textured nodes name a texture of the asset library."


def to_gltf_axes(tris: np.ndarray) -> np.ndarray:
    """Local (x east, y north, z up) -> glTF (x east, y up, z -north); a proper rotation."""
    out = np.empty_like(tris, dtype=np.float64)
    out[..., 0] = tris[..., 0]
    out[..., 1] = tris[..., 2]
    out[..., 2] = -tris[..., 1]
    return np.round(out, 4)


def to_trimesh(mesh: Mesh) -> trimesh.Trimesh:
    """Flat-shaded trimesh: unshared vertices, one RGBA vertex colour per triangle."""
    verts = to_gltf_axes(mesh.tris).reshape(-1, 3)
    faces = np.arange(len(verts)).reshape(-1, 3)
    rgba = np.concatenate([mesh.colours, np.full((len(mesh.colours), 1), 255, np.uint8)], axis=1)
    return trimesh.Trimesh(
        vertices=verts,
        faces=faces,
        vertex_colors=np.repeat(rgba, 3, axis=0),
        process=False,
    )


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
        vertices=verts, faces=np.arange(len(verts)).reshape(-1, 3), visual=visual, process=False
    )


def node_trimesh(name: str, mesh: Mesh | TexturedMesh) -> trimesh.Trimesh:
    if isinstance(mesh, TexturedMesh):
        return to_trimesh_textured(mesh, name.partition("_tex_")[2])
    return to_trimesh(mesh)


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
            scene.add_geometry(to_trimesh(mesh), node_name=name, geom_name=name)
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


def _dump(path: Path, doc: dict) -> None:
    # compact: the map loader parses these at every start, and indentation was most of trees.json (1.8 MB)
    path.write_text(json.dumps(doc, separators=(",", ":"), sort_keys=True) + "\n")


def _edit_defaults(rules_path: Path, area: str) -> dict:
    """Rule values the editor shows as the greyed defaults of fences and trees (area overlay applied)."""
    props = load_section("props", rules_path, area)
    fences, trees = props["fences"], props["trees"]
    return {
        "fence": {
            "kinds": list(fences["kinds"]),
            "probability": fences["probability"],
            "height_m": fences["height_m"],
            "thickness_m": fences["thickness_m"],
            "colour": fences["colour"],
        },
        "trees": {
            "density_per_1000m2": dict(trees["density_per_1000m2"]),
        },
    }


def run_export(
    work_dir: Path = DEFAULT_WORK_DIR,
    out_dir: Path = DEFAULT_OUT_DIR,
    raw_dir: Path = DEFAULT_RAW_DIR,
    rules_path: Path = DEFAULT_RULES,
    overrides_path: Path = DEFAULT_OVERRIDES,
    area: str = DEFAULT_AREA,
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

    scene = build_scene(work_dir, rules_path, area=area, edits_path=edits_path)
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
        data = build_chunk_scene(items, extra).export(file_type="glb")
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
        tree_cfg = load_section("props", rules_path, area)["trees"]
        chunk_m = float(parcels_doc["chunk_size_m"])
        _dump(out_dir / "trees.json", {
            "format_version": TREES_FORMAT_VERSION,
            "axes": "map metres: x east, y north, z up (ground height at the trunk foot)",
            "chunk_size_m": chunk_m,
            "model": {
                "trunk_width_m": float(tree_cfg["trunk_width_m"]),
                "trunk_height_m": float(tree_cfg["trunk_height_m"]),
                "canopy_m": float(tree_cfg["canopy_m"]),
            },
            "trees": [
                {
                    "x": t["x"], "y": t["y"], "z": round(t["z"], 3), "scale": t["scale"], "rot": t["rot"],
                    "shape": t["shape"],
                    "chunk": [int(math.floor(t["x"] / chunk_m)), int(math.floor(t["y"] / chunk_m))],
                }
                for t in scene.trees
            ],
        })
        files["trees"] = "trees.json"
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

    parcels_file = {
        "version": ARTIFACT_FORMAT_VERSION,
        "crs": parcels_doc["crs"],
        "axes": "x east, y north (local metres); glTF files use Y-up: x east, y up, z -north",
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "parcels": parcels_out,
    }
    if "origin_epsg5514" in parcels_doc:  # always there from the parcels stage on
        parcels_file["origin_epsg5514"] = parcels_doc["origin_epsg5514"]
    # the ground types a region can be given in the editor (hand-drawn ground areas): not the ones a rule makes, and
    # not water (a water region has a level instead)
    parcels_file["ground_types"] = [
        {"name": name, "colour": (spec.get("colours") or ["#808080"])[0].lower()}
        for name, spec in ground_types(rules_path, area).items()
        if not spec.get("rule") and not spec.get("water")
    ]
    _dump(out_dir / "parcels.json", parcels_file)
    water_file = out_dir / "water.json"
    if scene.terrain is not None:
        water_doc, water_skipped = build_water(
            ground_units(parcels_doc), scene.terrain, scene.regions, water_kinds(ground_types(rules_path, area))
        )
        for pid in water_skipped:
            print(f"export: warning: water parcel/region {pid} has no terrain under it (or none below its surface); no water body", file=sys.stderr)
        _dump(water_file, water_doc)
        files["water"] = "water.json"
    elif water_file.exists():
        water_file.unlink()  # no terrain: a water file from an earlier run would be stale
    regions_file = out_dir / "regions.json"
    if scene.regions:
        # ground paint for the minimap: local metres (x east, y north), drawn in ID order so the higher ID wins
        ordered = sorted(scene.regions.values(), key=lambda r: region_rank(r.id))
        _dump(regions_file, {"regions": [
            {"id": r.id, "colour": list(r.colour), "ring": [[round(float(x), 2), round(float(y), 2)] for x, y in r.polygon.exterior.coords]}
            for r in ordered
        ]})
        files["regions"] = "regions.json"
    elif regions_file.exists():
        regions_file.unlink()
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
        if scene.spawns is not None:
            generated |= {"spawns"}
    if any(name.startswith("region_") for nodes in scene.nodes.values() for name in nodes):
        generated.add("regions")
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
    }
    manifest = {
        "map_version": f"{MAP_VERSION_BASE}-{area}",
        "format_version": ARTIFACT_FORMAT_VERSION,
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "props_layout": PROPS_LAYOUT,
        "gltf_axes": "Y-up: x east, y up, z -north, metres, shared slice-origin frame",
        "chunks": chunk_entries,
        "attribution": raw_manifest.get("attribution", []),
        "inputs": inputs,
        "generated": sorted(generated),
        "files": files,
        "edit_defaults": _edit_defaults(rules_path, area),
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
    area: str = DEFAULT_AREA,
    out: str | None = None,
    work: str | None = None,
    edits: str | None = None,
    **_options: object,
) -> int:
    """``out`` and ``work`` override the area's map and work directories (experiments, tests)."""
    paths = area_paths(area)
    out_dir = Path(out) if out else paths.out
    try:
        manifest = run_export(Path(work) if work else paths.work, out_dir, paths.raw, area=area,
                              edits_path=Path(edits) if edits else DEFAULT_EDITS)
    except FileNotFoundError as exc:
        print(f"export: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    print(f"export: {len(manifest['chunks'])} chunk file(s) written to {out_dir}/")
    return 0
