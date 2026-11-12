"""Ground detail of a yard: draped patches (lawn tones, bare soil, gravel, leaf litter, beds) and
clutter instances (tufts, flowers, pebbles, rocks, leaves). Prototype: not a pipeline stage yet.

* **Patches** are polygons on the ground, flat-coloured or textured from the texture library, draped on
  the terrain by :func:`regions.drape` (so they follow the ground triangle by triangle) at ``detail.lift_m``.
  They never overlap: the layers are clipped in the order of :data:`PATCH_PRIORITY` (first wins), so
  nothing z-fights. Hand-drawn regions (3 cm) float above them.
* **Clutter** is data only: ``{"kind", "variant", "x", "y", "z", "rot", "scale"}`` per instance. The game will
  draw them as instanced meshes near the player; nothing in the map holds them as triangles.
* **Everything is seeded** with ``sha256("<parcel id>:detail:<purpose>")`` (:func:`props.seeded`), one
  generator per purpose, so identical input gives identical output and a change to one recipe step does
  not move the others.
* **Exclusion**: nothing lands on a building (plus a wall margin), on a fence, a trunk or the road.
  Crowns do not exclude: leaf litter belongs under them.

:func:`garden_detail` is the recipe for a building plot or garden. The tools it is made of (:func:`blob`,
:func:`poisson_disc`, :func:`thin`) are the same ones other area types would use.
"""

import json
import math
import random
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import shapely
import shapely.affinity
from shapely.geometry import LineString, MultiPolygon, Point, Polygon, box, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import nearest_points, unary_union

from suchohrad_pipeline.buildings import footprint_polygon, hex_to_rgb
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.props import road_zone, seeded
from suchohrad_pipeline.regions import drape
from suchohrad_pipeline.rules import DEFAULT_RULES, load_section
from suchohrad_pipeline.terrain import Terrain, load_terrain
from suchohrad_pipeline.textures import TexturedMesh, texture

CLUTTER_KINDS = ("tuft", "flower", "pebble", "rock", "leaf")
# The first layer wins where two overlap (a path crosses lawn, litter lies on lawn, ...).
PATCH_PRIORITY = ("gravel", "bed", "dirt", "litter", "lawn_tone", "lawn")
# Library texture of a layer; layers without one are flat colour (``detail.lawn`` colours).
PATCH_TEXTURE = {"gravel": "gravel", "bed": "soil_rows", "dirt": "dirt", "litter": "leaf_litter"}
_NEAR_ROAD_M = 60.0


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
    """What the recipe needs about one parcel, in local metres."""

    id: str
    kind: str
    polygon: Polygon
    buildings: list[Polygon]
    fences: list[LineString]
    trees: list[tuple[float, float, float]]  # x, y, scale (near the parcel, from any parcel)
    road: BaseGeometry | None  # carriageways near the parcel
    terrain: Terrain
    tree_cfg: dict


# --- loading --------------------------------------------------------------------------------


def load_context(
    parcel_id: str,
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    area: str | None = None,
    terrain: Terrain | None = None,
) -> ParcelContext:
    """The parcel and what stands around it, from the work files of an area."""
    parcels = json.loads((work_dir / "parcels.json").read_text())["parcels"]
    parcel = next((p for p in parcels if p["id"] == parcel_id), None)
    if parcel is None:
        raise ValueError(f"no parcel {parcel_id}")
    poly = shape(parcel["polygon"])
    if isinstance(poly, MultiPolygon):
        poly = max(poly.geoms, key=lambda g: g.area)
    near = poly.buffer(8.0)
    buildings = [
        footprint_polygon(b["footprint"])
        for b in json.loads((work_dir / "buildings.json").read_text())["buildings"]
    ]
    props = json.loads((work_dir / "props.json").read_text())
    fences = [LineString([s["a"], s["b"]]) for s in props["fences"]]
    graph = json.loads((work_dir / "roads.json").read_text())
    if terrain is None:
        terrain, _ = load_terrain(work_dir)
    shapely.prepare(near)
    road = road_zone(
        {"edges": [e for e in graph["edges"] if LineString(e["polyline"]).distance(poly) < _NEAR_ROAD_M]},
        0.0,
    )
    return ParcelContext(
        id=parcel_id,
        kind=parcel["kind"],
        polygon=poly,
        buildings=[b for b in buildings if b.intersects(near)],
        fences=[f for f in fences if near.intersects(f)],
        trees=[(t["x"], t["y"], float(t["scale"])) for t in props["trees"] if near.contains(Point(t["x"], t["y"]))],
        road=road,
        terrain=terrain,
        tree_cfg=load_section("props", rules_path, area)["trees"],
    )


# --- tools ----------------------------------------------------------------------------------


def blob(
    rng: random.Random,
    cx: float,
    cy: float,
    radius: float,
    wobble: float = 0.3,
    stretch: float = 1.0,
    n: int = 20,
) -> Polygon:
    """A smooth irregular blob: radius ``radius`` times (1 + wobble * noise) round the angle, stretched
    along a random axis by ``stretch`` (1 = round). The noise is three random harmonics."""
    harmonics = [(k, rng.random() * math.tau, 1.0 / k) for k in (2, 3, 5)]
    norm = sum(a for _, _, a in harmonics)
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
    set for the same generator state. Disconnected parts are reached by restarting from random seeds."""
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


def distance_to(geom: BaseGeometry | None):
    """A function (xs, ys) -> distance of each point to ``geom`` (inf when there is none)."""
    if geom is None or geom.is_empty:
        return lambda xs, ys: np.full(len(xs), np.inf)
    return lambda xs, ys: shapely.distance(shapely.points(xs, ys), geom)


def falloff(dist: np.ndarray, reach: float, near: float, far: float) -> np.ndarray:
    """``near`` at the object, ``far`` at ``reach`` metres and beyond, linear in between."""
    t = np.clip(dist / max(reach, 1e-9), 0.0, 1.0)
    return near + (far - near) * t


def _polys(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom]
    return [p for g in getattr(geom, "geoms", []) for p in _polys(g)]


def _rad(rng: random.Random, span: list[float]) -> float:
    return span[0] + rng.random() * (span[1] - span[0])


# --- the garden recipe ----------------------------------------------------------------------


def free_area(ctx: ParcelContext, cfg: dict) -> BaseGeometry:
    """The parcel without buildings (and a wall margin), fences, trunks and the road."""
    blocked = [b.buffer(float(cfg["wall_clearance_m"])) for b in ctx.buildings]
    blocked += [f.buffer(float(cfg["fence_clearance_m"])) for f in ctx.fences]
    blocked += [Point(x, y).buffer(float(cfg["trunk_radius_m"])) for x, y, _ in ctx.trees]
    if ctx.road is not None:
        blocked.append(ctx.road.buffer(float(cfg["road_clearance_m"])))
    free = ctx.polygon
    if blocked:
        free = free.difference(unary_union(blocked))
    return free


def _gravel_path(ctx: ParcelContext, free: BaseGeometry, cfg: dict, rng: random.Random) -> BaseGeometry:
    """A wobbling strip from the road side of the parcel to the nearest building."""
    g = cfg["gravel"]
    if not ctx.buildings or free.is_empty:
        return Polygon()
    boundary = ctx.polygon.exterior
    # the boundary point nearest the road (or the longest boundary side's middle without a road)
    start = nearest_points(boundary, ctx.road)[0] if ctx.road is not None else boundary.interpolate(0.5, normalized=True)
    target_b = min(ctx.buildings, key=lambda b: b.distance(start))
    end = nearest_points(target_b, start)[0]
    length = start.distance(end)
    if length < float(g["min_length_m"]):
        return Polygon()
    mid_t = 0.35 + 0.3 * rng.random()
    nx, ny = -(end.y - start.y) / length, (end.x - start.x) / length
    off = (rng.random() * 2 - 1) * float(g["wobble_m"])
    mid = (
        start.x + (end.x - start.x) * mid_t + nx * off,
        start.y + (end.y - start.y) * mid_t + ny * off,
    )
    pts = [(start.x, start.y), mid, (end.x, end.y)]
    # a smooth curve through the three points (two quadratic Bezier halves), then a varying width
    curve = []
    for t in np.linspace(0, 1, 18):
        a = (1 - t) ** 2, 2 * (1 - t) * t, t**2
        curve.append((sum(w * p[0] for w, p in zip(a, pts, strict=True)), sum(w * p[1] for w, p in zip(a, pts, strict=True))))
    w0 = _rad(rng, g["width_m"])
    strips = [
        Point(x, y).buffer(w0 / 2 * (0.85 + 0.3 * rng.random()), 8) for x, y in curve
    ]
    path = unary_union([LineString(curve).buffer(w0 / 2 * 0.8, cap_style="flat"), *strips])
    return path.intersection(free)


def _beds(
    ctx: ParcelContext, free: BaseGeometry, blocked: BaseGeometry, cfg: dict, rng: random.Random
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
    allowed = allowed.difference(unary_union([f.buffer(float(b["fence_distance_m"])) for f in ctx.fences]) if ctx.fences else Polygon())
    allowed = allowed.difference(blocked)
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

    # layers, each clipped to the free yard
    gravel = _gravel_path(ctx, free, cfg, seeded(f"{pid}:detail:gravel"))
    beds = _beds(ctx, free, gravel, cfg, seeded(f"{pid}:detail:beds"))
    bed_geom = unary_union([r for r, _, _ in beds]) if beds else Polygon()
    rim = unary_union([r.buffer(float(cfg["beds"]["rim_m"])) for r, _, _ in beds]) if beds else Polygon()

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
            dirt_parts.append(blob(rng, p.x, p.y, _rad(rng, d["wall_radius_m"]), 0.35, 1.4))
    for _ in range(round(float(d["open_per_1000m2"]) * ctx.polygon.area / 1000)):
        x0, y0, x1, y1 = free.bounds
        for _try in range(30):
            x, y = x0 + rng.random() * (x1 - x0), y0 + rng.random() * (y1 - y0)
            if shapely.contains_xy(free, x, y):
                dirt_parts.append(blob(rng, x, y, _rad(rng, d["open_radius_m"]), 0.4, 1.3))
                break
    dirt = unary_union(dirt_parts) if dirt_parts else Polygon()

    lit = cfg["litter"]
    rng = seeded(f"{pid}:detail:litter")
    litter_parts = []
    for x, y, s in ctx.trees:
        r = canopy * s * float(lit["radius_factor"])
        for _ in range(int(lit["blobs_per_tree"])):
            a, dist = rng.random() * math.tau, rng.random() * r * 0.5
            litter_parts.append(blob(rng, x + dist * math.cos(a), y + dist * math.sin(a), r * (0.7 + 0.4 * rng.random()), 0.35, 1.2))
    litter = unary_union(litter_parts) if litter_parts else Polygon()

    lw = cfg["lawn"]
    rng = seeded(f"{pid}:detail:lawn")
    lawn_parts = [
        blob(rng, x, y, _rad(rng, lw["blob_radius_m"]), 0.3, 1.3)
        for x, y in poisson_disc(rng, ctx.polygon, float(lw["blob_spacing_m"]))
        if rng.random() < float(lw["keep"])
    ]
    lawn_all = (unary_union(lawn_parts) if lawn_parts else Polygon())
    rng = seeded(f"{pid}:detail:tones")
    tone_parts = [
        (i % 2, blob(rng, x, y, _rad(rng, lw["tone_radius_m"]), 0.4, 1.5))
        for i, (x, y) in enumerate(poisson_disc(rng, ctx.polygon, float(lw["tone_spacing_m"])))
        if rng.random() < float(lw["tone_keep"])
    ]

    # clip in priority order: each layer loses what an earlier one took
    taken: BaseGeometry = Polygon()
    layers: dict[str, BaseGeometry] = {}

    def take(layer: str, geom: BaseGeometry) -> BaseGeometry:
        nonlocal taken
        g = geom.intersection(free).difference(taken)
        layers[layer] = g
        taken = taken.union(g) if not g.is_empty else taken
        return g

    take("gravel", gravel)
    take("bed", bed_geom)
    take("dirt", dirt)
    take("litter", litter)
    lawn_free = lawn_all.intersection(free).difference(taken)
    tone_geoms = [(v, g.intersection(lawn_free)) for v, g in tone_parts]
    tones = [unary_union([g for v, g in tone_geoms if v == k]) if any(v == k for v, _ in tone_geoms) else Polygon() for k in (0, 1)]
    tone_taken: BaseGeometry = Polygon()
    for k, g in enumerate(tones):  # the two tones do not overlap either
        tones[k] = g.difference(tone_taken)
        tone_taken = tone_taken.union(tones[k])
    layers["lawn_tone"] = tone_taken
    layers["lawn"] = lawn_free.difference(tone_taken)

    colours = {
        "lawn": [hex_to_rgb(c) for c in lw["colours"]],
        "tone": [hex_to_rgb(c) for c in lw["tone_colours"]],
    }
    rng = seeded(f"{pid}:detail:colours")
    for layer in PATCH_PRIORITY:
        geom = layers.get(layer)
        if geom is None or geom.is_empty:
            continue
        if layer == "lawn":
            # one base colour per piece, picked by position so a piece keeps one tone
            for poly in _polys(geom):
                if poly.area > 0.05:
                    res.patches.append(Patch(layer, poly, colour=colours["lawn"][rng.randrange(3) % len(colours["lawn"])]))
        elif layer == "lawn_tone":
            for k, g in enumerate(tones):
                if not g.is_empty:
                    res.patches.append(Patch(layer, g, colour=colours["tone"][k % len(colours["tone"])]))
        elif layer == "bed":
            for rect, ang, cen in beds:
                g = geom.intersection(rect.buffer(0.01))
                if not g.is_empty:
                    res.patches.append(Patch(layer, g, texture="soil_rows", angle=ang, origin=cen))
        else:
            res.patches.append(Patch(layer, geom, texture=PATCH_TEXTURE[layer]))

    res.clutter = _clutter(ctx, cfg, layers, free, canopy)
    return res


# --- clutter --------------------------------------------------------------------------------


def _instance(rng: random.Random, kind: str, x: float, y: float, span: list[float], variants: int, terrain: Terrain) -> dict:
    return {
        "kind": kind,
        "variant": rng.randrange(variants),
        "x": round(x, 3),
        "y": round(y, 3),
        "z": round(float(terrain.surface(x, y)), 3),
        "rot": round(rng.random() * math.tau, 3),
        "scale": round(_rad(rng, span), 3),
    }


def _clutter(ctx: ParcelContext, cfg: dict, layers: dict[str, BaseGeometry], free: BaseGeometry, canopy: float) -> list[dict]:
    pid, c = ctx.id, cfg["clutter"]
    out: list[dict] = []
    grass = unary_union([layers.get("lawn", Polygon()), layers.get("lawn_tone", Polygon())])
    gravel, dirt, litter = layers.get("gravel", Polygon()), layers.get("dirt", Polygon()), layers.get("litter", Polygon())
    bed = layers.get("bed", Polygon())
    walls = unary_union(ctx.buildings) if ctx.buildings else None
    edges = unary_union([ctx.polygon.exterior, *ctx.fences]) if ctx.fences else ctx.polygon.exterior
    trees = unary_union([Point(x, y).buffer(canopy * s * 0.5) for x, y, s in ctx.trees]) if ctx.trees else None
    d_wall, d_edge, d_tree = distance_to(walls), distance_to(edges), distance_to(trees)
    # ground that is not a bed: clutter stays off the tilled soil
    open_ground = free.difference(bed) if not bed.is_empty else free

    def region_density(parts: list[tuple[BaseGeometry, float]]):
        """Density by place: the value of the first region a point is in (0 elsewhere)."""
        def fn(xs, ys):
            pts = shapely.points(xs, ys)
            out_d = np.zeros(len(xs))
            done = np.zeros(len(xs), bool)
            for geom, value in parts:
                if geom.is_empty:
                    continue
                hit = shapely.contains(geom, pts) & ~done
                out_d[hit] = value(xs[hit], ys[hit]) if callable(value) else value
                done |= hit
            return out_d
        return fn

    # tufts: lawn at a low density, thicker along fences and the parcel edge and by the walls
    t = c["tuft"]
    rng = seeded(f"{pid}:detail:tuft")
    edge_m = float(t["edge_m"])

    def tuft_lawn(xs, ys):
        near = np.minimum(d_edge(xs, ys), d_wall(xs, ys))
        return float(t["lawn"]) + (float(t["edge"]) - float(t["lawn"])) * np.clip(1 - near / edge_m, 0, 1)

    pts = thin(rng, poisson_disc(rng, open_ground, float(t["spacing_m"])), region_density([(grass, tuft_lawn), (dirt, 0.04), (open_ground, lambda xs, ys: tuft_lawn(xs, ys) * 0.5)]))
    out += [_instance(rng, "tuft", x, y, t["scale"], int(t["variants"]), ctx.terrain) for x, y in pts]

    # flowers: small clusters in the lawn
    f = c["flower"]
    rng = seeded(f"{pid}:detail:flower")
    for cx, cy in poisson_disc(rng, grass.buffer(-0.4), float(f["cluster_spacing_m"])):
        if rng.random() > float(f["cluster_keep"]):
            continue
        variant = rng.randrange(int(f["variants"]))
        for _ in range(rng.randint(*(int(v) for v in f["cluster_size"]))):
            a, r = rng.random() * math.tau, float(f["cluster_radius_m"]) * math.sqrt(rng.random())
            x, y = cx + r * math.cos(a), cy + r * math.sin(a)
            if shapely.contains_xy(grass, x, y):
                inst = _instance(rng, "flower", x, y, f["scale"], int(f["variants"]), ctx.terrain)
                inst["variant"] = variant if rng.random() < 0.8 else inst["variant"]
                out.append(inst)

    # pebbles: dense on gravel, some on bare soil and along walls
    p = c["pebble"]
    rng = seeded(f"{pid}:detail:pebble")
    wall_m = float(p["wall_m"])

    def pebble_wall(xs, ys):
        return float(p["wall"]) * np.clip(1 - d_wall(xs, ys) / wall_m, 0, 1)

    pts = thin(rng, poisson_disc(rng, open_ground, float(p["spacing_m"])), region_density([(gravel, float(p["gravel"])), (dirt, float(p["dirt"])), (open_ground, pebble_wall)]))
    out += [_instance(rng, "pebble", x, y, p["scale"], int(p["variants"]), ctx.terrain) for x, y in pts]

    # rocks: few, on bare soil and where lawn meets an edge
    r_ = c["rock"]
    rng = seeded(f"{pid}:detail:rock")
    edge_r = float(r_["edge_m"])

    def rock_grass(xs, ys):
        return float(r_["lawn"]) + (float(r_["edge"]) - float(r_["lawn"])) * np.clip(1 - d_edge(xs, ys) / edge_r, 0, 1)

    pts = thin(rng, poisson_disc(rng, open_ground, float(r_["spacing_m"])), region_density([(gravel, 0.0), (dirt, float(r_["dirt"])), (open_ground, rock_grass)]))
    out += [_instance(rng, "rock", x, y, r_["scale"], int(r_["variants"]), ctx.terrain) for x, y in pts]

    # leaves: thick in the litter, scattered on the lawn under a tree
    lf = c["leaf"]
    rng = seeded(f"{pid}:detail:leaf")
    reach = float(lf["tree_reach_m"])

    def leaf_tree(xs, ys):
        return float(lf["tree_lawn"]) * np.clip(1 - d_tree(xs, ys) / reach, 0, 1)

    pts = thin(rng, poisson_disc(rng, open_ground, float(lf["spacing_m"])), region_density([(litter, float(lf["litter"])), (open_ground, leaf_tree)]))
    out += [_instance(rng, "leaf", x, y, lf["scale"], int(lf["variants"]), ctx.terrain) for x, y in pts]
    return out


def run_parcel(
    parcel_id: str,
    work_dir: Path,
    rules_path: Path = DEFAULT_RULES,
    area: str | None = None,
) -> tuple[ParcelContext, DetailResult]:
    ctx = load_context(parcel_id, work_dir, rules_path, area)
    return ctx, garden_detail(ctx, load_section("detail", rules_path, area))


# --- meshes ---------------------------------------------------------------------------------


def patch_meshes(res: DetailResult, terrain: Terrain, lift_m: float) -> tuple[Mesh, dict[str, TexturedMesh]]:
    """The patches as ground triangles: one flat-colour mesh and one textured mesh per texture id.

    Textured UVs are world coordinates in tile repeats (rotated by the patch's angle), so a texture
    runs on across neighbouring patches and parcels.
    """
    flat: list[Mesh] = []
    textured: dict[str, list[TexturedMesh]] = {}
    for p in res.patches:
        if p.texture is None:
            m = drape(p.geom, p.colour or [128, 128, 128], terrain, [], lift_m, lift_m)
            if m.face_count:
                flat.append(m)
            continue
        m = drape(p.geom, [255, 255, 255], terrain, [], lift_m, lift_m)
        if not m.face_count:
            continue
        tex = texture(p.texture)
        c, s = math.cos(-p.angle), math.sin(-p.angle)
        dx, dy = m.tris[:, :, 0] - p.origin[0], m.tris[:, :, 1] - p.origin[1]
        u, v = (dx * c - dy * s) / tex.tile_w, (dx * s + dy * c) / tex.tile_h
        textured.setdefault(p.texture, []).append(TexturedMesh(m.tris, m.colours, np.stack([u, v], axis=2)))
    return Mesh.concat(flat), {tid: TexturedMesh.concat(ms) for tid, ms in sorted(textured.items())}
