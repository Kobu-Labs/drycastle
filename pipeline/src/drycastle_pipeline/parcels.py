"""Parcels stage: registry matching, parcel kind rules, building assignment, ``parcels.json``."""

import json
import math
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from shapely import STRtree
from shapely.affinity import translate
from shapely.geometry import Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.normalise import DEFAULT_WORK_DIR
from drycastle_pipeline.registry import (
    DEFAULT_REGISTRY,
    MatchResult,
    load_registry,
    match_parcels,
    save_registry,
)
from drycastle_pipeline.parcel_fit import fit_roads
from drycastle_pipeline.rules import DEFAULT_RULES, ParcelRules, load_rules, load_section

KINDS = ("building_plot", "road", "forest", "field", "water", "garden", "other")
PARCELS_FORMAT_VERSION = 1


@dataclass
class Landuse:
    cls: str  # "landuse" or "natural"
    value: str
    geom: BaseGeometry


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


def classify_parcels(
    parcels: list[BaseGeometry],
    has_building: list[bool],
    highways: list[BaseGeometry],
    landuse: list[Landuse],
    buildings: list[BaseGeometry],
    rules: ParcelRules,
) -> list[str]:
    """Parcel kind per rules, in the order documented in ``rules.yaml``."""
    roads = unary_union([h.buffer(rules.road_buffer_width_m / 2) for h in highways])
    cover: dict[str, BaseGeometry] = {}
    for lu in landuse:
        kind = (rules.natural_kinds if lu.cls == "natural" else rules.landuse_kinds).get(lu.value)
        if kind:
            cover.setdefault(kind, []).append(lu.geom)
    cover = {k: unary_union(v) for k, v in cover.items()}
    builtup = unary_union(
        [lu.geom for lu in landuse if lu.cls == "landuse" and lu.value in rules.builtup_landuse]
    )
    building_tree = STRtree(buildings)

    def ratio(parcel: BaseGeometry, other: BaseGeometry) -> float:
        if other.is_empty or parcel.area <= 0:
            return 0.0
        return parcel.intersection(other).area / parcel.area

    kinds = []
    for parcel, built in zip(parcels, has_building, strict=True):
        if built:
            kinds.append("building_plot")
        elif ratio(parcel, roads) >= rules.road_min_covered_ratio:
            kinds.append("road")
        else:
            best = max(
                sorted(cover),
                key=lambda k: ratio(parcel, cover[k]),
                default=None,
            )
            if best is not None and ratio(parcel, cover[best]) >= rules.min_cover_ratio:
                kinds.append(best)
                continue
            small = parcel.area <= rules.garden_max_area_m2
            in_builtup = ratio(parcel, builtup) >= rules.min_cover_ratio or len(
                building_tree.query_nearest(parcel, max_distance=rules.builtup_distance_m)
            )
            kinds.append("garden" if small and in_builtup else "other")
    return kinds


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

    def report(self) -> list[str]:
        m = self.match
        lines = [f"parcels: {self.parcel_count}"]
        lines += [f"  kind {k}: {self.kinds.get(k, 0)}" for k in KINDS]
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


def build_parcels(
    work_dir: Path = DEFAULT_WORK_DIR,
    registry_path: Path = DEFAULT_REGISTRY,
    rules: ParcelRules | None = None,
    rules_path: Path = DEFAULT_RULES,
) -> ParcelsResult:
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
    highways = [
        shape(f["geometry"])
        for f in _read(norm / "highways.geojson")
        if f["properties"]["highway"] in rules.road_highways
    ]
    landuse = [
        Landuse(f["properties"]["class"], f["properties"]["value"], shape(f["geometry"]))
        for f in _read(norm / "landuse.geojson")
    ]

    # Registry works in absolute EPSG:5514 metres, independent of the slice centre.
    absolute = {r: translate(g, ox, oy) for r, g in zip(refs, parcels, strict=True)}
    ring = frame.get("fetched_bounds_epsg5514")
    bounds = Polygon(ring) if ring else None
    match = match_parcels(load_registry(registry_path), absolute, rules.min_iou, bounds)
    save_registry(match.registry, registry_path)

    assignment = assign_buildings(parcels, buildings, rules.max_assign_distance_m)
    kinds = classify_parcels(
        parcels,
        [i in assignment.by_parcel for i in range(len(parcels))],
        highways,
        landuse,
        list(buildings.values()),
        rules,
    )

    # Road parcels fitted to their OSM road (parcel_fit in rules.yaml); the registry above keeps the cadastre's outlines.
    ways = {f["properties"]["osm_id"]: shape(f["geometry"]) for f in _read(norm / "highways.geojson")}
    fitted = fit_roads({match.ids[r]: g for r, g in zip(refs, parcels, strict=True)}, ways, load_section("parcel_fit", rules_path))
    parcels = [fitted[match.ids[r]] for r in refs]

    out = []
    for i, (ref, geom, kind) in enumerate(zip(refs, parcels, kinds, strict=True)):
        anchor = geom.representative_point()
        out.append(
            {
                "id": match.ids[ref],
                "cadastral_ref": ref,
                "polygon": geom.__geo_interface__,
                "area": round(geom.area, 2),
                "kind": kind,
                "anchor": [round(anchor.x, 3), round(anchor.y, 3)],
                "buildings": assignment.by_parcel.get(i, []),
                "chunk": chunk_index(anchor.x, anchor.y, rules.chunk_size_m),
            }
        )
    out.sort(key=lambda p: p["id"])
    doc = {
        "version": PARCELS_FORMAT_VERSION,
        "crs": frame["crs"],
        "origin_epsg5514": frame["origin_epsg5514"],
        "chunk_size_m": rules.chunk_size_m,
        "parcels": out,
        "unassigned_buildings": assignment.unassigned,
    }
    (work_dir / "parcels.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")

    return ParcelsResult(
        match=match,
        kinds=Counter(kinds),
        buildings_total=len(buildings),
        buildings_assigned=len(buildings) - len(assignment.unassigned),
        unassigned=assignment.unassigned,
        parcel_count=len(parcels),
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
            f"parcels: {result.parcel_count} parcels, {result.buildings_assigned}/"
            f"{result.buildings_total} buildings assigned "
            f"({len(result.match.new)} new, {len(result.match.retired)} retired IDs); "
            "see --report"
        )
    return 0
