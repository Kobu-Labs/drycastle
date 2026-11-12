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

# The grasses (and ferns), the wild flowers of meadows and verges, the garden flowers of yards. "flower" is the first mixed
# flower mesh, kept for maps and tests that name it; the recipes use the species.
GRASSES = ("tuft", "tall_grass", "straw", "clover", "reed", "weed")
WILD_FLOWERS = ("dandelion", "daisy", "red_clover", "buttercup", "poppy", "cornflower", "yarrow")
GARDEN_FLOWERS = ("tulip", "sunflower", "spike", "rose")
KINDS = ("tuft", "flower", "pebble", "rock", "leaf", "stick", "fern", *GRASSES[1:], *WILD_FLOWERS, *GARDEN_FLOWERS)
# Kinds whose instances carry a tint byte (see meadow.py): the grasses and ferns, whose green is the point. Flowers keep their
# own colours; stones and litter are varied by their faces.
TINTABLE = frozenset({*GRASSES, "fern"})
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

    def spike(self, cx: float, cy: float, z0: float, half: float, turn: float, lean: float, height: float, colour: str) -> None:
        """Like :meth:`blade` but starting ``z0`` up: a seed head on a stalk."""
        base = [
            (cx + half * math.cos(turn + math.pi / 2 + k * math.tau / 3), cy + half * math.sin(turn + math.pi / 2 + k * math.tau / 3), z0)
            for k in range(3)
        ]
        apex = (cx + lean * math.cos(turn), cy + lean * math.sin(turn), z0 + height)
        for k in range(3):
            self.add(base[k], base[(k + 1) % 3], apex, colour)

    def pyramid(self, cx: float, cy: float, z0: float, half: float, z1: float, colour: str, base: bool = True) -> None:
        """A square pyramid (4 triangles, 6 with its base): a flower head or a bud."""
        ring = [(cx - half, cy - half, z0), (cx + half, cy - half, z0), (cx + half, cy + half, z0), (cx - half, cy + half, z0)]
        apex = (cx, cy, z1)
        for k in range(4):
            self.add(ring[k], ring[(k + 1) % 4], apex, colour)
        if base:
            self.add(ring[0], ring[2], ring[1], colour)
            self.add(ring[0], ring[3], ring[2], colour)

    def ball(self, cx: float, cy: float, z: float, r: float, colour: str) -> None:
        """An octahedron (8 triangles): a round head (a clover, a puffball)."""
        e, w, n, so, up, dn = (cx + r, cy, z), (cx - r, cy, z), (cx, cy + r, z), (cx, cy - r, z), (cx, cy, z + r), (cx, cy, z - r)
        for a, b in ((e, n), (n, w), (w, so), (so, e)):
            self.add(a, b, up, colour)
            self.add(b, a, dn, colour)

    def disc(self, cx: float, cy: float, z: float, r: float, sides: int, colour: str, both: bool = True, tilt: float = 0.0) -> None:
        """A flat regular polygon fan, facing up (and down when ``both``); ``tilt`` raises the +x edge."""
        pts = [(cx + r * math.cos(k * math.tau / sides), cy + r * math.sin(k * math.tau / sides), z + tilt * math.cos(k * math.tau / sides)) for k in range(sides)]
        for k in range(sides):
            self.add((cx, cy, z), pts[k], pts[(k + 1) % sides], colour)
            if both:
                self.add((cx, cy, z), pts[(k + 1) % sides], pts[k], colour)

    def leaf_blade(self, cx: float, cy: float, turn: float, length: float, width: float, rise: float, colour: str) -> None:
        """A flat two-sided leaf from (cx, cy) outward to ``turn``, ``rise`` metres higher at the tip (2 triangles each side)."""
        c, sn = math.cos(turn), math.sin(turn)
        mid = (cx + c * length * 0.45, cy + sn * length * 0.45, rise * 0.4)
        tip = (cx + c * length, cy + sn * length, rise)
        a = (mid[0] - sn * width, mid[1] + c * width, mid[2])
        b = (mid[0] + sn * width, mid[1] - c * width, mid[2])
        root = (cx, cy, 0.0)
        for tri in ((root, b, a), (a, b, tip)):
            self.add(*tri, colour)
            self.add(tri[0], tri[2], tri[1], colour)

    def mesh(self) -> Mesh:
        return Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8))

    def stem(self, x: float, y: float, h: float, colour: str = "#4f8f3e") -> None:
        self.blade(x, y, 0.012, 0.0, 0.0, h - 0.02, colour)


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




# --- grasses ----------------------------------------------------------------------------------


def tall_grass() -> Mesh:
    """Tall meadow grass: five leaning blades to half a metre with two tan seed heads (21 triangles)."""
    t = _Tris()
    greens = ["#6aa845", "#7fb84f", "#548f3a"]
    for i, (r, turn, lean, h) in enumerate([(0.0, 0.4, 0.10, 0.62), (0.05, 1.7, 0.14, 0.50), (0.05, 3.0, 0.12, 0.58), (0.04, 4.2, 0.13, 0.46), (0.05, 5.3, 0.09, 0.54)]):
        t.blade(r * math.cos(turn + 1), r * math.sin(turn + 1), 0.03, turn, lean, h, greens[i % 3])
    for turn, lean, h, (x, y) in ((0.4, 0.10, 0.62, (0.0, 0.0)), (3.0, 0.12, 0.58, (0.05 * math.cos(4.0), 0.05 * math.sin(4.0)))):
        t.spike(x + lean * math.cos(turn) * 0.9, y + lean * math.sin(turn) * 0.9, h * 0.8, 0.022, turn, lean * 0.15, 0.14, "#c9b36b")
    return t.mesh()


def straw() -> Mesh:
    """A dry tuft: six stiff blades in straw yellows, fanning out (18 triangles)."""
    t = _Tris()
    cols = ["#c9b26a", "#b89a52", "#d8c27e"]
    for i in range(6):
        turn = i * math.tau / 6 + 0.3
        t.blade(0.03 * math.cos(turn), 0.03 * math.sin(turn), 0.028, turn, 0.10 + 0.04 * (i % 2), 0.30 + 0.06 * (i % 3), cols[i % 3])
    return t.mesh()


def clover() -> Mesh:
    """A flat mat of seven trefoil leaves lying just off the ground, light greens, two-sided (14 triangles)."""
    t = _Tris()
    cols = ["#74b050", "#8cc460", "#68a548"]
    for i, (x, y, turn) in enumerate([(0.0, 0.0, 0.2), (0.09, 0.04, 1.4), (-0.08, 0.06, 2.5), (-0.05, -0.08, 3.6), (0.07, -0.08, 4.7), (0.14, -0.02, 5.6), (-0.13, -0.01, 0.9)]):
        r = 0.045
        z = 0.02 + 0.006 * (i % 3)
        a = [(x + r * math.cos(turn + k * math.tau / 3), y + r * math.sin(turn + k * math.tau / 3), z) for k in range(3)]
        t.add(a[0], a[1], a[2], cols[i % 3])
        t.add(a[0], a[2], a[1], cols[(i + 1) % 3])
    return t.mesh()


def reed() -> Mesh:
    """Reed or sedge: six narrow near-vertical blades to a metre and two brown flower heads (24 triangles)."""
    t = _Tris()
    cols = ["#5f9a46", "#4d8a3c", "#76ad54"]
    for i, (turn, lean, h) in enumerate([(0.3, 0.05, 1.05), (1.4, 0.08, 0.85), (2.6, 0.06, 1.15), (3.7, 0.09, 0.8), (4.8, 0.07, 0.95), (5.7, 0.05, 1.0)]):
        t.blade(0.05 * math.cos(turn), 0.05 * math.sin(turn), 0.022, turn, lean, h, cols[i % 3])
    for turn, lean, h in ((0.3, 0.05, 1.05), (2.6, 0.06, 1.15)):
        x, y = 0.05 * math.cos(turn) + lean * math.cos(turn) * 0.85, 0.05 * math.sin(turn) + lean * math.sin(turn) * 0.85
        t.spike(x, y, h * 0.82, 0.03, turn, lean * 0.1, 0.2, "#7a5a36")
    return t.mesh()


def weed() -> Mesh:
    """A weedy clump: five broad leaves sprawling out, blue-greens (20 triangles)."""
    t = _Tris()
    cols = ["#4f8f48", "#3f7d46", "#62a052", "#468a4e", "#59974c"]
    for i in range(5):
        t.leaf_blade(0.0, 0.0, i * math.tau / 5 + 0.4, 0.26 + 0.05 * (i % 2), 0.075, 0.13 + 0.05 * (i % 3), cols[i])
    return t.mesh()


# --- wild flowers ---------------------------------------------------------------------------


def dandelion() -> Mesh:
    """A yellow dandelion head, a white puffball on its own stalk and two low leaves (25 triangles)."""
    t = _Tris()
    t.stem(-0.05, 0.0, 0.2)
    t.pyramid(-0.05, 0.0, 0.18, 0.045, 0.215, "#f2cf2a")
    t.stem(0.06, 0.03, 0.3)
    t.ball(0.06, 0.03, 0.31, 0.04, "#ecebe2")
    for turn, col in ((0.8, "#4b8a3a"), (3.9, "#3f7a35")):
        t.leaf_blade(0.0, 0.0, turn, 0.15, 0.03, 0.05, col)
    return t.mesh()


def daisy() -> Mesh:
    """Two daisies, white petals round a yellow centre (22 triangles with stems)."""
    t = _Tris()
    for x, y, h in ((-0.05, 0.0, 0.22), (0.05, 0.04, 0.17)):
        t.stem(x, y, h)
        t.pyramid(x, y, h - 0.015, 0.05, h + 0.005, "#f6f4ec")
        t.pyramid(x, y, h, 0.018, h + 0.022, "#efc233", base=False)
    return t.mesh()


def red_clover() -> Mesh:
    """Two red-clover heads on stalks, magenta balls with a small leaf under each (24 triangles)."""
    t = _Tris()
    for x, y, h, col in ((-0.04, 0.0, 0.26, "#c4488c"), (0.05, 0.04, 0.2, "#d1609d")):
        t.stem(x, y, h)
        t.ball(x, y, h, 0.042, col)
    t.leaf_blade(0.0, -0.03, 4.2, 0.1, 0.03, 0.04, "#5c9a43")
    return t.mesh()


def buttercup() -> Mesh:
    """Three small bright-yellow cups on thin stems (27 triangles)."""
    t = _Tris()
    for x, y, h in ((-0.06, 0.02, 0.24), (0.05, 0.05, 0.19), (0.0, -0.06, 0.27)):
        t.stem(x, y, h)
        t.pyramid(x, y, h - 0.02, 0.038, h + 0.01, "#f4d313")
    return t.mesh()


def poppy() -> Mesh:
    """Two red poppies with dark centres (22 triangles)."""
    t = _Tris()
    for x, y, h in ((-0.05, 0.0, 0.3), (0.06, 0.05, 0.24)):
        t.stem(x, y, h)
        t.pyramid(x, y, h - 0.02, 0.06, h + 0.01, "#d6261f")
        t.pyramid(x, y, h + 0.005, 0.015, h + 0.02, "#2a1a1a", base=False)
    return t.mesh()


def cornflower() -> Mesh:
    """Three tall cornflowers, blue star heads on slim stems (27 triangles)."""
    t = _Tris()
    for x, y, h, col in ((-0.06, 0.02, 0.36, "#4a6fd0"), (0.05, 0.05, 0.3, "#5a7fe0"), (0.0, -0.06, 0.4, "#3f62c2")):
        t.stem(x, y, h, "#5a8f4a")
        t.pyramid(x, y, h - 0.02, 0.04, h + 0.022, col)
    return t.mesh()


def yarrow() -> Mesh:
    """Two yarrow umbels: flat white heads on tall stalks (30 triangles)."""
    t = _Tris()
    for x, y, h in ((-0.05, 0.0, 0.42), (0.06, 0.04, 0.34)):
        t.stem(x, y, h, "#5f9348")
        t.disc(x, y, h, 0.075, 6, "#f1efe6", both=True, tilt=0.01)
    return t.mesh()


# --- garden flowers -----------------------------------------------------------------------------


def tulip() -> Mesh:
    """Two tulips: a closed cup on a stem each, red and orange (30 triangles)."""
    t = _Tris()
    for x, y, h, col in ((-0.04, 0.0, 0.34, "#d8352f"), (0.05, 0.05, 0.28, "#e05a2a")):
        t.stem(x, y, h - 0.07, "#4a8a3e")
        ring0 = [(x - 0.03, y - 0.03, h - 0.07), (x + 0.03, y - 0.03, h - 0.07), (x + 0.03, y + 0.03, h - 0.07), (x - 0.03, y + 0.03, h - 0.07)]
        ring1 = [(x - 0.045, y - 0.045, h - 0.03), (x + 0.045, y - 0.045, h - 0.03), (x + 0.045, y + 0.045, h - 0.03), (x - 0.045, y + 0.045, h - 0.03)]
        apex = (x, y, h + 0.03)
        for k in range(4):
            t.add(ring0[k], ring0[(k + 1) % 4], ring1[(k + 1) % 4], col)
            t.add(ring0[k], ring1[(k + 1) % 4], ring1[k], col)
        # the cup: eight side triangles round the stem's top, then four to a point
        for k in range(4):
            t.add(ring1[k], ring1[(k + 1) % 4], apex, col)
    return t.mesh()


def sunflower() -> Mesh:
    """A sunflower: a tall stem, a dark seed disc ringed by yellow petals, tilted a little, and two leaves (29 triangles)."""
    t = _Tris()
    h = 0.92
    t.stem(0.0, 0.0, h, "#4c8a38")
    t.disc(0.0, 0.0, h, 0.06, 6, "#4a2e1a", both=True, tilt=0.03)
    r0, r1 = 0.06, 0.135
    for k in range(6):
        a0, a1, am = k * math.tau / 6, (k + 1) * math.tau / 6, (k + 0.5) * math.tau / 6
        pts = [(r * math.cos(a), r * math.sin(a)) for r, a in ((r0, a0), (r0, a1), (r1, am))]
        tri = [(x, y, h + 0.03 * math.cos(math.atan2(y, x)) * (math.hypot(x, y) / r0)) for x, y in pts]
        t.add(*tri, "#f0bc1c" if k % 2 else "#f7c929")
        t.add(tri[0], tri[2], tri[1], "#f0bc1c" if k % 2 else "#f7c929")
    t.add((0.0, 0.0, 0.34), (0.16, 0.05, 0.4), (0.06, 0.12, 0.38), "#3f7d32")
    t.add((0.0, 0.0, 0.5), (-0.14, -0.06, 0.55), (-0.04, -0.12, 0.54), "#3f7d32")
    return t.mesh()


def spike() -> Mesh:
    """A hollyhock or lupin: a tall stalk with five violet and pink blooms up its length, two low leaves (29 triangles)."""
    t = _Tris()
    t.stem(0.0, 0.0, 0.92, "#4f8f3e")
    for i, (z, side) in enumerate([(0.4, 1), (0.5, -1), (0.62, 1), (0.74, -1), (0.86, 1)]):
        t.pyramid(side * 0.032, 0.0, z, 0.034 - 0.002 * i, z + 0.075, "#7d5bc8" if i % 2 == 0 else "#a37ae6", base=False)
    for turn in (0.6, 3.7):
        t.blade(0.0, 0.0, 0.05, turn, 0.16, 0.14, "#3f7d36")
    return t.mesh()


def rose() -> Mesh:
    """A rose bush: a dark green dome with three red blooms (30 triangles)."""
    t = _Tris()
    n = 6
    ring0 = [(0.27 * math.cos(k * math.tau / n), 0.27 * math.sin(k * math.tau / n), 0.03) for k in range(n)]
    ring1 = [(0.2 * math.cos((k + 0.5) * math.tau / n), 0.2 * math.sin((k + 0.5) * math.tau / n), 0.3) for k in range(n)]
    apex = (0.0, 0.0, 0.42)
    greens = ["#356f3a", "#3f7c40", "#2f6636"]
    for k in range(n):
        t.add(ring0[k], ring0[(k + 1) % n], ring1[k], greens[k % 3])
        t.add(ring0[(k + 1) % n], ring1[(k + 1) % n], ring1[k], greens[(k + 1) % 3])
        t.add(ring1[k], ring1[(k + 1) % n], apex, greens[(k + 2) % 3])
    for x, y, z, col in ((0.13, 0.05, 0.31, "#d1243a"), (-0.1, 0.09, 0.36, "#e0405a"), (-0.02, -0.14, 0.28, "#c01f34")):
        t.pyramid(x, y, z, 0.04, z + 0.06, col, base=False)
    return t.mesh()


BUILDERS = {
    "tuft": tuft, "flower": flower, "pebble": pebble, "rock": rock, "leaf": leaf, "stick": stick, "fern": fern,
    "tall_grass": tall_grass, "straw": straw, "clover": clover, "reed": reed, "weed": weed,
    "dandelion": dandelion, "daisy": daisy, "red_clover": red_clover, "buttercup": buttercup, "poppy": poppy,
    "cornflower": cornflower, "yarrow": yarrow, "tulip": tulip, "sunflower": sunflower, "spike": spike, "rose": rose,
}


def clutter_meshes() -> dict[str, Mesh]:
    """The mesh of every kind of ``clutter.json``, in map axes (x east, y north, z up)."""
    assert set(BUILDERS) == set(KINDS)
    return {k: BUILDERS[k]() for k in KINDS}
