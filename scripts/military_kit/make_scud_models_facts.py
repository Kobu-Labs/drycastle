#!/usr/bin/env python3
"""Writes docs/research/scud-models-facts.md from docs/research/scud-models-facts.d/{humvee,scud,rocket_cart}.json, which the model scripts
(make_humvee_model.py, make_scud_model.py, make_rocket_cart_model.py) write. Numbers only, no pack geometry. Standard library only:

    python3 -I scripts/military_kit/make_scud_models_facts.py
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
D = ROOT / "docs" / "research" / "scud-models-facts.d"


def v(x):
    return "[" + ", ".join(("%.3f" % a).rstrip("0").rstrip(".") if a else "0" for a in x) + "]"


def glass_block(panes):
    lines = []
    for p in panes:
        lines.append('{ "id": "%s", "centre": %s, "normal": %s, "size": %s, "outline": [%s] }' % (p["id"], v(p["centre"]), v(p["normal"]), v(p["size"]), ", ".join(v(q) for q in p["outline"])))
    return ",\n".join("  " + l for l in lines)


def kv(d, indent=""):
    return "\n".join(f"{indent}- `{k}` {v(x) if isinstance(x, list) else x}" for k, x in d.items())


def main():
    h = json.loads((D / "humvee.json").read_text())
    s = json.loads((D / "scud.json").read_text())
    c = json.loads((D / "rocket_cart.json").read_text())
    out = []
    w = out.append
    w("# Scud, Humvee and rocket cart: model facts")
    w("")
    w("Written by `scripts/military_kit/make_scud_models_facts.py` from the numbers the three model scripts print (`docs/research/scud-models-facts.d/*.json`). For the `vehicles.json` entries.")
    w("The GLBs are PRIVATE (Synty SIMPLE Military geometry): `assets-private/synty/glb/simple-military/`, manifest ids `pack:synty-simple-military/<name>`, built by `scripts/military_kit/build_simple_military.sh`.")
    w("All numbers are in the vehicle frame: metres, x right, y up, forward is -Z, origin on the ground at the footprint centre. `model_scale` 1, `model_yaw_deg` 0, `model_offset` [0, 0, 0] for all of them.")
    w("The models are loaded as pack prefabs (`MapLoader.LoadPrefab`): the pack's palette texture comes from `glb/simple-military/materials.json`; every other material (glass, lamps, olive paint) is in the GLB. `paint_material`/`paints` are not offered for the Humvee and the Scud (their body wears the pack's palette texture, not a flat colour; a tint would multiply the whole atlas). The cart's paint is the flat material `Olive` (`paint_material: \"Olive\"` works).")
    w("")
    w("Limit to know first: `VehicleSeat.MaxSeats` is 4 (`godot/ShooterCore/Vehicles/VehicleRules.cs`). The Humvee has five seats (driver, front passenger, two rear passengers, roof gunner): `seats` holds at most 4 records until that constant, `SeatsRecord` and `SeatCodec` are widened. The model carries all five seat nodes.")
    w("")
    # ---------------------------------------------------------------- humvee
    w("## humvee")
    w("")
    w(f"- model `{h['model']}`; scale from the pack: body x 0.735, y 0.60, z 0.657 (the pack car is 7.0 m long, 2.94 m wide at the body, 3.1 m to the roof), wheels uniformly shrunk to radius 0.426.")
    sg = h["suggested"]
    w(f"- hit box: `half_width` {sg['half_width']}, `half_length` {sg['half_length']}, `height` {sg['height']} (roof). Sticking out of it: the pintle hook (to z +{h['bounds']['max'][2]}), the roof gunner and gun (to y 2.4), two whip antennas (to y {h['bounds']['max'][1]}), mirrors (x +-{h['bounds']['max'][0]}).")
    w(f"- model bounds: min {v(h['bounds']['min'])}, max {v(h['bounds']['max'])}.")
    wh = h["wheels"]
    w(f"- wheels: radius {wh['radius']}, wheelbase {wh['wheelbase']}, track {wh['track']}; centres " + ", ".join(f"`wheel_{k}` {v(p)}" for k, p in wh["centres"].items()) + ". Front wheels sit under `wheel_fl_steer`, `wheel_fr_steer` (same position), `wheel_*` are siblings of `hull`. Wheel width 0.34.")
    w(f"- tow hitch (pintle hook, empty node `tow_hitch`, mesh `tow_hook`): {v(h['tow_hitch'])}, height 0.62 (the rocket cart's eye is at the same height).")
    w("- seats (feet, `seats[].offset`; node `seat_*` in the GLB):")
    roles = {"seat_driver": "driver, front left", "seat_passenger_1": "front passenger, front right", "seat_passenger_2": "rear left", "seat_passenger_3": "rear right",
             "seat_gunner": "roof gunner, standing in the ring (`standing`/`exposed`; the head and shoulders are above the roof)"}
    for k, p in h["seats"].items():
        w(f"  - `{k}` {v(p)}: {roles[k]}")
    w("- suggested cockpit eye per seat (vehicle frame; eye = seat + [0, 1.05, 0.09], the Golf's rule; the camera sees through the hidden wheel meshes):")
    for k, p in h["cockpit_eye"].items():
        w(f"  - `{k}` {v(p)}")
    w("  - hide in the cockpit view: `steering_wheel_rim`, `steering_wheel_spokes` (the rig draws a turning wheel). The windscreen top is at y 1.79, the roof inside at about 1.83: the eye is below it.")
    rg = h["roof_gun"]
    w("- roof gun (the amphibious transport's ring-gun node pattern; `door_gun` in `vehicles.json`):")
    w(f"  - `gun_mount` (yaw about Y, rest 0 = forward -Z) at the ring centre {v(rg['ring_centre'])}; `gun` (pitch about X) at {v(rg['gun_pivot'])}; muzzle at {v(rg['muzzle'])} (`muzzle_length` {rg['muzzle_length_from_pivot']} from the pivot); `gunner_sight` {v(rg['gunner_sight'])}, i.e. `eye` relative to the gun pivot [0, 0.22, 0.55].")
    w("  - `door_gun.pivot` = the ring centre; `ring_radius` 0.55 (gun pivot ahead of the ring centre); `outward_yaw_deg` 0. The roof opening is 0.84 m wide, 0.67 m long, centred on the ring centre; the ring rail is a 0.5 m radius circle on the roof.")
    w("  - node chain: `gun_mount` > `gun` > `barrel` (recoil along +Z) > `muzzle`; `gunner_sight` under `gun`. All under `hull`.")
    w("- lamps (`lights`): head " + ", ".join(v(h["lamps"][k]) for k in ("light_head_l", "light_head_r")) + "; tail " + ", ".join(v(h["lamps"][k]) for k in ("light_tail_l", "light_tail_r")) + ". Materials `Emissive warm headlight lens`, `Emissive red tail lamps`, `Emissive amber indicators`, `Emissive reverse lamps` are in the model, so no extra lens meshes are made.")
    w("- glass (`vehicles.json` `glass`, 6 panes; node `glass_N` = list index; windows are real openings, nothing behind a shattered pane):")
    w("")
    w("```json")
    w(glass_block(h["glass"]))
    w("```")
    w("")
    w("- nodes: `humvee` > `hull` (body, interior, glass, lamps, seats, hook, gun) and `wheel_fl_steer` > `wheel_fl`, `wheel_fr_steer` > `wheel_fr`, `wheel_rl`, `wheel_rr` as siblings of `hull`.")
    w("- interior (all under `hull`): floor at y 0.53, tunnel, dash, `steering_wheel_rim`/`steering_wheel_spokes` (centre [-0.62, 1.10, -0.60], tilted 25 degrees), four seats (cushion top y 0.85), door cards, rear partition at z 0.955. The body shell is two-sided (pack material `Military_Green_2s`).")
    w("")
    # ---------------------------------------------------------------- scud
    w("## scud")
    w("")
    w(f"- model `{s['model']}`, flying missile `{s['missile_model']}`; scale from the pack {s['scale_from_pack']} (pack truck 14.8 m long, 3.9 m wide): {s['bounds']['max'][2] - s['bounds']['min'][2]:.1f} m long with the lying missile, {s['bounds']['max'][0] * 2:.2f} m wide.")
    sg = s["suggested"]
    w(f"- hit box: `half_width` {sg['half_width']}, `half_length` {sg['half_length']} (cab front z {s['lamps']['light_head_l'][2]}, rear bumper z {s['lamps']['light_tail_l'][2]}, the missile's nose reaches {s['bounds']['min'][2]} and its tail {s['rack']['missile_tail_from_origin'] + s['rack']['missile_origin_com'][2]:.2f} when lying, the rear frame bars z {s['bounds']['max'][2]}), `height` {sg['height_body']} (body) to {sg['height_with_lying_missile']} (top of the lying missile; the rack and the fins reach {sg['height_with_missile_fins']}). Model bounds: min {v(s['bounds']['min'])}, max {v(s['bounds']['max'])}.")
    wh = s["wheels"]
    w(f"- wheels: 4 axles, radius {wh['radius']}, track {wh['track']}, tyre width 0.44; axle z " + ", ".join(f"{k} {z}" for k, z in wh["axle_z"].items()) + f" (first to last {wh['wheelbase_first_to_last']}). Names: `wheel_l1..l4`, `wheel_r1..r4` (l = -x, the number counts from the front). Axles 1 and 2 steer: `wheel_l1_steer`, `wheel_r1_steer`, `wheel_l2_steer`, `wheel_r2_steer` (the parent at the wheel centre, turn about Y), the wheel under it rolls about X. Axles 3 and 4 are fixed. `wheel_*` are siblings of `hull`. Steering geometry (the game's `atan(wheelbase / radius)` rule) wants a front-axle-to-rear-axle distance: take the middle of the steered pair to the middle of the fixed pair, {(wh['axle_z']['axle3'] + wh['axle_z']['axle4']) / 2 - (wh['axle_z']['axle1'] + wh['axle_z']['axle2']) / 2:.3f}.")
    w("  - centres: " + ", ".join(f"`{k}` {v(p)}" for k, p in wh["centres"].items()))
    w("- seats (feet): " + ", ".join(f"`{k}` {v(p)}" for k, p in s["seats"].items()) + ". Driver in the left half cab, operator in the right one (the missile's nose lies between the two half cabs, as on the real truck).")
    w("- suggested cockpit eye (seat + [0, 1.05, 0.09]): " + ", ".join(f"`{k}` {v(p)}" for k, p in s["cockpit_eye"].items()) + f". Steering wheel centre {v(s['steering_wheel'])}; hide `steering_wheel_rim`, `steering_wheel_spokes`. The cab roof inside is at y 2.66, the windscreen spans y 1.99-2.42, side windows 2.11-2.55, so the eye is inside the window band.")
    w("- lamps (`lights`): head " + ", ".join(v(s["lamps"][k]) for k in ("light_head_l", "light_head_r")) + "; tail " + ", ".join(v(s["lamps"][k]) for k in ("light_tail_l", "light_tail_r")) + ". Emissive head and tail lens materials are in the model.")
    rk = s["rack"]
    w("- rack and missile:")
    w(f"  - node `rack` (under `hull`): pivot on the rear hinge axis at {v(rk['pivot_hinge'])}. Rotation about X: 0 = lying, pointing forward over the cab; +90 degrees = vertical, the tail down (at 90 degrees the missile axis is at z {rk['pivot_hinge'][2] + 0.477:.3f}, the tail end y 1.08 above the ground and the nose y 12.2: measured by the check script). The model does not animate it.")
    w(f"  - node `missile` under `rack`: origin at the missile's centre of mass, {v(rk['missile_origin_com'])} in vehicle frame when lying (axis height {rk['missile_axis_height_lying']}); nose along -Z. Hide it at launch and spawn the missile entity at its world position with the rack's direction.")
    w(f"  - the missile: length {rk['missile_length']}, body section 0.93 wide x 0.88 high (fins reach 1.81 high); from the origin the nose is {rk['missile_nose_from_origin']} ahead and the tail {rk['missile_tail_from_origin']} behind. Rack length {rk['rack_length']}.")
    w("  - `scud_missile.glb`: one mesh node `missile` at the origin (its centre of mass), nose along -Z, tail +Z; size [0.93, 1.81, 11.13] (x, y, z); fins are in the vertical (y) plane. Same material as the truck.")
    w("- glass (`vehicles.json` `glass`, 8 panes; the two small windows on the inner walls of the half cabs next to the missile are painted glass-coloured meshes, not panes):")
    w("")
    w("```json")
    w(glass_block(s["glass"]))
    w("```")
    w("")
    w("- nodes: `scud` > `hull` (body, cab, glass, `rack` > `missile`, seats, lamps) and the wheel nodes above as siblings of `hull`.")
    w("")
    # ---------------------------------------------------------------- cart
    w("## rocket_cart")
    w("")
    sg = c["suggested"]
    w(f"- model `{c['model']}`; generated cart (olive, flat materials `Olive`, `OliveDark`, `DarkMetal`, `Steel`, `Rubber`, `Khaki`) carrying a copy of the pack's missile mesh (same scale as the truck's, 11.1 m). Model bounds: min {v(c['bounds']['min'])}, max {v(c['bounds']['max'])}.")
    w(f"- hit box: `half_width` {sg['half_width']}, `half_length` {sg['half_length']}, `height` {sg['height']} (the missile's fin tips).")
    wh = c["wheels"]
    w(f"- single axle at {v(c['axle'])} (node `axle`): two wheels `wheel_l` {v(wh['centres']['wheel_l'])}, `wheel_r` {v(wh['centres']['wheel_r'])}; radius {wh['radius']}, tyre width {wh['width']}, track {wh['track']}; siblings of `hull`, roll about X, no steering.")
    w(f"- drawbar: runs forward under the missile's nose to a towing eye (node `tow_eye`, a horizontal ring) at {v(c['tow_eye'])}; length from the axle to the eye {c['drawbar_length_axle_to_eye']} m, eye height {c['eye_height']} (the Humvee's `tow_hitch` height). A landing leg stands under the drawbar, 0.6 m behind the missile's nose.")
    w(f"- coupling: {c['coupling']}. With the Humvee's hitch at [0, 0.62, 2.44] the cart's origin is 8.64 m behind the Humvee's origin, the cart's axle 10.04 m behind it.")
    ms = c["missile"]
    w(f"- cradle: two saddles with straps at z {c['axle'][2] - 2.8:.2f} and {c['axle'][2] + 2.8:.2f}, the frame rails at y 0.80. Node `missile` (under `hull`): origin at the missile's centre of mass {v(ms['origin_com'])} (over the axle), axis height {ms['axis_height']}, nose z {ms['nose_z']}, tail z {ms['tail_z']}, length {ms['length']}; hide it when the cart is empty.")
    w("- lamps: " + ", ".join(f"`{k}` {v(p)}" for k, p in c["lamps"].items()) + " (rear lamp board, red `Emissive red tail lamps` and amber `Emissive amber indicators` lenses in the model). No head lamps, no glass, no seats.")
    w("- nodes: `rocket_cart` > `hull` (frame, drawbar, cradle, fenders, lamps, `missile`, `tow_eye`, `axle`, `light_tail_*`), `wheel_l`, `wheel_r` as siblings of `hull`.")
    w("")
    (ROOT / "docs" / "research" / "scud-models-facts.md").write_text("\n".join(out) + "\n")
    print("wrote docs/research/scud-models-facts.md")


if __name__ == "__main__":
    main()
