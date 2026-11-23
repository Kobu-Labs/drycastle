"""Swamp ground: where it lies and its wet floor (mud, puddles), shared by the field and forest recipes."""

import math

import numpy as np
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.model import ParcelContext
from drycastle_pipeline.detail.tools import _gen, _rad, _union, blob, scatter
from drycastle_pipeline.ground.areas import water_kinds
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.props.stands import wet_zone
from drycastle_pipeline.terrain.terrain import Terrain

MUD_SALT, PUDDLE_SALT, DAMP_SALT = 5200, 5300, 5400  # the noises of mud and puddles (the Rust stage reads the same numbers)


def swamp_zone(units: list[dict], types: dict[str, dict], plan, terrain: Terrain, reach_m: float) -> BaseGeometry | None:
    """Where swamp ground lies: :func:`props.wet_zone` (water ground areas and the wet areas of water patches) buffered by ``reach_m``,
    the zone the swamp stand of trees stands in (``props.trees.stands.swamp.reach_m``). None without any water."""
    from drycastle_pipeline.terrain.water import wet_area

    wet = [wet_area(p.polygon, terrain, p.water_y) for p in plan.patches.values() if p.water_y is not None]
    wet = [w for w in wet if not w.is_empty]
    zone = wet_zone(units, water_kinds(types), unary_union(wet) if wet else None)
    return None if zone is None else zone.buffer(reach_m)


def _hollow(terrain: Terrain, xs: np.ndarray, ys: np.ndarray, h: dict) -> np.ndarray:
    """0..1: how far below its surroundings the ground lies (the mean height on a ring of ``ring_m`` minus the point's own, over ``depth_m``)."""
    r = float(h["ring_m"])
    ring = np.mean([terrain.surface(xs + r * math.cos(a), ys + r * math.sin(a)) for a in np.arange(8) * (math.tau / 8)], axis=0)
    return np.clip((ring - terrain.surface(xs, ys)) / float(h["depth_m"]), 0.0, 1.0)


def _swamp_layers(ctx: ParcelContext, cfg: dict, free: BaseGeometry) -> dict[str, BaseGeometry]:
    """The wet floor of ``ctx.swamp`` (``detail.swamp``): the base colour over all of it, large mud patches, and small puddles (likelier in
    hollows) each in a rim of mud. Shapes only; :func:`_assemble` clips them."""
    sc, hol = cfg["swamp"], cfg["hollow"]
    area = free.intersection(ctx.swamp)
    if area.is_empty:
        return {}
    rng, g = seeded(f"{ctx.id}:detail:swamp"), _gen(ctx.id, "swamp")
    mc, pc = sc["mud"], sc["puddle"]
    mud, puddles = [], []
    for x, y in scatter(g, area, float(mc["spacing_m"]), lambda xs, ys: float(mc["keep"]) * meadow_mod.clump(xs, ys, float(mc["clump"]), float(mc["clump_m"]), MUD_SALT)):
        mud.append(blob(rng, float(x), float(y), _rad(rng, mc["radius_m"]), 0.4, float(mc["stretch"]), 10))

    def likely(xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
        return (float(pc["keep"]) * meadow_mod.clump(xs, ys, float(pc["clump"]), float(pc["clump_m"]), PUDDLE_SALT)
                * (1.0 + float(hol["gain"]) * _hollow(ctx.terrain, xs, ys, hol)))

    for x, y in scatter(g, area, float(pc["spacing_m"]), likely):
        pud = blob(rng, float(x), float(y), _rad(rng, pc["radius_m"]), 0.35, float(pc["stretch"]), 9)
        puddles.append(pud)
        if float(pc["rim_m"]) > 0:
            mud.append(pud.buffer(float(pc["rim_m"]), quad_segs=2))
    return {"mud": _union(mud), "puddle": _union(puddles)}  # the dark base is the terrain's colour there (scene.ParcelColours)


def _add_swamp(ctx: ParcelContext, cfg: dict, free: BaseGeometry, layers: dict[str, BaseGeometry], colours: dict) -> None:
    """Swamp ground on a recipe's layers: the tone patches give way to the wet floor, whose layers join the recipe's own."""
    for name in ("tone_a", "tone_b"):
        if name in layers and not layers[name].is_empty:
            layers[name] = layers[name].difference(ctx.swamp)
    for name, geom in _swamp_layers(ctx, cfg, free).items():
        layers[name] = _union([layers[name], geom]) if name in layers else geom
    colours["swamp"] = [hex_to_rgb(h) for h in cfg["swamp"]["colours"]]
