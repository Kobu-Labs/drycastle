#!/usr/bin/env python3
"""Fence panel smoke test (`make viewer-panel-smoke`).

Exports a fresh slice map into a scratch directory (a copy of data/work, so nothing under data/ or out/ is
touched), seeds a scratch edits.json, starts the viewer headless with `--panel-smoke` (ViewerPanelSmoke.cs:
drives the panel's controller API, applies, waits for the watcher round trip, copies the edits file after every
Apply into <scratch>/steps) and then checks those files with the Python validator (`edits.validate`), the
canonical writer (`dump_text`) and the gate geometry (the gate's `at` lies on the parcel / group boundary
within 1 cm). With `--shots DIR` it also takes two windowed screenshots of the panel (needs a display).

    python3 godot/viewer/panel_smoke.py [--dir DIR] [--godot PATH] [--shots DIR]
"""

import argparse
import json
import math
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import editor_smoke as es  # noqa: E402  (shared geometry helpers)

REPO, PIPELINE = es.REPO, es.PIPELINE
CUSTOM_C = {"colour": "#336699", "height_m": 2.0, "mode": "custom", "type": "brick_wall"}


def text(parcels: dict, groups: dict) -> str:
    """Exactly what edits.dump_text writes."""
    doc = {"version": 1, "parcels": parcels, "groups": groups}
    return json.dumps(doc, sort_keys=True, indent=1, ensure_ascii=False) + "\n"


def dist_to_ring_edges(px: float, py: float, edges_: list) -> float:
    return min(es.seg_dist(px, py, a, b) for a, b in edges_)


def boundary_edges(parcel: dict) -> list:
    return [(a, b) for ring in parcel["polygon"]["coordinates"] for a, b in zip(ring, ring[1:] + ring[:1], strict=False)]


def length(e) -> float:
    return math.hypot(e[1][0] - e[0][0], e[1][1] - e[0][1])


def perimeter_len(p: dict) -> float:
    return sum(length(e) for e in boundary_edges(p))


def python_facts() -> dict:
    code = (
        "import json\n"
        "from drycastle_pipeline import edits\n"
        "from drycastle_pipeline.rules import DEFAULT_RULES, load_section\n"
        "print(json.dumps({'types': edits.FENCE_TYPES, 'modes': list(edits.FENCE_MODES), 'default_type': edits.DEFAULT_FENCE_TYPE,\n"
        "}))\n"
    )
    r = es.uv("-c", code)
    if r.returncode != 0:
        sys.exit(r.stderr)
    return json.loads(r.stdout)


def fence_sections(parcel_id: str, work: Path, scratch: Path) -> list[dict]:
    """The fence sections parcel-run makes for the parcel with the smoke's custom fence."""
    probe_edits = scratch / "probe_edits.json"
    probe_edits.write_text(text({parcel_id: {"fence": CUSTOM_C}}, {}))
    out = scratch / "probe_fragment.json"
    r = es.uv("-m", "drycastle_pipeline", "parcel-run", "--area", "slice", "--parcel", parcel_id,
              "--edits", str(probe_edits), "--work", str(work), "--out", str(out))
    if r.returncode != 0:
        return []
    return json.loads(out.read_text())["fences"]


def mid(s: dict) -> tuple[float, float]:
    return ((s["a"][0] + s["b"][0]) / 2, (s["a"][1] + s["b"][1]) / 2)


def probe_of(s: dict) -> dict:
    (ax, ay), (bx, by) = s["a"], s["b"]
    ln = math.hypot(bx - ax, by - ay)
    return {"x": (ax + bx) / 2, "y": (ay + by) / 2, "nx": -(by - ay) / ln, "ny": (bx - ax) / ln,
            "z": (s["za"] + s["zb"]) / 2, "tx": (bx - ax) / ln, "ty": (by - ay) / ln}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    ap.add_argument("--shots", help="also write two panel screenshots into this directory (needs a display)")
    ap.add_argument("--skip-smoke", action="store_true", help="only the screenshots")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-panel-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"panel smoke: scratch directory {scratch}")

    work = scratch / "work"
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(REPO / "data" / "work", work)
    pdoc = json.loads((work / "parcels.json").read_text())
    origin = pdoc["origin_epsg5514"]
    parcels = pdoc["parcels"]
    by_id = {p["id"]: p for p in parcels}
    usable = [p for p in parcels if p["kind"] in ("building_plot", "garden")]

    # group pair: the two parcels sharing most edges
    owners: dict = {}
    for p in usable:
        for e in set(es.edges(p)):
            owners.setdefault(e, []).append(p["id"])
    shared: Counter = Counter()
    for ids in owners.values():
        if len(ids) == 2:
            shared[tuple(sorted(ids))] += 1
    (a_id, b_id), _n = shared.most_common(1)[0]
    a, b = by_id[a_id], by_id[b_id]

    # parcel C: a plot whose custom fence has two sections far apart (probe points for the gate and the none check)
    c = probe1 = probe2 = None
    for cand in sorted((p for p in usable if p["id"] not in (a_id, b_id)), key=perimeter_len, reverse=True):
        secs = fence_sections(cand["id"], work, scratch)
        secs.sort(key=lambda s: -math.hypot(s["a"][0] - s["b"][0], s["a"][1] - s["b"][1]))
        if len(secs) < 2 or math.hypot(secs[0]["a"][0] - secs[0]["b"][0], secs[0]["a"][1] - secs[0]["b"][1]) < 1.5:
            continue
        m1 = mid(secs[0])
        far = [s for s in secs[1:] if math.hypot(mid(s)[0] - m1[0], mid(s)[1] - m1[1]) >= 6.0]
        if far:
            c, probe1, probe2 = cand, probe_of(secs[0]), probe_of(far[0])
            break
    if c is None:
        sys.exit("panel smoke: no parcel with two distant fence sections found")
    others = [p for p in usable if p["id"] not in (a_id, b_id, c["id"])]
    f, e = others[0], others[-1]
    c_id, f_id, e_id = c["id"], f["id"], e["id"]
    print(f"panel smoke: group {a_id}+{b_id}, parcel C {c_id}, F {f_id}, a stale entry on {e_id}")

    facts = python_facts()
    # ---- scenario texts ----
    stale = {"P-9999": {"fence": {"mode": "none"}}, e_id: {"fence": {"mode": "auto"}}}
    group0 = {"G-0001": {"parcels": sorted([a_id, b_id])}}
    seed = text(dict(stale), group0)
    expect = {
        "seed": seed,
        "t1": text({**stale, c_id: {"fence": CUSTOM_C}}, group0),
        "t3": text({**stale, c_id: {"fence": {"mode": "none"}}}, group0),
        "t6": text({**stale, c_id: {"fence": {"mode": "custom"}}, f_id: {"fence": {"mode": "none"}}}, group0),
        "t7": text({**stale, c_id: {"fence": {"mode": "none"}}, f_id: {"fence": {"mode": "none"}}}, group0),
    }
    edir = scratch / "expect"
    edir.mkdir(exist_ok=True)
    for k, v in expect.items():
        (edir / f"{k}.json").write_text(v)
    edits = scratch / "edits.json"
    edits.write_text(seed)

    # group gate point: midpoint of the longest outer edge of the pair
    cnt = Counter(e_ for p in (a, b) for e_ in es.edges(p))
    outer, shared_edges = [], []
    for p in (a, b):
        for ea, eb in boundary_edges(p):
            key = tuple(sorted((es.q(ea), es.q(eb))))
            (outer if cnt[key] == 1 else shared_edges).append((ea[:2], eb[:2]))
    longest = max(outer, key=length)
    gx, gy = (longest[0][0] + longest[1][0]) / 2, (longest[0][1] + longest[1][1]) / 2
    ln = length(longest)
    gt = [(longest[1][0] - longest[0][0]) / ln, (longest[1][1] - longest[0][1]) / ln]
    gn = [-gt[1], gt[0]]
    cx0 = [pt[0] for pt in c["polygon"]["coordinates"][0]]
    cy0 = [pt[1] for pt in c["polygon"]["coordinates"][0]]

    fence_types = {k: [v["height_m"], v["thickness_m"], v["colour"]] for k, v in facts["types"].items()}
    steps = scratch / "steps"
    if steps.exists():
        shutil.rmtree(steps)
    plan = {
        "a": a_id, "b": b_id, "c": c_id, "f": f_id, "group": "G-0001",
        "steps_dir": str(steps),
        "expect": {k: str(edir / f"{k}.json") for k in expect},
        "fence_types": fence_types, "fence_modes": facts["modes"], "default_type": facts["default_type"],
        "probe1": {k: probe1[k] for k in ("x", "y", "nx", "ny", "z")},
        "probe2": {k: probe2[k] for k in ("x", "y", "nx", "ny", "z")},
        "probe1_pt": [probe1["x"], probe1["y"]], "probe1_tangent": [probe1["tx"], probe1["ty"]],
        "probe1_normal": [probe1["nx"], probe1["ny"]],
        "far_point": [(min(cx0) + max(cx0)) / 2 + 300.0, (min(cy0) + max(cy0)) / 2],
        "group_gate_pt": [gx, gy], "group_gate_t": gt, "group_gate_n": gn,
    }
    (scratch / "plan.json").write_text(json.dumps(plan, indent=1))

    r = es.uv("-m", "drycastle_pipeline", "export", "--area", "slice", "--out", str(scratch / "map"), "--work", str(work))
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
        return 1
    mp = json.loads((scratch / "map" / "parcels.json").read_text())
    if mp.get("origin_epsg5514") != origin:
        print("FAIL exported parcels.json lacks origin_epsg5514")
        return 1
    build = subprocess.run(["dotnet", "build", str(REPO / "godot/viewer/viewer.csproj")], capture_output=True, text=True)
    if build.returncode != 0:
        print(build.stdout[-2000:])
        return 1

    code = 0
    if not args.skip_smoke:
        cmd = [args.godot, "--headless", "--path", str(REPO / "godot/viewer"), "--",
               "--map", str(scratch / "map"), "--seed", args.seed, "--area", "slice",
               "--edits", str(edits), "--state", str(scratch / "editor-state.json"),
               "--panel-smoke", "--panel-smoke-plan", str(scratch / "plan.json")]
        code = subprocess.run(cmd).returncode
        code = code or verify_steps(steps, origin, by_id, c_id, probe1, a, b, shared_edges, outer, a_id, b_id)

    if args.shots:
        code = code or shots(args, scratch, origin, c_id, probe1)
    return code


def verify_steps(steps: Path, origin, by_id, c_id, probe1, a, b, shared_edges, outer, a_id, b_id) -> int:
    """Python-side checks of the files the viewer saved: validator, canonical text, gate geometry."""
    files = sorted(steps.glob("*.json"))
    check = (
        "import sys, json\n"
        "from pathlib import Path\n"
        "from drycastle_pipeline import edits\n"
        "bad = 0\n"
        "for f in sys.argv[1:]:\n"
        "    t = Path(f).read_text(encoding='utf-8')\n"
        "    errs = edits.validate(json.loads(t))\n"
        "    canon = edits.dump_text(edits.load_edits(Path(f))) == t\n"
        "    ok = not errs and canon\n"
        "    print(('PASS' if ok else 'FAIL'), 'Python validator accepts and dump_text reproduces', Path(f).name, errs or '')\n"
        "    bad += not ok\n"
        "sys.exit(bad)\n"
    )
    r = es.uv("-c", check, *map(str, files))
    print(r.stdout.strip() or r.stderr.strip())
    bad = r.returncode
    ox, oy = origin

    def ok(cond: bool, msg: str) -> None:
        nonlocal bad
        print(("PASS " if cond else "FAIL ") + msg)
        bad += not cond

    ok(len(files) >= 10, f"{len(files)} step files were saved")
    # t2: custom fence of C plus one gate on C's boundary
    t2 = json.loads((steps / "t2.json").read_text())
    fence = t2["parcels"][c_id]["fence"]
    gaps = fence.get("gaps", [])
    rest = {k: v for k, v in fence.items() if k != "gaps"}
    ok(rest == CUSTOM_C and len(gaps) == 1, f"t2: C keeps the custom fence and gets one gate ({rest})")
    if gaps:
        lx, ly = gaps[0]["at"][0] - ox, gaps[0]["at"][1] - oy
        d = dist_to_ring_edges(lx, ly, boundary_edges(by_id[c_id]))
        ok(d < 0.01, f"t2: the gate lies on C's boundary ({d * 100:.3f} cm off, limit 1 cm)")
        ok(gaps[0]["width_m"] == 2.0, f"t2: gate width {gaps[0]['width_m']} m (dragged 1 m from the centre = 2 m)")
        near = math.hypot(lx - probe1["x"], ly - probe1["y"])
        ok(near < 0.05, f"t2: the gate is at the clicked fence point ({near * 100:.1f} cm from it)")
    # t8: group fence with a gate on the group's outer perimeter
    t8 = json.loads((steps / "t8.json").read_text())
    g = t8["groups"]["G-0001"]
    gf = g.get("fence", {})
    ggaps = gf.get("gaps", [])
    ok({k: v for k, v in gf.items() if k != "gaps"} == {"colour": "#aabbcc", "mode": "custom", "type": "wire"} and len(ggaps) == 1,
       f"t8: group fence is custom wire #aabbcc with one gate ({gf.get('mode')})")
    ok(all("fence" not in t8["parcels"].get(p, {}) for p in (a_id, b_id)), "t8: the fence is on the group, not on the member parcels")
    if ggaps:
        lx, ly = ggaps[0]["at"][0] - ox, ggaps[0]["at"][1] - oy
        d_out = dist_to_ring_edges(lx, ly, outer)
        d_shared = dist_to_ring_edges(lx, ly, shared_edges)
        ok(d_out < 0.01 and d_shared > 0.5, f"t8: the group gate is on the outer perimeter ({d_out * 100:.3f} cm off), not on a shared edge ({d_shared:.1f} m away)")
        ok(ggaps[0]["width_m"] == 3.0, f"t8: group gate width {ggaps[0]['width_m']} m")
    t8b = json.loads((steps / "t8b.json").read_text())
    ok(t8b["parcels"][a_id]["fence"] == {"mode": "none"} and t8b["groups"]["G-0001"]["fence"] == gf,
       "t8b: member override written to the parcel, group fence unchanged")
    t8c = json.loads((steps / "t8c.json").read_text())
    ok("fence" not in t8c["parcels"].get(a_id, {}), "t8c: Reset removed the member's fence override")
    print("PANEL SMOKE (python checks) " + ("PASS" if not bad else "FAIL"))
    return bad


def shots(args, scratch: Path, origin, c_id: str, probe1: dict) -> int:
    out = Path(args.shots).resolve()
    out.mkdir(parents=True, exist_ok=True)
    ox, oy = origin
    gate = {"at": [round(probe1["x"] + ox, 3), round(probe1["y"] + oy, 3)], "width_m": 3.0}
    fence = {**CUSTOM_C, "gaps": [gate]}
    runs = [
        ("panel_fence_gate.png", {c_id: {"fence": fence}}, ["--shot-gate", "0", "--shot-collapse", "Group"]),
    ]
    for name, parcels, extra in runs:
        shot_edits = scratch / (name + ".edits.json")
        shot_edits.write_text(text(parcels, {}))
        edits = scratch / "edits_shot.json"
        edits.write_text(text({}, {}))
        cmd = [args.godot, "--path", str(REPO / "godot/viewer"), "--", "--map", str(scratch / "map"), "--area", "slice",
               "--edits", str(edits), "--state", str(scratch / "editor-state-shot.json"),
               "--editor-shot", str(out / name), "--select", c_id, "--shot-at", c_id, "--shot-edits", str(shot_edits),
               "--shot-focus", f"{probe1['x']:.2f},{probe1['y']:.2f}", "--shot-dist", "40", *extra]
        code = subprocess.run(cmd).returncode
        print(f"screenshot {out / name}: {'ok' if code == 0 else 'FAILED'}")
        if code:
            return code
    return 0


if __name__ == "__main__":
    sys.exit(main())
