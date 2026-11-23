"""The village: the work files of the map, indexed, and one parcel context per parcel part."""

import json
import math
from pathlib import Path

import shapely
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.assets import load_manifest
from drycastle_pipeline.core.geometry import polygons
from drycastle_pipeline.core.rules import _merge, load_section
from drycastle_pipeline.detail.footpaths import path_polygons
from drycastle_pipeline.detail.model import _AROUND_M, _NEAR_M, ParcelContext
from drycastle_pipeline.detail.recipes import area_type, dike_parcels
from drycastle_pipeline.detail.swamp import swamp_zone
from drycastle_pipeline.detail.tools import _rad, _union, blob
from drycastle_pipeline.edits.schema import load_edits
from drycastle_pipeline.ground.areas import (
    ground_kind,
    ground_types,
    ground_units,
    type_colours,
    water_kinds,
    with_parcel_types,
)
from drycastle_pipeline.ground.attributes import attributes_of
from drycastle_pipeline.ground.patches import plan_patches
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.roads.roads import edge_lines
from drycastle_pipeline.terrain.terrain import load_terrain


def make_verge(graph: dict, cfg: dict) -> dict[str, list[Polygon]]:
    """Gravel and dirt blobs along the carriageway edges of the roads in ``cfg['highways']`` (global, per edge)."""
    out: dict[str, list[Polygon]] = {"gravel": [], "dirt": []}
    for e, line in edge_lines(graph):
        if e["highway"] not in cfg["highways"]:
            continue
        rng = seeded(f"{e['id']}:detail:verge")
        half, length = float(e["width"]) / 2, line.length
        s = rng.random() * float(cfg["step_m"][1])
        while s < length:
            p, q = line.interpolate(s), line.interpolate(min(length, s + 0.5))
            ang = math.atan2(q.y - p.y, q.x - p.x)
            for side in (-1, 1):
                if rng.random() > float(cfg["keep"]):
                    continue
                off = half + _rad(rng, cfg["offset_m"])
                cx, cy = p.x - side * math.sin(ang) * off, p.y + side * math.cos(ang) * off
                b = blob(rng, cx, cy, _rad(rng, cfg["radius_m"]), 0.35, _rad(rng, cfg["stretch"]), int(cfg["vertices"]), ang)
                out["gravel" if rng.random() < float(cfg["gravel_share"]) else "dirt"].append(b)
            s += _rad(rng, cfg["step_m"])
    return out


class Village:
    """The work files of the map, indexed, and one :class:`ParcelContext` per parcel part."""

    def __init__(self, work_dir: Path, rules_path: Path, edits_path: Path) -> None:
        from drycastle_pipeline.export.scene import ParcelColours

        self.cfg = load_section("detail", rules_path)
        self.tree_cfg = load_section("props", rules_path)["trees"]
        self.terrain, _ = load_terrain(work_dir)
        parcels_doc = json.loads((work_dir / "parcels.json").read_text())
        self.chunk_m = float(parcels_doc["chunk_size_m"])
        self.parcels = with_parcel_types(ground_units(parcels_doc), edits_path)  # parcels and ground areas: every unit gets its recipe
        buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
        props = json.loads((work_dir / "props.json").read_text())
        graph = json.loads((work_dir / "roads.json").read_text())
        objects = json.loads((work_dir / "objects.json").read_text())["objects"] if (work_dir / "objects.json").is_file() else []
        self.types = ground_types(rules_path, edits_path)
        colours = ParcelColours(self.parcels, {**load_section("terrain", rules_path)["colours"], **type_colours(self.types)})
        self._type_cfg: dict[str, dict] = {}
        self.colour = {p["id"]: colours._pick(p) for p in self.parcels}
        self.buildings = [footprint_polygon(b["footprint"]) for b in buildings]
        self.b_tree = shapely.STRtree(self.buildings)
        self.fences = [LineString([s["a"], s["b"]]) for s in props["fences"]]
        self.f_tree = shapely.STRtree(self.fences)
        self.trees = [(t["x"], t["y"], float(t["scale"])) for t in props["trees"]]
        self.t_tree = shapely.STRtree([Point(t[0], t[1]) for t in self.trees])
        self.road_polys = [ln.buffer(float(e["width"]) / 2) for e, ln in edge_lines(graph)]
        self.r_tree = shapely.STRtree(self.road_polys)
        # the dike's track follows these edges only: not the ways of ``detail.dike.not_track`` (regular roads beside the dike's own)
        not_track = set(self.cfg["dike"].get("not_track", []))
        self.road_lines = [(ln, float(e["width"])) for e, ln in edge_lines(graph) if e.get("osm_id") not in not_track]
        self.plain_polys = [ln.buffer(float(e["width"]) / 2) for e, ln in edge_lines(graph) if e.get("osm_id") in not_track]
        self.plain_tree = shapely.STRtree(self.plain_polys)
        self.rl_tree = shapely.STRtree([ln for ln, _ in self.road_lines])
        self.road_osm: dict[str, list[LineString]] = {}
        for e, ln in edge_lines(graph):
            self.road_osm.setdefault(e.get("osm_id"), []).append(ln)
        sizes = {a["id"]: a.get("size_m", [1, 1, 1]) for a in load_manifest().get("assets", [])}
        self.object_polys = []
        for o in objects:
            sx, _, sz = sizes.get(o["asset"], [1.0, 1.0, 1.0])
            sx, sz, k = (o["size_m"][0], o["size_m"][1], 1.0) if o.get("size_m") else (sx, sz, float(o.get("scale", 1.0)))
            r = 0.5 * math.hypot(sx, sz) * k + float(self.cfg["object_clearance_m"])
            self.object_polys.append(Point(o["position"][0], o["position"][1]).buffer(r, 8))
        self.o_tree = shapely.STRtree(self.object_polys)
        plan = plan_patches(load_edits(edits_path), parcels_doc.get("origin_epsg5514"), self.terrain, self.types)
        self.warnings = list(plan.warnings)
        water_patches = [p.polygon for p in plan.patches.values() if p.water_y is not None]
        self.water_patches = unary_union(water_patches) if water_patches else Polygon()
        shapely.prepare(self.water_patches)
        wet = water_kinds(self.types)
        self.water_polys = [shape(p["polygon"]) for p in self.parcels if p["kind"] in wet]
        self.w_tree = shapely.STRtree(self.water_polys)
        self.verge_polys = make_verge(graph, self.cfg["verge"])
        # worn footpaths of the edits are trodden ground like the dirt of the verges
        self.verge_polys["dirt"] += path_polygons(load_edits(edits_path).paths, tuple(parcels_doc.get("origin_epsg5514") or (0.0, 0.0)))
        self.v_tree = {k: shapely.STRtree(v) for k, v in self.verge_polys.items()}
        # crop patches, for the baulk between two that adjoin
        self.crop_units = [(p["id"], shape(p["polygon"])) for p in self.parcels if area_type(p, self.types) == "crop"]
        self.crop_tree = shapely.STRtree([g for _, g in self.crop_units])
        self._around: tuple[list, shapely.STRtree] | None = None
        stands = self.tree_cfg.get("stands")
        self.swamp_kinds = set(self.cfg["swamp"]["kinds"])
        self.swamp = swamp_zone(self.parcels, self.types, plan, self.terrain, float(stands["swamp"]["reach_m"])) if stands and "swamp" in stands else None
        if self.swamp is not None:
            shapely.prepare(self.swamp)
        ways = set(self.cfg["forest"]["mud"]["track"]["highways"])
        self.tracks = [(e["id"], ln, float(e["width"])) for e, ln in edge_lines(graph) if e["highway"] in ways]
        self.tk_tree = shapely.STRtree([t[1] for t in self.tracks])

    def cfg_for(self, parcel: dict, recipe: str) -> dict:
        """The detail rules for a unit: for a ground area whose type has ``detail`` settings, those laid over the
        section of its recipe."""
        kind = ground_kind(parcel)
        attrs = attributes_of(self.types.get(kind))
        if not attrs.get("detail") or attrs.get("recipe") != recipe:
            return self.cfg
        if kind not in self._type_cfg:
            self._type_cfg[kind] = {**self.cfg, recipe: _merge(self.cfg[recipe], attrs["detail"])}
        return self._type_cfg[kind]

    @staticmethod
    def _near(tree: shapely.STRtree, items: list, geom: BaseGeometry) -> list:
        return [items[i] for i in tree.query(geom, predicate="intersects")]

    def dike_clip(self, pid: str) -> BaseGeometry | None:
        """The part of a parcel that is the dike when only a part is (``detail.dike.clips``): the ground within
        ``within_m`` of a road way (``osm_id``); None for any other parcel."""
        clip = self.cfg["dike"].get("clips", {}).get(pid)
        if clip is None:
            return None
        lines = self.road_osm.get(clip["osm_id"], [])
        return unary_union([ln.buffer(float(clip["within_m"])) for ln in lines]) if lines else Polygon()

    def contexts(self, parcel: dict):
        """One context per polygon of the parcel clipped to the terrain coverage (the largest keeps the ID).
        A parcel that is a dike, or has a dike part, also gets ``dike`` contexts for it (the rest keeps its own recipe)."""
        poly = shape(parcel["polygon"]).intersection(self.terrain.coverage)
        parts = sorted(polygons(poly), key=lambda p: -p.area)
        whole = parcel["id"] in dike_parcels() or parcel["kind"] == "dike"
        clip = self.dike_clip(parcel["id"])
        k = 0
        for part in parts:
            if whole:
                pieces = [(part, True)]
            elif clip is not None:
                pieces = [(piece, dike) for dike, geom in ((False, part.difference(clip)), (True, part.intersection(clip))) for piece in polygons(geom)]
            else:
                pieces = [(part, False)]
            for piece, dike in pieces:
                if piece.area < float(self.cfg["min_parcel_m2"]):
                    continue
                yield self._context(parcel, piece, parcel["id"] if k == 0 else f"{parcel['id']}.{k}", dike)
                k += 1

    def _crop_neighbours(self, pid: str, part: Polygon) -> list[tuple[str, BaseGeometry]]:
        near = part.buffer(0.5)
        return [self.crop_units[i] for i in sorted(self.crop_tree.query(near, predicate="intersects")) if self.crop_units[i][0] != pid]

    def _ground_around(self, pid: str, part: Polygon) -> list[tuple[str, BaseGeometry, list[int], bool]]:
        """The other ground units within ``_AROUND_M`` of a crop patch with their terrain colour (built on first use)."""
        if self._around is None:
            units = [(p["id"], shape(p["polygon"]), self.colour[p["id"]], area_type(p, self.types) == "crop")
                     for p in self.parcels if area_type(p, self.types) is not None]
            self._around = (units, shapely.STRtree([g for _, g, _, _ in units]))
        units, tree = self._around
        return [units[i] for i in sorted(tree.query(part.buffer(_AROUND_M), predicate="intersects")) if units[i][0] != pid]

    def _context(self, parcel: dict, part: Polygon, cid: str, dike: bool) -> ParcelContext:
        near = part.buffer(_NEAR_M)
        roads = self._near(self.r_tree, self.road_polys, near)
        blocked = self._near(self.o_tree, self.object_polys, part)
        if not self.water_patches.is_empty and self.water_patches.intersects(part):
            blocked.append(self.water_patches.intersection(part.buffer(1.0)))
        verge = {
            name: _union(self._near(tree, self.verge_polys[name], part)).intersection(part)
            for name, tree in self.v_tree.items()
        }
        swamp = None
        if self.swamp is not None and parcel["kind"] in self.swamp_kinds and self.swamp.intersects(part):
            swamp = _union(polygons(self.swamp.intersection(part)))
            if swamp.is_empty:
                swamp = None
            else:
                shapely.prepare(swamp)
        return ParcelContext(
            id=cid,
            kind=parcel["kind"],
            polygon=part,
            buildings=self._near(self.b_tree, self.buildings, near),
            fences=self._near(self.f_tree, self.fences, near),
            trees=self._near(self.t_tree, self.trees, near),
            road=_union(roads) if roads else None,
            terrain=self.terrain,
            tree_cfg=self.tree_cfg,
            colour=self.colour[parcel["id"]],
            blocked=_union(blocked) if blocked else None,
            verge=verge,
            road_lines=self._near(self.rl_tree, self.road_lines, near),
            plain_road=_union(self._near(self.plain_tree, self.plain_polys, near)) if dike and self.plain_polys else None,
            dike=dike,
            crop_neighbours=self._crop_neighbours(parcel["id"], part) if area_type(parcel, self.types) == "crop" else None,
            ground_around=self._ground_around(parcel["id"], part) if area_type(parcel, self.types) == "crop" else None,
            swamp=swamp,
            tracks=self._near(self.tk_tree, self.tracks, near),
            water=_union(self._near(self.w_tree, self.water_polys, part.buffer(float(self.cfg["tint"]["water_m"])))) if self.water_polys else None,
        )
