"""Assemble all meshes of the map per chunk (shared by the export and preview stages).

Optional work files (terrain, roads, props) are used when present, so the export of an
older work directory still works; a missing terrain means buildings stand at z = 0.
"""

import hashlib
import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import Polygon

from drycastle_pipeline.building_detail import build_detail, load_details
from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.bunker import bunker_rooms
from drycastle_pipeline.detail import load_patches, patches_by_chunk
from drycastle_pipeline.edits import DEFAULT_EDITS, load_edits
from drycastle_pipeline.landmarks import build_landmark
from drycastle_pipeline.meshes import Mesh, building_mesh, collision_mesh
from drycastle_pipeline.propmeshes import fence_mesh, fence_meshes, parcel_props_meshes, trees_mesh
from drycastle_pipeline.ground import ground_types, ground_units, type_colours
from drycastle_pipeline.regions import node_name, plan_regions, region_meshes
from drycastle_pipeline.roads import road_mesh
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import Terrain, cut_holes, load_terrain
from drycastle_pipeline.textures import TexturedMesh, tex_node_name

Chunk = tuple[int, int]
GREY = (128, 128, 128)


class ParcelColours:
    """Flat terrain colour by the kind of the parcel or ground area under a point; a shade is picked per unit."""

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
    regions: dict = field(default_factory=dict)  # planned regions by ID (water.json takes the ones with a water_y)
    building_elevation: dict[str, float] = field(default_factory=dict)
    parcels: dict[Chunk, list[tuple[str, Mesh, Mesh]]] = field(default_factory=dict)
    # terrain, roads, props, ...; a "<node>_tex_<texture id>" entry is a TexturedMesh (textures.py)
    nodes: dict[Chunk, dict[str, Mesh | TexturedMesh]] = field(default_factory=dict)
    # fences and trees of one parcel (visual, collision, textured fence parts by texture id), in the
    # parcel's own chunk
    parcel_props: dict[Chunk, list[tuple[str, Mesh, Mesh, dict[str, TexturedMesh]]]] = field(
        default_factory=dict
    )
    graph: dict | None = None
    props: dict | None = None
    trees: list[dict] | None = None  # tree records as exported (z with the exaggeration), for trees.json
    spawns: dict | None = None
    warnings: list[str] = field(default_factory=list)  # stale or unusable edits entries
    windows: list[dict] = field(default_factory=list)  # tagged window panes, see building_detail
    bunkers: list[dict] = field(default_factory=list)  # rooms of the enterable bunkers, see bunker.bunker_rooms

    def chunks(self) -> list[Chunk]:
        return sorted(set(self.parcels) | set(self.nodes) | set(self.parcel_props))


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


def _raised(r: dict, dz: float) -> dict:
    """A bunker floor or ladder with its heights (relative to the building base) lifted onto the ground."""
    return {**r, "z0": round(r["z0"] + dz, 3), "z1": round(r["z1"] + dz, 3)}


def build_scene(
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    exaggeration: float = 1.0,
    edits_path: Path = DEFAULT_EDITS,
) -> MapScene:
    """All meshes of the map. ``exaggeration`` scales heights (preview only; 1 for export).

    ``edits_path`` is read for the regions (``region_<ID>`` nodes in every chunk they touch).
    """
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    chunk_m = float(parcels_doc["chunk_size_m"])
    buildings = {
        b["id"]: b for b in json.loads((work_dir / "buildings.json").read_text())["buildings"]
    }
    scene = MapScene()
    details = load_details()
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
            b = buildings[bid]
            for meshes, fn in ((vis, building_mesh), (col, collision_mesh)):
                if bid in details and not b.get("model"):
                    if fn is collision_mesh:
                        m = fn(b)
                    else:
                        m, tags = build_detail(b, details[bid])
                        for tag in tags:
                            tag.meta["centre"][2] += dz
                            scene.windows.append({**tag.meta, "parcel": p["id"]})
                            scene.nodes.setdefault(tuple(p["chunk"]), {})[tag.node] = Mesh(
                                tag.mesh.tris + [0, 0, dz], tag.mesh.colours
                            )
                elif b.get("model"):  # hand-modelled landmark: detailed visual, plain-massing collision
                    m = build_landmark(b, detail=fn is building_mesh)
                    if b["model"] == "bunker" and fn is building_mesh:
                        a = float(b["model_axis"])
                        rooms = bunker_rooms(b["footprint"]["exterior"], (math.cos(a), math.sin(a)))
                        if rooms:
                            scene.bunkers.append({
                                "id": bid, "parcel": p["id"],
                                "floors": [_raised(r, dz) for r in rooms["floors"]],
                                "ladders": [_raised(r, dz) for r in rooms["ladders"]],
                                "hatch": rooms["hatch"],
                                **({"intel": _raised(rooms["intel"], dz)} if "intel" in rooms else {}),
                            })
                else:
                    m = fn(b)
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
    colours = ParcelColours(ground_units(parcels_doc), {**tcfg["colours"], **type_colours(ground_types(rules_path, edits_path))})
    dec = int(tcfg["collision_decimate"])
    grey = np.array(GREY, np.uint8)
    # the hatch of each bunker is a hole in the ground (#224): the terrain, seen and collided with, has it cut out
    cuts = [Polygon(bk["hatch"]) for bk in scene.bunkers if bk["hatch"]]
    # ... and the ground a bunker stands on is collided with at the full 4 m grid, not decimated: the decimated surface
    # sits up to 0.3 m under the flattened floor there, which would put a step in the doorway
    exact = {
        (math.floor(x / chunk_m), math.floor(y / chunk_m))
        for c in cuts
        for x in (c.centroid.x - 8, c.centroid.x + 8)
        for y in (c.centroid.y - 8, c.centroid.y + 8)
    }
    for key in t.chunks():
        put("terrain", {key: cut_holes(t.chunk_mesh(*key, colours), cuts)})
        put(
            "terrain_collision",
            {
                key: cut_holes(
                    t.chunk_mesh(*key, lambda pts: np.tile(grey, (len(pts), 1)), 1 if key in exact else dec), cuts
                )
            },
        )

    scene.graph = _read(work_dir, "roads.json")
    if scene.graph:
        put("roads", _by_chunk(road_mesh(scene.graph, t, load_section("roads", rules_path)), chunk_m))

    rplan = plan_regions(load_edits(edits_path), parcels_doc.get("origin_epsg5514"), t, ground=False)
    scene.warnings += rplan.warnings
    scene.regions = rplan.regions
    if rplan.regions:
        meshes = region_meshes(
            rplan, t, scene.graph, load_section("roads", rules_path), load_section("regions", rules_path)
        )
        for rid, mesh in meshes.items():
            put(node_name(rid), _by_chunk(mesh, chunk_m))

    # ground detail (detail stage): patches draped on the terrain, one flat node and one node per texture in each
    # chunk; the clutter instances are not meshes, the export copies them as clutter.json
    patches = load_patches(work_dir)
    if patches is not None:
        for key, nodes in patches_by_chunk(patches[0], patches[1], chunk_m, k).items():
            scene.nodes.setdefault(key, {}).update(nodes)

    scene.props = _read(work_dir, "props.json")
    if scene.props:
        pcfg = load_section("props", rules_path)
        fences = [{**s, "za": s["za"] * k, "zb": s["zb"] * k} for s in scene.props["fences"]]
        trees = [{**tr, "z": tr["z"] * k} for tr in scene.props["trees"]]
        scene.trees = trees
        chunk_of_parcel = {p["id"]: tuple(p["chunk"]) for p in parcels_doc["parcels"]}

        def owned(it: dict) -> bool:  # OSM trees have no parcel (null or absent): chunk level
            return it.get("parcel") in chunk_of_parcel

        by_parcel_f = _group([s for s in fences if owned(s)], lambda s: s["parcel"])
        by_parcel_t = _group([t for t in trees if owned(t)], lambda t: t["parcel"])
        for pid in sorted(set(by_parcel_f) | set(by_parcel_t)):
            visual, collision, textured = parcel_props_meshes(
                by_parcel_f.get(pid, []), by_parcel_t.get(pid, []), pcfg
            )
            if visual.face_count or collision.face_count or textured:
                scene.parcel_props.setdefault(chunk_of_parcel[pid], []).append(
                    (pid, visual, collision, textured)
                )
        by_fence = _group([s for s in fences if not owned(s)], lambda s: _chunk(s["a"], chunk_m))
        by_tree = _group(
            [t for t in trees if not owned(t)], lambda t: _chunk((t["x"], t["y"]), chunk_m)
        )
        for key in sorted(set(by_fence) | set(by_tree)):
            fence_flat, textured = fence_meshes(by_fence.get(key, []), pcfg["fences"])
            visual = Mesh.concat([fence_flat, trees_mesh(by_tree.get(key, []), pcfg["trees"])])
            put("props", {key: visual})
            for tid, mesh in textured.items():
                put(tex_node_name("props", tid), {key: mesh})
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
