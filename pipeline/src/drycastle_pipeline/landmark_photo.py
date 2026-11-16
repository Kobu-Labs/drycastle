"""Measure a building from a photo (``.claude/skills/photo-to-building``): fit a camera to the photo on the building's
known footprint, then turn pixels on a wall into metres.

    uv run python -m drycastle_pipeline.landmark_photo grid PHOTO --out DIR
    uv run python -m drycastle_pipeline.landmark_photo fit PHOTO BUILDING --view X,Y --cam CAM.json POINT [POINT ...]
    uv run python -m drycastle_pipeline.landmark_photo measure CAM.json --edge I X,Y [X,Y ...]
    uv run python -m drycastle_pipeline.landmark_photo sample PHOTO X,Y [--white X,Y]
    uv run python -m drycastle_pipeline.landmark_photo overlay PHOTO CAM.json BUILDING [...] --model NAME --out DIR

``grid`` writes the photo with a labelled 50 px grid, to read pixel coordinates off. ``fit`` takes points of the building
seen in the photo, each ``WHERE:LEVEL:X,Y``: WHERE is a footprint corner (``c0``, ``c1``, ...) or the ridge end over an
edge (``r0``, ``r1``, ...: the top of the gable triangle on that edge); LEVEL is ``ground``, ``eave``, ``top`` or a height
in metres; X,Y is the pixel. It solves the camera (place, heading, tilt, focal length) together with the eave and ridge
heights and where along its wall the ridge sits, prints them with the error of every point, and writes the camera to ``--cam``. ``measure`` puts pixels on the
wall of one edge: metres along the edge from its first corner, and height above the ground. ``sample`` reads a colour
(the median of a small patch), corrected by a surface known to be white. ``overlay`` draws the model as the fitted
camera sees it over the photo (``overlay.png``): where the two do not line up, the model is wrong there.
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from drycastle_pipeline.landmark_tools import _buildings, _local, _raster

LEVELS = ("ground", "eave", "top")


def project(cam: dict, pts: np.ndarray) -> np.ndarray:
    """Pixels of map points ``(n, 3)`` (z up from the building base) for a camera {x, y, z, yaw, pitch, f, w, h}; yaw is
    the compass bearing it looks along (degrees), pitch is up (degrees). Points behind the camera come out as NaN."""
    yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    f = np.array([math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)])
    r = np.cross(f, [0, 0, 1.0])
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    rel = np.asarray(pts, float) - [cam["x"], cam["y"], cam["z"]]
    depth = rel @ f
    depth = np.where(depth > 0.05, depth, np.nan)
    return np.stack([cam["w"] / 2 + cam["f"] * (rel @ r) / depth, cam["h"] / 2 - cam["f"] * (rel @ u) / depth], axis=1)


def ray(cam: dict, px: float, py: float) -> np.ndarray:
    """The direction (map axes) a pixel looks along."""
    yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    f = np.array([math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)])
    r = np.cross(f, [0, 0, 1.0])
    r /= np.linalg.norm(r)
    u = np.cross(r, f)
    d = f * cam["f"] + r * (px - cam["w"] / 2) - u * (py - cam["h"] / 2)
    return d / np.linalg.norm(d)


def _parse_point(text: str, ring):
    try:
        where, level, px = text.split(":")
        x, y = (float(v) for v in px.split(","))
        i = int(where[1:])
        if where[0] == "c":
            xy = tuple(ring[i])
        elif where[0] == "r":
            xy = (tuple(ring[i]), tuple(ring[(i + 1) % len(ring)]))  # the ridge end: somewhere along this edge, solved
        else:
            raise ValueError
        lvl = level if level in LEVELS else float(level)
    except (ValueError, IndexError):
        raise SystemExit(f"bad point '{text}': use c<corner>|r<edge>:ground|eave|top|<metres>:X,Y, e.g. c1:eave:440,320") from None
    return xy, lvl, (x, y)


def solve(points, size: tuple[int, int], view: tuple[float, float], cam_height: float = 2.5, eave0: float = 3.0, top0: float = 5.5):
    """Least squares (Levenberg-Marquardt, numeric Jacobian) for the camera and the eave and ridge heights. ``points``
    are (xy, level, pixel). The viewpoint and the camera height are soft priors (3 m and 1 m); the focal length starts
    at a 60 degree lens and is held loosely. Returns (camera, eave or None, top or None, per-point error in px, ridge fraction or None: where along its edge, from the
    edge's first corner, the ridge meets the wall)."""
    w, h = size
    centre = np.mean([np.mean(xy, axis=0) if isinstance(xy[0], tuple) else xy for xy, _, _ in points], axis=0)
    yaw0 = math.degrees(math.atan2(centre[0] - view[0], centre[1] - view[1]))
    f0 = (w / 2) / math.tan(math.radians(30))
    p0 = np.array([view[0], view[1], cam_height, yaw0, 0.0, f0, eave0, top0, 0.5])
    ridge = any(isinstance(xy[0], tuple) for xy, _, _ in points)

    def place(xy, p):  # a corner, or the ridge end at the fraction p[8] along its edge
        if isinstance(xy[0], tuple):
            (ax, ay), (bx, by) = xy
            return (ax + (bx - ax) * p[8], ay + (by - ay) * p[8])
        return xy

    has = {lvl for _, lvl, _ in points}

    def unpack(p):
        return {"x": p[0], "y": p[1], "z": p[2], "yaw": p[3], "pitch": p[4], "f": p[5], "w": w, "h": h}

    def z_of(lvl, p):
        return {"ground": 0.0, "eave": p[6], "top": p[7]}.get(lvl, lvl)

    def resid(p):
        world = np.array([[*place(xy, p), z_of(lvl, p)] for xy, lvl, _ in points])
        err = (project(unpack(p), world) - np.array([px for _, _, px in points])).ravel()
        err = np.where(np.isfinite(err), err, 1e4)
        prior = [
            (p[0] - p0[0]) / 3.0 * 4, (p[1] - p0[1]) / 3.0 * 4, (p[2] - p0[2]) / 1.0 * 4,  # 4 px per sigma
            (math.log(p[5] / f0)) / 0.5 * 4,
            0.0 if "eave" in has else (p[6] - eave0) * 100, 0.0 if "top" in has else (p[7] - top0) * 100,
            max(0.0, p[6] - p[7] + 0.3) * 200, max(0.0, 1.5 - p[6]) * 200,  # the ridge is above the eave, the eave above 1.5 m
            (p[8] - 0.5) / 0.2 * 4 if ridge else (p[8] - 0.5) * 100,  # the ridge is near the middle of its wall
        ]  # fmt: skip
        return np.concatenate([err, prior])

    p, lam = p0.copy(), 1e-2
    r = resid(p)
    steps = np.array([0.01, 0.01, 0.01, 0.01, 0.01, 0.5, 0.005, 0.005, 0.002])
    for _ in range(300):
        J = np.stack([(resid(p + np.eye(9)[k] * steps[k]) - r) / steps[k] for k in range(9)], axis=1)
        A = J.T @ J
        g = J.T @ r
        while lam < 1e9:
            try:
                d = np.linalg.solve(A + lam * np.diag(np.diag(A) + 1e-9), -g)
            except np.linalg.LinAlgError:
                lam *= 10
                continue
            r2 = resid(p + d)
            if r2 @ r2 < r @ r:
                p, r, lam = p + d, r2, max(lam / 5, 1e-7)
                break
            lam *= 5
        else:
            break
        if np.abs(d / steps).max() < 1e-3:
            break
    n = len(points)
    errs = np.hypot(r[: 2 * n : 2], r[1 : 2 * n : 2])
    return unpack(p), (float(p[6]) if "eave" in has else None), (float(p[7]) if "top" in has else None), errs, (float(p[8]) if ridge else None)


def fit(photo: Path, ids: list[str], view, cam_path: Path, specs: list[str], cam_height: float) -> dict:
    b = _buildings(ids)[0]
    ring = b["footprint"]["exterior"]
    points = [_parse_point(s, ring) for s in specs]
    if len(points) < 4:
        raise SystemExit("give at least 4 points (5 or more is better): corners at the ground and at the eave, and a ridge end")
    size = Image.open(photo).size
    cam, eave, top, errs, ridge_at = solve(points, size, _local(view), cam_height)
    for s, e in zip(specs, errs):
        print(f"  {s:28} off by {e:5.1f} px" + ("   <- re-read this point" if e > 15 else ""))
    moved = math.dist((cam["x"], cam["y"]), _local(view))
    fov = math.degrees(2 * math.atan(size[0] / 2 / cam["f"]))
    print(f"camera: ({cam['x']:.1f}, {cam['y']:.1f}) {moved:.1f} m from the given viewpoint, {cam['z']:.1f} m up, looks {cam['yaw'] % 360:.0f} deg, "
          f"tilt {cam['pitch']:.1f} deg, lens {fov:.0f} deg")  # fmt: skip
    if eave is not None:
        print(f"EAVE = {eave:.2f}")
    if top is not None:
        print(f"TOP = {top:.2f}")
    if ridge_at is not None:
        print(f"RIDGE_AT = {ridge_at:.2f}   (0.5 is the middle of the gable wall; this is measured from the first corner of the r<edge> you gave)")
    rms = float(np.sqrt((errs**2).mean()))
    verdict = "good" if rms < 8 else "usable, heights to about 0.3 m" if rms < 15 else "BAD: re-read the points marked above (use `grid`), then fit again; do not use these numbers"
    print(f"fit error {rms:.1f} px: {verdict}")
    if not 12 < fov < 110 or moved > 15 or not 0.8 < cam["z"] < 6:
        print("WARNING: the camera is implausible (lens, place or height); a point is on the wrong corner, or the viewpoint is far off")
    cam_path.parent.mkdir(parents=True, exist_ok=True)
    cam_path.write_text(json.dumps({**cam, "building": b["id"], "eave": eave, "top": top, "ridge_at": ridge_at, "rms_px": rms}, indent=1))
    return cam


def measure(cam_path: Path, edge: int, pixels: list[tuple[float, float]]) -> list[tuple[float, float]]:
    cam = json.loads(cam_path.read_text())
    ring = _buildings([cam["building"]])[0]["footprint"]["exterior"]
    p, q = np.array(ring[edge]), np.array(ring[(edge + 1) % len(ring)])
    length = float(np.linalg.norm(q - p))
    u = (q - p) / length
    n = np.array([u[1], -u[0]])
    eye = np.array([cam["x"], cam["y"], cam["z"]])
    out = []
    for px, py in pixels:
        d = ray(cam, px, py)
        denom = d[:2] @ n
        t = ((p - eye[:2]) @ n) / denom if abs(denom) > 1e-9 else float("nan")
        hit = eye + d * t
        s, z = float((hit[:2] - p) @ u), float(hit[2])
        note = "" if -0.3 <= s <= length + 0.3 and t > 0 else "   <- not on this wall (wrong edge or pixel?)"
        print(f"  pixel ({px:.0f}, {py:.0f}): {s:5.2f} m along edge {edge} (of {length:.2f}), {z:5.2f} m up{note}")
        out.append((s, z))
    if len(out) == 2:
        (s0, z0), (s1, z1) = out
        print(f"  as an opening: s0 {min(s0, s1):.2f}, width {abs(s1 - s0):.2f}, z0 {min(z0, z1):.2f}, height {abs(z1 - z0):.2f}")
    return out


def sample(photo: Path, at: tuple[float, float], white: tuple[float, float] | None) -> list[int]:
    img = np.asarray(Image.open(photo).convert("RGB"), float)

    def patch(xy):
        x, y = int(xy[0]), int(xy[1])
        return np.median(img[max(0, y - 4) : y + 5, max(0, x - 4) : x + 5].reshape(-1, 3), axis=0)

    c = patch(at)
    if white:
        c = c * (242.0 / np.maximum(patch(white), 1.0))  # a white wall or frame is about 242 when lit
    rgb = [int(v) for v in np.clip(c, 0, 255).round()]
    print(rgb)
    return rgb


def grid(photo: Path, out: Path, step: int = 50) -> Path:
    img = Image.open(photo).convert("RGB")
    d = ImageDraw.Draw(img, "RGBA")
    w, h = img.size
    for x in range(0, w, step):
        major = x % (2 * step) == 0
        d.line([(x, 0), (x, h)], fill=(255, 255, 0, 150 if major else 70))
        if major:
            d.rectangle([x + 1, 0, x + 26, 11], fill=(0, 0, 0, 170))
            d.text((x + 2, 0), str(x), fill=(255, 255, 0, 255))
    for y in range(0, h, step):
        major = y % (2 * step) == 0
        d.line([(0, y), (w, y)], fill=(0, 255, 255, 150 if major else 70))
        if major:
            d.rectangle([0, y + 1, 26, y + 12], fill=(0, 0, 0, 170))
            d.text((1, y + 1), str(y), fill=(0, 255, 255, 255))
    out.mkdir(parents=True, exist_ok=True)
    path = out / "grid.png"
    img.save(path)
    print(f"{path}  ({w} x {h} px; yellow lines are x, cyan lines are y, every {step} px, labelled every {2 * step})")
    return path


def overlay(photo: Path, cam_path: Path, ids: list[str], model: str | None, out: Path) -> Path:
    from drycastle_pipeline.landmarks import build_landmark
    from drycastle_pipeline.meshes import Mesh

    cam = json.loads(cam_path.read_text())
    bs = _buildings(ids)
    if model:
        bs = [{**b, "model": model} for b in bs]
    mesh = Mesh.concat([build_landmark(b, detail=True) for b in bs])
    base = Image.open(photo).convert("RGB")
    yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    eye = np.array([cam["x"], cam["y"], cam["z"]])
    target = eye + [math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)]
    fov = math.degrees(2 * math.atan(cam["w"] / 2 / cam["f"]))
    render, mask = _raster(mesh.tris, mesh.colours.astype(float), eye, target, fov, base.size, with_mask=True)
    a, b = np.asarray(base, float), np.asarray(render, float)
    mixed = np.where(mask[..., None], a * 0.5 + b * 0.5, a)
    rim = mask ^ (np.roll(mask, 1, 0) & np.roll(mask, -1, 0) & np.roll(mask, 1, 1) & np.roll(mask, -1, 1) & mask)
    mixed[rim] = (255, 0, 255)  # the model's outline
    out.mkdir(parents=True, exist_ok=True)
    path = out / "overlay.png"
    Image.fromarray(mixed.astype(np.uint8)).save(path)
    print(f"{path}  (photo with the model drawn half see-through over it; magenta is the model's outline)")
    return path


def _xy(text: str) -> tuple[float, float]:
    x, y = text.split(",")
    return (float(x), float(y))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("grid")
    g.add_argument("photo", type=Path)
    g.add_argument("--out", type=Path, required=True)
    f = sub.add_parser("fit")
    f.add_argument("photo", type=Path)
    f.add_argument("building")
    f.add_argument("points", nargs="+")
    f.add_argument("--view", type=_xy, required=True)
    f.add_argument("--cam", type=Path, required=True)
    f.add_argument("--cam-height", type=float, default=2.5, help="2.5 for Street View (default), 1.6 for a hand-held photo")
    m = sub.add_parser("measure")
    m.add_argument("cam", type=Path)
    m.add_argument("--edge", type=int, required=True)
    m.add_argument("pixels", nargs="+", type=_xy)
    s = sub.add_parser("sample")
    s.add_argument("photo", type=Path)
    s.add_argument("at", type=_xy)
    s.add_argument("--white", type=_xy)
    o = sub.add_parser("overlay")
    o.add_argument("photo", type=Path)
    o.add_argument("cam", type=Path)
    o.add_argument("ids", nargs="+")
    o.add_argument("--model")
    o.add_argument("--out", type=Path, required=True)
    a = ap.parse_args(argv)
    if a.cmd == "grid":
        grid(a.photo, a.out)
    elif a.cmd == "fit":
        fit(a.photo, [a.building], a.view, a.cam, a.points, a.cam_height)
    elif a.cmd == "measure":
        measure(a.cam, a.edge, a.pixels)
    elif a.cmd == "sample":
        sample(a.photo, a.at, a.white)
    else:
        overlay(a.photo, a.cam, a.ids, a.model, a.out)


if __name__ == "__main__":
    main()
