#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/amphibious_transport.glb: the amphibious transport, an open-topped
amphibious car. A boat-shaped hull on four wheels, no roof and no doors, low sides, a propeller at the stern, two
seats in front and a machine gun on a ring mount over the rear compartment.

Original geometry written by hand as code (no third-party model, no texture); standard library only. The mesh helpers
(box, loft, lathe, ...) are the flak script's, loaded from the file next to this one:

    python3 -I scripts/make_amphibious_model.py [out.glb]

Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground at the centre.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0].

Node tree (every moving part is its own node, its origin is its pivot):

    amphibious_transport
      hull                       the body: hull, decks, fenders, seats, ring, fittings (the game leans and bobs this node;
                                 the wheels stay on the ground under it)
        gun_mount                the ring's carriage, origin at the ring centre (rotate about Y; yaw 0 points at the bow,
                                 + turns to the left), all the way round
          gun                    pitch pivot (rotate about X; + raises the muzzle), on the yoke, RING_R ahead of the ring centre
            barrel               origin at the barrel root (recoil along +Z)
              muzzle             empty: muzzle tip
            gunner_sight         empty: the gunner's camera point, behind and above the receiver
        propeller                origin at its hub under the stern (spin about Z)
        seat_driver              empty: driver seat (feet), front left
        seat_passenger           empty: the fighting seat (feet), front right
        seat_gunner              empty: the gunner's place (feet), standing inside the ring
        light_head_l/r, light_tail_l/r   empties: lamp positions
        smoke_engine             empty: the smoke point over the engine (it is in the stern)
        wake_bow                 empty: where the bow cuts the water, at the waterline
        wake_stern_l/r           empties: the stern's corners at the waterline
      wheel_fl_steer, wheel_fr_steer   front steering pivots, origin at the wheel centre (rotate about Y)
        wheel_fl, wheel_fr       front wheels, origin at the wheel centre (spin about X)
      wheel_rl, wheel_rr         rear wheels, origin at the wheel centre (spin about X)

The waterline afloat is WATERLINE above the origin (vehicles.json drive.water.draft).
"""
import importlib.util
import json
import math
import struct
import sys
from pathlib import Path

_spec = importlib.util.spec_from_file_location("make_flak_model", Path(__file__).resolve().with_name("make_flak_model.py"))
kit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(kit)

add, sub, mul, dot, cross, unit = kit.add, kit.sub, kit.mul, kit.dot, kit.cross, kit.unit
loft, extrude, box, lathe, cyl, move, rot, mirror, both = kit.loft, kit.extrude, kit.box, kit.lathe, kit.cyl, kit.move, kit.rot, kit.mirror, kit.both
Node = kit.Node

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength)
    "Olive": (0.36, 0.40, 0.22, 0.85, 0.0, 0),        # main paint
    "OliveDark": (0.24, 0.28, 0.16, 0.85, 0.0, 0),    # inside of the hull, frames, louvres
    "OliveLight": (0.47, 0.50, 0.30, 0.85, 0.0, 0),   # deck hatches, fender tops
    "Khaki": (0.62, 0.55, 0.36, 0.9, 0.0, 0),         # seat canvas, ammunition boxes, the gunner's back pad
    "Wood": (0.52, 0.38, 0.22, 0.9, 0.0, 0),          # paddle, floor slats
    "DarkMetal": (0.13, 0.14, 0.14, 0.7, 0.2, 0),     # floor, axles, exhaust, dashboard
    "Gunmetal": (0.27, 0.29, 0.31, 0.5, 0.5, 0),      # gun
    "Steel": (0.62, 0.64, 0.64, 0.5, 0.4, 0),         # ring, hubs, propeller
    "Rubber": (0.07, 0.07, 0.075, 0.95, 0.0, 0),      # tyres, steering wheel rim
    "Yellow": (0.90, 0.70, 0.12, 0.8, 0.0, 0),        # ammunition belt
    "Red": (0.72, 0.13, 0.10, 0.7, 0.0, 0.3),         # tail lamps
    "Lamp": (0.98, 0.94, 0.75, 0.4, 0.0, 0.6),        # headlamp lenses, gauges
}

# ----------------------------------------------------------------------------- dimensions

WHEEL_R, WHEEL_X, WHEEL_HALF = 0.40, 0.98, 0.12      # tyre radius, track half width (wheel centre), half tyre width
AXLE_F, AXLE_R = -1.42, 1.30                         # z of the front and rear axle
FLOOR = 0.64                                         # top of the cockpit floor
WALL = 0.045                                         # thickness of the cockpit's side walls
COCKPIT0, COCKPIT1 = -1.05, 1.70                     # z: front bulkhead (dashboard) and rear bulkhead
WATERLINE = 0.56                                     # the surface afloat, above the origin
SEAT_X, SEAT_Z = 0.45, -0.37                         # the two front seats (feet), left and right of the centre line
RING = (0.0, 1.52, 1.00)                             # centre of the gun ring
RING_R = 0.45                                        # the gun's pitch pivot rides this far out from the ring centre
PIVOT = (0.0, 2.10, RING[2] - RING_R)                # gun pitch pivot with the mount at yaw 0 (pointing at the bow)
BARREL_ROOT, BARREL_LEN = 0.26, 0.70                 # along the gun from the pivot
PROP_HUB = (0.0, 0.47, 2.26)
PROP_R = 0.20

# Hull stations from the bow to the stern: z, then half widths and heights of the keel, the chine and the sheer (the top edge).
#            z      bk    yk    bc    yc    bs    ys
STATIONS = [(-2.25, 0.02, 0.98, 0.05, 1.02, 0.08, 1.12),
            (-2.00, 0.20, 0.70, 0.42, 0.80, 0.52, 1.12),
            (-1.60, 0.36, 0.50, 0.62, 0.60, 0.74, 1.10),
            (COCKPIT0, 0.45, 0.40, 0.70, 0.52, 0.84, 1.08),
            (0.00, 0.48, 0.38, 0.74, 0.50, 0.88, 1.05),
            (COCKPIT1, 0.46, 0.40, 0.70, 0.52, 0.84, 1.08),
            (2.05, 0.40, 0.50, 0.60, 0.60, 0.76, 1.10)]
CROWN = 0.04                                         # how far the decks rise along the centre line


def station(z):
    """The hull section at z, interpolated between the stations."""
    for a, b in zip(STATIONS, STATIONS[1:]):
        if a[0] <= z <= b[0]:
            t = (z - a[0]) / (b[0] - a[0])
            return tuple(a[i] + (b[i] - a[i]) * t for i in range(7))
    raise ValueError(z)


def side_x(z, y):
    """Half width of the hull's outside at height y (between the chine and the sheer)."""
    _, _, _, bc, yc, bs, ys = station(z)
    return bc + (bs - bc) * (y - yc) / (ys - yc)


def decked(s):
    """A closed section with the deck on top: keel, chines, sheers and the crown of the deck."""
    z, bk, yk, bc, yc, bs, ys = s
    return [(-bk, yk, z), (bk, yk, z), (bc, yc, z), (bs, ys, z), (0.0, ys + CROWN * min(1.0, bs / 0.5), z), (-bs, ys, z), (-bc, yc, z)]


def tub(s):
    """The hull under the cockpit floor: keel and chines, closed by the floor."""
    z, bk, yk, bc, yc, bs, ys = s
    bf = bc + (bs - bc) * (FLOOR - yc) / (ys - yc)
    return [(-bk, yk, z), (bk, yk, z), (bc, yc, z), (bf, FLOOR, z), (-bf, FLOOR, z), (-bc, yc, z)]


def wall(s):
    """The right side wall of the cockpit, from the floor to the sheer."""
    z, bk, yk, bc, yc, bs, ys = s
    bf = bc + (bs - bc) * (FLOOR - yc) / (ys - yc)
    return [(bf - WALL, FLOOR, z), (bf, FLOOR, z), (bs, ys, z), (bs - WALL, ys, z)]


def tube(mat, pts, r, n=6):
    """A bent tube through the points."""
    t = []
    for p, q in zip(pts, pts[1:]):
        t += cyl(mat, p, q, r, n)
    return t


def torus(mat, centre, axis, big, small, n=20, m=6):
    """A ring of tube radius small, big from the axis through centre."""
    prof = [(small * math.cos(2 * math.pi * k / m), big + small * math.sin(2 * math.pi * k / m)) for k in range(m + 1)]
    return lathe(centre, axis, prof, n, mat=mat)


def arc(zc, yc, r, a0, a1, n=8):
    """Points (z, y) on a circle about (zc, yc) from angle a0 to a1 (degrees; 0 is +z, 90 is up)."""
    return [(zc + r * math.cos(math.radians(a0 + (a1 - a0) * k / n)), yc + r * math.sin(math.radians(a0 + (a1 - a0) * k / n))) for k in range(n + 1)]


def wheel():
    """A wheel about the X axis at the origin; the outer face is at +x. Lugs on the tread and bolts on the hub, so it shows when it turns."""
    h = WHEEL_HALF
    prof = [(-h, 0.0, "Rubber"), (-h, WHEEL_R - 0.07), (-h + 0.03, WHEEL_R - 0.015), (h - 0.03, WHEEL_R - 0.015), (h, WHEEL_R - 0.07), (h, 0.25, "Olive"),
            (h - 0.05, 0.23), (h - 0.05, 0.11, "Steel"), (h + 0.01, 0.08), (h + 0.01, 0.0)]
    t = lathe((0, 0, 0), (1, 0, 0), prof, 14)
    for k in range(10):   # tread lugs, alternating between the two shoulders
        x0, x1 = (-h + 0.01, 0.01) if k % 2 == 0 else (-0.01, h - 0.01)
        lug = box("Rubber", x0, x1, WHEEL_R - 0.03, WHEEL_R, -0.045, 0.045)
        t += rot(lug, 'x', 36.0 * k)
    for k in range(5):    # wheel bolts
        t += rot(cyl("DarkMetal", (h - 0.05, 0.165, 0), (h - 0.02, 0.165, 0), 0.018, 5, smooth=False), 'x', 72.0 * k)
    return t


def fender(z_axle):
    """A mudguard over the right wheel at z_axle: an arched plate from the hull side to just past the tyre."""
    outer = arc(z_axle, WHEEL_R, WHEEL_R + 0.11, 12, 168, 9)
    inner = arc(z_axle, WHEEL_R, WHEEL_R + 0.075, 168, 12, 9)
    return extrude("Olive", outer + inner, 'x', 0.70, WHEEL_X + WHEEL_HALF + 0.035)


def build():
    root = Node("amphibious_transport")
    hull = root.child("hull")

    # ---- the hull: a decked bow, the open cockpit (a tub with side walls) and a decked stern
    i0 = next(i for i, s in enumerate(STATIONS) if s[0] == COCKPIT0)
    i1 = next(i for i, s in enumerate(STATIONS) if s[0] == COCKPIT1)
    hull.add(loft("Olive", [decked(s) for s in STATIONS[:i0 + 1]]))
    hull.add(loft("Olive", [tub(s) for s in STATIONS[i0:i1 + 1]]))
    hull.add(both(loft("Olive", [wall(s) for s in STATIONS[i0:i1 + 1]])))
    hull.add(loft("Olive", [decked(s) for s in STATIONS[i1:]]))
    hull.add(box("DarkMetal", -0.70, 0.70, FLOOR, FLOOR + 0.012, COCKPIT0 + 0.02, COCKPIT1 - 0.02))                 # anti-slip floor
    for sx in (1, -1):   # a rubbing strake along each side, under the sheer
        hull.add(tube("OliveDark", [(sx * (side_x(z, 0.96) + 0.012), 0.96, z) for z in (-1.95, -1.60, COCKPIT0, 0.0, COCKPIT1, 2.03)], 0.022, 5))
    hull.add(tube("DarkMetal", [(0, 0.99, -2.24), (0, 0.80, -2.12), (0, 0.60, -1.82), (0, 0.45, -1.30)], 0.02, 5))   # stem band
    hull.add(torus("Steel", (0, 1.00, -2.27), (1, 0, 0), 0.045, 0.012, 10, 5))                                      # towing eye

    # ---- bow deck: the spare wheel lies on it, a hatch behind it
    spare = rot(wheel(), 'z', 90)                       # axis up, the hub on top
    hull.add(move(spare, (0.0, 1.14 + WHEEL_HALF, -1.58)))
    hull.add(box("OliveLight", -0.30, 0.30, 1.105, 1.125, -1.16, -1.08, c=0.01, axis='x', e=0))                     # coaming at the cockpit's front edge
    hull.add(both(tube("DarkMetal", [(0.52, 1.10, -1.20), (0.52, 1.17, -1.24), (0.52, 1.17, -1.50), (0.52, 1.10, -1.54)], 0.012, 5)))   # grab handles

    # ---- stern deck: the engine is under it (louvres, a hatch), the exhaust comes out on the right
    hull.add(box("OliveLight", -0.50, 0.50, 1.11, 1.13, COCKPIT1 + 0.04, 2.00, c=0.02, axis='y', e=0))
    for k in range(4):
        z = COCKPIT1 + 0.09 + k * 0.065
        hull.add(box("DarkMetal", -0.40, 0.40, 1.13, 1.142, z, z + 0.035))
    hull.add(tube("DarkMetal", [(0.60, 1.02, 1.95), (0.72, 1.14, 2.00), (0.72, 1.14, 2.14)], 0.03, 6))

    # ---- fenders, running boards, axles
    for z in (AXLE_F, AXLE_R):
        hull.add(both(fender(z)))
        hull.add(cyl("DarkMetal", (-(WHEEL_X - WHEEL_HALF), WHEEL_R, z), (WHEEL_X - WHEEL_HALF, WHEEL_R, z), 0.05, 6))
    hull.add(both(box("Olive", 0.72, WHEEL_X + WHEEL_HALF + 0.035, 0.60, 0.635, AXLE_F + 0.48, AXLE_R - 0.48)))       # running board
    hull.add(both(box("OliveDark", 0.74, 0.78, 0.50, 0.60, AXLE_F + 0.50, AXLE_R - 0.50)))                           # its skirt

    # ---- cockpit front: dashboard, gauges, steering column and wheel (left)
    hull.add(box("DarkMetal", -0.78, 0.78, 0.86, 1.07, COCKPIT0, COCKPIT0 + 0.06))
    for x in (-0.60, -0.30):
        hull.add(lathe((x, 0.99, COCKPIT0 + 0.06), (0, 0, 1), [(0, 0.0, "Steel"), (0, 0.055), (0.012, 0.055, "Lamp"), (0.014, 0.0)], 10))
    col0, col1 = (-SEAT_X, 0.96, COCKPIT0 + 0.06), (-SEAT_X, 1.10, -0.72)
    hull.add(cyl("DarkMetal", col0, col1, 0.018, 6))
    hull.add(torus("Rubber", col1, sub(col1, col0), 0.17, 0.016, 14, 5))
    for k in range(3):
        a = math.radians(90 + 120 * k)
        d = unit(sub(col1, col0))
        u = unit(cross(d, (1, 0, 0)))
        v = cross(d, u)
        hull.add(cyl("DarkMetal", col1, add(col1, add(mul(u, 0.17 * math.cos(a)), mul(v, 0.17 * math.sin(a)))), 0.01, 4, smooth=False))
    hull.add(cyl("DarkMetal", (0.0, FLOOR, -0.80), (0.0, 0.98, -0.74), 0.014, 5, smooth=False))                     # gear lever
    hull.add(lathe((0.0, 0.98, -0.74), (0, 1, 0), [(0, 0.0, "Rubber"), (0, 0.025), (0.04, 0.025), (0.05, 0.0)], 6))

    # ---- the two front seats: a frame, a canvas cushion and a backrest
    for sx in (-1, 1):
        x = sx * SEAT_X
        hull.add(box("OliveDark", x - 0.25, x + 0.25, FLOOR, FLOOR + 0.23, SEAT_Z - 0.25, SEAT_Z + 0.25, c=0.03, axis='x', e=0.02))
        hull.add(box("Khaki", x - 0.22, x + 0.22, FLOOR + 0.23, FLOOR + 0.29, SEAT_Z - 0.23, SEAT_Z + 0.21, c=0.02, axis='x', e=0))
        back = box("OliveDark", x - 0.24, x + 0.24, FLOOR + 0.25, FLOOR + 0.71, SEAT_Z + 0.22, SEAT_Z + 0.29, c=0.02, axis='x', e=0)
        back += box("Khaki", x - 0.21, x + 0.21, FLOOR + 0.31, FLOOR + 0.67, SEAT_Z + 0.20, SEAT_Z + 0.225, c=0.01, axis='x', e=0)
        hull.add(rot(back, 'x', -8, (x, FLOOR + 0.25, SEAT_Z + 0.25)))

    # ---- rear compartment: the gunner's grating, ammunition boxes, the ring on four stanchions
    hull.add(box("DarkMetal", -0.56, 0.56, FLOOR + 0.012, FLOOR + 0.03, 0.42, 1.58))
    for k in range(6):
        z = 0.47 + k * 0.19
        hull.add(box("Wood", -0.54, 0.54, FLOOR + 0.03, FLOOR + 0.045, z, z + 0.12))
    hull.add(box("Khaki", -0.72, -0.48, FLOOR + 0.012, FLOOR + 0.19, 1.22, 1.62, c=0.02, axis='y', e=0))
    hull.add(box("Khaki", 0.48, 0.72, FLOOR + 0.012, FLOOR + 0.19, 1.22, 1.62, c=0.02, axis='y', e=0))
    hull.add(box("Yellow", -0.70, -0.50, FLOOR + 0.19, FLOOR + 0.202, 1.30, 1.54), box("Yellow", 0.50, 0.70, FLOOR + 0.19, FLOOR + 0.202, 1.30, 1.54))
    hull.add(torus("Steel", RING, (0, 1, 0), 0.52, 0.026, 24, 6))
    hull.add(torus("DarkMetal", add(RING, (0, -0.045, 0)), (0, 1, 0), 0.52, 0.016, 24, 5))
    for sx in (1, -1):
        for sz in (1, -1):
            top = (sx * 0.52 * math.sin(math.radians(50)), RING[1] - 0.02, RING[2] + sz * 0.52 * math.cos(math.radians(50)))
            foot_z = RING[2] + sz * 0.52
            hull.add(tube("OliveDark", [(sx * (side_x(foot_z, 1.02) - 0.02), 1.02, foot_z), top], 0.022, 6))

    # ---- lamps (meshes; the empties below mark them for the game)
    for sx in (1, -1):
        x = sx * WHEEL_X
        hull.add(lathe((x, 1.00, -1.60), (0, 0, -1), [(0, 0.0, "Olive"), (0.02, 0.07), (0.16, 0.085), (0.18, 0.085, "Lamp"), (0.20, 0.06), (0.205, 0.0)], 10))
        hull.add(cyl("Olive", (x, 0.90, -1.52), (x, 0.96, -1.52), 0.02, 5, smooth=False))
        hull.add(box("Red", sx * 0.55 - 0.05, sx * 0.55 + 0.05, 0.93, 1.03, 2.045, 2.075))

    # ---- loose gear: a paddle on the left running board, a fuel can on the right one
    hull.add(cyl("Wood", (-0.93, 0.665, AXLE_F + 0.55), (-0.93, 0.665, 0.45), 0.018, 6))
    hull.add(box("Wood", -1.00, -0.86, 0.645, 0.685, 0.45, 0.80, c=0.012, axis='z', e=0.02))
    hull.add(box("OliveDark", 0.84, 1.02, 0.635, 0.98, 0.40, 0.68, c=0.025, axis='y', e=0.02))
    hull.add(box("DarkMetal", 0.90, 0.96, 0.98, 1.02, 0.46, 0.62))

    # ---- propeller: a shaft out of the stern under the waterline, a skeg under it, three blades on the turning hub
    hull.add(cyl("DarkMetal", (0, 0.52, 2.00), (PROP_HUB[0], PROP_HUB[1], PROP_HUB[2] - 0.05), 0.03, 6))
    hull.add(tube("DarkMetal", [(0, 0.44, 1.95), (0, 0.22, 2.12), (0, 0.22, 2.34)], 0.02, 5))
    prop = hull.child("propeller", PROP_HUB)
    prop.add(lathe(add(PROP_HUB, (0, 0, -0.05)), (0, 0, 1), [(0, 0.0, "Steel"), (0, 0.05), (0.09, 0.05), (0.14, 0.0)], 8))
    blade = box("Steel", -0.05, 0.05, 0.04, PROP_R, -0.008, 0.008, c=0.006, axis='y', e=0.02)
    blade = rot(blade, 'y', 28)                          # the blade's pitch
    for k in range(3):
        prop.add(move(rot(blade, 'z', 120.0 * k), PROP_HUB))

    # ---- the ring gun: a carriage on the ring (yaw), the gun on its yoke (pitch) and the barrel (recoil)
    mount = hull.child("gun_mount", RING)
    cx, cy, cz = RING
    px, py, pz = PIVOT
    mount.add(box("DarkMetal", -0.07, 0.07, cy - 0.07, cy + 0.05, cz - 0.58, cz - 0.46, c=0.015, axis='x', e=0))          # the carriage that rides the ring
    mount.add(tube("Steel", [(0, cy + 0.03, cz - 0.52), (0, cy + 0.30, cz - 0.50), (0, py - 0.10, pz)], 0.028, 6))        # the arm up to the yoke
    for dx in (-0.075, 0.075):                                                                                           # the yoke's two cheeks
        mount.add(box("Steel", dx - 0.012, dx + 0.012, py - 0.12, py + 0.05, pz - 0.035, pz + 0.035))
    mount.add(box("Steel", -0.087, 0.087, py - 0.13, py - 0.10, pz - 0.035, pz + 0.035))
    pad = box("Khaki", -0.22, 0.22, cy - 0.06, cy + 0.20, cz + 0.50, cz + 0.55, c=0.02, axis='x', e=0)                   # the gunner's back pad, opposite the gun
    mount.add(pad, tube("DarkMetal", [(-0.18, cy, cz + 0.50), (-0.18, cy, cz + 0.55)], 0.012, 4), tube("DarkMetal", [(0.18, cy, cz + 0.50), (0.18, cy, cz + 0.55)], 0.012, 4))

    gun = mount.child("gun", PIVOT)
    # the gun points along -Z; "behind" (towards the gunner) is +Z
    gun.add(box("Gunmetal", px - 0.045, px + 0.045, py - 0.055, py + 0.065, pz - BARREL_ROOT, pz + 0.42, c=0.015, axis='z', e=0.01))     # receiver
    gun.add(box("DarkMetal", px - 0.05, px + 0.05, py + 0.065, py + 0.10, pz - 0.05, pz + 0.30, c=0.012, axis='z', e=0))                # feed cover
    gun.add(box("Khaki", px - 0.21, px - 0.05, py - 0.20, py + 0.02, pz + 0.12, pz + 0.40, c=0.02, axis='z', e=0))                      # ammunition box on the left
    gun.add(box("Yellow", px - 0.16, px - 0.045, py + 0.02, py + 0.045, pz + 0.18, pz + 0.34))                                          # belt
    for dx in (-0.07, 0.07):                                                                                                            # spade grips
        gun.add(tube("DarkMetal", [(px + dx, py, pz + 0.42), (px + dx, py, pz + 0.56), (px + dx, py - 0.12, pz + 0.56)], 0.014, 5))
    gun.add(box("DarkMetal", px - 0.012, px + 0.012, py + 0.10, py + 0.17, pz + 0.27, pz + 0.30))                                        # rear sight
    gun.add(cyl("Gunmetal", (px, py - 0.075, pz - 0.05), (px, py - 0.075, pz - BARREL_ROOT - 0.30), 0.02, 6))                            # gas tube
    root_z = pz - BARREL_ROOT
    barrel = gun.child("barrel", (px, py, root_z))
    barrel.add(lathe((px, py, root_z), (0, 0, -1), [(0, 0.028, "Gunmetal"), (0.30, 0.024), (BARREL_LEN - 0.10, 0.018, "DarkMetal"), (BARREL_LEN - 0.10, 0.030),
                                                    (BARREL_LEN, 0.026), (BARREL_LEN, 0.012), (BARREL_LEN - 0.03, 0.0)], 8))
    barrel.add(box("DarkMetal", px - 0.008, px + 0.008, py + 0.02, py + 0.075, root_z - 0.44, root_z - 0.42))                            # front sight
    barrel.child("muzzle", (px, py, root_z - BARREL_LEN))
    gun.child("gunner_sight", (px, py + 0.22, pz + 0.62))

    # ---- markers
    hull.child("seat_driver", (-SEAT_X, FLOOR, SEAT_Z))
    hull.child("seat_passenger", (SEAT_X, FLOOR, SEAT_Z))
    hull.child("seat_gunner", (RING[0], FLOOR + 0.045, RING[2]))
    for sx, side in ((-1, "l"), (1, "r")):
        hull.child(f"light_head_{side}", (sx * WHEEL_X, 1.00, -1.81))
        hull.child(f"light_tail_{side}", (sx * 0.55, 0.98, 2.08))
        hull.child(f"wake_stern_{side}", (sx * 0.55, WATERLINE, 2.05))
    hull.child("smoke_engine", (0.0, 1.16, 1.88))
    hull.child("wake_bow", (0.0, WATERLINE, -2.02))

    # ---- wheels: the front pair on steering pivots
    for name, sx, z, steered in (("fl", -1, AXLE_F, True), ("fr", 1, AXLE_F, True), ("rl", -1, AXLE_R, False), ("rr", 1, AXLE_R, False)):
        centre = (sx * WHEEL_X, WHEEL_R, z)
        parent = root.child(f"wheel_{name}_steer", centre) if steered else root
        w = parent.child(f"wheel_{name}", centre)
        t = wheel() if sx > 0 else mirror(wheel())
        w.add(move(t, centre))
    return root


# ----------------------------------------------------------------------------- GLB

def write_glb(root, path):
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS)
    materials = []
    for name in mat_names:
        r, g, b, rough, metal, emis = MATERIALS[name]
        lin = [round(kit.srgb_to_linear(v), 4) for v in (r, g, b)]
        m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": metal, "roughnessFactor": rough}}
        if emis:
            m["emissiveFactor"] = [round(v * emis, 4) for v in lin]
        materials.append(m)

    def view(data, target):
        while len(buf) % 4:
            buf.append(0)
        views.append({"buffer": 0, "byteOffset": len(buf), "byteLength": len(data), "target": target})
        buf.extend(data)
        return len(views) - 1

    def emit(node, parent_origin):
        entry = {"name": node.name}
        t = sub(node.origin, parent_origin)
        if any(abs(v) > 1e-9 for v in t):
            entry["translation"] = [round(v, 5) + 0.0 for v in t]   # + 0.0: no "-0.0" in the file
        index = len(nodes)
        nodes.append(entry)
        if node.tris:
            prims = []
            for mi, mname in enumerate(mat_names):
                verts, lookup, idx = [], {}, []
                for m, ps, nr in node.tris:
                    if m != mname:
                        continue
                    fn = unit(cross(sub(ps[1], ps[0]), sub(ps[2], ps[0])))
                    for k in range(3):
                        p = sub(ps[k], node.origin)
                        n = nr[k] if nr else fn
                        key = tuple(round(v, 5) for v in p) + tuple(round(v, 3) for v in n)
                        if key not in lookup:
                            lookup[key] = len(verts)
                            verts.append((p, n))
                        idx.append(lookup[key])
                if not idx:
                    continue
                assert len(verts) < 65536
                pos = b"".join(struct.pack("<3f", *p) for p, _ in verts)
                nor = b"".join(struct.pack("<3f", *n) for _, n in verts)
                lo = [struct.unpack("<f", struct.pack("<f", min(p[i] for p, _ in verts)))[0] for i in range(3)]
                hi = [struct.unpack("<f", struct.pack("<f", max(p[i] for p, _ in verts)))[0] for i in range(3)]
                accessors.append({"bufferView": view(pos, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3", "min": lo, "max": hi})
                accessors.append({"bufferView": view(nor, 34962), "componentType": 5126, "count": len(verts), "type": "VEC3"})
                accessors.append({"bufferView": view(struct.pack(f"<{len(idx)}H", *idx), 34963), "componentType": 5123, "count": len(idx), "type": "SCALAR"})
                a = len(accessors)
                prims.append({"attributes": {"POSITION": a - 3, "NORMAL": a - 2}, "indices": a - 1, "material": mi})
            entry["mesh"] = len(meshes)
            meshes.append({"name": node.name, "primitives": prims})
        kids = [emit(c, node.origin) for c in node.children]
        if kids:
            entry["children"] = kids
        return index

    top = emit(root, (0.0, 0.0, 0.0))
    while len(buf) % 4:
        buf.append(0)
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_amphibious_model.py (Drycastle)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": root.name, "nodes": [top]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent / "assets" / "drycastle-generated-models" / "amphibious_transport.glb")
    root = build()
    write_glb(root, out)
    total = 0
    lo, hi = [1e9] * 3, [-1e9] * 3
    for n in root.walk():
        total += len(n.tris)
        for _, ps, _ in n.tris:
            for p in ps:
                for i in range(3):
                    lo[i], hi[i] = min(lo[i], p[i]), max(hi[i], p[i])
        print(f"{n.name:16s} pivot=({n.origin[0]:.3f}, {n.origin[1]:.3f}, {n.origin[2]:.3f}) tris={len(n.tris)}")
    print(f"triangles: {total}")
    print("bounds: x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
