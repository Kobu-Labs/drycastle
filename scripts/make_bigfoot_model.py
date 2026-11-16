#!/usr/bin/env python3
"""Builds the Bigfoot of the Bigfoot event (issue #286), body and worn head, from nothing but this file:

    assets/drycastle-generated-models/bigfoot.glb        the giant: one skinned mesh, 26 bones, its clips
    assets/drycastle-generated-models/bigfoot_head.glb   its head as the headgear Item (items.json id `bigfoot_head`), unskinned

    python3 scripts/make_bigfoot_model.py [out_dir]      (needs numpy; deterministic)

Original geometry and animation written as code: no third-party model, no texture. Flat-coloured and faceted like the other generated models.

Frame of both files: metres, x to the creature's right, y up, FACING -Z (the game's forward). The body stands on y 0 and is 3.6 m to the top of
its crest (bigfoot.json `height_m`, `model_scale` 1, `model_yaw_deg` 0). The head Item's origin is the middle of the wearer's head (the slot
frame of a worn headgear Item, CharacterView.Worn.cs); it is the body's head at 1 / HEAD_SCALE.

Skeleton: every bone's rest rotation is identity (a bone's local axes are the model's axes), so a pose is plain rotations about x / y / z.

    Root > Hips > Spine > Chest > Neck > Head > Jaw
                                Chest > Clavicle.S > UpperArm.S > Forearm.S > Hand.S > Fingers.S
                  Hips > Thigh.S > Shin.S > Foot.S > Toe.S                                           (S: L at -x, R at +x)

Clips are functions of the clip's phase that return a pose (see `Pose`): spine angles, where each hand and ankle is (two-bone IK solves the
limbs), jaw, fingers. They are sampled at 30 frames a second into rotation tracks for every bone and a translation track for the hips.
A looped clip's last frame is its first. STRIDE gives the metres a locomotion clip covers per loop: the game plays the clip at
ground speed / (stride / length), so the feet do not slide.
"""
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np

# ----------------------------------------------------------------------------- materials (sRGB)

MATERIALS = {
    # name: (r, g, b, roughness, emissive strength)
    "Fur": (0.19, 0.14, 0.11, 0.95, 0),
    "FurDark": (0.10, 0.075, 0.065, 0.95, 0),
    "FurLight": (0.30, 0.22, 0.16, 0.95, 0),
    "Skin": (0.31, 0.25, 0.23, 0.85, 0),          # leathery grey: face, chest, palms, soles
    "SkinDark": (0.15, 0.115, 0.105, 0.85, 0),
    "Mouth": (0.33, 0.04, 0.04, 0.8, 0),
    "Black": (0.02, 0.015, 0.015, 0.9, 0),
    "Eye": (1.0, 0.22, 0.04, 0.4, 3.0),           # burning: reads in shade and at range
    "Tooth": (0.88, 0.82, 0.62, 0.6, 0),
    "Claw": (0.70, 0.63, 0.47, 0.6, 0),
}
FUR = "FUR"  # a facet of fur: one of the three shades, picked per triangle


def fur_pick(i):
    h = (i * 2654435761) % 100
    return "Fur" if h < 60 else "FurDark" if h < 84 else "FurLight"


# ----------------------------------------------------------------------------- small maths

X, Y, Z = np.array([1.0, 0, 0]), np.array([0, 1.0, 0]), np.array([0, 0, 1.0])


def V(*a):
    return np.array(a, float)


def unit(v):
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else v


def rx(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def ry(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rz(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def euler(lean, yaw=0.0, roll=0.0):
    """Lean: forward (the top goes towards -z). Yaw: to the creature's left. Roll: its right side rises. Degrees."""
    return ry(yaw) @ rx(-lean) @ rz(roll)


def quat(m):
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1) * 2
        q = ((m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, s / 4)
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = (s / 4, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s)
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = ((m[0, 1] + m[1, 0]) / s, s / 4, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s)
    else:
        s = math.sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = ((m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, s / 4, (m[1, 0] - m[0, 1]) / s)
    return unit(np.array(q))


# ----------------------------------------------------------------------------- skeleton

HEAD_SCALE = 1.25                      # the body's head is the Item's head this much larger
HEAD_AT = V(0, 2.86, -0.86)            # the middle of the head, on the body
JAW_HINGE = V(0, -0.12, 0.02)          # in the head's own frame (Item metres)

_SIDE = [  # right side; the left is mirrored
    ("Clavicle", "Chest", (0.30, 2.95, -0.10)),
    ("UpperArm", "Clavicle", (0.98, 2.92, -0.08)),
    ("Forearm", "UpperArm", (1.14, 2.04, 0.04)),
    ("Hand", "Forearm", (1.12, 1.12, -0.20)),
    ("Fingers", "Hand", (1.12, 0.82, -0.24)),
    ("Thigh", "Hips", (0.40, 1.65, 0.15)),
    ("Shin", "Thigh", (0.50, 0.92, -0.15)),
    ("Foot", "Shin", (0.50, 0.18, 0.15)),
    ("Toe", "Foot", (0.50, 0.07, -0.30)),
]
BONES = [  # name, parent, rest position (model space)
    ("Root", None, (0, 0, 0)),
    ("Hips", "Root", (0, 1.70, 0.15)),
    ("Spine", "Hips", (0, 2.05, 0.12)),
    ("Chest", "Spine", (0, 2.50, 0.02)),
    ("Neck", "Chest", (0, 2.95, -0.35)),
    ("Head", "Neck", (0, 2.80, -0.62)),
    ("Jaw", "Head", tuple(HEAD_AT + JAW_HINGE * HEAD_SCALE)),
]
for _s, _sx in (("R", 1), ("L", -1)):
    for _n, _p, (_x, _y, _z) in _SIDE:
        BONES.append((f"{_n}.{_s}", _p if _p in ("Chest", "Hips") else f"{_p}.{_s}", (_x * _sx, _y, _z)))
NAMES = [b[0] for b in BONES]
PARENT = {b[0]: b[1] for b in BONES}
REST = {b[0]: V(*b[2]) for b in BONES}


# ----------------------------------------------------------------------------- mesh kit: every vertex carries its bone weights

class Ring:
    def __init__(self, pts, w, c=None):
        self.pts = [np.asarray(p, float) for p in pts]
        self.w = w
        self.c = np.mean(self.pts, axis=0) if c is None else np.asarray(c, float)

    def v(self, k):
        return (self.pts[k % len(self.pts)], self.w)


class Mesh:
    def __init__(self):
        self.tris = []  # (material, ((pos, weights), (pos, weights), (pos, weights)))
        self.count = 0

    def tri(self, mat, a, b, c, away):
        """One facet, wound so it faces away from the point `away`."""
        n = np.cross(b[0] - a[0], c[0] - a[0])
        if n @ n < 1e-12:
            return
        cen = (a[0] + b[0] + c[0]) / 3
        if n @ (cen - away) < 0:
            b, c = c, b
        if callable(mat):
            mat = mat(cen)
        if mat == FUR:
            mat = fur_pick(self.count)
        self.count += 1
        self.tris.append((mat, (a, b, c)))

    def cap(self, mat, r, away):
        cv = (r.c, r.w)
        for k in range(len(r.pts)):
            self.tri(mat, cv, r.v(k), r.v(k + 1), away)

    def loft(self, mat, rings, cap0=True, cap1=True):
        for r0, r1 in zip(rings, rings[1:]):
            away = (r0.c + r1.c) / 2
            for k in range(len(r0.pts)):
                a, b, c, d = r0.v(k), r0.v(k + 1), r1.v(k + 1), r1.v(k)
                self.tri(mat, a, b, c, away)
                self.tri(mat, a, c, d, away)
        if cap0:
            self.cap(mat, rings[0], rings[1].c)
        if cap1:
            self.cap(mat, rings[-1], rings[-2].c)

    def tube(self, mat, path, n=8, ref=X, cap0=True, cap1=True):
        """Rings square to a path of (point, radius or (ru, rv), weights); ru lies along `ref`."""
        pts = [np.asarray(p[0], float) for p in path]
        rings = []
        for i, (p, r, w) in enumerate(path):
            t = unit(pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)])
            u = unit(ref - (ref @ t) * t)
            v = np.cross(t, u)
            ru, rv = r if isinstance(r, tuple) else (r, r)
            rings.append(ring(pts[i], u, v, ru, rv, n, w))
        self.loft(mat, rings, cap0, cap1)

    def spike(self, mat, base, tip, r, w, n=4, ref=None):
        base, tip = np.asarray(base, float), np.asarray(tip, float)
        t = unit(tip - base)
        ref = ref if ref is not None else (X if abs(t @ X) < 0.8 else Y)
        u = unit(ref - (ref @ t) * t)
        self.loft(mat, [ring(base, u, np.cross(t, u), r, r, n, w), Ring([tip] * n, w, tip)], False, False)

    def prism(self, mat, poly, axis, lo, hi, w):
        """A polygon of (a, b) pushed along an axis: 'z' reads (x, y), 'x' reads (z, y), 'y' reads (x, z)."""
        def at(a, b, d):
            return V(a, b, d) if axis == 'z' else V(d, b, a) if axis == 'x' else V(a, d, b)
        self.loft(mat, [Ring([at(a, b, lo) for a, b in poly], w), Ring([at(a, b, hi) for a, b in poly], w)])

    def box(self, mat, x0, x1, y0, y1, z0, z1, w):
        self.prism(mat, [(x0, y0), (x1, y0), (x1, y1), (x0, y1)], 'z', z0, z1, w)

    def add(self, other, scale=1.0, at=None, flip=False):
        """Takes another mesh's facets in, scaled and moved; flip mirrors them in x (and .R bones become .L)."""
        at = np.zeros(3) if at is None else at
        m = V(-1, 1, 1) if flip else V(1, 1, 1)

        def vert(v):
            p, w = v
            return (p * scale * m + at, {k.replace(".R", ".L"): x for k, x in w.items()} if flip else w)
        for mat, (a, b, c) in other.tris:
            self.tris.append((mat, (vert(a), vert(c), vert(b)) if flip else (vert(a), vert(b), vert(c))))


def ring(c, u, v, ru, rv, n, w, phase=0.5):
    c = np.asarray(c, float)
    return Ring([c + u * ru * math.cos(2 * math.pi * (k + phase) / n) + v * rv * math.sin(2 * math.pi * (k + phase) / n) for k in range(n)], w, c)


def lerp(a, b, t):
    return a + (b - a) * t


# ----------------------------------------------------------------------------- the head (Item metres, origin the middle of the head)

HW, JW = {"Head": 1.0}, {"Jaw": 1.0}
DOME_N = 10
# y, half width, extent in front (-z), extent behind (+z): a low sloping forehead rising to a point at the back
DOME = [
    (-0.30, 0.20, 0.16, 0.22),
    (-0.18, 0.31, 0.26, 0.32),
    (-0.04, 0.37, 0.33, 0.37),
    (0.10, 0.38, 0.35, 0.40),
    (0.22, 0.33, 0.22, 0.42),
    (0.33, 0.22, 0.08, 0.38),
    (0.42, 0.10, -0.04, 0.27),
]


def dome_point(y, theta):
    """The skull's surface at height y and angle theta (0 straight ahead, positive towards +x)."""
    lv = DOME[0][1:]
    for (y0, *a), (y1, *b) in zip(DOME, DOME[1:]):
        if y >= y0:
            t = min(1.0, (y - y0) / (y1 - y0))
            lv = tuple(p + (q - p) * t for p, q in zip(a, b))
    a, f, b = lv
    c = math.cos(theta)
    return V(a * math.sin(theta), y, -f * c if c >= 0 else -b * c)


def build_head():
    m = Mesh()
    m.loft(FUR, [Ring([dome_point(y, 2 * math.pi * (k + 0.5) / DOME_N) for k in range(DOME_N)], HW) for y, *_ in DOME])

    side = Mesh()  # the right half of everything that comes in pairs
    # the face: a plate, black sockets, a heavy brow drawn down to the nose in a scowl, a burning slit of an eye under it
    m.prism("Skin", [(-0.10, -0.20), (0.10, -0.20), (0.24, -0.08), (0.27, 0.08), (0.16, 0.16), (-0.16, 0.16), (-0.27, 0.08), (-0.24, -0.08)], 'z', -0.24, -0.355, HW)
    side.prism("Black", [(0.045, -0.04), (0.235, 0.02), (0.24, 0.11), (0.05, 0.05)], 'z', -0.30, -0.362, HW)
    side.prism("Eye", [(0.075, -0.012), (0.205, 0.04), (0.205, 0.078), (0.075, 0.03)], 'z', -0.33, -0.372, HW)
    brow = [(0.0, 0.025), (0.33, 0.115), (0.385, 0.215), (0.15, 0.235), (0.0, 0.14)]
    side.loft("FurDark", [Ring([V(x, y, -0.20) for x, y in brow], HW), Ring([V(x, y, -0.40) for x, y in brow], HW),
                          Ring([V(x * 0.9, 0.02 + y * 0.72, -0.47) for x, y in brow], HW)])
    # the muzzle (the upper jaw), the nose on it
    m.loft("Skin", [Ring([V(-0.21, -0.17, -0.28), V(0.21, -0.17, -0.28), V(0.19, 0.0, -0.28), V(-0.19, 0.0, -0.28)], HW),
                    Ring([V(-0.165, -0.165, -0.52), V(0.165, -0.165, -0.52), V(0.12, -0.06, -0.50), V(-0.12, -0.06, -0.50)], HW)])
    m.box("SkinDark", -0.08, 0.08, -0.07, 0.025, -0.555, -0.44, HW)
    side.box("Black", 0.02, 0.058, -0.045, -0.01, -0.562, -0.55, HW)
    # the mouth: a dark red hollow between the jaws
    m.box("Mouth", -0.15, 0.15, -0.26, -0.15, -0.47, -0.10, HW)
    # upper teeth: two long fangs in front, small ones between and along the sides
    side.spike("Tooth", (0.105, -0.15, -0.495), (0.115, -0.33, -0.515), 0.036, HW)
    side.spike("Tooth", (0.035, -0.155, -0.505), (0.035, -0.225, -0.512), 0.024, HW)
    for z in (-0.44, -0.37, -0.30):
        side.spike("Tooth", (0.15, -0.155, z), (0.158, -0.225, z), 0.024, HW)
    # the lower jaw: a heavy slab, two tusks standing up outside the upper lip
    jaw = [(0.02, 0.31, -0.31, -0.16), (-0.26, 0.26, -0.35, -0.215), (-0.47, 0.20, -0.335, -0.215)]
    m.loft("Skin", [Ring([V(-hx, y0, z), V(hx, y0, z), V(hx, y1, z), V(-hx, y1, z)], JW) for z, hx, y0, y1 in jaw])
    side.spike("Tooth", (0.195, -0.24, -0.41), (0.25, -0.005, -0.45), 0.048, JW)
    for x, z in ((0.045, -0.455), (0.115, -0.45), (0.21, -0.33), (0.225, -0.25)):
        side.spike("Tooth", (x, -0.225, z), (x, -0.155, z), 0.024, JW)
    # ears, small and pointed
    side.spike("Skin", (0.34, 0.03, 0.04), (0.53, 0.2, 0.12), 0.07, HW)
    m.add(side)
    m.add(side, flip=True)

    # the crest: a row of jagged spikes over the top of the skull
    for z, y, length in ((-0.02, 0.34, 0.15), (0.10, 0.40, 0.22), (0.22, 0.40, 0.2), (0.32, 0.31, 0.15)):
        m.spike("FurDark", (0, y - 0.05, z), (0, y + length, z + length * 0.55), 0.085, HW, ref=X)
    # the mane: rows of spikes round the back, the cheeks and under the skull
    k = 0
    for y, n, length, down, a0 in ((0.12, 12, 0.24, 0.35, 62), (-0.02, 14, 0.25, 0.6, 50), (-0.15, 14, 0.25, 0.8, 58), (-0.27, 10, 0.2, 0.92, 75)):
        for i in range(n):
            th = math.radians(a0 + (360 - 2 * a0) * (i + 0.5) / n)
            p = dome_point(y + 0.015 * math.cos(k * 1.7), th)
            out = unit(V(p[0], 0, p[2]))
            d = unit(out * (1 - down) + V(0, -down, 0))
            root = p * V(0.92, 1, 0.92)
            m.spike(FUR, root, root + d * length * (1 + 0.2 * math.sin(k * 2.399)), 0.075, HW)
            k += 1
    # the beard, on the jaw
    for x, z, length in ((0.08, -0.40, 0.15), (-0.08, -0.40, 0.15), (0.0, -0.44, 0.19), (0.2, -0.22, 0.14), (-0.2, -0.22, 0.14), (0.24, -0.06, 0.13), (-0.24, -0.06, 0.13)):
        m.spike(FUR, (x, -0.31, z), (x * 1.1, -0.33 - length, z - 0.03), 0.06, JW)
    return m


# ----------------------------------------------------------------------------- the body

def bw(**kw):
    """Bone weights of the right side: bw(Chest=1), bw(UpperArm=.5, Forearm=.5)."""
    centre = ("Root", "Hips", "Spine", "Chest", "Neck", "Head", "Jaw")
    return {(k if k in centre else k + ".R"): v for k, v in kw.items()}


# y, z of the middle, half width, half depth, weights
TORSO = [
    (1.42, 0.16, 0.40, 0.34, {"Hips": 1}),
    (1.65, 0.17, 0.60, 0.46, {"Hips": 1}),
    (1.95, 0.13, 0.64, 0.52, {"Hips": 0.5, "Spine": 0.5}),
    (2.30, 0.04, 0.78, 0.58, {"Spine": 0.6, "Chest": 0.4}),
    (2.65, -0.04, 0.96, 0.64, {"Chest": 1}),
    (2.98, -0.06, 1.00, 0.62, {"Chest": 1}),
    (3.25, 0.06, 0.68, 0.52, {"Chest": 1}),
    (3.42, 0.14, 0.30, 0.26, {"Chest": 1}),
]


def torso_at(y):
    for (y0, *a), (y1, *b) in zip(TORSO, TORSO[1:]):
        if y <= y1:
            t = max(0.0, (y - y0) / (y1 - y0))
            return tuple(p + (q - p) * t for p, q in zip(a[:3], b[:3]))
    return TORSO[-1][1:4]


def spine_weights(y):
    return {"Chest": 1} if y > 2.5 else {"Spine": 1} if y > 2.05 else {"Hips": 1}


def build_body():
    m = Mesh()

    # the torso: narrow hips, a barrel chest, a hump behind the head; bare leathery skin down the front
    def torso_mat(c):
        zc, hw, hd = torso_at(c[1])
        return "Skin" if 1.9 < c[1] < 3.0 and c[2] < zc - hd * 0.8 and abs(c[0]) < hw * 0.55 else FUR
    m.loft(torso_mat, [ring(V(0, y, zc), X, Z, hw, hd, 12, w) for y, zc, hw, hd, w in TORSO])
    # the neck: a thick mass from the chest out to the back of the skull
    m.tube(FUR, [(V(0, 3.02, -0.25), (0.50, 0.40), {"Chest": 1}), (V(0, 2.95, -0.55), (0.44, 0.38), {"Chest": 0.4, "Neck": 0.6}), (V(0, 2.88, -0.80), (0.38, 0.34), {"Neck": 0.5, "Head": 0.5})], 8)

    s = Mesh()  # the right side
    sh, el, wr, fi = REST["UpperArm.R"], REST["Forearm.R"], REST["Hand.R"], REST["Fingers.R"]
    # shoulder: a boulder of a deltoid
    c = sh + V(0.02, 0.03, 0.0)
    s.loft(FUR, [Ring([c + V(0, 0.42, 0)] * 7, bw(Clavicle=0.6, UpperArm=0.4)),
                 ring(c + V(0, 0.24, 0), X, Z, 0.36, 0.38, 7, bw(Clavicle=0.6, UpperArm=0.4)),
                 ring(c + V(0.02, -0.02, 0), X, Z, 0.44, 0.44, 7, bw(Clavicle=0.3, UpperArm=0.7)),
                 ring(c + V(0.05, -0.3, 0), X, Z, 0.34, 0.36, 7, bw(UpperArm=1))], False, False)
    # the arm: long, the forearm thicker than the upper arm
    s.tube(FUR, [(sh, 0.30, bw(Clavicle=0.3, UpperArm=0.7)), (lerp(sh, el, 0.35), 0.32, bw(UpperArm=1)), (lerp(sh, el, 0.8), 0.26, bw(UpperArm=1)),
                 (el, 0.25, bw(UpperArm=0.5, Forearm=0.5)),
                 (lerp(el, wr, 0.3), 0.35, bw(Forearm=1)), (lerp(el, wr, 0.7), 0.31, bw(Forearm=1)), (wr, 0.21, bw(Forearm=0.6, Hand=0.4))], 8)
    # the hand hangs palm inwards: its width runs along z. A slab of a palm, four clawed fingers, a thumb
    s.tube("Skin", [(wr + V(0, 0.04, 0), (0.15 * 1.414, 0.22 * 1.414), bw(Hand=1)), (fi, (0.14 * 1.414, 0.28 * 1.414), bw(Hand=1))], 4)
    for dz in (-0.21, -0.07, 0.07, 0.21):
        b = fi + V(0, 0.02, dz)
        s.tube("Skin", [(b, 0.085, bw(Hand=0.5, Fingers=0.5)), (b + V(0, -0.27, 0), 0.07, bw(Fingers=1))], 4)
        s.spike("Claw", b + V(0, -0.24, 0), b + V(-0.05, -0.47, 0), 0.055, bw(Fingers=1))
    tb = lerp(wr, fi, 0.55) + V(-0.03, 0, -0.27)
    s.tube("Skin", [(tb, 0.08, bw(Hand=1)), (tb + V(-0.08, -0.2, -0.1), 0.065, bw(Hand=1))], 4)
    s.spike("Claw", tb + V(-0.07, -0.17, -0.09), tb + V(-0.16, -0.36, -0.15), 0.05, bw(Hand=1))

    # the leg: short and bent, a heavy thigh
    hp, kn, an, to = REST["Thigh.R"], REST["Shin.R"], REST["Foot.R"], REST["Toe.R"]
    s.tube(FUR, [(hp + V(0, 0.1, 0), 0.36, bw(Hips=0.6, Thigh=0.4)), (lerp(hp, kn, 0.35), 0.37, bw(Thigh=1)), (lerp(hp, kn, 0.8), 0.28, bw(Thigh=1)),
                 (kn, 0.25, bw(Thigh=0.5, Shin=0.5)),
                 (lerp(kn, an, 0.3), 0.28, bw(Shin=1)), (lerp(kn, an, 0.8), 0.18, bw(Shin=1)), (an + V(0, -0.02, 0), 0.17, bw(Shin=0.6, Foot=0.4))], 8)
    # the foot: long and flat, three clawed toes
    fx = an[0]
    s.loft("Skin", [ring(V(fx, 0.12, 0.40), X, Y, 0.15, 0.12, 6, bw(Foot=1)), ring(V(fx, 0.17, 0.15), X, Y, 0.21, 0.17, 6, bw(Foot=1)),
                    ring(V(fx, 0.11, -0.30), X, Y, 0.27, 0.11, 6, bw(Foot=0.5, Toe=0.5))])
    for dx in (-0.17, 0.0, 0.17):
        b = V(fx + dx, 0.10, -0.28)
        s.tube("Skin", [(b, (0.085, 0.095), bw(Foot=0.5, Toe=0.5)), (b + V(dx * 0.25, -0.02, -0.26), (0.075, 0.07), bw(Toe=1))], 4)
        s.spike("Claw", b + V(dx * 0.25, 0.0, -0.22), b + V(dx * 0.4, -0.06, -0.47), 0.055, bw(Toe=1))

    # fur: spikes off the shoulder, behind the elbow, down the forearm, on the thigh and the calf
    k = 0
    for az, el_deg, length in ((20, 55, 0.42), (80, 40, 0.46), (140, 50, 0.4), (200, 65, 0.34), (-40, 35, 0.36), (100, 75, 0.38)):
        d = V(math.cos(math.radians(el_deg)) * math.cos(math.radians(az)), math.sin(math.radians(el_deg)), math.cos(math.radians(el_deg)) * math.sin(math.radians(az)))
        base = c + d * V(0.3, 0.26, 0.3)
        s.spike(FUR, base, base + d * length * (1 + 0.15 * math.sin(k * 2.4)), 0.13, bw(Clavicle=0.5, UpperArm=0.5))
        k += 1
    for t, length in ((0.02, 0.36), (0.22, 0.32), (0.45, 0.28), (0.66, 0.22)):
        base = lerp(el, wr, t) + V(0.12, 0, 0.2)
        s.spike(FUR, base, base + V(0.14, -0.1, 0.9) * length, 0.11, bw(Forearm=1))
        base = lerp(el, wr, t + 0.1) + V(0.26, 0, 0.0)
        s.spike(FUR, base, base + V(0.8, -0.55, 0.15) * length, 0.1, bw(Forearm=1))
    for t, length in ((0.3, 0.3), (0.6, 0.26)):
        base = lerp(sh, el, t) + V(0.1, 0, 0.2)
        s.spike(FUR, base, base + V(0.3, -0.5, 0.75) * length, 0.11, bw(UpperArm=1))
    for t, length in ((0.2, 0.3), (0.5, 0.26), (0.8, 0.2)):
        base = lerp(hp, kn, t) + V(0.28, 0, 0.08)
        s.spike(FUR, base, base + V(0.75, -0.65, 0.2) * length, 0.11, bw(Thigh=1))
        base = lerp(hp, kn, t) + V(0.05, 0, 0.3)
        s.spike(FUR, base, base + V(0.1, -0.6, 0.8) * length, 0.11, bw(Thigh=1))
    for t in (0.25, 0.55):
        base = lerp(kn, an, t) + V(0.02, 0, 0.2)
        s.spike(FUR, base, base + V(0.05, -0.6, 0.8) * 0.24, 0.09, bw(Shin=1))
    m.add(s)
    m.add(s, flip=True)

    # the ridge down the back: big dark spikes from the hump to the tail of the spine
    for i, y in enumerate((3.38, 3.2, 2.98, 2.74, 2.5, 2.26, 2.02, 1.8)):
        zc, hw, hd = torso_at(y)
        length = 0.46 - 0.03 * i
        base = V(0, y, zc + hd * (0.8 if i else 0.2))
        m.spike("FurDark", base, base + V(0, 0.55 if i else 0.9, 0.85 if i else 0.4) * length, 0.15, spine_weights(y), ref=X)
    # the mantle: spikes off the top of the back and the hump, between the shoulders
    k = 0
    for y, n, length, a0 in ((3.3, 7, 0.34, 75), (3.1, 9, 0.36, 60)):
        zc, hw, hd = torso_at(y)
        for i in range(n):
            th = math.radians(a0 + (360 - 2 * a0) * (i + 0.5) / n)   # 180 is straight behind
            if abs(math.sin(th)) < 0.12:
                continue  # the ridge stands there
            p = V(hw * math.sin(th) * 0.88, y, zc - hd * math.cos(th) * 0.88)
            d = unit(V(math.sin(th), 0.75, -math.cos(th)))
            m.spike(FUR, p, p + d * length * (1 + 0.2 * math.sin(k * 2.4)), 0.13, {"Chest": 1})
            k += 1
    # a ragged skirt of fur round the hips
    for i in range(12):
        th = 2 * math.pi * (i + 0.5) / 12
        zc, hw, hd = torso_at(1.6)
        p = V(hw * math.sin(th) * 0.9, 1.62, zc - hd * math.cos(th) * 0.9)
        d = unit(V(math.sin(th) * 0.35, -1, -math.cos(th) * 0.35))
        m.spike(FUR, p, p + d * (0.34 + 0.08 * math.sin(i * 2.4)), 0.13, {"Hips": 1})

    m.add(build_head(), HEAD_SCALE, HEAD_AT)
    return m


def build_head_item(jaw_open=10.0):
    """The head alone, as worn: the jaw hangs a little open."""
    m = Mesh()
    r = rx(-jaw_open)
    for mat, tri in build_head().tris:
        m.tris.append((mat, tuple((JAW_HINGE + r @ (p - JAW_HINGE) if "Jaw" in w else p, {}) for p, w in tri)))
    return m


# ----------------------------------------------------------------------------- poses

# What a pose says. Angles are degrees, (lean, yaw, roll) as in euler(); a value given for one side is in the right side's sense
# (x outwards) and mirrored for the left.
POSE = {
    "hips_pos": (0, 0, 0),      # the hips' offset from rest, model space
    "hips": (0, 0, 0), "spine": (0, 0, 0), "chest": (0, 0, 0), "neck": (0, 0, 0), "head": (0, 0, 0),
    "jaw": (0,),                # open
}
for _s in "RL":
    POSE.update({
        f"clav_{_s}": (0, 0, 0),
        f"hand_{_s}": (0, 0, 0),     # the wrist's offset from where it hangs at rest, riding the chest
        f"handm_{_s}": (0, 0, 0),    # or a point in model space (x outwards)...
        f"handw_{_s}": (0,),         # ...by this weight (0 chest, 1 model)
        f"elbow_{_s}": (0.5, -0.2, 1.0),  # where the elbow points (chest space: out, down, back)
        f"wrist_{_s}": (0, 0, 0),
        f"curl_{_s}": (0,),          # the fingers closing
        f"foot_{_s}": (0, 0, 0),     # the ankle's offset from rest, model space
        f"footrot_{_s}": (0, 0, 0),  # the foot in model space: lean is toes down
        f"toe_{_s}": (0,),           # toes bending up
        f"knee_{_s}": (0.15, 0, -1.0),    # where the knee points (model space: out, up, back)
    })
POSE = {k: np.array(v, float) for k, v in POSE.items()}


def pose(base=None, **kw):
    p = {k: v.copy() for k, v in (base or POSE).items()}
    for k, v in kw.items():
        if k in p:
            p[k] = np.array(v if hasattr(v, "__len__") else (v,), float)
        else:  # "hand" sets both sides
            assert k + "_R" in p, k
            p[k + "_R"] = np.array(v if hasattr(v, "__len__") else (v,), float)
            p[k + "_L"] = p[k + "_R"].copy()
    return p


def shift(p, **kw):
    """A copy of a pose with these values added."""
    q = {k: v.copy() for k, v in p.items()}
    for k, v in kw.items():
        for name in ((k,) if k in q else (k + "_R", k + "_L")):
            q[name] = q[name] + np.array(v if hasattr(v, "__len__") else (v,), float)
    return q


def spline(keys, t, loop):
    """Cubic through (time, pose) keys, time 0..1; a loop wraps (its keys must not include time 1)."""
    ts = [k[0] for k in keys]
    n = len(keys)
    if loop:
        i = max(j for j in range(-1, n) if (ts[j] if j >= 0 else ts[-1] - 1) <= t)
        idx = [i - 1, i, i + 1, i + 2]
        tt = [ts[j % n] + (j // n) for j in idx]
        ps = [keys[j % n][1] for j in idx]
    else:
        i = max(j for j in range(n - 1) if ts[j] <= t) if t > ts[0] else 0
        idx = [max(i - 1, 0), i, i + 1, min(i + 2, n - 1)]
        tt = [ts[j] for j in idx]
        ps = [keys[j][1] for j in idx]
    u = (t - tt[1]) / (tt[2] - tt[1])
    out = {}
    for k in ps[1]:
        p0, p1, p2, p3 = (p[k] for p in ps)
        m1 = (p2 - p0) / (tt[2] - tt[0]) * (tt[2] - tt[1]) if tt[2] > tt[0] else 0
        m2 = (p3 - p1) / (tt[3] - tt[1]) * (tt[2] - tt[1]) if tt[3] > tt[1] else 0
        out[k] = (2 * u**3 - 3 * u**2 + 1) * p1 + (u**3 - 2 * u**2 + u) * m1 + (-2 * u**3 + 3 * u**2) * p2 + (u**3 - u**2) * m2
    return out


def ik2(root, target, pole, du, dl):
    """Two bones from root to target, the joint pushed towards pole. du, dl: the bones' rest vectors. Returns their world rotations and the joint."""
    a, b = np.linalg.norm(du), np.linalg.norm(dl)
    d = target - root
    c = min(max(np.linalg.norm(d), abs(a - b) + 1e-3), a + b - 1e-3)
    dr = unit(d)
    p = unit(pole - (pole @ dr) * dr)
    x = (a * a + c * c - b * b) / (2 * c)
    y = math.sqrt(max(a * a - x * x, 0.0))
    mid = root + dr * x + p * y
    end = root + dr * c
    nt, nr = unit(np.cross(p, dr)), unit(np.cross(du, dl))

    def frame(v, n):
        v = unit(v)
        return np.stack([v, n, np.cross(v, n)], axis=1)
    return frame(mid - root, nt) @ frame(du, nr).T, frame(end - mid, nt) @ frame(dl, nr).T, mid


def solve(p):
    """A pose to every bone's local rotation and the hips' position."""
    R, P = {"Root": np.eye(3)}, {"Root": np.zeros(3)}

    def fk(name, local):
        par = PARENT[name]
        P[name] = P[par] + R[par] @ (REST[name] - REST[par])
        R[name] = R[par] @ local

    P["Hips"], R["Hips"] = REST["Hips"] + p["hips_pos"], euler(*p["hips"])
    for name in ("Spine", "Chest", "Neck", "Head"):
        fk(name, euler(*p[name.lower()]))
    fk("Jaw", euler(p["jaw"][0]))
    for s, sx in (("R", 1), ("L", -1)):
        mir, mang = V(sx, 1, 1), V(1, sx, sx)
        b = lambda n: f"{n}.{s}"
        fk(b("Clavicle"), euler(*(p[f"clav_{s}"] * mang)))
        fk(b("UpperArm"), np.eye(3))
        chest_t = P["Chest"] + R["Chest"] @ (REST[b("Hand")] - REST["Chest"] + p[f"hand_{s}"] * mir)
        target = lerp(chest_t, p[f"handm_{s}"] * mir, p[f"handw_{s}"][0])
        ru, rl, mid = ik2(P[b("UpperArm")], target, R["Chest"] @ (p[f"elbow_{s}"] * mir), REST[b("Forearm")] - REST[b("UpperArm")], REST[b("Hand")] - REST[b("Forearm")])
        R[b("UpperArm")], R[b("Forearm")], P[b("Forearm")] = ru, rl, mid
        fk(b("Hand"), euler(*(p[f"wrist_{s}"] * mang)))
        fk(b("Fingers"), rz(-sx * p[f"curl_{s}"][0]))
        fk(b("Thigh"), np.eye(3))
        ru, rl, mid = ik2(P[b("Thigh")], REST[b("Foot")] + p[f"foot_{s}"] * mir, p[f"knee_{s}"] * mir, REST[b("Shin")] - REST[b("Thigh")], REST[b("Foot")] - REST[b("Shin")])
        R[b("Thigh")], R[b("Shin")], P[b("Shin")] = ru, rl, mid
        P[b("Foot")] = mid + rl @ (REST[b("Foot")] - REST[b("Shin")])
        R[b("Foot")] = euler(*(p[f"footrot_{s}"] * mang))
        fk(b("Toe"), euler(-p[f"toe_{s}"][0]))
    return {n: (R[PARENT[n]].T @ R[n] if PARENT[n] else R[n]) for n in NAMES}, P["Hips"]


# ----------------------------------------------------------------------------- clips

def wave(t, phase=0.0):
    return math.sin(2 * math.pi * (t + phase))


def smooth(u):
    u = min(1.0, max(0.0, u))
    return u * u * (3 - 2 * u)


# how it stands: hunched over its own chest, the head slung low and forward, arms hanging ahead of the knees
TOE_LEVER = 0.42   # ankle to the ball of the foot: a planted foot that leans on its toes lifts the ankle by this times the sine


def heel_lift(lean):
    return TOE_LEVER * math.sin(math.radians(max(0.0, lean)))


STAND = pose(hips_pos=(0, -0.14, 0.16), hips=(8, 0, 0), spine=(9, 0, 0), chest=(11, 0, 0), neck=(-8, 0, 0), head=(-6, 0, 0), jaw=7,
             hand=(0.10, 0.06, -0.10), curl=30, foot=(0.10, 0, 0), footrot=(0, -8, 0))


def clip_idle(t):
    br = wave(t * 2)  # two heavy breaths a loop
    p = shift(STAND, hips_pos=(0.035 * wave(t), 0.015 * br, 0), hips=(0, 0, 1.2 * wave(t)), spine=(1.0 * br, 0, 0), chest=(2.2 * br, 0, -1.2 * wave(t)),
              head=(2 * wave(t, 0.2), 12 * wave(t), 0), jaw=3 + 3 * br, hand=(0.02 * br, 0.04 * br, 0))
    return shift(p, curl_R=10 * wave(t, 0.1), curl_L=10 * wave(t, 0.45), hand_R=(0, 0, 0.05 * wave(t, 0.3)), hand_L=(0, 0, 0.05 * wave(t, 0.8)))


WALK_STRIDE = 2.8   # metres a loop
WALK_DUTY = 0.62    # share of the loop a foot is down


def walk_foot(ph):
    """Ankle offset (z, y) and foot lean over one loop of one foot; phase 0 is its heel strike."""
    half = WALK_STRIDE * WALK_DUTY / 2
    if ph < WALK_DUTY:
        u = ph / WALK_DUTY
        roll = smooth((u - 0.72) / 0.28)  # the heel peels off at the end of the stance
        lean = -16 * (1 - smooth(u / 0.14)) + 38 * roll
        return -half + 2 * half * u, heel_lift(lean), lean
    u = (ph - WALK_DUTY) / (1 - WALK_DUTY)
    return half - 2 * half * smooth(u), heel_lift(38) * (1 - u) + 0.34 * math.sin(math.pi * u) ** 1.2, lerp(38, -16, smooth(u * 1.15))


def clip_walk(t):
    p = shift(STAND, hips_pos=(0.11 * wave(t, -0.06), -0.06 - 0.055 * math.cos(4 * math.pi * (t - 0.06)), 0.02), hips=(3, 9 * math.cos(2 * math.pi * t), 4 * wave(t, -0.06)),
              spine=(1, -6 * math.cos(2 * math.pi * t), -2 * wave(t, -0.06)), chest=(2 + 2.5 * math.cos(4 * math.pi * (t - 0.1)), -10 * math.cos(2 * math.pi * t), -3 * wave(t, -0.06)),
              neck=(0, 3 * math.cos(2 * math.pi * t), 0), head=(2.5 * math.cos(4 * math.pi * (t - 0.16)), 4 * math.cos(2 * math.pi * t), 2 * wave(t, -0.06)), jaw=3)
    for s, off in (("R", 0.0), ("L", 0.5)):
        z, y, lean = walk_foot((t + off) % 1)
        swing = math.cos(2 * math.pi * (t + off))  # this side's leg is forward at +1, so its arm is back
        p = shift(p, **{f"foot_{s}": (0, y, z), f"footrot_{s}": (lean, 0, 0), f"toe_{s}": max(0.0, lean) * 0.6,
                        f"hand_{s}": (0.04, 0.07 * abs(swing), 0.50 * swing - 0.08), f"curl_{s}": 12 - 10 * swing})
    return p


RUN_STRIDE = 8.4    # metres a loop: a bound, hands then feet


def clip_run(t):
    """The lope: both fists hit the ground ahead, the body swings through over them, the feet land outside the hands and throw it forward."""
    def k(time, hy, lean, hand_z, hand_y, foot_z, foot_y, foot_lean, curl=85, jaw=16):  # foot_y: how far the toes clear the ground
        total = lean
        return (time, pose(hips_pos=(0, hy - REST["Hips"][1], 0), hips=(total * 0.5, 0, 0), spine=(total * 0.25, 0, 0), chest=(total * 0.25, 0, 0),
                           neck=(-(total - 20) * 0.45, 0, 0), head=(-(total - 20) * 0.45, 0, 0), jaw=jaw,
                           handm=(1.0, hand_y, hand_z), handw=1, elbow=(0.8, 0.1, 1.0), curl=curl, wrist=(0, 0, 0),
                           foot=(0.22, foot_y + heel_lift(foot_lean), foot_z), footrot=(foot_lean, -10, 0), toe=max(0.0, foot_lean) * 0.7, knee=(0.45, 0, -1.0)))
    v = RUN_STRIDE  # the ground passes this many metres per unit of phase
    keys = [
        #  time  hips  lean  hand z   hand y  foot z  foot y  foot lean
        k(0.00, 1.52, 62, -1.95, 0.34, 0.75, 0.30, 55),             # fists strike, legs trailing
        k(0.09, 1.42, 66, -1.95 + v * 0.09, 0.34, 0.30, 0.62, 30),  # weight on the arms, legs tucking through
        k(0.18, 1.45, 60, -1.95 + v * 0.18, 0.36, -0.35, 0.50, 0),  # fists leave behind the shoulders
        k(0.28, 1.42, 50, -0.55, 0.95, -0.80, 0.22, -15, 60),       # arms lifting, feet reaching ahead
        k(0.38, 1.33, 42, -1.00, 1.45, -0.78, 0.0, -8, 40),         # feet strike
        k(0.47, 1.28, 38, -1.60, 1.75, -0.78 + v * 0.09, 0.0, 5, 30, 24),   # crouched over the feet, arms thrown up and forward
        k(0.56, 1.48, 40, -2.10, 1.70, -0.78 + v * 0.18, 0.0, 50, 30, 28), # push off
        k(0.70, 1.72, 50, -2.45, 1.35, 1.05, 0.25, 60, 45, 24),     # flying, stretched out
        k(0.86, 1.68, 58, -2.35, 0.80, 1.00, 0.35, 60, 70),         # reaching for the ground
    ]
    p = spline(keys, t, True)
    q = spline(keys, (t - 0.045) % 1, True)  # the left side lands a moment after the right: a rolling gallop
    for name in p:
        if name.endswith("_L"):
            p[name] = q[name]
    p["hips"] = p["hips"] + V(0, 0, 3 * wave(t, 0.1))
    return p


DEATH_SECONDS = 3.4


def clip_death(t):
    """Rears back roaring, staggers a step, topples over backwards and lies on its back, arms flung out. The head comes down after the body:
    it whips back, bounces, rolls onto a cheek, and sinks with the last breath."""
    def lie(hips_y, **kw):  # flat on its back; the knees bend towards the sky, the left one falls outwards once it has settled
        return pose(STAND, **(dict(hips_pos=(0, hips_y, 1.10), hips=(-94, 0, 0), spine=(-2, 0, 0), chest=(0, 0, 0), handw=1, elbow=(1.0, 0, 0.3), knee=(0.4, 1.0, -0.2)) | kw))
    bent = dict(foot_L=(0.5, 0.2, -0.05), knee_L=(1.0, 0.45, -0.2), footrot_L=(-50, -50, 0), foot_R=(0.3, 0.18, -0.45), footrot_R=(-80, -30, 0))
    arms = dict(handm_R=(2.0, 0.24, 1.85), handm_L=(1.7, 0.24, 3.3))
    keys = [
        (0.00, STAND),
        (0.34, pose(STAND, hips_pos=(0, -0.08, 0.34), hips=(-4, 0, 0), spine=(-8, 0, 0), chest=(-14, 6, 0), neck=(-14, 0, 0), head=(-22, -8, 0), jaw=38,
                    hand=(0.45, 1.0, -0.35), curl=5, foot_L=(0.10, 0, 0.35))),
        (0.84, pose(STAND, hips_pos=(0.06, -0.20, 0.70), hips=(-12, 0, -3), spine=(-8, 0, 0), chest=(-8, -8, 0), neck=(4, 0, 0), head=(10, 6, 0), jaw=26,
                    hand_R=(0.55, 0.95, -0.55), hand_L=(0.35, 0.6, -0.75), curl=8, foot_L=(0.10, 0, 0.35), foot_R=(0.14, 0, 0.95))),
        (1.29, pose(STAND, hips_pos=(0, -0.62, 1.05), hips=(-42, 0, 0), spine=(-2, 0, 0), chest=(4, 0, 0), neck=(10, 0, 0), head=(16, 0, 0), jaw=30,
                    hand_R=(0.5, 0.9, -0.8), hand_L=(0.45, 0.8, -0.85), curl=5, foot_L=(0.12, 0.05, 0.45), foot_R=(0.14, 0, 0.95), footrot=(-20, -10, 0), knee=(0.3, 0.5, -0.8))),
        # the back hits the ground, legs kicked up; the head has not caught up yet, chin on the chest
        (1.68, lie(-1.16, neck=(14, 0, 0), head=(20, 0, 0), jaw=26, handm_R=(1.9, 0.6, 2.0), handm_L=(1.6, 0.7, 2.9), curl=5,
                   foot_L=(0.16, 1.0, -0.05), foot_R=(0.2, 0.8, 0.15), footrot=(-45, -14, 0))),
        # the body bounces; the head whips back, jaws thrown open
        (1.84, lie(-1.06, neck=(-26, 0, 0), head=(-34, 4, 0), jaw=44, handm_R=(2.0, 0.3, 1.9), handm_L=(1.7, 0.35, 3.2), curl=12,
                   foot_L=(0.2, 0.55, -0.4), foot_R=(0.26, 0.4, -0.3), footrot=(-65, -20, 0))),
        # the head rebounds off the ground and starts to turn
        (2.06, lie(-1.14, neck=(-4, 8, 0), head=(-6, 12, 0), jaw=22, curl=16, foot_L=(0.32, 0.3, -0.35), knee_L=(0.7, 0.8, -0.2), footrot_L=(-65, -32, 0),
                   foot_R=(0.3, 0.2, -0.45), footrot_R=(-78, -28, 0), **arms)),
        # it rolls onto its cheek
        (2.36, lie(-1.15, neck=(-30, 20, 0), head=(-22, 32, 6), jaw=16, curl=20, **bent, **arms)),
        # one last breath in: the chest lifts
        (2.72, lie(-1.14, spine=(1, 0, 0), chest=(5, 0, 0), neck=(-34, 22, 0), head=(-22, 34, 7), jaw=12, curl=22, **bent, **arms)),
        # and out: the chest falls, the jaw goes slack, the head sinks
        (3.10, lie(-1.15, spine=(-3, 0, 0), chest=(-2, 0, 0), neck=(-44, 24, 0), head=(-30, 42, 11), jaw=30, curl=26, **bent, **arms)),
        (3.40, lie(-1.15, spine=(-3, 0, 0), chest=(-2, 0, 0), neck=(-45, 24, 0), head=(-31, 43, 12), jaw=33, curl=28, **bent, **arms)),
    ]
    return spline([(time / DEATH_SECONDS, p) for time, p in keys], t, False)


def clip_hit(t):
    """A flinch: the chest and head snap back and round, and come home."""
    return spline([
        (0.00, STAND),
        (0.22, pose(STAND, hips_pos=(0.03, -0.16, 0.26), spine=(3, 4, 0), chest=(0, 13, 2), neck=(-12, -4, 0), head=(-16, -14, 6), jaw=24, hand_R=(0.2, 0.35, -0.25), hand_L=(0.12, 0.12, -0.05), curl=10)),
        (0.58, pose(STAND, hips_pos=(0, -0.15, 0.13), chest=(14, -4, 0), head=(-2, 5, 0), jaw=12)),
        (1.00, STAND),
    ], t, False)


def clip_roar(t):
    """Gathers itself, then throws its chest out and its arms wide and roars at the sky, shaking."""
    burst = dict(hips_pos=(0, -0.06, 0.24), hips=(-5, 0, 0), spine=(-8, 0, 0), chest=(-13, 0, 0), neck=(-14, 0, 0), head=(-20, 0, 0), curl=0, elbow=(1.0, 0.1, 0.6))
    p = spline([
        (0.00, STAND),
        (0.20, pose(STAND, hips_pos=(0, -0.34, 0.12), hips=(12, 0, 0), spine=(12, 0, 0), chest=(18, 0, 0), neck=(2, 0, 0), head=(8, 0, 0), jaw=2, hand=(-0.12, 0.25, -0.3), curl=70)),
        (0.36, pose(STAND, jaw=48, hand=(0.85, 0.62, 0.42), **burst)),
        (0.82, pose(STAND, jaw=44, hand=(0.9, 0.5, 0.5), **burst)),
        (1.00, STAND),
    ], t, False)
    shake = smooth((t - 0.3) / 0.08) * (1 - smooth((t - 0.8) / 0.12))
    return shift(p, head=(1.5 * shake * wave(t * 13), 5 * shake * wave(t * 9), 3 * shake * wave(t * 11, 0.3)), chest=(1.2 * shake * wave(t * 12), 0, 0),
                 hand=(0, 0.04 * shake * wave(t * 12, 0.5), 0), jaw=3 * shake * wave(t * 10))


def clip_trunk_throw(t):
    """The log goes up on the right shoulder, the body winds back over the rear foot, and in the last fifth of the clip it is hurled: the log leaves at the end."""
    carry = dict(elbow_R=(1.0, -0.4, -0.2), curl_R=60)
    return spline([
        (0.00, STAND),
        (0.28, pose(STAND, hips_pos=(0, -0.14, 0.2), hips=(4, -6, 0), spine=(2, -6, 0), chest=(0, -12, 0), head=(-6, 14, 0), jaw=12,
                    hand_R=(-0.15, 2.1, 0.25), hand_L=(0.1, 0.55, -0.55), **carry)),
        (0.74, pose(STAND, hips_pos=(0, -0.2, 0.36), hips=(-2, -12, 0), spine=(-6, -10, 0), chest=(-12, -16, 0), neck=(-4, 12, 0), head=(-4, 20, 0), jaw=26,
                    hand_R=(-0.05, 2.2, 0.8), hand_L=(0.15, 0.9, -0.8), **carry)),
        (0.88, pose(STAND, hips_pos=(0, -0.2, 0.0), hips=(12, 6, 0), spine=(8, 6, 0), chest=(10, 12, 0), head=(-10, -8, 0), jaw=34,
                    hand_R=(-0.25, 2.1, -1.0), hand_L=(0.25, 0.3, 0.2), elbow_R=(1.0, 0.2, 0.4), curl_R=30)),
        (1.00, pose(STAND, hips_pos=(0, -0.3, -0.22), hips=(18, 12, 0), spine=(12, 8, 0), chest=(18, 14, 0), neck=(-14, -8, 0), head=(-16, -10, 0), jaw=38,
                    hand_R=(-0.6, 0.7, -1.3), hand_L=(0.3, 0.25, 0.5), curl_R=5)),
    ], t, False)


# the stomp's last pose and the first of what follows: the right foot driven into the ground, the fists down beside it
STOMPED = pose(STAND, hips_pos=(0.08, -0.56, -0.02), hips=(20, 0, 0), spine=(12, 0, 0), chest=(16, 0, 0), neck=(-16, 0, 0), head=(-20, 0, 0), jaw=40,
               hand=(0.3, 0.05, -0.7), curl=85, foot_R=(0.2, 0, -0.5))


def clip_stomp_windup(t):
    """Rears up on its left leg, fists over its head and the right knee high, and comes down: the foot strikes at the very end (the shockwave starts there)."""
    return spline([
        (0.00, STAND),
        (0.34, pose(STAND, hips_pos=(-0.16, -0.06, 0.16), hips=(2, 0, 3), spine=(-2, 0, 0), chest=(-6, 0, 0), neck=(-10, 0, 0), head=(-12, 0, 0), jaw=28,
                    hand=(0.25, 2.3, -0.5), curl=80, foot_R=(0.16, 0.5, -0.25), footrot_R=(20, -8, 0))),
        (0.78, pose(STAND, hips_pos=(-0.2, 0.0, 0.2), hips=(-2, 0, 4), spine=(-6, 0, 0), chest=(-12, 0, 0), neck=(-12, 0, 0), head=(-14, 0, 0), jaw=40,
                    hand=(0.2, 2.75, -0.15), curl=85, foot_R=(0.2, 1.0, -0.45), footrot_R=(10, -8, 0))),
        (0.92, pose(STAND, hips_pos=(-0.04, -0.3, 0.06), hips=(12, 0, 1), spine=(6, 0, 0), chest=(8, 0, 0), neck=(-14, 0, 0), head=(-16, 0, 0), jaw=40,
                    hand=(0.28, 1.5, -0.9), curl=85, foot_R=(0.2, 0.35, -0.5), footrot_R=(-6, -8, 0))),
        (1.00, STOMPED),
    ], t, False)


def clip_stomp(t):
    """The blow has landed: it sinks into it, holds a moment, and rises."""
    return spline([
        (0.00, STOMPED),
        (0.18, shift(STOMPED, hips_pos=(0, -0.08, 0), chest=(3, 0, 0), head=(4, 0, 0), jaw=4)),
        (0.50, shift(STOMPED, hips_pos=(0, -0.02, 0), head=(-4, 0, 0))),
        (1.00, STAND),
    ], t, False)


LIFT_HANDS = 0.92   # half the gap between its hands under a vehicle


def clip_vehicle_lift(t):
    """Bends to the vehicle, takes it in both hands, straightens and has it over its head at three quarters of the clip (the server's lift), then leans back to throw."""
    both = dict(handw=1, curl=15, elbow=(1.0, 0.2, 0.5))
    return spline([
        (0.00, STAND),
        (0.20, pose(STAND, hips_pos=(0, -0.55, 0.2), hips=(30, 0, 0), spine=(14, 0, 0), chest=(16, 0, 0), neck=(-18, 0, 0), head=(-22, 0, 0), jaw=14, handm=(LIFT_HANDS, 0.75, -1.75), **both)),
        (0.46, pose(STAND, hips_pos=(0, -0.3, 0.14), hips=(12, 0, 0), spine=(4, 0, 0), chest=(4, 0, 0), neck=(-12, 0, 0), head=(-14, 0, 0), jaw=26, handm=(LIFT_HANDS, 2.3, -1.45), **both)),
        (0.75, pose(STAND, hips_pos=(0, -0.06, 0.1), hips=(-3, 0, 0), spine=(-5, 0, 0), chest=(-7, 0, 0), neck=(-12, 0, 0), head=(-16, 0, 0), jaw=34, handm=(LIFT_HANDS, 4.0, -0.2), **both)),
        (1.00, pose(STAND, hips_pos=(0, -0.1, 0.3), hips=(-6, 0, 0), spine=(-8, 0, 0), chest=(-12, 0, 0), neck=(-8, 0, 0), head=(-10, 0, 0), jaw=40, handm=(LIFT_HANDS, 3.95, 0.3), **both)),
    ], t, False)


def clip_launch(t):
    """The throw: both arms come over from behind the head and down in front, the body after them."""
    both = dict(handw=1, curl=10, elbow=(1.0, 0.2, 0.5))
    return spline([
        (0.00, pose(STAND, hips_pos=(0, -0.1, 0.3), hips=(-6, 0, 0), spine=(-8, 0, 0), chest=(-12, 0, 0), neck=(-8, 0, 0), head=(-10, 0, 0), jaw=40, handm=(LIFT_HANDS, 3.9, 0.3), **both)),
        (0.30, pose(STAND, hips_pos=(0, -0.2, -0.1), hips=(12, 0, 0), spine=(8, 0, 0), chest=(12, 0, 0), neck=(-14, 0, 0), head=(-16, 0, 0), jaw=44, handm=(0.85, 2.7, -2.0), **both)),
        (0.60, pose(STAND, hips_pos=(0, -0.42, -0.2), hips=(22, 0, 0), spine=(12, 0, 0), chest=(18, 0, 0), neck=(-18, 0, 0), head=(-20, 0, 0), jaw=40, handm=(0.75, 0.95, -1.7), **both)),
        (1.00, pose(STAND, hips_pos=(0, -0.3, 0.0), hips=(14, 0, 0), spine=(10, 0, 0), chest=(13, 0, 0), neck=(-12, 0, 0), head=(-12, 0, 0), jaw=22, handm=(0.85, 0.95, -1.1), **both)),
    ], t, False)


HOLD_HAND = (0.3, 2.7, -1.35)   # the right wrist while it holds a player: bigfoot.json grab hand_height_m, hand_forward_m


def clip_grab_reach(t):
    """Lunges, the right hand shooting out low for a player, and closes on them at the end."""
    return spline([
        (0.00, STAND),
        (0.30, pose(STAND, hips_pos=(0, -0.2, 0.3), hips=(4, -8, 0), chest=(6, -12, 0), head=(-8, 10, 0), jaw=18, hand_R=(0.2, 0.9, 0.3), hand_L=(0.1, 0.3, -0.4), curl_R=0)),
        (0.72, pose(STAND, hips_pos=(0, -0.5, -0.3), hips=(26, 8, 0), spine=(12, 4, 0), chest=(14, 12, 0), neck=(-18, -6, 0), head=(-22, -8, 0), jaw=30,
                    handm_R=(0.25, 1.35, -2.1), handw_R=1, curl_R=0, hand_L=(0.25, 0.3, 0.5))),
        (1.00, pose(STAND, hips_pos=(0, -0.46, -0.24), hips=(24, 8, 0), spine=(12, 4, 0), chest=(14, 10, 0), neck=(-18, -6, 0), head=(-22, -8, 0), jaw=22,
                    handm_R=(0.25, 1.2, -1.85), handw_R=1, curl_R=85, hand_L=(0.25, 0.3, 0.45))),
    ], t, False)


def clip_hold(t):
    """Holds a player up in its right fist in front of its face and bellows at them; the fist shakes."""
    x, y, z = HOLD_HAND
    return pose(STAND, hips_pos=(0, -0.12, 0.2), hips=(2, 0, 0), spine=(-2, 0, 0), chest=(-4 + 1.5 * wave(t * 2), 5 * wave(t), 0), neck=(-8, 0, 0), head=(-10, -6 + 4 * wave(t, 0.2), 3 * wave(t * 2)),
                jaw=34 + 8 * wave(t * 2), handm_R=(x + 0.05 * wave(t * 2, 0.2), y + 0.1 * wave(t * 2), z + 0.07 * wave(t, 0.4)), handw_R=1, curl_R=85, elbow_R=(1.0, -0.6, 0.2),
                hand_L=(0.5, 0.75 + 0.06 * wave(t * 2, 0.5), -0.25), curl_L=85)


# name, seconds, looped, pose at phase, metres per loop (0: stands still). The attack clips are as long as their act in bigfoot.json;
# the game stretches each over the act's time anyway.
CLIPS = [
    ("Idle", 4.0, True, clip_idle, 0.0),
    ("Walk", 0.84, True, clip_walk, WALK_STRIDE),
    ("Run", 0.62, True, clip_run, RUN_STRIDE),
    ("Death", DEATH_SECONDS, False, clip_death, 0.0),
    ("Hit", 0.5, False, clip_hit, 0.0),
    ("Roar", 1.8, False, clip_roar, 0.0),
    ("TrunkThrow", 1.0, False, clip_trunk_throw, 0.0),
    ("StompWindup", 1.1, False, clip_stomp_windup, 0.0),
    ("Stomp", 0.6, False, clip_stomp, 0.0),
    ("VehicleLift", 1.6, False, clip_vehicle_lift, 0.0),
    ("Launch", 0.5, False, clip_launch, 0.0),
    ("GrabReach", 0.7, False, clip_grab_reach, 0.0),
    ("Hold", 0.8, True, clip_hold, 0.0),
]
FPS = 30


def sample(fn, seconds, looped):
    n = max(1, round(seconds * FPS))
    times = [i / FPS for i in range(n + 1)]
    rot = {name: [] for name in NAMES}
    hips = []
    for i in range(n + 1):
        local, hp = solve(fn(0.0 if looped and i == n else i / n))
        for name in NAMES:
            q = quat(local[name])
            if rot[name] and rot[name][-1] @ q < 0:
                q = -q
            rot[name].append(q)
        hips.append(hp - REST["Root"])
    if looped:
        for name in NAMES:
            rot[name][-1] = rot[name][0]
    return times, rot, hips


# ----------------------------------------------------------------------------- GLB

def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def write_glb(mesh, path, name, skinned):
    buf = bytearray()
    views, accessors = [], []

    def view(data, target=None):
        while len(buf) % 4:
            buf.append(0)
        views.append({"buffer": 0, "byteOffset": len(buf), "byteLength": len(data)} | ({"target": target} if target else {}))
        buf.extend(data)
        return len(views) - 1

    def accessor(data, ctype, count, kind, target=None, **more):
        accessors.append({"bufferView": view(data, target), "componentType": ctype, "count": count, "type": kind} | more)
        return len(accessors) - 1

    def f32(v):
        return struct.unpack("<f", struct.pack("<f", v))[0]

    mat_names = [n for n in MATERIALS if any(t[0] == n for t in mesh.tris)]
    materials = []
    for mname in mat_names:
        r, g, b, rough, emis = MATERIALS[mname]
        lin = [round(srgb_to_linear(v), 4) for v in (r, g, b)]
        mat = {"name": mname, "pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": 0.0, "roughnessFactor": rough}}
        if emis:
            mat["emissiveFactor"] = lin
            mat["extensions"] = {"KHR_materials_emissive_strength": {"emissiveStrength": emis}}
        materials.append(mat)

    index = {n: i for i, n in enumerate(NAMES)}
    prims = []
    for mi, mname in enumerate(mat_names):
        verts, lookup, idx = [], {}, []
        for mat, tri in mesh.tris:
            if mat != mname:
                continue
            fn = unit(np.cross(tri[1][0] - tri[0][0], tri[2][0] - tri[0][0]))
            for p, w in tri:
                ws = sorted(((index[b], x) for b, x in w.items()), key=lambda e: -e[1])[:4] if skinned else []
                key = tuple(np.round(p, 5)) + tuple(np.round(fn, 3)) + tuple(ws)
                if key not in lookup:
                    lookup[key] = len(verts)
                    verts.append((p, fn, ws))
                idx.append(lookup[key])
        assert len(verts) < 65536
        pos = np.array([v[0] for v in verts], "<f4")
        attrs = {"POSITION": accessor(pos.tobytes(), 5126, len(verts), "VEC3", 34962, min=[f32(v) for v in pos.min(axis=0)], max=[f32(v) for v in pos.max(axis=0)]),
                 "NORMAL": accessor(np.array([v[1] for v in verts], "<f4").tobytes(), 5126, len(verts), "VEC3", 34962)}
        if skinned:
            joints, weights = np.zeros((len(verts), 4), "u1"), np.zeros((len(verts), 4), "<f4")
            for i, (_, _, ws) in enumerate(verts):
                total = sum(x for _, x in ws)
                for j, (b, x) in enumerate(ws):
                    joints[i, j], weights[i, j] = b, x / total
            attrs["JOINTS_0"] = accessor(joints.tobytes(), 5121, len(verts), "VEC4", 34962)
            attrs["WEIGHTS_0"] = accessor(weights.tobytes(), 5126, len(verts), "VEC4", 34962)
        prims.append({"attributes": attrs, "indices": accessor(np.array(idx, "<u2").tobytes(), 5123, len(idx), "SCALAR", 34963), "material": mi})

    gltf = {"asset": {"version": "2.0", "generator": "scripts/make_bigfoot_model.py (Drycastle)", "copyright": "CC0 1.0, generated by the project's own script"},
            "scene": 0, "materials": materials, "meshes": [{"name": name, "primitives": prims}]}
    if any("extensions" in m for m in materials):
        gltf["extensionsUsed"] = ["KHR_materials_emissive_strength"]
    if not skinned:
        gltf["nodes"] = [{"name": name, "mesh": 0}]
        gltf["scenes"] = [{"name": name, "nodes": [0]}]
    else:
        # node 0: the model; node 1: the mesh; nodes 2..: the bones, in BONES order
        first = 2
        nodes = [{"name": name, "children": [1, first]}, {"name": name + "_mesh", "mesh": 0, "skin": 0}]
        for bname in NAMES:
            par = PARENT[bname]
            node = {"name": bname, "translation": [f32(v) for v in (REST[bname] - (REST[par] if par else 0))]}
            kids = [first + index[c] for c in NAMES if PARENT[c] == bname]
            if kids:
                node["children"] = kids
            nodes.append(node)
        inverse = b"".join(np.array([[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [*(-REST[b]), 1]], "<f4").tobytes() for b in NAMES)  # column-major
        gltf["skins"] = [{"name": name, "joints": [first + i for i in range(len(NAMES))], "skeleton": first, "inverseBindMatrices": accessor(inverse, 5126, len(NAMES), "MAT4")}]
        gltf["nodes"] = nodes
        gltf["scenes"] = [{"name": name, "nodes": [0]}]
        anims = []
        for cname, seconds, looped, fn, stride in CLIPS:
            times, rot, hips = sample(fn, seconds, looped)
            t_acc = accessor(np.array(times, "<f4").tobytes(), 5126, len(times), "SCALAR", min=[0.0], max=[f32(times[-1])])
            samplers, channels = [], []
            for bname in NAMES:
                samplers.append({"input": t_acc, "output": accessor(np.array(rot[bname], "<f4").tobytes(), 5126, len(times), "VEC4"), "interpolation": "LINEAR"})
                channels.append({"sampler": len(samplers) - 1, "target": {"node": first + index[bname], "path": "rotation"}})
            samplers.append({"input": t_acc, "output": accessor(np.array(hips, "<f4").tobytes(), 5126, len(times), "VEC3"), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": first + index["Hips"], "path": "translation"}})
            anims.append({"name": cname, "samplers": samplers, "channels": channels, "extras": {"loop": looped, "stride_m": stride}})
        gltf["animations"] = anims

    while len(buf) % 4:
        buf.append(0)
    gltf |= {"accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buf)}]}
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(buf)) + struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(buf), b"BIN\0") + bytes(buf)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(out)


def report(label, mesh, path):
    pts = np.array([p for _, tri in mesh.tris for p, _ in tri])
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    print(f"{label}: {len(mesh.tris)} triangles, x {lo[0]:.2f}..{hi[0]:.2f}  y {lo[1]:.2f}..{hi[1]:.2f}  z {lo[2]:.2f}..{hi[2]:.2f}, wrote {path}")


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "assets" / "drycastle-generated-models"
    body = build_body()
    for _, tri in body.tris:
        for _, w in tri:
            assert w and all(b in REST for b in w), w
    write_glb(body, out / "bigfoot.glb", "bigfoot", True)
    report("body", body, out / "bigfoot.glb")
    head = build_head_item()
    write_glb(head, out / "bigfoot_head.glb", "bigfoot_head", False)
    report("head", head, out / "bigfoot_head.glb")
    for cname, seconds, looped, _, stride in CLIPS:
        print(f"clip {cname}: {seconds} s{', looped' if looped else ''}{f', {stride} m a loop ({stride / seconds:.1f} m/s)' if stride else ''}")


if __name__ == "__main__":
    main()
