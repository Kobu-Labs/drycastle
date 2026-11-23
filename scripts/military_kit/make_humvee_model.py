#!/usr/bin/env python3
"""Builds the Humvee (vehicles.json id `humvee`): the Synty SIMPLE Military troop car, scaled to a real HMMWV, with real window openings, a built
interior, a roof ring gun and a tow hitch.

PRIVATE: the model carries the pack's geometry, so it is written under assets-private/ (never committed):
    assets-private/synty/glb/simple-military/humvee.glb          model id  pack:synty-simple-military/humvee
Needs numpy (no -I) and the extracted pack (scripts/military_kit/build_simple_military.sh runs extract, parts export and the three model scripts):

    python3 scripts/military_kit/make_humvee_model.py [out.glb]

The pack model has no windows (glass is paint on a palette swatch) and no interior. This script removes the faces that wear the glass swatch
(two windscreen halves, four door windows, the roof hatch), puts a `glass_N` pane in each opening, and builds floor, tunnel, dash, steering wheel,
four seats, door cards and a rear partition behind them. The body shell is a single skin: it is drawn two-sided (pack material Military_Green_2s)
so the inside of the walls shows. Facts for vehicles.json: docs/research/scud-models-facts.md (written from facts/humvee.json).

Frame: the game's vehicle frame, metres, x right (left-hand drive: the driver is at x < 0), y up, forward is -Z, origin on the ground at the footprint
centre. The pack faces +Z and is 7.0 m long: the body is scaled (x 0.735, y 0.60, z 0.657, i.e. 2.17 m wide, 1.85 m to the roof, 4.6 m long)
and turned half a circle about Y; the wheels are scaled by their own factors so they roll true (radius 0.426 m).

Node tree (names are API, docs/standards/car-design.md):

    humvee
      hull                          body, interior, glass, lamps, seats (the body leans on it)
        glass_0 .. glass_5          windscreen L/R, front door L, rear door L, front door R, rear door R (listed in vehicles.json `glass` order)
        steering_wheel_rim, steering_wheel_spokes
        seat_driver, seat_passenger_1 (front right), seat_passenger_2 (rear left), seat_passenger_3 (rear right), seat_gunner (standing in the ring)
        gun_mount                   ring carriage, origin at the ring centre (yaw about Y, 0 = forward)
          gun                       pitch pivot (rotate about X, + raises the muzzle) on the yoke ahead of the ring
            barrel                  origin at the barrel root (recoil along +Z)
              muzzle                empty
            gunner_sight            empty
        light_head_l/r, light_tail_l/r, tow_hitch      empties
        tow_hook                    the pintle hook mesh
      wheel_fl_steer, wheel_fr_steer      steering pivots (turn about Y)
        wheel_fl, wheel_fr          front wheels (l = -x), origin at the wheel centre, roll about X
      wheel_rl, wheel_rr
"""
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simple_military_kit as K
from simple_military_kit import beam, box, r, rot_x

OUT = K.OUT_DIR / "humvee.glb"
FACTS = K.ROOT / "docs" / "research" / "scud-models-facts.d" / "humvee.json"

SX, SY, SZ = 0.735, 0.60, 0.6566           # pack -> real size (body)
WSX, WSY = 0.83, 0.60                      # wheel: width factor, radius factor
BODY_M = np.diag([-SX, SY, -SZ, 1.0])      # pack (+Z forward) -> game (-Z forward): scale and a half turn about Y


def squeeze_mirrors(m):
    """The pack's mirrors stick out to x 1.99 (3.98 m across at pack size): pull them in so the car is not wider than its hit box."""
    x = m.v[:, 0]
    out = np.abs(x) > 1.5
    m.v[out, 0] = np.sign(x[out]) * (1.47 + (np.abs(x[out]) - 1.47) * 0.12)
    return m


def build():
    pack = K.Pack("SK_Veh_Troop_Car_01")
    mdl = K.Model("humvee")
    G = K.gltf_mat
    mdl.pack_mat("Military_Green")
    mdl.pack_mat("Military_Green_2s")
    olive = mdl.mat("Olive paint", K.sample_palette((0.962, 0.282)), 0.1, 0.65)
    dark = mdl.mat("Dash graphite", (0.06, 0.065, 0.06), 0, 0.8)
    cloth = mdl.mat("Seat cloth", (0.17, 0.17, 0.13), 0, 0.95)
    trim = mdl.mat("Door card", (0.2, 0.21, 0.15), 0, 0.9)
    rubber = mdl.mat("Black rubber", (0.04, 0.04, 0.04), 0, 0.9)
    steel = mdl.mat("Gunmetal", (0.22, 0.23, 0.22), 0.7, 0.4)
    glass = mdl.mat("Transparent blue glass", (0.38, 0.62, 0.70), 0.05, 0.16, alpha=0.19, double=True)
    head = mdl.mat("Emissive warm headlight lens", (0.75, 0.85, 0.87), 0.25, 0.23, emission=1.0)
    mdl.materials[head]["emissiveFactor"] = [1.0, 0.85, 0.62]
    tail = mdl.mat("Emissive red tail lamps", (0.62, 0.025, 0.035), 0.15, 0.3, emission=0.5)
    amber = mdl.mat("Emissive amber indicators", (0.95, 0.39, 0.045), 0.15, 0.35, emission=0.8)
    rev = mdl.mat("Emissive reverse lamps", (0.86, 0.89, 0.85), 0.1, 0.3, emission=0.65)

    root = "humvee"
    hull = mdl.node("hull", root)

    # ---- body shell: the pack mesh with its painted windows cut out
    body_key = [k for k in pack.meshes if k.startswith("SK_Veh_Troop_Car_01")][0]
    shell = squeeze_mirrors(pack.get(body_key, "Military_Green_2s"))
    gmask = K.glass_faces(shell)
    cut = shell.subset(gmask)                      # the removed faces: window outlines come from them
    shell = shell.subset(~gmask)
    shell.mat = "Military_Green_2s"
    cut_t = cut.apply(BODY_M)
    shell_g = shell.apply(BODY_M)
    mdl.add(hull, shell_g)

    # ---- windows: one pane per opening, from the faces that were cut
    cen = cut_t.v[cut_t.f].mean(axis=1)
    groups = {}
    for i, c in enumerate(cen):
        if c[1] > 1.8 and abs(c[0]) < 0.5 and abs(c[2]) < 0.8:
            continue                                # the roof hatch (no pane: the opening is the ring)
        if c[2] < -0.9:
            key = "windscreen_l" if c[0] < 0 else "windscreen_r"
        else:
            side = "l" if c[0] < 0 else "r"
            key = ("door_front_" if c[2] < 0.0 else "door_rear_") + side
        groups.setdefault(key, []).extend(cut_t.v[cut_t.f[i]])
    order = ["windscreen_l", "windscreen_r", "door_front_l", "door_rear_l", "door_front_r", "door_rear_r"]
    kit_node = K.Node("panes")
    gl = K.GlassSet("humvee")
    for pid in order:
        outline = K.outline_of(groups[pid])
        gl.pane(kit_node, pid, [], [tuple(float(x) for x in p) for p in outline], inside=(0.0, 1.2, 0.0))
        n = len(gl.panes) - 1
        mdl.node(f"glass_{n}", hull)
        mdl.add(f"glass_{n}", K.poly(np.array(outline), glass, f"glass_{n}"))
    mdl.glass = gl

    # ---- roof ring opening (the pack's roof hatch): a low collar round it
    hx = SX * 0.57
    hz_front, hz_rear = -SZ * 0.35, SZ * 0.67          # hatch spans pack z -0.67..0.35 -> game z 0.44..-0.23
    roof_y = 3.09 * SY
    RING_C = (0.0, roof_y, (hz_front + hz_rear) / 2)
    t = 0.05
    h = 0.06
    for sx_ in (-1, 1):
        mdl.add(hull, box((sx_ * (hx + t / 2), roof_y + h / 2, RING_C[2]), (t, h, hz_rear - hz_front + 2 * t), olive, "ring_collar"))
    for zz in (hz_front - t / 2, hz_rear + t / 2):
        mdl.add(hull, box((0, roof_y + h / 2, zz), (2 * hx, h, t), olive, "ring_collar"))
    # the ring rail the gun rides on
    mdl.add(hull, K.ring((0, roof_y + h + 0.02, RING_C[2]), 0.50, 0.46, 0.04, steel, 24, "y", "ring_rail"))

    # ---- wheels
    centres = {"fl": (1.2766, 0.7104, 2.6005), "fr": (-1.2766, 0.7104, 2.6005), "rl": (1.2766, 0.7104, -2.2009), "rr": (-1.2766, 0.7104, -2.2009)}
    pack_wheels = {"fl": "Wheel_fl", "fr": "Wheel_fr", "rl": "Wheel_rl", "rr": "Wheel_rr"}
    wheel_pos = {}
    for nm, c in centres.items():
        # pack wheel "fl" sits at x +1.2766 (the pack's left); after the half turn that is game x -0.94 = left
        cg = np.array([-c[0] * SX, c[1] * SY, -c[2] * SZ])
        wheel_pos[nm] = cg
        wm = pack.get(pack_wheels[nm], "Military_Green")
        wm.v = wm.v - np.array(c)
        w_m = np.diag([-WSX, WSY, -WSY, 1.0])
        wm = wm.apply(w_m)
        wm.v = wm.v + cg
        if nm[0] == "f":
            mdl.node(f"wheel_{nm}_steer", root, cg)
            mdl.node(f"wheel_{nm}", f"wheel_{nm}_steer", cg)
        else:
            mdl.node(f"wheel_{nm}", root, cg)
        mdl.add(f"wheel_{nm}", wm)

    # ---- interior
    floor_y = 0.53
    zf, zr = -0.955, 0.955                  # cabin front (cowl) and rear partition
    ix = 1.06                               # inner face of the walls
    mdl.add(hull, box((0, floor_y - 0.015, (zf + zr) / 2), (2 * ix, 0.03, zr - zf), dark, "floor"))
    # centre tunnel and a shifter
    mdl.add(hull, box((0, floor_y + 0.125, -0.05), (0.56, 0.25, 1.50), dark, "tunnel"))
    mdl.add(hull, beam((0.0, floor_y + 0.25, -0.50), (0.0, floor_y + 0.50, -0.46), 0.035, rubber, "shifter"))
    mdl.add(hull, box((0, floor_y + 0.52, -0.46), (0.07, 0.07, 0.07), rubber, "shift_knob"))
    # dashboard along the cowl
    dz0, dz1 = zf, zf + 0.26
    mdl.add(hull, box((0, 0.80, (dz0 + dz1) / 2), (2 * ix, 0.52, dz1 - dz0), dark, "dash"))
    mdl.add(hull, box((0, 1.075, (dz0 + dz1) / 2 + 0.02), (2 * ix, 0.05, dz1 - dz0 + 0.04), cloth, "dash_top"))
    mdl.add(hull, box((-0.62, 1.15, -0.80), (0.50, 0.11, 0.09), dark, "binnacle"))
    mdl.add(hull, box((0.0, 0.92, dz1 + 0.01), (0.30, 0.20, 0.04), rubber, "radio"))
    for x in (-0.95, -0.28, 0.28, 0.95):
        mdl.add(hull, box((x, 1.00, dz1 + 0.005), (0.13, 0.05, 0.02), rubber, "vent"))
    # steering wheel, tilted 25 degrees towards the driver; the column from the dash
    SW = np.array([-0.62, 1.10, -0.60])
    mdl.add(hull, beam((SW[0], 0.88, -0.74), tuple(SW), 0.06, dark, "steering_column"))
    tilt = rot_x(-25.0)
    mdl.node("steering_wheel_rim", hull, SW)
    mdl.add("steering_wheel_rim", K.rotate_about(K.ring(SW, 0.19, 0.165, 0.03, rubber, 20, "z"), tilt, SW))
    mdl.node("steering_wheel_spokes", hull, SW)
    for a in (0.0, 2.1, 4.2):
        sp = beam(SW, SW + np.array([0.17 * math.sin(a), 0.17 * math.cos(a), 0]), 0.03, dark)
        mdl.add("steering_wheel_spokes", K.rotate_about(sp, tilt, SW))
    mdl.add("steering_wheel_spokes", K.rotate_about(box(SW, (0.10, 0.10, 0.05), dark), tilt, SW))

    # seats: cushion, back, headrest. front x +-0.62 z -0.22, rear x +-0.45 z 0.50
    cushion_top = floor_y + 0.32

    def seat(x, z, w, name):
        mdl.add(hull, box((x, cushion_top - 0.10, z), (w, 0.20, 0.50), cloth, name + "_base"))
        mdl.add(hull, box((x, floor_y + 0.15, z), (w * 0.8, 0.30, 0.30), dark, name + "_pedestal"))
        mdl.add(hull, box((x, cushion_top + 0.30, z + 0.30), (w, 0.62, 0.12), cloth, name + "_back"))
        mdl.add(hull, box((x, cushion_top + 0.68, z + 0.30), (w * 0.55, 0.17, 0.10), cloth, name + "_headrest"))
    seat(-0.62, -0.22, 0.52, "seat_front_l")
    seat(0.62, -0.22, 0.52, "seat_front_r")
    seat(-0.45, 0.50, 0.62, "seat_rear_l")
    seat(0.45, 0.50, 0.62, "seat_rear_r")
    mdl.add(hull, box((0, cushion_top - 0.10, 0.50), (0.36, 0.20, 0.50), cloth, "rear_bench_centre"))
    # rear partition and door cards
    mdl.add(hull, box((0, 1.15, zr + 0.01), (2 * ix, 1.30, 0.03), trim, "rear_partition"))
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * (ix - 0.02), 0.88, 0.0), (0.04, 0.60, 1.82), trim, "door_card"))
        mdl.add(hull, box((s_ * (ix - 0.07), 1.00, -0.40), (0.14, 0.05, 0.42), cloth, "armrest"))
        mdl.add(hull, box((s_ * (ix - 0.07), 1.00, 0.50), (0.14, 0.05, 0.42), cloth, "armrest"))
        mdl.add(hull, box((s_ * (ix - 0.06), 0.60, -0.40), (0.12, 0.10, 0.50), dark, "door_pocket"))
    # headliner strip on the roof edges is the shell itself

    # ---- lamps: lens plates on the pack's lamp boxes (front z 3.46, rear z -3.48 in pack units)
    for s_ in (-1, 1):
        xp = s_ * 1.05
        mdl.add(hull, box((-xp * SX, 1.595 * SY, -3.468 * SZ - 0.005), (0.42 * SX, 0.39 * SY, 0.012), head, "head_lens"))
        mdl.add(hull, box((-s_ * 1.09 * SX, 1.47 * SY, 3.485 * SZ + 0.004), (0.58 * SX, 0.36 * SY, 0.010), tail, "tail_lens"))
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * 0.93, 0.62, -2.31), (0.14, 0.07, 0.012), amber, "indicator"))
        mdl.add(hull, box((s_ * 0.50, 0.70, 2.30), (0.10, 0.06, 0.010), rev, "reverse_lamp"))

    # ---- tow hitch: a pintle hook on the rear crossmember
    HITCH = np.array([0.0, 0.62, 2.44])
    mdl.node("tow_hook", hull, HITCH)
    mdl.add("tow_hook", box((0, 0.62, 2.33), (0.16, 0.12, 0.10), steel, "hook_plate"), K.ring(tuple(HITCH), 0.065, 0.040, 0.05, steel, 16, "y"),
            box((0, 0.62, 2.395), (0.05, 0.05, 0.10), steel, "hook_neck"))

    # ---- the roof gun: the pack's gun mesh on a ring mount
    gun_key = "SK_Veh_Troop_Car_Gun_01"
    gm = pack.get(gun_key, "Military_Green")
    O = pack.origin[gun_key]
    loc = gm.v - O
    tris = loc[gm.f]
    tmin, tmax = tris.min(axis=1), tris.max(axis=1)
    post = (np.abs(tmax[:, 0]) <= 0.09) & (np.abs(tmin[:, 0]) <= 0.09) & (np.abs(tmax[:, 2]) <= 0.09) & (tmin[:, 1] < -0.5)
    plate = tmax[:, 1] <= -0.33
    barrel = tmin[:, 2] >= 0.29
    yoke = post | plate
    GX, GY = 0.62, 0.38
    T_L = np.array([0.0, 0.346, 0.0])                      # trunnion in gun-local pack units
    TR = np.array([0.0, roof_y + GY * (T_L[1] + 0.452), RING_C[2] - 0.55])   # game position of the trunnion: the plate's underside (-0.452) on the roof

    def to_game(mesh):
        out = mesh.copy()
        out.v = (mesh.v - O - T_L) * np.array([-GX, GY, -GX]) + TR
        out.n = None if mesh.n is None else mesh.n * np.array([-1.0, 1 / GY, -1.0])
        if out.n is not None:
            out.n /= np.maximum(np.linalg.norm(out.n, axis=1, keepdims=True), 1e-12)
        return out

    mount = mdl.node("gun_mount", hull, RING_C)
    gun = mdl.node("gun", mount, TR)
    mdl.add(mount, to_game(gm.subset(yoke)))
    mdl.add(gun, to_game(gm.subset(~yoke & ~barrel)))
    broot = (np.array([0.0, 0.346, 0.294]) - T_L) * np.array([-GX, GY, -GX]) + TR
    mdl.node("barrel", gun, broot)
    mdl.add("barrel", to_game(gm.subset(barrel & ~yoke)))
    mdl.node("muzzle", "barrel", (np.array([0.0, 0.346, 1.332]) - T_L) * np.array([-GX, GY, -GX]) + TR)
    mdl.node("gunner_sight", gun, TR + np.array([0.0, 0.22, 0.55]))

    # ---- seats (feet), lamps, hitch: empties under the hull
    seat_y = floor_y + 0.07
    SEATS = {"seat_driver": (-0.62, seat_y, -0.22), "seat_passenger_1": (0.62, seat_y, -0.22), "seat_passenger_2": (-0.45, seat_y, 0.50),
             "seat_passenger_3": (0.45, seat_y, 0.50), "seat_gunner": (RING_C[0], floor_y + 0.02, RING_C[2])}
    for k, p in SEATS.items():
        mdl.node(k, hull, p)
    LAMPS = {"light_head_l": (-0.77, 0.96, -2.28), "light_head_r": (0.77, 0.96, -2.28), "light_tail_l": (-0.80, 0.88, 2.29), "light_tail_r": (0.80, 0.88, 2.29)}
    for k, p in LAMPS.items():
        mdl.node(k, hull, p)
    mdl.node("tow_hitch", hull, HITCH)
    return mdl, dict(seats=SEATS, lamps=LAMPS, hitch=HITCH, ring=RING_C, trunnion=TR, wheel_pos=wheel_pos, floor_y=floor_y, cushion_top=cushion_top, gl=gl, sight=TR + np.array([0.0, 0.22, 0.55]), muzzle=(np.array([0.0, 0.346, 1.332]) - T_L) * np.array([-GX, GY, -GX]) + TR, broot=broot)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(OUT)
    K.ensure_pack_materials()
    mdl, info = build()
    size = mdl.write(out, "scripts/make_humvee_model.py (Drycastle; contains Synty SIMPLE Military geometry, private)")
    pts = mdl.all_points()
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    body_pts = np.vstack([m.v for n, nd in mdl.nodes.items() if n in ("hull",) for m in nd["meshes"]])
    wp = info["wheel_pos"]
    print(f"{mdl.tris} triangles, {size} bytes -> {out}")
    print("bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    info["gl"].report()
    seats = info["seats"]
    eyes = {k: r((p[0], p[1] + 1.05, p[2] + 0.09), 3) for k, p in seats.items() if k != "seat_gunner"}
    hull_lo, hull_hi = body_pts.min(axis=0), body_pts.max(axis=0)
    facts = {
        "model": "pack:synty-simple-military/humvee", "file": "synty/glb/simple-military/humvee.glb",
        "bounds": {"min": r(lo), "max": r(hi)}, "body_bounds": {"min": r(hull_lo), "max": r(hull_hi)},
        # the body: mirrors 1.13 m out, wheels 1.11, bumpers 2.31 m; the pintle hook (to 2.5) , the roof gunner, the gun and the antennas stick out of the box
        "suggested": {"half_width": 1.13, "half_length": round(float(max(-hull_lo[2], hull_hi[2])), 2), "height": 1.86},
        "seats": {k: r(p) for k, p in seats.items()}, "cockpit_eye": eyes,
        "lamps": {k: r(p) for k, p in info["lamps"].items()},
        "wheels": {"radius": round(float(wp["rl"][1]), 3), "wheelbase": round(float(wp["rl"][2] - wp["fl"][2]), 3), "track": round(float(wp["fr"][0] - wp["fl"][0]), 3),
                   "centres": {k: r(v) for k, v in wp.items()}},
        "tow_hitch": r(info["hitch"]),
        "roof_gun": {"ring_centre": r(info["ring"]), "gun_pivot": r(info["trunnion"]), "muzzle": r(info["muzzle"]), "gunner_sight": r(info["sight"]),
                     "muzzle_length_from_pivot": round(float(np.linalg.norm(info["muzzle"] - info["trunnion"])), 3)},
        "glass": [{"id": p["id"], "centre": r(p["centre"]), "normal": r(p["normal"]), "size": r(p["size"]), "outline": [r(q) for q in p["outline"]]} for p in info["gl"].panes],
    }
    FACTS.parent.mkdir(parents=True, exist_ok=True)
    FACTS.write_text(json.dumps(facts, indent=1) + "\n")
    print("facts ->", FACTS)


if __name__ == "__main__":
    main()
