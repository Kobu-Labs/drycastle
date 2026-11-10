"""Hand edits: ``pipeline/config/edits.json`` (editor-owned) -- schema, validator and writer.

The file is written by the editor (and by hand) and read by the props stage. It holds only the
developer's deliberate choices, so it survives every pipeline re-run. Keys are sorted and the
formatting is fixed, so git diffs stay small. Version 1::

    {"version": 1,
     "parcels": {"P-0412": {"fence": {...}, "trees": {...}, "objects": [...]}},
     "groups":  {"G-0001": {"parcels": ["P-0412", "P-0413"], "fence": {...}}}}

``fence`` (all keys optional)
    mode              auto | none | custom. Default auto, or custom when type, height_m,
                      thickness_m, colour or gaps is given without a mode.
                      auto = the rules in rules.yaml (props.fences); none = no fence;
                      custom = always fenced on the boundary, no seeded probability, no kind
                      filter (building and road clearances still apply).
    type              picket | wire | brick_wall (custom only). Each maps to the defaults in
                      FENCE_TYPES below.
    height_m, thickness_m  numbers > 0 (custom only), overriding the type.
    colour            "#rrggbb" (custom only), overriding the type.
    gaps              list of {"at": [x, y], "width_m": n} (custom only). A gap is cut out of
                      the boundary, centred on the nearest boundary point to ``at``. ``at`` is
                      absolute EPSG:5514 (S-JTSK) metres, the same frame as objects.

``trees`` (both keys are optional, but exactly one of density or count must be given)
    density_per_1000m2  number >= 0: trees per 1000 m2 of the parcel (replaces the rules'
                        density for this parcel).
    count               int >= 0: exact number of trees (not capped by max_per_parcel).
    seed_offset         int, default 0: mixed into the seed so that the layout rerolls. 0 keeps
                        the default layout.

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

from suchohrad_pipeline.config import PIPELINE_DIR

EDITS_VERSION = 1
DEFAULT_EDITS = PIPELINE_DIR / "config" / "edits.json"

FENCE_MODES = ("auto", "none", "custom")
DEFAULT_FENCE_TYPE = "picket"
# Each type is a height, thickness and colour; explicit fence keys override them.
FENCE_TYPES: dict[str, dict[str, float | str]] = {
    "picket": {"height_m": 1.0, "thickness_m": 0.06, "colour": "#9a8064"},
    "wire": {"height_m": 1.1, "thickness_m": 0.03, "colour": "#5c5f66"},
    "brick_wall": {"height_m": 1.8, "thickness_m": 0.3, "colour": "#b5523a"},
}

TOP_KEYS = ("version", "parcels", "groups")
PARCEL_KEYS = ("fence", "trees", "objects")
FENCE_KEYS = ("mode", "type", "height_m", "thickness_m", "colour", "gaps")
CUSTOM_FENCE_KEYS = ("type", "height_m", "thickness_m", "colour", "gaps")
GAP_KEYS = ("at", "width_m")
TREE_KEYS = ("density_per_1000m2", "count", "seed_offset")
OBJECT_KEYS = ("id", "asset", "position", "z", "rotation_deg", "scale", "colour")
GROUP_KEYS = ("parcels", "fence")

_PARCEL_ID = re.compile(r"P-\d{4,}")
_GROUP_ID = re.compile(r"G-\d{4,}")
_COLOUR = re.compile(r"#[0-9a-fA-F]{6}")
_ASSET = re.compile(r"pack:[^/\s]+/\S+|prim:[A-Za-z0-9_.-]+")


class EditsError(ValueError):
    """The edits document is not valid; ``errors`` lists every problem with its path."""

    def __init__(self, errors: list[str]) -> None:
        self.errors = list(errors)
        super().__init__("\n".join(self.errors))


@dataclass
class Edits:
    """A validated edits document. ``parcels`` and ``groups`` are JSON-native, as written."""

    parcels: dict[str, dict] = field(default_factory=dict)
    groups: dict[str, dict] = field(default_factory=dict)


@dataclass(frozen=True)
class FenceSpec:
    """A parcel's fence after group inheritance and type defaults. Gaps are (x, y, width_m)."""

    mode: str
    type: str
    height_m: float
    thickness_m: float
    colour: str
    gaps: tuple[tuple[float, float, float], ...] = ()


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


def _check_trees(v: object, path: str, errors: list[str]) -> None:
    if not _is_dict(v, path, errors):
        return
    _unknown_keys(v, TREE_KEYS, path, errors)
    has_density, has_count = "density_per_1000m2" in v, "count" in v
    if has_density == has_count:
        errors.append(f"{path}: give exactly one of density_per_1000m2 or count")
    if has_density and not (_is_num(v["density_per_1000m2"]) and v["density_per_1000m2"] >= 0):
        errors.append(f"{path}.density_per_1000m2: must be a number >= 0")
    if has_count and not (_is_int(v["count"]) and v["count"] >= 0):
        errors.append(f"{path}.count: must be an integer >= 0")
    if "seed_offset" in v and not _is_int(v["seed_offset"]):
        errors.append(f"{path}.seed_offset: must be an integer")


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
        _unknown_keys(entry, PARCEL_KEYS, path, errors)
        if "fence" in entry:
            _check_fence(entry["fence"], f"{path}.fence", errors)
        if "trees" in entry:
            _check_trees(entry["trees"], f"{path}.trees", errors)
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
    doc = {"version": EDITS_VERSION, "parcels": edits.parcels, "groups": edits.groups}
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
    )
