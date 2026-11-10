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
from shapely.geometry import LineString

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.config import DEFAULT_AREA, area_paths
from suchohrad_pipeline.meshes import Mesh
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


def ribbon_mesh(
    pts: np.ndarray, width: float, terrain: Terrain, lift: float, colour: list[int], step: float
) -> Mesh:
    """Triangle strip draped on the terrain surface (counter-clockwise seen from above)."""
    pts = resample(np.asarray(pts, float), step)
    left, right = ribbon_sides(pts, width)
    mid = pts
    rows = []
    for side in (right, mid, left):
        z = terrain.surface(side[:, 0], side[:, 1]) + lift
        rows.append(np.column_stack([side, z]))
    # Three vertices across (right, centre, left) so the strip follows a kink under the middle.
    tris = []
    for k in range(len(pts) - 1):
        for a, b in ((0, 1), (1, 2)):
            tris.append([rows[a][k], rows[a][k + 1], rows[b][k + 1]])
            tris.append([rows[a][k], rows[b][k + 1], rows[b][k]])
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


def road_mesh(graph: dict, terrain: Terrain, cfg: dict) -> Mesh:
    """All ribbons and junction patches of the graph, clipped to the terrain coverage."""
    order = {hw: i for i, hw in enumerate(cfg["order"])}
    lift, step = float(cfg["lift_m"]), float(cfg["sample_step_m"])
    meshes = []
    node_best: dict[str, tuple[float, int, list[int]]] = {}
    nodes = {n["id"]: n for n in graph["nodes"]}
    deg: dict[str, int] = {}
    for e in sorted(graph["edges"], key=lambda e: (order.get(e["highway"], 0), e["id"])):
        rank = order.get(e["highway"], 0)
        z = lift + rank * 0.003
        colour = road_colour(e["highway"], e["surface"], cfg)
        meshes.append(ribbon_mesh(np.array(e["polyline"]), e["width"], terrain, z, colour, step))
        for end in (e["from"], e["to"]):
            deg[end] = deg.get(end, 0) + 1
            if end not in node_best or (e["width"], rank) > node_best[end][:2]:
                node_best[end] = (e["width"], rank, colour)
    for nid in sorted(node_best):
        if deg[nid] >= 2:
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
