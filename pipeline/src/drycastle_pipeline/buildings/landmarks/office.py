"""Hand-modelled municipal office of Drycastle (parcel P-0288): three footprints, one hipped roof.

The walls, window reveals and doors come from the usual detail path (``config/buildings/<id>.yaml``,
measured from Street View). This module adds what the generic generator cannot: one continuous hipped
roof over the two bars (the long bar ``w378900312`` + ``w378900355`` and the east wing ``w378900320``,
whose roof runs into the long bar's slope), eave overhang with fascia and gutter, the brick plinth band,
window sills and the green blinds behind the glass.

The south-west bar and the entry of the middle bar are hollow (``office_interior.py``, the rozhlas detour): there the
walls, floors and furniture come from that module, and the middle bar's detail is built on what stays solid.
"""

import copy
import math
from functools import lru_cache

import numpy as np
from shapely.geometry.polygon import orient

from drycastle_pipeline.building_detail import DETAILS_DIR, build_detail, load_details
from drycastle_pipeline.buildings import footprint_polygon, hex_to_rgb
from drycastle_pipeline import office_interior as interior
from drycastle_pipeline.landmarks import _Solids
from drycastle_pipeline.meshes import Mesh, building_mesh
from drycastle_pipeline.office_interior import edge_box as _edge_box

EAVE = 3.0
OVERHANG = 0.5
WALL, ROOF, BRICK = "#f0ece2", "#c86040", "#8c4a3c"
FASCIA, GUTTER, BLIND, SILL = "#f2f0ea", "#aeb2b8", "#8fae9a", "#e9e6de"

# Roof rectangles in map metres: centre, ridge bearing (rad), half length and half width at the walls,
# (A is cut at v = 12.7: its NW end is skewed). Rise, and whether the (low-v, high-v) end is hipped. The east wing stops at the long bar's centre line.
ROOFS = {
    "w378900355": dict(c=(-204.58, 322.89), ang=1.087, a=13.915, w=3.38, rise=1.9, hips=(True, True)),
    "w378900320": dict(c=(-190.40, 333.53), ang=0.0376, a=8.60, w=2.70, rise=1.52, hips=(False, True)),
}
DETAIL_IDS = ("w378900312", "w378900355", "w378900320")


@lru_cache(maxsize=1)
def _details():
    return load_details(DETAILS_DIR)


def _roof(S: _Solids, r: dict) -> list[tuple[float, float, float, float, float]]:
    """Add one hipped/gabled roof; returns its long eave edges (u, v0, v1, z) for fascia and gutter."""
    a, w, rise, oh = r["a"], r["w"], r["rise"], OVERHANG
    s = rise / w
    ze, zr = EAVE - oh * s, EAVE + rise
    W = w + oh
    lo_hip, hi_hip = r["hips"]
    v0e = -(a + oh) if lo_hip else -a
    v1e = a + oh if hi_hip else a
    r0 = -(a - w) if lo_hip else -a
    r1 = a - w if hi_hip else a
    faces = [
        ([(W, v0e, ze), (W, v1e, ze), (0, r1, zr), (0, r0, zr)], 0),
        ([(-W, v0e, ze), (-W, v1e, ze), (0, r1, zr), (0, r0, zr)], 0),
        ([(-W, v0e, ze), (W, v0e, ze), (W, v1e, ze), (-W, v1e, ze)], 1),
    ]
    faces.append(([(-W, v0e, ze), (W, v0e, ze), (0, r0, zr)], 0))
    faces.append(([(-W, v1e, ze), (W, v1e, ze), (0, r1, zr)], 0))
    roof, fascia = hex_to_rgb(ROOF), hex_to_rgb(FASCIA)
    S.solid([(p, roof if c == 0 else fascia) for p, c in faces])
    # fascia board and gutter along both long eaves
    for sign in (1, -1):
        u0, u1 = sorted((sign * (W - 0.03), sign * (W + 0.0)))
        S.box(u0, u1, v0e, v1e, ze - 0.22, ze + 0.02, fascia)
        g0, g1 = sorted((sign * (W + 0.0), sign * (W + 0.11)))
        S.box(g0, g1, v0e, v1e, ze - 0.14, ze + 0.0, hex_to_rgb(GUTTER))
    return []


def _to_world(S: _Solids, r: dict) -> list:
    ca, sa = math.cos(r["ang"]), math.sin(r["ang"])
    out = []
    for tri in S.tris:
        out.append([(r["c"][0] + u * -sa + v * ca, r["c"][1] + u * ca + v * sa, z) for u, v, z in tri])
    return out


def office_mesh(b: dict, detail: bool = True) -> Mesh:
    bid = b["id"]
    d = _details().get(bid)
    hollow = interior.hollow(bid)
    parts = [interior.shell(b, d, detail)] if hollow else []
    inner_edges: tuple[int, ...] = ()
    if bid == interior.MIDDLE_BAR:  # what stays solid: without the porch and the entry
        b, d = interior.solid_part(b, d)
        inner_edges = interior.SOLID_PART_INNER_EDGES
    elif bid == interior.SW_BAR:  # all hollow: no block; the shared wall with the entry is inside
        inner_edges = (3,)
    if not detail:
        if bid != interior.SW_BAR:
            parts.append(building_mesh(b))
        return Mesh.concat(parts)
    flat = copy.deepcopy(b)
    flat.update(roof="flat", roof_height_m=0.0, eave_height_m=EAVE, height_m=EAVE, levels=1)
    flat["wall_colour"] = hex_to_rgb(WALL)
    if d is not None and bid != interior.SW_BAR:
        block, panes = build_detail(flat, d)
        # the panes are the back of the window recesses: baked in (not breakable), or the recess is open into the solid block
        parts += [block, *(t.mesh for t in panes)]
    S = _Solids()
    ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
    by_edge: dict[int, list] = {}
    for o in (d.openings if d else []):
        by_edge.setdefault(o.edge, []).append(o)
    brick, sill, blind = hex_to_rgb(BRICK), hex_to_rgb(SILL), hex_to_rgb(BLIND)
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        length = math.dist(p, q)
        if length < 0.5 or i in inner_edges:
            continue
        # plinth band, broken at doors
        gaps = sorted((o.at_m, o.at_m + o.width_m) for o in by_edge.get(i, []) if o.kind in ("door", "garage_door"))
        s = 0.0
        for g0, g1 in [*gaps, (length, length)]:
            if g0 - s > 0.2:
                _edge_box(S, p, q, s, g0, 0.0, 0.035, 0.0, 0.5, brick)
            s = max(s, g1)
        for o in by_edge.get(i, []):
            if o.kind == "window" and o.parent is None:
                x0, x1 = o.at_m, o.at_m + o.width_m
                _edge_box(S, p, q, x0 - 0.06, x1 + 0.06, 0.0, 0.09, o.sill_m - 0.06, o.sill_m + 0.01, sill)
                if bid == interior.SW_BAR:  # a real opening: nothing behind the glass
                    continue
                depth = max(0.02, o.recess_m - 0.03)
                _edge_box(
                    S, p, q, x0 + 0.04, x1 - 0.04, -depth - 0.02, -depth, o.sill_m + 0.3 * o.height_m,
                    o.sill_m + o.height_m - 0.03, blind,
                )
    parts.append(Mesh(np.array(S.tris, float).reshape(-1, 3, 3), np.array(S.cols, np.uint8)))
    r = ROOFS.get(bid)
    if r:
        R = _Solids()
        _roof(R, r)
        parts.append(Mesh(np.array(_to_world(R, r), float), np.array(R.cols, np.uint8)))
    return Mesh.concat([m for m in parts if m.tris.size])
