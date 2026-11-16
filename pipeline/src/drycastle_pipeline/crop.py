"""The crop recipe (ground types ``crop_grain``, ``crop_corn``, ``crop_potato``, ``crop_rapeseed``, ``crop_bare``; issue #328).

A crop is a ground *type* (terrain only, ADR 0022): a hand-drawn ground patch of one of these types is a crop field. The
recipe turns the patch into

* **ground patches**: flat tones (or a texture turned along the rows) draped on the terrain. The edge to the surrounding ground
  is soft (mode ``feather``: ``steps`` bands from the surround colour to the field colour over ``feather_m``, each band's edge
  wandering with value noise) or sharp (mode ``wall``). A dirt headland of blobs, kept in part, runs along the edge.
* **baulks**: where two crop patches adjoin, a strip of grass (or dirt), 0.5 to 1 m wide, noise-edged. Both patches take their
  half from the same position-based width, so the two halves meet.
* **instances** (:attr:`DetailResult.crops`, the ``crops.json`` of the map): the plants (rows or a jittered grid, thinning out
  toward a feathered edge, stopping dead at a wall) and a few artifacts (hay bales, a tractor, ...) clustered at a corner or
  on the edge. Rows ``[x, y, z, rot, scale]`` per chunk and kind, like the clutter, but a separate file and no tint.

Cosmetic only: nothing here collides or blocks a bullet. A field nobody painted keeps the ``field`` recipe.
Everything is seeded per unit (``sha256("<id>:detail:<purpose>")``), so reruns give identical output.
"""

import math

import numpy as np
import shapely
import shapely.affinity
from shapely.geometry import Point, Polygon
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


# --- geometry helpers -----------------------------------------------------------------------


def _row_frame(poly: Polygon) -> tuple[float, tuple[float, float]]:
    """The direction of the rows (the long side of the minimum rotated rectangle, radians) and its centre."""
    mrr = poly.minimum_rotated_rectangle
    ring = list(mrr.exterior.coords)
    a, b = max(zip(ring, ring[1:], strict=False), key=lambda e: math.dist(*e))
    return math.atan2(b[1] - a[1], b[0] - a[0]), (mrr.centroid.x, mrr.centroid.y)


def _wobble(poly: Polygon, amp: float, lattice: float, salt: int) -> BaseGeometry:
    """The exterior of ``poly`` with every vertex (a metre apart) pushed along its normal by smooth noise in +-``amp``."""
    poly = orient(poly, 1.0)
    xy = np.asarray(shapely.segmentize(poly.exterior, 1.0).coords)[:-1]
    if len(xy) < 4:
        return poly
    t = np.roll(xy, -1, axis=0) - np.roll(xy, 1, axis=0)
    ln = np.maximum(np.hypot(t[:, 0], t[:, 1]), 1e-9)
    normal = np.stack([-t[:, 1] / ln, t[:, 0] / ln], axis=1)  # left of a counter-clockwise ring: inward
    off = amp * (value_noise(xy[:, 0], xy[:, 1], lattice, salt) * 2 - 1)
    out = Polygon(xy + normal * off[:, None], [r.coords for r in poly.interiors]).buffer(0)
    return out if not out.is_empty else poly


def _inset(geom: BaseGeometry, d: float, amp: float, lattice: float, salt: int) -> BaseGeometry:
    """``geom`` shrunk by ``d`` with a wandering edge."""
    inner = geom.buffer(-d, join_style="round") if d > 0 else geom
    if inner.is_empty or amp <= 0:
        return inner
    return D._union([_wobble(p, amp, lattice, salt) for p in D._polys(inner)])


def _smooth(x: np.ndarray) -> np.ndarray:
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


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


# --- plants ---------------------------------------------------------------------------------


def _grid(g: np.random.Generator, geom: BaseGeometry, spec: dict, angle: float, origin: tuple[float, float]) -> np.ndarray:
    """Candidate plant points: rows ``row_m`` apart along ``angle`` with a plant every ``spacing_m``, or a jittered grid."""
    step, row = float(spec["spacing_m"]), float(spec.get("row_m", 0.0))
    if geom.is_empty:
        return np.zeros((0, 2))
    if row <= 0:
        return D.jitter_grid(g, geom, step, 0.9)
    local = shapely.affinity.rotate(geom, -angle, origin=origin, use_radians=True)
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


def _plants(ctx: D.ParcelContext, ec: dict, planted: BaseGeometry, edge_line: BaseGeometry, angle: float,
            origin: tuple[float, float], keep_off: list[tuple[float, float, float]]) -> list[dict]:
    out: list[dict] = []
    edge_d = D.distance_to(edge_line)
    for spec in ec["plants"]:
        kind = spec["kind"]
        g = D._gen(ctx.id, f"crop:{kind}")
        pts = _grid(g, planted, spec, angle, origin)
        if not len(pts):
            continue
        p = plant_density(edge_d(pts[:, 0], pts[:, 1]), ec["edge"]) * float(spec["density"])
        amp = float(spec.get("noise_amp", 0.0))
        if amp > 0:  # patches of thinner growth
            p = p * (1 - amp * (1 - value_noise(pts[:, 0], pts[:, 1], 5.0, _NOISE_SALT + 7)))
        if ec["edge"]["mode"] == "wall":  # a hard edge, only a hair of jitter
            p = np.where(edge_d(pts[:, 0], pts[:, 1]) + (g.random(len(pts)) - 0.5) * 0.2 >= float(ec["edge"]["plant_margin_m"]), p, 0.0)
        pts = pts[g.random(len(pts)) < p]
        for x, y, r in keep_off:
            if len(pts):
                pts = pts[np.hypot(pts[:, 0] - x, pts[:, 1] - y) >= r]
        out += _emit(g, kind, pts, spec["scale"], ctx)
    return out


def _emit(g: np.random.Generator, kind: str, pts: np.ndarray, span: list[float], ctx: D.ParcelContext) -> list[dict]:
    if not len(pts):
        return []
    pts = pts[D.terrain_ok(ctx.terrain, pts[:, 0], pts[:, 1])]
    if not len(pts):
        return []
    z = ctx.terrain.surface(pts[:, 0], pts[:, 1])
    rot = g.random(len(pts)) * math.tau
    sc = span[0] + g.random(len(pts)) * (span[1] - span[0])
    return [{"kind": kind, "x": round(float(x), 3), "y": round(float(y), 3), "z": round(float(h), 3),
             "rot": round(float(r), 3), "scale": round(float(s), 3)} for (x, y), h, r, s in zip(pts, z, rot, sc, strict=True)]


# --- artifacts ------------------------------------------------------------------------------


def _artifacts(ctx: D.ParcelContext, ac: dict, free: BaseGeometry) -> list[dict]:
    """A few props: clusters at a corner of the patch, spots on its edge, a lone one inside. At least ``min`` on a patch of
    ``min_area_m2`` or more (every crop has some); never more than ``max``."""
    items = ac.get("items") or []
    area = free.area
    if not items or free.is_empty or area < float(ac["min_area_m2"]):
        return []
    rng = seeded(f"{ctx.id}:detail:crop:art")
    n = max(int(ac["min"]), min(int(ac["max"]), round(float(ac["per_ha"]) * area / 1e4)))
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
    for _ in range(n * 6):  # bounded: a cramped patch just gets fewer
        if len(out) >= n:
            break
        item = rng.choices(items, weights)[0]
        place = item["place"]
        if place == "corner":
            anchor = anchors_corner[rng.randrange(len(anchors_corner))]
        elif place == "edge":
            anchor = nearest_points(ring.interpolate(rng.random() * ring.length), inner)[1]
        else:
            pts = D.jitter_grid(np.random.default_rng(rng.getrandbits(32)), deep, 4.0)
            anchor = Point(pts[rng.randrange(len(pts))]) if len(pts) else inner.representative_point()
        count = rng.randint(int(item["group"][0]), int(item["group"][1]))
        sep = float(item["sep_m"])
        got = 0
        for _ in range(count * 14):
            if got >= count or len(out) >= n:
                break
            a, r = rng.random() * math.tau, (0.0 if place == "interior" else math.sqrt(rng.random()) * cluster_m)
            x, y = anchor.x + math.cos(a) * r, anchor.y + math.sin(a) * r
            if not fits(x, y, item["kind"], sep):
                continue
            rot = rng.random() * math.tau if place == "interior" else heading(Point(x, y)) + (rng.random() - 0.5) * 0.5
            placed.append((x, y, item["kind"], sep))
            out += _emit_one(ctx, item["kind"], x, y, rot, D._rad(rng, item["scale"]))
            got += 1
    return out


def _emit_one(ctx: D.ParcelContext, kind: str, x: float, y: float, rot: float, scale: float) -> list[dict]:
    xy = np.array([[x, y]])
    if not D.terrain_ok(ctx.terrain, xy[:, 0], xy[:, 1])[0]:
        return []
    z = float(ctx.terrain.surface(xy[:, 0], xy[:, 1])[0])
    return [{"kind": kind, "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot % math.tau, 3), "scale": round(scale, 3)}]


# --- the recipe -----------------------------------------------------------------------------


def crop_detail(ctx: D.ParcelContext, cfg: dict) -> D.DetailResult:
    """Ground patches, plants and artifacts of one crop patch (see the module doc)."""
    pid, cc = ctx.id, cfg["crop"]
    res = D.DetailResult(pid)
    free = D.free_area(ctx, cfg, trunks=False)
    if free.is_empty:
        return res
    ec, bc = cc["edge"], cc["baulk"]
    cols = [_rgb(c) for c in cc["ground_colours"]]
    surround = _rgb(cc["surround"])
    angle, origin = _row_frame(ctx.polygon)

    nb_union = D._union([g for _, g in ctx.crop_neighbours or []])
    near_nb = nb_union.buffer(max(1.5, float(ec["feather_m"]) * 0.6)) if not nb_union.is_empty else Polygon()
    baulk_grass, baulk_dirt, has_baulk = _baulk(ctx, bc)
    baulk = D._union([baulk_grass, baulk_dirt]).intersection(free) if has_baulk else Polygon()
    head = _headland(ctx, free, cc["headland"], near_nb).difference(baulk) if cc["headland"]["keep"] > 0 else Polygon()

    # ordered layers, the first wins; the core is what is left
    layers: list[tuple[str, BaseGeometry, dict]] = []
    if not baulk_grass.is_empty:
        layers.append(("crop_baulk", baulk_grass, {"colour": _rgb(bc["colours"][0])}))
    if not baulk_dirt.is_empty:
        layers.append(("crop_baulk_dirt", baulk_dirt, {"colour": _rgb(bc["dirt_colour"])}))
    if not head.is_empty:
        layers.append(("crop_headland", head, {"texture": cc["headland"].get("texture", "dirt")}))
    feather, steps = float(ec["feather_m"]), int(ec["steps"])
    if ec["mode"] == "feather" and feather > 0 and steps > 0:
        prev = free
        for k in range(1, steps + 1):
            cur = _inset(free, feather * k / steps, float(ec["wobble_m"]), float(ec["wobble_lattice_m"]), _NOISE_SALT + 20 + k).intersection(prev)
            band = prev.difference(cur)
            if not near_nb.is_empty:
                band = band.difference(near_nb)  # a baulk side stays crisp: no fade into the neighbour
            w = k / (steps + 1)
            layers.append((f"crop_edge_{k}", band, {"colour": [round(a * (1 - w) + b * w) for a, b in zip(surround, cols[0], strict=True)]}))
            prev = cur
    taken: BaseGeometry = Polygon()
    clipped: dict[str, BaseGeometry] = {}
    for name, g, style in layers:
        g = D._clean(g.intersection(free), 0.15).intersection(free)
        if not taken.is_empty:
            g = g.difference(taken)
        g = D._clean(g, 0.0)
        if g.is_empty:
            continue
        clipped[name] = g
        taken = taken.union(g)
        res.patches.append(D.Patch(name, g, colour=style.get("colour"), texture=style.get("texture")))
    core = D._clean(D._clean(D._difference(free, taken), 0.1).intersection(free).difference(taken), 0.0) if not taken.is_empty else D._clean(free, 0.1)
    texture = cc.get("texture")
    if not core.is_empty:
        if texture:
            res.patches.append(D.Patch("crop_rows", core, colour=[255, 255, 255], texture=texture, angle=angle, origin=origin))
        else:
            rng = seeded(f"{pid}:detail:crop:tones")
            n = round(float(cc["tone_per_ha"]) * ctx.polygon.area / 1e4)
            tones = D._union([D.blob(rng, p[0], p[1], D._rad(rng, cc["tone_radius_m"]), 0.4, 1.5, 8)
                              for p in D.jitter_grid(D._gen(pid, "crop:tonepts"), core, max(2.0, math.sqrt(ctx.polygon.area / max(n, 1)) * 0.9))[:n]]) if n > 0 else Polygon()
            tones = D._clean(tones.intersection(core), 0.25).intersection(core)
            if not tones.is_empty and len(cols) > 1:
                res.patches.append(D.Patch("crop_tone", tones, colour=cols[1]))
                core = D._clean(core.difference(tones), 0.0)
            if not core.is_empty:
                res.patches.append(D.Patch("crop_ground", core, colour=cols[0]))

    # plants: everywhere on the free ground but the baulk and the headland, thinning toward a real edge (not a baulk's)
    planted = free.difference(D._union([baulk.buffer(0.15) if not baulk.is_empty else Polygon(), head.buffer(0.1) if not head.is_empty else Polygon()]))
    edge_line = free.boundary
    if not near_nb.is_empty:
        edge_line = edge_line.difference(near_nb)
    arts = _artifacts(ctx, cc["artifacts"], free)
    keep_off = [(a["x"], a["y"], float(next((i["radius_m"] for i in cc["artifacts"]["items"] if i["kind"] == a["kind"]), 1.0))) for a in arts]
    res.crops = _plants(ctx, cc, planted, edge_line, angle, origin, keep_off) + arts
    return res
