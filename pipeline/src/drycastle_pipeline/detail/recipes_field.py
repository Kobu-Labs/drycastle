"""The field (meadow) and village (street side) recipes."""

import math
import random

import shapely.affinity
from shapely.geometry import Polygon, box
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.detail.model import DetailResult, ParcelContext
from drycastle_pipeline.detail.parts import (
    _assemble,
    _emit,
    _split_kinds,
    _tone_layers,
    _trunk_filter,
    _verge,
    _zones,
    free_area,
)
from drycastle_pipeline.detail.swamp import _add_swamp
from drycastle_pipeline.detail.tools import _along, _gen, _rad, _union, blob
from drycastle_pipeline.props.helpers import seeded


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
    colours = {"tone_a": flat["tone_a"], "tone_b": flat["tone_b"]}
    if ctx.swamp is not None:  # a meadow within the reach of water: wet floor
        _add_swamp(ctx, cfg, free, layers, colours)
    got = _assemble(res, free, layers, colours, {"furrow": rects}, seeded(f"{pid}:detail:colours"))

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


def village_detail(ctx: ParcelContext, cfg: dict) -> DetailResult:
    """Village ground (``village`` ground areas: street sides, greens): grass over all of it beside the carriageway (OSM
    roads are the only paved ground), with gravel and dirt verge blobs, tufts along the asphalt edge and pebbles on the
    gravel."""
    pid, rc = ctx.id, cfg["village"]
    res = DetailResult(pid)
    if ctx.road is None or ctx.road.is_empty:
        return res
    free = free_area(ctx, cfg, trunks=False)
    verge_g, verge_d = _verge(ctx)
    cols = [hex_to_rgb(h) for h in rc["grass_colours"]]
    got = _assemble(res, free, {"verge_gravel": verge_g, "verge_dirt": verge_d, "lawn": free}, {"lawn": cols}, {}, seeded(f"{pid}:detail:colours"))
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
