#!/usr/bin/env python3
"""Numpy analysis of the vehicle dumps (scripts/vehicle_model_dump.gd) for docs/research/vehicle-models-facts.md.
    python3 scripts/vehicle_model_analyze.py <dump.json> [glass-node-substring]
Prints per mesh node: welded components (vertices merged at 1 mm) with bbox, tris, open-edge count (0 = closed solid; >0 = open or
zero-thickness skin); for the glass node the panes (components) with centre, outward normal (Godot front face = clockwise, so the
outward normal is minus the winding cross product; in the ray lists 'v' = front face seen from above, '^' = back face), size in the pane plane; and rays through the cabin (hits of the body meshes along x
and y at a few z) to tell a single-skinned shell from a thick/double-skinned one.
"""
import json, sys
from collections import defaultdict
import numpy as np


TOP = 30  # components listed per mesh node (largest boxes first)


def load(path):
    d = json.load(open(path))["nodes"]
    for n in d:
        n["V"] = np.array(n["v"], float).reshape(-1, 3)
        n["I"] = np.array(n["i"], int).reshape(-1, 3)
    return d


def components(V, I):
    key = {}
    vid = np.empty(len(V), int)
    for i, p in enumerate(np.round(V * 1000).astype(int)):
        vid[i] = key.setdefault(tuple(p), len(key))
    parent = list(range(len(key)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for t in I:
        a, b, c = vid[t]
        for x, y in ((a, b), (b, c)):
            ra, rb = find(x), find(y)
            if ra != rb:
                parent[ra] = rb
    comp = defaultdict(list)
    for ti, t in enumerate(I):
        comp[find(vid[t[0]])].append(ti)
    return vid, list(comp.values())


def open_edges(vid, I, tris):
    e = defaultdict(int)
    for ti in tris:
        a, b, c = vid[I[ti]]
        for x, y in ((a, b), (b, c), (c, a)):
            e[(min(x, y), max(x, y))] += 1
    return sum(1 for v in e.values() if v == 1), sum(1 for v in e.values() if v > 2)


def tri_normals(V, I):
    p0, p1, p2 = V[I[:, 0]], V[I[:, 1]], V[I[:, 2]]
    c = np.cross(p1 - p0, p2 - p0)
    area = np.linalg.norm(c, axis=1) / 2
    return -c / np.maximum(np.linalg.norm(c, axis=1)[:, None], 1e-12), area


def report_components(n, min_tris=1):
    V, I = n["V"], n["I"]
    vid, comps = components(V, I)
    rows = []
    for tris in comps:
        pts = V[I[tris].ravel()]
        lo, hi = pts.min(0), pts.max(0)
        oe, nm = open_edges(vid, I, tris)
        rows.append((len(tris), lo, hi, oe, nm))
    rows.sort(key=lambda r: -np.prod(np.maximum(r[2] - r[1], 0.01)))
    print(f"  {n['name']}: {len(V)} verts, {len(I)} tris, {len(comps)} components")
    for t, lo, hi, oe, nm in rows[:TOP]:
        if t >= min_tris:
            print(f"    comp tris {t:5d} x {lo[0]:+.3f}..{hi[0]:+.3f} y {lo[1]:+.3f}..{hi[1]:+.3f} z {lo[2]:+.3f}..{hi[2]:+.3f} open_edges {oe} nonmanifold {nm}")


def report_glass(n):
    V, I = n["V"], n["I"]
    vid, comps = components(V, I)
    N, A = tri_normals(V, I)
    print(f"  glass {n['name']}: {len(I)} tris, {len(comps)} welded components")
    for tris in comps:
        pts = V[I[tris].ravel()]
        lo, hi = pts.min(0), pts.max(0)
        nm = (N[tris] * A[tris][:, None]).sum(0)
        nm /= np.linalg.norm(nm)
        c = (lo + hi) / 2
        # size in the pane plane: extent along the up-ish axis in the plane and along the horizontal one
        up = np.array([0, 1, 0.0]) - nm * nm[1]
        up = up / np.linalg.norm(up) if np.linalg.norm(up) > 1e-6 else np.array([0, 0, 1.0])
        right = np.cross(up, nm)
        w = np.ptp(pts @ right)
        h = np.ptp(pts @ up)
        print(f"    pane tris {len(tris):3d} centre ({c[0]:+.3f} {c[1]:+.3f} {c[2]:+.3f}) outward normal ({nm[0]:+.2f} {nm[1]:+.2f} {nm[2]:+.2f}) size {w:.3f} x {h:.3f} (w x h) "
              f"x {lo[0]:+.3f}..{hi[0]:+.3f} y {lo[1]:+.3f}..{hi[1]:+.3f} z {lo[2]:+.3f}..{hi[2]:+.3f} area {A[tris].sum():.3f}")


def ray_hits(nodes, origin, direction):
    """Moller-Trumbore against every triangle of the given nodes; returns sorted (t, node name, facing) for hits in both directions."""
    o, d = np.array(origin, float), np.array(direction, float)
    hits = []
    for n in nodes:
        V, I = n["V"], n["I"]
        p0, p1, p2 = V[I[:, 0]], V[I[:, 1]], V[I[:, 2]]
        e1, e2 = p1 - p0, p2 - p0
        h = np.cross(d, e2)
        a = (e1 * h).sum(1)
        ok = np.abs(a) > 1e-12
        f = np.where(ok, 1 / np.where(ok, a, 1), 0)
        s = o - p0
        u = f * (s * h).sum(1)
        q = np.cross(s, e1)
        v = f * (q * d).sum(1)
        t = f * (e2 * q).sum(1)
        m = ok & (u >= 0) & (v >= 0) & (u + v <= 1)
        for ti in np.nonzero(m)[0]:
            nrm = -np.cross(e1[ti], e2[ti])
            hits.append((t[ti], n["name"], "away" if (nrm @ d) > 0 else "toward"))
    hits.sort(key=lambda h: h[0])
    return hits


def down_rays(body, lo, hi):
    """Rays straight down from above the roof at a grid of (x, z): the stack of surfaces under each point (floor, seat, dash, roof...)."""
    for z in np.linspace(lo[2] + 0.05 * (hi[2] - lo[2]), hi[2] - 0.05 * (hi[2] - lo[2]), 9):
        for x in (-0.6 * hi[0], -0.3 * hi[0], 0.3 * hi[0], 0.6 * hi[0]):
            hs = ray_hits(body, (x, hi[1] + 0.5, z), (0, -1, 0))
            ys = [f"{hi[1] + 0.5 - h[0]:.2f}{'v' if h[2] == 'toward' else '^'}" for h in hs]
            print(f"    ray down at x {x:+.2f} z {z:+.2f}: " + " ".join(ys[:14]))


def main():
    nodes = load(sys.argv[1])
    glass_key = sys.argv[2] if len(sys.argv) > 2 else "Glass"
    for n in nodes:
        report_components(n, 3 if "Wheel" not in n["name"] else 10**9)
    for n in nodes:
        if glass_key in n["name"]:
            report_glass(n)
    # rays through the whole vehicle: along x at a few heights/z, from the centre line outwards (both signs), body meshes only
    body = [n for n in nodes if "Glass" not in n["name"] and "Wheel" not in n["name"]]
    allv = np.concatenate([n["V"] for n in nodes])
    lo, hi = allv.min(0), allv.max(0)
    print(f"  model bounds min {lo.round(3)} max {hi.round(3)}")
    for z in np.linspace(lo[2] + 0.1 * (hi[2] - lo[2]), hi[2] - 0.1 * (hi[2] - lo[2]), 5):
        for y in (lo[1] + 0.45 * (hi[1] - lo[1]), lo[1] + 0.7 * (hi[1] - lo[1])):
            hs = [h for h in ray_hits(body, (0.0, y, z), (1, 0, 0)) if h[0] > 0]
            print(f"    ray +x from (0, {y:.2f}, {z:.2f}): " + ", ".join(f"{h[0]:.2f}{h[1][-8:]}/{h[2]}" for h in hs[:8]))
    down_rays(body, lo, hi)


if __name__ == "__main__":
    main()
