"""Bounding-box maths for the slice and the village."""

import functools
from dataclasses import dataclass

from shapely.geometry import LineString, MultiPoint, Polygon

# DMR 5.0 is served in ETRS89-TM34. It is also a metric CRS that is accurate enough
# at village scale to turn "radius in metres" into a box.
METRIC_CRS = "EPSG:3046"
WGS84 = "EPSG:4326"



@functools.cache
def _to_metric():
    """WGS84 -> metric CRS; pyproj is imported on first use because it is slow to import."""
    from pyproj import Transformer

    return Transformer.from_crs(WGS84, METRIC_CRS, always_xy=True)


@functools.cache
def _to_wgs84():
    from pyproj import Transformer

    return Transformer.from_crs(METRIC_CRS, WGS84, always_xy=True)


@dataclass(frozen=True)
class Bbox:
    """Box around the area (a square, or the bounds of a polygon), in WGS84 and EPSG:3046."""

    west: float
    south: float
    east: float
    north: float
    min_e: float
    min_n: float
    max_e: float
    max_n: float

    @property
    def overpass(self) -> str:
        """Overpass order: south,west,north,east."""
        return f"{self.south:.7f},{self.west:.7f},{self.north:.7f},{self.east:.7f}"

    @property
    def wfs(self) -> str:
        """WFS 2.0 with an EPSG:4326 URN: lat/lon axis order."""
        return (
            f"{self.south:.7f},{self.west:.7f},{self.north:.7f},{self.east:.7f},"
            "urn:ogc:def:crs:EPSG::4326"
        )


def centroid_of_geometry(points: list[tuple[float, float]]) -> tuple[float, float]:
    """Centroid (lon, lat) of a way given as (lon, lat) points."""
    if not points:
        raise ValueError("way has no geometry")
    if len(points) >= 4 and points[0] == points[-1]:
        c = Polygon(points).centroid
    elif len(points) >= 2:
        c = LineString(points).centroid
    else:
        c = MultiPoint(points).centroid
    return (c.x, c.y)


def compute_bbox(lon: float, lat: float, radius_m: float, margin_m: float = 0.0) -> Bbox:
    """Box of half-side ``radius_m + margin_m`` metres around (lon, lat)."""
    if radius_m <= 0 or margin_m < 0:
        raise ValueError("radius_m must be > 0 and margin_m >= 0")
    half = radius_m + margin_m
    e, n = _to_metric().transform(lon, lat)
    min_e, min_n, max_e, max_n = e - half, n - half, e + half, n + half
    corners = [
        _to_wgs84().transform(x, y)
        for x, y in ((min_e, min_n), (min_e, max_n), (max_e, min_n), (max_e, max_n))
    ]
    lons = [c[0] for c in corners]
    lats = [c[1] for c in corners]
    return Bbox(
        west=min(lons),
        south=min(lats),
        east=max(lons),
        north=max(lats),
        min_e=min_e,
        min_n=min_n,
        max_e=max_e,
        max_n=max_n,
    )


@functools.cache
def _sjtsk_to(crs: str):
    from pyproj import Transformer

    return Transformer.from_crs("EPSG:5514", crs, always_xy=True)


def bbox_of_polygon(ring: list[tuple[float, float]] | tuple, margin_m: float = 0.0) -> Bbox:
    """Box around a polygon given in absolute EPSG:5514 metres, grown by ``margin_m``.

    The box is axis-aligned in S-JTSK. WGS84 and EPSG:3046 are rotated against it (Krovak
    convergence), so its densified edges are transformed and the extremes taken.
    """
    if margin_m < 0:
        raise ValueError("margin_m must be >= 0")
    minx, miny, maxx, maxy = Polygon(ring).bounds
    minx, miny, maxx, maxy = minx - margin_m, miny - margin_m, maxx + margin_m, maxy + margin_m
    steps = 32
    ts = [i / steps for i in range(steps + 1)]
    edge = (
        [(minx + t * (maxx - minx), miny) for t in ts]
        + [(maxx, miny + t * (maxy - miny)) for t in ts]
        + [(maxx - t * (maxx - minx), maxy) for t in ts]
        + [(minx, maxy - t * (maxy - miny)) for t in ts]
    )
    xs, ys = [p[0] for p in edge], [p[1] for p in edge]
    lons, lats = _sjtsk_to(WGS84).transform(xs, ys)
    es, ns = _sjtsk_to(METRIC_CRS).transform(xs, ys)
    return Bbox(
        west=min(lons), south=min(lats), east=max(lons), north=max(lats),
        min_e=min(es), min_n=min(ns), max_e=max(es), max_n=max(ns),
    )
