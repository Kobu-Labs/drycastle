"""Orthographic cameras: view directions, framing, layout of the picture, scale-bar lengths."""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

VIEWS = ("front", "three_quarter", "top", "back")
AZIMUTH, ELEVATION = 35.0, 28.0  # three-quarter: from the front, turned toward +X, above
HUMAN_M, HUMAN_WIDTH_M, HUMAN_GAP_M = 1.8, 0.5, 0.3
HUMAN_RANGE_M = (0.5, 30.0)  # largest model dimension for which the human is drawn
MIN_HEIGHT_M = 0.3  # ... and only when the model is at least this tall


# A view that shows (almost) nothing, e.g. the front of a flat road, is redrawn from higher up.
FALLBACK_ELEVATION = {"front": 35.0, "back": 35.0, "top": 65.0}


def view_axes(name: str, elevation: float | None = None) -> tuple[np.ndarray, np.ndarray]:
    """(direction from the model to the camera, up hint). Front = camera on +Z (glTF front).

    elevation tilts front/back/top to that many degrees above the horizon (top: from the front).
    """
    if elevation is not None:
        e = math.radians(elevation)
        z = -1.0 if name == "back" else 1.0
        return np.array([0, math.sin(e), z * math.cos(e)]), np.array([0.0, 1.0, 0.0])
    az, el = math.radians(AZIMUTH), math.radians(ELEVATION)
    table = {
        "front": ((0, 0, 1), (0, 1, 0)),
        "back": ((0, 0, -1), (0, 1, 0)),
        "top": ((0, 1, 0), (0, 0, -1)),  # the model's front is at the bottom of the picture
        "three_quarter": (
            (math.cos(el) * math.sin(az), math.sin(el), math.cos(el) * math.cos(az)),
            (0, 1, 0),
        ),
    }
    d, up = table[name]
    return np.array(d, float), np.array(up, float)


def rotation(name: str, elevation: float | None = None) -> np.ndarray:
    """Rows: right, up, towards-camera."""
    d, up = view_axes(name, elevation)
    d = d / np.linalg.norm(d)
    right = np.cross(up, d)
    right /= np.linalg.norm(right)
    return np.stack([right, np.cross(d, right), d])


def nice_length(max_m: float) -> tuple[float, str]:
    """Largest 1/2/5 x 10^k metres not above max_m, with its label."""
    max_m = max(max_m, 1e-6)
    e = math.floor(math.log10(max_m))
    best = max(m * 10.0**e for m in (1, 2, 5) if m * 10.0**e <= max_m * (1 + 1e-9))
    return best, length_label(best)


def length_label(m: float) -> str:
    if m >= 1000:
        return f"{m / 1000:g} km"
    if m >= 1:
        return f"{m:g} m"
    if m >= 0.01:
        return f"{m * 100:g} cm"
    return f"{m * 1000:g} mm"


@dataclass
class Layout:
    name: str
    size: int
    rot: np.ndarray  # (3,3)
    mvp: np.ndarray  # (4,4) world -> NDC, column vectors
    px_per_m: float
    width_m: float  # extent of the model in the picture plane
    height_m: float
    depth_range_m: float
    model_box: tuple[float, float, float, float]  # left, top, right, bottom in pixels
    human_box: tuple[float, float, float, float] | None
    bar_m: float
    bar_label: str
    bar_y: float
    bar_x: float
    elevation: float | None = None  # set when the view was tilted up to show something

    @property
    def top_down(self) -> bool:
        return self.name == "top" and self.elevation is None


def make_layout(
    name: str, pts: np.ndarray, size: int = 768, human: bool = False, elevation: float | None = None
) -> Layout:
    k = size / 768
    left, right, top, bottom = 28 * k, size - 28 * k, 52 * k, size - 76 * k
    rot = rotation(name, elevation)
    v = pts.astype(np.float64) @ rot.T
    lo, hi = v.min(0), v.max(0)
    wm, hm = max(hi[0] - lo[0], 1e-9), max(hi[1] - lo[1], 1e-9)
    wc = wm + (HUMAN_GAP_M + HUMAN_WIDTH_M if human else 0.0)
    hc = max(hm, HUMAN_M) if human else hm
    s = min((right - left) / wc, (bottom - top) / hc)
    cx0 = left + ((right - left) - wc * s) / 2
    ybot = top + ((bottom - top) + hc * s) / 2
    zr = (hi[2] - lo[2]) * 1.1 + 2e-4
    zmax = hi[2] + 0.05 * (hi[2] - lo[2]) + 1e-4
    m = np.eye(4)
    m[0, :3], m[0, 3] = 2 * s / size * rot[0], 2 * (cx0 - lo[0] * s) / size - 1
    m[1, :3], m[1, 3] = 2 * s / size * rot[1], 1 - 2 * (ybot + lo[1] * s) / size
    m[2, :3], m[2, 3] = -2 / zr * rot[2], -1 + 2 * zmax / zr
    hbox = None
    if human:
        hx = cx0 + (wm + HUMAN_GAP_M) * s
        hbox = (hx, ybot - HUMAN_M * s, hx + HUMAN_WIDTH_M * s, ybot)
    bar_m, label = nice_length(0.3 * (right - left) / s)
    return Layout(
        name,
        size,
        rot,
        m,
        s,
        wm,
        hm,
        zr,
        (cx0, ybot - hm * s, cx0 + wm * s, ybot),
        hbox,
        bar_m,
        label,
        size - 34 * k,
        left,
        elevation,
    )


def wants_human(lo: np.ndarray, hi: np.ndarray) -> bool:
    """Human-scale things only; a flat sheet or a road has a width but nothing to stand beside."""
    size = hi - lo
    return HUMAN_RANGE_M[0] <= float(size.max()) <= HUMAN_RANGE_M[1] and size[1] >= MIN_HEIGHT_M
