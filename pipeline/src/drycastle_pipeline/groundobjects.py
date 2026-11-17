"""Solid ground objects of the forest: fallen logs, stumps, boulders, upturned root plates, anthills and burrows (#287).

Placed by the props stage, seeded per 25 m cell of the canopy mask (sha256, never ``hash()``), so identical input gives
identical output. Densities, sizes and clearances: rules.yaml ``props.ground_objects``. Meshes: ``propmeshes.ground_object_mesh``;
collision: ShooterCore ``GroundObjectColliders``; both follow the same ``model`` numbers (``export.ground_model``).

The timber and rock kinds are placed first, cell by cell, in clumps. Anthills and burrows come in a second pass over the cells (own
streams), so adding them moved none of the others: an anthill stands beside a pine or mixed-stand tree, never in swamp ground; a burrow
is likelier near the forest edge and on slopes.
"""

import math
from collections.abc import Callable

import numpy as np

CELL_M = 4.0  # grid cell of the overlap test; larger than any two touching discs' radii add up to
_KINDS = ("boulder", "log", "stump")  # the order the cell draws them in (fixed: it decides the random stream)
_TRIES = 10
_HEADINGS = 12


def _trunk_radius(tree: dict, tree_cfg: dict) -> float:
    """Plan radius of a trunk (its square section's corner), metres."""
    forest = tree.get("forest") or tree.get("src") == "ortho-canopy"
    width = float((tree_cfg.get("forest") or {}).get("trunk_width_m", tree_cfg["trunk_width_m"])) if forest else float(tree_cfg["trunk_width_m"])
    return width * float(tree["scale"]) / 2 * math.sqrt(2)


class _Discs:
    """Discs (x, y, r) in a grid: the trunk feet and the footprints of the objects placed so far."""

    def __init__(self) -> None:
        self.cells: dict[tuple[int, int], list[tuple[float, float, float]]] = {}

    def add(self, x: float, y: float, r: float) -> None:
        self.cells.setdefault((int(x // CELL_M), int(y // CELL_M)), []).append((x, y, r))

    def free(self, x: float, y: float, r: float, gap: float) -> bool:
        gx, gy = int(x // CELL_M), int(y // CELL_M)
        reach = math.ceil((r + 3.0 + gap) / CELL_M)
        for i in range(gx - reach, gx + reach + 1):
            for j in range(gy - reach, gy + reach + 1):
                for a, b, rr in self.cells.get((i, j), ()):
                    if math.hypot(x - a, y - b) < r + rr + gap:
                        return False
        return True


def make_ground_objects(
    trees: list[dict],
    tree_cfg: dict,
    cfg: dict,
    mask: np.ndarray,
    frame: tuple[float, float, float],
    inside: Callable[[np.ndarray, np.ndarray], np.ndarray],
    ground: Callable[[np.ndarray, np.ndarray], np.ndarray],
    seeded: Callable[[str], object],
    cell_px: int,
    swamp: Callable[[np.ndarray, np.ndarray], np.ndarray] | None = None,
) -> list[dict]:
    """The ground objects of the forest: dicts with kind, x, y, z, rot, scale (and len for a log), all in local metres.

    ``mask`` is the canopy mask (True = canopy) with ``frame`` = (x of its west edge, y of its north edge, metres per pixel),
    ``inside(xs, ys)`` says where an object may stand (terrain, forest ground, off properties, roads, buildings, water) and
    ``ground(xs, ys)`` is the terrain height, ``swamp(xs, ys)`` says where swamp ground lies (no anthills there). Objects are placed cell by
    cell in clumps; each keeps off every trunk foot and every earlier object (a log is tested along its length, a root plate across its face).
    """
    x0, y_top, res = frame
    discs = _Discs()
    for t in trees:
        discs.add(float(t["x"]), float(t["y"]), _trunk_radius(t, tree_cfg))
    gap = float(cfg["trunk_clearance_m"])
    model = cfg["model"]
    out: list[dict] = []

    def clear(points: list[tuple[float, float, float]]) -> bool:
        xs = np.array([p[0] for p in points])
        ys = np.array([p[1] for p in points])
        if not inside(xs, ys).all():
            return False
        return all(discs.free(x, y, r, gap) for x, y, r in points)

    def place_log(rng, x: float, y: float) -> list[dict] | None:
        lc = cfg["log"]
        length = rng.uniform(*lc["length_m"])
        thick = rng.uniform(*lc["thickness_m"])
        scale = thick / float(model["log"]["diameter_m"])
        radius = thick / 2 + 0.1
        rm = model["rootplate"]
        n = max(2, int(length // 1.0) + 1)
        want_plate = rng.random() < float(lc["rootplate_share"])
        ps = rng.uniform(*lc["rootplate_scale"])
        for with_plate in (True, False) if want_plate else (False,):  # no room for the plate: the log lies without one
            for _heading in range(_HEADINGS):  # a log in a forest has to lie between the trunks, so try other headings here
                rot = rng.random() * 2 * math.pi
                u = (math.cos(rot), math.sin(rot))
                pts = [(x + u[0] * (-length / 2 + length * i / (n - 1)), y + u[1] * (-length / 2 + length * i / (n - 1)), radius) for i in range(n)]
                ends = ground(np.array([pts[0][0], pts[-1][0]]), np.array([pts[0][1], pts[-1][1]]))
                if abs(float(ends[0] - ends[1])) > float(lc["max_end_drop_m"]):
                    continue
                plate = None
                if with_plate:
                    pr, pt = float(rm["radius_m"]) * ps, float(rm["thickness_m"]) * ps
                    v = (-u[1], u[0])
                    # the plate stands at the thick end (-u), its back face just overlapping the log's end
                    px, py = x - u[0] * (length / 2 + pt / 2 - 0.05), y - u[1] * (length / 2 + pt / 2 - 0.05)
                    plate = (px, py)
                    pts += [(px + v[0] * k * pr * 0.7, py + v[1] * k * pr * 0.7, pr * 0.45) for k in (-1, 0, 1)]
                if not clear(pts):
                    continue
                for p in pts:
                    discs.add(*p)
                z = float(ground(np.array([x]), np.array([y]))[0])
                made = [{"kind": "log", "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot, 3), "scale": round(scale, 3), "len": round(length, 3)}]
                if plate is not None:
                    pz = float(ground(np.array([plate[0]]), np.array([plate[1]]))[0])
                    made.append({"kind": "rootplate", "x": round(plate[0], 3), "y": round(plate[1], 3), "z": round(pz, 3), "rot": round(rot, 3), "scale": round(ps, 3)})
                return made
        return None

    def place_stump(rng, x: float, y: float) -> list[dict] | None:
        width = rng.uniform(*cfg["stump"]["width_m"])
        scale = width / float(model["stump"]["width_m"])
        rot = rng.random() * math.pi / 3
        if not clear([(x, y, width / 2 + 0.1)]):
            return None
        discs.add(x, y, width / 2 + 0.1)
        z = float(ground(np.array([x]), np.array([y]))[0])
        return [{"kind": "stump", "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot, 3), "scale": round(scale, 3)}]

    def place_boulder(rng, x: float, y: float) -> list[dict] | None:
        size = rng.uniform(*cfg["boulder"]["size_m"])
        scale = size / float(model["boulder"]["width_m"])
        rot = rng.random() * math.pi
        r = size / 2 * 0.95
        if not clear([(x, y, r)]):
            return None
        discs.add(x, y, r)
        z = float(ground(np.array([x]), np.array([y]))[0])
        return [{"kind": "boulder", "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot, 3), "scale": round(scale, 3)}]

    place = {"log": place_log, "stump": place_stump, "boulder": place_boulder}
    h, w = mask.shape
    anthill_trees = _trees_by_cell(trees, tree_cfg, cfg["anthill"]["stands"], (x0, y_top, res), cell_px)

    def place_anthill(rng, tree: dict) -> list[dict] | None:
        ac = cfg["anthill"]
        width = rng.uniform(*ac["size_m"])
        r = width / 2 * 0.95
        a = rng.random() * 2 * math.pi
        d = _trunk_radius(tree, tree_cfg) + gap + r + rng.uniform(*ac["foot_m"])  # beside the trunk, never on it
        x, y = float(tree["x"]) + d * math.cos(a), float(tree["y"]) + d * math.sin(a)
        if swamp is not None and swamp(np.array([x]), np.array([y]))[0]:
            return None
        rot = rng.random() * math.pi
        if not clear([(x, y, r)]):
            return None
        discs.add(x, y, r)
        z = float(ground(np.array([x]), np.array([y]))[0])
        return [{"kind": "anthill", "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot, 3), "scale": round(width / float(model["anthill"]["width_m"]), 3)}]

    def place_burrow(rng, x: float, y: float) -> list[dict] | None:
        bc = cfg["burrow"]
        rr = int(math.ceil(float(bc["edge_m"]) / res))
        row, col = int((y_top - y) / res), int((x - x0) / res)
        near_edge = not mask[max(0, row - rr) : row + rr + 1, max(0, col - rr) : col + rr + 1].all()  # open ground within edge_m
        step = 1.5
        gx = float(ground(np.array([x + step]), np.array([y]))[0] - ground(np.array([x - step]), np.array([y]))[0]) / (2 * step)
        gy = float(ground(np.array([x]), np.array([y + step]))[0] - ground(np.array([x]), np.array([y - step]))[0]) / (2 * step)
        steep = math.hypot(gx, gy) >= float(bc["slope_min"])
        if rng.random() >= float(bc["base"]) + float(bc["edge"]) * near_edge + float(bc["slope"]) * steep:
            return None
        width = rng.uniform(*bc["size_m"])
        r = width / 2 * 0.95
        rot = math.atan2(-gy, -gx) if steep else rng.random() * 2 * math.pi  # the hole faces downhill
        if not clear([(x, y, r)]):
            return None
        discs.add(x, y, r)
        z = float(ground(np.array([x]), np.array([y]))[0])
        return [{"kind": "burrow", "x": round(x, 3), "y": round(y, 3), "z": round(z, 3), "rot": round(rot, 3), "scale": round(width / float(model["burrow"]["width_m"]), 3)}]

    for r0 in range(0, h, cell_px):
        for c0 in range(0, w, cell_px):
            cell = mask[r0 : r0 + cell_px, c0 : c0 + cell_px]
            n = int(cell.sum())
            if n == 0:
                continue
            rng = seeded(f"ground:cell:{r0}:{c0}")
            ha = n * res * res / 10000.0
            x_lo, y_hi = x0 + c0 * res, y_top - r0 * res
            size_m = cell_px * res
            for kind in _KINDS:
                kc = cfg[kind]
                lo, hi = (int(v) for v in kc["clump"])
                expected = float(kc["per_ha"]) * ha / ((lo + hi) / 2)  # clumps, not objects
                clumps = int(expected) + (rng.random() < expected % 1)
                for _ in range(clumps):
                    centre = None
                    for _try in range(30):
                        cx, cy = x_lo + rng.random() * size_m, y_hi - rng.random() * size_m
                        row, col = int((y_hi - cy) / res), int((cx - x_lo) / res)
                        if row < cell.shape[0] and col < cell.shape[1] and cell[row, col]:
                            centre = (cx, cy)
                            break
                    if centre is None:
                        continue
                    sigma = float(kc["clump_sigma_m"])
                    for _member in range(rng.randint(lo, hi)):
                        for _try in range(_TRIES):
                            made = place[kind](rng, centre[0] + rng.gauss(0, sigma), centre[1] + rng.gauss(0, sigma))
                            if made:
                                out += made
                                break

    # second pass: anthills and burrows (own streams; the kinds above are untouched by them)
    for r0 in range(0, h, cell_px):
        for c0 in range(0, w, cell_px):
            cell = mask[r0 : r0 + cell_px, c0 : c0 + cell_px]
            n = int(cell.sum())
            if n == 0:
                continue
            rng = seeded(f"ground2:cell:{r0}:{c0}")
            ha = n * res * res / 10000.0
            x_lo, y_hi = x0 + c0 * res, y_top - r0 * res
            size_m = cell_px * res
            near = anthill_trees.get((r0, c0), [])
            expected = float(cfg["anthill"]["per_ha"]) * ha
            for _ in range(int(expected) + (rng.random() < expected % 1)):
                for _try in range(_TRIES):
                    made = place_anthill(rng, rng.choice(near)) if near else None
                    if made:
                        out += made
                        break
            expected = float(cfg["burrow"]["candidates_per_ha"]) * ha
            for _ in range(int(expected) + (rng.random() < expected % 1)):
                for _try in range(30):
                    cx, cy = x_lo + rng.random() * size_m, y_hi - rng.random() * size_m
                    row, col = int((y_hi - cy) / res), int((cx - x_lo) / res)
                    if row < cell.shape[0] and col < cell.shape[1] and cell[row, col]:
                        out += place_burrow(rng, cx, cy) or []
                        break
    return out


def _trees_by_cell(trees: list[dict], tree_cfg: dict, stands: list[str], frame: tuple[float, float, float], cell_px: int) -> dict[tuple[int, int], list[dict]]:
    """The forest trees of the given stands by the (row, column) pixel origin of their 25 m cell: where an anthill may stand beside one."""
    x0, y_top, res = frame
    out: dict[tuple[int, int], list[dict]] = {}
    for t in trees:
        if not (t.get("forest") or t.get("src") == "ortho-canopy") or t.get("stand", "mixed") not in stands:
            continue
        r0 = int((y_top - float(t["y"])) / res) // cell_px * cell_px
        c0 = int((float(t["x"]) - x0) / res) // cell_px * cell_px
        out.setdefault((r0, c0), []).append(t)
    return out
