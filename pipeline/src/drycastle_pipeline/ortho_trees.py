"""Where the trees are, read from the orthophoto -> ``data/village/ortho/{trees.json,canopy.png}``.

A derived, local result, not a map stage and not yet an input of the map: ``make ortho`` must have
downloaded the photo. The licence of the photo forbids redistributing and altering it, so only facts
read from it (tree positions, a canopy mask) are written, into the gitignored ``data/``, never to
``out/``. See docs/ortho.md.

    python -m drycastle_pipeline.ortho_trees

Two detectors, because the two kinds of tree look different from above:

* **Canopy** (forest, dense tree belts): a small logistic regression on the colour and the brightness
  texture of each pixel at three scales, trained on the photo itself. The training labels come free:
  OSM forest and wood polygons are "tree", meadow, farmland, buildings and roads are "not tree" (each
  polygon pulled in 15 m from its edge, where the tag is least exact). It sees context, so dark
  meadow is not mistaken for forest.
* **Standalone trees** (gardens, hedges, roadsides): compact green blobs darker than their ring, found
  at three crown sizes, one circle per crown. A tree is accepted where its relative darkness beats a
  threshold that is strict in open ground and sensitive where the neighbourhood is somewhat tree-like
  (gardens, hedges), and never inside the canopy or on a building.

``trees.json`` lists the standalone trees (local map metres, crown radius, score); the canopy is a
1-bit mask with the photo's pixel grid (``canopy.png``, bounds in the json). Where the canopy is, the
generator decides the trunks; the photo does not resolve single crowns in a forest.
"""

import argparse
import json
import zlib
from collections.abc import Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from shapely.geometry import Point, Polygon, shape

from drycastle_pipeline.config import map_paths

FORMAT_VERSION = 1
N_FEATURES = 17  # of ``features``; the model sees them and their squares
STRIP_ROWS = 1024  # the photo is processed in strips of this many rows ...
STRIP_MARGIN = 64  # ... plus this many each side, so filters see across the cut (3 sigma of the widest)
NOTICE = "Facts read from the orthophoto (GKU Bratislava, NLC): reference only, not to be shipped."


@dataclass(frozen=True)
class Params:
    tau_open: float = 0.18  # relative darkness a blob needs in open ground
    tau_wooded: float = 0.10  # ... where the surroundings are somewhat tree-like (gardens, hedges)
    wooded_p: float = 0.12  # mean canopy probability within about 8 m that counts as "somewhat tree-like"
    green: float = 0.11  # excess green a blob needs (shadows and roofs are not green enough)
    canopy_p: float = 0.5  # canopy probability above which a pixel is canopy
    sigmas_m: tuple[float, ...] = (1.5, 2.5, 4.0)  # blob sizes; a blob of sigma s is a crown of radius ~1.4 s
    crown_factor: float = 1.4
    ring_factor: float = 2.4  # the ring a blob is compared with is a blur of 2.4 sigma
    building_buffer_m: float = 2.4  # roofs and their edge shadows are not trees
    label_inset_m: float = 15.0


@dataclass(frozen=True)
class Grid:
    """The photo's pixel grid in absolute S-JTSK, and the local frame origin."""

    minx: float
    maxy: float
    res: float
    origin: tuple[float, float]

    def px(self, x_local: float, y_local: float) -> tuple[float, float]:
        return (x_local + self.origin[0] - self.minx) / self.res, (self.maxy - (y_local + self.origin[1])) / self.res

    def local(self, col: float, row: float) -> tuple[float, float]:
        return self.minx + col * self.res - self.origin[0], self.maxy - row * self.res - self.origin[1]


# --- image maths ---------------------------------------------------------------------------


def smooth(a: np.ndarray, sigma_px: float) -> np.ndarray:
    """Gaussian blur, separable, as shifted sums (fast on big arrays, no scipy)."""
    r = max(1, int(3 * sigma_px) + 1)
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma_px) ** 2)
    k /= k.sum()
    a = a.astype(np.float32)
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r, r)
        p = np.pad(a, pad, mode="edge")
        n = a.shape[axis]
        out = np.zeros_like(a)
        for i, w in enumerate(k):
            sl = [slice(None), slice(None)]
            sl[axis] = slice(i, i + n)
            out += np.float32(w) * p[tuple(sl)]
        a = out
    return a


def box_mean(a: np.ndarray, k: int) -> np.ndarray:
    """Mean over a k x k window (edge-reflected), via an integral image."""
    r = k // 2
    p = np.pad(a.astype(np.float64), r, mode="reflect")
    c = np.zeros((p.shape[0] + 1, p.shape[1] + 1))
    c[1:, 1:] = p.cumsum(0).cumsum(1)
    h, w = a.shape
    s = c[k : k + h, k : k + w] - c[:h, k : k + w] - c[k : k + h, :w] + c[:h, :w]
    return (s / (k * k)).astype(np.float32)


def local_max(score: np.ndarray, radius_px: float) -> np.ndarray:
    """True where a pixel is the maximum of its square neighbourhood (separable, so cheap)."""
    r = int(radius_px)

    def run(a: np.ndarray, axis: int) -> np.ndarray:
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r, r)
        p = np.pad(a, pad, constant_values=-np.inf)
        n = a.shape[axis]
        best = a.copy()
        for d in range(2 * r + 1):
            sl = [slice(None), slice(None)]
            sl[axis] = slice(d, d + n)
            best = np.maximum(best, p[tuple(sl)])
        return best

    return score >= run(run(score, 0), 1)


def median_mask(mask: np.ndarray, size: int) -> np.ndarray:
    im = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MedianFilter(size))
    return np.asarray(im) > 0


def features(rgb: np.ndarray) -> np.ndarray:
    """(H, W, N_FEATURES): colour, a vegetation index, and brightness texture at three scales."""
    R, G, B = (rgb[..., i].astype(np.float32) for i in range(3))
    tot = np.maximum(R + G + B, 1)
    lum = 0.3 * R + 0.59 * G + 0.11 * B
    exg = (2 * G - R - B) / tot
    f = [lum / 255, R / tot, B / tot, exg]
    gy, gx = np.gradient(lum)
    grad = np.hypot(gx, gy)
    for k in (5, 13, 31):
        m = box_mean(lum, k)
        sd = np.sqrt(np.maximum(box_mean(lum * lum, k) - m * m, 0))
        f += [m / 255, sd / 64, lum / 255 - m / 255]
    f += [box_mean(grad, 5) / 32, box_mean(grad, 13) / 32, box_mean(exg, 13), box_mean(exg, 31)]
    return np.stack(f, axis=-1)


def expand(x: np.ndarray) -> np.ndarray:
    return np.concatenate([x, x * x], axis=-1)


# --- the canopy model ----------------------------------------------------------------------


def fit(x: np.ndarray, y: np.ndarray, iters: int = 600, lr: float = 0.08, l2: float = 1e-3) -> dict:
    """Logistic regression (Adam) on standardised features; returns the arrays the model is made of."""
    mu, sd = x.mean(0), x.std(0) + 1e-6
    z = (x - mu) / sd
    w, b = np.zeros(z.shape[1]), 0.0
    m, v = np.zeros_like(w), np.zeros_like(w)
    for t in range(1, iters + 1):
        p = 1 / (1 + np.exp(-(z @ w + b)))
        g = z.T @ (p - y) / len(y) + l2 * w
        m = 0.9 * m + 0.1 * g
        v = 0.999 * v + 0.001 * g * g
        w -= lr * (m / (1 - 0.9**t)) / (np.sqrt(v / (1 - 0.999**t)) + 1e-8)
        b -= lr * float(np.mean(p - y))
    return {"mu": mu, "sd": sd, "w": w, "b": np.array(b)}


def predict(model: dict, x: np.ndarray) -> np.ndarray:
    z = ((x - model["mu"]) / model["sd"]) @ model["w"] + float(model["b"])
    return 1 / (1 + np.exp(-z))


def label_polys(work_dir: Path, params: Params) -> list[tuple[int, int, Polygon]]:
    """(split, label, polygon in local metres): label 1 tree canopy, 0 open ground or built; split is 0 or 1."""
    norm = work_dir / "normalised"
    out: list[tuple[int, int, Polygon]] = []
    for f in json.loads((norm / "landuse.geojson").read_text())["features"]:
        value = f["properties"].get("value")
        label = 1 if value in ("forest", "wood") else 0 if value in ("meadow", "farmland") else None
        if label is None:
            continue
        g = shape(f["geometry"]).buffer(-params.label_inset_m)  # off the edge, where the tag is least exact
        if not g.is_empty:
            out.append((zlib.crc32(str(f["properties"].get("osm_id")).encode()) % 2, label, g))
    for i, f in enumerate(json.loads((norm / "buildings.geojson").read_text())["features"]):
        out.append((i % 2, 0, shape(f["geometry"]).buffer(0.5)))
    for i, f in enumerate(json.loads((norm / "highways.geojson").read_text())["features"]):
        g = shape(f["geometry"])
        if g.geom_type in ("LineString", "MultiLineString"):
            out.append((i % 2, 0, g.buffer(2.5)))
    return out


def draw_polys(polys, grid: Grid, y0: int, h: int, w: int) -> np.ndarray:
    """Rasterise polygons (local metres) into a strip starting at global row ``y0``; holes stay empty."""
    im = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(im)
    for g in polys:
        for part in getattr(g, "geoms", [g]):
            if part.geom_type != "Polygon" or part.is_empty:
                continue
            ext = [(a, b - y0) for a, b in (grid.px(x, y) for x, y in part.exterior.coords)]
            if max(p[1] for p in ext) < -5 or min(p[1] for p in ext) > h + 5:
                continue
            d.polygon(ext, fill=255)
            for ring in part.interiors:
                d.polygon([(a, b - y0) for a, b in (grid.px(x, y) for x, y in ring.coords)], fill=0)
    return np.asarray(im)


def train(img: Image.Image, grid: Grid, work_dir: Path, params: Params, n_per: int = 3000, seed: int = 1) -> dict:
    """Fit the canopy model on samples of the photo labelled by OSM landuse, buildings and roads."""
    rng = np.random.default_rng(seed)
    polys = label_polys(work_dir, params)
    w_img, h_img = img.size
    xs, ys = [], []
    for y0 in range(0, h_img, STRIP_ROWS):
        ya, yb = max(0, y0 - STRIP_MARGIN), min(h_img, y0 + STRIP_ROWS + STRIP_MARGIN)
        rgb = np.asarray(img.crop((0, ya, w_img, yb)).convert("RGB"))
        if (rgb.sum(axis=2) == 0).mean() > 0.5:
            continue
        # labels as 1 / 2 (0 = unknown), later polygons overwrite earlier ones
        lab = np.zeros((yb - ya, w_img), np.uint8)
        for label in (1, 0):  # trees first, so a building or road over a forest polygon wins
            sel = [g for _, lb, g in polys if lb == label]
            m = draw_polys(sel, grid, ya, yb - ya, w_img) > 0
            lab[m] = 1 + (label == 1)
        top, bot = y0 - ya, y0 - ya + min(STRIP_ROWS, h_img - y0)
        fe = features(rgb)[top:bot]
        lab, valid = lab[top:bot], rgb[top:bot].sum(axis=2) > 0
        for label in (0, 1):
            idx = np.argwhere(valid & (lab == 1 + label))
            if len(idx):
                pick = idx[rng.choice(len(idx), min(n_per, len(idx)), replace=False)]
                xs.append(fe[pick[:, 0], pick[:, 1]])
                ys.append(np.full(len(pick), label))
    if not xs:
        raise ValueError("no labelled pixels: the photo and the OSM landuse do not overlap")
    return fit(expand(np.concatenate(xs)), np.concatenate(ys).astype(np.float64))


# --- detection -----------------------------------------------------------------------------


def detect_strip(rgb: np.ndarray, model: dict, built: np.ndarray, res: float, p: Params):
    """(canopy mask, candidate crowns [(score, col, row, radius_px)]) of one strip of the photo."""
    nodata = np.asarray(Image.fromarray(((rgb.sum(axis=2) == 0) * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(21))) > 0
    prob = predict(model, expand(features(rgb)))
    canopy = median_mask((prob > p.canopy_p) & ~built & ~nodata, 7)

    a = rgb.astype(np.float32)
    lum = 0.3 * a[..., 0] + 0.59 * a[..., 1] + 0.11 * a[..., 2]
    exg = (2 * a[..., 1] - a[..., 0] - a[..., 2]) / np.maximum(a.sum(axis=2), 1)
    green = smooth(exg, 1.5 / res) > p.green
    wooded = smooth(prob, 8.0 / res) > p.wooded_p
    allowed = green & ~built & ~canopy & ~nodata
    thr = np.where(wooded, min(p.tau_wooded, p.tau_open), p.tau_open)

    cands = []
    for s_m in p.sigmas_m:
        ring, core = smooth(lum, p.ring_factor * s_m / res), smooth(lum, s_m / res)
        score = (ring - core) / np.maximum(ring, 25.0)  # relative darkness: a tree in dark scrub counts like one on lawn
        rad = p.crown_factor * s_m / res
        for r, c in np.argwhere((score > thr) & allowed & local_max(score, rad)):
            cands.append((float(score[r, c]), int(c), int(r), rad))
    return canopy, cands


def suppress(cands: list[tuple[float, int, int, float]]) -> list[tuple[float, int, int, float]]:
    """Strongest first; a crown overlapping a stronger one (closer than the larger radius) is dropped."""
    cands = sorted(cands, key=lambda t: (-t[0], t[2], t[1]))
    cell = max((c[3] for c in cands), default=1.0) + 1.0
    grid: dict[tuple[int, int], list] = {}
    out = []
    for s, c, r, rad in cands:
        gr, gc = int(r // cell), int(c // cell)
        near = (
            o for i in (gr - 1, gr, gr + 1) for j in (gc - 1, gc, gc + 1) for o in grid.get((i, j), [])
        )
        if any((c - c2) ** 2 + (r - r2) ** 2 < max(rad, rad2) ** 2 for _, c2, r2, rad2 in near):
            continue
        grid.setdefault((gr, gc), []).append((s, c, r, rad))
        out.append((s, c, r, rad))
    return out


def detect(img: Image.Image, grid: Grid, model: dict, work_dir: Path, outline: Polygon | None, p: Params):
    """(canopy mask over the whole photo, standalone trees as dicts in local metres)."""
    norm = work_dir / "normalised"
    buildings = [shape(f["geometry"]).buffer(p.building_buffer_m)
                 for f in json.loads((norm / "buildings.geojson").read_text())["features"]]
    w_img, h_img = img.size
    canopy = np.zeros((h_img, w_img), bool)
    cands = []
    for y0 in range(0, h_img, STRIP_ROWS):
        ya, yb = max(0, y0 - STRIP_MARGIN), min(h_img, y0 + STRIP_ROWS + STRIP_MARGIN)
        rgb = np.asarray(img.crop((0, ya, w_img, yb)).convert("RGB"))
        built = draw_polys(buildings, grid, ya, yb - ya, w_img) > 0
        strip_canopy, strip_cands = detect_strip(rgb, model, built, grid.res, p)
        top, bot = y0 - ya, y0 - ya + min(STRIP_ROWS, h_img - y0)
        canopy[y0 : y0 + (bot - top)] = strip_canopy[top:bot]
        cands += [(s, c, r + ya, rad) for s, c, r, rad in strip_cands if y0 <= r + ya < y0 + STRIP_ROWS]
    trees = []
    for s, c, r, rad in suppress(cands):
        x, y = grid.local(c + 0.5, r + 0.5)
        if outline is not None and not outline.contains(Point(x, y)):
            continue
        trees.append({"x": round(x, 1), "y": round(y, 1), "radius_m": round(rad * grid.res, 1), "score": round(s, 3)})
    trees.sort(key=lambda t: (t["y"], t["x"]))
    if outline is not None:
        canopy &= draw_polys([outline], grid, 0, h_img, w_img) > 0
    return canopy, trees


# --- files ---------------------------------------------------------------------------------


def load_grid(raw_dir: Path, work_dir: Path, ortho_dir: Path) -> tuple[Grid, dict, Polygon | None]:
    info = json.loads((ortho_dir / "ortho.json").read_text())
    origin = json.loads((work_dir / "frame.json").read_text())["origin_epsg5514"]
    grid = Grid(info["bounds_epsg5514"][0], info["bounds_epsg5514"][3], info["pixel_m"], (origin[0], origin[1]))
    ring = json.loads((raw_dir / "area.json").read_text()).get("polygon_epsg5514")
    outline = Polygon([(x - origin[0], y - origin[1]) for x, y in ring]) if ring else None
    return grid, info, outline


def build(raw_dir: Path, work_dir: Path, ortho_dir: Path, *, retrain: bool = False, params: Params | None = None) -> dict:
    """Train (or load) the canopy model, detect, and write ``trees.json`` and ``canopy.png``."""
    params = params or Params()
    grid, info, outline = load_grid(raw_dir, work_dir, ortho_dir)
    Image.MAX_IMAGE_PIXELS = None
    img = Image.open(ortho_dir / info["file"])
    model_path = ortho_dir / "tree_model.npz"
    if retrain or not model_path.is_file():
        model = train(img, grid, work_dir, params)
        np.savez(model_path, **model)
    else:
        with np.load(model_path) as z:
            model = {k: z[k] for k in z.files}
    canopy, trees = detect(img, grid, model, work_dir, outline, params)
    Image.fromarray(canopy).save(ortho_dir / "canopy.png")  # 1-bit
    doc = {
        "format_version": FORMAT_VERSION,
        "axes": "x east, y north, metres in the map's local frame (origin_epsg5514 of frame.json)",
        "params": asdict(params),
        "canopy": {
            "file": "canopy.png", "bounds_epsg5514": info["bounds_epsg5514"], "pixel_m": info["pixel_m"],
            "fraction": round(float(canopy.mean()), 4),
        },
        "standalone": trees,
        "source": info.get("attribution"),
        "notice": NOTICE,
    }
    (ortho_dir / "trees.json").write_text(json.dumps(doc, indent=1) + "\n")
    return doc


def run_ortho_trees(*, retrain: bool = False, params: Params | None = None) -> int:
    """Detect the trees of the map from its photo; prints one line and returns the exit status."""
    paths = map_paths()
    ortho_dir = paths.raw.parent / "ortho"
    try:
        doc = build(paths.raw, paths.work, ortho_dir, retrain=retrain, params=params)
    except FileNotFoundError as exc:
        print(f"ortho-trees: error: missing {Path(str(exc.filename)).name}; run make fetch normalise ortho first")
        return 1
    except ValueError as exc:
        print(f"ortho-trees: error: {exc}")
        return 1
    print(f"ortho-trees: canopy {doc['canopy']['fraction'] * 100:.1f}% of the photo, "
          f"{len(doc['standalone'])} standalone trees -> {ortho_dir / 'trees.json'}")
    return 0


def run(force: bool = False, **_options: object) -> int:
    """The ``ortho-trees`` pipeline stage (after ``ortho``). The cached canopy model is reused; ``make ortho-trees RETRAIN=1``
    trains it again (``force`` is not that: it would make every ``--force`` run spend minutes on training)."""
    return run_ortho_trees()


def main(argv: Sequence[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="drycastle_pipeline.ortho_trees", description=__doc__.split("\n")[0])
    ap.add_argument("--retrain", action="store_true", help="fit the canopy model again (else tree_model.npz is reused)")
    ap.add_argument("--tau-open", type=float, default=Params.tau_open, help="blob darkness needed in open ground")
    ap.add_argument("--tau-wooded", type=float, default=Params.tau_wooded, help="... in tree-like surroundings")
    args = ap.parse_args(argv)
    return run_ortho_trees(retrain=args.retrain,
                           params=Params(tau_open=args.tau_open, tau_wooded=args.tau_wooded))


if __name__ == "__main__":
    raise SystemExit(main())
