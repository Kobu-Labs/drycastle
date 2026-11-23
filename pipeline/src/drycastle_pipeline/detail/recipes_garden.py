"""The garden recipe (``building_plot`` and ``garden`` parcels): gravel path, beds, dirt, litter, lawn and tones, and its clutter."""

import math
import random

import numpy as np
import shapely
import shapely.affinity
from shapely.geometry import LineString, Point, Polygon, box
from shapely.geometry.base import BaseGeometry
from shapely.ops import nearest_points, unary_union

from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.model import CELL_M, DetailResult, ParcelContext
from drycastle_pipeline.detail.parts import _assemble, _emit, _verge, free_area, palette
from drycastle_pipeline.detail.tools import (
    _gen,
    _lines,
    _rad,
    _union,
    blob,
    distance_to,
    poisson_disc,
    scatter,
)
from drycastle_pipeline.props.helpers import seeded


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


def _own_buildings(ctx: ParcelContext) -> list:
    """The buildings standing on this parcel (``ctx.buildings`` also holds those within 8 m on the neighbours' ground, which
    are only there to keep clearance: a path or a bed must never lead to one of them)."""
    return [b for b in ctx.buildings if b.intersects(ctx.polygon)]


def _gravel_path(ctx: ParcelContext, free: BaseGeometry, cfg: dict, rng: random.Random) -> BaseGeometry:
    """A wobbling strip from the gate to the nearest building (none without a gate or a building)."""
    g = cfg["gravel"]
    own = _own_buildings(ctx)
    if not own or free.is_empty:
        return Polygon()
    start = _gate_point(ctx, cfg)
    if start is None:
        return Polygon()
    target_b = min(own, key=lambda b: b.distance(start))
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
    own = _own_buildings(ctx)
    if n <= 0 or not own:
        return []
    main = max(own, key=lambda p: p.area)
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
