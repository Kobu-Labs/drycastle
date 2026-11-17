#!/usr/bin/env python3
"""Builds the Scud launcher truck (vehicles.json id `scud`) and the missile alone, from the Synty SIMPLE Military ScudTruck.

PRIVATE: both carry the pack's geometry, so they are written under assets-private/ (never committed):
    assets-private/synty/glb/simple-military/scud.glb            model id  pack:synty-simple-military/scud
    assets-private/synty/glb/simple-military/scud_missile.glb    model id  pack:synty-simple-military/scud_missile   (the flying missile entity)
Needs numpy (no -I) and the extracted pack (scripts/build_simple_military.sh):

    python3 scripts/make_scud_model.py [out dir]

The pack truck is 14.8 m long; the model is scaled by 0.8 (11.9 m long, 3.1 m wide, the missile 11.1 m by 0.88 m: a real Scud-B and a MAZ-543),
turned half a circle about Y (the pack faces +Z) and its eight wheels get their own radius so they stand on the ground. The cab is two half cabs with the
missile's nose lying between them (as on the real truck): the left one holds the driver, the right one the operator. The pack paints its windows
on a palette swatch; the faces wearing it are cut out, a `glass_N` pane is put in each opening and a minimal cab is built (floor, dash, steering wheel,
two seats, door cards). The body is one skin: it is drawn two-sided (pack material Military_Green_2s).

Frame: the game's vehicle frame, metres, x right (left-hand drive: driver at x < 0), y up, forward is -Z, origin on the ground at the footprint centre.

Node tree (names are API, docs/standards/car-design.md):

    scud
      hull                          body, cab, glass, rack with its missile (the body leans on the hull)
        glass_0 .. glass_7          windscreen L/R, door windows front/rear L, front/rear R, front corner window L/R  (vehicles.json `glass` order)
        steering_wheel_rim, steering_wheel_spokes
        seat_driver (left), seat_operator (right)
        rack                        the missile rack, origin on the REAR HINGE axis (x = 0, in the rack's rear end). Rotate about X: 0 = lying
                                    (pointing forward over the cab), +90 deg = vertical (the missile tail down). Not driven by the model: the game animates it.
          missile                   the missile, origin at its centre of mass (the same origin as scud_missile.glb), nose along -Z when lying
        light_head_l/r, light_tail_l/r      empties
      wheel_l1_steer, wheel_r1_steer, wheel_l2_steer, wheel_r2_steer      steering pivots of axle 1 and 2 (front), turn about Y
        wheel_l1, wheel_r1, wheel_l2, wheel_r2    origin at the wheel centre, roll about X
      wheel_l3, wheel_r3, wheel_l4, wheel_r4      rear axles (fixed)
    l = -x, r = +x; the numbers count the axles from the front.
"""
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simple_military_kit as K
from simple_military_kit import Mesh, box, beam, poly, rot_x, move_m

OUT_DIR = K.OUT_DIR
FACTS = K.ROOT / "docs" / "research" / "scud-models-facts.d" / "scud.json"

S = 0.8                                            # pack -> real size
M = np.diag([-S, S, -S, 1.0])                      # scale and the half turn about Y (pack +Z forward -> game -Z forward)
WHEEL_FIT = 0.975                                  # pack wheels sink 2.4 cm into the ground; this makes radius = centre height


def gp(p):
    """A pack point in game space."""
    return np.array([-p[0] * S, p[1] * S, -p[2] * S])


def volume_centroid(m):
    a, b, c = m.v[m.f[:, 0]], m.v[m.f[:, 1]], m.v[m.f[:, 2]]
    o = m.v.mean(axis=0)
    vol = np.einsum("ij,ij->i", a - o, np.cross(b - o, c - o)) / 6.0
    cen = ((a + b + c + o) / 4.0 - o) * vol[:, None]
    if abs(vol.sum()) < 1e-6:
        return (m.v.min(axis=0) + m.v.max(axis=0)) / 2
    return o + cen.sum(axis=0) / vol.sum()


def build():
    pack = K.Pack("SK_Veh_ScudTruck_01")
    mdl = K.Model("scud")
    mdl.pack_mat("Military_Green")
    mdl.pack_mat("Military_Green_2s")
    dark = mdl.mat("Dash graphite", (0.06, 0.065, 0.06), 0, 0.8)
    cloth = mdl.mat("Seat cloth", (0.17, 0.17, 0.13), 0, 0.95)
    trim = mdl.mat("Door card", (0.2, 0.21, 0.15), 0, 0.9)
    rubber = mdl.mat("Black rubber", (0.04, 0.04, 0.04), 0, 0.9)
    glass = mdl.mat("Transparent blue glass", (0.38, 0.62, 0.70), 0.05, 0.16, alpha=0.19, double=True)
    head = mdl.mat("Emissive warm headlight lens", (0.75, 0.85, 0.87), 0.25, 0.23, emission=1.0)
    mdl.materials[head]["emissiveFactor"] = [1.0, 0.85, 0.62]
    tail = mdl.mat("Emissive red tail lamps", (0.62, 0.025, 0.035), 0.15, 0.3, emission=0.5)

    root = "scud"
    hull = mdl.node("hull", root)

    # ---- body shell with the cab windows cut out
    shell = pack.get("SK_Veh_ScudTruck_012", "Military_Green_2s")
    gmask = K.glass_faces(shell)
    cen_p = shell.v[shell.f].mean(axis=1)
    cab_glass = gmask & (cen_p[:, 2] > 4.0)           # the other dark swatches (a bay behind the cab) stay painted
    cut = shell.subset(cab_glass).apply(M)
    shell = shell.subset(~cab_glass)
    shell.mat = "Military_Green_2s"
    mdl.add(hull, shell.apply(M))

    # ---- panes: from the cut faces
    cc = cut.v[cut.f].mean(axis=1)
    groups = {}
    for i, c in enumerate(cc):
        side = "l" if c[0] < 0 else "r"
        ax = abs(c[0])
        if abs(c[2] + 6.27) < 0.05:
            key = "windscreen_" + side
        elif ax < 0.55:
            key = "quarter_inner_" + side               # on the inner wall of the half cab, next to the missile: not breakable
        elif c[2] < -5.7:
            key = "corner_" + side
        elif c[2] < -5.0:
            key = "door_front_" + side
        else:
            key = "door_rear_" + side
        groups.setdefault(key, []).extend(cut.v[cut.f[i]])
    order = ["windscreen_l", "windscreen_r", "door_front_l", "door_rear_l", "door_front_r", "door_rear_r", "corner_l", "corner_r"]
    kit_node = K.flak.Node("panes")
    gl = K.GlassSet("scud")
    for pid in order:
        outline = K.outline_of(groups[pid])
        gl.pane(kit_node, pid, [], [tuple(float(x) for x in p) for p in outline], inside=(0.0, 2.0, -5.0))
        n = len(gl.panes) - 1
        mdl.node(f"glass_{n}", hull)
        mdl.add(f"glass_{n}", poly(np.array(outline), glass, f"glass_{n}"))
    for key in ("quarter_inner_l", "quarter_inner_r"):
        mdl.add(hull, poly(np.array(K.outline_of(groups[key])), glass, key))
    mdl.glass = gl

    # ---- wheels: axle 1..4 from the front; the pack names them fl/fr (axle 1), rl/rr, rl2/rr2, rl3/rr3
    axle_names = [("Wheel_fl", "Wheel_fr"), ("Wheel_rl", "Wheel_rr"), ("Wheel_rl2", "Wheel_rr2"), ("Wheel_rl3", "Wheel_rr3")]
    wheel_pos = {}
    for ax, (nl, nr) in enumerate(axle_names, start=1):
        for side, pn in (("l", nl), ("r", nr)):
            wm = pack.get(pn, "Military_Green")
            c = pack.origin[pn]
            wm.v = wm.v - c
            wm = wm.apply(np.diag([-S, S * WHEEL_FIT, -S * WHEEL_FIT, 1.0]))
            cg = gp(c)
            wm.v = wm.v + cg
            nm = f"wheel_{side}{ax}"
            wheel_pos[nm] = cg
            if ax <= 2:
                mdl.node(nm + "_steer", root, cg)
                mdl.node(nm, nm + "_steer", cg)
            else:
                mdl.node(nm, root, cg)
            mdl.add(nm, wm)

    # ---- rack and missile
    rk = pack.get("SK_Veh_ScudTruck_MissleRack_01", "Military_Green").apply(M)
    ms = pack.get("SK_Veh_ScudTruck_Missle_01", "Military_Green").apply(M)
    hinge = gp(pack.origin["SK_Veh_ScudTruck_MissleRack_01"])
    com = volume_centroid(ms)
    mdl.node("rack", hull, hinge)
    mdl.add("rack", rk)
    mdl.node("missile", "rack", com)
    mdl.add("missile", ms)

    # ---- cab interior: floor at pack y 1.35, seats in the two half cabs
    floor_y = 1.35 * S
    zf, zr = -7.84 * S + 0.02, -5.31 * S             # windscreen foot / cab rear wall (game z)
    half_x = {"l": (-1.67 * S + 0.04, -0.61 * S), "r": (0.61 * S, 1.67 * S - 0.04)}
    for side, (x0, x1) in half_x.items():
        cx = (x0 + x1) / 2
        mdl.add(hull, box((cx, floor_y - 0.015, (zf + zr) / 2), (x1 - x0, 0.03, zr - zf), dark, "floor"))
        # dash / engine cover, deep (the steering wheel sits at its back edge)
        mdl.add(hull, box((cx, floor_y + 0.25, zf + 0.37), (x1 - x0, 0.50, 0.74), dark, "dash"))
        mdl.add(hull, box((cx, floor_y + 0.52, zf + 0.37), (x1 - x0, 0.05, 0.76), cloth, "dash_top"))
        sz = zr - 0.45                              # seat centre z (+z is the rear)
        sx = cx
        mdl.add(hull, box((sx, floor_y + 0.22, sz), (0.52, 0.20, 0.52), cloth, "seat_base"))
        mdl.add(hull, box((sx, floor_y + 0.10, sz), (0.40, 0.20, 0.40), dark, "seat_pedestal"))
        mdl.add(hull, box((sx, floor_y + 0.63, sz + 0.28), (0.52, 0.66, 0.12), cloth, "seat_back"))
        mdl.add(hull, box((sx, floor_y + 1.00, sz + 0.28), (0.30, 0.16, 0.10), cloth, "seat_headrest"))
        # door cards on the outer wall and the inner partition
        outer = x0 + 0.01 if side == "l" else x1 - 0.01
        inner = x1 - 0.01 if side == "l" else x0 + 0.01
        mdl.add(hull, box((outer, floor_y + 0.45, (zf + zr) / 2), (0.03, 0.90, zr - zf - 0.06), trim, "door_card"))
        mdl.add(hull, box((inner, floor_y + 0.45, (zf + zr) / 2), (0.03, 0.90, zr - zf - 0.06), trim, "partition_card"))
    # steering wheel on the driver's side (left)
    dx = (half_x["l"][0] + half_x["l"][1]) / 2
    SW = np.array([dx, floor_y + 0.80, zf + 0.92])
    tilt = rot_x(-20.0)
    mdl.add(hull, beam((dx, floor_y + 0.55, zf + 0.78), tuple(SW), 0.07, dark, "steering_column"))
    mdl.node("steering_wheel_rim", hull, SW)
    mdl.add("steering_wheel_rim", K.rotate_about(K.ring(SW, 0.21, 0.185, 0.035, rubber, 20, "z"), tilt, SW))
    mdl.node("steering_wheel_spokes", hull, SW)
    for a in (0.0, 2.1, 4.2):
        mdl.add("steering_wheel_spokes", K.rotate_about(beam(SW, SW + np.array([0.19 * math.sin(a), 0.19 * math.cos(a), 0]), 0.035, dark), tilt, SW))
    mdl.add("steering_wheel_spokes", K.rotate_about(box(SW, (0.11, 0.11, 0.06), dark), tilt, SW))
    # an instrument panel for the operator
    ox = (half_x["r"][0] + half_x["r"][1]) / 2
    mdl.add(hull, box((ox, floor_y + 0.62, zf + 0.85), (0.60, 0.22, 0.10), rubber, "operator_panel"))

    # ---- lamps: lens plates on the cab front bumper band and the rear bumper (pack y 1.42..1.94)
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * 1.15 * S, 1.68 * S, -7.845 * S - 0.008), (0.52 * S, 0.30 * S, 0.014), head, "head_lens"))
        mdl.add(hull, box((s_ * 1.15 * S, 1.68 * S, 6.225 * S + 0.008), (0.52 * S, 0.30 * S, 0.014), tail, "tail_lens"))
    # the head lens plates sit on the pack's front face (pack z +7.84 -> game z -6.27); the tail ones on the rear bumper (pack z -6.22 -> game +4.98)
    SEATS = {"seat_driver": (dx, floor_y + 0.07, zr - 0.45), "seat_operator": (ox, floor_y + 0.07, zr - 0.45)}
    for k, p in SEATS.items():
        mdl.node(k, hull, p)
    LAMPS = {"light_head_l": (-1.15 * S, 1.68 * S, -7.85 * S), "light_head_r": (1.15 * S, 1.68 * S, -7.85 * S),
             "light_tail_l": (-1.15 * S, 1.68 * S, 6.23 * S), "light_tail_r": (1.15 * S, 1.68 * S, 6.23 * S)}
    for k, p in LAMPS.items():
        mdl.node(k, hull, p)
    info = dict(seats=SEATS, lamps=LAMPS, wheel_pos=wheel_pos, hinge=hinge, com=com, gl=gl, floor_y=floor_y, missile=ms, rack=rk, sw=SW)
    return mdl, info


def build_missile_only(ms_game, com):
    m = K.Model("scud_missile")
    m.pack_mat("Military_Green")
    mm = ms_game.copy()
    mm.v = mm.v - com
    m.node("missile", "scud_missile", (0, 0, 0))
    m.add("missile", mm)
    return m, mm


def r(x, n=3):
    return [round(float(v), n) + 0.0 for v in x]


def main():
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT_DIR
    K.ensure_pack_materials()
    mdl, info = build()
    # origin at the footprint centre: the missile's nose reaches a little past the cab, the rear bumper a little less far than that
    pts0 = mdl.all_points()
    d = np.array([0.0, 0.0, -(pts0[:, 2].min() + pts0[:, 2].max()) / 2])
    mdl.shift(d)
    info["seats"] = {k: tuple(np.array(p) + d) for k, p in info["seats"].items()}
    info["lamps"] = {k: tuple(np.array(p) + d) for k, p in info["lamps"].items()}
    info["wheel_pos"] = {k: v + d for k, v in info["wheel_pos"].items()}
    for k in ("hinge", "com", "sw"):
        info[k] = info[k] + d
    size = mdl.write(out_dir / "scud.glb", "scripts/make_scud_model.py (Drycastle; contains Synty SIMPLE Military geometry, private)")
    pts = mdl.all_points()
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    print(f"scud: {mdl.tris} triangles, {size} bytes -> {out_dir / 'scud.glb'}")
    print("bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    info["gl"].report()
    mm, mesh = build_missile_only(info["missile"], info["com"])
    msize = mm.write(out_dir / "scud_missile.glb", "scripts/make_scud_model.py (Drycastle; contains Synty SIMPLE Military geometry, private)")
    mlo, mhi = mesh.v.min(axis=0), mesh.v.max(axis=0)
    print(f"missile: {mm.tris} triangles, {msize} bytes; bounds x %.3f..%.3f y %.3f..%.3f z %.3f..%.3f" % (mlo[0], mhi[0], mlo[1], mhi[1], mlo[2], mhi[2]))
    wp = info["wheel_pos"]
    hull_nodes = [m.v for m in mdl.nodes["hull"]["meshes"]]
    body = np.vstack(hull_nodes)
    seats = info["seats"]
    # the missile as drawn on the rack: nose end and tail end (game z, lying)
    facts = {
        "model": "pack:synty-simple-military/scud", "file": "synty/glb/simple-military/scud.glb",
        "missile_model": "pack:synty-simple-military/scud_missile", "missile_file": "synty/glb/simple-military/scud_missile.glb",
        "scale_from_pack": S,
        "bounds": {"min": r(lo), "max": r(hi)}, "body_bounds": {"min": r(body.min(axis=0)), "max": r(body.max(axis=0))},
        "suggested": {"half_width": round(float(max(abs(lo[0]), hi[0])), 2), "half_length": round(float(max(-lo[2], hi[2])), 2), "height_body": round(float(body[:, 1].max()), 2), "height_with_lying_missile": round(float(info["com"][1] + 0.44), 2),
                      "height_with_missile_fins": round(float(hi[1]), 2)},
        "seats": {k: r(p) for k, p in seats.items()},
        "cockpit_eye": {k: r((p[0], p[1] + 1.05, p[2] + 0.09)) for k, p in seats.items()},
        "steering_wheel": r(info["sw"]),
        "lamps": {k: r(p) for k, p in info["lamps"].items()},
        "wheels": {"radius": round(float(wp["wheel_l1"][1]), 3), "axle_z": {f"axle{a}": round(float(wp[f"wheel_l{a}"][2]), 3) for a in (1, 2, 3, 4)},
                   "wheelbase_first_to_last": round(float(wp["wheel_l4"][2] - wp["wheel_l1"][2]), 3), "track": round(float(wp["wheel_r1"][0] - wp["wheel_l1"][0]), 3),
                   "steered_axles": [1, 2], "centres": {k: r(v) for k, v in wp.items()}},
        "rack": {"pivot_hinge": r(info["hinge"]), "rotation": "about +X, 0 lying (forward), +90 deg vertical", "rack_length": round(float(info["rack"].v[:, 2].max() - info["rack"].v[:, 2].min()), 3),
                 "missile_origin_com": r(info["com"]), "missile_length": round(float(mhi[2] - mlo[2]), 3), "missile_diameter": round(float(mhi[0] - mlo[0]), 3),
                 "missile_size": r(mhi - mlo, 2),
                 "missile_nose_from_origin": round(float(-mlo[2]), 3), "missile_tail_from_origin": round(float(mhi[2]), 3),
                 "missile_axis_height_lying": round(float(info["com"][1]), 3)},
        "glass": [{"id": p["id"], "centre": r(p["centre"]), "normal": r(p["normal"]), "size": r(p["size"]), "outline": [r(q) for q in p["outline"]]} for p in info["gl"].panes],
    }
    FACTS.parent.mkdir(parents=True, exist_ok=True)
    FACTS.write_text(json.dumps(facts, indent=1) + "\n")
    print("facts ->", FACTS)


if __name__ == "__main__":
    main()
