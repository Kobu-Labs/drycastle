"""Water bodies for the map artifact (``water.json``): one body per water parcel, with a flat surface and bed.

The shooter swims and drowns in these (``ShooterCore.Water``). Heights come from the terrain of the map (the DMR
surface, not rebased, like ``terrain.json``): the surface is the median terrain height along the shoreline (the
parcel outline), the bed is the lowest terrain height inside. A parcel with no terrain under its outline gets no body.
"""

import numpy as np
from shapely import contains_xy
from shapely.geometry import shape

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


def build_water(parcels: list[dict], terrain) -> tuple[dict, list[str]]:
    """The ``water.json`` document for the parcels of kind ``water``, and the ids skipped for lack of terrain."""
    bodies, skipped = [], []
    for p in sorted(parcels, key=lambda p: p["id"]):
        if p["kind"] != "water":
            continue
        heights = water_heights(shape(p["polygon"]), terrain)
        if heights is None:
            skipped.append(p["id"])
            continue
        surface, bed = heights
        bodies.append({"id": p["id"], "polygon": p["polygon"], "surface_y": surface, "bed_y": bed})
    doc = {
        "version": WATER_FORMAT_VERSION,
        "axes": "polygon: local metres, x east, y north (as parcels.json); heights: metres, as terrain.json",
        "sample_step_m": SAMPLE_STEP_M,
        "bodies": bodies,
    }
    return doc, skipped
