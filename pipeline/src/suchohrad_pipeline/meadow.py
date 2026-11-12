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
* **File**: ``MEAD`` magic, little endian. Header: u16 format, u16 cell size in mm, u16 cells per chunk side,
  f32 chunk size, f32 jitter, f32 trunk reach (a generated instance within this many metres times the tree's
  scale of a trunk is dropped), u8 layer count, per layer (u8 length + UTF-8 each) name, kind and alt kind, f32 alt share,
  f32 max per cell, f32 scale min, f32 scale max; u32 chunk count, per chunk (i32 ix, i32 iy, u32 offset, u32
  length) with the offset from the start of the data block; then the data block: per chunk the zlib of the
  layers, layer by layer, each ``cells x cells`` bytes row by row (y from the south edge, x from the west edge).
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

MEADOW_FORMAT_VERSION = 1
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


@dataclass
class MeadowLayer:
    name: str
    kind: str
    alt_kind: str
    alt_share: float
    max_per_cell: float
    scale: tuple[float, float]


@dataclass
class Meadow:
    """The raster of the village: ``chunks[(ix, iy)]`` is a ``(layers, n, n)`` uint8 array."""

    cell_m: float
    chunk_m: float
    layers: list[MeadowLayer]
    jitter_m: float = 0.85
    trunk_reach_m: float = 0.7
    chunks: dict[tuple[int, int], np.ndarray] = field(default_factory=dict)

    @property
    def n(self) -> int:
        return int(round(self.chunk_m / self.cell_m))

    def array(self, key: tuple[int, int]) -> np.ndarray:
        if key not in self.chunks:
            self.chunks[key] = np.zeros((len(self.layers), self.n, self.n), np.uint8)
        return self.chunks[key]

    def set_cells(self, gx: np.ndarray, gy: np.ndarray, values: np.ndarray) -> None:
        """``values`` is ``(layers, len(gx))``; the cells are global indices (floor of metres / cell size)."""
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
    out = []
    for name, s in cfg["layers"].items():
        out.append(MeadowLayer(name, s["kind"], s.get("alt_kind", ""), float(s.get("alt_share", 0.0)),
                               float(s["max_per_cell"]), (float(s["scale"][0]), float(s["scale"][1]))))
    return out


def new_meadow(cfg: dict, chunk_m: float) -> Meadow:
    cell = float(cfg["cell_m"])
    if abs(chunk_m / cell - round(chunk_m / cell)) > 1e-9:
        raise ValueError(f"detail.meadow.cell_m {cell} must divide the chunk size {chunk_m}")
    return Meadow(cell, chunk_m, meadow_layers(cfg), float(cfg["jitter_m"]), float(cfg["trunk_reach_m"]))


def _inside(geom: BaseGeometry | None, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    if geom is None or geom.is_empty or not len(xs):
        return np.zeros(len(xs), bool)
    return shapely.contains_xy(geom, xs, ys)


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

    amp, lattice = float(cfg["noise_amp"]), float(cfg["noise_m"]) / cell
    values = np.zeros((len(meadow.layers), len(gx)), np.uint8)
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
        d = np.clip(d * (1.0 - amp + 2.0 * amp * noise), 0.0, 1.0)
        values[li] = np.rint(d * 255).astype(np.uint8)
    keep = values.any(axis=0)
    if keep.any():
        meadow.set_cells(gx[keep], gy[keep], values[:, keep])
    return int(keep.sum())


# --- instances: the generator the game runs (MapLoader.Meadow / MeadowGen.cs mirror it exactly) -------------


def generate(meadow: Meadow, gx0: int, gx1: int, gy0: int, gy1: int) -> list[dict]:
    """The instances of the cells ``gx0 <= gx < gx1``, ``gy0 <= gy < gy1`` (global cell indices), as
    ``{"kind", "x", "y", "rot", "scale"}``, from the raster and a hash of the cell: layer ``li``, cell ``(gx, gy)``
    and salt ``li * 256 + s`` draw the numbers. The count is floor(e) plus one with probability frac(e), where
    e = value / 255 * max_per_cell (salt 0); instance ``j`` (at most 49) uses salts ``1 + 5j`` (x), ``2 + 5j`` (y),
    ``3 + 5j`` (turn), ``4 + 5j`` (scale), ``5 + 5j`` (alt kind). Position: cell centre plus ``(2h - 1) * jitter``;
    turn ``h * tau``; scale ``lo + h * (hi - lo)``. No trunk or terrain test here: the game does that."""
    n, out = meadow.n, []
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
                e = sub[iy, ix].astype(np.float64) / 255.0 * layer.max_per_cell
                whole = np.floor(e)
                count = (whole + (hash01(gx, gy, li * 256) < e - whole)).astype(int)
                for j in range(min(int(count.max(initial=0)), 49)):
                    sel = count > j
                    x, y = gx[sel], gy[sel]
                    h = [hash01(x, y, li * 256 + k + 5 * j) for k in (1, 2, 3, 4, 5)]
                    px = (x + 0.5) * meadow.cell_m + (h[0] * 2 - 1) * meadow.jitter_m
                    py = (y + 0.5) * meadow.cell_m + (h[1] * 2 - 1) * meadow.jitter_m
                    for k in range(len(x)):
                        kind = layer.alt_kind if layer.alt_kind and h[4][k] < layer.alt_share else layer.kind
                        out.append({"kind": kind, "x": float(px[k]), "y": float(py[k]), "rot": float(h[2][k] * math.tau),
                                    "scale": layer.scale[0] + float(h[3][k]) * (layer.scale[1] - layer.scale[0])})
    return out


# --- file -----------------------------------------------------------------------------------


def _s(text: str) -> bytes:
    b = text.encode()
    return bytes([len(b)]) + b


def encode(meadow: Meadow) -> bytes:
    """The ``meadow.bin`` bytes (see the module docstring)."""
    head = bytearray(MAGIC)
    head += struct.pack("<HHHfffB", MEADOW_FORMAT_VERSION, int(round(meadow.cell_m * 1000)), meadow.n, meadow.chunk_m,
                        meadow.jitter_m, meadow.trunk_reach_m, len(meadow.layers))
    for layer in meadow.layers:
        head += _s(layer.name) + _s(layer.kind) + _s(layer.alt_kind)
        head += struct.pack("<ffff", layer.alt_share, layer.max_per_cell, layer.scale[0], layer.scale[1])
    blobs, directory, offset = [], [], 0
    for key in sorted(meadow.chunks):
        arr = meadow.chunks[key]
        if not arr.any():
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
    ver, cell_mm, n, chunk_m, jitter, reach, count = struct.unpack_from("<HHHfffB", data, pos)
    pos += struct.calcsize("<HHHfffB")
    if ver != MEADOW_FORMAT_VERSION:
        raise ValueError(f"meadow format {ver}")

    def s() -> str:
        nonlocal pos
        size = data[pos]
        text = data[pos + 1:pos + 1 + size].decode()
        pos += 1 + size
        return text

    layers = []
    for _ in range(count):
        name, kind, alt = s(), s(), s()
        share, mx, lo, hi = struct.unpack_from("<ffff", data, pos)
        pos += 16
        layers.append(MeadowLayer(name, kind, alt, share, mx, (lo, hi)))
    (chunks,) = struct.unpack_from("<I", data, pos)
    pos += 4
    entries = [struct.unpack_from("<iiII", data, pos + 16 * i) for i in range(chunks)]
    base = pos + 16 * chunks
    out = Meadow(cell_m=cell_mm / 1000.0, chunk_m=chunk_m, layers=layers, jitter_m=jitter, trunk_reach_m=reach)
    for ix, iy, off, size in entries:
        raw = zlib.decompress(data[base + off:base + off + size])
        out.chunks[(ix, iy)] = np.frombuffer(raw, np.uint8).reshape(len(layers), n, n).copy()
    return out
