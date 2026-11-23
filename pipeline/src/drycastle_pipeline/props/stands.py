"""Forest stands and the canopy mask: age classes, the seeded stand field, the forest-edge distance, wet zones."""

import json
import math
from pathlib import Path

import numpy as np
import shapely
from shapely.geometry import shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.props.helpers import seeded

CANOPY_CELL_PX = 42  # the canopy mask is planted cell by cell (about 25 m at the photo's 0.6 m per pixel)


def age_class(ages: list[tuple[str, float, float]], rng) -> tuple[str, float]:
    """(name, scale factor) of a forest tree's age class, drawn by share; no classes: ("", 1)."""
    total = sum(share for _, share, _ in ages)
    if total <= 0:
        return "", 1.0
    pick = rng.random() * total
    for name, share, factor in ages:
        pick -= share
        if pick < 0:
            return name, factor
    return ages[-1][0], ages[-1][2]


def canopy_mask(cfg: dict):
    """(trees.json of the detector, canopy mask (True = canopy), west edge x and north edge y in EPSG:5514, metres per pixel)."""
    from PIL import Image

    ortho_dir = Path(cfg["ortho_dir"])
    doc = json.loads((ortho_dir / "trees.json").read_text())
    Image.MAX_IMAGE_PIXELS = None
    mask = np.asarray(Image.open(ortho_dir / doc["canopy"]["file"]).convert("L")) > 0
    minx, _, _, maxy = doc["canopy"]["bounds_epsg5514"]
    return doc, mask, minx, maxy, float(doc["canopy"]["pixel_m"])


STAND_NAMES = ("pine", "birch", "mixed", "thicket", "swamp")


class Stands:
    """The stand of every point of the forest (``props.trees.stands``): a seeded Voronoi field, water overriding it.

    One seed per ``cell_m`` grid cell, jittered inside the cell and drawn from the stands' shares, so the nearest of the 3 x 3
    surrounding seeds is the point's Voronoi cell; a forest of a few hectares has several. A point within ``swamp.reach_m`` of
    ``wet`` is swamp whatever the field says. Keyed by local position only, so reruns and chunk order do not matter.
    """

    def __init__(self, cfg: dict, wet: BaseGeometry | None) -> None:
        self.cfg = cfg
        self.cell = float(cfg["cell_m"])
        self.types = {k: v for k, v in cfg["types"].items()}
        self.names = list(self.types)
        self.shares = [float(self.types[k]["share"]) for k in self.names]
        self.seeds: dict[tuple[int, int], tuple[float, float, str]] = {}
        self.swamp = None
        if wet is not None and "swamp" in cfg:
            self.swamp = wet.buffer(float(cfg["swamp"]["reach_m"]))
            shapely.prepare(self.swamp)

    def seed(self, i: int, j: int) -> tuple[float, float, str]:
        got = self.seeds.get((i, j))
        if got is None:
            rng = seeded(f"stand:{self.cell}:{i}:{j}")
            name = rng.choices(self.names, weights=self.shares)[0]
            got = ((i + rng.random()) * self.cell, (j + rng.random()) * self.cell, name)
            self.seeds[(i, j)] = got
        return got

    def noise(self, x: float, y: float) -> str:
        i, j = int(x // self.cell), int(y // self.cell)
        best = min(
            (self.seed(a, b) for a in (i - 1, i, i + 1) for b in (j - 1, j, j + 1)),
            key=lambda s: (s[0] - x) ** 2 + (s[1] - y) ** 2,
        )
        return best[2]

    def at(self, x: float, y: float) -> str:
        if self.swamp is not None and shapely.contains_xy(self.swamp, x, y):
            return "swamp"
        return self.noise(x, y)


def edge_distance(mask: np.ndarray, row: int, col: int, res: float, reach_m: float, gap_m: float) -> float:
    """Metres from pixel (row, col) of the canopy mask to the nearest open ground within ``reach_m`` (``reach_m`` when none).

    Open ground is where the canopy fraction within ``gap_m`` is under a half, so a speck of a gap in the mask is no edge.
    Off the raster counts as canopy: the map's border is no forest edge.
    """
    k = max(1, int(round(gap_m / res / 2)))
    r = int(math.ceil(reach_m / res))
    h, w = mask.shape
    r0, r1, c0, c1 = row - r - k, row + r + k + 1, col - r - k, col + r + k + 1
    win = np.ones((r1 - r0, c1 - c0), np.float32)
    a0, a1, b0, b1 = max(r0, 0), min(r1, h), max(c0, 0), min(c1, w)
    win[a0 - r0 : a1 - r0, b0 - c0 : b1 - c0] = mask[a0:a1, b0:b1]
    n = 2 * k + 1
    acc = np.pad(win.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    mean = (acc[n:, n:] - acc[:-n, n:] - acc[n:, :-n] + acc[:-n, :-n]) / (n * n)  # (2r+1)^2: the window of every pixel within r
    rr, cc = np.nonzero(mean < 0.5)
    if rr.size == 0:
        return reach_m
    return min(reach_m, float(np.sqrt((rr - r) ** 2 + (cc - r) ** 2).min()) * res)


class EdgeField:
    """:func:`edge_distance` for many pixels of one mask: where the open ground is, worked out once for the whole raster."""

    def __init__(self, mask: np.ndarray, res: float, reach_m: float, gap_m: float):
        k = max(1, round(gap_m / res / 2))
        self.r = r = math.ceil(reach_m / res)
        self.res, self.reach_m = res, reach_m
        n = 2 * k + 1
        pad = np.pad(mask.astype(np.int32), r + k, constant_values=1)  # off the raster counts as canopy
        acc = np.pad(pad.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
        total = acc[n:, n:] - acc[:-n, n:] - acc[n:, :-n] + acc[:-n, :-n]
        self.open = 2 * total < n * n  # canopy fraction under a half; [row + i, col + j] is the pixel (row - r + i, col - r + j)

    def distance(self, row: int, col: int) -> float:
        r = self.r
        rr, cc = np.nonzero(self.open[row : row + 2 * r + 1, col : col + 2 * r + 1])
        if rr.size == 0:
            return self.reach_m
        return min(self.reach_m, float(np.sqrt((rr - r) ** 2 + (cc - r) ** 2).min()) * self.res)


def wet_zone(units: list[dict], kinds: set[str] | frozenset[str], water: BaseGeometry | None) -> BaseGeometry | None:
    """Where there is water: ground units of a water kind (ponds, streams; as :func:`water.plan_water` reads them) and the
    wet areas of the water patches (``water``, see :func:`water_zone`). Local metres; None without any."""
    parts = [shape(u["polygon"]).buffer(0) for u in units if u["kind"] in kinds]
    if water is not None:
        parts.append(water)
    return unary_union(parts) if parts else None
