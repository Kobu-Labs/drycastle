#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/utility_helicopter.glb: the utility helicopter, a two-blade single-engine
transport with an open cabin, skids and a pintle-mounted door gun on the right side.

Original geometry written by hand as code (no third-party model, no texture); standard library only. The mesh helpers
(box, loft, lathe, ...) are scripts/lib/meshkit.py's:

    python3 -I scripts/models/make_utility_helicopter_model.py [out.glb]

Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground under the rotor mast.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0].

Node tree (every moving part is its own node, its origin is its pivot):

    utility_helicopter
      hull                       nose, cockpit, open cabin, engine cowl, tail boom, fin, skids (static; was `fuselage`, renamed for the contract: #325)
        rotor_main               main rotor, origin at the hub on the mast (spin about Y)
        rotor_tail               tail rotor, origin at its hub on the left of the fin (spin about X)
        gun_mount                door gun yaw pivot on top of the pintle (rotate about Y). The node is turned so that its
                                 own -Z points out of the right door: a yaw of 0 aims straight out, + turns towards the nose
          gun                    pitch pivot (rotate about X; + raises the muzzle), origin on the cradle pin
            barrel               origin at the barrel root (recoil along +Z)
              muzzle             empty: muzzle tip
            gunner_sight         empty: the gunner's camera point, behind and above the receiver
        seat_pilot               empty: pilot seat (feet), front right
        seat_gunner              empty: door gunner seat (feet), behind the gun
        light_head               empty: landing light under the nose
        light_tail               empty: position light on the fin
        light_nav_l, light_nav_r empties: red (left) and green (right) navigation lights
        glass_0 .. glass_3       the breakable glass panes (two windscreen halves, two cockpit doors), one node each, numbered as the
                                 `glass` list of vehicles.json (the script rewrites that list, outline polygons included)
        smoke_engine             empty: the smoke point at the engine exhaust
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from glass import GlassSet, vehicles_json_path
from glb import materials_from, write_glb
from meshkit import Node, add, both, box, cyl, extrude, lathe, loft, panel, report, rot, slab, tube

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength, alpha)
    "Olive": (0.36, 0.40, 0.22, 0.85, 0.0, 0, 1),        # main paint
    "OliveDark": (0.24, 0.28, 0.16, 0.85, 0.0, 0, 1),    # panels, cowl, frames
    "OliveLight": (0.47, 0.50, 0.30, 0.85, 0.0, 0, 1),   # doors, hatches
    "Khaki": (0.62, 0.55, 0.36, 0.9, 0.0, 0, 1),         # seat canvas, soundproofing, ammunition box
    "DarkMetal": (0.13, 0.14, 0.14, 0.7, 0.2, 0, 1),     # skids, floor, blades, exhaust
    "Gunmetal": (0.27, 0.29, 0.31, 0.5, 0.5, 0, 1),      # gun
    "Steel": (0.62, 0.64, 0.64, 0.5, 0.4, 0, 1),         # hub, mast, pintle
    "Glass": (0.42, 0.58, 0.66, 0.15, 0.0, 0, 0.34),     # windscreen and windows (see-through)
    "Yellow": (0.90, 0.70, 0.12, 0.8, 0.0, 0, 1),        # blade tips, ammunition band
    "Red": (0.72, 0.13, 0.10, 0.7, 0.0, 0.3, 1),         # left navigation light, tail rotor warning band
    "Green": (0.12, 0.62, 0.25, 0.7, 0.0, 0.3, 1),       # right navigation light
    "Lamp": (0.98, 0.94, 0.75, 0.4, 0.0, 0.6, 1),        # landing light, position light
    "White": (0.86, 0.86, 0.82, 0.8, 0.0, 0, 1),         # tail band
}


# ----------------------------------------------------------------------------- dimensions

FLOOR = 0.72                       # cabin and cockpit floor (top)
BELLY = 0.52                       # underside of the hull
ROOF0, ROOF1 = 2.12, 2.25          # roof slab
HALF = 1.20                        # half width of the cabin
NOSE, COCKPIT, CABIN0, CABIN1 = -3.75, -2.75, -1.20, 1.30   # z: nose tip, instrument panel, cabin front and rear
HUB = (0.0, 3.40, 0.0)             # main rotor hub
ROTOR_R = 6.70                     # main rotor radius
TAIL_HUB = (-0.24, 3.10, 8.45)     # tail rotor hub (left of the fin)
TAIL_R = 1.25
SKID_X, SKID_Y = 1.05, 0.06
PINTLE = (1.30, 1.38, 0.15)        # door gun yaw pivot
TRUNNION = (1.30, 1.50, 0.15)      # door gun pitch pivot
BARREL_ROOT, BARREL_LEN = 0.26, 0.62


def octagon(z, w, y0, y1, cb, ct):
    """A hull section at z: half width w, from y0 to y1, chamfered below (cb) and above (ct)."""
    return [(-w + cb, y0, z), (w - cb, y0, z), (w, y0 + cb, z), (w, y1 - ct, z), (w - ct, y1, z), (-w + ct, y1, z), (-w, y1 - ct, z), (-w, y0 + cb, z)]


GLASS = GlassSet("utility_helicopter")


def build():
    root = Node("utility_helicopter")
    hull = root.child("hull")

    # ---- belly and floor: one tub under the cockpit and the cabin
    tub = [octagon(COCKPIT, 1.14, 0.56, FLOOR, 0.16, 0.01), octagon(-2.20, HALF, BELLY, FLOOR, 0.16, 0.01),
           octagon(CABIN0, HALF, BELLY, FLOOR, 0.16, 0.01), octagon(CABIN1 + 0.10, HALF, BELLY, FLOOR, 0.16, 0.01)]
    hull.add(loft("Olive", tub))
    hull.add(box("DarkMetal", -1.10, 1.10, FLOOR, FLOOR + 0.012, COCKPIT + 0.10, CABIN1))   # anti-slip floor

    # ---- nose: a solid cone up to the instrument panel
    nose = [octagon(NOSE, 0.24, 0.98, 1.24, 0.08, 0.08), octagon(-3.50, 0.66, 0.74, 1.42, 0.16, 0.14),
            octagon(-3.05, 1.00, 0.60, 1.50, 0.18, 0.12), octagon(COCKPIT, 1.14, 0.56, 1.50, 0.16, 0.06)]
    hull.add(loft("Olive", nose))
    hull.add(lathe((0, 1.10, NOSE), (0, 0, -1), [(0, 0.20, "OliveDark"), (0.05, 0.17), (0.09, 0.08), (0.10, 0.0)], 8))   # nose cap
    for i0, i1 in ((1, 2), (0, 7)):   # chin windows, on the lower corner faces of the nose
        hull.add(panel("Glass", nose[1][i0], nose[1][i1], nose[2][i1], nose[2][i0], 0.12, 0.88, 0.12, 0.88, 0.012, (0, 1.05, -3.25)))
    hull.add(box("DarkMetal", -0.62, 0.62, 1.10, 1.56, COCKPIT - 0.02, COCKPIT + 0.10, c=0.04, axis='z', e=0))   # instrument panel
    hull.add(box("DarkMetal", -0.16, 0.16, FLOOR, 1.10, COCKPIT + 0.05, -2.15, c=0.03, axis='x', e=0))           # centre console

    # ---- cockpit: sills, doors, posts, glazing (the glass is see-through, so the pilot shows)
    door_panes, screen_panes = [], []
    for sx in (1, -1):
        x = sx * (HALF - 0.03)
        hull.add(slab("OliveLight", (x, 1.38, COCKPIT), (x, 1.38, CABIN0 - 0.08), (x, FLOOR - 0.10, CABIN0 - 0.08), (x, FLOOR - 0.10, COCKPIT), 0.05))   # door, lower half
        hull.add(box("DarkMetal", x - 0.045, x + 0.045, 1.22, 1.26, -1.62, -1.46))                                                                    # door handle
        hull.add(tube("OliveDark", [(sx * 1.13, 1.50, COCKPIT), (sx * 1.15, ROOF0 + 0.05, -2.00)], 0.045, 6))                                         # A post
        hull.add(box("OliveDark", sx * HALF - 0.05 if sx > 0 else sx * HALF - 0.03, sx * HALF + 0.03 if sx > 0 else sx * HALF + 0.05, FLOOR, ROOF0, CABIN0 - 0.08, CABIN0))   # B post
        door = ((x, 1.40, COCKPIT + 0.06), (x, ROOF0, -1.98), (x, ROOF0, CABIN0 - 0.10), (x, 1.40, CABIN0 - 0.10))
        door_panes.append(("door_right" if sx > 0 else "door_left", [slab("Glass", *door, 0.012)], [(p[0] + sx * 0.006, p[1], p[2]) for p in door]))   # door window
    for sx in (1, -1):   # windscreen, two panes either side of a centre post
        screen = ((sx * 0.05, 1.52, COCKPIT), (sx * 1.09, 1.52, COCKPIT), (sx * 1.11, ROOF0 + 0.04, -2.00), (sx * 0.05, ROOF0 + 0.04, -2.00))
        screen_panes.append(("windscreen_right" if sx > 0 else "windscreen_left", [slab("Glass", *screen, 0.012)], screen))
    # the breakable panes are nodes of their own (glass_N, see GlassSet): the two windscreen halves first, then the cockpit doors
    for pid, slabs, outline in sorted(screen_panes, key=lambda q: q[0] != "windscreen_left") + sorted(door_panes, key=lambda q: q[0] != "door_left"):
        GLASS.pane(hull, pid, slabs, outline)
    hull.add(tube("OliveDark", [(0, 1.50, COCKPIT), (0, ROOF0 + 0.05, -2.00)], 0.04, 6))
    hull.add(box("OliveDark", -1.16, 1.16, ROOF0 - 0.02, ROOF0 + 0.06, -2.06, -1.98))   # windscreen header

    # ---- roof over the cockpit and the cabin
    hull.add(box("Olive", -HALF, HALF, ROOF0, ROOF1, -2.04, CABIN1 + 0.10, c=0.06, axis='z', e=0.03))
    for sx in (1, -1):   # cockpit overhead windows
        hull.add(slab("Glass", (sx * 0.25, ROOF1 + 0.004, -1.90), (sx * 0.95, ROOF1 + 0.004, -1.90), (sx * 0.95, ROOF1 + 0.004, -1.40), (sx * 0.25, ROOF1 + 0.004, -1.40), 0.01))

    # ---- pilot seats (armoured buckets) and the cyclic sticks
    for sx in (1, -1):
        x = sx * 0.55
        hull.add(box("OliveDark", x - 0.27, x + 0.27, FLOOR, 1.02, -2.02, -1.52, c=0.04, axis='x', e=0.02))
        hull.add(box("Khaki", x - 0.23, x + 0.23, 1.02, 1.08, -2.00, -1.56, c=0.02, axis='x', e=0))
        hull.add(rot(box("OliveDark", x - 0.27, x + 0.27, 1.02, 1.86, -1.56, -1.47, c=0.03, axis='y', e=0), 'x', -7, (x, 1.02, -1.5)))
        hull.add(both(box("OliveDark", 0.26, 0.31, 1.00, 1.45, -1.95, -1.50)) if sx > 0 else [])    # side armour of the right seat (mirrored for the left)
        hull.add(cyl("DarkMetal", (x, FLOOR, -2.25), (x, 1.18, -2.18), 0.018, 5, smooth=False), cyl("DarkMetal", (x, 1.18, -2.18), (x, 1.26, -2.20), 0.028, 5, smooth=False))

    # ---- open cabin: rear wall, a troop bench, the gunner's seat; nothing closes the sides
    # (the two openings are the `open_sides` of this vehicle in vehicles.json, written by hand: x = +-HALF, z from CABIN0 to the rear
    #  door posts, y from the sill to the door rail; bullets fly in and out through them. Change them there when these numbers change)
    hull.add(box("Olive", -HALF, HALF, FLOOR, ROOF0, CABIN1, CABIN1 + 0.10))
    hull.add(box("Khaki", -1.08, 1.08, 1.00, 1.98, CABIN1 - 0.03, CABIN1, c=0.05, axis='z', e=0))                # quilted soundproofing
    hull.add(box("Khaki", -1.08, 0.10, 1.04, 1.10, 0.86, CABIN1 - 0.03, c=0.02, axis='x', e=0))                  # bench canvas
    hull.add(tube("DarkMetal", [(-1.08, FLOOR, 0.88), (-1.08, 1.05, 0.88), (0.10, 1.05, 0.88), (0.10, FLOOR, 0.88)], 0.02, 5))
    hull.add(box("OliveDark", 0.22, 0.56, FLOOR, 0.98, -0.07, 0.37, c=0.03, axis='y', e=0))                      # gunner's seat (faces the door)
    hull.add(box("Khaki", 0.24, 0.54, 0.98, 1.03, -0.05, 0.35, c=0.02, axis='y', e=0))
    hull.add(box("OliveDark", 0.18, 0.24, 0.98, 1.45, -0.05, 0.35))
    for sx in (1, -1):   # door sills and the rails the doors would slide on
        hull.add(box("DarkMetal", sx * HALF - 0.03, sx * HALF + 0.03, FLOOR - 0.03, FLOOR + 0.03, CABIN0, CABIN1))
        hull.add(box("OliveDark", sx * HALF - 0.025, sx * HALF + 0.025, ROOF0 - 0.06, ROOF0, CABIN0, CABIN1))
    hull.add(both(box("OliveDark", HALF - 0.05, HALF + 0.03, FLOOR, ROOF0, CABIN1 - 0.08, CABIN1)))                # rear door posts

    # ---- engine and transmission: cowl on the roof, housing behind the cabin, exhaust
    cowl = [octagon(-0.85, 0.30, ROOF1 - 0.02, 2.55, 0.02, 0.10), octagon(-0.35, 0.56, ROOF1 - 0.02, 2.86, 0.02, 0.16),
            octagon(1.40, 0.60, ROOF1 - 0.02, 2.92, 0.02, 0.18), octagon(2.55, 0.42, 2.02, 2.80, 0.08, 0.16), octagon(3.05, 0.26, 2.10, 2.60, 0.08, 0.10)]
    hull.add(loft("OliveDark", cowl))
    for sx in (1, -1):   # intake grilles and an access hatch on each side of the cowl
        for k in range(4):
            hull.add(box("DarkMetal", sx * 0.59 - 0.012, sx * 0.59 + 0.012, 2.36 + k * 0.09, 2.41 + k * 0.09, 0.45, 1.25))
        hull.add(box("OliveLight", sx * 0.565 - 0.012, sx * 0.565 + 0.012, 2.34, 2.74, -0.20, 0.30, c=0.03, axis='x', e=0))
    hull.add(lathe((0, 2.52, 2.95), (0, 0.18, 1), [(0, 0.0, "DarkMetal"), (0, 0.19), (0.34, 0.17), (0.34, 0.13), (0.06, 0.13), (0.06, 0.0)], 8))   # exhaust
    body = [octagon(CABIN1 + 0.10, HALF, BELLY, ROOF1, 0.16, 0.06), octagon(2.05, 0.86, 0.86, 2.20, 0.18, 0.10),
            octagon(2.75, 0.44, 1.34, 2.10, 0.14, 0.10), octagon(3.20, 0.30, 1.48, 2.02, 0.10, 0.10)]
    hull.add(loft("Olive", body))
    hull.add(both(box("OliveLight", 0.99, 1.012, 1.00, 1.70, 1.52, 1.92, c=0.04, axis='x', e=0)))   # baggage hatch (approx. on the taper)

    # ---- tail: boom, stabiliser, fin, skid, tail rotor gearbox
    hull.add(cyl("Olive", (0, 1.75, 3.05), (0, 2.06, 8.20), 0.30, 10, r1=0.13))
    hull.add(cyl("White", (0, 1.985, 6.95), (0, 2.005, 7.25), 0.176, 10, r1=0.166))                 # tail band
    hull.add(box("Olive", -1.15, 1.15, 1.90, 1.95, 5.95, 6.55, c=0.02, axis='x', e=0.02))            # horizontal stabiliser
    hull.add(extrude("Olive", [(7.45, 2.02), (8.25, 1.98), (8.82, 3.62), (8.36, 3.62)], 'x', -0.055, 0.055, bevel=0.02))   # fin
    hull.add(tube("DarkMetal", [(0, 1.96, 7.70), (0, 1.52, 8.35)], 0.022, 5))                         # tail skid
    hull.add(cyl("OliveDark", (0.0, TAIL_HUB[1], TAIL_HUB[2]), (TAIL_HUB[0] + 0.05, TAIL_HUB[1], TAIL_HUB[2]), 0.10, 8))
    hull.add(tube("OliveDark", [(0, 2.09, 3.20), (0, 2.20, 7.55), (0, 3.05, 8.04)], 0.035, 5))        # tail rotor drive shaft cover, along the boom and up the fin

    # ---- skids: two tubes with turned-up toes, two cross tubes up to the belly
    for sx in (1, -1):
        x = sx * SKID_X
        hull.add(tube("DarkMetal", [(x, 0.30, -2.42), (x, SKID_Y + 0.05, -2.10), (x, SKID_Y, -1.85), (x, SKID_Y, 1.75), (x, SKID_Y + 0.03, 1.95)], 0.05, 6))
    for z in (-1.15, 0.95):
        hull.add(tube("DarkMetal", [(-SKID_X, SKID_Y, z), (-0.92, 0.34, z), (-0.55, BELLY + 0.02, z), (0.55, BELLY + 0.02, z), (0.92, 0.34, z), (SKID_X, SKID_Y, z)], 0.04, 6))

    # ---- lights (meshes; the empties below mark them for the game)
    hull.add(lathe((0, 0.60, -3.02), (0, -0.35, -1), [(0, 0.0, "DarkMetal"), (0, 0.10), (0.05, 0.10, "Lamp"), (0.07, 0.0)], 8))   # landing light
    hull.add(box("Red", -HALF - 0.035, -HALF + 0.005, 1.28, 1.36, -2.62, -2.50), box("Green", HALF - 0.005, HALF + 0.035, 1.28, 1.36, -2.62, -2.50))
    hull.add(lathe((0, 3.62, 8.59), (0, 1, 0), [(0, 0.035, "Lamp"), (0.05, 0.03), (0.07, 0.0)], 6))
    hull.add(lathe((0, 2.90, 1.00), (0, 1, 0), [(0, 0.04, "Red"), (0.06, 0.035), (0.08, 0.0)], 6))                     # beacon on the cowl

    # ---- main rotor: mast (static), then the turning head, two blades and the stabiliser bar
    hull.add(lathe((0, ROOF1, 0), (0, 1, 0), [(0.55, 0.0, "Steel"), (0.55, 0.16), (0.72, 0.16), (0.78, 0.07), (HUB[1] - ROOF1 - 0.12, 0.07)], 8))
    main = hull.child("rotor_main", HUB)
    main.add(lathe(add(HUB, (0, -0.14, 0)), (0, 1, 0), [(0, 0.0, "Steel"), (0, 0.13), (0.10, 0.17), (0.22, 0.17), (0.28, 0.10), (0.30, 0.0)], 10))
    for s in (1, -1):
        z0, z1 = s * 0.30, s * ROTOR_R
        lo, hi = min(z0, z1), max(z0, z1)
        tip0, tip1 = (ROTOR_R - 0.38, ROTOR_R) if s > 0 else (-ROTOR_R, -ROTOR_R + 0.38)
        main.add(box("Steel", -0.10, 0.10, HUB[1] - 0.045, HUB[1] + 0.045, min(s * 0.15, s * 0.75), max(s * 0.15, s * 0.75)))          # blade grip
        main.add(box("DarkMetal", -0.27, 0.27, HUB[1] - 0.03, HUB[1] + 0.03, lo + (0.45 if s > 0 else 0.38), hi - (0.38 if s > 0 else 0.45), c=0.025, axis='z', e=0))
        main.add(box("Yellow", -0.27, 0.27, HUB[1] - 0.03, HUB[1] + 0.03, tip0, tip1, c=0.025, axis='z', e=0))
    main.add(cyl("Steel", (-1.35, HUB[1] + 0.20, 0), (1.35, HUB[1] + 0.20, 0), 0.022, 5))                                            # stabiliser bar
    main.add(both(lathe((1.25, HUB[1] + 0.20, 0), (1, 0, 0), [(0, 0.0, "DarkMetal"), (0, 0.06), (0.22, 0.06), (0.22, 0.0)], 6)))
    main.add(cyl("Steel", (0, HUB[1] + 0.02, 0), (0, HUB[1] + 0.20, 0), 0.04, 6))

    # ---- tail rotor: two blades in the y-z plane, turning about X
    tail = hull.child("rotor_tail", TAIL_HUB)
    tx, ty, tz = TAIL_HUB
    tail.add(lathe((tx + 0.05, ty, tz), (-1, 0, 0), [(0, 0.0, "Steel"), (0, 0.07), (0.10, 0.07), (0.14, 0.03), (0.14, 0.0)], 8))
    for s in (1, -1):
        y0, y1 = (ty + 0.08, ty + TAIL_R) if s > 0 else (ty - TAIL_R, ty - 0.08)
        tail.add(box("DarkMetal", tx - 0.02, tx + 0.02, y0, y1, tz - 0.09, tz + 0.09, c=0.012, axis='y', e=0))
        tail.add(box("Red", tx - 0.022, tx + 0.022, y1 - 0.16 if s > 0 else y0, y1 if s > 0 else y0 + 0.16, tz - 0.092, tz + 0.092))

    # ---- door gun: a fixed pintle on the right door sill, then the mount (yaw), the gun (pitch) and the barrel (recoil)
    px, py, pz = PINTLE
    hull.add(lathe((px, FLOOR - 0.02, pz), (0, 1, 0), [(0, 0.0, "DarkMetal"), (0, 0.11), (0.04, 0.11), (0.07, 0.045, "Steel"), (py - FLOOR - 0.05, 0.035), (py - FLOOR - 0.05, 0.0)], 8))
    hull.add(tube("DarkMetal", [(px, FLOOR + 0.30, pz), (HALF - 0.10, FLOOR + 0.02, pz - 0.30)], 0.018, 5), tube("DarkMetal", [(px, FLOOR + 0.30, pz), (HALF - 0.10, FLOOR + 0.02, pz + 0.30)], 0.018, 5))
    mount = hull.child("gun_mount", PINTLE, yaw=-90.0)   # its -Z is the model's +X: out of the right door
    mount.add(lathe((px, py - 0.07, pz), (0, 1, 0), [(0, 0.0, "Steel"), (0, 0.06), (0.06, 0.06), (0.06, 0.0)], 8))
    for dz in (-0.075, 0.075):                             # the yoke's two cheeks, either side of the gun
        mount.add(box("Steel", px - 0.035, px + 0.035, py - 0.02, TRUNNION[1] + 0.05, pz + dz - 0.012, pz + dz + 0.012))
    gx, gy, gz = TRUNNION
    gun = mount.child("gun", TRUNNION)
    # the gun points along the model's +X; "behind" (towards the gunner) is -X
    gun.add(box("Gunmetal", gx - 0.42, gx + BARREL_ROOT, gy - 0.055, gy + 0.065, gz - 0.045, gz + 0.045, c=0.015, axis='x', e=0.01))     # receiver
    gun.add(box("DarkMetal", gx - 0.30, gx + 0.05, gy + 0.065, gy + 0.10, gz - 0.05, gz + 0.05, c=0.012, axis='x', e=0))                # feed cover
    gun.add(box("Khaki", gx - 0.40, gx - 0.12, gy - 0.20, gy + 0.02, gz - 0.21, gz - 0.05, c=0.02, axis='x', e=0))                      # ammunition box (towards the nose, behind the yoke)
    gun.add(box("Yellow", gx - 0.34, gx - 0.18, gy + 0.02, gy + 0.045, gz - 0.16, gz - 0.045))                                          # belt
    gun.add(tube("DarkMetal", [(gx - 0.42, gy, gz - 0.07), (gx - 0.56, gy, gz - 0.07), (gx - 0.56, gy - 0.12, gz - 0.07)], 0.014, 5))  # spade grips
    gun.add(tube("DarkMetal", [(gx - 0.42, gy, gz + 0.07), (gx - 0.56, gy, gz + 0.07), (gx - 0.56, gy - 0.12, gz + 0.07)], 0.014, 5))
    gun.add(box("DarkMetal", gx - 0.30, gx - 0.27, gy + 0.10, gy + 0.17, gz - 0.012, gz + 0.012))                                      # rear sight
    gun.add(cyl("Gunmetal", (gx + 0.05, gy - 0.075, gz), (gx + BARREL_ROOT + 0.30, gy - 0.075, gz), 0.02, 6))                            # gas tube
    barrel = gun.child("barrel", (gx + BARREL_ROOT, gy, gz))
    barrel.add(lathe((gx + BARREL_ROOT, gy, gz), (1, 0, 0), [(0, 0.028, "Gunmetal"), (0.30, 0.024), (BARREL_LEN - 0.10, 0.018, "DarkMetal"), (BARREL_LEN - 0.10, 0.030),
                                                             (BARREL_LEN, 0.026), (BARREL_LEN, 0.012), (BARREL_LEN - 0.03, 0.0)], 8))
    barrel.add(box("DarkMetal", gx + BARREL_ROOT + 0.42, gx + BARREL_ROOT + 0.44, gy + 0.02, gy + 0.075, gz - 0.008, gz + 0.008))      # front sight
    barrel.child("muzzle", (gx + BARREL_ROOT + BARREL_LEN, gy, gz))
    gun.child("gunner_sight", (gx - 0.62, gy + 0.22, gz))

    # ---- markers
    hull.child("seat_pilot", (0.55, FLOOR, -1.90), yaw=0.0)
    hull.child("seat_gunner", (0.40, FLOOR, 0.15), yaw=0.0)
    hull.child("light_head", (0.0, 0.58, -3.10), yaw=0.0)
    hull.child("light_tail", (0.0, 3.66, 8.59), yaw=0.0)
    hull.child("light_nav_l", (-HALF - 0.04, 1.32, -2.56), yaw=0.0)
    hull.child("light_nav_r", (HALF + 0.04, 1.32, -2.56), yaw=0.0)
    hull.child("smoke_engine", (0.0, 2.60, 3.30), yaw=0.0)
    return root


# ----------------------------------------------------------------------------- GLB

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parents[2] / "assets" / "drycastle-generated-models" / "utility_helicopter.glb")
    root = build()
    write_glb(root, out, materials_from(MATERIALS), "scripts/make_utility_helicopter_model.py (Drycastle)")
    report(root, 16, lambda n: f" yaw={n.yaw:.0f}")
    GLASS.report()
    if len(sys.argv) <= 1:
        GLASS.write_vehicles_json(vehicles_json_path())
        print("wrote the glass list of vehicles.json")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
