"""Regions: hand-drawn colour shapes draped on the ground (``regions`` in ``edits.json``).

A region is one flat-coloured mesh over the terrain and the road ribbons; it changes only colour
(no collision, nothing else is generated or moved). Meshes are built from the work files at
export and by ``region-run``, with the same code, so a hot-reloaded region equals the baked one.

* **Exact edges, draped.** The polygon is cut along the terrain mesh (every grid cell is split
  into the two triangles the terrain mesh uses, see :meth:`Terrain.surface`), so inside each piece
  the ground is one plane. Each piece is triangulated with its own corners only, and every vertex
  takes the terrain height plus a small lift: the region's boundary is the drawn polygon, and its
  surface follows the terrain triangle by triangle.
* **Roads.** Where the polygon covers a road ribbon (or junction patch) the piece is further cut
  along the ribbon outline and lifted above the ribbon, whose height is the terrain plus the road
  lift of its class (see :func:`road_layers`). Buildings, trees, fences and placed objects are
  ignored: the region just paints the ground under them.
* **Overlaps: the higher ID wins.** The visible area of a region is its polygon minus the polygons
  of all regions with a higher ID, cut in 2D before draping. There is never more than one region
  on a point, so nothing z-fights and the order needs no per-ID lift.
* **Water.** A region with ``water_y`` is a body of water: its mesh is flat at that height instead
  of draped (the ground simply hides the parts of it that lie under the terrain), and the export
  also writes it to ``water.json`` (see :func:`water.build_water`). The flat mesh sits
  ``WATER_VISUAL_DROP_M`` under the surface, so the shooter's own water surface, drawn at
  ``water_y``, always wins and nothing z-fights.
* Entries that fail the drape (no terrain, polygon outside the terrain coverage) are warned about
  and skipped; a schema error fails the file as for every other edit kind (see ``edits.py``).
"""

import math
from dataclasses import dataclass, field

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.edits import Edits, region_ring
from suchohrad_pipeline.meshes import Mesh, triangulate
from suchohrad_pipeline.roads import road_mesh
from suchohrad_pipeline.terrain import Terrain

_MIN_AREA_M2 = 1e-6
_RANK_STEP_M = 0.003  # roads.py lifts each higher road class by this much
_ROAD_MARGIN_M = 1.0
WATER_VISUAL_DROP_M = 0.02


@dataclass
class Region:
    id: str
    colour: list[int]
    polygon: Polygon  # local metres (x east, y north), clipped to the terrain coverage
    water_y: float | None = None  # flat water surface height (terrain datum), None: ground paint


@dataclass
class RegionPlan:
    """Edits resolved against this run's terrain (see :func:`plan_regions`)."""

    regions: dict[str, Region] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)


def _polygons(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom] if geom.area > _MIN_AREA_M2 else []
    return [p for g in getattr(geom, "geoms", []) for p in _polygons(g)]


def plan_regions(edits: Edits, origin: list[float] | None, terrain: Terrain | None) -> RegionPlan:
    """Resolve the regions of the edits: local frame, clipped to the terrain coverage.

    A region without terrain under it (none at all, or outside the coverage) is skipped with a
    warning. Parts outside the coverage are cut off silently, like road ribbons.
    """
    plan = RegionPlan()
    if not edits.regions:
        return plan
    if origin is None:
        raise ValueError("parcels.json has no origin_epsg5514; run the parcels stage again")
    for rid in sorted(edits.regions):
        entry = edits.regions[rid]
        if terrain is None:
            plan.warnings.append(f"regions.{rid}: no terrain; region skipped")
            continue
        ring = [(x - origin[0], y - origin[1]) for x, y in region_ring(entry["polygon"])]
        clipped = Polygon(ring).buffer(0).intersection(terrain.coverage)
        pieces = _polygons(clipped)
        if not pieces:
            plan.warnings.append(f"regions.{rid}: outside the terrain coverage; region skipped")
            continue
        poly = pieces[0] if len(pieces) == 1 else MultiPolygon(pieces)
        plan.regions[rid] = Region(rid, hex_to_rgb(entry["colour"]), poly, entry.get("water_y"))
    return plan


def region_rank(rid: str) -> tuple[int, str]:
    """Sort key for "higher ID wins": numeric, so R-10000 beats R-9999 (IDs have 4+ digits)."""
    tail = rid[2:] if rid.startswith("R-") else ""
    return (int(tail), rid) if tail.isdigit() else (-1, rid)


def visible_area(plan: RegionPlan, rid: str) -> BaseGeometry:
    """The polygon of ``rid`` minus every region with a higher ID (the higher ID wins)."""
    geom = plan.regions[rid].polygon
    higher = [r.polygon for other, r in plan.regions.items() if region_rank(other) > region_rank(rid)]
    if higher:
        geom = geom.difference(shapely.union_all(higher))
    return geom


def overlapping(plan: RegionPlan, ids: set[str]) -> set[str]:
    """``ids`` plus every region sharing area with one of them: their visible area depends on it."""
    out = set(ids)
    for rid in ids & set(plan.regions):
        poly = plan.regions[rid].polygon
        out |= {
            o for o, r in plan.regions.items() if r.polygon.intersection(poly).area > _MIN_AREA_M2
        }
    return out


# --- ground ---------------------------------------------------------------------------------


def road_layers(
    graph: dict, terrain: Terrain, roads_cfg: dict, bounds: tuple[float, float, float, float]
) -> list[tuple[BaseGeometry, float]]:
    """Outlines of the road ribbons near ``bounds`` by height above the terrain, highest first.

    Ribbons of one road class float ``lift_m + rank * 0.003`` above the terrain (see
    :func:`roads.road_mesh`); the result has one outline (union of the triangles, ribbons and
    junction patches) per class lift found.
    """
    minx, miny, maxx, maxy = bounds
    edges = []
    for e in graph["edges"]:
        pts = np.asarray(e["polyline"], float)
        m = e["width"] / 2 + _ROAD_MARGIN_M
        if (
            pts[:, 0].max() + m >= minx
            and pts[:, 0].min() - m <= maxx
            and pts[:, 1].max() + m >= miny
            and pts[:, 1].min() - m <= maxy
        ):
            edges.append(e)
    if not edges:
        return []
    # junction patches depend on every edge at a node, so they come from the full graph
    mesh = road_mesh(graph, terrain, roads_cfg, only={e["id"] for e in edges})
    if not mesh.face_count:
        return []
    base, ranks = float(roads_cfg["lift_m"]), len(roads_cfg["order"])
    c = mesh.tris.mean(axis=1)
    lift = c[:, 2] - terrain.surface(c[:, 0], c[:, 1])
    rank = np.clip(np.round((lift - base) / _RANK_STEP_M), 0, ranks).astype(int)
    out = []
    for k in sorted(set(rank.tolist()), reverse=True):
        polys = shapely.polygons(mesh.tris[rank == k][:, :, :2])
        polys = polys[shapely.area(polys) > 1e-9]
        if len(polys):
            out.append((shapely.union_all(polys), base + k * _RANK_STEP_M))
    return out


def _terrain_triangles(terrain: Terrain, geom: BaseGeometry) -> list[Polygon]:
    """The terrain mesh triangles (xy) that may overlap ``geom``, in the mesh's own split."""
    minx, miny, maxx, maxy = geom.bounds
    s = terrain.step
    ci0 = max(0, math.floor(minx / s) - terrain.i0)
    ci1 = min(terrain.ncols - 1, math.floor(maxx / s) - terrain.i0)
    cj0 = max(0, math.floor(miny / s) - terrain.j0)
    cj1 = min(terrain.nrows - 1, math.floor(maxy / s) - terrain.j0)
    if ci1 < ci0 or cj1 < cj0:
        return []
    ci, cj = np.meshgrid(np.arange(ci0, ci1 + 1), np.arange(cj0, cj1 + 1))
    ci, cj = ci.ravel(), cj.ravel()
    x0, y0 = (terrain.i0 + ci) * s, (terrain.j0 + cj) * s
    shapely.prepare(geom)
    near = shapely.intersects(geom, shapely.box(x0, y0, x0 + s, y0 + s))
    v = terrain.valid  # cells chunk_mesh does not mesh (any invalid corner) get no triangles
    near &= v[cj, ci] & v[cj, ci + 1] & v[cj + 1, ci] & v[cj + 1, ci + 1]
    h = terrain.heights
    tris: list[Polygon] = []
    for i, j, x, y in zip(ci[near], cj[near], x0[near], y0[near], strict=True):
        v00, v10, v01, v11 = (x, y), (x + s, y), (x, y + s), (x + s, y + s)
        # the diagonal choice of Terrain.chunk_mesh
        if abs(h[j, i + 1] - h[j + 1, i]) < abs(h[j, i] - h[j + 1, i + 1]):
            tris += [Polygon([v00, v10, v01]), Polygon([v10, v11, v01])]
        else:
            tris += [Polygon([v00, v10, v11]), Polygon([v00, v11, v01])]
    return tris


def drape(
    geom: BaseGeometry,
    colour: list[int],
    terrain: Terrain,
    layers: list[tuple[BaseGeometry, float]],
    lift: float,
    road_lift: float,
) -> Mesh:
    """Flat-coloured triangles covering ``geom`` on the ground, counter-clockwise from above.

    ``lift`` is the height above the terrain; over a road layer (outline, its lift above the
    terrain) the height is the layer's lift plus ``road_lift`` instead.
    """
    if geom.is_empty:
        return Mesh.empty()
    # 2D triangles and their lift, collected first: the terrain height is then read in one call for all of them
    flat: list[np.ndarray] = []
    aboves: list[float] = []

    def emit(piece: BaseGeometry, above: float) -> None:
        for poly in _polygons(piece):
            for tri in triangulate(poly):
                flat.append(np.array(tri, float))
                aboves.append(above)

    for cell in _terrain_triangles(terrain, geom):
        piece = geom.intersection(cell)
        for outline, road_top in layers:
            if piece.is_empty:
                break
            if piece.intersects(outline):
                emit(piece.intersection(outline), road_top + road_lift)
                piece = piece.difference(outline)
        emit(piece, lift)
    if not flat:
        return Mesh.empty()
    xy = np.stack(flat)  # (n, 3, 2)
    n = len(flat)
    ground = terrain.surface(xy[:, :, 0].ravel(), xy[:, :, 1].ravel()).reshape(n, 3)
    v = np.concatenate([xy, (ground + np.array(aboves)[:, None])[:, :, None]], axis=2)  # (n, 3, 3)
    # the orientation and the sliver test read only the x and y components, so they are the same per triangle
    e1, e2 = v[:, 1] - v[:, 0], v[:, 2] - v[:, 0]
    cz = np.cross(e1, e2)[:, 2]
    flip = cz < 0
    v[flip] = v[flip][:, [0, 2, 1]]
    keep = np.abs(cz) > 1e-12
    tris = v[keep]
    if len(tris) == 0:
        return Mesh.empty()
    return Mesh(tris, np.tile(np.array(colour, np.uint8), (len(tris), 1)))


def flat_mesh(geom: BaseGeometry, colour: list[int], z: float) -> Mesh:
    """Flat-coloured triangles covering ``geom`` at height ``z``, counter-clockwise from above."""
    tris = []
    for poly in _polygons(geom):
        for tri in triangulate(poly):
            a, b, c = (np.array([x, y, z], float) for x, y in tri)
            if np.cross(b - a, c - a)[2] < 0:
                b, c = c, b
            tris.append([a, b, c])
    if not tris:
        return Mesh.empty()
    return Mesh(np.array(tris), np.tile(np.array(colour, np.uint8), (len(tris), 1)))


def region_meshes(
    plan: RegionPlan,
    terrain: Terrain,
    graph: dict | None,
    roads_cfg: dict,
    rcfg: dict,
    only: set[str] | None = None,
) -> dict[str, Mesh]:
    """``{region ID: mesh}`` for the planned regions (``only``: just these), empty ones omitted."""
    out: dict[str, Mesh] = {}
    for rid in sorted(plan.regions if only is None else only & set(plan.regions)):
        geom = visible_area(plan, rid)
        if geom.is_empty:
            continue
        water_y = plan.regions[rid].water_y
        if water_y is not None:
            mesh = flat_mesh(geom, plan.regions[rid].colour, water_y - WATER_VISUAL_DROP_M)
            if mesh.face_count:
                out[rid] = mesh
            continue
        layers = road_layers(graph, terrain, roads_cfg, geom.bounds) if graph else []
        mesh = drape(
            geom,
            plan.regions[rid].colour,
            terrain,
            layers,
            float(rcfg["lift_m"]),
            float(rcfg["road_lift_m"]),
        )
        if mesh.face_count:
            out[rid] = mesh
    return out


def node_name(rid: str) -> str:
    return f"region_{rid}"
