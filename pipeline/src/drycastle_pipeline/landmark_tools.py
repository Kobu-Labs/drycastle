"""Tools for modelling a landmark from photos (``.claude/skills/photo-to-building``): read the footprint, look at the mesh.

    uv run python -m drycastle_pipeline.landmark_tools footprint P-0666 [--view X,Y]   (a parcel, a property G-xxxx, or building ids)
    uv run python -m drycastle_pipeline.landmark_tools render w378900337 w1226408502 --model bus_garage --out DIR

``footprint`` prints, for each building, its corners (in the order ``landmark_kit.check_footprint`` wants them), every
edge with its length, the compass side it faces, the building it is shared with and the distance to the nearest road, and
the corners as Python to paste; with ``--view`` (where a photo was taken) also which edges that photo shows. ``render`` builds the landmark mesh of the buildings together (``--model`` overrides the
model name, so ``make rules`` is not needed while iterating; ``--collision`` draws the collision mesh) and writes one
picture per outer edge, seen square-on from outside at eye height (``edge_<building>_<i>.png``), two from above
(``roof_a.png``, ``roof_b.png``) and, with ``--view X,Y`` (where a photo was taken), ``view.png`` as seen from there.

    uv run python -m drycastle_pipeline.landmark_tools near w378900337 [--within 6]

``near`` lists the fences, placed objects, lamps and trees of the exported map that stand close to the footprint.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image

from drycastle_pipeline.config import REPO_ROOT

WORK = REPO_ROOT / "data" / "village" / "work"
SIDES = ("N", "NE", "E", "SE", "S", "SW", "W", "NW")


def _buildings(ids: list[str]) -> list[dict]:
    by_id = {b["id"]: b for b in json.loads((WORK / "buildings.json").read_text())["buildings"]}
    if len(ids) == 1 and ids[0][:2] in ("P-", "G-"):  # a parcel, or a property (group of parcels): every building on it
        doc = json.loads((WORK / "parcels.json").read_text())
        wanted = {ids[0]}
        if ids[0].startswith("G-"):
            groups = json.loads((REPO_ROOT / "pipeline" / "config" / "edits.json").read_text()).get("groups", {})
            if ids[0] not in groups:
                raise SystemExit(f"no property {ids[0]} in edits.json")
            wanted = set(groups[ids[0]]["parcels"])
        found = [p for p in doc["parcels"] if p["id"] in wanted]
        if not found:
            raise SystemExit(f"no parcel {ids[0]}")
        ids = sorted(bid for p in found for bid in p["buildings"])
        if not ids:
            raise SystemExit(f"{ids or wanted}: no building stands there")
    missing = [i for i in ids if i not in by_id]
    if missing or not ids:
        raise SystemExit(f"no such building(s): {missing or ids}")
    return [by_id[i] for i in ids]


def _edges(b: dict) -> list[dict]:
    """The edges of the footprint with their outward normal (the ring may run either way round)."""
    ring = [tuple(p) for p in b["footprint"]["exterior"]]
    area = sum(p[0] * q[1] - q[0] * p[1] for p, q in zip(ring, ring[1:] + ring[:1])) / 2
    out = []
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        n = (uy, -ux) if area > 0 else (-uy, ux)
        out.append({"i": i, "p": p, "q": q, "length": length, "normal": n, "bearing": math.degrees(math.atan2(n[0], n[1])) % 360})
    return out


def _local(xy: tuple[float, float]) -> tuple[float, float]:
    """A viewpoint in local metres; absolute S-JTSK (a sketch point of the editor) is moved by the map origin."""
    if abs(xy[0]) < 100000:
        return xy
    ox, oy = json.loads((WORK / "parcels.json").read_text())["origin_epsg5514"]
    return (xy[0] - ox, xy[1] - oy)


def _seen(e: dict, mid, view) -> str:
    """How an edge shows from the viewpoint: its wall faces the camera when the camera is on its outer side."""
    if view is None:
        return ""
    dx, dy = view[0] - mid[0], view[1] - mid[1]
    dist = math.hypot(dx, dy)
    cos = (dx * e["normal"][0] + dy * e["normal"][1]) / max(dist, 1e-9)
    if cos <= 0.05:
        return "  view: HIDDEN (faces away)"
    ang = math.degrees(math.acos(min(1.0, cos)))
    return f"  view: VISIBLE from {dist:.0f} m, {ang:.0f} deg off square" + (" (head-on)" if ang < 25 else " (oblique)" if ang > 60 else "")


def _seg_dist(p, a, b) -> float:
    ax, ay, bx, by = a[0], a[1], b[0], b[1]
    t = max(0.0, min(1.0, ((p[0] - ax) * (bx - ax) + (p[1] - ay) * (by - ay)) / max(1e-9, (bx - ax) ** 2 + (by - ay) ** 2)))
    return math.dist(p, (ax + (bx - ax) * t, ay + (by - ay) * t))


def footprint(ids: list[str], view: tuple[float, float] | None = None) -> None:
    bs = _buildings(ids)
    view = _local(view) if view else None
    all_b = json.loads((WORK / "buildings.json").read_text())["buildings"]
    roads = json.loads((WORK / "roads.json").read_text()).get("edges", []) if (WORK / "roads.json").exists() else []
    elev = json.loads((WORK / "terrain.json").read_text())["building_elevation"]
    for b in bs:
        ring = b["footprint"]["exterior"]
        es = _edges(b)
        ccw = sum(p[0] * q[1] - q[0] * p[1] for p, q in zip(ring, ring[1:] + ring[:1])) > 0
        print(f"\n{b['id']}  parcel {b.get('parcel')}  model {b.get('model')}  generated: {b.get('roof')} roof, eave {b.get('eave_height_m')} m, "
              f"height {b.get('height_m')} m  base z {elev.get(b['id'], 0.0):.2f}  ring {'counter-clockwise' if ccw else 'CLOCKWISE (edge() needs counter-clockwise: reverse it)'}")  # fmt: skip
        skew = max(abs(90 - math.degrees(math.acos(max(-1.0, min(1.0, e0["normal"][0] * e1["normal"][0] + e0["normal"][1] * e1["normal"][1]))))) for e0, e1 in zip(es, es[1:] + es[:1]))
        print(f"  {len(ring)} corners, " + ("square corners" if skew < 2 else f"NOT square: corners are up to {skew:.0f} deg off 90 (the template copes with 4 corners; renders will look slightly skewed)") + ("" if len(ring) == 4 else "  MORE THAN 4 CORNERS: the template does not fit, see garage.py"))
        for k, p in enumerate(ring):
            print(f"  corner {k}: ({p[0]:.3f}, {p[1]:.3f})")
        for e in es:
            mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
            shared = [o["id"] for o in all_b if o["id"] != b["id"] and any(
                _seg_dist(mid, tuple(o["footprint"]["exterior"][j]), tuple(o["footprint"]["exterior"][(j + 1) % len(o["footprint"]["exterior"])])) < 0.1
                for j in range(len(o["footprint"]["exterior"])))]  # fmt: skip
            road = min((_seg_dist(mid, a, c) for r in roads for a, c in zip(r["polyline"], r["polyline"][1:])), default=float("nan"))
            side = SIDES[round(e["bearing"] / 45) % 8]
            print(f"  edge {e['i']}: corner {e['i']} -> {(e['i'] + 1) % len(ring)}  {e['length']:6.2f} m  faces {side:2} ({e['bearing']:5.1f} deg)  "
                  f"road {road:5.1f} m" + _seen(e, mid, view) + (f"  SHARED with {', '.join(shared)} (an inner wall: no photo side)" if shared else ""))  # fmt: skip
        print(f"  RING = ({', '.join(f'({p[0]}, {p[1]})' for p in ring)})")
    print("\nSeen from outside, an edge of a counter-clockwise ring runs from its first corner on the LEFT to its second on the RIGHT.")


def near(ids: list[str], within: float) -> None:
    """What stands within ``within`` metres of the footprints in the exported map: fences, placed objects, lamps, trees."""
    from shapely.geometry import LineString, Point, Polygon

    bs = _buildings(ids)
    out = REPO_ROOT / "out" / "village" / "map"
    count = 0
    for b in bs:
        poly = Polygon(b["footprint"]["exterior"])
        es = _edges(b)

        def side(pt, es=es):
            e = min(es, key=lambda e: _seg_dist(pt, e["p"], e["q"]))
            return f"edge {e['i']}"

        for f in json.loads((out / "fences.json").read_text())["fences"]:
            d = poly.distance(LineString([f["a"][:2], f["b"][:2]]))
            if d <= within:
                mid = ((f["a"][0] + f["b"][0]) / 2, (f["a"][1] + f["b"][1]) / 2)
                print(f"{b['id']}  fence {f.get('type')} {f.get('height_m')} m ({f.get('parcel')})  {d:.1f} m, off {side(mid)}")
                count += 1
        for name, key in (("objects", "objects"), ("lamps", "lamps"), ("trees", "trees")):
            path = out / f"{name}.json"
            if not path.exists():
                continue
            for o in json.loads(path.read_text()).get(key, []):
                pos = o.get("position") or o.get("pos") or [o.get("x"), o.get("y")]
                if pos[0] is None:
                    continue
                d = poly.distance(Point(pos[0], pos[1]))
                if d <= within:
                    print(f"{b['id']}  {name[:-1]} {o.get('id', '')} {o.get('type') or o.get('kind') or o.get('shape') or ''}  {d:.1f} m, off {side(pos[:2])}")
                    count += 1
    print(f"{count} thing(s) within {within} m. In front of a door or a gate they are in the way: report them, change nothing.")


def _raster(tris: np.ndarray, cols: np.ndarray, eye, target, fov: float, size=(1400, 900)) -> Image.Image:
    """A z-buffered picture: perspective, back faces dropped, shaded by a fixed sun."""
    w, h = size
    eye, target = np.array(eye, float), np.array(target, float)
    f = target - eye
    f /= np.linalg.norm(f)
    r = np.cross(f, [0, 0, 1.0])
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    rel = tris - eye
    x, y, z = rel @ r, rel @ u, rel @ f
    n = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
    n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-12
    sun = np.array([0.45, 0.35, 0.82])
    shade = 0.78 + 0.22 * np.clip(n @ (sun / np.linalg.norm(sun)), 0, 1)
    keep = (((eye - tris.mean(1)) * n).sum(1) > 0) & (z.min(1) > 0.2)
    k = (w / 2) / math.tan(math.radians(fov / 2))
    img = np.empty((h, w, 3), np.uint8)
    img[:] = (176, 204, 228)
    img[h // 2 :] = (132, 140, 118)  # the ground below the horizon
    depth = np.full((h, w), np.inf)
    for i in np.nonzero(keep)[0]:
        px, py = w / 2 + k * x[i] / z[i], h / 2 - k * y[i] / z[i]
        x0, x1 = max(0, int(px.min())), min(w - 1, int(px.max()) + 1)
        y0, y1 = max(0, int(py.min())), min(h - 1, int(py.max()) + 1)
        if x0 > x1 or y0 > y1:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (py[1] - py[2]) * (px[0] - px[2]) + (px[2] - px[1]) * (py[0] - py[2])
        if abs(d) < 1e-9:
            continue
        a = ((py[1] - py[2]) * (gx - px[2]) + (px[2] - px[1]) * (gy - py[2])) / d
        b = ((py[2] - py[0]) * (gx - px[2]) + (px[0] - px[2]) * (gy - py[2])) / d
        c = 1 - a - b
        inv = a / z[i, 0] + b / z[i, 1] + c / z[i, 2]  # perspective-correct depth
        zz = 1 / np.maximum(inv, 1e-9)
        sub = depth[y0 : y1 + 1, x0 : x1 + 1]
        hit = (a >= 0) & (b >= 0) & (c >= 0) & (zz < sub)
        sub[hit] = zz[hit]
        img[y0 : y1 + 1, x0 : x1 + 1][hit] = np.clip(cols[i] * shade[i], 0, 255).astype(np.uint8)
    return Image.fromarray(img)


def render(ids: list[str], out: Path, model: str | None, collision: bool, view: tuple[float, float] | None = None) -> list[Path]:
    from drycastle_pipeline.landmarks import build_landmark
    from drycastle_pipeline.meshes import Mesh

    bs = _buildings(ids)
    if model:
        bs = [{**b, "model": model} for b in bs]
    if any(not b.get("model") for b in bs):
        raise SystemExit("a building has no model: pass --model NAME (the name registered in landmarks.FOOTPRINT_MODELS)")
    mesh = Mesh.concat([build_landmark(b, detail=not collision) for b in bs])
    tris, cols = mesh.tris, mesh.colours.astype(float)
    lo, hi = tris.reshape(-1, 3).min(0), tris.reshape(-1, 3).max(0)
    centre, top = (lo + hi) / 2, hi[2]
    span = float(np.linalg.norm(hi[:2] - lo[:2]))
    out.mkdir(parents=True, exist_ok=True)
    written = []
    all_rings = [[tuple(p) for p in b["footprint"]["exterior"]] for b in bs]
    for b in bs:
        for e in _edges(b):
            mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
            inner = any(_seg_dist(mid, ring[j], ring[(j + 1) % len(ring)]) < 0.1 for ring in all_rings if ring != [tuple(p) for p in b["footprint"]["exterior"]] for j in range(len(ring)))  # fmt: skip
            if e["length"] < 2.0 or inner:
                continue
            dist = max(e["length"], top * 1.6) * 1.15 + 3.0
            eye = (mid[0] + e["normal"][0] * dist, mid[1] + e["normal"][1] * dist, 1.7)
            path = out / f"edge_{b['id']}_{e['i']}.png"
            _raster(tris, cols, eye, (mid[0], mid[1], top * 0.45), 50).save(path)
            written.append(path)
    if view:  # as the photo taken there: eye height, looking at the building
        vx, vy = _local(view)
        path = out / "view.png"
        _raster(tris, cols, (vx, vy, 1.6), (centre[0], centre[1], top * 0.5), 62).save(path)
        written.append(path)
    for name, ang in (("roof_a", 35.0), ("roof_b", 215.0)):
        d = (math.cos(math.radians(ang)), math.sin(math.radians(ang)))
        eye = (centre[0] + d[0] * span * 1.1, centre[1] + d[1] * span * 1.1, top + span * 0.7)
        path = out / f"{name}.png"
        _raster(tris, cols, eye, (centre[0], centre[1], top * 0.6), 50).save(path)
        written.append(path)
    for p in written:
        print(p)
    return written


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("footprint")
    f.add_argument("ids", nargs="+")
    f.add_argument("--view", help="X,Y of where a photo was taken (local metres, or absolute S-JTSK as a sketch point): marks the edges it shows")
    r = sub.add_parser("render")
    r.add_argument("ids", nargs="+")
    r.add_argument("--out", type=Path, required=True)
    r.add_argument("--model")
    r.add_argument("--collision", action="store_true")
    r.add_argument("--view", help="X,Y of where a photo was taken: also writes view.png, the building as seen from there")
    n = sub.add_parser("near")
    n.add_argument("ids", nargs="+")
    n.add_argument("--within", type=float, default=6.0)
    a = ap.parse_args(argv)
    if a.cmd == "footprint":
        footprint(a.ids, tuple(float(v) for v in a.view.split(",")) if a.view else None)
    elif a.cmd == "near":
        near(a.ids, a.within)
    else:
        render(a.ids, a.out, a.model, a.collision, tuple(float(v) for v in a.view.split(",")) if a.view else None)


if __name__ == "__main__":
    main()
