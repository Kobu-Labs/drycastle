"""The crop recipe (ground types ``crop_grain``, ``crop_corn``, ``crop_potato``, ``crop_rapeseed``, ``crop_bare``; issue #328).

A crop is a ground *type* (terrain only, ADR 0022): a hand-drawn ground patch of one of these types is a crop field. The
recipe turns the patch into

* **ground patches**: flat tones (or a texture turned along the rows) draped on the terrain. The edge is a wide border zone
  (``edge.feather_m``, 6 to 10 m): ``steps`` bands (8 or more) from the colour of the ground that *really* lies outside the
  patch (:attr:`ParcelContext.ground_around`: other ground, road, water; ``surround`` where nothing is known) to the field's own
  colour, each band edge wandering with value noise so the bands interleave. A patch row has ONE flat colour per triangle
  (no vertex colours), so the gradient is made of many small bands, each cut by the neighbour colour of its stretch of the
  edge (Voronoi cells of a probe every ``sample_m`` along the edge, colours averaged over a few probes so two neighbours blend
  along the edge). A dirt headland of blobs, kept in part, runs along the edge.
* **baulks**: where two crop patches adjoin, a strip of grass (or dirt), 0.5 to 1 m wide, noise-edged. Both patches take their
  half from the same position-based width, so the two halves meet. Their grounds blend towards each other over ``baulk.blend_m``.
* **per-patch variety** (everything seeded by the patch id): ground tone, plant height, row direction; tramlines (pairs of
  tractor tracks) and dirt strips along edges; a stubble stripe with bales in a line; weedy fallow (no crop plants); wet,
  bare and weedy spots; a pick of the crop's artifact kinds plus a chance of a tractor, a sprinkler line or a hay stack.
* **instances** (:attr:`DetailResult.crops`, the ``crops.json`` of the map): the plants (rows or a jittered grid, thinning out
  toward the edge over the same zone with a ragged edge, stopping dead at a wall) and the artifacts. Rows
  ``[x, y, z, rot, scale]`` per chunk and kind, like the clutter, but a separate file and no tint.

Cosmetic only: nothing here collides or blocks a bullet; colour and height jitter, no growth stages, no seasons. A field nobody
painted keeps the ``field`` recipe. Everything is seeded per unit (``sha256("<id>:detail:<purpose>")``), so reruns give
identical output.
"""

import math
from dataclasses import dataclass

import numpy as np
import shapely
import shapely.affinity
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.geometry.polygon import orient
from shapely.ops import nearest_points

from drycastle_pipeline import detail as D
from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meadow import value_noise
from drycastle_pipeline.props import seeded

#: The instance kinds of the recipe (``detail.crop_kinds`` in rules.yaml says what each is drawn with).
PLANT_KINDS = ("crop_wheat", "crop_corn", "crop_potato", "crop_rapeseed")
ARTIFACT_KINDS = (
    "art_hay_bale_round", "art_hay_bale_square", "art_hay_pile", "art_baler", "art_scarecrow", "art_tractor",
    "art_potato_box", "art_sign_potatoes", "art_grain_bag", "art_sprinkler", "art_tyre", "art_plough", "art_wood_stack",
)
CROP_KINDS = (*PLANT_KINDS, *ARTIFACT_KINDS)
CROP_FORMAT_VERSION = 1

_NOISE_SALT = 7100


def _rgb(hx: str) -> list[int]:
    return hex_to_rgb(hx)


def _mix(a, b, w: float) -> list[int]:
    return [round(x * (1 - w) + y * w) for x, y in zip(a, b, strict=True)]


# --- geometry helpers -----------------------------------------------------------------------


def _row_frame(poly: Polygon) -> tuple[float, tuple[float, float]]:
    """The direction of the long side of the minimum rotated rectangle (radians) and its centre."""
    mrr = poly.minimum_rotated_rectangle
    ring = list(mrr.exterior.coords)
    a, b = max(zip(ring, ring[1:], strict=False), key=lambda e: math.dist(*e))
    return math.atan2(b[1] - a[1], b[0] - a[0]), (mrr.centroid.x, mrr.centroid.y)


def _wobble(poly: Polygon, amp: float, lattice: float, salt: int, vertex_m: float = 1.0) -> BaseGeometry:
    """The exterior of ``poly`` with every vertex (``vertex_m`` apart) pushed along its normal by smooth noise in +-``amp``."""
    poly = orient(poly, 1.0)
    xy = np.asarray(shapely.segmentize(poly.exterior, vertex_m).coords)[:-1]
    if len(xy) < 4:
        return poly
    t = np.roll(xy, -1, axis=0) - np.roll(xy, 1, axis=0)
    ln = np.maximum(np.hypot(t[:, 0], t[:, 1]), 1e-9)
    normal = np.stack([-t[:, 1] / ln, t[:, 0] / ln], axis=1)  # left of a counter-clockwise ring: inward
    off = amp * (value_noise(xy[:, 0], xy[:, 1], lattice, salt) * 2 - 1)
    out = Polygon(xy + normal * off[:, None], [r.coords for r in poly.interiors]).buffer(0)
    return out if not out.is_empty else poly


def _inset(geom: BaseGeometry, d: float, amp: float, lattice: float, salt: int, vertex_m: float = 1.0) -> BaseGeometry:
    """``geom`` shrunk by ``d`` with a wandering edge."""
    inner = geom.buffer(-d, join_style="round") if d > 0 else geom
    if inner.is_empty or amp <= 0:
        return inner
    return D._union([_wobble(p, amp, lattice, salt, vertex_m) for p in D._polys(inner)])


def _smooth(x: np.ndarray) -> np.ndarray:
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


def _local(g: BaseGeometry, angle: float, origin: tuple[float, float]) -> BaseGeometry:
    return shapely.affinity.rotate(g, -angle, origin=origin, use_radians=True)


def _world(g: BaseGeometry, angle: float, origin: tuple[float, float]) -> BaseGeometry:
    return shapely.affinity.rotate(g, angle, origin=origin, use_radians=True)


# --- the plan of one patch (everything random about it, drawn in a fixed order) ---------------


@dataclass
class CropPlan:
    """What is special about this patch: seeded by its id, drawn in a fixed order whatever the config says."""

    fallow: bool
    stubble: bool
    angle: float  # direction of the rows (radians)
    tone: tuple[float, float, float, float]  # ground colour factor, then a per-channel drift
    plant_mult: float
    tram: bool
    tram_spacing: float
    tram_phase: float
    strips: int  # dirt strips along this many edges
    strip_w: float
    strip_seed: int
    stub_side: int
    stub_cover: float
    stub_seed: int
    round_bales: bool

    def shade(self, c) -> list[int]:
        k, dr, dg, db = self.tone
        return [max(0, min(255, round(v * (k + d)))) for v, d in zip(c, (dr, dg, db), strict=True)]


def crop_plan(ctx: D.ParcelContext, cc: dict, long_angle: float | None = None) -> CropPlan:
    """The patch's own look. Public so tests (and tools) can ask which patches are fallow, striped, ..."""
    rng = seeded(f"{ctx.id}:detail:crop:plan")
    vc, tc, sc, fc = cc["variety"], cc["tramlines"], cc["stubble"], cc["fallow"]
    area = ctx.polygon.area
    long_angle = _row_frame(ctx.polygon)[0] if long_angle is None else long_angle
    fallow = rng.random() < float(fc["probability"])
    stub_roll = rng.random()
    stubble = stub_roll < float(sc["probability"]) and area >= float(sc["min_area_m2"]) and not fallow
    ra = vc["row_angle"]
    u = rng.random()
    angle = long_angle + (math.pi / 2 if u < float(ra["cross_share"]) else 0.0)
    free_angle = rng.random() * math.pi
    if float(ra["cross_share"]) <= u < float(ra["cross_share"]) + float(ra["free_share"]):
        angle = free_angle
    angle += math.radians(rng.uniform(-1, 1) * float(ra["jitter_deg"]))
    ts = float(vc["tone_shift"])
    tone = (1 + rng.uniform(-ts, ts), rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02))
    lo, hi = vc["plant_scale"]
    plant_mult = rng.uniform(float(lo), float(hi))
    spacing = rng.uniform(*(float(x) for x in tc["spacing_m"]))
    phase = rng.random()
    es = tc["edge_strip"]
    n_strips = rng.randint(int(es["edges"][0]), int(es["edges"][1])) if rng.random() < float(es["probability"]) else 0
    strip_w = rng.uniform(*(float(x) for x in es["width_m"]))
    strip_seed = rng.getrandbits(32)
    side = 1 if rng.random() < 0.5 else -1
    cover = rng.uniform(*(float(x) for x in sc["coverage"]))
    stub_seed = rng.randrange(1000)
    round_bales = rng.random() < float(sc["bales"]["round_share"])
    tram = bool(tc["enabled"]) and area >= float(tc["min_area_m2"]) and not fallow
    return CropPlan(fallow, stubble, angle, tone, plant_mult, tram, spacing, phase, n_strips if tram else 0, strip_w, strip_seed,
                    side, cover, stub_seed, round_bales)


# --- baulk, headland, ground ----------------------------------------------------------------


def _baulk(ctx: D.ParcelContext, bc: dict) -> tuple[BaseGeometry, BaseGeometry, bool]:
    """``(grass, dirt, any)``: this patch's half of the baulks along the edges it shares with other crop patches.

    The width and wandering of the strip are a function of the position (value noise), the dirt-or-grass choice is seeded by
    the pair, so the neighbour computes the same strip and its own half meets this one."""
    grass: list[BaseGeometry] = []
    dirt: list[BaseGeometry] = []
    lo, hi = (float(w) / 2 for w in bc["width_m"])
    tol = float(bc["shared_tol_m"])
    for nid, geom in ctx.crop_neighbours or []:
        line = ctx.polygon.boundary.intersection(geom.buffer(tol))
        lines = D._lines(line)
        if sum(ln.length for ln in lines) < float(bc["min_shared_m"]):
            continue
        a, b = sorted((ctx.id.split(".")[0], nid))
        is_dirt = seeded(f"{a}:{b}:detail:baulk").random() < float(bc["dirt_share"])
        step = float(bc["step_m"])
        pts: list[tuple[float, float]] = []
        for ln in lines:
            n = max(2, int(ln.length / step) + 1)
            pts += [(p.x, p.y) for p in (ln.interpolate(i / (n - 1), normalized=True) for i in range(n))]
        xy = np.array(pts)
        # radius: a slow swell and a fast wobble, both functions of the position only
        k = np.clip(0.65 * value_noise(xy[:, 0], xy[:, 1], 4.0, _NOISE_SALT) + 0.35 * value_noise(xy[:, 0], xy[:, 1], 1.2, _NOISE_SALT + 1), 0, 1)
        r = lo + (hi - lo) * k
        strip = shapely.union_all(shapely.buffer(shapely.points(xy), r, quad_segs=3)).intersection(ctx.polygon)
        (dirt if is_dirt else grass).append(strip)
    g, d = D._union(grass), D._union(dirt)
    return g, d, bool(grass or dirt)


def _headland(ctx: D.ParcelContext, free: BaseGeometry, hc: dict, near_nb: BaseGeometry) -> BaseGeometry:
    rng = seeded(f"{ctx.id}:detail:crop:headland")
    blobs = []
    for poly in D._polys(free):
        for p, ang in D._along(rng, poly.exterior, float(hc["spacing_m"]), float(hc["keep"])):
            if not near_nb.is_empty and near_nb.contains(p):
                continue
            blobs.append(D.blob(rng, p.x, p.y, D._rad(rng, hc["radius_m"]), 0.4, float(hc["stretch"]), int(hc["vertices"]), ang))
    return D._union(blobs)


# --- what lies beside the edge --------------------------------------------------------------


def _edge_samples(free: BaseGeometry, step: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Points every ``step`` metres along every ring of ``free``, the outward normal there, and the ring each belongs to."""
    pts: list[tuple[float, float]] = []
    nrm: list[tuple[float, float]] = []
    rid: list[int] = []
    r = 0
    for poly in D._polys(free):
        poly = orient(poly, 1.0)  # inside on the left of every ring, so the right is out (of the polygon)
        for ring in (poly.exterior, *poly.interiors):
            length = ring.length
            if length < 1.0:
                continue
            n = max(3, round(length / step))
            for i in range(n):
                s = length * i / n
                a, b, p = ring.interpolate((s - 0.5) % length), ring.interpolate((s + 0.5) % length), ring.interpolate(s)
                dx, dy = b.x - a.x, b.y - a.y
                ln = math.hypot(dx, dy) or 1.0
                pts.append((p.x, p.y))
                nrm.append((dy / ln, -dx / ln))
                rid.append(r)
            r += 1
    return np.array(pts).reshape(-1, 2), np.array(nrm).reshape(-1, 2), np.array(rid, dtype=int)


def neighbour_colours(ctx: D.ParcelContext, cfg: dict, pts: np.ndarray, nrm: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """What lies just outside the edge at each point: ``(colour (n, 3) float, kind (n,))``, kind one of ``self`` (the cut of a
    building, fence or object: the same field goes on, no blend), ``road``, ``crop`` (another crop patch), ``ground`` (any other
    ground unit, its terrain colour), ``water``, ``none`` (the ``surround`` colour)."""
    ec = cfg["crop"]["edge"]
    nc = ec["neighbour"]
    n = len(pts)
    q = pts + nrm * float(nc["probe_m"])
    col = np.tile(np.array(_rgb(cfg["crop"]["surround"]), dtype=float), (n, 1))
    kind = np.full(n, "none", dtype=object)
    todo = np.ones(n, dtype=bool)

    def take(mask: np.ndarray, c, k: str) -> None:
        m = mask & todo
        if m.any():
            col[m] = np.array(c, dtype=float)
            kind[m] = k
            todo[m] = False

    if ctx.road is not None and not ctx.road.is_empty:
        zone = ctx.road.buffer(float(cfg["road_clearance_m"]) + float(nc["probe_m"]) + 0.5)
        take(shapely.contains_xy(zone, q[:, 0], q[:, 1]), _rgb(nc["road"]), "road")
    for _pid, geom, colour, is_crop in ctx.ground_around or []:
        if todo.any():
            take(shapely.contains_xy(geom, q[:, 0], q[:, 1]), colour, "crop" if is_crop else "ground")
    if ctx.water is not None and not ctx.water.is_empty and todo.any():
        take(shapely.contains_xy(ctx.water, q[:, 0], q[:, 1]), _rgb(nc["water"]), "water")
    take(shapely.contains_xy(ctx.polygon, q[:, 0], q[:, 1]), [0, 0, 0], "self")
    return col, kind


def _neighbour_groups(ctx: D.ParcelContext, cfg: dict, free: BaseGeometry) -> list[tuple[list[int], bool, BaseGeometry]]:
    """The neighbour colours along the edge as ``(colour, is_crop, zone)``: ``zone`` is the ground nearer to the stretches of
    the edge with that colour (Voronoi cells of the probes) than to any other group's. Stretches beside the field's own
    ground (``self``) give no group."""
    nc = cfg["crop"]["edge"]["neighbour"]
    pts, nrm, rid = _edge_samples(free, float(nc["sample_m"]))
    if not len(pts):
        return []
    col, kind = neighbour_colours(ctx, cfg, pts, nrm)
    k = int(nc["smooth"])
    sm = col.copy()
    for r in np.unique(rid):  # a moving average round each ring: the neighbours blend along the edge
        idx = np.flatnonzero(rid == r)
        if len(idx) > 2 * k + 1 and k > 0:
            for j, i in enumerate(idx):
                win = idx[[(j + o) % len(idx) for o in range(-k, k + 1)]]
                ok = kind[win] != "self"
                if ok.any() and kind[i] != "self":
                    sm[i] = col[win][ok].mean(axis=0)
    step = float(nc["colour_step"])
    keys = [("self",) if kd == "self" else (kd == "crop", *(int(c // step) for c in c3)) for kd, c3 in zip(kind, sm, strict=True)]
    extent = free.buffer(max(20.0, float(cfg["crop"]["edge"]["feather_m"]) * 2)).envelope
    if len(pts) >= 3 and len({tuple(p) for p in np.round(pts, 3)}) == len(pts):
        vor = shapely.voronoi_polygons(shapely.multipoints(pts), extend_to=extent)
        tree = shapely.STRtree(shapely.points(pts))
        cell = [Polygon()] * len(pts)
        for g in vor.geoms:
            hit = tree.query(g, predicate="contains")
            if len(hit):
                cell[int(hit[0])] = g
    else:
        cell = [extent] * len(pts)
    out = []
    for key in dict.fromkeys(keys):
        if key == ("self",):
            continue
        idx = [i for i, kk in enumerate(keys) if kk == key]
        try:
            zone = shapely.union_all([cell[i] for i in idx])
        except shapely.errors.GEOSException:  # a cell that is not valid on real data: repair each, then union
            zone = shapely.union_all([shapely.make_valid(cell[i]).buffer(0) for i in idx])
        out.append(([round(float(v)) for v in sm[idx].mean(axis=0)], bool(key[0]), zone))
    return out


def _edge_bands(free: BaseGeometry, ec: dict, groups: list, inner: list[tuple[BaseGeometry, list[int]]], base: list[int],
                reach_crop: float) -> list[tuple[str, BaseGeometry, dict]]:
    """The border zone: ``steps`` bands, band ``k`` the strip of depth (k-1, k] x feather/steps with a wandering edge,
    cut by the neighbour groups; the colour is the neighbour's mixed towards the field's (``inner`` regions, else ``base``)
    by the depth over the group's reach. A piece that has already reached the field's colour is left to the core."""
    feather, steps = float(ec["feather_m"]), int(ec["steps"])
    wob, lat, vm = float(ec["wobble_m"]), float(ec["wobble_lattice_m"]), float(ec["vertex_m"])
    out: list[tuple[str, BaseGeometry, dict]] = []
    prev = free
    for k in range(1, steps + 1):
        # simplified here, not later: bands and pieces then share their edges exactly (disjoint without a difference each)
        cur = _inset(free, feather * k / steps, wob, lat, _NOISE_SALT + 20 + k, vm).intersection(prev).simplify(0.15, preserve_topology=True).intersection(prev)
        band = prev.difference(cur)
        prev = cur
        if band.is_empty:
            continue
        depth = feather * (k - 0.5) / steps
        for nb, is_crop, zone in groups:
            piece = band.intersection(zone)
            if piece.is_empty:
                continue
            reach = reach_crop if is_crop else feather
            w = float(_smooth(np.array(depth / max(reach, 1e-6))))
            rest = piece
            parts: list[tuple[BaseGeometry, list[int]]] = []
            for reg, c in inner:
                hit = piece.intersection(reg)
                if not hit.is_empty:
                    parts.append((hit, c))
                    rest = rest.difference(reg)
            parts.append((rest, base))
            for g, inn in parts:
                col = _mix(nb, inn, w)
                if g.is_empty or w >= 0.97 or max(abs(a - b) for a, b in zip(col, inn, strict=True)) <= 2:
                    continue
                out.append((f"crop_edge_{k}", g, {"colour": col, "tol": 0.0}))
    return out


# --- tramlines, stubble, spots --------------------------------------------------------------


def _tramlines(free: BaseGeometry, plan: CropPlan, tc: dict, origin: tuple[float, float]) -> BaseGeometry:
    """Pairs of tractor tracks along the rows, a pair every ``plan.tram_spacing`` m, stopping ``margin_m`` short of the edge."""
    local = _local(free, plan.angle, origin)
    x0, y0, x1, y1 = local.bounds
    w, gap, sp = float(tc["track_width_m"]), float(tc["track_gap_m"]), plan.tram_spacing
    strips = []
    c = y0 + sp * (0.25 + 0.5 * plan.tram_phase)
    while c < y1:
        for s in (-1, 1):
            yc = c + s * gap / 2
            strips.append(shapely.box(x0 - 1, yc - w / 2, x1 + 1, yc + w / 2))
        c += sp
    if not strips:
        return Polygon()
    inner = free.buffer(-float(tc["margin_m"]), join_style="mitre")
    return D._clean(_world(shapely.union_all(strips), plan.angle, origin).intersection(inner), 0.05) if not inner.is_empty else Polygon()


def _edge_strips(free: BaseGeometry, plan: CropPlan, tc: dict) -> BaseGeometry:
    """Dirt strips along ``plan.strips`` of the longer edges of the patch."""
    if not plan.strips or free.is_empty:
        return Polygon()
    rng = seeded(f"{plan.strip_seed}:crop:strips")
    big = max(D._polys(free), key=lambda p: p.area)
    ring = list(orient(big, 1.0).exterior.coords)
    segs = [LineString([a, b]) for a, b in zip(ring, ring[1:], strict=False) if math.dist(a, b) >= float(tc["edge_strip"]["min_edge_m"])]
    rng.shuffle(segs)
    strips = [_wobble(p, float(tc["edge_strip"]["wobble_m"]), 2.0, _NOISE_SALT + 40 + i)
              for i, seg in enumerate(segs[: plan.strips])
              for p in D._polys(seg.buffer(plan.strip_w, single_sided=True, cap_style="flat"))]  # single-sided: the left, i.e. inside
    return D._clean(D._union(strips).intersection(free), 0.1) if strips else Polygon()


def _stubble(free: BaseGeometry, plan: CropPlan, sc: dict, origin: tuple[float, float]) -> tuple[BaseGeometry, float, tuple[float, float, float, float]]:
    """A stripe along the rows from one side of the patch covering ``plan.stub_cover`` of its area, edge from noise.
    Returns the stripe, the local y of its cut, and the local bounds."""
    local = _local(free, plan.angle, origin)
    x0, y0, x1, y1 = local.bounds
    xs = np.arange(x0 - 2, x1 + 2.01, 1.0)
    wob = float(sc["wobble_m"]) * (value_noise(xs, np.full_like(xs, float(plan.stub_seed)), 6.0, _NOISE_SALT + 50) * 2 - 1)
    side = plan.stub_side

    def region(yc: float) -> BaseGeometry:
        edge = list(zip(xs, yc + wob, strict=True))
        far = y1 + 5 if side > 0 else y0 - 5
        return Polygon([*edge, (xs[-1], far), (xs[0], far)]).buffer(0).intersection(local)

    lo, hi = y0, y1
    for _ in range(16):
        mid = (lo + hi) / 2
        share = region(mid).area / max(local.area, 1e-9)
        if (share > plan.stub_cover) == (side > 0):
            lo = mid
        else:
            hi = mid
    yc = (lo + hi) / 2
    return D._clean(_world(region(yc), plan.angle, origin).intersection(free), 0.1), yc, (x0, y0, x1, y1)


def _stubble_lanes(stubble: BaseGeometry, plan: CropPlan, sc: dict, origin: tuple[float, float]) -> BaseGeometry:
    """Darker lines along the stubble every ``row_spacing_m``."""
    local = _local(stubble, plan.angle, origin)
    x0, y0, x1, y1 = local.bounds
    sp, w = float(sc["row_spacing_m"]), float(sc["row_width_m"])
    ys = np.arange(y0 + sp / 2, y1, sp)
    if not len(ys):
        return Polygon()
    lanes = shapely.union_all([shapely.box(x0 - 1, y - w / 2, x1 + 1, y + w / 2) for y in ys])
    return D._clean(_world(lanes.intersection(local), plan.angle, origin).intersection(stubble), 0.05)


def _bales(ctx: D.ParcelContext, stubble: BaseGeometry, plan: CropPlan, sc: dict, yc: float, bounds: tuple, origin: tuple[float, float]) -> list[dict]:
    """Bales lined along the rows on the stubble: ``lines`` rows a few metres in from the stripe's cut."""
    bc = sc["bales"]
    kind = "art_hay_bale_round" if plan.round_bales else "art_hay_bale_square"
    spec = bc["round"] if plan.round_bales else bc["square"]
    rng = seeded(f"{ctx.id}:detail:crop:bales")
    safe = stubble.buffer(-float(spec["radius_m"]) - 0.3)
    if safe.is_empty:
        return []
    shapely.prepare(safe)
    x0, _, x1, _ = bounds
    sp = float(spec["spacing_m"])
    out: list[dict] = []
    c, s = math.cos(plan.angle), math.sin(plan.angle)
    for j in range(rng.randint(int(bc["lines"][0]), int(bc["lines"][1]))):
        y = yc + plan.stub_side * (float(spec["radius_m"]) + 1.2 + j * (sp + 1.0))
        x = x0 + rng.random() * sp
        while x < x1:
            lx, ly = x + (rng.random() - 0.5) * 0.4 * sp, y + (rng.random() - 0.5) * 0.5
            wx, wy = origin[0] + (lx - origin[0]) * c - (ly - origin[1]) * s, origin[1] + (lx - origin[0]) * s + (ly - origin[1]) * c
            if shapely.contains_xy(safe, wx, wy) and len(out) < int(bc["max"]):
                rot = rng.random() * math.tau if plan.round_bales else plan.angle + (rng.random() - 0.5) * 0.2
                out += _emit_one(ctx, kind, wx, wy, rot, D._rad(rng, spec["scale"]))
            x += sp
    return out


def _spots(free: BaseGeometry, area: float, pid: str, sc: dict) -> dict[str, BaseGeometry]:
    """Wet patches, bare soil and weed clumps where a position-based noise is high (so they cluster), seeded by the patch."""
    out: dict[str, BaseGeometry] = {}
    if free.is_empty:
        return out
    rng = seeded(f"{pid}:detail:crop:spots")
    g = D._gen(pid, "crop:spots")
    inner = free.buffer(-1.0)
    inner = inner if not inner.is_empty else free
    cand = D.jitter_grid(g, inner, 5.0)
    if not len(cand):
        return out
    for i, name in enumerate(("wet", "bare", "weeds")):
        spec = sc[name]
        want = float(spec["per_ha"]) * area / 1e4
        n = int(want) + (1 if rng.random() < want - int(want) else 0)
        if n <= 0:
            continue
        score = value_noise(cand[:, 0], cand[:, 1], float(sc["lattice_m"]), _NOISE_SALT + 60 + i) + 0.25 * g.random(len(cand))
        blobs = [D.blob(rng, x, y, D._rad(rng, spec["radius_m"]), 0.4, 1.5, 9) for x, y in cand[np.argsort(-score)[:n]]]
        geom = D._clean(D._union(blobs).intersection(free), 0.1)
        if not geom.is_empty:
            out[name] = geom
    return out


def _flowers(free: BaseGeometry, pid: str, fc: dict) -> list[tuple[str, BaseGeometry, dict]]:
    """Wildflower-like dabs of colour on a fallow patch, in clumps; one layer per colour."""
    spec = fc["flowers"]
    per_ha = float(spec["per_ha"])
    inner = free.buffer(-0.6)
    if per_ha <= 0 or inner.is_empty:
        return []
    rng = seeded(f"{pid}:detail:crop:flowers")
    g = D._gen(pid, "crop:flowers")
    pts = D.jitter_grid(g, inner, math.sqrt(1e4 / per_ha / 2.5))
    if not len(pts):
        return []
    nz = value_noise(pts[:, 0], pts[:, 1], 7.0, _NOISE_SALT + 70)
    pts = pts[g.random(len(pts)) < np.clip(2.5 * nz * nz, 0.0, 1.0)]
    cols = [_rgb(c) for c in spec["colours"]]
    by: dict[int, list[BaseGeometry]] = {}
    for x, y in pts:
        by.setdefault(rng.randrange(len(cols)), []).append(D.blob(rng, x, y, D._rad(rng, spec["radius_m"]), 0.3, 1.0, 7))
    return [(f"crop_flower_{i}", D._union(b), {"colour": cols[i], "tol": 0.03}) for i, b in sorted(by.items())]


# --- plants ---------------------------------------------------------------------------------


def _grid(g: np.random.Generator, geom: BaseGeometry, spec: dict, angle: float, origin: tuple[float, float]) -> np.ndarray:
    """Candidate plant points: rows ``row_m`` apart along ``angle`` with a plant every ``spacing_m``, or a jittered grid."""
    step, row = float(spec["spacing_m"]), float(spec.get("row_m", 0.0))
    if geom.is_empty:
        return np.zeros((0, 2))
    if row <= 0:
        return D.jitter_grid(g, geom, step, 0.9)
    local = _local(geom, angle, origin)
    x0, y0, x1, y1 = local.bounds
    xs = np.arange(x0, x1 + step, step)
    ys = np.arange(y0, y1 + row, row)
    gx, gy = np.meshgrid(xs, ys)
    gx = gx + (g.random(gx.shape) - 0.5) * step * 0.3
    gy = gy + (g.random(gy.shape) - 0.5) * row * 0.08
    pts = np.stack([gx.ravel(), gy.ravel()], axis=1)
    shapely.prepare(local)
    pts = pts[shapely.contains_xy(local, pts[:, 0], pts[:, 1])]
    c, s = math.cos(angle), math.sin(angle)
    dx, dy = pts[:, 0] - origin[0], pts[:, 1] - origin[1]
    return np.stack([origin[0] + dx * c - dy * s, origin[1] + dx * s + dy * c], axis=1)


def plant_density(d: np.ndarray, edge: dict) -> np.ndarray:
    """The share of plants kept at distance ``d`` from the edge: 0 within the margin; then it rises over ``plant_falloff_m``
    (feather) or jumps to 1 (falloff 0: the wall)."""
    margin, fall = float(edge["plant_margin_m"]), float(edge["plant_falloff_m"])
    if fall <= 0:
        return (d >= margin).astype(float)
    return _smooth((d - margin) / fall)


def _plants(ctx: D.ParcelContext, ec: dict, plants: list[dict], planted: BaseGeometry, edge_line: BaseGeometry, plan: CropPlan,
            origin: tuple[float, float], keep_off: list[tuple[float, float, float]], bare: BaseGeometry,
            weeds: BaseGeometry, weed_scale: float) -> list[dict]:
    out: list[dict] = []
    edge_d = D.distance_to(edge_line)
    edge = ec["edge"]
    jit, jlat = float(edge["plant_jitter_m"]), float(edge["plant_jitter_lattice_m"])
    for spec in plants:
        kind = spec["kind"]
        g = D._gen(ctx.id, f"crop:{kind}")
        pts = _grid(g, planted, spec, plan.angle, origin)
        if not len(pts):
            continue
        d = edge_d(pts[:, 0], pts[:, 1])
        if jit > 0:  # a ragged thinning: the distance to the edge wanders
            d = d + jit * (value_noise(pts[:, 0], pts[:, 1], jlat, _NOISE_SALT + 8) * 2 - 1)
        p = plant_density(d, edge) * float(spec["density"])
        amp = float(spec.get("noise_amp", 0.0))
        if amp > 0:  # patches of thinner growth
            p = p * (1 - amp * (1 - value_noise(pts[:, 0], pts[:, 1], 5.0, _NOISE_SALT + 7)))
        if edge["mode"] == "wall":  # a hard edge, only a hair of jitter
            p = np.where(d + (g.random(len(pts)) - 0.5) * 0.2 >= float(edge["plant_margin_m"]), p, 0.0)
        pts = pts[g.random(len(pts)) < p]
        if len(pts) and not bare.is_empty:
            pts = pts[~shapely.contains_xy(bare, pts[:, 0], pts[:, 1])]
        for x, y, r in keep_off:
            if len(pts):
                pts = pts[np.hypot(pts[:, 0] - x, pts[:, 1] - y) >= r]
        mult = np.full(len(pts), plan.plant_mult)
        if len(pts) and not weeds.is_empty:
            mult = np.where(shapely.contains_xy(weeds, pts[:, 0], pts[:, 1]), mult * weed_scale, mult)
        out += _emit(g, kind, pts, spec["scale"], ctx, mult)
    return out


def _emit(g: np.random.Generator, kind: str, pts: np.ndarray, span: list[float], ctx: D.ParcelContext, mult: np.ndarray) -> list[dict]:
    if not len(pts):
        return []
    ok = D.terrain_ok(ctx.terrain, pts[:, 0], pts[:, 1])
    rot = g.random(len(pts)) * math.tau  # drawn for every point, so the rest stays put when one falls off the terrain
    sc = (span[0] + g.random(len(pts)) * (span[1] - span[0])) * mult
    pts, rot, sc = pts[ok], rot[ok], sc[ok]
    if not len(pts):
        return []
    z = ctx.terrain.surface(pts[:, 0], pts[:, 1])
    return [{"kind": kind, "x": round(float(x), 3), "y": round(float(y), 3), "z": round(float(h), 3),
             "rot": round(float(r), 3), "scale": round(float(s), 3)} for (x, y), h, r, s in zip(pts, z, rot, sc, strict=True)]


# --- artifacts ------------------------------------------------------------------------------


def _artifacts(ctx: D.ParcelContext, ac: dict, free: BaseGeometry, max_count: int | None = None) -> list[dict]:
    """A few props: this patch deals ``kinds_per_patch`` of the crop's artifact kinds; clusters at a corner of the patch, spots on
    its edge, a lone one inside. At least ``min`` on a patch of ``min_area_m2`` or more (every crop has some); never more than
    ``max`` (or ``max_count``). On top of that each of the ``extras`` (a lone tractor, a sprinkler line, a hay stack) is tried
    once with its own probability."""
    items = ac.get("items") or []
    area = free.area
    if not items or free.is_empty or area < float(ac["min_area_m2"]):
        return []
    rng = seeded(f"{ctx.id}:detail:crop:art")
    kinds = list(dict.fromkeys(i["kind"] for i in items))
    lo, hi = (int(x) for x in ac["kinds_per_patch"])
    chosen = set(rng.sample(kinds, min(rng.randint(lo, hi), len(kinds))))
    items = [i for i in items if i["kind"] in chosen]
    extras = [e for e in ac.get("extras") or [] if rng.random() < float(e["probability"])]
    n = max(int(ac["min"]), min(int(ac["max"]), round(float(ac["per_ha"]) * area / 1e4)))
    if max_count is not None:
        n = min(n, max_count)
    inner = free.buffer(-float(ac["inset_m"]), join_style="round")
    inner = inner if not inner.is_empty else free
    ring_poly = max(D._polys(free), key=lambda p: p.area)
    ring = ring_poly.exterior
    mrr = list(ring_poly.minimum_rotated_rectangle.exterior.coords)[:4]
    corners = [nearest_points(Point(c), inner)[1] for c in mrr]
    rng.shuffle(corners)
    anchors_corner = corners[:2]
    cluster_m = float(ac["cluster_m"])
    weights = [float(i["weight"]) for i in items]
    placed: list[tuple[float, float, str, float]] = []  # x, y, kind, sep
    out: list[dict] = []
    safe = free.buffer(-0.4)
    safe = safe if not safe.is_empty else free
    shapely.prepare(safe)

    def heading(p: Point) -> float:
        s = ring.project(p)
        a, b = ring.interpolate(max(0.0, s - 0.4)), ring.interpolate(min(ring.length, s + 0.4))
        return math.atan2(b.y - a.y, b.x - a.x)

    def fits(x: float, y: float, kind: str, sep: float) -> bool:
        if not shapely.contains_xy(safe, x, y):
            return False
        return all(math.hypot(x - px, y - py) >= max(sep, ps) * (0.5 if k != kind else 1.0) for px, py, k, ps in placed)

    deep = free.buffer(-3.0)
    deep = deep if not deep.is_empty else inner

    def put(item: dict, budget: int) -> int:
        """Places one item (a cluster, a line) at an anchor; returns how many were placed (at most ``budget``)."""
        place = item["place"]
        if place == "corner":
            anchor = anchors_corner[rng.randrange(len(anchors_corner))]
        elif place == "edge":
            anchor = nearest_points(ring.interpolate(rng.random() * ring.length), inner)[1]
        else:
            pts = D.jitter_grid(np.random.default_rng(rng.getrandbits(32)), deep, 4.0)
            anchor = Point(pts[rng.randrange(len(pts))]) if len(pts) else inner.representative_point()
        count = min(rng.randint(int(item["group"][0]), int(item["group"][1])), budget)
        sep = float(item["sep_m"])
        got = 0
        if item.get("line"):  # a row of them (a sprinkler line) along the edge
            ang = heading(anchor)
            for i in range(count):
                x, y = anchor.x + math.cos(ang) * sep * i, anchor.y + math.sin(ang) * sep * i
                if fits(x, y, item["kind"], sep):
                    placed.append((x, y, item["kind"], sep))
                    out.extend(_emit_one(ctx, item["kind"], x, y, ang, D._rad(rng, item["scale"])))
                    got += 1
            return got
        for _ in range(count * 14):
            if got >= count:
                break
            a, r = rng.random() * math.tau, (0.0 if place == "interior" else math.sqrt(rng.random()) * cluster_m)
            x, y = anchor.x + math.cos(a) * r, anchor.y + math.sin(a) * r
            if not fits(x, y, item["kind"], sep):
                continue
            rot = rng.random() * math.tau if place == "interior" else heading(Point(x, y)) + (rng.random() - 0.5) * 0.5
            placed.append((x, y, item["kind"], sep))
            out.extend(_emit_one(ctx, item["kind"], x, y, rot, D._rad(rng, item["scale"])))
            got += 1
        return got

    for e in extras:
        put(e, 99)
    base = len(out)
    for _ in range(n * 6):  # bounded: a cramped patch just gets fewer
        if len(out) - base >= n:
            break
        put(rng.choices(items, weights)[0], n - (len(out) - base))
    return out


def _emit_one(ctx: D.ParcelContext, kind: str, x: float, y: float, rot: float, scale: float) -> list[dict]:
    xy = np.array([[x, y]])
    if not D.terrain_ok(ctx.terrain, xy[:, 0], xy[:, 1])[0]:
        return []
    z = float(ctx.terrain.surface(xy[:, 0], xy[:, 1])[0])
    return [{"kind": kind, "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot % math.tau, 3), "scale": round(scale, 3)}]


# --- the recipe -----------------------------------------------------------------------------


def _tones(free: BaseGeometry, pid: str, area: float, cc: dict, cols: list[list[int]]) -> list[tuple[str, BaseGeometry, dict]]:
    """Blobs of each ground colour after the first (flat ground only)."""
    out = []
    n = round(float(cc["tone_per_ha"]) * area / 1e4)
    if n <= 0 or free.is_empty:
        return out
    for i, col in enumerate(cols[1:], start=1):
        rng = seeded(f"{pid}:detail:crop:tones" + ("" if i == 1 else f":{i}"))
        pts = D.jitter_grid(D._gen(pid, "crop:tonepts" + ("" if i == 1 else f":{i}")), free, max(2.0, math.sqrt(area / max(n, 1)) * 0.9))[:n]
        tones = D._union([D.blob(rng, p[0], p[1], D._rad(rng, cc["tone_radius_m"]), 0.4, 1.5, 8) for p in pts]) if len(pts) else Polygon()
        out.append(("crop_tone" if i == 1 else f"crop_tone_{i}", tones, {"colour": col}))
    return out


def crop_detail(ctx: D.ParcelContext, cfg: dict) -> D.DetailResult:
    """Ground patches, plants and artifacts of one crop patch (see the module doc)."""
    pid, cc = ctx.id, cfg["crop"]
    res = D.DetailResult(pid)
    free = D.free_area(ctx, cfg, trunks=False)
    if free.is_empty:
        return res
    ec, bc, sc, tc, spc = cc["edge"], cc["baulk"], cc["stubble"], cc["tramlines"], cc["spots"]
    long_angle, origin = _row_frame(ctx.polygon)
    plan = crop_plan(ctx, cc, long_angle)
    angle = plan.angle
    ground = cc["fallow"]["colours"] if plan.fallow else cc["ground_colours"]
    cols = [plan.shade(_rgb(c)) for c in ground]
    texture = None if plan.fallow else cc.get("texture")

    nb_union = D._union([g for _, g in ctx.crop_neighbours or []])
    near_nb = nb_union.buffer(1.5) if not nb_union.is_empty else Polygon()
    baulk_grass, baulk_dirt, has_baulk = _baulk(ctx, bc)
    baulk = D._union([baulk_grass, baulk_dirt]).intersection(free) if has_baulk else Polygon()
    head = _headland(ctx, free, cc["headland"], near_nb).difference(baulk) if cc["headland"]["keep"] > 0 else Polygon()

    tram = _tramlines(free, plan, tc, origin) if plan.tram else Polygon()
    strip = _edge_strips(free, plan, tc)
    stubble: BaseGeometry = Polygon()
    lanes: BaseGeometry = Polygon()
    bales: list[dict] = []
    if plan.stubble:
        stubble, yc, bounds = _stubble(free, plan, sc, origin)
        if not stubble.is_empty:
            lanes = _stubble_lanes(stubble, plan, sc, origin)
            bales = _bales(ctx, stubble, plan, sc, yc, bounds, origin)
            tram = tram.difference(stubble) if not tram.is_empty else tram
    spots = _spots(free, ctx.polygon.area, pid, spc)
    wet_col = [round(v * float(spc["wet"]["darken"])) for v in cols[0]]
    straw = plan.shade(_rgb(sc["colours"][0]))

    # ordered batches of layers, the first wins (pieces of one batch are disjoint); the core is what is left
    batches: list[list[tuple[str, BaseGeometry, dict]]] = []
    if not baulk_grass.is_empty or not baulk_dirt.is_empty:
        batches += [[(n, g, {"colour": _rgb(c)})] for n, g, c in (("crop_baulk", baulk_grass, bc["colours"][0]), ("crop_baulk_dirt", baulk_dirt, bc["dirt_colour"])) if not g.is_empty]
    if not head.is_empty:
        batches.append([("crop_headland", head, {"texture": cc["headland"].get("texture", "dirt")})])
    if not strip.is_empty:
        batches.append([("crop_dirtstrip", strip, {"texture": cc["headland"].get("texture", "dirt")})])
    if not tram.is_empty:
        batches.append([("crop_tramline", tram, {"colour": _mix(cols[0], _rgb(tc["colour"]), float(tc["mix"]))})])
    feather, steps = float(ec["feather_m"]), int(ec["steps"])
    if feather > 0 and steps > 0:
        groups = _neighbour_groups(ctx, cfg, free)
        inner = [(stubble, straw)] if not stubble.is_empty else []
        batches.append(_edge_bands(free, ec, groups, inner, cols[0], float(bc["blend_m"])))
    if not lanes.is_empty:
        batches.append([("crop_stubble_rows", lanes, {"colour": plan.shade(_rgb(sc["colours"][1]))})])
    if not stubble.is_empty:
        batches.append([("crop_stubble", stubble, {"colour": straw})])
    for name, layer, colour in (("wet", "crop_wet", wet_col), ("bare", "crop_bare_spot", _rgb(spc["bare"]["colour"])), ("weeds", "crop_weeds", _rgb(spc["weeds"]["colour"]))):
        if name in spots:
            batches.append([(layer, spots[name], {"colour": colour})])
    if plan.fallow:
        batches += [[f] for f in _flowers(free, pid, cc["fallow"])]
    if not texture:
        ton = cc["tone_per_ha"] if not plan.fallow else cc["fallow"]["tone_per_ha"]
        rad = cc["tone_radius_m"] if not plan.fallow else cc["fallow"]["tone_radius_m"]
        batches += [[t] for t in _tones(free, pid, ctx.polygon.area, {"tone_per_ha": ton, "tone_radius_m": rad}, cols)]

    taken: BaseGeometry = Polygon()
    for batch in batches:
        made = []
        for name, g, style in batch:
            g = D._clean(g.intersection(free), float(style.get("tol", 0.15))).intersection(free)
            if not taken.is_empty:
                g = g.difference(taken)
            g = D._clean(g, 0.0)
            if g.is_empty:
                continue
            made.append(g)
            res.patches.append(D.Patch(name, g, colour=style.get("colour"), texture=style.get("texture")))
        if made:
            taken = D._union([taken, *made])
    core = D._clean(D._clean(D._difference(free, taken), 0.1).intersection(free).difference(taken), 0.0) if not taken.is_empty else D._clean(free, 0.1)
    if not core.is_empty:
        if texture:
            res.patches.append(D.Patch("crop_rows", core, colour=[255, 255, 255], texture=texture, angle=angle, origin=origin))
        else:
            res.patches.append(D.Patch("crop_ground", core, colour=cols[0]))

    # plants: everywhere on the free ground but the baulk, the headland, tracks, strips and stubble, thinning toward a real
    # edge (not a baulk's) over the same zone as the ground blend
    off = [baulk.buffer(0.15) if not baulk.is_empty else Polygon(), head.buffer(0.1) if not head.is_empty else Polygon(),
           tram.buffer(0.2) if not tram.is_empty else Polygon(), strip, stubble]
    planted = free.difference(D._union(off))
    edge_line = free.boundary
    if not near_nb.is_empty:
        edge_line = edge_line.difference(near_nb)
    arts = _artifacts(ctx, cc["artifacts"], free, int(cc["fallow"]["max_artifacts"]) if plan.fallow else None) + bales
    radii = {i["kind"]: float(i["radius_m"]) for i in (*cc["artifacts"]["items"], *(cc["artifacts"].get("extras") or []))}
    radii["art_hay_bale_round"], radii["art_hay_bale_square"] = float(sc["bales"]["round"]["radius_m"]), float(sc["bales"]["square"]["radius_m"])
    keep_off = [(a["x"], a["y"], radii.get(a["kind"], 1.0)) for a in arts]
    res.crops = _plants(ctx, cc, [] if plan.fallow else cc["plants"], planted, edge_line, plan, origin, keep_off, spots.get("bare", Polygon()),
                        spots.get("weeds", Polygon()), float(spc["weeds"]["plant_scale"])) + arts
    return res
