"""Export stage: ``data/work`` -> ``out/map/`` (chunk glTFs, parcels.json, manifest.json).

Output is byte-deterministic: no timestamps, sorted keys, sorted node order. glTF axes are Y-up:
glTF (x, y, z) = local (east, up, -north). parcels.json keeps local metres (x east, y north).
"""

import hashlib
import json
from pathlib import Path

import numpy as np
import trimesh

from suchohrad_pipeline.buildings import DEFAULT_OVERRIDES
from suchohrad_pipeline.config import DEFAULT_RAW_DIR, REPO_ROOT
from suchohrad_pipeline.meshes import Mesh, building_mesh, collision_mesh
from suchohrad_pipeline.normalise import DEFAULT_WORK_DIR
from suchohrad_pipeline.rules import DEFAULT_RULES

DEFAULT_OUT_DIR = REPO_ROOT / "out" / "map"
MAP_VERSION = "0.1.0-slice"
ARTIFACT_FORMAT_VERSION = 1
NOT_GENERATED_NOTE = (
    "Terrain, roads, fences, trees, spawn candidates and roads.json are not generated yet; "
    "chunk files contain only parcel building nodes."
)


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


def chunk_name(ix: int, iy: int) -> str:
    return f"chunk_{ix}_{iy}"


def build_chunk_scene(parcel_meshes: list[tuple[str, Mesh, Mesh]]) -> trimesh.Scene:
    scene = trimesh.Scene()
    for pid, visual, collision in sorted(parcel_meshes, key=lambda t: t[0]):
        for suffix, mesh in (("visual", visual), ("collision", collision)):
            name = f"parcel_{pid}_{suffix}"
            scene.add_geometry(to_trimesh(mesh), node_name=name, geom_name=name)
    return scene


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _dump(path: Path, doc: dict) -> None:
    path.write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")


def run_export(
    work_dir: Path = DEFAULT_WORK_DIR,
    out_dir: Path = DEFAULT_OUT_DIR,
    raw_dir: Path = DEFAULT_RAW_DIR,
    rules_path: Path = DEFAULT_RULES,
    overrides_path: Path = DEFAULT_OVERRIDES,
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

    by_chunk: dict[tuple[int, int], list[tuple[str, Mesh, Mesh]]] = {}
    parcels_out = []
    for p in sorted(parcels_doc["parcels"], key=lambda p: p["id"]):
        ids = [bid for bid in p["buildings"] if bid in buildings]
        if ids:
            blds = [buildings[bid] for bid in sorted(ids)]
            visual = Mesh.concat([building_mesh(b) for b in blds])
            collision = Mesh.concat([collision_mesh(b) for b in blds])
            by_chunk.setdefault(tuple(p["chunk"]), []).append((p["id"], visual, collision))
        parcels_out.append(
            {
                "id": p["id"],
                "cadastral_ref": p["cadastral_ref"],
                "kind": p["kind"],
                "polygon": p["polygon"],
                "anchor": p["anchor"],
                "chunk": p["chunk"],
                "buildings": sorted(ids),
            }
        )

    chunk_entries = []
    for (ix, iy), items in sorted(by_chunk.items()):
        rel = f"chunks/{chunk_name(ix, iy)}.glb"
        data = build_chunk_scene(items).export(file_type="glb")
        (out_dir / rel).write_bytes(data)
        chunk_entries.append(
            {
                "index": [ix, iy],
                "file": rel,
                "parcels": sorted(pid for pid, _, _ in items),
                "sha256": hashlib.sha256(data).hexdigest(),
            }
        )

    _dump(
        out_dir / "parcels.json",
        {
            "version": ARTIFACT_FORMAT_VERSION,
            "crs": parcels_doc["crs"],
            "axes": "x east, y north (local metres); glTF files use Y-up: x east, y up, z -north",
            "chunk_size_m": parcels_doc["chunk_size_m"],
            "parcels": parcels_out,
        },
    )
    inputs = {
        "raw": {f["file"]: f["sha256"] for f in raw_manifest.get("files", [])},
        "work/parcels.json": _sha256(work_dir / "parcels.json"),
        "work/buildings.json": _sha256(work_dir / "buildings.json"),
        "config/rules.yaml": _sha256(rules_path),
        "config/overrides.yaml": _sha256(overrides_path),
    }
    manifest = {
        "map_version": MAP_VERSION,
        "format_version": ARTIFACT_FORMAT_VERSION,
        "chunk_size_m": parcels_doc["chunk_size_m"],
        "gltf_axes": "Y-up: x east, y up, z -north, metres, shared slice-origin frame",
        "chunks": chunk_entries,
        "attribution": raw_manifest.get("attribution", []),
        "inputs": inputs,
        "not_generated": NOT_GENERATED_NOTE,
    }
    _dump(out_dir / "manifest.json", manifest)
    return manifest


def run(force: bool = False, **_options: object) -> int:
    try:
        manifest = run_export()
    except FileNotFoundError as exc:
        print(f"export: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    print(f"export: {len(manifest['chunks'])} chunk file(s) written to out/map/")
    return 0
