"""Flat-colour primitive meshes for assets that no pack covers (``prim:<name>`` asset ids).

Builders return a :class:`Mesh` in local metres (x east, y north, z up) standing on z = 0 and
centred on the origin in plan, wound counter-clockwise seen from outside, like every other mesh
in the pipeline. ``PRIMITIVES`` maps the name to its builder, default parameters and default
size. ``size_m`` there is ``[x, y, z]`` in glTF axes (y up, the convention of ``assets/manifest.json``):
a plan footprint of x by z and a height y. Output is deterministic: same parameters, same mesh.

The utility poles (``pole_*``, built by ``poles.py``) are the exception to "centred": their origin is
the foot of the column, wherever the lamp arm or a cabinet reaches, and the column goes half a metre
into the ground. Their ``size_m`` is the box centred on the origin that holds them (``centred`` is
False). The same goes for the house connectors (``connector_*``, ``connectors.py``): their origin is
on the wall or roof they sit on. The flood wall pieces (``floodwall_*``, ``floodwall.py``) have their
origin on the ground under the centre line of the upper wall and go a metre into the ground.
"""

import math
from collections.abc import Callable
from dataclasses import dataclass

import numpy as np

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.connectors import CONNECTORS, build_connector
from suchohrad_pipeline.floodwall import PIECES as FLOODWALL_PIECES
from suchohrad_pipeline.floodwall import build_piece
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.poles import VARIANTS as POLE_VARIANTS
from suchohrad_pipeline.poles import build_variant
from suchohrad_pipeline.propmeshes import _mesh, box_tris


def _box(length: float, thickness: float, height: float, colour: str) -> Mesh:
    """Closed box, ``length`` along x, ``thickness`` along y, standing on z = 0."""
    tris = box_tris((-length / 2, 0.0), (length / 2, 0.0), thickness, 0.0, height)
    return _mesh(tris, [hex_to_rgb(colour)] * len(tris))


def wall_mesh(params: dict | None = None) -> Mesh:
    """Garden or retaining wall: a closed box, 3 m long, 0.2 m thick, 1.2 m high by default."""
    p = {**PRIMITIVES["wall"].params, **(params or {})}
    return _box(float(p["length_m"]), float(p["thickness_m"]), float(p["height_m"]), p["colour"])


def slab_mesh(params: dict | None = None) -> Mesh:
    """Pavement slab: a closed 1 m x 1 m x 0.1 m box in light grey by default."""
    p = {**PRIMITIVES["slab"].params, **(params or {})}
    return _box(float(p["length_m"]), float(p["width_m"]), float(p["height_m"]), p["colour"])


def pot_mesh(params: dict | None = None) -> Mesh:
    """Flower pot: a tapered 8-sided cylinder with a rim, an inner wall and a coloured soil top."""
    p = {**PRIMITIVES["pot"].params, **(params or {})}
    sides = max(3, int(p["sides"]))
    h = float(p["height_m"])
    r_bot, r_top = float(p["bottom_radius_m"]), float(p["top_radius_m"])
    r_in = r_top * float(p["rim_ratio"])
    z_soil = h - float(p["soil_depth_m"])
    pot, soil = hex_to_rgb(p["colour"]), hex_to_rgb(p["soil_colour"])

    def ring(r: float, z: float) -> list[tuple[float, float, float]]:
        return [
            (r * math.cos(2 * math.pi * k / sides), r * math.sin(2 * math.pi * k / sides), z)
            for k in range(sides)
        ]

    bot, top, rim, floor = ring(r_bot, 0.0), ring(r_top, h), ring(r_in, h), ring(r_in, z_soil)
    tris, cols = [], []

    def add(tri: list, colour: list[int]) -> None:
        tris.append(tri)
        cols.append(colour)

    for k in range(sides):
        n = (k + 1) % sides
        add([bot[k], bot[n], top[n]], pot)  # outer wall
        add([bot[k], top[n], top[k]], pot)
        add([top[k], top[n], rim[n]], pot)  # rim, facing up
        add([top[k], rim[n], rim[k]], pot)
        add([rim[k], rim[n], floor[n]], pot)  # inner wall, facing the axis
        add([rim[k], floor[n], floor[k]], pot)
        add([(0.0, 0.0, 0.0), bot[n], bot[k]], pot)  # underside
        add([(0.0, 0.0, z_soil), floor[k], floor[n]], soil)  # soil
    return _mesh(tris, cols)


def _beam(p0, p1, width: float, colour: list[int]) -> tuple[list, list]:
    """Closed square-section beam from p0 to p1 (any direction), ``width`` across."""
    p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
    d = p1 - p0
    d /= max(float(np.linalg.norm(d)), 1e-12)
    helper = np.array([1.0, 0, 0]) if abs(d[0]) < 0.9 else np.array([0, 1.0, 0])
    u = np.cross(d, helper)
    u /= np.linalg.norm(u)
    v = np.cross(d, u)
    h = width / 2
    offs = [-u * h - v * h, u * h - v * h, u * h + v * h, -u * h + v * h]
    ring0, ring1 = [p0 + o for o in offs], [p1 + o for o in offs]
    centre = (p0 + p1) / 2
    quads = [(ring0[k], ring0[(k + 1) % 4], ring1[(k + 1) % 4], ring1[k]) for k in range(4)]
    quads += [tuple(ring0), tuple(ring1)]
    tris = []
    for a, b, c, e in quads:
        n = np.cross(b - a, c - a)
        if n @ ((a + b + c + e) / 4 - centre) < 0:
            a, b, c, e = e, c, b, a  # flip to face outward
        tris += [[tuple(a), tuple(b), tuple(c)], [tuple(a), tuple(c), tuple(e)]]
    return tris, [colour] * len(tris)


def radio_tower_mesh(params: dict | None = None) -> Mesh:
    """Cell-phone lattice mast on an equipment cabin: square tapering tower with cross-bracing,
    a top platform with a ring of panel antennas, a lower tier of panels and a mast pole.
    Symmetric about the origin; 36 m high by default."""
    p = {**PRIMITIVES["radio_tower"].params, **(params or {})}
    H = float(p["height_m"])
    steel, cabin = hex_to_rgb(p["colour"]), hex_to_rgb(p["cabin_colour"])
    panel, deck = hex_to_rgb(p["panel_colour"]), hex_to_rgb(p["platform_colour"])
    cab_x, cab_y, cab_h = float(p["cabin_x_m"]), float(p["cabin_y_m"]), float(p["cabin_height_m"])
    h0, h1 = float(p["base_half_m"]), float(p["top_half_m"])
    z0, zt = cab_h, H - 2.2  # tower from the cabin roof to the platform
    tris: list = []
    cols: list = []

    def add(t: list, colour: list[int]) -> None:
        tris.extend(t)
        cols.extend([colour] * len(t))

    def half(z: float) -> float:
        return h0 + (h1 - h0) * (z - z0) / (zt - z0)

    def corner(k: int, z: float) -> tuple[float, float, float]:
        sx, sy = ((1, 1), (-1, 1), (-1, -1), (1, -1))[k % 4]
        return (sx * half(z), sy * half(z), z)

    add(box_tris((-cab_x / 2, 0.0), (cab_x / 2, 0.0), cab_y, 0.0, cab_h), cabin)
    n = max(2, round((zt - z0) / float(p["section_m"])))
    zs = [z0 + (zt - z0) * i / n for i in range(n + 1)]
    for i in range(n):
        for k in range(4):
            t, c = _beam(corner(k, zs[i]), corner(k, zs[i + 1]), 0.12, steel)
            tris += t
            cols += c
            for a, b in ((k, k + 1), (k + 1, k)):  # X bracing on face k
                t, c = _beam(corner(a, zs[i]), corner(b, zs[i + 1]), 0.05, steel)
                tris += t
                cols += c
    for z in zs[1:]:  # horizontal rings
        for k in range(4):
            t, c = _beam(corner(k, z), corner(k + 1, z), 0.06, steel)
            tris += t
            cols += c
    add(box_tris((-1.0, 0.0), (1.0, 0.0), 2.0, zt, zt + 0.12), deck)  # platform
    add(box_tris((0.0, -0.06), (0.0, 0.06), 0.12, zt + 0.12, H), steel)  # mast pole
    for k in range(8):  # ring of panel antennas on the platform
        a = math.pi * k / 4 + math.pi / 8
        c = (1.0 * math.cos(a), 1.0 * math.sin(a))
        d = (-math.sin(a) * 0.15, math.cos(a) * 0.15)
        add(
            box_tris(
                (c[0] - d[0], c[1] - d[1]), (c[0] + d[0], c[1] + d[1]), 0.12, zt + 0.12, zt + 1.8
            ),
            panel,
        )
    zl = zt - 5.0  # lower tier: two panels on each face
    for k, (nx, ny) in enumerate(((1, 0), (0, 1), (-1, 0), (0, -1))):
        r = half(zl) + 0.2
        for s in (-0.3, 0.3):
            cx, cy = nx * r - ny * s, ny * r + nx * s
            add(
                box_tris(
                    (cx + ny * 0.15, cy - nx * 0.15),
                    (cx - ny * 0.15, cy + nx * 0.15),
                    0.12,
                    zl,
                    zl + 1.5,
                ),
                panel,
            )
    return _mesh(tris, cols)


def bunker_prim_mesh(params: dict | None = None) -> Mesh:
    """WW2 pillbox: rounded concrete dome on a chamfered rectangle, door facing -y (the object's
    front), firing slits, weathering streaks. Long axis along x."""
    from suchohrad_pipeline.bunker import bunker_mesh

    p = {**PRIMITIVES["bunker"].params, **(params or {})}
    hl, hw, ch = float(p["length_m"]) / 2, float(p["width_m"]) / 2, float(p["chamfer_m"])
    ring = [  # counter-clockwise, corners cut by ``ch``
        (-hl + ch, -hw), (hl - ch, -hw), (hl, -hw + ch), (hl, hw - ch),
        (hl - ch, hw), (-hl + ch, hw), (-hl, hw - ch), (-hl, -hw + ch),
    ]
    return bunker_mesh(ring, (0.0, -1.0))


@dataclass(frozen=True)
class Primitive:
    builder: Callable[[dict | None], Mesh]
    params: dict  # default parameters; the builder accepts overrides for any key
    size_m: tuple[float, float, float]  # default bounding box [x, y (up), z], glTF axes
    colour: str  # default colour, hex
    centred: bool = True  # False: origin at the foot, not the middle of the plan (utility poles)


PRIMITIVES: dict[str, Primitive] = {
    "wall": Primitive(
        wall_mesh,
        {"length_m": 3.0, "thickness_m": 0.2, "height_m": 1.2, "colour": "#b8b0a0"},
        (3.0, 1.2, 0.2),
        "#b8b0a0",
    ),
    "slab": Primitive(
        slab_mesh,
        {"length_m": 1.0, "width_m": 1.0, "height_m": 0.1, "colour": "#c8c8c8"},
        (1.0, 0.1, 1.0),
        "#c8c8c8",
    ),
    "pot": Primitive(
        pot_mesh,
        {
            "sides": 8,
            "height_m": 0.35,
            "bottom_radius_m": 0.14,
            "top_radius_m": 0.2,
            "rim_ratio": 0.85,
            "soil_depth_m": 0.04,
            "colour": "#b5623c",
            "soil_colour": "#4a3322",
        },
        (0.4, 0.35, 0.4),
        "#b5623c",
    ),
    "bunker": Primitive(
        bunker_prim_mesh,
        {"length_m": 6.5, "width_m": 4.2, "chamfer_m": 1.0},
        (6.6, 2.67727, 4.3),
        "#a9a698",
    ),
    "radio_tower": Primitive(
        radio_tower_mesh,
        {
            "height_m": 36.0,
            "cabin_x_m": 3.0,
            "cabin_y_m": 2.6,
            "cabin_height_m": 2.5,
            "base_half_m": 0.8,
            "top_half_m": 0.3,
            "section_m": 2.6,
            "colour": "#b9bdc2",
            "cabin_colour": "#e6e6e0",
            "platform_colour": "#8a8f95",
            "panel_colour": "#f0f0ee",
        },
        (3.0, 36.0, 2.6),
        "#b9bdc2",
    ),
}


def _pole_primitive(name: str) -> Primitive:
    archetype, seed = POLE_VARIANTS[name]
    pole = build_variant(name)
    return Primitive(
        lambda params=None: build_variant(name, params).mesh,
        {"archetype": archetype, "seed": seed},
        pole.size_m,
        pole.spec.column.colour,
        centred=False,
    )


PRIMITIVES.update({name: _pole_primitive(name) for name in POLE_VARIANTS})


def _connector_primitive(name: str) -> Primitive:
    connector = build_connector(name)
    return Primitive(
        lambda params=None: build_connector(name).mesh, {}, connector.size_m, connector.colour, centred=False
    )


PRIMITIVES.update({name: _connector_primitive(name) for name in CONNECTORS})


def build_primitive(name: str, params: dict | None = None) -> Mesh:
def _floodwall_primitive(name: str) -> Primitive:
    piece = build_piece(name)
    return Primitive(lambda params=None: build_piece(name).mesh, {}, piece.size_m, piece.colour, centred=False)


PRIMITIVES.update({name: _floodwall_primitive(name) for name in FLOODWALL_PIECES})


    """Mesh of ``prim:<name>`` with optional parameter overrides; KeyError for an unknown name."""
    return PRIMITIVES[name].builder(params)
