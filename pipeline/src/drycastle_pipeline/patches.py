"""Ground patches: the preview meshes of hand-drawn ground (``ground_patches`` in ``edits.json``).

A ground patch is a polygon of exactly one ground type (ADR 0022). In the baked map it is ground: the parcels stage turns
its polygon into a ground area of its type (``ground.py``), so terrain colour, detail and trees follow on the next full
run. Between two runs the editor shows it as a **preview**: one flat-coloured mesh in the type's first colour over the
terrain and the road ribbons (no collision, nothing else is generated or moved). The meshes are built from the work files
by ``patch-run`` and, for patches of a water type, at export, with the same code, so a hot-reloaded patch equals the baked
one.

* **Exact edges, draped.** The polygon is cut along the terrain mesh (every grid cell is split
  into the two triangles the terrain mesh uses, see :meth:`Terrain.surface`), so inside each piece
  the ground is one plane. Each piece is triangulated with its own corners only, and every vertex
  takes the terrain height plus a small lift: the patch's boundary is the drawn polygon, and its
  surface follows the terrain triangle by triangle.
* **Roads.** Where the polygon covers a road ribbon (or junction patch) the piece is further cut
  along the ribbon outline and lifted above the ribbon, whose height is the terrain plus the road
  lift of its class (see :func:`road_layers`). Buildings, trees, fences and placed objects are
  ignored: the patch just paints the ground under them.
* **Overlaps: the higher ID wins.** The visible area of a patch is its polygon minus the polygons
  of all patches with a higher ID, cut in 2D before draping. There is never more than one patch
  on a point, so nothing z-fights and the order needs no per-ID lift.
* **Water.** A patch of a water type has a ``water_y``: it is a body of water. Its mesh is flat at that height instead
  of draped, and covers the terrain below that level reached from the drawn outline (:func:`water.wet_area`:
  the outline is only a hint, the shore is where the ground rises above the level; overlaps with other
  patches are not resolved for water), and the export
  also writes it to ``water.json`` (see :func:`water.build_water`). The flat mesh sits
  ``WATER_VISUAL_DROP_M`` under the surface, so the shooter's own water surface, drawn at
  ``water_y``, always wins and nothing z-fights. A water patch is the only kind baked into the chunk files as
  ``patch_<ID>`` nodes; the other patches are baked as ground.
* Entries that fail the drape (no terrain, polygon outside the terrain coverage) are warned about
  and skipped; a schema error fails the file as for every other edit kind (see ``edits.py``).
"""

import math
from dataclasses import dataclass, field

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.edits import Edits, polygon_ring
from drycastle_pipeline.ground_attributes import attributes_of
from drycastle_pipeline.meshes import Mesh, triangulate
from drycastle_pipeline.roads import road_mesh
from drycastle_pipeline.terrain import Terrain
from drycastle_pipeline.water import wet_area

_MIN_AREA_M2 = 1e-6
_RANK_STEP_M = 0.003  # roads.py lifts each higher road class by this much
_ROAD_MARGIN_M = 1.0
WATER_VISUAL_DROP_M = 0.02


FALLBACK_COLOUR = "#808080"  # a type that carries no colours


@dataclass
class Patch:
    id: str
    type: str
    colour: list[int]  # the first terrain colour of the type
    polygon: Polygon  # local metres (x east, y north), clipped to the terrain coverage
    water_y: float | None = None  # flat water surface height (terrain datum), None: ground


@dataclass
class PatchPlan:
    """Edits resolved against this run's terrain (see :func:`plan_patches`)."""

    patches: dict[str, Patch] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)


def _polygons(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom] if geom.area > _MIN_AREA_M2 else []
    return [p for g in getattr(geom, "geoms", []) for p in _polygons(g)]


def type_colour(spec: dict | None) -> str:
    """The first terrain colour of a ground type (``#rrggbb``), grey when it has none."""
    return (attributes_of(spec).get("colours") or [FALLBACK_COLOUR])[0]


def plan_patches(
    edits: Edits,
    origin: list[float] | None,
    terrain: Terrain | None,
    types: dict[str, dict],
) -> PatchPlan:
    """Resolve the ground patches of the edits: local frame, clipped to the terrain coverage, in the colour of their type.

    A patch without terrain under it (none at all, or outside the coverage) is skipped with a warning, and so is one
    whose type is not in ``types``. Parts outside the coverage are cut off silently, like road ribbons.
    """
    plan = PatchPlan()
    if not edits.ground_patches:
        return plan
    if origin is None:
        raise ValueError("parcels.json has no origin_epsg5514; run the parcels stage again")
    for pid in sorted(edits.ground_patches, key=patch_rank):
        entry = edits.ground_patches[pid]
        if entry["type"] not in types:
            plan.warnings.append(f"ground_patches.{pid}: unknown ground type {entry['type']!r}; patch skipped")
            continue
        if terrain is None:
            plan.warnings.append(f"ground_patches.{pid}: no terrain; patch skipped")
            continue
        ring = [(x - origin[0], y - origin[1]) for x, y in polygon_ring(entry["polygon"])]
        clipped = Polygon(ring).buffer(0).intersection(terrain.coverage)
        pieces = _polygons(clipped)
        if not pieces:
            plan.warnings.append(f"ground_patches.{pid}: outside the terrain coverage; patch skipped")
            continue
        poly = pieces[0] if len(pieces) == 1 else MultiPolygon(pieces)
        plan.patches[pid] = Patch(pid, entry["type"], hex_to_rgb(type_colour(types[entry["type"]])), poly, entry.get("water_y"))
    return plan


def patch_rank(pid: str) -> tuple[int, str]:
    """Sort key for "higher ID wins": numeric, so GP-10000 beats GP-9999 (IDs have 4+ digits)."""
    tail = pid[3:] if pid.startswith("GP-") else ""
    return (int(tail), pid) if tail.isdigit() else (-1, pid)


def visible_area(plan: PatchPlan, pid: str) -> BaseGeometry:
    """The polygon of ``pid`` minus every patch with a higher ID (the higher ID wins)."""
    geom = plan.patches[pid].polygon
    higher = [r.polygon for other, r in plan.patches.items() if patch_rank(other) > patch_rank(pid)]
    if higher:
        geom = geom.difference(shapely.union_all(higher))
    return geom


def overlapping(plan: PatchPlan, ids: set[str]) -> set[str]:
    """``ids`` plus every patch sharing area with one of them: their visible area depends on it."""
    out = set(ids)
    for pid in ids & set(plan.patches):
        poly = plan.patches[pid].polygon
        out |= {
            o for o, r in plan.patches.items() if r.polygon.intersection(poly).area > _MIN_AREA_M2
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


def patch_meshes(
    plan: PatchPlan,
    terrain: Terrain,
    graph: dict | None,
    roads_cfg: dict,
    pcfg: dict,
    only: set[str] | None = None,
) -> dict[str, Mesh]:
    """``{patch ID: mesh}`` for the planned patches (``only``: just these), empty ones omitted."""
    out: dict[str, Mesh] = {}
    for pid in sorted(plan.patches if only is None else only & set(plan.patches)):
        geom = visible_area(plan, pid)
        if geom.is_empty:
            continue
        water_y = plan.patches[pid].water_y
        if water_y is not None:
            # the drawn outline is only a hint: the water lies wherever the terrain is below its level
            wet = wet_area(plan.patches[pid].polygon, terrain, water_y)
            mesh = flat_mesh(wet, plan.patches[pid].colour, water_y - WATER_VISUAL_DROP_M)
            if mesh.face_count:
                out[pid] = mesh
            continue
        layers = road_layers(graph, terrain, roads_cfg, geom.bounds) if graph else []
        mesh = drape(
            geom,
            plan.patches[pid].colour,
            terrain,
            layers,
            float(pcfg["lift_m"]),
            float(pcfg["road_lift_m"]),
        )
        if mesh.face_count:
            out[pid] = mesh
    return out


def node_name(pid: str) -> str:
    return f"patch_{pid}"
