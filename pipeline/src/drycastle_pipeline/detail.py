"""Ground detail of the village: draped patches (lawn tones, bare soil, gravel, leaf litter, beds, furrows,
road verges) and clutter instances (tufts, flowers, pebbles, rocks, leaves, sticks, ferns).

This is the ``detail`` stage (``make detail``, between ``props`` and ``export``). It reads the work files
(parcels, buildings, roads, terrain, props, objects) and ``edits.json`` and writes

* ``work/detail.json``: the clutter instances, per chunk and kind (the export copies it as ``clutter.json``),
* ``work/detail_patches.npy`` and ``detail_patches.json``: the patch triangles, draped on the terrain (the
  export puts them into the chunk files as ``detail_patches`` nodes).

* **Patches** are polygons on the ground, flat-coloured or textured from the texture library, draped on
  the terrain by :func:`regions.drape` (so they follow the ground triangle by triangle) at ``detail.lift_m``.
  They never overlap: the layers are clipped in the order of :data:`PATCH_PRIORITY` (first wins), parcels
  are disjoint, so nothing z-fights. Hand-drawn regions (3 cm) are cut out of every patch, so they never
  fight with a patch either.
* **Clutter** is data only: ``{"kind", "x", "y", "z", "rot", "scale"}`` per instance. The game draws them as
  instanced meshes near the player; nothing in the map holds them as triangles.
* **Area types.** :func:`area_type` is the one place that says what kind of ground a parcel is (garden, field,
  forest, road, or nothing); each type has a recipe (:data:`RECIPES`). Today it reads the parcel kind; a real
  area-type layer replaces that one function. Road verges are drawn along every carriageway, whatever the
  parcel.
* **Everything is seeded** with ``sha256("<parcel id>:detail:<purpose>")`` (:func:`props.seeded`), one
  generator per purpose, so identical input gives identical output and a change to one recipe step does
  not move the others.
* **Exclusion**: nothing lands on a building (plus a wall margin), on a fence, a trunk, a placed object, a
  hand-drawn region or the road. Crowns do not exclude: leaf litter belongs under them.
"""

import hashlib
import json
import math
import random
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import shapely
import shapely.affinity
from shapely.geometry import LineString, Point, Polygon, box, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import nearest_points, unary_union

from drycastle_pipeline.assets import load_manifest
from drycastle_pipeline.buildings import footprint_polygon, hex_to_rgb
from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import DEFAULT_EDITS, EditsError, load_edits
from drycastle_pipeline import meadow as meadow_mod
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.props import chunk_of, seeded
from drycastle_pipeline.regions import drape, plan_regions
from drycastle_pipeline.roads import edge_lines
from drycastle_pipeline.rules import DEFAULT_RULES, load_section
from drycastle_pipeline.terrain import Terrain, load_terrain
from drycastle_pipeline.detail_meshes import KINDS, TINTABLE
from drycastle_pipeline.textures import TexturedMesh, tex_node_name, texture

DETAIL_FORMAT_VERSION = 1
CLUTTER_KINDS = KINDS
# The first layer wins where two overlap (a path crosses lawn, litter lies on lawn, ...).
PATCH_PRIORITY = (
    "gravel", "verge_gravel", "bed", "furrow", "dirt", "verge_dirt", "litter", "tone_a", "tone_b", "lawn",
)
# Library texture of a layer; layers without one are flat colour.
PATCH_TEXTURE = {
    "gravel": "gravel", "verge_gravel": "gravel", "bed": "soil_rows", "furrow": "soil_rows",
    "dirt": "dirt", "verge_dirt": "dirt", "litter": "leaf_litter",
}
_SOFT_LAYERS = ("lawn", "tone_a", "tone_b", "litter")  # big soft shapes: simplified harder (fewer triangles)
_RECT_LAYERS = ("bed", "furrow")  # textured with their own angle and origin
_NEAR_M = 8.0  # what around a parcel counts as near it
_TILE_M = 24.0
CELL_M = 2.0  # the meadow cell: the tone noise is laid out in these (meadow.py), stored clutter reads it at its position
_MIN_PIECE_M2 = 0.15
_PATCH_ROW = 19  # x0 y0 z0 x1 y1 z1 x2 y2 z2, r g b, u0 v0 u1 v1 u2 v2, texture index (-1 = flat colour)


@dataclass
class Patch:
    layer: str  # one of PATCH_PRIORITY
    geom: BaseGeometry  # local metres, disjoint from every other patch
    colour: list[int] | None = None  # flat colour (no texture)
    texture: str | None = None
    angle: float = 0.0  # texture rotation (radians) about ``origin``
    origin: tuple[float, float] = (0.0, 0.0)


@dataclass
class DetailResult:
    parcel: str
    patches: list[Patch] = field(default_factory=list)
    clutter: list[dict] = field(default_factory=list)

    def counts(self) -> dict[str, int]:
        out = {k: 0 for k in CLUTTER_KINDS}
        for c in self.clutter:
            out[c["kind"]] += 1
        return out


@dataclass
class ParcelContext:
    """What a recipe needs about one parcel (or one part of it), in local metres."""

    id: str
    kind: str
    polygon: Polygon
    buildings: list[Polygon]
    fences: list[LineString]
    trees: list[tuple[float, float, float]]  # x, y, scale (near the parcel, from any parcel)
    road: BaseGeometry | None  # carriageways near the parcel
    terrain: Terrain
    tree_cfg: dict
    colour: list[int] | None = None  # the parcel's flat terrain colour (the lawn palette follows it)
    blocked: BaseGeometry | None = None  # placed objects and hand-drawn regions
    verge: dict[str, BaseGeometry] | None = None  # "gravel" and "dirt" verge blobs along the roads, clipped
    water: BaseGeometry | None = None  # water parcels near the parcel (grass beside them stands fresh)


# --- tools ----------------------------------------------------------------------------------


def blob(
    rng: random.Random,
    cx: float,
    cy: float,
    radius: float,
    wobble: float = 0.3,
    stretch: float = 1.0,
    n: int = 14,
    axis: float | None = None,
) -> Polygon:
    """A smooth irregular blob: radius ``radius`` times (1 + wobble * noise) round the angle, stretched
    along ``axis`` (random without one) by ``stretch`` (1 = round). The noise is three random harmonics."""
    harmonics = [(k, rng.random() * math.tau, 1.0 / k) for k in (2, 3, 5)]
    norm = sum(a for _, _, a in harmonics)
    if axis is None:
        axis = rng.random() * math.pi
    ca, sa = math.cos(axis), math.sin(axis)
    pts = []
    for i in range(n):
        t = math.tau * i / n
        r = radius * (1 + wobble * sum(a * math.sin(k * t + ph) for k, ph, a in harmonics) / norm)
        x, y = r * math.cos(t) * stretch, r * math.sin(t)
        pts.append((cx + x * ca - y * sa, cy + x * sa + y * ca))
    return Polygon(pts).buffer(0)


def poisson_disc(
    rng: random.Random, geom: BaseGeometry, min_dist: float, k: int = 20
) -> list[tuple[float, float]]:
    """Bridson's Poisson-disc sampling inside ``geom``: points at least ``min_dist`` apart, the same
    set for the same generator state. Disconnected parts are reached by restarting from random seeds.
    Pure Python: for the small counts (blob centres, beds); :func:`jitter_grid` is the fast one."""
    if geom.is_empty or min_dist <= 0:
        return []
    minx, miny, maxx, maxy = geom.bounds
    cell = min_dist / math.sqrt(2)
    grid: dict[tuple[int, int], int] = {}
    pts: list[tuple[float, float]] = []
    shapely.prepare(geom)

    def key(x: float, y: float) -> tuple[int, int]:
        return int((x - minx) / cell), int((y - miny) / cell)

    def fits(x: float, y: float) -> bool:
        i, j = key(x, y)
        for dj in range(-2, 3):
            for di in range(-2, 3):
                q = grid.get((i + di, j + dj))
                if q is not None and math.hypot(pts[q][0] - x, pts[q][1] - y) < min_dist:
                    return False
        return True

    def add(x: float, y: float, active: list[int]) -> None:
        grid[key(x, y)] = len(pts)
        active.append(len(pts))
        pts.append((x, y))

    seeds = int(60 + 20 * (maxx - minx) * (maxy - miny) / max(geom.area, 1e-6))
    for _ in range(seeds):
        x, y = minx + rng.random() * (maxx - minx), miny + rng.random() * (maxy - miny)
        if not (shapely.contains_xy(geom, x, y) and fits(x, y)):
            continue
        active: list[int] = []
        add(x, y, active)
        while active:
            a = rng.randrange(len(active))
            px, py = pts[active[a]]
            for _ in range(k):
                r, t = min_dist * (1 + rng.random()), rng.random() * math.tau
                x, y = px + r * math.cos(t), py + r * math.sin(t)
                if minx <= x <= maxx and miny <= y <= maxy and shapely.contains_xy(geom, x, y) and fits(x, y):
                    add(x, y, active)
                    break
            else:
                active.pop(a)
    return pts


def thin(rng: random.Random, pts: list[tuple[float, float]], density) -> list[tuple[float, float]]:
    """Keeps each point with probability ``density(xs, ys)`` (arrays in, arrays in [0, 1] out).
    Thinning a Poisson-disc set keeps its minimum distance."""
    if not pts:
        return []
    xs, ys = np.array([p[0] for p in pts]), np.array([p[1] for p in pts])
    p = np.clip(np.asarray(density(xs, ys), float), 0.0, 1.0)
    return [pt for pt, q in zip(pts, p, strict=True) if rng.random() < q]


def jitter_grid(g: np.random.Generator, geom: BaseGeometry, spacing: float, jitter: float = 0.9) -> np.ndarray:
    """One point per ``spacing`` metre cell of a grid, jittered inside the cell, kept inside ``geom``: (n, 2).

    Even enough for scatter and fast (vectorised, only the tiles that touch ``geom`` are visited), so it is
    what the clutter uses; deterministic for the same generator state."""
    if geom.is_empty or spacing <= 0:
        return np.zeros((0, 2))
    shapely.prepare(geom)
    minx, miny, maxx, maxy = geom.bounds
    per_tile = max(1, int(_TILE_M / spacing))
    tile = per_tile * spacing
    ci, cj = np.meshgrid(np.arange(per_tile), np.arange(per_tile))
    out = []
    for ti in range(int(math.ceil((maxx - minx) / tile))):
        for tj in range(int(math.ceil((maxy - miny) / tile))):
            x0, y0 = minx + ti * tile, miny + tj * tile
            if not shapely.intersects(geom, shapely.box(x0, y0, x0 + tile, y0 + tile)):
                continue
            u = g.random((2, per_tile, per_tile))
            xs = x0 + (ci + 0.5 + (u[0] - 0.5) * jitter) * spacing
            ys = y0 + (cj + 0.5 + (u[1] - 0.5) * jitter) * spacing
            inside = shapely.contains_xy(geom, xs, ys)
            if inside.any():
                out.append(np.stack([xs[inside], ys[inside]], axis=1))
    return np.concatenate(out) if out else np.zeros((0, 2))


def scatter(g: np.random.Generator, geom: BaseGeometry, spacing: float, density=1.0) -> np.ndarray:
    """:func:`jitter_grid` points of ``geom`` kept with probability ``density`` (a number, or a function
    of (xs, ys) returning an array in [0, 1])."""
    pts = jitter_grid(g, geom, spacing)
    if not len(pts):
        return pts
    p = density(pts[:, 0], pts[:, 1]) if callable(density) else density
    return pts[g.random(len(pts)) < np.clip(p, 0.0, 1.0)]


def distance_to(geom: BaseGeometry | None):
    """A function (xs, ys) -> distance of each point to ``geom`` (inf when there is none)."""
    if geom is None or geom.is_empty:
        return lambda xs, ys: np.full(len(xs), np.inf)
    return lambda xs, ys: shapely.distance(shapely.points(xs, ys), geom)


def _polys(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom]
    return [p for g in getattr(geom, "geoms", []) for p in _polys(g)]


def _lines(geom: BaseGeometry) -> list[LineString]:
    if geom.is_empty:
        return []
    if isinstance(geom, LineString):
        return [geom]
    return [ln for g in getattr(geom, "geoms", []) for ln in _lines(g)]


def _rad(rng: random.Random, span: list[float]) -> float:
    return span[0] + rng.random() * (span[1] - span[0])


def _gen(pid: str, purpose: str) -> np.random.Generator:
    return np.random.default_rng(seeded(f"{pid}:detail:{purpose}").getrandbits(64))


def _clean(geom: BaseGeometry, tol: float = 0.06) -> BaseGeometry:
    """Simplified, without slivers: fewer triangles for the same look."""
    keep = [p.simplify(tol, preserve_topology=True) if tol > 0 else p for p in _polys(geom) if p.area >= _MIN_PIECE_M2]
    keep = [p for p in keep if not p.is_empty and p.area >= _MIN_PIECE_M2]
    return unary_union(keep) if keep else Polygon()


def terrain_ok(t: Terrain, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    """Which points stand on a meshed terrain cell (all four corners valid)."""
    gx = np.floor(xs / t.step - t.i0).astype(int)
    gy = np.floor(ys / t.step - t.j0).astype(int)
    inside = (gx >= 0) & (gx < t.ncols) & (gy >= 0) & (gy < t.nrows)
    cx, cy = np.clip(gx, 0, t.ncols - 1), np.clip(gy, 0, t.nrows - 1)
    v = t.valid
    return inside & v[cy, cx] & v[cy, cx + 1] & v[cy + 1, cx] & v[cy + 1, cx + 1]


# --- palettes -------------------------------------------------------------------------------


def _shade(colour: list[int], f: float) -> list[int]:
    return [max(0, min(255, round(c * f))) for c in colour]


def _mix(a: list[int], b: list[int], w: float) -> list[int]:
    return [round(x * (1 - w) + y * w) for x, y in zip(a, b, strict=True)]


def palette(ctx: ParcelContext, fixed: list[str], shades: list[float], mix_by_kind: dict, default_mix: float = 1.0
            ) -> tuple[list[list[int]], float]:
    """Colours of a flat layer and how much they follow the parcel's terrain colour.

    Each colour is the fixed one at ``mix`` 0 and the terrain colour times its shade at 1, so a lawn on a
    green garden is a few shades of that garden's green, and on a tan plot it is still green-ish."""
    w = float(mix_by_kind.get(ctx.kind, default_mix))
    out = []
    for i, hx in enumerate(fixed):
        c = hex_to_rgb(hx)
        if ctx.colour is not None and w > 0:
            c = _mix(c, _shade(ctx.colour, shades[i % len(shades)]), w)
        out.append(c)
    return out, w


# --- shared recipe parts --------------------------------------------------------------------


def free_area(ctx: ParcelContext, cfg: dict, trunks: bool = True) -> BaseGeometry:
    """The parcel without buildings (and a wall margin), fences, trunks, placed objects, regions and the road.

    ``trunks=False`` (forest, field) leaves the trunk footprints in the patches (nobody sees the ground under
    a trunk) and keeps the polygons simple; the clutter then filters its points by :func:`_trunk_filter`.

    The result is kept on the context (the meadow raster asks for the same ground as the recipe)."""
    cache = ctx.__dict__.setdefault("_free", {})
    if trunks not in cache:
        cache[trunks] = _free_area(ctx, cfg, trunks)
    return cache[trunks]


def _free_area(ctx: ParcelContext, cfg: dict, trunks: bool) -> BaseGeometry:
    # square corners and flat ends: few vertices, so the patches cut against this have few triangles
    blocked = [b.buffer(float(cfg["wall_clearance_m"]), join_style="mitre") for b in ctx.buildings]
    blocked += [f.buffer(float(cfg["fence_clearance_m"]), cap_style="flat", join_style="mitre") for f in ctx.fences]
    if trunks:
        blocked += [Point(x, y).buffer(float(cfg["trunk_radius_m"]), quad_segs=2) for x, y, _ in ctx.trees]
    if ctx.road is not None:
        blocked.append(ctx.road.buffer(float(cfg["road_clearance_m"]), join_style="mitre").simplify(0.1))
    if ctx.blocked is not None and not ctx.blocked.is_empty:
        blocked.append(ctx.blocked)
    free = ctx.polygon
    if blocked:
        free = free.difference(unary_union(blocked))
    return free


def _verge(ctx: ParcelContext) -> tuple[BaseGeometry, BaseGeometry]:
    v = ctx.verge or {}
    return v.get("gravel", Polygon()), v.get("dirt", Polygon())


def _trunk_filter(ctx: ParcelContext, pts: np.ndarray, cfg: dict) -> np.ndarray:
    """Drops points within a trunk's radius."""
    if not len(pts) or not ctx.trees:
        return pts
    trunks = shapely.points([(x, y) for x, y, _ in ctx.trees])
    near = shapely.STRtree(trunks).query(shapely.points(pts), predicate="dwithin", distance=float(cfg["trunk_radius_m"]))
    keep = np.ones(len(pts), bool)
    keep[near[0]] = False
    return pts[keep]


def _tint_bias(ctx: ParcelContext, pts: np.ndarray, cfg: dict) -> np.ndarray:
    """Tint shift per point (tint units): fresher beside fences, walls and water."""
    t = cfg["tint"]
    cache = ctx.__dict__.setdefault("_tint_geoms", {})
    if "edges" not in cache:
        cache["edges"] = _union([*ctx.fences, *[b.exterior for b in ctx.buildings]]) if (ctx.fences or ctx.buildings) else None
    p = shapely.points(pts)
    bias = np.zeros(len(pts))
    if cache["edges"] is not None:
        near = shapely.distance(p, cache["edges"]) < float(t["fresh_m"])
        bias += np.where(near, float(t["fence_fresh"]), 0.0)
    if ctx.water is not None and not ctx.water.is_empty:
        w = float(t["water_m"])
        bias += float(t["water_fresh"]) * np.clip(1 - shapely.distance(p, ctx.water) / w, 0.0, 1.0)
    return bias


def _emit(g: np.random.Generator, kind: str, pts: np.ndarray, span: list[float], ctx: ParcelContext, cfg: dict | None = None) -> list[dict]:
    """Instances of ``kind`` at ``pts`` with a random turn and a scale in ``span`` (points off the meshed terrain are dropped).
    A tintable kind also gets its tint byte (``cfg``: the detail rules, for the bias)."""
    if not len(pts):
        return []
    pts = pts[terrain_ok(ctx.terrain, pts[:, 0], pts[:, 1])]
    if not len(pts):
        return []
    z = ctx.terrain.surface(pts[:, 0], pts[:, 1])
    rot = g.random(len(pts)) * math.tau
    sc = span[0] + g.random(len(pts)) * (span[1] - span[0])
    tint = None
    if kind in TINTABLE:
        cell = float(CELL_M)
        bias = _tint_bias(ctx, pts, cfg) if cfg is not None else 0.0
        tint = meadow_mod.tint_byte(meadow_mod.tone_noise(pts[:, 0] / cell, pts[:, 1] / cell), g.random(len(pts)), bias)
    out = [
        {"kind": kind, "x": round(float(x), 3), "y": round(float(y), 3), "z": round(float(h), 3),
         "rot": round(float(r), 3), "scale": round(float(s), 3)}
        for (x, y), h, r, s in zip(pts, z, rot, sc, strict=True)
    ]
    if tint is not None:
        for c, t in zip(out, tint, strict=True):
            c["tint"] = int(t)
    return out


def _split_kinds(g: np.random.Generator, pts: np.ndarray, kinds: list) -> dict[str, np.ndarray]:
    """Points dealt out to kinds by their shares (``[[kind, share], ...]``, drawn from ``g``)."""
    if not len(pts):
        return {}
    cum = np.cumsum([float(w) for _, w in kinds])
    idx = np.minimum(np.searchsorted(cum / cum[-1], g.random(len(pts)), side="right"), len(kinds) - 1)
    return {k: pts[idx == i] for i, (k, _) in enumerate(kinds) if (idx == i).any()}


def _zones(g: np.random.Generator, zones: list[tuple[BaseGeometry, float]], spacing: float) -> np.ndarray:
    """Scatter over several zones, each with its own density; zones are made disjoint in the order given."""
    taken: BaseGeometry = Polygon()
    parts = [np.zeros((0, 2))]
    for geom, v in zones:
        zone = geom.difference(taken) if not taken.is_empty else geom
        if float(v) > 0 and not zone.is_empty:
            parts.append(scatter(g, zone, spacing, float(v)))
        if not geom.is_empty:
            taken = taken.union(geom)
    return np.concatenate(parts)


def _assemble(
    res: DetailResult, free: BaseGeometry, layers: dict[str, BaseGeometry], flat: dict[str, list[list[int]]],
    rects: dict[str, list[tuple[Polygon, float, tuple[float, float]]]], rng: random.Random,
) -> dict[str, BaseGeometry]:
    """Clips ``layers`` to the free ground in priority order (an earlier layer keeps what it covers) and makes
    the patches. ``flat`` has the colours of the flat layers, ``rects`` the textured rectangles (beds, furrows)
    with their angle and centre. Returns the clipped layers."""
    taken: BaseGeometry = Polygon()
    out: dict[str, BaseGeometry] = {}
    for layer in PATCH_PRIORITY:
        g = layers.get(layer)
        if g is None or g.is_empty:
            continue
        # simplify the layer's own shape first, then cut it back to the free ground and to what earlier layers
        # took: simplifying last would let an outline slip over a wall or into another patch
        g = _clean(g.intersection(free), 0.25 if layer in _SOFT_LAYERS else 0.06).intersection(free)
        if not taken.is_empty:
            g = g.difference(taken)
        g = _clean(g, 0.0)
        if g.is_empty:
            continue
        out[layer] = g
        taken = taken.union(g)
    for layer, g in out.items():
        if layer == "lawn":
            for poly in _polys(g):
                res.patches.append(Patch(layer, poly, colour=flat["lawn"][rng.randrange(len(flat["lawn"]))]))
        elif layer in ("tone_a", "tone_b"):
            res.patches.append(Patch(layer, g, colour=flat[layer][0]))
        elif layer in _RECT_LAYERS:
            for rect, ang, cen in rects.get(layer, []):
                piece = g.intersection(rect.buffer(0.01))
                if not piece.is_empty:
                    res.patches.append(Patch(layer, piece, texture=PATCH_TEXTURE[layer], angle=ang, origin=cen))
        else:
            res.patches.append(Patch(layer, g, texture=PATCH_TEXTURE[layer]))
    return out


def _along(rng: random.Random, ring: BaseGeometry, spacing: float, keep: float):
    """Points along a ring every ~``spacing`` metres (each kept with probability ``keep``) with the heading there."""
    length = ring.length
    if length < 1.0:
        return
    steps = max(1, int(length / spacing))
    for i in range(steps):
        if rng.random() > keep:
            continue
        s = (i + rng.random()) / steps * length
        p, q = ring.interpolate(s), ring.interpolate(min(length, s + 0.5))
        yield p, math.atan2(q.y - p.y, q.x - p.x)


def _union(parts: list[BaseGeometry]) -> BaseGeometry:
    parts = [p for p in parts if not p.is_empty]
    return unary_union(parts) if parts else Polygon()


# --- the garden recipe ----------------------------------------------------------------------


def _gate_point(ctx: ParcelContext, cfg: dict) -> Point | None:
    """Where a path meets the road: the middle of the longest unfenced stretch of the parcel's boundary that
    is near the road (the gate gap of a fenced parcel; the road frontage of an open one)."""
    if ctx.road is None or ctx.road.is_empty:
        return None
    front = ctx.polygon.exterior.intersection(ctx.road.buffer(float(cfg["gravel"]["frontage_m"])))
    if ctx.fences:
        front = front.difference(unary_union(ctx.fences).buffer(0.4))
    lines = [ln for ln in _lines(front) if ln.length >= float(cfg["gravel"]["min_gate_m"])]
    if not lines:
        return None
    return max(lines, key=lambda ln: ln.length).interpolate(0.5, normalized=True)


def _gravel_path(ctx: ParcelContext, free: BaseGeometry, cfg: dict, rng: random.Random) -> BaseGeometry:
    """A wobbling strip from the gate to the nearest building (none without a gate or a building)."""
    g = cfg["gravel"]
    if not ctx.buildings or free.is_empty:
        return Polygon()
    start = _gate_point(ctx, cfg)
    if start is None:
        return Polygon()
    target_b = min(ctx.buildings, key=lambda b: b.distance(start))
    end = nearest_points(target_b, start)[0]
    length = start.distance(end)
    if length < float(g["min_length_m"]) or length > float(g["max_length_m"]):
        return Polygon()
    mid_t = 0.35 + 0.3 * rng.random()
    nx, ny = -(end.y - start.y) / length, (end.x - start.x) / length
    off = (rng.random() * 2 - 1) * float(g["wobble_m"])
    mid = (start.x + (end.x - start.x) * mid_t + nx * off, start.y + (end.y - start.y) * mid_t + ny * off)
    pts = [(start.x, start.y), mid, (end.x, end.y)]
    curve = []
    for t in np.linspace(0, 1, 14):
        a = (1 - t) ** 2, 2 * (1 - t) * t, t**2
        curve.append((sum(w * p[0] for w, p in zip(a, pts, strict=True)), sum(w * p[1] for w, p in zip(a, pts, strict=True))))
    w0 = _rad(rng, g["width_m"])
    path = LineString(curve).buffer(w0 / 2, cap_style="flat").union(
        unary_union([Point(x, y).buffer(w0 / 2 * (0.85 + 0.3 * rng.random()), 6) for x, y in curve[::2]])
    )
    return path.intersection(free)


def _beds(ctx: ParcelContext, free: BaseGeometry, cfg: dict, rng: random.Random
          ) -> list[tuple[Polygon, float, tuple[float, float]]]:
    """Vegetable beds: (rectangle, angle, centre), aligned with the main building."""
    b = cfg["beds"]
    n = min(int(b["max"]), round(float(b["per_1000m2"]) * ctx.polygon.area / 1000))
    if n <= 0 or not ctx.buildings:
        return []
    main = max(ctx.buildings, key=lambda p: p.area)
    ring = list(main.minimum_rotated_rectangle.exterior.coords)
    edge = max(zip(ring, ring[1:], strict=False), key=lambda e: math.dist(*e))
    angle = math.atan2(edge[1][1] - edge[0][1], edge[1][0] - edge[0][0])
    w, h = (float(v) for v in b["size_m"])
    allowed = free.difference(unary_union([p.buffer(float(b["building_distance_m"])) for p in ctx.buildings]))
    if ctx.fences:
        allowed = allowed.difference(unary_union([f.buffer(float(b["fence_distance_m"])) for f in ctx.fences]))
    out = []
    cands = poisson_disc(rng, allowed.buffer(-0.5), 1.0)
    rng.shuffle(cands)
    for cx, cy in cands:
        rect = shapely.affinity.rotate(box(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), angle, origin=(cx, cy), use_radians=True)
        rim = rect.buffer(float(b["rim_m"]))
        if allowed.contains(rim) and all(not rim.intersects(r) for r, _, _ in out):
            out.append((rect, angle, (cx, cy)))
            if len(out) == n:
                break
    return out


def garden_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Patches and clutter of one yard (the recipe for ``building_plot`` and ``garden`` parcels)."""
    pid = ctx.id
    res = DetailResult(pid)
    free = free_area(ctx, cfg)
    if free.is_empty:
        return res
    canopy = float(ctx.tree_cfg["canopy_m"])

    gravel = _gravel_path(ctx, free, cfg, seeded(f"{pid}:detail:gravel"))
    beds = _beds(ctx, free, cfg, seeded(f"{pid}:detail:beds"))
    bed_geom = _union([r for r, _, _ in beds])
    rim = _union([r.buffer(float(cfg["beds"]["rim_m"])) for r, _, _ in beds])
    verge_g, verge_d = _verge(ctx)

    d = cfg["dirt"]
    rng = seeded(f"{pid}:detail:dirt")
    dirt_parts = [rim.difference(bed_geom)] if beds else []
    for b in ctx.buildings:
        ring = b.exterior
        n = max(1, int(ring.length / float(d["wall_spacing_m"])))
        for i in range(n):
            if rng.random() > float(d["wall_keep"]):
                continue
            p = ring.interpolate((i + rng.random()) / n, normalized=True)
            dirt_parts.append(blob(rng, p.x, p.y, _rad(rng, d["wall_radius_m"]), 0.35, 1.4, int(d["blob_vertices"])))
    x0, y0, x1, y1 = free.bounds
    for _ in range(round(float(d["open_per_1000m2"]) * ctx.polygon.area / 1000)):
        for _try in range(30):
            x, y = x0 + rng.random() * (x1 - x0), y0 + rng.random() * (y1 - y0)
            if shapely.contains_xy(free, x, y):
                dirt_parts.append(blob(rng, x, y, _rad(rng, d["open_radius_m"]), 0.4, 1.3, int(d["blob_vertices"])))
                break

    lit = cfg["litter"]
    rng = seeded(f"{pid}:detail:litter")
    litter_parts = []
    for x, y, s in ctx.trees:
        r = canopy * s * float(lit["radius_factor"])
        for _ in range(int(lit["blobs_per_tree"])):
            a, dist = rng.random() * math.tau, rng.random() * r * 0.5
            litter_parts.append(blob(rng, x + dist * math.cos(a), y + dist * math.sin(a), r * (0.7 + 0.4 * rng.random()), 0.35, 1.2, 8))

    lw = cfg["lawn"]
    rng = seeded(f"{pid}:detail:lawn")
    base_cols, mix = palette(ctx, lw["colours"], lw["shades"], lw["terrain_mix"])
    tone_cols, _ = palette(ctx, lw["tone_colours"], lw["tone_shades"], lw["terrain_mix"])
    lawn = Polygon()
    if mix < 0.99:  # the lawn is another colour than the ground under it: draw it
        lawn = _union([
            blob(rng, x, y, _rad(rng, lw["blob_radius_m"]), 0.3, 1.3, 10)
            for x, y in poisson_disc(rng, ctx.polygon, float(lw["blob_spacing_m"]))
            if rng.random() < float(lw["keep"])
        ])
    rng = seeded(f"{pid}:detail:tones")
    tone: dict[int, list[BaseGeometry]] = {0: [], 1: []}
    for i, (x, y) in enumerate(poisson_disc(rng, ctx.polygon, float(lw["tone_spacing_m"]))):
        if rng.random() < float(lw["tone_keep"]):
            tone[i % 2].append(blob(rng, x, y, _rad(rng, lw["tone_radius_m"]), 0.4, 1.5, 8))

    layers = {
        "gravel": gravel, "verge_gravel": verge_g, "bed": bed_geom, "dirt": _union([*dirt_parts]),
        "verge_dirt": verge_d, "litter": _union(litter_parts),
        "tone_a": _union(tone[0]), "tone_b": _union(tone[1]), "lawn": lawn,
    }
    got = _assemble(res, free, layers, {"lawn": base_cols, "tone_a": [tone_cols[0]], "tone_b": [tone_cols[1]]},
                    {"bed": beds}, seeded(f"{pid}:detail:colours"))
    res.clutter = _garden_clutter(ctx, cfg, got, free, canopy)
    return res


def _region_density(parts: list[tuple[BaseGeometry, object]]):
    """Density by place: the value of the first region a point is in (0 elsewhere)."""
    def fn(xs, ys):
        pts = shapely.points(xs, ys)
        out = np.zeros(len(xs))
        done = np.zeros(len(xs), bool)
        for geom, value in parts:
            if geom.is_empty:
                continue
            hit = shapely.contains(geom, pts) & ~done
            if hit.any():
                out[hit] = value(xs[hit], ys[hit]) if callable(value) else value
            done |= hit
        return out
    return fn


def _garden_clutter(ctx: ParcelContext, cfg: dict, layers: dict[str, BaseGeometry], free: BaseGeometry, canopy: float) -> list[dict]:
    pid, c = ctx.id, cfg["clutter"]
    out: list[dict] = []
    gravel = _union([layers.get("gravel", Polygon()), layers.get("verge_gravel", Polygon())])
    dirt = _union([layers.get("dirt", Polygon()), layers.get("verge_dirt", Polygon())])
    litter, bed = layers.get("litter", Polygon()), layers.get("bed", Polygon())
    walls = unary_union(ctx.buildings) if ctx.buildings else None
    edges = unary_union([ctx.polygon.exterior, *ctx.fences]) if ctx.fences else ctx.polygon.exterior
    trees = unary_union([Point(x, y).buffer(canopy * s * 0.5) for x, y, s in ctx.trees]) if ctx.trees else None
    d_wall, d_edge, d_tree = distance_to(walls), distance_to(edges), distance_to(trees)
    open_ground = free.difference(bed) if not bed.is_empty else free  # clutter stays off the tilled soil
    bare = _union([gravel, dirt, litter])
    grass = open_ground.difference(bare) if not bare.is_empty else open_ground

    t = c["tuft"]
    edge_m = float(t["edge_m"])

    def tuft_lawn(xs, ys):
        near = np.minimum(d_edge(xs, ys), d_wall(xs, ys))
        base = float(t["lawn"]) + (float(t["edge"]) - float(t["lawn"])) * np.clip(1 - near / edge_m, 0, 1)
        # grass grows in clumps with gaps between them, not evenly
        return base * meadow_mod.clump(xs / CELL_M, ys / CELL_M, float(t["clump"]), float(t["clump_m"]) / CELL_M, 1500)

    g = _gen(pid, "tuft")
    pts = scatter(g, open_ground, float(t["spacing_m"]), _region_density([(grass, tuft_lawn), (dirt, 0.04), (open_ground, lambda xs, ys: tuft_lawn(xs, ys) * 0.5)]))
    # grass nobody mows (beside a fence or a wall) grows tall, the rest of the lawn has some clover among it
    if len(pts):
        unmown = np.minimum(d_edge(pts[:, 0], pts[:, 1]), d_wall(pts[:, 0], pts[:, 1])) < float(t["unmown_m"])
        r = g.random(len(pts))
        tall = unmown & (r < float(t["tall"]))
        patch = ~unmown & (r < float(t["clover"]))
        for kind, sel in (("tall_grass", tall), ("clover", patch), ("tuft", ~tall & ~patch)):
            out += _emit(g, kind, pts[sel], t["scale"], ctx, cfg)

    f = c["flower"]
    g = _gen(pid, "flower")
    species = f["species"]
    names = list(species)
    weights = np.array([float(species[k]["share"]) for k in names])
    for cx, cy in scatter(g, grass.buffer(-0.4), float(f["cluster_spacing_m"]), float(f["cluster_keep"])):
        kind = names[int(np.searchsorted(np.cumsum(weights) / weights.sum(), g.random(), side="right").clip(0, len(names) - 1))]
        size = species[kind]["size"]
        k = int(g.integers(int(size[0]), int(size[1]) + 1))
        a, r = g.random(k) * math.tau, float(f["cluster_radius_m"]) * np.sqrt(g.random(k))
        pts = np.stack([cx + r * np.cos(a), cy + r * np.sin(a)], axis=1)
        pts = pts[shapely.contains_xy(grass, pts[:, 0], pts[:, 1])]
        out += _emit(g, kind, pts, f["scale"], ctx)

    p = c["pebble"]
    wall_m = float(p["wall_m"])
    zone = _union([gravel, dirt, walls.buffer(wall_m) if walls is not None else Polygon()]).intersection(open_ground)

    def pebble_wall(xs, ys):
        return float(p["wall"]) * np.clip(1 - d_wall(xs, ys) / wall_m, 0, 1)

    g = _gen(pid, "pebble")
    pts = scatter(g, zone, float(p["spacing_m"]), _region_density([(gravel, float(p["gravel"])), (dirt, float(p["dirt"])), (open_ground, pebble_wall)]))
    out += _emit(g, "pebble", pts, p["scale"], ctx)

    r_ = c["rock"]
    edge_r = float(r_["edge_m"])

    def rock_grass(xs, ys):
        return float(r_["lawn"]) + (float(r_["edge"]) - float(r_["lawn"])) * np.clip(1 - d_edge(xs, ys) / edge_r, 0, 1)

    g = _gen(pid, "rock")
    pts = scatter(g, open_ground, float(r_["spacing_m"]), _region_density([(gravel, 0.0), (dirt, float(r_["dirt"])), (open_ground, rock_grass)]))
    out += _emit(g, "rock", pts, r_["scale"], ctx)

    lf = c["leaf"]
    reach = float(lf["tree_reach_m"])

    def leaf_tree(xs, ys):
        return float(lf["tree_lawn"]) * np.clip(1 - d_tree(xs, ys) / reach, 0, 1)

    g = _gen(pid, "leaf")
    zone = _union([litter, trees.buffer(reach) if trees is not None else Polygon()]).intersection(open_ground)
    pts = scatter(g, zone, float(lf["spacing_m"]), _region_density([(litter, float(lf["litter"])), (open_ground, leaf_tree)]))
    out += _emit(g, "leaf", pts, lf["scale"], ctx)

    s_ = c["stick"]
    g = _gen(pid, "stick")
    pts = scatter(g, litter.intersection(open_ground), float(s_["spacing_m"]), float(s_["litter"]))
    out += _emit(g, "stick", pts, s_["scale"], ctx)
    return out


# --- field, forest and road recipes ---------------------------------------------------------


def _tone_layers(ctx: ParcelContext, rng: random.Random, free: BaseGeometry, section: dict
                 ) -> tuple[dict[str, BaseGeometry], dict[str, list[list[int]]]]:
    """Large flat tone blobs (two tones) over the free ground: ``tone_a`` and ``tone_b`` layers and their colours."""
    cols, _ = palette(ctx, section["tone_colours"], section["tone_shades"], section.get("terrain_mix", {}), 1.0)
    n = round(float(section["tone_per_ha"]) * ctx.polygon.area / 1e4)
    parts: dict[int, list[BaseGeometry]] = {0: [], 1: []}
    if n > 0 and not free.is_empty:
        g = _gen(ctx.id, "tones")
        pts = jitter_grid(g, free, max(2.0, math.sqrt(ctx.polygon.area / n) * 0.9))
        g.shuffle(pts)
        for i, (x, y) in enumerate(pts[:n]):
            parts[i % 2].append(blob(rng, x, y, _rad(rng, section["tone_radius_m"]), 0.4, 1.5, 8))
    return (
        {"tone_a": _union(parts[0]), "tone_b": _union(parts[1])},
        {"tone_a": [cols[0]], "tone_b": [cols[1 % len(cols)]]},
    )


def _furrow_rect(rng: random.Random, poly: Polygon, fu: dict):
    """A strip of the parcel's minimum rotated rectangle, turned along its long side: (rect, angle, centre)."""
    mrr = poly.minimum_rotated_rectangle
    ring = list(mrr.exterior.coords)
    edges = sorted(zip(ring, ring[1:], strict=False), key=lambda e: -math.dist(*e))
    (a, b), (c, d) = edges[0], edges[1]
    long_len, short_len = math.dist(a, b), math.dist(c, d)
    if short_len < 6.0:
        return None
    angle = math.atan2(b[1] - a[1], b[0] - a[0])
    width = short_len * _rad(rng, fu["coverage"])
    off = (rng.random() - 0.5) * (short_len - width)
    cx, cy = mrr.centroid.x - math.sin(angle) * off, mrr.centroid.y + math.cos(angle) * off
    rect = shapely.affinity.rotate(box(cx - long_len / 2, cy - width / 2, cx + long_len / 2, cy + width / 2), angle, origin=(cx, cy), use_radians=True)
    return rect, angle, (cx, cy)


def field_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Meadow and field (``other`` parcels): tone patches, dirt headlands along the edge, some furrowed strips,
    sparse tufts and pebbles that thicken at the edge."""
    pid, fc = ctx.id, cfg["field"]
    res = DetailResult(pid)
    free = free_area(ctx, cfg, trunks=False)
    if free.is_empty:
        return res
    rng = seeded(f"{pid}:detail:field")
    verge_g, verge_d = _verge(ctx)
    band_m = float(fc["edge_band_m"])
    interior = free.buffer(-band_m)
    layers, flat = _tone_layers(ctx, seeded(f"{pid}:detail:fieldtones"), free, fc)

    head = fc["headland"]
    heads = [
        blob(rng, p.x, p.y, _rad(rng, head["radius_m"]), 0.4, float(head["stretch"]), int(head["vertices"]), ang)
        for p, ang in _along(rng, ctx.polygon.exterior, float(head["spacing_m"]), float(head["keep"]))
    ]
    rects: list[tuple[Polygon, float, tuple[float, float]]] = []
    furrow: BaseGeometry = Polygon()
    fu = fc["furrow"]
    if ctx.polygon.area >= float(fu["min_area_m2"]) and rng.random() < float(fu["probability"]):
        rect = _furrow_rect(rng, ctx.polygon, fu)
        if rect is not None:
            piece = rect[0].intersection(interior)
            if piece.area > float(fu["max_area_m2"]):  # a strip, not the whole field: keep the part nearest its middle
                piece = piece.intersection(rect[0].centroid.buffer(math.sqrt(float(fu["max_area_m2"]) / math.pi) * 1.6))
            if not piece.is_empty and piece.area > float(fu["min_piece_m2"]):
                furrow, rects = piece, [(rect[0].buffer(0.5), rect[1], rect[2])]
    layers.update({"verge_gravel": verge_g, "verge_dirt": verge_d, "furrow": furrow, "dirt": _union(heads)})
    got = _assemble(res, free, layers, {"tone_a": flat["tone_a"], "tone_b": flat["tone_b"]}, {"furrow": rects}, seeded(f"{pid}:detail:colours"))

    c, out = fc["clutter"], []
    furrow = got.get("furrow", Polygon())
    bare = _union([got.get("dirt", Polygon()), got.get("verge_dirt", Polygon()), got.get("verge_gravel", Polygon())])
    tones = _union([got.get("tone_a", Polygon()), got.get("tone_b", Polygon())])
    meadow = free.difference(furrow) if not furrow.is_empty else free
    edge_band = meadow.difference(interior)
    for kind in ("tuft", "pebble", "rock", "stick", "flower"):
        s = c.get(kind)
        if s is None:
            continue
        g = _gen(pid, kind)
        pts = _zones(g, [(bare.intersection(meadow), s.get("dirt", 0.0)), (edge_band, s.get("edge", 0.0)),
                         (tones.intersection(meadow), s.get("tone", 0.0)), (meadow, s.get("interior", 0.0))], float(s["spacing_m"]))
        out += _emit(g, kind, _trunk_filter(ctx, pts, cfg), s["scale"], ctx, cfg)
    res.clutter = out
    return res


def forest_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Forest: leaf litter under the trees, forest-floor tone patches, a few bare spots, and rocks, sticks, ferns, leaves."""
    pid, fc = ctx.id, cfg["forest"]
    res = DetailResult(pid)
    free = free_area(ctx, cfg, trunks=False)
    if free.is_empty:
        return res
    rng = seeded(f"{pid}:detail:forest")
    verge_g, verge_d = _verge(ctx)
    canopy = float(ctx.tree_cfg["canopy_m"])
    inside = [(x, y, s) for x, y, s in ctx.trees if ctx.polygon.contains(Point(x, y))]
    lit = fc["litter"]
    litter_parts = []
    for x, y, s in inside:
        r = canopy * s * float(lit["radius_factor"])
        for _ in range(int(lit["blobs_per_tree"])):
            if rng.random() > float(lit["keep"]):
                continue
            a, dist = rng.random() * math.tau, rng.random() * r * 0.6
            litter_parts.append(blob(rng, x + dist * math.cos(a), y + dist * math.sin(a), r * (0.7 + 0.5 * rng.random()), 0.35, 1.3, 8))
    layers, flat = _tone_layers(ctx, seeded(f"{pid}:detail:foresttones"), free, fc)
    d = fc["dirt"]
    dirt = []
    x0, y0, x1, y1 = free.bounds
    for _ in range(round(float(d["per_ha"]) * ctx.polygon.area / 1e4)):
        for _try in range(10):
            x, y = x0 + rng.random() * (x1 - x0), y0 + rng.random() * (y1 - y0)
            if shapely.contains_xy(free, x, y):
                dirt.append(blob(rng, x, y, _rad(rng, d["radius_m"]), 0.4, 1.4, 10))
                break
    layers.update({"verge_gravel": verge_g, "verge_dirt": verge_d, "dirt": _union(dirt), "litter": _union(litter_parts)})
    got = _assemble(res, free, layers, {"tone_a": flat["tone_a"], "tone_b": flat["tone_b"]}, {}, seeded(f"{pid}:detail:colours"))

    c, out = fc["clutter"], []
    litter = got.get("litter", Polygon())
    bare = _union([got.get("dirt", Polygon()), got.get("verge_dirt", Polygon()), got.get("verge_gravel", Polygon())])
    tones = _union([got.get("tone_a", Polygon()), got.get("tone_b", Polygon())])
    for kind in ("leaf", "stick", "rock", "pebble", "tuft"):
        s = c.get(kind)
        if s is None:
            continue
        g = _gen(pid, kind)
        pts = _zones(g, [(bare, s.get("dirt", 0.0)), (litter, s.get("litter", 0.0)), (tones, s.get("tone", 0.0)), (free, s.get("interior", 0.0))], float(s["spacing_m"]))
        out += _emit(g, kind, _trunk_filter(ctx, pts, cfg), s["scale"], ctx, cfg)
    f = c["fern"]  # ferns stand in small groups round the trunks
    g = _gen(pid, "fern")
    fp = []
    for x, y, _ in inside:
        if g.random() > float(f["per_tree"]):
            continue
        k = int(g.integers(1, int(f["group_max"]) + 1))
        a = g.random(k) * math.tau
        r = float(f["ring_m"][0]) + g.random(k) * (float(f["ring_m"][1]) - float(f["ring_m"][0]))
        fp += list(zip(x + r * np.cos(a), y + r * np.sin(a), strict=True))
    pts = np.array(fp) if fp else np.zeros((0, 2))
    if len(pts):
        pts = pts[shapely.contains_xy(free, pts[:, 0], pts[:, 1])]
    out += _emit(g, "fern", _trunk_filter(ctx, pts, cfg), f["scale"], ctx, cfg)
    res.clutter = out
    return res


def road_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Road land (``road`` parcels): the grass shoulder beside the carriageway, with gravel and dirt verge
    blobs, tufts along the asphalt edge and pebbles on the gravel."""
    pid, rc = ctx.id, cfg["road"]
    res = DetailResult(pid)
    if ctx.road is None or ctx.road.is_empty:
        return res
    free = free_area(ctx, cfg, trunks=False)
    strip = ctx.road.buffer(float(rc["grass_width_m"])).intersection(free)
    verge_g, verge_d = _verge(ctx)
    cols = [hex_to_rgb(h) for h in rc["grass_colours"]]
    got = _assemble(res, free, {"verge_gravel": verge_g, "verge_dirt": verge_d, "lawn": strip}, {"lawn": cols}, {}, seeded(f"{pid}:detail:colours"))
    c, out = rc["clutter"], []
    grass = got.get("lawn", Polygon())
    gravel, dirt = got.get("verge_gravel", Polygon()), got.get("verge_dirt", Polygon())
    for kind in ("tuft", "pebble", "rock", "flower", "leaf"):
        s = c.get(kind)
        if s is None:
            continue
        g = _gen(pid, kind)
        pts = _zones(g, [(gravel, s.get("gravel", 0.0)), (dirt, s.get("dirt", 0.0)), (grass, s.get("grass", 0.0))], float(s["spacing_m"]))
        for k, sub in (_split_kinds(g, pts, s["kinds"]).items() if "kinds" in s else [(kind, pts)]):
            out += _emit(g, k, sub, s["scale"], ctx, cfg)
    res.clutter = out
    return res


RECIPES = {"garden": garden_detail, "field": field_detail, "forest": forest_detail, "road": road_detail}


def area_type(parcel: dict) -> str | None:
    """The kind of ground of a parcel, which picks its recipe (:data:`RECIPES`); None: no detail (water).

    The only place that reads the parcel kind. A real area-type layer (gardens, fields, paths, ... drawn or
    derived per area instead of per parcel) replaces this function."""
    return {"garden": "garden", "building_plot": "garden", "other": "field", "forest": "forest", "road": "road", "water": None}.get(
        parcel["kind"], "field"
    )


# --- the village ----------------------------------------------------------------------------


def make_verge(graph: dict, cfg: dict) -> dict[str, list[Polygon]]:
    """Gravel and dirt blobs along the carriageway edges of the roads in ``cfg['highways']`` (global, per edge)."""
    out: dict[str, list[Polygon]] = {"gravel": [], "dirt": []}
    for e, line in edge_lines(graph):
        if e["highway"] not in cfg["highways"]:
            continue
        rng = seeded(f"{e['id']}:detail:verge")
        half, length = float(e["width"]) / 2, line.length
        s = rng.random() * float(cfg["step_m"][1])
        while s < length:
            p, q = line.interpolate(s), line.interpolate(min(length, s + 0.5))
            ang = math.atan2(q.y - p.y, q.x - p.x)
            for side in (-1, 1):
                if rng.random() > float(cfg["keep"]):
                    continue
                off = half + _rad(rng, cfg["offset_m"])
                cx, cy = p.x - side * math.sin(ang) * off, p.y + side * math.cos(ang) * off
                b = blob(rng, cx, cy, _rad(rng, cfg["radius_m"]), 0.35, _rad(rng, cfg["stretch"]), int(cfg["vertices"]), ang)
                out["gravel" if rng.random() < float(cfg["gravel_share"]) else "dirt"].append(b)
            s += _rad(rng, cfg["step_m"])
    return out


class Village:
    """The work files of an area, indexed, and one :class:`ParcelContext` per parcel part."""

    def __init__(self, work_dir: Path, rules_path: Path, area: str | None, edits_path: Path) -> None:
        from drycastle_pipeline.scene import ParcelColours  # heavy imports, only for the stage

        self.cfg = load_section("detail", rules_path, area)
        self.tree_cfg = load_section("props", rules_path, area)["trees"]
        self.terrain, _ = load_terrain(work_dir)
        parcels_doc = json.loads((work_dir / "parcels.json").read_text())
        self.chunk_m = float(parcels_doc["chunk_size_m"])
        self.parcels = parcels_doc["parcels"]
        buildings = json.loads((work_dir / "buildings.json").read_text())["buildings"]
        props = json.loads((work_dir / "props.json").read_text())
        graph = json.loads((work_dir / "roads.json").read_text())
        objects = json.loads((work_dir / "objects.json").read_text())["objects"] if (work_dir / "objects.json").is_file() else []
        colours = ParcelColours(self.parcels, load_section("terrain", rules_path)["colours"])
        self.colour = {p["id"]: colours._pick(p) for p in self.parcels}
        self.buildings = [footprint_polygon(b["footprint"]) for b in buildings]
        self.b_tree = shapely.STRtree(self.buildings)
        self.fences = [LineString([s["a"], s["b"]]) for s in props["fences"]]
        self.f_tree = shapely.STRtree(self.fences)
        self.trees = [(t["x"], t["y"], float(t["scale"])) for t in props["trees"]]
        self.t_tree = shapely.STRtree([Point(t[0], t[1]) for t in self.trees])
        self.road_polys = [ln.buffer(float(e["width"]) / 2) for e, ln in edge_lines(graph)]
        self.r_tree = shapely.STRtree(self.road_polys)
        sizes = {a["id"]: a.get("size_m", [1, 1, 1]) for a in load_manifest().get("assets", [])}
        self.object_polys = []
        for o in objects:
            sx, _, sz = sizes.get(o["asset"], [1.0, 1.0, 1.0])
            r = 0.5 * math.hypot(sx, sz) * float(o.get("scale", 1.0)) + float(self.cfg["object_clearance_m"])
            self.object_polys.append(Point(o["position"][0], o["position"][1]).buffer(r, 8))
        self.o_tree = shapely.STRtree(self.object_polys)
        plan = plan_regions(load_edits(edits_path), parcels_doc.get("origin_epsg5514"), self.terrain)
        self.warnings = list(plan.warnings)
        self.regions = unary_union([r.polygon for r in plan.regions.values()]) if plan.regions else Polygon()
        shapely.prepare(self.regions)
        self.water_polys = [shape(p["polygon"]) for p in self.parcels if p["kind"] == "water"]
        self.w_tree = shapely.STRtree(self.water_polys)
        self.verge_polys = make_verge(graph, self.cfg["verge"])
        self.v_tree = {k: shapely.STRtree(v) for k, v in self.verge_polys.items()}

    @staticmethod
    def _near(tree: shapely.STRtree, items: list, geom: BaseGeometry) -> list:
        return [items[i] for i in tree.query(geom, predicate="intersects")]

    def contexts(self, parcel: dict):
        """One context per polygon of the parcel clipped to the terrain coverage (the largest keeps the ID)."""
        poly = shape(parcel["polygon"]).intersection(self.terrain.coverage)
        parts = sorted(_polys(poly), key=lambda p: -p.area)
        for k, part in enumerate(parts):
            if part.area < float(self.cfg["min_parcel_m2"]):
                continue
            near = part.buffer(_NEAR_M)
            roads = self._near(self.r_tree, self.road_polys, near)
            blocked = self._near(self.o_tree, self.object_polys, part)
            if not self.regions.is_empty and self.regions.intersects(part):
                blocked.append(self.regions.intersection(part.buffer(1.0)))
            verge = {
                name: _union(self._near(tree, self.verge_polys[name], part)).intersection(part)
                for name, tree in self.v_tree.items()
            }
            yield ParcelContext(
                id=parcel["id"] if k == 0 else f"{parcel['id']}.{k}",
                kind=parcel["kind"],
                polygon=part,
                buildings=self._near(self.b_tree, self.buildings, near),
                fences=self._near(self.f_tree, self.fences, near),
                trees=self._near(self.t_tree, self.trees, near),
                road=_union(roads) if roads else None,
                terrain=self.terrain,
                tree_cfg=self.tree_cfg,
                colour=self.colour[parcel["id"]],
                blocked=_union(blocked) if blocked else None,
                verge=verge,
                water=_union(self._near(self.w_tree, self.water_polys, part.buffer(float(self.cfg["tint"]["water_m"])))) if self.water_polys else None,
            )


@dataclass
class VillageDetail:
    chunk_m: float
    clutter: dict[tuple[int, int], dict[str, list[list[float]]]]
    patches: np.ndarray  # (n, _PATCH_ROW)
    textures: list[str]
    stats: dict = field(default_factory=dict)
    meadow: "meadow_mod.Meadow | None" = None  # density raster of the clutter the game generates (field, forest)


def cap_clutter(chunks: dict, caps: dict[str, int]) -> dict:
    """At most ``caps[kind]`` instances of a kind per chunk (0 or no entry = no cap). Which ones go is decided
    by a hash of the position, so it is stable and spread over the chunk."""
    for kinds in chunks.values():
        for kind, rows in kinds.items():
            limit = int(caps.get(kind, 0))
            if limit > 0 and len(rows) > limit:
                rows.sort(key=lambda r: hashlib.sha256(f"{r[0]}:{r[1]}:{kind}:cap".encode()).hexdigest())
                del rows[limit:]
                rows.sort()
    return chunks


def patch_rows(flat: Mesh, textured: dict[str, TexturedMesh], textures: list[str]) -> np.ndarray:
    """Patch triangles as rows of :data:`_PATCH_ROW` numbers (the layout of ``detail_patches.npy``)."""
    rows = []
    if flat.face_count:
        r = np.zeros((flat.face_count, _PATCH_ROW))
        r[:, :9] = flat.tris.reshape(-1, 9)
        r[:, 9:12] = flat.colours
        r[:, 18] = -1
        rows.append(r)
    for tid, m in textured.items():
        r = np.zeros((m.face_count, _PATCH_ROW))
        r[:, :9] = m.tris.reshape(-1, 9)
        r[:, 9:12] = m.colours
        r[:, 12:18] = m.uvs.reshape(-1, 6)
        r[:, 18] = textures.index(tid)
        rows.append(r)
    return np.concatenate(rows) if rows else np.zeros((0, _PATCH_ROW))


def build_village_detail(
    work_dir: Path, rules_path: Path = DEFAULT_RULES, area: str | None = None, edits_path: Path = DEFAULT_EDITS,
    only: set[str] | None = None, log=None,
) -> VillageDetail:
    """The detail of every parcel of an area (``only``: just these parcel IDs; tests, previews)."""
    v = Village(work_dir, rules_path, area, edits_path)
    for w in v.warnings:
        print(f"detail: warning: {w}", file=sys.stderr)
    lift = float(v.cfg["lift_m"])
    textures = sorted(set(PATCH_TEXTURE.values()))
    chunks: dict[tuple[int, int], dict[str, list[list[float]]]] = {}
    rows = []
    by_type: dict[str, dict[str, int]] = {}
    mcfg = v.cfg["meadow"]
    meadow = meadow_mod.new_meadow(mcfg, v.chunk_m)
    stored = set(mcfg["stored_kinds"])
    meadow_cells = 0
    t0 = time.time()
    for n, parcel in enumerate(sorted(v.parcels, key=lambda p: p["id"])):
        atype = area_type(parcel)
        if atype is None or (only is not None and parcel["id"] not in only):
            continue
        for ctx in v.contexts(parcel):
            res = RECIPES[atype](ctx, v.cfg)
            if atype in meadow_mod.MEADOW_TYPES:
                # the game generates the dense kinds from the raster; the recipe's own instances stay only for the rest
                res.clutter = [c for c in res.clutter if c["kind"] in stored]
                band = float(v.cfg[atype].get("edge_band_m", 0.0))
                meadow_cells += meadow_mod.add_parcel(meadow, ctx, res, atype, free_area(ctx, v.cfg, trunks=False), mcfg, band)
            flat, tex = patch_meshes(res, v.terrain, lift, by_type.setdefault(atype, {}))
            rows.append(patch_rows(flat, tex, textures))
            for c in res.clutter:
                key = tuple(chunk_of(c["x"], c["y"], v.chunk_m))
                row = [round(c["x"], 2), round(c["y"], 2), round(c["z"], 2), round(c["rot"], 2), round(c["scale"], 2)]
                if "tint" in c:
                    row.append(c["tint"])
                chunks.setdefault(key, {}).setdefault(c["kind"], []).append(row)
        if log and n % 200 == 0:
            log(f"{n}/{len(v.parcels)} parcels, {time.time() - t0:.0f} s")
    count = lambda: {k: sum(len(kinds.get(k, [])) for kinds in chunks.values()) for k in CLUTTER_KINDS}  # noqa: E731
    before = count()
    cap_clutter(chunks, v.cfg["caps_per_chunk"])
    patches = np.concatenate(rows) if rows else np.zeros((0, _PATCH_ROW))
    stats = {
        "clutter_before_caps": before,
        "clutter": count(),
        "max_per_chunk": {k: max((len(kinds.get(k, [])) for kinds in chunks.values()), default=0) for k in CLUTTER_KINDS},
        "patch_triangles": len(patches),
        "patch_triangles_by_type": {a: dict(sorted(l.items())) | {"total": sum(l.values())} for a, l in sorted(by_type.items())},
        "meadow_cells": meadow_cells,
        "meadow_expected": {k: round(x) for k, x in meadow.expected().items()},
        "meadow_busiest": meadow.busiest({"tuft": 70, "fern": 70, "flower": 55, "pebble": 40, "litter": 40}),
        "seconds": round(time.time() - t0, 1),
    }
    return VillageDetail(v.chunk_m, chunks, patches, textures, stats, meadow)


def clutter_document(detail: VillageDetail) -> dict:
    """The ``clutter.json`` document: one mesh per kind (``clutter_meshes.glb``), instances per chunk and kind."""
    chunks = {}
    for ix, iy in sorted(detail.clutter):
        by_kind = {k: rows for k, rows in sorted(detail.clutter[(ix, iy)].items()) if rows}
        if by_kind:
            chunks[f"{ix}_{iy}"] = by_kind
    return {
        "format_version": DETAIL_FORMAT_VERSION,
        "axes": "map metres: x east, y north, z up (ground height at the instance)",
        "chunk_size_m": detail.chunk_m,
        "kinds": {k: ({"mesh": k, "tint": True} if k in TINTABLE else {"mesh": k}) for k in CLUTTER_KINDS},
        "chunks": chunks,
    }


def write_detail(work_dir: Path, detail: VillageDetail) -> None:
    (work_dir / "detail.json").write_text(json.dumps(clutter_document(detail), separators=(",", ":")) + "\n")
    np.save(work_dir / "detail_patches.npy", detail.patches)
    if detail.meadow is not None and detail.meadow.chunks:
        (work_dir / "detail_meadow.bin").write_bytes(meadow_mod.encode(detail.meadow))
    else:
        (work_dir / "detail_meadow.bin").unlink(missing_ok=True)
    (work_dir / "detail_patches.json").write_text(
        json.dumps({"format_version": DETAIL_FORMAT_VERSION, "textures": detail.textures,
                    "row": "x0 y0 z0 x1 y1 z1 x2 y2 z2 r g b u0 v0 u1 v1 u2 v2 texture_index(-1: flat colour)"}, indent=1) + "\n"
    )


def load_patches(work_dir: Path) -> tuple[np.ndarray, list[str]] | None:
    """The patch rows and texture ids of the work files, or None when the stage has not run."""
    npy, meta = work_dir / "detail_patches.npy", work_dir / "detail_patches.json"
    if not (npy.is_file() and meta.is_file()):
        return None
    return np.load(npy), json.loads(meta.read_text())["textures"]


def patches_by_chunk(rows: np.ndarray, textures: list[str], chunk_m: float, k: float = 1.0) -> dict[tuple[int, int], dict]:
    """``{chunk: {"detail_patches": Mesh, "detail_patches_tex_<id>": TexturedMesh}}`` from the patch rows
    (``k`` scales heights, preview only). A triangle belongs to the chunk of its centroid; terrain cells never
    cross a chunk border, so no patch triangle does."""
    out: dict[tuple[int, int], dict] = {}
    if not len(rows):
        return out
    tris = rows[:, :9].reshape(-1, 3, 3).copy()
    tris[:, :, 2] *= k
    c = tris.mean(axis=1)
    ix, iy = np.floor(c[:, 0] / chunk_m).astype(int), np.floor(c[:, 1] / chunk_m).astype(int)
    tid = rows[:, 18].astype(int)
    for key in sorted(set(zip(ix.tolist(), iy.tolist(), strict=True))):
        in_chunk = (ix == key[0]) & (iy == key[1])
        nodes: dict = {}
        flat = in_chunk & (tid < 0)
        if flat.any():
            nodes["detail_patches"] = Mesh(tris[flat], rows[flat, 9:12].astype(np.uint8))
        for i, name in enumerate(textures):
            sel = in_chunk & (tid == i)
            if sel.any():
                nodes[tex_node_name("detail_patches", name)] = TexturedMesh(
                    tris[sel], rows[sel, 9:12].astype(np.uint8), rows[sel, 12:18].reshape(-1, 3, 2)
                )
        out[key] = nodes
    return out


# --- single parcel (tests, preview) ---------------------------------------------------------


def run_parcel(
    parcel_id: str,
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    area: str | None = None,
    edits_path: Path = DEFAULT_EDITS,
    with_meadow: bool = False,
    meadow_window: float = 56.0,
) -> tuple[ParcelContext, DetailResult]:
    """The detail of one parcel, with the same inputs and recipe as the stage. ``with_meadow`` (field and forest
    only) swaps the recipe's dense kinds for what the game would generate from the raster of this parcel (the
    instances of :func:`meadow.generate`, minus those within a trunk's reach), for the preview, within a
    ``meadow_window`` metre square round the parcel's representative point (a forest is too big to draw)."""
    v = Village(work_dir, rules_path, area, edits_path)
    parcel = next((p for p in v.parcels if p["id"] == parcel_id), None)
    if parcel is None:
        raise ValueError(f"no parcel {parcel_id}")
    atype = area_type(parcel)
    if atype is None:
        raise ValueError(f"parcel {parcel_id} ({parcel['kind']}) has no ground detail")
    ctx = next(v.contexts(parcel))
    res = RECIPES[atype](ctx, v.cfg)
    if with_meadow and atype in meadow_mod.MEADOW_TYPES:
        mcfg = v.cfg["meadow"]
        m = meadow_mod.new_meadow(mcfg, v.chunk_m)
        meadow_mod.add_parcel(m, ctx, res, atype, free_area(ctx, v.cfg, trunks=False), mcfg, float(v.cfg[atype].get("edge_band_m", 0.0)))
        res.clutter = [c for c in res.clutter if c["kind"] in set(mcfg["stored_kinds"])]
        rp = ctx.polygon.representative_point()
        h = meadow_window / 2
        x0, y0, x1, y1 = max(rp.x - h, ctx.polygon.bounds[0]), max(rp.y - h, ctx.polygon.bounds[1]), min(rp.x + h, ctx.polygon.bounds[2]), min(rp.y + h, ctx.polygon.bounds[3])
        cell = m.cell_m
        trunks = shapely.points([(x, y) for x, y, _ in ctx.trees]) if ctx.trees else None
        for c in meadow_mod.generate(m, math.floor(x0 / cell), math.ceil(x1 / cell), math.floor(y0 / cell), math.ceil(y1 / cell)):
            if trunks is not None and len(shapely.STRtree(trunks).query(Point(c["x"], c["y"]), predicate="dwithin", distance=m.trunk_reach_m)):
                continue
            res.clutter.append({**c, "z": 0.0})
    return ctx, res


# --- meshes ---------------------------------------------------------------------------------


def patch_meshes(res: DetailResult, terrain: Terrain, lift_m: float, tally: dict | None = None) -> tuple[Mesh, dict[str, TexturedMesh]]:
    """The patches as ground triangles: one flat-colour mesh and one textured mesh per texture id.

    Textured UVs are world coordinates in tile repeats (rotated by the patch's angle), so a texture
    runs on across neighbouring patches and parcels. ``tally`` (a dict) gets the triangle count per layer added.
    """
    flat: list[Mesh] = []
    textured: dict[str, list[TexturedMesh]] = {}
    for p in res.patches:
        if p.texture is None:
            m = drape(p.geom, p.colour or [128, 128, 128], terrain, [], lift_m, lift_m)
            if m.face_count:
                flat.append(m)
                if tally is not None:
                    tally[p.layer] = tally.get(p.layer, 0) + m.face_count
            continue
        m = drape(p.geom, [255, 255, 255], terrain, [], lift_m, lift_m)
        if not m.face_count:
            continue
        if tally is not None:
            tally[p.layer] = tally.get(p.layer, 0) + m.face_count
        tex = texture(p.texture)
        c, s = math.cos(-p.angle), math.sin(-p.angle)
        dx, dy = m.tris[:, :, 0] - p.origin[0], m.tris[:, :, 1] - p.origin[1]
        u, v = (dx * c - dy * s) / tex.tile_w, (dx * s + dy * c) / tex.tile_h
        textured.setdefault(p.texture, []).append(TexturedMesh(m.tris, m.colours, np.stack([u, v], axis=2)))
    return Mesh.concat(flat), {tid: TexturedMesh.concat(ms) for tid, ms in sorted(textured.items())}


# --- the stage ------------------------------------------------------------------------------


def run(force: bool = False, area: str = DEFAULT_AREA, edits: str | None = None, work: str | None = None, **_options: object) -> int:
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    work_dir = Path(work) if work else area_paths(area).work
    try:
        detail = build_village_detail(work_dir, area=area, edits_path=edits_path, log=lambda m: print(f"detail: {m}", file=sys.stderr))
    except FileNotFoundError as exc:
        print(f"detail: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"detail: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    write_detail(work_dir, detail)
    s = detail.stats
    print(
        f"detail: patch triangles: {s['patch_triangles']}, clutter: "
        + ", ".join(f"{k}: {n}" for k, n in s["clutter"].items())
        + f" ({s['seconds']} s)"
    )
    print(f"detail: meadow: {s['meadow_cells']} cells; expected instances " + ", ".join(f"{k}: {n}" for k, n in s["meadow_expected"].items())
          + "; busiest 70 m disc " + ", ".join(f"{k}: {n}" for k, n in s["meadow_busiest"].items()), file=sys.stderr)
    print("detail: patch triangles by area type: " + "; ".join(f"{a} {l['total']}" for a, l in s["patch_triangles_by_type"].items()), file=sys.stderr)
    return 0
