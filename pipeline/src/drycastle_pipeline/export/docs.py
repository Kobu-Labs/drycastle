"""Writers and builders of the small JSON/GLB documents of the map (trees, ground objects, groups, primitives, graves, clutter meshes)."""

import hashlib
import json
import math
import re
from pathlib import Path

import trimesh

from drycastle_pipeline.detail.meshes import clutter_meshes
from drycastle_pipeline.edits.schema import load_edits
from drycastle_pipeline.export.gltf import build_chunk_scene, to_trimesh
from drycastle_pipeline.kits.primitives import PRIMITIVES, build_primitive
from drycastle_pipeline.props.propmeshes import GROUND_BAKED_ONLY, is_forest

TREES_FORMAT_VERSION = 1
GROUND_OBJECTS_FORMAT_VERSION = 1
TREES_AXES = "map metres: x east, y north, z up (ground height at the trunk foot)"
GROUPS_FILE = "groups.json"


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


def write_graves(out_dir: Path) -> int:
    """``graves/<id>.glb`` for every placed ``prim:grave`` of ``objects.json`` (its own mesh at its real size, ``graves.object_mesh``),
    and that object's ``model`` in ``objects.json``: the loader draws it in place of the asset's shared model. Returns how many."""
    from drycastle_pipeline.kits import graves

    path = out_dir / "objects.json"
    doc = json.loads(path.read_text())
    graves_dir = out_dir / "graves"
    if graves_dir.is_dir():
        for old in graves_dir.glob("*.glb"):
            old.unlink()
    count = 0
    for obj in doc.get("objects", []):
        if obj.get("asset") != graves.ASSET:
            continue
        graves_dir.mkdir(parents=True, exist_ok=True)
        name = re.sub(r"[^A-Za-z0-9_-]", "_", obj["id"])
        scene = build_chunk_scene([], {name: graves.object_mesh(obj)})
        (graves_dir / f"{name}.glb").write_bytes(scene.export(file_type="glb"))
        obj["model"] = f"graves/{name}.glb"
        count += 1
    if count:
        path.write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")  # as props writes it
    return count


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
                "chunk": [math.floor(o["x"] / chunk_m), math.floor(o["y"] / chunk_m)],
            }
            for o in objects
        ],
    }


def trees_doc(trees: list[dict], tree_cfg: dict, chunk_m: float) -> dict:
    """The ``trees.json`` document: tree collision solids (trunk and crown), built by the game from these records and the model numbers."""
    return {
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
                "chunk": [math.floor(t["x"] / chunk_m), math.floor(t["y"] / chunk_m)],
            }
            for t in trees
        ],
    }


def dump_doc(path: Path, doc: dict) -> None:
    # compact: the map loader parses these at every start, and indentation was most of trees.json (1.8 MB)
    path.write_text(json.dumps(doc, separators=(",", ":"), sort_keys=True) + "\n")


def write_optional(out_dir: Path, name: str, doc: dict | None, files: dict, key: str) -> None:
    """``name`` in ``out_dir`` holds ``doc`` and ``files[key]`` names it; no doc (any more): no file, no entry."""
    path = out_dir / name
    if doc is None:
        path.unlink(missing_ok=True)
        return
    dump_doc(path, doc)
    files[key] = name


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
