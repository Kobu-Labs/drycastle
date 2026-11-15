#!/usr/bin/env python3
"""Ground patch hot reload smoke test (`make viewer-patches-smoke`).

Exports a fresh map with a few ground patches into a scratch directory (nothing under data/ or out/ is
touched), prepares a series of edits files (retype to another colour, reshape, add, remove, a patch and a parcel edit in
one file change, a broken file) and starts the viewer headless with `--watch --edits <scratch edits.json>`;
the viewer drives and asserts the run (`--patches-smoke`, see ViewerPatchesSmoke.cs). A patch's paint colour is its
type's first terrain colour, so every colour is a ground type of its own (`paint_<rrggbb>`, colours only).
Exits with the viewer's exit code. (Not run by the agent that last changed it: it runs the game.)

    python3 godot/viewer/patches_smoke.py [--dir DIR] [--godot PATH]
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
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-patches-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"patches smoke: scratch directory {scratch}")

    parcels_doc = json.loads((REPO / "data" / "village" / "work" / "parcels.json").read_text())
    ox, oy = parcels_doc["origin_epsg5514"]
    parcels = sorted(parcels_doc["parcels"], key=lambda p: p["id"])
    plots = [p for p in parcels if p["kind"] == "building_plot"]
    if len(plots) < 2:
        sys.exit("patches smoke: the map has too few building plots")

    def box(cx: float, cy: float, w: float = 24.0, h: float = 18.0) -> list[list[float]]:
        """Absolute S-JTSK rectangle around a local point."""
        return [[round(cx - w / 2 + ox, 3), round(cy - h / 2 + oy, 3)], [round(cx + w / 2 + ox, 3), round(cy - h / 2 + oy, 3)],
                [round(cx + w / 2 + ox, 3), round(cy + h / 2 + oy, 3)], [round(cx - w / 2 + ox, 3), round(cy + h / 2 + oy, 3)]]

    # GP-0001 sits on the chunk border nearest to a parcel anchor (so it is split over chunk files when the
    # ground continues across it); GP-0002 overlaps it and wins; GP-0003 and GP-0004 sit elsewhere.
    border = min(parcels, key=lambda p: abs(p["anchor"][0] - round(p["anchor"][0] / CHUNK) * CHUNK))
    bx = round(border["anchor"][0] / CHUNK) * CHUNK
    by = border["anchor"][1]
    a, b = plots[0]["anchor"], plots[-1]["anchor"]
    if (a[0] - bx) ** 2 + (a[1] - by) ** 2 < 60 ** 2 or (b[0] - bx) ** 2 + (b[1] - by) ** 2 < 60 ** 2 or a == b:
        a = max(parcels, key=lambda p: (p["anchor"][0] - bx) ** 2 + (p["anchor"][1] - by) ** 2)["anchor"]
        b = [a[0] + 40.0, a[1] + 40.0]
    colours = {"GP-0001": "#aa3322", "GP-0002": "#2244aa", "GP-0003": "#22aa44", "GP-0004": "#aaaa22"}
    extra_colours = ["#11aa99", "#772299", "#000000"]  # the colours the steps change patches to

    def paint(colour: str) -> str:
        """The ground type of a plain colour (a type of the user's own with colours only)."""
        return "paint_" + colour.lstrip("#").lower()

    ground_types = {paint(c): {"attributes": {"colours": [c]}} for c in [*colours.values(), *extra_colours]}
    polys = {
        "GP-0001": box(bx, by),
        "GP-0002": box(bx + 10, by + 4),
        "GP-0003": box(a[0], a[1]),
        "GP-0004": box(b[0], b[1]),
    }

    def reg(ids: list[str], **over: dict) -> dict:
        """Patches with the baseline colours and shapes, overridden per ID (`colour` picks the type)."""
        out = {}
        for i in ids:
            o = dict(over.get(i.replace("-", "_"), {}))
            colour = o.pop("colour", colours[i])
            out[i] = {"type": paint(colour), "polygon": polys[i], **o}
        return out

    def doc(patches: dict, parcel_entries: dict | None = None) -> str:
        d: dict = {"version": 1, "parcels": parcel_entries or {}, "groups": {}, "ground_types": ground_types}
        if patches:
            d["ground_patches"] = patches
        return json.dumps(d)

    plot = plots[0]["id"]
    fence_none = {plot: {"fence": {"mode": "none"}}}
    recol = {"GP_0001": {"colour": "#11aa99"}}
    moved = {"GP_0003": {"polygon": box(a[0] + 15, a[1] - 6, 30, 22)}}
    ids = ["GP-0001", "GP-0002", "GP-0003"]
    steps = {
        "recolour": doc(reg(ids, **recol)),
        "reshape": doc(reg(ids, **recol, **moved)),
        "add": doc(reg(ids + ["GP-0004"], **recol, **moved)),
        "remove_top": doc(reg(["GP-0001", "GP-0003", "GP-0004"], **recol, **moved)),
        "both": doc(reg(["GP-0001", "GP-0003", "GP-0004"], GP_0001={"colour": "#772299"}, **moved), fence_none),
        "broken": '{"version": 1, "ground_patches": {"GP-0001": {"type": ',
        "toggle_a": doc(reg(["GP-0001", "GP-0003", "GP-0004"], GP_0001={"colour": "#772299"}, GP_0004={"colour": "#000000"}, **moved), fence_none),
        "toggle_b": doc(reg(["GP-0001", "GP-0003", "GP-0004"], GP_0001={"colour": "#772299"}, **moved), fence_none),
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
        "patches": list(colours), "colours": colours, "parcel": plot,
        "baseline_patches": ["GP-0001", "GP-0002", "GP-0003"],
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
           "--patches-smoke", "--patches-smoke-plan", str(scratch / "plan.json")]
    return subprocess.run(cmd).returncode


if __name__ == "__main__":
    sys.exit(main())
