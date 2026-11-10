"""Loader for ``config/rules.yaml``. Every value has a default so the file may be partial."""

from dataclasses import dataclass, field
from pathlib import Path

import yaml

from suchohrad_pipeline.config import PIPELINE_DIR

DEFAULT_RULES = PIPELINE_DIR / "config" / "rules.yaml"


@dataclass(frozen=True)
class ParcelRules:
    # Building assignment
    max_assign_distance_m: float = 10.0
    # Road
    road_highways: tuple[str, ...] = (
        "motorway",
        "trunk",
        "primary",
        "secondary",
        "tertiary",
        "unclassified",
        "residential",
        "living_street",
        "service",
    )
    road_buffer_width_m: float = 8.0
    road_min_covered_ratio: float = 0.3
    # Landuse / natural
    landuse_kinds: dict[str, str] = field(default_factory=dict)
    natural_kinds: dict[str, str] = field(default_factory=dict)
    min_cover_ratio: float = 0.5
    # Garden
    garden_max_area_m2: float = 1500.0
    builtup_landuse: tuple[str, ...] = ("residential",)
    builtup_distance_m: float = 30.0
    # Chunking
    chunk_size_m: float = 250.0
    # Matching
    min_iou: float = 0.5


def load_rules(path: Path = DEFAULT_RULES) -> ParcelRules:
    data = (yaml.safe_load(path.read_text()) or {}) if path.is_file() else {}
    kind = data.get("parcel_kind") or {}
    d = ParcelRules()
    road = kind.get("road") or {}
    cover = kind.get("landuse") or {}
    garden = kind.get("garden") or {}
    return ParcelRules(
        max_assign_distance_m=float(
            (data.get("building_assignment") or {}).get("max_distance_m", d.max_assign_distance_m)
        ),
        road_highways=tuple(road.get("highways", d.road_highways)),
        road_buffer_width_m=float(road.get("buffer_width_m", d.road_buffer_width_m)),
        road_min_covered_ratio=float(road.get("min_covered_ratio", d.road_min_covered_ratio)),
        landuse_kinds=dict(cover.get("landuse", {})),
        natural_kinds=dict(cover.get("natural", {})),
        min_cover_ratio=float(cover.get("min_cover_ratio", d.min_cover_ratio)),
        garden_max_area_m2=float(garden.get("max_area_m2", d.garden_max_area_m2)),
        builtup_landuse=tuple(garden.get("builtup_landuse", d.builtup_landuse)),
        builtup_distance_m=float(garden.get("builtup_distance_m", d.builtup_distance_m)),
        chunk_size_m=float((data.get("chunking") or {}).get("size_m", d.chunk_size_m)),
        min_iou=float((data.get("registry") or {}).get("min_iou", d.min_iou)),
    )
