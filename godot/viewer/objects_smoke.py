#!/usr/bin/env python3
"""Objects, gizmo and session undo smoke test (`make viewer-objects-smoke`).

Exports a fresh slice map into a scratch directory (a copy of data/work, so nothing under data/ or out/ is
touched), seeds a scratch edits.json, computes in Python the exact text edits.json must have after every step
of the scenario (place a pack model and a primitive, drag, rotate, cross-parcel move, field edits, nudge,
duplicate, delete, group, fence Apply, ...), starts the viewer headless with `--objects-smoke`
(ViewerObjectsSmoke.cs: drives the editor through its APIs and key events, asserts the file text after every
step, that objects-only edits run no parcel-run, the scene nodes, undo/redo byte for byte, the 200 cap and the
edit-to-visible latency) and finally checks the saved files with the Python validator (`edits.validate`), the
canonical writer (`dump_text`) and `props.plan_edits` (no warnings: every object lies in its parcel).
With `--shots DIR` it also takes two windowed screenshots (needs a display).

    python3 godot/viewer/objects_smoke.py [--dir DIR] [--godot PATH] [--shots DIR] [--skip-smoke]
"""

import argparse
import copy
import json
import math
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import editor_smoke as es  # noqa: E402  (shared geometry helpers)

REPO, PIPELINE = es.REPO, es.PIPELINE
CAR = "pack:kenney-car-kit/sedan"
SLAB = "prim:slab"
POT = "prim:pot"
BUSH = "pack:quaternius-stylized-nature-megakit/Bush_Common_Flowers"
CLEARANCE = 4.5


def text(parcels: dict, groups: dict) -> str:
    """Exactly what edits.dump_text writes."""
    doc = {"version": 1, "parcels": parcels, "groups": groups}
    return json.dumps(doc, sort_keys=True, indent=1, ensure_ascii=False) + "\n"


def snap(v: float, step: float = 0.5) -> float:
    return math.copysign(math.floor(abs(v) / step + 0.5) * step, v)


def obj(oid: str, asset: str, x: float, y: float, rot: float, scale: float | None = None, colour: str | None = None) -> dict:
    o = {"id": oid, "asset": asset, "position": [round(x, 3), round(y, 3)], "rotation_deg": float(rot)}
    if scale is not None:
        o["scale"] = scale
    if colour is not None:
        o["colour"] = colour
    return o


def rings(parcel: dict) -> list:
    return parcel["polygon"]["coordinates"]


def clearance(parcel: dict, x: float, y: float) -> float:
    if not es.inside(rings(parcel), x, y):
        return -1.0
    return min(es.seg_dist(x, y, a, b) for ring in rings(parcel) for a, b in zip(ring, ring[1:] + ring[:1], strict=False))


def best_point(parcel: dict, origin: list[float]) -> tuple[float, tuple[float, float]] | None:
    """The point of the parcel (absolute coordinates on the 0.5 m grid) farthest from its boundary."""
    xs = [p[0] for p in rings(parcel)[0]]
    ys = [p[1] for p in rings(parcel)[0]]
    best = None
    for i in range(61):
        for j in range(61):
            lx = min(xs) + (max(xs) - min(xs)) * i / 60
            ly = min(ys) + (max(ys) - min(ys)) * j / 60
            ax, ay = snap(origin[0] + lx), snap(origin[1] + ly)
            c = clearance(parcel, ax - origin[0], ay - origin[1])
            if c >= CLEARANCE and (best is None or c > best[0]):
                best = (c, (ax, ay))
    return best


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", help="scratch directory (default: a new temp dir)")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--seed", default="1")
    ap.add_argument("--shots", help="also write two screenshots into this directory (needs a display)")
    ap.add_argument("--skip-smoke", action="store_true", help="only the screenshots")
    args = ap.parse_args()
    scratch = Path(args.dir or tempfile.mkdtemp(prefix="drycastle-objects-smoke-")).resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    print(f"objects smoke: scratch directory {scratch}")

    work = scratch / "work"
    if work.exists():
        shutil.rmtree(work)
    shutil.copytree(REPO / "data" / "work", work)
    pdoc = json.loads((work / "parcels.json").read_text())
    origin = pdoc["origin_epsg5514"]
    parcels = pdoc["parcels"]
    by_id = {p["id"]: p for p in parcels}
    usable = [p for p in parcels if p["kind"] in ("building_plot", "garden")]

    # parcels A and B (roomy, preferably neighbours: a group of them), C (the seed's own object, never touched)
    spots = {}
    for p in usable:
        xs, ys = [pt[0] for pt in rings(p)[0]], [pt[1] for pt in rings(p)[0]]
        if min(max(xs) - min(xs), max(ys) - min(ys)) < 2 * CLEARANCE:
            continue
        bp = best_point(p, origin)
        if bp:
            spots[p["id"]] = bp
    ranked = sorted(spots, key=lambda i: -spots[i][0])
    if len(ranked) < 3:
        sys.exit("objects smoke: need three parcels with a point 4.5 m from their boundary")
    a_id = ranked[0]
    owners: dict = {}
    for pid in ranked:
        for e in set(es.edges(by_id[pid])):
            owners.setdefault(e, []).append(pid)
    neighbours = {pid for ids in owners.values() if a_id in ids for pid in ids if pid != a_id}
    b_id = next((p for p in ranked if p in neighbours), next(p for p in ranked if p != a_id))
    c_id = next(p for p in ranked if p not in (a_id, b_id))
    (ax, ay), (bx, by), (cx, cy) = spots[a_id][1], spots[b_id][1], spots[c_id][1]
    print(f"objects smoke: A {a_id} (clearance {spots[a_id][0]:.1f} m), B {b_id} ({spots[b_id][0]:.1f} m), C {c_id}; "
          f"A and B are {'neighbours' if b_id in neighbours else 'not neighbours'}")
    all_x = [pt[0] for p in parcels for pt in rings(p)[0]]
    outside = [max(all_x) + 400.0, sum(pt[1] for pt in rings(by_id[a_id])[0]) / len(rings(by_id[a_id])[0])]

    # ---- the scenario, step by step, as the files it must produce ----
    stale = {"P-9999": {"fence": {"mode": "none"}}}
    seed_obj = obj("car-1", POT, cx, cy, 0.0)

    state = {"parcels": {**copy.deepcopy(stale), c_id: {"objects": [seed_obj]}}, "groups": {}}
    steps: dict[str, str] = {"seed": text(state["parcels"], state["groups"])}

    def lst(pid: str) -> list:
        return state["parcels"].setdefault(pid, {}).setdefault("objects", [])

    def find(oid: str) -> tuple[str, dict]:
        for pid, entry in state["parcels"].items():
            for o in entry.get("objects", []):
                if o["id"] == oid:
                    return pid, o
        raise KeyError(oid)

    def commit(name: str) -> None:
        for pid in [p for p, e in state["parcels"].items() if not e]:
            del state["parcels"][pid]
        steps[name] = text(state["parcels"], state["groups"])

    o1 = obj("o-0001", CAR, ax, ay, 0.0)
    lst(a_id).append(o1)
    commit("t1")
    lst(a_id).append(obj("o-0002", SLAB, ax + 2.0, ay + 1.5, 30.0))
    commit("t2")
    lst(b_id).append(obj("o-0003", SLAB, bx, by, 30.0))
    commit("t3")
    _, o = find("o-0001")
    px, py = ax + 2.5, ay + 1.0
    o["position"] = [px, py]
    commit("t4")
    px += 1.0
    o["position"] = [px, py]
    commit("t5")
    px, py = px + 0.125, py - 0.25
    o["position"] = [round(px, 3), round(py, 3)]
    commit("t6")
    o["rotation_deg"] = 30.0
    commit("t7")
    o["rotation_deg"] = 112.6
    commit("t8")
    # cross-parcel move: out of A's list, appended to B's
    lst(a_id).remove(o)
    if not state["parcels"][a_id]["objects"]:
        del state["parcels"][a_id]["objects"]
    px, py = bx + 1.0, by + 1.0
    o["position"] = [px, py]
    lst(b_id).append(o)
    commit("t9")
    px += 2.0
    o["position"] = [px, py]
    commit("t10")
    o["rotation_deg"] = 100.0
    commit("t11")
    o["scale"] = 1.5
    commit("t12")
    o["colour"] = "#aabbcc"
    commit("t13")
    del o["colour"]
    commit("t14")
    del o["scale"]
    commit("t15")
    for name, dx, dy in (("t16", 0.5, 0), ("t17", 0, 0.5), ("t18", -0.5, 0), ("t19", 0, -0.5)):
        px, py = px + dx, py + dy
        o["position"] = [round(px, 3), round(py, 3)]
        commit(name)
    dup = copy.deepcopy(o)
    dup["id"] = "o-0004"
    dup["position"] = [round(px + 1.0, 3), round(py, 3)]
    lst(b_id).append(dup)
    commit("t20")
    lst(b_id).remove(dup)
    commit("t21")
    members = sorted([a_id, b_id])
    state["groups"]["G-0001"] = {"parcels": members}
    commit("t22")
    _, o2 = find("o-0002")
    o2["position"] = [ax + 2.5, ay + 1.5]
    commit("t23")
    state["groups"]["G-0001"]["fence"] = {"mode": "none"}
    commit("t24")
    lst(a_id).append(obj("o-0004", CAR, ax - 1.0, ay - 1.5, 30.0))
    commit("t25")
    # (the smoke undoes t25 and places a slab instead: it becomes o-0004 of the earlier state)
    last = state["parcels"][a_id]["objects"].pop()
    lst(a_id).append(obj("o-0004", SLAB, ax + 2.0, ay - 1.5, 30.0))
    commit("t26")
    del last

    edir = scratch / "expect"
    edir.mkdir(exist_ok=True)
    for k, v in steps.items():
        (edir / f"{k}.json").write_text(v)
    edits = scratch / "edits.json"
    edits.write_text(steps["seed"])

    # rotation drag targets: 3 m from the object's centre at 37.4 and 112.6 degrees (object at ax + 3.625, ay + 0.75 after t6)
    cxo, cyo = ax + 3.625, ay + 0.75

    def ring_pt(deg: float) -> list[float]:
        return [cxo + 3.0 * math.cos(math.radians(deg)) - origin[0], cyo + 3.0 * math.sin(math.radians(deg)) - origin[1]]

    manifest = json.loads((REPO / "assets" / "manifest.json").read_text())
    plan = {
        "a": a_id, "b": b_id, "c": c_id, "car": CAR, "slab": SLAB, "origin": origin,
        "a_base": [ax - origin[0], ay - origin[1]], "a_base_abs": [ax, ay],
        "b_base": [bx - origin[0], by - origin[1]],
        "outside": outside,
        "rot1_pt": ring_pt(37.4), "rot2_pt": ring_pt(112.6),
        "manifest": [[a["id"], a["category"], a["name"]] for a in manifest["assets"]],
        "steps_dir": str(scratch / "steps"),
        "expect": {k: str(edir / f"{k}.json") for k in steps},
    }
    steps_dir = scratch / "steps"
    if steps_dir.exists():
        shutil.rmtree(steps_dir)
    (scratch / "plan.json").write_text(json.dumps(plan, indent=1))

    r = es.uv("-m", "drycastle_pipeline", "export", "--area", "slice", "--out", str(scratch / "map"), "--work", str(work))
    print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr)
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
               "--objects-smoke", "--objects-smoke-plan", str(scratch / "plan.json")]
        code = subprocess.run(cmd).returncode
        code = code or verify_steps(steps_dir, work, pdoc)

    if args.shots:
        code = code or shots(args, scratch, origin, a_id, (ax, ay), (bx, by))
    return code


def verify_steps(steps_dir: Path, work: Path, pdoc: dict) -> int:
    """Python-side checks of the files the viewer saved: validator, canonical text, plan_edits warnings."""
    files = sorted(steps_dir.glob("*.json"))
    check = (
        "import sys, json\n"
        "from pathlib import Path\n"
        "from drycastle_pipeline import edits, props\n"
        "pdoc = json.loads((Path(sys.argv[1]) / 'parcels.json').read_text())\n"
        "bad = 0\n"
        "for f in sys.argv[2:]:\n"
        "    t = Path(f).read_text(encoding='utf-8')\n"
        "    errs = edits.validate(json.loads(t))\n"
        "    canon = (not errs) and edits.dump_text(edits.load_edits(Path(f))) == t\n"
        "    warns = []\n"
        "    if not errs:\n"
        "        plan = props.plan_edits(edits.load_edits(Path(f)), pdoc['parcels'], pdoc.get('origin_epsg5514'), float(pdoc['chunk_size_m']))\n"
        "        warns = [w for w in plan.warnings if 'P-9999' not in w]  # the seed's stale entry warns on purpose\n"
        "    ok = not errs and canon and not warns\n"
        "    print(('PASS' if ok else 'FAIL'), 'validate + dump_text byte-stable + plan_edits without warnings:', Path(f).name, errs or warns or '')\n"
        "    bad += not ok\n"
        "sys.exit(bad)\n"
    )
    r = es.uv("-c", check, str(work), *map(str, files))
    print(r.stdout.strip() or r.stderr.strip())
    bad = r.returncode
    if len(files) < 20:
        print(f"FAIL only {len(files)} step files were saved")
        bad += 1
    print("OBJECTS SMOKE (python checks) " + ("PASS" if not bad else "FAIL"))
    return bad


def shots(args, scratch: Path, origin, a_id: str, a_base, b_base) -> int:
    out = Path(args.shots).resolve()
    out.mkdir(parents=True, exist_ok=True)
    ax, ay = a_base
    lx, ly = ax - origin[0], ay - origin[1]
    runs = [
        ("objects_car_selected.png",
         {a_id: {"objects": [obj("o-0001", CAR, ax, ay, 30.0), obj("o-0002", SLAB, ax + 3.0, ay + 1.5, 0.0),
                             obj("o-0003", "pack:kenney-car-kit/van", ax - 2.5, ay - 4.0, 340.0)]}},
         ["--shot-object", "o-0001", "--shot-open", "car", "--shot-collapse", "Group,Fence,Trees", "--shot-dist", "16",
          "--shot-focus", f"{lx:.2f},{ly:.2f}"]),
        ("objects_pot_slab_bush.png",
         {a_id: {"objects": [obj("o-0001", POT, ax - 1.0, ay + 1.0, 0.0), obj("o-0002", POT, ax - 0.5, ay + 1.0, 0.0),
                             obj("o-0003", SLAB, ax + 1.0, ay - 0.5, 0.0), obj("o-0004", SLAB, ax + 2.0, ay - 0.5, 0.0),
                             obj("o-0005", SLAB, ax + 3.0, ay - 0.5, 0.0),
                             obj("o-0006", BUSH, ax - 3.0, ay + 2.0, 20.0)]}},
         ["--shot-arm", "pack:quaternius-stylized-nature-megakit/Bush_Common_Flowers", "--shot-ghost", f"{lx + 2.0:.2f},{ly + 3.0:.2f}",
          "--shot-ghost-rot", "45", "--shot-open", "pot,slab,bush", "--shot-collapse", "Group,Fence,Trees", "--shot-dist", "14",
          "--shot-focus", f"{lx:.2f},{ly + 0.5:.2f}"]),
    ]
    for name, parcels, extra in runs:
        shot_edits = scratch / (name + ".edits.json")
        shot_edits.write_text(text(parcels, {}))
        edits = scratch / "edits_shot.json"
        edits.write_text(text({}, {}))
        cmd = [args.godot, "--path", str(REPO / "godot/viewer"), "--", "--map", str(scratch / "map"), "--area", "slice",
               "--edits", str(edits), "--state", str(scratch / "editor-state-shot.json"),
               "--editor-shot", str(out / name), "--select", a_id, "--shot-at", a_id, "--shot-edits", str(shot_edits), *extra]
        code = subprocess.run(cmd).returncode
        print(f"screenshot {out / name}: {'ok' if code == 0 else 'FAILED'}")
        if code:
            return code
    return 0


if __name__ == "__main__":
    sys.exit(main())
