"""Water bodies for the map artifact (``water.json``): one body per water parcel, water ground area or water patch.

The shooter swims in these (``ShooterCore.Water``). Each body has a flat surface and a natural bed (``bed.py``): a depth
grid over the outline, the depth growing from the banks, with pools and riffles along a river (see
:mod:`drycastle_pipeline.bed`). Heights come from the terrain of the map (the DMR surface, not rebased, like
``terrain.json``): the surface is the median terrain height along the shoreline (the parcel outline), or the
patch's ``water_y``. The terrain is then carved to the bed (:func:`bed.carve_terrain`), so the visible ground,
the walkable collision and the depth query agree. A parcel with no terrain under its outline gets no body.

``water.json`` version 2 (see :func:`water_doc`): per body ``surface_y``, ``bed_y`` (the lowest bed, for readers of
version 1), ``max_depth_m``, the depth grid, and ``flow`` (reserved, empty until flow is added).
"""

import math

import numpy as np
import shapely
from shapely import contains_xy
from shapely.geometry import mapping, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.bed import WaterBody, depth_grid
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import Terrain

WATER_FORMAT_VERSION = 2
SAMPLE_STEP_M = 2.0
WET_STEP_M = 1.0
WET_REACH_M = 8.0  # how far past the drawn outline the water may run to find its shoreline
MIN_PIECE_M2 = 1.0


def water_rules(rules_path=DEFAULT_RULES) -> dict:
    """The ``water`` section of rules.yaml (bed parameters, river ids, carve blend)."""
    return load_section("water", rules_path)


def shore_surface(geom, terrain: Terrain, step: float = SAMPLE_STEP_M) -> float | None:
    """The median terrain height along the outline of a water polygon, or None when the terrain does not cover it.

    ``geom`` is a shapely polygon in the local frame (x east, y north); ``terrain`` is a ``Terrain``.
    """
    ring = geom.exterior
    count = max(8, int(ring.length // step))
    shore = [ring.interpolate(d) for d in np.linspace(0.0, ring.length, count, endpoint=False)]
    sx = np.array([p.x for p in shore])
    sy = np.array([p.y for p in shore])
    on_shore = contains_xy(terrain.coverage, sx, sy)
    if not on_shore.any():
        return None
    minx, miny, maxx, maxy = geom.bounds
    gx, gy = np.meshgrid(np.arange(minx + step / 2, maxx, step), np.arange(miny + step / 2, maxy, step))
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(geom, gx, gy) & contains_xy(terrain.coverage, gx, gy)
    if not inside.any():
        return None
    return round(float(np.median(terrain.surface(sx[on_shore], sy[on_shore]))), 3)


def patch_bed(geom, terrain: Terrain, step: float = SAMPLE_STEP_M) -> float | None:
    """The lowest terrain height inside a polygon, or None when the terrain does not cover it."""
    minx, miny, maxx, maxy = geom.bounds
    gx, gy = np.meshgrid(np.arange(minx + step / 2, maxx, step), np.arange(miny + step / 2, maxy, step))
    gx, gy = gx.ravel(), gy.ravel()
    inside = contains_xy(geom, gx, gy) & contains_xy(terrain.coverage, gx, gy)
    if not inside.any():
        return None
    return round(float(np.min(terrain.surface(gx[inside], gy[inside]))), 3)


def wet_area(outline, terrain: Terrain, water_y: float, step: float = WET_STEP_M):
    """Where a flat water surface at ``water_y`` really lies: the terrain below it, reached from ``outline``.

    The drawn outline of a water patch is only a hint (traced on the coarse terrain grid); the shore is where the
    ground rises above the level. This samples the terrain on a ``step`` grid within ``WET_REACH_M`` of the outline,
    keeps the cells below ``water_y`` and returns the pieces that touch the outline (a pit that is not connected to it
    stays dry), smoothed a little so the shore is not a staircase. Empty when nothing there is below the level.
    """
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


def is_river(body_id: str, geom, rules: dict) -> bool:
    """Whether a body is a river: its ID is in ``river_ids``, or its outline is long and narrow.

    Only a property is a parcel; the water of the cadastre is a ground area whose ID (``A-NNNN``) is numbered by type
    and place and moves with the inputs, so an ID list alone cannot name the Morava. A body at least
    ``river_min_length_m`` long whose minimum rotated rectangle is at least ``river_min_aspect`` times as long as wide
    is a river too.
    """
    if body_id.partition(".")[0] in rules["river_ids"]:
        return True
    rect = geom.minimum_rotated_rectangle
    if rect.geom_type != "Polygon":
        return False
    pts = list(rect.exterior.coords)
    a = math.dist(pts[0], pts[1])
    b = math.dist(pts[1], pts[2])
    length, width = max(a, b), min(a, b)
    return length >= float(rules["river_min_length_m"]) and length >= float(rules["river_min_aspect"]) * max(width, 1e-6)


def _body(body_id: str, geom, surface: float, rules: dict, max_depth: float | None = None) -> WaterBody:
    river = is_river(body_id, geom, rules)
    params = rules["river"] if river else rules["pond"]
    grid = depth_grid(geom, body_id, params, river, rules["bed"], max_depth)
    return WaterBody(body_id, geom, surface, grid, river)


class WaterPlan:
    """The bodies of a map, before and after the terrain is carved (see :func:`plan_water`)."""

    def __init__(self) -> None:
        self.bodies: list[WaterBody] = []
        self.skipped: list[str] = []
        self.wet: dict[str, BaseGeometry] = {}  # patch ID -> the wet area (the patch's paint is draped over it)


def plan_water(
    parcels: list[dict],
    terrain: Terrain,
    patches: dict | None,
    rules: dict,
    kinds: frozenset[str] | set[str] = frozenset({"water"}),
) -> WaterPlan:
    """The water bodies of the ground units (parcels, ground areas) of a water kind (``kinds``) and of the ground patches with a ``water_y``.

    Must run on the terrain *before* it is carved: the surfaces are read off the shoreline and the wet areas off the
    ground. ``patches`` is ``plan_patches(...).patches`` (a patch of a water type); a patch's ``max_depth`` (when set)
    is its deepest point. A ground unit with no terrain under it, and a patch whose terrain is nowhere below its
    surface, get no body and are listed in ``skipped``.
    """
    plan = WaterPlan()
    for p in sorted(parcels, key=lambda p: p["id"]):
        if p["kind"] not in kinds:
            continue
        geom = shape(p["polygon"])
        surface = shore_surface(geom, terrain)
        if surface is None:
            plan.skipped.append(p["id"])
            continue
        plan.bodies.append(_body(p["id"], geom, surface, rules))
    for pid, patch in sorted((patches or {}).items()):
        if patch.water_y is None:
            continue
        wet = wet_area(patch.polygon, terrain, patch.water_y)
        plan.wet[pid] = wet
        pieces = [g for g in getattr(wet, "geoms", [wet]) if not g.is_empty and g.area > MIN_PIECE_M2]
        if not pieces:
            plan.skipped.append(pid)
            continue
        for k, g in enumerate(pieces, 1):
            body_id = pid if len(pieces) == 1 else f"{pid}.{k}"
            plan.bodies.append(_body(body_id, g, round(patch.water_y, 3), rules, patch.max_depth))
    return plan


def water_doc(bodies: list[WaterBody], rules: dict) -> dict:
    """The ``water.json`` document (version 2) of the bodies, sorted by ID."""
    out = []
    for b in sorted(bodies, key=lambda b: b.id):
        g = b.grid
        out.append(
            {
                "id": b.id,
                "polygon": mapping(b.geom),
                "surface_y": round(b.surface_y, 3),
                "bed_y": b.bed_y,
                "max_depth_m": round(b.max_depth_m, 2),
                "depth": {
                    "origin": [round(g.x0, 3), round(g.y0, 3)],
                    "cell_m": g.cell_m,
                    "cols": g.cols,
                    "rows": g.rows,
                    "cm": g.encode(),
                },
                "flow": [],
            }
        )
    return {
        "version": WATER_FORMAT_VERSION,
        "axes": (
            "polygon: local metres, x east, y north (as parcels.json); heights: metres, as terrain.json. "
            "depth.origin: the south-west corner of the grid (local metres); depth.cm: base64 of little-endian uint16 "
            "centimetres, rows from the south (origin) row, columns west to east; cell centres at origin + (i + 0.5) * cell_m; "
            "0 outside the outline. bed_y is the lowest bed (surface_y - max_depth_m). flow: reserved (empty)."
        ),
        "sample_step_m": rules["bed"]["cell_m"],
        "bodies": out,
    }
