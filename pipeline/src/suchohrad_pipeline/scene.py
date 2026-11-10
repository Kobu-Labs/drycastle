"""Assemble all meshes of the map per chunk (shared by the export and preview stages).

Optional work files (terrain, roads, props) are used when present, so the export of an
older work directory still works; a missing terrain means buildings stand at z = 0.
"""

import hashlib
import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import shapely

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh, building_mesh, collision_mesh
from suchohrad_pipeline.propmeshes import fence_mesh, trees_mesh
from suchohrad_pipeline.roads import road_mesh
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section
from suchohrad_pipeline.terrain import Terrain, load_terrain

Chunk = tuple[int, int]
GREY = (128, 128, 128)


class ParcelColours:
    """Flat terrain colour by the parcel kind under a point; a shade is picked per parcel."""

    def __init__(self, parcels: list[dict], palette: dict[str, list[str]]):
        from shapely.geometry import shape

        self.parcels = sorted(parcels, key=lambda p: p["id"])
        self.tree = shapely.STRtree([shape(p["polygon"]) for p in self.parcels])
        self.polys = self.tree.geometries
        self.palette = {k: [hex_to_rgb(c) for c in v] for k, v in palette.items()}
        self.fallback = self.palette["other"][0]
        self.parcel_colour = [self._pick(p) for p in self.parcels]

    def _pick(self, p: dict) -> list[int]:
        shades = self.palette.get(p["kind"], self.palette["other"])
        h = int.from_bytes(hashlib.sha256(p["id"].encode()).digest()[:4], "big")
        return shades[h % len(shades)]

    def __call__(self, pts: np.ndarray) -> np.ndarray:
        out = np.tile(np.array(self.fallback, np.uint8), (len(pts), 1))
        found = np.full(len(pts), len(self.parcels))
        idx, tidx = self.tree.query(shapely.points(pts[:, 0], pts[:, 1]), predicate="within")
        np.minimum.at(found, idx, tidx)
        for i in np.nonzero(found < len(self.parcels))[0]:
            out[i] = self.parcel_colour[found[i]]
        return out


@dataclass
class MapScene:
    terrain: Terrain | None = None
    building_elevation: dict[str, float] = field(default_factory=dict)
    parcels: dict[Chunk, list[tuple[str, Mesh, Mesh]]] = field(default_factory=dict)
    nodes: dict[Chunk, dict[str, Mesh]] = field(default_factory=dict)  # terrain, roads, props, ...
    graph: dict | None = None
    props: dict | None = None
    spawns: dict | None = None

    def chunks(self) -> list[Chunk]:
        return sorted(set(self.parcels) | set(self.nodes))


def _by_chunk(mesh: Mesh, chunk_m: float) -> dict[Chunk, Mesh]:
    if not mesh.face_count:
        return {}
    c = mesh.tris.mean(axis=1)
    ix = np.floor(c[:, 0] / chunk_m).astype(int)
    iy = np.floor(c[:, 1] / chunk_m).astype(int)
    out = {}
    for key in sorted(set(zip(ix.tolist(), iy.tolist(), strict=True))):
        sel = (ix == key[0]) & (iy == key[1])
        out[key] = Mesh(mesh.tris[sel], mesh.colours[sel])
    return out


def _read(work_dir: Path, name: str) -> dict | None:
    path = work_dir / name
    return json.loads(path.read_text()) if path.is_file() else None


def build_scene(
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    exaggeration: float = 1.0,
    area: str | None = None,
) -> MapScene:
    """All meshes of the map. ``exaggeration`` scales heights (preview only; 1 for export)."""
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    chunk_m = float(parcels_doc["chunk_size_m"])
    buildings = {
        b["id"]: b for b in json.loads((work_dir / "buildings.json").read_text())["buildings"]
    }
    scene = MapScene()
    k = exaggeration
    if (work_dir / "terrain.json").is_file():
        scene.terrain, elev = load_terrain(work_dir)
        scene.building_elevation = {b: e * k for b, e in elev.items()}
        scene.terrain = scene.terrain.scaled(k) if k != 1 else scene.terrain

    for p in sorted(parcels_doc["parcels"], key=lambda p: p["id"]):
        ids = sorted(bid for bid in p["buildings"] if bid in buildings)
        if not ids:
            continue
        vis, col = [], []
        for bid in ids:
            dz = scene.building_elevation.get(bid, 0.0)
            for meshes, fn in ((vis, building_mesh), (col, collision_mesh)):
                m = fn(buildings[bid])
                meshes.append(Mesh(m.tris + [0, 0, dz], m.colours))
        scene.parcels.setdefault(tuple(p["chunk"]), []).append(
            (p["id"], Mesh.concat(vis), Mesh.concat(col))
        )

    t = scene.terrain
    if t is None:
        return scene

    def put(name: str, per_chunk: dict[Chunk, Mesh]) -> None:
        for key, mesh in per_chunk.items():
            if mesh.face_count:
                scene.nodes.setdefault(key, {})[name] = mesh

    tcfg = load_section("terrain", rules_path)
    colours = ParcelColours(parcels_doc["parcels"], tcfg["colours"])
    dec = int(tcfg["collision_decimate"])
    grey = np.array(GREY, np.uint8)
    for key in t.chunks():
        put("terrain", {key: t.chunk_mesh(*key, colours)})
        put(
            "terrain_collision",
            {key: t.chunk_mesh(*key, lambda pts: np.tile(grey, (len(pts), 1)), dec)},
        )

    scene.graph = _read(work_dir, "roads.json")
    if scene.graph:
        put("roads", _by_chunk(road_mesh(scene.graph, t, load_section("roads", rules_path)), chunk_m))

    scene.props = _read(work_dir, "props.json")
    if scene.props:
        pcfg = load_section("props", rules_path, area)
        fences = [{**s, "za": s["za"] * k, "zb": s["zb"] * k} for s in scene.props["fences"]]
        trees = [{**tr, "z": tr["z"] * k} for tr in scene.props["trees"]]
        by_fence = _group(fences, lambda s: _chunk(s["a"], chunk_m))
        by_tree = _group(trees, lambda tr: _chunk((tr["x"], tr["y"]), chunk_m))
        for key in sorted(set(by_fence) | set(by_tree)):
            visual = Mesh.concat(
                [fence_mesh(by_fence.get(key, []), pcfg["fences"]),
                 trees_mesh(by_tree.get(key, []), pcfg["trees"])]
            )
            put("props", {key: visual})
            if pcfg["fences"]["collision"]:
                put("props_collision", {key: fence_mesh(by_fence.get(key, []), pcfg["fences"], True)})
    scene.spawns = _read(work_dir, "spawns.json")
    return scene


def _chunk(xy, chunk_m: float) -> Chunk:
    return (int(np.floor(xy[0] / chunk_m)), int(np.floor(xy[1] / chunk_m)))


def _group(items: list[dict], key) -> dict[Chunk, list[dict]]:
    out: dict[Chunk, list[dict]] = {}
    for it in items:
        out.setdefault(key(it), []).append(it)
    return out
