"""Flat-colour primitive meshes for assets that no pack covers (``prim:<name>`` asset ids).

Builders return a :class:`Mesh` in local metres (x east, y north, z up) standing on z = 0 and
centred on the origin in plan, wound counter-clockwise seen from outside, like every other mesh
in the pipeline. ``PRIMITIVES`` maps the name to its builder, default parameters and default
size. ``size_m`` there is ``[x, y, z]`` in glTF axes (y up, the convention of ``assets/manifest.json``):
a plan footprint of x by z and a height y. Output is deterministic: same parameters, same mesh.

The utility poles (``pole_*``, built by ``kits/poles.py``) are the exception to "centred": their origin is
the foot of the column, wherever the lamp arm or a cabinet reaches, and the column goes half a metre
into the ground. Their ``size_m`` is the box centred on the origin that holds them (``centred`` is
False). The same goes for the house connectors (``connector_*``, ``kits/connectors.py``): their origin is
on the wall or roof they sit on. The flood wall pieces (``floodwall_*``, ``kits/floodwall.py``) have their
origin on the ground under the centre line of the upper wall and go a metre into the ground.
"""

import math
from collections.abc import Callable
from dataclasses import dataclass

import numpy as np

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.kits import graves
from drycastle_pipeline.kits.connectors import CONNECTORS, build_connector
from drycastle_pipeline.kits.floodwall import PIECES as FLOODWALL_PIECES
from drycastle_pipeline.kits.floodwall import build_piece
from drycastle_pipeline.kits.poles import VARIANTS as POLE_VARIANTS
from drycastle_pipeline.kits.poles import build_variant
from drycastle_pipeline.props.propmeshes import _mesh, box_tris


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


def mound_tiers(params: dict | None = None) -> list[tuple[float, float]]:
    """Tiers of ``prim:mound`` from the bottom up as ``(half_size, top_z)``: nested centred squares, the
    plan shrinking evenly from ``base_m`` to ``top_m`` while the height climbs evenly to ``peak_m``, in as
    few tiers as keep every rise at or under ``step_rise_m`` (the player auto-steps 0.3 m)."""
    p = {**PRIMITIVES["mound"].params, **(params or {})}
    base, peak, top, rise = (float(p[k]) for k in ("base_m", "peak_m", "top_m", "step_rise_m"))
    if not (0 < top < base and peak > 0 and rise > 0):
        raise ValueError("mound needs 0 < top_m < base_m, peak_m > 0 and step_rise_m > 0")
    n = max(1, math.ceil(peak / rise - 1e-9))
    run = (base - top) / 2 / max(1, n - 1)  # plan inset per tier, per side
    return [(max(top, base) / 2 - i * run if n > 1 else top / 2, peak * (i + 1) / n) for i in range(n)]


def mound_solids(params: dict | None = None) -> list[dict]:
    """Collision boxes of ``prim:mound``, one per tier, each from the ground up (so the tiers nest), in the
    mesh frame (x, y plan, z up), as the ``solids`` of the asset manifest."""
    return [
        {"min": [round(-h, 3), round(-h, 3), 0.0], "max": [round(h, 3), round(h, 3), round(z, 3)], "surface": "dirt"}
        for h, z in mound_tiers(params)
    ]


def mound_mesh(params: dict | None = None) -> Mesh:
    """Terraced hill: tiers of closed boxes stacked on each other, every tier from the top of the one below
    to its own top, so the picture is what mound_solids lets you stand on. Odd tiers a little darker."""
    p = {**PRIMITIVES["mound"].params, **(params or {})}
    base = hex_to_rgb(p["colour"])
    dark = tuple(int(c * 0.9) for c in base)
    tris, colours, z0 = [], [], 0.0
    for i, (h, z1) in enumerate(mound_tiers(p)):
        box = box_tris((-h, 0.0), (h, 0.0), 2 * h, z0, z1)
        tris += box
        colours += [dark if i % 2 else base] * len(box)
        z0 = z1
    return _mesh(tris, colours)


def mound_entry() -> dict:
    """The ``assets/manifest.json`` record of ``prim:mound`` (solids from the default parameters)."""
    prim = PRIMITIVES["mound"]
    return {
        "id": "prim:mound",
        "category": "other",
        "name": "Trash mound (terraced, climbable hill)",
        "file": None,
        "size_m": list(prim.size_m),
        "tags": ["mound", "hill", "terrain", "flat-colour", "primitive"],
        "licence": "CC0",
        "source": "https://github.com/Kobu-Labs/drycastle (pipeline primitive, pipeline/src/drycastle_pipeline/kits/primitives.py)",
        "pack": None,
        # the game collides with these boxes, not one box: each tier rises <= 0.28 m so the player steps up
        "solids": mound_solids(),
    }


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


def cartel_contact_mesh(params: dict | None = None) -> Mesh:
    """The marker of the cartel's Contact (docs/cartel.md "Contact"): a plain standing figure on a square plate, facing the
    object's front (-y, as the bunker's door) with a gold arrow on the plate and a nose on the head, so the editor shows where
    he stands and which way he looks. The game hides this marker and draws the Contact's own character there
    (``godot/shooter/Cartel/ContactView.cs``, ``godot/shooter/data/contact.json``)."""
    p = {**PRIMITIVES["cartel_contact"].params, **(params or {})}
    plate, h = float(p["plate_m"]), float(p["height_m"])
    t = float(p["plate_thickness_m"])
    tris: list = []
    cols: list = []

    def add(p0, p1, width: float, z0: float, z1: float, colour: str) -> None:
        part = box_tris(p0, p1, width, z0, z1)
        tris.extend(part)
        cols.extend([hex_to_rgb(colour)] * len(part))

    half = plate / 2
    add((-half, 0.0), (half, 0.0), plate, 0.0, t, p["colour"])  # the plate: the figure's whole plan
    # the arrow: the way he faces
    add((0.0, -0.1), (0.0, -half + 0.04), 0.14, t, t + 0.01, p["arrow_colour"])
    leg_top, torso_top = h * 0.53, h * 0.84
    add((-0.18, 0.0), (0.18, 0.0), 0.22, t, leg_top, p["legs_colour"])
    add((-0.22, 0.0), (0.22, 0.0), 0.26, leg_top, torso_top, p["suit_colour"])
    add((-0.11, 0.0), (0.11, 0.0), 0.22, torso_top + 0.02, h, p["skin_colour"])
    nose_z = torso_top + 0.02 + (h - torso_top) * 0.4
    add((0.0, -0.11), (0.0, -0.17), 0.08, nose_z, nose_z + 0.06, p["skin_colour"])
    return _mesh(tris, cols)


def cartel_pallet_mesh(params: dict | None = None) -> Mesh:
    """The marker of the Cartel pallet (docs/cartel.md "Delivery"): a plain wooden pallet, three runners under five deck boards, with a gold
    plate on each of the four bag spots, so the editor shows where the pallet stands and where the bags lie. The footprint and height are the
    Gang Warfare pallet's (``SM_Prop_Pallet_01.glb``, read from the glb); the spots are ``pallet.spots`` of ``godot/shooter/data/cartel.json``
    (mirrored, the four lie symmetric). The game hides this marker and draws that pallet there (``godot/shooter/Cartel/PalletView.cs``); the
    marker itself is solid, a box of the footprint like any placed prop."""
    p = {**PRIMITIVES["cartel_pallet"].params, **(params or {})}
    length, width, h = float(p["length_m"]), float(p["width_m"]), float(p["height_m"])
    plate = float(p["spot_plate_m"])
    tris: list = []
    cols: list = []

    def add(p0, p1, w: float, z0: float, z1: float, colour: str) -> None:
        part = box_tris(p0, p1, w, z0, z1)
        tris.extend(part)
        cols.extend([hex_to_rgb(colour)] * len(part))

    deck_top, runner_top = h - plate, h * 0.5
    runner = 0.14  # the runners: along y, at both edges and the middle
    for x in (-(length / 2 - runner / 2), 0.0, length / 2 - runner / 2):
        add((x, -width / 2), (x, width / 2), runner, 0.0, runner_top, p["colour"])
    boards = 5  # the deck: five boards along x, with a gap between
    bw = 0.24
    gap = (width - boards * bw) / (boards - 1)
    for j in range(boards):
        y = -width / 2 + bw / 2 + j * (bw + gap)
        add((-length / 2, y), (length / 2, y), bw, runner_top, deck_top, p["colour"])
    for sx, sy in p["spots"]:  # the bag spots (x across the width, y along the depth, the plate a bag long by half a bag deep)
        add((sx - 0.35, sy), (sx + 0.35, sy), 0.35, deck_top, h, p["spot_colour"])
    return _mesh(tris, cols)


def bunker_prim_mesh(params: dict | None = None) -> Mesh:
    """WW2 pillbox: rounded concrete dome on a chamfered rectangle, door facing -y (the object's
    front), firing slits, weathering streaks. Long axis along x. The closed form: a placed object
    collides as one box, so nobody can go in (the bunkers of the map are buildings, see ``buildings/landmarks/bunker.py``)."""
    from drycastle_pipeline.buildings.landmarks.bunker import bunker_mesh

    p = {**PRIMITIVES["bunker"].params, **(params or {})}
    hl, hw, ch = float(p["length_m"]) / 2, float(p["width_m"]) / 2, float(p["chamfer_m"])
    ring = [  # counter-clockwise, corners cut by ``ch``
        (-hl + ch, -hw), (hl - ch, -hw), (hl, -hw + ch), (hl, hw - ch),
        (hl - ch, hw), (-hl + ch, hw), (-hl, hw - ch), (-hl, -hw + ch),
    ]
    return bunker_mesh(ring, (0.0, -1.0), enterable=False)


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
    "mound": Primitive(
        mound_mesh,
        {"base_m": 12.0, "peak_m": 4.0, "top_m": 1.0, "step_rise_m": 0.28, "colour": "#5a4a3a"},
        (12.0, 4.0, 12.0),
        "#5a4a3a",
    ),
    "bunker": Primitive(
        bunker_prim_mesh,
        {"length_m": 6.5, "width_m": 4.2, "chamfer_m": 1.0},
        (6.6, 2.67727, 4.3),
        "#a9a698",
    ),
    "cartel_contact": Primitive(
        cartel_contact_mesh,
        {
            "plate_m": 0.8,
            "plate_thickness_m": 0.03,
            "height_m": 1.8,
            "colour": "#3a3532",
            "arrow_colour": "#c9a227",
            "legs_colour": "#2b2b33",
            "suit_colour": "#e8e2d0",
            "skin_colour": "#c68e62",
        },
        (0.8, 1.8, 0.8),
        "#3a3532",
    ),
    "cartel_pallet": Primitive(
        cartel_pallet_mesh,
        {
            "length_m": 1.441,
            "width_m": 1.418,
            "height_m": 0.235,
            "spot_plate_m": 0.01,
            "spots": [(-0.36, -0.33), (0.36, -0.33), (-0.36, 0.33), (0.36, 0.33)],
            "colour": "#8a6a45",
            "spot_colour": "#c9a227",
        },
        (1.441, 0.235, 1.418),
        "#8a6a45",
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


def _floodwall_primitive(name: str) -> Primitive:
    piece = build_piece(name)
    return Primitive(lambda params=None: build_piece(name).mesh, {}, piece.size_m, piece.colour, centred=False)


PRIMITIVES.update({name: _floodwall_primitive(name) for name in FLOODWALL_PIECES})


PRIMITIVES["crucifix"] = Primitive(graves.crucifix_mesh, {}, tuple(graves.crucifix_entry()["size_m"]), graves.STONES["terrazzo"][0])
PRIMITIVES["grave"] = Primitive(graves.default_mesh, {}, tuple(graves.manifest_entry()["size_m"]), graves.STONES[graves.DEFAULT_SPEC.stone][0])


def build_primitive(name: str, params: dict | None = None) -> Mesh:
    """Mesh of ``prim:<name>`` with optional parameter overrides; KeyError for an unknown name."""
    return PRIMITIVES[name].builder(params)
