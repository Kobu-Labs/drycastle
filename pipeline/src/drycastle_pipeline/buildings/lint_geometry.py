"""Geometry lint of one built house: the faults a player sees at once, found on the mesh itself.

``check(b, mesh, tags)`` takes the derived building (buildings/derive.py), its visual mesh with the detail
(``openings.build_detail``) and the window tags, and returns one short line per fault (empty: none found):

- **wall above the roof**: a wall (wall-coloured, or lying in a footprint wall) that shows above the eave with no roof
  over it on either side: a gable poking out beside a roof or up through it.
- **roofs through each other**: a roof face that passes through another roof face (not along a shared crease).
- **roof not believable**: a steep roof (over 50 degrees) that rises far more than the walls stand, a roof that climbs
  more than 8 m from its eave (one ridge across a house too deep for it), steeper than 62 degrees, or steeper than 60
  degrees across the house's narrow side.
- **faces at the ground**: an upward face at ground level (the flattened terrain is at z = 0: the two flicker), or two
  upward faces of different colours in one plane near the ground.

Run over the village: ``uv run python -m drycastle_pipeline.buildings.lint_geometry [ids...]``.
"""

import math
import sys

import numpy as np
from shapely.geometry import Polygon
from shapely.ops import unary_union

_VERTICAL = 0.2  # |normal z| below this: a wall
_UP = 0.15  # normal z above this: a roof (or anything one can stand on)
_OFFSET_M = 0.04  # each side of a wall, how far off its face the roof is looked for
_ROOF_TOL_M = 0.05  # a roof this little below a wall's point still covers it (the wall ends a hair under it)
_ABOVE_EAVE_M = 0.1
_SPACING_M = 0.2  # the samples on a wall triangle
_MIN_EXPOSED_M2 = 0.08
_CROSS_M = 0.15  # a roof face must reach this far each side of another's plane to count as passing through it
_CROSS_LEN_M = 0.3  # and cross it along this much
_GROUND_M = 0.02  # an upward face lower than this lies on the ground
_NEAR_GROUND_M = 1.0  # faces wholly below this are checked for fighting each other (core/zfight.py)
_STEEP_DEG = 50.0  # a roof this steep may not rise much above its walls
_STEEPEST_DEG = 62.0
_RUN_M = 8.0  # no roof climbs further than this from its eave to its top (a house 16 m deep under one ridge)


def _normals(tris: np.ndarray) -> np.ndarray:
    n = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
    length = np.linalg.norm(n, axis=1)
    return n / np.maximum(length, 1e-12)[:, None], length / 2


def _z_at(pts: np.ndarray, tris: np.ndarray) -> np.ndarray:
    """(m, k): the height of triangle k over point m (x, y), NaN where the point is not under it."""
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    v0, v1 = (b - a)[:, :2], (c - a)[:, :2]
    den = v0[:, 0] * v1[:, 1] - v0[:, 1] * v1[:, 0]
    ok = np.abs(den) > 1e-12
    den = np.where(ok, den, 1.0)
    d = pts[:, None, :2] - a[None, :, :2]
    u = (d[..., 0] * v1[None, :, 1] - d[..., 1] * v1[None, :, 0]) / den
    v = (v0[None, :, 0] * d[..., 1] - v0[None, :, 1] * d[..., 0]) / den
    inside = (u >= -1e-6) & (v >= -1e-6) & (u + v <= 1 + 1e-6) & ok[None, :]
    z = a[None, :, 2] + u * (b - a)[None, :, 2] + v * (c - a)[None, :, 2]
    return np.where(inside, z, np.nan)


def _samples(tri: np.ndarray, spacing: float) -> np.ndarray:
    """Points inside a triangle (not on its rim), about ``spacing`` apart."""
    longest = max(np.linalg.norm(tri[1] - tri[0]), np.linalg.norm(tri[2] - tri[1]), np.linalg.norm(tri[0] - tri[2]))
    n = max(2, min(40, int(math.ceil(longest / spacing))))
    pts = []
    for i in range(n + 1):
        for j in range(n + 1 - i):
            u, v = (i + 0.3) / (n + 1), (j + 0.3) / (n + 1)
            if u + v < 0.97:
                pts.append(tri[0] + u * (tri[1] - tri[0]) + v * (tri[2] - tri[0]))
    return np.array(pts) if pts else np.zeros((0, 3))


def _footprint(b: dict) -> Polygon:
    from shapely.geometry.polygon import orient

    from drycastle_pipeline.buildings.footprint import footprint_polygon

    return orient(footprint_polygon(b["footprint"]), 1.0)


def _on_walls(tris: np.ndarray, poly: Polygon, tol: float = 0.015) -> np.ndarray:
    """Which (upright) triangles lie in one of the footprint's walls."""
    ring = np.asarray(poly.exterior.coords)
    out = np.zeros(len(tris), bool)
    for p, q in zip(ring[:-1], ring[1:]):
        d = q - p
        length = float(np.hypot(*d))
        if length < 1e-6:
            continue
        u = d / length
        rel = tris[:, :, :2] - p
        off = np.abs(rel[..., 0] * u[1] - rel[..., 1] * u[0])
        along = rel[..., 0] * u[0] + rel[..., 1] * u[1]
        out |= (off.max(axis=1) < tol) & (along.min(axis=1) > -tol) & (along.max(axis=1) < length + tol)
    return out


def exposed_walls(b: dict, mesh) -> tuple[float, np.ndarray | None]:
    """(m2 of wall above the eave with no roof over it on either side, where the worst of it is)."""
    if b.get("roof") == "flat" or float(b.get("roof_height_m") or 0.0) <= 0.0 or mesh.face_count == 0:
        return 0.0, None
    tris = np.asarray(mesh.tris, float)
    cols = np.asarray(mesh.colours)
    n, area = _normals(tris)
    eave = min(float(b["eave_height_m"]), *(float(p["eave_height_m"]) for p in _parts(b)))
    upright = np.abs(n[:, 2]) < _VERTICAL
    walls = upright & (tris[:, :, 2].max(axis=1) > eave + _ABOVE_EAVE_M) & (area > 1e-4)
    wall_c = np.asarray(b["wall_colour"][:3], np.uint8)
    walls &= np.all(cols == wall_c, axis=1) | _on_walls(tris, _footprint(b))
    roofs = tris[(n[:, 2] > _UP) & ~walls]
    if not walls.any() or len(roofs) == 0:
        return 0.0, None
    pts, normals, weights = [], [], []
    for k in np.flatnonzero(walls):
        s = _samples(tris[k], _SPACING_M)
        s = s[s[:, 2] > eave + _ABOVE_EAVE_M]
        if len(s):
            pts.append(s)
            normals.append(np.repeat(n[k][None], len(s), axis=0))
            weights.append(np.full(len(s), area[k] / max(len(_samples(tris[k], _SPACING_M)), 1)))
    if not pts:
        return 0.0, None
    pts, normals, weights = np.concatenate(pts), np.concatenate(normals), np.concatenate(weights)
    bare = np.ones(len(pts), bool)
    for side in (1.0, -1.0):
        q = pts + side * _OFFSET_M * normals
        for lo in range(0, len(q), 256):
            z = _z_at(q[lo : lo + 256], roofs)
            covered = np.any(z >= q[lo : lo + 256, 2:3] - _ROOF_TOL_M, axis=1)
            bare[lo : lo + 256] &= ~covered
    if not bare.any():
        return 0.0, None
    worst = pts[bare][np.argmax(pts[bare][:, 2])]
    return float(weights[bare].sum()), worst


def _parts(b: dict) -> list[dict]:
    from drycastle_pipeline.buildings.meshes import split_parts

    return split_parts(b) if b.get("roof_part") else [b]


def _roof_faces(b: dict, mesh) -> np.ndarray:
    """The roof's own faces: its deck when it is tiled (the courses on it are left out), else every roof-coloured face."""
    tris = np.asarray(mesh.tris, float)
    cols = np.asarray(mesh.colours)
    n, area = _normals(tris)
    roof_c = np.asarray(b["roof_colour"][:3], float)
    deck = np.all(cols == (roof_c * 0.72).astype(int).astype(np.uint8), axis=1)
    pick = deck if deck.sum() > 4 else np.all(cols == roof_c.astype(np.uint8), axis=1)
    return tris[pick & (n[:, 2] > _UP) & (area > 1e-4)]


def _tri_tri_cross(t: np.ndarray, s: np.ndarray) -> float:
    """How long a stretch of triangle ``t`` passes through ``s`` with both sides reaching _CROSS_M past it (0: it does not)."""
    def plane(tri):
        nn = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        nn = nn / max(np.linalg.norm(nn), 1e-12)
        return nn, float(nn @ tri[0])

    ns, ds = plane(s)
    dt = t @ ns - ds
    if dt.max() < _CROSS_M or dt.min() > -_CROSS_M:
        return 0.0
    nt, dtt = plane(t)
    d_s = s @ nt - dtt
    if d_s.max() < _CROSS_M or d_s.min() > -_CROSS_M:
        return 0.0

    def seg(tri, dist):  # where the other plane cuts the triangle
        pts = []
        for i in range(3):
            a, c = tri[i], tri[(i + 1) % 3]
            da, dc = dist[i], dist[(i + 1) % 3]
            if (da > 0) != (dc > 0):
                pts.append(a + (c - a) * (da / (da - dc)))
        return pts if len(pts) == 2 else None

    a, c = seg(t, dt), seg(s, d_s)
    if a is None or c is None:
        return 0.0
    line = np.cross(ns, nt)
    if np.linalg.norm(line) < 1e-9:
        return 0.0
    line /= np.linalg.norm(line)
    ta, tc = sorted([float(p @ line) for p in a]), sorted([float(p @ line) for p in c])
    return max(0.0, min(ta[1], tc[1]) - max(ta[0], tc[0]))


def roof_crossings(b: dict, mesh) -> tuple[float, np.ndarray | None]:
    """The longest stretch along which one roof face passes through another, and where."""
    if b.get("roof") == "flat":
        return 0.0, None
    roofs = _roof_faces(b, mesh)
    if len(roofs) < 2:
        return 0.0, None
    lo, hi = roofs.min(axis=1), roofs.max(axis=1)
    best, at = 0.0, None
    for i in range(len(roofs)):
        near = np.flatnonzero(np.all(lo[i + 1 :] <= hi[i] + 1e-6, axis=1) & np.all(hi[i + 1 :] >= lo[i] - 1e-6, axis=1)) + i + 1
        for j in near:
            got = _tri_tri_cross(roofs[i], roofs[j])
            if got > best:
                best, at = got, roofs[i].mean(axis=0)
    return best, at


def overhang_of(b: dict, mesh) -> float:
    """How far the roof reaches past the walls (the detail's overhang_m), read off the mesh."""
    roofs = _roof_faces(b, mesh)
    if len(roofs) == 0:
        return 0.0
    from shapely import distance, points

    poly = _footprint(b)
    far = distance(poly, points(roofs.reshape(-1, 3)[:, :2]))
    far = far[far > 0.02]
    return round(min(float(np.median(far)), 1.0), 2) if len(far) else 0.0


def roof_parts_through(b: dict, overhang: float = 0.0) -> list[str]:
    """Parts of a fitted roof (roof_fit.py) that show where they should not: a part's roof coming up again inside another
    part's on the far side of it (the two pass through each other), or a single slope rising above the roof it leans
    against."""
    if not b.get("roof_fit") or b.get("roof") == "flat" or float(b.get("roof_height_m") or 0.0) <= 0.0:
        return []
    from drycastle_pipeline.buildings.roof_fit import fit_building

    fit = fit_building(b, overhang)
    if fit is None or len(fit.parts) < 2:
        return []
    key = lambda pl: tuple(round(float(x), 6) for x in pl)  # noqa: E731
    owner = {}
    for k, p in enumerate(fit.parts):
        for pl in p.planes:
            owner.setdefault(key(pl), k)
    won: dict[int, list] = {}
    for g, pl in fit.pieces:
        if key(pl) in owner:
            won.setdefault(owner[key(pl)], []).append(g)
    out = []
    for k, p in enumerate(fit.parts):
        if k == 0 or k not in won:
            continue
        region = unary_union(won[k]).buffer(-0.02, join_style=2)
        others = [q for j, q in enumerate(fit.parts) if j != k]
        inner = unary_union([q.core.buffer(-0.1, join_style=2) for q in others if q.form != "lean_to"])
        if p.form == "lean_to":
            inside = region.intersection(inner)
            if inside.area > 0.25:
                c = inside.representative_point()
                out.append(f"roofs through each other: a single slope rises above the roof it leans against ({inside.area:.2f} m2 near ({c.x:.1f}, {c.y:.1f}))")
            continue
        own = p.core.difference(unary_union([q.core for q in others]).buffer(0.05, join_style=2)).buffer(-0.3, join_style=2).buffer(0.3, join_style=2)
        for g in getattr(region, "geoms", [region]):
            # (own stops 5 cm short of the other parts: a piece that runs on from it across their wall is 5 cm away)
            if g.is_empty or g.area < 0.25 or g.distance(own) < 0.1 or not g.buffer(-0.05).intersects(inner):
                continue
            c = g.representative_point()
            out.append(f"roofs through each other: a part's roof comes up through another's ({g.area:.2f} m2 near ({c.x:.1f}, {c.y:.1f}))")
    return out


def through_roof(b: dict, mesh) -> tuple[int, np.ndarray | None]:
    """Upright pieces that are neither wall nor roof (downpipes, gutters, fascias, frames) and pass through a roof: above
    it and well below it at one place, away from the roof's rim. (count, where the first is)"""
    if b.get("roof") == "flat":
        return 0, None
    tris = np.asarray(mesh.tris, float)
    cols = np.asarray(mesh.colours)
    n, area = _normals(tris)
    roofs = _roof_faces(b, mesh)
    if len(roofs) == 0:
        return 0, None
    wall_c = np.asarray(b["wall_colour"][:3], np.uint8)
    roof_c = np.asarray(b["roof_colour"][:3], float)
    roofish = np.all(cols == roof_c.astype(np.uint8), axis=1) | np.all(cols == (roof_c * 0.72).astype(int).astype(np.uint8), axis=1)
    cand = np.flatnonzero((np.abs(n[:, 2]) < _VERTICAL) & ~np.all(cols == wall_c, axis=1) & ~roofish & (area > 1e-4))
    lo_z = float(roofs[:, :, 2].min()) - 0.3
    cand = cand[tris[cand, :, 2].max(axis=1) > lo_z]
    # the roof in its planes: a piece is above one plane and below it at one place to pass through it. (Above a lower
    # roof and under the overhang of a higher one, as a ridge cap or a gutter that runs in under the eave of a higher
    # part, it passes through neither.)
    rn, _ = _normals(roofs)
    keys = np.round(np.column_stack([rn, np.einsum("ij,ij->i", rn, roofs[:, 0])]), 2)
    planes = [roofs[np.all(keys == key, axis=1)] for key in np.unique(keys, axis=0)]
    hits, at = 0, None
    for k in cand:
        s = _samples(tris[k], 0.1)
        if len(s) == 0:
            continue
        lo, hi = s.min(axis=0), s.max(axis=0)
        for plane in planes:
            if np.any(plane.min(axis=(0, 1))[:2] > hi[:2] + 0.1) or np.any(plane.max(axis=(0, 1))[:2] < lo[:2] - 0.1):
                continue
            zs = []
            for dx, dy in ((0, 0), (0.06, 0), (-0.06, 0), (0, 0.06), (0, -0.06)):
                z = _z_at(s + np.array([dx, dy, 0.0]), plane)
                zs.append(np.where(np.isnan(z), -np.inf, z).max(axis=1))
            zs = np.array(zs)
            inside = np.all(np.isfinite(zs), axis=0)  # well inside the plane's plan, not at its rim
            if inside.sum() < 2:
                continue
            dz = s[inside, 2] - zs[0][inside]
            if dz.max() > 0.05 and dz.min() < -0.2:
                hits += 1
                at = at if at is not None else s[inside][np.argmin(np.abs(dz))]
                break
    return hits, at


def roof_rise(b: dict, mesh) -> str | None:
    """Why the roof is not believable, or None."""
    if b.get("roof") == "flat" or float(b.get("roof_height_m") or 0.0) <= 0.0:
        return None
    roofs = _roof_faces(b, mesh)
    if len(roofs) == 0:
        return None
    eave = float(b["eave_height_m"])
    rise = float(roofs[:, :, 2].max()) - eave
    rect = _footprint(b).minimum_rotated_rectangle
    sides = sorted(math.dist(*pq) for pq in zip(rect.exterior.coords[:-1], rect.exterior.coords[1:]))
    narrow = sides[0] if sides else 0.0
    # the roof's pitch: of its slopes, by their area (the main roof's; a small hip or bump does not move it)
    n, area = _normals(roofs)
    order = np.argsort(n[:, 2])
    cum = np.cumsum(area[order])
    nz = float(n[order][np.searchsorted(cum, cum[-1] / 2), 2])
    pitch = math.degrees(math.acos(min(max(nz, -1.0), 1.0)))
    run = rise / max(math.tan(math.radians(pitch)), 1e-6)  # how far the slopes climb from the eave to the top
    # A roof may rise well above its walls (a steep roof over an attic, 45 degrees over a house 11 m deep, stands
    # higher than the walls do): taller than 1.35 times the walls is not believed when it is steep too, or when one
    # roof spans a house wider than a gabled or hipped roof does (the ridge read across the house)
    if rise > 1.35 * eave and pitch > _STEEP_DEG:
        return f"roof not believable: rises {rise:.1f} m over walls {eave:.1f} m high, at {pitch:.0f} degrees"
    if run > _RUN_M:
        return f"roof not believable: rises {rise:.1f} m over walls {eave:.1f} m high, one roof {2 * run:.0f} m across"
    if pitch > _STEEPEST_DEG:
        return f"roof not believable: {pitch:.0f} degrees steep"
    if narrow > 0 and rise > 0.9 * narrow:
        return f"roof not believable: rises {rise:.1f} m over a house {narrow:.1f} m across"
    return None


def ground_faces(b: dict, mesh) -> list[str]:
    """Upward faces that fight the ground or each other near it."""
    tris = np.asarray(mesh.tris, float)
    if len(tris) == 0:
        return []
    cols = np.asarray(mesh.colours)
    n, area = _normals(tris)
    flat = (n[:, 2] > 0.999) & (area > 2e-4) & (np.ptp(tris[:, :, 2], axis=1) < 1e-4)
    z = tris[:, 0, 2]
    out = []
    on_ground = flat & (z > -0.005) & (z < _GROUND_M)
    if on_ground.any():
        worst = int(np.flatnonzero(on_ground)[np.argmax(area[on_ground])])
        c = tris[worst].mean(axis=0)
        out.append(f"faces at the ground: {area[on_ground].sum():.2f} m2 of upward face at z={z[worst]:.3f} (fights the terrain) near ({c[0]:.1f}, {c[1]:.1f})")
    from drycastle_pipeline.core import zfight

    low = np.flatnonzero(tris[:, :, 2].max(axis=1) < _NEAR_GROUND_M)
    fought = zfight.fights(tris[low], cols[low])
    total = sum(a for _, _, a in fought)
    if total > zfight.WARN_M2:
        i = low[max(fought, key=lambda f: f[2])[0]]
        c = tris[i].mean(axis=0)
        out.append(f"faces at the ground: {total:.2f} m2 of faces in one plane in two colours (they flicker) near ({c[0]:.1f}, {c[1]:.1f}, {c[2]:.2f})")
    return out


def check(b: dict, mesh, tags=None) -> list[str]:
    """One short line per geometry fault of the built house (empty: none found)."""
    out = []
    m2, at = exposed_walls(b, mesh)
    if m2 >= _MIN_EXPOSED_M2:
        out.append(f"wall above the roof: {m2:.2f} m2 with no roof over it, highest at ({at[0]:.1f}, {at[1]:.1f}, {at[2]:.1f})")
    run, at = roof_crossings(b, mesh)
    if run >= _CROSS_LEN_M:
        out.append(f"roofs through each other: a roof face passes through another along {run:.2f} m near ({at[0]:.1f}, {at[1]:.1f}, {at[2]:.1f})")
    out += roof_parts_through(b, overhang_of(b, mesh))
    count, at = through_roof(b, mesh)
    if count:
        out.append(f"roofs through each other: {count} pipe, gutter or trim face(s) pass through a roof, first near ({at[0]:.1f}, {at[1]:.1f}, {at[2]:.1f})")
    why = roof_rise(b, mesh)
    if why:
        out.append(why)
    out += ground_faces(b, mesh)
    return out


KINDS = {"wall above the roof": "a", "roofs through each other": "b", "roof not believable": "c", "faces at the ground": "d"}


def main(argv: list[str] | None = None) -> int:
    """Lints every house with a detail file (or the ids given) as the current config builds it."""
    from drycastle_pipeline.buildings.openings import build_detail
    from drycastle_pipeline.edits.building_run import BuildingWorld

    argv = sys.argv[1:] if argv is None else argv
    world = BuildingWorld()
    ids = argv or sorted(p.stem for p in world.details_dir.glob("*.yaml"))
    counts = dict.fromkeys(KINDS.values(), 0)
    for bid in ids:
        try:
            b, d = world.derived(bid), world.detail(bid)
            if d is None:
                continue
            mesh, tags = build_detail(b, d)
        except Exception as exc:  # noqa: BLE001  (a house that does not build is reported, the rest go on)
            print(f"{bid}: does not build: {exc}")
            continue
        found = check(b, mesh, tags)
        for kind in {KINDS[k] for line in found for k in KINDS if line.startswith(k)}:
            counts[kind] += 1
        for line in found:
            print(f"{bid}: {line}")
    print("houses flagged per check: " + ", ".join(f"{k}={v}" for k, v in counts.items()) + f" (of {len(ids)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
