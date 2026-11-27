"""A building from its Street View captures: the geometry is found by the computer, a model only names what it sees.

    uv run python -m drycastle_pipeline.photos.facade_pipeline ends  w378920491 [--out OUT]   (default OUT: beside the capture, <photos>/<id>/run; walls/apply take OUT or the id)
    uv run python -m drycastle_pipeline.photos.facade_pipeline walls OUT            (reads OUT/ends.json)
    uv run python -m drycastle_pipeline.photos.facade_pipeline apply OUT [--dry] [--replace]   (reads OUT/names.json)

The three commands and the two answers between them:

``ends``   For every capture: the camera's heading and tilt from the straight lines in the picture, then, for every
           wall the capture sees, the upright lines near each of its ends, lettered (``ends_c<n>_e<edge>_left.png``,
           ``..._right.png``). ``ends_ask.md`` says what to answer.
ANSWER 1   ``OUT/ends.json``: which letter is where each wall stops.
``walls``  The camera is moved so the footprint's corners fall on the chosen lines; its height comes from the terrain.
           Each wall is flattened to a straight-on picture in metres with its level lines lettered and its dark
           rectangles numbered (``wall_c<n>_e<edge>.png``). ``walls_ask.md`` says what to answer.
ANSWER 2   ``OUT/names.json``: the line that is the wall's top, what each rectangle is, the roof's form.
``apply``  Heights, openings, the ridge (from the roof's outline against the sky), colours; the building detail and
           the building's entry in ``overrides.yaml`` (as ``landmark_photo apply`` writes them); ``overlay_<n>.png``.

Nothing here asks a model for a pixel. What it cannot find it reports as a ``PROBLEM:`` line.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np

from drycastle_pipeline.photos import facade_rectify as fr
from drycastle_pipeline.photos.facade_rectify import PX_PER_M
from drycastle_pipeline.photos.landmark_photo import project
from drycastle_pipeline.photos.palettes import (  # (the lists live in palettes.py, which the building worker loads without numpy)
    FRAME_COLOURS,
    ROOF_COLOURS,
    SURROUND_COLOURS,
    WALL_COLOURS,
)

MAX_CAPTURES = 3
MIN_WALL_M = 2.0
MAX_OFF_SQUARE_DEG = 78.0
ROOFS = ("gabled", "hipped", "flat")
OPENING_KINDS = ("window", "door", "glass_door", "garage_door")  # glass_door: a glazed door to the floor, built as a window from the ground
ROOF_EDGES = ("eave", "gable")
LINE_MERGE_PX = 10
# A photo's colours are the light of that hour, not the paint: a cream wall in shade is grey-brown in pixels. What a
# model can say reliably is the kind of colour; the photo then only gives the hue. Lightness is OpenCV's L (0..255).
WALL_TONES = {"light": 205.0, "mid": 150.0, "dark": 95.0}
RENDER_GLASS = (58, 70, 84)  # panes in the pictures: a window by day is dark, not the pale blue the generator tags it with
FRAME_M = 0.06
DOOR_MAX_W_M = 1.25  # an entrance door is not wider: a wider rectangle took in what stands beside the leaf


# ---------------------------------------------------------------- shared helpers


def _col(s: float) -> int:
    return round((s + fr.SIDE_M) * fr.PX_PER_M)


def _row(z: float) -> int:
    return round((fr.Z_RANGE[1] - z) * fr.PX_PER_M)


def _z_of_row(y: float) -> float:
    return fr.Z_RANGE[1] - y / fr.PX_PER_M


def _s_of_col(x: float) -> float:
    return x / fr.PX_PER_M - fr.SIDE_M


def _letters(n: int) -> list[str]:
    return [chr(ord("A") + i) for i in range(n)]


def _load(out: Path) -> dict:
    return json.loads((out / "state.json").read_text())


def _hex(bgr) -> str:
    b, g, r = (int(max(0, min(255, round(float(v))))) for v in bgr)
    return f"#{r:02x}{g:02x}{b:02x}"


def wall_directions(ring) -> list[np.ndarray]:
    """The level directions the building's walls run along (walls under MIN_WALL_M and repeats left out)."""
    out: list[np.ndarray] = []
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        v = np.array([q[0] - p[0], q[1] - p[1], 0.0])
        n = np.linalg.norm(v)
        if n < MIN_WALL_M:
            continue
        v /= n
        if all(abs(v @ o) < math.cos(math.radians(3)) for o in out):
            out.append(v)
    return out


def visible_walls(b: dict, others: list[dict], cam: dict) -> list[dict]:
    """The walls a camera can measure: facing it, not too oblique, not shared with a neighbour, both ends in the
    picture. Each {edge, left, right} names its ends as corner numbers, left and right as the picture shows them."""
    from drycastle_pipeline.photos.landmark_tools import _edges, _seg_dist

    ring = b["footprint"]["exterior"]
    walls = []
    for e in _edges(b):
        mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
        if e["length"] < MIN_WALL_M:
            continue
        if any(_seg_dist(mid, tuple(o["footprint"]["exterior"][j]), tuple(o["footprint"]["exterior"][(j + 1) % len(o["footprint"]["exterior"])])) < 0.1
               for o in others for j in range(len(o["footprint"]["exterior"]))):  # fmt: skip
            continue
        dx, dy = cam["x"] - mid[0], cam["y"] - mid[1]
        cos = (dx * e["normal"][0] + dy * e["normal"][1]) / max(math.hypot(dx, dy), 1e-9)
        if cos < math.cos(math.radians(MAX_OFF_SQUARE_DEG)):
            continue
        i, j = e["i"], (e["i"] + 1) % len(ring)
        px = project(cam, np.array([[*ring[i], cam["z"]], [*ring[j], cam["z"]]]))
        if not np.isfinite(px).all() or px[:, 0].min() < -0.05 * cam["w"] or px[:, 0].max() > 1.05 * cam["w"]:
            continue
        left, right = (i, j) if px[0, 0] < px[1, 0] else (j, i)
        walls.append({"edge": i, "left": left, "right": right, "length": round(e["length"], 2),
                      "off_square_deg": round(math.degrees(math.acos(min(1.0, cos))))})  # fmt: skip
    return walls


def order_marks(cam: dict, ring, marks: dict) -> dict[int, float]:
    """Corner marks (footprint corner -> picture column) with the two ends of a wall put right when they were marked
    the wrong way round: a wall's ends lie in the picture in the order the map gives them, whatever the camera's exact
    place."""
    m = {int(k): float(x) for k, x in (marks or {}).items() if x is not None}
    for i in sorted(m):
        j = (i + 1) % len(ring)
        if j in m:
            px = project(cam, np.array([[ring[i][0], ring[i][1], 0.0], [ring[j][0], ring[j][1], 0.0]]))
            if (px[0][0] - px[1][0]) * (m[i] - m[j]) < 0:
                m[i], m[j] = m[j], m[i]
    return m


def move_camera(cam: dict, picks: list[tuple[tuple[float, float], float]]) -> dict:
    """The camera moved (heading kept) so each footprint corner falls on its picture column: least squares over all
    (corner, column) pairs. Needs two that are not in line with the camera."""
    rows, rhs = [], []
    for corner, x in picks:
        d = fr.bearing(cam, x)
        rows.append([d[1], -d[0]])  # the camera is on the line through the corner along d
        rhs.append(corner[0] * d[1] - corner[1] * d[0])
    pos, *_ = np.linalg.lstsq(np.array(rows), np.array(rhs), rcond=None)
    return {**cam, "x": float(pos[0]), "y": float(pos[1])}


# ---------------------------------------------------------------- stage 1: ends


def stage_ends(building: str, out: Path, use_marks: bool = True, captures: list[int] | None = None) -> None:
    """captures: the capture numbers to use (default: the newest MAX_CAPTURES)."""
    import cv2

    from drycastle_pipeline.photos.landmark_tools import WORK
    from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR

    all_b = json.loads((WORK / "buildings.json").read_text())["buildings"]
    b = next((x for x in all_b if x["id"] == building), None)
    if b is None:
        raise SystemExit(f"no such building {building}")
    if b.get("model"):
        raise SystemExit(f"{building} is a hand-modelled landmark ({b['model']}): not for this tool")
    ring = b["footprint"]["exterior"]
    others = [o for o in all_b if o["id"] != building]
    sides = sorted((PHOTOS_DIR / building).glob("*.json"), key=lambda p: int(p.stem))
    if captures is None:
        sides = sides[-MAX_CAPTURES:]
    else:
        have = {int(p.stem): p for p in sides}
        missing = sorted(set(captures) - set(have))
        if missing:
            raise SystemExit(f"{building} has no capture {', '.join(map(str, missing))} (it has: {', '.join(map(str, sorted(have))) or 'none'})")
        if len(set(captures)) > MAX_CAPTURES:
            raise SystemExit(f"at most {MAX_CAPTURES} captures at once, got {len(set(captures))}")
        sides = [have[n] for n in sorted(set(captures))]
    if not sides:
        raise SystemExit(f"{building} has no captures in {PHOTOS_DIR / building}: take them with streetview_follow --capture")
    out.mkdir(parents=True, exist_ok=True)
    dirs = [np.array([0, 0, 1.0]), *wall_directions(ring)]
    state = {"building": building, "parcel": b.get("parcel"), "captures": []}
    ask: list[str] = []
    for side in sides:
        c = json.loads(side.read_text())
        n = int(side.stem)
        photo = cv2.imread(str(side.parent / c["photo"]))
        segs = fr.find_segments(cv2.cvtColor(photo, cv2.COLOR_BGR2GRAY))
        cam = fr.refine_rotation(fr.capture_camera(c), segs, dirs)
        np.save(out / f"segs_c{n}.npy", segs)
        up = fr.classify(cam, segs, {"up": dirs[0], **{f"d{k}": d for k, d in enumerate(dirs[1:])}})
        walls = visible_walls(b, others, cam)
        if not use_marks:  # (to compare: the wall ends picked from the computer's candidates instead)
            c = {**c, "ends": {}}
        hand_ends = order_marks(cam, ring, c.get("ends") or {})
        if any(hand_ends[int(k)] != float(x) for k, x in (c.get("ends") or {}).items() if x is not None):
            print(f"capture {n}: the two ends of a wall were marked the wrong way round: swapped")
        for w in walls:
            lo, hi = ring[w["left"]], ring[w["right"]]
            outline = project(cam, np.array([[*lo, 0.0], [*hi, 0.0], [*hi, 3.0], [*lo, 3.0]]))
            for end in ("left", "right"):
                marked = hand_ends.get(int(w[end]))
                if marked is not None:  # marked by hand at capture (capture_guide): nothing to ask
                    w[end + "_marked_x"], w[end + "_candidates"] = float(marked), []
                    continue
                cands, where = fr.end_candidates(cam, segs, up, ring[w[end]])
                for cnd, letter in zip(cands, _letters(len(cands))):
                    cnd["letter"] = letter
                key = f"c{n}_e{w['edge']}_{end}"
                cv2.imwrite(str(out / f"ends_{key}.png"), fr.candidates_picture(photo, cands, where, f"{end.upper()} end of the wall is in this box", outline))
                w[end + "_candidates"] = cands
                ask.append(key)
        state["captures"].append({"n": n, "photo": str(side.parent / c["photo"]), "camera": cam, "walls": walls,
                                  "turned_deg": round(cam["yaw"] - fr.capture_camera(c)["yaw"], 2)})  # fmt: skip
        if not state["captures"][:-1]:
            print(f"{building}  parcel {b.get('parcel')}  ({b.get('roof')} roof as generated)")
        print(f"capture {n}: {len(segs)} straight segments, heading turned {state['captures'][-1]['turned_deg']:+.1f} deg; "
              + (", ".join(f"edge {w['edge']} ({w['length']} m, {w['off_square_deg']} deg off square)" for w in walls) or "NO wall of the building is measurable from here"))  # fmt: skip
    (out / "state.json").write_text(json.dumps(state, indent=1))
    template = {k: "?" for k in ask}
    if not ask:
        (out / "ends.json").write_text("{}\n")
        print(f"\nevery wall end was marked by hand at capture: nothing to answer, {out}/ends.json is written. next: walls")
        return
    (out / "ends_ask.md").write_text(ENDS_ASK.format(pictures="\n".join(f"- `ends_{k}.png`" for k in ask), template=json.dumps(template, indent=1)))
    print(f"\nnext: read {out}/ends_ask.md, look at the {len(ask)} pictures it lists, write {out}/ends.json")


ENDS_ASK = """# Answer 1: where each wall stops

One picture per wall end. Its file name says which wall (`e<edge>`) and which end, LEFT or RIGHT as the photo shows it.

On the left of a picture is the whole photo. The thin WHITE outline on it is where the map puts that wall, roughly
(up to a metre or so off): it tells you WHICH wall is meant and about how long it is. The wall is one flat surface of
the house; a lower wing, a garage or a neighbour attached to it is NOT part of it. The yellow box is the part enlarged
on the right. On the enlarged part, upright lines found by the computer are drawn in colour, each with a letter above
it (solid at the top and bottom, dashed between, so it does not hide what it marks; the letters sit in two rows, a
letter belongs to the line of the same colour right under it).

For each picture choose the ONE letter whose line lies where that wall's own surface ends:
- at an outside corner of the house: the edge where the wall meets the next wall. Not the roof's edge above it, which
  sticks out further.
- where another building or a lower wing is attached: the line where this wall's surface stops and the other begins,
  at the height of the windows. That the wall shows on above a lower wing's roof does not move its end.
- a downpipe standing right at that corner or junction marks it well: choose it if no line lies on the edge itself.
Never a window or door frame, a fence pillar, a pole, a tree, or anything standing on the roof (a chimney).

Answer with a letter. If the end lies between two lines next to each other, or two lines are so close (less than a
window frame's width apart) that you cannot say which one it is, answer both, e.g. `"B-C"` (the middle between them is
taken). If the end is hidden (a tree, a car) or no line is near it, answer `"none"`.

Pictures:
{pictures}

Write `ends.json` in this folder:

{template}
"""


# ---------------------------------------------------------------- stage 2: walls


def _pick_x(cands: list[dict], answer: str) -> float | None:
    by = {c["letter"]: c["x"] for c in cands}
    parts = [p.strip().upper() for p in str(answer).replace("/", "-").split("-")]
    xs = [by[p] for p in parts if p in by]
    return float(np.mean(xs)) if xs and len(xs) == len(parts) else None


def find_lines(cam: dict, segs: np.ndarray, kinds: list, h_inv, size, length: float, keep: int = 12) -> list[dict]:
    """The level lines on a flattened wall: [{z, length_m}] top to bottom, the ``keep`` longest. A line is every
    segment along the wall whose flat picture lies within LINE_MERGE_PX of the same row."""
    import cv2

    hits = []
    for s, k in zip(segs, kinds):
        if k != "wall":
            continue
        p = cv2.perspectiveTransform(s.reshape(1, 2, 2).astype(np.float32), h_inv)[0]
        if abs(p[1, 1] - p[0, 1]) > 6 or max(p[:, 0]) < _col(-0.3) or min(p[:, 0]) > _col(length + 0.3):
            continue
        if not (0 <= p[:, 1].mean() < size[1]):
            continue
        hits.append((float(p[:, 1].mean()), float(abs(p[1, 0] - p[0, 0])), float(p[:, 0].min()), float(p[:, 0].max())))
    hits.sort()
    groups: list[list[tuple]] = []
    for h in hits:
        if groups and h[0] - groups[-1][0][0] <= LINE_MERGE_PX:
            groups[-1].append(h)
        else:
            groups.append([h])
    lines = [{"y": sum(g_[0] * g_[1] for g_ in g) / sum(g_[1] for g_ in g), "px": sum(g_[1] for g_ in g),
              "x0": min(g_[2] for g_ in g), "x1": max(g_[3] for g_ in g)} for g in groups]  # fmt: skip
    lines = [ln for ln in lines if ln["px"] >= 0.12 * length * fr.PX_PER_M]
    lines = sorted(sorted(lines, key=lambda ln: -ln["px"])[:keep], key=lambda ln: ln["y"])
    return [{"z": round(_z_of_row(ln["y"]), 3), "length_m": round(ln["px"] / fr.PX_PER_M, 1),
             "s0": round(_s_of_col(ln["x0"]), 2), "s1": round(_s_of_col(ln["x1"]), 2)} for ln in lines]  # fmt: skip


def gable_verges(flat, length: float) -> tuple[float, float] | None:
    """On a gable wall made flat: the two sloping roof edges over it, as (the height at which they pass the wall's two
    ends, the height where they meet). The roof's edge stands a little in front of the wall, which on the wall's plane
    shifts it by centimetres. The uppermost pair of long straight edges that rise towards the wall's middle at the same
    pitch and meet over it; None when there is no such pair (trees, a hipped end, another roof in front)."""
    import cv2

    edges_img = cv2.Canny(cv2.cvtColor(flat, cv2.COLOR_BGR2GRAY), 50, 150)
    segs = cv2.HoughLinesP(edges_img, 1, np.pi / 360, 50, minLineLength=int(0.8 * fr.PX_PER_M), maxLineGap=12)
    if segs is None:
        return None
    sides: dict[int, list[tuple[float, float, float]]] = {1: [], -1: []}  # rising to the right (left edge), falling: (slope, z at s=0, length)
    for x0, y0, x1, y1 in np.asarray(segs, float).reshape(-1, 4):
        s0, s1, z0, z1 = x0 / fr.PX_PER_M - fr.SIDE_M, x1 / fr.PX_PER_M - fr.SIDE_M, fr.Z_RANGE[1] - y0 / fr.PX_PER_M, fr.Z_RANGE[1] - y1 / fr.PX_PER_M
        if abs(s1 - s0) < 0.3 or max(z0, z1) < 2.5:
            continue
        a = (z1 - z0) / (s1 - s0)
        if not math.tan(math.radians(22)) <= abs(a) <= math.tan(math.radians(62)):
            continue
        mid = (s0 + s1) / 2
        if (a > 0) != (mid < length / 2):  # each edge lies on its own half of the wall
            continue
        sides[1 if a > 0 else -1].append((a, z0 - a * s0, math.hypot(s1 - s0, z1 - z0)))

    def lines(found: list[tuple[float, float, float]], at: float) -> list[tuple[float, float, float]]:
        """Segments on one line are one edge: (slope, z at s=0, the length they cover together)."""
        out: list[tuple[float, float, float]] = []
        for a, b, n in sorted(found, key=lambda t: -t[2]):
            hit = next((k for k, (a2, b2, _) in enumerate(out) if abs(a - a2) <= 0.06 and abs((a * at + b) - (a2 * at + b2)) <= 0.1), None)
            if hit is None:
                out.append((a, b, n))
            else:
                out[hit] = (out[hit][0], out[hit][1], out[hit][2] + n)
        return [t for t in out if t[2] >= 1.5]

    left, right = lines(sides[1], length / 4), lines(sides[-1], 3 * length / 4)
    pairs = []
    for al, bl, nl in left:
        for ar, br, nr in right:
            s_top = (br - bl) / (al - ar)
            z_top = al * s_top + bl
            z_ends = (bl, ar * length + br)
            if abs(s_top - length / 2) <= max(0.4, 0.08 * length) and abs(al + ar) <= 0.12 and abs(z_ends[0] - z_ends[1]) <= 0.4 and min(z_ends) >= 2.2 and z_top - max(z_ends) >= 1.0 and (al - ar) / 2 >= math.tan(math.radians(27)):
                pairs.append((nl + nr, sum(z_ends) / 2, z_top))
    if not pairs:
        return None
    most = max(p[0] for p in pairs)
    _, z_end, z_top = max((p for p in pairs if p[0] >= 0.6 * most), key=lambda p: p[1])  # the uppermost: the tiles' edge, not the board or its shadow under it
    return float(z_end - 0.25), float(z_top - 0.15)  # the wall's top is under the roof's thickness, the ridge just under the tiles' edge


def wall_colour_lab(flat, length: float) -> np.ndarray:
    """The wall's own colour (Lab): the commonest colour of the band where a wall is mostly bare. The commonest, not
    the middle one: windows, a fence and its pillars together can cover more than half of that band."""
    import cv2

    lab = cv2.cvtColor(flat, cv2.COLOR_BGR2LAB).astype(np.float32)
    px = lab[_row(3.2) : _row(1.2), _col(0.3) : _col(length - 0.3)].reshape(-1, 3)
    cells = (px // 8).astype(np.int32)
    keys, counts = np.unique(cells, axis=0, return_counts=True)
    best = keys[counts.argmax()]
    return px[(np.abs(cells - best) <= 1).all(axis=1)].mean(axis=0)


def find_boxes(flat, length: float, tol: int = 8) -> list[dict]:
    """Upright rectangles on a flattened wall whose colour is not the wall's: windows, doors, and whatever else is
    dark and square (a model sorts them). Found row by row: a rectangle is a stack of rows whose stretch of other
    colour starts and stops at the same columns; a row where something in front (a fence pillar) widens the stretch
    still counts, a row where the stretch no longer reaches both sides ends it."""
    import cv2

    lab = cv2.cvtColor(flat, cv2.COLOR_BGR2LAB).astype(np.float32)
    other = (np.linalg.norm(lab - wall_colour_lab(flat, length), axis=2) > 24).astype(np.uint8)
    other = cv2.morphologyEx(other, cv2.MORPH_OPEN, np.ones((7, 7), np.uint8))  # fence bars, wires, downpipes
    other = cv2.morphologyEx(other, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))  # glazing bars, reflections
    x_lo, x_hi = _col(0.05), _col(length - 0.05)
    groups: list[dict] = []
    done: list[dict] = []
    for y in range(_row(fr.Z_RANGE[1] - 0.4), _row(-0.3)):
        line = other[y, x_lo:x_hi]
        edges = np.flatnonzero(np.diff(np.concatenate([[0], line, [0]])))
        runs = [(x_lo + a, x_lo + b - 1) for a, b in zip(edges[::2], edges[1::2]) if b - a >= 30]
        for g in groups:
            g["hit"] = False
        for a, b in runs:
            exact = False
            for g in groups:
                if abs(a - g["l"]) <= tol and abs(b - g["r"]) <= tol:
                    g["rows"].append((y, a, b))
                    g["hit"] = exact = True
                elif a <= g["l"] + tol and b >= g["r"] - tol and (b - a) <= 1.8 * (g["r"] - g["l"]):
                    g["cover"] += 1
                    g["hit"] = True
            if not exact:
                groups.append({"l": a, "r": b, "rows": [(y, a, b)], "cover": 0, "hit": True, "last": y})
        for g in groups:
            if g["hit"]:
                g["last"] = y
        done += [g for g in groups if y - g["last"] > 6]
        groups = [g for g in groups if y - g["last"] <= 6]
    boxes = []
    for g in done + groups:
        top, bottom = g["rows"][0][0], g["last"]
        left, right = float(np.median([a for _, a, _ in g["rows"]])), float(np.median([b for _, _, b in g["rows"]]))
        w, h = (right - left) / fr.PX_PER_M, (bottom - top) / fr.PX_PER_M
        if not (0.35 <= w <= 6.0 and 0.35 <= h <= 3.6) or len(g["rows"]) < 0.5 * (bottom - top):
            continue
        boxes.append({"s0": round(_s_of_col(left), 2), "s1": round(_s_of_col(right), 2), "z0": round(_z_of_row(bottom), 2), "z1": round(_z_of_row(top), 2)})
    boxes.sort(key=lambda r: -(r["s1"] - r["s0"]) * (r["z1"] - r["z0"]))
    kept: list[dict] = []
    for r in boxes:  # a smaller box inside a kept one is the same thing found twice
        if not any(r["s0"] >= k["s0"] - 0.1 and r["s1"] <= k["s1"] + 0.1 and r["z0"] >= k["z0"] - 0.1 and r["z1"] <= k["z1"] + 0.1 for k in kept):
            kept.append(r)
    return sorted(kept, key=lambda r: r["s0"])[:14]


def _merge_runs(items: list[tuple[float, float, float]], gap: float, tol: float) -> list[tuple[float, float, float]]:
    """Segments (level, from, to) on the same level (within ``tol``) and at most ``gap`` apart, joined."""
    out: list[list[float]] = []
    for lvl, a, b in sorted(items):
        for m in out:
            if abs(m[0] - lvl) <= tol and a <= m[2] + gap and b >= m[1] - gap:
                n = (m[2] - m[1]) + (b - a)
                m[0] = (m[0] * (m[2] - m[1]) + lvl * (b - a)) / max(n, 1e-9)
                m[1], m[2] = min(m[1], a), max(m[2], b)
                break
        else:
            out.append([lvl, a, b])
    return [tuple(m) for m in out]


def find_frames(flat, length: float) -> list[dict]:
    """Rectangles on a flattened wall drawn by their own edges: a level edge (the head of an opening) with an upright
    edge going down from each of its ends. This finds windows whose glass mirrors the sky, which no colour gives away.
    The bottom is the lower end of the uprights, or a level edge of the same width just below it (the sill)."""
    import cv2

    segs = fr.find_segments(cv2.cvtColor(flat, cv2.COLOR_BGR2GRAY))
    level, upright = [], []
    for x0, y0, x1, y1 in segs:
        dx, dy = abs(x1 - x0), abs(y1 - y0)
        if dy <= 0.06 * dx:
            level.append(((y0 + y1) / 2, min(x0, x1), max(x0, x1)))
        elif dx <= 0.06 * dy:
            upright.append(((x0 + x1) / 2, min(y0, y1), max(y0, y1)))
    level, upright = _merge_runs(level, 14, 4), _merge_runs(upright, 14, 4)
    near = 12
    out = []
    for y, xa, xb in level:
        w = (xb - xa) / fr.PX_PER_M
        if not (0.4 <= w <= 5.0) or xa < _col(0) - near or xb > _col(length) + near or not (_row(fr.Z_RANGE[1] - 0.4) < y < _row(0.8)):
            continue
        # an upright edge at each end, starting near the head (something leaning on the wall may hide its first part)
        reach = 0.45 * fr.PX_PER_M
        left = [u for u in upright if abs(u[0] - xa) <= near and y - near <= u[1] <= y + reach and u[2] - y >= 0.3 * fr.PX_PER_M]
        right = [u for u in upright if abs(u[0] - xb) <= near and y - near <= u[1] <= y + reach and u[2] - y >= 0.3 * fr.PX_PER_M]
        if not left or not right:
            continue
        lo = max(max(u[2] for u in left), max(u[2] for u in right))
        sills = [h for h in level if lo - near <= h[0] <= lo + 0.6 * fr.PX_PER_M and min(h[2], xb) - max(h[1], xa) >= 0.7 * (xb - xa)]
        if sills:
            lo = min(sills)[0]
        if not (0.4 <= (lo - y) / fr.PX_PER_M <= 3.4):
            continue
        out.append({"s0": round(_s_of_col(xa), 2), "s1": round(_s_of_col(xb), 2), "z0": round(_z_of_row(lo), 2), "z1": round(_z_of_row(y), 2)})
    return out


def _overlap(a: dict, b: dict) -> float:
    """Intersection over union of two rectangles in wall metres."""
    w = min(a["s1"], b["s1"]) - max(a["s0"], b["s0"])
    h = min(a["z1"], b["z1"]) - max(a["z0"], b["z0"])
    if w <= 0 or h <= 0:
        return 0.0
    inter = w * h
    return inter / ((a["s1"] - a["s0"]) * (a["z1"] - a["z0"]) + (b["s1"] - b["s0"]) * (b["z1"] - b["z0"]) - inter)


def find_rectangles(flat, length: float, keep: int = 14) -> list[dict]:
    """Every rectangle worth asking about: those drawn by edges first, then those of another colour that are not the
    same thing again. Of two that are nearly the same the larger stays (the frame, not the glass)."""
    cands = sorted(find_frames(flat, length), key=lambda r: -(r["s1"] - r["s0"]) * (r["z1"] - r["z0"])) + find_boxes(flat, length)
    kept: list[dict] = []
    for r in cands:
        # the same left and right edges, one above the other and touching: one opening found in two parts (its head
        # by its edges, its lower part, behind a fence, by its colour)
        # (the part found by colour is often a little wider: a pillar or a shadow beside it; the edges' width is kept)
        twin = next((k for k in kept if abs(k["s0"] - r["s0"]) <= 0.3 and abs(k["s1"] - r["s1"]) <= 0.3
                     and min(k["z1"], r["z1"]) - max(k["z0"], r["z0"]) > -0.1), None)  # fmt: skip
        if twin is not None:
            twin["z0"], twin["z1"] = min(twin["z0"], r["z0"]), max(twin["z1"], r["z1"])
        elif all(_overlap(r, k) < 0.45 for k in kept):
            kept.append(r)
    return sorted(kept[:keep], key=lambda r: r["s0"])


def wall_picture(flat, length: float, lines: list[dict], boxes: list[dict], cam_z: float):
    """The flattened wall with a metre scale, its level lines lettered at both margins and its rectangles numbered."""
    import cv2

    img = cv2.copyMakeBorder(flat, 0, 0, 60, 60, cv2.BORDER_CONSTANT, value=(30, 30, 30))
    ox = 60
    for i, ln in enumerate(lines):
        col = fr.CAND_COLOURS[i % len(fr.CAND_COLOURS)]
        y = _row(ln["z"])
        cv2.line(img, (ox - 22, y), (ox + 40, y), col, 2)
        cv2.line(img, (img.shape[1] - ox - 40, y), (img.shape[1] - ox + 22, y), col, 2)
        for x in range(ox + 40, img.shape[1] - ox - 40, 30):
            cv2.line(img, (x, y), (x + 5, y), col, 1)
        for x in (6, img.shape[1] - 44):
            cv2.putText(img, ln["letter"], (x + (22 if i % 2 else 0), y + 8), cv2.FONT_HERSHEY_SIMPLEX, 0.7, col, 2, cv2.LINE_AA)
    for r in boxes:
        p0, p1 = (ox + _col(r["s0"]) - 4, _row(r["z1"]) - 4), (ox + _col(r["s1"]) + 4, _row(r["z0"]) + 4)
        cv2.rectangle(img, p0, p1, (255, 255, 255), 3)
        cv2.rectangle(img, p0, p1, (0, 0, 255), 1)
        tag = str(r["number"])
        cv2.rectangle(img, (p0[0], p0[1] - 30), (p0[0] + 16 + 16 * len(tag), p0[1]), (0, 0, 255), -1)
        cv2.putText(img, tag, (p0[0] + 6, p0[1] - 7), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2, cv2.LINE_AA)
    for s in range(int(length) + 1):
        x = ox + _col(s)
        cv2.line(img, (x, img.shape[0] - 14), (x, img.shape[0]), (255, 255, 255), 2)
        cv2.putText(img, f"{s} m", (x + 3, img.shape[0] - 4), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1, cv2.LINE_AA)
    for s in (0.0, length):
        cv2.line(img, (ox + _col(s), 0), (ox + _col(s), 24), (255, 255, 255), 2)
    return img


def eave_picture(flat, length: float, lines: list[dict], stretch: int = 3):
    """The top of the flattened wall stretched ``stretch`` times in height, so the lines at the eave (gutter, soffit,
    wall top: often 10 cm apart) can be told from each other. None when no line is above 2 m."""
    import cv2

    high = [(i, ln) for i, ln in enumerate(lines) if ln["z"] >= 2.0]
    if not high:
        return None
    y0 = max(0, _row(max(ln["z"] for _, ln in high) + 0.35))
    y1 = _row(min(ln["z"] for _, ln in high) - 0.5)
    part = cv2.resize(flat[y0:y1, _col(-0.3) : _col(length + 0.3)], None, fx=1, fy=stretch, interpolation=cv2.INTER_LANCZOS4)
    img = cv2.copyMakeBorder(part, 0, 0, 70, 70, cv2.BORDER_CONSTANT, value=(30, 30, 30))
    for i, ln in high:
        col = fr.CAND_COLOURS[i % len(fr.CAND_COLOURS)]
        y = (_row(ln["z"]) - y0) * stretch
        cv2.line(img, (40, y), (110, y), col, 2)
        cv2.line(img, (img.shape[1] - 110, y), (img.shape[1] - 40, y), col, 2)
        for x in range(110, img.shape[1] - 110, 30):
            cv2.line(img, (x, y), (x + 5, y), col, 1)
        for x in (10, img.shape[1] - 30):
            cv2.putText(img, ln["letter"], (x, y + 9), cv2.FONT_HERSHEY_SIMPLEX, 0.8, col, 2, cv2.LINE_AA)
    return img


def stage_walls(out: Path) -> None:
    import cv2

    from drycastle_pipeline.photos.landmark_tools import WORK
    from drycastle_pipeline.terrain.terrain import load_terrain

    state = _load(out)
    answers = json.loads((out / "ends.json").read_text())
    b = next(x for x in json.loads((WORK / "buildings.json").read_text())["buildings"] if x["id"] == state["building"])
    ring = b["footprint"]["exterior"]
    terrain, elevations = load_terrain(WORK, mmap=True)
    base = float(elevations.get(state["building"], 0.0))
    problems: list[str] = []
    ask: list[str] = []
    for cap in state["captures"]:
        n, cam = cap["n"], cap["camera"]
        photo = cv2.imread(cap["photo"])
        segs = np.load(out / f"segs_c{n}.npy")
        columns: dict[int, list[float]] = {}
        for w in cap["walls"]:
            for end in ("left", "right"):
                x = w.get(end + "_marked_x")
                if x is None:
                    x = _pick_x(w[end + "_candidates"], answers.get(f"c{n}_e{w['edge']}_{end}", "none"))
                w[end + "_x"] = x
                if x is not None:
                    columns.setdefault(w[end], []).append(x)
        picks = [(tuple(ring[k]), float(np.mean(v))) for k, v in columns.items()]
        if len(picks) >= 2:
            moved = move_camera(cam, picks)
            step = math.dist((moved["x"], moved["y"]), (cam["x"], cam["y"]))
            if step < 6.0:
                cam = moved
            else:
                problems.append(f"capture {n}: the chosen wall ends would move the camera {step:.1f} m: not believed, the capture's own place is kept")
        else:
            problems.append(f"capture {n}: fewer than two wall ends chosen: the capture's own place is kept, sizes may be 10% off")
        ground = float(terrain.surface(np.array([cam["x"]]), np.array([cam["y"]]))[0])
        cam = {**cam, "z": ground + 2.5 - base}
        cap["moved_m"] = round(math.dist((cam["x"], cam["y"]), (cap["camera"]["x"], cap["camera"]["y"])), 2)
        cap["camera_final"] = cam
        (out / f"cam_{n}.json").write_text(json.dumps(cam))
        for w in cap["walls"]:
            a, c = ring[w["left"]], ring[w["right"]]
            h, size, length = fr.wall_homography(cam, a, c)
            flat = cv2.warpPerspective(photo, h, size, flags=cv2.INTER_LANCZOS4 | cv2.WARP_INVERSE_MAP)
            cv2.imwrite(str(out / f"flat_c{n}_e{w['edge']}.png"), flat)
            v = np.array([c[0] - a[0], c[1] - a[1], 0.0])
            kinds = fr.classify(cam, segs, {"up": np.array([0, 0, 1.0]), "wall": v / np.linalg.norm(v)})
            lines = find_lines(cam, segs, kinds, np.linalg.inv(h), size, length)
            boxes = find_rectangles(flat, length)
            for ln, letter in zip(lines, _letters(len(lines))):
                ln["letter"] = letter
            for k, r in enumerate(boxes):
                r["number"] = k + 1
            w["lines"], w["boxes"], w["homography"] = lines, boxes, h.tolist()
            key = f"c{n}_e{w['edge']}"
            cv2.imwrite(str(out / f"wall_{key}.png"), wall_picture(flat, length, lines, boxes, cam["z"]))
            top = eave_picture(flat, length, lines)
            if top is not None:
                cv2.imwrite(str(out / f"eave_{key}.png"), top)
            ask.append(key)
            print(f"capture {n} edge {w['edge']}: camera moved {cap['moved_m']} m, {cam['z']:.2f} m above the building's base; {len(lines)} level lines, {len(boxes)} rectangles")
    state["problems"] = problems
    (out / "state.json").write_text(json.dumps(state, indent=1))
    template = {"what": "?", "roof": "?", "roof_colour": "?", "wall_colour": "?", "frame_colour": "?", "surround_colour": "?", "gable_colour": "?", "cornice": "?", "corner_strips": "?", "shutter_boxes": "?", "canopy": "?", "balcony": "?", "chimney": "?", "not_modelled": "?",
                "walls": {k: {"wall_top": "?", "roof_edge": "?", "roof_slope": "?", "plinth_top": "?", "boxes": {str(r["number"]): "?" for r in next(w for c in state["captures"] for w in c["walls"] if f"c{c['n']}_e{w['edge']}" == k)["boxes"]},
                              "dormers_missed": "?", "missed": "?"} for k in ask}}  # fmt: skip
    photos = "\n".join(f"- `{c['photo']}`" for c in state["captures"])
    (out / "walls_ask.md").write_text(WALLS_ASK.format(pictures="\n".join(f"- `wall_{k}.png`" + (f" and `eave_{k}.png`" if (out / f"eave_{k}.png").exists() else "") for k in ask), photos=photos, template=json.dumps(template, indent=1)))
    for p in problems:
        print(f"PROBLEM: {p}")
    print(f"\nnext: read {out}/walls_ask.md, look at the pictures it lists, write {out}/names.json")


WALLS_ASK = """# Answer 2: what is what on each wall

Each `wall_...png` is one wall of the house made flat, as if photographed straight-on, to scale (the ruler at the
bottom is metres along the wall; the two white ticks at the top are the wall's ends). Things in front of the wall (a
fence, a car, plants) are smeared over it; the roof above it is stretched and means nothing here.

On it:
- LEVEL LINES found by the computer, each with a letter at both margins (solid near the margins, dashed across).
- RECTANGLES found by the computer, each in a red frame with a number.

`eave_...png` is the top part of the same wall, stretched three times in height, with the same letters: use it to
tell the lines at the eave apart.

Letters and numbers are new every time `walls` runs: answers written for an earlier run are void.

For each wall answer:
- `wall_top`: the letter of the line where the wall's surface ends at the top. At the eave there is usually a group
  of lines close together. From the top down: the gutter's upper edge, the gutter's lower edge and, when the roof
  sticks out over the wall, the line where the underside of the roof (the soffit, often painted like the wall) meets
  the wall. The wall's top is the LOWEST line of that group: under it is plain wall down to the windows. A line
  through the heads of the windows is not it. Between two lines: both letters, e.g. `"C-D"`. No line near it: `"none"`.
- `roof_edge`: what the roof does above this wall. `"eave"`: the roof slopes down to this wall and its lower edge (with
  the gutter) runs level along the wall's top. `"gable"`: the wall goes up into a triangle between two slopes, the
  roof's edges run up along it. Cannot tell, or a flat roof: `"none"`.
- `roof_slope`: only above an `"eave"` wall, from the PHOTO (not the flattened wall): the outline of the roof's slope
  that comes down to this wall. `"whole"`: its top edge (the ridge) is level and about as long as the wall, the slope
  is a rectangle or a parallelogram (at the roof's ends is a gable). `"part"`: its top edge is level but clearly
  shorter than the wall, the slope is a trapezoid narrowing upwards (the roof's ends slope too: hips). `"point"`: the
  slope is a triangle. Not an eave wall, or the roof's top is out of the picture: `"none"`.
- `plinth_top`: if the foot of the wall is a band of another colour or material (a plinth, usually 20 to 60 cm
  high), the letter of the line at its upper edge. No plinth, or its edge has no line, or it is hidden: `"none"`.
- `boxes`: for every number one of `"window"`, `"door"`, `"glass_door"`, `"garage_door"`, `"dormer"`, `"no"`. `"door"`: the
  house's entrance door (a wall has at most one). `"glass_door"`: a glazed door or window that reaches down to the
  floor (a terrace or balcony door, a French window). `"dormer"`: the
  rectangle frames the window of a dormer (a small roofed box with a window standing on the roof's slope above this
  wall); only above an `"eave"` wall. A rectangle is a window or door only
  if its frame is the opening itself: `"no"` for a fence pillar, a shadow, a sign, a shutter box, a plant, half of a
  window, or a frame that takes in more than the opening. One opening found as two rectangles: name the better one,
  `"no"` for the other. A window whose lower part is hidden by a fence is still a `"window"`.
- `dormers_missed`: for every dormer above this wall whose window has NO rectangle you named `"dormer"`: the place of
  the middle of its window read off the ruler at the bottom, in metres, e.g. `[5.9]`. None: `[]`.
- `missed`: every window or door of this wall that has NO rectangle you named for it (hidden behind a tree, a pole, a
  car, or simply not found): `{{"kind": "window" | "door" | "garage_door" | "glass_door", "at": the middle of it read off
  the ruler in metres, "floor": "ground" | "upper"}}`, e.g. `[{{"kind": "window", "at": 5.5, "floor": "ground"}}]`.
  None: `[]`. A window you can only guess is there because the row of windows has a gap where one belongs counts too.

Then look at the photos themselves for the whole house:
{photos}
- `roof`: `"gabled"` (two slopes, a triangle of wall at two ends), `"hipped"` (slopes on all sides, no triangle of
  wall) or `"flat"`. Anything else (one slope, several roofs at different heights): the nearest of the three, and say
  so in `not_modelled`.
- `roof_colour`: the roof covering's own colour, one of `"anthracite"` (black, dark grey), `"grey"`, `"brown"`,
  `"red"`, `"orange"` (terracotta), `"green"`. Judge the material, not the glare of the sky on it.
- `wall_colour`: what the walls are painted or built with, as it would look in plain daylight, one of `"white"`,
  `"cream"`, `"yellow"`, `"peach"` (light orange, apricot), `"orange"`, `"pink"`, `"green"`, `"blue"`, `"grey"`,
  `"beige"`, `"brown"`, `"brick"`, `"timber"`, `"stone"`. A wall in shade looks greyer and darker than it is: judge
  it from its sunlit part if the photos show one (often a side wall), and name the paint, not the shade.
- `frame_colour`: the window frames, one of `"white"`, `"brown"`, `"dark"` (anthracite, black), `"wood"` (light
  brown), `"grey"`.
- `surround_colour`: the flat painted band round the windows, where it is another colour than the wall: one of
  `"white"`, `"cream"`, `"grey"`, `"green"`, `"brown"`, `"dark"`. No such band: `"none"`.
- `gable_colour`: the wall of the gable (the triangle under a gabled roof), where it is another colour or material
  than the walls below: one of the `wall_colour` names. The same as the walls, or no gable to be seen: `"same"`.
- `cornice`: `"yes"` if a band of another colour or standing proud runs along the wall's top under the roof, else `"no"`.
- `corner_strips`: `"yes"` if the house's corners carry an upright strip of another colour (pilasters, quoins), else `"no"`.
- `shutter_boxes`: `"yes"` if the windows have a box over their head (roller shutters), else `"no"`.
- `canopy`: `"yes"` if a small roof of its own hangs over the entrance door, else `"no"`.
- `balcony`: `"yes"` if a balcony with a railing hangs on the wall that faces the camera, above the ground floor, else `"no"`.
- `chimney`: where a chimney stands on this house's roof as the photo shows it: `"left"`, `"middle"`, `"right"`. No
  chimney to be seen: `"none"`.
- `what`: the house in a few words (e.g. `"bungalow, cream render, dark tiled roof"`).
- `not_modelled`: what belongs to the house but is none of the above (chimney, porch, balcony, attached garage...).
  Not what merely stands in front of it (a fence, a gate, a car).

Pictures:
{pictures}

Write `names.json` in this folder:

{template}
"""


# ---------------------------------------------------------------- stage 3: apply


def toned(lab, target_l: float) -> np.ndarray:
    """A sampled colour (Lab) brought to the lightness its kind of surface has in daylight; the hue is kept and its
    strength grows with the lightness, as it does when a shaded surface comes into the sun (at most 1.6 times)."""
    lab = np.asarray(lab, float)
    k = min(2.0, max(0.6, target_l / max(lab[0], 1.0)))
    # a surface that has to be brightened much was in shade, where the light is the blue sky's: a little yellow back
    warm = 6.0 * min(1.0, max(0.0, (k - 1.25) / 0.5))
    return np.array([target_l, 128 + (lab[1] - 128) * k, 128 + (lab[2] - 128) * k + warm])


def shade(named_hex: str, sampled_lab) -> str:
    """The named colour, pulled a little toward the hue the photo shows, so two houses named the same colour are not
    the same colour. The lightness stays the named one (a photo's is the weather's), the hue moves at most 4 units."""
    import cv2

    if sampled_lab is None:
        return named_hex
    rgb = [int(named_hex[i : i + 2], 16) for i in (1, 3, 5)]
    lab = cv2.cvtColor(np.uint8([[rgb[::-1]]]), cv2.COLOR_BGR2LAB)[0, 0].astype(float)
    seen = toned(sampled_lab, lab[0])
    a, b = lab[1] + float(np.clip(seen[1] - lab[1], -4, 4)), lab[2] + float(np.clip(seen[2] - lab[2], -4, 4))
    # a photo is greyer than the paint: the pull may turn the hue, not wash the colour out
    was, now = math.hypot(lab[1] - 128, lab[2] - 128), math.hypot(a - 128, b - 128)
    if 1e-6 < now < was:
        a, b = 128 + (a - 128) * was / now, 128 + (b - 128) * was / now
    return _lab_hex([lab[0], a, b])


def varied(colour_hex: str, key: str, light: float = 3.0, hue: float = 2.0) -> str:
    """The colour a little lighter or darker and a touch off in hue, the same on every run for the key: two houses of
    the same paint or the same tiles are not one flat colour side by side."""
    import cv2

    from drycastle_pipeline.buildings import house_rules as hr

    rgb = [int(colour_hex[i : i + 2], 16) for i in (1, 3, 5)]
    lab = cv2.cvtColor(np.uint8([[rgb[::-1]]]), cv2.COLOR_BGR2LAB)[0, 0].astype(float)
    t = [hr.chance(key, w) * 2 - 1 for w in ("light", "a", "b")]
    return _lab_hex([lab[0] + t[0] * light * 2.55, lab[1] + t[1] * hue, lab[2] + t[2] * hue])


def darker(colour_hex: str, by: float, grey: float = 0.5) -> str:
    """The colour at the share by (0..1) of its lightness and with the share grey of its hue left: a plinth."""
    import cv2

    rgb = [int(colour_hex[i : i + 2], 16) for i in (1, 3, 5)]
    lab = cv2.cvtColor(np.uint8([[rgb[::-1]]]), cv2.COLOR_BGR2LAB)[0, 0].astype(float)
    return _lab_hex([lab[0] * by, 128 + (lab[1] - 128) * grey, 128 + (lab[2] - 128) * grey])


def muted(lab) -> np.ndarray:
    """A dark sampled colour (a plinth, a downpipe) with half its hue: what little hue a dark, shaded sample has is
    mostly the sky's blue."""
    lab = np.asarray(lab, float)
    return np.array([lab[0], 128 + (lab[1] - 128) * 0.5, 128 + (lab[2] - 128) * 0.5])


def _lab_hex(lab) -> str:
    import cv2

    return _hex(cv2.cvtColor(np.uint8([[np.clip(lab, 0, 255)]]), cv2.COLOR_LAB2BGR)[0, 0])


def _wall_distance(cam: dict, a, c) -> float:
    a, c = np.asarray(a, float), np.asarray(c, float)
    u = (c - a) / np.linalg.norm(c - a)
    rel = np.array([cam["x"], cam["y"]]) - a
    return float(abs(rel[0] * u[1] - rel[1] * u[0]))


def roof_edge_line(lines: list[dict], named_top: float, length: float) -> dict | None:
    """The line of the roof's outer edge (the gutter) on a flattened wall: the longest line at or above the one named
    as the wall's top that runs along most of the wall."""
    over = [ln for ln in lines if named_top - 0.01 <= ln["z"] <= named_top + 1.2 and ln["length_m"] >= 0.5 * length]
    return max(over, key=lambda ln: ln["length_m"]) if over else None


def overhang_from_corner(cam: dict, homography, edge_line: dict, corner, n_wall, n_next, end_s: float, wall_len: float, a, c) -> float | None:
    """How far the roof sticks out, from where its edge ends: the roof's corner lies the overhang out from the house's
    corner along both walls, so the further the roof's edge runs past the wall's end, the larger the overhang. The
    overhang is the one that puts the roof's corner where that line ends. None when no overhang fits."""
    import cv2

    d = _wall_distance(cam, a, c)
    inv = np.linalg.inv(np.asarray(homography, float))
    best = None
    for out in np.arange(0.1, 2.01, 0.02):
        z = cam["z"] + (edge_line["z"] - cam["z"]) * (d - out) / d  # the edge's real height: it is `out` nearer than the wall
        at = np.asarray(corner, float) + (np.asarray(n_wall) + np.asarray(n_next)) * out
        px = project(cam, np.array([[at[0], at[1], z]]))
        if not np.isfinite(px).all():
            continue
        flat = cv2.perspectiveTransform(px.reshape(1, 1, 2).astype(np.float32), inv)[0, 0]
        err = abs(_s_of_col(float(flat[0])) - end_s)
        if best is None or err < best[0]:
            best = (err, float(out))
    return round(best[1], 2) if best is not None and best[0] < 0.1 else None


def window_posts(flat, r: dict) -> list[float]:
    """Where a window's upright posts are, as fractions of its width: columns of its picture where the brightness
    changes sharply over most of its height. Two changes close together are the two sides of one post."""
    import cv2

    x0, x1, y0, y1 = _col(r["s0"]), _col(r["s1"]), _row(r["z1"]), _row(r["z0"])
    h, w = y1 - y0, x1 - x0
    if w < 60 or h < 40:
        return []
    grey = cv2.cvtColor(flat[y0 + h // 6 : y0 + int(h * 0.6), x0:x1], cv2.COLOR_BGR2GRAY).astype(np.float32)  # the upper part: a fence hides the lower
    step = np.abs(np.diff(cv2.GaussianBlur(grey, (5, 5), 0), axis=1))
    strong = (step > 10).mean(axis=0)  # the share of rows with an edge in this column
    cols = [i for i in range(12, w - 12) if strong[i] > 0.6 and strong[i] == strong[max(0, i - 3) : i + 4].max()]
    posts: list[list[int]] = []
    for i in cols:
        if posts and i - posts[-1][-1] <= 14:
            posts[-1].append(i)
        else:
            posts.append([i])
    out = [round(float(np.mean(g)) / w, 2) for g in posts]
    return [f for f in out if 0.12 <= f <= 0.88][:3]


def grow_to_frame(flat, r: dict, wall_lab, reach: float = 0.14) -> dict:
    """A window's rectangle grown out to the outer edge of its frame. The rectangle found is often the glass: the frame
    round it (white on a grey wall, say) is neither glass nor wall. Each side moves out, at most ``reach`` metres,
    while the strip just outside it is mostly not the wall's colour."""
    import cv2

    lab = cv2.cvtColor(flat, cv2.COLOR_BGR2LAB).astype(np.float32)
    other = np.linalg.norm(lab - np.asarray(wall_lab, np.float32), axis=2) > 20
    x0, x1, y0, y1 = _col(r["s0"]), _col(r["s1"]), _row(r["z1"]), _row(r["z0"])
    h, w = other.shape
    most = int(reach * PX_PER_M)
    for _ in range(most):  # left
        if x0 <= 1 or other[y0:y1, x0 - 1].mean() < 0.6:
            break
        x0 -= 1
    for _ in range(most):  # right
        if x1 >= w - 2 or other[y0:y1, x1 + 1].mean() < 0.6:
            break
        x1 += 1
    for _ in range(most):  # top
        if y0 <= 1 or other[y0 - 1, x0:x1].mean() < 0.6:
            break
        y0 -= 1
    for _ in range(most):  # bottom
        if y1 >= h - 2 or other[y1 + 1, x0:x1].mean() < 0.6:
            break
        y1 += 1
    return {**r, "s0": round(_s_of_col(x0), 2), "s1": round(_s_of_col(x1), 2), "z0": round(_z_of_row(y1), 2), "z1": round(_z_of_row(y0), 2)}


def find_twins(flat, windows: list[dict], length: float, score: float = 0.5) -> list[dict]:
    """Windows the rectangle finder missed that look like one it found: the found window's picture is slid along the
    wall at its own height, and where it matches well, clear of every window already known, there is another."""
    import cv2

    grey = cv2.cvtColor(flat, cv2.COLOR_BGR2GRAY)
    known = [dict(w) for w in windows]
    out = []
    for w in sorted(windows, key=lambda r: -(r["s1"] - r["s0"]) * (r["z1"] - r["z0"])):
        pad = 8
        x0, x1, y0, y1 = _col(w["s0"]) - pad, _col(w["s1"]) + pad, _row(w["z1"]) - pad, _row(w["z0"]) + pad
        if x0 < 0 or y0 < 0 or x1 - x0 < 30 or y1 - y0 < 30:
            continue
        patch = grey[y0:y1, x0:x1]
        band0, band1 = max(0, y0 - 15), min(grey.shape[0], y1 + 15)
        left, right = max(0, _col(0.1)), min(grey.shape[1], _col(length - 0.1))
        strip = grey[band0:band1, left:right]
        if strip.shape[1] <= patch.shape[1] or strip.shape[0] < patch.shape[0]:
            continue
        match = cv2.matchTemplate(strip, patch, cv2.TM_CCOEFF_NORMED)
        while True:
            _, best, _, (mx, my) = cv2.minMaxLoc(match)
            if best < score:
                break
            s0 = _s_of_col(left + mx + pad)
            cand = {"s0": round(s0, 2), "s1": round(s0 + (w["s1"] - w["s0"]), 2), "z1": round(_z_of_row(band0 + my + pad), 2), "twin_of": w.get("number"), "score": round(float(best), 2)}
            cand["z0"] = round(cand["z1"] - (w["z1"] - w["z0"]), 2)
            if not any(k["s0"] < cand["s1"] + 0.25 and cand["s0"] < k["s1"] + 0.25 for k in known):
                known.append(cand)
                out.append(cand)
            match[:, max(0, mx - patch.shape[1]) : mx + patch.shape[1]] = -1
    return out


def window_rails(flat, r: dict) -> list[float]:
    """Where a window's level bars are, as fractions of its height from the sill: rows of its picture where the
    brightness changes sharply over most of its width (a fanlight's bar). The two sides of one bar are one bar."""
    import cv2

    x0, x1, y0, y1 = _col(r["s0"]), _col(r["s1"]), _row(r["z1"]), _row(r["z0"])
    h, w = y1 - y0, x1 - x0
    if w < 40 or h < 60:
        return []
    grey = cv2.cvtColor(flat[y0:y1, x0 + w // 6 : x1 - w // 6], cv2.COLOR_BGR2GRAY).astype(np.float32)
    step = np.abs(np.diff(cv2.GaussianBlur(grey, (5, 5), 0), axis=0))
    strong = (step > 10).mean(axis=1)
    rows = [i for i in range(12, h - 12) if strong[i] > 0.6 and strong[i] == strong[max(0, i - 3) : i + 4].max()]
    bars: list[list[int]] = []
    for i in rows:
        if bars and i - bars[-1][-1] <= 14:
            bars[-1].append(i)
        else:
            bars.append([i])
    out = [round(1 - float(np.mean(g)) / h, 2) for g in bars]
    return sorted(f for f in out if 0.15 <= f <= 0.85)[:2]


def find_downpipes(flat, length: float, wall_top: float, wall_lab) -> list[dict]:
    """Downpipes on a flattened wall: narrow upright strips that are not the wall's colour from under the eave down
    past the windows' heads. [{s, lab}]: metres along the wall and the strip's colour."""
    import cv2

    lab = cv2.cvtColor(flat, cv2.COLOR_BGR2LAB).astype(np.float32)
    other = np.linalg.norm(lab - wall_lab, axis=2) > 24
    x0, x1 = _col(-0.15), _col(length + 0.15)
    high = other[_row(wall_top - 0.1) : _row(wall_top - 0.7), x0:x1].mean(axis=0)
    whole = other[_row(wall_top - 0.1) : _row(1.0), x0:x1].mean(axis=0)
    hit = (high > 0.8) & (whole > 0.55)
    edges = np.flatnonzero(np.diff(np.concatenate([[0], hit.astype(np.int8), [0]])))
    out = []
    for i, j in zip(edges[::2], edges[1::2]):
        if 4 <= j - i <= 22:  # 4 to 22 cm wide
            strip = lab[_row(wall_top - 0.1) : _row(wall_top - 0.7), x0 + i : x0 + j].reshape(-1, 3)
            out.append({"s": round(_s_of_col(x0 + (i + j) / 2), 2), "lab": np.median(strip, axis=0).tolist()})
    return out


def guessed_openings(b: dict, others: list[dict], seen: set[int], like: list[dict]) -> list[dict]:
    """Windows for the outer walls no capture saw, so they are not blank: as large and as high as the measured ones
    (their middle size), one for about every 5 m of wall, evenly spread. Marked confidence low."""
    from drycastle_pipeline.photos.landmark_tools import _edges, _seg_dist

    wins = [o for o in like if o["kind"] == "window"]
    if not wins:
        return []
    w, h, sill = (float(np.median([o[k] for o in wins])) for k in ("width_m", "height_m", "sill_m"))
    posts = next((list(o["posts"]) for o in wins if o.get("posts")), [])
    out = []
    for e in _edges(b):
        mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
        shared = any(_seg_dist(mid, tuple(o["footprint"]["exterior"][j]), tuple(o["footprint"]["exterior"][(j + 1) % len(o["footprint"]["exterior"])])) < 0.1
                     for o in others for j in range(len(o["footprint"]["exterior"])))  # fmt: skip
        if e["i"] in seen or shared or e["length"] < w + 1.6:
            continue
        n = max(1, min(round(e["length"] / 5.0), int((e["length"] - 0.8) // (w + 0.8))))
        gap = (e["length"] - n * w) / (n + 1)
        for k in range(n):
            out.append({"kind": "window", "edge": e["i"], "at_m": round(gap + k * (w + gap), 2), "width_m": round(w, 2), "height_m": round(h, 2),
                        "sill_m": round(sill, 2), "recess_m": 0.12, "confidence": "low", "frame_m": FRAME_M, **({"posts": posts} if posts else {}), "sill": True, "_off": 90})  # fmt: skip
    return out


def main_part(ring, edges: list[dict], i: int) -> dict | None:
    """The part of the footprint a gabled roof with its eave along wall ``i`` covers: the rectangle behind that wall,
    as deep as the longer of the two walls that leave its ends about square to it. {ring, middle, half (its depth)}.
    None when the wall has no such neighbour, or the rectangle takes in nearly the whole footprint anyway."""
    from shapely.geometry import Polygon

    n = len(ring)
    by_i = {e["i"]: e for e in edges}
    e = by_i[i]
    u = np.array([e["q"][0] - e["p"][0], e["q"][1] - e["p"][1]]) / e["length"]
    from shapely.geometry import LineString

    # as deep as the footprint stays about as wide as the wall behind it: a roof with its eave on this wall spans the
    # house to where it steps in to a narrower wing (or ends)
    inward = -np.asarray(e["normal"], float)
    p, q = np.asarray(e["p"], float) - u * 0.05, np.asarray(e["q"], float) + u * 0.05
    poly = Polygon(ring).buffer(0)
    depth, t = 0.0, 0.25
    while t < 40.0:
        across = poly.intersection(LineString([p + inward * t, q + inward * t])).length
        if across < 0.8 * e["length"]:
            break
        depth, t = t, t + 0.25
    if depth < 2.0:
        return None
    rect = [p, q, q + inward * depth, p + inward * depth]
    inside = poly.intersection(Polygon(rect))
    if inside.area > 0.85 * poly.area:
        return None
    # the roof's part is the one piece of the footprint behind the wall, not the rectangle: where the footprint steps
    # in, the rectangle would cut thin strips off the rest
    piece = max(getattr(inside, "geoms", [inside]), key=lambda g: g.area)
    if piece.geom_type != "Polygon" or piece.area < 0.5 * Polygon(rect).area:
        return None
    mid = (p + q) / 2 + inward * depth / 2
    return {"ring": [[round(float(x), 2), round(float(y), 2)] for x, y in list(piece.exterior.coords)[:-1]], "middle": (float(mid[0]), float(mid[1])), "half": depth / 2}


def dormer_from_box(sd: dict, eave: float, slope: float) -> dict | None:
    """A dormer from the rectangle round its window on the flattened wall. The dormer stands back from the wall, on the
    roof, so on the wall's plane it shows too low and too near the camera's own place along the wall: its foot lies
    where the line of sight to the rectangle's lower edge meets the roof's slope, and its size grows with that
    distance. None when the sight line does not meet the roof."""
    r, d, cz, cs = sd["box"], sd["d"], sd["cam_z"], sd["cam_s"]
    if r is None:  # no rectangle, only its place along the wall: as far up the roof and as large as the one that has one
        like = sd.get("like")
        t = like["_t"] if like else 1.2
        k = (d + t) / d
        mid = cs + (sd["s"] - cs) * k
        win_w, win_h = like["window_m"] if like else (0.9, 1.0)
        return {"edge": sd["edge"], "at_m": round(mid if sd["forward"] else sd["length"] - mid, 2), "bottom_m": round(eave + slope * t - 0.1, 2),
                "width_m": round(win_w + 0.4, 2), "height_m": round(win_h + 0.3, 2), "rise_m": round(max(0.35, (win_w + 0.4) * 0.3), 2),
                "window_m": [round(win_w, 2), round(win_h, 2)], "_t": t}  # fmt: skip
    climb = (r["z0"] - cz) / d  # how fast the sight line to the window's foot rises per metre of depth
    if r["z0"] <= eave or slope - climb < 0.05:
        return None
    t = (r["z0"] - eave) / (slope - climb)  # metres behind the wall
    k = (d + t) / d
    if not 0.05 <= t <= 6.0:
        return None
    s0, s1 = cs + (r["s0"] - cs) * k, cs + (r["s1"] - cs) * k
    mid = (s0 + s1) / 2
    win_w, win_h = (s1 - s0), (r["z1"] - r["z0"]) * k
    return {"edge": sd["edge"], "at_m": round(mid if sd["forward"] else sd["length"] - mid, 2), "bottom_m": round(eave + slope * t - 0.1, 2),
            "width_m": round(win_w + 0.4, 2), "height_m": round(win_h + 0.3, 2), "rise_m": round(max(0.35, (win_w + 0.4) * 0.3), 2),
            "window_m": [round(win_w, 2), round(win_h, 2)], "_t": t}  # fmt: skip


def hand_marks(photo: str) -> dict:
    """What was marked by hand on a capture (``capture_guide``), from the file next to its picture; {} when nothing."""
    side = Path(photo).with_suffix(".json")
    return (json.loads(side.read_text()).get("marks") or {}) if side.exists() else {}


def wall_top_from_mark(cam: dict, line, a, c) -> float | None:
    """The height of a wall's top from the line marked along it in the picture: where the sight lines to the line's two
    ends meet the wall's upright plane (through footprint corners a and c), their mean."""
    from drycastle_pipeline.photos.landmark_photo import ray

    a, c = np.asarray(a, float), np.asarray(c, float)
    u = (c - a) / np.linalg.norm(c - a)
    normal = np.array([u[1], -u[0]])
    eye = np.array([cam["x"], cam["y"]])
    zs = []
    for x, y in line:
        d = ray(cam, float(x), float(y))
        along = float(d[:2] @ normal)
        if abs(along) < 1e-6:
            return None
        t = float((a - eye) @ normal) / along
        if t <= 0:
            return None
        zs.append(cam["z"] + t * d[2])
    return float(np.mean(zs))


def long_axis(ring) -> float:
    """The direction (radians, 0..pi) of the long side of the footprint's tightest rectangle: where the generator runs a ridge."""
    from shapely.geometry import Polygon

    c = list(Polygon(ring).minimum_rotated_rectangle.exterior.coords)
    p, q = max(((c[0], c[1]), (c[1], c[2])), key=lambda s: math.dist(*s))
    return math.atan2(q[1] - p[1], q[0] - p[0]) % math.pi


def ridge_from_mark(cam: dict, px, middle, direction: float) -> float | None:
    """The ridge's height from a point marked on it: the ridge runs level through ``middle`` along ``direction``, so the
    sight line to the mark is as high as the ridge where it passes over that line on the map. None when the camera
    looks along the ridge (the two lines then never cross cleanly)."""
    from drycastle_pipeline.photos.landmark_photo import ray

    d = ray(cam, float(px[0]), float(px[1]))
    u = np.array([math.cos(direction), math.sin(direction)])
    flat = d[:2] / max(np.linalg.norm(d[:2]), 1e-9)
    cross = flat[0] * u[1] - flat[1] * u[0]
    if abs(cross) < 0.2:
        return None
    rel = np.asarray(middle, float) - [cam["x"], cam["y"]]
    t = (rel[0] * u[1] - rel[1] * u[0]) / (d[0] * u[1] - d[1] * u[0])
    return float(cam["z"] + t * d[2]) if t > 0 else None


def sky_edge(photo) -> np.ndarray:
    """For every picture column, the row where the sky ends coming down from the top (the first stretch of 12 rows
    that are not sky). -1 where there is none."""
    import cv2

    hsv = cv2.cvtColor(photo, cv2.COLOR_BGR2HSV)
    hue, sat, val = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    sky = (val > 135) & ((sat < 70) | ((hue > 85) & (hue < 135)))
    solid = cv2.erode((~sky).astype(np.uint8), np.ones((12, 1), np.uint8), anchor=(0, 0))  # 1 where 12 rows down from here are all not sky
    first = solid.argmax(axis=0)
    return np.where(solid.max(axis=0) > 0, first, -1)


def ridge_height(photo, cam: dict, at: tuple[float, float], eave: float) -> float | None:
    """How high the roof's top is over the plan point ``at`` (the middle of the ridge): the height at which that point
    falls on the roof's outline against the sky."""
    edge = sky_edge(photo)
    best = None
    for h in np.arange(eave + 0.2, eave + 8.0, 0.02):
        x, y = project(cam, np.array([[at[0], at[1], h]]))[0]
        if not np.isfinite(x) or not (10 <= x < photo.shape[1] - 10):
            return best
        rows = edge[int(x) - 8 : int(x) + 9]
        rows = rows[rows >= 0]
        if not len(rows):
            return best
        if y >= np.median(rows) - 1:  # still on the roof (below its outline)
            best = float(h)
        else:
            break
    return best


def roof_middle(ring) -> tuple[tuple[float, float], float]:
    """The middle of the footprint's tightest rectangle, and half its short side."""
    from shapely.geometry import Polygon

    rect = Polygon(ring).minimum_rotated_rectangle
    c = list(rect.exterior.coords)
    return (rect.centroid.x, rect.centroid.y), min(math.dist(c[0], c[1]), math.dist(c[1], c[2])) / 2


def _patch(img, x: float, y: float, r: int = 6):
    h, w = img.shape[:2]
    x, y = round(x), round(y)
    if not (r <= x < w - r and r <= y < h - r):
        return None
    return np.median(img[y - r : y + r + 1, x - r : x + r + 1].reshape(-1, 3), axis=0)


def band_colour_lab(flat, length: float, z0: float, z1: float) -> np.ndarray | None:
    """The commonest colour (Lab) of a level band of the flattened wall, between heights z0 and z1."""
    import cv2

    lab = cv2.cvtColor(flat, cv2.COLOR_BGR2LAB).astype(np.float32)
    px = lab[_row(z1) : _row(z0), _col(0.3) : _col(length - 0.3)].reshape(-1, 3)
    if len(px) < 200:
        return None
    cells = (px // 8).astype(np.int32)
    keys, counts = np.unique(cells, axis=0, return_counts=True)
    return px[(np.abs(cells - keys[counts.argmax()]) <= 1).all(axis=1)].mean(axis=0)


def _letters_z(lines: list[dict], answer) -> float | None:
    by = {ln["letter"]: ln["z"] for ln in lines}
    parts = [p.strip().upper() for p in str(answer or "none").replace("/", "-").split("-")]
    return float(np.mean([by[p] for p in parts])) if parts and all(p in by for p in parts) else None


def render_views(mesh, b: dict, cams: dict[int, dict], photos: dict[int, str], out: Path) -> list[Path]:
    """Pictures of the model alone: beside each photo from that photo's camera (``compare_<n>.png``), and from four
    corners above (``render_<k>.png``). So a run can be judged without the game."""
    from PIL import Image

    from drycastle_pipeline.photos.landmark_tools import _raster

    from drycastle_pipeline.buildings.openings import GLASS

    tris, cols = mesh.tris, mesh.colours.astype(float)
    cols[(cols == GLASS).all(axis=1)] = RENDER_GLASS
    lo, hi = tris.reshape(-1, 3).min(0), tris.reshape(-1, 3).max(0)
    c, top, span = (lo + hi) / 2, hi[2], float(np.linalg.norm(hi[:2] - lo[:2]))
    written = []
    for n, cam in cams.items():
        yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
        eye = np.array([cam["x"], cam["y"], cam["z"]])
        target = eye + [math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)]
        photo = Image.open(photos[n]).convert("RGB")
        model = _raster(tris, cols, eye, target, math.degrees(2 * math.atan(cam["w"] / 2 / cam["f"])), photo.size)
        both = Image.new("RGB", (photo.width * 2 + 8, photo.height), (30, 30, 30))
        both.paste(photo, (0, 0))
        both.paste(model, (photo.width + 8, 0))
        both.save(out / f"compare_{n}.png")
        written.append(out / f"compare_{n}.png")
    for k, ang in enumerate((35.0, 125.0, 215.0, 305.0)):
        d = (math.cos(math.radians(ang)), math.sin(math.radians(ang)))
        eye = (c[0] + d[0] * span * 1.15, c[1] + d[1] * span * 1.15, 2.2 + span * 0.12)  # a little above a standing person: the walls, not only the roof
        _raster(tris, cols, eye, (c[0], c[1], top * 0.42), 50).save(out / f"render_{k}.png")
        written.append(out / f"render_{k}.png")
    return written


def stage_apply(out: Path, dry: bool, replace: bool, guess: bool = True) -> None:
    import cv2

    from drycastle_pipeline.buildings import house_rules as hr

    from drycastle_pipeline.buildings.openings import (
        DETAILS_DIR,
        DetailError,
        build_detail,
        parse_detail,
    )
    from drycastle_pipeline.core.config import PIPELINE_DIR
    from drycastle_pipeline.photos.landmark_photo import _derived, _upsert_override, overlay_mesh
    from drycastle_pipeline.photos.landmark_tools import WORK
    from drycastle_pipeline.core.mesh import Mesh

    state = _load(out)
    names = json.loads((out / "names.json").read_text())
    # what was marked by hand at capture (capture_guide) stands above what the model answered
    by_cap = {cap["n"]: hand_marks(cap["photo"]) for cap in state["captures"]}
    for cap in state["captures"]:  # the newest capture's word on the whole house
        names.update({k: v for k, v in (by_cap[cap["n"]].get("house") or {}).items() if v})
    bid = state["building"]
    all_b = json.loads((WORK / "buildings.json").read_text())["buildings"]
    b0 = next(x for x in all_b if x["id"] == bid)
    ring = b0["footprint"]["exterior"]
    problems: list[str] = list(state.get("problems", []))
    roof = names.get("roof")
    if roof not in ROOFS:
        problems.append(f"roof '{roof}' is not one of {ROOFS}: the generated '{b0.get('roof')}' is kept")
        roof = b0.get("roof") if b0.get("roof") in ROOFS else "gabled"
    wall_hex = WALL_COLOURS.get(str(names.get("wall_colour")).lower())
    tone = WALL_TONES.get(str(names.get("wall_tone")).lower(), WALL_TONES["light"])  # answers written before wall_colour
    if wall_hex is None and "wall_tone" not in names:
        problems.append(f"wall_colour '{names.get('wall_colour')}' is not one of {tuple(WALL_COLOURS)}: taken from the photo, which is greyer than the wall")

    from drycastle_pipeline.photos.landmark_tools import _edges, _seg_dist

    edges = _edges(b0)
    shared = {e["i"] for e in edges if any(
        _seg_dist(((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2), tuple(o["footprint"]["exterior"][j]), tuple(o["footprint"]["exterior"][(j + 1) % len(o["footprint"]["exterior"])])) < 0.1
        for o in all_b if o["id"] != bid for j in range(len(o["footprint"]["exterior"])))}  # fmt: skip
    eaves, openings, wall_labs, plinths, overhangs, pipes, fascias, boards = [], [], [], [], [], [], [], []
    invented: list[str] = []
    roof_edges: list[tuple[float, str, int]] = []
    gable_eaves: list[tuple[float, float, float, str]] = []  # (how much of itself the wall shows, eave, apex, wall) per gable wall
    fronts: list[tuple[float, int, int, int]] = []
    seen_dormers: list[dict] = []
    slopes: list[tuple[float, str, int]] = []
    for cap in state["captures"]:
        n, cam = cap["n"], cap["camera_final"]
        for w in cap["walls"]:
            key = f"c{n}_e{w['edge']}"
            said = names.get("walls", {}).get(key)
            hand = (by_cap[n].get("walls") or {}).get(str(w["edge"])) or {}
            if said is None:
                problems.append(f"{key}: no answer for this wall, nothing taken from it")
                continue
            if hand.get("roof_edge"):
                said = {**said, "roof_edge": hand["roof_edge"]}
            flat = cv2.imread(str(out / f"flat_{key}.png"))
            length = math.dist(ring[w["left"]], ring[w["right"]])
            forward = w["left"] == w["edge"]  # the wall's left end in the picture is the edge's first corner

            def along(s0: float, s1: float, forward=forward, length=length) -> float:
                return s0 if forward else length - s1

            wall_lab = wall_colour_lab(flat, length)
            wall_labs.append(wall_lab)
            fronts.append((-w["length"] * math.cos(math.radians(w["off_square_deg"])), int(w["edge"]), w["left"], w["right"]))  # the wall that shows most of itself leads: long and square-on
            show = -w["length"] * math.cos(math.radians(w["off_square_deg"]))
            if str(said.get("roof_edge")).lower() == "eave" and str(said.get("roof_slope")).lower() in ("whole", "part", "point"):
                slopes.append((show, str(said["roof_slope"]).lower(), int(w["edge"])))
            lo_c, hi_c = np.asarray(ring[w["left"]], float), np.asarray(ring[w["right"]], float)
            for s_mid in said.get("dormers_missed") or []:
                if isinstance(s_mid, (int, float)) and 0.3 < s_mid < length - 0.3:
                    seen_dormers.append({"edge": int(w["edge"]), "forward": forward, "length": length, "box": None, "s": float(s_mid), "cam_z": cam["z"], "_off": w["off_square_deg"],
                                         "cam_s": float((np.array([cam["x"], cam["y"]]) - lo_c) @ (hi_c - lo_c) / length), "d": _wall_distance(cam, lo_c, hi_c)})  # fmt: skip
            if str(said.get("roof_edge")).lower() in ROOF_EDGES:
                roof_edges.append((-w["length"] * math.cos(math.radians(w["off_square_deg"])), str(said["roof_edge"]).lower(), int(w["edge"])))
            hand_top = wall_top_from_mark(cam, hand["top"], ring[w["left"]], ring[w["right"]]) if hand.get("top") else None
            top = hand_top if hand_top is not None else _letters_z(w["lines"], said.get("wall_top"))
            if top is None:
                problems.append(f"{key}: no line named as the wall's top")
            else:
                gutter = roof_edge_line(w["lines"], top, length)
                reach = None
                if gutter is not None and gutter["z"] - top > 0.1:  # the board under the gutter: painted like the wall, or dark like the gutter
                    band = band_colour_lab(flat, length, top + 0.03, gutter["z"] - 0.03)
                    if band is not None:
                        boards.append(bool(band[0] >= wall_lab[0] - 12))
                if gutter is not None and roof != "flat":
                    by_i = {e["i"]: e for e in edges}
                    for end, end_s, past in (("left", gutter["s0"], -gutter["s0"]), ("right", gutter["s1"], gutter["s1"] - length)):
                        k = w[end]
                        nxt = by_i[k] if k != w["edge"] else by_i[(k - 1) % len(ring)]  # the other wall at that corner
                        if past < 0.15 or nxt["length"] < 1.0 or nxt["i"] in shared:
                            continue
                        got = overhang_from_corner(cam, w["homography"], gutter, ring[k], by_i[w["edge"]]["normal"], nxt["normal"], end_s, length, ring[w["left"]], ring[w["right"]])
                        if got is not None:
                            reach = got
                if reach is not None:
                    overhangs.append(reach)
                    # Under an overhang two lines can be the lowest of the eave's group: where the roof's underside meets
                    # the wall, or (when that corner lies in shadow and shows no line) the underside's OUTER edge. The
                    # outer edge is nearer the camera, so on the wall's plane it lands higher than it is. If the named
                    # line were the wall's top, the outer edge would show as a line a known distance above it: no line
                    # there means the named line IS the outer edge, and the wall's top is lower by that much.
                    d = _wall_distance(cam, ring[w["left"]], ring[w["right"]])
                    outer = cam["z"] + (top - cam["z"]) * d / (d - reach)
                    named = min(w["lines"], key=lambda ln: abs(ln["z"] - top))
                    runs_past = max(-named["s0"], named["s1"] - length) > 0.3  # a line of the wall stops where the wall does
                    if hand_top is None and top > cam["z"] + 0.3 and (runs_past or not any(abs(ln["z"] - outer) <= 0.06 for ln in w["lines"])):
                        top = cam["z"] + (top - cam["z"]) * (d - reach) / d
                        fascia_top = cam["z"] + (gutter["z"] - cam["z"]) * (d - reach) / d
                        if 0.1 <= fascia_top - top <= 0.5:
                            fascias.append(fascia_top - top)
                eaves.append((-w["length"] * math.cos(math.radians(w["off_square_deg"])), top, key))
                for dp in find_downpipes(flat, length, top, wall_lab):
                    pipes.append({"edge": int(w["edge"]), "at_m": round(min(max(along(dp["s"], dp["s"]) - 0.045, 0.0), length - 0.1), 2), "_lab": dp["lab"]})  # at_m is the pipe's left side
            foot = _letters_z(w["lines"], said.get("plinth_top"))
            if foot is not None and 0.05 <= foot <= 1.5:
                plinths.append((foot, band_colour_lab(flat, length, 0.03, foot - 0.03), wall_lab))
            box_said = {str(r["number"]): str(said.get("boxes", {}).get(str(r["number"]), "no")).lower() for r in w["boxes"]}
            # a rectangle named "no" that is a named window's like (its size, its foot and head on the same lines) and
            # covers no named opening is that window again: the answer passed it over (it was drawn twice, or half hidden)
            named_any = [r for r in w["boxes"] if box_said[str(r["number"])] in (*OPENING_KINDS, "dormer")]
            likes = [r for r in w["boxes"] if box_said[str(r["number"])] == "no"
                     and any(box_said[str(g["number"])] == "window" and abs((g["s1"] - g["s0"]) - (r["s1"] - r["s0"])) <= 0.15 and abs(g["z0"] - r["z0"]) <= 0.15 and abs(g["z1"] - r["z1"]) <= 0.15 for g in w["boxes"])
                     and not any(min(g["s1"], r["s1"]) - max(g["s0"], r["s0"]) > 0.1 and min(g["z1"], r["z1"]) - max(g["z0"], r["z0"]) > 0.1 for g in named_any)]  # fmt: skip
            for r in likes if guess else ():
                if not any(box_said[str(g["number"])] == "window" and g is not r and min(g["s1"], r["s1"]) - max(g["s0"], r["s0"]) > 0.1 and min(g["z1"], r["z1"]) - max(g["z0"], r["z0"]) > 0.1 for g in w["boxes"]):
                    box_said[str(r["number"])] = "window"
                    invented.append(f"rectangle {r['number']} on edge {w['edge']} taken as a window though named none: it is the like of a named window of that wall")
            said = {**said, "boxes": box_said}
            if str(said.get("roof_edge")).lower() == "gable" and roof == "gabled":
                verge = gable_verges(flat, length)
                if verge is not None:
                    gable_eaves.append((show, verge[0], verge[1], key))
            named_w = [grow_to_frame(flat, r, wall_lab) for r in w["boxes"] if str(said.get("boxes", {}).get(str(r["number"]), "no")).lower() == "window"]
            twins = find_twins(flat, [r for r in named_w if r["z0"] < 2.2], length) if guess else []
            if twins:
                invented.append(f"{len(twins)} window(s) on edge {w['edge']} found by their likeness to a named one (no rectangle had been drawn for them)")
            for r in [*w["boxes"], *({**t, "number": f"twin{k}"} for k, t in enumerate(twins))]:
                kind = "window" if "twin_of" in r else str(said.get("boxes", {}).get(str(r["number"]), "no")).lower()
                if kind == "window" and "twin_of" not in r:
                    r = next(g for g in named_w if g["number"] == r["number"])
                if kind == "dormer":
                    lo, hi = np.asarray(ring[w["left"]], float), np.asarray(ring[w["right"]], float)
                    seen_dormers.append({"edge": int(w["edge"]), "forward": forward, "length": length, "box": r, "cam_z": cam["z"], "_off": w["off_square_deg"],
                                         "cam_s": float((np.array([cam["x"], cam["y"]]) - lo) @ (hi - lo) / length), "d": _wall_distance(cam, lo, hi)})  # fmt: skip
                    continue
                if kind not in OPENING_KINDS:
                    continue
                sill = 0.0 if kind != "window" else max(0.0, r["z0"])
                if kind == "door" and r["z1"] > 2.3:  # a door is not higher than that: the rectangle took in what is above it
                    r = {**r, "z1": 2.1}
                if kind == "door" and r["s1"] - r["s0"] > DOOR_MAX_W_M:  # nor wider: the rectangle took in what is beside it
                    mid_s = (r["s0"] + r["s1"]) / 2
                    r = {**r, "s0": mid_s - 0.5, "s1": mid_s + 0.5}
                posts = window_posts(flat, r) if kind in ("window", "glass_door") else []
                rails = window_rails(flat, r) if kind in ("window", "glass_door") else []
                if not forward:
                    posts = sorted(round(1 - f, 2) for f in posts)
                openings.append({"kind": kind, "edge": int(w["edge"]), "at_m": round(along(r["s0"], r["s1"]), 2), "width_m": round(r["s1"] - r["s0"], 2),
                                 "height_m": round(r["z1"] - sill, 2), "sill_m": round(sill, 2), "recess_m": 0.12,
                                 "confidence": "medium" if w["left_x"] is not None and w["right_x"] is not None else "low",
                                 "frame_m": FRAME_M, **({"posts": posts} if posts else {}), **({"rails": rails} if rails else {}), **({"sill": True} if kind == "window" else {}),
                                 "_off": w["off_square_deg"]})  # fmt: skip
            if said.get("missed"):
                placed = []
                for m in hr.missed_items(said["missed"]) if guess else ():
                    edge_i = int(w["edge"])
                    upper = m["floor"] == "upper"
                    mine = [o for o in openings if o["edge"] == edge_i and o["kind"] == "window"]
                    ref = [o for o in mine if (o["sill_m"] >= 2.3) == upper] or ([] if upper else mine)
                    if m["kind"] == "window":
                        if ref:
                            ww, wh, sill = (float(np.median([o[k] for o in ref])) for k in ("width_m", "height_m", "sill_m"))
                        elif upper:
                            continue
                        else:
                            ww, wh, sill = 1.2, 1.3, 0.95
                        if m["small"]:
                            sill, ww, wh = sill + wh - hr.SMALL_WINDOW_M, hr.SMALL_WINDOW_M, hr.SMALL_WINDOW_M
                    elif m["kind"] == "garage_door":
                        ww, wh, sill = max(m["s1"] - m["s0"], hr.GARAGE_W_M), hr.GARAGE_H_M, 0.0
                    else:
                        ww, wh, sill = hr.DOOR_W_M, hr.DOOR_H_M, 0.0
                    mid = (m["s0"] + m["s1"]) / 2
                    o = {"kind": m["kind"], "edge": edge_i, "at_m": round(along(mid - ww / 2, mid + ww / 2), 2), "width_m": round(ww, 2), "height_m": round(wh, 2),
                         "sill_m": round(sill, 2), "recess_m": 0.12, "confidence": "low", "frame_m": FRAME_M, **({"sill": True} if m["kind"] == "window" else {}),
                         **({"posts": list(ref[0]["posts"])} if m["kind"] == "window" and ref and ref[0].get("posts") and not m["small"] else {}), "_off": w["off_square_deg"]}  # fmt: skip
                    if 0.1 <= o["at_m"] and o["at_m"] + ww <= length - 0.1 and hr._clear(o, [k for k in openings if k["edge"] == edge_i], length, 0.1):
                        openings.append(o)
                        placed.append(f"{m['kind']} at {mid:g} m")
                if placed:
                    invented.append(f"{key}: openings the answer saw with no rectangle put where it said: {', '.join(placed)}")
                else:
                    problems.append(f"{key}: openings with no rectangle, left out: {said['missed']}")
    # an opening seen in two captures: the one seen most square-on
    openings.sort(key=lambda o: o["_off"])
    kept: list[dict] = []
    for o in openings:
        # (the same place on the wall: side by side AND one above the other they overlap; a window over another is not it)
        if not any(k["edge"] == o["edge"] and k["at_m"] < o["at_m"] + o["width_m"] and o["at_m"] < k["at_m"] + k["width_m"]
                   and k["sill_m"] < o["sill_m"] + o["height_m"] and o["sill_m"] < k["sill_m"] + k["height_m"] for k in kept):  # fmt: skip
            kept.append(o)
    # a window whose posts are hidden (a ladder, a bush) has them where a window of its size on the same house does
    for o in kept:
        if o["kind"] == "window" and not o.get("posts"):
            twin = next((k for k in kept if k.get("posts") and abs(k["width_m"] - o["width_m"]) <= 0.15 and abs(k["height_m"] - o["height_m"]) <= 0.15), None)
            if twin is not None:
                o["posts"] = list(twin["posts"])
    # windows in one row of one wall that are nearly alike are alike: the same size, sill and bars (the differences are
    # the measuring's, a plant in front of one or its frame read a little short)
    for o in kept:
        if o["kind"] != "window" or o.get("_row"):
            continue
        row = [k for k in kept if k["kind"] == "window" and k["edge"] == o["edge"] and abs(k["width_m"] - o["width_m"]) <= 0.15
               and abs(k["height_m"] - o["height_m"]) <= 0.25 and abs(k["sill_m"] + k["height_m"] - o["sill_m"] - o["height_m"]) <= 0.2]  # fmt: skip
        if len(row) > 1:
            w_, h_, top_ = (float(np.median(v)) for v in ([k["width_m"] for k in row], [k["height_m"] for k in row], [k["sill_m"] + k["height_m"] for k in row]))
            bars = {key: next((list(k[key]) for k in row if k.get(key)), None) for key in ("posts", "rails")}
            for k in row:
                mid = k["at_m"] + k["width_m"] / 2
                k.update({"at_m": round(mid - w_ / 2, 2), "width_m": round(w_, 2), "height_m": round(h_, 2), "sill_m": round(top_ - h_, 2), "_row": True})
                for key, v in bars.items():
                    if v:
                        k[key] = list(v)
    # two windows of a row that touch (under 0.2 m of wall between them, or none) are the two sashes of one window,
    # each boxed on its own: one window, with a post where they meet
    kept.sort(key=lambda o: (o["edge"], o["at_m"]))
    joined: list[dict] = []
    for o in kept:
        k = joined[-1] if joined else None
        # (one of the two may be a sash read short, its foot or head hidden: they stand at one height when the shorter
        # one's span lies mostly inside the taller one's)
        both = (min(k["sill_m"] + k["height_m"], o["sill_m"] + o["height_m"]) - max(k["sill_m"], o["sill_m"])) if k is not None else 0.0
        if (k is not None and k["kind"] == o["kind"] == "window" and k["edge"] == o["edge"] and o["at_m"] - (k["at_m"] + k["width_m"]) < 0.3
                and both >= 0.7 * min(k["height_m"], o["height_m"])):  # fmt: skip
            end = max(k["at_m"] + k["width_m"], o["at_m"] + o["width_m"])
            seam = ((k["at_m"] + k["width_m"] + o["at_m"]) / 2 - k["at_m"]) / (end - k["at_m"])
            old = [f * k["width_m"] / (end - k["at_m"]) for f in k.get("posts", [])]
            foot, head = min(k["sill_m"], o["sill_m"]), max(k["sill_m"] + k["height_m"], o["sill_m"] + o["height_m"])
            k.update({"width_m": round(end - k["at_m"], 2), "posts": sorted(round(f, 2) for f in [*old, seam] if 0.1 < f < 0.9), "_off": min(k["_off"], o["_off"]),
                      "sill_m": round(foot, 2), "height_m": round(head - foot, 2), "_pair": True})  # fmt: skip
            if o["confidence"] != "low":
                k["confidence"] = o["confidence"]
            continue
        joined.append(o)
    paired = hr.pair_sashes(joined, {e["i"]: e["length"] for e in edges})
    if paired != joined:
        invented.append("a window of which one sash was boxed made a whole two-sash window, like the one beside it")
    kept = hr.even_row(paired)
    if kept != paired:
        invented.append("one kind of window per wall: sashes joined, the windows of a wall brought to its one size")
    seen = sorted({w["edge"] for cap in state["captures"] for w in cap["walls"]})
    for o in kept:
        del o["_off"]
        o.pop("_row", None)
        o.pop("_pair", None)

    # the outline of the slope over the wall that shows most of itself says more than a name for the whole roof: a
    # gabled and a hipped roof both come down to the street wall, but one as a rectangle and the other as a trapezoid
    hip_along = None
    if roof != "flat" and slopes:
        _, shape_seen, i_s = min(slopes)
        was = roof
        roof = {"whole": "gabled", "part": "hipped"}.get(shape_seen, roof)
        if roof != was:
            invented.append(f"the roof taken as {roof}, not {was} as named: the slope over edge {i_s} is {'as long as the wall at its top' if roof == 'gabled' else 'shorter at its top than the wall'}")
        if roof == "hipped" and shape_seen == "part":
            hip_along = i_s
            # a hipped roof's ridge is shorter than the house by the house's depth: on a house about as deep as this
            # wall is long it all but comes to a point. A level top edge was seen, so the slopes at the ends are not
            # full hips (a gable seen from the side, or a half-hip): a gabled roof along this wall
            from shapely.geometry import Polygon as _HipPoly

            from drycastle_pipeline.buildings import roof_fit as _hip_fit

            e_s = next(x for x in edges if x["i"] == i_s)
            from shapely.geometry.polygon import orient as _hip_orient

            sq = _hip_fit.squared(_hip_fit.smooth(_hip_orient(_HipPoly(ring), 1.0)))  # the walls made square, as the roof's own search does
            stand = _hip_fit._on_wall(sq, *_hip_fit._same_wall(sq, e_s["p"], e_s["q"]))
            if stand is not None and e_s["length"] - stand.d < 0.4 * e_s["length"]:
                roof, hip_along = "gabled", None
                if not any(k == "eave" and i == i_s for _, k, i in roof_edges):
                    roof_edges.append((min(slopes)[0], "eave", i_s))
                invented.append(f"the roof taken as gabled along edge {i_s}, not hipped as named: the house is {stand.d:.1f} m deep behind a {e_s['length']:.1f} m wall, where a hipped roof has all but no level top edge, and one was seen")
    lead = None
    if roof == "gabled" and roof_edges:
        # a gabled roof's eaves run along its ridge: a "wall top" read on a wall that runs across the ridge (beside the
        # gable wall: a garage's front, a porch) is a door's head or a lower roof's edge, not the house's eave
        _, kind0, i0 = min(roof_edges)
        e0 = next(x for x in edges if x["i"] == i0)
        from drycastle_pipeline.buildings.roof_fit import gable_believable

        across0 = [p[0] * e0["normal"][0] + p[1] * e0["normal"][1] for p in ring]
        if kind0 == "gable" and not gable_believable(e0["length"], max(across0) - min(across0)):
            problems.append(f"edge {i0} was named a gable wall, but it is {e0['length']:.1f} m long on a house {max(across0) - min(across0):.1f} m deep: taken as an eave wall")
            roof_edges = [(s, "eave" if i == i0 else k, i) for s, k, i in roof_edges]
            kind0 = "eave"
        way0 = math.atan2(e0["q"][1] - e0["p"][1], e0["q"][0] - e0["p"][0]) + (0.0 if kind0 == "eave" else math.pi / 2)
        if kind0 == "gable":
            lead = [[round(float(v), 2) for v in e0["p"]], [round(float(v), 2) for v in e0["q"]]]

        def across(key: str) -> bool:
            e = next(x for x in edges if x["i"] == int(key.rsplit("_e", 1)[1]))
            return abs(math.cos(math.atan2(e["q"][1] - e["p"][1], e["q"][0] - e["p"][0]) - way0)) < 0.5

        for t in [t for t in eaves if across(t[2])]:
            eaves.remove(t)
            problems.append(f"{t[2]}: the line named as the wall's top ({t[1]:.2f} m) is not the house's eave (this wall runs across the ridge): not used")
    if eaves:
        eave = min(eaves)[1]  # the wall seen most square-on
        vals = [e for _, e, _ in eaves]
        if max(vals) - min(vals) > 0.3:
            problems.append("the walls disagree on the wall's top: " + ", ".join(f"{k} {e:.2f} m" for _, e, k in eaves) + f"; used {eave:.2f} m (the wall that shows most of itself)")
    else:
        heads = [o["sill_m"] + o["height_m"] for o in kept if o["sill_m"] < 1.2]
        # (the generated height is kept beside the run: a second apply reads a building this run has already changed)
        gen_file = out / "generated_eave.json"
        if not heads and not gen_file.exists():
            gen_file.write_text(json.dumps(float(b0.get("eave_height_m") or 3.0)))
        if gable_eaves:  # no eave wall was seen, a gable wall was: its two sloping roof edges come down to the eave at the wall's ends
            _, eave, _, gkey = min(gable_eaves)
            eave = round(eave, 2)
            invented.append(f"the eave's height ({eave:.2f} m) read off the sloping roof edges of the gable wall {gkey}, where they pass its ends")
        else:
            eave = max(2.8, max(heads) + 0.45) if heads else float(json.loads(gen_file.read_text()))
            problems.append(f"NO WALL TOP MEASURED: the eave is put at {eave:.2f} m" + (" (over the heads of the ground floor's openings)" if heads else " (as generated)"))

    # which way the ridge runs, from the wall seen most square-on that was named an eave or a gable wall. Without it the
    # generator runs the ridge along the footprint's long side, which a rear wing can turn the wrong way.
    ridge_dir, part = None, None
    if roof == "gabled" and roof_edges:
        _, what_edge, i = min(roof_edges)
        e = next(x for x in edges if x["i"] == i)
        along_wall = math.atan2(e["q"][1] - e["p"][1], e["q"][0] - e["p"][0])
        ridge_dir = round((along_wall if what_edge == "eave" else along_wall + math.pi / 2) % math.pi, 4)
        if what_edge == "eave" and not guess:
            part = main_part(ring, edges, i)
    elif roof == "hipped" and hip_along is not None:  # a hipped roof's ridge runs along the wall whose slope is a trapezoid
        e = next(x for x in edges if x["i"] == hip_along)
        ridge_dir = round(math.atan2(e["q"][1] - e["p"][1], e["q"][0] - e["p"][0]) % math.pi, 4)
    top = eave
    fitted = None
    if roof != "flat":
        middle, half = roof_middle(ring)
        if part is not None:
            middle, half = part["middle"], part["half"]
        from shapely.geometry import Polygon as _Plain

        # all but a rectangle (a shallow jog, a porch's step): one plain roof over it, as such a house has, not a part per jog
        plain = _Plain(ring).area >= 0.9 * _Plain(ring).minimum_rotated_rectangle.area
        if guess and not plain:  # the roof is fitted to the whole footprint: its main part gives the ridge's place and the half-width
            from shapely.geometry import Polygon as _FitPoly
            from shapely.geometry.polygon import orient as _fit_orient

            from drycastle_pipeline.buildings import roof_fit

            fit_poly = _fit_orient(_FitPoly(ring), 1.0)
            fitted = roof_fit.fit(fit_poly, roof, ridge_dir, 1.0, eave, lead=lead)
            if fitted is not None:
                middle, half = fitted.middle, fitted.half
        found, marked_ridges = [], []
        for cap in state["captures"]:
            mark = by_cap[cap["n"]].get("ridge")
            h = ridge_from_mark(cap["camera_final"], mark, middle, ridge_dir if ridge_dir is not None else long_axis(ring)) if mark else None
            if h is not None:
                marked_ridges.append(h)
            else:
                # a gable wall facing the camera: the roof's top shows where its front end is, over that wall (a little
                # in front of it: the verge), not over the middle of the house
                at = middle
                if lead is not None:
                    e0n = next(x for x in edges if x["p"][0] == e0["p"][0] and x["p"][1] == e0["p"][1])["normal"]
                    at = ((lead[0][0] + lead[1][0]) / 2 + e0n[0] * 0.4, (lead[0][1] + lead[1][1]) / 2 + e0n[1] * 0.4)
                h = ridge_height(cv2.imread(cap["photo"]), cap["camera_final"], at, eave)
            if h is not None:
                found.append(h)
        if marked_ridges:  # a ridge marked by hand is believed over one read off the sky's outline
            found = marked_ridges
        elif not found and gable_eaves and not eaves:  # the sky's outline gave none: where the gable's two roof edges meet
            found = [min(gable_eaves)[2]]
        if found and not eaves and lead is not None:
            # no eave was seen, the gable wall was: its upper windows stand under the roof, so the roof's slopes pass
            # over their corners and meet the wall's ends no lower than that
            ridge_z, mid_s = float(np.median(found)), e0["length"] / 2
            ups = [o for o in kept if o["edge"] == e0["i"] and o["sill_m"] >= 2.0]
            if ups:
                fall = min((ridge_z - (o["sill_m"] + o["height_m"]) - 0.3) / max(abs(s - mid_s), 0.5) for o in ups for s in (o["at_m"], o["at_m"] + o["width_m"]))
                need = min(ridge_z - fall * half, ridge_z - math.tan(math.radians(25)) * half)
                if need > eave:
                    problems.append(f"the eave is raised from {eave:.2f} m to {need:.2f} m: the roof must pass over the upper window(s) of the gable wall")
                    eave = round(need, 2)
        rise = (float(np.median(found)) - eave) if found else None
        steepest = math.tan(math.radians(60 if roof == "gabled" else 52)) * half
        if rise is not None and steepest < rise <= 1.5 * steepest:
            # a little too steep to be true (the outline took in a dormer's top or a chimney), yet it does say the roof
            # is steep: the steepest roof that is believed, not the 30 degrees of a roof nothing is known about
            problems.append(f"the roof's outline gives a rise of {rise:.2f} m over a half-width of {half:.1f} m, steeper than a roof is: built as steep as is believed ({steepest:.2f} m rise)")
            rise = steepest
        if rise is None or not (math.tan(math.radians(25)) * half <= rise <= math.tan(math.radians(60 if roof == "gabled" else 52)) * half):
            guess_rise = math.tan(math.radians(30)) * half
            problems.append(("NO RIDGE MEASURED" if rise is None else f"the roof's outline gives a rise of {rise:.2f} m over a half-width of {half:.1f} m, not a believable roof")
                            + f": built with a 30 degree roof ({guess_rise:.2f} m rise). Check the roof")  # fmt: skip
            rise = guess_rise
        elif len(found) > 1 and max(found) - min(found) > 0.5:
            problems.append("the captures disagree on the ridge: " + ", ".join(f"{h:.2f} m" for h in found))
        top = eave + rise

    reach = round(float(np.median(overhangs)), 2) if overhangs else None
    fascia = round(float(np.median(fascias)), 2) if fascias else 0.18
    shell_eave = eave
    from drycastle_pipeline.buildings import house_rules as hr

    pitch = math.degrees(math.atan2(top - eave, half)) if roof != "flat" else 0.0
    style = hr.eave_style(roof, pitch, reach) if guess else {"overhang_m": reach if reach is not None else 0.4, "level_soffit": roof == "hipped" and reach is not None, "rafters": False}
    boxed, out_m = style["level_soffit"], style["overhang_m"]  # the overhang the roof is built with
    if roof != "flat":
        # (also for a gabled roof's eave: the gutter hangs at the wall's top, not below it over the windows' heads)
        # The generated roof is one plane from the ridge to its rim, and the wall ends where that plane crosses it. A
        # boxed eave is level under the rim, so the wall we measured ends at the rim's height less the fascia: the
        # plane then crosses the wall line higher, by the overhang times the roof's slope. That crossing is the
        # shell's eave; the level soffit hides the wall above the measured top.
        shell_eave = (eave + fascia + out_m * top / half) / (1 + out_m / half)
    ov = {"roof": roof, "height_m": round(top, 2), "levels": 1 if eave < 4.2 else 2}
    if roof != "flat":
        ov["roof_height_m"] = round(top - shell_eave, 2)
    if ridge_dir is not None:
        ov["ridge_dir"] = ridge_dir
    rest_low = eave
    if fitted is not None:
        ov["roof_fit"] = True
        if lead is not None:
            ov["roof_lead"] = lead
        fitted = roof_fit.fit(fit_poly, roof, ridge_dir, top - shell_eave, shell_eave, lead=lead)
        if len(fitted.parts) > 1:
            invented.append(f"the roof over the whole footprint: {len(fitted.parts)} parts, each with its own slopes, meeting in valleys")
    if part is not None:
        ov["roof_part"] = part["ring"]
        ov["rest_height_m"] = round(eave - (0.15 if guess else 0.0), 2)
        if guess:  # a wing behind the house has a single slope falling away from it, far more often than a flat roof
            from drycastle_pipeline.buildings.meshes import split_parts

            ov["rest_roof"] = "auto"
            rest_low = min((float(p["eave_height_m"]) for p in split_parts(_derived(bid, ov))[1:]), default=eave)
            invented.append("the roofs of the lower parts of the footprint (a gabled wing at a gable wall, a single slope behind an eave)")
        problems.append(f"the roof is put over the front {2 * part['half']:.1f} m of the house only (behind the wall with the eave); the rest of the footprint is a lower block {ov['rest_height_m']} m high: its real roof was not seen")
    wall_lab = np.median(np.array(wall_labs, float), axis=0) if wall_labs else None
    front_lab = wall_labs[min(range(len(fronts)), key=lambda k: fronts[k])] if fronts else None  # the wall seen most square-on
    if wall_hex is not None:
        ov["wall_colour"] = varied(shade(wall_hex, front_lab), bid + ":wall", 1.5, 0.0)
    elif wall_lab is not None:
        ov["wall_colour"] = _lab_hex(toned(wall_lab, tone))
    roof_hex = ROOF_COLOURS.get(str(names.get("roof_colour")).lower())
    if roof_hex is None:
        problems.append(f"roof_colour '{names.get('roof_colour')}' is not one of {tuple(ROOF_COLOURS)}: the generated colour is kept")
    else:
        ov["roof_colour"] = varied(roof_hex, bid + ":roof")
    frame_hex = FRAME_COLOURS.get(str(names.get("frame_colour")).lower())
    if frame_hex is None:
        problems.append(f"frame_colour '{names.get('frame_colour')}' is not one of {tuple(FRAME_COLOURS)}: white is used")
        frame_hex = FRAME_COLOURS["white"]

    # what no capture saw is made up by rule from what was measured (house_rules), and said so
    others = [o for o in all_b if o["id"] != bid]
    ridge_way = fitted.angle if fitted is not None else ridge_dir if ridge_dir is not None else long_axis(ring)
    front = min(fronts) if fronts else None
    if guess and front is not None:
        roles = hr.wall_roles(ring, edges, others, roof, ridge_way if roof == "gabled" else None, part["ring"] if part is not None else None)
        win = hr.house_window(kept, eave)
        snapped = [hr.to_house_window(o, win) for o in kept]
        if snapped != kept:
            invented.append(f"the measured windows brought to the house's one size ({win['width_m']} x {win['height_m']} m, sill {win['sill_m']} m)")
        kept = snapped
        if fitted is not None:  # eave or gable as the fitted roof really stands over each wall
            for e in edges:
                (px, py), (qx, qy) = e["p"], e["q"]
                ends_z = min(fitted.top(px, py), fitted.top(qx, qy))
                roles[e["i"]]["roof_edge"] = "gable" if fitted.top((px + qx) / 2, (py + qy) / 2) - ends_z > 0.5 else "eave"
                roles[e["i"]]["peak_m"] = fitted.top((px + qx) / 2, (py + qy) / 2)
        made = hr.invent_openings(roles, set(seen), kept, eave, top, rest_low, roles[front[1]]["normal"], bid)
        if fitted is not None:  # a window made up for a gable that turns out lower than the house's is left out

            def room(o: dict) -> bool:
                e = next(x for x in edges if x["i"] == o["edge"])
                ux, uy = (e["q"][0] - e["p"][0]) / e["length"], (e["q"][1] - e["p"][1]) / e["length"]
                return o["sill_m"] + o["height_m"] <= min(fitted.top(e["p"][0] + ux * s, e["p"][1] + uy * s) for s in (o["at_m"], o["at_m"] + o["width_m"])) - 0.25

            made = [o for o in made if room(o)]
        for o in made:
            o.update({"recess_m": 0.12, "confidence": "low", "frame_m": FRAME_M, **({"sill": True} if o["kind"] == "window" else {})})
        kept += made
        if made:
            invented.append(f"{sum(o['kind'] == 'window' for o in made)} window(s) and {sum(o['kind'] == 'door' for o in made)} door(s) on walls no capture saw (edges {sorted({o['edge'] for o in made})})")
    # one door for people per wall: the first kept (a measured one before a made-up one, the one seen most square-on first)
    doors: set[int] = set()
    for o in list(kept):
        if o["kind"] != "door":
            continue
        if o["edge"] in doors:
            o["kind"] = "glass_door"
            invented.append(f"door on edge {o['edge']} at {o['at_m']} m built as a glazed door: a wall has one entrance door, and edge {o['edge']} already has one")
        doors.add(o["edge"])
    tidied = hr.tidy_walls(kept, {e["i"]: e["length"] for e in edges}, set(seen))
    if tidied != kept:
        invented.append("measured walls: openings centred on their wall, doors at least " + f"{hr.DOOR_MIN_W_M} m wide")
    kept = tidied
    if guess:  # as a builder sets them: one opening per floor, rows on one line, a lone window mirrored (docs/house-anomalies.md)
        kept, evened = hr.regularise(kept, {e["i"]: e["length"] for e in edges}, eave, set(seen))
        invented += evened
    openings = sorted(kept, key=lambda o: (o["edge"], o["at_m"]))
    counts: dict[str, int] = {}
    for o in openings:
        counts[o["kind"]] = counts.get(o["kind"], 0) + 1
        o["id"] = f"{'gd' if o['kind'] == 'glass_door' else o['kind'][0]}{counts[o['kind']]}"  # (glass_door and garage_door share a letter)
    openings = [{"id": o["id"], **{k: v for k, v in o.items() if k != "id"}} for o in openings]

    detail: dict = {"id": bid, "source": "Street View captures, measured by facade_pipeline" + ("; walls no capture saw, pipes, eaves and chimney by rule" if guess else "")}
    if roof != "flat":
        detail["overhang_m"] = out_m
        if reach is None:
            (invented if guess else problems).append(f"the roof's overhang ({out_m} m: not measured, no roof corner seen past an outside corner of a wall)")
    detail["frame_colour"] = frame_hex
    surround_hex = SURROUND_COLOURS.get(str(names.get("surround_colour")).lower())
    if surround_hex is not None:  # the band seen round the front's windows goes round every window
        detail["surround_colour"] = surround_hex
        for o in openings:
            if o["kind"] == "window":
                o["surround_m"] = hr.SURROUND_M
    plinth_m = round(min(min(plinths, key=lambda t: t[0])[0], 0.6), 2) if plinths else 0.0
    base = hr.base_style(bid)
    made_plinth = False
    if not plinths and guess and front is not None and str(names.get("wall_colour")).lower() not in hr.PLAIN_WALLS and hr.chance(bid, "plinth") < 0.75:
        # no plinth was seen (a fence, a hedge or the kerb hides most): a plastered house mostly has one
        plinth_m, made_plinth = base["plinth_m"], True
        if any(o["sill_m"] < plinth_m + 0.1 and o["kind"] == "window" for o in openings):
            plinth_m, made_plinth = 0.0, False
    if guess and front is not None:
        # the trim: one colour for bands, corner strips and the gable; the doors' steps, panels and canopies
        wall_name = str(names.get("wall_colour")).lower()
        gable_name = str(names.get("gable_colour")).lower()
        trim = hr.trim_style(bid, names, wall_name, bool(wall_lab is not None and wall_lab[0] > 205), surround_hex,
                             WALL_COLOURS.get(gable_name) if gable_name != wall_name else None, roof, eave,
                             [o["sill_m"] + o["height_m"] + o.get("surround_m", 0.0) for o in openings if roles[o["edge"]]["main"] and o["sill_m"] < eave - 0.5], plinth_m)  # fmt: skip
        detail["trim_colour"] = trim["trim_colour"]
        # a band runs clear of every opening: one that would cross a window (a tall window, a wall measured too high) is left out
        trim["bands"] = [bd for bd in trim["bands"] if not any(o["sill_m"] < bd["top_m"] and o["sill_m"] + o["height_m"] > bd["top_m"] - bd["height_m"] for o in openings)]
        if trim["bands"]:
            detail["bands"] = trim["bands"]
        if trim["corners"]:
            detail["corners"] = trim["corners"]
        if trim["gable_colour"]:
            detail["gable_colour"], detail["gable_from_m"] = trim["gable_colour"], round(eave, 2)
        for k, o in enumerate(openings):
            if o["kind"] == "window" and trim["lintel_m"] and o["sill_m"] + o["height_m"] + trim["lintel_m"] <= (eave if roles[o["edge"]]["main"] else rest_low) - 0.05 and o["sill_m"] < eave - 0.5:
                o["lintel_m"] = trim["lintel_m"]
            if o["kind"] in ("door", "garage_door"):
                openings[k] = hr.dress_door(o, roles[o["edge"]], eave if roles[o["edge"]]["main"] else rest_low, out_m, plinth_m, o["confidence"] == "low",
                                            {"yes": True, "no": False}.get(str(names.get("canopy")).lower()))
        invented += trim["made"]
        invented.append("door panels, steps and canopies")
        if roof != "flat":
            detail["ridge_caps"] = True
    if plinths:
        foot, lab, _ = min(plinths, key=lambda t: t[0])
        # as sampled, not brightened: a plinth is dark. Nearly black is not its paint but the shadow of what stands in
        # front of it (a fence): a plain grey then
        colour = _lab_hex(muted(lab)) if lab is not None and lab[0] >= 70 else "#77726c"
        detail["plinth"] = {"height_m": round(min(foot, 0.6), 2), "colour": colour}  # higher than 0.6 m is not a plinth: a line of the fence or the ground
    elif made_plinth:
        painted = darker(ov.get("wall_colour", "#b0a090"), 0.6, 0.4)
        plinth_hex = {"paint": painted, "stone": varied(hr.PLINTH_STONE, bid + ":plinth"), "brick": varied(hr.PLINTH_BRICK, bid + ":plinth")}[base["plinth"]]
        detail["plinth"] = {"height_m": plinth_m, "colour": plinth_hex}
        invented.append(f"a plinth {plinth_m} m high ({base['plinth']}; none was seen)")
    if guess and front is not None:
        strip_hex = varied(hr.STRIP_COLOURS[base["strip"]], bid + ":strip") if base["strip"] != "footing" else detail["plinth"]["colour"] if "plinth" in detail else darker(ov.get("wall_colour", "#b0a090"), 0.55, 0.5)
        detail["apron"] = {"width_m": base["width_m"], "colour": strip_hex, **({"joints_m": base["joints_m"]} if "joints_m" in base else {})}
        invented.append(f"the strip round the foot of the house ({base['strip']}, {base['width_m']} m)")
        if roof != "flat":
            detail["roof_tiles"] = True
    pipe_c = _lab_hex(muted(np.median(np.array([dp["_lab"] for dp in pipes], float), axis=0))) if pipes else (roof_hex or "#4a4a4a")
    if roof != "flat" and detail.get("overhang_m", 0) > 0:
        light = bool(boards) and sum(boards) * 2 > len(boards)  # the board under the gutter is as light as the wall
        detail["eaves"] = {"colour": "#ece6d8" if light else pipe_c, "fascia_m": fascia, "gutter": True, **({"level_soffit": True} if boxed else {}),
                           **({"rafters": True} if style["rafters"] else {}), **({"gutter_colour": pipe_c} if light else {})}  # fmt: skip
        if style["rafters"]:
            invented.append("rafter tails under the eaves")
        if roof == "gabled" and guess:
            detail["bargeboard_colour"] = frame_hex
            invented.append("bargeboards on the gables, in the frames' colour")
    # a strip between or beside the panes of a window is its post or its surround, not a pipe
    pipes = [dp for dp in pipes if not any(o["edge"] == dp["edge"] and o["at_m"] - 0.3 <= dp["at_m"] <= o["at_m"] + o["width_m"] + 0.3 for o in openings)]
    if guess and front is not None:
        ruled = hr.invent_downpipes(roles, openings, roof)
        edge_len = {e["i"]: e["length"] for e in edges}  # (a role's length is the whole straight wall's, which may be several edges)
        ruled = [{**dp, "at_m": round(min(dp["at_m"], edge_len[dp["edge"]] - 0.1), 2)} for dp in ruled if edge_len[dp["edge"]] >= 0.5]
        pipes = []
        if ruled:
            detail["downpipes"] = [{**dp, "colour": pipe_c, "bends": True} for dp in ruled]
            invented.append(f"{len(ruled)} downpipe(s) at the ends of the eaves")
        if roof != "flat":
            u = np.array([math.cos(ridge_way), math.sin(ridge_way)])
            spans = [float(np.asarray(pt, float) @ u) for pt in (part["ring"] if part is not None else fitted.parts[0].rect.corners if fitted is not None else ring)]
            ridge_len = max(spans) - min(spans) - (2 * half if roof == "hipped" else 0.0)
            from shapely.geometry import Polygon as _Poly

            ch = hr.invent_chimney(bid, str(names.get("chimney") or "unknown").lower(), roof, middle, ridge_way, ridge_len, top,
                                   np.subtract(ring[front[2]], ring[front[3]]), roles[front[1]]["normal"], _Poly(ring).area, half)  # fmt: skip
            if ch is not None:
                detail["chimneys"] = [{**ch, "cap": True}]
                invented.append("the chimney's place and size" + ("" if str(names.get("chimney") or "unknown").lower() in ("left", "middle", "right") else " (none was seen: made up)"))
    if pipes:
        seen_at: list[dict] = []
        for dp in sorted(pipes, key=lambda d: (d["edge"], d["at_m"])):  # the same pipe in two captures is one pipe
            if not any(k["edge"] == dp["edge"] and abs(k["at_m"] - dp["at_m"]) < 0.4 for k in seen_at):
                seen_at.append({"edge": dp["edge"], "at_m": dp["at_m"], "colour": pipe_c})
        detail["downpipes"] = seen_at
    for o in openings:  # a glazed door is built as a window standing on the floor (glass, breakable), with no sill under it
        if o["kind"] == "glass_door":
            o["kind"] = "window"
            o.pop("sill", None)
    detail["openings"] = openings
    if seen_dormers and roof != "flat":
        slope = (top - eave) / half  # of the roof as it is built: the fitted main part's when the roof is fitted
        made_d: list[dict] = []
        for sd in sorted(seen_dormers, key=lambda s: (s["box"] is None, s["_off"])):  # those with a rectangle first: the others take their size
            if sd["box"] is None:
                sd["like"] = next((m for m in made_d if m["edge"] == sd["edge"]), made_d[0] if made_d else None)
            dm = dormer_from_box(sd, eave, slope)
            if dm is None or dm["at_m"] - dm["width_m"] / 2 < 0.3 or dm["at_m"] + dm["width_m"] / 2 > sd["length"] - 0.3:
                why = "the sight line to it does not meet the roof" if dm is None else f"it would stand at {dm['at_m']} m, {dm['width_m']} m wide, past the end of the {sd['length']:.1f} m wall"
                problems.append(f"a dormer above edge {sd['edge']} (seen {sd['_off']:.0f} degrees off square) could not be placed on the roof, left out: {why}")
            elif not any(k["edge"] == dm["edge"] and abs(k["at_m"] - dm["at_m"]) < (k["width_m"] + dm["width_m"]) / 2 for k in made_d):
                made_d.append(dm)
        if len(made_d) > 1:  # one kind of dormer per roof, as one kind of window per wall
            for key in ("bottom_m", "width_m", "height_m", "rise_m"):
                mid_v = round(float(np.median([m[key] for m in made_d])), 2)
                for m in made_d:
                    m[key] = mid_v
            win = [round(float(np.median([m["window_m"][a] for m in made_d])), 2) for a in (0, 1)]
            for m in made_d:
                m["window_m"] = win
        for k, dm in enumerate(sorted(made_d, key=lambda m: (m["edge"], m["at_m"]))):
            dm.pop("_t", None)
            dm["id"] = f"dm{k + 1}"
            dm["colour"] = ov.get("wall_colour", "#b0a090")
        if made_d:
            detail["dormers"] = [{"id": dm["id"], **{k: v for k, v in dm.items() if k != "id"}} for dm in made_d]

    if guess and front is not None and (roof != "flat" or hr.levels_of(eave) == 2):
        said_b = names.get("balcony") or ("yes" if "balcony" in str(names.get("not_modelled") or "").lower() else "no")
        bal = hr.invent_balcony(said_b, front[1], roles[front[1]], eave, top, openings)
        if bal is not None:
            detail["balconies"] = [bal]
            for o in openings:  # a window over the balcony whose foot the railing hid is the door onto it
                if o["edge"] == bal["edge"] and o["kind"] == "window" and bal["floor_m"] < o["sill_m"] < bal["floor_m"] + 1.7 and bal["at_m"] <= o["at_m"] and o["at_m"] + o["width_m"] <= bal["at_m"] + bal["width_m"]:
                    head = o["sill_m"] + o["height_m"]
                    o.update({"sill_m": round(bal["floor_m"] + 0.05, 2), "height_m": round(head - bal["floor_m"] - 0.05, 2), "sill": False})
                    o.pop("lintel_m", None)
            invented.append("the balcony's size and height (one was seen on the front)")
    if guess and front is not None and roof != "flat":
        lights = hr.invent_skylights(bid, roles, set(seen), roof, eave, top, half, {dm["edge"] for dm in detail.get("dormers", [])})
        if lights:
            detail["skylights"] = lights
            invented.append(f"{len(lights)} roof window(s) on the slopes no capture saw")
    b = _derived(bid, ov)
    while True:  # an opening the generated shell cannot hold is left out and reported
        try:
            mesh, tags = build_detail(b, parse_detail(detail, bid))
            break
        except DetailError as e:
            bad = next((o for o in openings if f"opening {o['id']}:" in str(e)), None)
            if bad is None and "chimney" in str(e) and detail.get("chimneys"):
                del detail["chimneys"]
                problems.append(f"the chimney left out: {str(e).split(': ', 1)[-1]}")
                continue
            if bad is None and "skylight" in str(e) and detail.get("skylights"):
                del detail["skylights"]
                continue
            if bad is None and "dormer" in str(e) and detail.get("dormers"):
                gone = next((dm for dm in detail["dormers"] if f"dormer {dm['id']}" in str(e)), detail["dormers"][0])
                detail["dormers"].remove(gone)
                problems.append(f"dormer above edge {gone['edge']} at {gone['at_m']} m left out: {str(e).split(': ', 1)[-1]}")
                if not detail["dormers"]:
                    del detail["dormers"]
                continue
            if bad is None:
                raise SystemExit(f"the detail does not build: {e}") from None
            openings.remove(bad)
            problems.append(f"{bad['kind']} on edge {bad['edge']} at {bad['at_m']} m, {bad['sill_m']} m up, left out: {str(e).split(': ', 1)[-1]}")
    full = Mesh.concat([mesh, *(t.mesh for t in tags)])
    for cap in state["captures"]:
        overlay_mesh(Path(cap["photo"]), out / f"cam_{cap['n']}.json", full, out / f"overlay_{cap['n']}.png")
    render_views(full, b, {c["n"]: c["camera_final"] for c in state["captures"]}, {c["n"]: c["photo"] for c in state["captures"]}, out)
    what, lost = names.get("what") or "house", names.get("not_modelled") or ""
    result = {"invented": invented, "building": bid, "roof": roof, "eave_m": round(eave, 2), "top_m": round(top, 2), "overrides": ov, "detail": detail, "walls_seen": seen, "problems": problems}
    (out / "result.json").write_text(json.dumps(result, indent=1))
    if not dry:
        import yaml

        head = [f"# {b['parcel']}: {what}. Measured by facade_pipeline from {len(state['captures'])} Street View capture(s).",
                f"# Wall top {eave:.2f} m, roof top {top:.2f} m. Walls seen: edges {seen}." + (f" Not modelled: {lost}." if lost else "")]  # fmt: skip
        rest = {k: v for k, v in detail.items() if k != "openings"}
        if "roof_part" not in ov:  # every house with a detail is hollow (ADR 0040); one cut in parts cannot be yet
            rest["interior"] = True
        body = yaml.safe_dump(rest, sort_keys=False, default_flow_style=None, width=200)

        def flow(v) -> str:
            if isinstance(v, bool):
                return "true" if v else "false"
            return f'"{v}"' if isinstance(v, str) and v.startswith("#") else str(v)

        rows = "".join("  - {" + ", ".join(f"{k}: {flow(v)}" for k, v in o.items()) + "}\n" for o in openings)
        from drycastle_pipeline.edits.building_edit import atomic_write

        atomic_write(DETAILS_DIR / f"{bid}.yaml", "\n".join(head) + "\n" + body + ("openings:\n" + rows if openings else "openings: []\n"))  # (the viewer watches the file: never half of it)
        _upsert_override(PIPELINE_DIR / "config" / "overrides.yaml", bid, f"{b['parcel']}: {what}, from Street View captures; openings in config/buildings/.", ov, replace)
    print(f"{bid}  parcel {b['parcel']}")
    print(f"{bid}: {roof} roof, wall top {eave:.2f} m, roof top {top:.2f} m, overhang {detail.get('overhang_m', 0)} m, "
          f"plinth {detail['plinth']['height_m'] if 'plinth' in detail else 'none'}, {len(detail.get('downpipes', []))} downpipe(s), "
          f"colours wall {ov.get('wall_colour')} roof {ov.get('roof_colour')} frames {frame_hex}")  # fmt: skip
    for o in openings:
        print(f"  {o['id']}: {o['kind']} on edge {o['edge']}, {o['at_m']} m along, {o['width_m']} x {o['height_m']} m, sill {o['sill_m']} m"
              + (f", posts at {o['posts']}" if o.get("posts") else "") + (" (guessed)" if o["confidence"] == "low" else ""))  # fmt: skip
    for dm in detail.get("dormers", []):
        print(f"  {dm['id']}: dormer above edge {dm['edge']}, {dm['at_m']} m along, {dm['width_m']} m wide, foot {dm['bottom_m']} m up")
    for ch in detail.get("chimneys", []):
        print(f"  chimney at ({ch['x']}, {ch['y']}), top {ch['top_m']} m")
    for p in problems:
        print(f"PROBLEM: {p}")
    if invented:
        print("MADE UP BY RULE (not seen in a capture): " + "; ".join(invented))
    print("dry run: nothing written" if dry else f"written: config/buildings/{bid}.yaml and its entry in config/overrides.yaml")
    print(f"look at {out}/compare_<n>.png (the photo beside the model from the same place) and {out}/overlay_<n>.png (the model's outline in magenta over the photo)")


def _run_dir(arg: str) -> Path:
    """A run folder as given, or a building id's saved run beside its captures."""
    p = Path(arg)
    if p.is_dir() or "/" in arg:
        return p
    from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR
    return PHOTOS_DIR / arg / "run"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("ends")
    e.add_argument("building")
    e.add_argument("--out", type=Path, help="run folder (default: beside the capture, data/.../photos/<id>/run)")
    e.add_argument("--no-marks", action="store_true", help="ignore the wall ends marked by hand at capture: ask for them among the computer's candidates")
    w = sub.add_parser("walls")
    w.add_argument("out", type=_run_dir, help="run folder, or a building id for its saved run")
    a = sub.add_parser("apply")
    a.add_argument("out", type=_run_dir, help="run folder, or a building id for its saved run")
    a.add_argument("--dry", action="store_true", help="measure and draw, write nothing under config/")
    a.add_argument("--replace", action="store_true", help="overwrite the building's entry in overrides.yaml")
    a.add_argument("--no-guess", action="store_true", help="leave walls no capture saw blank instead of giving them windows like the measured ones")
    r = sub.add_parser("read", help="answer the open question of each run folder by one API call (needs ANTHROPIC_API_KEY)")
    r.add_argument("outs", type=_run_dir, nargs="+", help="run folders or building ids")
    r.add_argument("--again", action="store_true", help="ask the last question again though it is answered")
    r.add_argument("--batch", action="store_true", help="all folders as one Message Batch: half price, waits for it (minutes)")
    au = sub.add_parser("auto", help="the whole thing in one command, for a UI: ends, read, walls, read, apply (or a photo of your own); see auto_build.py")
    from drycastle_pipeline.photos import auto_build

    auto_build.add_arguments(au)
    n = sub.add_parser("answers", help="put an agent's reply ({folder or id: answer}) into the run folders")
    n.add_argument("reply", type=Path)
    n.add_argument("--again", action="store_true", help="overwrite the answer of the last question")
    args = ap.parse_args(argv)
    if args.cmd == "auto":
        import sys

        return auto_build.run(args, sys.modules[__name__])
    if args.cmd in ("read", "answers"):
        from drycastle_pipeline.photos import facade_reader

        if args.cmd == "read":
            (facade_reader.stage_read_batch if args.batch else facade_reader.stage_read)(args.outs, args.again)
        else:
            facade_reader.stage_answers(args.reply, args.again, _run_dir)
    elif args.cmd == "ends":
        stage_ends(args.building, args.out or _run_dir(args.building), not args.no_marks)
    elif args.cmd == "walls":
        stage_walls(args.out)
    else:
        stage_apply(args.out, args.dry, args.replace, not args.no_guess)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
