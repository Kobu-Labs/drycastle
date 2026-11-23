"""Hand-written triangle meshes for the procedural model scripts (scripts/models/, scripts/military_kit/): standard library only.

A triangle is (material name, (p0, p1, p2), per-vertex normals or None); None means flat shaded. Shapes (box, cyl, lathe, loft, extrude, panel)
return lists of triangles; move / rot / mirror / both transform them; a Node is one glTF node (its origin is its pivot) that owns triangles.
glb.write_glb turns a Node tree into a .glb.
"""
import math


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


def tube(mat, pts, r, n=6):
    """A bent tube through the points."""
    t = []
    for p, q in zip(pts, pts[1:]):
        t += cyl(mat, p, q, r, n)
    return t


def slab(mat, a, b, c, d, t=0.02):
    """A thin plate on the quad a-b-c-d, t thick (a window pane, a door skin)."""
    n = unit(cross(sub(b, a), sub(d, a)))
    q = [a, b, c, d]
    return loft(mat, [[add(p, mul(n, -t / 2)) for p in q], [add(p, mul(n, t / 2)) for p in q]])


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


# ----------------------------------------------------------------------------- node tree

def turn(p, deg):
    """Rotates a vector about Y by deg (Godot's sense: + turns -Z towards -X)."""
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return (p[0] * c + p[2] * s, p[1], -p[0] * s + p[2] * c)


class Node:
    """A node of the model. Geometry is given in model space; yaw (degrees about Y, Godot's sense) turns the node's own frame."""

    def __init__(self, name, origin=(0.0, 0.0, 0.0), yaw=0.0):
        self.name, self.origin, self.yaw, self.tris, self.children = name, origin, yaw, [], []

    def add(self, *parts):
        for p in parts:
            self.tris += p
        return self

    def child(self, name, origin=None, yaw=None):
        n = Node(name, self.origin if origin is None else origin, self.yaw if yaw is None else yaw)
        self.children.append(n)
        return n

    def walk(self):
        yield self
        for c in self.children:
            yield from c.walk()


def report(root, name_width=0, detail=lambda node: ""):
    """Prints the triangle count and bounds of a Node tree (and, with name_width, one line per node); returns (low corner, high corner)."""
    total = 0
    lo, hi = [1e9] * 3, [-1e9] * 3
    for n in root.walk():
        total += len(n.tris)
        for _, ps, _ in n.tris:
            for p in ps:
                for i in range(3):
                    lo[i], hi[i] = min(lo[i], p[i]), max(hi[i], p[i])
        if name_width:
            print(f"{n.name:{name_width}s} pivot=({n.origin[0]:.3f}, {n.origin[1]:.3f}, {n.origin[2]:.3f}){detail(n)} tris={len(n.tris)}")
    print(f"triangles: {total}")
    print("bounds: x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    return lo, hi
