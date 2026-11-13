"""Runtime attributes: baked candidate points for the game server (``attribute_points.json``, issue #263).

The game never sees ground areas (the pipeline bakes them into terrain colours and detail), so the server cannot ask for
"a random spot inside a Bigfoot area". For every **runtime attribute** it can instead read a pool of candidate points.
The runtime attributes are an explicit allowlist, the ``runtime_points`` section of ``rules.yaml`` (first and only entry:
``event_spawn: bigfoot``); every other attribute stays in the build.

For each entry the export stage takes every area that carries it, across stacked types (ADR 0015):

* the ground areas whose type carries the attribute (a type of ``ground.types`` that ticks ``event_spawn``), and
* the hand-drawn attribute layers (``ground_layers`` of ``parcels.json``) with a stacked type that carries it,

unions them, keeps the part inside the terrain coverage and clear of buildings and water (not of trees: the Bigfoot walks
through and tramples them, and buildings are solid for it), and draws seeded, uniformly distributed points in what is left,
the way ``props.make_spawns`` does. The count is a density rule in data: one point per ``m2_per_point`` of that usable
ground, kept between ``min_points`` and ``max_points``. No polygons are exported; a point test against the live area is
not offered (add it when a feature needs it).
"""

import bisect
import math
import random

import numpy as np
import shapely
from shapely.geometry import shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.ground_attributes import ATTRIBUTES, LIST_VALUES, UNION, carries_value
from drycastle_pipeline.props import seeded

ATTRIBUTE_POINTS_FORMAT_VERSION = 1
FILE_NAME = "attribute_points.json"

RULE_KEYS = ("m2_per_point", "min_points", "max_points", "building_clearance_m", "water_clearance_m")
MAX_ATTEMPTS_PER_POINT = 30  # rejected draws (a rounded point that falls just outside) before the pool is left short


class AttributePointsError(ValueError):
    """The ``runtime_points`` rules are wrong."""


def allowlist(section: dict) -> list[tuple[str, str, dict]]:
    """The runtime attributes of the ``runtime_points`` rules section as ``(attribute, value, rule)``, in catalog order.

    Raises ``AttributePointsError`` for an attribute that is not a list attribute of the catalog, a value it cannot hold, an unknown
    key or a value out of range: the allowlist is explicit, so a mistake is an error and never a silent skip."""
    out: dict[tuple[str, str], dict] = {}
    for attribute, values in section.items():
        if attribute not in LIST_VALUES or ATTRIBUTES[attribute].stacking != UNION:
            raise AttributePointsError(f"runtime_points.{attribute}: not a list attribute of the catalog ({', '.join(LIST_VALUES)})")
        if not isinstance(values, dict):
            raise AttributePointsError(f"runtime_points.{attribute}: expected {{value: rule}}")
        for value, rule in values.items():
            where = f"runtime_points.{attribute}.{value}"
            if value not in LIST_VALUES[attribute]:
                raise AttributePointsError(f"{where}: unknown value (known: {', '.join(LIST_VALUES[attribute])})")
            if not isinstance(rule, dict) or set(rule) != set(RULE_KEYS):
                raise AttributePointsError(f"{where}: needs exactly the keys {', '.join(RULE_KEYS)}")
            m2, lo, hi = rule["m2_per_point"], rule["min_points"], rule["max_points"]
            if not (isinstance(m2, int | float) and m2 > 0):
                raise AttributePointsError(f"{where}.m2_per_point: must be a number above 0")
            if not (isinstance(lo, int) and isinstance(hi, int) and 0 <= lo <= hi):
                raise AttributePointsError(f"{where}: min_points and max_points must be whole numbers with 0 <= min_points <= max_points")
            for key in ("building_clearance_m", "water_clearance_m"):
                if not (isinstance(rule[key], int | float) and rule[key] >= 0):
                    raise AttributePointsError(f"{where}.{key}: must be a number of metres, 0 or more")
            out[(attribute, value)] = rule
    order = [(a, v) for a in ATTRIBUTES for v in LIST_VALUES.get(a, ())]
    return [(a, v, out[(a, v)]) for a, v in order if (a, v) in out]


def carrying_areas(
    parcels_doc: dict, types: dict[str, dict], attribute: str, value: str
) -> list[tuple[str, BaseGeometry]]:
    """``(id, polygon)`` of every area that carries ``attribute`` with ``value``, local metres, sorted by id.

    The ground areas of a type that carries it (their own polygon: outside the parcels) and the attribute layers with a
    stacked type that carries it (the hand-drawn region's whole polygon). An area carries what its own type and every layer
    over it carry, so both are taken and the union is what counts."""
    out: list[tuple[str, BaseGeometry]] = []
    for area in parcels_doc.get("ground", []):
        if carries_value(types.get(area["kind"]), attribute, value):
            out.append((area["id"], shape(area["polygon"])))
    for layer in parcels_doc.get("ground_layers", []):
        if any(carries_value(types.get(name), attribute, value) for name in layer["types"]):
            out.append((layer["id"], shape(layer["polygon"])))
    return sorted(out, key=lambda t: t[0])


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


def build_attribute_points(
    parcels_doc: dict,
    buildings: list[dict],
    terrain,
    water_doc: dict,
    types: dict[str, dict],
    section: dict,
) -> tuple[dict | None, list[str]]:
    """The ``attribute_points.json`` document and its warnings, or ``(None, [])`` when no area carries a runtime attribute.

    ``parcels_doc`` is ``parcels.json`` (ground areas and layers), ``buildings`` the ``buildings.json`` records, ``terrain``
    a ``Terrain`` (coverage and heights), ``water_doc`` the ``water.json`` document, ``types`` the effective ground types
    and ``section`` the ``runtime_points`` rules. An attribute that is on the allowlist and has areas is always in the
    document (with no points when nothing usable is left, and a warning); one with no areas is not."""
    footprints = [footprint_polygon(b["footprint"]) for b in buildings]
    water = [shape(b["polygon"]) for b in water_doc.get("bodies", [])]
    warnings: list[str] = []
    entries = []
    zones: dict[tuple[str, float], BaseGeometry] = {}

    def zone(kind: str, geoms: list[BaseGeometry], clearance: float) -> BaseGeometry:
        if (kind, clearance) not in zones:
            zones[(kind, clearance)] = unary_union(geoms).buffer(clearance) if geoms else shapely.Polygon()
        return zones[(kind, clearance)]

    for attribute, value, rule in allowlist(section):
        areas = carrying_areas(parcels_doc, types, attribute, value)
        if not areas:
            continue
        name = f"{attribute}: {value}"
        union = unary_union([g if g.is_valid else g.buffer(0) for _, g in areas])
        usable = (
            union.intersection(terrain.coverage)
            .difference(zone("buildings", footprints, float(rule["building_clearance_m"])))
            .difference(zone("water", water, float(rule["water_clearance_m"])))
        )
        pieces = [g for g in getattr(usable, "geoms", [usable]) if g.geom_type == "Polygon" and g.area > 0.01]
        usable = unary_union(pieces) if pieces else shapely.Polygon()
        count = point_count(usable.area, rule)
        xy = sample_points(usable, count, seeded(f"attribute_points:{attribute}:{value}"))
        if not xy:
            warnings.append(
                f"{name}: {len(areas)} area(s) carry it but no usable ground is left (outside the terrain, or inside the "
                "clearance of buildings and water); no points"
            )
        elif len(xy) < count:
            warnings.append(f"{name}: drew {len(xy)} of {count} points")
        z = terrain.surface(np.array([p[0] for p in xy]), np.array([p[1] for p in xy])) if xy else []
        entries.append(
            {
                "attribute": attribute,
                "value": value,
                "areas": [aid for aid, _ in areas],
                "area_m2": round(usable.area, 1),
                "points": [[x, y, round(float(h), 3)] for (x, y), h in zip(xy, z, strict=True) if math.isfinite(h)],
            }
        )
    if not entries:
        return None, warnings
    return (
        {
            "version": ATTRIBUTE_POINTS_FORMAT_VERSION,
            "axes": "points: local metres, x east, y north (as parcels.json), z the terrain height (as terrain.json)",
            "attributes": entries,
        },
        warnings,
    )
