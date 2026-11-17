#!/usr/bin/env python3
"""The one standardization step of docs/standards/car-design.md part B (issue #325): a Synty pack prefab GLB -> a GLB that meets the vehicle contract,
driven by a committed per-vehicle facts file (scripts/vehicles/<id>.json). Needs numpy (no -I) and the private pack in assets-private/.

    python3 scripts/standardize_vehicle.py scripts/vehicles/car_small.json [--private DIR] [--vehicles-json godot/shooter/data/vehicles.json]

What is generic (this file; no vehicle's numbers in it):
  - reads the prefab (any node tree: parts may be children of the body mesh), strips collision bodies, takes every mesh in the prefab frame
  - frame: turns the model to forward -Z (`facing`), puts the origin on the ground (lowest wheel point) at the footprint centre (`origin.centre`)
  - `hull` holds the body (interior included), glass panes, steering wheel, seat/lamp empties and lamp lenses; wheels are SIBLINGS of `hull`, named
    `wheel_<name>`, centred on the tyre (measured from the wheel mesh, not the pack's pivot), `Position.Y` = radius, a `wheel_<name>_steer` parent on a
    steered one. Any number of wheels: the names are the facts file's (fl/fr/rl/rr, l1..r3, ...)
  - the glass mesh (optional: an open vehicle has none) is cut into its welded pieces: one `glass_N` node per pane (double-sided glass material), each with
    centre, normal, size and a convex outline (the `glass` list of vehicles.json)
  - the steering wheel mesh (optional) is split into `steering_wheel_rim` and `steering_wheel_spokes` (by radius from its axis)
  - two wheels (`wheel_f`, `wheel_r`) or any other count, each its own radius (a bike's differ); `dx` moves a wheel the pack put off its pair's x
  - a steered wheel's parent `wheel_<name>_steer` turns about Y; with `axis` + `pivot` (source frame) it is a TILTED steering axis (a bike's raked fork): the node is
    oriented so its local Y is the axis, the roll stays about X, and the animator turns it about local Y
  - `controls`: the parts that turn with the steering and are not a steering wheel (handlebars, fork): node `steer_bars`, hung under the hull and turning about its own tilted
    axis (a quad's column), or, with `parent_wheel`, under that wheel's steer parent (a bike: fork, bars and front wheel turn together)
  - `parts`: more prefab meshes that join the body in the hull (racks, a swing arm)
  - `nodes`: prefab meshes that keep a node of their own under the hull, under the name given (a harvester's reel that spins about its `pivot`; a glass mesh that is
    not a breakable pane, `glass: true`: drawn in the standard glass material, hidden by the cockpit rig). Nothing is split or renamed
  - seat empties (`seat_*`, at the rider's feet), lamp empties (`light_*`) + emissive lens plates, optional extra empties
  - colour variants: the same model once per skin, only the atlas material name differs (`<id>_<skin>.glb`); materials.json gets the atlases
  - the pack manifest rows, the measured numbers (docs/research/vehicle-facts.d/<id>.json) and, with --vehicles-json, the model-owned keys of the
    vehicle's entry (model, skins, box, drive.wheelbase/track, seats, glass, lights): generated, never hand-copied
What is per vehicle (the facts file): the source node names, which node is the body/glass/steering/each wheel and which wheels steer, the seat feet and
eye heights, the lamp positions and lens sizes, the skins (atlas names), whether the shell is one skin (`two_sided_shell`).
Not done here (later steps of #325): the interior of a vehicle that has none, seat cover, pane kinds, the client animator of a vehicle without a `drive` block (#295).
"""
import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import simple_military_kit as K  # noqa: E402
from simple_military_kit import Mesh, Model, box  # noqa: E402

ROOT = HERE.parent
FACTS_DIR = ROOT / "docs" / "research" / "vehicle-facts.d"
GLASS_MAT = "Transparent blue glass"
HEAD_MAT, TAIL_MAT = "Emissive warm headlight lens", "Emissive red tail lamps"
YAW180 = np.diag([-1.0, 1.0, -1.0, 1.0])


# ----------------------------------------------------------------------------- the prefab

def read_prefab(path):
    """{node name: (Mesh in the prefab frame, world matrix)} of every mesh node, collision bodies (OMI_physics_*) and their subtrees skipped.
    One mesh per node (the primitives are merged: the pack's vehicle parts wear one material each)."""
    doc, blob = K.read_glb(path)
    nodes = doc["nodes"]
    out = {}

    def walk(i, parent):
        nd = nodes[i]
        if any(k.startswith("OMI_physics") for k in nd.get("extensions", {})):
            return
        w = parent @ K._local(nd)
        if "mesh" in nd:
            parts = []
            for prim in doc["meshes"][nd["mesh"]]["primitives"]:
                if "material" not in prim:
                    continue  # a convex collision shape, not a drawn part
                at = prim["attributes"]
                v = K._acc(doc, blob, at["POSITION"]).astype(float)
                n = K._acc(doc, blob, at["NORMAL"]).astype(float) if "NORMAL" in at else None
                uv = K._acc(doc, blob, at["TEXCOORD_0"]).astype(float) if "TEXCOORD_0" in at else None
                f = K._acc(doc, blob, prim["indices"]).astype(int).reshape(-1, 3)
                m = Mesh(v, f, n, uv, doc["materials"][prim["material"]]["name"], nd.get("name", ""))
                parts.append(m)
            if parts:
                local = K.merge(parts) if len(parts) > 1 else parts[0]
                out[nd.get("name", str(i))] = (local, w)
        for c in nd.get("children", []):
            walk(c, w)
    for s in doc["scenes"][0]["nodes"]:
        walk(s, np.eye(4))
    return out


def translate(t):
    m = np.eye(4)
    m[:3, 3] = t
    return m


def bbox(mesh):
    return mesh.v.min(axis=0), mesh.v.max(axis=0)


# ----------------------------------------------------------------------------- glass

def components(mesh):
    """Face index -> welded component (vertices at the same place are one)."""
    key = {}
    vid = np.array([key.setdefault(tuple(np.round(p, 4)), len(key)) for p in mesh.v])
    parent = list(range(len(key)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for f in mesh.f:
        a, b, c = (find(vid[i]) for i in f)
        parent[b] = a
        parent[find(c)] = a
    roots = {}
    return np.array([roots.setdefault(find(vid[f[0]]), len(roots)) for f in mesh.f])


def cross2(a, b):
    return a[0] * b[1] - a[1] * b[0]


def convex_outline(points, normal, max_pts=12, tol=0.004):
    """The corners of the 2D convex hull of the points seen along `normal` (in the plane axes GlassPaneRect uses), as the original 3D points:
    nearly collinear corners dropped, at most max_pts (the least significant corner goes first)."""
    right = np.array([1.0, 0, 0]) if abs(normal[1]) > 0.999 else np.cross([0, 1.0, 0], normal)
    right /= np.linalg.norm(right)
    up = np.cross(normal, right)
    p = np.unique(np.round(points, 5), axis=0)
    uv = np.stack([p @ right, p @ up], axis=1)
    order = np.lexsort((uv[:, 1], uv[:, 0]))

    def half(idx):
        h = []
        for i in idx:
            while len(h) >= 2 and cross2(uv[h[-1]] - uv[h[-2]], uv[i] - uv[h[-2]]) <= 1e-12:
                h.pop()
            h.append(i)
        return h
    lo, hi = half(order), half(order[::-1])
    hull = lo[:-1] + hi[:-1]                         # counter-clockwise in (right, up)

    def drop(h, below):
        while len(h) > 3:
            best, bi = None, -1
            for k in range(len(h)):
                a, b, c = uv[h[k - 1]], uv[h[k]], uv[h[(k + 1) % len(h)]]
                area2 = abs(cross2(b - a, c - a))
                dist = area2 / max(np.linalg.norm(c - a), 1e-9)
                if best is None or dist < best:
                    best, bi = dist, k
            if not below(best, len(h)):
                break
            h.pop(bi)
        return h
    hull = drop(hull, lambda d, n: d < tol or n > max_pts)
    return [p[i] for i in hull]


def pane_id(normal, centre, taken):
    """windscreen / rear by the horizontal direction the normal faces, side_left / side_right by x (left = -x); `_1`, `_2` from the front when there are several."""
    if abs(normal[2]) > abs(normal[0]):               # the horizontal direction it faces (a raked windscreen leans up)
        return "windscreen" if normal[2] < 0 else "rear"
    return "side_left" if centre[0] < 0 else "side_right"


# ----------------------------------------------------------------------------- build

def build(cfg, parts, skin_mat, info=None):
    """One model: returns (Model, info) with everything in the game frame. `info` is filled on the first call (it does not depend on the skin)."""
    yaw = YAW180 if cfg["facing"].lower() == "+z" else np.eye(4)
    body_l, body_w = parts[cfg["body"]]
    wheel_src = {w["name"]: parts[w["node"]] for w in cfg["wheels"]}
    extra_src = [parts[n] for n in cfg.get("parts", [])]
    node_src = [(n, parts[n["node"]]) for n in cfg.get("nodes", [])]
    control_src = {c["name"]: [parts[n] for n in c["nodes"]] for c in cfg.get("controls", [])}
    glass_src = parts[cfg["glass"]["node"]] if cfg.get("glass") else None
    steer_src = parts[cfg["steering"]["node"]] if cfg.get("steering") else None
    dx_of = {w["name"]: w.get("dx", 0.0) for w in cfg["wheels"]}

    # frame: yaw, then ground = lowest wheel point, then the footprint centre
    wm = {n: m.apply(yaw @ w) for n, (m, w) in wheel_src.items()}
    ground = -min(bbox(m)[0][1] for m in wm.values())
    drawn = [parts[cfg["body"]]] + extra_src + [t for ts in control_src.values() for t in ts] + [t for t in (glass_src, steer_src) if t] + [t for _, t in node_src] + list(wheel_src.values())
    pts = np.vstack([m.apply(yaw @ w).v for m, w in drawn])
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    cz = -(lo[2] + hi[2]) / 2 if cfg["origin"]["centre"] == "bounds" else 0.0
    cx = -(lo[0] + hi[0]) / 2
    assert abs(cx) < cfg["origin"].get("x_tolerance", 0.02), f"the model is not symmetric in x (centre {-cx:.3f}): set origin.centre explicitly"
    M = translate([0.0, ground, cz]) @ yaw

    def put(p):
        return (M @ np.array([p[0], p[1], p[2], 1.0]))[:3]

    mdl = Model(cfg["id"])
    G = K.gltf_mat
    atlas = skin_mat + ("_2s" if cfg.get("two_sided_shell") else "")
    mdl.pack_mat(atlas)
    mdl.materials[GLASS_MAT] = G(GLASS_MAT, (0.38, 0.62, 0.70), 0.05, 0.16, alpha=0.19, double=True)
    mdl.mat(HEAD_MAT, (0.75, 0.85, 0.87), 0.25, 0.23, emission=1.0)
    mdl.materials[HEAD_MAT]["emissiveFactor"] = [1.0, 0.85, 0.62]
    mdl.mat(TAIL_MAT, (0.62, 0.025, 0.035), 0.15, 0.3, emission=0.5)

    def final(m, w):
        out = m.apply(M @ w)
        out.mat = atlas if m.mat == cfg["atlas_material"] else m.mat
        return out

    hull = mdl.node("hull", cfg["id"])
    mdl.add(hull, final(body_l, body_w))
    for e in extra_src:
        mdl.add(hull, final(*e))

    for n, src in node_src:
        mdl.node(n["name"], hull, put(n["pivot"]) if n.get("pivot") else (0, 0, 0))
        m = final(*src)
        if n.get("glass"):
            m.mat, m.n, m.uv = GLASS_MAT, None, None
        mdl.add(n["name"], m)

    def frame_of(axis):
        """The orientation (in the game frame) of a node whose local Y is the source-frame `axis`."""
        return K.frame_from_axis(M[:3, :3] @ np.asarray(axis, float))

    # wheels: siblings of the hull, centred on the tyre, `Position.Y` = radius (the ground is the lowest wheel point)
    wheels = {}
    steer_node = {}
    for w in cfg["wheels"]:
        m = final(*wheel_src[w["name"]])
        if dx_of[w["name"]]:
            m.v = m.v + (M[:3, :3] @ np.array([dx_of[w["name"]], 0, 0]))
        wlo, whi = bbox(m)
        c = (wlo + whi) / 2
        if abs(c[1] - (whi[1] - wlo[1]) / 2) > 0.002:   # a wheel the pack lets float or sink (the ground is the lowest wheel point of ALL): it stands on the ground
            m.v = m.v + np.array([0, (whi[1] - wlo[1]) / 2 - c[1], 0])
            wlo, whi = bbox(m)
            c = (wlo + whi) / 2
        wheels[w["name"]] = {"centre": c, "radius": (whi[1] - wlo[1]) / 2, "width": whi[0] - wlo[0], "steer": bool(w.get("steer"))}
        if w.get("steer"):
            sn = f"wheel_{w['name']}_steer"
            if w.get("axis"):                      # a tilted steering axis: the parent sits on the axis (at the pivot) and is oriented along it
                rot = frame_of(w["axis"])
                mdl.node(sn, cfg["id"], put(w["pivot"]), rot)
                mdl.node(f"wheel_{w['name']}", sn, c, rot)
                wheels[w["name"]]["axis"] = M[:3, :3] @ (np.asarray(w["axis"], float) / np.linalg.norm(w["axis"]))
                wheels[w["name"]]["pivot"] = put(w["pivot"])
            else:
                mdl.node(sn, cfg["id"], c)
                mdl.node(f"wheel_{w['name']}", sn, c)
            steer_node[w["name"]] = sn
        else:
            mdl.node(f"wheel_{w['name']}", cfg["id"], c)
        mdl.add(f"wheel_{w['name']}", m)

    # controls (handlebars, fork): `steer_bars`, under the hull turning about its own axis, or under a steered wheel's parent and turning with the wheel
    controls = {}
    for ctl in cfg.get("controls", []):
        wn = ctl.get("parent_wheel")
        if wn:
            wh = next(w for w in cfg["wheels"] if w["name"] == wn)
            assert wh.get("axis"), f"control {ctl['name']}: wheel {wn} has no tilted axis"
            mdl.node(ctl["name"], steer_node[wn], put(wh["pivot"]), frame_of(wh["axis"]))
            axis, pivot = wh["axis"], wh["pivot"]
        else:
            mdl.node(ctl["name"], hull, put(ctl["pivot"]), frame_of(ctl["axis"]))
            axis, pivot = ctl["axis"], ctl["pivot"]
        for t in control_src[ctl["name"]]:
            mdl.add(ctl["name"], final(*t))
        controls[ctl["name"]] = {"axis": M[:3, :3] @ (np.asarray(axis, float) / np.linalg.norm(axis)), "pivot": put(pivot), "under": steer_node[wn] if wn else "hull"}

    # glass: one pane per welded piece of the glass mesh
    found = []
    gl = K.GlassSet(cfg["vehicle"])
    if glass_src:
        g_l, g_w = glass_src
        g = g_l.apply(M @ g_w)
        comp = components(g)
        inside = put(cfg["glass"]["inside"])
    for ci in range(comp.max() + 1 if glass_src else 0):
        sub = g.subset(comp == ci)
        pts = sub.v
        c0 = pts.mean(axis=0)
        u, s, vt = np.linalg.svd(pts - c0)
        nrm = vt[2]
        if np.dot(nrm, c0 - inside) < 0:
            nrm = -nrm
        outline = convex_outline(pts, nrm)
        found.append((pane_id(nrm, c0, None), c0, sub, outline))
    # order: windscreen, rear, then the sides from the front to the back, left (-x) before right
    rank = {"windscreen": 0, "rear": 1}
    found.sort(key=lambda t: (rank.get(t[0], 2), t[0], t[1][2], t[1][0]))
    counted = {}
    kit_node = K.flak.Node("panes")
    for pid, c0, sub, outline in found:
        k = counted.get(pid, 0)
        counted[pid] = k + 1
        total = sum(1 for t in found if t[0] == pid)
        name = pid if total == 1 else f"{pid}_{k + 1}"
        gl.pane(kit_node, name, [], [tuple(float(x) for x in p) for p in outline], inside=tuple(inside))
        n = len(gl.panes) - 1
        mdl.node(f"glass_{n}", hull)
        sub.mat, sub.n, sub.uv = GLASS_MAT, None, None
        mdl.add(f"glass_{n}", sub)
    mdl.glass = gl

    # steering wheel: rim and spokes (+ column), pivot at the wheel's own origin (none on a vehicle steered by handlebars)
    if steer_src:
        s_l, s_w = steer_src
        ax = "xyz".index(cfg["steering"]["axis"])
        plane = [i for i in range(3) if i != ax]
        cen = s_l.v[s_l.f].mean(axis=1)
        radius = np.hypot(cen[:, plane[0]], cen[:, plane[1]])
        rim = radius >= cfg["steering"]["rim_from_radius"]
        pivot = put(s_w[:3, 3])
        for nm, mask in (("steering_wheel_rim", rim), ("steering_wheel_spokes", ~rim)):
            mdl.node(nm, hull, pivot)
            part = s_l.subset(mask)
            part.mat = atlas if part.mat == cfg["atlas_material"] else part.mat
            mdl.add(nm, part.apply(M @ s_w))

    # seats and lamps (empties at the points, lens plates on the lamps)
    seats = {}
    for s in cfg["seats"]:
        p = put(s["feet"])
        seats[s["name"]] = {"role": s["role"], "feet": p, "eye_height": s.get("eye_height"), "exposed": bool(s.get("exposed"))}
        mdl.node(s["name"], hull, p)
    lamps = {}
    for lp in cfg["lamps"]:
        p = put(lp["pos"])
        lamps[lp["name"]] = {"kind": lp["kind"], "pos": p}
        mdl.node(lp["name"], hull, p)
        if lp.get("lens"):
            sign = 1.0 if lp["face"] == "+z" else -1.0
            sign = -sign if yaw is YAW180 else sign      # the face direction in the game frame
            centre = p + np.array([0, 0, sign * 0.006])
            mdl.add(hull, box(centre, (lp["lens"][0], lp["lens"][1], 0.012), HEAD_MAT if lp["kind"] == "head" else TAIL_MAT, lp["name"] + "_lens"))
    for e in cfg.get("empties", []):
        mdl.node(e["name"], hull, put(e["pos"]))
    return mdl, dict(M=M, wheels=wheels, controls=controls, gl=gl, seats=seats, lamps=lamps, body=final(body_l, body_w), ground=ground, centre_z=cz)


def ceiling_above(body, p):
    """The lowest triangle of the body mesh above the point (looking straight up), or None."""
    tri = body.v[body.f]
    a, b, c = tri[:, 0], tri[:, 1], tri[:, 2]
    px, pz = p[0], p[2]
    d = (b[:, 2] - c[:, 2]) * (a[:, 0] - c[:, 0]) + (c[:, 0] - b[:, 0]) * (a[:, 2] - c[:, 2])
    ok = np.abs(d) > 1e-12
    w1 = np.where(ok, ((b[:, 2] - c[:, 2]) * (px - c[:, 0]) + (c[:, 0] - b[:, 0]) * (pz - c[:, 2])) / np.where(ok, d, 1), -1)
    w2 = np.where(ok, ((c[:, 2] - a[:, 2]) * (px - c[:, 0]) + (a[:, 0] - c[:, 0]) * (pz - c[:, 2])) / np.where(ok, d, 1), -1)
    w3 = 1 - w1 - w2
    hit = ok & (w1 >= 0) & (w2 >= 0) & (w3 >= 0)
    y = (w1 * a[:, 1] + w2 * b[:, 1] + w3 * c[:, 1])[hit]
    y = y[y > p[1] + 1e-3]
    return float(y.min()) if len(y) else None


# ----------------------------------------------------------------------------- outputs

def r3(x):
    return [round(float(v), 3) + 0.0 for v in x]


def num(x):
    s = ("%.3f" % x).rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def vec(v):
    return "[" + ", ".join(num(x) for x in v) + "]"


def write_materials(cfg, private, skins):
    """glb/<out_dir>/materials.json: the atlas materials of the skins (the source pack's material, another texture), kept beside this vehicle's others."""
    src = json.loads((private / (Path(cfg["source"]).parent / "materials.json")).read_text())["materials"]
    base = src[cfg["atlas_material"]]
    out_dir = private / cfg["out_dir"]
    path = out_dir / "materials.json"
    doc = json.loads(path.read_text()) if path.exists() else {"materials": {}}
    for s in skins:
        tex = Path("..") / Path(cfg["atlas_dir"]).relative_to("synty/glb") / f"{s['atlas']}.png"
        assert (out_dir / tex).resolve().exists(), f"atlas missing: {out_dir / tex}"
        spec = dict(base, albedo_texture=str(tex))
        doc["materials"][skin_material(s)] = spec
        if cfg.get("two_sided_shell"):
            doc["materials"][skin_material(s) + "_2s"] = dict(spec, cull_mode=2)
    path.write_text(json.dumps(doc, indent=1) + "\n")


def skin_material(skin):
    return f"{skin['atlas']}_mat"


def update_manifest(cfg, private, entries):
    path = private / "manifest.json"
    doc = json.loads(path.read_text())
    ids = {e["id"] for e in entries}
    doc["assets"] = [a for a in doc["assets"] if a.get("id") not in ids] + entries
    n = sum(1 for a in doc["assets"] if a.get("pack") == cfg["pack"])
    doc["packs"] = [p for p in doc["packs"] if p.get("id") != cfg["pack"]] + [{"assets": n, "dir": cfg["out_dir"], "id": cfg["pack"]}]
    path.write_text(json.dumps(doc, indent=1) + "\n")


def entry_fields(cfg, info, facts, has_drive=True):
    """The model-owned keys of the vehicle's entry, as (key path, text). `drive.*` only for a type that has a drive block (every ground vehicle with a driver has one, ADR 0036)."""
    ids = [f"pack:{cfg['pack']}/{cfg['id']}_{s['id']}" for s in cfg["skins"]]
    seats = []
    for k, s in info["seats"].items():
        # an exposed rider (ADR 0012) has no cabin: the stance gives the eye, so no eye_height
        # every seat declares its cover (ADR 0035): an open vehicle's rider is exposed, a closed one's sits behind glass (an armoured seat is set by hand)
        tail = ', "cover": "exposed"' if s["exposed"] else ', "cover": "glass", "eye_height": %s' % num(s["eye_height"])
        seats.append('{ "role": "%s", "offset": %s%s }' % (s["role"], vec(s["feet"]), tail))
    lamps = info["lamps"]
    head = [lamps[k]["pos"] for k in sorted(lamps) if lamps[k]["kind"] == "head"]
    tail = [lamps[k]["pos"] for k in sorted(lamps) if lamps[k]["kind"] == "tail"]
    head.sort(key=lambda p: p[0])
    tail.sort(key=lambda p: p[0])
    out = {
        "model": json.dumps(ids[0]), "skins": json.dumps(ids), "model_scale": "1", "model_yaw_deg": "0", "model_offset": "[0, 0, 0]",
        "half_width": num(facts["suggested"]["half_width"]), "half_length": num(facts["suggested"]["half_length"]), "height": num(facts["suggested"]["height"]),
        "seats": "[\n" + ",\n".join("        " + s for s in seats) + "\n      ]",
    }
    if head:                     # a vehicle without lamps (the harvester) writes no `lights` block: vehicles.json wants 1 to 4 head lamps when it is there
        out["lights"] = '{ "head": [%s], "tail": [%s], "beam_range": %s, "beam_angle": %s }' % (", ".join(vec(p) for p in head), ", ".join(vec(p) for p in tail),
                                                                                              num(cfg.get("beam_range", 40)), num(cfg.get("beam_angle", 26)))
    if has_drive:
        out["drive.wheelbase"], out["drive.track"] = num(facts["wheels"]["wheelbase"]), num(facts["wheels"]["track"])
    if info["gl"].panes:
        out["glass"] = "[\n" + info["gl"].json_lines("        ") + "\n      ]"
    return out


def _skip_string(text, i):
    """i at an opening quote: the index after the closing one."""
    i += 1
    while text[i] != '"':
        i += 2 if text[i] == "\\" else 1
    return i + 1


def _value_end(text, j):
    """The index after the JSON value that starts at j."""
    if text[j] == '"':
        return _skip_string(text, j)
    if text[j] in "{[":
        depth = 0
        while True:
            ch = text[j]
            if ch == '"':
                j = _skip_string(text, j)
                continue
            depth += ch in "{[" and 1 or ch in "}]" and -1 or 0
            j += 1
            if depth == 0:
                return j
    while text[j] not in ",}] \n\t":
        j += 1
    return j


def scan_object(text, lo):
    """text[lo] is '{': the top-level members as {key: (value start, value end)}, and the index of the closing brace."""
    assert text[lo] == "{"
    members, i = {}, lo + 1
    while True:
        while text[i] in " \t\n,":
            i += 1
        if text[i] == "}":
            return members, i
        assert text[i] == '"', text[i - 20:i + 20]
        k = _skip_string(text, i)
        key = text[i + 1:k - 1]
        i = k
        while text[i] in " \t\n:":
            i += 1
        e = _value_end(text, i)
        members[key] = (i, e)
        i = e


def patch_entry(text, vehicle, fields):
    """Replaces (or appends) the given keys in the vehicle's entry of vehicles.json; dotted keys are inside a nested object. Everything else keeps its bytes."""
    at = text.index(f'"id": "{vehicle}"')
    entry = text.rindex("{", 0, at)
    for key, value in fields.items():
        lo = entry
        path = key.split(".")
        for p in path[:-1]:
            lo = scan_object(text, lo)[0][p][0]
        members, close = scan_object(text, lo)
        if path[-1] in members:
            vs, ve = members[path[-1]]
            text = text[:vs] + value + text[ve:]
        else:
            ins = close
            while text[ins - 1] in " \n\t":
                ins -= 1
            text = text[:ins] + f',\n      "{path[-1]}": {value}' + text[ins:]
    return text


# ----------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("facts")
    ap.add_argument("--private", default=str(K.PRIVATE))
    ap.add_argument("--vehicles-json", default=None, help="write the model-owned keys of the vehicle's entry (default: only print the facts)")
    a = ap.parse_args()
    cfg = json.loads(Path(a.facts).read_text())
    private = Path(a.private)
    parts = read_prefab(private / cfg["source"])
    out_dir = private / cfg["out_dir"]
    out_dir.mkdir(parents=True, exist_ok=True)

    entries, facts, info = [], None, None
    for skin in cfg["skins"]:
        mdl, inf = build(cfg, parts, skin_material(skin))
        if info is None:
            info = inf
        name = f"{cfg['id']}_{skin['id']}"
        size = mdl.write(out_dir / f"{name}.glb", f"scripts/standardize_vehicle.py {cfg['id']} (Drycastle; contains Synty geometry, private)")
        pts = mdl.all_points()
        lo, hi = pts.min(axis=0), pts.max(axis=0)
        entries.append({"category": "other", "file": f"{cfg['out_dir']}/{name}.glb", "id": f"pack:{cfg['pack']}/{name}", "name": f"{cfg['name']}, {skin['id']}",
                        "pack": cfg["pack"], "prefab": True, "size_m": [round(float(hi[i] - lo[i]), 2) for i in range(3)], "tags": ["private", cfg["pack"], "vehicles", cfg["id"]]})
        print(f"{name}: {mdl.tris} triangles, {size} bytes")
    write_materials(cfg, private, cfg["skins"])
    update_manifest(cfg, private, entries)

    w = info["wheels"]
    steered = [v for v in w.values() if v["steer"]]
    fixed = [v for v in w.values() if not v["steer"]]
    wheelbase = float(np.mean([v["centre"][2] for v in fixed]) - np.mean([v["centre"][2] for v in steered])) if steered and fixed else 0.0
    track = float(2 * np.mean([abs(v["centre"][0]) for v in w.values()]))
    mdl, _ = build(cfg, parts, skin_material(cfg["skins"][0]))
    pts = mdl.all_points()
    lo, hi = pts.min(axis=0), pts.max(axis=0)
    up = lambda x: math.ceil(x * 100 - 1e-6) / 100
    facts = {
        "model": entries[0]["id"], "out_dir": cfg["out_dir"], "skins": [{"id": s["id"], "model": e["id"], "atlas": s["atlas"]} for s, e in zip(cfg["skins"], entries)],
        "bounds": {"min": r3(lo), "max": r3(hi)},
        "suggested": {"half_width": up(max(-lo[0], hi[0])), "half_length": up(max(-lo[2], hi[2])), "height": up(hi[1])},
        "origin": {"ground_shift_y": round(float(info["ground"]), 4), "centre_shift_z": round(float(info["centre_z"]), 4)},
        "wheels": {"radius": round(float(np.mean([v["radius"] for v in w.values()])), 3), "wheelbase": round(wheelbase, 3), "track": round(track, 3),
                   "tyre_x": round(float(np.mean([abs(v["centre"][0]) for v in w.values()])), 3), "axle_z_mean_abs": round(float(np.mean([abs(v["centre"][2]) for v in w.values()])), 3),
                   "centres": {k: dict(centre=r3(v["centre"]), radius=round(float(v["radius"]), 3), width=round(float(v["width"]), 3), steer=v["steer"],
                                       **({"steer_axis": r3(v["axis"]), "steer_pivot": r3(v["pivot"])} if "axis" in v else {})) for k, v in w.items()}},
        "controls": {k: dict(axis=r3(c["axis"]), pivot=r3(c["pivot"]), under=c["under"]) for k, c in info["controls"].items()},
        "seats": {k: ({"role": s["role"], "feet": r3(s["feet"]), "exposed": True} if s["exposed"]
                      else dict(role=s["role"], feet=r3(s["feet"]), eye_height=s["eye_height"], eye=r3(s["feet"] + [0, s["eye_height"], 0]))) for k, s in info["seats"].items()},
        "lamps": {k: dict(kind=s["kind"], pos=r3(s["pos"])) for k, s in info["lamps"].items()},
        "glass": [{"id": p["id"], "centre": r3(p["centre"]), "normal": r3(p["normal"]), "size": r3(p["size"]), "outline": [r3(q) for q in p["outline"]]} for p in info["gl"].panes],
        "nodes": sorted(n for n in mdl.nodes),
    }
    problems = []
    box_ = facts["suggested"]
    for k, s in facts["seats"].items():
        if s.get("exposed"):
            if abs(s["feet"][0]) > box_["half_width"] or abs(s["feet"][2]) > box_["half_length"] or s["feet"][1] > box_["height"]:
                problems.append(f"{k}: seat outside the hit box")
            continue
        ceil = ceiling_above(info["body"], np.array(s["eye"]))
        s["ceiling_above_eye"] = None if ceil is None else round(ceil, 3)
        if ceil is not None and ceil - s["eye"][1] < 0.03:
            problems.append(f"{k}: eye y {s['eye'][1]} is under the ceiling at {ceil:.3f}")
        if abs(s["eye"][0]) > box_["half_width"] or abs(s["eye"][2]) > box_["half_length"] or s["eye"][1] > box_["height"]:
            problems.append(f"{k}: eye outside the hit box")
    if problems:
        print("PROBLEMS:\n  " + "\n  ".join(problems))
        sys.exit(1)
    FACTS_DIR.mkdir(parents=True, exist_ok=True)
    fp = FACTS_DIR / f"{cfg['id']}.json"
    fp.write_text(json.dumps(facts, indent=1) + "\n")
    info["gl"].report()
    print(f"box half_width {box_['half_width']} half_length {box_['half_length']} height {box_['height']}; wheel radius {facts['wheels']['radius']}, wheelbase {facts['wheels']['wheelbase']}, track {facts['wheels']['track']}")
    for k, s in facts["seats"].items():
        print(f"{k}: feet {s['feet']}" + (" exposed" if s.get("exposed") else f" eye {s['eye']} ceiling {s['ceiling_above_eye']}"))
    for k, v in w.items():
        print(f"wheel_{k}: centre {r3(v['centre'])} radius {v['radius']:.3f}" + (f" steer axis {r3(v['axis'])} pivot {r3(v['pivot'])}" if "axis" in v else ""))
    for k, c in info["controls"].items():
        print(f"{k}: under {c['under']} axis {r3(c['axis'])} pivot {r3(c['pivot'])}")
    print("facts ->", fp)
    if a.vehicles_json:
        path = Path(a.vehicles_json)
        text = path.read_text()
        at = text.index(f'"id": "{cfg["vehicle"]}"')
        has_drive = '"drive"' in scan_object(text, text.rindex("{", 0, at))[0]
        path.write_text(patch_entry(text, cfg["vehicle"], entry_fields(cfg, info, facts, has_drive)))
        print("vehicles.json entry", cfg["vehicle"], "updated:", path)


if __name__ == "__main__":
    main()
