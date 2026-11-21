#!/usr/bin/env python3
"""Builds the sentry turret model (#306): the pack's SM_Prop_Mg_stand_01 (a tripod machine gun, ONE mesh in the pack) split into a fixed `stand` and a
yawing `gun`. Synty SIMPLE Military geometry, so the model is PRIVATE and is written under assets-private/ (never committed):
    assets-private/synty/glb/simple-military/sentry_turret.glb     model id  pack:synty-simple-military/sentry_turret
Needs numpy (no -I) and the extracted pack (scripts/build_simple_military.sh):

    python3 scripts/make_sentry_turret_model.py [out.glb]

Split: the mesh is eight separate pieces. The three tripod legs (top at the head's foot) are the `stand`; the head, cradle, receiver and barrel are the `gun`.
Frame: the game's vehicle frame, metres, x right, y up, forward is -Z, origin on the ground under the yaw axis (the pack's own barrel already points to -Z).

Node tree (names are API):

    sentry_turret
      stand                       the three legs; never moves
      gun                         pivot on the yaw axis at the head's foot: the client sets its rotation.y (yaw only)
        muzzle                    empty at the barrel's tip: tracer start, flash, casing-less sound origin
        eject                     empty at the receiver's right side: where a case leaves (casing fx)
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simple_military_kit as K

OUT = K.OUT_DIR / "sentry_turret.glb"
FACTS = K.ROOT / "docs" / "research" / "scud-models-facts.d" / "sentry_turret.json"
S = 68.0          # pack mesh is ~0.02 units tall (a 0.01 m FBX unit): x100 = its Synty metres (~2 m); x0.68 puts the muzzle at 1.2 m = TurretRules.MuzzleHeightM (the server fires from there)
SPLIT_Y = 0.0095  # the legs' top / the head's foot, in pack units (1.0 cm-scaled)


def pieces(m):
    """The connected pieces of a mesh (faces sharing a vertex position), as boolean face masks."""
    pos = {}
    idx = np.array([pos.setdefault(tuple(np.round(v, 5)), len(pos)) for v in m.v])
    par = list(range(len(pos)))

    def fd(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a
    for f in m.f:
        a, b, c = idx[f]
        par[fd(a)] = fd(b)
        par[fd(b)] = fd(c)
    groups = {}
    for fi, f in enumerate(m.f):
        groups.setdefault(fd(idx[f[0]]), []).append(fi)
    return [np.isin(np.arange(len(m.f)), fs) for fs in groups.values()]


def build():
    pack = K.Pack("SM_Prop_Mg_stand_01")
    src = pack.get(next(iter(pack.meshes)), "Military_Green").apply(np.diag([S, S, S, 1.0]))  # pack units are 0.01 m: x100 = m, x S = scale
    gun_mask = np.zeros(len(src.f), bool)
    for mask in pieces(src):
        if src.subset(mask).v[:, 1].min() >= SPLIT_Y * S - 1e-6:
            gun_mask |= mask
    stand, gun = src.subset(~gun_mask), src.subset(gun_mask)
    assert len(stand.f) and len(gun.f), (len(stand.f), len(gun.f))
    lo, hi = src.bounds()
    # the barrel points to -Z in the pack frame already
    assert gun.v[:, 2].min() < -abs(gun.v[:, 2].max()), "barrel is expected to point to -Z"
    pivot_y = float(gun.v[:, 1].min())
    mdl = K.Model("sentry_turret")
    mdl.pack_mat("Military_Green")
    mdl.node("stand", "sentry_turret")
    mdl.add("stand", stand)
    mdl.node("gun", "sentry_turret", (0.0, pivot_y, 0.0))
    mdl.add("gun", gun)
    tip = gun.v[np.argmin(gun.v[:, 2])]
    bar = gun.v[gun.v[:, 2] < gun.v[:, 2].min() + 0.05]
    muzzle = np.array([float(bar[:, 0].mean()), float(bar[:, 1].mean()), float(gun.v[:, 2].min())])
    mdl.node("muzzle", "gun", muzzle)
    rec = gun.v[(gun.v[:, 2] > -0.2) & (gun.v[:, 1] > pivot_y + 0.3)]
    eject = np.array([float(rec[:, 0].max()), float(rec[:, 1].mean()), float(rec[:, 2].mean())])
    mdl.node("eject", "gun", eject)
    return mdl, {"pivot_y": pivot_y, "muzzle": muzzle, "eject": eject, "stand_tris": len(stand.f), "gun_tris": len(gun.f)}


def r(v):
    return [round(float(x), 3) for x in v]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(OUT)
    K.ensure_pack_materials()
    mdl, info = build()
    size = mdl.write(out, "scripts/make_sentry_turret_model.py (Drycastle; Synty SIMPLE Military geometry, private)")
    pts = mdl.all_points()
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    print(f"{mdl.tris} triangles, {size} bytes -> {out}")
    print("bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    facts = {"model": "pack:synty-simple-military/sentry_turret", "file": "synty/glb/simple-military/sentry_turret.glb",
             "bounds": {"min": r(lo), "max": r(hi)}, "yaw_pivot": r((0.0, info["pivot_y"], 0.0)), "muzzle": r(info["muzzle"]), "eject": r(info["eject"]),
             "tris": {"stand": info["stand_tris"], "gun": info["gun_tris"]}}
    FACTS.parent.mkdir(parents=True, exist_ok=True)
    FACTS.write_text(json.dumps(facts, indent=1) + "\n")
    print("facts ->", FACTS)


if __name__ == "__main__":
    main()
