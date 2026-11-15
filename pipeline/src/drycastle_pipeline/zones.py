"""Zones: what a marked area means in play (ADR 0022). The catalog of zone kinds and the candidate points for the server.

A **zone kind** says what an area means in play: ``bigfoot`` (the event may start there), ``player_spawn`` (players may
spawn there), one kind per wild species (``deer`` ... that species may live there). The kinds are a fixed catalog in code
(:data:`KINDS`): a kind needs something in the pipeline or the game that reads it, so a new kind is one entry here, its
section in ``rules.yaml`` ``zones`` and the code that reads it. The editor shows the catalog from ``parcels.json``
``zone_kinds``.

A **zone** is an area of one kind. It never changes the terrain (a zone over a forest does not cut the forest) and zones
overlap freely. It comes from two places that are the same to the export:

* a **drawn zone**: a polygon of ``edits.json`` ``zones`` (``Z-0001``), and
* a **rule zone**: a kind whose ``rules.yaml`` ``zones.<kind>.where_ground`` lists ground types applies wherever a ground
  area of one of them lies (deer wherever there is forest), nothing is drawn.

The game never sees ground areas or zones (the pipeline bakes the ground into terrain colours and detail), so the server
cannot ask for "a random spot inside a Bigfoot zone". For every kind with zones the export stage instead writes a pool of
candidate points (``zone_points.json``): the union of its areas, inside the terrain coverage and clear of buildings and
water (not of trees: the Bigfoot walks through and tramples them, and buildings are solid for it), with seeded, uniformly
distributed points, the way ``props.make_spawns`` does. The count is a density rule in data: one point per ``m2_per_point``
of that usable ground, kept between ``min_points`` and ``max_points``. No polygons are exported.

To the user all of these are just zones: one kind, one count, each one selectable, movable, retypable, deletable. The editor
does that to a rule zone lazily: editing one writes a drawn zone with its polygon (nothing for a plain delete) and a **cut**
(``edits.json`` ``zone_cuts``, ``ZC-0001``): the polygon the area had, of its kind, that the export subtracts from the rule zone
of that kind (:func:`zone_cuts`, :data:`CUT_BUFFER_M`). A cut is geometry, not a ground area id: the ids are not stable across
builds. A cut never touches drawn zones (the detached copy is one). Nothing cut: the export is exactly what it was.

``player_spawn`` has no points: ``props.make_spawns`` takes the ground areas inside it (the whole areas of the rule zone's ground types and
:func:`drawn_spawn_units`) as places players may spawn and writes ``spawns.json``.
"""

import bisect
import math
import random
from dataclasses import dataclass

import numpy as np
import shapely
from shapely.geometry import Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

ZONE_POINTS_FORMAT_VERSION = 2
FILE_NAME = "zone_points.json"

#: The kind that is read by ``props.make_spawns`` instead of being exported as points.
PLAYER_SPAWN = "player_spawn"
BIGFOOT = "bigfoot"

#: The wildlife species that are zone kinds. They must match the species ids of godot/shooter/data/animals.json
#: (a test checks it); the server spawns a species at the points of its kind.
WILDLIFE = ("deer", "doe", "stag", "moose", "boar", "bear", "fox", "wolf", "rabbit", "raccoon", "skunk")

#: The flying wildlife (godot/shooter/data/wildlife.json ``flocks``): zone kinds too, not species of animals.json. A flock
#: starts at one of the points of its kind and then flies about.
FLOCKS = ("crow",)

RULE_KEYS = ("m2_per_point", "min_points", "max_points", "building_clearance_m", "water_clearance_m")
#: A cut is grown by this much before it is subtracted, so a rebuilt, slightly different area leaves no sliver.
CUT_BUFFER_M = 0.5
MAX_ATTEMPTS_PER_POINT = 30  # rejected draws (a rounded point that falls just outside) before the pool is left short


@dataclass(frozen=True)
class ZoneKind:
    name: str
    label: str
    group: str  # event, player or wildlife
    tint: str  # the colour of its overlay in the editor


EVENT_TINT = "#c46be0"
PLAYER_TINT = "#4aa3e8"
WILDLIFE_TINT = "#6bc27a"

#: The catalog, in order: the order of ``zone_kinds`` of parcels.json and of the entries of zone_points.json.
KINDS: tuple[ZoneKind, ...] = (
    ZoneKind(BIGFOOT, "Bigfoot", "event", EVENT_TINT),
    ZoneKind(PLAYER_SPAWN, "Player spawn", "player", PLAYER_TINT),
    *(ZoneKind(w, w.capitalize(), "wildlife", WILDLIFE_TINT) for w in (*WILDLIFE, *FLOCKS)),
)
KIND_NAMES = tuple(k.name for k in KINDS)


class ZonesError(ValueError):
    """The ``zones`` rules are wrong."""


def rules(section: dict) -> dict[str, dict]:
    """The ``zones`` rules section by kind, in catalog order.

    A kind with a density rule has all of :data:`RULE_KEYS`; ``player_spawn`` has none (it is not exported as points). Every
    kind may have ``where_ground``, a list of ground type names. Raises ``ZonesError`` for a kind that is not in the catalog, a
    missing or unknown key or a value out of range: the sections are explicit, so a mistake is an error and never a silent
    skip. Whether the ground types exist is checked by :func:`check_ground_types` (the types are not known here)."""
    if not isinstance(section, dict):
        raise ZonesError("zones: expected {kind: rule}")
    for kind in section:
        if kind not in KIND_NAMES:
            raise ZonesError(f"zones.{kind}: not a zone kind of the catalog ({', '.join(KIND_NAMES)})")
    out: dict[str, dict] = {}
    for kind in KIND_NAMES:
        if kind not in section:
            continue
        rule = section[kind]
        where = f"zones.{kind}"
        if not isinstance(rule, dict):
            raise ZonesError(f"{where}: expected an object")
        needed = () if kind == PLAYER_SPAWN else RULE_KEYS
        if set(rule) - {"where_ground"} != set(needed):
            raise ZonesError(
                f"{where}: needs exactly the keys {', '.join(needed) or 'none'} (and optionally where_ground)"
            )
        if "where_ground" in rule:
            names = rule["where_ground"]
            if not (isinstance(names, list) and names and all(isinstance(n, str) for n in names) and len(set(names)) == len(names)):
                raise ZonesError(f"{where}.where_ground: must be a non-empty list of ground type names, none twice")
        if needed:
            m2, lo, hi = rule["m2_per_point"], rule["min_points"], rule["max_points"]
            if not (isinstance(m2, int | float) and not isinstance(m2, bool) and m2 > 0):
                raise ZonesError(f"{where}.m2_per_point: must be a number above 0")
            if not (isinstance(lo, int) and isinstance(hi, int) and 0 <= lo <= hi):
                raise ZonesError(f"{where}: min_points and max_points must be whole numbers with 0 <= min_points <= max_points")
            for key in ("building_clearance_m", "water_clearance_m"):
                if not (isinstance(rule[key], int | float) and not isinstance(rule[key], bool) and rule[key] >= 0):
                    raise ZonesError(f"{where}.{key}: must be a number of metres, 0 or more")
        out[kind] = rule
    return out


def check_ground_types(section_rules: dict[str, dict], types: dict[str, dict]) -> None:
    """Raise ``ZonesError`` for a ``where_ground`` that names a ground type that does not exist."""
    for kind, rule in section_rules.items():
        unknown = [n for n in rule.get("where_ground", []) if n not in types]
        if unknown:
            raise ZonesError(f"zones.{kind}.where_ground: unknown ground type {', '.join(unknown)} (known: {', '.join(types)})")


def kind_rows(section_rules: dict[str, dict]) -> list[dict]:
    """The ``zone_kinds`` of parcels.json: every kind of the catalog, in order, with the ground types of its rule zone."""
    return [
        {
            "name": k.name,
            "label": k.label,
            "group": k.group,
            "tint": k.tint,
            "where_ground": list(section_rules.get(k.name, {}).get("where_ground") or []) or None,
        }
        for k in KINDS
    ]


def drawn_zones(zones: dict[str, dict], origin: list[float] | tuple[float, float]) -> dict[str, tuple[str, Polygon]]:
    """``{id: (kind, polygon)}`` of the ``zones`` of the edits, polygons in local metres (made valid), sorted by id."""
    from drycastle_pipeline.edits import polygon_ring

    out: dict[str, tuple[str, Polygon]] = {}
    for zid in sorted(zones):
        ring = [(x - origin[0], y - origin[1]) for x, y in polygon_ring(zones[zid]["polygon"])]
        out[zid] = (zones[zid]["kind"], Polygon(ring))
    return out


def zone_cuts(cuts: dict[str, dict], origin: list[float] | tuple[float, float]) -> dict[str, BaseGeometry]:
    """``{kind: region}`` of the ``zone_cuts`` of the edits: the union of the kind's cut polygons grown by
    :data:`CUT_BUFFER_M`, in local metres."""
    from drycastle_pipeline.edits import polygon_ring

    by_kind: dict[str, list[BaseGeometry]] = {}
    for cid in sorted(cuts):
        ring = [(x - origin[0], y - origin[1]) for x, y in polygon_ring(cuts[cid]["polygon"])]
        poly = Polygon(ring)
        by_kind.setdefault(cuts[cid]["kind"], []).append((poly if poly.is_valid else poly.buffer(0)).buffer(CUT_BUFFER_M))
    return {k: unary_union(v) for k, v in by_kind.items()}


def rule_parts(area: dict) -> list[tuple[str, Polygon]]:
    """The selectable pieces of a ground area, ``(id, polygon)``: the area's id for one outline, ``A-0012.1``, ``A-0012.2``, ...
    for each outline of a multi-part area. Holes are not part of the outline (an editor works on outlines)."""
    geom = shape(area["polygon"])
    parts = list(getattr(geom, "geoms", [geom]))
    return [(area["id"] if len(parts) == 1 else f"{area['id']}.{i}", Polygon(p.exterior)) for i, p in enumerate(parts, 1) if p.geom_type == "Polygon"]


def rule_part_is_cut(poly: BaseGeometry, cut: BaseGeometry | None) -> bool:
    """True when a cut (:func:`zone_cuts`, of the part's kind) took the whole part out: what is left is a sliver."""
    return cut is not None and poly.intersects(cut) and poly.difference(cut).area <= max(1.0, 0.02 * poly.area)


def _cut_region(geom: BaseGeometry, cut: BaseGeometry | None) -> BaseGeometry | None:
    """``geom`` less ``cut``: itself (the same object) when nothing is cut, ``None`` when nothing is left."""
    if cut is None or not geom.intersects(cut):
        return geom
    left = (geom if geom.is_valid else geom.buffer(0)).difference(cut)
    return None if left.is_empty or left.area <= 0.01 else left


def kind_areas(
    kind: str,
    parcels_doc: dict,
    drawn: dict[str, tuple[str, Polygon]],
    where_ground: list[str] | None,
    cuts: dict[str, BaseGeometry] | None = None,
) -> list[tuple[str, BaseGeometry]]:
    """``(id, polygon)`` of every zone of ``kind``, local metres, sorted by id: the ground areas of the rule zone's ground
    types (their own polygon, less the kind's cuts) and the drawn zones of the kind."""
    out: list[tuple[str, BaseGeometry]] = []
    cut = (cuts or {}).get(kind)
    if where_ground:
        for area in parcels_doc.get("ground", []):
            if area["kind"] in where_ground and (left := _cut_region(shape(area["polygon"]), cut)) is not None:
                out.append((area["id"], left))
    out += [(zid, poly) for zid, (k, poly) in drawn.items() if k == kind]
    return sorted(out, key=lambda t: t[0])


def rule_spawn_units(parcels_doc: dict, where_ground: list[str] | None, cuts: dict[str, BaseGeometry] | None) -> dict[str, dict]:
    """``{area id: unit}`` of the ground areas of the ``player_spawn`` rule zone that a cut touches, as units shaped like a
    parcel with the cut taken out. ``polygon`` is ``None`` for an area that is cut away completely. Areas no cut touches are
    not here. ``props.make_spawns`` replaces those areas by these."""
    cut = (cuts or {}).get(PLAYER_SPAWN)
    out: dict[str, dict] = {}
    if cut is None:
        return out
    for area in sorted(parcels_doc.get("ground", []), key=lambda a: a["id"]):
        if area["kind"] not in (where_ground or []):
            continue
        poly = shape(area["polygon"])
        if not poly.intersects(cut):
            continue
        left = _cut_region(poly, cut)
        out[area["id"]] = {"id": area["id"], "kind": area["kind"], "polygon": None if left is None else left.__geo_interface__}
    return out


def drawn_spawn_units(
    parcels_doc: dict,
    drawn: dict[str, tuple[str, Polygon]],
    where_ground: list[str] | None,
    cuts: dict[str, BaseGeometry] | None = None,
) -> list[dict]:
    """The part of every ground area that lies inside a drawn ``player_spawn`` zone, as units shaped like a parcel
    (``id`` and ``kind`` of the area, ``polygon``), sorted by id. ``props.make_spawns`` takes spawn points from them.

    The areas of the rule zone's ground types (``where_ground``) are not here: they are whole spawn kinds, which
    ``make_spawns`` takes by their kind. Only what a cut took out of them is here, where a drawn zone covers it again (a
    rule area that was moved)."""
    inside = [g if g.is_valid else g.buffer(0) for k, g in drawn.values() if k == PLAYER_SPAWN]
    if not inside:
        return []
    zone = unary_union(inside)
    cut = (cuts or {}).get(PLAYER_SPAWN)
    out = []
    for area in sorted(parcels_doc.get("ground", []), key=lambda a: a["id"]):
        poly = shape(area["polygon"])
        if not poly.intersects(zone):
            continue
        if area["kind"] in (where_ground or []):
            if cut is None or not poly.intersects(cut):
                continue
            part = poly.intersection(zone).intersection(cut)
        else:
            part = poly.intersection(zone)
        pieces = [g for g in getattr(part, "geoms", [part]) if g.geom_type == "Polygon" and g.area > 0.01]
        if pieces:
            out.append({"id": area["id"], "kind": area["kind"], "polygon": unary_union(pieces).__geo_interface__})
    return out


def _triangles(geom: BaseGeometry) -> list[tuple[tuple[float, float], tuple[float, float], tuple[float, float]]]:
    """The constrained Delaunay triangles of a polygonal geometry, in a stable order (nothing is sampled across a hole)."""
    tris = []
    for t in shapely.constrained_delaunay_triangles(geom).geoms:
        c = list(t.exterior.coords)[:3]
        tris.append((c[0], c[1], c[2]))
    tris.sort(key=lambda t: (round((t[0][0] + t[1][0] + t[2][0]) / 3, 3), round((t[0][1] + t[1][1] + t[2][1]) / 3, 3)))
    return tris


def sample_points(region: BaseGeometry, count: int, rng: random.Random) -> list[tuple[float, float]]:
    """``count`` points (fewer only if the draws keep missing) uniformly distributed over ``region``, seeded by ``rng``.

    A triangle is picked in proportion to its area and a point drawn inside it, so a region of many pieces and holes gets
    its share everywhere with no rejected draws; the point is rounded to a millimetre and kept only if it is still inside."""
    if count <= 0 or region.is_empty:
        return []
    tris = _triangles(region)
    cumulative: list[float] = []
    total = 0.0
    for (ax, ay), (bx, by), (cx, cy) in tris:
        total += abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) / 2
        cumulative.append(total)
    if total <= 0:
        return []
    shapely.prepare(region)
    out: list[tuple[float, float]] = []
    for _ in range(count * MAX_ATTEMPTS_PER_POINT):
        if len(out) >= count:
            break
        (ax, ay), (bx, by), (cx, cy) = tris[min(bisect.bisect_left(cumulative, rng.random() * total), len(tris) - 1)]
        u, v = rng.random(), rng.random()
        if u + v > 1:
            u, v = 1 - u, 1 - v
        x, y = round(ax + u * (bx - ax) + v * (cx - ax), 3), round(ay + u * (by - ay) + v * (cy - ay), 3)
        if shapely.contains_xy(region, x, y):
            out.append((x, y))
    return out


def point_count(usable_m2: float, rule: dict) -> int:
    """The density rule: one point per ``m2_per_point`` of usable ground, between ``min_points`` and ``max_points``; none
    where there is no usable ground."""
    if usable_m2 <= 0:
        return 0
    return min(int(rule["max_points"]), max(int(rule["min_points"]), round(usable_m2 / float(rule["m2_per_point"]))))


def build_zone_points(
    parcels_doc: dict,
    drawn: dict[str, tuple[str, Polygon]],
    buildings: list[dict],
    terrain,
    water_doc: dict,
    section_rules: dict[str, dict],
    cuts: dict[str, BaseGeometry] | None = None,
) -> tuple[dict | None, list[str]]:
    """The ``zone_points.json`` document and its warnings, or ``(None, [])`` when no kind has a zone.

    ``parcels_doc`` is ``parcels.json`` (the ground areas), ``drawn`` the drawn zones (:func:`drawn_zones`), ``buildings`` the
    ``buildings.json`` records, ``terrain`` a ``Terrain`` (coverage and heights), ``water_doc`` the ``water.json`` document
    ``section_rules`` the ``zones`` rules (:func:`rules`) and ``cuts`` the cut regions (:func:`zone_cuts`). A kind that has zones is always in the document (with no points
    when nothing usable is left, and a warning); one with no zones is not. ``player_spawn`` is never in it."""
    from drycastle_pipeline.buildings import footprint_polygon
    from drycastle_pipeline.props import seeded

    footprints = [footprint_polygon(b["footprint"]) for b in buildings]
    water = [shape(b["polygon"]) for b in water_doc.get("bodies", [])]
    warnings: list[str] = []
    entries = []
    cleared: dict[tuple[str, float], BaseGeometry] = {}

    def clearance(what: str, geoms: list[BaseGeometry], metres: float) -> BaseGeometry:
        if (what, metres) not in cleared:
            cleared[(what, metres)] = unary_union(geoms).buffer(metres) if geoms else shapely.Polygon()
        return cleared[(what, metres)]

    for zid, (kind, _) in drawn.items():
        if kind not in section_rules:
            warnings.append(f"{zid}: zone kind {kind} has no rule in rules.yaml zones; no points")
    for kind, rule in section_rules.items():
        if kind == PLAYER_SPAWN:
            continue
        areas = kind_areas(kind, parcels_doc, drawn, rule.get("where_ground"), cuts)
        if not areas:
            continue
        union = unary_union([g if g.is_valid else g.buffer(0) for _, g in areas])
        usable = (
            union.intersection(terrain.coverage)
            .difference(clearance("buildings", footprints, float(rule["building_clearance_m"])))
            .difference(clearance("water", water, float(rule["water_clearance_m"])))
        )
        pieces = [g for g in getattr(usable, "geoms", [usable]) if g.geom_type == "Polygon" and g.area > 0.01]
        usable = unary_union(pieces) if pieces else shapely.Polygon()
        count = point_count(usable.area, rule)
        xy = sample_points(usable, count, seeded(f"zone_points:{kind}"))
        if not xy:
            warnings.append(
                f"{kind}: {len(areas)} zone(s) but no usable ground is left (outside the terrain, or inside the "
                "clearance of buildings and water); no points"
            )
        elif len(xy) < count:
            warnings.append(f"{kind}: drew {len(xy)} of {count} points")
        z = terrain.surface(np.array([p[0] for p in xy]), np.array([p[1] for p in xy])) if xy else []
        entries.append(
            {
                "kind": kind,
                "areas": [aid for aid, _ in areas],
                "area_m2": round(usable.area, 1),
                "points": [[x, y, round(float(h), 3)] for (x, y), h in zip(xy, z, strict=True) if math.isfinite(h)],
            }
        )
    if not entries:
        return None, warnings
    return (
        {
            "version": ZONE_POINTS_FORMAT_VERSION,
            "axes": "points: local metres, x east, y north (as parcels.json), z the terrain height (as terrain.json)",
            "zones": entries,
        },
        warnings,
    )
