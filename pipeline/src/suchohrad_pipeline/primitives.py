"""Flat-colour primitive meshes for assets that no pack covers (``prim:<name>`` asset ids).

Builders return a :class:`Mesh` in local metres (x east, y north, z up) standing on z = 0 and
centred on the origin in plan, wound counter-clockwise seen from outside, like every other mesh
in the pipeline. ``PRIMITIVES`` maps the name to its builder, default parameters and default
size. ``size_m`` there is ``[x, y, z]`` in glTF axes (y up, the convention of ``assets/manifest.json``):
a plan footprint of x by z and a height y. Output is deterministic: same parameters, same mesh.
"""

import math
from collections.abc import Callable
from dataclasses import dataclass

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh
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


@dataclass(frozen=True)
class Primitive:
    builder: Callable[[dict | None], Mesh]
    params: dict  # default parameters; the builder accepts overrides for any key
    size_m: tuple[float, float, float]  # default bounding box [x, y (up), z], glTF axes
    colour: str  # default colour, hex


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
}


def build_primitive(name: str, params: dict | None = None) -> Mesh:
    """Mesh of ``prim:<name>`` with optional parameter overrides; KeyError for an unknown name."""
    return PRIMITIVES[name].builder(params)
