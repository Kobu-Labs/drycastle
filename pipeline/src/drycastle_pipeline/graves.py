"""Graves of the village graveyard, generated: one mesh per grave at its real size (#390).

A grave is assembled from parts inside its plan rectangle (``width`` along x, ``length`` along y, centred on the origin,
standing on z = 0, the head at +y, the writing facing -y):

* **base**: a kerb frame (optionally on a low step, with corner posts);
* **cover**: a stone slab with a raised panel, two slabs (a double grave), half a slab and a bed, gravel, soil or grass;
* **headstone** on a plinth at the head: upright (straight, arched, gabled, shouldered), a low desk, a stone cross, an old
  iron cross or an obelisk, with a plaque and lines of "writing" (too small to be text);
* **dressing**: lantern, candle, vase, flowers, wreath;
* **neglect** (0 = kept, 1 = abandoned): stained stone, moss, a cracked and sunken slab, a leaning or broken headstone,
  weeds, no dressing.

:class:`GraveSpec` says which parts; :func:`spec_for` picks one from a seed (the same seed, size and neglect give the same
grave), :func:`grave_mesh` builds it. Flat colours, wound counter-clockwise seen from outside, deterministic.
"""

import hashlib
import math
import random
from dataclasses import dataclass, field

import numpy as np

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.propmeshes import _outward, box_tris, frustum_tris, pyramid_tris

# stone: body, trim (kerbs, panels, plinths), writing
STONES: dict[str, tuple[str, str, str]] = {
    "terrazzo": ("#b9b7ae", "#a19f96", "#3b3b3b"),
    "black_granite": ("#34353a", "#26272b", "#c9a84a"),
    "grey_granite": ("#7d7f84", "#696b70", "#e4e2da"),
    "red_granite": ("#7c4b41", "#653a32", "#d2b25a"),
    "white_marble": ("#dedcd4", "#c7c5bd", "#3a3a3a"),
    "concrete": ("#a9a9a4", "#93938e", "#3f3f3d"),
    "sandstone": ("#aba189", "#968c75", "#4a4436"),
}
COVERS = ("slab", "double_slab", "half", "gravel", "soil", "grass")
HEADS = ("rect", "round", "gable", "shoulder", "desk", "cross", "iron_cross", "obelisk", "none")
DRESSINGS = ("lantern", "candle", "vase", "flowers", "wreath")

MOSS = ("#5f7a3a", "#6f8a45", "#4f6a34")
WEED = ("#5d8a3c", "#6c9a46", "#4d7a34", "#7a9a4a")
GRAVEL = ("#d6d3ca", "#c9c6bc")
SOIL = "#5f4a37"
GRASS = "#7f9a62"
IRON = "#2b2b2d"
PETALS = ("#c8323c", "#e6c63a", "#e8e4dc", "#b05ac0", "#e07a2a")
STAIN = "#5f6650"  # what neglected stone drifts towards


@dataclass(frozen=True)
class GraveSpec:
    stone: str = "terrazzo"
    cover: str = "slab"
    head: str = "rect"
    head_stone: str | None = None  # another stone for the headstone (None: the same)
    head_h: float = 0.85  # metres above its plinth
    kerb_h: float = 0.22
    kerb_t: float = 0.12
    step: bool = False  # the frame stands on a low wider step
    posts: bool = False  # little blocks on the kerb's corners
    plaque: str = "lines"  # "plate" (a contrasting plate with lines), "lines" (cut in the stone), "none"
    dressing: tuple[str, ...] = field(default=())
    neglect: float = 0.0


def _rng(seed: str) -> random.Random:
    return random.Random(int(hashlib.sha256(seed.encode()).hexdigest()[:12], 16))


def _mix(a: list[int], b: list[int], t: float) -> list[int]:
    return [round(x + (y - x) * t) for x, y in zip(a, b, strict=True)]


class _Parts:
    """Triangles with their colours, collected part by part."""

    def __init__(self) -> None:
        self.tris: list = []
        self.cols: list = []

    def add(self, tris: list, colour: list[int]) -> None:
        self.tris += tris
        self.cols += [colour] * len(tris)

    def box(self, x0: float, x1: float, y0: float, y1: float, z0: float, z1: float, colour: list[int]) -> None:
        if x1 - x0 <= 1e-6 or y1 - y0 <= 1e-6 or z1 - z0 <= 1e-6:
            return
        ym = (y0 + y1) / 2
        self.add(box_tris((x0, ym), (x1, ym), y1 - y0, z0, z1), colour)

    def prism_xz(self, profile: list[tuple[float, float]], y0: float, y1: float, colour: list[int]) -> None:
        """A convex ``profile`` of (x, z) points pushed from y0 to y1."""
        a = [(x, y0, z) for x, z in profile]
        b = [(x, y1, z) for x, z in profile]
        self._prism(a, b, colour)

    def prism_yz(self, profile: list[tuple[float, float]], x0: float, x1: float, colour: list[int]) -> None:
        """A convex ``profile`` of (y, z) points pushed from x0 to x1."""
        a = [(x0, y, z) for y, z in profile]
        b = [(x1, y, z) for y, z in profile]
        self._prism(a, b, colour)

    def _prism(self, a: list, b: list, colour: list[int]) -> None:
        n = len(a)
        tris = [[a[0], a[k], a[k + 1]] for k in range(1, n - 1)] + [[b[0], b[k], b[k + 1]] for k in range(1, n - 1)]
        for k in range(n):
            j = (k + 1) % n
            tris += [[a[k], a[j], b[j]], [a[k], b[j], b[k]]]
        centre = np.mean(np.array(a + b, float), axis=0)
        self.add(_outward(tris, centre), colour)

    def turned(self, pivot: tuple[float, float, float], lean: float, roll: float, yaw: float = 0.0) -> "_Parts":
        """These parts leaned back about the x axis (``lean``), rolled about y and turned about z, radians, around ``pivot``."""
        if not self.tris:
            return self
        cl, sl, cr, sr, cy, sy = math.cos(lean), math.sin(lean), math.cos(roll), math.sin(roll), math.cos(yaw), math.sin(yaw)
        rx = np.array([[1, 0, 0], [0, cl, sl], [0, -sl, cl]])
        ry = np.array([[cr, 0, sr], [0, 1, 0], [-sr, 0, cr]])
        rz = np.array([[cy, -sy, 0], [sy, cy, 0], [0, 0, 1]])
        p = np.array(pivot, float)
        pts = (np.array(self.tris, float) - p) @ (rz @ ry @ rx).T + p
        out = _Parts()
        out.tris, out.cols = pts.tolist(), list(self.cols)
        return out

    def extend(self, other: "_Parts") -> None:
        self.tris += other.tris
        self.cols += other.cols

    def mesh(self) -> Mesh:
        return Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8))


def _writing(p: _Parts, x0: float, x1: float, z0: float, z1: float, y_face: float, ink: list[int], plate: list[int] | None,
             rng: random.Random) -> None:
    """A plaque on the face at ``y_face`` (looking -y): an optional plate, a small cross and lines of writing."""
    y = y_face
    if plate is not None:
        p.box(x0, x1, y - 0.008, y + 0.01, z0, z1, plate)
        y -= 0.008
        x0, x1, z0, z1 = x0 + 0.03, x1 - 0.03, z0 + 0.03, z1 - 0.03
    w, cx = x1 - x0, (x0 + x1) / 2
    lines = max(2, min(5, int((z1 - z0) / 0.075)))
    pitch = (z1 - z0) / (lines + 1)
    top = z1 - 0.3 * pitch
    if pitch > 0.07 and rng.random() < 0.6:  # a cross over the name
        p.box(cx - 0.008, cx + 0.008, y - 0.004, y + 0.004, top - 0.085, top, ink)
        p.box(cx - 0.03, cx + 0.03, y - 0.0045, y + 0.004, top - 0.04, top - 0.024, ink)
        top -= 0.11
        lines = max(2, int((top - z0) / pitch))
    for k in range(lines):
        z = top - (k + 0.5) * (top - z0) / lines
        share = 0.9 if k == 0 else rng.uniform(0.35, 0.8)
        h = 0.02 if k == 0 else 0.012
        p.box(cx - w * share / 2, cx + w * share / 2, y - 0.004, y + 0.004, z - h, z + h, ink)


def _headstone(spec: GraveSpec, w: float, d: float, rng: random.Random, body: list[int], trim: list[int], ink: list[int],
               broken: bool) -> tuple[_Parts, float]:
    """The headstone standing on z = 0, centred on x, its face at y = -d / 2. Returns the parts and its height."""
    p, h, kind = _Parts(), spec.head_h, spec.head
    f, b = -d / 2, d / 2
    plate = _mix(body, [30, 30, 32], 0.75) if sum(body) > 380 else _mix(body, [225, 222, 212], 0.8)
    plate_ink = [201, 168, 74] if sum(plate) < 380 else [50, 50, 50]

    def write(x0: float, x1: float, z0: float, z1: float) -> None:
        if spec.plaque == "plate":
            _writing(p, x0, x1, z0, z1, f, plate_ink, plate, rng)
        elif spec.plaque == "lines":
            _writing(p, x0, x1, z0, z1, f, ink, None, rng)

    if broken and kind in ("rect", "round", "gable", "shoulder"):  # snapped off: a slanted stump
        hb = h * rng.uniform(0.3, 0.5)
        lo, hi = (hb * 0.75, hb) if rng.random() < 0.5 else (hb, hb * 0.75)
        p.prism_xz([(-w / 2, 0), (w / 2, 0), (w / 2, hi), (-w / 2, lo)], f, b, body)
        return p, hb
    if kind == "rect":
        p.box(-w / 2, w / 2, f, b, 0, h, body)
        write(-w * 0.38, w * 0.38, h * 0.3, h * 0.86)
    elif kind == "round":
        rise, n = 0.2 * w, 6
        arc = [(w / 2 * math.cos(math.pi * k / n), h - rise + rise * math.sin(math.pi * k / n)) for k in range(n + 1)]
        p.prism_xz([(-w / 2, 0), (w / 2, 0), *arc], f, b, body)
        write(-w * 0.36, w * 0.36, h * 0.28, h * 0.8)
    elif kind == "gable":
        p.prism_xz([(-w / 2, 0), (w / 2, 0), (w / 2, h * 0.8), (0, h), (-w / 2, h * 0.8)], f, b, body)
        write(-w * 0.36, w * 0.36, h * 0.25, h * 0.76)
    elif kind == "shoulder":
        p.box(-w / 2, w / 2, f, b, 0, h * 0.62, body)
        p.box(-w * 0.3, w * 0.3, f, b, h * 0.62, h, body)
        p.box(-w / 2 - 0.02, w / 2 + 0.02, f - 0.02, b + 0.02, h * 0.62 - 0.03, h * 0.62 + 0.012, trim)
        write(-w * 0.38, w * 0.38, h * 0.1, h * 0.55)
    elif kind == "desk":  # a low block, its top sloping towards the foot, the writing on a plate on the slope
        dd, hf, hb = max(d, 0.34), h * 0.55, h
        f, b = -dd / 2, dd / 2
        p.prism_yz([(f, 0), (b, 0), (b, hb), (f, hf)], -w / 2, w / 2, body)
        ny, nz = -(hb - hf), dd  # the slope's outward normal (unnormalised)
        k = 0.012 / math.hypot(ny, nz)
        oy, oz = ny * k, nz * k
        a0, a1 = 0.14, 0.86
        ya, za, yb, zb = f + dd * a0, hf + (hb - hf) * a0, f + dd * a1, hf + (hb - hf) * a1
        p.prism_yz([(ya - oy, za - oz), (yb - oy, zb - oz), (yb + oy, zb + oz), (ya + oy, za + oz)], -w * 0.4, w * 0.4, plate)
    elif kind == "cross":
        arm = 0.17
        p.box(-w * 0.3, w * 0.3, f - 0.03, b + 0.03, 0, h * 0.22, trim)
        p.box(-arm / 2, arm / 2, f, b, h * 0.22, h, body)
        p.box(-w * 0.34, w * 0.34, f, b, h * 0.66, h * 0.66 + arm, body)
        write(-w * 0.24, w * 0.24, h * 0.03, h * 0.2)
    elif kind == "iron_cross":
        iron, t = hex_to_rgb(IRON), 0.022
        p.box(-0.11, 0.11, -0.11, 0.11, 0, 0.1, trim)
        p.box(-t, t, -t, t, 0.1, h, iron)
        p.box(-0.24, 0.24, -t, t, h * 0.72, h * 0.72 + 2 * t, iron)
        for sx in (-1, 1):  # scroll ends
            p.box(sx * 0.24 - 0.03, sx * 0.24 + 0.03, -t, t, h * 0.72 - 0.02, h * 0.72 + 2 * t + 0.02, iron)
        p.box(-0.03, 0.03, -t, t, h - 0.02, h + 0.04, iron)
        p.box(-0.1, 0.1, -t - 0.012, -t + 0.005, h * 0.42, h * 0.58, [224, 221, 211])
        _writing(p, -0.08, 0.08, h * 0.43, h * 0.57, -t - 0.012, [50, 50, 50], None, rng)
    elif kind == "obelisk":
        r0, r1 = min(w, 0.5) / 2, min(w, 0.5) / 2 * 0.62
        p.box(-r0 - 0.06, r0 + 0.06, -r0 - 0.06, r0 + 0.06, 0, 0.14, trim)
        p.add(frustum_tris((0, 0), r0, r1, 0.14, h * 0.88, 0.0), body)
        p.add(pyramid_tris((0, 0), r1, h * 0.88, h, 0.0), body)
        _writing(p, -r0 * 0.7, r0 * 0.7, 0.26, h * 0.5, -r0 * 0.93, ink, None, rng)
    return p, h


def _lantern(p: _Parts, x: float, y: float, z: float, rng: random.Random, lit: bool = True) -> None:
    iron = hex_to_rgb(IRON)
    glass = hex_to_rgb(rng.choice(("#a8322a", "#b8452a", "#c9a23a"))) if lit else [70, 62, 58]
    p.box(x - 0.055, x + 0.055, y - 0.055, y + 0.055, z, z + 0.025, iron)
    p.box(x - 0.042, x + 0.042, y - 0.042, y + 0.042, z + 0.025, z + 0.17, glass)
    p.add(pyramid_tris((x, y), 0.06, z + 0.17, z + 0.24, 0.0), iron)


def _candle(p: _Parts, x: float, y: float, z: float, rng: random.Random) -> None:
    p.box(x - 0.035, x + 0.035, y - 0.035, y + 0.035, z, z + 0.12, hex_to_rgb(rng.choice(("#b02a2a", "#e8e4dc", "#b02a2a"))))
    p.box(x - 0.026, x + 0.026, y - 0.026, y + 0.026, z + 0.12, z + 0.135, [230, 205, 120])


def _flowers(p: _Parts, x: float, y: float, z: float, r: float, rng: random.Random, n: int) -> None:
    green, petal = hex_to_rgb(WEED[0]), hex_to_rgb(rng.choice(PETALS))
    p.add(frustum_tris((x, y), r * 0.5, r, z, z + 0.09, rng.uniform(0, 1.5)), green)
    for _ in range(n):
        a, d = rng.uniform(0, 2 * math.pi), rng.uniform(0, r * 0.8)
        fx, fy, s = x + d * math.cos(a), y + d * math.sin(a), rng.uniform(0.03, 0.05)
        p.add(pyramid_tris((fx, fy), s, z + 0.07, z + 0.07 + rng.uniform(0.06, 0.11), rng.uniform(0, 1.5)), petal)


def _vase(p: _Parts, x: float, y: float, z: float, rng: random.Random, colour: list[int]) -> None:
    p.add(frustum_tris((x, y), 0.045, 0.065, z, z + 0.2, 0.0), colour)
    _flowers(p, x, y, z + 0.2, 0.1, rng, 4)


def _wreath(p: _Parts, x: float, y: float, z: float, rng: random.Random) -> None:
    r, green = 0.2, hex_to_rgb("#3f6a34")
    for k in range(8):
        a = 2 * math.pi * k / 8
        cx, cy = x + r * math.cos(a), y + r * math.sin(a)
        p.add(frustum_tris((cx, cy), 0.075, 0.045, z, z + 0.07, a), green)
    p.box(x - 0.05, x + 0.05, y - r - 0.07, y - r + 0.07, z + 0.02, z + 0.085, hex_to_rgb(rng.choice(("#b02a2a", "#e8e4dc", "#c9a23a"))))


def _weeds(p: _Parts, area: tuple[float, float, float, float], z: float, n: int, rng: random.Random, tall: float) -> None:
    x0, x1, y0, y1 = area
    if x1 - x0 <= 0.02 or y1 - y0 <= 0.02:
        return
    for _ in range(n):
        s, h = rng.uniform(0.04, 0.1), rng.uniform(0.06, tall)
        p.add(pyramid_tris((rng.uniform(x0, x1), rng.uniform(y0, y1)), s, z, z + h, rng.uniform(0, 1.5)), hex_to_rgb(rng.choice(WEED)))


def _moss(p: _Parts, tops: list[tuple[float, float, float, float, float]], neglect: float, rng: random.Random) -> None:
    """Flat moss blotches on the top faces (x0, x1, y0, y1, z), more of them the more neglected."""
    for x0, x1, y0, y1, z in tops:
        area = (x1 - x0) * (y1 - y0)
        for _ in range(round(area * 14 * max(0.0, neglect - 0.25) * rng.uniform(0.5, 1.5))):
            w, d = rng.uniform(0.05, 0.22), rng.uniform(0.05, 0.22)
            cx, cy = rng.uniform(x0, x1), rng.uniform(y0, y1)
            p.box(max(x0, cx - w / 2), min(x1, cx + w / 2), max(y0, cy - d / 2), min(y1, cy + d / 2), z - 0.01, z + 0.006,
                  hex_to_rgb(rng.choice(MOSS)))


def grave_mesh(length: float, width: float, spec: GraveSpec, seed: str = "grave") -> Mesh:
    """The grave of ``spec`` in a plan rectangle ``width`` (x) by ``length`` (y) metres, see the module text."""
    rng = _rng(seed)
    n = max(0.0, min(1.0, spec.neglect))
    stain = hex_to_rgb(STAIN)

    def aged(hex_colour: str) -> list[int]:
        return _mix(_mix(hex_to_rgb(hex_colour), stain, 0.4 * n), [0, 0, 0], 0.08 * n)

    body, trim, ink = (aged(c) for c in STONES[spec.stone])
    hbody, htrim, hink = (aged(c) for c in STONES[spec.head_stone or spec.stone])
    ink, hink = _mix(ink, body, 0.5 * n), _mix(hink, hbody, 0.5 * n)  # the writing fades
    p = _Parts()
    x0, x1, y0, y1 = -width / 2, width / 2, -length / 2, length / 2
    tops: list[tuple[float, float, float, float, float]] = []

    # --- base: step and kerb frame
    z = 0.0
    if spec.step:
        p.box(x0, x1, y0, y1, 0, 0.06, trim)
        z, x0, x1, y0, y1 = 0.06, x0 + 0.05, x1 - 0.05, y0 + 0.05, y1 - 0.05
    t, kz = spec.kerb_t, spec.kerb_h
    ix0, ix1, iy0, iy1 = x0 + t, x1 - t, y0 + t, y1 - t
    for bx0, bx1, by0, by1 in ((x0, x1, y0, iy0), (x0, x1, iy1, y1), (x0, ix0, iy0, iy1), (ix1, x1, iy0, iy1)):
        p.box(bx0, bx1, by0, by1, z, kz, trim)
        tops.append((bx0, bx1, by0, by1, kz))
    if spec.posts:
        s = t + 0.03
        for cx in (x0, x1 - s):
            for cy in (y0, y1 - s):
                p.box(cx, cx + s, cy, cy + s, kz, kz + 0.07, trim)

    # --- headstone on its plinth at the head
    has_head = spec.head != "none"
    hw = min(width * (0.5 if spec.head in ("cross", "obelisk") else 0.78), 1.9)
    hd = rng.uniform(0.11, 0.16)
    plinth_d = (0.5 if spec.head in ("desk", "obelisk") else hd + 0.16) if has_head else 0.0
    plinth_z = kz + 0.07
    py0 = y1 - plinth_d
    bed_y1 = py0 if has_head else iy1  # the cover ends where the plinth starts
    if has_head:
        pw = min(hw + 0.2, x1 - x0 - 0.016)
        if spec.head != "iron_cross":
            p.box(-pw / 2, pw / 2, py0, y1 - 0.008, kz - 0.06, plinth_z, htrim)
            tops.append((-pw / 2, pw / 2, py0, y1 - 0.008, plinth_z))
        broken = n > 0.85 and rng.random() < 0.7
        stone, sh = _headstone(spec, hw, hd, rng, hbody, htrim, hink, broken)
        lean = math.radians(rng.uniform(3, 11)) * max(0.0, n - 0.45) / 0.55 * rng.choice((1, 1, -0.4)) if n > 0.45 else 0.0
        roll = math.radians(rng.uniform(-5, 5)) * max(0.0, n - 0.45) / 0.55
        base_z = z if spec.head == "iron_cross" else plinth_z
        yc = py0 + plinth_d / 2
        placed = _Parts()
        placed.tris = (np.array(stone.tris, float) + [0, yc, base_z - 0.01]).tolist() if stone.tris else []
        placed.cols = stone.cols
        p.extend(placed.turned((0, yc, base_z), lean, roll))
        if broken:  # the piece that fell lies on the cover
            piece = _Parts()
            piece.box(-hw / 2, hw / 2, -spec.head_h * 0.25, spec.head_h * 0.25, 0, hd, hbody)
            fy = (iy0 + bed_y1) / 2 + rng.uniform(-0.2, 0.3)
            moved = _Parts()
            moved.tris = (np.array(piece.tris, float) + [rng.uniform(-0.1, 0.1), fy, kz + 0.02]).tolist()
            moved.cols = piece.cols
            p.extend(moved.turned((0, fy, kz), math.radians(rng.uniform(-6, 6)), 0.0, math.radians(rng.uniform(-25, 25))))

    # --- cover
    cover, cz = spec.cover, kz - 0.05
    bed: tuple[float, float, float, float] | None = None  # where plants grow, with cz as its ground
    slabs: list[tuple[float, float, float, float]] = []
    if cover == "slab":
        slabs = [(ix0 - 0.03, ix1 + 0.03, iy0 - 0.03, bed_y1 + 0.02)]
    elif cover == "double_slab":
        slabs = [(ix0 - 0.03, -0.015, iy0 - 0.03, bed_y1 + 0.02), (0.015, ix1 + 0.03, iy0 - 0.03, bed_y1 + 0.02)]
    elif cover == "half":
        split = iy0 + (bed_y1 - iy0) * 0.58
        slabs = [(ix0 - 0.03, ix1 + 0.03, iy0 - 0.03, split)]
        bed = (ix0, ix1, split, bed_y1)
        p.box(ix0 - 0.02, ix1 + 0.02, split - 0.02, bed_y1 + 0.02, z, cz, hex_to_rgb(SOIL))
    else:
        bed = (ix0, ix1, iy0, bed_y1)
        fill = {"gravel": hex_to_rgb(rng.choice(GRAVEL)), "soil": hex_to_rgb(SOIL), "grass": hex_to_rgb(GRASS)}[cover]
        p.box(ix0 - 0.02, ix1 + 0.02, iy0 - 0.02, bed_y1 + 0.02, z, cz, _mix(fill, stain, 0.25 * n))
    for sx0, sx1, sy0, sy1 in slabs:
        top = kz + 0.045
        parts = [(sy0, sy1, top)]
        if n > 0.6 and rng.random() < 0.75:  # cracked across, one part sunk
            cy = sy0 + (sy1 - sy0) * rng.uniform(0.35, 0.65)
            sunk = top - rng.uniform(0.015, 0.04)
            parts = [(sy0, cy - 0.012, top), (cy + 0.012, sy1, sunk)] if rng.random() < 0.5 else [(sy0, cy - 0.012, sunk), (cy + 0.012, sy1, top)]
        for a, b, zt in parts:
            p.box(sx0, sx1, a, b, kz - 0.005, zt, body)
            if b - a > 0.5 and sx1 - sx0 > 0.5:  # the raised panel
                p.box(sx0 + 0.09, sx1 - 0.09, a + 0.09, b - 0.09, zt - 0.01, zt + 0.022, body)
                p.box(sx0 + 0.078, sx1 - 0.078, a + 0.078, b - 0.078, zt - 0.01, zt + 0.008, trim)  # a shadow line at its foot
                tops.append((sx0 + 0.09, sx1 - 0.09, a + 0.09, b - 0.09, zt + 0.022))
            else:
                tops.append((sx0, sx1, a, b, zt))
    slab_z = kz + 0.067

    # --- dressing (a kept grave only) and what grows
    if n < 0.5:
        front = bed_y1 - 0.14  # just before the plinth
        dz = cz if cover in ("gravel", "soil", "grass") else slab_z
        if cover == "half":
            dz = cz
        spots = [(-width * 0.27, front), (width * 0.27, front), (0.0, front - 0.05), (-width * 0.12, front - 0.3), (width * 0.15, front - 0.34)]
        rng.shuffle(spots)
        for item, (sx, sy) in zip(spec.dressing, spots, strict=False):
            if item == "lantern":
                _lantern(p, sx, sy, dz, rng)
            elif item == "candle":
                _candle(p, sx, sy, dz, rng)
            elif item == "vase":
                _vase(p, sx, sy, dz, rng, trim)
            elif item == "flowers":
                _flowers(p, sx, sy, dz, 0.13, rng, 5)
            elif item == "wreath" and slabs:
                wy = (slabs[0][2] + slabs[0][3]) / 2
                _wreath(p, 0.0, wy, slab_z, rng)
        if bed is not None and cover in ("half", "soil") and "flowers" in spec.dressing:  # a planted bed
            for _ in range(rng.randint(3, 6)):
                _flowers(p, rng.uniform(bed[0] + 0.12, bed[1] - 0.12), rng.uniform(bed[2] + 0.12, bed[3] - 0.12), cz, rng.uniform(0.08, 0.13), rng, 4)
    elif n < 0.7 and "lantern" in spec.dressing:  # a burnt-out lantern is all that is left
        _lantern(p, width * 0.25, bed_y1 - 0.14, cz if bed is not None and cover != "half" else slab_z, rng, lit=False)
    if n > 0.3:
        _moss(p, tops, n, rng)
        if bed is not None:
            _weeds(p, bed, cz, round(60 * (n - 0.3) * (bed[1] - bed[0]) * (bed[3] - bed[2])), rng, 0.14 + 0.3 * n)
        for sx0, sx1, sy0, sy1 in slabs:  # out of the joints along the kerb
            if n > 0.55:
                for edge in ((sx0, sx0 + 0.04, sy0, sy1), (sx1 - 0.04, sx1, sy0, sy1), (sx0, sx1, sy0, sy0 + 0.04)):
                    _weeds(p, edge, kz, round(10 * (n - 0.55) * max(edge[1] - edge[0], edge[3] - edge[2])), rng, 0.22)
    return p.mesh()


def spec_for(seed: str, length: float, width: float, neglect: float | None = None) -> GraveSpec:
    """The grave a seed gets in a rectangle of this size: what is likely in a village graveyard, the unlikely too.

    ``neglect`` None: mostly kept graves, about one in seven abandoned."""
    rng = _rng(f"{seed}:spec")
    if neglect is None:
        r = rng.random()
        neglect = rng.uniform(0.7, 1.0) if r < 0.14 else rng.uniform(0.3, 0.6) if r < 0.34 else rng.uniform(0.0, 0.25)
    old = neglect > 0.55 and rng.random() < 0.6  # the abandoned ones are mostly the old ones
    small, double = length < 1.5, width >= 1.9
    pick = lambda pairs: rng.choices([a for a, _ in pairs], [b for _, b in pairs])[0]  # noqa: E731
    if old:
        stone = pick([("sandstone", 4), ("concrete", 3), ("terrazzo", 2), ("white_marble", 1)])
        head = pick([("iron_cross", 3), ("cross", 3), ("round", 2), ("gable", 2), ("obelisk", 1), ("rect", 1)])
        cover = pick([("soil", 3), ("grass", 3), ("slab", 2), ("gravel", 1)])
    else:
        stone = pick([("terrazzo", 5), ("black_granite", 4), ("grey_granite", 3), ("red_granite", 2), ("white_marble", 1.5), ("concrete", 1.5)])
        head = pick([("rect", 4), ("round", 2), ("shoulder", 2), ("gable", 1.5), ("desk", 1.5), ("cross", 1.5)])
        cover = pick([("slab", 5), ("half", 2.5), ("gravel", 2.5), ("soil", 1), ("grass", 0.5)])
    if double and cover == "slab" and rng.random() < 0.6:
        cover = "double_slab"
    if small:  # an urn grave
        head, cover = pick([("desk", 3), ("rect", 2), ("round", 1)]), pick([("slab", 3), ("gravel", 2)])
    head_stone = None
    if not old and rng.random() < 0.25:  # a dark headstone on a light frame, as often
        head_stone = pick([("black_granite", 3), ("grey_granite", 1), ("red_granite", 1)])
    tall = {"rect": (0.7, 1.0), "round": (0.75, 1.05), "gable": (0.8, 1.1), "shoulder": (0.8, 1.05), "desk": (0.3, 0.45),
            "cross": (0.95, 1.3), "iron_cross": (1.0, 1.3), "obelisk": (1.2, 1.6)}[head]
    head_h = rng.uniform(*tall) * (0.7 if small and head != "desk" else 1.0)
    count = pick([(0, 2), (1, 4), (2, 4), (3, 2)])
    dressing = tuple(rng.sample(["lantern", "lantern", "candle", "vase", "flowers", "flowers", "wreath"], count))
    return GraveSpec(
        stone=stone, cover=cover, head=head, head_stone=head_stone, head_h=head_h,
        kerb_h=rng.uniform(0.14, 0.2) if old else rng.uniform(0.16, 0.3), kerb_t=rng.uniform(0.09, 0.15),
        step=not old and rng.random() < 0.3, posts=rng.random() < 0.15,
        plaque=pick([("plate", 2), ("lines", 5)]) if head != "desk" else "plate",
        dressing=tuple(dict.fromkeys(dressing)), neglect=neglect,
    )


# --- graves on the map ----------------------------------------------------------------------

ASSET = "prim:grave"  # a placed object of this asset gets its own mesh at the export (:func:`object_mesh`)
DEFAULT_SIZE = (1.5, 2.7)  # width, length of the default grave (what the editor shows before the next export)
DEFAULT_SPEC = GraveSpec(dressing=("lantern", "vase"))
NEGLECT_CELL_M = 9.0  # neglect comes in patches of about this size, so abandoned graves stand together


def neglect_at(x: float, y: float) -> float:
    """How neglected a grave at this place is (0 kept, 1 abandoned): a smooth noise over the map, so the abandoned
    graves stand in clusters; about one grave in seven is over 0.7."""
    def corner(i: int, j: int) -> float:
        return _rng(f"neglect:{i}:{j}").random()

    gx, gy = x / NEGLECT_CELL_M, y / NEGLECT_CELL_M
    i, j = math.floor(gx), math.floor(gy)
    fx, fy = gx - i, gy - j
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    v = (corner(i, j) * (1 - fx) + corner(i + 1, j) * fx) * (1 - fy) + (corner(i, j + 1) * (1 - fx) + corner(i + 1, j + 1) * fx) * fy
    own = _rng(f"neglect:{x:.2f}:{y:.2f}").random()  # every grave a little its own
    v = 0.8 * v + 0.2 * own
    return max(0.0, min(1.0, (v - 0.47) * 3.6))  # the lower half of the noise is a kept grave


def object_mesh(obj: dict) -> Mesh:
    """The grave of a placed object (``objects.json``): its ``size_m`` is [width, length, height of the solid part], its seed
    its id, its neglect that of its place. Local metres as :func:`grave_mesh` (the head at +y)."""
    size = obj.get("size_m") or [*DEFAULT_SIZE, 0.3]
    width, length = float(size[0]), float(size[1])
    x, y = obj["position"]
    return grave_mesh(length, width, spec_for(obj["id"], length, width, neglect_at(float(x), float(y))), obj["id"])


def default_mesh(params: dict | None = None) -> Mesh:
    """``prim:grave`` itself: the grave shown where no export has built the object's own yet."""
    return grave_mesh(DEFAULT_SIZE[1], DEFAULT_SIZE[0], DEFAULT_SPEC, "default")


def manifest_entry() -> dict:
    """The ``assets/manifest.json`` record of ``prim:grave``."""
    pts = default_mesh().tris.reshape(-1, 3)
    return {
        "id": ASSET,
        "category": "slab",
        "name": "Grave (generated per object: size_m = [width, length, solid height])",
        "file": None,
        "size_m": [DEFAULT_SIZE[0], round(float(pts[:, 2].max()), 4), DEFAULT_SIZE[1]],
        "tags": ["grave", "graveyard", "headstone", "flat-colour", "primitive", "generated"],
        "licence": "CC0",
        "source": "https://github.com/Kobu-Labs/drycastle (pipeline primitive, pipeline/src/drycastle_pipeline/graves.py)",
        "pack": None,
    }


# --- the graveyard's cross -------------------------------------------------------------------

CRUCIFIX_ASSET = "prim:crucifix"
# (half size, top z) of the stone under the cross, from the ground up: three steps, the pedestal, its cornice
_CRUCIFIX_STONE = ((1.2, 0.18), (0.92, 0.36), (0.66, 0.56), (0.4, 1.86), (0.48, 1.98))
_CRUCIFIX_TOP = 5.6


def crucifix_mesh(params: dict | None = None) -> Mesh:
    """``prim:crucifix``: the big cross of a village graveyard, 5.6 m: three stone steps, a pedestal with an inscription, a dark
    cross with the body of Christ, the INRI board and a little tin roof. It faces -y (like a grave's writing), centred on the origin."""
    p, rng = _Parts(), _rng("crucifix")
    stone, trim, ink = (hex_to_rgb(c) for c in STONES["terrazzo"])
    wood, body, cloth, tin = hex_to_rgb("#3a2c22"), hex_to_rgb("#d8cdb6"), hex_to_rgb("#efece4"), hex_to_rgb("#5c6064")
    z0 = 0.0
    for k, (h, z1) in enumerate(_CRUCIFIX_STONE):
        if k == 3:  # a foot moulding under the pedestal
            p.box(-h - 0.07, h + 0.07, -h - 0.07, h + 0.07, z0, z0 + 0.1, trim)
        p.box(-h, h, -h, h, z0 - (0.01 if k else 0.0), z1, trim if k in (1, 4) else stone)
        z0 = z1
    _writing(p, -0.3, 0.3, 0.8, 1.7, -0.4, [201, 168, 74], hex_to_rgb(STONES["black_granite"][0]), rng)
    # the cross
    t, arm_z, arm_half = 0.13, 4.45, 1.1
    p.box(-t, t, -t, t, z0 - 0.01, 5.3, wood)
    p.box(-arm_half, arm_half, -t + 0.004, t - 0.004, arm_z - t, arm_z + t, wood)
    for sx in (-1, 1):  # the widened ends of the arms and of the top
        p.box(sx * arm_half - 0.07, sx * arm_half + 0.07, -t - 0.02, t + 0.02, arm_z - t - 0.05, arm_z + t + 0.05, wood)
    p.box(-t - 0.05, t + 0.05, -t - 0.02, t + 0.02, 5.16, 5.3, wood)
    # the little roof: two tin sheets meeting over the top
    for sx in (-1, 1):
        p.prism_xz([(0.0, 5.6), (sx * 0.62, 5.2), (sx * 0.62, 5.16), (0.0, 5.56)], -0.34, 0.2, tin)
    # INRI
    p.box(-0.2, 0.2, -t - 0.03, -t + 0.005, 4.9, 5.06, cloth)
    p.box(-0.14, 0.14, -t - 0.034, -t - 0.026, 4.965, 4.995, ink)
    # the body, in front of the cross (its back at y = -t)
    f = -t
    p.box(-0.105, 0.105, f - 0.2, f - 0.0, 4.36, 4.62, body)  # head, bowed forward
    p.box(-0.125, 0.125, f - 0.215, f - 0.01, 4.56, 4.66, hex_to_rgb("#4a3a2c"))  # hair and the crown of thorns
    p.prism_xz([(-0.15, 3.62), (0.15, 3.62), (0.21, 4.34), (-0.21, 4.34)], f - 0.15, f + 0.005, body)  # chest
    for sx in (-1, 1):  # arms up to the nails
        p.prism_xz([(sx * 0.17, 4.2), (sx * 0.17, 4.33), (sx * 0.94, 4.5), (sx * 0.94, 4.41)], f - 0.1, f + 0.005, body)
        p.box(sx * 0.94 - 0.05, sx * 0.94 + 0.05, f - 0.07, f + 0.005, 4.4, 4.52, body)
    p.box(-0.19, 0.19, f - 0.17, f + 0.005, 3.38, 3.66, cloth)  # the cloth
    p.box(0.1, 0.24, f - 0.16, f - 0.02, 3.2, 3.5, cloth)  # its end hanging at the hip
    p.prism_xz([(-0.13, 2.72), (0.05, 2.72), (0.15, 3.4), (-0.15, 3.4)], f - 0.14, f + 0.005, body)  # legs, knees to one side
    p.box(-0.12, 0.04, f - 0.19, f + 0.005, 2.6, 2.74, body)  # feet
    p.box(-0.16, 0.08, f - 0.2, f + 0.005, 2.52, 2.6, wood)  # the block under them
    # on the steps: two lanterns and flowers
    _lantern(p, -0.62, -0.74, 0.36, rng)
    _lantern(p, 0.62, -0.74, 0.36, rng)
    _flowers(p, -0.25, -0.76, 0.36, 0.14, rng, 6)
    _flowers(p, 0.2, -0.78, 0.36, 0.14, rng, 6)
    _wreath(p, 0.0, -0.9, 0.18, rng)
    return p.mesh()


def crucifix_entry() -> dict:
    """The ``assets/manifest.json`` record of ``prim:crucifix``: solid are the stone and the shaft, not the box around it all."""
    solids = [{"min": [-h, -h, 0.0], "max": [h, h, z], "surface": "concrete"} for h, z in _CRUCIFIX_STONE]
    solids.append({"min": [-0.15, -0.3, 1.98], "max": [0.15, 0.15, 5.3], "surface": "wood"})
    solids.append({"min": [-1.2, -0.15, 4.3], "max": [1.2, 0.15, 4.6], "surface": "wood"})
    return {
        "id": CRUCIFIX_ASSET,
        "category": "other",
        "name": "Graveyard cross with the body of Christ (5.6 m, on stone steps)",
        "file": None,
        "size_m": [2.4, _CRUCIFIX_TOP, 2.4],
        "tags": ["cross", "crucifix", "graveyard", "monument", "flat-colour", "primitive"],
        "licence": "CC0",
        "source": "https://github.com/Kobu-Labs/drycastle (pipeline primitive, pipeline/src/drycastle_pipeline/graves.py)",
        "pack": None,
        "solids": solids,
    }
