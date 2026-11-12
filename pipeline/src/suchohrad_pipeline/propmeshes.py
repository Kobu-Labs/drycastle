"""Low-poly prop meshes: fence boxes and blocky trees (counter-clockwise seen from outside).

Fence sections with a type are drawn by ``fencestyles`` (textured panels, columns, caps); the plain
box here is the look of a section without one, and the collision shape of every section.
"""

import math

import numpy as np

from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.meshes import Mesh


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


def tree_mesh(tree: dict, cfg: dict) -> Mesh:
    """Trunk box plus a canopy: two stacked boxes (broadleaf) or two stacked pyramids (conifer)."""
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
    return Mesh.concat([tree_mesh(t, cfg) for t in trees])


def fence_meshes(sections: list[dict], cfg: dict) -> tuple[Mesh, dict]:
    """The visual of some fence sections: flat-colour triangles, and textured ones by texture id.

    Sections without a fence type are plain boxes; typed ones get their style (``fencestyles``).
    """
    from suchohrad_pipeline.fencestyles import styled_fence_meshes  # it imports this module

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
