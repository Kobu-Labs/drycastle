"""Dominant colours of a masked render, named in everyday words."""

import numpy as np
from pydantic import BaseModel

COLOR_NAMES: dict[str, tuple[int, int, int]] = {
    "red": (200, 30, 30),
    "dark red": (120, 20, 25),
    "orange": (235, 130, 30),
    "brown": (130, 80, 40),
    "dark brown": (70, 45, 30),
    "beige": (225, 205, 165),
    "tan": (190, 150, 100),
    "yellow": (240, 210, 50),
    "olive": (120, 120, 40),
    "light green": (150, 210, 110),
    "green": (60, 150, 60),
    "dark green": (30, 80, 40),
    "teal": (30, 130, 130),
    "light blue": (140, 190, 230),
    "blue": (40, 90, 200),
    "navy": (25, 35, 95),
    "purple": (120, 50, 150),
    "pink": (240, 150, 180),
    "magenta": (200, 40, 160),
    "white": (245, 245, 245),
    "light grey": (200, 200, 200),
    "grey": (128, 128, 128),
    "dark grey": (65, 65, 65),
    "black": (15, 15, 15),
}
GREY_NAMES = ("white", "light grey", "grey", "dark grey", "black")

MAX_SAMPLES = 20000
K = 8
ITERATIONS = 15
MERGE_DELTA_E = 8.0
SAME_NAME_DELTA_E = 15.0
MIN_SHARE = 0.02
GREY_CHROMA = 8.0  # below this a colour is only ever named a grey

_D65 = np.array([0.95047, 1.0, 1.08883])
_RGB_TO_XYZ = np.array(
    [
        [0.4124564, 0.3575761, 0.1804375],
        [0.2126729, 0.7151522, 0.0721750],
        [0.0193339, 0.1191920, 0.9503041],
    ]
)


class DominantColor(BaseModel):
    hex: str
    name: str
    share: float


def _to_lab(rgb: np.ndarray) -> np.ndarray:
    """Plain sRGB (0..255, shape Nx3) to CIE Lab D65."""
    c = np.asarray(rgb, dtype=np.float64) / 255.0
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    t = (lin @ _RGB_TO_XYZ.T) / _D65
    f = np.where(t > 216 / 24389, np.cbrt(t), (24389 / 27 * t + 16) / 116)
    return np.stack([116 * f[:, 1] - 16, 500 * (f[:, 0] - f[:, 1]), 200 * (f[:, 1] - f[:, 2])], 1)


_NAMES = list(COLOR_NAMES)
_REF_LAB = _to_lab(np.array(list(COLOR_NAMES.values())))
_IS_GREY = np.array([n in GREY_NAMES for n in _NAMES])


def color_name(rgb: tuple[int, int, int]) -> str:
    lab = _to_lab(np.array([rgb]))[0]
    dist = np.linalg.norm(_REF_LAB - lab, axis=1)
    if np.hypot(lab[1], lab[2]) < GREY_CHROMA:
        dist = np.where(_IS_GREY, dist, np.inf)
    return _NAMES[int(np.argmin(dist))]


def _kmeans(lab: np.ndarray, k: int, rng: np.random.Generator) -> np.ndarray:
    """Cluster index per point; k-means++ seeding from a seeded generator."""
    centres = [lab[rng.integers(len(lab))]]
    d2 = ((lab - centres[0]) ** 2).sum(1)
    for _ in range(k - 1):
        if d2.sum() <= 0:
            break
        centres.append(lab[rng.choice(len(lab), p=d2 / d2.sum())])
        d2 = np.minimum(d2, ((lab - centres[-1]) ** 2).sum(1))
    c = np.array(centres)
    for _ in range(ITERATIONS):
        labels = _nearest(lab, c)
        counts = np.bincount(labels, minlength=len(c))
        for ch in range(3):
            sums = np.bincount(labels, weights=lab[:, ch], minlength=len(c))
            c[counts > 0, ch] = sums[counts > 0] / counts[counts > 0]
    return _nearest(lab, c)


def _nearest(lab: np.ndarray, centres: np.ndarray) -> np.ndarray:
    # argmin of |x-c|^2 = |c|^2 - 2 x.c (|x|^2 is constant per row)
    return ((centres**2).sum(1) - 2 * lab @ centres.T).argmin(1)


class _Cluster:
    def __init__(self, n: int, lab_sum: np.ndarray, rgb_sum: np.ndarray):
        self.n, self.lab_sum, self.rgb_sum = n, lab_sum, rgb_sum

    @property
    def lab(self) -> np.ndarray:
        return self.lab_sum / self.n

    @property
    def rgb(self) -> tuple[int, int, int]:
        r, g, b = np.rint(self.rgb_sum / self.n).astype(int)
        return int(r), int(g), int(b)

    def merged(self, other: "_Cluster") -> "_Cluster":
        return _Cluster(
            self.n + other.n, self.lab_sum + other.lab_sum, self.rgb_sum + other.rgb_sum
        )


def _merge_pair(clusters: list[_Cluster]) -> tuple[int, int] | None:
    """Closest pair that is near enough, or near-ish and named the same."""
    best, best_d = None, np.inf
    names = [color_name(c.rgb) for c in clusters]
    for i in range(len(clusters)):
        for j in range(i + 1, len(clusters)):
            d = float(np.linalg.norm(clusters[i].lab - clusters[j].lab))
            limit = SAME_NAME_DELTA_E if names[i] == names[j] else MERGE_DELTA_E
            if d < limit and d < best_d:
                best, best_d = (i, j), d
    return best


def dominant_colors(rgb: np.ndarray, mask: np.ndarray, max_colors: int = 5) -> list[DominantColor]:
    idx = np.flatnonzero(mask)
    if len(idx) == 0:
        return []
    rng = np.random.default_rng(0)
    if len(idx) > MAX_SAMPLES:
        idx = np.sort(rng.choice(idx, MAX_SAMPLES, replace=False))
    pixels = rgb.reshape(-1, 3)[idx].astype(np.float64)
    lab = _to_lab(pixels)
    labels = _kmeans(lab, min(K, len(lab)), rng)

    counts = np.bincount(labels)
    clusters = [
        _Cluster(int(counts[i]), lab[labels == i].sum(0), pixels[labels == i].sum(0))
        for i in np.flatnonzero(counts)
    ]
    while (pair := _merge_pair(clusters)) is not None:
        i, j = pair
        clusters[i] = clusters[i].merged(clusters[j])
        del clusters[j]

    total = len(idx)
    kept = [c for c in clusters if c.n / total >= MIN_SHARE] or [max(clusters, key=lambda c: c.n)]
    kept.sort(key=lambda c: -c.n)
    return [
        DominantColor(
            hex="#{:02x}{:02x}{:02x}".format(*c.rgb),
            name=color_name(c.rgb),
            share=round(c.n / total, 3),
        )
        for c in kept[:max_colors]
    ]
