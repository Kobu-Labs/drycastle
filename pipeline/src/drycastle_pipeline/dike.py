"""The flood dike: a gravel track with grass patches along the road, flat grass beside it and thick grass as geometry.

Recipe ``dike`` of the detail stage (``detail.dike`` in ``rules.yaml``; the parcels listed there, P-0073 for the dike
north of the village). The parcel is the dike: its polygon is the foot of the hill, so no profile is made up here, the
patches and the clutter lie on the terrain (the terrain already holds the hill).

* **track**: the road edges inside the parcel as a gravel strip ``track_width_m`` wide (narrower than the carriageway)
  with a wandering edge, textured ``dike_gravel`` (fine light gravel, hard-edged grass patches) and lifted above the lawn,
  which is lifted above the road ribbon and so covers what the track leaves of it. It is laid in pieces of ``piece_m``, each turned along its own stretch of road and started at another place of
  the tile, so the grass patches do not repeat in rows;
* **slopes**: the free ground of the parcel in flat greens (the ``lawn`` layer);
* **grass**: instances of ``tall_grass``, ``tuft`` and ``straw`` (meshes of ``clutter_meshes.glb``) over the free
  ground, taller the further from the track, and tufts on the grass patches of the track (read from the texture, so they
  sit on the drawn patches).
"""

import dataclasses
import math
import random

import matplotlib.image as mpimg
import numpy as np
import shapely
from shapely.geometry import LineString, Polygon
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.assets import ASSETS_DIR
from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.detail import (
    DetailResult,
    ParcelContext,
    Patch,
    _assemble,
    _clean,
    _emit,
    _gen,
    _polys,
    _split_kinds,
    _zones,
    blob,
    distance_to,
    free_area,
    scatter,
)
from drycastle_pipeline.props import seeded
from drycastle_pipeline.textures import texture

TEXTURE_ID = "dike_gravel"
# The grass patches of the texture (fill, rim), as scripts/make_textures.py draws them.
PATCH_RGB = ((0x6F, 0xA0, 0x4A), (0x4F, 0x7D, 0x3A))
_IMAGE: list[np.ndarray] = []


def _wander(rng: random.Random, s: np.ndarray) -> np.ndarray:
    """Rough noise 0..1 along ``s`` metres: waves of 1.4 to 16 m, stretched so that it reaches both ends often."""
    waves = ((1.4, 0.35), (3.1, 0.5), (7.0, 0.7), (16.0, 0.6))
    out = sum(amp * np.sin(s / wl * math.tau + rng.random() * math.tau) for wl, amp in waves) / sum(a for _, a in waves)
    return 0.5 + 0.5 * np.clip(1.7 * out, -1.0, 1.0)


def _ribbon(line: LineString, half: float, extra: float, rng: random.Random, spill: dict) -> Polygon:
    """A ribbon round ``line``, ``half`` wide each side at least, the edges wandering out by up to ``extra`` and
    loose pebbles lying beyond them (small irregular blobs; ``spill``: spacing_m, radius_m [lo, hi], reach_m)."""
    n = max(2, int(line.length / 0.35) + 1)
    s = np.linspace(0.0, line.length, n)
    pts = np.array([line.interpolate(float(d)).coords[0] for d in s])
    tan = np.gradient(pts, axis=0)
    tan /= np.maximum(np.linalg.norm(tan, axis=1, keepdims=True), 1e-9)
    nor = np.stack([-tan[:, 1], tan[:, 0]], axis=1)
    off_l, off_r = half + extra * _wander(rng, s), half + extra * _wander(rng, s)
    parts = [Polygon(np.concatenate([pts + nor * off_l[:, None], (pts - nor * off_r[:, None])[::-1]])).buffer(0)]
    for off, sign in ((off_l, 1.0), (off_r, -1.0)):
        d = rng.random() * float(spill["spacing_m"])
        while d < line.length:
            i = min(n - 1, int(d / line.length * (n - 1)))
            r = float(spill["radius_m"][0]) + rng.random() * (float(spill["radius_m"][1]) - float(spill["radius_m"][0]))
            out = off[i] + (rng.random() * 1.1 - 0.35) * float(spill["reach_m"])
            c = pts[i] + sign * nor[i] * out
            parts.append(blob(rng, float(c[0]), float(c[1]), r, wobble=0.55, stretch=1.0 + rng.random(), axis=float(math.atan2(tan[i][1], tan[i][0]))))
            d += float(spill["spacing_m"]) * (0.4 + 1.2 * rng.random())
    return unary_union(parts)


def _closed_wander(rng: random.Random, length: float, s: np.ndarray) -> np.ndarray:
    """Rough noise 0..1 along a closed ring of ``length`` metres (whole numbers of waves, so it joins up)."""
    out = np.zeros_like(s)
    for wl, amp in ((7.0, 0.5), (14.0, 0.8), (32.0, 0.7)):  # long, so that the stage's 25 cm simplifying leaves it round
        out += amp * np.sin(s / length * math.tau * max(1, round(length / wl)) + rng.random() * math.tau)
    return 0.5 + 0.5 * np.clip(1.5 * out / 2.0, -1.0, 1.0)


def _ragged(poly: Polygon, rng: random.Random, inward: float, outward: float) -> tuple[Polygon, np.ndarray, np.ndarray, np.ndarray]:
    """The parcel's outline pushed in by up to ``inward`` and out by up to ``outward`` metres, smoothly and at random.
    Returns the polygon and the outline's points, outward normals and offsets (for the blobs beyond it)."""
    poly = orient(poly.buffer(0))
    ring = LineString(poly.exterior.coords)
    n = max(8, int(ring.length / 0.7))
    s = np.linspace(0.0, ring.length, n, endpoint=False)
    pts = np.array([ring.interpolate(float(d)).coords[0] for d in s])
    tan = np.roll(pts, -1, axis=0) - np.roll(pts, 1, axis=0)
    tan /= np.maximum(np.linalg.norm(tan, axis=1, keepdims=True), 1e-9)
    nor = np.stack([tan[:, 1], -tan[:, 0]], axis=1)  # outward for a counter-clockwise ring
    off = -inward + (inward + outward) * _closed_wander(rng, ring.length, s) ** 1.3
    hull = Polygon(pts + nor * off[:, None]).buffer(0)
    if hull.is_empty or hull.geom_type != "Polygon":
        hull = max(_polys(hull), key=lambda g: g.area, default=poly)
    return hull, pts, nor, off


def _texel(u: np.ndarray, v: np.ndarray) -> np.ndarray:
    if not _IMAGE:
        _IMAGE.append((mpimg.imread(ASSETS_DIR / "drycastle-generated-textures" / f"{TEXTURE_ID}.png")[..., :3] * 255).round())
    img = _IMAGE[0]
    h, w = img.shape[:2]
    return img[np.floor((1.0 - v) % 1.0 * h).astype(int) % h, np.floor(u % 1.0 * w).astype(int) % w]


def _uv(pts: np.ndarray, angle: float, origin: tuple[float, float]) -> tuple[np.ndarray, np.ndarray]:
    """The UVs the stage gives a patch point (``detail.patch_meshes``): tile repeats, turned by ``angle`` about ``origin``."""
    tex = texture(TEXTURE_ID)
    c, s = math.cos(-angle), math.sin(-angle)
    dx, dy = pts[:, 0] - origin[0], pts[:, 1] - origin[1]
    return (dx * c - dy * s) / tex.tile_w, (dx * s + dy * c) / tex.tile_h


def _track_pieces(ctx: ParcelContext, rc: dict, rng: random.Random) -> list[Patch]:
    """The track as patches: per road stretch of ``piece_m``, a rectangle along it clipped to the track."""
    tex = texture(TEXTURE_ID)
    track_parts, pieces = [], []
    for line, width in ctx.road_lines or []:
        inside = line.intersection(ctx.polygon)
        for seg in (inside.geoms if hasattr(inside, "geoms") else [inside]):
            if seg.is_empty or seg.length < 0.5 or not isinstance(seg, LineString):
                continue
            track_parts.append(_ribbon(seg, float(rc["track_width_m"]) / 2, float(rc["track_wobble_m"]), rng, rc["spill"]))
            k = max(1, round(seg.length / float(rc["piece_m"])))
            for i in range(k):
                a, b = seg.interpolate(seg.length * i / k), seg.interpolate(seg.length * (i + 1) / k)
                pieces.append((a.coords[0], b.coords[0]))
    if not track_parts:
        return []
    track = _clean(unary_union(track_parts).intersection(ctx.polygon).difference(unary_union(ctx.buildings) if ctx.buildings else Polygon()), 0.04)
    patches, taken = [], Polygon()
    for a, b in pieces:
        ang = math.atan2(b[1] - a[1], b[0] - a[0])
        ux, uy = math.cos(ang), math.sin(ang)
        length = math.hypot(b[0] - a[0], b[1] - a[1])
        # a rectangle along the stretch, a little longer than it so that bends leave no gap, wide enough for the track
        rect = LineString([(a[0] - ux * 0.6, a[1] - uy * 0.6), (b[0] + ux * 0.6, b[1] + uy * 0.6)]).buffer(float(rc["track_wobble_m"]) + 3.0, cap_style="flat")
        geom = track.intersection(rect).difference(taken)
        if geom.is_empty or geom.area < 0.05:
            continue
        taken = taken.union(geom)
        # the track's centre line at v = 0.5 of the tile, and a random start along it
        shift = rng.random() * tex.tile_w
        origin = (a[0] - ux * shift + uy * 0.5 * tex.tile_h, a[1] - uy * shift - ux * 0.5 * tex.tile_h)
        patches.append(Patch("dike_track", geom, texture=TEXTURE_ID, angle=ang, origin=origin, lift=float(rc["track_lift_m"])))
    rest = _clean(track.difference(taken), 0.0)
    if not rest.is_empty and patches:  # leftovers at bends: the texture as the first piece lays it
        first = patches[0]
        patches.append(Patch("dike_track", rest, texture=TEXTURE_ID, angle=first.angle, origin=first.origin, lift=first.lift))
    return patches


def dike_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    pid, rc = ctx.id, cfg["dike"]
    res = DetailResult(pid)
    rng = seeded(f"{pid}:detail:dike")
    track_patches = _track_pieces(ctx, rc, rng)
    track = unary_union([p.geom for p in track_patches]) if track_patches else Polygon()

    bc = rc["border"]
    hull, ring, nor, off = _ragged(ctx.polygon, rng, float(bc["inward_m"]), float(bc["outward_m"]))
    wide = dataclasses.replace(ctx, polygon=hull)  # the same ground rules (buildings, fences, road, ...) on the ragged outline
    # the lawn covers the road inside the parcel (the track is narrower than the carriageway under it), not beyond the parcel line
    free = free_area(dataclasses.replace(wide, road=None), cfg)
    if ctx.road is not None and not ctx.road.is_empty:
        free = free.difference(ctx.road.difference(ctx.polygon.buffer(0.3)).buffer(float(cfg["road_clearance_m"]), join_style="mitre"))
    cols = [hex_to_rgb(h) for h in rc["lawn_colours"]]
    lawn = len(res.patches)
    got = _assemble(res, free, {"lawn": free}, {"lawn": cols}, {}, seeded(f"{pid}:detail:colours"))
    for p in res.patches[lawn:]:
        p.lift = float(bc["lawn_lift_m"])  # over the neighbours' own patches, which lie at lift_m
    covered = got.get("lawn", Polygon())
    # loose blobs of lawn beyond the border, smaller the further out, so the green frays into what lies there
    reach = free_area(dataclasses.replace(ctx, polygon=hull.buffer(float(bc["blob_reach_m"]) + 2.0)), cfg)
    blobs, d = [], rng.random() * float(bc["blob_spacing_m"])
    step = float(bc["blob_spacing_m"])
    while d < len(ring) * 0.7:
        i = min(len(ring) - 1, int(d / 0.7))
        away = rng.random() ** 1.5 * float(bc["blob_reach_m"])  # most of them close to the border
        r = float(bc["blob_radius_m"][1]) * (1.0 - 0.75 * away / float(bc["blob_reach_m"])) * (0.5 + 0.5 * rng.random())
        c = ring[i] + nor[i] * (off[i] + away)
        blobs.append(blob(rng, float(c[0]), float(c[1]), max(r, float(bc["blob_radius_m"][0])), wobble=0.5, stretch=1.0 + rng.random()))
        d += step * (0.4 + 1.2 * rng.random())
    spill = _clean(unary_union(blobs).intersection(reach).difference(hull), 0.1) if blobs else Polygon()
    for poly in _polys(spill):
        res.patches.append(Patch("lawn", poly, colour=cols[rng.randrange(len(cols))], lift=float(bc["lawn_lift_m"])))
    grass = unary_union([covered, spill])
    if not track.is_empty:
        grass = grass.difference(track.buffer(0.05))

    out = []
    gc = rc["grass"]
    g = _gen(pid, "dike_grass")
    pts = _zones(g, [(grass, 1.0)], float(gc["spacing_m"]))
    if len(pts):  # the grass thins out across the border (half as dense on it, none beyond fade_m outside, full inside)
        inside = shapely.contains_xy(ctx.polygon, pts[:, 0], pts[:, 1])
        dist = shapely.distance(shapely.points(pts[:, 0], pts[:, 1]), ctx.polygon.exterior)
        keep = g.random(len(pts)) < np.clip(0.5 + np.where(inside, dist, -dist) / (2.0 * float(bc["fade_m"])), 0.0, 1.0)
        pts = pts[keep]
    emitted = []
    for k, sub in _split_kinds(g, pts, gc["kinds"]).items():
        emitted += _emit(g, k, sub, gc["scale"], ctx, cfg)
    if emitted:
        dist = distance_to(track)(np.array([c["x"] for c in emitted]), np.array([c["y"] for c in emitted]))
        for c, dd in zip(emitted, dist, strict=True):  # taller the further from the track: the sides of the dike
            c["scale"] = round(c["scale"] * (1.0 + float(gc["reach_scale"]) * min(1.0, dd / float(gc["reach_m"]))), 3)
    out += emitted

    res.patches += track_patches  # last, so that a painter's-order preview shows it over the lawn like the game does (higher lift)

    pt = rc["patch_tuft"]
    g2 = _gen(pid, "dike_patch_tuft")
    for p in track_patches:  # tufts on the drawn grass patches
        pts = scatter(g2, p.geom, float(pt["spacing_m"]))
        if not len(pts):
            continue
        u, v = _uv(pts, p.angle, p.origin)
        rgb = _texel(u, v)
        on = np.any([np.abs(rgb - np.array(c)).sum(axis=-1) < 12 for c in PATCH_RGB], axis=0) & (g2.random(len(pts)) < float(pt["keep"]))
        out += _emit(g2, "tuft", pts[on], pt["scale"], ctx, cfg)
    res.clutter = out
    return res
