"""The map's work files as shapely geometry, and the placement frame of a parcel."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import shapely
from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import LinearRing, LineString, Point, Polygon, shape
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.mcp.ids import PARCEL_ID
from drycastle_pipeline.roads.roads import edge_lines

EDGE_MERGE_DEG = 8.0  # consecutive polygon edges turning less than this form one "side"
MIN_FRONT_SIDE_M = 2.0
ROAD_FAR_M = 30.0


def pt(x: float, y: float) -> list[float]:
    return [r3(x), r3(y)]


@dataclass
class Side:
    """One side of a parcel: consecutive near-collinear polygon edges merged."""

    index: int
    coords: list[tuple[float, float]]
    length: float
    mid: tuple[float, float]
    normal: tuple[float, float]  # unit, pointing out of the parcel
    road_distance: float = math.inf


@dataclass
class Frame:
    """Placement frame of a parcel (local metres): origin M on the front edge, axes right/inward."""

    parcel: str
    side: Side
    origin: tuple[float, float]
    inward: tuple[float, float]
    right: tuple[float, float]

    def at(self, a: float, i: float) -> tuple[float, float]:
        return (
            self.origin[0] + a * self.right[0] + i * self.inward[0],
            self.origin[1] + a * self.right[1] + i * self.inward[1],
        )

    def to_ai(self, x: float, y: float) -> tuple[float, float]:
        dx, dy = x - self.origin[0], y - self.origin[1]
        return dx * self.right[0] + dy * self.right[1], dx * self.inward[0] + dy * self.inward[1]


class MapData:
    """The work files of the map, as shapely geometry in local metres. Reloaded when they change."""

    def __init__(self, work_dir: Path) -> None:
        self.work_dir = work_dir
        self._stamp: tuple | None = None
        self.parcels: dict[str, dict] = {}
        self.ground: list[dict] = []
        self.polys: dict[str, Polygon] = {}
        self.buildings: dict[str, dict] = {}
        self.bpolys: dict[str, Polygon] = {}
        self.origin = (0.0, 0.0)
        self._ptree: shapely.STRtree | None = None
        self._pids: list[str] = []
        self._btree: shapely.STRtree | None = None
        self._bids: list[str] = []
        self.road_zone: Any = None
        self._sides: dict[str, list[Side]] = {}

    def _files(self) -> list[Path]:
        return [self.work_dir / n for n in ("parcels.json", "buildings.json", "roads.json")]

    def ensure(self) -> MapData:
        files = self._files()
        missing = [f.name for f in files if not f.is_file()]
        if missing:
            raise ToolError(
                f"map work files missing in {self.work_dir}: {', '.join(missing)}. Run the "
                "pipeline stages first (make normalise parcels rules terrain roads, with the same "
                ") or start the server with --work pointing at a built map."
            )
        stamp = tuple(f.stat().st_mtime_ns for f in files)
        if stamp != self._stamp:
            self._load()
            self._stamp = stamp
        return self

    def _load(self) -> None:
        doc = json.loads((self.work_dir / "parcels.json").read_text())
        origin = doc.get("origin_epsg5514")
        if not origin:
            raise ToolError(
                "parcels.json has no origin_epsg5514 (map built before version 0.4.0); re-run "
                "the parcels stage"
            )
        self.origin = (float(origin[0]), float(origin[1]))
        self.parcels = {p["id"]: p for p in doc["parcels"]}
        self.ground = doc.get("ground", [])
        self._pids = sorted(self.parcels)
        self.polys = {}
        for pid in self._pids:
            g = shape(self.parcels[pid]["polygon"])
            if g.geom_type == "MultiPolygon":
                g = max(g.geoms, key=lambda x: x.area)
            self.polys[pid] = g
        self._ptree = shapely.STRtree([self.polys[p] for p in self._pids])
        bdoc = json.loads((self.work_dir / "buildings.json").read_text())["buildings"]
        self.buildings = {b["id"]: b for b in bdoc}
        self._bids = sorted(self.buildings)
        self.bpolys = {b: footprint_polygon(self.buildings[b]["footprint"]) for b in self._bids}
        self._btree = shapely.STRtree([self.bpolys[b] for b in self._bids])
        graph = json.loads((self.work_dir / "roads.json").read_text())
        parts = [ln.buffer(e["width"] / 2) for e, ln in edge_lines(graph)]
        self.road_zone = unary_union(parts) if parts else None
        if self.road_zone is not None:
            shapely.prepare(self.road_zone)
        self._sides = {}

    # coordinates ---------------------------------------------------------------------------
    def to_abs(self, x: float, y: float) -> list[float]:
        return pt(x + self.origin[0], y + self.origin[1])

    def to_local(self, x: float, y: float) -> tuple[float, float]:
        return x - self.origin[0], y - self.origin[1]

    # queries -------------------------------------------------------------------------------
    def need(self, pid: object) -> str:
        if not isinstance(pid, str) or pid not in self.parcels:
            hint = ""
            if isinstance(pid, str) and PARCEL_ID.fullmatch(pid):
                hint = " (it may be retired or outside this map)"
            raise ToolError(f"unknown parcel {pid!r}{hint}. Use find_parcels to look up IDs.")
        return pid

    def parcels_near(self, geom: Any, distance: float) -> list[str]:
        assert self._ptree is not None
        idx = self._ptree.query(geom, predicate="dwithin", distance=distance)
        return sorted(self._pids[int(i)] for i in idx)

    def buildings_near(self, geom: Any, distance: float) -> list[str]:
        assert self._btree is not None
        idx = self._btree.query(geom, predicate="dwithin", distance=distance)
        return sorted(self._bids[int(i)] for i in idx)

    def parcel_at(self, x: float, y: float) -> str | None:
        hits = self.parcels_near(Point(x, y), 0.0)
        hits = [h for h in hits if self.polys[h].covers(Point(x, y))]
        return hits[0] if hits else None

    def sides(self, pid: str) -> list[Side]:
        if pid not in self._sides:
            self._sides[pid] = self._compute_sides(self.polys[pid])
        return self._sides[pid]

    def _compute_sides(self, poly: Polygon) -> list[Side]:
        ring = LinearRing(poly.exterior.coords)
        if not ring.is_ccw:
            ring = LinearRing(list(ring.coords)[::-1])
        pts: list[tuple[float, float]] = []
        for x, y in list(ring.coords)[:-1]:
            if not pts or math.hypot(x - pts[-1][0], y - pts[-1][1]) > 0.05:
                pts.append((x, y))
        n = len(pts)

        def heading(a: tuple, b: tuple) -> float:
            return math.atan2(b[1] - a[1], b[0] - a[0])

        def turn(i: int) -> float:  # direction change at vertex i, degrees, absolute
            d = heading(pts[i - 1], pts[i]) - heading(pts[i], pts[(i + 1) % n])
            return abs((math.degrees(d) + 180) % 360 - 180)

        breaks = [i for i in range(n) if turn(i) > EDGE_MERGE_DEG]
        chains: list[list[tuple[float, float]]] = []
        if len(breaks) < 2:  # a smooth ring: every edge is its own side
            chains = [[pts[i], pts[(i + 1) % n]] for i in range(n)]
        else:
            for k, start in enumerate(breaks):
                end = breaks[(k + 1) % len(breaks)]
                chain = [pts[start]]
                i = start
                while i != end:
                    i = (i + 1) % n
                    chain.append(pts[i])
                chains.append(chain)
        sides = []
        for idx, chain in enumerate(chains):
            line = LineString(chain)
            length = line.length
            if length <= 0:
                continue
            (x0, y0), (x1, y1) = chain[0], chain[-1]
            cx, cy = x1 - x0, y1 - y0
            norm = math.hypot(cx, cy) or 1.0
            mid = line.interpolate(length / 2)
            side = Side(
                idx, chain, length, (mid.x, mid.y), (cy / norm, -cx / norm)
            )  # CCW ring: interior on the left, so outside is on the right
            if self.road_zone is not None:
                samples = [line.interpolate(length * f) for f in (0.25, 0.5, 0.75)]
                side.road_distance = sum(self.road_zone.distance(p) for p in samples) / 3
            sides.append(side)
        for k, s in enumerate(sides):
            s.index = k
        return sides

    def frame(self, pid: str) -> tuple[Frame, list[str]]:
        """The parcel's placement frame and warnings about how reliable the front edge is."""
        sides = self.sides(pid)
        if not sides:
            raise ToolError(f"parcel {pid} has a degenerate outline; use an exact position")
        warnings: list[str] = []
        pool = [s for s in sides if s.length >= MIN_FRONT_SIDE_M] or sides
        if all(math.isinf(s.road_distance) for s in pool):
            warnings.append(f"no roads in the map data; front edge of {pid} is its longest side")
            front = max(pool, key=lambda s: s.length)
        else:
            front = min(pool, key=lambda s: (round(s.road_distance), -s.length))
            if front.road_distance > ROAD_FAR_M:
                warnings.append(
                    f"no road within {ROAD_FAR_M:.0f} m of {pid}; the front edge "
                    f"(side {front.index}, {front.road_distance:.0f} m from a road) is a guess"
                )
        nx, ny = front.normal
        inward = (-nx, -ny)
        right = (inward[1], -inward[0])
        return Frame(pid, front, front.mid, inward, right), warnings
