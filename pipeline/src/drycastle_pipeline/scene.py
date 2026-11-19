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

from drycastle_pipeline.building_detail import GLASS as PANE_GLASS
from drycastle_pipeline.bed import carve_terrain
from drycastle_pipeline.building_detail import build_detail, load_details
from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.bunker import bunker_rooms
from drycastle_pipeline.detail import load_patches, patches_by_chunk
from drycastle_pipeline.edits import DEFAULT_EDITS, load_edits
from drycastle_pipeline.ground import ground_types, ground_units, type_colours, water_kinds
from drycastle_pipeline.landmarks import build_landmark
from drycastle_pipeline.meshes import Mesh, building_mesh, collision_mesh
from drycastle_pipeline.patches import node_name, patch_meshes, plan_patches
from drycastle_pipeline.propmeshes import fence_mesh, fence_meshes, ground_objects_mesh, parcel_props_meshes, trees_mesh
from drycastle_pipeline.roads import clip_graph, road_mesh
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import Terrain, cut_holes, load_terrain
from drycastle_pipeline.textures import TexturedMesh, tex_node_name
from drycastle_pipeline.water import WaterPlan, plan_water, water_rules

Chunk = tuple[int, int]
GREY = (128, 128, 128)


class ParcelColours:
    """Flat terrain colour by the kind of the parcel or ground area under a point; a shade is picked per unit."""

    def __init__(self, parcels: list[dict], palette: dict[str, list[str]], blend: dict | None = None, swamp: dict | None = None):
        """``blend`` ({kinds, radius_m}, the terrain's ``colour_blend``): around the outline of ground of these kinds the colour is the mean of the
        colours within ``radius_m``, so a forest runs into its neighbours over a band instead of ending at a line.
        ``swamp`` ({zone, kinds, colours}): ground of these kinds inside the zone takes one of these colours instead (the wet floor of
        ``detail.swamp``), and its outline is blended like the others."""
        from shapely.geometry import shape

        self.parcels = sorted(parcels, key=lambda p: p["id"])
        self.tree = shapely.STRtree([shape(p["polygon"]) for p in self.parcels])
        self.polys = self.tree.geometries
        self._prepared: set[int] = set()
        self.palette = {k: [hex_to_rgb(c) for c in v] for k, v in palette.items()}
        self.fallback = self.palette["other"][0]
        self.parcel_colour = [self._pick(p) for p in self.parcels]
        self.swamp = swamp["zone"] if swamp and swamp.get("zone") is not None and not swamp["zone"].is_empty else None
        if self.swamp is not None:
            shapely.prepare(self.swamp)
            shades = [hex_to_rgb(c) for c in swamp["colours"]]
            kinds_wet = set(swamp["kinds"])
            # the wet colour of each unit of a swamp kind (None: the unit stays as it is)
            self.swamp_colour = [self._shade(p, shades) if p["kind"] in kinds_wet else None for p in self.parcels]
        kinds = set((blend or {}).get("kinds", []))
        self.blend_m = float((blend or {}).get("radius_m", 0.0))
        edges = [g.boundary for g, p in zip(self.polys, self.parcels) if p["kind"] in kinds] if self.blend_m > 0 else []
        if self.swamp is not None and self.blend_m > 0:
            edges.append(self.swamp.boundary)
        self.edges = shapely.STRtree(_short_lines(edges)) if edges else None

    def _pick(self, p: dict) -> list[int]:
        return self._shade(p, self.palette.get(p["kind"], self.palette["other"]))

    @staticmethod
    def _shade(p: dict, shades: list[list[int]]) -> list[int]:
        h = int.from_bytes(hashlib.sha256(p["id"].encode()).digest()[:4], "big")
        return shades[h % len(shades)]

    def __call__(self, pts: np.ndarray) -> np.ndarray:
        out = self._flat(pts)
        if self.edges is None or not len(pts):
            return out
        near = np.unique(self.edges.query(shapely.points(pts[:, 0], pts[:, 1]), predicate="dwithin", distance=self.blend_m)[0])
        if not len(near):
            return out
        # mean of the flat colour on two rings around the point (and the point itself, twice): a soft band, as wide as the radius on each side
        ang = np.arange(8) * (math.pi / 4)
        offs = np.concatenate([np.zeros((2, 2)), 0.5 * self.blend_m * np.c_[np.cos(ang), np.sin(ang)], self.blend_m * np.c_[np.cos(ang + math.pi / 8), np.sin(ang + math.pi / 8)]])
        acc = np.zeros((len(near), 3))
        for o in offs:
            acc += self._flat(pts[near] + o)
        out[near] = np.rint(acc / len(offs)).astype(np.uint8)
        return out

    def _flat(self, pts: np.ndarray) -> np.ndarray:
        out = np.tile(np.array(self.fallback, np.uint8), (len(pts), 1))
        found = np.full(len(pts), len(self.parcels))
        # candidates by bounding box, then every polygon tests its own points at once (prepared: a point query with a
        # predicate would run the unprepared test of each point against whole forest polygons)
        idx, tidx = self.tree.query(shapely.points(pts[:, 0], pts[:, 1]))
        order = np.argsort(tidx, kind="stable")
        idx, tidx = idx[order], tidx[order]
        cuts = np.flatnonzero(np.diff(tidx)) + 1
        for sel in np.split(np.arange(len(tidx)), cuts) if len(tidx) else ():
            t = tidx[sel[0]]
            if t not in self._prepared:
                shapely.prepare(self.polys[t])
                self._prepared.add(t)
            mine = idx[sel]
            inside = mine[shapely.contains_xy(self.polys[t], pts[mine, 0], pts[mine, 1])]
            found[inside] = np.minimum(found[inside], t)
        hit = np.nonzero(found < len(self.parcels))[0]
        for i in hit:
            out[i] = self.parcel_colour[found[i]]
        if self.swamp is not None and len(hit):
            wet = hit[shapely.contains_xy(self.swamp, pts[hit, 0], pts[hit, 1])]
            for i in wet:
                c = self.swamp_colour[found[i]]
                if c is not None:
                    out[i] = c
        return out


def _short_lines(lines: list, most: int = 16) -> np.ndarray:
    """The same lines cut into runs of at most ``most`` segments: a point is within a distance of a line exactly when it is of
    one of its runs, and a tree of short runs answers that without measuring against a whole forest outline."""
    out = []
    for part in shapely.get_parts(shapely.get_parts(lines)):  # collections, then multi-lines, down to single lines and rings
        xy = shapely.get_coordinates(part)
        out += [xy[i : i + most + 1] for i in range(0, len(xy) - 1, most)]
    return np.array([shapely.linestrings(run) for run in out], dtype=object)


@dataclass
class MapScene:
    terrain: Terrain | None = None
    patches: dict = field(default_factory=dict)  # planned ground patches by ID (water.json takes the ones with a water_y)
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
    ground_objects: list[dict] | None = None  # logs, stumps, boulders, root plates, anthills, burrows (z with the exaggeration), for ground_objects.json
    spawns: dict | None = None
    warnings: list[str] = field(default_factory=list)  # stale or unusable edits entries
    windows: list[dict] = field(default_factory=list)  # tagged window panes, see building_detail
    bunkers: list[dict] = field(default_factory=list)  # rooms of the enterable bunkers, see bunker.bunker_rooms
    church: dict | None = None  # the inside of the church, see church_interior.church_info
    urad: dict | None = None  # the inside of the municipal office, see office_interior.urad_info
    garage: dict | None = None  # the bus garage's doorway, bay and parking place, see garage.garage_info
    water: WaterPlan | None = None  # the water bodies (water.json) and the terrain they are carved into, with a terrain

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


def _swamp_colours(units: list[dict], types: dict, plan, terrain: Terrain, rules_path) -> dict | None:
    """The swamp ground for :class:`ParcelColours`: the zone of ``detail.swamp_zone`` with the kinds and colours of ``detail.swamp`` (the
    wet floor is the terrain's own colour, so that it fades into the ground beside it; the detail stage lays mud and puddles on it)."""
    from drycastle_pipeline.detail import swamp_zone

    stands = load_section("props", rules_path)["trees"].get("stands") or {}
    sc = load_section("detail", rules_path).get("swamp")
    if plan is None or sc is None or "swamp" not in stands:
        return None
    return {"zone": swamp_zone(units, types, plan, terrain, float(stands["swamp"]["reach_m"])), "kinds": sc["kinds"], "colours": sc["colours"]}


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

    ``edits_path`` is read for the ground types and the water patches (``patch_<ID>`` nodes in every chunk they touch; the
    other patches are ground areas, baked into the terrain by the parcels stage).
    """
    parcels_doc = json.loads((work_dir / "parcels.json").read_text())
    chunk_m = float(parcels_doc["chunk_size_m"])
    buildings = {
        b["id"]: b for b in json.loads((work_dir / "buildings.json").read_text())["buildings"]
    }
    scene = MapScene()
    details = load_details()
    k = exaggeration
    types = ground_types(rules_path, edits_path)
    pplan = None
    if (work_dir / "terrain.json").is_file():
        base, elev = load_terrain(work_dir)
        scene.building_elevation = {b: e * k for b, e in elev.items()}
        # the water and the patches are worked out on the terrain as it is, then the terrain is carved to the beds
        pplan = plan_patches(load_edits(edits_path), parcels_doc.get("origin_epsg5514"), base, types)
        wrules = water_rules(rules_path)
        scene.water = plan_water(ground_units(parcels_doc), base, pplan.patches, wrules, water_kinds(types))
        scene.terrain = base.scaled(k) if k != 1 else base
        beds = [b.scaled(k) for b in scene.water.bodies] if k != 1 else scene.water.bodies
        carve_terrain(scene.terrain, beds, float(wrules["carve_blend_m"]))

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
                    if b["model"] == "church" and fn is building_mesh:
                        from drycastle_pipeline.church_interior import church_info, church_panes

                        scene.church = {"id": bid, "parcel": p["id"], **church_info(b, dz)}
                        for node, tris, meta in church_panes({**b, "id": bid}):  # see-through, breakable glass
                            meta["centre"][2] = round(meta["centre"][2] + dz, 3)
                            scene.windows.append({**meta, "parcel": p["id"]})
                            scene.nodes.setdefault(tuple(p["chunk"]), {})[node] = Mesh(
                                np.array(tris, float) + [0, 0, dz], np.array([PANE_GLASS] * len(tris), np.uint8)
                            )
                    if b["model"] == "office" and fn is building_mesh:
                        from drycastle_pipeline import office_interior
                        from drycastle_pipeline.office import _details as office_details

                        if bid == office_interior.MIDDLE_BAR:
                            scene.urad = {"id": bid, "parcel": p["id"], **office_interior.urad_info(scene.building_elevation)}
                        for node, tris, meta in office_interior.office_panes({**b, "id": bid}, office_details().get(bid)):
                            meta["centre"][2] = round(meta["centre"][2] + dz, 3)
                            scene.windows.append({**meta, "parcel": p["id"]})
                            scene.nodes.setdefault(tuple(p["chunk"]), {})[node] = Mesh(
                                np.array(tris, float) + [0, 0, dz], np.array([PANE_GLASS] * len(tris), np.uint8)
                            )
                    if b["model"] == "bus_garage" and fn is building_mesh:
                        from drycastle_pipeline import garage

                        if bid == garage.DEEP:
                            scene.garage = {"id": bid, "parcel": p["id"], **garage.garage_info(scene.building_elevation)}
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
    colours = ParcelColours(ground_units(parcels_doc), {**tcfg["colours"], **type_colours(types)}, tcfg.get("colour_blend"),
                            _swamp_colours(ground_units(parcels_doc), types, pplan, t, rules_path))
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
    # ... and so is the ground of a water body: the carved bed is what the player wades and swims over
    for b in scene.water.bodies:
        minx, miny, maxx, maxy = b.geom.bounds
        exact |= {
            (cx, cy)
            for cx in range(math.floor(minx / chunk_m), math.floor(maxx / chunk_m) + 1)
            for cy in range(math.floor(miny / chunk_m), math.floor(maxy / chunk_m) + 1)
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
        scene.graph = clip_graph(scene.graph, t.coverage)  # nothing of the road network lies off the terrain
        put("roads", _by_chunk(road_mesh(scene.graph, t, load_section("roads", rules_path)), chunk_m))

    scene.warnings += pplan.warnings
    scene.patches = pplan.patches
    water = {pid for pid, p in pplan.patches.items() if p.water_y is not None}
    if water:
        meshes = patch_meshes(
            pplan, t, scene.graph, load_section("roads", rules_path), load_section("ground_patches", rules_path), only=water,
            wet=scene.water.wet,
        )
        for pid, mesh in meshes.items():
            put(node_name(pid), _by_chunk(mesh, chunk_m))

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
        if "ground_objects" in scene.props:  # props.json of a run before map 0.13.0 has none: no file, no meshes
            scene.ground_objects = [{**o, "z": o["z"] * k} for o in scene.props["ground_objects"]]
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
        by_ground = _group(scene.ground_objects or [], lambda o: _chunk((o["x"], o["y"]), chunk_m))
        for key in sorted(set(by_fence) | set(by_tree) | set(by_ground)):
            fence_flat, textured = fence_meshes(by_fence.get(key, []), pcfg["fences"])
            visual = Mesh.concat([
                fence_flat,
                trees_mesh(by_tree.get(key, []), pcfg["trees"]),
                ground_objects_mesh(by_ground.get(key, []), pcfg["ground_objects"]),
            ])
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
