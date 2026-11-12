"""Meadow: the density raster of the ground clutter that the game generates near the player (``meadow.bin``).

Fields, meadows and forests are about 400 ha. Stored clutter instances there would be tens of millions, so the
detail stage writes only a coarse raster instead: per chunk, per layer, one byte per 2 m cell, "how much of this
layer stands here" (0 = nothing, 255 = the layer's ``max_per_cell``). The game (``MapLoader.Meadow.cs``) turns the
cells around the player into instances with a hash of the cell index, so every client sees the same ground and
nothing is sent over the network.

* **Where**: field and forest parcels only (:data:`MEADOW_TYPES`); gardens, plots and road verges keep their stored
  instances in ``clutter.json``. A cell is on the raster only if the centre and the four corners of its jitter
  window (``jitter_m`` around the centre) are on free ground: not on buildings, fences, roads, placed objects or
  hand-drawn regions, and on the terrain. Trunks are not cut out here (the loader drops instances within a
  trunk's reach from ``trees.json``).
* **How much** (per layer and parcel type, 0..1 of ``max_per_cell``): ``interior`` everywhere, ``edge`` within the
  recipe's edge band of the boundary, ``tone`` on tone patches, ``litter`` on leaf litter, ``tree`` near a trunk,
  ``bare`` on dirt and gravel (replaces: no tufts on a patch of soil, pebbles instead); furrows get nothing.
  Then a smooth value noise (``noise_m`` lattice, ``noise_amp``) makes patches of thicker and thinner growth.
* **Zones** (format 3): one more plane per chunk, the *zone byte* of a cell. Its high nibble is the *mix* (0..15): a patch of
  ground where one species dominates, 3 to 8 m across (a Voronoi patch per ``patch_m``, so the shares are exact), with
  overrides (tall grass where nobody mows, along fences, walls and building bases; the wet mix beside water; the dry mix in a
  field's edge band). Each layer lists, per mix, which kinds it makes and in what shares, so a tall-meadow cell makes tall
  grass and a poppy field makes poppies. The low nibble is the *dryness* (8 = neutral): a bias of the tint, fresher beside
  water and fences, drier at a field's edge (:data:`DRY_STEP` tint units a step).
* **File**: ``MEAD`` magic, little endian. Header: u16 format (3), u16 cell size in mm, u16 cells per chunk side,
  f32 chunk size, f32 jitter, f32 trunk reach (a generated instance within this many metres times the tree's
  scale of a trunk is dropped), u8 kind count, per kind (u8 length + UTF-8) name and u8 flags (bit 0: its instances carry a
  tint); u8 layer count, per layer (u8 length + UTF-8) name, f32 max per cell, f32 scale min, f32 scale max, u8 mix count, per
  mix u8 entry count and per entry u8 kind index, f32 share; u32 chunk count, per chunk (i32 ix, i32 iy, u32 offset, u32
  length) with the offset from the start of the data block; then the data block: per chunk the zlib of the
  planes, layer by layer and the zone plane last, each ``cells x cells`` bytes row by row (y from the south edge, x from the
  west edge).
"""

import math
import struct
import zlib
from dataclasses import dataclass, field

import numpy as np
import shapely
from shapely.geometry import Point
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

MEADOW_FORMAT_VERSION = 3
DRY_STEP = 10.0  # tint units per step of the zone byte's dryness nibble (8 = neutral)
# Per-instance tint (clutter.json's optional sixth number, and the instances the game generates): one byte, 128 = the
# mesh's own colours, below it fresher (darker, greener), above it drier (the loader multiplies the vertex colours
# by detail_meshes.tint_rgb). It follows a smooth value noise over the ground (TONE_LATTICE_CELLS cells of 2 m) plus
# a small per-instance jitter, so a stretch of grass has one mood instead of white noise.
NEUTRAL_TINT = 128
TONE_LATTICE_CELLS = 12.0
TONE_SALT = 2000
TONE_AMP = 100.0
TONE_JITTER = 14.0
MEADOW_TYPES = ("field", "forest")
MAGIC = b"MEAD"
_BARE_LAYERS = ("dirt", "verge_dirt", "verge_gravel", "gravel")


def hash01(ix: np.ndarray, iy: np.ndarray, salt: int) -> np.ndarray:
    """A deterministic number in [0, 1) per integer pair (64-bit mixing, the same on every machine)."""
    with np.errstate(over="ignore"):
        x = ix.astype(np.int64).view(np.uint64) * np.uint64(0x9E3779B97F4A7C15)
        x ^= iy.astype(np.int64).view(np.uint64) * np.uint64(0xC2B2AE3D27D4EB4F)
        x ^= np.uint64((salt * 0x165667B19E3779F9) & 0xFFFFFFFFFFFFFFFF)
        x ^= np.uint64(0x243F6A8885A308D3)
        x ^= x >> np.uint64(33)
        x *= np.uint64(0xFF51AFD7ED558CCD)
        x ^= x >> np.uint64(33)
        x *= np.uint64(0xC4CEB9FE1A85EC53)
        x ^= x >> np.uint64(33)
    return (x >> np.uint64(11)).astype(np.float64) / float(1 << 53)


def value_noise(gx: np.ndarray, gy: np.ndarray, lattice: float, salt: int) -> np.ndarray:
    """Smooth noise in [0, 1) at the global cell indices (bilinear between lattice corners of ``lattice`` cells)."""
    fx, fy = gx / lattice, gy / lattice
    x0, y0 = np.floor(fx).astype(np.int64), np.floor(fy).astype(np.int64)
    tx, ty = fx - x0, fy - y0
    tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)
    a, b = hash01(x0, y0, salt), hash01(x0 + 1, y0, salt)
    c, d = hash01(x0, y0 + 1, salt), hash01(x0 + 1, y0 + 1, salt)
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty


def voronoi01(gx: np.ndarray, gy: np.ndarray, lattice: float, salt: int) -> np.ndarray:
    """A number in [0, 1) per Voronoi patch (one random point per ``lattice``-cell square, ties to the first): every cell of a
    patch gets the same number, and patches are about ``lattice`` cells across. Uniform across patches."""
    fx, fy = np.asarray(gx, float) / lattice, np.asarray(gy, float) / lattice
    ix, iy = np.floor(fx).astype(np.int64), np.floor(fy).astype(np.int64)
    best, out = np.full(fx.shape, np.inf), np.zeros(fx.shape)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            cx, cy = ix + dx, iy + dy
            d = (fx - (cx + hash01(cx, cy, salt))) ** 2 + (fy - (cy + hash01(cx, cy, salt + 1))) ** 2
            better = d < best
            best = np.where(better, d, best)
            out = np.where(better, hash01(cx, cy, salt + 2), out)
    return out


def tone_noise(gx, gy) -> np.ndarray:
    """The smooth tone field at cell coordinates (floats allowed: metres / cell size), in [0, 1)."""
    return value_noise(np.asarray(gx, float), np.asarray(gy, float), TONE_LATTICE_CELLS, TONE_SALT)


def tint_byte(noise, jitter01, bias=0.0) -> np.ndarray:
    """The tint byte of a point: the tone ``noise`` (0..1), a per-instance ``jitter01`` (0..1) and a ``bias`` in tint units
    (negative = fresher, positive = drier). floor(x + 0.5) so the game rounds the same way."""
    t = np.floor(NEUTRAL_TINT + (np.asarray(noise) - 0.5) * 2 * TONE_AMP + (np.asarray(jitter01) - 0.5) * 2 * TONE_JITTER + bias + 0.5)
    return np.clip(t, 0, 255).astype(np.int64)


@dataclass
class MeadowLayer:
    """A layer of the raster: at most ``max_per_cell`` instances per cell, scaled in ``scale``. ``mixes[m]`` is what the layer
    makes in a cell of mix ``m``: ``(kind, share)`` entries (shares add up to 1, f32 exact like the file); an id past the
    list uses mix 0."""

    name: str
    max_per_cell: float
    scale: tuple[float, float]
    mixes: list[list[tuple[str, float]]]

    def __post_init__(self) -> None:
        self.mixes = [[(k, float(np.float32(w))) for k, w in mix] for mix in self.mixes]

    @property
    def kinds(self) -> list[str]:
        return list(dict.fromkeys(k for mix in self.mixes for k, _ in mix))

    def mix(self, mix_id: int) -> list[tuple[str, float]]:
        return self.mixes[mix_id if mix_id < len(self.mixes) else 0]


@dataclass
class Meadow:
    """The raster of the village: ``chunks[(ix, iy)]`` is a ``(layers + 1, n, n)`` uint8 array (the zone plane last)."""

    cell_m: float
    chunk_m: float
    layers: list[MeadowLayer]
    jitter_m: float = 0.85
    trunk_reach_m: float = 0.7
    tint_kinds: frozenset[str] = frozenset()  # kinds whose instances carry a tint byte
    chunks: dict[tuple[int, int], np.ndarray] = field(default_factory=dict)

    @property
    def n(self) -> int:
        return int(round(self.chunk_m / self.cell_m))

    def array(self, key: tuple[int, int]) -> np.ndarray:
        if key not in self.chunks:
            self.chunks[key] = np.zeros((len(self.layers) + 1, self.n, self.n), np.uint8)
        return self.chunks[key]

    def set_cells(self, gx: np.ndarray, gy: np.ndarray, values: np.ndarray) -> None:
        """``values`` is ``(layers + 1, len(gx))`` (the zone byte last); the cells are global indices (floor of metres / cell size)."""
        n = self.n
        cx, cy = gx // n, gy // n
        for key in sorted(set(zip(cx.tolist(), cy.tolist(), strict=True))):
            sel = (cx == key[0]) & (cy == key[1])
            self.array(key)[:, gy[sel] % n, gx[sel] % n] = values[:, sel]

    def nonzero(self) -> dict[str, int]:
        return {
            layer.name: int(sum(int(np.count_nonzero(a[i])) for a in self.chunks.values()))
            for i, layer in enumerate(self.layers)
        }

    def expected(self) -> dict[str, float]:
        """Expected instances per layer over the whole village."""
        return {
            layer.name: float(sum(a[i].sum(dtype=np.int64) for a in self.chunks.values())) / 255.0 * layer.max_per_cell
            for i, layer in enumerate(self.layers)
        }

    def busiest(self, radius_m: dict[str, float]) -> dict[str, int]:
        """The most expected instances of a layer in a square of ``2 * radius`` metres inside one chunk, times
        pi / 4 (the disc the loader draws). Windows across chunk borders are not counted: an estimate."""
        out = {}
        for i, layer in enumerate(self.layers):
            w = max(1, int(radius_m.get(layer.name, 70.0) / self.cell_m)) * 2
            best = 0.0
            for a in self.chunks.values():
                e = a[i].astype(np.float64) / 255.0 * layer.max_per_cell
                if not e.any():
                    continue
                s = np.pad(e.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
                w_ = min(w, e.shape[0])
                win = s[w_:, w_:] - s[:-w_, w_:] - s[w_:, :-w_] + s[:-w_, :-w_]
                best = max(best, float(win.max()))
            out[layer.name] = int(best * math.pi / 4)
        return out


def meadow_layers(cfg: dict) -> list[MeadowLayer]:
    """The layers of the rules' ``meadow`` section: ``mixes`` (a list, index = mix id) override the layer's own ``kinds``."""
    mixes = cfg.get("mixes") or [{}]
    out = []
    for name, s in cfg["layers"].items():
        per_mix = [[(k, float(w)) for k, w in (mix.get(name) or s["kinds"])] for mix in mixes]
        out.append(MeadowLayer(name, float(s["max_per_cell"]), (float(s["scale"][0]), float(s["scale"][1])), per_mix))
    return out


def new_meadow(cfg: dict, chunk_m: float) -> Meadow:
    from drycastle_pipeline.detail_meshes import TINTABLE

    cell = float(cfg["cell_m"])
    if abs(chunk_m / cell - round(chunk_m / cell)) > 1e-9:
        raise ValueError(f"detail.meadow.cell_m {cell} must divide the chunk size {chunk_m}")
    layers = meadow_layers(cfg)
    used = {k for layer in layers for k in layer.kinds}
    return Meadow(cell, chunk_m, layers, float(cfg["jitter_m"]), float(cfg["trunk_reach_m"]), frozenset(used & TINTABLE))


def _inside(geom: BaseGeometry | None, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    if geom is None or geom.is_empty or not len(xs):
        return np.zeros(len(xs), bool)
    return shapely.contains_xy(geom, xs, ys)


def _smoothstep(x: np.ndarray, lo: float, hi: float) -> np.ndarray:
    t = np.clip((x - lo) / (hi - lo), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def clump(gx: np.ndarray, gy: np.ndarray, amount: float, lattice_cells: float, salt: int) -> np.ndarray:
    """A density factor around 1 that makes growth patchy: ``amount`` 0 = none, 1 = bare gaps between thick clumps
    (a smoothed value noise of ``lattice_cells``; the mean stays near 1)."""
    return (1.0 - amount) + amount * 2.0 * _smoothstep(value_noise(gx, gy, lattice_cells, salt), 0.3, 0.7)


def zone_cells(meadow: Meadow, ctx, atype: str, gx: np.ndarray, gy: np.ndarray, edge: np.ndarray, cfg: dict) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """The zone byte of cells (global indices) of a parcel part, plus the masks of the cells nobody mows (``unmown``: within
    ``unmown_m`` of a fence, a wall or the edge of a building) and of those beside water. Mix: a Voronoi patch of
    ``patch_m`` picks it from the type's shares (``zones.<type>.mixes``, ``[[mix id, share], ...]``); the rules'
    ``unmown_mix`` overrides it where nobody mows, ``wet_mix`` beside water (``wet_m``), ``edge_dry_mix`` in half of the
    edge band (``edge_dry_share``), when the type has them. Dryness: neutral 8, plus ``dry_edge`` in the edge band, minus
    ``fresh_unmown`` where nobody mows and ``fresh_water`` beside water."""
    zc, cell = cfg["zones"][atype], meadow.cell_m
    cx, cy = (gx + 0.5) * cell, (gy + 0.5) * cell
    owner = voronoi01(gx + 0.5, gy + 0.5, float(cfg["patch_m"]) / cell, 3000)
    ids = np.array([int(i) for i, _ in zc["mixes"]])
    cum = np.cumsum([float(w) for _, w in zc["mixes"]])
    cum /= cum[-1]
    mix = ids[np.minimum(np.searchsorted(cum, owner, side="right"), len(ids) - 1)]
    dry = np.full(len(gx), 8)
    unmown = np.zeros(len(gx), bool)
    reach = float(cfg["unmown_m"])
    x0, y0 = cx.min() - reach, cy.min() - reach
    x1, y1 = cx.max() + reach, cy.max() + reach
    parts = [f for f in ctx.fences if f.bounds[0] <= x1 and f.bounds[2] >= x0 and f.bounds[1] <= y1 and f.bounds[3] >= y0]
    parts += [b.exterior for b in ctx.buildings if b.bounds[0] <= x1 and b.bounds[2] >= x0 and b.bounds[1] <= y1 and b.bounds[3] >= y0]
    if parts:
        unmown = shapely.distance(shapely.points(cx, cy), unary_union(parts)) < reach
    wet = np.zeros(len(gx), bool)
    wet_m = float(cfg["wet_m"])
    if ctx.water is not None and not ctx.water.is_empty:
        wet = shapely.distance(shapely.points(cx, cy), ctx.water) < wet_m
    if "edge_dry_mix" in zc:
        dryer = hash01(gx, gy, 3100) < float(zc["edge_dry_share"])
        mix = np.where(edge & dryer, int(zc["edge_dry_mix"]), mix)
    if "wet_mix" in zc:
        mix = np.where(wet, int(zc["wet_mix"]), mix)
    if "unmown_mix" in zc:
        mix = np.where(unmown, int(zc["unmown_mix"]), mix)
    if "dry_edge" in zc:
        dry = dry + np.where(edge, int(zc["dry_edge"]), 0)
    dry = dry - np.where(unmown, int(cfg["fresh_unmown"]), 0) - np.where(wet, int(cfg["fresh_water"]), 0)
    zone = (mix.astype(np.int64) << 4) | np.clip(dry, 0, 15)
    return zone.astype(np.uint8), unmown, wet


def add_parcel(meadow: Meadow, ctx, res, atype: str, free: BaseGeometry, cfg: dict, edge_band_m: float) -> int:
    """Puts the cells of one parcel part on the raster. ``free`` is the part's free ground (no buildings, fences,
    roads, objects, regions), ``res`` its detail result (the patches say where soil, litter and tones are).
    Returns the number of cells."""
    if free.is_empty:
        return 0
    cell, jit = meadow.cell_m, float(cfg["jitter_m"])
    x0, y0, x1, y1 = free.bounds
    gxs = np.arange(math.floor(x0 / cell), math.ceil(x1 / cell))
    gys = np.arange(math.floor(y0 / cell), math.ceil(y1 / cell))
    if not len(gxs) or not len(gys):
        return 0
    gx, gy = (a.ravel() for a in np.meshgrid(gxs, gys))
    cx, cy = (gx + 0.5) * cell, (gy + 0.5) * cell
    shapely.prepare(free)
    ok = _inside(free, cx, cy)
    for sx, sy in ((-jit, -jit), (jit, -jit), (jit, jit), (-jit, jit)):
        idx = np.flatnonzero(ok)
        ok[idx] = _inside(free, cx[idx] + sx, cy[idx] + sy)
    if not ok.any():
        return 0
    gx, gy, cx, cy = gx[ok], gy[ok], cx[ok], cy[ok]

    def zone(layers: tuple[str, ...]) -> np.ndarray:
        g = unary_union([p.geom for p in res.patches if p.layer in layers])
        return _inside(g, cx, cy)

    bare, furrow, litter = zone(_BARE_LAYERS), zone(("furrow", "bed")), zone(("litter",))
    tone = zone(("tone_a", "tone_b"))
    edge = np.zeros(len(gx), bool)
    if edge_band_m > 0:
        edge = ~_inside(free.buffer(-edge_band_m), cx, cy)
    near_tree = np.zeros(len(gx), bool)
    reach = float(cfg["tree_reach_m"])
    for tx, ty, _ in ctx.trees:
        if x0 - reach <= tx <= x1 + reach and y0 - reach <= ty <= y1 + reach:
            near_tree |= (cx - tx) ** 2 + (cy - ty) ** 2 <= (reach + cell * 0.5) ** 2
    zone_byte, unmown, _wet = zone_cells(meadow, ctx, atype, gx, gy, edge, cfg)

    amp, lattice = float(cfg["noise_amp"]), float(cfg["noise_m"]) / cell
    clump_cells = float(cfg["clump_m"]) / cell
    values = np.zeros((len(meadow.layers) + 1, len(gx)), np.uint8)
    for li, layer in enumerate(meadow.layers):
        spec = cfg[atype].get(layer.name)
        if not spec:
            continue
        d = np.full(len(gx), float(spec.get("interior", 0.0)))
        for mask, key in ((edge, "edge"), (tone, "tone"), (litter, "litter"), (near_tree, "tree")):
            if key in spec:
                d = np.where(mask, np.maximum(d, float(spec[key])), d)
        if "bare" in spec:
            d = np.where(bare, float(spec["bare"]), d)
        d = np.where(furrow, 0.0, d)
        noise = value_noise(gx, gy, lattice, 1000 + li)
        d = d * (1.0 - amp + 2.0 * amp * noise)
        lc = cfg["layers"][layer.name]
        if float(lc.get("clump", 0.0)) > 0:  # growth in clumps with gaps between them
            d = d * clump(gx + 0.5, gy + 0.5, float(lc["clump"]), clump_cells, 1500 + li)
        if "unmown" in lc:  # taller, thicker growth where nobody mows
            d = np.where(unmown, d * float(lc["unmown"]), d)
        values[li] = np.rint(np.clip(d, 0.0, 1.0) * 255).astype(np.uint8)
    keep = values[:-1].any(axis=0)
    if keep.any():
        values[-1] = zone_byte
        meadow.set_cells(gx[keep], gy[keep], values[:, keep])
    return int(keep.sum())


# --- instances: the generator the game runs (MapLoader.Meadow / MeadowGen.cs mirror it exactly) -------------


def generate(meadow: Meadow, gx0: int, gx1: int, gy0: int, gy1: int) -> list[dict]:
    """The instances of the cells ``gx0 <= gx < gx1``, ``gy0 <= gy < gy1`` (global cell indices), as
    ``{"kind", "x", "y", "rot", "scale"}`` (and ``"tint"``), from the raster and a hash of the cell: layer ``li``, cell
    ``(gx, gy)`` and salt ``li * 256 + s`` draw the numbers. The count is floor(e) plus one with probability frac(e), where
    e = value / 255 * max_per_cell (salt 0); instance ``j`` (at most 49) uses salts ``1 + 5j`` (x), ``2 + 5j`` (y),
    ``3 + 5j`` (turn), ``4 + 5j`` (scale), ``5 + 5j`` (kind). Position: cell centre plus ``(2h - 1) * jitter``;
    turn ``h * tau``; scale ``lo + h * (hi - lo)``. Kind: the layer's mix for the cell's zone (high nibble of the zone byte),
    the first entry whose running share exceeds ``h``. A tint kind also gives ``"tint"``: :func:`tint_byte` of the tone
    noise at the cell centre, the jitter hash of salt ``0x10000 + li * 256 + j`` and the bias ``(dryness - 8) * DRY_STEP``.
    No trunk or terrain test here: the game does that."""
    n, out = meadow.n, []
    nl = len(meadow.layers)
    for cy in range(math.floor(gy0 / n), math.floor((gy1 - 1) / n) + 1):
        for cx in range(math.floor(gx0 / n), math.floor((gx1 - 1) / n) + 1):
            arr = meadow.chunks.get((cx, cy))
            if arr is None:
                continue
            lx0, lx1 = max(gx0, cx * n) - cx * n, min(gx1, (cx + 1) * n) - cx * n
            ly0, ly1 = max(gy0, cy * n) - cy * n, min(gy1, (cy + 1) * n) - cy * n
            for li, layer in enumerate(meadow.layers):
                sub = arr[li, ly0:ly1, lx0:lx1]
                iy, ix = np.nonzero(sub)
                if not len(ix):
                    continue
                gx, gy = ix + lx0 + cx * n, iy + ly0 + cy * n
                zone = arr[nl, iy + ly0, ix + lx0]
                e = sub[iy, ix].astype(np.float64) / 255.0 * layer.max_per_cell
                whole = np.floor(e)
                count = (whole + (hash01(gx, gy, li * 256) < e - whole)).astype(int)
                for j in range(min(int(count.max(initial=0)), 49)):
                    sel = count > j
                    x, y, z = gx[sel], gy[sel], zone[sel]
                    h = [hash01(x, y, li * 256 + k + 5 * j) for k in (1, 2, 3, 4, 5)]
                    px = (x + 0.5) * meadow.cell_m + (h[0] * 2 - 1) * meadow.jitter_m
                    py = (y + 0.5) * meadow.cell_m + (h[1] * 2 - 1) * meadow.jitter_m
                    noise = tone_noise(x + 0.5, y + 0.5)
                    jit = hash01(x, y, 0x10000 + li * 256 + j)
                    for k in range(len(x)):
                        mix, acc = layer.mix(int(z[k]) >> 4), 0.0
                        kind = mix[-1][0]
                        for name, share in mix:
                            acc += share
                            if h[4][k] < acc:
                                kind = name
                                break
                        inst = {"kind": kind, "x": float(px[k]), "y": float(py[k]), "rot": float(h[2][k] * math.tau),
                                "scale": layer.scale[0] + float(h[3][k]) * (layer.scale[1] - layer.scale[0])}
                        if kind in meadow.tint_kinds:
                            bias = ((int(z[k]) & 15) - 8) * DRY_STEP
                            inst["tint"] = int(tint_byte(noise[k], jit[k], bias))
                        out.append(inst)
    return out


# --- file -----------------------------------------------------------------------------------


def _s(text: str) -> bytes:
    b = text.encode()
    return bytes([len(b)]) + b


def encode(meadow: Meadow) -> bytes:
    """The ``meadow.bin`` bytes (see the module docstring)."""
    kinds = list(dict.fromkeys(k for layer in meadow.layers for k in layer.kinds))
    head = bytearray(MAGIC)
    head += struct.pack("<HHHfffB", MEADOW_FORMAT_VERSION, int(round(meadow.cell_m * 1000)), meadow.n, meadow.chunk_m,
                        meadow.jitter_m, meadow.trunk_reach_m, len(kinds))
    for k in kinds:
        head += _s(k) + bytes([1 if k in meadow.tint_kinds else 0])
    head += bytes([len(meadow.layers)])
    for layer in meadow.layers:
        head += _s(layer.name) + struct.pack("<fffB", layer.max_per_cell, layer.scale[0], layer.scale[1], len(layer.mixes))
        for mix in layer.mixes:
            head += bytes([len(mix)])
            for k, w in mix:
                head += struct.pack("<Bf", kinds.index(k), w)
    blobs, directory, offset = [], [], 0
    for key in sorted(meadow.chunks):
        arr = meadow.chunks[key]
        if not arr[:-1].any():
            continue
        blob = zlib.compress(arr.tobytes(), 9)
        directory.append(struct.pack("<iiII", key[0], key[1], offset, len(blob)))
        blobs.append(blob)
        offset += len(blob)
    return bytes(head) + struct.pack("<I", len(directory)) + b"".join(directory) + b"".join(blobs)


def decode(data: bytes) -> Meadow:
    """Reads ``meadow.bin`` back (tests, previews)."""
    if data[:4] != MAGIC:
        raise ValueError("not a meadow file")
    pos = 4
    ver, cell_mm, n, chunk_m, jitter, reach, nkinds = struct.unpack_from("<HHHfffB", data, pos)
    pos += struct.calcsize("<HHHfffB")
    if ver != MEADOW_FORMAT_VERSION:
        raise ValueError(f"meadow format {ver}")

    def s() -> str:
        nonlocal pos
        size = data[pos]
        text = data[pos + 1:pos + 1 + size].decode()
        pos += 1 + size
        return text

    kinds, tinted = [], set()
    for _ in range(nkinds):
        kinds.append(s())
        if data[pos] & 1:
            tinted.add(kinds[-1])
        pos += 1
    count = data[pos]
    pos += 1
    layers = []
    for _ in range(count):
        name = s()
        mx, lo, hi, nmix = struct.unpack_from("<fffB", data, pos)
        pos += 13
        mixes = []
        for _m in range(nmix):
            entries, pos = data[pos], pos + 1
            mix = []
            for _e in range(entries):
                idx, w = struct.unpack_from("<Bf", data, pos)
                pos += 5
                mix.append((kinds[idx], w))
            mixes.append(mix)
        layers.append(MeadowLayer(name, mx, (lo, hi), mixes))
    (chunks,) = struct.unpack_from("<I", data, pos)
    pos += 4
    entries = [struct.unpack_from("<iiII", data, pos + 16 * i) for i in range(chunks)]
    base = pos + 16 * chunks
    out = Meadow(cell_m=cell_mm / 1000.0, chunk_m=chunk_m, layers=layers, jitter_m=jitter, trunk_reach_m=reach, tint_kinds=frozenset(tinted))
    for ix, iy, off, size in entries:
        raw = zlib.decompress(data[base + off:base + off + size])
        out.chunks[(ix, iy)] = np.frombuffer(raw, np.uint8).reshape(len(layers) + 1, n, n).copy()
    return out
