#!/usr/bin/env python3
"""Builds the rocket cart (vehicles.json id `rocket_cart`): a towable single-axle olive cart carrying one Scud missile.

The pack has no such cart: the frame, wheels, drawbar and cradle are generated here (original geometry, olive paint like the flak and the other
generated military vehicles). The missile on it is a copy of the Synty SIMPLE Military Scud missile mesh, so the model is PRIVATE and is written under
assets-private/ (never committed):
    assets-private/synty/glb/simple-military/rocket_cart.glb     model id  pack:synty-simple-military/rocket_cart
Needs numpy (no -I) and the extracted pack (scripts/military_kit/build_simple_military.sh); it reads the pack itself, make_scud_model.py need not run first:

    python3 scripts/military_kit/make_rocket_cart_model.py [out.glb]

The missile (11.1 m, the same scale as the truck's) lies on two saddles with its centre of mass over the axle; the drawbar runs forward under its nose to a
towing eye at the height of the Humvee's pintle hook. Coupling (the integration's job): put the cart so that its `tow_eye` meets the tower's `tow_hitch`.

Frame: the game's vehicle frame, metres, x right, y up, forward is -Z, origin on the ground at the footprint centre (between the towing eye and the missile's tail).

Node tree (names are API, docs/standards/car-design.md):

    rocket_cart
      hull                          frame, drawbar, cradle, fenders, lamps, the missile
        missile                     the pack's missile, origin at its centre of mass (hide it when the cart is empty), nose along -Z
        tow_eye                     empty: the point a tower's hitch takes (the drawbar's eye)
        axle                        empty: the axle centre on the ground plane's side (wheel centres' middle)
        light_tail_l, light_tail_r  empties: tail lamp positions
      wheel_l, wheel_r              origin at the wheel centre (l = -x), roll about X
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simple_military_kit as K
from simple_military_kit import beam, box, cylinder, r, volume_centroid

OUT = K.OUT_DIR / "rocket_cart.glb"
FACTS = K.ROOT / "docs" / "research" / "scud-models-facts.d" / "rocket_cart.json"

S = 0.8                                            # the missile's scale from the pack (the same as make_scud_model.py)
M = np.diag([-S, S, -S, 1.0])
WHEEL_R, WHEEL_W, TRACK = 0.46, 0.26, 2.04         # radius, tyre width, wheel centre to wheel centre
AXLE_Z = 1.40                                       # axle position in the model frame (the missile's centre of mass is over it)
AXIS_Y = 1.45                                       # missile axis height
EYE = (0.0, 0.62, -6.20)                            # towing eye
RAIL_Y = 0.80                                       # frame rail centre height (the rails are 0.17 high)
BODY_HALF = 0.44                                    # the missile body's half height (a square section, 0.88 m)


def build():
    pack = K.Pack("SK_Veh_ScudTruck_01")
    mdl = K.Model("rocket_cart")
    mdl.pack_mat("Military_Green")
    olive = mdl.mat("Olive", (0.36, 0.40, 0.22), 0.0, 0.85)
    olive_dark = mdl.mat("OliveDark", (0.24, 0.28, 0.16), 0.0, 0.85)
    metal = mdl.mat("DarkMetal", (0.13, 0.14, 0.14), 0.2, 0.7)
    steel = mdl.mat("Steel", (0.62, 0.64, 0.64), 0.4, 0.5)
    rubber = mdl.mat("Rubber", (0.07, 0.07, 0.075), 0.0, 0.95)
    strap = mdl.mat("Khaki", (0.62, 0.55, 0.36), 0.0, 0.9)
    tail = mdl.mat("Emissive red tail lamps", (0.62, 0.025, 0.035), 0.15, 0.3, emission=0.5)
    amber = mdl.mat("Emissive amber indicators", (0.95, 0.39, 0.045), 0.15, 0.35, emission=0.8)

    root = "rocket_cart"
    hull = mdl.node("hull", root)

    # ---- the missile: the pack's mesh, its centre of mass over the axle at the axis height
    ms = pack.get("SK_Veh_ScudTruck_Missle_01", "Military_Green").apply(M)
    com = volume_centroid(ms)
    ms.v = ms.v - com + np.array([0.0, AXIS_Y, AXLE_Z])
    mdl.node("missile", hull, (0.0, AXIS_Y, AXLE_Z))
    mdl.add("missile", ms)
    nose_z, tail_z = float(ms.v[:, 2].min()), float(ms.v[:, 2].max())
    half_h = float(ms.v[:, 1].max() - AXIS_Y)

    # ---- frame: two rails, cross members, the A-frame drawbar
    z_front, z_rear = AXLE_Z - 3.30, AXLE_Z + 3.40
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * 0.55, RAIL_Y, (z_front + z_rear) / 2), (0.13, 0.17, z_rear - z_front), olive, "rail"))
    for z in (z_front, AXLE_Z - 1.6, AXLE_Z, AXLE_Z + 1.6, z_rear):
        mdl.add(hull, box((0, RAIL_Y, z), (1.23, 0.11, 0.11), olive_dark, "cross_member"))
    # drawbar: two arms from the frame front converge on the eye, sloping down to the eye's height; a single bar to the eye
    a_end = (0.0, EYE[1], EYE[2] + 0.55)
    for s_ in (-1, 1):
        mdl.add(hull, beam((s_ * 0.55, RAIL_Y, z_front), (s_ * 0.06, EYE[1], a_end[2] + 0.3), 0.12, olive, "drawbar_arm", depth=0.14))
    mdl.add(hull, beam((0, EYE[1], a_end[2] + 0.3), (0, EYE[1], EYE[2] + 0.10), 0.14, olive, "drawbar_bar", depth=0.14))
    mdl.add(hull, box((0, EYE[1], a_end[2] + 0.18), (0.30, 0.14, 0.12), olive_dark, "drawbar_junction"))
    mdl.add(hull, K.ring(EYE, 0.10, 0.052, 0.045, steel, 18, "y", "towing_eye"))
    mdl.add(hull, box((0, EYE[1], EYE[2] + 0.12), (0.10, 0.08, 0.10), steel, "towing_eye_neck"))
    # landing leg (a jockey stand) on the drawbar, folded down a little
    jz = nose_z + 0.60
    mdl.add(hull, cylinder((0, EYE[1] + 0.02, jz), (0, 0.08, jz), 0.045, metal, 10, "landing_leg"))
    mdl.add(hull, box((0, 0.045, jz), (0.26, 0.09, 0.30), metal, "landing_foot"))
    mdl.add(hull, box((0, EYE[1] + 0.02, jz), (0.12, 0.10, 0.16), olive_dark, "landing_clamp"))

    # ---- axle, springs, fenders
    mdl.add(hull, box((0, WHEEL_R, AXLE_Z), (TRACK - 0.30, 0.12, 0.12), metal, "axle"))
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * 0.80, WHEEL_R + 0.12, AXLE_Z), (0.09, 0.06, 1.15), metal, "leaf_spring"))
        mdl.add(hull, box((s_ * 0.80, WHEEL_R + 0.065, AXLE_Z), (0.12, 0.08, 0.14), metal, "spring_seat"))
        # fender over the wheel and a short skirt
        mdl.add(hull, box((s_ * (TRACK / 2), WHEEL_R + 0.55, AXLE_Z), (0.42, 0.05, 1.30), olive, "fender"))
        mdl.add(hull, box((s_ * (TRACK / 2 + s_ * 0.19), WHEEL_R + 0.45, AXLE_Z), (0.04, 0.22, 1.30), olive, "fender_lip"))
        mdl.add(hull, box((s_ * (TRACK / 2 - s_ * 0.19), WHEEL_R + 0.45, AXLE_Z), (0.04, 0.22, 1.30), olive, "fender_lip"))
        # fender brace to the rail
        mdl.add(hull, box((s_ * 0.80, WHEEL_R + 0.40, AXLE_Z - 0.55), (0.35 + 0.0, 0.05, 0.05), metal, "fender_brace"))

    # ---- cradle: two saddles (pad, two cheeks) and straps over the missile
    for dz in (-2.8, 2.8):
        z = AXLE_Z + dz
        pad_bot = AXIS_Y - BODY_HALF - 0.08                      # the pad's underside; its top touches the missile
        mdl.add(hull, box((0, (RAIL_Y + 0.085 + pad_bot) / 2, z), (0.84, pad_bot - RAIL_Y - 0.085 + 0.001, 0.46), olive_dark, "saddle_stand"))
        mdl.add(hull, box((0, pad_bot + 0.04, z), (1.00, 0.08, 0.50), olive, "saddle_pad"))
        for s_ in (-1, 1):
            mdl.add(hull, box((s_ * 0.54, AXIS_Y, z), (0.09, 0.72, 0.30), olive, "saddle_cheek"))
        mdl.add(hull, box((0, AXIS_Y + 0.485, z), (1.18, 0.05, 0.14), strap, "strap"))
        for s_ in (-1, 1):
            mdl.add(hull, box((s_ * 0.59, AXIS_Y + 0.44, z), (0.04, 0.10, 0.14), metal, "strap_buckle"))
    # gussets from the rails up to the pads
    for s_ in (-1, 1):
        mdl.add(hull, beam((s_ * 0.55, RAIL_Y, AXLE_Z - 2.8), (s_ * 0.42, AXIS_Y - 0.55, AXLE_Z - 2.8), 0.07, metal, "saddle_gusset"))
        mdl.add(hull, beam((s_ * 0.55, RAIL_Y, AXLE_Z + 2.8), (s_ * 0.42, AXIS_Y - 0.55, AXLE_Z + 2.8), 0.07, metal, "saddle_gusset"))

    # ---- lamp board at the rear of the frame
    lamp_z = z_rear + 0.10
    mdl.add(hull, box((0, RAIL_Y + 0.02, lamp_z), (2.10, 0.24, 0.06), olive_dark, "lamp_board"))
    for s_ in (-1, 1):
        mdl.add(hull, box((s_ * 0.92, RAIL_Y + 0.02, lamp_z + 0.04), (0.20, 0.14, 0.020), tail, "tail_lens"))
        mdl.add(hull, box((s_ * 0.66, RAIL_Y + 0.02, lamp_z + 0.04), (0.12, 0.10, 0.020), amber, "indicator_lens"))
        mdl.add(hull, box((s_ * 0.55, RAIL_Y, z_front - 0.05), (0.10, 0.10, 0.06), amber, "front_marker"))

    # ---- wheels: tyre, rim, hub; origin at the centre
    wheel_pos = {}
    for side, sx in (("l", -1), ("r", 1)):
        c = np.array([sx * TRACK / 2, WHEEL_R, AXLE_Z])
        nm = f"wheel_{side}"
        wheel_pos[nm] = c
        mdl.node(nm, root, c)
        parts = [K.ring(c, WHEEL_R, WHEEL_R * 0.62, WHEEL_W, rubber, 24, "x", "tyre"),
                 K.ring(c + [sx * 0.0, 0, 0], WHEEL_R * 0.64, 0.10, WHEEL_W * 0.7, olive_dark, 20, "x", "rim"),
                 K.disk(c + [sx * WHEEL_W * 0.36, 0, 0], WHEEL_R * 0.64, olive_dark, 20, "x", "rim_face"),
                 K.ring(c + [sx * (WHEEL_W * 0.36 + 0.025), 0, 0], 0.11, 0.05, 0.05, steel, 12, "x", "hub")]
        for p in parts:
            mdl.add(nm, p)
    LAMPS = {"light_tail_l": (-0.92, RAIL_Y + 0.02, lamp_z + 0.05), "light_tail_r": (0.92, RAIL_Y + 0.02, lamp_z + 0.05)}
    for k, p in LAMPS.items():
        mdl.node(k, hull, p)
    mdl.node("tow_eye", hull, EYE)
    mdl.node("axle", hull, (0.0, WHEEL_R, AXLE_Z))
    return mdl, dict(wheel_pos=wheel_pos, lamps=LAMPS, nose=nose_z, tail=tail_z, com=(0.0, AXIS_Y, AXLE_Z), ms=ms, half_h=half_h, z_front=z_front, z_rear=z_rear, lamp_z=lamp_z)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(OUT)
    K.ensure_pack_materials()
    mdl, info = build()
    size = mdl.write(out, "scripts/make_rocket_cart_model.py (Drycastle; the missile is Synty SIMPLE Military geometry, private)")
    pts = mdl.all_points()
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    print(f"{mdl.tris} triangles, {size} bytes -> {out}")
    print("bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    ms = info["ms"]
    facts = {
        "model": "pack:synty-simple-military/rocket_cart", "file": "synty/glb/simple-military/rocket_cart.glb",
        "bounds": {"min": r(lo), "max": r(hi)},
        "suggested": {"half_width": round(float(max(abs(lo[0]), hi[0])), 2), "half_length": round(float(max(-lo[2], hi[2])), 2), "height": round(float(ms.v[:, 1].max()), 2),
                      "hit_box_note": "the box is centred on the model origin; the missile's tail and the drawbar are the two ends"},
        "wheels": {"radius": WHEEL_R, "width": WHEEL_W, "track": TRACK, "centres": {k: r(v) for k, v in info["wheel_pos"].items()}},
        "axle": r((0.0, WHEEL_R, AXLE_Z)),
        "tow_eye": r(EYE), "drawbar_length_axle_to_eye": round(AXLE_Z - EYE[2], 3), "eye_height": EYE[1],
        "missile": {"node": "missile", "origin_com": r(info["com"]), "nose_z": round(info["nose"], 3), "tail_z": round(info["tail"], 3),
                    "length": round(info["tail"] - info["nose"], 3), "axis_height": AXIS_Y, "fin_height_above_axis": round(info["half_h"], 3)},
        "lamps": {k: r(p) for k, p in info["lamps"].items()},
        "coupling": "a tower's tow_hitch must meet this model's tow_eye: cart origin = tower origin + R(yaw) * (tower.tow_hitch - cart.tow_eye) (both vehicle frames, y equal at 0.62)",
    }
    FACTS.parent.mkdir(parents=True, exist_ok=True)
    FACTS.write_text(json.dumps(facts, indent=1) + "\n")
    print("facts ->", FACTS)


if __name__ == "__main__":
    main()
