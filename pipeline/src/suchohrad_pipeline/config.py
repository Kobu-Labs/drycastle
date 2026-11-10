"""Slice configuration: which part of the village the pipeline works on."""

from dataclasses import dataclass
from pathlib import Path

import yaml

PACKAGE_DIR = Path(__file__).resolve().parent
PIPELINE_DIR = PACKAGE_DIR.parents[1]
REPO_ROOT = PIPELINE_DIR.parent
DEFAULT_CONFIG = PIPELINE_DIR / "config" / "slice.local.yaml"
DEFAULT_RAW_DIR = REPO_ROOT / "data" / "raw"

DEFAULT_RADIUS_M = 100.0
DEFAULT_MARGIN_M = 25.0


class ConfigError(Exception):
    """The slice configuration is missing or invalid."""


@dataclass(frozen=True)
class SliceConfig:
    osm_way: int
    radius_m: float = DEFAULT_RADIUS_M
    margin_m: float = DEFAULT_MARGIN_M


def load_slice_config(path: Path = DEFAULT_CONFIG) -> SliceConfig:
    if not path.is_file():
        raise ConfigError(
            f"slice config not found: {path.name}. "
            "Copy pipeline/config/slice.example.yaml to pipeline/config/slice.local.yaml "
            "and fill in osm_way."
        )
    data = yaml.safe_load(path.read_text()) or {}
    if not isinstance(data, dict) or "osm_way" not in data:
        raise ConfigError(f"{path.name} must be a mapping with an 'osm_way' key")
    try:
        way = int(data["osm_way"])
        radius = float(data.get("radius_m", DEFAULT_RADIUS_M))
        margin = float(data.get("margin_m", DEFAULT_MARGIN_M))
    except (TypeError, ValueError) as exc:
        raise ConfigError(f"{path.name}: osm_way, radius_m and margin_m must be numbers") from exc
    if way <= 0 or radius <= 0 or margin < 0:
        raise ConfigError(f"{path.name}: osm_way and radius_m must be > 0, margin_m >= 0")
    return SliceConfig(osm_way=way, radius_m=radius, margin_m=margin)
