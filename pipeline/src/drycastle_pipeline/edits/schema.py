"""Hand edits: ``pipeline/config/edits.json`` (editor-owned) -- schema, validator and writer.

The file is written by the editor (and by hand) and read by the props stage. It holds only the
developer's deliberate choices, so it survives every pipeline re-run. Keys are sorted and the
formatting is fixed, so git diffs stay small. Version 1::

    {"version": 1,
     "parcels": {"P-0412": {"fence": {...}}},
     "objects": [{"id": "o-0001", "asset": "prim:pole_lamp_1", "position": [x, y], "rotation_deg": 0}],
     "groups":  {"G-0001": {"parcels": ["P-0412", "P-0413"], "fence": {...}}},
     "ground_patches": {"GP-0001": {"type": "meadow", "polygon": [[x, y], ...]}},
     "zones":   {"Z-0001": {"kind": "bigfoot", "polygon": [[x, y], ...]}},
     "zone_cuts": {"ZC-0001": {"kind": "deer", "polygon": [[x, y], ...]}},
     "cables":  {"C-0001": {"from": "o-0012", "to": "o-0015"}},
     "mineral_nodes": {"MN-0001": {"mineral": "smetiskomite", "position": [x, y]}},
     "ground_types": {"meadow": {"attributes": {"colours": ["#7aa860"]}},
                      "my_dunes": {"attributes": {"colours": ["#d8c38a"], "recipe": "field"}}}}

``fence`` (all keys optional)
    mode              auto | none | custom. Default auto, or custom when type, height_m,
                      thickness_m, colour or gaps is given without a mode.
                      auto = the rules in rules.yaml (props.fences); none = no fence;
                      custom = always fenced on the boundary, no seeded probability, no kind
                      filter (building and road clearances still apply).
    type              picket | wire | brick_red | brick_white | stone_wall | concrete (custom
                      only; brick_wall is the old name of brick_red). Each maps to the defaults in
                      FENCE_TYPES below and to a style in props/fencestyles.py (panel texture, column,
                      cap).
    column            one of COLUMN_TYPES below (custom only): the posts or piers at the section
                      joints and run ends, replacing the type's own (e.g. picket between brick_red
                      piers); "none" leaves them out.
    height_m, thickness_m  numbers > 0 (custom only), overriding the type.
    colour            "#rrggbb" (custom only), overriding the type. It is a tint: picket, wire and
                      concrete are near-white textures, so the colour is their paint; brick and
                      stone carry their own colours (white = as drawn).
    house_colour      true (custom only; type brick_red, brick_white or concrete, else ignored): the colour is the wall
                      colour of the parcel's house (its largest building; a group member without one
                      takes the group's), over ``colour``. No house: ``colour`` (or the type's) applies.
    gaps              list of {"at": [x, y], "width_m": n} (custom only). A gap is cut out of
                      the boundary, centred on the nearest boundary point to ``at``. ``at`` is
                      absolute EPSG:5514 (S-JTSK) metres, the same frame as objects.
    sides             list of {"at": [x, y], "mode"?, type?, column?, height_m?, thickness_m?, colour?,
                      house_colour?} (custom only): one side of the fence looks different. A side is the
                      boundary between two corners (a vertex turning more than SIDE_CORNER_DEG) of the
                      parcel, or of the group's outer edge; ``at`` (absolute, as gaps) picks the side
                      nearest to it. Its keys win over the fence's (then the type table of its type);
                      mode "none" leaves the side unfenced. A later entry for the same side wins.

``ground_type`` (a parcel's, optional)
    name of a ground type (as a patch's ``type``; not a water type): the parcel's ground looks like that type, its terrain
    colours and its detail ``recipe`` (a type without a recipe: no detail), instead of the yard of its kind. Kind, layout,
    fence and other parcels are unchanged. Needs the next make map-update.

``outline`` (a parcel's, optional)
    polygon [[x, y], ...], absolute EPSG:5514 (S-JTSK) metres (the frame of objects): replaces the cadastral outline of this one
    parcel (no key: untouched). A simple polygon of at least 3 points; the parcel's buildings must stay inside it (checked
    where it is applied, the parcels stage: a parcel whose outline leaves a building keeps its cadastral outline). Where
    it overlaps an unedited parcel, the edited one wins and the neighbour is cut back by the overlap; two edited outlines that
    overlap are an error. Land the parcel no longer holds becomes ground (default ground unless another outline takes it).
    The outline is applied once, in the parcels stage, so fence, ground, detail and parcel lookup follow it: needs ``make parcels``
    and then ``make map-update``.

``objects`` (top level, a list; placed objects, emitted to objects.json, not instanced in props.json). An object
    belongs to no parcel: it only needs its absolute position, so a pole beside a road (ground, not a parcel)
    is as good as a car in a yard. Files written before this kept the list under ``parcels.<ID>.objects``;
    :func:`parse` still reads that and moves the objects here, the next write uses the new shape. The list is
    written sorted by id (so an undone removal or an edit never reorders the file).
    id                  str, unique within the whole file.
    asset               "pack:<pack>/<model>" or "prim:<name>".
    position            [x, y] absolute EPSG:5514 metres.
    z                   optional metres, absolute above the DMR datum (not relative to the terrain); omitted = terrain height
                        at runtime.
    rotation_deg        number, default 0.
    scale               number > 0, default 1 (uniform).
    size_m              optional [length, width, height], each a number > 0: the exact final bounding-box size in metres, length
                        along the object's local x (map east at rotation 0), width along its local map-y, height up. The model is
                        scaled non-uniformly by size_m / the asset's manifest size_m (glTF [x, y up, z]); colliders follow. Wins over
                        ``scale`` when both are present (``scale`` is then ignored).
    colour              optional "#rrggbb" override.
    solid               optional bool, default true; written only as false: no collision at all (players, vehicles and shots
                        pass through), for dressing laid over a climbable prim such as trash on prim:mound.
    (objects.json entries also carry ``parcel`` (the parcel containing the object, or null) and
    ``chunk``; both are derived.)

``groups`` (a property group: parcels that share one fence around their combined outer edge)
    parcels             non-empty list of unique parcel IDs. A parcel is in at most one group.
    fence               same shape as a parcel fence. A member's own fence overrides the group
                        fence field by field. Each piece of the group's outer edge takes the
                        settings of the member that owns it. Edges shared by two members get no
                        fence.

``ground_patches`` (a hand-drawn ground area of exactly one ground type, ADR 0022; ``GP-`` and 4 or more digits)
    type                name of a ground type: one of ``ground.types`` in rules.yaml or of ``ground_types`` below, not a
                        deleted one. The patch is ground of that type (terrain colour, detail, trees) outside the parcels,
                        before the rule types, on the next full run; the editor shows it at once as a coloured preview
                        (the type's first colour). A colour that no type has is a ground type of your own.
    polygon             one ring: a list of at least 3 [x, y] points in absolute S-JTSK metres, x
                        east, y north. Not closed by repeating the first point (a repeated last
                        point is tolerated), no repeated or collinear-only points, no
                        self-intersection (edges may not cross or touch each other). Patches are
                        independent of parcel boundaries. Where patches overlap, the higher ID
                        wins. The ``ground_patches`` key is only written when there are patches.
    water_y             a number, required when the type carries the ``water`` attribute and refused otherwise: the patch
                        is a body of water whose flat surface is at this height, in metres on the terrain's datum (as
                        terrain.json, e.g. 185.6). The body is also written to water.json (swimming, the shooter's water
                        surface) with its bed at the natural bed (terrain/bed.py: banks shelve, pools and riffles on a river,
                        the terrain under it carved to that bed).
    max_depth           optional number of metres (0 < x <= 20), only with water_y: the body's deepest
                        point. Its bed is scaled so the deepest point is this deep. Default: the class
                        depth in rules.yaml (water.river.pool_depth_m, water.pond.max_depth_m).

``zones`` (a marked area of exactly one zone kind, ADR 0022; ``Z-`` and 4 or more digits; see edits/zones.py)
    kind                a zone kind of the catalog (``zones.KIND_NAMES``): what the area means in play. A zone never
                        changes the terrain; zones overlap freely and add up. The export turns them into candidate points
                        for the server (``zone_points.json``), for ``player_spawn`` into spawn candidates and for ``plantation`` into polygons
                        (``zone_polygons.json``).
    polygon             as of a ground patch. The ``zones`` key is only written when there are zones.

``paths`` (worn footpaths, by ID ``PA-`` and 4 digits; the key is only written when there are any)
    points              [[x, y], ...], at least 2, absolute EPSG:5514 metres: the line people walk.
    width_m             optional, 0.3 to 4 (default 0.8): how wide the trodden ground is on average.
    closed              optional true: the line returns to its first point (a loop, at least 3 points).
    The detail stage lays bare trodden earth along it over whatever ground is there (``detail/footpaths.py``); parcels, ground
    areas and their kinds are unchanged. Needs the next make map-update.

``zone_cuts`` (internal: the editor's record that a zone that comes from a rule was moved, retyped or deleted; ``ZC-`` and 4
or more digits; see edits/zones.py). The user never sees these as zones.
    kind                a zone kind of the catalog.
    polygon             as of a ground patch: the area that was taken out, in absolute S-JTSK metres. The export subtracts it
                        (grown by zones.CUT_BUFFER_M, so a rebuilt, slightly different area leaves no sliver) from the rule
                        zone of that kind. It never touches drawn zones. Geometry, not ground area ids: those are not
                        stable across builds. The ``zone_cuts`` key is only written when there are cuts.

``mineral_nodes`` (a static mineral node: a crystal the game places at a fixed spot, GLOSSARY "Mineral node"; by ID ``MN-`` and 4 digits; the key is only written when there are any)
    mineral             lower_snake mineral id of the game (godot/ShooterCore/Mining/Minerals.cs, e.g. ``smetiskomite``); the game ignores one it does not know.
    position            [x, y] absolute EPSG:5514 metres.
    z                   optional metres on the terrain's datum (as an object's ``z``): a spot above the ground (a platform, a roof); omitted = the terrain height.
    Exported to ``mineral_nodes.json`` (``export.docs.build_mineral_nodes``), read by the game server (``MineralNodeFile``). Needs the next make export.

``cables`` (the wires strung between two placed utility poles, or from a pole to a house connector;
drawn by the game, see docs/utility-poles.md)
    from, to            ids of two different placed objects: utility poles (``prim:pole_*``) or
                        house connectors (``prim:connector_*``, placed on a wall or roof with a
                        ``z``). The direction has no meaning. A pair is joined by at most one cable.
                        A cable whose end no longer exists, or is neither, is skipped with a
                        warning by the props stage. The ``cables`` key is only written when there
                        are cables.

``ground_types`` (overrides and additions to the ground types of ``ground.types`` in rules.yaml, by name; see
ground/areas.py and ground/attributes.py. The key is only written when there are some.)
    name                lower_snake, ``^[a-z][a-z0-9_]{0,23}$``. A name of rules.yaml overrides that type, a new name
                        is a new type (appended after the rules' types). Names are IDs: there is no rename.
    attributes          the attributes the type carries (the fixed catalog of ground/attributes.py), each one key:
                          colours       1-6 terrain shades, each "#rrggbb" (replaces the type's list).
                          recipe        field | forest | village | dike (replaces the type's recipe).
                          detail        object laid over the shipped ``detail.<recipe>`` section of rules.yaml (deep
                                        merge); its top-level keys must exist in that section. Needs a recipe, own or
                                        the rules type's.
                          water         true.
                        A ground type is terrain only: ``spawns``, ``event_spawn``, ``wildlife`` and the like are unknown
                        attributes (what an area means in play is a zone). A type with only ``colours`` is valid.
                        A value ``null`` removes that attribute of the rules type (only for a type of rules.yaml).
    osm                 {landuse|natural: [str, ...]}: the OSM values the type takes (a key replaces that class's list).
                        A type that takes OSM land must carry terrain attributes.
    deleted             true (and nothing else): removes the type that rules.yaml defines. Not the default type and
                        not a type with a ``rule`` (the dike).
    There is no ``rule`` key: rule types stay in rules.yaml. Unknown keys are errors.

Unknown keys are validation errors. Entries that refer to parcels that no longer exist are not
errors: the props stage warns about them and skips them (see props.plan_edits).
"""

import contextlib
import copy
import json
import math
import os
import re
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

from shapely.geometry import LinearRing, Polygon

from drycastle_pipeline.core.config import PIPELINE_DIR
from drycastle_pipeline.edits.zones import KIND_NAMES
from drycastle_pipeline.ground.attributes import (
    ATTRIBUTES,
    COLOURS_MAX,
    FLAG_KEYS,
    RECIPES,
    TERRAIN_KEYS,
)

EDITS_VERSION = 1
DEFAULT_EDITS = PIPELINE_DIR / "config" / "edits.json"

FENCE_MODES = ("auto", "none", "custom")
DEFAULT_FENCE_TYPE = "picket"
# Each type is a height, thickness and colour (a tint, see the module docstring); explicit fence
# keys override them. How a type is drawn (texture, column, cap) is in fencestyles.FENCE_STYLES.
FENCE_TYPES: dict[str, dict[str, float | str]] = {
    "picket": {"height_m": 1.0, "thickness_m": 0.06, "colour": "#9a8064"},
    "wire": {"height_m": 1.1, "thickness_m": 0.03, "colour": "#5c5f66"},
    "brick_red": {"height_m": 1.8, "thickness_m": 0.3, "colour": "#ffffff"},
    "brick_white": {"height_m": 1.8, "thickness_m": 0.3, "colour": "#ffffff"},
    "stone_wall": {"height_m": 1.4, "thickness_m": 0.45, "colour": "#ffffff"},
    "concrete": {"height_m": 1.8, "thickness_m": 0.15, "colour": "#cfcac0"},
    "brick_wall": {"height_m": 1.8, "thickness_m": 0.3, "colour": "#ffffff"},  # old name of brick_red
}
# Types whose fence can take the house's wall colour (house_colour).
HOUSE_COLOUR_TYPES = ("brick_red", "brick_white", "concrete", "brick_wall")
# Columns a fence can be given instead of its type's own (fencestyles.COLUMN_STYLES).
COLUMN_TYPES = ("none", "wood_post", "metal_post", "brick_red", "brick_white", "stone", "concrete")

TOP_KEYS = ("version", "parcels", "objects", "groups", "ground_patches", "zones", "zone_cuts", "cables", "ground_types", "paths", "mineral_nodes")
OUTLINE_OVERLAP_M2 = 0.01  # two edited parcel outlines may touch, or overlap by less than this
MINERAL_NODE_KEYS = ("mineral", "position", "z")
PATH_KEYS = ("points", "width_m", "closed")
PATH_WIDTH_M = (0.3, 4.0)  # a footpath is this wide (metres); wider is a road
PARCEL_KEYS = ("fence", "ground_type", "outline", "objects")  # "objects" only so a file of the old shape still validates; see parse()
FENCE_KEYS = ("mode", "type", "column", "height_m", "thickness_m", "colour", "house_colour", "gaps", "sides")
CUSTOM_FENCE_KEYS = ("type", "column", "height_m", "thickness_m", "colour", "house_colour", "gaps", "sides")
SIDE_KEYS = ("at", "mode", "type", "column", "height_m", "thickness_m", "colour", "house_colour")
SIDE_MODES = ("custom", "none")
SIDE_CORNER_DEG = 10.0  # a boundary vertex that turns more than this splits two sides (props.fences, the editor's FenceSides)
GAP_KEYS = ("at", "width_m")
OBJECT_KEYS = ("id", "asset", "position", "z", "rotation_deg", "scale", "size_m", "colour", "solid")
GROUP_KEYS = ("parcels", "fence")
PATCH_KEYS = ("type", "polygon", "water_y", "max_depth")
ZONE_KEYS = ("kind", "polygon")
MAX_DEPTH_LIMIT_M = 20.0  # a water body's deepest point may be set up to this (metres)
CABLE_KEYS = ("from", "to")
GROUND_TYPE_KEYS = ("attributes", "osm", "deleted")
GROUND_OSM_CLASSES = ("landuse", "natural")
GROUND_RECIPES = RECIPES  # the detail.RECIPES a ground type can use (not garden)
GROUND_COLOURS_MAX = COLOURS_MAX
GROUND_NAME_MAX = 24
POLE_ASSET_PREFIX = "prim:pole_"
CONNECTOR_ASSET_PREFIX = "prim:connector_"
WIRE_ASSET_PREFIXES = (POLE_ASSET_PREFIX, CONNECTOR_ASSET_PREFIX)  # the assets a cable can end on

_PARCEL_ID = re.compile(r"P-\d{4,}")
_GROUP_ID = re.compile(r"G-\d{4,}")
_PATCH_ID = re.compile(r"GP-\d{4,}")
_ZONE_ID = re.compile(r"Z-\d{4,}")
_PATH_ID = re.compile(r"PA-\d{4,}")
_MINERAL_NODE_ID = re.compile(r"MN-\d{4,}")
_MINERAL_ID = re.compile(r"[a-z][a-z0-9_]{0,31}")
_ZONE_CUT_ID = re.compile(r"ZC-\d{4,}")
_GROUND_TYPE = re.compile(r"[a-z][a-z0-9_]*")
_GROUND_NAME = re.compile(r"[a-z][a-z0-9_]{0,%d}" % (GROUND_NAME_MAX - 1))
_CABLE_ID = re.compile(r"C-\d{4,}")
_COLOUR = re.compile(r"#[0-9a-fA-F]{6}")
_ASSET = re.compile(r"pack:[^/\s]+/\S+|prim:[A-Za-z0-9_.-]+")


class EditsError(ValueError):
    """The edits document is not valid; ``errors`` lists every problem with its path."""

    def __init__(self, errors: list[str]) -> None:
        self.errors = list(errors)
        super().__init__("\n".join(self.errors))


@dataclass
class Edits:
    """A validated edits document. ``parcels``, ``objects``, ``groups``, ``ground_patches``, ``zones``, ``zone_cuts``, ``cables`` and ``ground_types`` are JSON-native, as written."""

    parcels: dict[str, dict] = field(default_factory=dict)
    objects: list[dict] = field(default_factory=list)
    groups: dict[str, dict] = field(default_factory=dict)
    ground_patches: dict[str, dict] = field(default_factory=dict)
    zones: dict[str, dict] = field(default_factory=dict)
    zone_cuts: dict[str, dict] = field(default_factory=dict)
    paths: dict[str, dict] = field(default_factory=dict)  # worn footpaths (detail/footpaths.py)
    cables: dict[str, dict] = field(default_factory=dict)
    ground_types: dict[str, dict] = field(default_factory=dict)
    mineral_nodes: dict[str, dict] = field(default_factory=dict)  # static mineral nodes (export.docs.build_mineral_nodes)


@dataclass(frozen=True)
class FenceSpec:
    """A parcel's fence after group inheritance and type defaults. Gaps are (x, y, width_m)."""

    mode: str
    type: str
    height_m: float
    thickness_m: float
    colour: str
    gaps: tuple[tuple[float, float, float], ...] = ()
    column: str | None = None  # None = the type's own column
    house_colour: bool = False  # the colour is the house's wall colour (props.make_fences), type permitting
    sides: tuple[tuple[float, float, "FenceSpec"], ...] = ()  # (x, y, the side's own spec); x, y as gaps


# --- validation -----------------------------------------------------------------------------


def _is_num(v: object) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)


def _is_int(v: object) -> bool:
    return isinstance(v, int) and not isinstance(v, bool)


def _unknown_keys(d: dict, allowed: tuple[str, ...], path: str, errors: list[str]) -> None:
    for key in d:
        if key not in allowed:
            errors.append(f"{path}.{key}: unknown key" if path else f"{key}: unknown key")


def _is_dict(v: object, path: str, errors: list[str]) -> bool:
    if not isinstance(v, dict):
        errors.append(f"{path}: must be an object")
        return False
    return True


def _check_positive(v: object, path: str, errors: list[str]) -> None:
    if not (_is_num(v) and v > 0):
        errors.append(f"{path}: must be a number > 0")


def _check_colour(v: object, path: str, errors: list[str]) -> None:
    if not (isinstance(v, str) and _COLOUR.fullmatch(v)):
        errors.append(f'{path}: must be a colour like "#rrggbb"')


def _check_point(v: object, path: str, errors: list[str]) -> None:
    if not (isinstance(v, list) and len(v) == 2 and all(_is_num(c) for c in v)):
        errors.append(f"{path}: must be [x, y] in absolute S-JTSK metres")


def _check_style(v: dict, path: str, errors: list[str]) -> None:
    """The look keys a fence and a side share: type, column, height_m, thickness_m, colour, house_colour."""
    if "type" in v and not (isinstance(v["type"], str) and v["type"] in FENCE_TYPES):
        errors.append(f"{path}.type: must be one of {', '.join(FENCE_TYPES)}")
    if "column" in v and not (isinstance(v["column"], str) and v["column"] in COLUMN_TYPES):
        errors.append(f"{path}.column: must be one of {', '.join(COLUMN_TYPES)}")
    for key in ("height_m", "thickness_m"):
        if key in v:
            _check_positive(v[key], f"{path}.{key}", errors)
    if "colour" in v:
        _check_colour(v["colour"], f"{path}.colour", errors)
    if "house_colour" in v:
        if not isinstance(v["house_colour"], bool):
            errors.append(f"{path}.house_colour: must be true or false")
        elif v["house_colour"] and "type" in v and v["type"] not in HOUSE_COLOUR_TYPES:
            errors.append(f"{path}.house_colour: only with type {', '.join(HOUSE_COLOUR_TYPES[:3])}")


def _check_fence(v: object, path: str, errors: list[str]) -> None:
    if not _is_dict(v, path, errors):
        return
    _unknown_keys(v, FENCE_KEYS, path, errors)
    mode = v.get("mode")
    if "mode" in v and mode not in FENCE_MODES:
        errors.append(f"{path}.mode: must be one of {', '.join(FENCE_MODES)}")
    if mode in ("auto", "none"):
        for key in CUSTOM_FENCE_KEYS:
            if key in v:
                errors.append(f"{path}.{key}: not used with mode {mode!r}")
    _check_style(v, path, errors)
    if "sides" in v:
        sides = v["sides"]
        if not isinstance(sides, list):
            errors.append(f"{path}.sides: must be a list")
        else:
            for i, side in enumerate(sides):
                spath = f"{path}.sides.{i}"
                if not _is_dict(side, spath, errors):
                    continue
                _unknown_keys(side, SIDE_KEYS, spath, errors)
                if "at" not in side:
                    errors.append(f"{spath}.at: required")
                else:
                    _check_point(side["at"], f"{spath}.at", errors)
                if side.get("mode", "custom") not in SIDE_MODES:
                    errors.append(f"{spath}.mode: must be one of {', '.join(SIDE_MODES)}")
                if side.get("mode") == "none":
                    for key in SIDE_KEYS[2:]:
                        if key in side:
                            errors.append(f"{spath}.{key}: not used with mode 'none'")
                _check_style(side, spath, errors)
    if "gaps" in v:
        gaps = v["gaps"]
        if not isinstance(gaps, list):
            errors.append(f"{path}.gaps: must be a list")
            return
        for i, gap in enumerate(gaps):
            gpath = f"{path}.gaps.{i}"
            if not _is_dict(gap, gpath, errors):
                continue
            _unknown_keys(gap, GAP_KEYS, gpath, errors)
            if "at" not in gap:
                errors.append(f"{gpath}.at: required")
            else:
                _check_point(gap["at"], f"{gpath}.at", errors)
            if "width_m" not in gap:
                errors.append(f"{gpath}.width_m: required")
            else:
                _check_positive(gap["width_m"], f"{gpath}.width_m", errors)


def _check_objects(v: object, path: str, errors: list[str], seen: dict[str, str]) -> None:
    if not isinstance(v, list):
        errors.append(f"{path}: must be a list")
        return
    for i, obj in enumerate(v):
        opath = f"{path}.{i}"
        if not _is_dict(obj, opath, errors):
            continue
        _unknown_keys(obj, OBJECT_KEYS, opath, errors)
        for key in ("id", "asset", "position"):
            if key not in obj:
                errors.append(f"{opath}.{key}: required")
        if "id" in obj:
            oid = obj["id"]
            if not (isinstance(oid, str) and oid):
                errors.append(f"{opath}.id: must be a non-empty string")
            elif oid in seen:
                errors.append(f"{opath}.id: duplicate object id {oid!r} (first at {seen[oid]})")
            else:
                seen[oid] = opath
        if "asset" in obj and not (
            isinstance(obj["asset"], str) and _ASSET.fullmatch(obj["asset"])
        ):
            errors.append(f'{opath}.asset: must be "pack:<pack>/<model>" or "prim:<name>"')
        if "position" in obj:
            _check_point(obj["position"], f"{opath}.position", errors)
        if "z" in obj and not _is_num(obj["z"]):
            errors.append(f"{opath}.z: must be a number")
        if "rotation_deg" in obj and not _is_num(obj["rotation_deg"]):
            errors.append(f"{opath}.rotation_deg: must be a number")
        if "scale" in obj:
            _check_positive(obj["scale"], f"{opath}.scale", errors)
        if "size_m" in obj:
            sz = obj["size_m"]
            if not (isinstance(sz, list) and len(sz) == 3 and all(_is_num(c) and c > 0 for c in sz)):
                errors.append(f"{opath}.size_m: must be [length, width, height], three numbers > 0 (metres)")
        if "colour" in obj:
            _check_colour(obj["colour"], f"{opath}.colour", errors)
        if "solid" in obj and not isinstance(obj["solid"], bool):
            errors.append(f"{opath}.solid: must be true or false")


def _check_parcels(parcels: dict, errors: list[str], seen: dict[str, str], own_types: dict | None = None) -> None:
    outlines: dict[str, Polygon] = {}
    for pid, entry in parcels.items():
        path = f"parcels.{pid}"
        if not _PARCEL_ID.fullmatch(pid):
            errors.append(f"{path}: parcel IDs look like P-0412")
        if not _is_dict(entry, path, errors):
            continue
        if "trees" in entry:  # removed: trees are planted by the rules or read from the orthophoto, not set per parcel
            errors.append(f"{path}.trees: per-parcel tree settings were removed; delete this entry")
        _unknown_keys({k: v for k, v in entry.items() if k != "trees"}, PARCEL_KEYS, path, errors)
        if "fence" in entry:
            _check_fence(entry["fence"], f"{path}.fence", errors)
        if "ground_type" in entry:
            _check_parcel_ground_type(entry["ground_type"], f"{path}.ground_type", own_types or {}, errors)
        if "outline" in entry:
            n = len(errors)
            _check_polygon(entry, path, errors, "outline")
            if len(errors) == n:
                outlines[pid] = Polygon(polygon_ring(entry["outline"]))
        if "objects" in entry:
            _check_objects(entry["objects"], f"{path}.objects", errors, seen)
    ids = sorted(outlines)
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            if outlines[a].intersection(outlines[b]).area > OUTLINE_OVERLAP_M2:
                errors.append(f"parcels.{b}.outline: overlaps the edited outline of {a}; two edited outlines must not overlap")


def _check_groups(groups: dict, errors: list[str]) -> None:
    owner: dict[str, str] = {}  # parcel ID -> group ID of its first listing
    for gid, entry in groups.items():
        path = f"groups.{gid}"
        if not _GROUP_ID.fullmatch(gid):
            errors.append(f"{path}: group IDs look like G-0001")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, GROUP_KEYS, path, errors)
        members = entry.get("parcels")
        if members is None:
            errors.append(f"{path}.parcels: required")
        elif not isinstance(members, list) or not members:
            errors.append(f"{path}.parcels: must be a non-empty list of parcel IDs")
        else:
            for i, pid in enumerate(members):
                mpath = f"{path}.parcels.{i}"
                if not (isinstance(pid, str) and _PARCEL_ID.fullmatch(pid)):
                    errors.append(f"{mpath}: must be a parcel ID like P-0412")
                elif pid in owner and owner[pid] == gid:
                    errors.append(f"{mpath}: parcel {pid} listed twice")
                elif pid in owner:
                    errors.append(f"{mpath}: parcel {pid} is already in group {owner[pid]}")
                else:
                    owner[pid] = gid
        if "fence" in entry:
            _check_fence(entry["fence"], f"{path}.fence", errors)


def polygon_ring(polygon: list) -> list[tuple[float, float]]:
    """The points of a patch or zone polygon as tuples, without a repeated closing point."""
    pts = [(float(p[0]), float(p[1])) for p in polygon]
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts.pop()
    return pts


def _check_polygon(entry: dict, path: str, errors: list[str], key: str = "polygon") -> None:
    """The ``polygon`` of a patch or a zone (a parcel's ``outline``): one simple ring of at least 3 points."""
    if key not in entry:
        errors.append(f"{path}.{key}: required")
        return
    poly = entry[key]
    ppath = f"{path}.{key}"
    if not isinstance(poly, list):
        errors.append(f"{ppath}: must be a list of [x, y] points")
        return
    n = len(errors)
    for i, pt in enumerate(poly):
        _check_point(pt, f"{ppath}.{i}", errors)
    if len(errors) > n:
        return
    ring = polygon_ring(poly)
    if len(ring) < 3:
        errors.append(f"{ppath}: needs at least 3 points")
    elif any(a == b for a, b in zip(ring, ring[1:] + ring[:1], strict=True)):
        errors.append(f"{ppath}: has repeated consecutive points")
    elif not LinearRing(ring).is_simple or abs(Polygon(ring).area) <= 0:
        errors.append(f"{ppath}: must not intersect or touch itself, and must enclose an area")


def _effective_type_names(own: dict, shipped: dict) -> set[str]:
    """The ground type names once the ``ground_types`` of the edits are laid over rules.yaml's (deleted ones are gone)."""
    names = set(shipped)
    for name, entry in own.items():
        if isinstance(entry, dict) and entry.get("deleted"):
            names.discard(name)
        else:
            names.add(name)
    return names


def _check_parcel_ground_type(name: object, path: str, own_types: dict, errors: list[str]) -> None:
    """A parcel's ``ground_type``: a type that exists (rules.yaml or ``ground_types``, not deleted) and is not water."""
    from drycastle_pipeline.core.rules import load_section

    shipped = load_section("ground")["types"]
    if not isinstance(name, str) or name not in _effective_type_names(own_types, shipped):
        errors.append(f"{path}: {name!r} is not a ground type (or it is deleted)")
        return
    own = own_types.get(name)
    if _effective_attributes(name, own if isinstance(own, dict) else {}, shipped).get("water"):
        errors.append(f"{path}: {name} is a water type, a parcel cannot be water")


def _check_patches(patches: dict, own_types: dict, errors: list[str]) -> None:
    from drycastle_pipeline.core.rules import load_section

    shipped = load_section("ground")["types"]
    known = _effective_type_names(own_types, shipped)
    for pid, entry in patches.items():
        path = f"ground_patches.{pid}"
        if not _PATCH_ID.fullmatch(pid):
            errors.append(f"{path}: ground patch IDs look like GP-0001")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, PATCH_KEYS, path, errors)
        name = entry.get("type")
        if "type" not in entry:
            errors.append(f"{path}.type: required")
        elif not isinstance(name, str) or name not in known:
            errors.append(f"{path}.type: must be a ground type ({', '.join(sorted(known))})")
        else:
            own = own_types.get(name)
            carries_water = bool(_effective_attributes(name, own if isinstance(own, dict) else {}, shipped).get("water"))
            if carries_water and "water_y" not in entry:
                errors.append(f"{path}.water_y: required, {name} is a water type")
            elif not carries_water and "water_y" in entry:
                errors.append(f"{path}.water_y: only for a water type, {name} does not carry the water attribute")
        if "water_y" in entry and not (_is_num(entry["water_y"]) and math.isfinite(entry["water_y"])):
            errors.append(f"{path}.water_y: must be a number (metres, terrain datum)")
        if "max_depth" in entry:
            md = entry["max_depth"]
            if "water_y" not in entry:
                errors.append(f"{path}.max_depth: only a body of water (with water_y) has a depth")
            elif not (_is_num(md) and math.isfinite(md) and 0 < md <= MAX_DEPTH_LIMIT_M):
                errors.append(f"{path}.max_depth: must be a number of metres in (0, {MAX_DEPTH_LIMIT_M:g}]")
        _check_polygon(entry, path, errors)


def _check_zones(zones: dict, errors: list[str], key: str = "zones") -> None:
    cuts = key == "zone_cuts"
    for zid, entry in zones.items():
        path = f"{key}.{zid}"
        if not (_ZONE_CUT_ID if cuts else _ZONE_ID).fullmatch(zid):
            errors.append(f"{path}: {'zone cut' if cuts else 'zone'} IDs look like {'ZC-0001' if cuts else 'Z-0001'}")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, ZONE_KEYS, path, errors)
        if "kind" not in entry:
            errors.append(f"{path}.kind: required")
        elif entry["kind"] not in KIND_NAMES:
            errors.append(f"{path}.kind: must be a zone kind ({', '.join(KIND_NAMES)})")
        _check_polygon(entry, path, errors)


def _check_paths(paths: dict, errors: list[str]) -> None:
    for pid, entry in paths.items():
        path = f"paths.{pid}"
        if not _PATH_ID.fullmatch(pid):
            errors.append(f"{path}: path IDs look like PA-0001")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, PATH_KEYS, path, errors)
        pts = entry.get("points")
        if not (isinstance(pts, list) and len(pts) >= 2 and all(
                isinstance(p, list) and len(p) == 2 and all(_is_num(v) and math.isfinite(v) for v in p) for p in pts)):
            errors.append(f"{path}.points: must be a list of at least 2 [x, y] points")
        elif all(p == pts[0] for p in pts):
            errors.append(f"{path}.points: the points must not all be the same")
        if "width_m" in entry and not (_is_num(entry["width_m"]) and PATH_WIDTH_M[0] <= entry["width_m"] <= PATH_WIDTH_M[1]):
            errors.append(f"{path}.width_m: must be a number of metres from {PATH_WIDTH_M[0]:g} to {PATH_WIDTH_M[1]:g}")
        if "closed" in entry and not isinstance(entry["closed"], bool):
            errors.append(f"{path}.closed: must be true or false")
        elif entry.get("closed") and isinstance(pts, list) and len(pts) < 3:
            errors.append(f"{path}.closed: a closed path needs at least 3 points")


def _check_mineral_nodes(nodes: dict, errors: list[str]) -> None:
    for nid, entry in nodes.items():
        path = f"mineral_nodes.{nid}"
        if not _MINERAL_NODE_ID.fullmatch(nid):
            errors.append(f"{path}: mineral node IDs look like MN-0001")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, MINERAL_NODE_KEYS, path, errors)
        for key in ("mineral", "position"):
            if key not in entry:
                errors.append(f"{path}.{key}: required")
        if "mineral" in entry and not (isinstance(entry["mineral"], str) and _MINERAL_ID.fullmatch(entry["mineral"])):
            errors.append(f"{path}.mineral: must be a mineral id (lower_snake)")
        if "position" in entry:
            _check_point(entry["position"], f"{path}.position", errors)
        if "z" in entry and not _is_num(entry["z"]):
            errors.append(f"{path}.z: must be a number")


def cable_pair(entry: dict) -> tuple[str, str]:
    """The two pole ids of a cable, in a fixed order (a cable has no direction)."""
    a, b = str(entry["from"]), str(entry["to"])
    return (a, b) if a <= b else (b, a)


def _check_cables(cables: dict, errors: list[str]) -> None:
    seen: dict[tuple[str, str], str] = {}
    for cid, entry in cables.items():
        path = f"cables.{cid}"
        if not _CABLE_ID.fullmatch(cid):
            errors.append(f"{path}: cable IDs look like C-0001")
        if not _is_dict(entry, path, errors):
            continue
        _unknown_keys(entry, CABLE_KEYS, path, errors)
        ok = True
        for key in CABLE_KEYS:
            if key not in entry:
                errors.append(f"{path}.{key}: required")
                ok = False
            elif not (isinstance(entry[key], str) and entry[key]):
                errors.append(f"{path}.{key}: must be the id of a placed object")
                ok = False
        if not ok:
            continue
        if entry["from"] == entry["to"]:
            errors.append(f"{path}: from and to must be two different poles")
            continue
        pair = cable_pair(entry)
        if pair in seen:
            errors.append(f"{path}: {pair[0]} and {pair[1]} are already joined by {seen[pair]}")
        else:
            seen[pair] = cid


def protected_ground_types() -> dict[str, str]:
    """The types of the shipped rules.yaml that cannot be deleted, with the reason: the default type, and the types
    made by a rule (the dike, which ``detail.dike`` needs)."""
    from drycastle_pipeline.core.rules import load_section

    gcfg = load_section("ground")
    out = {name: "has a rule (detail.dike needs it)" for name, spec in gcfg["types"].items() if (spec or {}).get("rule")}
    out[gcfg["default"]] = "is the default type (what no other type claims)"
    return out


def _effective_attributes(name: str, entry: dict, shipped: dict) -> dict:
    """The attributes a type has once its entry is laid over rules.yaml's (a ``null`` removes one)."""
    out = dict((shipped.get(name) or {}).get("attributes") or {})
    own = entry.get("attributes")
    for key, value in own.items() if isinstance(own, dict) else []:
        if value is None:
            out.pop(key, None)
        else:
            out[key] = value
    return out


def _check_attributes(name: str, attrs: object, path: str, errors: list[str], shipped: dict) -> None:
    from drycastle_pipeline.core.rules import load_section

    if not _is_dict(attrs, path, errors):
        return
    known = tuple(ATTRIBUTES)
    for key in attrs:
        if key not in known:
            errors.append(f"{path}.{key}: unknown attribute (the catalog has {', '.join(known)})")
    for key, value in attrs.items():
        kpath = f"{path}.{key}"
        if key not in known:
            continue
        if value is None:
            if name not in shipped or key not in ((shipped[name] or {}).get("attributes") or {}):
                errors.append(f"{kpath}: null removes an attribute of the type in rules.yaml, and {name} has no {key} there")
            continue
        if key == "colours":
            if not (isinstance(value, list) and 1 <= len(value) <= GROUND_COLOURS_MAX):
                errors.append(f"{kpath}: must be a list of 1 to {GROUND_COLOURS_MAX} colours")
            else:
                for i, c in enumerate(value):
                    _check_colour(c, f"{kpath}.{i}", errors)
        elif key == "recipe":
            if value not in GROUND_RECIPES:
                errors.append(f"{kpath}: must be one of {', '.join(GROUND_RECIPES)}")
        elif key in FLAG_KEYS:
            if value is not True:
                errors.append(f"{kpath}: must be true (null removes it)")
    if "detail" in attrs and attrs["detail"] is not None:
        detail = attrs["detail"]
        if _is_dict(detail, f"{path}.detail", errors):
            recipe = attrs["recipe"] if "recipe" in attrs else (((shipped.get(name) or {}).get("attributes") or {}).get("recipe"))
            section = load_section("detail").get(recipe) if recipe in GROUND_RECIPES else None
            if detail and not isinstance(section, dict):
                errors.append(f"{path}.detail: needs a recipe (one of {', '.join(GROUND_RECIPES)}) to lay the settings over")
            else:
                for key in detail:
                    if section is not None and key not in section:
                        errors.append(f"{path}.detail.{key}: not a setting of detail.{recipe} (known: {', '.join(section)})")


def _check_ground_type(name: str, entry: object, path: str, errors: list[str]) -> None:
    from drycastle_pipeline.core.rules import load_section

    if not (_GROUND_NAME.fullmatch(name)):
        errors.append(f"{path}: type names are lower case letters, digits and _, starting with a letter, at most {GROUND_NAME_MAX} long")
    if not _is_dict(entry, path, errors):
        return
    if "rule" in entry:
        errors.append(f"{path}.rule: rule types stay in rules.yaml")
    _unknown_keys({k: v for k, v in entry.items() if k != "rule"}, GROUND_TYPE_KEYS, path, errors)
    shipped = load_section("ground")["types"]
    if "deleted" in entry:
        if entry["deleted"] is not True:
            errors.append(f"{path}.deleted: must be true (remove the entry to bring the type back)")
        if len([k for k in entry if k != "rule"]) > 1:
            errors.append(f"{path}: a deleted type has no other keys")
        if name not in shipped:
            errors.append(f"{path}.deleted: {name!r} is not a type of rules.yaml; remove this entry instead")
        elif name in (why := protected_ground_types()):
            errors.append(f"{path}.deleted: {name!r} cannot be deleted, it {why[name]}")
        return
    n = len(errors)
    if "attributes" in entry:
        _check_attributes(name, entry["attributes"], f"{path}.attributes", errors, shipped)
    osm = entry.get("osm")
    if "osm" in entry and _is_dict(osm, f"{path}.osm", errors):
        _unknown_keys(osm, GROUND_OSM_CLASSES, f"{path}.osm", errors)
        for cls, values in osm.items():
            if cls in GROUND_OSM_CLASSES and not (
                isinstance(values, list) and all(isinstance(v, str) and v.strip() for v in values)
            ):
                errors.append(f"{path}.osm.{cls}: must be a list of OSM values (text)")
    own_source = isinstance(osm, dict) and any(osm.values())
    takes_land = own_source or (shipped.get(name) or {}).get("osm") or (shipped.get(name) or {}).get("rule")
    if len(errors) == n and takes_land and not any(k in _effective_attributes(name, entry, shipped) for k in TERRAIN_KEYS):
        errors.append(f"{path}.attributes: a type that takes OSM land needs appearance or water attributes ({', '.join(TERRAIN_KEYS)})")


def _check_ground_types(types: dict, errors: list[str]) -> None:
    for name, entry in types.items():
        _check_ground_type(name, entry, f"ground_types.{name}", errors)


def validate(doc: object) -> list[str]:
    """Every problem in an edits document as ``path: message``; an empty list means valid."""
    if not isinstance(doc, dict):
        return ["<root>: must be an object"]
    errors: list[str] = []
    _unknown_keys(doc, TOP_KEYS, "", errors)
    version = doc.get("version")
    if not (_is_int(version) and version == EDITS_VERSION):
        errors.append(f"version: must be {EDITS_VERSION}")
    seen: dict[str, str] = {}  # object id -> where it first appears (ids are unique across the whole file)
    if "objects" in doc:
        _check_objects(doc["objects"], "objects", errors, seen)
    parcels = doc.get("parcels", {})
    gtypes = doc.get("ground_types", {})
    if _is_dict(parcels, "parcels", errors):
        _check_parcels(parcels, errors, seen, gtypes if isinstance(gtypes, dict) else {})
    groups = doc.get("groups", {})
    if _is_dict(groups, "groups", errors):
        _check_groups(groups, errors)
    patches = doc.get("ground_patches", {})
    if _is_dict(patches, "ground_patches", errors):
        _check_patches(patches, gtypes if isinstance(gtypes, dict) else {}, errors)
    zones = doc.get("zones", {})
    if _is_dict(zones, "zones", errors):
        _check_zones(zones, errors)
    zone_cuts = doc.get("zone_cuts", {})
    if _is_dict(zone_cuts, "zone_cuts", errors):
        _check_zones(zone_cuts, errors, "zone_cuts")
    cables = doc.get("cables", {})
    if _is_dict(cables, "cables", errors):
        _check_cables(cables, errors)
    if _is_dict(gtypes, "ground_types", errors):
        _check_ground_types(gtypes, errors)
    paths = doc.get("paths", {})
    if _is_dict(paths, "paths", errors):
        _check_paths(paths, errors)
    mineral_nodes = doc.get("mineral_nodes", {})
    if _is_dict(mineral_nodes, "mineral_nodes", errors):
        _check_mineral_nodes(mineral_nodes, errors)
    return errors


def parse(doc: object) -> Edits:
    """Validate a parsed document and return it as :class:`Edits`; raises :class:`EditsError`."""
    errors = validate(doc)
    if errors:
        raise EditsError(errors)
    assert isinstance(doc, dict)
    parcels = copy.deepcopy(doc.get("parcels", {}))
    objects = copy.deepcopy(doc.get("objects", []))
    legacy: list[dict] = []  # the old shape: objects listed under their parcel
    for pid in list(parcels):
        listed = parcels[pid].pop("objects", None)
        if listed is not None:
            legacy.extend(listed)
            if not parcels[pid]:
                del parcels[pid]
    objects += sorted(legacy, key=lambda o: o["id"])
    return Edits(
        parcels=parcels,
        objects=objects,
        groups=copy.deepcopy(doc.get("groups", {})),
        ground_patches=copy.deepcopy(doc.get("ground_patches", {})),
        zones=copy.deepcopy(doc.get("zones", {})),
        zone_cuts=copy.deepcopy(doc.get("zone_cuts", {})),
        paths=copy.deepcopy(doc.get("paths", {})),
        cables=copy.deepcopy(doc.get("cables", {})),
        ground_types=copy.deepcopy(doc.get("ground_types", {})),
        mineral_nodes=copy.deepcopy(doc.get("mineral_nodes", {})),
    )


# --- reading and writing --------------------------------------------------------------------


def load_edits(path: Path = DEFAULT_EDITS) -> Edits:
    """Read and validate the edits file. A missing file means no edits."""
    if not path.is_file():
        return Edits()
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise EditsError([f"not valid JSON: {exc}"]) from exc
    return parse(doc)


def dump_text(edits: Edits) -> str:
    """The canonical text of an edits document (sorted keys, indent 1, trailing newline)."""
    doc: dict = {"version": EDITS_VERSION, "parcels": edits.parcels, "groups": edits.groups}
    if edits.objects:  # omitted when empty, so files without objects keep their bytes; sorted by id, so no edit reorders them
        doc["objects"] = sorted(edits.objects, key=lambda o: o["id"])
    if edits.ground_patches:  # omitted when empty, so files without patches keep their bytes
        doc["ground_patches"] = edits.ground_patches
    if edits.zones:  # likewise
        doc["zones"] = edits.zones
    if edits.zone_cuts:  # likewise
        doc["zone_cuts"] = edits.zone_cuts
    if edits.paths:  # likewise
        doc["paths"] = edits.paths
    if edits.cables:  # likewise
        doc["cables"] = edits.cables
    if edits.ground_types:  # likewise
        doc["ground_types"] = edits.ground_types
    if edits.mineral_nodes:  # likewise
        doc["mineral_nodes"] = edits.mineral_nodes
    errors = validate(doc)
    if errors:
        raise EditsError(errors)
    return json.dumps(doc, sort_keys=True, indent=1, ensure_ascii=False) + "\n"


def dump_edits(edits: Edits, path: Path = DEFAULT_EDITS) -> None:
    """Write the edits file atomically (temporary file in the same folder, then replace)."""
    text = dump_text(edits)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        os.chmod(tmp, 0o644)
        os.replace(tmp, path)
    except BaseException:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(tmp)
        raise


# --- resolution -----------------------------------------------------------------------------


def resolve_fence(parcel_fence: dict | None, group_fence: dict | None = None) -> FenceSpec:
    """Effective fence of one parcel: the group fence, overridden field by field by its own.

    Gaps are returned as given (absolute metres); the props stage converts them.
    """
    merged = {**(group_fence or {}), **(parcel_fence or {})}
    base = {k: v for k, v in merged.items() if k not in ("gaps", "sides")}
    mode = merged.get("mode")
    if mode is None:
        mode = "custom" if any(k in merged for k in CUSTOM_FENCE_KEYS) else "auto"
    fence_type = merged.get("type", DEFAULT_FENCE_TYPE)
    table = FENCE_TYPES[fence_type]
    return FenceSpec(
        mode=mode,
        type=fence_type,
        height_m=float(merged.get("height_m", table["height_m"])),
        thickness_m=float(merged.get("thickness_m", table["thickness_m"])),
        colour=str(merged.get("colour", table["colour"])),
        gaps=tuple(
            (float(g["at"][0]), float(g["at"][1]), float(g["width_m"]))
            for g in merged.get("gaps", [])
        ),
        column=merged.get("column"),
        house_colour=merged.get("house_colour") is True and fence_type in HOUSE_COLOUR_TYPES,
        sides=tuple(
            (float(s["at"][0]), float(s["at"][1]),
             resolve_fence({**{k: v for k, v in s.items() if k != "at"}, "mode": s.get("mode", "custom")}, base))
            for s in merged.get("sides", [])
        ),
    )
