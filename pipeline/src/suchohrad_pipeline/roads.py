"""Roads stage: OSM highways -> ``data/work/roads.json`` (graph) and ribbon meshes draped on terrain.

Graph: nodes are way ends and vertices shared by two ways; a way is split into edges at nodes.
Coordinates are local metres (x east, y north). Ribbons are built at export from the graph and
the terrain (``road_mesh``).
"""

import json
import math
from itertools import pairwise
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import LineString, Polygon

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.config import DEFAULT_AREA, area_paths
from suchohrad_pipeline.meshes import Mesh, _polygons, triangulate
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section
from suchohrad_pipeline.terrain import Terrain

ROADS_FORMAT_VERSION = 1
_TRUE = {"yes", "true", "1"}
_MITER_LIMIT = 0.5  # min cos of the half angle at a bend; keeps ribbons from spiking
_KINK_RAD = math.radians(8)


def road_width(tags: dict, highway: str, cfg: dict) -> float | None:
    """Width in metres: OSM width tag, else lanes * lane width, else the class default."""
    widths = cfg["widths"]
    if highway not in widths:
        return None
    try:
        w = float(str(tags.get("width", "")).replace(",", ".").split()[0])
        if w > 0:
            return round(w, 2)
    except (ValueError, IndexError):
        pass
    try:
        lanes = int(tags.get("lanes", ""))
        if lanes > 0 and highway not in ("path", "footway", "cycleway", "track"):
            return round(max(float(widths[highway]), lanes * float(cfg["lane_width_m"])), 2)
    except ValueError:
        pass
    return float(widths[highway])


def is_unpaved(highway: str, surface: str | None, cfg: dict) -> bool:
    if surface:
        return surface in cfg["unpaved_surfaces"]
    return highway in cfg["unpaved_by_default"]


def road_colour(highway: str, surface: str | None, cfg: dict) -> list[int]:
    colours = cfg["colours"]
    if is_unpaved(highway, surface, cfg):
        return hex_to_rgb(colours["path"] if highway in ("path", "footway") else colours["gravel"])
    return hex_to_rgb(colours["asphalt"])


def build_graph(features: list[dict], cfg: dict) -> dict:
    """Road graph from normalised highway features (GeoJSON-like dicts with LineStrings)."""
    ways = []
    for f in sorted(features, key=lambda f: f["properties"]["osm_id"]):
        props = f["properties"]
        hw = props["highway"]
        width = road_width(props, hw, cfg)
        if width is None or f["geometry"]["type"] != "LineString":
            continue
        pts = [tuple(round(c, 3) for c in p[:2]) for p in f["geometry"]["coordinates"]]
        pts = [p for i, p in enumerate(pts) if i == 0 or p != pts[i - 1]]
        if len(pts) < 2:
            continue
        oneway = str(props.get("oneway", "")).lower()
        if oneway == "-1":
            pts.reverse()
        ways.append((props["osm_id"], hw, width, oneway in _TRUE or oneway == "-1", props, pts))

    uses: dict[tuple, int] = {}
    for *_, pts in ways:
        for p in pts:
            uses[p] = uses.get(p, 0) + 1
    node_pts = set()
    for *_, pts in ways:
        node_pts.update({pts[0], pts[-1]})
        node_pts.update(p for p in pts if uses[p] > 1)
    node_ids = {p: f"N-{i:04d}" for i, p in enumerate(sorted(node_pts), 1)}

    edges = []
    for osm_id, hw, width, oneway, props, pts in ways:
        start = 0
        for i in range(1, len(pts)):
            if pts[i] in node_ids and (i > start):
                seg = pts[start : i + 1]
                length = float(sum(math.dist(a, b) for a, b in pairwise(seg)))
                edges.append(
                    {
                        "osm_id": osm_id,
                        "from": node_ids[seg[0]],
                        "to": node_ids[seg[-1]],
                        "polyline": [list(p) for p in seg],
                        "highway": hw,
                        "width": width,
                        "oneway": oneway,
                        "surface": props.get("surface"),
                        "length_m": round(length, 2),
                    }
                )
                start = i
    for i, e in enumerate(edges, 1):
        e["id"] = f"E-{i:04d}"
    nodes = [
        {"id": nid, "x": p[0], "y": p[1]}
        for p, nid in sorted(node_ids.items(), key=lambda kv: kv[1])
    ]
    return {
        "version": ROADS_FORMAT_VERSION,
        "axes": "local metres, x east, y north",
        "nodes": nodes,
        "edges": edges,
    }


# --- meshes ---------------------------------------------------------------------------------


def resample(pts: np.ndarray, step: float) -> np.ndarray:
    """Insert points so no segment is longer than ``step``."""
    out = [pts[0]]
    for a, b in pairwise(pts):
        k = max(1, math.ceil(math.dist(a, b) / step))
        out.extend(a + (b - a) * (i / k) for i in range(1, k + 1))
    return np.array(out)


def ribbon_sides(pts: np.ndarray, width: float) -> tuple[np.ndarray, np.ndarray]:
    """Left and right edge points of a ribbon of the given width around a polyline."""
    d = np.diff(pts, axis=0)
    d /= np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-12)
    normals = np.stack([-d[:, 1], d[:, 0]], axis=1)
    off = np.empty_like(pts)
    off[0], off[-1] = normals[0], normals[-1]
    for i in range(1, len(pts) - 1):
        m = normals[i - 1] + normals[i]
        norm = np.linalg.norm(m)
        if norm < 1e-9:
            off[i] = normals[i]
            continue
        m /= norm
        off[i] = m / max(float(m @ normals[i]), _MITER_LIMIT)
    half = width / 2
    return pts + off * half, pts - off * half


def _terrain_triangles(terrain: Terrain, bounds: tuple[float, float, float, float], area=None):
    """Terrain mesh triangles (3x3 vertex arrays) of the cells touching ``bounds``.

    With ``area`` (a geometry, prepared here) only the cells that touch it are yielded; a triangle
    of a cell that misses ``area`` would clip to nothing, so the result for ``area`` is the same.
    Cells are split along the same diagonal as in ``Terrain.chunk_mesh`` / ``Terrain.surface``.
    """
    st, h = terrain.step, terrain.heights
    c0 = max(int(math.floor(bounds[0] / st)) - terrain.i0, 0)
    c1 = min(int(math.floor(bounds[2] / st)) - terrain.i0, terrain.ncols - 1)
    r0 = max(int(math.floor(bounds[1] / st)) - terrain.j0, 0)
    r1 = min(int(math.floor(bounds[3] / st)) - terrain.j0, terrain.nrows - 1)
    if area is not None and r1 >= r0 and c1 >= c0:
        shapely.prepare(area)
        cj_all, ci_all = np.meshgrid(np.arange(r0, r1 + 1), np.arange(c0, c1 + 1), indexing="ij")
        cj_all, ci_all = cj_all.ravel(), ci_all.ravel()  # row-major: the same order as the loops below
        x_all, y_all = (terrain.i0 + ci_all) * st, (terrain.j0 + cj_all) * st
        touch = shapely.intersects(area, shapely.box(x_all, y_all, x_all + st, y_all + st))
        cells = list(zip(cj_all[touch].tolist(), ci_all[touch].tolist()))
    else:
        cells = [(cj, ci) for cj in range(r0, r1 + 1) for ci in range(c0, c1 + 1)]
    for cj, ci in cells:
        if not terrain.valid[cj : cj + 2, ci : ci + 2].all():
            continue  # no terrain mesh here
        x, y = (terrain.i0 + ci) * st, (terrain.j0 + cj) * st
        v00 = (x, y, h[cj, ci])
        v10 = (x + st, y, h[cj, ci + 1])
        v01 = (x, y + st, h[cj + 1, ci])
        v11 = (x + st, y + st, h[cj + 1, ci + 1])
        if abs(v10[2] - v01[2]) < abs(v00[2] - v11[2]):
            yield np.array([v00, v10, v01])
            yield np.array([v10, v11, v01])
        else:
            yield np.array([v00, v10, v11])
            yield np.array([v00, v11, v01])


def _area2(t) -> float:
    (x0, y0), (x1, y1), (x2, y2) = t
    return (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)


def _area2(t) -> float:
    (x0, y0), (x1, y1), (x2, y2) = t
    return (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)


def ribbon_mesh(
    pts: np.ndarray, width: float, terrain: Terrain, lift: float, colour: list[int], step: float
) -> Mesh:
    """Ribbon laid exactly on the terrain surface (counter-clockwise seen from above).

    The ribbon footprint is clipped against every terrain triangle it touches, and each piece
    lies in that triangle's plane (plus ``lift``). The ribbon therefore has vertices on every
    terrain edge it crosses and cannot be cut through by a terrain crease between its samples.
    """
    pts = resample(np.asarray(pts, float), step)
    left, right = ribbon_sides(pts, width)
    quads = [
        Polygon([left[k], left[k + 1], right[k + 1], right[k]]) for k in range(len(pts) - 1)
    ]
    foot = shapely.unary_union([q.buffer(0) for q in quads if q.area > 1e-9])
    tris = []
    for tri in _terrain_triangles(terrain, foot.bounds, foot):
        piece = foot.intersection(Polygon(tri[:, :2]))
        if piece.is_empty:
            continue
        a, b, c = tri
        det = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
        if abs(det) < 1e-12:
            continue
        # plane z = a.z + gx * (x - a.x) + gy * (y - a.y)
        gx = ((b[2] - a[2]) * (c[1] - a[1]) - (c[2] - a[2]) * (b[1] - a[1])) / det
        gy = ((c[2] - a[2]) * (b[0] - a[0]) - (b[2] - a[2]) * (c[0] - a[0])) / det
        for poly in _polygons(piece):
            for t in triangulate(poly):
                if abs(_area2(t)) < 2e-6:
                    continue  # sliver from clipping along a terrain edge
                if abs(_area2(t)) < 2e-6:
                    continue  # sliver from clipping along a terrain edge
                v = np.array([(x, y, a[2] + gx * (x - a[0]) + gy * (y - a[1]) + lift) for x, y in t])
                if (v[1, 0] - v[0, 0]) * (v[2, 1] - v[0, 1]) - (v[2, 0] - v[0, 0]) * (
                    v[1, 1] - v[0, 1]
                ) < 0:
                    v = v[[0, 2, 1]]
                tris.append(v)
    if not tris:
        return Mesh.empty()
    return Mesh(np.array(tris), np.tile(np.array(colour, np.uint8), (len(tris), 1)))


def junction_mesh(
    centre: tuple[float, float], radius: float, terrain: Terrain, lift: float, colour: list[int]
) -> Mesh:
    """Octagon patch closing the gap where ribbons meet at a bend."""
    angles = [2 * math.pi * k / 8 for k in range(9)]
    ring = np.array(
        [(centre[0] + radius * math.cos(a), centre[1] + radius * math.sin(a)) for a in angles]
    )
    z = terrain.surface(ring[:, 0], ring[:, 1]) + lift
    zc = float(terrain.surface(np.array([centre[0]]), np.array([centre[1]]))[0]) + lift
    tris = [[(*centre, zc), (*ring[k], z[k]), (*ring[k + 1], z[k + 1])] for k in range(8)]
    return Mesh(np.array(tris), np.tile(np.array(colour, np.uint8), (8, 1)))


def road_mesh(graph: dict, terrain: Terrain, cfg: dict, only: set[str] | None = None) -> Mesh:
    """All ribbons and junction patches of the graph, clipped to the terrain coverage.

    ``only`` limits the output to those edge IDs (and the junctions they touch); junction
    patches are still derived from every edge of the graph.
    """
    order = {hw: i for i, hw in enumerate(cfg["order"])}
    lift, step = float(cfg["lift_m"]), float(cfg["sample_step_m"])
    meshes = []
    node_best: dict[str, tuple[float, int, list[int]]] = {}
    nodes = {n["id"]: n for n in graph["nodes"]}
    deg: dict[str, int] = {}
    touched: set[str] = set()
    for e in sorted(graph["edges"], key=lambda e: (order.get(e["highway"], 0), e["id"])):
        rank = order.get(e["highway"], 0)
        z = lift + rank * 0.003
        colour = road_colour(e["highway"], e["surface"], cfg)
        keep = only is None or e["id"] in only
        if keep:
            meshes.append(ribbon_mesh(np.array(e["polyline"]), e["width"], terrain, z, colour, step))
            touched.update((e["from"], e["to"]))
        for end in (e["from"], e["to"]):
            deg[end] = deg.get(end, 0) + 1
            if end not in node_best or (e["width"], rank) > node_best[end][:2]:
                node_best[end] = (e["width"], rank, colour)
    for nid in sorted(node_best):
        if deg[nid] >= 2 and nid in touched:
            w, rank, colour = node_best[nid]
            n = nodes[nid]
            meshes.append(
                junction_mesh((n["x"], n["y"]), w / 2, terrain, lift + rank * 0.003, colour)
            )
    mesh = Mesh.concat(meshes)
    if not mesh.face_count:
        return mesh
    c = mesh.tris.mean(axis=1)
    inside = shapely.contains_xy(terrain.coverage, c[:, 0], c[:, 1])
    return Mesh(mesh.tris[inside], mesh.colours[inside])


def read_highways(work_dir: Path) -> list[dict]:
    return json.loads((work_dir / "normalised" / "highways.geojson").read_text())["features"]


def run(force: bool = False, area: str = DEFAULT_AREA, **_options: object) -> int:
    work_dir = area_paths(area).work
    try:
        graph = build_graph(read_highways(work_dir), load_section("roads", DEFAULT_RULES))
    except FileNotFoundError as exc:
        print(f"roads: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    (work_dir / "roads.json").write_text(json.dumps(graph, indent=1, sort_keys=True) + "\n")
    print(f"roads: {len(graph['nodes'])} nodes, {len(graph['edges'])} edges")
    return 0


def edge_lines(graph: dict) -> list[tuple[dict, LineString]]:
    return [(e, LineString(e["polyline"])) for e in graph["edges"]]
