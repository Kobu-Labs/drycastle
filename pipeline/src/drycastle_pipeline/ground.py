"""Ground areas: the typed ground outside the parcels (``ground`` in ``rules.yaml``, made by the parcels stage).

Only a property is a parcel: a cadastral parcel that holds a building, or that is in a property group (``edits.json``)
with one that does. All the other land of the cadastre (fields, forest, water, the land the roads lie on, loose
gardens) is not kept as parcels; it is cut into **ground areas** by type instead:

* a region of ``edits.json`` with a ``ground`` type is a hand-drawn ground area: its polygon outside the parcels
  is ground of that type, whatever the rules say (:func:`drawn_ground`; where two overlap the higher ID wins);
* then the types of ``ground.types`` are tried in their order and each takes what is left of its source: OSM
  landuse/natural polygons (``osm``), or the flood dike of ``detail.dike`` (``rule: dike``, see :func:`dike_ground`);
* what no type claims is ``ground.default``.

A ground area is written to ``parcels.json`` under ``ground`` in the shape of a parcel (``id`` ``A-0001``..., ``kind``
= its type, ``polygon``, ``area``, ``anchor``, ``chunk``), so the stages that only ask what the ground is (terrain
colour, detail recipes, trees, water) read parcels and ground areas alike through :func:`ground_units`.

**The type is the whole description of the ground** (one entry of ``ground.types``): where it comes from (``osm`` or
``rule``), its terrain ``colours``, its detail ``recipe`` (one of ``detail.RECIPES``, none: no detail) with optional
``detail`` settings laid over that recipe's section of the rules, its ``trees`` (``per_1000m2``, ``forest``: forest
trees), whether it is ``water`` and whether players may spawn on it (``spawns``). The functions at the end of this
module hand those to the stages. The user can override and extend the types in ``edits.json`` (``ground_types``, see
:func:`ground_types`); every stage that reads the types passes the edits path. Ground areas have no fence, no edits and no cadastral reference, and their IDs are
only stable while the inputs are (they are numbered by type and place).
"""

import copy
from pathlib import Path

from drycastle_pipeline.rules import DEFAULT_RULES, _merge, load_section

from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

ID_PREFIX = "A-"


def _polys(g: BaseGeometry) -> list[Polygon]:
    if g.is_empty:
        return []
    return [p for p in getattr(g, "geoms", [g]) if isinstance(p, Polygon)]


def kept_parcels(with_building: set[str], groups: dict[str, list[str]]) -> set[str]:
    """The parcel IDs that stay parcels: those with a building, and the members of a group that has one."""
    kept = set(with_building)
    for members in groups.values():
        if with_building.intersection(members):
            kept.update(members)
    return kept


def dike_ground(geoms: dict[str, BaseGeometry], ways: dict[str, BaseGeometry], dike_cfg: dict) -> BaseGeometry:
    """The flood dike of ``detail.dike`` as one outline: its ``parcels`` whole, and of each ``clips`` parcel the
    ground within ``within_m`` of the named way. ``geoms`` are the cadastral outlines by parcel ID (the dropped
    parcels only: a kept parcel's dike part stays with the parcel, see ``detail.Village.contexts``)."""
    parts = [geoms[pid] for pid in dike_cfg.get("parcels", []) if pid in geoms]
    for pid, clip in (dike_cfg.get("clips") or {}).items():
        way = ways.get(clip["osm_id"])
        if pid in geoms and way is not None:
            parts.append(geoms[pid].intersection(way.buffer(float(clip["within_m"]))))
    return unary_union(parts) if parts else Polygon()


def drawn_ground(
    regions: dict[str, dict], origin: tuple[float, float], types: dict[str, dict]
) -> tuple[list[tuple[str, Polygon]], list[str]]:
    """The hand-drawn ground areas of the edits' ``regions`` (those with a ``ground`` type) as ``(type, polygon)`` in
    local metres, the winner of an overlap first (the higher ID), and warnings for the types the rules do not know."""
    from drycastle_pipeline.edits import region_ring
    from drycastle_pipeline.regions import region_rank

    out, warnings = [], []
    for rid in sorted(regions, key=region_rank, reverse=True):
        name = regions[rid].get("ground")
        if name is None:
            continue
        if name not in types:
            warnings.append(f"regions.{rid}: unknown ground type {name!r} (ground.types has {', '.join(types)}); skipped")
            continue
        out.append((name, Polygon([(x - origin[0], y - origin[1]) for x, y in region_ring(regions[rid]["polygon"])])))
    return out, warnings


def build_ground(
    free: BaseGeometry,
    landuse: list[tuple[str, str, BaseGeometry]],
    dike: BaseGeometry,
    cfg: dict,
    drawn: list[tuple[str, Polygon]] | None = None,
    parcels: BaseGeometry | None = None,
) -> list[tuple[str, Polygon]]:
    """``free`` (the land outside the parcels) cut into ``(type, polygon)`` pieces, sorted by type order and place.

    ``landuse`` holds ``(class, value, geometry)`` of the OSM landuse/natural polygons. ``drawn`` are the hand-drawn
    areas (:func:`drawn_ground`), the first wins; they come before every rule type and may reach beyond ``free``, but
    never into ``parcels`` (the union of the parcels)."""
    min_area = float(cfg.get("min_area_m2", 1.0))
    out: list[tuple[str, Polygon]] = []
    left = free
    taken: BaseGeometry = parcels if parcels is not None else Polygon()
    for name, poly in drawn or []:
        mine = poly.difference(taken)
        taken = taken.union(poly)
        left = left.difference(poly)
        out += [(name, p) for p in _polys(mine)]
    for name, spec in cfg["types"].items():
        spec = spec or {}
        if name == cfg["default"]:
            continue
        if spec.get("rule") == "dike":
            source = dike
        else:
            osm = spec.get("osm") or {}
            source = unary_union([g for cls, value, g in landuse if value in osm.get(cls, [])])
        if source.is_empty:
            continue
        mine = left.intersection(source)
        left = left.difference(source)
        out += [(name, p) for p in _polys(mine)]
    out += [(cfg["default"], p) for p in _polys(left)]
    order = list(cfg["types"])
    out = [(name, p) for name, p in out if p.area >= min_area]
    out.sort(key=lambda t: (order.index(t[0]), round(t[1].centroid.x, 2), round(t[1].centroid.y, 2), -t[1].area))
    return out


def ground_units(doc: dict) -> list[dict]:
    """The parcels and the ground areas of a ``parcels.json`` document: every unit of ground that has a kind."""
    return doc["parcels"] + doc.get("ground", [])


# --- the type table -------------------------------------------------------------------------


def rules_ground_types(rules_path: Path = DEFAULT_RULES, area: str | None = None) -> dict[str, dict]:
    """``ground.types`` of the rules by name (with the map area's overlay, ``areas.<area>.ground``), every entry a
    dict; the user's overrides of ``edits.json`` are not in it (see :func:`ground_types`)."""
    return {name: spec or {} for name, spec in load_section("ground", rules_path, area)["types"].items()}


def merge_ground_types(rules: dict[str, dict], edited: dict[str, dict]) -> dict[str, dict]:
    """``rules`` types with the user's ``edited`` types (``ground_types`` of ``edits.json``) laid over them: a key of
    an edited entry overrides that key of the rules entry (the dicts ``osm`` and ``detail`` key by key,
    deeper dicts the same way, lists and values replace); a new name is a new type, appended at the end; an entry
    with ``deleted`` removes the rules type. Neither input is changed."""
    out = {name: copy.deepcopy(spec) for name, spec in rules.items()}
    for name, entry in edited.items():
        if entry.get("deleted"):
            out.pop(name, None)
        else:
            out[name] = _merge(out.get(name, {}), copy.deepcopy(entry))
    return out


def ground_types(rules_path: Path = DEFAULT_RULES, area: str | None = None, edits_path: Path | None = None) -> dict[str, dict]:
    """The effective ground types by name: those of ``rules.yaml`` (with the area overlay) and, when ``edits_path`` is
    given, the user's overrides and additions of that edits file laid over them (:func:`merge_ground_types`)."""
    types = rules_ground_types(rules_path, area)
    if edits_path is None:
        return types
    from drycastle_pipeline.edits import load_edits

    return merge_ground_types(types, load_edits(edits_path).ground_types)


def ground_type_row(name: str, rules_spec: dict | None, spec: dict) -> dict:
    """One ground type of ``parcels.json`` ``ground_types`` for the editor: ``spec`` is the effective type (summary:
    colour, recipe, water, spawns, source), ``rules_spec`` the entry of rules.yaml (None: a type of the user,
    ``custom``), which the editor lays the current edits over."""
    row = {
        "name": name,
        "colour": (spec.get("colours") or ["#808080"])[0].lower(),
        "colours": [c.lower() for c in spec.get("colours") or []],
        "recipe": spec.get("recipe"),
        "water": bool(spec.get("water")),
        "spawns": bool(spec.get("spawns")),
        "source": "rule" if spec.get("rule") else ("osm" if spec.get("osm") else "drawn"),
    }
    if rules_spec is None:
        row["custom"] = True
    else:
        row["rules"] = rules_spec
    return row


def ground_type_source(name: str, rules: dict[str, dict], edited: dict[str, dict]) -> str:
    """Where a type comes from: ``rules`` (as in rules.yaml), ``overridden`` (rules.yaml plus an override of the
    user), ``custom`` (made by the user) or ``deleted`` (a type of the rules the user removed)."""
    entry = edited.get(name)
    if name not in rules:
        return "custom"
    if entry is None:
        return "rules"
    return "deleted" if entry.get("deleted") else "overridden"


def type_colours(types: dict[str, dict]) -> dict[str, list[str]]:
    """Terrain shades by type, to lay over ``terrain.colours`` (which holds the parcel kinds and the fallback)."""
    return {name: spec["colours"] for name, spec in types.items() if spec.get("colours")}


def tree_density(types: dict[str, dict]) -> dict[str, float]:
    """Rule trees per 1000 m2 by type, to lay over ``props.trees.density_per_1000m2`` (the parcel kinds)."""
    return {name: float((spec.get("trees") or {}).get("per_1000m2", 0.0)) for name, spec in types.items()}


def forest_kinds(types: dict[str, dict]) -> set[str]:
    return {name for name, spec in types.items() if (spec.get("trees") or {}).get("forest")}


def water_kinds(types: dict[str, dict]) -> set[str]:
    return {name for name, spec in types.items() if spec.get("water")}


def spawn_kinds(types: dict[str, dict]) -> set[str]:
    return {name for name, spec in types.items() if spec.get("spawns")}
