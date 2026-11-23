"""The inside of the municipal office (Obecny Urad, parcel P-0288), for the rozhlas detour (docs/events/README.md).

Hollow: the whole south-west bar ``w378900312`` (the clerks' office and the mayor's room) and the entry at the south-west
end of the middle bar ``w378900355``. The rest of the middle bar and the east wing stay solid. Along the road the middle
bar's wall stands ``SETBACK`` inside the footprint: the strip in front of it is a covered walkway under the roof (the
porch), and the entrance door is in that set-back wall.

Laid out from the owner's editor sketch (2026-11-14, points A to H, lines L2 to L10; 0.5 m grid, so walls and furniture are
squared to the building here):

  G  entrance door, porch to entry            L2   wall between the entry and the office (the footprints' shared edge), door F
  L10 wall closing the entry off from the      L3   wall between the office and the mayor's room, door E
      rest of the middle bar (stays solid)     L6   the clerks' double desk (C, D)      L7  the dictation desk (announcer B)
  L4 conference table, L5 the mayor's          H    the CCTV station (reserved, issue #270; nothing is built there)
     L-shaped desk (mayor A)

Two frames, both in map metres on the footprint (z up from the building base):
  bar frame  (t, w): t along the south-west bar's road wall from its south corner (``A2``), w inward from that wall;
  porch frame (s, d): s along the middle bar's road edge from the shared corner (``V5``), d inward from that edge.

``shell`` is the mesh (visual or collision), ``solid_part`` the middle bar's remaining solid block, ``office_panes`` the
window glass, ``urad_info`` what the game needs to know (``urad.json``).

Furniture is not in the mesh: ``PROPS`` lists it (model, place, facing, collision boxes) and the game places it, as the
church's pews (``props`` of ``urad.json``). The models are Synty SIMPLE Office Interiors (private, ``assets-private/synty/
glb/office``, built by its ``tools/office_convert.gd``); the sizes here are read off that pack's manifest. Without the pack
the game draws the collision boxes instead, so a desk is never missing.
"""

import copy
import math

import numpy as np

from drycastle_pipeline.buildings.landmarks.catalog import _Solids
from drycastle_pipeline.buildings.openings import Detail
from drycastle_pipeline.core.geometry import bearing_deg
from drycastle_pipeline.core.mesh import Mesh

SW_BAR, MIDDLE_BAR = "w378900312", "w378900355"

# the footprints (data/village/work/buildings.json), counter-clockwise; ``shell`` checks them against the building it is given
A0, A1, A2, A3 = (-209.146, 320.579), (-213.635, 311.914), (-208.026, 309.035), (-203.538, 317.699)
V0, V1, V2, V3, V4, V5 = (-196.724, 330.581), (-194.04, 335.811), (-201.724, 335.577), (-204.224, 330.83), A0, A3

EAVE = 3.0  # wall top (office.EAVE)
CEIL = 2.72  # the underside of the roof solid: the ceiling
F = 0.15  # floor, above the building base
T = 0.3  # outer walls
TI = 0.15  # the wall between the office and the mayor's room
SETBACK = 1.4  # the middle bar's road wall, inside its footprint edge
DOOR_H = 2.1
ENTRY_S = 3.9  # the entry spans s in [0, ENTRY_S] of the set-back wall (the first window of the solid part starts at 4.05)
DOOR_G = (1.8, 3.0)  # s: the entrance
DOOR_F = (3.3, 4.4)  # w: entry to office, through L2
DOOR_E = (3.25, 4.35)  # w: office to mayor, through L3
L3_T = (3.95, 3.95 + TI)
SEAT_H = 0.45
RAMP = 0.6  # the porch edge slopes down to the ground over this much

BAR_L, BAR_W = math.dist(A2, A3), math.dist(A1, A2)

WALL, WALL_IN, FLOOR, PORCH = [240, 236, 226], [226, 222, 208], [150, 132, 110], [176, 172, 164]
METAL = [60, 60, 64]

DESK_TOP = 0.79  # the pack's desks
SET_AT = (5.0, 5.1)  # the middle of the dictation desk: the set stands here, and this is ``table``
# who sits where: role, t, w, facing, female, chair model
WORKERS = (
    ("mayor", 1.75, 5.4, "-w", False, "chair_01"),  # A, in the elbow of the L desk
    ("announcer", 5.9, 5.45, "-t", True, "chair_03"),  # B
    ("clerk", 8.55, 1.61, "-t", False, "chair_02"),  # C
    ("clerk", 4.65, 1.78, "+t", True, "chair_02"),  # D
)
CCTV = (3.27, 0.5)  # H
# the mayor's room windows (sketch points I and J, 2026-11-14): (id, wall, t0, t1, z0, z1), one in each long wall

_DESK = ((0, 0.4, 0, 2.55, 0.8, 0.94),)  # collision boxes in the model's frame: (centre x, y up, z, size x, y, z); +z is its front
_TABLE = ((0, 0.4, 0, 0.9, 0.8, 1.8),)
# (model, frame, along, across, facing, height above the floor, boxes). Frames as above: "bar" (t, w), "porch" (s, d).
# A model's front (+z) looks along ``facing``; a desk preset's front is the side its user sits at.
PROPS = (
    # the clerks' office: two desks face to face under the road windows (L6), the dictation desk (L7)
    ("desk_preset_01", "bar", 7.63, 1.65, "+t", 0, _DESK),
    ("desk_preset_05", "bar", 5.57, 1.65, "-t", 0, _DESK),
    ("desk_06", "bar", *SET_AT, "+w", 0, _TABLE),
    ("deskphone_01", "bar", 4.85, 5.65, "+t", DESK_TOP, ()),
    ("papers_01", "bar", 5.15, 4.6, "+t", DESK_TOP, ()),
    ("lamp_01", "bar", 4.72, 4.4, "+t", DESK_TOP, ()),
    ("file_cabinet_02", "bar", 6.75, 5.67, "-w", 0, ((0, 0.645, 0, 0.55, 1.29, 0.62),)),
    ("file_cabinet_02", "bar", 7.32, 5.67, "-w", 0, ((0, 0.645, 0, 0.55, 1.29, 0.62),)),
    ("cupboard", "bar", 8.05, 5.76, "-w", 0, ((0, 0.75, 0, 0.86, 1.5, 0.45),)),
    ("plant_03", "bar", 9.2, 5.7, "-t", 0, ()),
    ("plant_03", "bar", 9.2, 0.55, "-t", 0, ()),
    ("pinboard_01", "bar", L3_T[1] + 0.04, 1.9, "+t", 0.75, ()),
    ("clock", "bar", BAR_L - T - 0.04, 3.85, "-t", 2.15, ()),
    ("rubbish_bin_01", "bar", 6.6, 3.25, "+t", 0, ()),
    # the mayor's room: the L desk (L5) in the corner by the courtyard window, the conference table (L4) running from it
    ("desk_preset_06", "bar", 1.33, 4.97, "+t", 0, ((-0.57, 0.4, 0, 0.9, 0.8, 2.04), (0.45, 0.4, -0.57, 1.14, 0.8, 0.9))),
    ("desk_06", "bar", 1.78, 1.25, "+w", 0, _TABLE),
    ("desk_06", "bar", 1.78, 3.05, "+w", 0, _TABLE),
    *(("chair_04", "bar", 0.95, w, "+t", 0, ()) for w in (0.9, 2.15, 3.4)),
    *(("chair_04", "bar", 2.62, w, "-t", 0, ()) for w in (0.9, 2.15, 3.4)),
    ("papers_02", "bar", 1.78, 2.15, "+w", DESK_TOP, ()),
    ("painting_01", "bar", L3_T[0], 1.2, "-t", 1.4, ()),
    ("painting_02", "bar", L3_T[0], 2.2, "-t", 1.4, ()),
    ("plant_01", "bar", 3.4, 5.5, "-t", 0, ()),
    # the entry: somewhere to wait
    ("couch_02", "porch", ENTRY_S - 0.39, 4.7, "-t", 0, ((0, 0.3, 0, 1.44, 0.6, 0.77),)),
    ("pinboard_02", "porch", ENTRY_S - 0.04, 4.7, "-t", 1.0, ()),
    ("plant_03", "porch", 3.6, 5.8, "-t", 0, ()),
    ("water_cooler_01", "porch", 0.22, 2.4, "+t", 0, ((0, 0.74, 0, 0.33, 1.47, 0.4),)),
    ("photocopier", "porch", 1.6, 5.5, "-w", 0, ((0, 0.6, 0, 1.46, 1.19, 0.93),)),
    # the workers' chairs
    *((chair, "bar", t, w, f, 0, ()) for _, t, w, f, _, chair in WORKERS),
)
MAYOR_WINDOWS = (("i", "courtyard", 0.85, 2.65, 0.9, 2.3), ("j", "road", 0.85, 2.65, 0.9, 2.3))


def _unit(p, q):
    n = math.dist(p, q)
    return ((q[0] - p[0]) / n, (q[1] - p[1]) / n)


BAR_U = _unit(A2, A3)
BAR_N = (-BAR_U[1], BAR_U[0])  # inward
PORCH_U = _unit(V5, V0)
PORCH_N = (-PORCH_U[1], PORCH_U[0])
PORCH_L = math.dist(V5, V0)


def bar(t: float, w: float) -> tuple[float, float]:
    return (A2[0] + BAR_U[0] * t + BAR_N[0] * w, A2[1] + BAR_U[1] * t + BAR_N[1] * w)


def porch(s: float, d: float) -> tuple[float, float]:
    return (V5[0] + PORCH_U[0] * s + PORCH_N[0] * d, V5[1] + PORCH_U[1] * s + PORCH_N[1] * d)


def _entry_back_corner() -> tuple[float, float]:
    """Where the entry's far wall (s = ENTRY_S) meets the middle bar's courtyard wall (V3 to V4)."""
    step = ((V3[0] - V4[0]) * PORCH_U[0] + (V3[1] - V4[1]) * PORCH_U[1])
    k = (ENTRY_S - ((V4[0] - V5[0]) * PORCH_U[0] + (V4[1] - V5[1]) * PORCH_U[1])) / step
    return (V4[0] + (V3[0] - V4[0]) * k, V4[1] + (V3[1] - V4[1]) * k)


ENTRY_X = _entry_back_corner()
ENTRY_Y = porch(ENTRY_S, SETBACK)
ENTRY_D1 = (ENTRY_X[0] - V5[0]) * PORCH_N[0] + (ENTRY_X[1] - V5[1]) * PORCH_N[1]  # depth of the entry's back corner


def _quad_box(S: _Solids, corners, z0: float, z1: float, colour, top=None) -> None:
    """A prism on a convex polygon (map metres)."""
    top = top or colour
    faces = [([(x, y, z0) for x, y in corners], colour), ([(x, y, z1) for x, y in corners], top)]
    n = len(corners)
    for k in range(n):
        (xa, ya), (xb, yb) = corners[k], corners[(k + 1) % n]
        faces.append(([(xa, ya, z0), (xb, yb, z0), (xb, yb, z1), (xa, ya, z1)], colour))
    S.solid(faces)


def edge_box(S: _Solids, p, q, s0, s1, d0, d1, z0, z1, colour) -> None:
    """Box along wall edge p->q of a counter-clockwise ring: ``s`` along it, ``d`` outward, z up."""
    ux, uy = _unit(p, q)
    nx, ny = uy, -ux
    _quad_box(S, [(p[0] + ux * s + nx * d, p[1] + uy * s + ny * d) for s, d in ((s0, d0), (s1, d0), (s1, d1), (s0, d1))], z0, z1, colour)


def _bbox(S: _Solids, t0, t1, w0, w1, z0, z1, colour, top=None) -> None:
    _quad_box(S, [bar(t0, w0), bar(t1, w0), bar(t1, w1), bar(t0, w1)], z0, z1, colour, top)


def _pbox(S: _Solids, s0, s1, d0, d1, z0, z1, colour, top=None) -> None:
    _quad_box(S, [porch(s0, d0), porch(s1, d0), porch(s1, d1), porch(s0, d1)], z0, z1, colour, top)


def _wall(box, a0: float, a1: float, b0: float, b1: float, holes, colour) -> None:
    """A wall from the base to the eave, ``a`` along it and ``b`` across; ``holes`` are (a0, a1, z0, z1) openings.
    ``box(a0, a1, b0, b1, z0, z1, colour)`` puts one piece in the right frame. A doorway is a hole from the base (z0 = 0):
    the floor slab fills its foot, and a wall piece ending at floor height would flicker against it."""
    a = a0
    for h0, h1, z0, z1 in sorted(holes):
        if h0 > a:
            box(a, h0, b0, b1, 0.0, EAVE, colour)
        if z0 > 0:
            box(h0, h1, b0, b1, 0.0, z0, colour)
        if z1 < EAVE:
            box(h0, h1, b0, b1, z1, EAVE, colour)
        a = h1
    if a1 > a:
        box(a, a1, b0, b1, 0.0, EAVE, colour)


def _windows(d: Detail | None, wall: str = "road") -> list[tuple[str, float, float, float, float]]:
    """The windows of one long wall of the south-west bar: (id, t0, t1, z0, z1). The road wall's measured ones come from
    its detail file (edge 2 is that wall); the mayor's room adds one to each wall."""
    out = [(wid, t0, t1, z0, z1) for wid, w, t0, t1, z0, z1 in MAYOR_WINDOWS if w == wall]
    if wall == "road":
        out += [
            (o.id, o.at_m, o.at_m + o.width_m, o.sill_m, o.sill_m + o.height_m)
            for o in (d.openings if d else []) if o.kind == "window" and o.edge == 2 and o.parent is None
        ]  # fmt: skip
    return out


def _check(b: dict) -> None:
    want = {SW_BAR: (A0, A1, A2, A3), MIDDLE_BAR: (V0, V1, V2, V3, V4, V5)}[b["id"]]
    got = b["footprint"]["exterior"]
    if len(got) != len(want) or any(math.dist(p, q) > 0.05 for p, q in zip(got, want)):
        raise ValueError(f"office_interior: the footprint of {b['id']} changed; the interior is laid out on the old one")


def hollow(bid: str) -> bool:
    return bid in (SW_BAR, MIDDLE_BAR)


def solid_part(b: dict, d: Detail | None) -> tuple[dict, Detail | None]:
    """The middle bar without its porch and entry: the building and detail to hand to the usual generators.
    The ring starts at the set-back road wall (edge 0), so the openings measured on the old road edge move to it."""
    _check(b)
    out = copy.deepcopy(b)
    out["footprint"] = {"exterior": [list(p) for p in (ENTRY_Y, porch(PORCH_L, SETBACK), V0, V1, V2, V3, ENTRY_X)], "holes": []}
    if d is None:
        return out, None
    d2 = copy.deepcopy(d)
    d2.openings = [o for o in d2.openings if o.edge == 5 and o.at_m >= ENTRY_S]
    for o in d2.openings:
        o.edge, o.at_m = 0, round(o.at_m - ENTRY_S, 3)
    return out, d2


SOLID_PART_INNER_EDGES = (1, 6)  # of solid_part's ring: the porch's end and the entry's far wall, no plinth there


def shell(b: dict, d: Detail | None, detail: bool = True) -> Mesh:
    """The hollow part of building ``b`` (south-west bar: all of it; middle bar: porch and entry), standing on the base.
    ``detail`` False is the collision: the same walls and floors, a thick slab for the roof."""
    _check(b)
    S = _Solids()
    top = EAVE if detail else EAVE + 0.9  # the collision keeps the old massing's roof
    if b["id"] == SW_BAR:
        holes = [(t0, t1, z0, z1) for _, t0, t1, z0, z1 in _windows(d)]
        yard = [(t0, t1, z0, z1) for _, t0, t1, z0, z1 in _windows(d, "courtyard")]

        def along(a0, a1, b0, b1, z0, z1, c):  # a wall running along the bar
            _bbox(S, a0, a1, b0, b1, z0, z1, c)

        def across(a0, a1, b0, b1, z0, z1, c):  # a wall running across it
            _bbox(S, b0, b1, a0, a1, z0, z1, c)

        _wall(along, 0.0, BAR_L, 0.0, T, holes, WALL)  # road
        _wall(along, 0.0, BAR_L, BAR_W - T, BAR_W, yard, WALL)  # courtyard
        _wall(across, T, BAR_W - T, 0.0, T, [], WALL)  # south-west end
        _wall(across, T, BAR_W - T, BAR_L - T, BAR_L, [(*DOOR_F, 0.0, F + DOOR_H)], WALL)  # L2
        _wall(across, T, BAR_W - T, *L3_T, [(*DOOR_E, 0.0, F + DOOR_H)], WALL_IN)  # L3
        _bbox(S, 0.0, BAR_L, 0.0, BAR_W, -0.3, F, FLOOR)
        _bbox(S, 0.0, BAR_L, 0.0, BAR_W, CEIL if not detail else EAVE - 0.02, top, WALL)
        if detail:  # the set on the dictation desk, and its microphone (the desk itself is a prop)
            mt, mw = SET_AT
            _bbox(S, mt - 0.2, mt + 0.2, mw - 0.14, mw + 0.14, F + DESK_TOP, F + DESK_TOP + 0.12, METAL)
            _bbox(S, mt + 0.25, mt + 0.28, mw - 0.015, mw + 0.015, F + DESK_TOP, F + DESK_TOP + 0.3, METAL)
            _bbox(S, mt + 0.22, mt + 0.31, mw - 0.04, mw + 0.04, F + DESK_TOP + 0.3, F + DESK_TOP + 0.36, METAL)
    else:

        def front(a0, a1, b0, b1, z0, z1, c):
            _pbox(S, a0, a1, b0, b1, z0, z1, c)

        _wall(front, 0.0, ENTRY_S, SETBACK, SETBACK + T, [(*DOOR_G, 0.0, F + DOOR_H)], WALL)
        # the courtyard wall of the entry (the shared wall L2 is the south-west bar's, the far wall is the solid part's face)
        edge_box(S, ENTRY_X, V4, 0.0, math.dist(ENTRY_X, V4), -T, 0.0, 0.0, EAVE, WALL)
        _quad_box(S, [porch(0.0, 0.0), porch(ENTRY_S, 0.0), ENTRY_Y, ENTRY_X, V4], -0.3, F, FLOOR)  # entry floor, and the porch before it
        _pbox(S, ENTRY_S, PORCH_L, 0.0, SETBACK, -0.3, F, PORCH)  # the walkway along the solid part
        ramp = [(0.0, F), (0.0, -0.3), (-RAMP, -0.3), (-RAMP, 0.0)]  # (d, z): the step down to the ground, as a slope
        a, c = [(*porch(0.0, dd), z) for dd, z in ramp], [(*porch(PORCH_L, dd), z) for dd, z in ramp]
        S.solid([(a, PORCH), (c, PORCH)] + [([a[k], a[(k + 1) % 4], c[(k + 1) % 4], c[k]], PORCH) for k in range(4)])
        if not detail:  # the roof over the porch and the entry
            _quad_box(S, [porch(0.0, 0.0), porch(ENTRY_S, 0.0), ENTRY_Y, ENTRY_X, V4], CEIL, top, WALL)
            _pbox(S, ENTRY_S, PORCH_L, 0.0, SETBACK, CEIL, top, WALL)
    return Mesh(np.array(S.tris, float).reshape(-1, 3, 3), np.array(S.cols, np.uint8).reshape(-1, 3))


def office_panes(b: dict, d: Detail | None) -> list[tuple[str, list, dict]]:
    """The glass in the south-west bar's road windows, as ``church_interior.church_panes``: (node, two-sided triangles in
    map metres on the building base, the ``windows.json`` entry). See-through and breakable in the game."""
    if b["id"] != SW_BAR:
        return []
    out = []
    walls = (("road", T / 2, -1.0), ("courtyard", BAR_W - T / 2, 1.0))
    for wid, t0, t1, z0, z1, w, out_n in [(*win, w, n) for wall, w, n in walls for win in _windows(d, wall)]:
        c = [(*bar(t, w), z) for t, z in ((t0, z0), (t1, z0), (t1, z1), (t0, z1))]
        tris = [[c[0], c[1], c[2]], [c[0], c[2], c[1]], [c[0], c[2], c[3]], [c[0], c[3], c[2]]]
        node = f"window_{SW_BAR}_{wid}"
        out.append((node, tris, {
            "building": SW_BAR, "id": wid, "node": node, "kind": "window", "breakable": True, "confidence": "high",
            "width_m": round(t1 - t0, 3), "height_m": round(z1 - z0, 3),
            "centre": [round(v, 3) for v in (*bar((t0 + t1) / 2, w), (z0 + z1) / 2)],
            "normal": [round(out_n * BAR_N[0], 4), round(out_n * BAR_N[1], 4)],
        }))  # fmt: skip
    return out


def urad_info(elevation: dict[str, float]) -> dict:
    """``urad.json``: the entrance, the rooms, where the workers sit, the dictation desk and the CCTV station.

    Points are ``[x east, y north, z up]`` in local metres (as ``parcels.json``; z as ``terrain.json``), yaws are compass
    bearings in degrees (0 north, 90 east). ``elevation`` is the base height of each building.
    """
    zb, zm = elevation.get(SW_BAR, 0.0), elevation.get(MIDDLE_BAR, 0.0)
    facing = {"+t": BAR_U, "-t": (-BAR_U[0], -BAR_U[1]), "+w": BAR_N, "-w": (-BAR_N[0], -BAR_N[1])}

    def at(xy, z):
        return [round(xy[0], 3), round(xy[1], 3), round(z, 3)]

    def room(rid, pts, z):
        return {"id": rid, "floor_z": round(z, 3), "ceiling_z": round(z + CEIL, 3), "footprint": [[round(x, 3), round(y, 3)] for x, y in pts]}

    l3 = (L3_T[0] + L3_T[1]) / 2
    mt, mw = SET_AT
    frames = {"bar": (bar, BAR_U, BAR_N, zb), "porch": (porch, PORCH_U, PORCH_N, zm)}

    def prop(model, frame, a, b, f, up, boxes):
        to, u, n, z = frames[frame]
        e, nn = {"+t": u, "-t": (-u[0], -u[1]), "+w": n, "-w": (-n[0], -n[1])}[f]
        return {"model": f"office/{model}", "pos": at(to(a, b), z + F + up), "yaw": bearing_deg(e, nn), "boxes": [list(x) for x in boxes]}

    return {
        "doorway": {
            "pos": at(porch((DOOR_G[0] + DOOR_G[1]) / 2, SETBACK + T / 2), zm + F),
            "yaw": bearing_deg(-PORCH_N[0], -PORCH_N[1]), "width": round(DOOR_G[1] - DOOR_G[0], 3), "height": DOOR_H,
        },
        "volume": [
            room("entry", [porch(0.0, SETBACK), ENTRY_Y, ENTRY_X, V4], zm),
            room("office", [bar(l3, 0), bar(BAR_L, 0), bar(BAR_L, BAR_W), bar(l3, BAR_W)], zb),
            room("mayor", [bar(0, 0), bar(l3, 0), bar(l3, BAR_W), bar(0, BAR_W)], zb),
        ],
        "workers": [
            {"role": role, "pos": at(bar(t, w), zb + F + SEAT_H), "yaw": bearing_deg(*facing[f]), "female": female}
            for role, t, w, f, female, _ in WORKERS
        ],
        "atlas": "glb/atlas/simple_office.png",
        "props": [prop(*p) for p in PROPS],
        "glass_prefix": f"window_{SW_BAR}_",  # the panes that stand in real openings: the game draws them clear
        "table": {"pos": at(bar(mt, mw), zb + F), "yaw": bearing_deg(*BAR_U)},
        "cctv": {"pos": at(bar(*CCTV), zb + F), "yaw": bearing_deg(-BAR_N[0], -BAR_N[1])},
    }
