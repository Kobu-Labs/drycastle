"""Slice configuration: which part of the village the pipeline works on."""

from dataclasses import dataclass
from pathlib import Path

import yaml

PACKAGE_DIR = Path(__file__).resolve().parent
PIPELINE_DIR = PACKAGE_DIR.parents[1]
REPO_ROOT = PIPELINE_DIR.parent
DEFAULT_CONFIG = PIPELINE_DIR / "config" / "slice.local.yaml"
DEFAULT_RAW_DIR = REPO_ROOT / "data" / "raw"

AREAS = ("slice", "village")
DEFAULT_AREA = "slice"
DEFAULT_VILLAGE_RADIUS_M = 700.0

DEFAULT_RADIUS_M = 100.0
DEFAULT_MARGIN_M = 25.0


class ConfigError(Exception):
    """The slice configuration is missing or invalid."""


@dataclass(frozen=True)
class SliceConfig:
    osm_way: int
    radius_m: float = DEFAULT_RADIUS_M
    margin_m: float = DEFAULT_MARGIN_M
    # Optional map outline: absolute EPSG:5514 (S-JTSK) metres, an open ring (at least 3 points).
    # When set it replaces the square: ``radius_m`` is ignored, ``margin_m`` is fetched around it.
    polygon: tuple[tuple[float, float], ...] | None = None


@dataclass(frozen=True)
class AreaPaths:
    """Directories of one area. The slice keeps its original paths, the village has its own."""

    area: str
    raw: Path
    work: Path
    out: Path
    preview: Path


def area_paths(area: str = DEFAULT_AREA, root: Path = REPO_ROOT) -> AreaPaths:
    if area == "slice":
        return AreaPaths(
            area, root / "data" / "raw", root / "data" / "work",
            root / "out" / "map", root / "out" / "preview",
        )
    if area == "village":
        return AreaPaths(
            area, root / "data" / "village" / "raw", root / "data" / "village" / "work",
            root / "out" / "village" / "map", root / "out" / "village" / "preview",
        )
    raise ConfigError(f"unknown area {area!r}; choose one of {', '.join(AREAS)}")


def load_slice_config(path: Path = DEFAULT_CONFIG, area: str = DEFAULT_AREA) -> SliceConfig:
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
    if area == "slice":
        return SliceConfig(osm_way=way, radius_m=radius, margin_m=margin)
    if area != "village":
        raise ConfigError(f"unknown area {area!r}; choose one of {', '.join(AREAS)}")
    village = data.get("village") or {}
    if not isinstance(village, dict):
        raise ConfigError(f"{path.name}: 'village' must be a mapping")
    try:
        v_radius = float(village.get("radius_m", DEFAULT_VILLAGE_RADIUS_M))
        v_margin = float(village.get("margin_m", margin))
    except (TypeError, ValueError) as exc:
        raise ConfigError(f"{path.name}: village radius_m and margin_m must be numbers") from exc
    if v_radius <= 0 or v_margin < 0:
        raise ConfigError(f"{path.name}: village radius_m must be > 0, margin_m >= 0")
    polygon = parse_polygon(village.get("polygon"), path.name)
    return SliceConfig(osm_way=way, radius_m=v_radius, margin_m=v_margin, polygon=polygon)


def parse_polygon(raw: object, source: str = "config") -> tuple[tuple[float, float], ...] | None:
    """A ``polygon`` value (list of [x, y] S-JTSK metres) as an open tuple ring, or None."""
    if raw is None:
        return None
    try:
        points = list(raw)  # type: ignore[call-overload]
        if any(len(p) != 2 for p in points):
            raise ValueError
        ring = [(float(p[0]), float(p[1])) for p in points]
    except (TypeError, ValueError) as exc:
        raise ConfigError(f"{source}: village polygon must be a list of [x, y] numbers") from exc
    if len(ring) > 1 and ring[0] == ring[-1]:
        ring.pop()  # a ring repeating its first point is accepted
    if len(ring) < 3:
        raise ConfigError(f"{source}: village polygon needs at least 3 points")
    from shapely.geometry import Polygon

    shape = Polygon(ring)
    if not shape.is_valid or shape.area <= 0:
        raise ConfigError(f"{source}: village polygon is not a valid simple polygon (self-crossing?)")
    return tuple(ring)


def load_dotenv(path: Path | None = None) -> None:
    """Load KEY=VALUE lines from the repo-root ``.env`` (gitignored) into the environment.

    Variables already set in the environment win.
    """
    import os

    path = path or REPO_ROOT / ".env"
    if not path.is_file():
        return
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        name, _, value = line.partition("=")
        os.environ.setdefault(name.strip(), value.strip().strip("'\""))
