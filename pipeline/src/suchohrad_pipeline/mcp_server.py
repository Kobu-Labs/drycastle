"""MCP server for parcel edits: lets an LLM (Claude Code) change ``pipeline/config/edits.json``.

Run it over stdio (Claude Code starts it)::

    uv run --directory <repo>/pipeline python -m suchohrad_pipeline.mcp_server [--area slice|village]
        [--edits PATH] [--state PATH] [--assets PATH] [--work DIR]

It talks only to files: it reads the map's work files (``parcels.json``, ``buildings.json``,
``roads.json``), the asset manifest and, for "the selected parcel", ``out/editor-state.json`` written
by the viewer; it writes ``edits.json`` (the viewer, started with ``--editor --watch``, notices the
change and re-applies the affected parcels). The schema and the validator are the pipeline's own
(:mod:`suchohrad_pipeline.edits`, :mod:`suchohrad_pipeline.assets`), so a file written here is
exactly what the pipeline and the Godot editor accept.

All writes are reload -> modify -> validate the whole file -> atomic write, under an exclusive
``flock`` on ``<edits>.lock`` so two writers (two MCP calls, or the editor) never lose each other's
changes. Coordinates in tool input and output are absolute S-JTSK (EPSG:5514) metres, x east and
y north, the frame of ``edits.json``; the local map frame is only used internally.

Placement frame (``place_object``): the *front edge* of a parcel is the boundary side nearest a
road. Standing on its midpoint and looking into the parcel, ``along`` grows to the right and
``inward`` grows away from the road. An object's front is its glTF +Z axis (cars face +Z in the
Kenney kit); ``rotation_deg`` is counter-clockwise seen from above, as in ``edits.json``.
"""

from __future__ import annotations

import argparse
import contextlib
import fcntl
import itertools
import json
import math
import re
import sys
import time
from collections.abc import Callable, Iterator
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import shapely
import shapely.affinity
from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import LinearRing, LineString, Point, Polygon, shape
from shapely.ops import nearest_points, unary_union

from suchohrad_pipeline.assets import DEFAULT_MANIFEST, load_manifest, validate_asset_ref
from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.config import DEFAULT_AREA, REPO_ROOT, area_paths
from suchohrad_pipeline.edits import (
    COLUMN_TYPES,
    CUSTOM_FENCE_KEYS,
    DEFAULT_EDITS,
    FENCE_TYPES,
    POLE_ASSET_PREFIX,
    Edits,
    EditsError,
    cable_pair,
    dump_edits,
    load_edits,
    region_ring,
    resolve_fence,
    validate,
)
from suchohrad_pipeline.fencestyles import FENCE_STYLES
from suchohrad_pipeline.regions import region_rank
from suchohrad_pipeline.roads import edge_lines
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section

DEFAULT_STATE = REPO_ROOT / "out" / "editor-state.json"
STATE_FORMAT_VERSION = 1
STATE_STALE_S = 600.0  # a selection older than this probably comes from a viewer that has exited

ANCHORS = ("front", "back", "left", "right", "centre", "near_building")
EDGE_MERGE_DEG = 8.0  # consecutive polygon edges turning less than this form one "side"
MIN_FRONT_SIDE_M = 2.0
ANCHOR_MARGIN_M = 1.5  # anchors sit this far inside the parcel
INTERIOR_M = 0.3  # a moved point lands this far inside the boundary
NEAR_BUILDING_M = 2.0
ROAD_FAR_M = 30.0
OVERLAP_SEARCH_M = 25.0
_PARCEL_ID = re.compile(r"P-\d{4,}")
_GROUP_ID = re.compile(r"G-\d{4,}")
_REGION_ID = re.compile(r"R-\d{4,}")
_COLOUR = re.compile(r"#[0-9a-fA-F]{6}")
_GENERATED_ID = re.compile(r"[oGRC]-(\d+)")


def _r3(v: float) -> float:
    return round(float(v), 3)


def _pt(x: float, y: float) -> list[float]:
    return [_r3(x), _r3(y)]


# --- settings and map data ------------------------------------------------------------------


@dataclass(frozen=True)
class Settings:
    area: str = DEFAULT_AREA
    work_dir: Path | None = None
    edits_path: Path = DEFAULT_EDITS
    state_path: Path = DEFAULT_STATE
    assets_path: Path = DEFAULT_MANIFEST
    rules_path: Path = DEFAULT_RULES

    @property
    def work(self) -> Path:
        return self.work_dir or area_paths(self.area).work


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
    """The work files of one area, as shapely geometry in local metres. Reloaded when they change."""

    def __init__(self, work_dir: Path) -> None:
        self.work_dir = work_dir
        self._stamp: tuple | None = None
        self.parcels: dict[str, dict] = {}
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
                "AREA) or start the server with --area/--work pointing at a built area."
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
        parts += [self.polys[p] for p in self._pids if self.parcels[p]["kind"] == "road"]
        self.road_zone = unary_union(parts) if parts else None
        if self.road_zone is not None:
            shapely.prepare(self.road_zone)
        self._sides = {}

    # coordinates ---------------------------------------------------------------------------
    def to_abs(self, x: float, y: float) -> list[float]:
        return _pt(x + self.origin[0], y + self.origin[1])

    def to_local(self, x: float, y: float) -> tuple[float, float]:
        return x - self.origin[0], y - self.origin[1]

    # queries -------------------------------------------------------------------------------
    def need(self, pid: object) -> str:
        if not isinstance(pid, str) or pid not in self.parcels:
            hint = ""
            if isinstance(pid, str) and _PARCEL_ID.fullmatch(pid):
                hint = " (it may be retired or outside this area; check --area)"
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


# --- edits file access ----------------------------------------------------------------------


@contextlib.contextmanager
def edits_lock(path: Path) -> Iterator[None]:
    """Exclusive advisory lock on ``<edits>.lock`` (other writers that honour it wait)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path.with_name(path.name + ".lock"), "a+") as fh:
        fcntl.flock(fh, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(fh, fcntl.LOCK_UN)


def _bullets(errors: list[str]) -> str:
    return "\n".join(f"  - {e}" for e in errors)


def _read_edits(path: Path) -> Edits:
    try:
        return load_edits(path)
    except EditsError as exc:
        raise ToolError(
            f"{path.name} is currently invalid, so nothing was changed. Fix it first:\n"
            + _bullets(exc.errors)
        ) from exc


def _edits_doc(edits: Edits) -> dict:
    """The edits as the document ``validate`` checks (regions included when present)."""
    doc: dict = {"version": 1, "parcels": edits.parcels, "groups": edits.groups}
    if edits.regions:
        doc["regions"] = edits.regions
    if edits.cables:
        doc["cables"] = edits.cables
    return doc


def _empty_parcel_cleanup(edits: Edits) -> None:
    for pid in [p for p, e in edits.parcels.items() if not e]:
        del edits.parcels[pid]


def _next_id(prefix: str, ids: list[str]) -> str:
    top = 0
    for i in ids:
        m = _GENERATED_ID.fullmatch(i)
        if m and i.startswith(prefix + "-"):
            top = max(top, int(m.group(1)))
    return f"{prefix}-{top + 1:04d}"


def _all_objects(edits: Edits) -> list[tuple[str, dict]]:
    return [(pid, o) for pid, e in sorted(edits.parcels.items()) for o in e.get("objects", [])]


# --- geometry helpers -----------------------------------------------------------------------


def _facing_rotation(direction: tuple[float, float]) -> float:
    """``rotation_deg`` that turns an object's front (+Z) toward ``direction`` (map frame)."""
    phi = math.degrees(math.atan2(direction[1], direction[0]))
    return _norm_deg(phi + 90.0)


def _norm_deg(deg: float) -> float:
    deg = deg % 360.0
    return round(deg, 3) + 0.0


def object_polygon(
    xy: tuple[float, float], size_m: list[float] | None, rotation_deg: float, scale: float
) -> Polygon:
    """Approximate footprint of a placed object (local metres): its bbox about the origin."""
    sx, sz = (size_m[0], size_m[2]) if size_m else (1.0, 1.0)
    hx, hz = sx * scale / 2, sz * scale / 2
    th = math.radians(rotation_deg)
    c, s = math.cos(th), math.sin(th)
    pts = []
    for x, z in ((-hx, -hz), (hx, -hz), (hx, hz), (-hx, hz)):
        xr, zr = x * c + z * s, -x * s + z * c  # Godot rotation about Y
        pts.append((xy[0] + xr, xy[1] - zr))  # map y = -z
    return Polygon(pts)


def _line_pieces(poly: Polygon, p0: tuple[float, float], d: tuple[float, float]) -> list[tuple]:
    """Parameter ranges (t0, t1) along p0 + t d (unit d) that lie inside ``poly``."""
    reach = 4 * (max(poly.bounds[2] - poly.bounds[0], poly.bounds[3] - poly.bounds[1]) + 10)
    line = LineString(
        [(p0[0] - reach * d[0], p0[1] - reach * d[1]), (p0[0] + reach * d[0], p0[1] + reach * d[1])]
    )
    hit = poly.intersection(line)
    parts = [hit] if hit.geom_type == "LineString" else list(getattr(hit, "geoms", []))
    out = []
    for g in parts:
        if g.geom_type != "LineString" or g.is_empty:
            continue
        ts = [(x - p0[0]) * d[0] + (y - p0[1]) * d[1] for x, y in g.coords]
        out.append((min(ts), max(ts)))
    return sorted(out)


def _interior_point(poly: Polygon, x: float, y: float) -> tuple[float, float]:
    inner = poly.buffer(-INTERIOR_M)
    target = poly if inner.is_empty else inner
    p = nearest_points(target, Point(x, y))[0]
    return p.x, p.y


# --- the tools ------------------------------------------------------------------------------


def _check_num(name: str, v: object, *, positive: bool = False, minimum: float | None = None) -> float:
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
        raise ToolError(f"{name} must be a number, got {v!r}")
    if positive and v <= 0:
        raise ToolError(f"{name} must be > 0, got {v}")
    if minimum is not None and v < minimum:
        raise ToolError(f"{name} must be >= {minimum}, got {v}")
    return float(v)


@dataclass
class ParcelEditTools:
    """The tool implementations; each public method is one MCP tool (JSON in, JSON out)."""

    settings: Settings
    _map: MapData | None = field(default=None, init=False, repr=False)
    _manifest: dict | None = field(default=None, init=False, repr=False)
    _manifest_stamp: int | None = field(default=None, init=False, repr=False)

    # plumbing ------------------------------------------------------------------------------
    @property
    def map(self) -> MapData:
        if self._map is None:
            self._map = MapData(self.settings.work)
        return self._map.ensure()

    @property
    def manifest(self) -> dict:
        path = self.settings.assets_path
        try:
            stamp = path.stat().st_mtime_ns
        except FileNotFoundError as exc:
            raise ToolError(f"asset manifest not found: {path} (see --assets)") from exc
        if self._manifest is None or stamp != self._manifest_stamp:
            self._manifest = load_manifest(path)
            self._manifest_stamp = stamp
        return self._manifest

    def _asset(self, ref: object) -> dict:
        problem = validate_asset_ref(ref, self.manifest)  # type: ignore[arg-type]
        if problem:
            raise ToolError(f"{problem}. Call list_assets to see the valid ids.")
        return next(a for a in self.manifest["assets"] if a["id"] == ref)

    def _write(self, change: Callable[[Edits], Any]) -> Any:
        """Reload edits.json under the lock, apply ``change``, validate everything, write."""
        path = self.settings.edits_path
        with edits_lock(path):
            edits = _read_edits(path)
            result = change(edits)
            _empty_parcel_cleanup(edits)
            errors = validate(_edits_doc(edits))
            if errors:
                raise ToolError(
                    "refused: the change would make edits.json invalid, nothing was written:\n"
                    + _bullets(errors)
                )
            dump_edits(edits, path)
        return result

    def _group_of(self, edits: Edits, pid: str) -> str | None:
        for gid, g in edits.groups.items():
            if pid in g.get("parcels", []):
                return gid
        return None

    # list_assets ---------------------------------------------------------------------------
    def list_assets(self, category: str | None = None) -> dict:
        """List placeable assets (see the registered tool description)."""
        from suchohrad_pipeline.assets import CATEGORIES

        if category is not None and category not in CATEGORIES:
            raise ToolError(f"unknown category {category!r}; choose one of {', '.join(CATEGORIES)}")
        rows = [
            {
                "id": a["id"],
                "category": a["category"],
                "name": a["name"],
                "size_m": a.get("size_m"),
                "tags": a.get("tags", []),
            }
            for a in self.manifest["assets"]
            if category is None or a["category"] == category
        ]
        return {"count": len(rows), "assets": rows}

    # find_parcels --------------------------------------------------------------------------
    def find_parcels(
        self,
        near: dict | None = None,
        kind: str | None = None,
        selected: bool = False,
        limit: int = 20,
    ) -> dict:
        """Find parcels (see the registered tool description)."""
        from suchohrad_pipeline.parcels import KINDS

        m = self.map
        if kind is not None and kind not in KINDS:
            raise ToolError(f"unknown kind {kind!r}; choose one of {', '.join(KINDS)}")
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
            raise ToolError("limit must be an integer >= 1")
        if selected and near is not None:
            raise ToolError("give either selected or near, not both")
        edits = _read_edits(self.settings.edits_path)
        extra: dict = {}
        warnings: list[str] = []
        distances: dict[str, float] = {}
        if selected:
            ids, extra, warnings = self._read_state()
            pool = ids
        elif near is not None:
            if not isinstance(near, dict):
                raise ToolError('near must be {"x": .., "y": ..} or {"parcel": "P-0001", "radius_m": ..}')
            radius = _check_num("near.radius_m", near.get("radius_m", 100.0), positive=True)
            if "parcel" in near:
                src = m.polys[m.need(near["parcel"])]
            elif "x" in near and "y" in near:
                x = _check_num("near.x", near["x"])
                y = _check_num("near.y", near["y"])
                src = Point(*m.to_local(x, y))
            else:
                raise ToolError('near needs "x" and "y" (S-JTSK metres) or "parcel"')
            pool = m.parcels_near(src, radius)
            distances = {p: m.polys[p].distance(src) for p in pool}
            pool = sorted(pool, key=lambda p: (distances[p], p))
        else:
            pool = sorted(m.parcels)
        if kind:
            pool = [p for p in pool if m.parcels[p]["kind"] == kind]
        rows = [self._parcel_row(p, edits, distances.get(p)) for p in pool[:limit]]
        out = {"count": len(rows), "total_matching": len(pool), "parcels": rows, **extra}
        if warnings:
            out["warnings"] = warnings
        return out

    def _parcel_row(self, pid: str, edits: Edits, distance: float | None = None) -> dict:
        m = self.map
        p = m.parcels[pid]
        ax, ay = p["anchor"]
        row = {
            "id": pid,
            "kind": p["kind"],
            "area_m2": round(m.polys[pid].area, 2),
            "anchor": m.to_abs(ax, ay),
            "group": self._group_of(edits, pid),
            "has_edits": bool(edits.parcels.get(pid)) or self._group_of(edits, pid) is not None,
        }
        if distance is not None:
            row["distance_m"] = round(distance, 1)
        return row

    def _read_state(self) -> tuple[list[str], dict, list[str]]:
        path = self.settings.state_path
        guide = (
            "Start the viewer in editor mode (godot --path godot/viewer -- --editor --watch ...), "
            "select parcels by clicking, then try again; or pass parcel IDs explicitly."
        )
        if not path.is_file():
            raise ToolError(f"no editor state file at {path}. {guide}")
        try:
            doc = json.loads(path.read_text())
            if doc.get("format_version") != STATE_FORMAT_VERSION:
                raise ValueError(f"format_version {doc.get('format_version')!r}")
            ids = list(doc["selected"])
        except (OSError, ValueError, KeyError, TypeError) as exc:
            raise ToolError(f"editor state file {path} is unreadable ({exc}). {guide}") from exc
        if not ids:
            raise ToolError(
                f"nothing is selected in the viewer (state file {path.name}). Click a parcel in "
                "editor mode (Shift+click adds more), or pass IDs explicitly."
            )
        unknown = [i for i in ids if i not in self.map.parcels]
        if unknown:
            raise ToolError(
                f"the viewer's selection {unknown[:5]} is not in this map: the state file comes "
                f"from another area or an older map (this server uses --area {self.settings.area}). "
                "Check the --area/--state options."
            )
        age = max(0.0, time.time() - path.stat().st_mtime)
        warnings = []
        if age > STATE_STALE_S:
            warnings.append(
                f"the state file is {age / 60:.0f} min old; the viewer may no longer be running, "
                "so this selection could be outdated"
            )
        extra = {"selection_age_s": round(age, 1), "camera": doc.get("camera")}
        return sorted(ids), extra, warnings

    # get_parcel ----------------------------------------------------------------------------
    def get_parcel(self, id: str) -> dict:
        """Describe one parcel (see the registered tool description)."""
        m = self.map
        pid = m.need(id)
        p, poly = m.parcels[pid], m.polys[pid]
        edits = _read_edits(self.settings.edits_path)
        frame, warnings = m.frame(pid)
        sides = m.sides(pid)
        c = poly.centroid
        minx, miny, maxx, maxy = poly.bounds
        gid = self._group_of(edits, pid)
        entry = edits.parcels.get(pid, {})
        group_fence = edits.groups[gid].get("fence") if gid else None
        fence_rules = load_section("props", self.settings.rules_path, self.settings.area)
        fcfg, tcfg = fence_rules["fences"], fence_rules["trees"]
        effective = resolve_fence(entry.get("fence"), group_fence)
        buildings = []
        for bid in p.get("buildings", []):
            if bid not in m.bpolys:
                continue
            bp = m.bpolys[bid]
            bx0, by0, bx1, by1 = bp.bounds
            buildings.append(
                {
                    "id": bid,
                    "footprint_centroid": m.to_abs(bp.centroid.x, bp.centroid.y),
                    "bounds": [*m.to_abs(bx0, by0), *m.to_abs(bx1, by1)],
                    "area_m2": round(bp.area, 1),
                    "height_m": m.buildings[bid].get("height_m"),
                }
            )
        neighbours = [
            {"id": q, "kind": m.parcels[q]["kind"]}
            for q in m.parcels_near(poly, 0.5)
            if q != pid
        ]
        nx, ny = frame.side.normal
        return {
            "id": pid,
            "kind": p["kind"],
            "area_m2": round(m.polys[pid].area, 2),
            "cadastral_ref": p.get("cadastral_ref"),
            "group": gid,
            "polygon": {
                "bounds": [*m.to_abs(minx, miny), *m.to_abs(maxx, maxy)],
                "centroid": m.to_abs(c.x, c.y),
                "vertices": len(poly.exterior.coords) - 1,
            },
            "edges": [
                {
                    "index": s.index,
                    "length_m": round(s.length, 2),
                    "midpoint": m.to_abs(*s.mid),
                    "outward_normal": _pt(*s.normal),
                    "road_distance_m": None if math.isinf(s.road_distance) else round(s.road_distance, 1),
                    "is_front": s.index == frame.side.index,
                }
                for s in sides
            ],
            "front_edge": {
                "index": frame.side.index,
                "length_m": round(frame.side.length, 2),
                "midpoint": m.to_abs(*frame.side.mid),
                "outward_normal": _pt(nx, ny),
                "road_distance_m": None
                if math.isinf(frame.side.road_distance)
                else round(frame.side.road_distance, 1),
                "inward_axis": _pt(*frame.inward),
                "right_axis": _pt(*frame.right),
                "note": "along grows to the right (standing on the front edge, facing in), "
                "inward grows away from the road; anchors front/back/left/right/centre are "
                "relative to this",
            },
            "buildings": buildings,
            "neighbours": neighbours,
            "edits": {
                "fence": entry.get("fence"),
                "trees": entry.get("trees"),
                "objects": entry.get("objects", []),
                "group_fence": group_fence,
            },
            "effective_fence": {
                "mode": effective.mode,
                "type": effective.type,
                "column": effective.column or FENCE_STYLES[effective.type].column,
                "height_m": effective.height_m,
                "thickness_m": effective.thickness_m,
                "colour": effective.colour,
                "gaps": [[g[0], g[1], g[2]] for g in effective.gaps],
            },
            "rule_defaults": {
                "fence": {
                    "kind_gets_fence": p["kind"] in fcfg["kinds"],
                    "probability": fcfg["probability"],
                    "auto_height_m": fcfg["height_m"],
                    "gate_gap_m": fcfg["gate_gap_m"],
                    "custom_types": FENCE_TYPES,
                    "column_types": list(COLUMN_TYPES),
                },
                "trees": {
                    "density_per_1000m2": tcfg["density_per_1000m2"].get(p["kind"], 0.0),
                },
            },
            **({"warnings": warnings} if warnings else {}),
        }

    # fence ---------------------------------------------------------------------------------
    def _fence_target(self, target: str) -> tuple[str, str]:
        if isinstance(target, str) and _GROUP_ID.fullmatch(target):
            return "group", target
        if isinstance(target, str) and _PARCEL_ID.fullmatch(target):
            return "parcel", self.map.need(target)
        raise ToolError(f"target {target!r} is neither a parcel ID (P-0412) nor a group ID (G-0001)")

    def _gaps(self, gaps: object, kind: str, target: str) -> list[dict]:
        if not isinstance(gaps, list):
            raise ToolError('gaps must be a list of {"at": [x, y], "width_m": n}')
        out = []
        for i, g in enumerate(gaps):
            if not isinstance(g, dict) or set(g) - {"at", "width_m"}:
                raise ToolError(f'gaps[{i}] must be {{"at": [x, y] or "front", "width_m": n}}')
            at = g.get("at")
            if at == "front":
                if kind != "parcel":
                    raise ToolError(f'gaps[{i}]: "front" works for a single parcel only; give [x, y]')
                frame, _ = self.map.frame(target)
                at = self.map.to_abs(*frame.origin)
            if not (isinstance(at, (list, tuple)) and len(at) == 2):
                raise ToolError(f'gaps[{i}].at must be [x, y] in S-JTSK metres or "front"')
            width = _check_num(f"gaps[{i}].width_m", g.get("width_m", 3.0), positive=True)
            out.append(
                {
                    "at": [_check_num(f"gaps[{i}].at", at[0]), _check_num(f"gaps[{i}].at", at[1])],
                    "width_m": width,
                }
            )
        return out

    def set_fence(
        self,
        target: str,
        mode: str | None = None,
        type: str | None = None,
        height_m: float | None = None,
        thickness_m: float | None = None,
        colour: str | None = None,
        gaps: list[dict] | None = None,
        column: str | None = None,
    ) -> dict:
        """Set the fence of a parcel or property group (see the registered tool description)."""
        kind, tid = self._fence_target(target)
        custom = {
            "type": type, "column": column, "height_m": height_m, "thickness_m": thickness_m,
            "colour": colour, "gaps": gaps,
        }
        custom = {k: v for k, v in custom.items() if v is not None}
        if mode is None and not custom:
            raise ToolError(
                "nothing to set: give mode (auto|none|custom) and/or type, column, height_m, "
                "thickness_m, colour, gaps"
            )
        if mode is not None and mode not in ("auto", "none", "custom"):
            raise ToolError(f"mode must be one of auto, none, custom (got {mode!r})")
        if mode in ("auto", "none") and custom:
            raise ToolError(
                f"mode {mode!r} cannot be combined with {', '.join(custom)}: those keys are "
                "custom-only. Use mode 'custom' (or omit mode), or drop the keys."
            )
        if "type" in custom and custom["type"] not in FENCE_TYPES:
            raise ToolError(f"type must be one of {', '.join(FENCE_TYPES)} (got {type!r})")
        if "column" in custom and custom["column"] not in COLUMN_TYPES:
            raise ToolError(f"column must be one of {', '.join(COLUMN_TYPES)} (got {column!r})")
        for key in ("height_m", "thickness_m"):
            if key in custom:
                _check_num(key, custom[key], positive=True)
        if "colour" in custom and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(custom["colour"])):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        if "gaps" in custom:
            custom["gaps"] = self._gaps(custom["gaps"], kind, tid)
        warnings: list[str] = []

        def change(edits: Edits) -> dict:
            if kind == "group":
                if tid not in edits.groups:
                    raise ToolError(f"group {tid} does not exist; groups: {sorted(edits.groups)}")
                holder = edits.groups[tid]
            else:
                gid = self._group_of(edits, tid)
                if gid:
                    warnings.append(
                        f"{tid} is in group {gid}: this writes a fence override on the parcel, "
                        f"which wins field by field over the group's fence. To change the whole "
                        f"group use target {gid}."
                    )
                holder = edits.parcels.setdefault(tid, {})
            fence = dict(holder.get("fence", {}))
            if mode in ("auto", "none"):
                fence = {"mode": mode}
            else:
                if mode == "custom":
                    fence["mode"] = "custom"
                else:  # custom keys without a mode imply custom
                    fence.pop("mode", None)
                fence.update(custom)
            holder["fence"] = fence
            return fence

        fence = self._write(change)
        return {
            "summary": f"fence of {tid} set: {json.dumps(fence, sort_keys=True)}",
            "target": tid,
            "fence": fence,
            "warnings": warnings,
        }

    def reset_fence(self, target: str) -> dict:
        """Remove the fence entry of a parcel or group, so the rules (or the group) apply again."""
        kind, tid = self._fence_target(target)

        def change(edits: Edits) -> bool:
            holder = edits.groups.get(tid) if kind == "group" else edits.parcels.get(tid)
            if kind == "group" and holder is None:
                raise ToolError(f"group {tid} does not exist; groups: {sorted(edits.groups)}")
            if not holder or "fence" not in holder:
                return False
            del holder["fence"]
            return True

        changed = self._write(change)
        return {
            "summary": f"fence of {tid} reset to the rules" if changed else f"{tid} had no fence edit",
            "target": tid,
            "changed": changed,
        }

    # trees ---------------------------------------------------------------------------------
    def set_trees(
        self,
        parcel: str,
        density_per_1000m2: float | None = None,
        count: int | None = None,
        seed_offset: int | None = None,
    ) -> dict:
        """Set generated trees of a parcel (see the registered tool description)."""
        pid = self.map.need(parcel)
        if density_per_1000m2 is not None and count is not None:
            raise ToolError("give exactly one of density_per_1000m2 or count, not both")
        if density_per_1000m2 is not None:
            _check_num("density_per_1000m2", density_per_1000m2, minimum=0)
        if count is not None and (isinstance(count, bool) or not isinstance(count, int) or count < 0):
            raise ToolError(f"count must be an integer >= 0 (got {count!r})")
        if seed_offset is not None and (isinstance(seed_offset, bool) or not isinstance(seed_offset, int)):
            raise ToolError(f"seed_offset must be an integer (got {seed_offset!r})")
        if density_per_1000m2 is None and count is None and seed_offset is None:
            raise ToolError("nothing to set: give density_per_1000m2 or count (and/or seed_offset)")

        def change(edits: Edits) -> dict:
            holder = edits.parcels.setdefault(pid, {})
            trees = dict(holder.get("trees", {}))
            if density_per_1000m2 is not None:
                trees.pop("count", None)
                trees["density_per_1000m2"] = density_per_1000m2
            if count is not None:
                trees.pop("density_per_1000m2", None)
                trees["count"] = count
            if seed_offset is not None:
                trees["seed_offset"] = seed_offset
            if "density_per_1000m2" not in trees and "count" not in trees:
                tcfg = load_section("props", self.settings.rules_path, self.settings.area)["trees"]
                rate = tcfg["density_per_1000m2"].get(self.map.parcels[pid]["kind"], 0.0)
                raise ToolError(
                    "a seed_offset alone cannot be stored: edits.json needs a density or a count "
                    f"with it. The rules' density for this parcel's kind is {rate} per 1000 m2; "
                    "pass density_per_1000m2 (e.g. that value) together with seed_offset."
                )
            holder["trees"] = trees
            return trees

        trees = self._write(change)
        return {
            "summary": f"trees of {pid} set: {json.dumps(trees, sort_keys=True)}",
            "parcel": pid,
            "trees": trees,
            "warnings": [],
        }

    def reset_trees(self, parcel: str) -> dict:
        """Remove the trees entry of a parcel so the rules' tree density applies again."""
        pid = self.map.need(parcel)

        def change(edits: Edits) -> bool:
            holder = edits.parcels.get(pid)
            if not holder or "trees" not in holder:
                return False
            del holder["trees"]
            return True

        changed = self._write(change)
        return {
            "summary": f"trees of {pid} reset to the rules" if changed else f"{pid} had no trees edit",
            "parcel": pid,
            "changed": changed,
        }

    # objects -------------------------------------------------------------------------------
    def _place_point(
        self,
        pid: str,
        anchor: str | None,
        offset_m: dict | None,
        position: list[float] | None,
        warnings: list[str],
    ) -> tuple[float, float, Frame]:
        """Local position for an anchor or exact position; the frame is the parcel's."""
        m = self.map
        poly = m.polys[pid]
        frame, fw = m.frame(pid)
        warnings.extend(fw)
        if position is not None:
            if anchor is not None or offset_m is not None:
                raise ToolError("give either position or anchor/offset_m, not both")
            if not (isinstance(position, (list, tuple)) and len(position) == 2):
                raise ToolError("position must be [x, y] in absolute S-JTSK metres")
            px = _check_num("position[0]", position[0])
            py = _check_num("position[1]", position[1])
            return (*m.to_local(px, py), frame)
        anchor = anchor or "centre"
        if anchor not in ANCHORS:
            raise ToolError(f"anchor must be one of {', '.join(ANCHORS)} (got {anchor!r})")
        along = inward = 0.0
        if offset_m is not None:
            if not isinstance(offset_m, dict) or set(offset_m) - {"along", "inward"}:
                raise ToolError('offset_m must be {"along": metres, "inward": metres}')
            along = _check_num("offset_m.along", offset_m.get("along", 0.0))
            inward = _check_num("offset_m.inward", offset_m.get("inward", 0.0))
        pa, pi = self._anchor_ai(pid, frame, anchor, warnings)
        x, y = frame.at(pa + along, pi + inward)
        if not poly.covers(Point(x, y)):
            nx, ny = _interior_point(poly, x, y)
            warnings.append(
                f"anchor {anchor} with the offset falls outside {pid}; moved "
                f"{math.hypot(nx - x, ny - y):.1f} m to the nearest interior point"
            )
            x, y = nx, ny
        return x, y, frame

    def _anchor_ai(
        self, pid: str, frame: Frame, anchor: str, warnings: list[str]
    ) -> tuple[float, float]:
        """Anchor as (along, inward) in the parcel's front frame."""
        m = self.map
        poly = m.polys[pid]
        ext = [frame.to_ai(x, y) for x, y in poly.exterior.coords]
        depth = max(i for _, i in ext)
        margin = min(ANCHOR_MARGIN_M, max(depth, 0.0) / 4)
        if anchor == "front":
            return 0.0, margin
        if anchor == "back":
            pieces = _line_pieces(poly, frame.origin, frame.inward)
            far = max((t1 for _, t1 in pieces), default=depth)
            return 0.0, max(far - margin, 0.0)
        if anchor in ("left", "right"):
            mid = frame.at(0.0, depth / 2)
            pieces = _line_pieces(poly, mid, frame.right)
            if not pieces:
                warnings.append(f"{anchor} of {pid}: no parcel width at mid depth; used centre")
                return self._anchor_ai(pid, frame, "centre", warnings)
            lo, hi = pieces[0][0], pieces[-1][1]
            width_margin = min(ANCHOR_MARGIN_M, (hi - lo) / 4)
            return (lo + width_margin if anchor == "left" else hi - width_margin), depth / 2
        if anchor == "near_building":
            ids = [b for b in m.parcels[pid].get("buildings", []) if b in m.bpolys]
            if not ids:
                warnings.append(f"{pid} has no building; near_building used the centre")
                return self._anchor_ai(pid, frame, "centre", warnings)
            bp = max((m.bpolys[b] for b in ids), key=lambda g: g.area)
            c = bp.centroid
            dx, dy = frame.origin[0] - c.x, frame.origin[1] - c.y
            norm = math.hypot(dx, dy) or 1.0
            d = (dx / norm, dy / norm)  # building centre -> front edge
            pieces = _line_pieces(bp, (c.x, c.y), d)
            exit_t = max((t1 for t0, t1 in pieces if t0 <= 0 <= t1), default=0.0)
            t = exit_t + NEAR_BUILDING_M
            return frame.to_ai(c.x + d[0] * t, c.y + d[1] * t)
        # centre
        p = poly.centroid if poly.covers(poly.centroid) else poly.representative_point()
        return frame.to_ai(p.x, p.y)

    def _placement_warnings(
        self,
        pid: str,
        xy: tuple[float, float],
        asset: dict,
        rotation: float,
        scale: float,
        edits: Edits,
        skip_id: str | None,
    ) -> list[str]:
        m = self.map
        warnings: list[str] = []
        pt = Point(xy)
        if not m.polys[pid].covers(pt):
            inside = m.parcel_at(*xy)
            where = f"inside {inside}" if inside else "outside every parcel"
            warnings.append(f"position is outside {pid} ({where}); placed anyway as requested")
        fp = object_polygon(xy, asset.get("size_m"), rotation, scale)
        for bid in m.buildings_near(fp, 0.0):
            warnings.append(
                f"overlaps building {bid} (approximate footprint of {asset['id']} at this "
                "rotation and scale)"
            )
        for opid, other in _all_objects(edits):
            if other["id"] == skip_id:
                continue
            ox, oy = m.to_local(*other["position"])
            if math.hypot(ox - xy[0], oy - xy[1]) > OVERLAP_SEARCH_M:
                continue
            osz = next(
                (a.get("size_m") for a in self.manifest["assets"] if a["id"] == other["asset"]), None
            )
            ofp = object_polygon(
                (ox, oy), osz, float(other.get("rotation_deg", 0)), float(other.get("scale", 1))
            )
            if fp.intersects(ofp):
                warnings.append(f"overlaps placed object {other['id']} ({other['asset']}, in {opid})")
        size = asset.get("size_m") or [1, 1, 1]
        if max(size) < 1.0 and scale == 1.0:
            warnings.append(
                f"{asset['id']} is small at scale 1 (largest side {max(size):.2f} m); pass scale "
                "if it should be bigger"
            )
        return warnings

    def place_object(
        self,
        parcel: str,
        asset: str,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
    ) -> dict:
        """Place an object on a parcel (see the registered tool description)."""
        pid = self.map.need(parcel)
        info = self._asset(asset)
        if scale is not None:
            _check_num("scale", scale, positive=True)
        if rotation_deg is not None:
            _check_num("rotation_deg", rotation_deg)
        if colour is not None and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(colour)):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        warnings: list[str] = []
        x, y, frame = self._place_point(pid, anchor, offset_m, position, warnings)
        rot = _norm_deg(rotation_deg) if rotation_deg is not None else _facing_rotation(frame.side.normal)
        scl = float(scale) if scale is not None else 1.0
        absolute = self.map.to_abs(x, y)

        def change(edits: Edits) -> dict:
            oid = _next_id("o", [o["id"] for _, o in _all_objects(edits)])
            entry: dict = {"id": oid, "asset": asset, "position": absolute, "rotation_deg": rot}
            if scale is not None:
                entry["scale"] = scl
            if colour is not None:
                entry["colour"] = colour
            warnings.extend(self._placement_warnings(pid, (x, y), info, rot, scl, edits, None))
            edits.parcels.setdefault(pid, {}).setdefault("objects", []).append(entry)
            return entry

        entry = self._write(change)
        return {
            "summary": f"placed {entry['id']} ({asset}) on {pid} at {absolute}, rotation {rot} deg"
            + (f", {len(warnings)} warning(s)" if warnings else ""),
            "id": entry["id"],
            "parcel": pid,
            "position": absolute,
            "rotation_deg": rot,
            "scale": scl,
            "entry": entry,
            "warnings": warnings,
        }

    def remove_object(self, id: str) -> dict:
        """Remove a placed object by ID; the cables hanging from it (a utility pole) go with it."""
        dropped: list[str] = []

        def change(edits: Edits) -> str:
            for pid, e in edits.parcels.items():
                objs = e.get("objects", [])
                for o in objs:
                    if o["id"] == id:
                        objs.remove(o)
                        if not objs:
                            del e["objects"]
                        for cid in [c for c, cb in edits.cables.items() if id in (cb["from"], cb["to"])]:
                            dropped.append(cid)
                            del edits.cables[cid]
                        return pid
            known = [o["id"] for _, o in _all_objects(edits)]
            raise ToolError(
                f"no placed object {id!r}. Existing ids: {known[:20] if known else 'none'}. "
                "get_parcel lists a parcel's objects."
            )

        pid = self._write(change)
        note = f" and its cable{'s' if len(dropped) > 1 else ''} {', '.join(dropped)}" if dropped else ""
        return {"summary": f"removed {id} from {pid}{note}", "id": id, "parcel": pid,
                "cables_removed": dropped, "warnings": []}

    # cables ----------------------------------------------------------------------------------
    def _pole(self, edits: Edits, oid: object) -> dict:
        """The placed utility pole with this id."""
        for _, o in _all_objects(edits):
            if o["id"] == oid:
                if not o["asset"].startswith(POLE_ASSET_PREFIX):
                    raise ToolError(
                        f"{oid} is {o['asset']}, not a utility pole. Cables hang between objects placed "
                        f"from the pole assets (list_assets category \"pole\")."
                    )
                return o
        poles = [o["id"] for _, o in _all_objects(edits) if o["asset"].startswith(POLE_ASSET_PREFIX)]
        raise ToolError(f"no placed object {oid!r}. Placed poles: {poles[:30] if poles else 'none'}.")

    @staticmethod
    def _cable_row(cid: str, cable: dict, edits: Edits) -> dict:
        pos = {o["id"]: o["position"] for _, o in _all_objects(edits)}
        a, b = pos.get(cable["from"]), pos.get(cable["to"])
        length = _r3(math.hypot(a[0] - b[0], a[1] - b[1])) if a and b else None
        return {"id": cid, "from": cable["from"], "to": cable["to"], "length_m": length}

    def connect_poles(self, poles: list[str]) -> dict:
        """String cables along a chain of placed poles: each to the next."""
        if not isinstance(poles, list) or len(poles) < 2:
            raise ToolError("poles must list two or more placed pole ids, in the order the line runs")
        made: list[dict] = []
        skipped: list[str] = []

        def change(edits: Edits) -> None:
            for oid in poles:
                self._pole(edits, oid)
            for a, b in itertools.pairwise(poles):
                if a == b:
                    raise ToolError(f"{a} is listed twice in a row; a cable joins two different poles")
                pair = cable_pair({"from": a, "to": b})
                old = next((c for c, cb in edits.cables.items() if cable_pair(cb) == pair), None)
                if old:
                    skipped.append(f"{a} and {b} are already joined by {old}")
                    continue
                cid = _next_id("C", list(edits.cables))
                edits.cables[cid] = {"from": a, "to": b}
                made.append(self._cable_row(cid, edits.cables[cid], edits))

        self._write(change)
        summary = (
            f"strung {len(made)} cable{'' if len(made) == 1 else 's'}: "
            + ", ".join(f"{c['id']} {c['from']} to {c['to']}" for c in made)
            if made else "no new cable"
        )
        return {"summary": summary, "cables": made, "warnings": skipped}

    def disconnect_poles(self, cable: str | None = None, poles: list[str] | None = None) -> dict:
        """Remove one cable, by its id or by the two poles it joins."""
        if (cable is None) == (poles is None):
            raise ToolError("give either cable (an id like C-0001) or poles (the two pole ids)")

        def change(edits: Edits) -> tuple[str, dict]:
            if cable is not None:
                if cable not in edits.cables:
                    raise ToolError(f"no cable {cable!r}. Existing: {sorted(edits.cables)[:30] or 'none'}.")
                return cable, edits.cables.pop(cable)
            if not isinstance(poles, list) or len(poles) != 2:
                raise ToolError("poles must be the two pole ids the cable joins")
            pair = cable_pair({"from": poles[0], "to": poles[1]})
            cid = next((c for c, cb in edits.cables.items() if cable_pair(cb) == pair), None)
            if cid is None:
                raise ToolError(f"{poles[0]} and {poles[1]} are not joined by a cable")
            return cid, edits.cables.pop(cid)

        cid, entry = self._write(change)
        return {"summary": f"removed {cid} ({entry['from']} to {entry['to']})", "id": cid, "entry": entry,
                "warnings": []}

    def list_cables(self, pole: str | None = None) -> dict:
        """The cables in the file, or those of one pole, and the placed poles."""
        edits = _read_edits(self.settings.edits_path)
        rows = [self._cable_row(cid, cb, edits) for cid, cb in sorted(edits.cables.items())
                if pole is None or pole in (cb["from"], cb["to"])]
        placed = {o["id"]: o for _, o in _all_objects(edits)}
        warnings = [f"{r['id']}: {end} is not a placed utility pole (the cable is skipped)"
                    for r in rows for end in (r["from"], r["to"])
                    if not placed.get(end, {}).get("asset", "").startswith(POLE_ASSET_PREFIX)]
        poles = [{"id": o["id"], "asset": o["asset"], "position": o["position"],
                  "rotation_deg": o.get("rotation_deg", 0),
                  "cables": sum(o["id"] in (cb["from"], cb["to"]) for cb in edits.cables.values())}
                 for o in placed.values() if o["asset"].startswith(POLE_ASSET_PREFIX)]
        return {"summary": f"{len(rows)} cable{'' if len(rows) == 1 else 's'}, {len(poles)} placed poles",
                "cables": rows, "poles": sorted(poles, key=lambda p: p["id"]), "warnings": warnings}

    def move_object(
        self,
        id: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
    ) -> dict:
        """Change position, rotation, scale or colour of a placed object (see tool description)."""
        if scale is not None:
            _check_num("scale", scale, positive=True)
        if rotation_deg is not None:
            _check_num("rotation_deg", rotation_deg)
        if colour is not None and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(colour)):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        if all(v is None for v in (parcel, anchor, offset_m, position, rotation_deg, scale, colour)):
            raise ToolError("nothing to change: give a new anchor/offset_m/position/rotation_deg/scale/colour")
        relocate = any(v is not None for v in (parcel, anchor, offset_m, position))
        warnings: list[str] = []
        path = self.settings.edits_path
        with edits_lock(path):
            edits = _read_edits(path)
            owner = next((p for p, o in _all_objects(edits) if o["id"] == id), None)
            if owner is None:
                raise ToolError(f"no placed object {id!r}; get_parcel lists a parcel's objects.")
            obj = next(o for o in edits.parcels[owner]["objects"] if o["id"] == id)
            target = self.map.need(parcel) if parcel else owner
            new = dict(obj)
            if relocate:
                if position is None and anchor is None and offset_m is not None:
                    anchor = "centre"
                x, y, frame = self._place_point(target, anchor, offset_m, position, warnings)
                new["position"] = self.map.to_abs(x, y)
                if rotation_deg is None and (anchor is not None or parcel is not None):
                    new["rotation_deg"] = _facing_rotation(frame.side.normal)
            if rotation_deg is not None:
                new["rotation_deg"] = _norm_deg(rotation_deg)
            if scale is not None:
                new["scale"] = float(scale)
            if colour is not None:
                new["colour"] = colour
            info = self._asset(new["asset"])
            lx, ly = self.map.to_local(*new["position"])
            warnings.extend(
                self._placement_warnings(
                    target, (lx, ly), info, float(new.get("rotation_deg", 0)),
                    float(new.get("scale", 1)), edits, id,
                )
            )
            if target != owner:
                edits.parcels[owner]["objects"].remove(obj)
                if not edits.parcels[owner]["objects"]:
                    del edits.parcels[owner]["objects"]
                edits.parcels.setdefault(target, {}).setdefault("objects", []).append(new)
            else:
                edits.parcels[owner]["objects"][edits.parcels[owner]["objects"].index(obj)] = new
            _empty_parcel_cleanup(edits)
            errors = validate(_edits_doc(edits))
            if errors:
                raise ToolError(
                    "refused: the change would make edits.json invalid, nothing was written:\n"
                    + _bullets(errors)
                )
            dump_edits(edits, path)
        return {
            "summary": f"moved {id} on {target} to {new['position']}, rotation {new.get('rotation_deg', 0)} deg"
            + (f", {len(warnings)} warning(s)" if warnings else ""),
            "id": id,
            "parcel": target,
            "entry": new,
            "warnings": warnings,
        }

    # groups --------------------------------------------------------------------------------
    def _group_fence(self, fence: dict | None) -> dict | None:
        """Validate a group fence given to a tool call; None when none was given."""
        if fence is None:
            return None
        if not isinstance(fence, dict):
            raise ToolError("fence must be an object like {mode, type, height_m, ...}")
        mode = fence.get("mode")
        custom = {k: v for k, v in fence.items() if k != "mode"}
        if "gaps" in custom:
            custom["gaps"] = self._gaps(custom["gaps"], "group", "")
        if mode in ("auto", "none") and custom:
            raise ToolError(f"mode {mode!r} cannot be combined with {', '.join(custom)}")
        unknown = set(custom) - set(CUSTOM_FENCE_KEYS)
        if unknown:
            raise ToolError(f"unknown fence keys: {sorted(unknown)}")
        return {**({"mode": mode} if mode else {}), **custom}

    def create_group(self, parcels: list[str], fence: dict | None = None) -> dict:
        """Create a property group (see the registered tool description)."""
        if not isinstance(parcels, list) or len(parcels) < 2:
            raise ToolError("a group needs 2 or more parcels (give a list of parcel IDs)")
        ids = sorted({self.map.need(p) for p in parcels})
        if len(ids) < 2:
            raise ToolError("a group needs 2 or more different parcels")
        fence_entry = self._group_fence(fence)
        warnings: list[str] = []
        union = unary_union([self.map.polys[p].buffer(0.3) for p in ids])
        if union.geom_type != "Polygon":
            warnings.append(
                "the parcels are not all touching each other; the group fence runs around each "
                "separate piece"
            )

        def change(edits: Edits) -> str:
            taken = {p: self._group_of(edits, p) for p in ids if self._group_of(edits, p)}
            if taken:
                raise ToolError(
                    "already in a group: "
                    + ", ".join(f"{p} ({g})" for p, g in sorted(taken.items()))
                    + ". dissolve_group first."
                )
            gid = _next_id("G", list(edits.groups))
            group: dict = {"parcels": ids}
            if fence_entry is not None:
                group["fence"] = fence_entry
            edits.groups[gid] = group
            return gid

        gid = self._write(change)
        entry = load_edits(self.settings.edits_path).groups[gid]
        return {
            "summary": f"created {gid} from {', '.join(ids)}",
            "id": gid,
            "entry": entry,
            "warnings": warnings,
        }

    def merge_groups(
        self, ids: list[str], fence: dict | None = None, parcels: list[str] | None = None
    ) -> dict:
        """Merge property groups (and loose parcels) into a new group (see the registered tool description)."""
        extra = sorted({self.map.need(p) for p in (parcels or [])})
        if not isinstance(ids, list) or len(ids) < (1 if extra else 2):
            raise ToolError("merging needs 2 or more group IDs like G-0001 (or 1 plus `parcels`)")
        for i in ids:
            if not (isinstance(i, str) and _GROUP_ID.fullmatch(i)):
                raise ToolError(f"{i!r} is not a group ID like G-0001")
        if len(set(ids)) < len(ids):
            raise ToolError("merging needs 2 or more different groups (or 1 plus `parcels`)")
        given = self._group_fence(fence)
        warnings: list[str] = []

        def change(edits: Edits) -> tuple[str, dict, list[str]]:
            missing = [i for i in ids if i not in edits.groups]
            if missing:
                raise ToolError(f"no such group: {', '.join(missing)}; groups: {sorted(edits.groups) or 'none'}")
            taken = {p: self._group_of(edits, p) for p in extra if self._group_of(edits, p) not in (None, *ids)}
            if taken:
                raise ToolError(
                    "already in another group: "
                    + ", ".join(f"{p} ({g})" for p, g in sorted(taken.items()))
                )
            members = sorted({p for i in ids for p in edits.groups[i]["parcels"]} | set(extra))
            if given is not None:
                chosen = given
            else:
                fences = [edits.groups[i].get("fence") for i in ids]
                distinct = {json.dumps(f, sort_keys=True) for f in fences if f is not None}
                chosen = next((f for f in fences if f is not None), None)
                if len(distinct) > 1:
                    warnings.append(
                        f"the groups had different fences; kept the one of "
                        f"{next(i for i in ids if edits.groups[i].get('fence') is not None)} "
                        "(pass `fence` to choose)"
                    )
            for i in ids:
                del edits.groups[i]
            gid = _next_id("G", list(edits.groups))
            group: dict = {"parcels": members}
            if chosen is not None:
                group["fence"] = chosen
            edits.groups[gid] = group
            return gid, group, members

        gid, group, members = self._write(change)
        union = unary_union([self.map.polys[p].buffer(0.3) for p in members])
        if union.geom_type != "Polygon":
            warnings.append(
                "the parcels are not all touching each other; the group fence runs around each "
                "separate piece"
            )
        return {
            "summary": f"merged {', '.join(ids)} into {gid} ({', '.join(members)})",
            "id": gid,
            "entry": group,
            "merged": ids,
            "warnings": warnings,
        }

    def dissolve_group(self, id: str) -> dict:
        """Remove a property group (parcel fences of its members stay as they are)."""
        if not (isinstance(id, str) and _GROUP_ID.fullmatch(id)):
            raise ToolError(f"{id!r} is not a group ID like G-0001")

        def change(edits: Edits) -> dict:
            if id not in edits.groups:
                raise ToolError(f"group {id} does not exist; groups: {sorted(edits.groups) or 'none'}")
            return edits.groups.pop(id)

        group = self._write(change)
        return {
            "summary": f"dissolved {id} ({', '.join(group['parcels'])})",
            "id": id,
            "entry": group,
            "warnings": [],
        }


    # regions -------------------------------------------------------------------------------
    @staticmethod
    def _region_colour(colour: object) -> str:
        if not (isinstance(colour, str) and _COLOUR.fullmatch(colour)):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        return colour.lower()

    @staticmethod
    def _region_id(id: object) -> str:
        if not (isinstance(id, str) and _REGION_ID.fullmatch(id)):
            raise ToolError(f"{id!r} is not a region ID like R-0001. list_regions shows the existing ones.")
        return id

    @staticmethod
    def _xy(name: str, p: object) -> list[float]:
        if not (isinstance(p, (list, tuple)) and len(p) == 2):
            raise ToolError(f"{name} must be [x, y] in absolute S-JTSK metres, got {p!r}")
        return _pt(_check_num(f"{name}[0]", p[0]), _check_num(f"{name}[1]", p[1]))

    def _region_entry(self, edits: Edits, rid: str) -> dict:
        if rid not in edits.regions:
            raise ToolError(
                f"region {rid} does not exist; regions: {sorted(edits.regions) or 'none'}. "
                "Call list_regions to see them."
            )
        return edits.regions[rid]

    @staticmethod
    def _region_row(rid: str, entry: dict) -> dict:
        ring = region_ring(entry["polygon"])
        poly = Polygon(ring)
        c = poly.centroid
        return {
            "id": rid,
            "colour": entry["colour"],
            **({"water_y": entry["water_y"]} if "water_y" in entry else {}),
            "area_m2": round(poly.area, 1),
            "points": len(entry["polygon"]),
            "bbox": [_r3(v) for v in poly.bounds],
            "centroid": _pt(c.x, c.y),
        }

    @staticmethod
    def _overlap_warnings(edits: Edits, rid: str) -> list[str]:
        mine = Polygon(region_ring(edits.regions[rid]["polygon"]))
        if not mine.is_valid:
            return []
        out = []
        for oid, other in sorted(edits.regions.items()):
            if oid == rid:
                continue
            op = Polygon(region_ring(other["polygon"]))
            if op.is_valid and mine.intersection(op).area > 0.01:
                winner = max(rid, oid, key=region_rank)
                out.append(f"overlaps region {oid}; where they overlap {winner} wins (higher ID)")
        return out

    def create_region(
        self,
        colour: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list | None = None,
        water_y: float | None = None,
    ) -> dict:
        """Create a region (see the registered tool description)."""
        level = None if water_y is None else _check_num("water_y", water_y)
        col = self._region_colour(colour)
        if (polygon is None) == (points is None):
            raise ToolError(
                "give exactly one of polygon (absolute [x, y] points) or parcel + points "
                "(parcel-relative along/inward metres)"
            )
        if polygon is not None:
            if parcel is not None:
                raise ToolError("parcel only goes with points; polygon is absolute S-JTSK metres")
            if not isinstance(polygon, list):
                raise ToolError("polygon must be a list of [x, y] points")
            pts = [self._xy(f"polygon[{i}]", p) for i, p in enumerate(polygon)]
        else:
            if parcel is None:
                raise ToolError("points are relative to a parcel: pass parcel too (e.g. P-0001)")
            pid = self.map.need(parcel)
            if not isinstance(points, list):
                raise ToolError("points must be a list of [along, inward] metres")
            frame, _ = self.map.frame(pid)
            pts = []
            for i, q in enumerate(points):
                if isinstance(q, dict) and set(q) <= {"along", "inward"}:
                    q = [q.get("along", 0.0), q.get("inward", 0.0)]
                if not (isinstance(q, (list, tuple)) and len(q) == 2):
                    raise ToolError(f"points[{i}] must be [along, inward] or {{along, inward}}, got {q!r}")
                a = _check_num(f"points[{i}].along", q[0])
                d = _check_num(f"points[{i}].inward", q[1])
                pts.append(self.map.to_abs(*frame.at(a, d)))
        if len(pts) > 1 and pts[0] == pts[-1]:
            pts.pop()  # the file does not need the closing point
        if len(pts) < 3:
            raise ToolError(f"a region needs at least 3 points, got {len(pts)}. Nothing was changed.")

        def change(edits: Edits) -> dict:
            rid = _next_id("R", list(edits.regions))
            edits.regions[rid] = {"colour": col, "polygon": pts, **({} if level is None else {"water_y": level})}
            return {"id": rid, "warnings": self._overlap_warnings(edits, rid)}

        out = self._write(change)
        row = self._region_row(out["id"], {"colour": col, "polygon": pts, **({} if level is None else {"water_y": level})})
        return {
            "summary": f"created {out['id']} ({col}, {row['points']} points, {row['area_m2']} m2)"
            + (f", {len(out['warnings'])} warning(s)" if out["warnings"] else ""),
            **row,
            "polygon": pts,
            "warnings": out["warnings"],
        }

    def set_region_water(self, id: str, water_y: float | None) -> dict:
        """Make a region a body of water at a flat height (terrain datum, metres), or None to make it ground paint again."""
        rid = self._region_id(id)
        level = None if water_y is None else _check_num("water_y", water_y)

        def change(edits: Edits) -> object:
            entry = self._region_entry(edits, rid)
            old = entry.get("water_y")
            if level is None:
                entry.pop("water_y", None)
            else:
                entry["water_y"] = level
            return old

        old = self._write(change)
        return {"summary": f"{rid} water_y {old} -> {level}", "id": rid, "water_y": level, "warnings": []}

    def set_region_colour(self, id: str, colour: str) -> dict:
        """Change a region's colour."""
        rid = self._region_id(id)
        col = self._region_colour(colour)

        def change(edits: Edits) -> str:
            entry = self._region_entry(edits, rid)
            old = entry["colour"]
            entry["colour"] = col
            return old

        old = self._write(change)
        return {"summary": f"{rid} colour {old} -> {col}", "id": rid, "colour": col, "warnings": []}

    def move_region_point(
        self,
        id: str,
        index: int,
        position: list[float] | None = None,
        offset: dict | None = None,
    ) -> dict:
        """Move one polygon point to a position or by an offset."""
        rid = self._region_id(id)
        if isinstance(index, bool) or not isinstance(index, int):
            raise ToolError(f"index must be an integer, got {index!r}")
        if (position is None) == (offset is None):
            raise ToolError('give exactly one of position [x, y] or offset {"dx": m, "dy": m}')
        if position is not None:
            target: list[float] | None = self._xy("position", position)
        else:
            if not isinstance(offset, dict) or set(offset) - {"dx", "dy"}:
                raise ToolError('offset must be {"dx": metres east, "dy": metres north}')
            dx = _check_num("offset.dx", offset.get("dx", 0.0))
            dy = _check_num("offset.dy", offset.get("dy", 0.0))
            target = None

        def change(edits: Edits) -> dict:
            entry = self._region_entry(edits, rid)
            poly = entry["polygon"]
            if not 0 <= index < len(poly):
                raise ToolError(
                    f"index {index} is out of range: {rid} has {len(poly)} points (0..{len(poly) - 1}). "
                    "list_regions shows the point count."
                )
            old = poly[index]
            new = target if target is not None else _pt(old[0] + dx, old[1] + dy)
            poly[index] = new
            return {"old": old, "new": new, "warnings": self._overlap_warnings(edits, rid)}

        out = self._write(change)
        return {
            "summary": f"moved {rid} point {index} {out['old']} -> {out['new']}",
            "id": rid,
            "index": index,
            "old": out["old"],
            "position": out["new"],
            "warnings": out["warnings"],
        }

    def delete_region(self, id: str) -> dict:
        """Delete a region."""
        rid = self._region_id(id)

        def change(edits: Edits) -> dict:
            self._region_entry(edits, rid)
            return edits.regions.pop(rid)

        entry = self._write(change)
        return {"summary": f"deleted {rid} ({entry['colour']})", "id": rid, "entry": entry, "warnings": []}

    def list_regions(self, near: dict | None = None, limit: int = 50) -> dict:
        """List regions, optionally those near a point or parcel."""
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
            raise ToolError("limit must be an integer >= 1")
        edits = _read_edits(self.settings.edits_path)
        rows = [self._region_row(rid, e) for rid, e in sorted(edits.regions.items())]
        if near is not None:
            if not isinstance(near, dict):
                raise ToolError('near must be {"x": .., "y": ..} or {"parcel": "P-0001", "radius_m": ..}')
            radius = _check_num("near.radius_m", near.get("radius_m", 100.0), positive=True)
            if "parcel" in near:
                src: Any = shapely.affinity.translate(
                    self.map.polys[self.map.need(near["parcel"])], *self.map.origin
                )
            elif "x" in near and "y" in near:
                src = Point(_check_num("near.x", near["x"]), _check_num("near.y", near["y"]))
            else:
                raise ToolError('near needs "x" and "y" (S-JTSK metres) or "parcel"')
            keep = []
            for row in rows:
                d = Polygon(region_ring(edits.regions[row["id"]]["polygon"])).distance(src)
                if d <= radius:
                    keep.append({**row, "distance_m": round(d, 1)})
            rows = sorted(keep, key=lambda r: (r["distance_m"], r["id"]))
        return {"count": len(rows[:limit]), "total_matching": len(rows), "regions": rows[:limit]}


# --- MCP registration -----------------------------------------------------------------------

_ASSET_NOTE = (
    "Sizes are the raw bounding boxes in metres [x, y, z] (y up). Some packs are authored small "
    "(the Kenney suburban kit: a tree is 0.77 m, a fence section 0.5 m), so a `scale` on "
    "place_object may be needed; cars and primitives are real size."
)


def build_server(settings: Settings) -> Any:
    """An MCP server (stdio or in-memory) exposing the tools over ``settings``."""
    from mcp.server.mcpserver import MCPServer

    tools = ParcelEditTools(settings)
    server = MCPServer(
        "suchohrad-parcel-edits",
        instructions=(
            "Edit parcels of the Suchohrad map: fences, trees, placed objects, property "
            "groups and ground-colour regions, all stored in edits.json (a viewer in --editor --watch "
            "mode shows changes within about a second). Coordinates are absolute S-JTSK metres (x east, y north). "
            "Typical flow: find_parcels (selected=true for what the user selected) -> get_parcel "
            "-> set_fence / set_trees / place_object; create_region for ground colour."
        ),
    )

    @server.tool(description=f"List assets that place_object can use. Each has id (use as `asset`), "
                 f"category (car, slab, pot, flower, bush, tree, wall, fence, pole, other), name, size_m "
                 f"and tags; primitives (prim:wall, prim:slab, prim:pot, prim:radio_tower, prim:bunker and the "
                 f"utility poles prim:pole_*, category pole: origin at the foot, the wires run along the object's "
                 f"x axis, the lamp reaches to its -y) are included. "
                 f"Optional filter `category`. {_ASSET_NOTE}")
    def list_assets(category: str | None = None) -> dict:
        return tools.list_assets(category)

    @server.tool(description=(
        "Find parcels. Filters: `near` = {\"x\": .., \"y\": ..} in S-JTSK metres with optional "
        "radius_m (default 100), or {\"parcel\": \"P-0412\", \"radius_m\": 50} for parcels around "
        "another one; `kind` (building_plot, road, forest, field, water, garden, other); "
        "`selected: true` returns the parcels currently selected in the viewer's editor mode "
        "(read from editor-state.json; use this for 'the selected parcel' / 'this house' and "
        "also returns the camera). Results (up to `limit`, default 20; nearest first with `near`) "
        "have id, kind, area_m2, anchor (a point inside, S-JTSK), group and has_edits."))
    def find_parcels(
        near: dict | None = None, kind: str | None = None, selected: bool = False, limit: int = 20
    ) -> dict:
        return tools.find_parcels(near, kind, selected, limit)

    @server.tool(description=(
        "Everything needed to edit one parcel: kind, area, cadastral ref, polygon bounds and "
        "centroid, edges with lengths, the FRONT EDGE (the side nearest a road: midpoint, outward "
        "normal and the axes used by place_object: `along` grows to the right when standing on "
        "the front edge looking into the parcel, `inward` grows away from the road), buildings on "
        "it (footprint centroid, bounds), neighbouring parcels, its group, the current edits "
        "(fence, trees, objects), the effective fence after group inheritance, and the rules' "
        "defaults (fence probability, tree density for its kind). Coordinates are S-JTSK metres."))
    def get_parcel(id: str) -> dict:
        return tools.get_parcel(id)

    @server.tool(description=(
        "Set the fence of a parcel (P-0412) or of a property group (G-0001: one fence around the "
        "group's outer edge). `mode`: auto (the generator's rules), none (no fence) or custom "
        "(always fenced). `type` (picket | wire | brick_red | brick_white | stone_wall | concrete; "
        "textured, each with its own posts or piers), `column` (none | wood_post | metal_post | "
        "brick_red | brick_white | stone | concrete: other columns than the type's own, e.g. picket "
        "between brick_red piers), `height_m`, `thickness_m`, `colour` (\"#rrggbb\": a tint; it is the "
        "paint of picket, wire and concrete, while brick and stone have their own colours and default "
        "to white) and `gaps` (gates: list of {\"at\": [x, y] or \"front\", \"width_m\": n}) are "
        "custom-only: giving any of them without a mode means custom; combining them with mode "
        "auto/none is an error. Given keys are merged into the existing fence entry. A parcel that "
        "is in a group gets an override on top of the group's fence (a warning says so)."))
    def set_fence(
        target: str,
        mode: str | None = None,
        type: str | None = None,
        height_m: float | None = None,
        thickness_m: float | None = None,
        colour: str | None = None,
        gaps: list[dict] | None = None,
        column: str | None = None,
    ) -> dict:
        return tools.set_fence(target, mode, type, height_m, thickness_m, colour, gaps, column)

    @server.tool(description="Remove the fence edit of a parcel or group so the rules (or the group's fence) apply again.")
    def reset_fence(target: str) -> dict:
        return tools.reset_fence(target)

    @server.tool(description=(
        "Set the generated trees of a parcel: exactly one of `density_per_1000m2` (trees per "
        "1000 m2) or `count` (exact number), and optionally `seed_offset` (an integer that "
        "rerolls the layout; a seed_offset alone is accepted only when the parcel already has a "
        "density or count). Generated trees avoid buildings, roads and placed objects."))
    def set_trees(
        parcel: str,
        density_per_1000m2: float | None = None,
        count: int | None = None,
        seed_offset: int | None = None,
    ) -> dict:
        return tools.set_trees(parcel, density_per_1000m2, count, seed_offset)

    @server.tool(description="Remove the trees edit of a parcel so the rules' density applies again.")
    def reset_trees(parcel: str) -> dict:
        return tools.reset_trees(parcel)

    @server.tool(description=(
        "Place an asset (see list_assets) on a parcel. Position is either `anchor` = front | back "
        "| left | right | centre | near_building (default centre) plus optional `offset_m` "
        "{\"along\": m, \"inward\": m}, or an exact `position` [x, y] in S-JTSK metres. Anchors "
        "are relative to the parcel's front edge (the side nearest a road; see get_parcel): "
        "left/right as seen standing on the front edge looking into the parcel, along > 0 = "
        "further right, inward > 0 = further from the road. `near_building` is between the "
        "building and the front edge. An anchor-based point is kept inside the parcel (moved to "
        "the nearest interior point with a warning). Default rotation turns the object's front "
        "toward the road (right for a car parked in front of the house); `rotation_deg` is "
        "counter-clockwise seen from above, absolute. `scale` is uniform, default 1 (small pack "
        "models may need it); `colour` is \"#rrggbb\". Unknown asset/parcel and bad values are "
        "errors; overlaps with buildings or other objects and positions outside the parcel are "
        "applied and reported in `warnings`. Returns the new object's id, final position, rotation."))
    def place_object(
        parcel: str,
        asset: str,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
    ) -> dict:
        return tools.place_object(
            parcel, asset, anchor, offset_m, position, rotation_deg, scale, colour
        )

    @server.tool(description="Remove a placed object by its id (e.g. o-0003). The cables hanging from a "
                 "removed utility pole are removed with it.")
    def remove_object(id: str) -> dict:
        return tools.remove_object(id)

    @server.tool(description=(
        "String cables between placed utility poles (objects placed from the prim:pole_* assets). `poles` is "
        "a list of two or more pole object ids in the order the line runs; each pole is joined to the next, "
        "so a whole street is one call. A pair that is already joined is left alone (reported in warnings). "
        "The poles keep their rotation: the wires are matched left to right across each span, so they never "
        "cross; turn a pole with move_object so its crossarm stands across the line. list_cables shows the poles."))
    def connect_poles(poles: list[str]) -> dict:
        return tools.connect_poles(poles)

    @server.tool(description="Remove one cable: `cable` (an id like C-0001) or `poles` (the two pole ids it joins).")
    def disconnect_poles(cable: str | None = None, poles: list[str] | None = None) -> dict:
        return tools.disconnect_poles(cable, poles)

    @server.tool(description=(
        "List the cables (id, from, to, length_m) and the placed utility poles (id, asset, position, "
        "rotation_deg, number of cables). Optional `pole`: only that pole's cables."))
    def list_cables(pole: str | None = None) -> dict:
        return tools.list_cables(pole)

    @server.tool(description=(
        "Change a placed object: new position (`anchor` + `offset_m` relative to `parcel`, "
        "default the parcel that lists it, or an exact `position`), `rotation_deg`, `scale` or "
        "`colour`. With a new anchor and no rotation_deg the object turns to face the road "
        "again; a new `parcel` re-lists the object under that parcel."))
    def move_object(
        id: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
    ) -> dict:
        return tools.move_object(id, parcel, anchor, offset_m, position, rotation_deg, scale, colour)

    @server.tool(description=(
        "Create a property group (id G-NNNN) from 2 or more parcels that belong to one owner: "
        "one fence runs around their combined outer edge (none on the boundaries between "
        "them). A parcel can be in one group only. Optional `fence` takes the set_fence keys "
        "(mode, type, height_m, thickness_m, colour, gaps)."))
    def create_group(parcels: list[str], fence: dict | None = None) -> dict:
        return tools.create_group(parcels, fence)

    @server.tool(description=(
        "Merge 2 or more property groups (G-0001, G-0002, ...) into one new group (id G-NNNN) "
        "holding all their parcels; the old groups disappear. With `parcels` (loose parcels that "
        "are in no other group) one group is enough: group + parcels -> new group. Parcel-level fence edits stay on "
        "the parcels. The new group keeps the fence of the old ones when they agree; otherwise "
        "the first listed group's fence is kept and a warning says so. Optional `fence` "
        "(mode, type, height_m, thickness_m, colour, gaps) replaces it."))
    def merge_groups(ids: list[str], fence: dict | None = None, parcels: list[str] | None = None) -> dict:
        return tools.merge_groups(ids, fence, parcels)

    @server.tool(description="Dissolve a property group (id G-0001). Parcel ids, boundaries and the members' own fence edits stay.")
    def dissolve_group(id: str) -> dict:
        return tools.dissolve_group(id)

    @server.tool(description=(
        "Create a region (id R-NNNN): a flat-coloured shape painted on the ground, independent "
        "of parcel boundaries (a meadow, a gravel yard, a path). `colour` is \"#rrggbb\". Give "
        "exactly one shape form: `polygon` = [[x, y], ...] in absolute S-JTSK metres, or `parcel` "
        "+ `points` = [[along, inward], ...] metres in that parcel's front-edge frame (same axes "
        "as place_object: along grows right, inward grows away from the road; see get_parcel). "
        "Needs 3+ points; edges may not cross or touch each other. Invalid shapes are refused "
        "and nothing is written. Where regions overlap the higher ID wins (reported in "
        "`warnings`). Optional `water_y` makes it a body of water with a flat surface at that height "
        "(metres, terrain datum): see set_region_water. Returns the id, area, bbox and the final "
        "absolute polygon."))
    def create_region(
        colour: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list[list[float]] | None = None,
        water_y: float | None = None,
    ) -> dict:
        return tools.create_region(colour, polygon, parcel, points, water_y)

    @server.tool(description=(
        "Make a region a real body of water: a flat surface at `water_y` (metres on the terrain's "
        "datum, e.g. 185.6) that stays level over the terrain, also written to water.json "
        "(swimming, the shooter's water surface). The region's colour tints it. `water_y` null "
        "turns it back into ground paint. Raise or lower the level by calling this again."))
    def set_region_water(id: str, water_y: float | None) -> dict:
        return tools.set_region_water(id, water_y)

    @server.tool(description="Change a region's colour (\"#rrggbb\"). Use list_regions for ids.")
    def set_region_colour(id: str, colour: str) -> dict:
        return tools.set_region_colour(id, colour)

    @server.tool(description=(
        "Move one point of a region's polygon (`index`, 0-based as in the file) to an absolute "
        "`position` [x, y] or by an `offset` {\"dx\": east m, \"dy\": north m}; give exactly one. "
        "A move that would make the polygon self-intersect is refused and nothing is written."))
    def move_region_point(
        id: str, index: int, position: list[float] | None = None, offset: dict | None = None
    ) -> dict:
        return tools.move_region_point(id, index, position, offset)

    @server.tool(description="Delete a region by id (e.g. R-0001).")
    def delete_region(id: str) -> dict:
        return tools.delete_region(id)

    @server.tool(description=(
        "List regions: id, colour, area_m2, point count, bbox [minx, miny, maxx, maxy] and "
        "centroid (absolute S-JTSK). Optional filter `near` = {\"x\", \"y\", radius_m?} or "
        "{\"parcel\", radius_m?} (default radius 100 m; adds distance_m, nearest first); `limit`."))
    def list_regions(near: dict | None = None, limit: int = 50) -> dict:
        return tools.list_regions(near, limit)

    return server


def parse_args(argv: list[str] | None = None) -> Settings:
    ap = argparse.ArgumentParser(
        prog="python -m suchohrad_pipeline.mcp_server",
        description="MCP server (stdio) that edits pipeline/config/edits.json",
    )
    ap.add_argument("--area", choices=("slice", "village"), default=DEFAULT_AREA)
    ap.add_argument("--edits", type=Path, default=DEFAULT_EDITS, help="edits file to read and write")
    ap.add_argument("--state", type=Path, default=DEFAULT_STATE, help="editor-state.json of the viewer")
    ap.add_argument("--assets", type=Path, default=DEFAULT_MANIFEST, help="asset manifest")
    ap.add_argument("--work", type=Path, default=None, help="work dir (default: from --area)")
    a = ap.parse_args(argv)
    return Settings(
        area=a.area,
        work_dir=a.work.resolve() if a.work else None,
        edits_path=a.edits.resolve(),
        state_path=a.state.resolve(),
        assets_path=a.assets.resolve(),
    )


def main(argv: list[str] | None = None) -> int:
    settings = parse_args(argv)
    print(
        f"suchohrad MCP server: area={settings.area} edits={settings.edits_path} "
        f"state={settings.state_path}",
        file=sys.stderr,
    )
    build_server(settings).run("stdio")
    return 0


if __name__ == "__main__":
    sys.exit(main())
