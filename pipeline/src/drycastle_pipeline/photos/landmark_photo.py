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

The short way, for ordinary houses (the output is a building detail, ``config/buildings/<id>.yaml``, plus the building's
entry in ``config/overrides.yaml``; no code):

    uv run python -m drycastle_pipeline.landmark_photo prep w378920410 --out DIR                  (its captures: footprint + grids)
    uv run python -m drycastle_pipeline.landmark_photo prep G-xxxx --photo PHOTO --view X,Y --out DIR   (a photo of your own)
    uv run python -m drycastle_pipeline.landmark_photo apply SHEET.json --out DIR [--replace] [--dry]

``apply`` does fit, measure, sample, the two files and the overlay from one sheet::

    {"building": "w378920410", "what": "house, green render, white gables", "source": "owner's photo 2026-11-16, point A",
     "roof": "gabled",                 gabled | flat | hipped
     "gable_edge": 1,                  gabled: an edge with the triangle on top (sets the ridge direction)
     "photo": "/path/4.png", "view": [-589057.5, -1250175], "cam_height": 2.5,
     "points": ["c1:ground:443,535", "c1:eave:432,318", "c0:eave:122,332", "c2:eave:738,352", "r1:top:568,120"],
     "openings": [{"edge": 0, "kind": "window", "px": [[158, 368], [208, 440]]},       two opposite corners
                  {"edge": 1, "kind": "door", "px": [[548, 205], [588, 318]], "colour": "#b08a6a"}],
     "colours": {"wall": [250, 400], "roof": [300, 250], "frame": [331, 372], "wall_in_shade": true},
     "overhang_m": 0.3, "not_modelled": "outside stair, gate"}

The roof is best READ, not declared: ``"profile": {"edge": 4, "px": [[1495, 110], [1660, 330]]}`` in a photo gives the
outline of the roof on a side wall (``edge``): the pixel of its PEAK and, if it can be seen, of the point where that
outline ENDS on the far side of the peak (where the roof meets the wall's far corner, or where it stops being this roof).
From these the tool takes the ridge height, decides between a gable (the outline comes down again) and one slope followed
by a flat roof (it stays up), sizes the house (a gable is symmetric about its ridge) and, when the footprint's wall is
longer than that, puts the pitched roof over the house alone. With a profile, ``roof``, ``gable_edge``, ``main_only`` and
``main_corner`` need not be given, and no ``r<edge>:top`` point is needed.
``"roof": "slope_then_flat"``: one slope rising from the low front wall, then a flat roof at its top over everything behind
(a house raised at the back). ``gable_edge`` is then the side wall on which that profile is seen, ``main_corner`` its end
at the front, and the point ``r<gable_edge>:top`` is where the slope ends and the flat begins. ``rest_height_m`` overrides
the height of the flat part (default: the top).
``"main_only": true`` (with ``gable_edge``): the footprint holds more than the house (lower extensions behind or beside it).
The pitched roof then covers only the part behind the gable wall, as wide as that wall; the rest is a flat-roofed block
``"rest_height_m"`` high (``roof_part`` and ``rest_height_m`` in overrides.yaml). ``"main_corner": 5`` names the end of
the gable edge that is the house's own outer corner: with a fitted ridge point the house is then taken as wide as twice
the corner's distance to the ridge, also when the gable wall runs on past the edge's other end.
A capture of ``photo_capture`` (the picture of a Street View tab with its camera): ``"capture": 0`` instead of ``photo``,
``view`` and ``cam_height``; the camera is then known: four points are enough (one of them at the ground).
Several photos: put ``photo``, ``view``, ``cam_height``, ``points`` and ``openings`` of each in a list ``"photos": [...]``;
a colour may name its photo as a third number (``[250, 400, 1]``). ``eave_m`` / ``top_m`` in the sheet override the fit.
A colour may also be given as ``"#rrggbb"``. ``"white": [x, y]`` is a white surface in the same light as the wall.
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


def solve(points, size: tuple[int, int], view: tuple[float, float], cam_height: float = 2.5, eave0: float = 3.0, top0: float = 5.5, pose: dict | None = None):
    """Least squares (Levenberg-Marquardt, numeric Jacobian) for the camera and the eave and ridge heights. ``points``
    are (xy, level, pixel). The viewpoint and the camera height are soft priors (3 m and 1 m); the focal length starts
    at a 60 degree lens and is held loosely. ``pose`` ({heading, pitch, fov} of a capture) is a known camera: heading
    and tilt are then held to a few degrees and the lens to the given one, so three points are enough. Returns (camera, eave or None, top or None, per-point error in px, ridge fraction or None: where along its edge, from the
    edge's first corner, the ridge meets the wall)."""
    w, h = size
    centre = np.mean([np.mean(xy, axis=0) if isinstance(xy[0], tuple) else xy for xy, _, _ in points], axis=0)
    yaw0 = math.degrees(math.atan2(centre[0] - view[0], centre[1] - view[1]))
    f0 = (w / 2) / math.tan(math.radians(30))
    pitch0 = 0.0
    if pose:
        yaw0 = yaw0 + (pose["heading"] - yaw0 + 180) % 360 - 180  # the same turn as the aim at the building, not 360 off
        pitch0, f0 = pose["pitch"], (w / 2) / math.tan(math.radians(pose["fov"] / 2))
    p0 = np.array([view[0], view[1], cam_height, yaw0, pitch0, f0, eave0, top0, 0.5])
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
            # 4 px per sigma. A capture's camera rides on a car's mast at a known height: holding it there keeps one
            # misplaced ground point from moving every height (it then shows as that point's own error)
            (p[0] - p0[0]) / 3.0 * 4, (p[1] - p0[1]) / 3.0 * 4, (p[2] - p0[2]) / (0.25 if pose else 1.0) * 4,
            (math.log(p[5] / f0)) / (0.08 if pose else 0.5) * 4,
            (p[3] - yaw0) / 4.0 * 4 if pose else 0.0, (p[4] - pitch0) / 4.0 * 4 if pose else 0.0,
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


def fit(photo: Path, ids: list[str], view, cam_path: Path, specs: list[str], cam_height: float, pose: dict | None = None) -> dict:
    b = _buildings(ids)[0]
    ring = b["footprint"]["exterior"]
    points = [_parse_point(s, ring) for s in specs]
    if len(points) < (3 if pose else 4):
        raise SystemExit("give at least 4 points (3 with a capture's known camera): corners at the ground and at the eave, and a ridge end")
    size = Image.open(photo).size
    cam, eave, top, errs, ridge_at = solve(points, size, _local(view), cam_height, pose=pose)
    for s, e in zip(specs, errs):
        print(f"  {s:28} off by {e:5.1f} px" + ("   <- re-read this point" if e > 15 * size[0] / 825 else ""))
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
    k = size[0] / 825  # the limits were set on an 825 px wide photo
    verdict = "good" if rms < 8 * k else "usable, heights to about 0.3 m" if rms < 15 * k else "BAD: re-read the points marked above (use `grid`), then fit again; do not use these numbers"
    print(f"fit error {rms:.1f} px: {verdict}")
    odd = moved > (6 if pose else 15) or not 0.8 < cam["z"] < 6 or (not pose and not 12 < fov < 110) or abs(cam["pitch"]) > 35
    if odd:
        print(f"WARNING ({photo.name}): the fitted camera is implausible ({moved:.1f} m from where it was taken, {cam['z']:.1f} m up); a point is on the wrong corner. Heights from this photo are doubtful")
    grounded = any(lvl == "ground" for _, lvl, _ in points)
    if not grounded:
        print("NO GROUND POINT: the heights hang on an assumed camera height and can be 0.5 m off. Add a corner at `ground` (where a wall meets the ground) and fit again")
    cam_path.parent.mkdir(parents=True, exist_ok=True)
    cam_path.write_text(json.dumps({**cam, "building": b["id"], "eave": eave, "top": top, "ridge_at": ridge_at, "rms_px": rms, "bad": rms >= 15 * k, "good": rms < 8 * k, "grounded": grounded, "odd": bool(odd)}, indent=1))
    return cam


def measure(cam_path: Path, edge: int, pixels: list[tuple[float, float]], beyond: bool = False) -> list[tuple[float, float]]:
    """Pixels put on the wall of one edge: (metres along the edge from its first corner, metres up). ``beyond``: the
    points may lie on the wall's plane past its ends (a roof outline running on over an extension)."""
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
        note = "" if (beyond or -0.3 <= s <= length + 0.3) and t > 0 else "   <- not on this wall (wrong edge or pixel?)"
        print(f"  pixel ({px:.0f}, {py:.0f}): {s:5.2f} m along edge {edge} (of {length:.2f}), {z:5.2f} m up{note}")
        out.append((s, z))
    if len(out) == 2 and not beyond:
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


def grid(photo: Path, out: Path, step: int | None = None, name: str = "grid.png", box: tuple[float, float, float, float] | None = None) -> Path:
    """The photo with a labelled grid, written 1400 px wide. ``box`` (x0, y0, x1, y1 in the photo's pixels) cuts it to
    that part first, so a building that fills a third of a wide picture is read three times as finely. The lines and
    labels are always in the photo's OWN pixels: what is read off the grid is right for the full photo."""
    src = Image.open(photo).convert("RGB")
    w, h = src.size
    x0, y0, x1, y1 = (0, 0, w, h) if box is None else (max(0, int(box[0])), max(0, int(box[1])), min(w, int(box[2])), min(h, int(box[3])))
    k = min(1400 / (x1 - x0), 3.0)
    img = src.crop((x0, y0, x1, y1)).resize((round((x1 - x0) * k), round((y1 - y0) * k)), Image.LANCZOS)
    step = step or next(s for s in (10, 20, 25, 50, 100, 200, 500) if s * k >= 42)
    d = ImageDraw.Draw(img, "RGBA")
    for x in range(-(-x0 // step) * step, x1, step):
        major = x % (2 * step) == 0
        d.line([((x - x0) * k, 0), ((x - x0) * k, img.size[1])], fill=(255, 255, 0, 150 if major else 70))
        if True:  # every line carries its number: nothing has to be worked out between two labels
            d.rectangle([(x - x0) * k + 1, 0, (x - x0) * k + 32, 11], fill=(0, 0, 0, 170))
            d.text(((x - x0) * k + 2, 0), str(x), fill=(255, 255, 0, 255))
    for y in range(-(-y0 // step) * step, y1, step):
        major = y % (2 * step) == 0
        d.line([(0, (y - y0) * k), (img.size[0], (y - y0) * k)], fill=(0, 255, 255, 150 if major else 70))
        if True:
            d.rectangle([0, (y - y0) * k + 1, 32, (y - y0) * k + 12], fill=(0, 0, 0, 170))
            d.text((1, (y - y0) * k + 1), str(y), fill=(0, 255, 255, 255))
    out.mkdir(parents=True, exist_ok=True)
    path = out / name
    img.save(path)
    part = "" if box is None else f", cut to x {x0}..{x1}, y {y0}..{y1}"
    print(f"{path}  (photo {w} x {h} px{part}; yellow lines are x, cyan lines are y, every {step} px OF THE PHOTO, each with its number)")
    return path


def building_box(c: dict, ring, margin: float = 0.12) -> tuple[float, float, float, float] | None:
    """Where a capture shows the building: the pixel box of the near half of its footprint from the ground to 9 m up,
    as the camera of the tab's address sees it, with a margin. None when that is not a useful cut."""
    cam = {"x": c["x"], "y": c["y"], "z": c.get("cam_height", 2.5), "yaw": c["heading"], "pitch": c["pitch"], "w": c["width"], "h": c["height"],
           "f": (c["width"] / 2) / math.tan(math.radians(horizontal_fov(c["fov"], c["width"], c["height"]) / 2))}  # fmt: skip
    # only the corners near the camera: a long building's far end would stretch the box over the whole picture
    near = sorted(ring, key=lambda p: math.dist(p, (c["x"], c["y"])))[: max(3, len(ring) // 2 + 1)]
    px = project(cam, np.array([[x, y, z] for x, y in near for z in (0.0, 9.0)]))
    px = px[np.isfinite(px).all(axis=1)]
    if len(px) < 2:
        return None
    x0, y0, x1, y1 = px[:, 0].min(), px[:, 1].min(), px[:, 0].max(), px[:, 1].max()
    mx, my = (x1 - x0) * margin + 40, (y1 - y0) * margin + 40
    x0, y0, x1, y1 = max(0, x0 - mx), max(0, y0 - my), min(c["width"], x1 + mx), min(c["height"], y1 + my)
    if x1 - x0 < 200 or y1 - y0 < 150 or (x1 - x0) > 0.85 * c["width"]:
        return None
    return (x0, y0, x1, y1)


def overlay(photo: Path, cam_path: Path, ids: list[str], model: str | None, out: Path) -> Path:
    from drycastle_pipeline.landmarks import build_landmark
    from drycastle_pipeline.meshes import Mesh

    bs = _buildings(ids)
    if model:
        bs = [{**b, "model": model} for b in bs]
    mesh = Mesh.concat([build_landmark(b, detail=True) for b in bs])
    path = overlay_mesh(photo, cam_path, mesh, out / "overlay.png")
    print(f"{path}  (photo with the model drawn half see-through over it; magenta is the model's outline)")
    return path


def horizontal_fov(vertical_deg: float, width: int, height: int) -> float:
    """The zoom of a Street View address (``75y``) is the VERTICAL field of view of the tab (checked on a capture of
    2532 x 1325 px: 64.1y, and the fit found a 101 degree lens across); this is the horizontal one."""
    return math.degrees(2 * math.atan(math.tan(math.radians(vertical_deg) / 2) * width / height))


def _from_capture(ph: dict, bid: str) -> dict:
    """The photo, viewpoint and camera of a sheet entry that names a capture (``"capture": 0``: the building's capture
    number, or a path to its ``.json``); nothing for an entry that gives ``photo`` and ``view`` itself."""
    if "capture" not in ph:
        return {}
    from drycastle_pipeline.photo_capture import PHOTOS_DIR

    side = Path(str(ph["capture"]))
    if not side.suffix:
        side = PHOTOS_DIR / bid / f"{ph['capture']}.json"
    if not side.exists():
        raise SystemExit(f"no capture {side}")
    c = json.loads(side.read_text())
    return {"photo": str(side.parent / c["photo"]), "view": [c["x"], c["y"]], "cam_height": c.get("cam_height", 2.5),
            "pose": {"heading": c["heading"], "pitch": c["pitch"], "fov": horizontal_fov(c["fov"], c["width"], c["height"])}}  # fmt: skip


def _derived(bid: str, ov: dict) -> dict:
    """The building as the rules stage would make it with these overrides (no ``make rules`` needed)."""
    from shapely.geometry import shape

    from drycastle_pipeline.buildings import derive_building, load_building_rules
    from drycastle_pipeline.landmark_tools import WORK

    b = _buildings([bid])[0]
    feats = json.loads((WORK / "normalised" / "buildings.geojson").read_text())
    feats = feats["features"] if isinstance(feats, dict) else feats
    f = next(f for f in feats if f["properties"]["osm_id"] == bid)
    return derive_building(bid, b["parcel"], shape(f["geometry"]), f["properties"].get("tags", {}), load_building_rules(), ov)


def _hex(rgb) -> str:
    return "#" + "".join(f"{int(max(0, min(255, round(v)))):02x}" for v in rgb)


def _upsert_override(path: Path, bid: str, comment: str, fields: dict, replace: bool) -> None:
    """Add the building's block at the end of overrides.yaml, or with ``replace`` swap the block it has (its ``  <id>:``
    line, the indented lines under it and the comment lines right above it)."""
    import fcntl
    import tempfile

    # several buildings may be written at once: one writer at a time (the lock file lives outside the repo)
    with open(Path(tempfile.gettempdir()) / "drycastle-overrides.lock", "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        _upsert_locked(path, bid, comment, fields, replace)


def _upsert_locked(path: Path, bid: str, comment: str, fields: dict, replace: bool) -> None:
    lines = path.read_text().splitlines()
    block = [f"  # {c}" for c in comment.splitlines()] + [f"  {bid}:"] + [
        f"    {k}: " + (f'"{v}"' if isinstance(v, str) and v.startswith("#") else str(v)) for k, v in fields.items()
    ]
    at = next((i for i, ln in enumerate(lines) if ln.rstrip() == f"  {bid}:"), None)
    if at is not None:
        if not replace:
            raise SystemExit(f"{bid} already has an entry in overrides.yaml: look at it, then pass --replace to overwrite it")
        end = at + 1
        while end < len(lines) and lines[end].startswith("    "):
            end += 1
        start = at
        while start > 0 and lines[start - 1].startswith("  # "):  # its own comment goes with it
            start -= 1
        lines[start:end] = block
    else:
        lines += ["", *block]
    path.write_text("\n".join(lines) + "\n")


def apply(sheet_path: Path, out: Path, replace: bool, dry: bool) -> None:
    """The whole measuring pass from one sheet (see the module docstring): fit every photo, measure the openings, sample
    the colours, write ``config/buildings/<id>.yaml`` and the building's entry in ``overrides.yaml``, and draw the
    result over each photo."""
    from drycastle_pipeline.building_detail import (
        DETAILS_DIR,
        DetailError,
        build_detail,
        parse_detail,
    )
    from drycastle_pipeline.config import PIPELINE_DIR
    from drycastle_pipeline.meshes import Mesh

    sheet = json.loads(sheet_path.read_text())
    bid = _buildings([sheet["building"]])[0]["id"]
    ring = _buildings([bid])[0]["footprint"]["exterior"]
    roof = sheet.get("roof", "gabled")
    if roof not in ("gabled", "flat", "hipped", "slope_then_flat"):
        raise SystemExit(f"roof '{roof}': gabled, flat, hipped or slope_then_flat. Anything else needs a hand-modelled landmark (LANDMARK.md)")
    profiled = any("profile" in ph for ph in (sheet.get("photos") or [sheet]))
    stf = roof == "slope_then_flat"
    if stf and not profiled and ("gable_edge" not in sheet or "main_corner" not in sheet):
        raise SystemExit("slope_then_flat needs gable_edge (the side wall on which the roof's profile is seen) and main_corner (the end of that edge at the low, sloped front)")
    photos = sheet.get("photos") or [sheet]
    cams, eaves, tops, ridges, openings, problems, profiles = [], [], [], [], [], [], []
    for n, ph in enumerate(photos):
        ph.update(_from_capture(ph, bid))
        photo = Path(ph["photo"])
        print(f"photo {n}: {photo.name}")
        cam_path = out / f"cam_{n}.json"
        fit(photo, [bid], tuple(ph["view"]), cam_path, ph["points"], float(ph.get("cam_height", 2.5)), ph.get("pose"))
        meta = json.loads(cam_path.read_text())
        cams.append((photo, cam_path, meta["rms_px"], meta["bad"]))
        if meta["bad"]:
            problems.append(f"photo {n}: fit error {meta['rms_px']:.0f} px, its numbers are not used")
            continue
        if not meta.get("grounded", True):
            problems.append(f"photo {n}: no ground point, heights may be 0.5 m off")
        if meta["eave"] is not None:
            # the photo with the most points is trusted first: three points fit any camera, and say little
            eaves.append((-len(ph["points"]), meta["rms_px"], meta["eave"]))
        if meta["top"] is not None:
            tops.append((meta["rms_px"], meta["top"]))
        if meta.get("ridge_at") is not None and any(pt.startswith(f"r{sheet.get('gable_edge')}:") for pt in ph["points"]):
            ridges.append((meta["rms_px"], meta["ridge_at"]))
        if ph.get("profile"):  # the roof's outline on a side wall: its peak and, when it can be seen, where it ends
            print(f"  roof profile on edge {ph['profile']['edge']}:")
            profiles.append((meta["rms_px"], int(ph["profile"]["edge"]), measure(cam_path, int(ph["profile"]["edge"]), [tuple(p) for p in ph["profile"]["px"]], beyond=True), meta["eave"]))
        for o in ph.get("openings", []):
            (s0, z0), (s1, z1) = measure(cam_path, int(o["edge"]), [tuple(p) for p in o["px"]])
            length = math.dist(ring[o["edge"]], ring[(o["edge"] + 1) % len(ring)])
            a, w = min(s0, s1), abs(s1 - s0)
            sill, h = (0.0, max(z0, z1)) if o["kind"] != "window" and min(z0, z1) < 0.5 else (min(z0, z1), abs(z1 - z0))
            if a < 0.1 or a + w > length - 0.1 or w < 0.3 or h < 0.3:
                problems.append(f"opening on edge {o['edge']} at px {o['px']} does not fit the wall ({a:.2f} + {w:.2f} of {length:.2f} m): left out")
                continue
            k = sum(1 for x in openings if x["kind"] == o["kind"]) + 1
            openings.append({
                "id": f"{o['kind'][0]}{k}", "kind": o["kind"], "edge": int(o["edge"]), "at_m": round(a, 2), "width_m": round(w, 2),
                "height_m": round(h, 2), "sill_m": round(max(0.0, sill), 2), "recess_m": 0.12,
                **({"colour": o["colour"]} if o.get("colour") else {}),
                "confidence": "medium" if meta["good"] else "low",
            })  # fmt: skip
    eave = float(sheet["eave_m"]) if sheet.get("eave_m") else (min(eaves)[2] if eaves else None)
    if eave is not None and not sheet.get("eave_m") and len(eaves) > 1:
        vals = [e for *_, e in eaves]
        if max(vals) - min(vals) > 0.3:  # no photo is plainly the better one: the middle of what they say
            eave = sum(vals) / len(vals)
    if profiles and eave is not None and roof in ("gabled", "slope_then_flat"):
        # The roof is read off its outline on a side wall, not declared: the peak says how high and how far in the ridge
        # is, the end says whether the roof comes down again (a gable) or stays up (one slope, then flat). A gable is
        # taken as symmetric about its ridge, so the peak's distance from the house's corner also gives the house's
        # width: when that is less than the footprint's wall, the roof covers only that part of the footprint.
        _, pe, pts, own_eave = min(profiles)
        length = math.dist(ring[pe], ring[(pe + 1) % len(ring)])
        lift = eave - own_eave if own_eave is not None else 0.0  # heights of this photo, hung on the eave that was chosen
        pts = [(s_, z_ + lift) for s_, z_ in pts]
        s_p, z_p = pts[0]
        first = s_p <= length / 2  # the house's corner is the end of the edge the peak is nearer to
        d_p = s_p if first else length - s_p
        end = pts[1] if len(pts) > 1 else None
        d_e = None if end is None else (end[0] if first else length - end[0])
        stays_up = end is not None and end[1] > eave + 0.6 * (z_p - eave) and d_e > d_p + 0.5
        width = 2 * d_p
        rise = z_p - eave
        if not stays_up and (width < 4.0 or width > length + 1.0 or rise < 0.6 or rise / max(d_p, 0.1) > 1.8):
            # no house is 2.7 m wide, and no roof is steeper than 60 degrees: the peak was not the ridge (a half-hip, a
            # misread pixel). Keep what is likely: the roof spans the whole wall, the ridge over its middle, at a
            # height a 38 degree roof would reach unless the measured one is believable for that span
            was = f"ridge {d_p:.1f} m in at {z_p:.2f} m (a {width:.1f} m house, rise {rise:.1f} m)"
            d_p, width, first = length / 2, length, True
            s_p = length / 2
            if not 0.25 * d_p <= rise <= 1.2 * d_p:
                z_p = eave + 0.78 * d_p
            problems.append(f"the roof outline read as {was} is not believable: built as a gable over the whole {length:.1f} m wall, ridge at {z_p:.2f} m. Check the roof")
        if stays_up:
            roof, stf = "slope_then_flat", True
            print(f"roof read from the profile: one slope {d_p:.2f} m deep up to {z_p:.2f} m, then flat (its far end is still {end[1]:.2f} m up)")
        else:
            roof, stf = "gabled", False
            if d_e is not None and abs(d_e - width) > 0.2 * width + 0.3:
                problems.append(f"the roof's two slopes differ (peak {d_p:.1f} m in, far end {d_e:.1f} m in): built symmetric about the ridge, {width:.1f} m wide")
            print(f"roof read from the profile: gable, ridge {d_p:.2f} m in at {z_p:.2f} m, house {width:.2f} m wide on a {length:.2f} m wall")
        sheet.update({"gable_edge": pe, "main_corner": pe if first else (pe + 1) % len(ring)})
        if not stays_up and width < length - 0.6:
            sheet["main_only"] = True
            print(f"  the footprint's wall is {length - width:.1f} m longer than the house: the pitched roof covers the house only")
        tops, ridges = [(0.0, z_p)], [(0.0, s_p / length)]
    top = float(sheet["top_m"]) if sheet.get("top_m") else (min(tops)[1] if tops else None)
    if eave is None:
        raise SystemExit("no usable eave height: no photo fitted. Re-read the points on grid.png, or give eave_m (and top_m) in the sheet")
    if roof != "flat" and top is None:
        # a guessed ridge has built roofs twice the height of the real one: without a measured ridge the building gets its
        # walls and openings under a flat roof, and says so
        problems.append(f"NO RIDGE MEASURED (no r<edge>:top point in a photo that fitted): written with a FLAT roof at the eave instead of a {roof} one. Needs a capture that shows the gable")
        roof, stf, top = "flat", False, eave
    ov = {"roof": "lean_to" if stf else roof, "height_m": round(top if roof != "flat" else eave, 2), "levels": 1 if eave < 4.2 else 2}
    if roof != "flat":
        ov["roof_height_m"] = round(top - eave, 2)
    if roof == "gabled" and "gable_edge" in sheet:
        p, q = ring[sheet["gable_edge"]], ring[(sheet["gable_edge"] + 1) % len(ring)]
        ov["ridge_dir"] = round((math.atan2(q[1] - p[1], q[0] - p[0]) + math.pi / 2) % math.pi, 4)
    if stf:
        # one slope from the low front wall up to the roof's top, flat from there back: the footprint is cut along the
        # line where the slope ends; the front strip gets the slope, everything behind it is a flat block as high as the top
        from shapely.geometry import Polygon

        n_r, ge, corner = len(ring), sheet["gable_edge"], sheet["main_corner"]
        if corner not in (ge, (ge + 1) % n_r):
            raise SystemExit(f"main_corner {corner} is not an end of gable_edge {ge}")
        p, q = np.array(ring[ge]), np.array(ring[(ge + 1) % n_r])
        length = float(np.linalg.norm(q - p))
        if not ridges:
            raise SystemExit(f"slope_then_flat needs the point where the slope ends on the side wall: r{ge}:top:X,Y in a photo that fits")
        depth = min(ridges)[1] * length if corner == ge else (1 - min(ridges)[1]) * length  # from the front corner to where the slope ends
        # the front wall is the other edge at main_corner; inward is on its left (the ring is counter-clockwise)
        fe = (ge - 1) % n_r if corner == ge else (ge + 1) % n_r
        a, b2 = np.array(ring[fe]), np.array(ring[(fe + 1) % n_r])
        u = (b2 - a) / np.linalg.norm(b2 - a)
        n_in = np.array([-u[1], u[0]])
        strip = Polygon([a - u * 60 - n_in, b2 + u * 60 - n_in, b2 + u * 60 + n_in * depth, a - u * 60 + n_in * depth])
        part = Polygon(ring).intersection(strip)
        part = max(getattr(part, "geoms", [part]), key=lambda g: g.area)
        ov["roof_part"] = [[round(x, 3), round(y, 3)] for x, y in part.exterior.coords[:-1]]
        ov["rest_height_m"] = round(float(sheet.get("rest_height_m", top)), 2)
        ov["ridge_dir"] = round(math.atan2(u[1], u[0]) % (2 * math.pi), 4)  # the high edge on the left: inside
        print(f"slope: {depth:.2f} m deep from the front wall (edge {fe}), then flat at {ov['rest_height_m']:.2f} m")
    if sheet.get("main_only") and not stf:
        # the pitched roof covers only the house behind the gable wall: the strip of the footprint as wide as that wall
        from shapely.geometry import Polygon

        if roof != "gabled" or "gable_edge" not in sheet:
            raise SystemExit("main_only needs a gabled roof and gable_edge: the gable wall of the main house")
        ge = sheet["gable_edge"]
        p, q = np.array(ring[ge]), np.array(ring[(ge + 1) % len(ring)])
        length = float(np.linalg.norm(q - p))
        u = (q - p) / length
        n_in = np.array([-u[1], u[0]])
        s0, s1 = 0.0, length  # the house's gable wall along the edge, from its first corner
        corner = sheet.get("main_corner")
        if corner is not None and ridges:
            # the house's own corner on the gable wall is known and the ridge was measured: the ridge of a gable is over
            # its middle, so the wall reaches as far past the ridge as the corner is before it (also past the edge's end)
            at = min(ridges)[1] * length
            if corner == ge:
                s0, s1 = 0.0, 2 * at
            elif corner == (ge + 1) % len(ring):
                s0, s1 = length - 2 * (length - at), length
            else:
                raise SystemExit(f"main_corner {corner} is not an end of gable_edge {ge}")
        elif corner is not None:
            problems.append("main_corner given but no ridge point (r<edge>:top) fitted: the house is taken as wide as the gable edge")
        a, b2 = p + u * (s0 - 0.05), p + u * (s1 + 0.05)
        strip = Polygon([a - n_in, b2 - n_in, b2 + n_in * 300, a + n_in * 300])
        part = Polygon(ring).intersection(strip)
        part = max(getattr(part, "geoms", [part]), key=lambda g: g.area)
        ov["roof_part"] = [[round(x, 3), round(y, 3)] for x, y in part.exterior.coords[:-1]]
        ov["rest_height_m"] = float(sheet.get("rest_height_m", 2.8))
        if "rest_height_m" not in sheet:
            problems.append("height of the lower parts not measured: guessed 2.8 m (give rest_height_m)")
    if len(eaves) > 1 and max(e for *_, e in eaves) - min(e for *_, e in eaves) > 0.3:
        problems.append("the photos disagree on the eave: " + ", ".join(f"{e:.2f} m" for *_, e in eaves) + f"; used their mean, {eave:.2f} m. A ground point is probably off: re-read the wall's foot at the near corner in each")
    col = sheet.get("colours", {})
    first = Path(photos[0]["photo"])

    def colour(key, lift=1.0):
        spec = col.get(key)
        if spec is None:
            return None
        if isinstance(spec, str):
            return spec
        src = Path(photos[int(spec[2])]["photo"]) if len(spec) > 2 else first
        rgb = np.array(sample(src, (spec[0], spec[1]), tuple(col["white"]) if col.get("white") else None), float)
        if rgb[2] > rgb[0] + 25 and rgb[2] > rgb[1] + 8 and rgb.mean() > 110:
            if key not in skyish:
                skyish.append(key)
                problems.append(f"the {key} colour pixel {spec[:2]} is on the sky, not on the {key}: the generated colour was kept. Pick a pixel well inside the surface")
            return None
        return _hex(rgb * lift)

    skyish: list[str] = []
    shade = 1.4 if col.get("wall_in_shade") and not col.get("white") else 1.0
    if colour("wall", shade):
        ov["wall_colour"] = colour("wall", shade)
    if colour("roof"):
        ov["roof_colour"] = colour("roof")
    detail = {
        "id": bid, "source": sheet.get("source", "photo"), "overhang_m": float(sheet.get("overhang_m", 0.3)),
        **({"frame_colour": colour("frame", shade)} if colour("frame", shade) else {}), "openings": openings,
    }  # fmt: skip
    b = _derived(bid, ov)
    while True:  # an opening the generated shell cannot hold (above its roof line, across a corner) is left out and reported
        try:
            mesh, tags = build_detail(b, parse_detail(detail, bid))
            break
        except DetailError as e:
            bad = next((o for o in openings if f"opening {o['id']}:" in str(e)), None)
            if bad is None:
                raise SystemExit(f"the detail does not build: {e}") from None
            openings.remove(bad)
            problems.append(f"{bad['kind']} on edge {bad['edge']} at {bad['at_m']} m, {bad['sill_m']} m up, left out: {str(e).split(': ', 1)[-1]}")
    full = Mesh.concat([mesh, *(t.mesh for t in tags)])
    for n, (photo, cam_path, _, bad) in enumerate(cams):
        if not bad:
            overlay_mesh(photo, cam_path, full, out / f"overlay_{n}.png")
    fit_note = ", ".join(f"photo {n} {rms:.0f} px" for n, (_, _, rms, _) in enumerate(cams))
    lost = sheet.get("not_modelled", "")
    if not dry:
        import yaml

        head = [f"# {b['parcel']}: {sheet.get('what', 'house')}. Measured by landmark_photo (camera fit: {fit_note}).",
                f"# Eave {eave:.2f} m, top {b['height_m']:.2f} m. Sides with no photo have no openings." + (f" Not modelled: {lost}." if lost else "")]  # fmt: skip
        body = yaml.safe_dump({k: v for k, v in detail.items() if k != "openings"}, sort_keys=False, default_flow_style=False)
        rows = "".join("  - {" + ", ".join(f"{k}: " + (f'"{v}"' if isinstance(v, str) and v.startswith("#") else str(v)) for k, v in o.items()) + "}\n" for o in openings)
        (DETAILS_DIR / f"{bid}.yaml").write_text("\n".join(head) + "\n" + body + ("openings:\n" + rows if openings else "openings: []\n"))
        _upsert_override(PIPELINE_DIR / "config" / "overrides.yaml", bid, f"{b['parcel']}: {sheet.get('what', 'house')}, from {sheet.get('source', 'a photo')}; openings in config/buildings/.", ov, replace)
    print(f"\n{bid}: {roof} roof, eave {eave:.2f} m, top {b['height_m']:.2f} m, {len(openings)} opening(s), colours {ov.get('wall_colour')} / {ov.get('roof_colour')}")
    for line in problems:
        print(f"PROBLEM: {line}")
    print("dry run: nothing written" if dry else f"written: config/buildings/{bid}.yaml and its entry in config/overrides.yaml")
    print(f"look at {out}/overlay_0.png: the model's outline (magenta) and windows must sit on the photo's")


def overlay_mesh(photo: Path, cam_path: Path, mesh, path: Path) -> Path:
    """The mesh as the fitted camera sees it, half see-through over the photo, its outline in magenta."""
    cam = json.loads(cam_path.read_text())
    base = Image.open(photo).convert("RGB")
    yaw, pitch = math.radians(cam["yaw"]), math.radians(cam["pitch"])
    eye = np.array([cam["x"], cam["y"], cam["z"]])
    target = eye + [math.sin(yaw) * math.cos(pitch), math.cos(yaw) * math.cos(pitch), math.sin(pitch)]
    fov = math.degrees(2 * math.atan(cam["w"] / 2 / cam["f"]))
    render, mask = _raster(mesh.tris, mesh.colours.astype(float), eye, target, fov, base.size, with_mask=True)
    a, b = np.asarray(base, float), np.asarray(render, float)
    mixed = np.where(mask[..., None], a * 0.5 + b * 0.5, a)
    rim = mask ^ (np.roll(mask, 1, 0) & np.roll(mask, -1, 0) & np.roll(mask, 1, 1) & np.roll(mask, -1, 1) & mask)
    mixed[rim] = (255, 0, 255)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(mixed.astype(np.uint8)).save(path)
    return path


def prep(ids: list[str], out: Path, photo: Path | None = None, view=None, max_captures: int = 2) -> None:
    """The first look in one call: the footprint with what each photo's viewpoint sees, and each photo with a grid.
    Without ``photo`` it takes the building's newest captures (``photo_capture``, the last ``max_captures``):
    ``grid_<n>.png`` for capture n."""
    from drycastle_pipeline.landmark_tools import _edges, _seen, footprint
    from drycastle_pipeline.photo_capture import PHOTOS_DIR

    if photo is not None:
        footprint(ids, view)
        grid(photo, out)
        return
    bid = _buildings(ids)[0]["id"]
    sides = sorted((PHOTOS_DIR / bid).glob("*.json"), key=lambda p: int(p.stem))
    if not sides:
        raise SystemExit(f"{bid} has no captures in {PHOTOS_DIR / bid}; give a photo: prep {bid} --photo FILE [--view X,Y]")
    footprint([bid])
    for side in sides[-max_captures:]:
        c = json.loads(side.read_text())
        seen = [f"edge {e['i']}{_seen(e, ((e['p'][0] + e['q'][0]) / 2, (e['p'][1] + e['q'][1]) / 2), (c['x'], c['y']))[7:]}" for e in _edges(_buildings([bid])[0])]
        print(f"\ncapture {side.stem}: {c['width']} x {c['height']} px, camera at ({c['x']}, {c['y']}) looking {c['heading']:.0f} deg")
        print("  " + "\n  ".join(v for v in seen if "HIDDEN" not in v))
        grid(side.parent / c["photo"], out, name=f"grid_{side.stem}.png", box=building_box(c, _buildings([bid])[0]["footprint"]["exterior"]))


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
    pr = sub.add_parser("prep")
    pr.add_argument("ids", nargs="+")
    pr.add_argument("--photo", type=Path, help="a photo of your own; without it the building's captures are used")
    pr.add_argument("--view", type=_xy)
    pr.add_argument("--out", type=Path, required=True)
    apl = sub.add_parser("apply")
    apl.add_argument("sheet", type=Path)
    apl.add_argument("--out", type=Path, required=True)
    apl.add_argument("--replace", action="store_true", help="overwrite the building's existing entry in overrides.yaml")
    apl.add_argument("--dry", action="store_true", help="fit and draw, write nothing into config/")
    a = ap.parse_args(argv)
    if a.cmd == "prep":
        prep(a.ids, a.out, a.photo, a.view)
    elif a.cmd == "apply":
        apply(a.sheet, a.out, a.replace, a.dry)
    elif a.cmd == "grid":
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
