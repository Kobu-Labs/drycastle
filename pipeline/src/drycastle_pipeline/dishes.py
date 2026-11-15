"""Satellite dishes: a seeded share of the houses gets one, on a wall under the eave that faces the nearest road, into ``edits.json``.

Same way of working as the house connectors (``house_connections.py``): the result is placed objects of the edits
(``objects`` with a ``z``), so a dish is visible and movable in the map editor and ends up in ``objects.json`` with the
other placed objects, and a rerun only adds::

    cd pipeline
    uv run python -m drycastle_pipeline.dishes --dry-run   # what it would do
    uv run python -m drycastle_pipeline.dishes

Which houses: the dwellings (``area:house``, ``area:large``, ``type:commercial``; not the hand-modelled landmarks) that have
no dish yet. Each has ``dishes.dish_chance`` (rules.yaml, default 0.4) of getting one, rolled from a generator seeded with
``<building id>:dish``, so the same houses are chosen on every run and after every rebuild of the map.

Where: on the wall that faces the nearest road (the facing wall nearest to the road's nearest point), ``drop_m`` under
the eave, at a seeded place along the wall, at least ``wall_end_m`` from the corners, clear of the wall openings the pipeline
knows (the windows, doors and garage doors of ``config/buildings/<id>.yaml``) and of a house connector on that wall. A wall
with no room left (too short, too low, openings and connectors in the way) means no dish for that house.

Ids and hand edits: a new dish gets the next free ``o-NNNN`` of the file and is never renumbered, so the ids of the dishes
that are there stay what they are across rebuilds. A house that has a dish already (one within ``owned_m`` of its footprint,
the house it is nearest to) is left alone wherever the dish was moved to, and a dish deleted by hand is put back by the next
run (the chance is seeded, not remembered): set ``dish_chance: 0`` or do not rerun. It adds, it never moves what is there.

Frame: the dish's ``z`` is an elevation in the map's datum, the flattened ground under the building
(``terrain.json`` ``building_elevation``) plus the height on the building. The model's origin is on the wall at the arm's
root and its +x points out of the wall, so ``rotation_deg`` is the way the wall faces (as for the wall connectors).
"""

import argparse
import itertools
import json
import math
import random
import sys
from dataclasses import dataclass

from shapely.geometry import LineString, Point
from shapely.geometry.polygon import orient
from shapely.ops import nearest_points

from drycastle_pipeline.building_detail import Detail, load_details
from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.config import map_paths
from drycastle_pipeline.edits import load_edits
from drycastle_pipeline.house_connections import HOUSE_CLASSES
from drycastle_pipeline.rules import DEFAULT_RULES, load_section

DISH_ASSET = "pack:drycastle-generated-models/satellite_dish"
CONNECTOR_PREFIX = "prim:connector_"
ABOVE_M = 0.40  # the dish reaches this far above its origin (the model's top)
BELOW_M = 0.30  # and this far below it
HALF_WIDTH_M = 0.35  # and this far to either side along the wall
OPENING_MARGIN_M = 0.15  # clear space between the dish and a window frame, beside it


@dataclass(frozen=True)
class Dish:
    """One dish: the house it hangs on and its place (local metres, ``z`` an elevation)."""

    house: str
    asset: str
    x: float
    y: float
    z: float
    rotation_deg: float


def _facing_wall(poly, q: tuple[float, float]):
    """The wall of ``poly`` (oriented counter-clockwise) that faces ``q`` and is nearest to it.

    Returns ``(index, a, b, length)`` with the index of the wall in the ring (vertex i to i+1, as ``building_detail``
    numbers its edges), or None when no wall faces ``q`` (it is inside or behind everything).
    """
    ring = list(poly.exterior.coords)
    best = None
    for i, ((ax, ay), (bx, by)) in enumerate(itertools.pairwise(ring)):
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        if length < 0.5:
            continue
        nx, ny = dy / length, -dx / length  # outward normal of a counter-clockwise ring
        if nx * (q[0] - (ax + bx) / 2) + ny * (q[1] - (ay + by) / 2) <= 0:
            continue  # the road is behind this wall
        t = min(max(((q[0] - ax) * dx + (q[1] - ay) * dy) / (length * length), 0.0), 1.0)
        d = math.hypot(q[0] - (ax + dx * t), q[1] - (ay + dy * t))
        key = (round(d, 3), -length, i)  # nearest, then the longer wall, then the first
        if best is None or key < best[0]:
            best = (key, i, (ax, ay), (bx, by), length)
    return None if best is None else best[1:]


def _blocked(detail: Detail | None, edge: int, z0: float, z1: float) -> list[tuple[float, float]]:
    """Stretches of the dish's centre along a wall (from the wall's first vertex) taken by openings in the way of a dish
    that reaches from ``z0`` to ``z1`` above the ground."""
    out = []
    for o in detail.openings if detail else ():
        if o.edge != edge or o.sill_m >= z1 or o.sill_m + o.height_m <= z0:
            continue
        out.append((o.at_m - HALF_WIDTH_M - OPENING_MARGIN_M, o.at_m + o.width_m + HALF_WIDTH_M + OPENING_MARGIN_M))
    return out


def plan_dishes(
    buildings: list[dict],
    elevations: dict[str, float],
    roads: list[LineString],
    connectors: list[tuple[float, float]] = (),
    existing: list[tuple[float, float]] = (),
    details: dict[str, Detail] | None = None,
    cfg: dict | None = None,
    chance: float | None = None,
) -> list[Dish]:
    """The new dishes for the dwellings of ``buildings`` (local metres) beside ``roads`` (lines).

    ``connectors`` and ``existing`` are the places (x, y) of the house connectors and of the dishes already placed;
    ``details`` the hand-described buildings (their openings are kept clear); ``cfg`` the rules' ``dishes`` section
    (the shipped one when omitted) and ``chance`` overrides its ``dish_chance``.
    """
    cfg = cfg or load_section("dishes")
    chance = float(cfg["dish_chance"] if chance is None else chance)
    details = details or {}
    min_eave, end_m = float(cfg["min_eave_m"]), float(cfg["wall_end_m"])
    drop_lo, drop_hi = (float(v) for v in cfg["drop_m"])
    min_height, connector_clear = float(cfg["min_height_m"]), float(cfg["connector_clear_m"])
    owned_m, road_max = float(cfg["owned_m"]), float(cfg["road_max_m"])
    houses = {b["id"]: b for b in buildings if b["class"] in HOUSE_CLASSES and not b.get("model") and b["id"] in elevations}
    polys = {}
    for bid in sorted(houses):
        poly = footprint_polygon(houses[bid]["footprint"])
        polys[bid] = orient(poly if poly.is_valid else poly.buffer(0), 1.0)
    # a dish that is there belongs to the house it is nearest to, however far it was dragged (within owned_m)
    have: set[str] = set()
    for pos in existing:
        near = min(polys, key=lambda h: (polys[h].distance(Point(pos)), h), default=None)
        if near is not None and polys[near].distance(Point(pos)) <= owned_m:
            have.add(near)
    out = []
    for hid in sorted(polys):
        b, poly = houses[hid], polys[hid]
        rng = random.Random(f"{hid}:dish")
        roll, where, drop = rng.random(), rng.random(), rng.uniform(drop_lo, drop_hi)  # drawn always: a house keeps its roll
        eave = float(b["eave_height_m"])
        if hid in have or roll >= chance or eave < min_eave:
            continue
        road = min(roads, key=lambda ln: (poly.distance(ln), ln.wkt), default=None)
        if road is None or poly.distance(road) > road_max:
            continue
        q = nearest_points(poly, road)[1]
        wall = _facing_wall(poly, (q.x, q.y))
        if wall is None:
            continue
        edge, (ax, ay), (bx, by), length = wall
        ux, uy = (bx - ax) / length, (by - ay) / length
        nx, ny = uy, -ux
        height = max(eave - drop, min_height)
        lo, hi = end_m, length - end_m
        if hi < lo:
            continue  # the wall is too short for a dish
        blocked = _blocked(details.get(hid), edge, height - BELOW_M, height + ABOVE_M)
        for cx, cy in connectors:  # a connector on this wall, however it was placed
            across = (cx - ax) * nx + (cy - ay) * ny
            along = (cx - ax) * ux + (cy - ay) * uy
            if abs(across) <= 1.0 and -connector_clear <= along <= length + connector_clear:
                blocked.append((along - connector_clear, along + connector_clear))
        target = lo + (hi - lo) * (0.25 + 0.5 * where)
        steps = int((hi - lo) / 0.1) + 1
        free = [s for s in (lo + 0.1 * k for k in range(steps + 1)) if s <= hi + 1e-9
                and not any(a < s < c for a, c in blocked)]
        if not free:
            continue
        s = min(free, key=lambda v: (abs(v - target), v))
        out.append(Dish(hid, DISH_ASSET, round(ax + ux * s, 3), round(ay + uy * s, 3),
                        round(elevations[hid] + height, 3), round(math.degrees(math.atan2(ny, nx)) % 360.0, 1)))
    return out


def add_dishes(objects: list[dict], dishes: list[Dish], origin: list[float]) -> list[str]:
    """Append ``dishes`` (local metres) to the edits' ``objects`` (absolute S-JTSK metres), each with the next free
    ``o-NNNN``; the objects that are there keep their ids. Returns the new ids."""
    oid = max([int(o["id"][2:]) for o in objects if o["id"][2:].isdigit()] or [0])
    ids = []
    for d in dishes:
        oid += 1
        ids.append(f"o-{oid:04d}")
        objects.append({
            "id": ids[-1], "asset": d.asset, "position": [round(d.x + origin[0], 3), round(d.y + origin[1], 3)],
            "z": d.z, "rotation_deg": d.rotation_deg,
        })
    return ids


def apply(chance: float | None, dry_run: bool) -> int:
    from drycastle_pipeline.edits import DEFAULT_EDITS, dump_edits, validate
    from drycastle_pipeline.mcp_server import _edits_doc, edits_lock
    from drycastle_pipeline.roads import edge_lines

    paths = map_paths()
    work = paths.work
    origin = json.loads((work / "parcels.json").read_text())["origin_epsg5514"]
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    elevations = json.loads((work / "terrain.json").read_text())["building_elevation"]
    roads = [ln for _e, ln in edge_lines(json.loads((work / "roads.json").read_text()))]
    cfg = load_section("dishes", DEFAULT_RULES)
    with edits_lock(DEFAULT_EDITS):
        edits = load_edits(DEFAULT_EDITS)

        def local(o):
            return (o["position"][0] - origin[0], o["position"][1] - origin[1])

        connectors = [local(o) for o in edits.objects if o["asset"].startswith(CONNECTOR_PREFIX)]
        existing = [local(o) for o in edits.objects if o["asset"] == DISH_ASSET]
        dishes = plan_dishes(buildings, elevations, roads, connectors, existing, load_details(), cfg, chance)
        dwellings = sum(1 for b in buildings if b["class"] in HOUSE_CLASSES and not b.get("model"))
        print(f"{dwellings} dwellings, {len(existing)} dishes already: {len(dishes)} new dishes "
              f"(dish_chance {cfg['dish_chance'] if chance is None else chance:g})")
        if dry_run or not dishes:
            return 0
        added = add_dishes(edits.objects, dishes, origin)
        errors = validate(_edits_doc(edits))
        if errors:
            print("refused, the result would be invalid:\n" + "\n".join(errors), file=sys.stderr)
            return 1
        dump_edits(edits, DEFAULT_EDITS)
    print(f"wrote {len(added)} dishes to {DEFAULT_EDITS}")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--chance", type=float, help="chance per house, instead of dishes.dish_chance of rules.yaml")
    ap.add_argument("--dry-run", action="store_true", help="say what would be placed, write nothing")
    args = ap.parse_args(argv)
    return apply(args.chance, args.dry_run)


if __name__ == "__main__":
    raise SystemExit(main())
