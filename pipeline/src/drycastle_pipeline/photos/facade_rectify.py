"""Experiment: a capture's camera from the straight lines in the picture, and one wall flattened to a straight-on view.

    uv run python -m drycastle_pipeline.photos.facade_rectify w378920491 0 --edge 0 --out DIR

No model reads anything here. The capture gives where the camera stood and how it looked (``photo_capture``); the
footprint gives which way the walls run. Then:

1. the heading is turned from compass north to the map grid's north (about 6 degrees apart in the village);
2. straight segments are found in the picture (LSD) and the heading and tilt are turned until as many as possible
   point along the wall, along the next wall or straight up (the place of the camera plays no part in this);
3. the two ends of the wall are looked for as strong upright lines near where the map puts them, and the camera is
   moved so both fall on them (a capture's place is good to a few metres only);
4. the wall is warped flat: ``wall_<edge>.png``, PX_PER_M pixels per metre, the wall's first corner on the left,
   height 0 at the building's base as far as the camera's assumed height above it goes.

``lines.png`` shows the segments by the direction they were taken for, and the wall's outline before and after.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np

from drycastle_pipeline.photos.landmark_photo import project

PX_PER_M = 100
Z_RANGE = (-1.0, 7.0)  # metres of height in the flattened wall
SIDE_M = 1.5  # metres shown past each end of the wall
MIN_SEG_PX = 22
TOL_DEG = 2.0  # a segment this close to a direction counts for it in full
CORNER_SEARCH_PX = 110
MERGE_PX = 15
COLOURS = {"up": (0, 255, 0), "wall": (255, 0, 255), "next": (0, 200, 255)}  # BGR


def capture_camera(c: dict, cam_height: float = 2.5) -> dict:
    """The camera of a capture in map axes: the heading against the grid's north, the lens from the tab's vertical zoom."""
    from drycastle_pipeline.photos.streetview_follow import position_doc

    grid = position_doc(c["lat"], c["lon"], c["heading"], 0)["heading_grid"]
    f = (c["height"] / 2) / math.tan(math.radians(c["fov"] / 2))
    return {"x": c["x"], "y": c["y"], "z": cam_height, "yaw": grid, "pitch": c["pitch"], "f": f, "w": c["width"], "h": c["height"]}


def _axes(cam: dict):
    yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    f = np.array([math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)])
    r = np.cross(f, [0, 0, 1.0])
    r /= np.linalg.norm(r)
    return f, r, np.cross(r, f)


def find_segments(gray) -> np.ndarray:
    """Straight segments (n, 4: x0, y0, x1, y1) at least MIN_SEG_PX long."""
    import cv2

    lines = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(gray)[0]
    if lines is None:
        return np.zeros((0, 4))
    s = lines.reshape(-1, 4).astype(float)
    return s[np.hypot(s[:, 2] - s[:, 0], s[:, 3] - s[:, 1]) >= MIN_SEG_PX]


def off_direction(cam: dict, segs: np.ndarray, d) -> np.ndarray:
    """Degrees between each segment and the picture of the world direction ``d`` at the segment's middle."""
    f, r, u = _axes(cam)
    mid = (segs[:, :2] + segs[:, 2:]) / 2
    rays = f * cam["f"] + (mid[:, :1] - cam["w"] / 2) * r - (mid[:, 1:] - cam["h"] / 2) * u
    # a point 10 m out along the ray, and the same point moved along d: their pictures give d's direction there
    p = rays / np.linalg.norm(rays, axis=1, keepdims=True) * 10.0
    q = p + np.asarray(d, float) * 0.5

    def pix(v):
        depth = v @ f
        return np.stack([cam["f"] * (v @ r) / depth, -cam["f"] * (v @ u) / depth], axis=1)

    want = pix(q) - pix(p)
    have = segs[:, 2:] - segs[:, :2]
    cross = np.abs(want[:, 0] * have[:, 1] - want[:, 1] * have[:, 0])
    norm = np.linalg.norm(want, axis=1) * np.linalg.norm(have, axis=1) + 1e-12
    return np.degrees(np.arcsin(np.clip(cross / norm, 0, 1)))


def _score(cam: dict, segs: np.ndarray, lengths: np.ndarray, dirs: list) -> float:
    best = np.min([off_direction(cam, segs, d) for d in dirs], axis=0)
    return float((lengths * np.exp(-((best / TOL_DEG) ** 2))).sum())


def refine_rotation(cam: dict, segs: np.ndarray, dirs: list) -> dict:
    """Heading and tilt that line the most segment length up with the given world directions (coarse, then fine)."""
    lengths = np.hypot(segs[:, 2] - segs[:, 0], segs[:, 3] - segs[:, 1])
    best = dict(cam)
    for span_yaw, span_pitch, step in ((6.0, 3.0, 0.5), (0.6, 0.6, 0.05)):
        base, top = dict(best), -1.0
        for dy in np.arange(-span_yaw, span_yaw + 1e-9, step):
            for dp in np.arange(-span_pitch, span_pitch + 1e-9, step):
                trial = {**base, "yaw": base["yaw"] + dy, "pitch": base["pitch"] + dp}
                s = _score(trial, segs, lengths, dirs)
                if s > top:
                    top, best = s, trial
    return best


def classify(cam: dict, segs: np.ndarray, dirs: dict) -> list[str | None]:
    off = np.stack([off_direction(cam, segs, d) for d in dirs.values()])
    names = list(dirs)
    return [names[i] if off[i, k] < 2 * TOL_DEG else None for k, i in enumerate(off.argmin(axis=0))]


def corner_column(cam: dict, segs: np.ndarray, kinds: list, corner, z=(0.5, 4.5)) -> tuple[float, float] | None:
    """The picture column of the strongest upright line near where the map puts a footprint corner: (x at the camera's
    level, how far that is from the map's place in px). None when nothing upright is near."""
    want = project(cam, np.array([[*corner, cam["z"]], [*corner, z[0]], [*corner, z[1]]]))
    if not np.isfinite(want).all():
        return None
    x0, (ylo, yhi) = want[0, 0], sorted((want[1, 1], want[2, 1]))
    votes: dict[int, float] = {}
    for s, k in zip(segs, kinds):
        if k != "up":
            continue
        ya, yb = sorted((s[1], s[3]))
        if yb < ylo - 40 or ya > yhi + 40:
            continue
        x = (s[0] + s[2]) / 2
        if abs(x - x0) <= CORNER_SEARCH_PX:
            votes[round(x / 4)] = votes.get(round(x / 4), 0.0) + (yb - ya)
    if not votes:
        return None
    x = max(votes, key=votes.get) * 4.0
    return x, x - x0


def end_candidates(cam: dict, segs: np.ndarray, kinds: list, corner, z=(0.6, 3.0), keep: int = 7) -> tuple[list[dict], tuple[float, float, float]]:
    """The upright lines near where the map puts a footprint corner: [{x, length}] left to right (the ``keep`` longest),
    and (the map's column, top row, bottom row) of the corner. Only lines that lie mostly at the wall's own height
    count: a chimney or a pole above the roof is upright too, and is never where a wall ends."""
    want = project(cam, np.array([[*corner, cam["z"]], [*corner, z[0]], [*corner, z[1]]]))
    x0, (ylo, yhi) = want[0, 0], sorted((want[1, 1], want[2, 1]))
    hits = []
    for s, k in zip(segs, kinds):
        ya, yb = sorted((s[1], s[3]))
        x = (s[0] + s[2]) / 2
        inside = min(yb, yhi) - max(ya, ylo)
        if k == "up" and inside >= 0.5 * (yb - ya) and abs(x - x0) <= CORNER_SEARCH_PX:
            hits.append((x, yb - ya))
    hits.sort()
    groups: list[list[tuple[float, float]]] = []
    for h in hits:  # lines within MERGE_PX of each other are one candidate: closer ones cannot be told apart by eye
        if groups and h[0] - groups[-1][0][0] <= MERGE_PX:
            groups[-1].append(h)
        else:
            groups.append([h])
    cands = [{"x": round(sum(x * n for x, n in g) / sum(n for _, n in g), 1), "length": round(sum(n for _, n in g))} for g in groups]
    cands = sorted(sorted(cands, key=lambda c: -c["length"])[:keep], key=lambda c: c["x"])
    return cands, (float(x0), float(ylo), float(yhi))


CAND_COLOURS = [(0, 255, 255), (255, 0, 255), (0, 255, 0), (0, 128, 255), (255, 255, 0), (255, 0, 0), (180, 105, 255)]  # BGR


def candidates_picture(photo, cands: list[dict], where: tuple[float, float, float], title: str, outline=None):
    """The photo with the part around one wall end marked, beside that part enlarged with each candidate line lettered.
    A line is drawn solid only above and below the middle, dashed between, so it does not hide what it marks."""
    import cv2

    x0, ylo, yhi = where
    hh, ww = photo.shape[:2]
    xs = [c["x"] for c in cands] + [x0]
    bx0, bx1 = int(max(0, min(xs) - 90)), int(min(ww, max(xs) + 90))
    by0, by1 = int(max(0, ylo - 110)), int(min(hh, yhi + 110))
    k = 900 / (by1 - by0)
    zoom = cv2.resize(photo[by0:by1, bx0:bx1], None, fx=k, fy=k, interpolation=cv2.INTER_LANCZOS4)
    zoom = cv2.copyMakeBorder(zoom, 70, 0, 0, 0, cv2.BORDER_CONSTANT, value=(30, 30, 30))
    for i, c in enumerate(cands):
        col = CAND_COLOURS[i % len(CAND_COLOURS)]
        x = int((c["x"] - bx0) * k)
        cv2.line(zoom, (x, 70), (x, 70 + 110), col, 2)
        cv2.line(zoom, (x, zoom.shape[0] - 110), (x, zoom.shape[0]), col, 2)
        for y in range(70 + 110, zoom.shape[0] - 110, 26):
            cv2.line(zoom, (x, y), (x, y + 5), col, 1)
        ty = 30 if i % 2 == 0 else 62
        cv2.putText(zoom, c["letter"], (x - 11, ty), cv2.FONT_HERSHEY_SIMPLEX, 1.0, col, 3, cv2.LINE_AA)
    ko = zoom.shape[0] / hh
    over = cv2.resize(photo, None, fx=ko, fy=ko, interpolation=cv2.INTER_AREA)
    if outline is not None and np.isfinite(outline).all():  # roughly where the map puts the wall: which wall is meant
        cv2.polylines(over, [(np.asarray(outline) * ko).astype(np.int32)], True, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.rectangle(over, (int(bx0 * ko), int(by0 * ko)), (int(bx1 * ko), int(by1 * ko)), (0, 255, 255), 3)
    cv2.putText(over, title, (20, over.shape[0] - 24), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 0, 0), 5, cv2.LINE_AA)
    cv2.putText(over, title, (20, over.shape[0] - 24), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 255, 255), 2, cv2.LINE_AA)
    return np.hstack([over, np.full((zoom.shape[0], 12, 3), 30, np.uint8), zoom])


def bearing(cam: dict, x: float) -> np.ndarray:
    """The level direction (map x, y) the picture column ``x`` looks along, at the camera's own level."""
    f, r, u = _axes(cam)
    # the row of the horizon in that column: where the ray is level
    d0 = f * cam["f"] + (x - cam["w"] / 2) * r
    t = d0[2] / u[2] if abs(u[2]) > 1e-9 else 0.0
    d = d0 - t * u
    return d[:2] / np.linalg.norm(d[:2])


def refine_position(cam: dict, a, xa: float, b, xb: float) -> dict:
    """The camera moved (heading kept) so the footprint corners ``a`` and ``b`` fall on the columns ``xa`` and ``xb``."""
    da, db = bearing(cam, xa), bearing(cam, xb)
    # camera = a - ta * da = b - tb * db
    m = np.array([[-da[0], db[0]], [-da[1], db[1]]])
    ta, _ = np.linalg.solve(m, np.asarray(b, float) - np.asarray(a, float))
    pos = np.asarray(a, float) - ta * da
    return {**cam, "x": float(pos[0]), "y": float(pos[1])}


def wall_homography(cam: dict, a, b):
    """The 3x3 map from the flattened wall's pixels to the photo's, and the flat picture's size."""
    import cv2

    a, b = np.asarray(a, float), np.asarray(b, float)
    length = float(np.linalg.norm(b - a))
    e = (b - a) / length
    s0, s1 = -SIDE_M, length + SIDE_M
    plane = np.array([[s0, Z_RANGE[1]], [s1, Z_RANGE[1]], [s1, Z_RANGE[0]], [s0, Z_RANGE[0]]])
    world = np.array([[*(a + e * s), z] for s, z in plane])
    flat = np.array([[(s - s0) * PX_PER_M, (Z_RANGE[1] - z) * PX_PER_M] for s, z in plane], dtype=np.float32)
    h = cv2.getPerspectiveTransform(flat, project(cam, world).astype(np.float32))
    return h, (int((s1 - s0) * PX_PER_M), int((Z_RANGE[1] - Z_RANGE[0]) * PX_PER_M)), length


def flatten(photo, cam: dict, a, b, segs: np.ndarray, kinds: list):
    """The wall from ``a`` to ``b`` straight-on, with a metre scale and the segments that lie along or up it."""
    import cv2

    h, size, length = wall_homography(cam, a, b)
    flat = cv2.warpPerspective(photo, h, size, flags=cv2.INTER_LANCZOS4 | cv2.WARP_INVERSE_MAP)
    plain = flat.copy()
    inv = np.linalg.inv(h)
    for s, k in zip(segs, kinds):
        if k not in ("up", "wall"):
            continue
        p = cv2.perspectiveTransform(s.reshape(1, 2, 2).astype(np.float32), inv)[0]
        if (p[:, 0].min() < -50) or (p[:, 0].max() > size[0] + 50) or (p[:, 1].min() < -50) or (p[:, 1].max() > size[1] + 50):
            continue
        cv2.line(flat, tuple(int(v) for v in p[0]), tuple(int(v) for v in p[1]), COLOURS[k], 2, cv2.LINE_AA)

    def col(s):
        return int((s + SIDE_M) * PX_PER_M)

    def row(z):
        return int((Z_RANGE[1] - z) * PX_PER_M)

    for img in (flat, plain):
        for s in (0.0, length):
            cv2.line(img, (col(s), 0), (col(s), size[1]), (255, 255, 255), 1)
        cv2.line(img, (0, row(0)), (size[0], row(0)), (255, 255, 255), 1)
        cv2.line(img, (0, row(cam["z"])), (size[0], row(cam["z"])), (0, 255, 255), 1)
        for z in range(int(Z_RANGE[0]), int(Z_RANGE[1]) + 1):
            cv2.line(img, (0, row(z)), (18, row(z)), (255, 255, 255), 2)
            cv2.putText(img, f"{z} m", (22, row(z) + 5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)
        for s in range(int(length) + 1):
            cv2.line(img, (col(s), size[1] - 18), (col(s), size[1]), (255, 255, 255), 2)
            cv2.putText(img, str(s), (col(s) + 3, size[1] - 22), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 255), 1, cv2.LINE_AA)
    return flat, plain


def _outline(img, cam: dict, a, b, colour, z=(0.0, 3.0)) -> None:
    import cv2

    q = project(cam, np.array([[*a, z[0]], [*b, z[0]], [*b, z[1]], [*a, z[1]]]))
    if np.isfinite(q).all():
        cv2.polylines(img, [q.astype(np.int32)], True, colour, 2, cv2.LINE_AA)


def run(building: str, capture: int, edge: int, out: Path, columns: tuple[str, str] | None = None) -> dict:
    import cv2

    from drycastle_pipeline.photos.landmark_tools import WORK
    from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR

    side = PHOTOS_DIR / building / f"{capture}.json"
    c = json.loads(side.read_text())
    ring = next(b for b in json.loads((WORK / "buildings.json").read_text())["buildings"] if b["id"] == building)["footprint"]["exterior"]
    n = len(ring)
    a, b, nxt = ring[edge], ring[(edge + 1) % n], ring[(edge + 2) % n]

    def unit(p, q):
        v = np.array([q[0] - p[0], q[1] - p[1], 0.0])
        return v / np.linalg.norm(v)

    dirs = {"up": np.array([0, 0, 1.0]), "wall": unit(a, b), "next": unit(b, nxt)}
    photo = cv2.imread(str(side.parent / c["photo"]))
    segs = find_segments(cv2.cvtColor(photo, cv2.COLOR_BGR2GRAY))
    cam0 = capture_camera(c)
    cam1 = refine_rotation(cam0, segs, list(dirs.values()))
    kinds = classify(cam1, segs, dirs)
    cam2, cols = cam1, {}
    out.mkdir(parents=True, exist_ok=True)
    cands, letter = {}, 0
    for name, corner, side_name in (("a", a, "LEFT"), ("b", b, "RIGHT")):
        cols[name] = corner_column(cam1, segs, kinds, corner)
        cands[name], where = end_candidates(cam1, segs, kinds, corner)
        for cnd in cands[name]:
            cnd["letter"] = chr(ord("A") + letter)
            letter += 1
        cv2.imwrite(str(out / f"end_{name}.png"), candidates_picture(photo, cands[name], where, f"{side_name} end of the wall is somewhere in this box"))
    (out / "candidates.json").write_text(json.dumps(cands, indent=1))
    if columns:
        by = {cnd["letter"]: cnd["x"] for v in cands.values() for cnd in v}
        for name, corner, pick in (("a", a, columns[0]), ("b", b, columns[1])):
            x = by[pick] if pick in by else float(pick)
            cols[name] = (x, x - project(cam1, np.array([[*corner, cam1["z"]]]))[0, 0])
    if cols["a"] and cols["b"]:
        moved = refine_position(cam1, a, cols["a"][0], b, cols["b"][0])
        if math.dist((moved["x"], moved["y"]), (cam1["x"], cam1["y"])) < 5.0:
            cam2 = moved
    lines = photo.copy()
    for s, k in zip(segs, kinds):
        if k:
            cv2.line(lines, (int(s[0]), int(s[1])), (int(s[2]), int(s[3])), COLOURS[k], 1, cv2.LINE_AA)
    _outline(lines, cam0, a, b, (0, 0, 255))
    _outline(lines, cam2, a, b, (255, 255, 255))
    for v in cols.values():
        if v:
            cv2.line(lines, (int(v[0]), 0), (int(v[0]), photo.shape[0]), (0, 255, 255), 1)
    cv2.imwrite(str(out / "lines.png"), lines)
    flat, plain = flatten(photo, cam2, a, b, segs, kinds)
    cv2.imwrite(str(out / f"wall_{edge}_lines.png"), flat)
    cv2.imwrite(str(out / f"wall_{edge}.png"), plain)
    report = {
        "segments": len(segs), "used": {k: kinds.count(k) for k in dirs},
        "heading_compass": c["heading"], "heading_grid": round(cam0["yaw"], 2), "heading_fitted": round(cam1["yaw"], 2),
        "pitch_capture": c["pitch"], "pitch_fitted": round(cam1["pitch"], 2),
        "ends_from": "given" if columns else "strongest upright line",
        "corner_columns": {k: (None if v is None else {"x": round(v[0], 1), "off_map_px": round(v[1], 1)}) for k, v in cols.items()},
        "camera_moved_m": round(math.dist((cam2["x"], cam2["y"]), (cam0["x"], cam0["y"])), 2),
        "camera": {k: round(v, 3) for k, v in cam2.items()},
    }  # fmt: skip
    (out / "camera.json").write_text(json.dumps(report, indent=1))
    return report


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("building")
    ap.add_argument("capture", type=int)
    ap.add_argument("--edge", type=int, required=True, help="the wall to flatten: edge i runs from corner i to i + 1")
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--ends", help="LEFT,RIGHT: the wall's two ends, each a letter of candidates.json or a photo column in px")
    a = ap.parse_args(argv)
    print(json.dumps(run(a.building, a.capture, a.edge, a.out, tuple(a.ends.split(",")) if a.ends else None), indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
