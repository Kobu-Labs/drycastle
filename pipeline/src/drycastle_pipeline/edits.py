"""Hand edits: ``pipeline/config/edits.json`` (editor-owned) -- schema, validator and writer.

The file is written by the editor (and by hand) and read by the props stage. It holds only the
developer's deliberate choices, so it survives every pipeline re-run. Keys are sorted and the
formatting is fixed, so git diffs stay small. Version 1::

    {"version": 1,
     "parcels": {"P-0412": {"fence": {...}, "objects": [...]}},
     "groups":  {"G-0001": {"parcels": ["P-0412", "P-0413"], "fence": {...}}},
     "regions": {"R-0001": {"colour": "#8a7a5c", "polygon": [[x, y], ...]}},
     "cables":  {"C-0001": {"from": "o-0012", "to": "o-0015"}}}

``fence`` (all keys optional)
    mode              auto | none | custom. Default auto, or custom when type, height_m,
                      thickness_m, colour or gaps is given without a mode.
                      auto = the rules in rules.yaml (props.fences); none = no fence;
                      custom = always fenced on the boundary, no seeded probability, no kind
                      filter (building and road clearances still apply).
    type              picket | wire | brick_red | brick_white | stone_wall | concrete (custom
                      only; brick_wall is the old name of brick_red). Each maps to the defaults in
                      FENCE_TYPES below and to a style in fencestyles.py (panel texture, column,
                      cap).
    column            one of COLUMN_TYPES below (custom only): the posts or piers at the section
                      joints and run ends, replacing the type's own (e.g. picket between brick_red
                      piers); "none" leaves them out.
    height_m, thickness_m  numbers > 0 (custom only), overriding the type.
    colour            "#rrggbb" (custom only), overriding the type. It is a tint: picket, wire and
                      concrete are near-white textures, so the colour is their paint; brick and
                      stone carry their own colours (white = as drawn).
    gaps              list of {"at": [x, y], "width_m": n} (custom only). A gap is cut out of
                      the boundary, centred on the nearest boundary point to ``at``. ``at`` is
                      absolute EPSG:5514 (S-JTSK) metres, the same frame as objects.

``objects`` (list; placed objects, emitted to objects.json, not instanced in props.json)
    id                  str, unique within the whole file.
    asset               "pack:<pack>/<model>" or "prim:<name>".
    position            [x, y] absolute EPSG:5514 metres.
    z                   optional metres; omitted = terrain height at runtime.
    rotation_deg        number, default 0.
    scale               number > 0, default 1.
    colour              optional "#rrggbb" override.
    (objects.json entries also carry ``parcel`` (the parcel containing the object, or null),
    ``owner`` (the parcel entry that lists it) and ``chunk``; both are derived.)

``groups`` (a property group: parcels that share one fence around their combined outer edge)
    parcels             non-empty list of unique parcel IDs. A parcel is in at most one group.
    fence               same shape as a parcel fence. A member's own fence overrides the group
                        fence field by field. Each piece of the group's outer edge takes the
                        settings of the member that owns it. Edges shared by two members get no
                        fence.

``regions`` (a hand-drawn shape painted in one colour over the ground; only the colour changes)
    colour              "#rrggbb".
    polygon             one ring: a list of at least 3 [x, y] points in absolute S-JTSK metres, x
                        east, y north. Not closed by repeating the first point (a repeated last
                        point is tolerated), no repeated or collinear-only points, no
                        self-intersection (edges may not cross or touch each other). Regions are
                        independent of parcel boundaries. Where regions overlap, the higher ID
                        wins. The ``regions`` key is only written when there are regions.
    water_y             optional number: the region is a body of water whose flat surface is at this
                        height, in metres on the terrain's datum (as terrain.json, e.g. 185.6). The
                        colour then tints the surface in the editor view; the body is also written
                        to water.json (swimming, drowning, the shooter's water surface) with its bed
                        at the lowest terrain inside the polygon. Without it the region is ground paint.
    ground              optional ground type (a name of ``ground.types`` in rules.yaml, e.g. "meadow"): the
                        region is a hand-drawn ground area. The parcels stage makes its polygon (outside the
                        parcels) ground of that type, before the rule types and with the higher ID winning, so
                        it gets the type's terrain colour, detail and trees on the next full run; it is then
                        not painted as a region (the colour only tints the editor's preview until that run).
                        A type the rules do not know is skipped with a warning. Not together with ``water_y``.

``cables`` (the wires strung between two placed utility poles, or from a pole to a house connector;
drawn by the game, see docs/utility-poles.md)
    from, to            ids of two different placed objects: utility poles (``prim:pole_*``) or
                        house connectors (``prim:connector_*``, placed on a wall or roof with a
                        ``z``). The direction has no meaning. A pair is joined by at most one cable.
                        A cable whose end no longer exists, or is neither, is skipped with a
                        warning by the props stage. The ``cables`` key is only written when there
                        are cables.

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

from drycastle_pipeline.config import PIPELINE_DIR

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
# Columns a fence can be given instead of its type's own (fencestyles.COLUMN_STYLES).
COLUMN_TYPES = ("none", "wood_post", "metal_post", "brick_red", "brick_white", "stone", "concrete")

TOP_KEYS = ("version", "parcels", "groups", "regions", "cables")
PARCEL_KEYS = ("fence", "objects")
FENCE_KEYS = ("mode", "type", "column", "height_m", "thickness_m", "colour", "gaps")
CUSTOM_FENCE_KEYS = ("type", "column", "height_m", "thickness_m", "colour", "gaps")
GAP_KEYS = ("at", "width_m")
OBJECT_KEYS = ("id", "asset", "position", "z", "rotation_deg", "scale", "colour")
GROUP_KEYS = ("parcels", "fence")
REGION_KEYS = ("colour", "polygon", "water_y", "ground")
CABLE_KEYS = ("from", "to")
POLE_ASSET_PREFIX = "prim:pole_"
CONNECTOR_ASSET_PREFIX = "prim:connector_"
WIRE_ASSET_PREFIXES = (POLE_ASSET_PREFIX, CONNECTOR_ASSET_PREFIX)  # the assets a cable can end on

_PARCEL_ID = re.compile(r"P-\d{4,}")
_GROUP_ID = re.compile(r"G-\d{4,}")
_REGION_ID = re.compile(r"R-\d{4,}")
_GROUND_TYPE = re.compile(r"[a-z][a-z0-9_]*")
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
    """A validated edits document. ``parcels``, ``groups``, ``regions`` and ``cables`` are JSON-native, as written."""

    parcels: dict[str, dict] = field(default_factory=dict)
    groups: dict[str, dict] = field(default_factory=dict)
    regions: dict[str, dict] = field(default_factory=dict)
    cables: dict[str, dict] = field(default_factory=dict)


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
    if "type" in v and not (isinstance(v["type"], str) and v["type"] in FENCE_TYPES):
        errors.append(f"{path}.type: must be one of {', '.join(FENCE_TYPES)}")
    if "column" in v and not (isinstance(v["column"], str) and v["column"] in COLUMN_TYPES):
        errors.append(f"{path}.column: must be one of {', '.join(COLUMN_TYPES)}")
    for key in ("height_m", "thickness_m"):
        if key in v:
            _check_positive(v[key], f"{path}.{key}", errors)
    if "colour" in v:
        _check_colour(v["colour"], f"{path}.colour", errors)
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
        if "colour" in obj:
            _check_colour(obj["colour"], f"{opath}.colour", errors)


def _check_parcels(parcels: dict, errors: list[str]) -> None:
    seen: dict[str, str] = {}
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
        if "objects" in entry:
            _check_objects(entry["objects"], f"{path}.objects", errors, seen)


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


def region_ring(polygon: list) -> list[tuple[float, float]]:
    """The points of a region polygon as tuples, without a repeated closing point."""
    pts = [(float(p[0]), float(p[1])) for p in polygon]
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts.pop()
    return pts


def _check_region(entry: object, path: str, errors: list[str]) -> None:
    if not _is_dict(entry, path, errors):
        return
    _unknown_keys(entry, REGION_KEYS, path, errors)
    if "colour" not in entry:
        errors.append(f"{path}.colour: required")
    else:
        _check_colour(entry["colour"], f"{path}.colour", errors)
    if "water_y" in entry and not (_is_num(entry["water_y"]) and math.isfinite(entry["water_y"])):
        errors.append(f"{path}.water_y: must be a number (metres, terrain datum)")
    if "ground" in entry:
        if not (isinstance(entry["ground"], str) and _GROUND_TYPE.fullmatch(entry["ground"])):
            errors.append(f"{path}.ground: must be a ground type name (lower case letters, digits, _)")
        if "water_y" in entry:
            errors.append(f"{path}.ground: not together with water_y (a water region is its own body of water)")
    if "polygon" not in entry:
        errors.append(f"{path}.polygon: required")
        return
    poly = entry["polygon"]
    ppath = f"{path}.polygon"
    if not isinstance(poly, list):
        errors.append(f"{ppath}: must be a list of [x, y] points")
        return
    n = len(errors)
    for i, pt in enumerate(poly):
        _check_point(pt, f"{ppath}.{i}", errors)
    if len(errors) > n:
        return
    ring = region_ring(poly)
    if len(ring) < 3:
        errors.append(f"{ppath}: needs at least 3 points")
    elif any(a == b for a, b in zip(ring, ring[1:] + ring[:1], strict=True)):
        errors.append(f"{ppath}: has repeated consecutive points")
    elif not LinearRing(ring).is_simple or abs(Polygon(ring).area) <= 0:
        errors.append(f"{ppath}: must not intersect or touch itself, and must enclose an area")


def _check_regions(regions: dict, errors: list[str]) -> None:
    for rid, entry in regions.items():
        path = f"regions.{rid}"
        if not _REGION_ID.fullmatch(rid):
            errors.append(f"{path}: region IDs look like R-0001")
        _check_region(entry, path, errors)


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


def validate(doc: object) -> list[str]:
    """Every problem in an edits document as ``path: message``; an empty list means valid."""
    if not isinstance(doc, dict):
        return ["<root>: must be an object"]
    errors: list[str] = []
    _unknown_keys(doc, TOP_KEYS, "", errors)
    version = doc.get("version")
    if not (_is_int(version) and version == EDITS_VERSION):
        errors.append(f"version: must be {EDITS_VERSION}")
    parcels = doc.get("parcels", {})
    if _is_dict(parcels, "parcels", errors):
        _check_parcels(parcels, errors)
    groups = doc.get("groups", {})
    if _is_dict(groups, "groups", errors):
        _check_groups(groups, errors)
    regions = doc.get("regions", {})
    if _is_dict(regions, "regions", errors):
        _check_regions(regions, errors)
    cables = doc.get("cables", {})
    if _is_dict(cables, "cables", errors):
        _check_cables(cables, errors)
    return errors


def parse(doc: object) -> Edits:
    """Validate a parsed document and return it as :class:`Edits`; raises :class:`EditsError`."""
    errors = validate(doc)
    if errors:
        raise EditsError(errors)
    assert isinstance(doc, dict)
    return Edits(
        parcels=copy.deepcopy(doc.get("parcels", {})),
        groups=copy.deepcopy(doc.get("groups", {})),
        regions=copy.deepcopy(doc.get("regions", {})),
        cables=copy.deepcopy(doc.get("cables", {})),
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
    if edits.regions:  # omitted when empty, so files without regions keep their bytes
        doc["regions"] = edits.regions
    if edits.cables:  # likewise
        doc["cables"] = edits.cables
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
    )
