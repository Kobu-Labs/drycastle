#!/usr/bin/env python3
"""Editor mode smoke test (`make viewer-editor-smoke`).

Exports a fresh slice map into a scratch directory (a copy of data/work, so nothing under data/ or out/
is touched), seeds a scratch edits.json (an existing group, a stale parcel entry), starts the viewer
headless with `--editor-smoke` (see ViewerEditorSmoke.cs: drives the editor APIs, checks the saved files,
outlines, editor-state.json and the walk-mode round trip) and then checks the files the viewer saved
against the pipeline's own writer (`edits.dump_text`).

    python3 godot/viewer/editor_smoke.py [--dir DIR] [--godot PATH]
"""

import argparse
import json
import math
import random
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PIPELINE = REPO / "pipeline"
FIXTURE = PIPELINE / "tests" / "fixtures" / "edits_roundtrip.json"


def uv(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["uv", "run", "python", *args], cwd=PIPELINE, capture_output=True, text=True,
    )


def q(c) -> tuple[int, int]:
    return (round(c[0] * 100), round(c[1] * 100))


def edges(parcel: dict):
    for ring in parcel["polygon"]["coordinates"]:
        for a, b in zip(ring, ring[1:] + ring[:1], strict=False):
            if q(a) != q(b):
                yield tuple(sorted((q(a), q(b))))


def perimeter(parcels: list[dict]) -> int:
    """Edges that appear once among the parcels' edges (shared boundaries cancel)."""
    n = Counter(e for p in parcels for e in edges(p))
    return sum(1 for c in n.values() if c == 1)


def inside(poly: list, x: float, y: float) -> bool:
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


def seg_dist(px, py, a, b) -> float:
    dx, dy = b[0] - a[0], b[1] - a[1]
    t = 0.0 if dx == dy == 0 else max(0.0, min(1.0, ((px - a[0]) * dx + (py - a[1]) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - a[0] - t * dx, py - a[1] - t * dy)


def interior_point(parcel: dict) -> list[float]:
    """A point well inside the polygon: best of a 40x40 grid by distance to the boundary."""
    poly = parcel["polygon"]["coordinates"]
    xs = [p[0] for p in poly[0]]
    ys = [p[1] for p in poly[0]]
    best, best_d = None, -1.0
    for i in range(41):
        for j in range(41):
            x = min(xs) + (max(xs) - min(xs)) * i / 40
            y = min(ys) + (max(ys) - min(ys)) * j / 40
            if not inside(poly, x, y):
                continue
            d = min(
                seg_dist(x, y, a, b)
                for ring in poly for a, b in zip(ring, ring[1:] + ring[:1], strict=False)
            )
            if d > best_d:
                best, best_d = [round(x, 3), round(y, 3)], d
    assert best is not None
    return best


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-editor-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"editor smoke: scratch directory {scratch}")

    work = scratch / "work"
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(REPO / "data" / "work", work)
    parcels = json.loads((work / "parcels.json").read_text())["parcels"]
    by_id = {p["id"]: p for p in parcels}

    usable = [p for p in parcels if p["kind"] in ("building_plot", "garden")]
    shared: Counter = Counter()
    owners: dict = {}
    for p in usable:
        for e in set(edges(p)):
            owners.setdefault(e, []).append(p["id"])
    for ids in owners.values():
        if len(ids) == 2:
            shared[tuple(sorted(ids))] += 1
    (a_id, b_id), n_shared = shared.most_common(1)[0]
    rest = [p["id"] for p in usable if p["id"] not in (a_id, b_id)]
    c_id, d_id = rest[0], rest[-1]
    a, b, c, d = (by_id[i] for i in (a_id, b_id, c_id, d_id))

    edits = scratch / "edits.json"
    seed = {
        "version": 1,
        "parcels": {"P-9999": {"fence": {"mode": "none"}}, c_id: {"fence": {"mode": "auto"}}},
        "groups": {"G-0002": {"parcels": sorted([c_id, d_id]), "fence": {"mode": "none"}}},
    }
    edits.write_text(json.dumps(seed, sort_keys=True, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")

    rng = random.Random(7)
    floats = []
    for _ in range(300):
        v = rng.uniform(-1, 1) * 10 ** rng.randint(-9, 20)
        raw = rng.choice([repr(v), format(v, ".17e"), format(v, ".17e").upper()])
        floats.append([raw, repr(float(raw))])
    floats += [["1E5", "100000.0"], ["1.50", "1.5"], ["-0", "0"], ["0.0001", "0.0001"], ["0.00001", "1e-05"]]

    after_group, final = scratch / "after_group.json", scratch / "final.json"
    plan = {
        "parcel_a": a_id, "parcel_b": b_id, "parcel_c": c_id,
        "point_a": interior_point(a), "point_b": interior_point(b), "point_c": interior_point(c),
        "fixture": str(FIXTURE), "floats": floats,
        "sel_segments": sum(1 for p in (a, b) for _ in edges(p)),
        "c_segments": sum(1 for _ in edges(c)),
        "shared_edges": n_shared,
        "group_ab_segments": perimeter([a, b]),
        "existing_group": "G-0002", "existing_group_segments": perimeter([c, d]),
        "next_group": "G-0003",
        "after_group": str(after_group), "final": str(final),
    }
    print(f"editor smoke: group {a_id}+{b_id} ({n_shared} shared edges), existing group {c_id}+{d_id}")
    (scratch / "plan.json").write_text(json.dumps(plan, indent=1))

    r = uv("-m", "drycastle_pipeline", "export", "--area", "slice", "--out", str(scratch / "map"), "--work", str(work))
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
        return 1
    build = subprocess.run(["dotnet", "build", str(REPO / "godot/viewer/viewer.csproj")], capture_output=True, text=True)
    if build.returncode != 0:
        print(build.stdout[-2000:])
        return 1
    cmd = [args.godot, "--headless", "--path", str(REPO / "godot/viewer"), "--",
           "--map", str(scratch / "map"), "--seed", args.seed, "--area", "slice",
           "--edits", str(edits), "--state", str(scratch / "editor-state.json"),
           "--editor-smoke", "--editor-smoke-plan", str(scratch / "plan.json")]
    code = subprocess.run(cmd).returncode

    # the files the viewer saved must be exactly what the pipeline's own writer produces
    check = (
        "import sys, json\n"
        "from pathlib import Path\n"
        "from drycastle_pipeline.edits import load_edits, dump_text\n"
        "bad = 0\n"
        "for f in sys.argv[1:]:\n"
        "    t = Path(f).read_text(encoding='utf-8')\n"
        "    ok = dump_text(load_edits(Path(f))) == t\n"
        "    print(('PASS' if ok else 'FAIL'), 'dump_edits reproduces', Path(f).name, 'byte for byte')\n"
        "    bad += not ok\n"
        "sys.exit(bad)\n"
    )
    r = uv("-c", check, str(after_group), str(final))
    print(r.stdout.strip() or r.stderr.strip())
    return code or r.returncode


if __name__ == "__main__":
    sys.exit(main())
