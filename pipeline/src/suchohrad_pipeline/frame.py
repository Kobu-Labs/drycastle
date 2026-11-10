"""Local metric frame: WGS84 -> EPSG:5514 (S-JTSK / Krovak East North) -> origin at slice centre.

With ``always_xy`` pyproj returns EPSG:5514 as (east-like, north-like) metres, so local x grows
eastwards and local y northwards. The axes stay those of EPSG:5514 (they are rotated a few
degrees from true north by the Krovak grid convergence); we do not re-orient them.
"""

import functools
from dataclasses import dataclass

from shapely import transform
from shapely.geometry.base import BaseGeometry

WGS84 = "EPSG:4326"
SJTSK = "EPSG:5514"


@functools.cache
def _transformer():
    """WGS84 -> S-JTSK; built on first use because importing pyproj is slow."""
    from pyproj import Transformer

    return Transformer.from_crs(WGS84, SJTSK, always_xy=True)


# Coordinates are rounded to a millimetre so that outputs are byte-stable across runs.
DECIMALS = 3


@dataclass(frozen=True)
class Frame:
    """Local frame whose origin is the slice centre; ``origin`` is in EPSG:5514 metres."""

    lon: float
    lat: float
    origin_x: float
    origin_y: float

    @classmethod
    def at(cls, lon: float, lat: float) -> "Frame":
        x, y = _transformer().transform(lon, lat)
        return cls(lon, lat, x, y)

    def to_local(self, geom: BaseGeometry) -> BaseGeometry:
        """Reproject a WGS84 (lon, lat) geometry to local metres."""

        def convert(coords):  # coords: (n, 2) array
            x, y = _transformer().transform(coords[:, 0], coords[:, 1])
            out = coords.copy()
            out[:, 0] = (x - self.origin_x).round(DECIMALS)
            out[:, 1] = (y - self.origin_y).round(DECIMALS)
            return out

        return transform(geom, convert)

    def local_to_sjtsk(self, x: float, y: float) -> tuple[float, float]:
        return x + self.origin_x, y + self.origin_y


def box_to_sjtsk(west: float, south: float, east: float, north: float, steps: int = 16):
    """Absolute EPSG:5514 polygon of a WGS84 lon/lat box (edges densified, they are curved)."""
    from shapely.geometry import Polygon

    ts = [i / steps for i in range(steps + 1)]
    ring = (
        [(west + t * (east - west), south) for t in ts]
        + [(east, south + t * (north - south)) for t in ts]
        + [(east - t * (east - west), north) for t in ts]
        + [(west, north - t * (north - south)) for t in ts]
    )
    xs, ys = _transformer().transform([p[0] for p in ring], [p[1] for p in ring])
    return Polygon(list(zip(xs, ys, strict=True)))
