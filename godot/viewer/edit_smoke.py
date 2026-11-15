#!/usr/bin/env python3
"""Hot reload smoke test (`make viewer-edit-smoke`).

Exports a fresh map into a scratch directory (a copy of data/village/work plus a small objects.json, so
nothing under data/ or out/ is touched), prepares a series of edits files, starts the viewer headless
with `--watch --edits <scratch edits.json>` and lets the viewer drive and assert the run
(`--edit-smoke`, see ViewerEditSmoke.cs). Exits with the viewer's exit code.

    python3 godot/viewer/edit_smoke.py [--dir DIR] [--godot PATH]
"""

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PIPELINE = REPO / "pipeline"


def uv(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["uv", "run", "python", "-m", "drycastle_pipeline", *args],
        cwd=PIPELINE, capture_output=True, text=True,
    )


def inside(poly: list, x: float, y: float) -> bool:
    """Point in a GeoJSON polygon (outer ring minus holes), even-odd."""
    n = 0
    for ring in poly:
        j = len(ring) - 1
        for i in range(len(ring)):
            xi, yi = ring[i][:2]
            xj, yj = ring[j][:2]
            if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                n += 1
            j = i
    return n % 2 == 1


def interior_points(parcel: dict, step: float = 1.0, margin: float = 0.0) -> list[tuple[float, float]]:
    poly = parcel["polygon"]["coordinates"]
    xs = [p[0] for p in poly[0]]
    ys = [p[1] for p in poly[0]]
    pts = []
    x = min(xs)
    while x <= max(xs):
        y = min(ys)
        while y <= max(ys):
            ok = all(inside(poly, x + dx, y + dy)
                     for dx, dy in ((0, 0), (margin, 0), (-margin, 0), (0, margin), (0, -margin)))
            if ok:
                pts.append((round(x, 2), round(y, 2)))
            y += step
        x += step
    return pts


def find_fenced_parcel(parcels: list[dict], scratch: Path, edits: Path) -> tuple[dict, dict]:
    """First building plot whose parcel-run result has a fence section of at least 2 m."""
    for p in sorted(parcels, key=lambda p: p["id"]):
        if p["kind"] != "building_plot":
            continue
        out = scratch / "probe.json"
        r = uv("parcel-run", "--parcel", p["id"], "--edits", str(edits), "--out", str(out))
        if r.returncode != 0:
            continue
        fragment = json.loads(out.read_text())
        sections = [s for s in fragment["fences"]
                    if ((s["a"][0] - s["b"][0]) ** 2 + (s["a"][1] - s["b"][1]) ** 2) ** 0.5 >= 2.0]
        if sections:
            return p, max(sections, key=lambda s: (s["a"][0] - s["b"][0]) ** 2 + (s["a"][1] - s["b"][1]) ** 2)
    sys.exit("edit smoke: no parcel with a fence found in the map")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-edit-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"edit smoke: scratch directory {scratch}")

    work = scratch / "work"
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(REPO / "data" / "village" / "work", work)
    parcels_doc = json.loads((work / "parcels.json").read_text())
    origin = parcels_doc["origin_epsg5514"]
    parcels = parcels_doc["parcels"]

    edits = scratch / "edits.json"
    empty = {"version": 1, "parcels": {}, "groups": {}}
    edits.write_text(json.dumps(empty))

    fenced, section = find_fenced_parcel(parcels, scratch, edits)
    fid = fenced["id"]
    (ax, ay), (bx, by) = section["a"], section["b"]
    length = ((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5
    probe = {
        "x": (ax + bx) / 2, "y": (ay + by) / 2,
        "nx": -(by - ay) / length, "ny": (bx - ax) / length,
        "z": (section["za"] + section["zb"]) / 2,
    }

    pts = interior_points(fenced, margin=1.5) or interior_points(fenced)
    if len(pts) < 2:
        sys.exit(f"edit smoke: parcel {fid} has no interior room for an object")
    p1 = pts[len(pts) // 3]
    def d2(q: tuple[float, float]) -> float:
        return (q[0] - p1[0]) ** 2 + (q[1] - p1[1]) ** 2

    far = [q for q in pts if d2(q) >= 2.0**2]
    p2 = min(far, key=d2) if far else max(pts, key=d2)

    other = next(p for p in sorted(parcels, key=lambda p: p["id"])
                 if p["id"] != fid and p["kind"] in ("building_plot", "garden") and len(interior_points(p)) >= 2)
    q = interior_points(other)
    baked = [
        {"id": "baked-slab", "asset": "prim:slab", "position": list(q[0]), "z": None, "rotation_deg": 0.0,
         "scale": 2.0, "colour": "#334455", "parcel": other["id"], "owner": other["id"], "chunk": other["chunk"]},
        {"id": "baked-missing", "asset": "pack:missing/nothing", "position": list(q[-1]), "z": None,
         "rotation_deg": 45.0, "scale": 1.0, "colour": None, "parcel": other["id"], "owner": other["id"],
         "chunk": other["chunk"]},
    ]
    (work / "objects.json").write_text(json.dumps({"format_version": 1, "objects": baked}, indent=1))

    r = uv("export", "--out", str(scratch / "map"), "--work", str(work))
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
        return 1

    def absolute(pt: tuple[float, float]) -> list[float]:
        return [round(pt[0] + origin[0], 3), round(pt[1] + origin[1], 3)]

    def step(obj_pos: tuple[float, float] | None) -> dict:
        entry: dict = {"fence": {"mode": "none"}}
        if obj_pos:
            entry["objects"] = [{"id": "smoke-sedan", "asset": "pack:kenney-car-kit/sedan",
                                 "position": absolute(obj_pos), "rotation_deg": 30}]
        return {"version": 1, "parcels": {fid: entry}, "groups": {}}

    steps = scratch / "steps"
    steps.mkdir(exist_ok=True)
    files = {
        "1": json.dumps(step(p1)),
        "2": json.dumps(step(p2)),
        "broken": '{"version": 1, "parcels": {"' + fid + '": {"fence": ',
        "3": json.dumps(step(None)),
        "4": json.dumps(empty),
    }
    for name, text in files.items():
        (steps / f"{name}.json").write_text(text)
    plan = {
        "fence_parcel": fid, "probe": probe, "object_id": "smoke-sedan",
        "object_local": list(p1), "object_local_2": list(p2),
        "baked_object_ids": ["baked-slab", "baked-missing"],
        "steps": {name: str(steps / f"{name}.json") for name in files},
    }
    (scratch / "plan.json").write_text(json.dumps(plan, indent=1))

    build = subprocess.run(["dotnet", "build", str(REPO / "godot/viewer/viewer.csproj")],
                           capture_output=True, text=True)
    if build.returncode != 0:
        print(build.stdout[-2000:])
        return 1
    cmd = [args.godot, "--headless", "--path", str(REPO / "godot/viewer"), "--",
           "--map", str(scratch / "map"), "--seed", args.seed,
           "--edits", str(edits), "--state", str(scratch / "editor-state.json"),
           "--edit-smoke", "--edit-smoke-plan", str(scratch / "plan.json")]
    return subprocess.run(cmd).returncode


if __name__ == "__main__":
    sys.exit(main())
