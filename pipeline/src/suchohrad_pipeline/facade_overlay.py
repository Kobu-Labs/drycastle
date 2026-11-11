"""Measurement overlay: draw a building's wall edges into a Street View frame (reference only).

Projects the OSM footprint (wall base at ground, eave line, ridge height) into a downloaded frame
with the camera known from ``index.json`` (pano position, heading, fov, pitch), and draws edge
numbers and 1 m ticks along each wall, so window and door positions can be read off the photo in
facade coordinates (edge number + metres from the edge start).

    python -m suchohrad_pipeline.facade_overlay w378920362 0                 # frame 0 -> overlay_0.png
    python -m suchohrad_pipeline.facade_overlay w378920362 0 village E N H   # calibrate: shift camera
                                                                             # E/N metres, heading H degrees

Edge numbers are those of ``orient(footprint, 1.0)`` in buildings.json, the ones used by
``config/buildings/<id>.yaml`` (see building_detail). The OSM ring is registered onto that footprint.

Assumes flat ground at the building and a camera 2.5 m above it (the Street View car).
"""

import json
import math
import sys

import numpy as np
from PIL import Image, ImageDraw
from shapely.geometry.polygon import orient

from suchohrad_pipeline.buildings import footprint_polygon
from suchohrad_pipeline.config import area_paths
from suchohrad_pipeline.streetview import M_PER_DEG

CAMERA_H = 2.5
SIZE = 640


def project(pt, cam, heading_deg, pitch_deg, fov_deg):
    """pt/cam: (east_m, north_m, up_m). Returns pixel (x, y) or None when behind the camera."""
    dx, dy, dz = pt[0] - cam[0], pt[1] - cam[1], pt[2] - cam[2]
    yaw = math.radians(heading_deg)
    right = dx * math.cos(yaw) - dy * math.sin(yaw)
    fwd = dx * math.sin(yaw) + dy * math.cos(yaw)
    pit = math.radians(pitch_deg)
    depth = fwd * math.cos(pit) + dz * math.sin(pit)
    up = -fwd * math.sin(pit) + dz * math.cos(pit)
    if depth <= 0.1:
        return None
    f = (SIZE / 2) / math.tan(math.radians(fov_deg) / 2)
    return SIZE / 2 + f * right / depth, SIZE / 2 - f * up / depth


def local_xy(lon, lat, lon0, lat0):
    return ((lon - lon0) * M_PER_DEG * math.cos(math.radians(lat0)), (lat - lat0) * M_PER_DEG)


def register(src, dst):
    """Rotation + translation taking ring ``src`` onto ring ``dst`` (any start vertex, either order)."""
    best = None
    for pts in (src, src[::-1]):
        for k in range(len(pts)):
            a = np.array(pts[k:] + pts[:k], float)
            b = np.array(dst, float)
            if len(a) != len(b):
                continue
            ca, cb = a.mean(0), b.mean(0)
            h = (a - ca).T @ (b - cb)
            u, _, vt = np.linalg.svd(h)
            r = vt.T @ u.T
            if np.linalg.det(r) < 0:
                continue
            res = float(np.sqrt(((a - ca) @ r.T + cb - b) ** 2).mean()) if False else float(
                np.sqrt((((a - ca) @ r.T + cb - b) ** 2).sum(1).mean())
            )
            if best is None or res < best[2]:
                best = (r, cb - ca @ r.T, res, ca)
    if best is None:
        raise SystemExit("cannot register the OSM ring with buildings.json (vertex count differs)")
    r, _, res, ca = best
    cb = np.array(dst, float).mean(0)
    return r, (cb, ca), res


def apply(rot, shift, pt):
    if isinstance(shift, tuple) and len(shift) == 2 and hasattr(shift[0], "__len__"):
        cb, ca = shift
        v = rot @ (np.array(pt, float) - ca) + cb
    else:
        v = rot @ np.array(pt, float) + np.array(shift, float)
    return float(v[0]), float(v[1])


def main(argv=None) -> int:
    args = list(argv if argv is not None else sys.argv[1:])
    bid, n = args[0], int(args[1])
    area = args[2] if len(args) > 2 else "village"
    paths = area_paths(area)
    folder = paths.raw.parent / "streetview"
    view = json.loads((folder / "index.json").read_text())[bid][n]
    osm = json.loads((paths.raw / "osm.json").read_text())
    way = next(e for e in osm["elements"] if e["type"] == "way" and f"w{e['id']}" == bid)
    b = next(x for x in json.loads((paths.work / "buildings.json").read_text())["buildings"] if x["id"] == bid)
    eave = float(b["eave_height_m"])

    lon0, lat0 = view["location"]["lng"], view["location"]["lat"]
    osm_ring = [local_xy(p["lon"], p["lat"], lon0, lat0) for p in way["geometry"]]
    if osm_ring[0] == osm_ring[-1]:
        osm_ring = osm_ring[:-1]
    ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
    rot, shift, resid = register(osm_ring, ring)
    cam_xy = apply(rot, shift, (0.0, 0.0))
    cam = (cam_xy[0], cam_xy[1], CAMERA_H)
    hx, hy = apply(rot, (0, 0), (math.sin(math.radians(view["heading"])), math.cos(math.radians(view["heading"]))))
    heading = math.degrees(math.atan2(hx, hy)) % 360
    off_e, off_n, off_h = (float(v) for v in (args[3:6] + ["0", "0", "0"])[:3])  # calibration
    cam = (cam[0] + off_e, cam[1] + off_n, cam[2])
    heading += off_h
    print(f"registration residual {resid:.2f} m, local heading {heading:.1f}")

    def px(x, y, z):
        return project((x, y, z), cam, heading, view["pitch"], view["fov"])

    img = Image.open(folder / view["file"]).convert("RGB")
    d = ImageDraw.Draw(img, "RGBA")
    xs, ys = [], []
    for i, (a, c) in enumerate(zip(ring, ring[1:] + ring[:1])):
        length = math.dist(a, c)
        nx, ny = (c[1] - a[1]) / length, -(c[0] - a[0]) / length  # outward normal (ccw ring)
        mx, my = (a[0] + c[0]) / 2, (a[1] + c[1]) / 2
        facing = nx * (cam[0] - mx) + ny * (cam[1] - my) > 0
        width = 3 if facing else 1
        for z, col in ((0.0, (255, 40, 40)), (eave, (40, 200, 255))):
            pa, pc = px(*a, z), px(*c, z)
            if pa and pc:
                d.line([pa, pc], fill=(*col, 255 if facing else 90), width=width)
                if facing:
                    xs += [pa[0], pc[0]]
                    ys += [pa[1], pc[1]]
        if not facing:
            continue
        for k in range(int(length) + 1):  # 1 m ticks along the wall base, 5 m ticks bigger
            t = k / length
            p = px(a[0] + (c[0] - a[0]) * t, a[1] + (c[1] - a[1]) * t, 0.0)
            if p:
                r = 4 if k % 5 == 0 else 2
                d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=(255, 255, 255, 255))
        pa0, pa1 = px(*a, 0.0), px(*a, eave)
        if pa0 and pa1:
            d.line([pa0, pa1], fill=(255, 255, 0, 255), width=2)  # edge start corner
        mid = px(mx, my, eave * 0.6)
        if mid:
            d.text((mid[0] - 8, mid[1]), f"E{i} ({length:.1f}m)", fill=(255, 255, 0, 255))
    out = folder / bid / f"overlay_{n}.png"
    img.save(out)
    if xs:
        box = (max(0, min(xs) - 40), max(0, min(ys) - 80), min(SIZE, max(xs) + 40), min(SIZE, max(ys) + 40))
        crop = img.crop(tuple(int(v) for v in box))
        crop.resize((crop.width * 3, crop.height * 3), Image.LANCZOS).save(
            folder / bid / f"overlay_{n}_crop.png"
        )
    print(out, "edges:", [(i, round(math.dist(a, c), 1)) for i, (a, c) in enumerate(zip(ring, ring[1:] + ring[:1]))])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
