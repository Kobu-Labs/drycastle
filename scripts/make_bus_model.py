#!/usr/bin/env python3
"""Builds assets/suchohrad-generated-models/bus.glb: the bus, a 12 m low-entry regional bus in red, laid out after the
one that serves the village (two doors on the right, low floor between them, raised floor behind the middle door).

Original geometry written by hand as code (no third-party model, no texture, no operator branding); standard library
only. The mesh helpers (box, loft, lathe, ...) are the flak script's, loaded from the file next to this one:

    python3 -I scripts/make_bus_model.py [out.glb]

Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground at the centre.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0]. Positions along the bus are written as
s, metres behind the front bumper (z = s - 6.025).

Layout, front to back (s):

    0.00 .. 0.53   front: windscreen with wrapped corners, destination display, mask with headlamps
    0.55 .. 1.85   right: front door (two glazed leaves)        left: driver's window over the black sweep
    1.85 .. 3.73   front axle at 2.71; tall glazing whose lower edge rises to the window line
    3.73 .. 5.28   window bay; two rows of seats on the low floor
    5.41 .. 6.71   right: middle door (two glazed leaves)       left: window bay, pram and wheelchair space
    7.00 .. 7.50   two steps up to the raised floor
    7.50 .. 10.10  window bays over the rear axle (8.73); four rows of seats
    10.10 .. 11.58 blank black panel; rear bench of five on the engine cover
    11.58 .. 12.05 rear: window, engine hatch, tail lamps

Node tree (every moving part is its own node, its origin is its pivot):

    bus
      hull                         shell, glazing, floor, seats, handrails, driver's cab (static)
        wheel_fl, wheel_fr         front wheels (l = -x), origin at the wheel centre, spin about X
        wheel_rl, wheel_rr         rear wheels (twin tyres)
        door_front_1, _2           front door leaves (1 is the forward one), origin at the leaf's outer edge on the sill
        door_mid_1, _2             middle door leaves
        seat_driver                empty: driver seat (feet)
        seat_passenger_1 .. _3     empties: the three passenger seats of vehicles.json (feet)
        light_head_l/r, light_tail_l/r, smoke_engine   empties: lamp positions and the smoke point (the engine is at the rear)
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
    # name: (r, g, b, roughness, metallic, emissive strength, alpha)
    "Red": (0.82, 0.12, 0.11, 0.45, 0.0, 0, 1),            # body paint
    "White": (0.92, 0.92, 0.90, 0.6, 0.0, 0, 1),           # the stripe along the skirt, number plates
    "Black": (0.045, 0.05, 0.055, 0.35, 0.0, 0, 1),        # window pillars, the sweep, door frames, bumpers
    "Glass": (0.10, 0.14, 0.17, 0.1, 0.0, 0, 0.42),        # tinted side and rear glazing (see-through)
    "GlassClear": (0.50, 0.62, 0.68, 0.1, 0.0, 0, 0.22),   # windscreen, cab partition
    "DarkMetal": (0.13, 0.14, 0.14, 0.7, 0.2, 0, 1),       # axles, wheel wells, thresholds, seat legs
    "Rubber": (0.07, 0.07, 0.075, 0.95, 0.0, 0, 1),        # tyres
    "Steel": (0.66, 0.68, 0.68, 0.45, 0.5, 0, 1),          # rims, mirror glass, badge
    "Trim": (0.30, 0.31, 0.33, 0.8, 0.0, 0, 1),            # dashboard, cab, wall lining, engine cover
    "Floor": (0.36, 0.37, 0.39, 0.9, 0.0, 0, 1),           # floor covering
    "Ceiling": (0.80, 0.80, 0.78, 0.9, 0.0, 0, 1),         # roof lining
    "Seat": (0.14, 0.19, 0.30, 0.95, 0.0, 0, 1),           # seat fabric
    "SeatHead": (0.10, 0.40, 0.48, 0.95, 0.0, 0, 1),       # headrest covers
    "Yellow": (0.95, 0.78, 0.10, 0.5, 0.0, 0, 1),          # handrails, step edges
    "Lamp": (0.98, 0.94, 0.75, 0.4, 0.0, 0.6, 1),          # headlamps
    "TailLamp": (0.78, 0.08, 0.06, 0.5, 0.0, 0.3, 1),      # tail lamps, marker lamps
    "Amber": (0.98, 0.55, 0.08, 0.5, 0.0, 0.7, 1),         # indicators, the destination display's lit dots
}

# ----------------------------------------------------------------------------- dimensions

LEN2, W2 = 6.025, 1.275            # half length, half width (12.05 m x 2.55 m)
WALL = 0.06                        # side wall thickness
XI = W2 - WALL                     # inner face of the side walls
SKIRT = 0.30                       # underside of the body
FLOOR, HIGH = 0.38, 0.86           # low floor (front door to the steps) and the raised floor behind it
SILL = 1.40                        # lower edge of the window band
WTOP = 2.56                        # upper edge of the window band
CANT = 2.80                        # top of the side walls, the ceiling
ROOF = 3.07
DOOR_TOP = 2.20
DOOR_SILL = 0.34
WHEEL_R = 0.48
ARCH_R = 0.56


def zs(s):
    """z of a point s metres behind the front bumper."""
    return s - LEN2


ZF, ZR = zs(0.525), zs(11.575)     # the side walls run between the front and the rear caps
AXLE_F, AXLE_R = zs(2.71), zs(8.73)
WHEEL_XF, WHEEL_XR = 1.05, 0.89    # wheel centres (single front tyres, twin rear)
BAYS = (3.73, 5.28, 6.84, 8.40, 10.10)          # window bay seams behind the tall front glazing
DOOR_F = (0.55, 1.85)              # door openings on the right side, s
DOOR_M = (5.41, 6.71)
STEP0, STEP1 = 7.00, 7.50          # the two steps
ENGINE = zs(10.975)                # front of the engine cover (the rear bench sits on it)
WELL_F = (0.66, 1.02)              # wheel wells: inner x and top y
WELL_R = (0.52, 1.04)
SEATS = {                          # vehicles.json seats (feet): the driver and three passengers
    "seat_driver": (-0.70, 0.62, zs(1.20)),
    "seat_passenger_1": (0.53, FLOOR, zs(3.85)),
    "seat_passenger_2": (-0.97, HIGH, zs(7.85)),
    "seat_passenger_3": (0.0, HIGH, zs(10.93)),
}


def sweep(s):
    """Height of the rising line that bounds the front glazing from below (the black sweep on the left side)."""
    return 0.75 + (s - 0.525) * 0.65 / (3.73 - 0.525)


def slab(mat, a, b, c, d, t=0.02):
    """A thin plate on the quad a-b-c-d, t thick (a window pane)."""
    n = unit(cross(sub(b, a), sub(d, a)))
    q = [a, b, c, d]
    return loft(mat, [[add(p, mul(n, -t / 2)) for p in q], [add(p, mul(n, t / 2)) for p in q]])


def tube(mat, pts, r, n=6):
    t = []
    for p, q in zip(pts, pts[1:]):
        t += cyl(mat, p, q, r, n)
    return t


def wall(mat, profile, x0=XI, x1=W2):
    """A piece of the right side wall: a (z, y) outline pushed through the wall's thickness."""
    return extrude(mat, profile, 'x', x0, x1)


def rect(s0, s1, y0, y1):
    return [(zs(s0), y0), (zs(s1), y0), (zs(s1), y1), (zs(s0), y1)]


def pane(profile):
    """Side glazing: thinner than the wall and set back from its outer face."""
    return wall("Glass", profile, W2 - 0.04, W2 - 0.012)


def pillar(s, y0, half=0.04):
    """A black window pillar on a bay seam, a little proud of the wall on both faces."""
    return wall("Black", rect(s - half, s + half, y0, WTOP), XI - 0.004, W2 + 0.004)


def arch(zc):
    """Wheel arch cut-out in the skirt, as outline points from front to back."""
    a0 = math.degrees(math.asin((WHEEL_R - SKIRT) / ARCH_R))
    return [(zc + ARCH_R * math.cos(math.radians(a)), WHEEL_R + ARCH_R * math.sin(math.radians(a)))
            for a in (180 + a0, 160, 135, 112, 90, 68, 45, 20, -a0)]


def front_outline(y):
    """Plan outline of the front cap at height y, as (x, z): chamfered corners, the windscreen leaning back above its base."""
    zf = -LEN2 + 0.07 * (min(max(y, 1.05), 2.62) - 1.05)
    ch = 0.24
    return [(-W2, ZF), (-W2, zf + ch), (-W2 + ch, zf), (W2 - ch, zf), (W2, zf + ch), (W2, ZF)]


def rear_outline(ch=0.10):
    return [(-W2, ZR), (-W2, LEN2 - ch), (-W2 + ch, LEN2), (W2 - ch, LEN2), (W2, LEN2 - ch), (W2, ZR)]


def wheel(twin):
    """A wheel about the X axis at the origin; the outer face is at +x. A twin wheel is two tyres with a dished rim."""
    if twin:
        prof = [(-0.31, 0.0, "Rubber"), (-0.31, 0.44), (-0.28, 0.48), (-0.04, 0.48), (-0.015, 0.45), (0.015, 0.45), (0.04, 0.48), (0.28, 0.48),
                (0.31, 0.44), (0.31, 0.29, "Steel"), (0.17, 0.26), (0.17, 0.13), (0.27, 0.10), (0.27, 0.0)]
    else:
        prof = [(-0.15, 0.0, "Rubber"), (-0.15, 0.44), (-0.12, 0.48), (0.12, 0.48), (0.15, 0.44), (0.15, 0.29, "Steel"),
                (0.11, 0.27), (0.11, 0.15), (0.17, 0.12), (0.17, 0.0)]
    return lathe((0, 0, 0), (1, 0, 0), prof, 14)


def well(xi, top, zc, half=0.62):
    """The wheel well of the right side as plates round the wheel (open to the outside, so the wheel shows in the arch)."""
    t = box("DarkMetal", xi, xi + 0.04, SKIRT, top, zc - half, zc + half)
    t += box("DarkMetal", xi, XI, top - 0.04, top, zc - half, zc + half)
    t += box("DarkMetal", xi, XI, SKIRT, top, zc - half, zc - half + 0.04)
    t += box("DarkMetal", xi, XI, SKIRT, top, zc + half - 0.04, zc + half)
    return t


def seat(x, y, z, w=0.43):
    """One forward-facing seat: x, z the cushion centre, y the height of the cushion's underside."""
    t = box("Seat", x - w / 2, x + w / 2, y, y + 0.10, z - 0.22, z + 0.22, c=0.02, axis='x', e=0)
    back = box("Seat", x - w / 2, x + w / 2, y + 0.08, y + 0.62, z + 0.17, z + 0.25, c=0.02, axis='x', e=0)
    back += box("SeatHead", x - w / 2 + 0.03, x + w / 2 - 0.03, y + 0.62, y + 0.80, z + 0.17, z + 0.25, c=0.02, axis='x', e=0)
    return t + rot(back, 'x', 9, (x, y + 0.08, z + 0.21))


def seat_pair(side, floor, z):
    """Two seats beside the window on one side (side = 1 right, -1 left), on a leg, with a grab handle on the aisle corner."""
    y = floor + 0.34
    t = seat(side * 0.985, y, z) + seat(side * 0.535, y, z)
    t += box("DarkMetal", side * 0.76 - 0.04, side * 0.76 + 0.04, floor, y, z - 0.10, z + 0.14)
    t += box("Yellow", side * 0.325 - 0.025, side * 0.325 + 0.025, y + 0.74, y + 0.80, z + 0.27, z + 0.31)
    return t


def door(s0, s1):
    """The fixed parts of a door of the right side: threshold and the black panel over the leaves."""
    t = wall("DarkMetal", rect(s0, s1, SKIRT, DOOR_SILL))
    t += wall("Black", rect(s0, s1, DOOR_TOP, WTOP))
    t += box("Yellow", XI - 0.05, XI, FLOOR - 0.002, FLOOR + 0.004, zs(s0), zs(s1))     # the edge of the floor at the door
    return t


def leaf(s0, s1):
    """One glazed door leaf between s0 and s1: a black frame with a handrail bar and two panes."""
    x0, x1 = W2 - 0.05, W2 - 0.008
    f = 0.045
    t = wall("Black", rect(s0, s0 + f, DOOR_SILL, DOOR_TOP), x0, x1) + wall("Black", rect(s1 - f, s1, DOOR_SILL, DOOR_TOP), x0, x1)
    for y0, y1 in ((DOOR_SILL, DOOR_SILL + 0.10), (1.02, 1.08), (DOOR_TOP - f, DOOR_TOP)):
        t += wall("Black", rect(s0 + f, s1 - f, y0, y1), x0, x1)
    t += wall("Glass", rect(s0 + f, s1 - f, DOOR_SILL + 0.10, 1.02), W2 - 0.04, W2 - 0.018)
    t += wall("Glass", rect(s0 + f, s1 - f, 1.08, DOOR_TOP - f), W2 - 0.04, W2 - 0.018)
    return t


def build():
    root = Node("bus")
    hull = root.child("hull")

    # ================================================================ side walls
    # ---- left side (no doors): one red panel from the skirt to the window line, the black sweep under the driver's window
    left = wall("Red", [(ZF, SKIRT)] + arch(AXLE_F) + arch(AXLE_R) + [(ZR, SKIRT), (ZR, SILL), (zs(3.73), SILL), (ZF, sweep(0.525))])
    s_flat = 0.525 + (1.05 - 0.75) * (3.73 - 0.525) / 0.65      # where the sweep reaches the driver's window sill (1.05)
    left += wall("Black", [(ZF, sweep(0.525)), (zs(s_flat), 1.05), (ZF, 1.05)])
    left += pane([(ZF, 1.05), (zs(1.85), 1.05), (zs(1.85), WTOP), (ZF, WTOP)])
    left += pane([(zs(1.85), 1.05), (zs(s_flat), 1.05), (zs(3.73), SILL), (zs(3.73), WTOP), (zs(1.85), WTOP)])
    left += pillar(1.85, 1.05)
    for s0, s1 in zip(BAYS, BAYS[1:]):
        left += pane(rect(s0, s1, SILL, WTOP))
        left += pillar(s0, SILL)
        if s0 > 5.0:      # opening vents at the top of the rear bays
            left += wall("Black", rect(s0 + 0.04, s1 - 0.04, 2.20, 2.23), W2 - 0.045, W2 - 0.006)
    left += wall("Black", rect(10.10, 11.575, SILL, WTOP))                       # blank panel beside the engine
    left += wall("Black", rect(10.85, 11.45, 0.92, 1.22), W2 - 0.01, W2 + 0.006)  # engine air grille
    for k in range(3):
        left += wall("DarkMetal", rect(10.88, 11.42, 0.97 + k * 0.08, 1.00 + k * 0.08), W2, W2 + 0.012)
    for s0, s1 in ((0.60, 2.12), (3.30, 8.14), (9.32, 11.50)):                   # white stripe along the skirt
        left += wall("White", rect(s0, s1, 0.44, 0.475), W2 - 0.01, W2 + 0.004)
    hull.add(mirror(left))

    # ---- right side: the doors, the same glazing line
    right = wall("Black", rect(0.525, 0.55, SKIRT, WTOP))
    right += door(*DOOR_F) + door(*DOOR_M)
    right += wall("Red", [(zs(1.85), SKIRT)] + arch(AXLE_F) + [(zs(DOOR_M[0]), SKIRT), (zs(DOOR_M[0]), SILL), (zs(3.73), SILL), (zs(1.85), sweep(1.85))])
    right += wall("Red", [(zs(DOOR_M[1]), SKIRT)] + arch(AXLE_R) + [(ZR, SKIRT), (ZR, SILL), (zs(DOOR_M[1]), SILL)])
    right += pane([(zs(1.85), sweep(1.85)), (zs(3.73), SILL), (zs(3.73), WTOP), (zs(1.85), WTOP)])
    right += wall("Black", rect(1.85, 1.93, DOOR_SILL, WTOP), XI - 0.004, W2 + 0.004)       # door posts
    right += wall("Black", rect(5.28, DOOR_M[0], SILL, WTOP), XI - 0.004, W2 + 0.004)
    right += wall("Black", rect(DOOR_M[1], 6.84, SILL, WTOP), XI - 0.004, W2 + 0.004)
    for s0, s1 in ((3.73, 5.28), (6.84, 8.40), (8.40, 10.10)):
        right += pane(rect(s0, s1, SILL, WTOP))
        right += wall("Black", rect(s0 + 0.04, s1 - 0.04, 2.20, 2.23), W2 - 0.045, W2 - 0.006)
    right += pillar(3.73, SILL) + pillar(8.40, SILL)
    right += wall("Black", rect(10.10, 11.575, SILL, WTOP))
    for s0, s1 in ((3.30, DOOR_M[0]), (DOOR_M[1], 8.14), (9.32, 11.50)):
        right += wall("White", rect(s0, s1, 0.44, 0.475), W2 - 0.01, W2 + 0.004)
    right += wall("Black", rect(0.75, 1.65, 2.27, 2.47), W2 - 0.004, W2 + 0.006)            # side display over the front door
    right += wall("Amber", rect(0.85, 1.10, 2.33, 2.41), W2 + 0.006, W2 + 0.009) + wall("Amber", rect(1.18, 1.55, 2.33, 2.41), W2 + 0.006, W2 + 0.009)
    hull.add(right)

    # ---- cant rail (both sides) and the roof
    hull.add(both(wall("Red", [(ZF, WTOP), (ZR, WTOP), (ZR, CANT), (ZF, CANT)])))
    crown = [(-W2, CANT), (W2, CANT), (W2, 2.86), (1.17, 3.00), (0.95, ROOF), (-0.95, ROOF), (-1.17, 3.00), (-W2, 2.86)]

    def roof_ring(z, k):
        return [(x * (0.93 + 0.07 * k), CANT + (y - CANT) * (0.35 + 0.65 * k), z) for x, y in crown]
    zroof = front_outline(CANT)[2][1]
    hull.add(loft("Red", [roof_ring(zroof + 0.06, 0), roof_ring(zroof + 0.40, 1), roof_ring(LEN2 - 0.30, 1), roof_ring(LEN2 - 0.03, 0)]))
    hull.add(box("Ceiling", -XI, XI, CANT - 0.035, CANT - 0.005, ZF, ZR))
    # roof equipment: the air conditioning pod, two hatches
    hull.add(box("Red", -0.86, 0.86, ROOF - 0.02, 3.32, zs(2.50), zs(6.00), c=0.09, axis='z', e=0.08))
    hull.add(both(box("Black", 0.80, 0.868, 3.145, 3.225, zs(3.0), zs(5.5))))
    for s in (7.6, 10.0):
        hull.add(box("Black", -0.40, 0.40, ROOF - 0.01, ROOF + 0.05, zs(s), zs(s + 0.80), c=0.015, axis='z', e=0.015))

    # ================================================================ front
    low = front_outline(0)
    hull.add(extrude("Red", low, 'y', SKIRT, 1.05))
    b0, b1 = front_outline(1.05), front_outline(2.62)

    def at(o, y): return (o[0], y, o[1])
    for i in (1, 2, 3):                                    # windscreen: the front pane and two wrapped corner panes
        hull.add(slab("GlassClear", at(b0[i], 1.05), at(b0[i + 1], 1.05), at(b1[i + 1], 2.62), at(b1[i], 2.62)))
    for i in (1, 2, 3, 4):                                 # A pillars
        hull.add(cyl("Black", at(b0[i], 1.05), at(b1[i], 2.62), 0.035 if i in (1, 4) else 0.028, 4, smooth=False))
    stub = [(ZF, 1.05), (b0[4][1], 1.05), (b1[4][1], 2.62), (ZF, 2.62)]
    hull.add(both(wall("Black", stub)))
    hull.add(extrude("Red", front_outline(2.62), 'y', 2.62, CANT))
    hull.add(extrude("Black", b0, 'y', 1.05, 1.062))             # the base of the windscreen: a dark deck over the mask
    zf = -LEN2
    # mask: grille slot, number plate, badge, lamps, bumper edge
    hull.add(box("Black", -0.62, 0.62, 0.62, 0.76, zf - 0.012, zf + 0.02))
    hull.add(box("White", -0.26, 0.26, 0.44, 0.56, zf - 0.014, zf + 0.02))
    hull.add(box("Steel", -0.10, 0.10, 0.90, 0.95, zf - 0.012, zf + 0.02))
    hull.add(box("Black", -1.03, 1.03, SKIRT, 0.36, zf - 0.02, zf + 0.02))
    lamp = box("Black", 0.70, 1.03, 0.60, 0.80, zf - 0.012, zf + 0.02) + box("Lamp", 0.76, 0.99, 0.63, 0.77, zf - 0.02, zf + 0.02)
    lamp += slab("Amber", (1.045, 0.63, zf + 0.012), (1.20, 0.63, zf + 0.167), (1.20, 0.77, zf + 0.167), (1.045, 0.77, zf + 0.012), 0.03)
    hull.add(both(lamp))
    for x in (-0.55, 0.45):                                # wipers, parked
        hull.add(cyl("Black", (x, 1.07, zf - 0.03), (x + 0.62, 1.16, zf - 0.02), 0.014, 4, smooth=False))
    # destination display behind the top of the windscreen: lit blocks stand for the line number and the destination
    zd = front_outline(2.45)[2][1] + 0.05
    hull.add(box("Black", -0.98, 0.98, 2.28, 2.62, zd, zd + 0.06))
    for x0, x1 in ((-0.86, -0.60), (-0.44, -0.16), (-0.10, 0.22), (0.28, 0.48), (0.54, 0.86)):
        hull.add(box("Amber", x0, x1, 2.39, 2.51, zd - 0.006, zd + 0.01))
    # mirrors on arms from the roof corners
    arm = tube("Black", [(1.20, 2.50, zf + 0.20), (1.50, 2.44, zf - 0.16), (1.50, 2.02, zf - 0.16)], 0.02, 5)
    arm += box("Black", 1.40, 1.60, 1.56, 2.04, zf - 0.21, zf - 0.13, c=0.03, axis='z', e=0.02)
    arm += box("Steel", 1.42, 1.58, 1.59, 2.01, zf - 0.135, zf - 0.124)
    hull.add(both(arm))

    # ================================================================ rear
    ro = rear_outline()
    hull.add(extrude("Red", ro, 'y', SKIRT, 1.75))
    corner = [(XI - 0.215, ZR), (W2, ZR), (W2, LEN2 - 0.10), (W2 - 0.10, LEN2), (XI - 0.215, LEN2)]
    hull.add(both(extrude("Red", corner, 'y', 1.75, WTOP)))
    hull.add(slab("Glass", (-1.0, 1.75, LEN2 - 0.02), (1.0, 1.75, LEN2 - 0.02), (1.0, WTOP, LEN2 - 0.02), (-1.0, WTOP, LEN2 - 0.02), 0.024))
    hull.add(extrude("Red", ro, 'y', WTOP, CANT))
    hull.add(box("Trim", -1.0, 1.0, 1.75, 1.762, ZR, LEN2 - 0.04))  # parcel shelf under the rear window
    hull.add(box("Trim", -XI, XI, 1.28, 1.75, ZR - 0.012, ZR))       # lining behind the rear bench
    zb = LEN2
    hull.add(box("Black", -1.17, 1.17, SKIRT, 0.50, zb - 0.02, zb + 0.03))                    # bumper
    for k in range(4):                                                                       # engine hatch louvres
        hull.add(box("Black", -0.62, 0.62, 0.60 + k * 0.09, 0.65 + k * 0.09, zb - 0.02, zb + 0.012))
    hull.add(box("Black", -0.90, 0.90, 1.52, 1.535, zb - 0.02, zb + 0.008))                   # hatch seam
    hull.add(box("White", -0.26, 0.26, 1.06, 1.18, zb - 0.02, zb + 0.014))
    tail = box("TailLamp", 0.93, 1.15, 0.98, 1.36, zb - 0.02, zb + 0.016) + box("Amber", 0.93, 1.15, 1.38, 1.52, zb - 0.02, zb + 0.016)
    tail += box("White", 0.93, 1.15, 0.86, 0.96, zb - 0.02, zb + 0.016) + box("TailLamp", 0.95, 1.13, 2.62, 2.68, zb - 0.02, zb + 0.014)
    hull.add(both(tail))

    # ================================================================ floor, wells, running gear
    hull.add(box("Floor", -WELL_F[0], WELL_F[0], SKIRT, FLOOR, ZF, zs(STEP0)))              # low floor, between the wells
    for z0, z1 in ((ZF, AXLE_F - 0.62), (AXLE_F + 0.62, zs(STEP0))):
        hull.add(both(box("Floor", WELL_F[0], XI, SKIRT, FLOOR, z0, z1)))
    hull.add(both(well(*WELL_F, AXLE_F)))
    mid = (STEP0 + STEP1) / 2
    hull.add(box("Floor", -XI, XI, SKIRT, 0.54, zs(STEP0), zs(mid)), box("Floor", -XI, XI, SKIRT, 0.70, zs(mid), zs(STEP1)))   # the two steps
    for s, y in ((STEP0, 0.54), (mid, 0.70), (STEP1, HIGH)):
        hull.add(box("Yellow", -XI, XI, y - 0.03, y + 0.004, zs(s) - 0.004, zs(s) + 0.04))
    hull.add(box("Floor", -WELL_R[0], WELL_R[0], SKIRT, HIGH, zs(STEP1), ENGINE))            # raised floor
    for z0, z1 in ((zs(STEP1), AXLE_R - 0.62), (AXLE_R + 0.62, ENGINE)):
        hull.add(both(box("Floor", WELL_R[0], XI, SKIRT, HIGH, z0, z1)))
    hull.add(both(well(*WELL_R, AXLE_R)))
    hull.add(box("Trim", -XI, XI, SKIRT, 1.28, ENGINE, ZR))                                  # engine cover
    hull.add(box("DarkMetal", -WHEEL_XF + 0.15, WHEEL_XF - 0.15, 0.20, 0.29, AXLE_F - 0.06, AXLE_F + 0.06))   # dropped front axle beam, under the floor
    for name, x, z, twin in (("wheel_fl", -WHEEL_XF, AXLE_F, False), ("wheel_fr", WHEEL_XF, AXLE_F, False), ("wheel_rl", -WHEEL_XR, AXLE_R, True), ("wheel_rr", WHEEL_XR, AXLE_R, True)):
        n = hull.child(name, (x, WHEEL_R, z))
        w = wheel(twin)
        n.add(move(w if x > 0 else mirror(w), n.origin))

    # ================================================================ doors (leaves are nodes)
    for name, (s0, s1) in (("door_front", DOOR_F), ("door_mid", DOOR_M)):
        sm = (s0 + s1) / 2
        hull.child(name + "_1", (W2 - 0.03, DOOR_SILL, zs(s0))).add(leaf(s0, sm - 0.004))
        hull.child(name + "_2", (W2 - 0.03, DOOR_SILL, zs(s1))).add(leaf(sm + 0.004, s1))

    # ================================================================ interior
    # ---- wall lining under the windows, dashboard back
    def lining(s0, s1, y0):
        return box("Trim", XI - 0.012, XI, y0, SILL, zs(s0), zs(s1))
    high = lining(STEP1, 10.975, WELL_R[1])                   # above the rear wheel well, so the wheel shows in its arch
    hull.add(mirror(lining(3.73, STEP1, FLOOR) + high))
    hull.add(lining(3.73, DOOR_M[0], FLOOR), lining(DOOR_M[1], STEP1, FLOOR), high)
    hull.add(box("Trim", -XI, XI, FLOOR, 1.05, ZF, ZF + 0.03))
    hull.add(box("Trim", -0.20, XI, 0.98, 1.05, ZF, ZF + 0.22))                               # shelf under the windscreen, door side

    # ---- driver's cab (front left, on a platform)
    cab_y = SEATS["seat_driver"][1]
    hull.add(box("Floor", -XI, -0.24, FLOOR, cab_y, ZF, zs(1.72)))
    hull.add(box("Trim", -XI, -0.24, cab_y, 1.12, ZF, zs(0.86), c=0.05, axis='x', e=0))        # dashboard
    hull.add(box("Black", -1.00, -0.42, 1.12, 1.24, zs(0.60), zs(0.80), c=0.03, axis='x', e=0))  # instrument binnacle
    col0, col1 = (-0.70, 1.02, zs(0.82)), (-0.70, 1.22, zs(0.93))
    hull.add(cyl("Black", col0, col1, 0.03, 6))
    hull.add(lathe(col1, sub(col1, col0), [(0.0, 0.035, "Black"), (0.03, 0.05), (0.03, 0.19), (0.015, 0.215), (0.045, 0.235), (0.06, 0.215), (0.045, 0.19), (0.045, 0.0)], 12))
    hull.add(box("DarkMetal", -0.82, -0.58, cab_y, cab_y + 0.34, zs(1.14), zs(1.42)))         # seat base
    hull.add(seat(-0.70, cab_y + 0.34, zs(1.30), 0.48))
    hull.add(box("Trim", -XI, -0.26, cab_y, 1.50, zs(1.69), zs(1.72)))                        # partition behind the driver
    hull.add(slab("GlassClear", (-XI, 1.50, zs(1.705)), (-0.26, 1.50, zs(1.705)), (-0.26, 2.25, zs(1.705)), (-XI, 2.25, zs(1.705)), 0.016))
    hull.add(box("Trim", -0.27, -0.24, FLOOR, 1.32, zs(0.86), zs(1.72)))                      # cab door to the aisle
    hull.add(box("Black", -0.24, -0.10, 1.05, 1.34, zs(0.92), zs(1.14), c=0.02, axis='y', e=0))  # ticket machine
    hull.add(cyl("DarkMetal", (-0.17, FLOOR, zs(1.03)), (-0.17, 1.05, zs(1.03)), 0.03, 6))

    # ---- seats. Front wheel wells carry two single seats each
    for side in (1, -1):
        for s in (2.42, 3.12):
            hull.add(seat(side * 0.96, WELL_F[1], zs(s)))
        for s in (3.95, 4.70):                              # low floor rows
            hull.add(seat_pair(side, FLOOR, zs(s)))
        for s in (7.95, 8.70, 9.45, 10.20):                 # raised floor rows
            hull.add(seat_pair(side, HIGH, zs(s)))
    hull.add(seat_pair(-1, FLOOR, zs(5.45)))          # left only: a third low row before the pram space
    for x in (-0.94, -0.47, 0.0, 0.47, 0.94):                # rear bench of five on the engine cover
        hull.add(seat(x, 1.28, ENGINE + 0.26, 0.45))
    # ---- pram and wheelchair space, left, opposite the middle door: a padded rest, two tip-up seats folded to the wall, a rail
    hull.add(box("Seat", -XI + 0.012, -XI + 0.09, 0.78, 1.30, zs(5.86), zs(5.94)))
    for s in (6.10, 6.56):
        hull.add(box("Seat", -XI + 0.012, -XI + 0.10, 0.80, 1.24, zs(s), zs(s + 0.40), c=0.02, axis='x', e=0))
    hull.add(tube("Yellow", [(-XI + 0.02, 1.02, zs(6.04)), (-XI + 0.16, 1.02, zs(6.06)), (-XI + 0.16, 1.02, zs(6.98)), (-XI + 0.02, 1.02, zs(7.0))], 0.016, 5))

    # ---- handrails: two rails under the ceiling along the aisle, stanchions at the doors, the steps and the seat rows
    rail_y = 2.58
    for side in (1, -1):
        x = side * 0.33
        hull.add(cyl("Yellow", (x, rail_y, zs(1.95)), (x, rail_y, zs(10.60)), 0.016, 5))
        for s in (2.0, 4.3, 6.2, 8.3, 10.5):
            hull.add(cyl("Yellow", (x, rail_y, zs(s)), (x, CANT - 0.03, zs(s)), 0.012, 4, smooth=False))
        for s, y0 in ((3.55, FLOOR), (STEP0 - 0.04, FLOOR), (9.07, HIGH)):
            hull.add(cyl("Yellow", (x, y0, zs(s)), (x, rail_y, zs(s)), 0.016, 5))
    for s in (DOOR_F[1] + 0.12, DOOR_M[0] - 0.06, DOOR_M[1] + 0.06):                         # door posts, floor to ceiling
        hull.add(cyl("Yellow", (0.96, FLOOR, zs(s)), (0.96, CANT - 0.03, zs(s)), 0.016, 5))
    hull.add(cyl("Yellow", (0.96, 1.0, zs(DOOR_M[0] - 0.06)), (XI, 1.0, zs(DOOR_M[0] - 0.06)), 0.014, 4, smooth=False))
    hull.add(cyl("Yellow", (0.96, 1.0, zs(DOOR_M[1] + 0.06)), (XI, 1.0, zs(DOOR_M[1] + 0.06)), 0.014, 4, smooth=False))

    # ================================================================ markers
    for name, p in SEATS.items():
        hull.child(name, p)
    for side, sx in (("l", -1), ("r", 1)):
        hull.child(f"light_head_{side}", (sx * 0.875, 0.70, -LEN2 - 0.02))
        hull.child(f"light_tail_{side}", (sx * 1.04, 1.17, LEN2 + 0.02))
    hull.child("smoke_engine", (0.0, 1.30, LEN2 + 0.10))
    return root


# ----------------------------------------------------------------------------- GLB

def write_glb(root, path):
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS)
    materials = []
    for name in mat_names:
        r, g, b, rough, metal, emis, alpha = MATERIALS[name]
        lin = [round(kit.srgb_to_linear(v), 4) for v in (r, g, b)]
        m = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": lin + [alpha], "metallicFactor": metal, "roughnessFactor": rough}}
        if emis:
            m["emissiveFactor"] = [round(v * emis, 4) for v in lin]
        if alpha < 1:
            m["alphaMode"] = "BLEND"
            m["doubleSided"] = True
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
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_bus_model.py (Suchohrad)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": root.name, "nodes": [top]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent / "assets" / "suchohrad-generated-models" / "bus.glb")
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
        print(f"{n.name:18s} pivot=({n.origin[0]:.3f}, {n.origin[1]:.3f}, {n.origin[2]:.3f}) tris={len(n.tris)}")
    print(f"triangles: {total}")
    print("bounds: x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
