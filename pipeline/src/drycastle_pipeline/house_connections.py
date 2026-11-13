"""Service drops: house connectors and the cables from the nearest poles, placed the way a street would have them.

The map editor connects a house with one click each; this does a whole street at once, into ``edits.json``
(``objects`` and ``cables``), so the result is hand-editable like anything else the editor placed::

    cd pipeline
    uv run python -m drycastle_pipeline.house_connections --area village --dry-run   # what it would do
    uv run python -m drycastle_pipeline.house_connections --area village

Which houses: the dwellings (``area:house``, ``area:large``, ``type:commercial``; not sheds, garages, the church or the
hand-made overrides) that stand within ``--service-m`` of a pole and have no connector within a few metres already.
Each house takes the nearest pole that still has room (``--per-pole`` drops at most), over the nearest wire that does
not pass through a building. On the house the wire lands on the wall that faces the pole (a wall hook, or a pair of
J-hooks, under the eave, away from the corners); a house with a flat roof sometimes gets a roof mast near that edge
instead. The choice of kind is seeded by the building's id, so a rerun gives the same answer.

Frame: the connectors' ``z`` is an elevation in the map's datum, the flattened ground under the building
(``terrain.json`` ``building_elevation``) plus the height on the building.
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

from drycastle_pipeline.buildings import footprint_polygon
from drycastle_pipeline.config import DEFAULT_AREA, area_paths
from drycastle_pipeline.edits import POLE_ASSET_PREFIX, load_edits

HOUSE_CLASSES = ("area:house", "area:large", "type:commercial")
WALL_KINDS = (("prim:connector_wall_hook", 5), ("prim:connector_wall_2", 3), ("prim:connector_wall_4", 2))
ROOF_KINDS = (("prim:connector_roof_mast_hook", 4), ("prim:connector_roof_mast_2", 3), ("prim:connector_roof_mast_4", 3))
ROOF_MAST_SHARE = 0.5  # of the flat-roofed houses
SERVICE_M = 40.0
PER_POLE = 4
CORNER_M = 0.8  # the wire keeps this far from a corner of the wall
EAVE_DROP_M = 0.8  # a wall connector sits this far under the eave
MIN_HEIGHT_M = 2.2  # and never lower than this above the ground
ROOF_INSET_M = 1.0  # a roof mast stands this far in from the edge
ALREADY_M = 6.0  # a house with a connector this close to its footprint has one


@dataclass(frozen=True)
class Drop:
    """One service drop: the house, the pole it hangs from and the connector made for it (local metres, an elevation)."""

    house: str
    pole: str
    asset: str
    x: float
    y: float
    z: float
    rotation_deg: float


def _pick(rng: random.Random, kinds: tuple[tuple[str, int], ...]) -> str:
    return rng.choices([k for k, _ in kinds], [w for _, w in kinds])[0]


def _facing_edge(poly, q: tuple[float, float]):
    """The wall that faces ``q``: (point on it, outward normal), or None when no wall does (``q`` is inside or behind)."""
    ring = list(orient(poly, 1.0).exterior.coords)  # counter-clockwise: the outward normal of an edge is (dy, -dx)
    best = None
    for (ax, ay), (bx, by) in itertools.pairwise(ring):
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        if length < 0.5:
            continue
        nx, ny = dy / length, -dx / length
        if nx * (q[0] - (ax + bx) / 2) + ny * (q[1] - (ay + by) / 2) <= 0:
            continue  # the pole is behind this wall
        t = ((q[0] - ax) * dx + (q[1] - ay) * dy) / (length * length)
        margin = CORNER_M / length if length > 2 * CORNER_M else 0.5  # a short wall takes the wire in its middle
        t = min(max(t, margin), 1 - margin)
        px, py = ax + dx * t, ay + dy * t
        d = math.hypot(q[0] - px, q[1] - py)
        if best is None or d < best[0]:
            best = (d, (px, py), (nx, ny))
    return None if best is None else (best[1], best[2])


def plan_drops(
    buildings: list[dict],
    elevations: dict[str, float],
    poles: list[tuple[str, float, float]],
    taken: list[tuple[float, float]] = (),
    service_m: float = SERVICE_M,
    per_pole: int = PER_POLE,
) -> list[Drop]:
    """The drops for the dwellings of ``buildings`` (local metres) from ``poles`` (id, x, y); ``taken`` are the places of connectors already there."""
    houses = {b["id"]: b for b in buildings if b["class"] in HOUSE_CLASSES and b["id"] in elevations}
    polys = {b["id"]: footprint_polygon(b["footprint"]) for b in buildings}
    for hid in list(houses):
        poly = polys[hid]
        if not poly.is_valid:
            polys[hid] = poly = poly.buffer(0)
        if any(poly.distance(Point(t)) <= ALREADY_M for t in taken):
            del houses[hid]
    candidates = sorted(
        (polys[h].distance(Point(x, y)), h, pid, (x, y))
        for h in houses
        for pid, x, y in poles
        if polys[h].distance(Point(x, y)) <= service_m
    )
    load: dict[str, int] = {}
    done: dict[str, Drop] = {}
    for _dist, hid, pid, q in candidates:
        if hid in done or load.get(pid, 0) >= per_pole:
            continue
        facing = _facing_edge(polys[hid], q)
        if facing is None:
            continue
        (px, py), (nx, ny) = facing
        rng = random.Random(f"{hid}:connector")
        b = houses[hid]
        base = elevations[hid]
        eave = float(b["eave_height_m"])
        mast = b["roof"] == "flat" and eave >= 3.0 and rng.random() < ROOF_MAST_SHARE
        if mast:
            x, y = px - nx * ROOF_INSET_M, py - ny * ROOF_INSET_M
            if not polys[hid].contains(Point(x, y)):
                x, y = px, py
            asset, z = _pick(rng, ROOF_KINDS), base + eave
            rot = math.degrees(math.atan2(q[1] - y, q[0] - x))  # a roof mast is turned towards its pole
        else:
            x, y = px, py
            asset = _pick(rng, WALL_KINDS)
            z = base + min(max(eave - EAVE_DROP_M, MIN_HEIGHT_M), eave)
            rot = math.degrees(math.atan2(ny, nx))  # a wall connector faces out of the wall
        # the wire must not go through a building: not another one, and not this house (it ends on its wall, or runs
        # over its roof to a mast)
        wire = LineString([q, (x, y)])
        if any(wire.intersection(polys[o].buffer(-0.1)).length > 0.05 for o in polys if not (mast and o == hid)
               and polys[o].bounds[0] <= max(q[0], x) + 1
               and polys[o].bounds[2] >= min(q[0], x) - 1 and polys[o].bounds[1] <= max(q[1], y) + 1
               and polys[o].bounds[3] >= min(q[1], y) - 1):
            continue
        done[hid] = Drop(hid, pid, asset, round(x, 3), round(y, 3), round(z, 3), round(rot % 360.0, 1))
        load[pid] = load.get(pid, 0) + 1
    return [done[h] for h in sorted(done)]


def apply(area: str, service_m: float, per_pole: int, dry_run: bool) -> int:
    from drycastle_pipeline.edits import DEFAULT_EDITS, dump_edits, validate
    from drycastle_pipeline.mcp_server import _edits_doc, edits_lock

    paths = area_paths(area)
    work = paths.work
    origin = json.loads((work / "parcels.json").read_text())["origin_epsg5514"]
    buildings = json.loads((work / "buildings.json").read_text())["buildings"]
    elevations = json.loads((work / "terrain.json").read_text())["building_elevation"]
    with edits_lock(DEFAULT_EDITS):
        edits = load_edits(DEFAULT_EDITS)
        poles = [
            (o["id"], o["position"][0] - origin[0], o["position"][1] - origin[1])
            for o in edits.objects
            if o["asset"].startswith(POLE_ASSET_PREFIX)
        ]
        taken = [
            (o["position"][0] - origin[0], o["position"][1] - origin[1])
            for o in edits.objects
            if o["asset"].startswith("prim:connector_")
        ]
        drops = plan_drops(buildings, elevations, poles, taken, service_m, per_pole)
        dwellings = sum(1 for b in buildings if b["class"] in HOUSE_CLASSES)
        kinds = {}
        for d in drops:
            kinds[d.asset[len("prim:connector_"):]] = kinds.get(d.asset[len("prim:connector_"):], 0) + 1
        print(f"{len(poles)} poles, {dwellings} dwellings: {len(drops)} service drops "
              f"({', '.join(f'{k} {v}' for k, v in sorted(kinds.items()))}); "
              f"{dwellings - len(drops)} dwellings without one (no pole within {service_m:g} m, or none with room)")
        if dry_run or not drops:
            return 0
        oid = max([int(o["id"][2:]) for o in edits.objects if o["id"][2:].isdigit()] or [0])
        cid = max([int(c[2:]) for c in edits.cables if c[2:].isdigit()] or [0])
        for d in drops:
            oid += 1
            cid += 1
            edits.objects.append({
                "id": f"o-{oid:04d}", "asset": d.asset, "position": [round(d.x + origin[0], 3), round(d.y + origin[1], 3)],
                "z": d.z, "rotation_deg": d.rotation_deg,
            })
            edits.cables[f"C-{cid:04d}"] = {"from": d.pole, "to": f"o-{oid:04d}"}
        errors = validate(_edits_doc(edits))
        if errors:
            print("refused, the result would be invalid:\n" + "\n".join(errors), file=sys.stderr)
            return 1
        dump_edits(edits, DEFAULT_EDITS)
    print(f"wrote {len(drops)} connectors and cables to {DEFAULT_EDITS}")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--area", default=DEFAULT_AREA)
    ap.add_argument("--service-m", type=float, default=SERVICE_M, help=f"furthest pole a house is connected to (default {SERVICE_M:g})")
    ap.add_argument("--per-pole", type=int, default=PER_POLE, help=f"most drops from one pole (default {PER_POLE})")
    ap.add_argument("--dry-run", action="store_true", help="say what would be placed, write nothing")
    args = ap.parse_args(argv)
    return apply(args.area, args.service_m, args.per_pole, args.dry_run)


if __name__ == "__main__":
    raise SystemExit(main())
