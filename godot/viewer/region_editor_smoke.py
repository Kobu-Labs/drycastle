#!/usr/bin/env python3
"""Region tool smoke test (`make viewer-region-editor-smoke`).

Exports a fresh slice map (with one seeded region R-0001) into a scratch directory (nothing under data/ or out/ is
touched), computes the exact edits.json text the Python writer (`edits.dump_text`) produces after every step of the
scenario, then starts the viewer headless with `--region-editor-smoke` (ViewerRegionEditorSmoke.cs): it draws, closes,
colours, recolours, drags a corner (valid and self-crossing), deletes with confirm, undoes and redoes every step and
compares the saved file with the expected text byte for byte. Afterwards the saved steps are checked with the Python
validator and writer.

    python3 godot/viewer/region_editor_smoke.py [--dir DIR] [--godot PATH]
"""

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import editor_smoke as es  # noqa: E402

REPO, PIPELINE = es.REPO, es.PIPELINE


def text(regions: dict) -> str:
    """Exactly what edits.dump_text writes (the regions key only when there are regions)."""
    d: dict = {"version": 1, "parcels": {}, "groups": {}}
    if regions:
        d["regions"] = regions
    return json.dumps(d, sort_keys=True, indent=1, ensure_ascii=False) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-region-editor-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"region editor smoke: scratch directory {scratch}")

    doc = json.loads((REPO / "data" / "work" / "parcels.json").read_text())
    ox, oy = doc["origin_epsg5514"]
    plots = sorted((p for p in doc["parcels"] if p["kind"] == "building_plot"), key=lambda p: p["id"])
    if len(plots) < 2:
        sys.exit("region editor smoke: the slice has too few building plots")

    def half(v: float) -> float:
        return round(v * 2) / 2

    a, b = plots[0]["anchor"], plots[-1]["anchor"]
    x0, y0 = half(ox + a[0]), half(oy + a[1])
    bx, by = half(ox + b[0]), half(oy + b[1])
    quad = [[x0, y0], [x0 + 20.0, y0], [x0 + 20.0, y0 + 15.0], [x0, y0 + 15.0]]
    r1 = {"colour": "#aa3322", "polygon": [[bx, by], [bx + 24.0, by], [bx + 24.0, by + 18.0], [bx, by + 18.0]]}
    move_to = [x0 + 24.5, y0 - 3.0]
    moved = [quad[0], move_to, quad[2], quad[3]]

    def r2(colour: str, polygon: list) -> dict:
        return {"R-0001": r1, "R-0002": {"colour": colour, "polygon": polygon}}

    expect = {
        "seed": text({"R-0001": r1}),
        "created": text(r2("#336699", quad)),
        "recoloured": text(r2("#99cc33", quad)),
        "moved": text(r2("#99cc33", moved)),
        "empty": text({}),
    }
    edir = scratch / "expect"
    edir.mkdir(exist_ok=True)
    for k, v in expect.items():
        (edir / f"{k}.json").write_text(v)
    edits = scratch / "edits.json"
    edits.write_text(expect["seed"])
    plan = {
        "q0": quad[0], "q1": quad[1], "q2": quad[2], "q3": quad[3],
        "bow": [x0 + 40.0, y0 + 40.0],
        "invalid_to": [x0 - 5.0, y0 + 8.0],
        "move_to": move_to,
        "inside_r1": [bx + 12.0, by + 9.0],
        "expect": {k: str(edir / f"{k}.json") for k in expect},
        "steps_dir": str(scratch / "steps"),
    }
    (scratch / "plan.json").write_text(json.dumps(plan, indent=1))

    r = es.uv("-m", "drycastle_pipeline", "export", "--area", "slice", "--out", str(scratch / "map"), "--edits", str(edits))
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
           "--region-editor-smoke", "--region-editor-smoke-plan", str(scratch / "plan.json")]
    code = subprocess.run(cmd).returncode

    steps = sorted((scratch / "steps").glob("*.json"))
    check = (
        "import sys\n"
        "from pathlib import Path\n"
        "from drycastle_pipeline.edits import load_edits, dump_text\n"
        "bad = 0\n"
        "for f in sys.argv[1:]:\n"
        "    t = Path(f).read_text(encoding='utf-8')\n"
        "    ok = dump_text(load_edits(Path(f))) == t\n"
        "    print(('PASS' if ok else 'FAIL'), 'edits.validate accepts and dump_text reproduces', Path(f).name, 'byte for byte')\n"
        "    bad += not ok\n"
        "sys.exit(bad)\n"
    )
    r = es.uv("-c", check, *map(str, steps))
    print(r.stdout.strip() or r.stderr.strip())
    return code or r.returncode


if __name__ == "__main__":
    sys.exit(main())
