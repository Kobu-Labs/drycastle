#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/flak.glb: the flak, a 6x6 truck with a twin 35 mm anti-aircraft turret.

Original geometry written by hand as code (no third-party model, no texture); standard library only:

    python3 -I scripts/models/make_flak_model.py [out.glb]

Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground at the centre.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0].

Node tree (every moving part is its own node, its origin is its pivot; the contract is docs/standards/car-design.md part A):

    flak
      hull                         frame, bed, fittings (static)
        cab                        bonnet and a HOLLOW driver cab: floor, firewall, doors, back wall, pillars and roof round real window openings,
                                   dash with gauges, steering column, driver's seat (the cockpit view sees this room from inside)
        glass_0 .. glass_5         the breakable panes (GlassSet), one node each, in the window openings of the cab
        steering_wheel_rim, _spokes   the wheel (meshes): the cockpit view hides them and draws its own wheel that turns (CockpitRig)
        outrigger_fl .. _rr        4 stabiliser jacks, origin at the jack top (lower them along -Y)
        turret                     yaw pivot (rotate about Y), origin at the ring centre on the deck
          radar                    dish, origin on top of its mast (spin about Y)
          gun_cradle               pitch pivot (rotate about X; +X rotation raises the muzzles), origin on the trunnion axis
            barrel_l, barrel_r     origin at the barrel root (recoil along +Z)
              muzzle_l, muzzle_r   empty: muzzle tip
            sight                  empty: gunner camera point between the guns
          seat_gunner              empty: gunner seat (feet)
        seat_driver                empty: driver seat (feet)
        light_head_l/r, light_tail_l/r, smoke_hood   empties: lamp positions and the smoke point
      wheel_fl_steer, _fr_steer    the front axle's steering parents (turn about Y), at the wheel centre, SIBLINGS of the hull
        wheel_fl, wheel_fr         the front wheels, origin at the wheel centre, spin about X
      wheel_ml, _mr, _rl, _rr      the other four wheels (f, m, r axle; l = -x), siblings of the hull
"""
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from glass import GlassSet, vehicles_json_path
from glb import materials_from, write_glb
from meshkit import Node, add, both, box, cross, cyl, dot, extrude, lathe, loft, mirror, move, mul, panel, report, rot, sub, unit

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, metallic, emissive strength)
    "Olive": (0.36, 0.40, 0.22, 0.85, 0.0, 0),        # main paint
    "OliveDark": (0.24, 0.28, 0.16, 0.85, 0.0, 0),    # panels, turret fittings
    "OliveLight": (0.47, 0.50, 0.30, 0.85, 0.0, 0),   # highlights: doors, lids
    "Khaki": (0.62, 0.55, 0.36, 0.9, 0.0, 0),         # ammunition crates, seat cushions
    "DarkMetal": (0.13, 0.14, 0.14, 0.7, 0.2, 0),     # frame, grille, muzzle brakes
    "Gunmetal": (0.27, 0.29, 0.31, 0.5, 0.5, 0),      # barrels, receivers
    "Steel": (0.62, 0.64, 0.64, 0.5, 0.4, 0),         # hubs, jack rods, dish face
    "Rubber": (0.07, 0.07, 0.075, 0.95, 0.0, 0),      # tyres
    "Glass": (0.20, 0.33, 0.40, 0.2, 0.0, 0),         # windows, lenses
    "Yellow": (0.90, 0.70, 0.12, 0.8, 0.0, 0),        # hazard accents, ammunition bands
    "Red": (0.72, 0.13, 0.10, 0.7, 0.0, 0),           # tow hooks, extinguisher
    "Emissive red tail lamps": (0.72, 0.13, 0.10, 0.7, 0.0, 0.3),  # tail lamps: the contract name, CarLights drives it
    "Lamp": (0.98, 0.94, 0.75, 0.4, 0.0, 0.6),        # headlamp lenses
    "Amber": (0.95, 0.50, 0.08, 0.6, 0.0, 0.2),       # indicators
}


def panel_face(a, b, c, d, u0, u1, v0, v1, h, centre):
    """The four corners of the outer face of the raised panel `panel` makes with the same arguments (the outline of a glass pane)."""
    def P(u, v):
        top = add(mul(a, 1 - u), mul(b, u))
        bot = add(mul(d, 1 - u), mul(c, u))
        return add(mul(top, 1 - v), mul(bot, v))
    n = unit(cross(sub(b, a), sub(d, a)))
    mid = mul(add(add(a, b), add(c, d)), 0.25)
    if dot(n, sub(mid, centre)) < 0:
        n = mul(n, -1)
    return [add(P(u, v), mul(n, h)) for u, v in ((u0, v0), (u1, v0), (u1, v1), (u0, v1))]


# ----------------------------------------------------------------------------- dimensions

WHEEL_R, WHEEL_X = 0.56, 1.07
AXLES = {"f": -2.35, "m": 1.15, "r": 2.40}
DECK = 1.35                      # top of the bed
TURRET = (0.0, DECK, 1.75)       # yaw pivot
TRUNNION = (0.0, DECK + 1.20, 1.75)  # pitch pivot
BARREL_X, BARREL_ROOT, BARREL_LEN = 0.27, -1.20, 2.45  # barrel roots in cradle space
OUTRIGGERS = {"f": 0.20, "r": 3.25}


def wheel(spare=False):
    """A wheel about the X axis at the origin; the outer face is at +x."""
    prof = [(-0.20, 0.0, "Rubber"), (-0.20, 0.56), (0.15, 0.56), (0.20, 0.47), (0.20, 0.34, "Olive"),
            (0.11, 0.30), (0.11, 0.16, "Steel"), (0.22, 0.12), (0.22, 0.0)]
    return lathe((0, 0, 0), (1, 0, 0), prof, 10 if spare else 12)


GLASS = GlassSet("flak")


def build():
    root = Node("flak")
    chassis = root.child("hull")
    cab = chassis.child("cab")

    # ---- frame and running gear (chassis)
    chassis.add(both(box("DarkMetal", 0.36, 0.50, 0.64, 0.88, -3.34, 3.40)))
    for z in (-0.5, 1.78, 3.0):
        chassis.add(box("DarkMetal", -0.36, 0.36, 0.68, 0.84, z - 0.05, z + 0.05))
    chassis.add(box("DarkMetal", -0.50, 0.50, 0.50, 0.92, -3.20, -0.90))  # engine and gearbox
    for name, z in AXLES.items():
        diff = [(0.62, 0.09), (0.74, 0.21), (1.02, 0.21), (1.14, 0.09)] if name != "f" else []
        chassis.add(lathe((-0.88, WHEEL_R, z), (1, 0, 0), [(0, 0.08)] + diff + [(1.76, 0.08)], 6, mat="DarkMetal"))
    # rear suspension: a walking beam between the two rear axles
    chassis.add(both(extrude("DarkMetal", [(1.15, 0.50), (2.40, 0.50), (2.30, 0.64), (1.90, 0.74), (1.65, 0.74), (1.25, 0.64)], 'x', 0.52, 0.62)))
    chassis.add(both(extrude("DarkMetal", [(-2.85, 0.70), (-1.85, 0.70), (-1.95, 0.60), (-2.75, 0.60)], 'x', 0.52, 0.60)))  # front leaf spring

    # ---- front bumper, skid plate, tow hooks
    chassis.add(box("DarkMetal", -1.27, 1.27, 0.52, 0.80, -3.54, -3.36, c=0.045, axis='x'))
    chassis.add(extrude("DarkMetal", [(-3.42, 0.54), (-2.95, 0.38), (-2.95, 0.44), (-3.42, 0.60)], 'x', -0.55, 0.55))
    chassis.add(both(box("Red", 0.42, 0.52, 0.60, 0.72, -3.60, -3.52)))
    chassis.add(both(box("Amber", 1.02, 1.20, 0.61, 0.71, -3.555, -3.53)))

    # ---- bonnet
    def hood_ring(z, hw, top, ch):
        return [(-hw, 0.90, z), (hw, 0.90, z), (hw, top - ch, z), (hw - ch, top, z), (-hw + ch, top, z), (-hw, top - ch, z)]
    hood = [hood_ring(-3.36, 0.54, 1.48, 0.10), hood_ring(-3.28, 0.64, 1.58, 0.13), hood_ring(-2.13, 0.84, 1.76, 0.17)]
    cab.add(loft("Olive", hood))
    hc = (0, 1.3, -2.7)
    for i in range(4):  # radiator grille
        y = 1.00 + i * 0.12
        cab.add(box("DarkMetal", -0.44, 0.44, y, y + 0.055, -3.385, -3.35))
    cab.add(box("OliveLight", -0.035, 0.035, 1.47, 1.50, -3.38, -3.34))            # badge bar above the grille
    cab.add(extrude("OliveDark", [(-0.03, 1.575), (0.03, 1.575), (0.03, 1.60), (-0.03, 1.60)], 'z', -3.26, -2.2))  # hinge line (approx.)
    for s in (1, -1):  # cooling louvres on the bonnet sides
        a, b, c, d = hood[1][2 if s > 0 else 5], hood[2][2 if s > 0 else 5], hood[2][1 if s > 0 else 0], hood[1][1 if s > 0 else 0]
        for k in range(3):
            cab.add(panel("DarkMetal", a, b, c, d, 0.30 + k * 0.14, 0.37 + k * 0.14, 0.22, 0.62, 0.012, hc))
    cab.add(cyl("DarkMetal", (0.62, 1.60, -2.75), (0.62, 2.02, -2.75), 0.055, 6))   # air intake snorkel
    cab.add(lathe((0.62, 2.02, -2.75), (0, 1, 0), [(0, 0), (0, 0.10), (0.10, 0.10), (0.14, 0.05), (0.14, 0)], 6, mat="OliveDark"))

    # ---- front mudguards with headlamps
    arch = [(AXLES["f"] + 0.78 * math.cos(math.radians(a)), WHEEL_R + 0.78 * math.sin(math.radians(a))) for a in (-8, 22, 52, 90, 128, 158, 188)]
    arch += [(AXLES["f"] + 0.71 * math.cos(math.radians(a)), WHEEL_R + 0.71 * math.sin(math.radians(a))) for a in (188, 158, 128, 90, 52, 22, -8)]
    cab.add(both(extrude("Olive", arch, 'x', 0.70, 1.29)))
    cab.add(both(box("OliveDark", 0.72, 1.27, 0.40, 0.50, -1.60, -1.56)))            # mud flap (hangs behind the wheel)
    lamp = lathe((0.98, 1.47, -2.84), (0, 0, -1), [(0, 0.0, "Olive"), (0.10, 0.13), (0.26, 0.13, "DarkMetal"), (0.26, 0.105, "Lamp"), (0.285, 0.0)], 8)
    cab.add(both(lamp))
    cab.add(both(box("Olive", 0.95, 1.01, 1.30, 1.36, -3.02, -2.92)))                # lamp stalk
    cab.add(both(box("Amber", 1.14, 1.24, 1.27, 1.33, -3.10, -3.04)))                # indicator

    # ---- cab: a hollow body (floor, firewall, doors, back wall) under a glasshouse of pillars, header and roof round real window openings
    cc = (0, 2.0, -1.4)                  # a point inside the cab: faces look away from it
    W = 0.06                             # wall thickness of the body
    cab.add(box("Olive", -1.20, 1.20, 0.95, 1.78, -2.15, -2.15 + W))                       # firewall
    cab.add(both(box("Olive", 1.20 - W, 1.20, 0.95, 1.78, -2.15, -0.80)))                    # door sides
    cab.add(box("Olive", -1.20, 1.20, 0.95, 1.78, -0.80 - W, -0.80))                        # back wall
    cab.add(box("OliveDark", -1.14, 1.14, 0.93, 0.97, -2.09, -0.86))                         # floor
    cab.add(both(box("OliveDark", 1.08, 1.14, 1.00, 1.62, -1.98, -1.04, c=0.02, axis='x', e=0)))  # door cards
    cab.add(both(box("DarkMetal", 1.02, 1.08, 1.50, 1.56, -1.30, -1.12)))                    # door pulls

    def ring(y, xh, zf, zb):
        return [(-xh, y, zf), (xh, y, zf), (xh, y, zb), (-xh, y, zb)]
    g0, g1, g2 = ring(1.78, 1.17, -2.12, -0.83), ring(2.28, 1.03, -1.80, -0.90), ring(2.36, 0.93, -1.68, -1.00)

    def facing(a, b, c, d):
        n = unit(cross(sub(b, a), sub(d, a)))
        mid = mul(add(add(a, b), add(c, d)), 0.25)
        return n if dot(n, sub(mid, cc)) >= 0 else mul(n, -1)

    def bil(f, u, v):
        a, b, c, d = f
        return add(mul(add(mul(a, 1 - u), mul(b, u)), 1 - v), mul(add(mul(d, 1 - u), mul(c, u)), v))

    def rect(f, u0, u1, v0, v1, t=0.05):
        """The part u0..u1, v0..v1 of the face f as a closed slab t thick, pushed inwards: a frame piece seen from both sides."""
        if u1 - u0 < 1e-6 or v1 - v0 < 1e-6:
            return []
        n = facing(*f)
        q = [bil(f, u, v) for u, v in ((u0, v0), (u1, v0), (u1, v1), (u0, v1))]
        return loft("Olive", [q, [sub(p, mul(n, t)) for p in q]])

    def framed(f, windows, v0, v1):
        """The face f without the window openings (u ranges `windows`, all between v0 and v1): sill, header and the pillars between."""
        out = rect(f, 0, 1, 0, v0) + rect(f, 0, 1, v1, 1)
        edge = 0.0
        for u0, u1 in sorted(windows):
            out += rect(f, edge, u0, v0, v1)
            edge = u1
        return out + rect(f, edge, 1, v0, v1)

    f = (g0[0], g0[1], g1[1], g1[0])     # windscreen face
    cab.add(framed(f, ((0.045, 0.485), (0.515, 0.955)), 0.10, 0.90))
    # the breakable panes are nodes of their own (glass_N, see GlassSet), in the openings: two windscreen halves, and on each side a door window and a quarter light
    for pid, u0, u1 in (("windscreen_left", 0.045, 0.485), ("windscreen_right", 0.515, 0.955)):
        GLASS.pane(chassis, pid, [panel("Glass", *f, u0, u1, 0.10, 0.90, 0.012, cc)], panel_face(*f, u0, u1, 0.10, 0.90, 0.012, cc))
    for i0, i1, s in ((1, 2, 1), (3, 0, -1)):  # side windows: door glass and a quarter light (u runs along the face)
        q = (g0[i0], g0[i1], g1[i1], g1[i0])
        d1, d2, q1, q2 = (0.06, 0.60, 0.68, 0.93) if s > 0 else (0.40, 0.94, 0.07, 0.32)
        side = "right" if s > 0 else "left"
        cab.add(framed(q, ((d1, d2), (q1, q2)), 0.10, 0.88))
        for pid, u0, u1 in (("door_" + side, d1, d2), ("quarter_" + side, q1, q2)):
            GLASS.pane(chassis, pid, [panel("Glass", *q, u0, u1, 0.10, 0.88, 0.012, cc)], panel_face(*q, u0, u1, 0.10, 0.88, 0.012, cc))
    rear = (g0[2], g0[3], g1[3], g1[2])
    cab.add(framed(rear, ((0.28, 0.72),), 0.15, 0.80))
    cab.add(panel("Glass", *rear, 0.28, 0.72, 0.15, 0.80, 0.012, cc))                       # rear window (not a pane: the turret shows through it)
    for a, b, c, d in ((0, 1, 1, 0), (1, 2, 2, 1), (2, 3, 3, 2), (3, 0, 0, 3)):             # the narrow band up to the roof, and the roof
        cab.add(rect((g1[a], g1[b], g2[c], g2[d]), 0, 1, 0, 1))
    cab.add(rect((g2[0], g2[1], g2[2], g2[3]), 0, 1, 0, 1))
    # ---- inside the cab: dash with the instruments, steering column and wheel, the driver's seat (the cockpit view looks at these)
    cab.add(box("OliveDark", -1.14, 1.14, 1.40, 1.76, -2.09, -1.86, c=0.04, axis='x', e=0))                                   # dash
    cab.add(box("DarkMetal", -0.80, -0.20, 1.48, 1.74, -1.88, -1.855))                                                            # instrument panel
    for x in (-0.62, -0.38):
        cab.add(cyl("Steel", (x, 1.61, -1.858), (x, 1.61, -1.845), 0.085, 12))                                                # gauges
    cab.add(cyl("DarkMetal", (-0.50, 1.50, -1.86), (-0.50, 1.66, -1.72), 0.03, 6))                                                # steering column
    cab.add(box("DarkMetal", -0.55, -0.45, 0.95, 1.30, -1.46, -1.24))                                                         # seat pedestal
    cab.add(box("Khaki", -0.76, -0.24, 1.30, 1.40, -1.60, -1.08, c=0.03, axis='x', e=0))                                      # seat cushion
    cab.add(rot(box("Khaki", -0.76, -0.24, 1.40, 2.00, -1.10, -1.02, c=0.03, axis='y', e=0), 'x', 10, (0, 1.40, -1.06)))     # backrest
    # the steering wheel: face up and back at the driver (45 degrees from level), the cockpit view hides it and draws a wheel that turns
    wc, wax = (-0.50, 1.66, -1.72), (0.0, math.sin(math.radians(45)), math.cos(math.radians(45)))
    wv = (0.0, -wax[2], wax[1])          # the wheel's own "down the face" direction
    rim = chassis.child("steering_wheel_rim", wc)
    rim.add(lathe(wc, wax, [(0.02 * math.sin(a), 0.20 + 0.02 * math.cos(a), "DarkMetal") for a in [k * math.pi / 4 for k in range(9)]], 12))
    spokes = chassis.child("steering_wheel_spokes", wc)
    spokes.add(cyl("DarkMetal", add(wc, (-0.20, 0, 0)), add(wc, (0.20, 0, 0)), 0.022, 6), cyl("DarkMetal", wc, add(wc, mul(wv, 0.20)), 0.022, 6),
               cyl("DarkMetal", sub(wc, mul(wax, 0.02)), add(wc, mul(wax, 0.03)), 0.055, 8))
    cab.add(extrude("OliveDark", [(-2.02, 2.20), (-1.76, 2.30), (-1.76, 2.335), (-2.04, 2.23)], 'x', -1.02, 1.02))  # sun visor
    cab.add(lathe((0.42, 2.36, -1.34), (0, 1, 0), [(0, 0), (0, 0.30), (0.035, 0.30), (0.06, 0.25), (0.06, 0)], 8, mat="OliveDark"))  # roof hatch
    cab.add(both(box("Amber", 0.62, 0.74, 2.33, 2.375, -1.72, -1.66)))               # roof marker lamps
    cab.add(cyl("DarkMetal", (-0.90, 2.28, -0.96), (-0.90, 2.42, -0.96), 0.035, 6))   # aerial base
    cab.add(cyl("DarkMetal", (-0.90, 2.42, -0.96), (-0.90, 3.02, -0.90), 0.012, 4, r1=0.006, smooth=False))
    # doors, handles, steps, mirrors
    cab.add(both(box("OliveLight", 1.195, 1.215, 1.03, 1.72, -1.98, -1.04, c=0.04, axis='x', e=0)))
    cab.add(both(box("DarkMetal", 1.21, 1.235, 1.50, 1.54, -1.22, -1.10)))
    cab.add(both(box("DarkMetal", 0.98, 1.27, 0.60, 0.65, -1.52, -1.02)))
    cab.add(both(cyl("DarkMetal", (1.17, 1.86, -2.02), (1.42, 1.92, -2.10), 0.015, 4, smooth=False)))
    cab.add(both(box("DarkMetal", 1.38, 1.46, 1.72, 2.10, -2.13, -2.09)))
    cab.add(both(panel("Steel", (1.38, 2.10, -2.09), (1.46, 2.10, -2.09), (1.46, 1.72, -2.09), (1.38, 1.72, -2.09), 0.15, 0.85, 0.06, 0.94, 0.004, (1.42, 1.9, -2.2))))

    # ---- behind the cab: carrier deck, spare wheel, exhaust, extinguisher, jerry cans
    chassis.add(box("DarkMetal", -1.18, 1.18, 1.10, 1.17, -0.80, -0.30))
    spare = rot(wheel(spare=True), 'y', 90)     # axis along z
    chassis.add(move(spare, (-0.52, 1.17 + WHEEL_R, -0.54)))
    chassis.add(lathe((0.88, 1.17, -0.55), (0, 1, 0), [(0, 0, "DarkMetal"), (0, 0.10), (0.05, 0.14), (0.65, 0.14), (0.70, 0.07, "Steel"), (1.22, 0.07), (1.22, 0.05, "DarkMetal"), (1.10, 0.05), (1.10, 0)], 6))
    chassis.add(lathe((0.42, 1.17, -0.50), (0, 1, 0), [(0, 0, "Red"), (0, 0.075), (0.36, 0.075), (0.42, 0.03, "DarkMetal"), (0.48, 0.03), (0.48, 0)], 6))
    chassis.add(box("OliveDark", 0.02, 0.20, 1.17, 1.65, -0.72, -0.38, c=0.03, axis='y', e=0), cyl("DarkMetal", (0.11, 1.65, -0.48), (0.11, 1.70, -0.48), 0.03, 6))

    # ---- under-bed fittings: fuel tank (left), tool locker (right)
    chassis.add(lathe((-0.93, 0.80, -0.74), (0, 0, 1), [(0, 0), (0, 0.20), (0.05, 0.27), (0.73, 0.27), (0.78, 0.20), (0.78, 0)], 8, mat="OliveDark"))
    for z in (-0.56, -0.14):
        chassis.add(lathe((-0.93, 0.80, z), (0, 0, 1), [(0, 0), (0.0, 0.285), (0.05, 0.285)], 8, mat="DarkMetal"))
    chassis.add(cyl("Yellow", (-1.02, 1.04, -0.35), (-1.06, 1.10, -0.35), 0.04, 6))
    chassis.add(box("OliveDark", 0.62, 1.25, 0.54, 1.08, -0.74, 0.04, c=0.04, axis='z', e=0))
    chassis.add(box("OliveLight", 1.245, 1.262, 0.62, 1.00, -0.66, -0.04, c=0.03, axis='x', e=0))
    chassis.add(box("DarkMetal", 1.26, 1.275, 0.90, 0.95, -0.56, -0.50), box("DarkMetal", 1.26, 1.275, 0.90, 0.95, -0.20, -0.14))

    # ---- bed
    chassis.add(box("Olive", -1.29, 1.29, 1.20, DECK - 0.01, -0.30, 3.48, c=0.03, axis='z', e=0))
    chassis.add(box("DarkMetal", -1.21, 1.21, DECK - 0.01, DECK, -0.24, 3.42))   # anti-slip deck
    chassis.add(box("Olive", -1.27, 1.27, DECK, 1.96, -0.30, -0.24))             # headboard
    chassis.add(cyl("OliveDark", (-1.27, 1.97, -0.27), (1.27, 1.97, -0.27), 0.035, 6))
    for x in (-0.6, 0.6):
        chassis.add(box("OliveDark", x - 0.035, x + 0.035, DECK, 1.94, -0.335, -0.30))

    def board(z0, z1, ribs):
        t = both(box("Olive", 1.23, 1.27, DECK, 1.69, z0, z1))
        for z in ribs:
            t += both(box("OliveDark", 1.27, 1.295, DECK, 1.66, z - 0.03, z + 0.03))
        return t
    chassis.add(board(-0.24, 0.62, (-0.05, 0.42)), board(2.88, 3.44, (3.16,)))
    chassis.add(box("Olive", -1.23, 1.23, DECK, 1.67, 3.42, 3.46), box("OliveDark", -1.27, 1.27, 1.66, 1.70, 3.40, 3.48))  # tailgate
    for x in (-0.6, 0.6):
        chassis.add(box("OliveDark", x - 0.03, x + 0.03, DECK, 1.66, 3.46, 3.485))
    # rear mudguards over the bogie
    guard = [(0.40, 0.92), (0.56, 1.19), (3.00, 1.19), (3.14, 0.92), (3.08, 0.92), (2.96, 1.14), (0.60, 1.14), (0.46, 0.92)]
    chassis.add(both(extrude("Olive", guard, 'x', 0.78, 1.29)))
    # rear beam, tail lamps, hitch
    chassis.add(box("DarkMetal", -1.26, 1.26, 0.84, 1.10, 3.38, 3.47, c=0.03, axis='x', e=0))
    chassis.add(both(box("Emissive red tail lamps", 0.94, 1.18, 0.90, 1.04, 3.465, 3.50)), both(box("Amber", 0.80, 0.92, 0.90, 1.04, 3.465, 3.495)))
    chassis.add(cyl("DarkMetal", (0, 0.76, 3.36), (0, 0.76, 3.60), 0.06, 6))
    chassis.add(box("Steel", -0.26, 0.26, 0.90, 1.04, 3.468, 3.48))            # number plate

    # ---- load on the bed: ammunition crates (left front), hydraulic power pack (right front), cable reel and crates (rear)
    def crate(x, y, z, yaw=0.0, w=0.62, h=0.24, d=0.36):
        t = box("Khaki", -w / 2, w / 2, 0, h, -d / 2, d / 2, c=0.025, axis='x', e=0)
        t += box("Yellow", -0.07, 0.07, 0.03, h - 0.03, -d / 2 - 0.006, d / 2 + 0.006)
        return move(rot(t, 'y', yaw), (x, y, z))
    chassis.add(crate(-0.82, DECK, 0.02), crate(-0.80, DECK, 0.42, 4), crate(-0.16, DECK, 0.03, -3), crate(-0.50, DECK + 0.24, 0.05, 6))
    chassis.add(box("OliveDark", 0.30, 1.16, DECK, 1.92, -0.20, 0.52, c=0.06, axis='x', e=0.04))
    for k in range(3):
        chassis.add(box("DarkMetal", 0.40, 1.06, 1.50 + k * 0.12, 1.56 + k * 0.12, 0.52, 0.535))
    chassis.add(cyl("DarkMetal", (0.52, 1.92, 0.0), (0.52, 2.10, 0.0), 0.045, 6), box("Yellow", 0.72, 1.02, 1.92, 1.95, 0.10, 0.36))
    chassis.add(lathe((-1.10, DECK + 0.26, 3.14), (1, 0, 0), [(0, 0, "OliveDark"), (0, 0.26), (0.03, 0.26), (0.03, 0.17, "DarkMetal"), (0.33, 0.26, "OliveDark"), (0.36, 0.26), (0.36, 0)], 8))
    chassis.add(crate(0.62, DECK, 3.16, 3))

    # ---- wheels
    for ax, z in AXLES.items():
        for side, sx in (("l", -1), ("r", 1)):
            at = (sx * WHEEL_X, WHEEL_R, z)
            # the front axle steers: its steering parent sits at the wheel centre (turn about Y), the wheel under it rolls about X
            n = (root.child(f"wheel_{ax}{side}_steer", at) if ax == "f" else root).child(f"wheel_{ax}{side}", at)   # siblings of the hull
            w = wheel()
            n.add(move(w if sx > 0 else mirror(w), n.origin))

    # ---- stabiliser outriggers (stowed: feet off the ground)
    for ax, z in OUTRIGGERS.items():
        chassis.add(box("DarkMetal", -1.24, 1.24, 0.96, 1.14, z - 0.09, z + 0.09))
        for side, sx in (("l", -1), ("r", 1)):
            n = chassis.child(f"outrigger_{ax}{side}", (sx * 1.15, 1.05, z))
            x = sx * 1.15
            n.add(lathe((x, 1.20, z), (0, -1, 0), [(0, 0, "Olive"), (0, 0.095), (0.42, 0.095, "Steel"), (0.42, 0.055), (0.80, 0.055, "Yellow"), (0.84, 0.21), (0.88, 0.21), (0.88, 0)], 8))

    # ---- turret (yaw)
    turret = chassis.child("turret", TURRET)
    T = TURRET

    def tl(t): return move(t, T)   # turret-local geometry to model space
    turret.add(tl(lathe((0, 0, 0), (0, 1, 0), [(0, 0, "DarkMetal"), (0, 1.05), (0.07, 1.05), (0.10, 0.97, "Olive"), (0.20, 0.97), (0.24, 0.91), (0.24, 0)], 16)))
    cheek = [(-0.44, 0.24), (0.52, 0.24), (0.52, 0.84), (0.24, 1.37), (-0.17, 1.37), (-0.44, 0.92)]
    turret.add(tl(both(extrude("Olive", cheek, 'x', 0.56, 0.69, bevel=0.025))))
    turret.add(tl(both(lathe((0.69, 1.20, 0), (1, 0, 0), [(0, 0.17, "OliveDark"), (0.04, 0.17), (0.06, 0.12, "Steel"), (0.06, 0)], 8))))
    turret.add(tl(both(extrude("OliveDark", [(-0.30, 0.34), (0.40, 0.34), (0.40, 0.74), (0.20, 0.96), (-0.30, 0.80)], 'x', 0.685, 0.705))))
    turret.add(tl(box("OliveDark", -0.56, 0.56, 0.24, 0.60, -0.36, 0.40, c=0.06, axis='x', e=0)))                 # pedestal
    turret.add(tl(extrude("Olive", [(-0.62, 0.24), (-0.40, 0.24), (-0.40, 0.56), (-0.46, 0.56)], 'x', -0.56, 0.56)))  # front apron
    # ready-use ammunition boxes with feed chutes
    turret.add(tl(both(box("OliveDark", 0.70, 0.92, 0.24, 0.82, -0.34, 0.36, c=0.035, axis='y', e=0))))
    turret.add(tl(both(box("Yellow", 0.922, 0.93, 0.50, 0.58, -0.26, 0.28))))
    turret.add(tl(both(box("OliveLight", 0.73, 0.89, 0.82, 0.835, -0.28, 0.30))))
    # gunner station at the rear: seat, hand controls, guard rail
    turret.add(tl(box("DarkMetal", -0.20, 0.20, 0.24, 0.50, 0.46, 0.76, c=0.04, axis='y', e=0)))
    turret.add(tl(box("Khaki", -0.24, 0.24, 0.50, 0.59, 0.40, 0.80, c=0.03, axis='x', e=0)))
    turret.add(tl(rot(box("Khaki", -0.22, 0.22, 0.58, 1.14, 0.78, 0.86, c=0.03, axis='y', e=0), 'x', 10, (0, 0.58, 0.82))))
    turret.add(tl(both(box("DarkMetal", 0.26, 0.31, 0.24, 0.78, 0.50, 0.55))), tl(both(box("DarkMetal", 0.24, 0.33, 0.78, 0.82, 0.38, 0.62))))
    rail = [(0.90 * math.sin(math.radians(a)), 0.92, 0.90 * math.cos(math.radians(a))) for a in (-70, -35, 0, 35, 70)]
    for p, q in zip(rail, rail[1:]):
        turret.add(tl(cyl("OliveDark", p, q, 0.022, 4, smooth=False)))
    for p in (rail[0], rail[2], rail[4]):
        turret.add(tl(cyl("OliveDark", (p[0], 0.24, p[2]), p, 0.022, 4, smooth=False)))
    # optical sight on the left cheek
    turret.add(tl(box("OliveDark", -0.72, -0.54, 1.37, 1.56, -0.22, 0.14, c=0.03, axis='z', e=0)))
    turret.add(tl(lathe((-0.63, 1.465, -0.22), (0, 0, -1), [(0, 0, "DarkMetal"), (0, 0.065), (0.10, 0.065), (0.10, 0.05, "Glass"), (0.08, 0.0)], 6)))
    # radar mast (the dish is its own node)
    mast = (0.66, 0.24, 0.62)
    turret.add(tl(lathe(mast, (0, 1, 0), [(0, 0, "OliveDark"), (0, 0.11), (0.10, 0.11), (0.16, 0.05, "DarkMetal"), (1.24, 0.045), (1.24, 0)], 6)))
    rpos = add(T, (mast[0], 1.48, mast[2]))
    radar = turret.child("radar", rpos)
    aim = unit((0, math.sin(math.radians(18)), -math.cos(math.radians(18))))
    radar.add(lathe(rpos, (0, 1, 0), [(-0.02, 0, "OliveDark"), (-0.02, 0.07), (0.05, 0.07), (0.05, 0)], 8))
    radar.add(lathe(add(rpos, mul(aim, -0.02)), aim, [(0, 0, "OliveDark"), (0, 0.06), (0.05, 0.22), (0.13, 0.34), (0.135, 0.34, "Steel"), (0.065, 0.21), (0.035, 0.0)], 10))
    radar.add(lathe(add(rpos, mul(aim, 0.02)), aim, [(0, 0, "DarkMetal"), (0, 0.018), (0.26, 0.014, "Yellow"), (0.26, 0.045), (0.31, 0.045), (0.31, 0)], 4, smooth=False))

    # ---- gun cradle (pitch)
    cradle = turret.child("gun_cradle", TRUNNION)
    C = TRUNNION

    def cl(t): return move(t, C)
    cradle.add(cl(box("OliveDark", -0.52, 0.52, -0.13, 0.13, -0.62, 0.50, c=0.05, axis='x', e=0)))
    for sx in (1, -1):
        x = sx * BARREL_X
        cradle.add(cl(box("Gunmetal", x - 0.125, x + 0.125, -0.17, 0.19, -0.98, 0.58, c=0.04, axis='z', e=0)))          # receiver
        cradle.add(cl(box("OliveDark", x - 0.10, x + 0.10, 0.19, 0.30, -0.22, 0.34, c=0.035, axis='z', e=0)))           # feed cover
        cradle.add(cl(lathe((x, 0, -0.98), (0, 0, -1), [(0, 0.115, "Gunmetal"), (0.18, 0.115), (0.24, 0.09), (0.24, 0)], 8)))  # recoil sleeve
        cradle.add(cl(box("OliveDark", sx * 0.45 - 0.03, sx * 0.45 + 0.03, -0.05, 0.05, -0.80, -0.60)))                 # shield stay
    # sight between the guns
    cradle.add(cl(box("OliveDark", -0.10, 0.10, 0.13, 0.40, -0.52, -0.08, c=0.035, axis='z', e=0)))
    cradle.add(cl(lathe((0, 0.29, -0.52), (0, 0, -1), [(0, 0, "DarkMetal"), (0, 0.07), (0.07, 0.07), (0.07, 0.055, "Glass"), (0.05, 0.0)], 6)))
    # gun shield: a centre plate with two barrel slots and swept-back wings, leaning back
    th = 0.022

    def plate(outline, mat="Olive"): return extrude(mat, outline, 'z', -th, th)
    shield = plate([(-0.40, 0.17), (0.40, 0.17), (0.40, 0.50), (0.30, 0.58), (-0.30, 0.58), (-0.40, 0.50)])
    shield += plate([(-0.40, -0.40), (0.40, -0.40), (0.40, -0.17), (-0.40, -0.17)])
    shield += plate([(-0.14, -0.17), (0.14, -0.17), (0.14, 0.17), (-0.14, 0.17)])
    shield += move(plate([(-0.07, 0.34), (0.07, 0.34), (0.07, 0.46), (-0.07, 0.46)], "Glass"), (0, 0, -0.012))  # sight port
    wing = plate([(0.40, -0.40), (0.76, -0.40), (0.90, -0.22), (0.90, 0.30), (0.72, 0.50), (0.40, 0.50)])
    wing += move(plate([(0.50, -0.30), (0.74, -0.30), (0.82, -0.19), (0.82, 0.24), (0.69, 0.40), (0.50, 0.40)], "OliveDark"), (0, 0, -0.012))
    wing = rot(wing, 'y', -24, (0.40, 0, 0))
    shield += both(wing)
    shield += both(box("OliveDark", 0.385, 0.415, -0.40, 0.50, -0.04, 0.03))   # hinge strips
    shield = move(rot(shield, 'x', 10), (0, 0, -0.80))
    cradle.add(cl(shield))

    # ---- barrels
    prof = [(0, 0.075, "Gunmetal"), (0.55, 0.075), (0.60, 0.055), (1.96, 0.042, "DarkMetal"), (1.98, 0.074), (2.20, 0.074), (2.22, 0.05), (2.30, 0.05),
            (BARREL_LEN, 0.09), (BARREL_LEN, 0.066), (2.36, 0.03), (2.36, 0)]
    for side, sx in (("l", -1), ("r", 1)):
        o = add(C, (sx * BARREL_X, 0, BARREL_ROOT))
        b = cradle.child(f"barrel_{side}", o)
        b.add(lathe(o, (0, 0, -1), prof, 8))
        b.child(f"muzzle_{side}", add(o, (0, 0, -BARREL_LEN)))

    # ---- markers
    cradle.child("sight", add(C, (0, 0.29, -0.60)))
    turret.child("seat_gunner", add(T, (0, 0.24, 0.62)))
    chassis.child("seat_driver", (-0.50, 0.95, -1.40))
    for side, sx in (("l", -1), ("r", 1)):
        chassis.child(f"light_head_{side}", (sx * 0.98, 1.47, -3.13))
        chassis.child(f"light_tail_{side}", (sx * 1.06, 0.97, 3.50))
    chassis.child("smoke_hood", (0, 1.80, -2.70))
    return root


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parents[2] / "assets" / "drycastle-generated-models" / "flak.glb")
    root = build()
    write_glb(root, out, materials_from(MATERIALS), "scripts/make_flak_model.py (Drycastle)")
    report(root, 16)
    GLASS.report()
    if len(sys.argv) <= 1:
        GLASS.write_vehicles_json(vehicles_json_path())
        print("wrote the glass list of vehicles.json")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
