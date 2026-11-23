#!/usr/bin/env python3
"""Tree colliders against the pack models the player sees (issue #375): one picture and one table, no game run.

A tree's colliders are the code-built tree (ShooterCore TreeColliders.Parts: trunk + stacked crown boxes or pyramids, numbers from
rules.yaml props.trees), while within pack_trees.json near_m the player sees a pack model scaled to the same height only. For every
(collider kind, model) pair the game can make, this draws the model's silhouette with the collider solids over it (side views at
yaw 0 and 90, top view) and measures, over 8 view directions:

  phantom  share of the crown collider's silhouette where no part of the model is drawn (a shot there stops in empty air)
  leak     share of the model's foliage silhouette outside every collider (a shot there passes through leaves)

    pipeline/.venv/bin/python scripts/tools/tree_collider_audit.py [--private DIR] [--out DIR] [--map DIR] [--only NAME,NAME]

Writes <out>/sheet-NN.png (--per-page trees each, worst phantom first) and <out>/table.md. Needs numpy, trimesh, pillow, pyyaml and the private nature pack.
--map DIR (a built map with trees.json) takes the tree numbers and the scales from the map instead of rules.yaml.
--trunk-only SHARE audits the limb solids the game uses for pack trees instead of the code-built ones; --write writes them to
godot/shooter/data/tree_limbs.json (regenerate when pack_trees.json names a new tree model).
The collider arithmetic here mirrors TreeColliders.Parts / ForestParts: change both together.
"""
import argparse
import json
import math
import statistics
import sys
from pathlib import Path

import numpy as np
import trimesh
import yaml
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
PX_H = 420            # raster rows a tree of collider height takes
HALF_W = 1.0          # raster half-width, in tree heights (metrics)
VIEW_HALF_W = 0.6     # drawn half-width, in tree heights
Z_LO, Z_HI = -0.06, 1.08
YAWS = [i * math.pi / 8 for i in range(8)]
BG, WOOD, LEAF, PHANTOM = (244, 244, 240), (120, 90, 60), (96, 160, 84), (230, 60, 50)
CROWN_LINE, TRUNK_LINE = (200, 20, 20), (20, 60, 235)
TRUNK_FILL = (90, 140, 255)


def tree_numbers(rules_path, map_dir):
    """(model block like trees.json's, forest scale, plain scale)."""
    if map_dir:
        t = json.loads((Path(map_dir) / "trees.json").read_text())
        fs = [x["scale"] for x in t["trees"] if x.get("forest")]
        ps = [x["scale"] for x in t["trees"] if not x.get("forest")]
        return t["model"], statistics.median(fs) if fs else 1.8, statistics.median(ps) if ps else 1.6
    r = yaml.safe_load(Path(rules_path).read_text())["props"]["trees"]
    return {k: r[k] for k in ("trunk_height_m", "trunk_width_m", "canopy_m", "forest")}, 1.8, 1.6


def parts(model, forest, cone):
    """TreeColliders.Parts at scale 1, foot at z 0: (role, shape, z0, z1, half, half_top)."""
    if forest:
        f = model["forest"]
        half = f["trunk_width_m"] / 2
        out = [("trunk", "frustum", -0.2, f["trunk_height_m"], half, half * f["trunk_top_ratio"])]
        for lo, h, hi in f["cone_tiers"] if cone else f["box_tiers"]:
            out.append(("crown", "pyramid", lo, hi, h, 0.0) if cone else ("crown", "box", lo, hi, h, h))
        return out
    tw, th, c = model["trunk_width_m"], model["trunk_height_m"], model["canopy_m"]
    out = [("trunk", "box", -0.2, th, tw / 2, tw / 2)]
    if cone:
        out += [("crown", "pyramid", th * 0.5, th + c * 0.7, c * 0.55, 0.0), ("crown", "pyramid", th + c * 0.35, th + c * 1.5, c * 0.38, 0.0)]
    else:
        lo = th - 0.3
        out += [("crown", "box", lo, lo + c, c / 2, c / 2), ("crown", "box", lo + c, lo + c * 1.6, c * 0.3, c * 0.3)]
    return out


def part_points(p):
    _, _, z0, z1, half, top = p[:6]
    x0, y0, x1, y1 = p[6:] if len(p) > 6 else (0.0, 0.0, 0.0, 0.0)   # a limb: its lower and upper ring have centres of their own
    pts = [(x0 + sx * half, y0 + sy * half, z0) for sx in (-1, 1) for sy in (-1, 1)]
    pts += [(x1 + sx * top, y1 + sy * top, z1) for sx in (-1, 1) for sy in (-1, 1)]
    return np.array(pts)


def hull(pts):
    pts = sorted(set(map(tuple, np.round(pts, 6))))
    if len(pts) < 3:
        return pts

    def half(points):
        h = []
        for p in points:
            while len(h) >= 2 and (h[-1][0] - h[-2][0]) * (p[1] - h[-2][1]) - (h[-1][1] - h[-2][1]) * (p[0] - h[-2][0]) <= 0:
                h.pop()
            h.append(p)
        return h[:-1]
    return half(pts) + half(pts[::-1])


class Model:
    """A pack model's triangles (glTF axes, y up, origin at the foot) and which of them are foliage."""

    def __init__(self, glb, materials, tex_dir, leaf_lo):
        scene = trimesh.load(glb)
        tris, leaf = [], []
        for node in scene.graph.nodes_geometry:
            xf, gname = scene.graph[node]
            g = scene.geometry[gname]
            v = trimesh.transformations.transform_points(g.vertices, xf)
            tris.append(v[g.faces])
            leaf.append(self._foliage(g, materials, tex_dir, leaf_lo))
        self.tris = np.concatenate(tris)
        self.leaf = np.concatenate(leaf)
        self.y0, self.y1 = self.tris[..., 1].min(), self.tris[..., 1].max()
        self.height = self.y1 - self.y0     # PackModelMesh.HeightM

    @staticmethod
    def _foliage(g, materials, tex_dir, leaf_lo):
        """Per face: is its texel green (pack_tree.gdshader's test, (g - max(r, b)) / g above leaf_lo)."""
        n = len(g.faces)
        mat = materials.get(getattr(g.visual.material, "name", ""), {})
        tex = mat.get("albedo_texture")
        uv = getattr(g.visual, "uv", None)
        if not tex or uv is None or not (tex_dir.parent / tex).exists():
            return np.ones(n, bool)   # unknown: counts as foliage, so nothing is hidden
        img = Image.open(tex_dir.parent / tex).convert("RGB")
        c = trimesh.visual.color.uv_to_color(uv[g.faces].mean(axis=1) % 1.0, img)[:, :3].astype(float)
        r, gr, b = c[:, 0], c[:, 1], c[:, 2]
        return (gr - np.maximum(r, b)) / np.maximum(gr, 1) > leaf_lo


class Raster:
    """Side view (u across, z up) or top view of a tree whose collider height is h, PX_H rows per h."""

    def __init__(self, h):
        self.px = h / PX_H
        self.w = int(2 * HALF_W * PX_H)
        self.hgt = int((Z_HI - Z_LO) * PX_H)
        self.h = h

    def xy(self, u, z):
        return (u / self.px + self.w / 2, (Z_HI * self.h - z) / self.px)

    def top_xy(self, x, y):
        return (x / self.px + self.w / 2, y / self.px + self.w / 2)

    def mask(self, polys, top=False):
        img = Image.new("1", (self.w, self.w if top else self.hgt), 0)
        d = ImageDraw.Draw(img)
        for poly in polys:
            if len(poly) >= 3:
                d.polygon(poly, fill=1)
        return np.array(img, bool)


def side_polys(r, tris, yaw, scale):
    c, s = math.cos(yaw), math.sin(yaw)
    u = (tris[..., 0] * c + tris[..., 2] * s) * scale
    z = tris[..., 1] * scale
    x, y = r.xy(u, z)
    return np.stack([x, y], axis=-1).reshape(len(tris), -1).tolist()


def part_poly(r, p, yaw):
    c, s = math.cos(yaw), math.sin(yaw)
    return [r.xy(u, z) for u, z in hull([(px * c + py * s, pz) for px, py, pz in part_points(p)])]


def measure(model, h):
    """Of the model scaled to height h: where its foliage starts (m), how wide its foliage is (m), how wide its trunk is near the foot (m). nan when it has none."""
    scale = h / model.height
    leaf_v = model.tris[model.leaf]
    if len(leaf_v):
        base = float(np.percentile(leaf_v[..., 1].min(axis=1), 3)) * scale
        width = 2 * float(np.percentile(np.hypot(leaf_v[..., 0], leaf_v[..., 2]).ravel() * scale, 97))
    else:
        base, width = float("nan"), float("nan")
    # visible trunk width: the wood cut by a level plane at a tenth of the height, its extent across, mean over the views
    w = model.tris[~model.leaf] * scale
    cut, pts = 0.10 * h, []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        p, q = w[:, i], w[:, j]
        cross = (p[:, 1] - cut) * (q[:, 1] - cut) < 0
        f = ((cut - p[cross, 1]) / (q[cross, 1] - p[cross, 1]))[:, None]
        pts.append(p[cross] + f * (q[cross] - p[cross]))
    pts = np.concatenate(pts) if pts else np.empty((0, 3))
    tw = float(np.mean([np.ptp(pts[:, 0] * math.cos(y) + pts[:, 2] * math.sin(y)) for y in YAWS])) if len(pts) else float("nan")
    return base, width, tw


STEP = 0.05        # a level cut of the wood every this share of the tree height
THIN = 0.3         # a limb thinner than this share of the trunk at its foot is a twig: no solid
STRAIGHT = 0.3     # a cut is dropped when it lies this close (in its own half-widths) to the line between its neighbours


def wood_cut(w, z):
    """The wood (triangles, metres, y up) cut by the level plane y = z: the limbs it crosses, each (x, y, half-width), x and y in the model's x and z."""
    d = w[..., 1] - z
    crossing = (d.min(axis=1) < 0) & (d.max(axis=1) > 0)
    w, d = w[crossing], d[crossing]
    if not len(w):
        return []
    segs = []
    for tri, dd in zip(w, d):
        pts = []
        for i, j in ((0, 1), (1, 2), (2, 0)):
            if (dd[i] < 0) != (dd[j] < 0) and dd[i] != dd[j]:
                f = dd[i] / (dd[i] - dd[j])
                pts.append(tri[i] + f * (tri[j] - tri[i]))
        if len(pts) == 2:
            segs.append((pts[0][[0, 2]], pts[1][[0, 2]]))
    # outlines: segments that share an end; then outlines whose boxes touch are one limb
    boxes = []
    for a, b in segs:
        boxes.append([min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1])])
    merged = True
    while merged:
        merged = False
        for i in range(len(boxes)):
            for j in range(i + 1, len(boxes)):
                p, q = boxes[i], boxes[j]
                if p[0] <= q[2] + 1e-3 and q[0] <= p[2] + 1e-3 and p[1] <= q[3] + 1e-3 and q[1] <= p[3] + 1e-3:
                    boxes[i] = [min(p[0], q[0]), min(p[1], q[1]), max(p[2], q[2]), max(p[3], q[3])]
                    del boxes[j]
                    merged = True
                    break
            if merged:
                break
    return [((b[0] + b[2]) / 2, (b[1] + b[3]) / 2, (b[2] - b[0] + b[3] - b[1]) / 4) for b in boxes]


def trunk_only(model, ps, h, share, top_ratio):
    """
    The proposal: no crown solid; the wood itself as solids. The wood is cut level every STEP of the height, from the foot up; a limb is followed until it has
    run inside foliage for `share` of the height between where the foliage starts and the top (so bare wood is solid however high it is); each limb a cut crosses is joined to the limb under it by one solid (a square ring below, a square ring above, each
    with its own centre), so a leaning trunk is followed and a fork gets a solid per arm. Cuts on a straight run are dropped, so a straight trunk is one solid.
    """
    base, _, tw = measure(model, h)
    if math.isnan(tw):
        return []   # no trunk to see (a bush): nothing solid
    if math.isnan(base):
        base = h   # no foliage at all: every limb is bare
    deep = share * (h - base)
    w = model.tris[~model.leaf] * (h / model.height)
    leaves = (model.tris[model.leaf] * (h / model.height)).reshape(-1, 3)
    nodes, level = [], []   # node: [x, y, half, z, parent index or None, metres run inside foliage]
    z = STEP * h
    while z <= h:
        here = []
        for x, y, half in wood_cut(w, z):
            if half < THIN * tw / 2:
                continue
            parent, best = None, float("inf")
            for i in level:
                dist = math.hypot(x - nodes[i][0], y - nodes[i][1])
                if dist < best and dist <= nodes[i][2] + half + STEP * h:
                    parent, best = i, dist
            if parent is None and level:
                continue   # a branch that comes in from the side with nothing under it
            near = leaves[np.abs(leaves[:, 1] - z) < STEP * h / 2] if len(leaves) else leaves
            hidden = len(near) and bool((np.hypot(near[:, 0] - x, near[:, 2] - y) < half + 0.02 * h).any())
            run = (nodes[parent][5] if parent is not None else 0.0) + (STEP * h if hidden else 0.0)
            if run > deep + 1e-6:
                continue
            nodes.append([x, y, half, z, parent, run])
            here.append(len(nodes) - 1)
        if not here:
            break
        level = here
        z += STEP * h
    # drop the cuts on a straight run
    changed = True
    while changed:
        changed = False
        kids = {}
        for i, n in enumerate(nodes):
            if n is not None and n[4] is not None:
                kids.setdefault(n[4], []).append(i)
        for i, n in enumerate(nodes):
            if n is None or n[4] is None or len(kids.get(i, [])) != 1:
                continue
            a, c = nodes[n[4]], nodes[kids[i][0]]
            f = (n[3] - a[3]) / (c[3] - a[3])
            off = math.hypot(n[0] - (a[0] + f * (c[0] - a[0])), n[1] - (a[1] + f * (c[1] - a[1])))
            if off < STRAIGHT * n[2] and abs(n[2] - (a[2] + f * (c[2] - a[2]))) < STRAIGHT * n[2]:
                c[4] = n[4]
                nodes[i] = None
                changed = True
                break
    out = []
    for n in nodes:
        if n is None:
            continue
        if n[4] is None:   # the foot: straight down into the ground
            out.append(("trunk", "limb", -0.2, n[3], n[2], n[2], n[0], n[1], n[0], n[1]))
        else:
            a = nodes[n[4]]
            out.append(("trunk", "limb", a[3], n[3], a[2], n[2], a[0], a[1], n[0], n[1]))
    return out


def audit(model, ps, h, proposal=False):
    """Metrics over YAWS and the masks of the drawn views."""
    scale = h / model.height
    r = Raster(h)
    crown = [p for p in ps if p[0] == "crown"]
    trunk = [p for p in ps if p[0] == "trunk"]
    ph, lk, views = [], [], []
    for yaw in YAWS:
        m_all = r.mask(side_polys(r, model.tris, yaw, scale))
        m_leaf = r.mask(side_polys(r, model.tris[model.leaf], yaw, scale)) if model.leaf.any() else np.zeros_like(m_all)
        c = r.mask([part_poly(r, p, yaw) for p in crown])
        t = r.mask([part_poly(r, p, yaw) for p in trunk])
        if proposal:   # the trunk solid alone: where it stands with nothing drawn, and how much of the wood it misses
            m_wood = r.mask(side_polys(r, model.tris[~model.leaf], yaw, scale)) if (~model.leaf).any() else np.zeros_like(m_all)
            ph.append((t & ~m_all).sum() / max(t.sum(), 1))
            lk.append((m_wood & ~t).sum() / max(m_wood.sum(), 1) if m_wood.any() else 0.0)
        else:
            ph.append((c & ~m_all).sum() / max(c.sum(), 1))
            lk.append((m_leaf & ~(c | t)).sum() / max(m_leaf.sum(), 1) if m_leaf.any() else 0.0)
        if yaw in (YAWS[0], YAWS[4]):
            views.append((m_all, m_leaf, c, t, [part_poly(r, p, yaw) for p in ps], [p[0] for p in ps]))
    # top view
    sx, sz = model.tris[..., 0] * scale, model.tris[..., 2] * scale
    tx, ty = r.top_xy(sx, sz)
    top_all = r.mask(np.stack([tx, ty], axis=-1).reshape(len(model.tris), -1).tolist(), top=True)
    lt = model.tris[model.leaf]
    tx, ty = r.top_xy(lt[..., 0] * scale, lt[..., 2] * scale)
    top_leaf = r.mask(np.stack([tx, ty], axis=-1).reshape(len(lt), -1).tolist(), top=True) if len(lt) else np.zeros_like(top_all)
    top_polys = [[r.top_xy(x, y) for x, y in hull(part_points(p)[:, :2].tolist())] for p in ps]
    top_c = r.mask([q for q, p in zip(top_polys, ps) if p[0] == "crown"], top=True)
    top_t = r.mask([q for q, p in zip(top_polys, ps) if p[0] == "trunk"], top=True)
    views.append((top_all, top_leaf, top_c, top_t, top_polys, [p[0] for p in ps]))

    base, width, tw = measure(model, h)
    return {
        "phantom": float(np.mean(ph)), "leak": float(np.mean(lk)),
        "crown_base_c": min((p[2] for p in crown), default=float("nan")), "crown_base_m": base,
        "crown_w_c": 2 * max((p[4] for p in crown), default=float("nan")), "crown_w_m": width,
        "trunk_w_c": 2 * trunk[0][4] if trunk else float("nan"), "trunk_w_m": tw, "trunk_top": max(p[3] for p in trunk) if trunk else float("nan"), "solids": len(ps), "scale": scale,
    }, views


def draw_view(view, w_crop):
    m_all, m_leaf, c, t, polys, roles = view
    img = np.empty(m_all.shape + (3,), np.uint8)
    img[:] = BG
    img[m_all] = WOOD
    img[m_leaf] = LEAF
    ph = c & ~m_all
    img[ph] = (np.array(BG) * 0.45 + np.array(PHANTOM) * 0.55).astype(np.uint8)
    img[t] = (img[t] * 0.35 + np.array(TRUNK_FILL) * 0.65).astype(np.uint8)   # the trunk collider, over whatever is behind it
    pic = Image.fromarray(img)
    d = ImageDraw.Draw(pic)
    for want in ("crown", "trunk"):   # trunk last, so it is never under a crown line
        for poly, role in zip(polys, roles):
            if role == want and len(poly) >= 2:
                d.line(list(poly) + [poly[0]], fill=CROWN_LINE if role == "crown" else TRUNK_LINE, width=3)
    x0 = (pic.width - w_crop) // 2
    return pic.crop((x0, 0, x0 + w_crop, pic.height))


def verdict(m):
    bad = []
    if m["phantom"] > 0.30:
        bad.append("phantom blocks")
    if m["leak"] > 0.30:
        bad.append("leaks")
    return ", ".join(bad) or "ok"


def combos(cfg):
    """{(forest, cone, model name): [who uses it]} for every pair the game can make."""
    out = {}

    def add(forest, cone, entries, who):
        for e in entries:
            if e.get("weight", 1) > 0:
                out.setdefault((forest, cone, e["name"], e.get("pack", "nature")), []).append(who)
    stands = cfg.get("stands", {})
    for cone in (True, False):
        shape = "cone" if cone else "box"
        add(False, cone, cfg["shapes"].get(shape, []), "standalone")
        for stand in ("pine", "birch", "mixed", "thicket", "swamp"):
            s = stands.get(stand)
            entries = (s.get(shape) or s.get("any") or []) if s else []
            add(True, cone, entries or cfg["shapes"].get(shape, []), stand)
            edge = stands.get("edge")
            if edge and (s is None or s.get("edge", True)):
                add(True, cone, edge.get(shape) or edge.get("any") or [], "edge")
    return {k: sorted(set(v), key=v.index) for k, v in out.items()}


LIMBS = ROOT / "godot/shooter/data/tree_limbs.json"


def write_limbs(cfg, private, share, top_ratio, leaf_lo):
    """tree_limbs.json: per tree model its native height and its limbs in its own metres (ShooterCore TreeLimbsFile). Numbers only, no mesh."""
    entries, missing = [], []
    for pack, name in sorted({(k[3], k[2]) for k in combos(cfg)}):
        pack_dir = private / "synty/glb" / pack
        glb = pack_dir / f"{name}.glb"
        if not glb.exists():
            missing.append(f"{pack}/{name}")
            continue
        mats = json.loads((pack_dir / "materials.json").read_text()).get("materials", {}) if (pack_dir / "materials.json").exists() else {}
        m = Model(glb, mats, pack_dir / "tex", leaf_lo)
        limbs = []
        for _, _, z0, z1, h0, h1, x0, y0, x1, y1 in trunk_only(m, [], m.height, share, top_ratio):
            # u is the model's x, v minus its z; the foot solid is marked by z0 = 0 (the game starts it below the ground)
            limbs.append([round(float(v), 3) for v in (max(z0, 0.0), z1, h0, h1, x0, -y0, x1, -y1)])
        entries.append({"pack": pack, "name": name, "height_m": round(float(m.height), 4), "limbs": limbs})
    if missing:
        sys.exit(f"not written: no model for {', '.join(missing)} (every tree model of pack_trees.json must be in the table)")
    note = ("Where the wood of every pack tree model is (#375): a tree is solid there and its leaves stop nothing. Written by scripts/tools/tree_collider_audit.py --write; "
            "regenerate when pack_trees.json names a new tree model or a model changes. limbs: [z0, z1, half0, half1, u0, v0, u1, v1] in the model's own metres "
            f"(ShooterCore Limb). A limb is followed until it has run inside foliage for {share:g} of the crown's height.")
    body = ",\n".join("  " + json.dumps(e, separators=(", ", ": ")) for e in entries)
    LIMBS.write_text('{\n"version": 1,\n"_note": ' + json.dumps(note) + ',\n"models": [\n' + body + "\n]\n}\n")
    print(f"{LIMBS}: {len(entries)} models, {sum(len(e['limbs']) for e in entries)} limbs")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--private", default=str(ROOT / "assets-private"), help="private assets dir (holds synty/glb/<pack>)")
    ap.add_argument("--out", help="default out/tree-colliders, or out/tree-colliders-trunk-only with --trunk-only")
    ap.add_argument("--map", help="built map dir: tree numbers and median scales from its trees.json")
    ap.add_argument("--trunk-only", type=float, metavar="SHARE", help="audit the proposal instead: no crown solid, the trunk fitted to the model and extended this share (0..1) into the crown")
    ap.add_argument("--write", action="store_true", help="write godot/shooter/data/tree_limbs.json (the limbs of every tree model pack_trees.json names; SHARE of --trunk-only, else 0.3) and stop")
    ap.add_argument("--top", action="store_true", help="also draw the top view")
    ap.add_argument("--only", help="model names, comma separated")
    ap.add_argument("--cols", type=int, default=2)
    ap.add_argument("--per-page", type=int, default=4, help="trees per image")
    a = ap.parse_args()
    prop = a.trunk_only is not None
    a.out = a.out or str(ROOT / ("out/tree-colliders-trunk-only" if prop else "out/tree-colliders"))

    cfg = json.loads((ROOT / "godot/shooter/data/pack_trees.json").read_text())
    numbers, f_scale, p_scale = tree_numbers(ROOT / "pipeline/config/rules.yaml", a.map)
    leaf_lo = cfg.get("colour", {}).get("leaf_lo", 0.01)
    only = set(a.only.split(",")) if a.only else None
    models, rows = {}, []
    if a.write:
        write_limbs(cfg, Path(a.private), a.trunk_only if prop else 0.3, numbers["forest"]["trunk_top_ratio"], leaf_lo)
        return
    for (forest, cone, name, pack), who in combos(cfg).items():
        if only and name not in only:
            continue
        pack_dir = Path(a.private) / "synty/glb" / pack
        glb = pack_dir / f"{name}.glb"
        if not glb.exists():
            print(f"skip {pack}/{name}: no model", file=sys.stderr)
            continue
        if (pack, name) not in models:
            mats = json.loads((pack_dir / "materials.json").read_text()).get("materials", {}) if (pack_dir / "materials.json").exists() else {}
            models[(pack, name)] = Model(glb, mats, pack_dir / "tex", leaf_lo)
        ps = parts(numbers, forest, cone)
        h = max(p[3] for p in ps)   # PackTreeMath.CodeHeight at scale 1
        if prop:
            ps = trunk_only(models[(pack, name)], ps, h, a.trunk_only, numbers["forest"]["trunk_top_ratio"])
        m, views = audit(models[(pack, name)], ps, h, prop)
        kind = f"{'forest' if forest else 'standalone'} {'cone' if cone else 'box'}"
        rows.append((kind, name, who, m, views, f_scale if forest else p_scale))
    if not rows:
        sys.exit("nothing to audit (is the private nature pack there? --private)")
    if prop:   # the trunk solid follows the model alone, so one picture per model: its forest pairing when it has one
        by_name = {}
        for row in sorted(rows, key=lambda r: not r[0].startswith("forest")):
            if row[1] in by_name:
                by_name[row[1]][2].extend(w for w in row[2] if w not in by_name[row[1]][2])
            else:
                by_name[row[1]] = (row[0].split()[0], row[1], list(row[2]), *row[3:])
        rows = list(by_name.values())
    n_views = 3 if a.top else 2
    rows.sort(key=lambda r: -r[3]["phantom"])

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    font = ImageFont.load_default(size=24)
    small = ImageFont.load_default(size=19)
    vw = int(2 * VIEW_HALF_W * PX_H)
    vh = int((Z_HI - Z_LO) * PX_H)
    cell_w, cell_h, head = n_views * vw + 24, vh + 126, 104
    md = ["| collider | model | used by | phantom | leak | crown base c/m (m) | crown width c/m (m) | trunk width c/m (m) | trunk top (m) | solids | verdict |", "|---|---|---|---|---|---|---|---|---|---|---|"]
    for old in out.glob("sheet*.png"):
        old.unlink()
    pages = [rows[i:i + a.per_page] for i in range(0, len(rows), a.per_page)]
    files = []
    for n, page in enumerate(pages):
        cols = min(a.cols, len(page))
        sheet = Image.new("RGB", (max(cols * cell_w, 2 * cell_w), head + math.ceil(len(page) / a.cols) * cell_h), "white")
        d = ImageDraw.Draw(sheet)
        d.text((10, 6), f"Tree colliders vs pack models (#375), page {n + 1}/{len(pages)}, worst phantom first. Views: side yaw 0, side yaw 90{', top' if a.top else ''}.", fill="black", font=font)
        if prop:
            d.text((10, 40), f"PROPOSAL: no crown solid; solids (blue) follow the wood, limb by limb, until a limb has run inside foliage for {a.trunk_only:.0%} of the crown's height. "
                             "phantom = solid with nothing drawn; leak = wood outside the solids.", fill="black", font=small)
        else:
            d.text((10, 40), "Red line = crown collider (shot-only). Blue line + blue fill = trunk collider (solid). Red fill = PHANTOM: collider, nothing drawn. "
                         "Green/brown outside the lines = LEAK: drawn, no collider.", fill="black", font=small)
        d.text((10, 66), f"c/m = collider / model. Metres at the median scale: forest x{f_scale:.2f}, standalone x{p_scale:.2f}. Each tree is drawn scaled to its collider height.", fill="black", font=small)
        for i, (kind, name, who, m, views, s) in enumerate(page):
            x, y = (i % a.cols) * cell_w, head + (i // a.cols) * cell_h
            v = verdict(m)
            d.text((x + 8, y + 6), f"{name}  [{kind}]  {', '.join(who)}", fill="black", font=font)
            d.text((x + 8, y + 40), f"phantom {m['phantom']:.0%}   leak {m['leak']:.0%}", fill="black", font=small)
            d.text((x + 8, y + 62), f"crown base c/m {m['crown_base_c'] * s:.1f}/{m['crown_base_m'] * s:.1f} m   "
                                    f"crown width c/m {m['crown_w_c'] * s:.1f}/{m['crown_w_m'] * s:.1f} m", fill="black", font=small)
            d.text((x + 8, y + 84), f"trunk width c/m {m['trunk_w_c'] * s:.2f}/{m['trunk_w_m'] * s:.2f} m   trunk top {m['trunk_top'] * s:.1f} m   solids {m['solids']}", fill="black", font=small)
            d.text((x + 320, y + 38), v.upper(), fill=(190, 20, 20) if v != "ok" else (20, 120, 20), font=font)
            for k, view in enumerate(views[:n_views]):
                pic = draw_view(view, vw)
                if pic.height > vh:   # top view is square: keep the middle
                    t0 = (pic.height - vh) // 2
                    pic = pic.crop((0, t0, vw, t0 + vh))
                sheet.paste(pic, (x + 8 + k * (vw + 4), y + 118))
            d.rectangle((x, y, x + cell_w - 1, y + cell_h - 1), outline=(170, 170, 170))
            md.append(f"| {kind} | {name} | {', '.join(who)} | {m['phantom']:.0%} | {m['leak']:.0%} | {m['crown_base_c'] * s:.1f} / {m['crown_base_m'] * s:.1f} | "
                      f"{m['crown_w_c'] * s:.1f} / {m['crown_w_m'] * s:.1f} | {m['trunk_w_c'] * s:.2f} / {m['trunk_w_m'] * s:.2f} | {m['trunk_top'] * s:.1f} | {m['solids']} | {v} |")
        files.append(out / f"sheet-{n + 1:02d}.png")
        sheet.save(files[-1])
    (out / "table.md").write_text("\n".join(md) + "\n")
    print("\n".join(md))
    print("\n" + "\n".join(map(str, files)) + f"\n{out / 'table.md'}")


if __name__ == "__main__":
    main()
