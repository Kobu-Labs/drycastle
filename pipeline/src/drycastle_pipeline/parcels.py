"""Parcels stage: registry matching, building assignment, the parcels that stay parcels (properties) and the ground
areas around them (:mod:`drycastle_pipeline.ground`), ``parcels.json``."""

import json
import math
import sys
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from shapely import STRtree
from shapely.affinity import translate
from shapely.geometry import Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import DEFAULT_EDITS, load_edits
from drycastle_pipeline.ground import ID_PREFIX as GROUND_PREFIX
from drycastle_pipeline.ground import build_ground, carve_parcel, dike_ground, drawn_ground, kept_parcels
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.registry import (
    DEFAULT_REGISTRY,
    MatchResult,
    load_registry,
    match_parcels,
    save_registry,
)
from drycastle_pipeline.rules import DEFAULT_RULES, ParcelRules, load_rules, load_section

KINDS = ("building_plot", "garden")  # a parcel holds a building, or is the rest of a property that does
PARCELS_FORMAT_VERSION = 2  # 2: only properties are parcels; the other land is in "ground" (ground.py)


@dataclass
class Assignment:
    by_parcel: dict[int, list[str]] = field(default_factory=dict)  # parcel index -> building ids
    unassigned: list[str] = field(default_factory=list)


def assign_buildings(
    parcels: list[BaseGeometry], buildings: dict[str, BaseGeometry], max_distance_m: float
) -> Assignment:
    """Largest overlap wins; no overlap -> nearest parcel within ``max_distance_m``; else none.

    Ties go to the parcel with the lowest index (parcels are sorted by cadastral reference).
    """
    result = Assignment()
    tree = STRtree(parcels)
    for bid in sorted(buildings):
        b = buildings[bid]
        best, best_area = None, 0.0
        for i in sorted(int(i) for i in tree.query(b, predicate="intersects")):
            area = parcels[i].intersection(b).area
            if area > best_area:
                best, best_area = i, area
        if best is None:
            near = tree.query_nearest(b, max_distance=max_distance_m, all_matches=True)
            if len(near):
                best = min(int(i) for i in near)
        if best is None:
            result.unassigned.append(bid)
        else:
            result.by_parcel.setdefault(best, []).append(bid)
    return result


def chunk_index(x: float, y: float, size: float) -> list[int]:
    return [math.floor(x / size), math.floor(y / size)]


@dataclass
class ParcelsResult:
    match: MatchResult
    kinds: Counter
    buildings_total: int
    buildings_assigned: int
    unassigned: list[str]
    parcel_count: int
    dropped: int = 0  # cadastral parcels that are not a property: their land is ground
    ground: Counter = field(default_factory=Counter)  # ground areas by type

    def report(self) -> list[str]:
        m = self.match
        lines = [f"parcels: {self.parcel_count}"]
        lines += [f"  kind {k}: {self.kinds.get(k, 0)}" for k in KINDS]
        lines.append(f"cadastral parcels that are not a property (their land is ground): {self.dropped}")
        lines.append(f"ground areas: {sum(self.ground.values())}")
        lines += [f"  type {k}: {n}" for k, n in sorted(self.ground.items())]
        lines.append(
            f"buildings: {self.buildings_total}, assigned: {self.buildings_assigned}, "
            f"unassigned: {len(self.unassigned)}"
        )
        for name, ids in (
            ("new", m.new),
            ("renumbered (kept ID)", m.renumbered),
            ("revived", m.revived),
            ("retired", m.retired),
        ):
            lines.append(f"ids {name}: {len(ids)}")
        active = sum(1 for e in m.registry["parcels"] if e["status"] == "active")
        retired = len(m.registry["parcels"]) - active
        lines.append(f"registry: {active} active, {retired} retired")
        return lines


def _read(path: Path) -> list[dict]:
    return json.loads(path.read_text())["features"]


def _record(pid: str, geom: BaseGeometry, kind: str, chunk_m: float) -> dict:
    anchor = geom.representative_point()
    return {
        "id": pid,
        "polygon": geom.__geo_interface__,
        "area": round(geom.area, 2),
        "kind": kind,
        "anchor": [round(anchor.x, 3), round(anchor.y, 3)],
        "chunk": chunk_index(anchor.x, anchor.y, chunk_m),
    }


def build_parcels(
    work_dir: Path = DEFAULT_WORK_DIR,
    registry_path: Path = DEFAULT_REGISTRY,
    rules: ParcelRules | None = None,
    rules_path: Path = DEFAULT_RULES,
    edits_path: Path = DEFAULT_EDITS,
) -> ParcelsResult:
    """``edits_path`` is read for the property groups: they decide which parcels without a building stay parcels."""
    rules = rules or load_rules(rules_path)
    norm = work_dir / "normalised"
    frame = json.loads((work_dir / "frame.json").read_text())
    ox, oy = frame["origin_epsg5514"]

    pfeats = sorted(_read(norm / "parcels.geojson"), key=lambda f: f["properties"]["cadastral_ref"])
    refs = [f["properties"]["cadastral_ref"] for f in pfeats]
    if len(set(refs)) != len(refs):
        raise ValueError("duplicate cadastral references in the parcel data")
    parcels = [shape(f["geometry"]) for f in pfeats]
    buildings = {
        f["properties"]["osm_id"]: shape(f["geometry"]) for f in _read(norm / "buildings.geojson")
    }

    # Registry works in absolute EPSG:5514 metres, independent of the slice centre. Every cadastral parcel keeps its
    # entry and ID there, also the ones whose land becomes ground below.
    absolute = {r: translate(g, ox, oy) for r, g in zip(refs, parcels, strict=True)}
    ring = frame.get("fetched_bounds_epsg5514")
    bounds = Polygon(ring) if ring else None
    match = match_parcels(load_registry(registry_path), absolute, rules.min_iou, bounds)
    save_registry(match.registry, registry_path)

    assignment = assign_buildings(parcels, buildings, rules.max_assign_distance_m)
    ids = [match.ids[r] for r in refs]
    built = {ids[i] for i in assignment.by_parcel}
    edits = load_edits(edits_path)
    groups = {gid: g.get("parcels", []) for gid, g in edits.groups.items()}
    kept = kept_parcels(built, groups)

    # parcels cut down to their buildings (ground.carve in rules.yaml): the land round them is ground
    gcfg = load_section("ground", rules_path)
    geoms = list(parcels)
    leftovers: dict[str, BaseGeometry] = {}
    carve = gcfg.get("carve") or {}
    index = {pid: i for i, pid in enumerate(ids)}
    others = None
    for pid, spec in sorted(carve.items()):
        i = index.get(pid)
        if i is None or pid not in kept:
            print(f"parcels: warning: ground.carve: {pid} is not a kept parcel; skipped", file=sys.stderr)
            continue
        if others is None:
            others = unary_union([g for j, g in enumerate(parcels) if ids[j] in kept and ids[j] not in carve])
        new, rest = carve_parcel(
            parcels[i], [buildings[b] for b in assignment.by_parcel.get(i, [])], float((spec or {}).get("margin_m", 2.0)), others
        )
        if new is None:
            print(f"parcels: warning: ground.carve: {pid} has no building to keep; skipped", file=sys.stderr)
            continue
        geoms[i] = new
        leftovers[pid] = rest

    out = []
    for i, (ref, geom) in enumerate(zip(refs, geoms, strict=True)):
        if ids[i] not in kept:
            continue
        rec = _record(ids[i], geom, "building_plot" if ids[i] in built else "garden", rules.chunk_size_m)
        out.append({**rec, "cadastral_ref": ref, "buildings": assignment.by_parcel.get(i, [])})
    out.sort(key=lambda p: p["id"])

    # the land of all the other cadastral parcels, by ground type
    dropped = {pid: g for pid, g in zip(ids, parcels, strict=True) if pid not in kept} | leftovers
    free = unary_union([g if g.is_valid else g.buffer(0) for g in dropped.values()]) if dropped else Polygon()
    if leftovers:  # a carved parcel may reach over land that was another parcel's
        free = free.difference(unary_union([geoms[index[pid]] for pid in leftovers]))
    ways = {f["properties"]["osm_id"]: shape(f["geometry"]) for f in _read(norm / "highways.geojson")}
    landuse = [
        (f["properties"]["class"], f["properties"]["value"], shape(f["geometry"])) for f in _read(norm / "landuse.geojson")
    ]
    dike = dike_ground(dropped, ways, load_section("detail", rules_path)["dike"])
    drawn, warnings = drawn_ground(edits.regions, (ox, oy), gcfg["types"])
    for warning in warnings:
        print(f"parcels: warning: {warning}", file=sys.stderr)
    kept_union = unary_union([g for pid, g in zip(ids, geoms, strict=True) if pid in kept]) if drawn else None
    ground = [
        {**_record(f"{GROUND_PREFIX}{n:04d}", geom, kind, rules.chunk_size_m), "buildings": []}
        for n, (kind, geom) in enumerate(build_ground(free, landuse, dike, gcfg, drawn, kept_union), 1)
    ]

    doc = {
        "version": PARCELS_FORMAT_VERSION,
        "crs": frame["crs"],
        "origin_epsg5514": frame["origin_epsg5514"],
        "chunk_size_m": rules.chunk_size_m,
        "parcels": out,
        "ground": ground,
        "unassigned_buildings": assignment.unassigned,
    }
    (work_dir / "parcels.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")

    return ParcelsResult(
        match=match,
        kinds=Counter(p["kind"] for p in out),
        buildings_total=len(buildings),
        buildings_assigned=len(buildings) - len(assignment.unassigned),
        unassigned=assignment.unassigned,
        parcel_count=len(out),
        dropped=len(dropped),
        ground=Counter(g["kind"] for g in ground),
    )


def run(
    force: bool = False, report: bool = False, area: str = DEFAULT_AREA, **_options: object
) -> int:
    try:
        result = build_parcels(area_paths(area).work)
    except FileNotFoundError as exc:
        print(f"parcels: error: missing input {Path(str(exc.filename)).name}; run normalise first")
        return 1
    if report:
        for line in result.report():
            print(line)
    else:
        print(
            f"parcels: {result.parcel_count} parcels, {sum(result.ground.values())} ground areas, {result.buildings_assigned}/"
            f"{result.buildings_total} buildings assigned "
            f"({len(result.match.new)} new, {len(result.match.retired)} retired IDs); "
            "see --report"
        )
    return 0
