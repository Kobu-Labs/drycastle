"""Utility pole parts: the kit that ``poles.py`` assembles poles from.

Two layers:

* :class:`Kit` collects flat-coloured triangles and offers the few solids everything is made of
  (loft between rings, lathe, tube, beam, box, swept cable). Every solid is closed and wound
  counter-clockwise seen from outside, like every other mesh of the pipeline.
* Part builders (``insulator``, ``crossarm``, ``lamp``, ``megaphone``, ``junction_box``, ...) return a
  :class:`Part`: a kit in the part's own frame plus named points (where a wire sits, where a feed
  cable enters, where the light is). The frame of a part that hangs on a pole has its origin on the
  pole surface, ``+x`` pointing away from the pole and ``+z`` up; ``poles.py`` turns and moves it into
  place. ``PARTS`` lists every part with its default look (the parts sheet draws it).

The column itself and what follows its surface (riser pipes, down cables, slack coils) are built by
functions that take the :class:`Column`, since they depend on its taper.

Sizes are metres, colours ``#rrggbb``. Nothing here is random: builders that want variation take a
``random.Random`` from the caller.
"""

import math
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh

UP = np.array([0.0, 0.0, 1.0])
_X = np.array([1.0, 0.0, 0.0])

# The colours parts use unless told otherwise.
CONCRETE = "#a9a69d"
RUST = "#6b4a35"
STEEL = "#8b9096"
STEEL_DARK = "#4d4f52"
CABLE = "#1d1d1f"
WIRE = "#3f4246"
PLASTIC_GREY = "#c4c6c3"
PLASTIC_WHITE = "#e4e4df"
LAMP_BODY = "#b4b8bc"
LAMP_BOWL = "#efe9cf"
HORN = "#c9c2ae"
RED = "#c0392b"
YELLOW = "#e0b81e"
WHITE = "#f1f1ee"
INSULATOR_COLOURS = {
    "brown": "#5c3826",
    "white": "#e9e7e0",
    "glass": "#8fb8a6",
    "dark": "#2e2824",
}
# (height above the pin top, radius) of the insulator body, lathed around the pin.
_INSULATOR_PROFILES = {
    "brown": [(0.0, 0.030), (0.012, 0.052), (0.030, 0.052), (0.040, 0.030), (0.058, 0.030),
              (0.066, 0.044), (0.092, 0.040), (0.104, 0.020)],
    "glass": [(0.0, 0.044), (0.050, 0.048), (0.064, 0.028), (0.080, 0.028), (0.088, 0.040),
              (0.110, 0.034), (0.120, 0.015)],
    "dark": [(0.0, 0.030), (0.020, 0.060), (0.060, 0.065), (0.085, 0.035), (0.100, 0.035),
             (0.110, 0.050), (0.135, 0.030)],
}
_INSULATOR_PROFILES["white"] = _INSULATOR_PROFILES["brown"]


def vec(x: float, y: float, z: float) -> np.ndarray:
    return np.array([x, y, z], float)


def unit(v: np.ndarray) -> np.ndarray:
    return v / np.linalg.norm(v)


def rgb(colour) -> list[int]:
    """A colour as ``[r, g, b]``; accepts ``#rrggbb`` or an RGB triple."""
    return hex_to_rgb(colour) if isinstance(colour, str) else [int(c) for c in colour]


def shade(colour, delta: int) -> list[int]:
    """``colour`` lighter (positive) or darker by ``delta`` on every channel."""
    return [max(0, min(255, c + delta)) for c in rgb(colour)]


def rot_z(deg: float) -> np.ndarray:
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0.0], [s, c, 0.0], [0.0, 0.0, 1.0]])


def frame(d: np.ndarray, hint: np.ndarray | None = None):
    """Right-handed ``(d, u, v)`` with ``u x v = d``; ``u`` is as close to ``hint`` (default up) as it gets."""
    d = unit(np.asarray(d, float))
    for h in (hint, UP, _X):
        if h is None:
            continue
        u = h - d * float(h @ d)
        n = float(np.linalg.norm(u))
        if n > 1e-6:
            u = u / n
            return d, u, np.cross(d, u)
    raise ValueError("no frame")


def ring(centre, d, radius: float, sides: int, hint=None, rv: float | None = None) -> list[np.ndarray]:
    """``sides`` points around ``centre``, counter-clockwise about ``d``; an ellipse when ``rv`` is given
    (``radius`` along the hint, ``rv`` across it)."""
    _, u, v = frame(d, hint)
    rv = radius if rv is None else rv
    return [
        np.asarray(centre, float)
        + radius * math.cos(2 * math.pi * k / sides) * u
        + rv * math.sin(2 * math.pi * k / sides) * v
        for k in range(sides)
    ]


def hang(p0, p1, sag: float, bulge=None, n: int = 8) -> list[np.ndarray]:
    """A cable hanging from ``p0`` to ``p1``: a parabola ``sag`` metres deep at the middle, pushed
    sideways by ``bulge`` (a vector) there."""
    p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
    pts = []
    for i in range(n + 1):
        t = i / n
        w = 4 * t * (1 - t)
        p = p0 + (p1 - p0) * t - UP * (sag * w)
        pts.append(p if bulge is None else p + np.asarray(bulge, float) * w)
    return pts


class Kit:
    """A growing list of flat-coloured triangles."""

    def __init__(self) -> None:
        self.tris: list[np.ndarray] = []  # each (3, 3)
        self.cols: list[list[int]] = []

    def __len__(self) -> int:
        return len(self.tris)

    def _quad(self, a, b, c, d, colour, inward: bool) -> None:
        if inward:
            self.tris += [np.array([a, c, b]), np.array([a, d, c])]
        else:
            self.tris += [np.array([a, b, c]), np.array([a, c, d])]
        self.cols += [colour, colour]

    def loft(self, rings, colour, cap0: bool = True, cap1: bool = True, inward: bool = False,
             cap_colour=None) -> None:
        """Skin over consecutive rings (same point count, each counter-clockwise about the way the
        rings advance), with flat end caps. ``colour`` is a colour or ``f(band, side) -> colour``.
        ``inward`` turns every face round (the inside of a shell)."""
        n = len(rings[0])
        flat = None if callable(colour) else rgb(colour)
        for k in range(len(rings) - 1):
            r0, r1 = rings[k], rings[k + 1]
            for i in range(n):
                j = (i + 1) % n
                col = flat if flat is not None else rgb(colour(k, i))
                self._quad(r0[i], r0[j], r1[j], r1[i], col, inward)
        cc = rgb(cap_colour) if cap_colour is not None else flat
        for on, r, flip in ((cap0, rings[0], True), (cap1, rings[-1], False)):
            if not on:
                continue
            col = cc if cc is not None else rgb(colour(0 if flip else len(rings) - 2, 0))
            for i in range(1, n - 1):
                tri = [r[0], r[i + 1], r[i]] if flip != inward else [r[0], r[i], r[i + 1]]
                self.tris.append(np.array(tri))
                self.cols.append(col)

    def lathe(self, base, axis, profile, sides: int, colour) -> None:
        """Solid of revolution: ``profile`` is ``(distance along the axis, radius)`` pairs."""
        base, axis = np.asarray(base, float), unit(np.asarray(axis, float))
        self.loft([ring(base + axis * t, axis, r, sides) for t, r in profile], colour)

    def tube(self, p0, p1, r0: float, colour, r1: float | None = None, sides: int = 8) -> None:
        """Round bar or cone frustum from ``p0`` to ``p1``."""
        p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
        d = p1 - p0
        self.loft([ring(p0, d, r0, sides), ring(p1, d, r0 if r1 is None else r1, sides)], colour)

    def beam(self, p0, p1, width: float, height: float, colour, hint=None) -> None:
        """Rectangular bar from ``p0`` to ``p1``: ``height`` along the hint (default up), ``width`` across."""
        p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
        _, u, v = frame(p1 - p0, hint)
        hu, hv = height / 2, width / 2
        offs = [-hu * u - hv * v, hu * u - hv * v, hu * u + hv * v, -hu * u + hv * v]
        self.loft([[p0 + o for o in offs], [p1 + o for o in offs]], colour)

    def box(self, lo, hi, colour) -> None:
        """Axis-aligned box between two corners."""
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        c = ((x0 + x1) / 2, (y0 + y1) / 2)
        self.beam(vec(*c, z0), vec(*c, z1), y1 - y0, x1 - x0, colour)

    def sweep(self, pts, radius: float, colour, sides: int = 3, closed: bool = False) -> None:
        """A cable or pipe along a polyline (a planar loop when ``closed``)."""
        pts = [np.asarray(p, float) for p in pts]
        n = len(pts)
        rings, u = [], None
        for i in range(n):
            a = pts[i - 1] if (i > 0 or closed) else pts[i]
            b = pts[(i + 1) % n] if (i < n - 1 or closed) else pts[i]
            t = unit(b - a)
            if u is not None:
                u = u - t * float(u @ t)
                u = u / np.linalg.norm(u) if np.linalg.norm(u) > 1e-6 else None
            if u is None:
                _, u, _ = frame(t)
            v = np.cross(t, u)
            rings.append([
                pts[i] + radius * (math.cos(2 * math.pi * k / sides) * u
                                   + math.sin(2 * math.pi * k / sides) * v)
                for k in range(sides)
            ])
        if closed:
            self.loft([*rings, rings[0]], colour, cap0=False, cap1=False)
        else:
            self.loft(rings, colour)

    def add(self, other: "Kit", yaw_deg: float = 0.0, offset=(0.0, 0.0, 0.0)) -> None:
        """Append ``other`` turned ``yaw_deg`` about the vertical and moved by ``offset``."""
        if not other.tris:
            return
        arr = np.array(other.tris) @ rot_z(yaw_deg).T + np.asarray(offset, float)
        self.tris += list(arr)
        self.cols += other.cols

    def transform(self, rotation: np.ndarray) -> None:
        """Rotate everything about the origin."""
        if self.tris:
            self.tris = list(np.array(self.tris) @ rotation.T)

    def mesh(self) -> Mesh:
        if not self.tris:
            return Mesh.empty()
        return Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8))


@dataclass
class Part:
    kit: Kit
    points: dict[str, np.ndarray] = field(default_factory=dict)

    def placed(self, yaw_deg: float, offset) -> dict[str, np.ndarray]:
        """The named points after the same turn and move as ``Kit.add``."""
        r, o = rot_z(yaw_deg), np.asarray(offset, float)
        return {k: r @ p + o for k, p in self.points.items()}


# ----------------------------------------------------------------------------- insulators, arms


def insulator(style: str = "brown", scale: float = 1.0) -> Part:
    """Pin insulator standing on its steel pin; origin at the foot of the pin. ``wire`` is its top."""
    k = Kit()
    s = scale
    k.tube(vec(0, 0, 0), vec(0, 0, 0.06 * s), 0.011 * s, STEEL_DARK, sides=5)
    prof = _INSULATOR_PROFILES[style]
    k.lathe(vec(0, 0, 0.05 * s), UP, [(t * s, r * s) for t, r in prof], 6, INSULATOR_COLOURS[style])
    return Part(k, {"wire": vec(0, 0, (0.05 + prof[-1][0]) * s)})


def pin_positions(length: float, pins: int, layout: str = "even", offset: float = 0.0) -> list[float]:
    """Where the pins stand along a crossarm (distance from the pole, along the arm)."""
    y0, y1 = offset - length / 2 + 0.07, offset + length / 2 - 0.07
    if pins <= 1:
        return [(y0 + y1) / 2][:pins]
    if layout == "paired" and pins % 2 == 0:
        half = pins // 2
        gap = min(0.26, (y1 - y0) / (2 * half))
        return [y0 + gap * i for i in range(half)] + [y1 - gap * i for i in reversed(range(half))]
    return [y0 + (y1 - y0) * i / (pins - 1) for i in range(pins)]


def crossarm(length: float = 1.2, pins: int = 4, style: str = "brown", layout: str = "even",
             brace: str = "none", offset: float = 0.0, colour: str = RUST, section: float = 0.06,
             pin_scale: float = 1.0) -> Part:
    """Steel crossarm bolted to the face of the pole, running along ``y``, with pin insulators.

    ``brace``: ``none``, ``one`` (a strut to one side) or ``two`` (a V). Points ``wire_0`` .. are the
    insulator tops, in order along the arm.
    """
    k = Kit()
    s = section
    y0, y1 = offset - length / 2, offset + length / 2
    k.beam(vec(s / 2, y0, 0), vec(s / 2, y1, 0), s, s, colour)
    k.box(vec(-0.004, -0.10, -s / 2 - 0.02), vec(0.002, 0.10, s / 2 + 0.02), shade(colour, -12))
    sides = {"none": (), "one": (-1,), "two": (-1, 1)}[brace]
    for sgn in sides:
        reach = (y1 if sgn > 0 else -y0) * 0.72
        k.beam(vec(0.012, sgn * 0.04, -reach * 0.8), vec(s / 2, sgn * reach, -s / 2), 0.035, 0.012,
               shade(colour, -6), hint=_X)
    pts = {}
    for i, y in enumerate(pin_positions(length, pins, layout, offset)):
        ins = insulator(style, pin_scale)
        at = (s / 2, y, s / 2)
        k.add(ins.kit, 0.0, at)
        pts[f"wire_{i}"] = ins.placed(0.0, at)["wire"]
    return Part(k, pts)


def top_pin(style: str = "brown", scale: float = 1.2) -> Part:
    """A single insulator on a stub on top of the column; origin at the middle of the column top."""
    k = Kit()
    k.tube(vec(0, 0, -0.02), vec(0, 0, 0.07), 0.016, STEEL_DARK, sides=6)
    ins = insulator(style, scale)
    k.add(ins.kit, 0.0, (0, 0, 0.06))
    return Part(k, {"wire": ins.placed(0.0, (0, 0, 0.06))["wire"]})


def side_bracket(kind: str = "pin", style: str = "white", reach: float = 0.2,
                 colour: str = STEEL_DARK) -> Part:
    """A small bracket on the side of the pole for one lower wire.

    ``pin``: an L-bracket with a pin insulator. ``spool``: a fork holding a spool insulator on a
    vertical bolt. ``hook``: a bare hook with a wedge clamp (for a bundled cable).
    """
    k = Kit()
    k.box(vec(0.0, -0.03, -0.09), vec(0.008, 0.03, 0.05), colour)
    if kind == "pin":
        k.beam(vec(0, 0, 0), vec(reach, 0, 0), 0.035, 0.012, colour)
        k.beam(vec(0.004, 0, -0.08), vec(reach * 0.8, 0, -0.004), 0.03, 0.01, colour, hint=_X)
        ins = insulator(style, 0.9)
        k.add(ins.kit, 0.0, (reach, 0, 0.004))
        return Part(k, {"wire": ins.placed(0.0, (reach, 0, 0.004))["wire"]})
    if kind == "spool":
        for z in (-0.045, 0.045):
            k.beam(vec(0, 0, z), vec(reach, 0, z), 0.035, 0.008, colour)
        x = reach - 0.03
        k.tube(vec(x, 0, -0.06), vec(x, 0, 0.06), 0.006, colour, sides=5)
        k.lathe(vec(x, 0, -0.038), UP, [(0.0, 0.032), (0.018, 0.032), (0.028, 0.020), (0.048, 0.020),
                                         (0.058, 0.032), (0.076, 0.032)], 6, INSULATOR_COLOURS[style])
        return Part(k, {"wire": vec(x + 0.024, 0, 0)})
    k.beam(vec(0, 0, 0), vec(reach * 0.6, 0, 0), 0.02, 0.02, colour)
    k.beam(vec(reach * 0.6, 0, 0.01), vec(reach * 0.6, 0, -0.07), 0.02, 0.02, colour, hint=_X)
    k.beam(vec(reach * 0.6, 0, -0.07), vec(reach * 0.6 + 0.08, 0, -0.09), 0.024, 0.03, CABLE)
    return Part(k, {"wire": vec(reach * 0.6 + 0.08, 0, -0.09)})


# ----------------------------------------------------------------------------- lamp, megaphone


def _head_cobra(k: Kit, tip, d, body, bowl) -> np.ndarray:
    """Cobra-head luminaire from the arm tip along ``d``; returns the point under the bowl."""
    up = frame(d, UP)[1]
    prof = [(-0.04, 0.030, 0.030), (0.04, 0.045, 0.050), (0.17, 0.060, 0.110), (0.48, 0.060, 0.125),
            (0.63, 0.045, 0.090), (0.68, 0.020, 0.040)]
    k.loft([ring(tip + d * t, d, ru, 8, UP, rv) for t, ru, rv in prof], body)
    mid = tip + d * 0.40 - up * 0.035
    k.loft([ring(mid, -up, 0.20, 8, d, 0.098), ring(mid - up * 0.05, -up, 0.15, 8, d, 0.062)], bowl)
    return mid - up * 0.06


def _head_led(k: Kit, tip, d, body, bowl) -> np.ndarray:
    """Flat LED luminaire from the arm tip along ``d``; returns the point under its panel."""
    up = frame(d, UP)[1]
    k.beam(tip - d * 0.04, tip + d * 0.10, 0.07, 0.06, shade(body, -20))
    k.beam(tip + d * 0.08, tip + d * 0.58, 0.24, 0.045, body)
    low = -up * 0.026
    k.beam(tip + d * 0.14 + low, tip + d * 0.54 + low, 0.19, 0.008, bowl)
    return tip + d * 0.34 - up * 0.03


def lamp(arm_m: float = 1.0, rise_deg: float = 15.0, style: str = "straight", head: str = "cobra",
         arm_colour: str = STEEL, body: str = LAMP_BODY, bowl: str = LAMP_BOWL) -> Part:
    """Street lamp: an arm clamped to the pole and a luminaire at its end, reaching along ``+x``.

    ``style``: ``straight`` (the arm leaves the pole at ``rise_deg``) or ``swan`` (it climbs the pole
    for half a metre, then bends out). ``head``: ``cobra`` or ``led``. Points: ``light`` (under the
    luminaire), ``feed`` (the low end of the arm, where its cable enters).
    """
    k = Kit()
    r = 0.022
    x0 = r + 0.014
    a = math.radians(rise_deg)
    if style == "swan":
        path = [vec(x0, 0, -0.45), vec(x0, 0, 0.30)]
        for i in range(1, 5):  # quarter bend of 0.25 m radius, ending at the rise angle
            q = (math.pi / 2 - a) * i / 4
            path.append(vec(x0 + 0.25 * (1 - math.cos(q)), 0, 0.30 + 0.25 * math.sin(q)))
        low = -0.45
    else:
        path = [vec(x0, 0, -0.35), vec(x0, 0, 0.0), vec(x0 + 0.07, 0, 0.07 + 0.07 * math.tan(a))]
        low = -0.35
    d = vec(math.cos(a), 0, math.sin(a))
    tip = path[-1] + d * arm_m
    path.append(tip)
    k.sweep(path, r, arm_colour, sides=6)
    for z in (low + 0.05, -0.05 if style == "straight" else 0.22):
        k.box(vec(0.0, -0.04, z - 0.02), vec(x0 + r * 0.5, 0.04, z + 0.02), shade(arm_colour, -40))
    light = (_head_cobra if head == "cobra" else _head_led)(k, tip, d, body, bowl)
    return Part(k, {"light": light, "feed": path[0], "tip": tip})


def megaphone(length: float = 0.42, mouth_r: float = 0.19, tilt_deg: float = 8.0,
              colour: str = HORN, standoff: float = 0.12) -> Part:
    """Horn loudspeaker of the village public address, pointing along ``+x`` and a little down."""
    k = Kit()
    a = math.radians(tilt_deg)
    d = vec(math.cos(a), 0, -math.sin(a))
    k.beam(vec(0, 0, 0), vec(standoff, 0, 0), 0.03, 0.03, STEEL_DARK)
    k.box(vec(0.0, -0.035, -0.07), vec(0.008, 0.035, 0.07), STEEL_DARK)
    p0 = vec(standoff - 0.02, 0, 0)
    k.tube(p0, p0 + d * 0.11, 0.055, shade(colour, -45), sides=8)
    q0 = p0 + d * 0.10
    stations = [i / 5 for i in range(6)]
    rings = [ring(q0 + d * (length * t), d, 0.035 + (mouth_r - 0.035) * t**1.5, 10)
             for t in stations]
    k.loft(rings, colour, cap1=False)
    k.loft(rings[2:], shade(colour, -60), cap1=False, inward=True)
    lip = [ring(q0 + d * length, d, mouth_r + e, 10) for e in (0.0, 0.012)]
    k.loft([lip[0], lip[1]], shade(colour, -15), cap0=False, cap1=False)
    k.loft([lip[0], lip[1]], shade(colour, -15), cap0=False, cap1=False, inward=True)
    return Part(k, {"feed": vec(0.03, 0, 0.03)})


# ----------------------------------------------------------------------------- boxes, marks


def junction_box(w: float = 0.2, h: float = 0.28, d: float = 0.11, colour: str = PLASTIC_GREY) -> Part:
    """Small box strapped to the pole; origin at the middle of its back. Points ``top``, ``bottom``."""
    k = Kit()
    x0, x1 = 0.012, 0.012 + d
    k.box(vec(x0, -w / 2, -h / 2), vec(x1, w / 2, h / 2), colour)
    k.box(vec(x1, -w / 2 + 0.015, -h / 2 + 0.015), vec(x1 + 0.006, w / 2 - 0.015, h / 2 - 0.015),
          shade(colour, -16))
    k.box(vec(x1 + 0.006, w / 2 - 0.045, -0.012), vec(x1 + 0.012, w / 2 - 0.025, 0.012), STEEL_DARK)
    for y in (-w / 4, w / 4):
        k.tube(vec((x0 + x1) / 2, y, -h / 2 - 0.035), vec((x0 + x1) / 2, y, -h / 2), 0.012, STEEL_DARK,
               sides=5)
    for z in (-h / 2 + 0.04, h / 2 - 0.04):
        k.box(vec(0.0, -w / 2 - 0.01, z - 0.012), vec(x0, w / 2 + 0.01, z + 0.012), STEEL)
    mid = (x0 + x1) / 2
    return Part(k, {"top": vec(mid, 0, h / 2), "bottom": vec(mid, 0, -h / 2 - 0.035)})


def meter_box(w: float = 0.36, h: float = 0.52, d: float = 0.18, colour: str = PLASTIC_GREY) -> Part:
    """Meter or fuse cabinet on the pole, with a small roof, a door and a window."""
    k = Kit()
    x0, x1 = 0.02, 0.02 + d
    k.box(vec(x0, -w / 2, -h / 2), vec(x1, w / 2, h / 2), colour)
    k.box(vec(x0 - 0.01, -w / 2 - 0.015, h / 2), vec(x1 + 0.025, w / 2 + 0.015, h / 2 + 0.025),
          shade(colour, -22))
    k.box(vec(x1, -w / 2 + 0.02, -h / 2 + 0.02), vec(x1 + 0.007, w / 2 - 0.02, h / 2 - 0.02),
          shade(colour, -12))
    k.box(vec(x1 + 0.007, -0.06, h / 2 - 0.16), vec(x1 + 0.010, 0.06, h / 2 - 0.07), "#2b3138")
    k.box(vec(x1 + 0.007, w / 2 - 0.06, -0.02), vec(x1 + 0.016, w / 2 - 0.035, 0.02), STEEL_DARK)
    for z in (-h / 2 + 0.06, h / 2 - 0.06):
        k.box(vec(0.0, -w / 2 - 0.01, z - 0.015), vec(x0, w / 2 + 0.01, z + 0.015), STEEL)
    mid = (x0 + x1) / 2
    return Part(k, {"top": vec(mid, 0, h / 2 + 0.025), "bottom": vec(mid, 0, -h / 2)})


def ground_cabinet(w: float = 0.5, d: float = 0.26, h: float = 1.1, colour: str = PLASTIC_WHITE) -> Part:
    """Plastic distribution cabinet standing on the ground on a concrete plinth, door facing ``+x``.

    Origin on the ground under its middle; the plinth goes 0.3 m into the ground so it also stands
    on a slope. A cabinet wider than 0.6 m has two doors.
    """
    k = Kit()
    z0 = 0.07
    k.box(vec(-d / 2 - 0.03, -w / 2 - 0.03, -0.3), vec(d / 2 + 0.03, w / 2 + 0.03, z0), "#9a988f")
    k.box(vec(-d / 2, -w / 2, z0), vec(d / 2, w / 2, z0 + h), colour)
    k.box(vec(-d / 2 - 0.012, -w / 2 - 0.012, z0 + h), vec(d / 2 + 0.012, w / 2 + 0.012, z0 + h + 0.035),
          shade(colour, -14))
    doors = [(-w / 2 + 0.025, w / 2 - 0.025)] if w <= 0.6 else [(-w / 2 + 0.025, -0.008), (0.008, w / 2 - 0.025)]
    x = d / 2
    for ya, yb in doors:
        k.box(vec(x, ya, z0 + 0.05), vec(x + 0.008, yb, z0 + h - 0.05), shade(colour, -9))
        k.box(vec(x + 0.008, yb - 0.05, z0 + h * 0.5), vec(x + 0.016, yb - 0.03, z0 + h * 0.5 + 0.05),
              STEEL_DARK)
        ym = (ya + yb) / 2
        for i in range(3):
            z = z0 + h - 0.13 - 0.03 * i
            k.box(vec(x + 0.008, ym - 0.07, z), vec(x + 0.011, ym + 0.07, z + 0.012), shade(colour, -70))
    ym = sum(doors[0]) / 2
    tri = [vec(x + 0.008, ym - 0.05, z0 + h * 0.62), vec(x + 0.008, ym + 0.05, z0 + h * 0.62),
           vec(x + 0.008, ym, z0 + h * 0.62 + 0.09)]
    k.loft([tri, [p + vec(0.003, 0, 0) for p in tri]], YELLOW)
    return Part(k, {"top": vec(0, 0, z0 + h + 0.035), "back": vec(-d / 2, 0, z0 + h * 0.8)})


def plate(w: float = 0.12, h: float = 0.08, colour: str = YELLOW) -> Part:
    """A flat sign on the pole surface, facing ``+x``."""
    k = Kit()
    k.box(vec(0.002, -w / 2, -h / 2), vec(0.006, w / 2, h / 2), colour)
    return Part(k)


def warning_mark(flip: bool = False) -> Part:
    """The red-on-white marker stuck near the foot of a pole: a white label with a red arrow."""
    k = Kit()
    k.box(vec(0.002, -0.07, -0.05), vec(0.005, 0.07, 0.05), WHITE)
    s = -1.0 if flip else 1.0
    tri = [vec(0.005, s * -0.05, -0.036), vec(0.005, s * 0.045, 0.0), vec(0.005, s * -0.05, 0.036)]
    if flip:
        tri.reverse()
    k.loft([tri, [p + vec(0.002, 0, 0) for p in tri]], RED)
    return Part(k)


def band(radius: float, h: float = 0.05, colour: str = STEEL_DARK, sides: int = 10) -> Part:
    """Steel strap around a round column of ``radius``, with its bolt lug at ``-x``; origin on the axis."""
    k = Kit()
    k.tube(vec(0, 0, -h / 2), vec(0, 0, h / 2), radius + 0.007, colour, sides=sides)
    k.box(vec(-radius - 0.035, -0.02, -h / 2), vec(-radius, 0.02, h / 2), colour)
    return Part(k)


# ----------------------------------------------------------------------------- the column


@dataclass
class Column:
    """The standing column(s) of a pole: where its surface is at a height and a compass direction.

    ``kind``: ``round`` (one tapered spun-concrete column), ``twin`` (two of them side by side along
    ``axis_deg``, touching all the way up) or ``slim`` (one thin column, the wooden field pole).
    """

    kind: str
    height: float
    r_base: float
    r_top: float
    axis_deg: float = 0.0

    def radius(self, z: float) -> float:
        t = min(1.0, max(0.0, z / self.height))
        return self.r_base + (self.r_top - self.r_base) * t

    def centres(self, z: float) -> list[np.ndarray]:
        if self.kind != "twin":
            return [vec(0, 0, z)]
        a = self.out(self.axis_deg) * self.radius(z)
        return [vec(0, 0, z) + a, vec(0, 0, z) - a]

    @staticmethod
    def out(az_deg: float) -> np.ndarray:
        a = math.radians(az_deg)
        return vec(math.cos(a), math.sin(a), 0)

    def reach(self, az_deg: float, z: float) -> float:
        """Distance from the pole axis to its surface in direction ``az_deg`` at height ``z``."""
        o = self.out(az_deg)
        return max(float(c @ o) for c in self.centres(z)) + self.radius(z)

    def surface(self, az_deg: float, z: float, gap: float = 0.0) -> np.ndarray:
        return vec(0, 0, z) + self.out(az_deg) * (self.reach(az_deg, z) + gap)


def build_column(k: Kit, col: Column, colour: str, rng, bands: tuple[float, ...] = (),
                 band_colour: str = RUST, sides: int = 10, buried: float = 0.5) -> None:
    """The column(s), weathered: every facet a little lighter or darker, damp and dark at the foot,
    with a couple of stains running down. A twin gets a steel clamp at each height in ``bands``."""
    zs = [-buried, 0.45]
    n = max(3, round((col.height - 0.45) / 1.15))
    zs += [0.45 + (col.height - 0.45) * i / n for i in range(1, n + 1)]
    base = rgb(colour)
    for ci in range(len(col.centres(0.0))):
        stains = {rng.randrange(sides): rng.randrange(1, len(zs) - 1) for _ in range(2)}

        def tone(band_i: int, side: int, stains=stains) -> list[int]:
            d = rng.randint(-6, 6)
            if band_i == 0:
                d -= 16
            if side in stains and band_i < stains[side]:
                d -= 11
            return shade(base, d)

        rings = [ring(col.centres(max(z, 0.0))[ci] + vec(0, 0, min(z, 0.0)), UP, col.radius(z), sides)
                 for z in zs]
        k.loft(rings, tone, cap_colour=base)
    if col.kind == "twin":
        half = sides // 2
        for z in bands:
            def stadium(zz: float, grow: float, z=z) -> list[np.ndarray]:
                pts = []
                for sgn, c in zip((1, -1), col.centres(z), strict=True):
                    start = col.axis_deg - 90 + (0 if sgn > 0 else 180)
                    for i in range(half + 1):
                        o = col.out(start + 180 * i / half)
                        pts.append(vec(c[0], c[1], zz) + o * (col.radius(z) + grow))
                return pts
            k.loft([stadium(z - 0.035, 0.008), stadium(z + 0.035, 0.008)], band_colour)
            for sgn in (1, -1):  # the bolts that pull the clamp together, at the waist
                p = vec(0, 0, z) + col.out(col.axis_deg + 90) * (sgn * (col.radius(z) + 0.02))
                k.beam(p - UP * 0.03, p + UP * 0.03, 0.05, 0.03, shade(band_colour, -10),
                       hint=col.out(col.axis_deg + 90))


def strut(k: Kit, col: Column, az_deg: float, spread: float, z_join: float, colour: str, rng,
          r_base: float = 0.115, r_top: float = 0.075, sides: int = 9, buried: float = 0.5) -> None:
    """A second, leaning column that props the pole: its foot stands ``spread`` metres out at
    ``az_deg`` and its head is clamped to the pole at ``z_join``."""
    foot = col.out(az_deg) * spread
    head = col.surface(az_deg, z_join, r_top * 0.6)
    d = unit(head - foot)
    foot = foot - d * buried
    n = 5
    base = rgb(colour)
    rings = [ring(foot + (head - foot) * i / n, d, r_base + (r_top - r_base) * i / n, sides)
             for i in range(n + 1)]
    k.loft(rings, lambda b, s: shade(base, rng.randint(-6, 6) - (14 if b == 0 else 0)), cap_colour=base)
    for dz in (-0.1, -0.5):
        z = z_join + dz
        k.beam(col.surface(az_deg + 90, z, 0.01), col.surface(az_deg - 90, z, 0.01) + col.out(az_deg) * 0.02,
               0.06, 0.05, RUST)


# ----------------------------------------------------------------------------- along the column


def riser(k: Kit, col: Column, az_deg: float, z0: float, z1: float, radius: float = 0.03,
          colour: str = PLASTIC_GREY, head: bool = True) -> np.ndarray:
    """A conduit pipe up the side of the pole from ``z0`` to ``z1``, held by saddle clips, with a
    weatherhead bend at the top when ``head``. Returns where its cable comes out."""
    out = col.out(az_deg)
    lo, hi = col.surface(az_deg, z0, radius + 0.004), col.surface(az_deg, z1, radius + 0.004)
    path = [lo, hi]
    if head:
        path += [hi + out * 0.05 + UP * 0.07, hi + out * 0.13 + UP * 0.05, hi + out * 0.16 - UP * 0.03]
    k.sweep(path, radius, colour, sides=6)
    n = max(2, round((z1 - z0) / 1.4))
    side = col.out(az_deg + 90)
    for i in range(n):
        p = lo + (hi - lo) * ((i + 0.5) / n)
        k.beam(p - side * (radius + 0.03) - out * radius * 0.4, p + side * (radius + 0.03) - out * radius * 0.4,
               radius * 1.5, 0.025, STEEL_DARK)
    return path[-1]


def down_cable(k: Kit, col: Column, az_deg: float, z0: float, z1: float, rng, radius: float = 0.011,
               colour: str = CABLE) -> list[np.ndarray]:
    """A cable running down the pole surface from ``z1`` to ``z0``, a little wavy. Returns its points
    (top first)."""
    n = max(2, round((z1 - z0) / 0.5))
    pts = []
    for i in range(n + 1):
        z = z1 + (z0 - z1) * i / n
        wob = 0.0 if i in (0, n) else rng.uniform(-5.0, 5.0)
        pts.append(col.surface(az_deg + wob, z, radius + 0.003))
    k.sweep(pts, radius, colour, sides=3)
    return pts


def slack_coil(k: Kit, top, plane, rng, loops: int = 3, rx: float = 0.13, ry: float = 0.32,
               radius: float = 0.011, colour: str = CABLE, out=None) -> None:
    """Spare cable hung up in loops: ``loops`` ovals hanging from ``top`` in the vertical plane along
    ``plane`` (a horizontal unit vector), each a little different, fanned along ``out``."""
    top, plane = np.asarray(top, float), np.asarray(plane, float)
    out = np.cross(plane, UP) if out is None else np.asarray(out, float)
    for i in range(loops):
        a, b = rx * rng.uniform(0.8, 1.25), ry * rng.uniform(0.75, 1.2)
        skew = rng.uniform(-0.25, 0.25)
        c = top - UP * b + out * (0.014 * i)
        pts = []
        for j in range(12):
            q = 2 * math.pi * j / 12
            pts.append(c + plane * (a * math.sin(q) + skew * b * (math.cos(q) - 1) * 0.5) + UP * (b * math.cos(q)))
        k.sweep(pts, radius, colour, sides=3, closed=True)


# ----------------------------------------------------------------------------- the parts list

PartBuilder = Callable[[], Part]


def _column_part(kind: str) -> Part:
    import random

    k = Kit()
    col = {"round": Column("round", 9.0, 0.16, 0.09), "twin": Column("twin", 9.4, 0.15, 0.09),
           "slim": Column("slim", 7.4, 0.11, 0.07)}[kind]
    bands = tuple(0.9 + 1.7 * i for i in range(5)) if kind == "twin" else ()
    build_column(k, col, "#7b756b" if kind == "slim" else CONCRETE, random.Random(1), bands)
    return Part(k)


def _surface_part(what: str) -> Part:
    """Riser, down cable and coil shown on a short piece of column."""
    import random

    k = Kit()
    rng = random.Random(2)
    col = Column("round", 9.0, 0.16, 0.09)
    stub = Kit()
    build_column(stub, col, CONCRETE, rng)
    lo, hi = (0.0, 3.2)
    k.tris = [t for t in stub.tris if lo - 0.6 <= t[:, 2].mean() <= hi]
    k.cols = [c for t, c in zip(stub.tris, stub.cols, strict=True) if lo - 0.6 <= t[:, 2].mean() <= hi]
    if what == "riser":
        riser(k, col, 0.0, 0.0, 2.9)
    else:
        pts = down_cable(k, col, 0.0, 0.3, 3.1, rng)
        slack_coil(k, pts[2], col.out(90), rng, out=col.out(0))
    return Part(k)


def _hang_part() -> Part:
    k = Kit()
    k.sweep(hang(vec(0, -0.6, 0.5), vec(0, 0.6, 0.3), 0.35), 0.011, CABLE)
    return Part(k)


# name -> (builder with the default look, what it is)
PARTS: dict[str, tuple[PartBuilder, str]] = {
    "column_round": (lambda: _column_part("round"), "spun-concrete column, tapered"),
    "column_twin": (lambda: _column_part("twin"), "two columns clamped together"),
    "column_slim": (lambda: _column_part("slim"), "thin field pole"),
    "crossarm": (crossarm, "steel crossarm with pin insulators"),
    "crossarm_braced": (lambda: crossarm(1.9, 6, "dark", "paired", "two", colour=STEEL_DARK),
                        "long braced crossarm, paired pins"),
    "insulator_brown": (lambda: insulator("brown"), "porcelain pin insulator"),
    "insulator_white": (lambda: insulator("white"), "white porcelain pin insulator"),
    "insulator_glass": (lambda: insulator("glass"), "glass pin insulator"),
    "insulator_dark": (lambda: insulator("dark"), "old dark pin insulator"),
    "top_pin": (top_pin, "insulator on top of the column"),
    "bracket_pin": (lambda: side_bracket("pin"), "side bracket with a pin insulator"),
    "bracket_spool": (lambda: side_bracket("spool"), "side fork with a spool insulator"),
    "bracket_hook": (lambda: side_bracket("hook"), "hook and clamp for a bundled cable"),
    "lamp_cobra": (lamp, "lamp arm with a cobra-head luminaire"),
    "lamp_swan_led": (lambda: lamp(1.3, 6.0, "swan", "led"), "swan-neck arm with an LED luminaire"),
    "megaphone": (megaphone, "public address horn"),
    "junction_box": (junction_box, "small box on the pole"),
    "meter_box": (meter_box, "meter or fuse cabinet on the pole"),
    "ground_cabinet": (ground_cabinet, "distribution cabinet beside the pole"),
    "ground_cabinet_wide": (lambda: ground_cabinet(0.8, 0.3, 1.25, PLASTIC_GREY), "two-door cabinet"),
    "band": (lambda: band(0.12), "steel strap"),
    "plate": (plate, "number or owner plate"),
    "warning_mark": (warning_mark, "red and white foot marker"),
    "riser": (lambda: _surface_part("riser"), "conduit pipe with a weatherhead"),
    "down_cable_coil": (lambda: _surface_part("cable"), "down cable with a slack coil"),
    "jumper": (_hang_part, "hanging cable between two points"),
}
