"""The tiny blocky meshes of the ground clutter (``clutter_meshes.glb``, one node per kind of ``clutter.json``).

Each mesh is 3 to 27 triangles with flat colours (a few shades of the kind's palette across its faces, so one
mesh already looks varied; an instance varies only in turn and scale), at scale 1 in metres, standing on z = 0
with its middle on the origin, wound counter-clockwise seen from outside like every other mesh of the
pipeline. Built in map axes (x east, y north, z up); the export turns them into glTF axes (Y up) like the
chunk files. Ground-lying things (a leaf, a pebble) are closed or two-sided, so they show from any side.
"""

import math
import random

import numpy as np

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.propmeshes import box_tris

KINDS = ("tuft", "flower", "pebble", "rock", "leaf", "stick", "fern")
# Kinds whose instances carry a tint byte (see meadow.py): the grasses and ferns, whose green is the point. Flowers keep their
# own colours; stones and litter are varied by their faces.
TINTABLE = frozenset({"tuft", "fern"})
# The loader multiplies the vertex colours by these (ClutterData.cs, ClutterTint.Rgb): 0 = fresh, 128 = as drawn, 255 = dry.
# A multiplication cannot add yellow to a green, only take green and blue away, so "dry" reads as olive; real straw is
# its own kind.
_FRESH, _DRY = (0.80, 0.97, 0.78), (1.0, 0.80, 0.55)


def tint_rgb(t: int) -> tuple[float, float, float]:
    """The colour factor of a tint byte (piecewise linear: fresh at 0, white at 128, dry at 255)."""
    t = max(0, min(255, int(t)))
    end, w = (_FRESH, (128 - t) / 128.0) if t < 128 else (_DRY, (t - 128) / 127.0)
    return tuple(1.0 + (end[i] - 1.0) * w for i in range(3))


class _Tris:
    def __init__(self) -> None:
        self.tris: list[list[tuple[float, float, float]]] = []
        self.cols: list[list[int]] = []

    def add(self, a, b, c, colour: str) -> None:
        self.tris.append([tuple(a), tuple(b), tuple(c)])
        self.cols.append(hex_to_rgb(colour))

    def blade(self, cx: float, cy: float, half: float, turn: float, lean: float, height: float, colour: str) -> None:
        """A thin tetrahedron without its base: three side faces from a small triangle on the ground to an apex
        ``height`` up and ``lean`` metres out towards ``turn``."""
        base = [
            (cx + half * math.cos(turn + math.pi / 2 + k * math.tau / 3), cy + half * math.sin(turn + math.pi / 2 + k * math.tau / 3), 0.0)
            for k in range(3)
        ]
        apex = (cx + lean * math.cos(turn), cy + lean * math.sin(turn), height)
        for k in range(3):
            self.add(base[k], base[(k + 1) % 3], apex, colour)

    def mesh(self) -> Mesh:
        return Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8))


def tuft() -> Mesh:
    """A clump of grass: five leaning blades in three greens."""
    t = _Tris()
    greens = ["#5c9a3e", "#78b04a", "#4a8535"]
    for i, (r, turn, lean, h) in enumerate([(0.0, 0.3, 0.07, 0.30), (0.04, 1.6, 0.09, 0.22), (0.05, 2.9, 0.08, 0.27), (0.04, 4.1, 0.1, 0.2), (0.04, 5.2, 0.06, 0.25)]):
        t.blade(r * math.cos(turn + 1), r * math.sin(turn + 1), 0.032, turn, lean, h, greens[i % 3])
    return t.mesh()


def fern() -> Mesh:
    """Six wide fronds arching out of a point, in dark greens."""
    t = _Tris()
    greens = ["#3d7a3a", "#2f6a34", "#4a8a40"]
    for i in range(6):
        t.blade(0.0, 0.0, 0.07, i * math.tau / 6 + 0.2, 0.34 + 0.06 * (i % 2), 0.2 + 0.05 * (i % 3), greens[i % 3])
    return t.mesh()


def flower() -> Mesh:
    """Three stems with a pink, a white and a yellow head."""
    t = _Tris()
    for (x, y, h), head in zip([(-0.07, 0.02, 0.26), (0.06, 0.05, 0.22), (0.0, -0.07, 0.3)], ["#e0789a", "#f4f1e4", "#f0c93c"], strict=True):
        t.blade(x, y, 0.012, 0.0, 0.0, h - 0.02, "#4f8f3e")
        s = 0.045
        ring = [(x - s, y - s, h - 0.02), (x + s, y - s, h - 0.02), (x + s, y + s, h - 0.02), (x - s, y + s, h - 0.02)]
        apex = (x, y, h + 0.035)
        for k in range(4):
            t.add(ring[k], ring[(k + 1) % 4], apex, head)
        t.add(ring[0], ring[2], ring[1], head)
        t.add(ring[0], ring[3], ring[2], head)
    return t.mesh()


def pebble() -> Mesh:
    """A flattened octahedron, 8 triangles, in two greys."""
    t = _Tris()
    z = 0.02
    ring = [(0.06, 0.0, z), (0.0, 0.045, z), (-0.06, 0.0, z), (0.0, -0.045, z)]
    top, bottom = (0.004, 0.0, 0.045), (0.0, 0.0, 0.0)
    cols = ["#9a978e", "#7f7c75", "#b3ad9c", "#8a877f"]
    for k in range(4):
        t.add(ring[k], ring[(k + 1) % 4], top, cols[k])
        t.add(ring[(k + 1) % 4], ring[k], bottom, cols[(k + 2) % 4])
    return t.mesh()


def rock() -> Mesh:
    """A chunky stone: a lower and an upper ring of six, a peak, no bottom; 18 triangles in three greys."""
    t = _Tris()
    rng = random.Random(7)
    n = 6
    low = [(0.17 * (0.85 + 0.3 * rng.random()) * math.cos(k * math.tau / n), 0.17 * (0.85 + 0.3 * rng.random()) * math.sin(k * math.tau / n), 0.0) for k in range(n)]
    up = [(0.21 * (0.8 + 0.4 * rng.random()) * math.cos((k + 0.3) * math.tau / n), 0.21 * (0.8 + 0.4 * rng.random()) * math.sin((k + 0.3) * math.tau / n), 0.12 + 0.04 * rng.random()) for k in range(n)]
    peak = (0.02, -0.01, 0.23)
    cols = ["#6f6d69", "#85817a", "#5f5d5a"]
    for k in range(n):
        a, b, c, d = low[k], low[(k + 1) % n], up[(k + 1) % n], up[k]
        t.add(a, b, c, cols[k % 3])
        t.add(a, c, d, cols[(k + 1) % 3])
        t.add(d, c, peak, cols[(k + 2) % 3])
    return t.mesh()


def leaf() -> Mesh:
    """A fallen leaf: a flat diamond just above the ground, drawn on both sides (4 triangles)."""
    t = _Tris()
    z = 0.008
    a, b, c, d = (0.075, 0.0, z), (0.0, 0.04, z), (-0.075, 0.0, z), (0.0, -0.04, z)
    t.add(a, b, c, "#b4642e")
    t.add(a, c, d, "#c98a3a")
    t.add(a, c, b, "#9a5528")
    t.add(a, d, c, "#b87a34")
    return t.mesh()


def stick() -> Mesh:
    """A twig: a thin box 0.5 m long, in two browns (12 triangles)."""
    t = box_tris((-0.25, 0.0), (0.25, 0.0), 0.025, 0.0, 0.025)
    cols = [hex_to_rgb("#6e4f33" if i % 2 else "#85623f") for i in range(len(t))]
    return Mesh(np.array(t, float), np.array(cols, np.uint8))


BUILDERS = {"tuft": tuft, "flower": flower, "pebble": pebble, "rock": rock, "leaf": leaf, "stick": stick, "fern": fern}


def clutter_meshes() -> dict[str, Mesh]:
    """The mesh of every kind of ``clutter.json``, in map axes (x east, y north, z up)."""
    assert set(BUILDERS) == set(KINDS)
    return {k: BUILDERS[k]() for k in KINDS}
