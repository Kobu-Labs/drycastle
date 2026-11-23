"""Small shared helpers for coordinates and shapely geometry."""

import math

from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry


def r3(v: float) -> float:
    """A coordinate rounded to the millimetre, as a plain float."""
    return round(float(v), 3)


def polygons(geom: BaseGeometry, min_area: float | None = None) -> list[Polygon]:
    """Every non-empty polygon of a geometry, whatever collection it is nested in (only those larger than ``min_area``, if given)."""
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom] if min_area is None or geom.area > min_area else []
    return [p for g in getattr(geom, "geoms", []) for p in polygons(g, min_area)]


def bearing_deg(e: float, n: float) -> float:
    """Compass bearing of the vector (east, north), degrees clockwise from north, 0..360."""
    return round(math.degrees(math.atan2(e, n)) % 360, 2)
