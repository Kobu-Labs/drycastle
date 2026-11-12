"""The flood dike: a gravel track with grass patches along the road, flat grass beside it and thick grass as geometry.

Recipe ``dike`` of the detail stage (``detail.dike`` in ``rules.yaml``; the parcels listed there, P-0073 for the dike
north of the village). The parcel is the dike: its polygon is the foot of the hill, so no profile is made up here, the
patches and the clutter lie on the terrain (the terrain already holds the hill).

* **track**: the road edges inside the parcel, wider than their carriageway by ``track_extra_m`` plus a wandering edge,
  textured ``dike_gravel`` (fine light gravel, hard-edged grass patches) and lifted above the road so the texture
  shows. It is laid in pieces of ``piece_m``, each turned along its own stretch of road and started at another place of
  the tile, so the grass patches do not repeat in rows;
* **slopes**: the free ground of the parcel in flat greens (the ``lawn`` layer);
* **grass**: instances of ``tall_grass``, ``tuft`` and ``straw`` (meshes of ``clutter_meshes.glb``) over the free
  ground, taller the further from the track, and tufts on the grass patches of the track (read from the texture, so they
  sit on the drawn patches).
"""

import math
import random

import matplotlib.image as mpimg
import numpy as np
from shapely.geometry import LineString, Polygon
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
            track_parts.append(_ribbon(seg, width / 2 + float(rc["track_extra_m"]) / 2, float(rc["track_wobble_m"]), rng, rc["spill"]))
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
    res.patches += track_patches
    track = unary_union([p.geom for p in track_patches]) if track_patches else Polygon()

    free = free_area(ctx, cfg)
    cols = [hex_to_rgb(h) for h in rc["lawn_colours"]]
    got = _assemble(res, free, {"lawn": free}, {"lawn": cols}, {}, seeded(f"{pid}:detail:colours"))
    grass = got.get("lawn", Polygon())
    if not track.is_empty:
        grass = grass.difference(track.buffer(0.05))

    out = []
    gc = rc["grass"]
    g = _gen(pid, "dike_grass")
    pts = _zones(g, [(grass, 1.0)], float(gc["spacing_m"]))
    emitted = []
    for k, sub in _split_kinds(g, pts, gc["kinds"]).items():
        emitted += _emit(g, k, sub, gc["scale"], ctx, cfg)
    if emitted:
        dist = distance_to(track)(np.array([c["x"] for c in emitted]), np.array([c["y"] for c in emitted]))
        for c, d in zip(emitted, dist, strict=True):  # taller the further from the track: the sides of the dike
            c["scale"] = round(c["scale"] * (1.0 + float(gc["reach_scale"]) * min(1.0, d / float(gc["reach_m"]))), 3)
    out += emitted

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
