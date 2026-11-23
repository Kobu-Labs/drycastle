"""Building blocks for hand-modelled landmarks laid out on a real footprint (map metres, z up from the building base).

Lifted from ``buildings/landmarks/garage.py``, the reference model; ``.claude/skills/photo-to-building`` describes how a new building is
modelled with them from photos. Everything adds convex solids to a ``landmarks._Solids``; ``to_mesh`` turns it into the
``Mesh`` a model returns. Flat colours only ([r, g, b]); every function is deterministic.

  ``Frame``            oblique axes on two footprint edges: ``at(a, v)`` to map metres, ``av(p)`` back
  ``check_footprint``  refuse to build on a footprint that changed
  ``prism``, ``edge``  a prism on a convex polygon; a piece along one footprint edge (the two primitives)
  ``wall``             a wall on an edge, with holes for openings and a sloped top
  ``window``, ``plinth``, ``door_frame``   trim on the face of a wall
  ``boundary``         a footprint side as ``a = f(v)``, for roofs on footprints that are not rectangles
  ``sheet_roof``       a lean-to plane of standing-seam sheet (also tiles in courses: see its ``sheets``)
  ``gable_roof``       two such planes meeting at a ridge, with the gable triangles left to ``wall`` tops
"""

import itertools
import math
import random
from collections.abc import Callable, Sequence

import numpy as np

from drycastle_pipeline.buildings.landmarks.catalog import _Solids
from drycastle_pipeline.core.mesh import Mesh

Colour = list[int]
Pt = tuple[float, float]


def unit(p, q) -> Pt:
    n = math.dist(p, q)
    return ((q[0] - p[0]) / n, (q[1] - p[1]) / n)


class Frame:
    """Axes on the footprint: origin ``o``, ``a`` towards ``a_to``, ``v`` towards ``v_to`` (need not be square to each other)."""

    def __init__(self, o, a_to, v_to) -> None:
        self.o, self.A, self.V = tuple(o), unit(o, a_to), unit(o, v_to)
        self._det = self.A[0] * self.V[1] - self.A[1] * self.V[0]

    def at(self, a: float, v: float) -> Pt:
        return (self.o[0] + self.A[0] * a + self.V[0] * v, self.o[1] + self.A[1] * a + self.V[1] * v)

    def av(self, p) -> Pt:
        """(a, v) of a map point."""
        dx, dy = p[0] - self.o[0], p[1] - self.o[1]
        return ((dx * self.V[1] - dy * self.V[0]) / self._det, (self.A[0] * dy - self.A[1] * dx) / self._det)


def check_footprint(model: str, b: dict, rings: dict[str, Sequence[Pt]]) -> None:
    """Raise when the footprint of ``b`` is not the ring the model was laid out on (same corners, same order)."""
    want, got = rings[b["id"]], b["footprint"]["exterior"]
    if len(got) != len(want) or any(math.dist(p, q) > 0.05 for p, q in zip(got, want)):
        raise ValueError(f"{model}: the footprint of {b['id']} changed; the {model} is laid out on the old one")


def to_mesh(S: _Solids) -> Mesh:
    return Mesh(np.array(S.tris, float).reshape(-1, 3, 3), np.array(S.cols, np.uint8).reshape(-1, 3))


def prism(S: _Solids, corners, z0, z1, side, top=None, bottom=None) -> None:
    """A prism on a convex polygon (map metres); ``z0`` and ``z1`` give the bottom and the top at each corner."""
    lo = [(x, y, z) for (x, y), z in zip(corners, z0)]
    hi = [(x, y, z) for (x, y), z in zip(corners, z1)]
    faces = [(lo, bottom or side), (hi, top or side)]
    n = len(corners)
    faces += [([lo[k], lo[(k + 1) % n], hi[(k + 1) % n], hi[k]], side) for k in range(n)]
    S.solid(faces)


def edge(S: _Solids, p, q, s0, s1, d0, d1, z0, z1, colour) -> None:
    """A piece along edge p->q of a counter-clockwise ring: ``s`` along it, ``d`` outward (negative is into the building).
    ``z0`` and ``z1`` are numbers, or (at s0, at s1) pairs for a sloped bottom or top. Seen from outside, ``s`` runs left
    to right."""
    ux, uy = unit(p, q)
    nx, ny = uy, -ux
    b0, b1 = z0 if isinstance(z0, tuple) else (z0, z0)
    t0, t1 = z1 if isinstance(z1, tuple) else (z1, z1)
    c = [(p[0] + ux * s + nx * d, p[1] + uy * s + ny * d) for s, d in ((s0, d0), (s1, d0), (s1, d1), (s0, d1))]
    prism(S, c, (b0, b1, b1, b0), (t0, t1, t1, t0), colour)


def wall(S: _Solids, p, q, top_p: float, top_q: float, thickness: float, colour, holes=(), base: float = -0.3) -> None:
    """The wall on edge p->q, ``thickness`` inward, from ``base`` up to a top that runs from ``top_p`` to ``top_q``.
    ``holes`` are (s0, s1, z0, z1) openings. A doorway is a hole from the base (z0 = 0): the floor slab fills its foot."""
    length = math.dist(p, q)

    def top(s):
        return top_p + (top_q - top_p) * s / length

    s = 0.0
    for h0, h1, z0, z1 in sorted(holes):
        if h0 > s:
            edge(S, p, q, s, h0, -thickness, 0.0, base, (top(s), top(h0)), colour)
        if z0 > 0:
            edge(S, p, q, h0, h1, -thickness, 0.0, base, z0, colour)
        edge(S, p, q, h0, h1, -thickness, 0.0, z1, (top(h0), top(h1)), colour)
        s = h1
    if length > s:
        edge(S, p, q, s, length, -thickness, 0.0, base, (top(s), top(length)), colour)


def plinth(S: _Solids, p, q, colour, height: float = 0.32, gaps=(), base: float = -0.3) -> None:
    """A weathered band along the foot of the wall on edge p->q, broken at ``gaps`` ((s0, s1): doors)."""
    s = 0.0
    for g0, g1 in [*sorted(gaps), (math.dist(p, q),) * 2]:
        if g0 > s:
            edge(S, p, q, s, g0, 0.0, 0.012, base, height, colour)
        s = max(s, g1)


def door_frame(S: _Solids, p, q, s0, s1, top: float, colour, width: float = 0.09) -> None:
    """A frame round a doorway (s0..s1, floor to ``top``) on the face of the wall on edge p->q."""
    edge(S, p, q, s0 - width, s0, 0.0, 0.03, 0.0, top + width, colour)
    edge(S, p, q, s1, s1 + width, 0.0, 0.03, 0.0, top + width, colour)
    edge(S, p, q, s0, s1, 0.0, 0.03, top, top + width, colour)


def window(S: _Solids, p, q, s0, s1, z0, z1, frame, glass, casements: int = 1, bars=None, bars_n: tuple[int, int] = (0, 0)) -> None:
    """A window drawn on the face of a solid wall on edge p->q (no hole): frame, sill, glass, ``casements`` side by
    side, and with ``bars`` a grille of ``bars_n`` (upright, level) bars in that colour."""
    edge(S, p, q, s0 - 0.07, s1 + 0.07, 0.0, 0.035, z0 - 0.07, z1 + 0.07, frame)
    edge(S, p, q, s0, s1, 0.0, 0.045, z0, z1, glass)
    edge(S, p, q, s0 - 0.1, s1 + 0.1, 0.0, 0.08, z0 - 0.12, z0 - 0.07, frame)  # sill
    for k in range(1, casements):
        s = s0 + (s1 - s0) * k / casements
        edge(S, p, q, s - 0.03, s + 0.03, 0.0, 0.055, z0, z1, frame)
    if bars:
        for k in range(1, bars_n[0] + 1):
            s = s0 + (s1 - s0) * k / (bars_n[0] + 1)
            edge(S, p, q, s - 0.008, s + 0.008, 0.045, 0.07, z0, z1, bars)
        for k in range(1, bars_n[1] + 1):
            z = z0 + (z1 - z0) * k / (bars_n[1] + 1)
            edge(S, p, q, s0, s1, 0.045, 0.065, z - 0.008, z + 0.008, bars)


def boundary(frame: Frame, pts) -> Callable[[float], float]:
    """``a`` as a function of ``v`` along a footprint side given by its corners, held flat beyond its ends."""
    k = sorted((frame.av(p)[1], frame.av(p)[0]) for p in pts)

    def f(v: float) -> float:
        if v <= k[0][0]:
            return k[0][1]
        for (v0, a0), (v1, a1) in itertools.pairwise(k):
            if v <= v1:
                return a0 + (a1 - a0) * (v - v0) / (v1 - v0)
        return k[-1][1]

    return f


def roof_piece(S: _Solids, frame: Frame, roof_z, lo, hi, a0, a1, v0, v1, z0, z1, top, side=None, bottom=None, clip=True) -> None:
    """One piece of a roof plane: ``a0..a1`` (clipped to the plane's edges ``lo(v)..hi(v)``) by ``v0..v1``, from ``z0``
    to ``z1`` above the underside ``roof_z(a)``."""
    ends = [(max(lo(v), min(hi(v), a)) if clip else a, v) for a, v in ((a0, v0), (a1, v0), (a1, v1), (a0, v1))]
    zs = [roof_z(a) for a, _ in ends]
    prism(S, [frame.at(a, v) for a, v in ends], [z + z0 for z in zs], [z + z1 for z in zs], side or top, top, bottom)


def sheet_roof(
    S: _Solids, frame: Frame, roof_z, lo, hi, v_lo: float, v_hi: float, detail: bool, *, seed: str, sheets, worn, streaks,
    seam, underside, thickness: float = 0.1, panel: float = 0.55, worn_share: float = 0.14, worn_run: float = 0.55,
    streak_share: float = 0.12, worn_streak_share: float = 0.45, seam_height: float = 0.04, collision=(128, 128, 128),
) -> None:  # fmt: skip
    """One roof plane that slopes along ``a`` (underside ``roof_z(a)``), between ``lo(v)`` and ``hi(v)``, from ``v_lo`` to
    ``v_hi``. Visual: a sheet between every two standing seams, each ONE piece from eave to top in a colour of
    ``sheets`` (or of ``worn``: sheets that weathered, which come in runs), some with a full-length streak of a colour of
    ``streaks`` beside a seam. Never cut a sheet across: stepped patches read as a zig-zag. The pattern is fixed by
    ``seed``. Collision (``detail`` False): one slab. Tiles and shingles have no standing seams: ``seam_height=0`` and a
    narrow ``panel`` give plain strips of slightly different shades."""
    if not detail:
        roof_piece(S, frame, roof_z, lo, hi, -9.0, 99.0, v_lo, v_hi, 0.0, 0.3, list(collision))
        return
    rng = random.Random(seed)
    is_worn = False
    n = round((v_hi - v_lo) / panel)
    step = (v_hi - v_lo) / n
    for k in range(n):
        v0, v1 = v_lo + k * step, v_lo + (k + 1) * step
        a0, a1 = min(lo(v0), lo(v1)), max(hi(v0), hi(v1))
        is_worn = rng.random() < (worn_run if is_worn else worn_share)
        base = [c + rng.randint(-4, 4) for c in rng.choice(worn if is_worn else sheets)]
        streak = rng.uniform(0.25, 0.5) * step if rng.random() < (worn_streak_share if is_worn else streak_share) else 0.0
        roof_piece(S, frame, roof_z, lo, hi, a0, a1, v0 + streak, v1, 0.0, thickness, base, seam, underside)
        if streak:
            roof_piece(S, frame, roof_z, lo, hi, a0, a1, v0, v0 + streak, 0.0, thickness, rng.choice(streaks), seam, underside)
        if k and seam_height > 0:
            roof_piece(S, frame, roof_z, lo, hi, a0, a1, v0 - 0.02, v0 + 0.02, thickness, thickness + seam_height, seam)


def gable_roof(
    S: _Solids, frame: Frame, width: float, v_lo: float, v_hi: float, eave_z: float, ridge_z: float, detail: bool, *,
    overhang: float = 0.3, ridge_at: float = 0.5, **style,
) -> Callable[[float], float]:  # fmt: skip
    """A gabled roof over ``a`` in 0..``width``, the ridge along ``v`` at ``ridge_at`` of the width (0.5: the middle; else
    one slope is longer and flatter than the other): two ``sheet_roof`` planes (``style`` is passed on; the seed gets a
    suffix per plane). Returns the underside ``z(a)``: use it for the tops of the gable walls."""
    half = width * ridge_at

    def z(a: float) -> float:
        return eave_z + (ridge_z - eave_z) * (1 - abs(a - half) / (half if a < half else width - half))

    seed = style.pop("seed")
    sheet_roof(S, frame, z, lambda v: -overhang, lambda v: half, v_lo, v_hi, detail, seed=seed + ":a", **style)
    sheet_roof(S, frame, z, lambda v: half, lambda v: width + overhang, v_lo, v_hi, detail, seed=seed + ":b", **style)
    return z
