"""Water bodies for the map artifact (``water.json``): one body per water parcel, with a flat surface and bed.

The shooter swims and drowns in these (``ShooterCore.Water``). Heights come from the terrain of the map (the DMR
surface, not rebased, like ``terrain.json``): the surface is the median terrain height along the shoreline (the
parcel outline), the bed is the lowest terrain height inside. A parcel with no terrain under its outline gets no body.
"""

import numpy as np
import shapely
from shapely import contains_xy
from shapely.geometry import mapping, shape

WATER_FORMAT_VERSION = 1
SAMPLE_STEP_M = 2.0


def water_heights(geom, terrain, step: float = SAMPLE_STEP_M) -> tuple[float, float] | None:
    """``(surface_y, bed_y)`` in metres for a water polygon, or None when the terrain does not cover it.

    ``geom`` is a shapely polygon in the local frame (x east, y north); ``terrain`` is a ``Terrain``.
    """
    ring = geom.exterior
    count = max(8, int(ring.length // step))
    shore = [ring.interpolate(d) for d in np.linspace(0.0, ring.length, count, endpoint=False)]
    sx = np.array([p.x for p in shore])
    sy = np.array([p.y for p in shore])
    on_shore = contains_xy(terrain.coverage, sx, sy)

    minx, miny, maxx, maxy = geom.bounds
    gx, gy = np.meshgrid(np.arange(minx + step / 2, maxx, step), np.arange(miny + step / 2, maxy, step))
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(geom, gx, gy)
    gx, gy = gx[inside], gy[inside]
    covered = contains_xy(terrain.coverage, gx, gy)
    gx, gy = gx[covered], gy[covered]

    if not on_shore.any() or gx.size == 0:
        return None
    surface = float(np.median(terrain.surface(sx[on_shore], sy[on_shore])))
    bed = float(np.min(terrain.surface(gx, gy)))
    return round(surface, 3), round(bed, 3)


def region_bed(geom, terrain, step: float = SAMPLE_STEP_M) -> float | None:
    """The lowest terrain height inside a water region's polygon, or None when the terrain does not cover it."""
    minx, miny, maxx, maxy = geom.bounds
    gx, gy = np.meshgrid(np.arange(minx + step / 2, maxx, step), np.arange(miny + step / 2, maxy, step))
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(geom, gx, gy)
    gx, gy = gx[inside], gy[inside]
    covered = contains_xy(terrain.coverage, gx, gy)
    gx, gy = gx[covered], gy[covered]
    if gx.size == 0:
        return None
    return round(float(np.min(terrain.surface(gx, gy))), 3)


WET_STEP_M = 1.0
WET_REACH_M = 8.0  # how far past the drawn outline the water may run to find its shoreline


def wet_area(outline, terrain, water_y: float, step: float = WET_STEP_M):
    """Where a flat water surface at ``water_y`` really lies: the terrain below it, reached from ``outline``.

    The drawn outline of a water region is only a hint (traced on the coarse terrain grid); the shore is where the
    ground rises above the level. This samples the terrain on a ``step`` grid within ``WET_REACH_M`` of the outline,
    keeps the cells below ``water_y`` and returns the pieces that touch the outline (a pit that is not connected to it
    stays dry), smoothed a little so the shore is not a staircase. Empty when nothing there is below the level.
    """
    from shapely.geometry import box as _box
    from shapely.ops import unary_union

    reach = outline.buffer(WET_REACH_M)
    minx, miny, maxx, maxy = reach.bounds
    gx, gy = np.meshgrid(np.arange(minx + step / 2, maxx, step), np.arange(miny + step / 2, maxy, step))
    gx, gy = gx.ravel(), gy.ravel()
    keep = contains_xy(reach, gx, gy) & contains_xy(terrain.coverage, gx, gy)
    gx, gy = gx[keep], gy[keep]
    if gx.size == 0:
        return outline.__class__()
    wet = terrain.surface(gx, gy) < water_y + 0.15  # a hair over, so the water tucks under the bank
    gx, gy = gx[wet], gy[wet]
    if gx.size == 0:
        return outline.__class__()
    cells = shapely.box(gx - step / 2, gy - step / 2, gx + step / 2, gy + step / 2)
    shore = unary_union(cells).buffer(0.35).simplify(0.25)
    parts = [g for g in getattr(shore, "geoms", [shore]) if g.intersects(outline)]
    return unary_union(parts) if parts else outline.__class__()


def build_water(
    parcels: list[dict], terrain, regions: dict | None = None, kinds: frozenset[str] | set[str] = frozenset({"water"})
) -> tuple[dict, list[str]]:
    """The ``water.json`` document for the ground areas (and parcels) of a water kind (``kinds``), and the ids skipped for lack of terrain.

    ``regions`` (``plan_regions(...).regions``) adds one body per region that has a ``water_y``: its polygon, that
    surface and the lowest terrain inside as the bed. A region whose terrain is nowhere below its surface holds no
    water and is skipped too.
    """
    bodies, skipped = [], []
    for p in sorted(parcels, key=lambda p: p["id"]):
        if p["kind"] not in kinds:
            continue
        heights = water_heights(shape(p["polygon"]), terrain)
        if heights is None:
            skipped.append(p["id"])
            continue
        surface, bed = heights
        bodies.append({"id": p["id"], "polygon": p["polygon"], "surface_y": surface, "bed_y": bed})
    for rid, region in sorted((regions or {}).items()):
        if region.water_y is None:
            continue
        wet = wet_area(region.polygon, terrain, region.water_y)
        pieces = [g for g in getattr(wet, "geoms", [wet]) if not g.is_empty and g.area > 1.0]
        beds = [region_bed(g, terrain) for g in pieces]
        if not pieces or any(b is None for b in beds):
            skipped.append(rid)
            continue
        for k, (g, bed) in enumerate(zip(pieces, beds, strict=True), 1):
            bodies.append(
                {
                    "id": rid if len(pieces) == 1 else f"{rid}.{k}",
                    "polygon": mapping(g),
                    "surface_y": round(region.water_y, 3),
                    "bed_y": min(bed, round(region.water_y - 0.05, 3)),
                }
            )
    doc = {
        "version": WATER_FORMAT_VERSION,
        "axes": "polygon: local metres, x east, y north (as parcels.json); heights: metres, as terrain.json",
        "sample_step_m": SAMPLE_STEP_M,
        "bodies": bodies,
    }
    return doc, skipped
