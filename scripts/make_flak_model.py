#!/usr/bin/env python3
"""Builds assets/suchohrad-generated-models/flak.glb: the flak, a 6x6 truck with a twin 35 mm anti-aircraft turret.

Original geometry written by hand as code (no third-party model, no texture); standard library only:

    python3 -I scripts/make_flak_model.py [out.glb]

Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground at the centre.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0].

Node tree (every moving part is its own node, its origin is its pivot):

    flak
      chassis                      frame, bed, fittings (static)
        cab                        bonnet and driver cab
        wheel_fl .. wheel_rr       6 wheels (f, m, r axle; l = -x), origin at the wheel centre, spin about X
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
"""
import json
import math
import struct
import sys
from pathlib import Path

# ----------------------------------------------------------------------------- vectors

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def unit(a):
    l = math.sqrt(dot(a, a))
    return (a[0] / l, a[1] / l, a[2] / l) if l > 1e-12 else (0.0, 1.0, 0.0)


# A triangle is (material, (p0, p1, p2), normals or None). None: flat shaded.

def fix_winding(tris):
    """Orients a closed surface outwards (by its signed volume) and makes vertex normals agree with the faces."""
    if not tris:
        return tris
    n = sum(len(t[1]) for t in tris)
    c = (0.0, 0.0, 0.0)
    for t in tris:
        for p in t[1]:
            c = add(c, p)
    c = mul(c, 1.0 / n)
    vol = sum(dot(sub(t[1][0], c), cross(sub(t[1][1], c), sub(t[1][2], c))) for t in tris)
    out = []
    for m, (a, b, cc), nr in tris:
        if vol < 0:
            a, b, cc = a, cc, b
            if nr:
                nr = (nr[0], nr[2], nr[1])
        if nr:
            fn = cross(sub(b, a), sub(cc, a))
            nr = tuple(v if dot(v, fn) >= 0 else mul(v, -1) for v in nr)
        out.append((m, (a, b, cc), nr))
    return out


def triangulate(ring):
    """Ear clipping of a planar polygon (3D points); returns index triples."""
    n = len(ring)
    if n < 3:
        return []
    nrm = (0.0, 0.0, 0.0)
    for i in range(n):
        a, b = ring[i], ring[(i + 1) % n]
        nrm = add(nrm, ((a[1] - b[1]) * (a[2] + b[2]), (a[2] - b[2]) * (a[0] + b[0]), (a[0] - b[0]) * (a[1] + b[1])))
    nrm = unit(nrm)
    u = unit(cross(nrm, (1, 0, 0) if abs(nrm[0]) < 0.9 else (0, 1, 0)))
    v = cross(nrm, u)
    pts = [(dot(p, u), dot(p, v)) for p in ring]

    def area2(a, b, c): return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])

    idx = list(range(n))
    out = []
    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        clipped = False
        for k in range(len(idx)):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % len(idx)]
            a, b, c = pts[i0], pts[i1], pts[i2]
            if area2(a, b, c) <= 1e-12:
                continue
            inside = False
            for j in idx:
                if j in (i0, i1, i2):
                    continue
                p = pts[j]
                if area2(a, b, p) > 1e-12 and area2(b, c, p) > 1e-12 and area2(c, a, p) > 1e-12:
                    inside = True
                    break
            if inside:
                continue
            out.append((i0, i1, i2))
            idx.pop(k)
            clipped = True
            break
        if not clipped:
            idx.pop(0)  # degenerate: give up on a vertex
    if len(idx) == 3:
        out.append(tuple(idx))
    return out


def loft(mat, rings, cap0=True, cap1=True):
    """Skin between consecutive rings (same point count), with end caps."""
    tris = []
    n = len(rings[0])
    for k in range(len(rings) - 1):
        r0, r1 = rings[k], rings[k + 1]
        for i in range(n):
            j = (i + 1) % n
            a, b, c, d = r0[i], r0[j], r1[j], r1[i]
            tris.append((mat, (a, b, c), None))
            tris.append((mat, (a, c, d), None))
    if cap0:
        tris += [(mat, (rings[0][c], rings[0][b], rings[0][a]), None) for a, b, c in triangulate(rings[0])]
    if cap1:
        tris += [(mat, (rings[-1][a], rings[-1][b], rings[-1][c]), None) for a, b, c in triangulate(rings[-1])]
    tris = [t for t in tris if dot(cross(sub(t[1][1], t[1][0]), sub(t[1][2], t[1][0])), cross(sub(t[1][1], t[1][0]), sub(t[1][2], t[1][0]))) > 1e-16]
    return fix_winding(tris)


def place(axis, a, b, t):
    """Profile point (a, b) at position t along the axis. x: profile is (z, y). y: (x, z). z: (x, y)."""
    if axis == 'x': return (t, b, a)
    if axis == 'y': return (a, t, b)
    return (a, b, t)


def inset(profile, d):
    n = len(profile)
    area = sum(profile[i][0] * profile[(i + 1) % n][1] - profile[(i + 1) % n][0] * profile[i][1] for i in range(n))
    s = 1.0 if area > 0 else -1.0
    out = []
    for i in range(n):
        p0, p1, p2 = profile[i - 1], profile[i], profile[(i + 1) % n]

        def inward(a, b):
            ex, ey = b[0] - a[0], b[1] - a[1]
            l = math.hypot(ex, ey) or 1.0
            return (-ey / l * s, ex / l * s)
        n1, n2 = inward(p0, p1), inward(p1, p2)
        k = 1.0 + n1[0] * n2[0] + n1[1] * n2[1]
        k = max(k, 0.3)
        out.append((p1[0] + d * (n1[0] + n2[0]) / k, p1[1] + d * (n1[1] + n2[1]) / k))
    return out


def extrude(mat, profile, axis, lo, hi, bevel=0.0):
    """A polygon profile pushed along an axis; bevel > 0 chamfers both end faces."""
    if bevel > 0:
        small = inset(profile, bevel)
        rings = [[place(axis, a, b, lo) for a, b in small], [place(axis, a, b, lo + bevel) for a, b in profile],
                 [place(axis, a, b, hi - bevel) for a, b in profile], [place(axis, a, b, hi) for a, b in small]]
    else:
        rings = [[place(axis, a, b, lo) for a, b in profile], [place(axis, a, b, hi) for a, b in profile]]
    return loft(mat, rings)


def box(mat, x0, x1, y0, y1, z0, z1, c=0.0, axis='x', e=None):
    """A box; c chamfers the four edges along the axis, e (default c) bevels the two end faces."""
    if axis == 'x': a0, a1, b0, b1, lo, hi = z0, z1, y0, y1, x0, x1
    elif axis == 'y': a0, a1, b0, b1, lo, hi = x0, x1, z0, z1, y0, y1
    else: a0, a1, b0, b1, lo, hi = x0, x1, y0, y1, z0, z1
    if c > 0:
        prof = [(a0 + c, b0), (a1 - c, b0), (a1, b0 + c), (a1, b1 - c), (a1 - c, b1), (a0 + c, b1), (a0, b1 - c), (a0, b0 + c)]
    else:
        prof = [(a0, b0), (a1, b0), (a1, b1), (a0, b1)]
    return extrude(mat, prof, axis, lo, hi, c if e is None else e)


def lathe(origin, direction, profile, n=12, smooth=True, mat=None):
    """Surface of revolution about the line origin + t * direction. profile: (t, r) or (t, r, material of the segment that starts here)."""
    d = unit(direction)
    u = unit(cross(d, (0, 1, 0) if abs(d[1]) < 0.9 else (1, 0, 0)))
    v = cross(d, u)
    cur = mat
    pts = []
    for p in profile:
        if len(p) > 2:
            cur = p[2]
        pts.append((p[0], p[1], cur))
    tris = []
    for k in range(len(pts) - 1):
        t0, r0, m = pts[k]
        t1, r1, _ = pts[k + 1]
        if abs(t1 - t0) < 1e-9 and abs(r1 - r0) < 1e-9:
            continue
        l = math.hypot(t1 - t0, r1 - r0)
        nt, nr = -(r1 - r0) / l, (t1 - t0) / l
        for i in range(n):
            a0, a1 = 2 * math.pi * i / n, 2 * math.pi * (i + 1) / n

            def P(t, r, a): return add(add(origin, mul(d, t)), add(mul(u, r * math.cos(a)), mul(v, r * math.sin(a))))

            def N(a): return unit(add(mul(d, nt), add(mul(u, nr * math.cos(a)), mul(v, nr * math.sin(a)))))
            A, B, C, D = P(t0, r0, a0), P(t0, r0, a1), P(t1, r1, a1), P(t1, r1, a0)
            nn = (lambda *aa: tuple(N(x) for x in aa)) if smooth else (lambda *aa: None)
            if r0 > 1e-9:
                tris.append((m, (A, B, C), nn(a0, a1, a1)))
            if r1 > 1e-9:
                tris.append((m, (A, C, D), nn(a0, a1, a0)))
    return fix_winding(tris)


def cyl(mat, p0, p1, r, n=8, r1=None, smooth=True):
    """A capped cylinder (or cone frustum with r1) between two points."""
    r1 = r if r1 is None else r1
    l = math.sqrt(dot(sub(p1, p0), sub(p1, p0)))
    return lathe(p0, sub(p1, p0), [(0, 0), (0, r), (l, r1), (l, 0)], n, smooth, mat)


def panel(mat, a, b, c, d, u0, u1, v0, v1, h, centre):
    """A raised panel on the quad a-b-c-d (u runs a to b, v runs a to d), standing h out of the face (away from centre)."""
    def P(u, v):
        top = add(mul(a, 1 - u), mul(b, u))
        bot = add(mul(d, 1 - u), mul(c, u))
        return add(mul(top, 1 - v), mul(bot, v))
    q = [P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1)]
    n = unit(cross(sub(b, a), sub(d, a)))
    mid = mul(add(add(a, b), add(c, d)), 0.25)
    if dot(n, sub(mid, centre)) < 0:
        n = mul(n, -1)
    return loft(mat, [[add(p, mul(n, -0.01)) for p in q], [add(p, mul(n, h)) for p in q]], cap0=False)


def move(tris, t):
    return [(m, tuple(add(p, t) for p in ps), nr) for m, ps, nr in tris]


def rot(tris, axis, deg, about=(0.0, 0.0, 0.0)):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))

    def r(p):
        x, y, z = p
        if axis == 'x': return (x, y * c - z * s, y * s + z * c)
        if axis == 'y': return (x * c + z * s, y, -x * s + z * c)
        return (x * c - y * s, x * s + y * c, z)
    return [(m, tuple(add(r(sub(p, about)), about) for p in ps), tuple(r(v) for v in nr) if nr else None) for m, ps, nr in tris]


def mirror(tris):
    """Mirror in the x = 0 plane."""
    def f(p): return (-p[0], p[1], p[2])
    return [(m, (f(a), f(c), f(b)), (f(nr[0]), f(nr[2]), f(nr[1])) if nr else None) for m, (a, b, c), nr in tris]


def both(tris):
    return tris + mirror(tris)


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
    "Red": (0.72, 0.13, 0.10, 0.7, 0.0, 0),           # tail lamps, tow hooks, extinguisher
    "Lamp": (0.98, 0.94, 0.75, 0.4, 0.0, 0.6),        # headlamp lenses
    "Amber": (0.95, 0.50, 0.08, 0.6, 0.0, 0.2),       # indicators
}


class Node:
    def __init__(self, name, origin=(0.0, 0.0, 0.0)):
        self.name, self.origin, self.tris, self.children = name, origin, [], []

    def add(self, *parts):
        for p in parts:
            self.tris += p
        return self

    def child(self, name, origin=None):
        n = Node(name, self.origin if origin is None else origin)
        self.children.append(n)
        return n

    def walk(self):
        yield self
        for c in self.children:
            yield from c.walk()


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


def build():
    root = Node("flak")
    chassis = root.child("chassis")
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

    # ---- cab: lower body and glasshouse
    cab.add(box("Olive", -1.20, 1.20, 0.95, 1.78, -2.15, -0.80, c=0.05, axis='x', e=0.03))

    def ring(y, xh, zf, zb):
        return [(-xh, y, zf), (xh, y, zf), (xh, y, zb), (-xh, y, zb)]
    g0, g1, g2 = ring(1.78, 1.17, -2.12, -0.83), ring(2.28, 1.03, -1.80, -0.90), ring(2.36, 0.93, -1.68, -1.00)
    cab.add(loft("Olive", [g0, g1, g2], cap0=False))
    cc = (0, 2.0, -1.4)
    f = (g0[0], g0[1], g1[1], g1[0])     # windscreen face
    cab.add(panel("Glass", *f, 0.045, 0.485, 0.10, 0.90, 0.012, cc), panel("Glass", *f, 0.515, 0.955, 0.10, 0.90, 0.012, cc))
    for i0, i1, s in ((1, 2, 1), (3, 0, -1)):  # side windows: door glass and a quarter light (u runs along the face)
        q = (g0[i0], g0[i1], g1[i1], g1[i0])
        d1, d2, q1, q2 = (0.06, 0.60, 0.68, 0.93) if s > 0 else (0.40, 0.94, 0.07, 0.32)
        cab.add(panel("Glass", *q, d1, d2, 0.10, 0.88, 0.012, cc), panel("Glass", *q, q1, q2, 0.10, 0.88, 0.012, cc))
    cab.add(panel("Glass", g0[2], g0[3], g1[3], g1[2], 0.28, 0.72, 0.15, 0.80, 0.012, cc))  # rear window
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
    chassis.add(both(box("Red", 0.94, 1.18, 0.90, 1.04, 3.465, 3.50)), both(box("Amber", 0.80, 0.92, 0.90, 1.04, 3.465, 3.495)))
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
            n = chassis.child(f"wheel_{ax}{side}", (sx * WHEEL_X, WHEEL_R, z))
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


# ----------------------------------------------------------------------------- GLB

def srgb_to_linear(c): return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def write_glb(root, path):
    buf = bytearray()
    views, accessors, meshes, nodes = [], [], [], []
    mat_names = list(MATERIALS)
    materials = []
    for name in mat_names:
        r, g, b, rough, metal, emis = MATERIALS[name]
        lin = [round(srgb_to_linear(v), 4) for v in (r, g, b)]
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
            entry["translation"] = [round(v, 5) for v in t]
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
                lo = [min(p[i] for p, _ in verts) for i in range(3)]
                hi = [max(p[i] for p, _ in verts) for i in range(3)]
                lo = [struct.unpack("<f", struct.pack("<f", v))[0] for v in lo]
                hi = [struct.unpack("<f", struct.pack("<f", v))[0] for v in hi]
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
    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_flak_model.py (Suchohrad)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "scenes": [{"name": "flak", "nodes": [top]}], "nodes": nodes, "meshes": meshes, "materials": materials,
            "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent / "assets" / "suchohrad-generated-models" / "flak.glb")
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
