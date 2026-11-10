"""Bounding-box maths for the slice."""

from dataclasses import dataclass

from pyproj import Transformer
from shapely.geometry import LineString, MultiPoint, Polygon

# DMR 5.0 is served in ETRS89-TM34. It is also a metric CRS that is accurate enough
# at village scale to turn "radius in metres" into a box.
METRIC_CRS = "EPSG:3046"
WGS84 = "EPSG:4326"

_to_metric = Transformer.from_crs(WGS84, METRIC_CRS, always_xy=True)
_to_wgs84 = Transformer.from_crs(METRIC_CRS, WGS84, always_xy=True)


@dataclass(frozen=True)
class Bbox:
    """Square box around the slice centre, in both WGS84 and EPSG:3046 (easting/northing)."""

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
    e, n = _to_metric.transform(lon, lat)
    min_e, min_n, max_e, max_n = e - half, n - half, e + half, n + half
    corners = [
        _to_wgs84.transform(x, y)
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
