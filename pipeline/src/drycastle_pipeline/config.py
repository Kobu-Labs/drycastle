"""Map configuration: which part of the village the pipeline works on."""

from dataclasses import dataclass
from pathlib import Path

import yaml

PACKAGE_DIR = Path(__file__).resolve().parent
PIPELINE_DIR = PACKAGE_DIR.parents[1]
REPO_ROOT = PIPELINE_DIR.parent
DEFAULT_CONFIG = PIPELINE_DIR / "config" / "map.local.yaml"
DEFAULT_RAW_DIR = REPO_ROOT / "data" / "village" / "raw"

DEFAULT_MARGIN_M = 25.0


class ConfigError(Exception):
    """The map configuration is missing or invalid."""


@dataclass(frozen=True)
class MapConfig:
    osm_way: int
    # The map outline: absolute EPSG:5514 (S-JTSK) metres, an open ring (at least 3 points).
    # ``margin_m`` is fetched around it and dropped.
    polygon: tuple[tuple[float, float], ...]
    margin_m: float = DEFAULT_MARGIN_M


@dataclass(frozen=True)
class MapPaths:
    """Directories of the map."""

    raw: Path
    work: Path
    out: Path
    preview: Path


def map_paths(root: Path = REPO_ROOT) -> MapPaths:
    return MapPaths(
        root / "data" / "village" / "raw", root / "data" / "village" / "work",
        root / "out" / "village" / "map", root / "out" / "village" / "preview",
    )


def load_map_config(path: Path = DEFAULT_CONFIG) -> MapConfig:
    if not path.is_file():
        raise ConfigError(
            f"map config not found: {path.name}. "
            "Copy pipeline/config/map.example.yaml to pipeline/config/map.local.yaml "
            "and fill in osm_way and polygon."
        )
    data = yaml.safe_load(path.read_text()) or {}
    if not isinstance(data, dict) or "osm_way" not in data:
        raise ConfigError(f"{path.name} must be a mapping with an 'osm_way' key")
    try:
        way = int(data["osm_way"])
        margin = float(data.get("margin_m", DEFAULT_MARGIN_M))
    except (TypeError, ValueError) as exc:
        raise ConfigError(f"{path.name}: osm_way and margin_m must be numbers") from exc
    if way <= 0 or margin < 0:
        raise ConfigError(f"{path.name}: osm_way must be > 0, margin_m >= 0")
    polygon = parse_polygon(data.get("polygon"), path.name)
    return MapConfig(osm_way=way, polygon=polygon, margin_m=margin)


def parse_polygon(raw: object, source: str = "config") -> tuple[tuple[float, float], ...]:
    """A ``polygon`` value (list of [x, y] S-JTSK metres) as an open tuple ring."""
    if raw is None:
        raise ConfigError(f"{source}: 'polygon' (the map outline, a list of [x, y] S-JTSK metres) is required")
    try:
        points = list(raw)  # type: ignore[call-overload]
        if any(len(p) != 2 for p in points):
            raise ValueError
        ring = [(float(p[0]), float(p[1])) for p in points]
    except (TypeError, ValueError) as exc:
        raise ConfigError(f"{source}: map polygon must be a list of [x, y] numbers") from exc
    if len(ring) > 1 and ring[0] == ring[-1]:
        ring.pop()  # a ring repeating its first point is accepted
    if len(ring) < 3:
        raise ConfigError(f"{source}: map polygon needs at least 3 points")
    from shapely.geometry import Polygon

    shape = Polygon(ring)
    if not shape.is_valid or shape.area <= 0:
        raise ConfigError(f"{source}: map polygon is not a valid simple polygon (self-crossing?)")
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
