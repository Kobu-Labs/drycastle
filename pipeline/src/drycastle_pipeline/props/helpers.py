"""Small shared helpers of the props stage: seeded RNG, chunk index, line merging, road zone."""

import hashlib
import math
import random

from shapely.geometry import LineString, MultiLineString
from shapely.ops import linemerge, unary_union

from drycastle_pipeline.roads.roads import edge_lines


def seeded(key: str) -> random.Random:
    return random.Random(int.from_bytes(hashlib.sha256(key.encode()).digest()[:8], "big"))


def chunk_of(x: float, y: float, chunk_m: float) -> list[int]:
    return [math.floor(x / chunk_m), math.floor(y / chunk_m)]


def _lines(geom) -> list[LineString]:
    if geom.is_empty:
        return []
    if isinstance(geom, LineString):
        return [geom]
    if isinstance(geom, MultiLineString) or hasattr(geom, "geoms"):
        return [ln for g in geom.geoms for ln in _lines(g)]
    return []


def merged_lines(geom) -> list[LineString]:
    """Line parts of a geometry with touching parts joined."""
    parts = [ln for ln in _lines(geom) if not ln.is_empty]
    return _lines(linemerge(parts)) if parts else []


def road_zone(graph: dict, extra: float):
    """Union of the road carriageways grown by ``extra`` metres."""
    parts = [ln.buffer(e["width"] / 2 + extra) for e, ln in edge_lines(graph)]
    return unary_union(parts) if parts else None
