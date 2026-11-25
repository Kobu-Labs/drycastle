"""How well each building's footprint (traced in OpenStreetMap) agrees with the cadastre's plots. A report, nothing
is changed:

    uv run python -m drycastle_pipeline.buildings.cadastre_check            (the counts and the worst cases)
    uv run python -m drycastle_pipeline.buildings.cadastre_check --all      (every building that is not "ok")

The same judgement corrects the footprints when the buildings are derived (buildings/derive.py), for every building
not listed under keep_traced_footprint in overrides.yaml. Run after it, this report shows what is still off.

Each building gets one finding:

* ``match``: its plot is the house itself (a house plot) and the footprint lies on it.
* ``replace``: its plot is a house plot and the footprint is beside it or of another size: the plot is the surveyed
  outline and could be taken instead.
* ``shift``: its plot holds a yard too; the footprint leaves it, and sliding it a little puts it inside.
* ``clip``: it runs onto a neighbour's building plot (an attached house traced too long).
* ``look``: it leaves its plot and none of the above explains it.
* ``ok``: inside its plot, which holds a yard too: the cadastre says nothing more.
"""

import argparse
import json
import sys
from pathlib import Path

from shapely.affinity import translate
from shapely.geometry import Polygon, shape

from drycastle_pipeline.parcels.normalise import DEFAULT_WORK_DIR

HOUSE_PLOT_M2 = (25.0, 260.0)  # a plot of this size ...
HOUSE_PLOT_FILL = 0.7  # ... that fills this much of its own bounding rectangle ...
HOUSE_PLOT_COVERED = 0.5  # ... and is covered this much by one footprint, is that house's outline
MATCH_IOU = 0.9
REPLACE_AREA = (0.7, 1.4)  # the plot is taken for the footprint only when it is about as large: else it is a part, or more
CLIP_KEEPS = 0.5  # a footprint cut at a neighbour's plot must keep this much of itself
SPILL_M2 = 5.0  # a footprint this much outside its plot is not a tracing's roughness
MAX_SHIFT_M = 2.5


def is_house_plot(plot: Polygon, kind: str, footprint: Polygon) -> bool:
    if kind != "building_plot" or not HOUSE_PLOT_M2[0] <= plot.area <= HOUSE_PLOT_M2[1]:
        return False
    return plot.area / plot.minimum_rotated_rectangle.area >= HOUSE_PLOT_FILL and plot.intersection(footprint).area >= HOUSE_PLOT_COVERED * plot.area


def best_shift(footprint: Polygon, plot: Polygon) -> tuple[float, float, float]:
    """The smallest slide (dx, dy) within MAX_SHIFT_M that leaves the least of the footprint outside the plot:
    (dx, dy, area still outside)."""
    best = (footprint.difference(plot).area, 0.0, 0.0, 0.0)
    steps = [k * 0.25 for k in range(-int(MAX_SHIFT_M / 0.25), int(MAX_SHIFT_M / 0.25) + 1)]
    for dx in steps:
        for dy in steps:
            d = (dx * dx + dy * dy) ** 0.5
            if 0 < d <= MAX_SHIFT_M:
                out = translate(footprint, dx, dy).difference(plot).area
                if out < best[0] - 0.5 or (abs(out - best[0]) <= 0.5 and d < best[1]):
                    best = (out, d, dx, dy)
    return best[2], best[3], best[0]


class Plots:
    """The cadastre's plots as geometry, looked up by what they touch."""

    def __init__(self, parcels: list[dict]) -> None:
        from shapely.strtree import STRtree

        self.parcels = [p for p in parcels if p.get("polygon")]
        parcels = self.parcels
        self.geoms = [shape(p["polygon"]).buffer(0) for p in parcels]
        self.by_id = {p["id"]: g for p, g in zip(parcels, self.geoms)}
        self.tree = STRtree(self.geoms)

    def touching(self, f: Polygon) -> list[tuple[float, dict, Polygon]]:
        """(area shared, plot, its geometry) for every plot the footprint lies on, the most shared first."""
        got = [(self.geoms[int(i)].intersection(f).area, self.parcels[int(i)], self.geoms[int(i)]) for i in self.tree.query(f)]
        return sorted((t for t in got if t[0] > 0), key=lambda t: -t[0])


def judge(f: Polygon, parcel_id: str | None, plots: Plots) -> tuple[dict, Polygon]:
    """One footprint against the cadastre: (the finding, the footprint as the cadastre would have it). The second is
    the footprint itself for match, ok and look."""
    own = plots.by_id.get(parcel_id)
    hits = plots.touching(f)
    house = next(((p, g) for _, p, g in hits if is_house_plot(g, p["kind"], f)), None)
    if house is not None:
        p, g = house
        iou = g.intersection(f).area / g.union(f).area
        row = {"plot": p["id"], "plot_area": round(g.area, 1), "iou": round(iou, 2)}
        if iou >= MATCH_IOU:
            return {"finding": "match", **row}, f
        if REPLACE_AREA[0] <= g.area / f.area <= REPLACE_AREA[1]:
            return {"finding": "replace", **row}, Polygon(g.exterior).simplify(0.05)
        return {"finding": "look", **row, "why": "its house plot is of another size"}, f
    if own is None:
        return {"finding": "look", "why": "no plot"}, f
    spill = f.difference(own).area
    if spill < SPILL_M2:
        return {"finding": "ok"}, f
    dx, dy, left = best_shift(f, own)
    if left < 1.0:
        return {"finding": "shift", "spill": round(spill, 1), "dx": dx, "dy": dy}, translate(f, dx, dy)
    onto = max(((a, p, g) for a, p, g in hits if p["id"] != parcel_id and p["kind"] == "building_plot"), key=lambda t: t[0], default=None)
    if onto is not None and onto[0] >= SPILL_M2:
        cut = f.difference(onto[2]).buffer(-0.05).buffer(0.05, join_style=2)
        cut = max(cut.geoms, key=lambda g: g.area) if cut.geom_type == "MultiPolygon" else cut
        if not cut.is_empty and cut.area >= CLIP_KEEPS * f.area:
            return {"finding": "clip", "spill": round(spill, 1), "onto": onto[1]["id"], "onto_m2": round(onto[0], 1)}, Polygon(cut.exterior).simplify(0.05)
    return {"finding": "look", "spill": round(spill, 1), "why": "leaves its plot"}, f


def check(buildings: list[dict], parcels: list[dict]) -> list[dict]:
    plots = Plots(parcels)
    out = []
    for b in buildings:
        f = Polygon(b["footprint"]["exterior"], b["footprint"].get("holes") or []).buffer(0)
        finding, _ = judge(f, b.get("parcel"), plots)
        out.append({"id": b["id"], "parcel": b.get("parcel"), "area": round(f.area, 1), **finding})
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--work-dir", type=Path, default=DEFAULT_WORK_DIR)
    ap.add_argument("--all", action="store_true", help="list every building that is not ok or match")
    ap.add_argument("--json", type=Path, help="also write every finding to this file")
    args = ap.parse_args(argv)
    rows = check(json.loads((args.work_dir / "buildings.json").read_text())["buildings"], json.loads((args.work_dir / "parcels.json").read_text())["parcels"])
    counts: dict[str, int] = {}
    for r in rows:
        counts[r["finding"]] = counts.get(r["finding"], 0) + 1
    print(f"cadastre check: {len(rows)} buildings: " + ", ".join(f"{k} {counts.get(k, 0)}" for k in ("match", "ok", "replace", "shift", "clip", "look")))
    bad = sorted((r for r in rows if r["finding"] not in ("ok", "match")), key=lambda r: (r["finding"], r.get("iou", 0), -r.get("spill", 0)))
    for r in bad if args.all else bad[:25]:
        print("  " + ", ".join(f"{k} {v}" for k, v in r.items()))
    if args.json:
        args.json.write_text(json.dumps(rows, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
