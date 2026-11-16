"""Low-poly prop meshes: fence boxes and blocky trees (counter-clockwise seen from outside).

Fence sections with a type are drawn by ``fencestyles`` (textured panels, columns, caps); the plain
box here is the look of a section without one, and the collision shape of every section.
"""

import math

import numpy as np

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meshes import Mesh


def box_tris(
    p0, p1, width: float, z0: float, z1: float, bottom: bool = True
) -> list[list[tuple[float, float, float]]]:
    """Box along the axis p0 -> p1 (plan view), ``width`` across, from z0 to z1.

    Only the corners of the plan rectangle are used, so slanted ends are not possible; 10 or 12
    triangles.
    """
    p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
    d = p1 - p0
    d /= max(float(np.linalg.norm(d)), 1e-12)
    v = np.array([-d[1], d[0]]) * width / 2
    ring = [p0 - v, p1 - v, p1 + v, p0 + v]
    tris = []
    for k in range(4):
        a, b = ring[k], ring[(k + 1) % 4]
        tris.append([(*a, z0), (*b, z0), (*b, z1)])
        tris.append([(*a, z0), (*b, z1), (*a, z1)])
    top = [(*p, z1) for p in ring]
    tris += [[top[0], top[1], top[2]], [top[0], top[2], top[3]]]
    if bottom:
        low = [(*p, z0) for p in ring]
        tris += [[low[0], low[2], low[1]], [low[0], low[3], low[2]]]
    return tris


def pyramid_tris(centre, half: float, z0: float, z1: float, angle: float):
    """Four-sided pyramid with a square base (base included, 6 triangles)."""
    c, s = math.cos(angle), math.sin(angle)
    ring = [
        (centre[0] + c * dx - s * dy, centre[1] + s * dx + c * dy)
        for dx, dy in ((-half, -half), (half, -half), (half, half), (-half, half))
    ]
    apex = (centre[0], centre[1], z1)
    tris = [[(*ring[k], z0), (*ring[(k + 1) % 4], z0), apex] for k in range(4)]
    base = [(*p, z0) for p in ring]
    tris += [[base[0], base[2], base[1]], [base[0], base[3], base[2]]]
    return tris


def _mesh(tris: list, colours: list[list[int]]) -> Mesh:
    if not tris:
        return Mesh.empty()
    return Mesh(np.array(tris, float), np.array(colours, np.uint8))


def fence_mesh(
    sections: list[dict], cfg: dict, bottom: bool = False, untyped_only: bool = False
) -> Mesh:
    """One thin box per fence section (visual: no bottom face; collision: closed).

    ``untyped_only`` leaves out the sections that have a fence type (``fencestyles`` draws those).
    """
    tris, cols = [], []
    for s in sections:
        if untyped_only and s.get("type", cfg.get("type")) is not None:
            continue
        # custom (edited) sections carry their own style; the rest use the rules
        height = float(s.get("height_m", cfg["height_m"]))
        thick = float(s.get("thickness_m", cfg["thickness_m"]))
        colour = hex_to_rgb(s.get("colour", cfg["colour"]))
        z0 = min(s["za"], s["zb"]) - 0.1
        z1 = max(s["za"], s["zb"]) + height
        t = box_tris(s["a"], s["b"], thick, z0, z1, bottom)
        tris += t
        cols += [colour] * len(t)
    return _mesh(tris, cols)


def frustum_tris(centre, half0: float, half1: float, z0: float, z1: float, angle: float):
    """Four-sided taper with square rings: ``half0`` at z0, ``half1`` at z1, no bottom (10 triangles, like ``box_tris``)."""
    c, s = math.cos(angle), math.sin(angle)

    def ring(half: float, z: float):
        # the corner order of box_tris along the yaw axis: (-u,-v), (+u,-v), (+u,+v), (-u,+v)
        return [
            (centre[0] + c * dx - s * dy, centre[1] + s * dx + c * dy, z)
            for dx, dy in ((-half, -half), (half, -half), (half, half), (-half, half))
        ]

    low, top = ring(half0, z0), ring(half1, z1)
    tris = []
    for k in range(4):
        a, b = low[k], low[(k + 1) % 4]
        c1, d1 = top[(k + 1) % 4], top[k]
        tris.append([a, b, c1])
        tris.append([a, c1, d1])
    tris += [[top[0], top[1], top[2]], [top[0], top[2], top[3]]]
    return tris


def is_forest(tree: dict) -> bool:
    """A tree planted inside the detected canopy. Trees of older props.json have no flag: their ``src`` says it."""
    return bool(tree.get("forest", tree.get("src") == "ortho-canopy"))


def forest_tree_mesh(tree: dict, cfg: dict) -> Mesh:
    """Forest tree: a tapered trunk with a clear stem, then stacked pyramids (conifer) or boxes (broadleaf).

    Numbers: ``cfg["forest"]`` (rules.yaml props.trees.forest), metres at scale 1 times the tree's scale.
    """
    f = cfg["forest"]
    s = float(tree["scale"])
    x, y, z = tree["x"], tree["y"], tree["z"]
    rot = float(tree["rot"])
    ux, uy = math.cos(rot), math.sin(rot)
    trunk_c, canopy_c = hex_to_rgb(cfg["trunk_colour"]), hex_to_rgb(tree["colour"])
    half0 = float(f["trunk_width_m"]) * s / 2
    t = frustum_tris((x, y), half0, half0 * float(f["trunk_top_ratio"]), z - 0.2, z + float(f["trunk_height_m"]) * s, rot)
    tris, cols = t, [trunk_c] * len(t)
    if tree["shape"] == "cone":
        for base, half, top in f["cone_tiers"]:
            t = pyramid_tris((x, y), float(half) * s, z + float(base) * s, z + float(top) * s, rot)
            tris += t
            cols += [canopy_c] * len(t)
    else:
        for k, (lo, half, hi) in enumerate(f["box_tiers"]):
            h = float(half) * s
            t = box_tris((x - ux * h, y - uy * h), (x + ux * h, y + uy * h), h * 2, z + float(lo) * s, z + float(hi) * s, bottom=k == 0)
            tris += t
            cols += [canopy_c] * len(t)
    return _mesh(tris, cols)


def tree_mesh(tree: dict, cfg: dict) -> Mesh:
    """Trunk box plus a canopy: two stacked boxes (broadleaf) or two stacked pyramids (conifer).

    Forest trees (``is_forest``) get ``forest_tree_mesh``, when the rules have the forest model.
    """
    if is_forest(tree) and "forest" in cfg:
        return forest_tree_mesh(tree, cfg)
    s = float(tree["scale"])
    trunk_w, trunk_h, canopy = (
        float(cfg["trunk_width_m"]) * s,
        float(cfg["trunk_height_m"]) * s,
        float(cfg["canopy_m"]) * s,
    )
    x, y, z = tree["x"], tree["y"], tree["z"]
    rot = float(tree["rot"])
    ux, uy = math.cos(rot), math.sin(rot)
    trunk_c, canopy_c = hex_to_rgb(cfg["trunk_colour"]), hex_to_rgb(tree["colour"])

    def along(half: float):
        return (x - ux * half, y - uy * half), (x + ux * half, y + uy * half)

    tris = box_tris(*along(trunk_w / 2), trunk_w, z - 0.2, z + trunk_h, bottom=False)
    cols = [trunk_c] * len(tris)
    if tree["shape"] == "cone":
        for base, half, top in (
            (trunk_h * 0.5, canopy * 0.55, trunk_h + canopy * 0.7),
            (trunk_h + canopy * 0.35, canopy * 0.38, trunk_h + canopy * 1.5),
        ):
            t = pyramid_tris((x, y), half, z + base, z + top, rot)
            tris += t
            cols += [canopy_c] * len(t)
    else:
        for half, lo, hi in (
            (canopy / 2, trunk_h - 0.3 * s, trunk_h - 0.3 * s + canopy),
            (canopy * 0.3, trunk_h - 0.3 * s + canopy, trunk_h - 0.3 * s + canopy * 1.6),
        ):
            t = box_tris(*along(half), half * 2, z + lo, z + hi, bottom=half > canopy * 0.4)
            tris += t
            cols += [canopy_c] * len(t)
    return _mesh(tris, cols)


def trees_mesh(trees: list[dict], cfg: dict) -> Mesh:
    """All trees; every triangle remembers its tree's base (x, y), so the game can hide single trees on the GPU."""
    meshes = []
    for t in trees:
        m = tree_mesh(t, cfg)
        m.bases = np.tile([float(t["x"]), float(t["y"])], (m.face_count, 1))
        meshes.append(m)
    return Mesh.concat(meshes)


# --- ground objects (logs, stumps, boulders, root plates; rules.yaml props.ground_objects) -------------------------------


def _outward(tris: list, centre) -> list:
    """Wind every triangle counter-clockwise seen from outside, for a solid that is star-shaped around ``centre``."""
    c = np.asarray(centre, float)
    out = []
    for a, b, d in tris:
        n = np.cross(np.subtract(b, a), np.subtract(d, a))
        mid = (np.asarray(a) + np.asarray(b) + np.asarray(d)) / 3
        out.append([a, b, d] if float(n @ (mid - c)) >= 0 else [a, d, b])
    return out


def _band(lo: list, hi: list) -> list:
    """Side triangles between two rings of the same length."""
    n = len(lo)
    tris = []
    for k in range(n):
        j = (k + 1) % n
        tris += [[lo[k], lo[j], hi[j]], [lo[k], hi[j], hi[k]]]
    return tris


def _fan(ring: list, centre) -> list:
    return [[ring[k], ring[(k + 1) % len(ring)], tuple(centre)] for k in range(len(ring))]


def _ring_yz(origin, u, v, r: float, zc: float, n: int, phase: float, along: float = 0.0, jitter=None) -> list:
    """A ring of ``n`` points in the plane across the axis ``u`` (v horizontal, z up) around height ``zc``, ``along`` metres along u."""
    pts = []
    for k in range(n):
        a = phase + 2 * math.pi * k / n
        f = 1.0 if jitter is None else jitter[k]
        pts.append((origin[0] + u[0] * along + v[0] * r * f * math.cos(a), origin[1] + u[1] * along + v[1] * r * f * math.cos(a), zc + r * f * math.sin(a)))
    return pts


def log_mesh(o: dict, model: dict, cfg: dict, rng) -> tuple[list, list]:
    """A fallen trunk: a hexagonal prism along the heading ``rot``, thick end at -u, flat bottom just under the ground (24 triangles)."""
    m = model["log"]
    r0 = float(m["diameter_m"]) * float(o["scale"]) / 2
    r1 = r0 * float(m["top_ratio"])
    half = float(o["len"]) / 2
    u, v = (math.cos(o["rot"]), math.sin(o["rot"])), (-math.sin(o["rot"]), math.cos(o["rot"]))
    base = o["z"] - 0.07 * r0  # both ends rest on the same level, so the thin end sits lower on its axis
    c = (o["x"], o["y"])
    a = _ring_yz(c, u, v, r0, base + 0.866 * r0, 6, 0.0, -half)
    b = _ring_yz(c, u, v, r1, base + 0.866 * r1, 6, 0.0, half)
    ca = (c[0] - u[0] * half, c[1] - u[1] * half, base + 0.866 * r0)
    cb = (c[0] + u[0] * half, c[1] + u[1] * half, base + 0.866 * r1)
    tris = _band(a, b) + _fan(a, ca) + _fan(b, cb)
    centre = (c[0], c[1], base + 0.8 * r0)
    tris = _outward(tris, centre)
    colour = hex_to_rgb(cfg["log"]["colours"][rng.randrange(len(cfg["log"]["colours"]))])
    return tris, [colour] * len(tris)


def stump_mesh(o: dict, model: dict, cfg: dict, rng) -> tuple[list, list]:
    """A cut stump: a hexagonal taper from 0.1 m under the ground to its height, with a lighter cut face on top (18 triangles)."""
    m = model["stump"]
    s = float(o["scale"])
    r0, r1, h = float(m["width_m"]) * s / 2, float(m["width_m"]) * s / 2 * float(m["top_ratio"]), float(m["height_m"]) * s
    c, rot = (o["x"], o["y"]), o["rot"]
    ring = lambda r, z: [(c[0] + r * math.cos(rot + k * math.pi / 3), c[1] + r * math.sin(rot + k * math.pi / 3), z) for k in range(6)]
    lo, hi = ring(r0, o["z"] - 0.1), ring(r1, o["z"] + h)
    side = _outward(_band(lo, hi), (c[0], c[1], o["z"] + h / 2))
    top = _outward(_fan(hi, (c[0], c[1], o["z"] + h)), (c[0], c[1], o["z"]))
    bark = hex_to_rgb(cfg["stump"]["colours"][rng.randrange(len(cfg["stump"]["colours"]))])
    cut = hex_to_rgb(cfg["stump"]["top_colour"])
    return side + top, [bark] * len(side) + [cut] * len(top)


def boulder_mesh(o: dict, model: dict, cfg: dict, rng) -> tuple[list, list]:
    """A squashed rock: three jittered hexagonal rings (a sunk foot, a wide belly, a narrow shoulder) and a peak (30 triangles)."""
    m = model["boulder"]
    s = float(o["scale"])
    a_ax = float(m["width_m"]) * s / 2
    b_ax = a_ax * (0.75 + 0.25 * rng.random())  # how squashed in plan
    h = float(m["height_m"]) * s
    c, rot = (o["x"], o["y"]), o["rot"]
    cr, sr = math.cos(rot), math.sin(rot)
    rings = []
    for k_ring, (zf, rf) in enumerate(((-0.12, 0.8), (0.4, 1.0), (0.8, 0.6))):
        ring = []
        for k in range(6):
            ang = 2 * math.pi * (k + (rng.random() - 0.5) * 0.4) / 6
            f = rf * (0.85 + 0.25 * rng.random())
            dx, dy = a_ax * f * math.cos(ang), b_ax * f * math.sin(ang)
            ring.append((c[0] + cr * dx - sr * dy, c[1] + sr * dx + cr * dy, o["z"] + h * (zf + (rng.random() - 0.5) * 0.08)))
        rings.append(ring)
    peak = (c[0] + (rng.random() - 0.5) * 0.2 * a_ax, c[1] + (rng.random() - 0.5) * 0.2 * b_ax, o["z"] + h)
    tris = _band(rings[0], rings[1]) + _band(rings[1], rings[2]) + _fan(rings[2], peak)
    tris = _outward(tris, (c[0], c[1], o["z"] + h * 0.4))
    colour = hex_to_rgb(cfg["boulder"]["colours"][rng.randrange(len(cfg["boulder"]["colours"]))])
    return tris, [colour] * len(tris)


def rootplate_mesh(o: dict, model: dict, cfg: dict, rng) -> tuple[list, list]:
    """An upturned root disc: an octagonal slab standing on edge, its flat faces across the heading ``rot`` (32 triangles)."""
    m = model["rootplate"]
    s = float(o["scale"])
    r, t, lift = float(m["radius_m"]) * s, float(m["thickness_m"]) * s, float(m["lift_m"]) * s
    u, v = (math.cos(o["rot"]), math.sin(o["rot"])), (-math.sin(o["rot"]), math.cos(o["rot"]))
    c = (o["x"], o["y"])
    jit = [0.85 + 0.15 * rng.random() for _ in range(8)]
    front = _ring_yz(c, u, v, r, o["z"] + lift, 8, math.pi / 8, -t / 2, jit)
    back = _ring_yz(c, u, v, r, o["z"] + lift, 8, math.pi / 8, t / 2, jit)
    mid = o["z"] + lift
    # the lower rim is buried whatever the jitter, so the slab never hangs in the air
    front, back = ([(x, y, min(z, o["z"] - 0.08) if z < mid else z) for x, y, z in ring] for ring in (front, back))
    tris = _band(front, back) + _fan(front, (c[0] - u[0] * t / 2, c[1] - u[1] * t / 2, mid)) + _fan(back, (c[0] + u[0] * t / 2, c[1] + u[1] * t / 2, mid))
    tris = _outward(tris, (c[0], c[1], mid))
    colour = hex_to_rgb(cfg["rootplate_colour"])
    return tris, [colour] * len(tris)


GROUND_MESHES = {"log": log_mesh, "stump": stump_mesh, "boulder": boulder_mesh, "rootplate": rootplate_mesh}


def ground_object_mesh(o: dict, cfg: dict) -> Mesh:
    """One ground object (``cfg`` = rules props.ground_objects); the look of a boulder or root plate is seeded from its position."""
    rng = seeded_xy("ground", o["x"], o["y"])
    tris, cols = GROUND_MESHES[o["kind"]](o, cfg["model"], cfg, rng)
    return _mesh(tris, cols)


def seeded_xy(purpose: str, x: float, y: float):
    """A ``random.Random`` seeded from a position (sha256, never ``hash()``), like the rest of the props."""
    import hashlib
    import random

    return random.Random(int.from_bytes(hashlib.sha256(f"{purpose}:{float(x):.3f}:{float(y):.3f}".encode()).digest()[:8], "big"))


def ground_objects_mesh(objects: list[dict], cfg: dict) -> Mesh:
    """All ground objects; every triangle remembers its object's base (x, y), as for trees (UV2, so the game can swap a detailed model in)."""
    meshes = []
    for o in objects:
        m = ground_object_mesh(o, cfg)
        m.bases = np.tile([float(o["x"]), float(o["y"])], (m.face_count, 1))
        meshes.append(m)
    return Mesh.concat(meshes)


def fence_meshes(sections: list[dict], cfg: dict) -> tuple[Mesh, dict]:
    """The visual of some fence sections: flat-colour triangles, and textured ones by texture id.

    Sections without a fence type are plain boxes; typed ones get their style (``fencestyles``).
    """
    from drycastle_pipeline.fencestyles import styled_fence_meshes  # it imports this module

    styled_flat, textured = styled_fence_meshes(sections, cfg)
    return Mesh.concat([fence_mesh(sections, cfg, untyped_only=True), styled_flat]), textured


def parcel_props_meshes(
    fences: list[dict], trees: list[dict], pcfg: dict
) -> tuple[Mesh, Mesh, dict]:
    """Visual (fences + trees), collision (fences, only if the rule is on) and the textured fence
    parts (``{texture id: TexturedMesh}``) of one parcel.

    The one place that builds per-parcel prop meshes: the chunk export and ``parcel-run --glb``
    both call it, so a regenerated parcel matches the baked one triangle for triangle.
    """
    fence_flat, textured = fence_meshes(fences, pcfg["fences"])
    visual = Mesh.concat([fence_flat, trees_mesh(trees, pcfg["trees"])])
    collision = (
        fence_mesh(fences, pcfg["fences"], True) if pcfg["fences"]["collision"] else Mesh.empty()
    )
    return visual, collision, textured
