"""Shared recipe parts: palettes, the free ground of a parcel, clutter emission, scatter zones and patch assembly."""

import math
import random

import numpy as np
import shapely
from shapely.geometry import Point, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.core.colour import blend, hex_to_rgb
from drycastle_pipeline.core.geometry import polygons
from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.meshes import TINTABLE
from drycastle_pipeline.detail.model import (
    _RECT_LAYERS,
    _SOFT_LAYERS,
    CELL_M,
    PATCH_PRIORITY,
    PATCH_TEXTURE,
    DetailResult,
    ParcelContext,
    Patch,
)
from drycastle_pipeline.detail.tools import (
    _clean,
    _difference,
    _gen,
    _rad,
    _union,
    blob,
    jitter_grid,
    scatter,
    terrain_ok,
)


def _shade(colour: list[int], f: float) -> list[int]:
    return [max(0, min(255, round(c * f))) for c in colour]


def palette(ctx: ParcelContext, fixed: list[str], shades: list[float], mix_by_kind: dict | float, default_mix: float = 1.0
            ) -> tuple[list[list[int]], float]:
    """Colours of a flat layer and how much they follow the parcel's terrain colour.

    Each colour is the fixed one at ``mix`` 0 and the terrain colour times its shade at 1, so a lawn on a
    green garden is a few shades of that garden's green, and on a tan plot it is still green-ish. ``mix_by_kind`` is
    one mix for all, or one per parcel kind (the others get ``default_mix``)."""
    w = float(mix_by_kind.get(ctx.kind, default_mix) if isinstance(mix_by_kind, dict) else mix_by_kind)
    out = []
    for i, hx in enumerate(fixed):
        c = hex_to_rgb(hx)
        if ctx.colour is not None and w > 0:
            c = blend(c, _shade(ctx.colour, shades[i % len(shades)]), w)
        out.append(c)
    return out, w


def free_area(ctx: ParcelContext, cfg: dict, trunks: bool = True) -> BaseGeometry:
    """The parcel without buildings (and a wall margin), fences, trunks, placed objects, water patches and the road.

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
        zone = _difference(geom, taken)
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
        if layer in ("lawn", "swamp"):
            for poly in polygons(g):
                res.patches.append(Patch(layer, poly, colour=flat[layer][rng.randrange(len(flat[layer]))]))
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
