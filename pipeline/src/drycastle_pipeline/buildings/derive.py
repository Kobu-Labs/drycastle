"""Rules stage: heights, roofs and colours for every building -> ``data/village/work/buildings.json``.

Order of precedence (see ``config/rules.yaml``): overrides.yaml > OSM tags > building type class >
footprint-area class. Colours and the few random choices come from a generator seeded with
sha256(parcel id + ":" + building id), never from Python's ``hash()``.
"""

import hashlib
import json
import math
import random
import re
from collections import Counter
from dataclasses import dataclass, field
from itertools import pairwise
from pathlib import Path

import yaml
from shapely.geometry import Polygon, shape
from shapely.geometry.polygon import orient

from drycastle_pipeline.buildings.footprint import largest_polygon, roof_frame
from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.config import PIPELINE_DIR, map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.parcels.normalise import DEFAULT_WORK_DIR

DEFAULT_OVERRIDES = PIPELINE_DIR / "config" / "overrides.yaml"
BUILDINGS_FORMAT_VERSION = 1
ROOFS = ("flat", "gabled", "hipped", "lean_to")
OVERRIDE_FIELDS = (
    "height_m",
    "roof_height_m",
    "levels",
    "roof",
    "ridge_dir",
    "wall_colour",
    "roof_colour",
    "model",
    "model_axis",
    "remove",

    "roof_part",
    "rest_height_m",
    "rest_roof",
    "roof_fit",
    "roof_lead",
)
_COLOUR = re.compile(r"^#[0-9a-fA-F]{6}$")
_NUMBER = re.compile(r"^\s*(-?\d+(?:[.,]\d+)?)\s*(m)?\s*$")


class RulesError(Exception):
    """Invalid rules or overrides."""


@dataclass(frozen=True)
class BuildingClass:
    name: str
    height_m: float
    roof: dict[str, float]  # roof type -> weight
    pitch_deg: float = 30.0
    max_area_m2: float | None = None


@dataclass(frozen=True)
class BuildingRules:
    metres_per_level: float = 3.0
    metres_per_roof_level: float = 1.5
    min_eave_height_m: float = 2.4
    max_roof_rise_m: float = 5.0
    min_roof_rise_m: float = 0.5
    min_roof_fill: float = 0.8
    height_jitter_m: float = 0.6
    roof_shapes: dict[str, str] = field(default_factory=dict)
    types: dict[str, BuildingClass] = field(default_factory=dict)
    areas: tuple[BuildingClass, ...] = ()
    walls: tuple[str, ...] = ("#d9c7a0",)
    roofs: tuple[str, ...] = ("#a8442f",)
    flat_roofs: tuple[str, ...] = ("#6a6a6e",)


def _class(name: str, raw: dict) -> BuildingClass:
    roof = raw.get("roof", "gabled")
    weights = {roof: 1.0} if isinstance(roof, str) else {str(k): float(v) for k, v in roof.items()}
    if not weights or any(k not in ROOFS for k in weights):
        raise RulesError(f"buildings class {name}: roof must be one of {ROOFS}")
    cap = raw.get("max_area_m2")
    return BuildingClass(
        name=name,
        height_m=float(raw["height_m"]),
        roof=weights,
        pitch_deg=float(raw.get("pitch_deg", 30.0)),
        max_area_m2=None if cap is None else float(cap),
    )


def load_building_rules(path: Path = DEFAULT_RULES) -> BuildingRules:
    data = (yaml.safe_load(path.read_text()) or {}) if path.is_file() else {}
    raw = data.get("buildings") or {}
    d = BuildingRules()
    colours = raw.get("colours") or {}
    areas = tuple(_class(str(a.get("name", "area")), a) for a in raw.get("areas") or ())
    for earlier, later in pairwise(areas):
        if earlier.max_area_m2 is None or (
            later.max_area_m2 is not None and later.max_area_m2 <= earlier.max_area_m2
        ):
            raise RulesError("buildings.areas must be ascending, only the last without max_area_m2")
    return BuildingRules(
        metres_per_level=float(raw.get("metres_per_level", d.metres_per_level)),
        metres_per_roof_level=float(raw.get("metres_per_roof_level", d.metres_per_roof_level)),
        min_eave_height_m=float(raw.get("min_eave_height_m", d.min_eave_height_m)),
        max_roof_rise_m=float(raw.get("max_roof_rise_m", d.max_roof_rise_m)),
        min_roof_rise_m=float(raw.get("min_roof_rise_m", d.min_roof_rise_m)),
        min_roof_fill=float(raw.get("min_roof_fill", d.min_roof_fill)),
        height_jitter_m=float(raw.get("height_jitter_m", d.height_jitter_m)),
        roof_shapes=dict(raw.get("roof_shapes") or {}),
        types={str(k): _class(str(k), v) for k, v in (raw.get("types") or {}).items()},
        areas=areas,
        walls=tuple(colours.get("walls", d.walls)),
        roofs=tuple(colours.get("roofs", d.roofs)),
        flat_roofs=tuple(colours.get("flat_roofs", d.flat_roofs)),
    )


def load_traced(path: Path = DEFAULT_OVERRIDES) -> set[str]:
    """Buildings (or whole parcels) whose footprint stays as traced, whatever the cadastre says: those modelled by hand
    on that outline. The list keep_traced_footprint of overrides.yaml."""
    data = (yaml.safe_load(path.read_text()) or {}) if path.is_file() else {}
    got = data.get("keep_traced_footprint") or []
    if not isinstance(got, list) or not all(isinstance(k, str) for k in got):
        raise RulesError("overrides: keep_traced_footprint must be a list of building or parcel ids")
    return set(got)


def load_overrides(path: Path = DEFAULT_OVERRIDES) -> dict[str, dict]:
    data = (yaml.safe_load(path.read_text()) or {}) if path.is_file() else {}
    out = data.get("buildings") or {}
    for key, fields in out.items():
        if not isinstance(fields, dict):
            raise RulesError(f"overrides: {key} must be a mapping of fields")
        for name, value in fields.items():
            if name not in OVERRIDE_FIELDS:
                raise RulesError(f"overrides: {key}: unknown field {name}")
            if name == "remove" and not isinstance(value, bool):
                raise RulesError(f"overrides: {key}: remove must be true or false")
            if name == "roof_height_m" and not (isinstance(value, int | float) and value > 0):
                raise RulesError(f"overrides: {key}: roof_height_m must be a positive number")
            if name == "roof" and value not in ROOFS:
                raise RulesError(f"overrides: {key}: roof must be one of {ROOFS}")
            if name == "model":
                from drycastle_pipeline.buildings.landmarks.catalog import FOOTPRINT_MODELS, MODELS

                known = sorted({*MODELS, *FOOTPRINT_MODELS})
                if value not in known:
                    raise RulesError(f"overrides: {key}: model must be one of {known}")
            if name == "model_axis" and not isinstance(value, int | float):
                raise RulesError(f"overrides: {key}: model_axis must be a number (radians)")
            if name == "roof_part" and not (
                isinstance(value, list) and len(value) >= 3 and all(isinstance(p, list) and len(p) == 2 for p in value)
            ):
                raise RulesError(f"overrides: {key}: roof_part must be a polygon [[x, y], ...] in map metres")
            if name == "roof_lead" and not (isinstance(value, list) and len(value) == 2 and all(isinstance(p, list) and len(p) == 2 for p in value)):
                raise RulesError(f"overrides: {key}: roof_lead must be a wall [[x, y], [x, y]] in map metres")
            if name == "roof_fit" and not isinstance(value, bool):
                raise RulesError(f"overrides: {key}: roof_fit must be true or false")
            if name == "rest_roof" and value not in ("flat", "lean_to", "auto"):
                raise RulesError(f"overrides: {key}: rest_roof must be flat, lean_to or auto")
            if name == "rest_height_m" and not (isinstance(value, int | float) and value > 0):
                raise RulesError(f"overrides: {key}: rest_height_m must be a positive number")
            if name.endswith("_colour") and not _COLOUR.match(str(value)):
                raise RulesError(f"overrides: {key}: {name} must be #rrggbb")
    return {str(k): dict(v) for k, v in out.items()}


def seed_for(parcel_id: str, building_id: str) -> int:
    digest = hashlib.sha256(f"{parcel_id}:{building_id}".encode()).digest()
    return int.from_bytes(digest[:8], "big")


def parse_metres(value: object) -> float | None:
    m = _NUMBER.match(str(value))
    if not m:
        return None
    v = float(m.group(1).replace(",", "."))
    return v if v > 0 else None


def _pick(rng_value: float, items):
    return items[min(int(rng_value * len(items)), len(items) - 1)]


def _pick_weighted(rng_value: float, weights: dict[str, float]) -> str:
    total = sum(weights.values())
    acc = 0.0
    for key in sorted(weights):
        acc += weights[key] / total
        if rng_value < acc:
            return key
    return max(weights)


def choose_class(tags: dict, area: float, rules: BuildingRules) -> tuple[BuildingClass, str]:
    btype = tags.get("building")
    if btype in rules.types:
        return rules.types[btype], f"type:{btype}"
    for cls in rules.areas:
        if cls.max_area_m2 is None or area <= cls.max_area_m2:
            return cls, f"area:{cls.name}"
    fallback = BuildingClass("default", 6.0, {"gabled": 1.0})
    return fallback, "area:default"


def derive_building(
    building_id: str,
    parcel_id: str,
    footprint: Polygon,
    tags: dict,
    rules: BuildingRules,
    overrides: dict | None = None,
) -> dict:
    """The rules for one building; see module docstring. Returns the buildings.json entry."""
    ov = overrides or {}
    footprint = orient(largest_polygon(footprint), 1.0)
    area = footprint.area
    rng = random.Random(seed_for(parcel_id, building_id))
    r_wall, r_roof_colour, r_jitter, r_shape = (rng.random() for _ in range(4))

    cls, cls_name = choose_class(tags, area, rules)

    # Roof type: override > OSM roof:shape > OSM roof:levels > class
    roof_levels = parse_metres(tags.get("roof:levels"))
    shape_tag = rules.roof_shapes.get(str(tags.get("roof:shape", "")))
    class_roof = _pick_weighted(r_shape, cls.roof)
    if "roof" in ov:
        roof, forced = ov["roof"], True
    elif shape_tag:
        roof, forced = shape_tag, False
    elif roof_levels:
        roof, forced = (class_roof if class_roof != "flat" else "gabled"), False
    else:
        roof, forced = class_roof, False

    ridge_override = ov.get("ridge_dir")
    angle, _, _, a, b = roof_frame(
        footprint,
        # a lean-to keeps the full turn: its high edge is on the left of the ridge direction
        None if ridge_override is None else float(ridge_override) % (2 * math.pi if roof == "lean_to" else math.pi),
        hipped=roof == "hipped",
    )
    if roof != "flat" and not forced and area / max(4 * a * b, 1e-9) < rules.min_roof_fill:
        roof = "flat"

    # Heights
    height_tag = ov.get("height_m") or parse_metres(tags.get("height"))
    levels_tag = ov.get("levels") or parse_metres(tags.get("building:levels"))
    rise = 0.0
    if roof != "flat":
        if roof_levels:
            rise = roof_levels * rules.metres_per_roof_level
        else:
            rise = b * math.tan(math.radians(cls.pitch_deg))
        rise = min(rise, rules.max_roof_rise_m)
    if roof != "flat" and ov.get("roof_height_m"):
        rise = float(ov["roof_height_m"])  # measured rise from eave to ridge, no pitch or cap
    if height_tag:
        total = float(height_tag)
    elif levels_tag:
        total = float(levels_tag) * rules.metres_per_level + rise
    else:
        jitter = (r_jitter * 2 - 1) * rules.height_jitter_m
        total = round(cls.height_m + jitter, 1)
    if roof != "flat":
        rise = min(rise, max(total - rules.min_eave_height_m, 0.0))
        if rise < rules.min_roof_rise_m:
            roof, rise = "flat", 0.0
    if roof == "flat":
        angle = None
    eave = total - rise
    levels = int(levels_tag) if levels_tag else max(1, round(eave / rules.metres_per_level))

    wall = ov.get("wall_colour") or _pick(r_wall, rules.walls)
    roof_palette = rules.flat_roofs if roof == "flat" else rules.roofs
    roof_colour = ov.get("roof_colour") or _pick(r_roof_colour, roof_palette)

    exterior = [[round(x, 3), round(y, 3)] for x, y in footprint.exterior.coords[:-1]]
    holes = [[[round(x, 3), round(y, 3)] for x, y in r.coords[:-1]] for r in footprint.interiors]
    landmark = (
        {"model": str(ov["model"]), "model_axis": float(ov.get("model_axis", 0.0))}
        if ov.get("model")
        else {}
    )
    part = (
        {"roof_part": [[float(x), float(y)] for x, y in ov["roof_part"]], "rest_height_m": float(ov.get("rest_height_m", 2.8)),
         **({"rest_roof": ov["rest_roof"]} if ov.get("rest_roof") in ("lean_to", "auto") else {})}
        if ov.get("roof_part")
        else {}
    )
    return {
        **landmark,
        **part,
        **({"roof_fit": True} if ov.get("roof_fit") and not ov.get("roof_part") else {}),
        **({"roof_lead": [[float(x), float(y)] for x, y in ov["roof_lead"]]} if ov.get("roof_fit") and ov.get("roof_lead") else {}),
        "id": building_id,
        "parcel": parcel_id,
        "footprint": {"exterior": exterior, "holes": holes},
        "class": "override" if "height_m" in ov else ("osm" if height_tag else cls_name),
        "height_m": round(total, 2),
        "eave_height_m": round(eave, 2),
        "roof_height_m": round(rise, 2),
        "levels": levels,
        "roof": roof,
        "ridge_dir": None if angle is None else round(angle, 4),
        "wall_colour": hex_to_rgb(wall),
        "roof_colour": hex_to_rgb(roof_colour),
    }


def height_class(h: float) -> str:
    if h < 4:
        return "<4 m"
    if h < 6:
        return "4-6 m"
    if h < 8:
        return "6-8 m"
    return ">=8 m"


@dataclass
class BuildingsResult:
    buildings: list[dict]
    unassigned: list[str]
    unused_overrides: list[str]
    footprints_fixed: dict[str, int] | None = None  # by the cadastre: {replace|shift|clip: how many}

    def report(self) -> list[str]:
        lines = [
            f"buildings: {len(self.buildings)}, without parcel (skipped): {len(self.unassigned)}"
        ]
        roofs = Counter(b["roof"] for b in self.buildings)
        lines.append("roof types: " + ", ".join(f"{k} {roofs.get(k, 0)}" for k in ROOFS))
        heights = Counter(height_class(b["height_m"]) for b in self.buildings)
        order = ["<4 m", "4-6 m", "6-8 m", ">=8 m"]
        lines.append("height classes: " + ", ".join(f"{k} {heights.get(k, 0)}" for k in order))
        cross = Counter((b["roof"], height_class(b["height_m"])) for b in self.buildings)
        for roof in ROOFS:
            lines.append(f"  {roof}: " + ", ".join(f"{k} {cross.get((roof, k), 0)}" for k in order))
        if self.footprints_fixed:
            lines.append("footprints set right by the cadastre: " + ", ".join(f"{k} {n}" for k, n in sorted(self.footprints_fixed.items())))
        for key in self.unused_overrides:
            lines.append(f"warning: override {key} matches no building")
        return lines


def build_buildings(
    work_dir: Path = DEFAULT_WORK_DIR,
    rules: BuildingRules | None = None,
    overrides: dict[str, dict] | None = None,
) -> BuildingsResult:
    rules = rules or load_building_rules()
    overrides = load_overrides() if overrides is None else overrides
    parcels = json.loads((work_dir / "parcels.json").read_text())
    feats = json.loads((work_dir / "normalised" / "buildings.geojson").read_text())["features"]
    by_id = {f["properties"]["osm_id"]: f for f in feats}

    from drycastle_pipeline.buildings.cadastre_check import Plots, judge

    plots, kept, fixed = Plots(parcels["parcels"]), load_traced(), Counter()
    out, used = [], set()
    for parcel in sorted(parcels["parcels"], key=lambda p: p["id"]):
        for bid in sorted(parcel["buildings"]):
            f = by_id[bid]
            merged: dict = {}
            for key in (parcel["id"], bid):
                if key in overrides:
                    merged.update(overrides[key])
                    used.add(key)
            if merged.pop("remove", False):
                continue  # replaced by a placed object (a hangar from a pack): no mesh, no collider, no interior
            geom = shape(f["geometry"])
            if bid not in kept and parcel["id"] not in kept and geom.geom_type == "Polygon":
                # the outline as traced in OpenStreetMap, set right by the cadastre where the cadastre knows better
                finding, geom = judge(geom.buffer(0), parcel["id"], plots)
                if finding["finding"] in ("replace", "shift", "clip"):
                    fixed[finding["finding"]] += 1
            out.append(derive_building(bid, parcel["id"], geom, f["properties"]["tags"], rules, merged))
    return BuildingsResult(
        out, sorted(parcels.get("unassigned_buildings", [])), sorted(set(overrides) - used), dict(fixed)
    )


def write_buildings(work_dir: Path, result: BuildingsResult) -> Path:
    path = work_dir / "buildings.json"
    doc = {"version": BUILDINGS_FORMAT_VERSION, "buildings": result.buildings}
    path.write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")
    return path


def run(
    force: bool = False, report: bool = False, **_options: object
) -> int:
    work_dir = map_paths().work
    try:
        result = build_buildings(work_dir)
    except FileNotFoundError as exc:
        print(f"rules: error: missing input {Path(str(exc.filename)).name}; run parcels first")
        return 1
    except RulesError as exc:
        print(f"rules: error: {exc}")
        return 1
    write_buildings(work_dir, result)
    for line in result.report():
        print(line if line.startswith("warning") else f"rules: {line}")
    return 0
