"""Ground areas: the typed ground outside the parcels (``ground`` in ``rules.yaml``, made by the parcels stage).

Only a property is a parcel: a cadastral parcel that holds a building, or that is in a property group (``edits.json``)
with one that does. All the other land of the cadastre (fields, forest, water, the land the roads lie on, loose
gardens) is not kept as parcels; it is cut into **ground areas** by type instead:

* a ground patch of ``edits.json`` (``ground_patches``) is a hand-drawn ground area: its polygon outside the parcels is
  ground of its type, whatever the rules say (:func:`drawn_ground`; where two overlap the higher ID wins). A patch of a
  water type (it has a ``water_y``) is no ground area: it is its own body of water (``water.py``);
* then the types of ``ground.types`` are tried in their order and each takes what is left of its source: OSM
  landuse/natural polygons (``osm``), or the flood dike of ``detail.dike`` (``rule: dike``, see :func:`dike_ground`);
* what no type claims is ``ground.default``.

A ground area is written to ``parcels.json`` under ``ground`` in the shape of a parcel (``id`` ``A-0001``..., ``kind``
= its type, ``polygon``, ``area``, ``anchor``, ``chunk``), so the stages that only ask what the ground is (terrain
colour, detail recipes, trees, water) read parcels and ground areas alike through :func:`ground_units`.

**A type is a named bundle of terrain attributes** (ADR 0015, 0022; the catalog is
:mod:`drycastle_pipeline.ground_attributes`): the entry of ``ground.types`` has ``osm`` or ``rule`` (where it takes land
from, none: hand-drawn or the default only) and ``attributes``: terrain ``colours``, detail ``recipe`` (one of
``detail.RECIPES``) with optional ``detail`` settings laid over that recipe's section of the rules, and ``water``. (Trees
are not a property of a type: they come from the orthophoto only. What an area means in play is a zone, ``zones.py``.)
Every point has exactly one ground type, so a ground area has one ``kind`` and there is nothing to stack. The user can
override and extend the types in ``edits.json`` (``ground_types``, see :func:`ground_types`); every stage that reads the
types passes the edits path. Ground areas have no fence, no edits and no cadastral reference, and their IDs are only
stable while the inputs are (they are numbered by type and place).
"""

import copy
from pathlib import Path

from shapely.geometry import Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.ground_attributes import ATTRIBUTES, attributes_of, carries_terrain
from drycastle_pipeline.rules import DEFAULT_RULES, _merge, load_section

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


def carve_parcel(
    geom: BaseGeometry, footprints: list[BaseGeometry], margin_m: float, keep_out: BaseGeometry
) -> tuple[BaseGeometry | None, BaseGeometry]:
    """A parcel cut down to its buildings: ``(new parcel, the rest of the old one)``.

    The new parcel is the footprints grown by ``margin_m`` (a slim yard round the building), minus ``keep_out`` (the
    other parcels, so no two parcels overlap). It may reach beyond the old outline when the building lies partly on
    other cadastral land; that land stops being ground (the caller takes it out of the free land). None when there
    is no building or nothing is left of it."""
    if not footprints:
        return None, geom
    new = unary_union(footprints).buffer(margin_m, join_style="mitre").difference(keep_out)
    parts = [p for p in _polys(new) if p.area >= 1.0]
    if not parts:
        return None, geom
    new = unary_union(parts)
    return new, geom.difference(new)


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
    patches: dict[str, dict], origin: tuple[float, float], types: dict[str, dict]
) -> tuple[list[tuple[str, Polygon]], list[str]]:
    """The hand-drawn ground of the edits' ``ground_patches``: ``(areas, warnings)``, polygons in local metres.

    ``areas`` are ``(type, polygon)``, the winner of an overlap first (the higher ID). A patch of a water type (it has a
    ``water_y``) makes no ground area: it is a body of water of its own. ``warnings`` name the patches whose type is not in
    ``types`` (or carries no attribute at all); such a patch is skipped."""
    from drycastle_pipeline.edits import polygon_ring
    from drycastle_pipeline.patches import patch_rank

    areas: list[tuple[str, Polygon]] = []
    warnings: list[str] = []
    for pid in sorted(patches, key=patch_rank, reverse=True):
        entry = patches[pid]
        name = entry["type"]
        if name not in types or not carries_terrain(types[name]):
            warnings.append(f"ground_patches.{pid}: unknown ground type {name!r} (ground.types has {', '.join(types)}); skipped")
            continue
        if "water_y" in entry:
            continue
        areas.append((name, Polygon([(x - origin[0], y - origin[1]) for x, y in polygon_ring(entry["polygon"])])))
    return areas, warnings


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
        if name == cfg["default"] or not carries_terrain(spec):  # a type without terrain attributes cannot decide what land is
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


def clip_areas(areas: list[dict], coverage: BaseGeometry) -> list[dict]:
    """The ground areas (``id``, ``kind``, ``area``, ``polygon`` as in parcels.json) limited to ``coverage`` (the terrain).

    An area keeps its ID and its largest piece inside; further pieces of a cut one get new IDs after the last. One wholly
    outside is dropped. An area wholly inside is returned as it is."""
    last = max((int(a["id"][2:]) for a in areas), default=0)
    out: list[dict] = []
    for a in areas:
        poly = shape(a["polygon"])
        if not poly.is_valid:
            poly = poly.buffer(0)
        if poly.within(coverage):
            out.append(a)
            continue
        pieces = sorted(_polys(poly.intersection(coverage)), key=lambda p: -p.area)
        for i, piece in enumerate(pieces):
            if piece.area < 1.0:
                continue
            if i:
                last += 1
            out.append({**a, "id": a["id"] if i == 0 else f"A-{last:04d}", "area": round(piece.area, 2), "polygon": piece.__geo_interface__})
    return out


def ground_units(doc: dict) -> list[dict]:
    """The parcels and the ground areas of a ``parcels.json`` document: every unit of ground that has a kind."""
    return doc["parcels"] + doc.get("ground", [])


# --- the type table -------------------------------------------------------------------------


def rules_ground_types(rules_path: Path = DEFAULT_RULES) -> dict[str, dict]:
    """``ground.types`` of the rules by name, every entry a
    dict; the user's overrides of ``edits.json`` are not in it (see :func:`ground_types`)."""
    return {name: spec or {} for name, spec in load_section("ground", rules_path)["types"].items()}


def merge_ground_types(rules: dict[str, dict], edited: dict[str, dict]) -> dict[str, dict]:
    """``rules`` types with the user's ``edited`` types (``ground_types`` of ``edits.json``) laid over them: a key of
    an edited entry overrides that key of the rules entry (the dicts ``osm``, ``attributes`` and ``detail`` key by key,
    deeper dicts the same way, lists and values replace); an attribute set to ``null`` is removed (the type no longer
    carries it); a new name is a new type, appended at the end; an entry with ``deleted`` removes the rules type.
    Neither input is changed."""
    out = {name: copy.deepcopy(spec) for name, spec in rules.items()}
    for name, entry in edited.items():
        if entry.get("deleted"):
            out.pop(name, None)
            continue
        merged = _merge(out.get(name, {}), copy.deepcopy(entry))
        attrs = {k: v for k, v in (merged.get("attributes") or {}).items() if v is not None}
        if attrs:
            merged["attributes"] = attrs
        else:
            merged.pop("attributes", None)
        out[name] = merged
    return out


def ground_types(rules_path: Path = DEFAULT_RULES, edits_path: Path | None = None) -> dict[str, dict]:
    """The effective ground types by name: those of ``rules.yaml`` and, when ``edits_path`` is
    given, the user's overrides and additions of that edits file laid over them (:func:`merge_ground_types`)."""
    types = rules_ground_types(rules_path)
    if edits_path is None:
        return types
    from drycastle_pipeline.edits import load_edits

    return merge_ground_types(types, load_edits(edits_path).ground_types)


def ground_type_row(name: str, rules_spec: dict | None, spec: dict) -> dict:
    """One ground type of ``parcels.json`` ``ground_types`` for the editor: ``spec`` is the effective type (summary:
    colour, recipe, water, the attributes it carries, source), ``rules_spec`` the entry of rules.yaml (None: a
    type of the user, ``custom``), which the editor lays the current edits over."""
    attrs = attributes_of(spec)
    row = {
        "name": name,
        "colour": (attrs.get("colours") or ["#808080"])[0].lower(),
        "colours": [c.lower() for c in attrs.get("colours") or []],
        "recipe": attrs.get("recipe"),
        "water": bool(attrs.get("water")),
        "attributes": [k for k in ATTRIBUTES if k in attrs],
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
    return {name: attributes_of(spec)["colours"] for name, spec in types.items() if attributes_of(spec).get("colours")}


def ground_kind(unit: dict) -> str:
    """The name the terrain of a unit goes by: the ground type the edits gave the parcel (``ground_type``, laid on by
    :func:`with_parcel_types`), else its kind. Only the look of the ground (terrain colour, detail recipe) reads this; what the
    parcel is (fence, trees, buildings) stays its kind."""
    return unit.get("ground_type") or unit["kind"]


def with_parcel_types(units: list[dict], edits_path: Path | None) -> list[dict]:
    """``units`` with the ground type of the edits (``parcels.<ID>.ground_type``) laid on as ``ground_type``; a unit without
    one is the same dict. :func:`ground_kind` reads it."""
    if edits_path is None:
        return units
    from drycastle_pipeline.edits import load_edits

    own = {pid: e["ground_type"] for pid, e in load_edits(edits_path).parcels.items() if e.get("ground_type")}
    return [{**u, "ground_type": own[u["id"]]} if u["id"] in own else u for u in units] if own else units


def water_kinds(types: dict[str, dict]) -> set[str]:
    return {name for name, spec in types.items() if attributes_of(spec).get("water")}
