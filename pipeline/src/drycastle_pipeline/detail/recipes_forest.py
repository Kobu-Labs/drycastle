"""The forest recipe: litter, moss, tones, mud and puddles under the trees, and its clutter."""

import math

import numpy as np
import shapely
from shapely.geometry import Point, Polygon
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.model import DetailResult, ParcelContext
from drycastle_pipeline.detail.parts import (
    _assemble,
    _emit,
    _tone_layers,
    _trunk_filter,
    _verge,
    _zones,
    free_area,
)
from drycastle_pipeline.detail.swamp import DAMP_SALT, _add_swamp, _hollow
from drycastle_pipeline.detail.tools import _gen, _rad, _union, blob, scatter
from drycastle_pipeline.props.helpers import seeded

MOSS_SALT = 5100  # the noise of the moss clumps (the Rust stage reads the same number)


def _moss(ctx: ParcelContext, cfg: dict, free: BaseGeometry, trees: list[tuple[float, float, float]],
          verge_g: BaseGeometry, verge_d: BaseGeometry) -> BaseGeometry:
    """Moss patches of the forest floor: small blobs at the tree feet and over the open floor, thick in the clumps of a
    position-only noise (``meadow.clump``, so a tiled run agrees with itself) and thin in the gaps. None within ``edge_m`` of the
    carriageway and its verges: the ground there is trodden. Every number is ``detail.forest.moss``."""
    mc = cfg["forest"]["moss"]
    edge = float(mc["edge_m"])
    near = [verge_g, verge_d]
    if ctx.road is not None and not ctx.road.is_empty:
        near.append(ctx.road.buffer(float(cfg["road_clearance_m"]), quad_segs=2))
    ban = _union(near).buffer(edge, quad_segs=2)
    allowed = free.difference(ban) if not ban.is_empty else free
    if allowed.is_empty:
        return Polygon()

    def dens(xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
        return meadow_mod.clump(xs, ys, float(mc["clump"]), float(mc["clump_m"]), MOSS_SALT)

    rng, g = seeded(f"{ctx.id}:detail:moss"), _gen(ctx.id, "moss")
    parts = []
    t = mc["tree"]
    for x, y, _ in trees:
        if rng.random() >= float(t["keep"]) * float(dens(np.array([x]), np.array([y]))[0]):
            continue
        a, dist = rng.random() * math.tau, float(t["reach_m"][0]) + rng.random() * (float(t["reach_m"][1]) - float(t["reach_m"][0]))
        parts.append(blob(rng, x + dist * math.cos(a), y + dist * math.sin(a), _rad(rng, t["radius_m"]), 0.4, 1.3, 8))
    i = mc["interior"]
    pts = scatter(g, allowed, float(i["spacing_m"]), lambda xs, ys: float(i["keep"]) * dens(xs, ys))
    for x, y in pts:
        parts.append(blob(rng, float(x), float(y), _rad(rng, i["radius_m"]), 0.4, float(i["stretch"]), 10))
    moss = _union(parts)
    return moss.difference(ban) if not ban.is_empty else moss


def _forest_mud(ctx: ParcelContext, cfg: dict, free: BaseGeometry) -> tuple[BaseGeometry, BaseGeometry]:
    """Mud and puddles of the plain forest floor, outside the swamp (``detail.forest.mud``): a few patches per hectare in damp spots (a noise,
    likelier in hollows) and at the edge of the paths and tracks; a puddle in the middle of some. (mud, puddles)."""
    mc, hol = cfg["forest"]["mud"], cfg["hollow"]
    area = free.difference(ctx.swamp) if ctx.swamp is not None else free
    if area.is_empty:
        return Polygon(), Polygon()
    rng, g = seeded(f"{ctx.id}:detail:mud"), _gen(ctx.id, "mud")
    spots: list[tuple[float, float, float | None]] = []  # x, y, heading of the way it lies along
    d = mc["damp"]

    def damp(xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
        return (float(d["keep"]) * meadow_mod.clump(xs, ys, float(d["clump"]), float(d["clump_m"]), DAMP_SALT)
                * (1.0 + float(hol["gain"]) * _hollow(ctx.terrain, xs, ys, hol)))

    spots += [(float(x), float(y), None) for x, y in scatter(g, area, float(d["spacing_m"]), damp)]
    tr = mc["track"]
    for eid, line, width in ctx.tracks or []:
        if line.length < 1.0 or line.distance(area) > 3.0:
            continue
        er = seeded(f"{eid}:detail:mud")  # the way's own stream: every parcel it crosses sees the same spots
        s = er.random() * float(tr["spacing_m"])
        while s < line.length:
            if er.random() < float(tr["keep"]):
                p, q = line.interpolate(s), line.interpolate(min(line.length, s + 0.5))
                ang = math.atan2(q.y - p.y, q.x - p.x)
                off = width / 2 + _rad(er, mc["radius_m"]) * 0.4
                side = 1 if er.random() < 0.5 else -1
                spots.append((p.x - side * math.sin(ang) * off, p.y + side * math.cos(ang) * off, ang))
            s += float(tr["spacing_m"]) * (0.6 + 0.8 * er.random())
    mud, puddles = [], []
    for x, y, ang in spots:
        if not shapely.contains_xy(area, x, y):
            continue
        r = _rad(rng, mc["radius_m"])
        mud.append(blob(rng, x, y, r, 0.4, float(mc["stretch"]), 10, ang))
        if rng.random() < float(mc["puddle_share"]):
            puddles.append(blob(rng, x, y, r * _rad(rng, mc["puddle_scale"]), 0.35, 1.3, 8))
    return _union(mud), _union(puddles)


def forest_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Forest: leaf litter and moss under the trees, forest-floor tone patches, a few bare spots, a few mud patches and puddles, and rocks,
    sticks, ferns, leaves. In swamp ground (``ctx.swamp``) the wet floor of :func:`_swamp_layers` replaces tones and most of the litter and dirt."""
    pid, fc = ctx.id, cfg["forest"]
    res = DetailResult(pid)
    free = free_area(ctx, cfg, trunks=False)
    if free.is_empty:
        return res
    rng = seeded(f"{pid}:detail:forest")
    verge_g, verge_d = _verge(ctx)
    canopy = float(ctx.tree_cfg["canopy_m"])
    inside = [(x, y, s) for x, y, s in ctx.trees if ctx.polygon.contains(Point(x, y))]
    swamp = ctx.swamp
    wet_tree = shapely.contains_xy(swamp, [t[0] for t in inside], [t[1] for t in inside]) if swamp is not None and inside else np.zeros(len(inside), bool)
    lit = fc["litter"]
    litter_parts = []
    for (x, y, s), wet in zip(inside, wet_tree, strict=True):
        r = canopy * s * float(lit["radius_factor"])
        keep = float(lit["keep"]) * (float(cfg["swamp"]["litter_factor"]) if wet else 1.0)
        for _ in range(int(lit["blobs_per_tree"])):
            if rng.random() > keep:
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
                if swamp is not None and shapely.contains_xy(swamp, x, y) and rng.random() >= float(cfg["swamp"]["dirt_factor"]):
                    break
                dirt.append(blob(rng, x, y, _rad(rng, d["radius_m"]), 0.4, 1.4, 10))
                break
    layers.update({"verge_gravel": verge_g, "verge_dirt": verge_d, "dirt": _union(dirt), "litter": _union(litter_parts),
                   "moss": _moss(ctx, cfg, free, inside, verge_g, verge_d)})
    layers["mud"], layers["puddle"] = _forest_mud(ctx, cfg, free)
    colours = {"tone_a": flat["tone_a"], "tone_b": flat["tone_b"]}
    if swamp is not None:
        _add_swamp(ctx, cfg, free, layers, colours)
    got = _assemble(res, free, layers, colours, {}, seeded(f"{pid}:detail:colours"))

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
