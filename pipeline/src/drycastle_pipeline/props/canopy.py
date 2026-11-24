"""Tree planting from the orthophoto detector: standalone trees and the canopy mask (Python reference and Rust engine)."""

import json
import math

import numpy as np
import shapely
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.props.helpers import seeded
from drycastle_pipeline.props.stands import (
    CANOPY_CELL_PX,
    EdgeField,
    Stands,
    age_class,
    canopy_mask,
)
from drycastle_pipeline.terrain.terrain import Terrain


def planting_engine():
    """The Rust engine's module if it is installed and plants trees, else None."""
    try:
        import drycastle_detail
    except ImportError:
        return None
    return drycastle_detail if hasattr(drycastle_detail, "plant_trees") else None


def plant_canopy_rust(module, terrain: Terrain, zones: tuple, cfg: dict, origin, mask: np.ndarray, frame: tuple[float, float, float],
                      placed: list[tuple[float, float]], stands: "Stands | None") -> list[dict]:
    """The canopy trees of :func:`ortho_trees` from the Rust engine (``planting.rs``): the same draws in the same order, so the
    same trees. The records are the ones :func:`make_trees` builds (``tree`` there), heights from the terrain here."""
    minx, maxy, res = frame
    ocfg, scfg = cfg.get("ortho", {}), cfg.get("stands")
    crown_lo, crown_hi = (float(v) for v in ocfg.get("canopy_crown_scale", (1.7, 2.5)))
    ages = [(str(a["name"]), float(a["share"]), float(a["scale"])) for a in ocfg.get("age_classes", ())]
    palette = cfg["forest_canopy_colours"]
    doc: dict = {
        "w": int(mask.shape[1]), "h": int(mask.shape[0]), "minx": minx, "maxy": maxy, "res": res, "ox": origin[0], "oy": origin[1],
        "cell_px": CANOPY_CELL_PX, "spacing": float(ocfg.get("canopy_min_spacing_m", 4.0)), "density": float(ocfg.get("canopy_per_1000m2", 25.0)),
        "crown_lo": crown_lo, "crown_hi": crown_hi, "ages": [[share, scale] for _, share, scale in ages], "palette": len(palette),
        "stands": None, "swamp_zone": False,
    }
    geoms = [terrain.coverage, *(g for g in zones if g is not None)]
    if stands is not None:
        def stand(st: dict) -> dict:
            scale = st.get("scale", 1.0)
            return {"share": float(st.get("share", 0.0)), "cone_share": float(st["cone_share"]), "keep": float(st.get("keep", 1.0)),
                    "scale": [float(scale[0]), float(scale[1])] if isinstance(scale, (list, tuple)) else float(scale)}

        thicket, ecfg = stands.types.get("thicket"), scfg.get("edge")
        doc["stands"] = {
            "cell": stands.cell, "key": f"stand:{stands.cell}", "types": [stand(stands.types[k]) for k in stands.names],
            "swamp": stand(scfg["swamp"]) if "swamp" in scfg else None,
            "thicket": {"index": stands.names.index("thicket"), "density_factor": float(thicket.get("density_factor", 1.0)),
                        "spacing_scale": float(thicket.get("spacing_scale", 1.0))} if thicket else None,
            "edge": {"edge_m": float(ecfg["edge_m"]), "lo": float(ecfg["scale"][0]), "hi": float(ecfg["scale"][1]), "cone_share": float(ecfg["cone_share"]),
                     "k": max(1, round(float(ecfg["gap_m"]) / res / 2)), "r": math.ceil(float(ecfg["edge_m"]) / res)} if ecfg else None,
        }
        if stands.swamp is not None:
            doc["swamp_zone"] = True
            geoms.append(stands.swamp)
    raw = module.plant_trees(json.dumps(doc), np.ascontiguousarray(mask, dtype=np.uint8).tobytes(), [shapely.to_wkb(g) for g in geoms],
                             np.array(placed, dtype="<f8").reshape(-1, 2).tobytes())
    rows = np.frombuffer(raw, "<f8").reshape(-1, 9)
    zs = terrain.surface(rows[:, 0], rows[:, 1]) if len(rows) else []
    out = []
    for (x, y, scale, rot, cone, colour, age, st, edge), z in zip(rows.tolist(), np.asarray(zs).tolist(), strict=True):
        rec = {"src": "ortho-canopy", "parcel": None, "x": r3(x), "y": r3(y), "z": r3(z), "scale": round(scale, 3), "rot": round(rot, 3),
               "shape": "cone" if cone else "box", "colour": palette[int(colour)], "forest": True, "age": ages[int(age)][0] if age >= 0 else ""}
        if stands is not None:
            rec["stand"] = "swamp" if int(st) == len(stands.names) else stands.names[int(st)]
            if edge:
                rec["edge"] = True
        out.append(rec)
    return out


class _Spacing:
    """The planted trunks on a grid of ``spacing``-metre cells: is a spot clear of every neighbour?"""

    def __init__(self, spacing: float) -> None:
        self.spacing = spacing
        self.grid: dict[tuple[int, int], list[tuple[float, float]]] = {}

    def free(self, x: float, y: float, gap: float | None = None) -> bool:  # gap <= spacing: the 3 x 3 grid cells still hold every neighbour
        gap = self.spacing if gap is None else gap
        spacing = self.spacing
        gx, gy = int(x // spacing), int(y // spacing)
        return not any(
            math.hypot(x - a, y - b) < gap
            for i in (gx - 1, gx, gx + 1)
            for j in (gy - 1, gy, gy + 1)
            for a, b in self.grid.get((i, j), ())
        )

    def add(self, x: float, y: float) -> None:
        self.grid.setdefault((int(x // self.spacing), int(y // self.spacing)), []).append((x, y))

    def spots(self) -> list[tuple[float, float]]:
        return [p for spots in self.grid.values() for p in spots]


def _range(v, u: float) -> float:
    """A scale setting: a number, or [lo, hi] drawn with ``u`` in [0, 1)."""
    return float(v) if not isinstance(v, (list, tuple)) else float(v[0]) + (float(v[1]) - float(v[0])) * u


def standalone_trees(tree, doc: dict, terrain: Terrain, zones: tuple, cfg: dict, spacing: _Spacing) -> list[dict]:
    """The single trees of the detector's ``trees.json``, planted where it found them; each is added to ``spacing``."""
    canopy_m = float(cfg["canopy_m"])
    road = zones[1]  # (building, road, object, water)
    trees: list[dict] = []
    for t in doc["standalone"]:
        x, y = float(t["x"]), float(t["y"])
        if not shapely.contains_xy(terrain.coverage, x, y):
            continue
        if road is not None and shapely.contains_xy(road, x, y):  # a trunk on a carriageway (roadside trees the photo puts on the road)
            continue
        if zones[3] is not None and shapely.contains_xy(zones[3], x, y):  # or in water
            continue
        scale = min(3.0, max(0.8, 2.0 * float(t["radius_m"]) / canopy_m))
        trees.append(tree(x, y, seeded(f"ortho:{t['x']}:{t['y']}"), None, False, "ortho", scale))
        spacing.add(x, y)
    return trees


def plant_canopy_python(tree, terrain: Terrain, zones: tuple, cfg: dict, origin, mask: np.ndarray, frame: tuple[float, float, float],
                        spacing: _Spacing, stands: "Stands | None") -> list[dict]:
    """The canopy trees of :func:`ortho_trees`, the Python reference: the mask planted cell by cell (``CANOPY_CELL_PX`` pixels), each
    cell with its own seeded stream, then the extra trees of a thicket. ``spacing`` already holds the standalone trees; the trees
    planted here are added to it."""
    minx, maxy, res = frame
    ox, oy = origin
    ocfg = cfg.get("ortho", {})
    free, add = spacing.free, spacing.add
    spacing_m = spacing.spacing
    density = float(ocfg.get("canopy_per_1000m2", 25.0))
    crown_lo, crown_hi = (float(v) for v in ocfg.get("canopy_crown_scale", (1.7, 2.5)))
    ages = [(str(a["name"]), float(a["share"]), float(a["scale"])) for a in ocfg.get("age_classes", ())]
    h, w = mask.shape
    scfg = cfg.get("stands")
    ecfg = (scfg or {}).get("edge")
    edges = EdgeField(mask, res, float(ecfg["edge_m"]), float(ecfg["gap_m"])) if stands is not None and ecfg else None
    trees: list[dict] = []

    def plant(x: float, y: float, rng, stand: str, row: int, col: int) -> dict:
        """A forest tree: crown, age class, then the stand's shape mix and size, then the edge mantle. (row, col): its mask pixel."""
        crown = crown_lo + (crown_hi - crown_lo) * rng.random()
        age, grow = age_class(ages, seeded(f"ortho:age:{x:.3f}:{y:.3f}"))  # its own seed: the planting above stays as it was
        scale, cone, edge = crown * grow, 0.6, False
        if stands is not None:
            st = scfg["swamp"] if stand == "swamp" else stands.types[stand]
            scale *= _range(st.get("scale", 1.0), seeded(f"stand:size:{x:.3f}:{y:.3f}").random())
            cone = float(st["cone_share"])
            if ecfg:
                edge_m = float(ecfg["edge_m"])
                d = edges.distance(row, col)  # edge_distance(mask, row, col, res, edge_m, gap_m)
                if d < edge_m:  # the wall steps down: smallest at the edge itself
                    lo, hi = (float(v) for v in ecfg["scale"])
                    scale *= lo + (hi - lo) * d / edge_m
                    cone, edge = float(ecfg["cone_share"]), True
        rec = {**tree(x, y, rng, None, True, "ortho-canopy", scale, cone), "age": age}
        if stands is not None:
            rec["stand"] = stand
            if edge:
                rec["edge"] = True
        return rec

    def candidate(rng, x_lo: float, y_hi: float, size: float, cell: np.ndarray, only: str | None = None):
        """A random point of the canopy in this cell (of stand ``only``, if given) that is inside the coverage and clear of the
        zones: (x, y, row, col) or None."""
        x, y = x_lo + rng.random() * size, y_hi - rng.random() * size
        row, col = int((y_hi - y) / res), int((x - x_lo) / res)
        if row >= cell.shape[0] or col >= cell.shape[1] or not cell[row, col]:
            return None
        if only is not None and stands.noise(x, y) != only:  # cheap: before the geometry tests
            return None
        hit = shapely.contains_xy(tests, x, y)  # one call for the coverage and every zone
        if not hit[0] or hit[1:].any():
            return None
        return x, y, row, col

    tests = np.array([terrain.coverage, *(g for g in zones if g is not None)], dtype=object)

    thicket = stands.types.get("thicket") if stands else None
    for r0 in range(0, h, CANOPY_CELL_PX):
        for c0 in range(0, w, CANOPY_CELL_PX):
            cell = mask[r0 : r0 + CANOPY_CELL_PX, c0 : c0 + CANOPY_CELL_PX]
            n = int(cell.sum())
            if n == 0:
                continue
            rng = seeded(f"ortho:canopy:{r0}:{c0}")
            expected = density * n * res * res / 1000.0
            count = int(expected) + (rng.random() < expected % 1)
            x_lo, y_hi = minx + c0 * res - ox, maxy - r0 * res - oy  # the cell's north-west corner, local metres
            size = CANOPY_CELL_PX * res
            placed = 0
            for _ in range(count * 100):
                if placed >= count:
                    break
                got = candidate(rng, x_lo, y_hi, size, cell)
                if got is None:
                    continue
                x, y, row, col = got
                if not free(x, y):
                    continue
                stand = stands.at(x, y) if stands else "mixed"
                keep = float((scfg["swamp"] if stand == "swamp" else stands.types[stand]).get("keep", 1.0)) if stands else 1.0
                if keep < 1.0 and seeded(f"stand:keep:{x:.3f}:{y:.3f}").random() >= keep:
                    placed += 1  # thinned out: no tree, and it leaves the room free
                    continue
                add(x, y)
                trees.append(plant(x, y, rng, stand, r0 + row, c0 + col))
                placed += 1
            extra_n = expected * (float(thicket.get("density_factor", 1.0)) - 1.0) if thicket else 0.0
            if extra_n <= 0 or not any(  # no thicket in the cell (looked at on a 3 x 3 grid): skip the attempts
                stands.noise(x_lo + fx * size, y_hi - fy * size) == "thicket" for fx in (0, 0.5, 1) for fy in (0, 0.5, 1)
            ):
                continue
            # a thicket is denser than the canopy rule plants: add trees between the others, closer than the normal spacing
            xrng = seeded(f"ortho:thicket:{r0}:{c0}")
            extra = int(extra_n) + (xrng.random() < extra_n % 1)
            gap = spacing_m * float(thicket.get("spacing_scale", 1.0))
            placed = 0
            for _ in range(extra * 60):
                if placed >= extra:
                    break
                got = candidate(xrng, x_lo, y_hi, size, cell, "thicket")
                if got is None:
                    continue
                x, y, row, col = got
                if stands.at(x, y) != "thicket" or not free(x, y, gap):
                    continue
                add(x, y)
                trees.append(plant(x, y, xrng, "thicket", r0 + row, c0 + col))
                placed += 1
    return trees


def ortho_trees(
    tree, terrain: Terrain, zones: tuple, cfg: dict, origin: tuple[float, float] | None, wet: BaseGeometry | None = None,
    engine: str = "python",
) -> list[dict]:
    """Every tree, planted from the orthophoto detector (``make ortho-trees``).

    * each standalone tree of ``trees.json`` is planted where the detector found it, its size following the crown
      radius (``scale = 2 r / canopy_m``, kept within 0.8-3), unless its trunk would stand on a road;
    * the canopy mask (``canopy.png``) is planted so that the forest looks closed from above, as in the photo:
      ``ortho.canopy_per_1000m2`` trees per 1000 m2 of mask at least ``ortho.canopy_min_spacing_m`` apart, each with a
      crown ``ortho.canopy_crown_scale`` times the normal one (the rules' own forest density is far too thin for
      that). Seeded per 25 m cell; never on a building, road, placed object or water. Each gets an age class
      (``ortho.age_classes``: young, mature, giant) that multiplies its scale, seeded from its position;
    * a forest tree also belongs to a stand (:class:`Stands`, ``stands`` in the rules: pine, birch, mixed, thicket, swamp
      near ``wet``) that sets its shape mix, size and density (a thicket gets extra trees between the others, down to
      ``spacing_scale`` of the spacing), and within ``edge.edge_m`` of the canopy's outer edge (:func:`edge_distance`) it is
      an ``edge`` tree, smaller toward the edge. Without ``stands`` every forest tree is mixed, as before.

    The photo does not resolve single crowns in a forest, so the canopy trunks are random within the detected extent.

    ``engine``: "python" (the reference, with any ``tree``), or "rust" / "auto" (the Rust engine, when installed, plants the canopy:
    the same trees, :func:`plant_canopy_rust`; only for the ``tree`` of :func:`make_trees`, whose records it builds itself).
    """
    if origin is None:
        raise ValueError("trees from the orthophoto need the frame origin (parcels.json origin_epsg5514)")
    doc, mask, minx, maxy, res = canopy_mask(cfg)
    spacing = _Spacing(float(cfg.get("ortho", {}).get("canopy_min_spacing_m", 4.0)))
    shapely.prepare(terrain.coverage)
    trees = standalone_trees(tree, doc, terrain, zones, cfg, spacing)
    scfg = cfg.get("stands")
    stands = Stands(scfg, wet) if scfg else None
    module = planting_engine() if engine != "python" else None
    if engine == "rust" and module is None:
        raise ValueError("the Rust engine is not installed: build it with `make detail-rust-build`")
    frame = (minx, maxy, res)
    if module is not None:
        return trees + plant_canopy_rust(module, terrain, zones, cfg, origin, mask, frame, spacing.spots(), stands)  # spots: the standalone trees
    return trees + plant_canopy_python(tree, terrain, zones, cfg, origin, mask, frame, spacing, stands)
