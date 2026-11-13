#!/usr/bin/env python3
"""Regions hot reload smoke test (`make viewer-regions-smoke`).

Exports a fresh map with a few regions into a scratch directory (nothing under data/ or out/ is
touched), prepares a series of edits files (colour change, reshape, add, remove, a region and a parcel edit in
one file change, a broken file) and starts the viewer headless with `--watch --edits <scratch edits.json>`;
the viewer drives and asserts the run (`--regions-smoke`, see ViewerRegionsSmoke.cs). Exits with the viewer's
exit code.

    python3 godot/viewer/regions_smoke.py [--dir DIR] [--godot PATH]
"""

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PIPELINE = REPO / "pipeline"
CHUNK = 250.0


def uv(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["uv", "run", "python", "-m", "drycastle_pipeline", *args],
        cwd=PIPELINE, capture_output=True, text=True,
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-regions-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"regions smoke: scratch directory {scratch}")

    parcels_doc = json.loads((REPO / "data" / "village" / "work" / "parcels.json").read_text())
    ox, oy = parcels_doc["origin_epsg5514"]
    parcels = sorted(parcels_doc["parcels"], key=lambda p: p["id"])
    plots = [p for p in parcels if p["kind"] == "building_plot"]
    if len(plots) < 2:
        sys.exit("regions smoke: the map has too few building plots")

    def box(cx: float, cy: float, w: float = 24.0, h: float = 18.0) -> list[list[float]]:
        """Absolute S-JTSK rectangle around a local point."""
        return [[round(cx - w / 2 + ox, 3), round(cy - h / 2 + oy, 3)], [round(cx + w / 2 + ox, 3), round(cy - h / 2 + oy, 3)],
                [round(cx + w / 2 + ox, 3), round(cy + h / 2 + oy, 3)], [round(cx - w / 2 + ox, 3), round(cy + h / 2 + oy, 3)]]

    # R-0001 sits on the chunk border nearest to a parcel anchor (so it is split over chunk files when the
    # ground continues across it); R-0002 overlaps it and wins; R-0003 and R-0004 sit elsewhere.
    border = min(parcels, key=lambda p: abs(p["anchor"][0] - round(p["anchor"][0] / CHUNK) * CHUNK))
    bx = round(border["anchor"][0] / CHUNK) * CHUNK
    by = border["anchor"][1]
    a, b = plots[0]["anchor"], plots[-1]["anchor"]
    if (a[0] - bx) ** 2 + (a[1] - by) ** 2 < 60 ** 2 or (b[0] - bx) ** 2 + (b[1] - by) ** 2 < 60 ** 2 or a == b:
        a = max(parcels, key=lambda p: (p["anchor"][0] - bx) ** 2 + (p["anchor"][1] - by) ** 2)["anchor"]
        b = [a[0] + 40.0, a[1] + 40.0]
    colours = {"R-0001": "#aa3322", "R-0002": "#2244aa", "R-0003": "#22aa44", "R-0004": "#aaaa22"}
    polys = {
        "R-0001": box(bx, by),
        "R-0002": box(bx + 10, by + 4),
        "R-0003": box(a[0], a[1]),
        "R-0004": box(b[0], b[1]),
    }

    def reg(ids: list[str], **over: dict) -> dict:
        """Regions with the baseline colours and shapes, overridden per ID."""
        out = {}
        for i in ids:
            out[i] = {"colour": colours[i], "polygon": polys[i], **over.get(i.replace("-", "_"), {})}
        return out

    def doc(regions: dict, parcel_entries: dict | None = None) -> str:
        d: dict = {"version": 1, "parcels": parcel_entries or {}, "groups": {}}
        if regions:
            d["regions"] = regions
        return json.dumps(d)

    plot = plots[0]["id"]
    fence_none = {plot: {"fence": {"mode": "none"}}}
    recol = {"R_0001": {"colour": "#11aa99"}}
    moved = {"R_0003": {"polygon": box(a[0] + 15, a[1] - 6, 30, 22)}}
    ids = ["R-0001", "R-0002", "R-0003"]
    steps = {
        "recolour": doc(reg(ids, **recol)),
        "reshape": doc(reg(ids, **recol, **moved)),
        "add": doc(reg(ids + ["R-0004"], **recol, **moved)),
        "remove_top": doc(reg(["R-0001", "R-0003", "R-0004"], **recol, **moved)),
        "both": doc(reg(["R-0001", "R-0003", "R-0004"], R_0001={"colour": "#772299"}, **moved), fence_none),
        "broken": '{"version": 1, "regions": {"R-0001": {"colour": ',
        "toggle_a": doc(reg(["R-0001", "R-0003", "R-0004"], R_0001={"colour": "#772299"}, R_0004={"colour": "#000000"}, **moved), fence_none),
        "toggle_b": doc(reg(["R-0001", "R-0003", "R-0004"], R_0001={"colour": "#772299"}, **moved), fence_none),
        "clear": doc({}),
    }
    sdir = scratch / "steps"
    sdir.mkdir(exist_ok=True)
    for name, text in steps.items():
        (sdir / f"{name}.json").write_text(text)

    edits = scratch / "edits.json"
    edits.write_text(doc(reg(ids)))
    r = uv("export", "--out", str(scratch / "map"), "--edits", str(edits))
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
        return 1

    plan = {
        "regions": list(colours), "colours": colours, "parcel": plot,
        "baseline_regions": ["R-0001", "R-0002", "R-0003"],
        "steps": {name: str(sdir / f"{name}.json") for name in steps},
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
           "--regions-smoke", "--regions-smoke-plan", str(scratch / "plan.json")]
    return subprocess.run(cmd).returncode


if __name__ == "__main__":
    sys.exit(main())
