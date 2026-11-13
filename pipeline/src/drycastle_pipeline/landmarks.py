"""Hand-modelled landmark buildings that replace the rule-generated box-and-roof mesh.

A building opts in with the ``model`` and ``model_axis`` override fields (``config/overrides.yaml``).
``build_landmark(b)`` then returns the visual mesh (``detail=True``) or a lighter collision mesh in the
same frame as ``meshes.building_mesh`` (map metres, x east, y north, z up, standing on z = 0).

Models are written in a local frame (u across, v along the front axis, z up) whose origin is the mean of
the footprint's exterior vertices and whose v axis points along the bearing ``model_axis`` (radians
counter-clockwise from east). Output is deterministic and flat-coloured like every other mesh.
"""

import math
from collections.abc import Callable, Sequence

import numpy as np

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meshes import Mesh

Pt = tuple[float, float, float]
Face = tuple[Sequence[Pt], list[int]]  # convex polygon, colour


class _Solids:
    """Collects convex solids; every triangle is wound counter-clockwise seen from outside."""

    def __init__(self) -> None:
        self.tris: list[list[Pt]] = []
        self.cols: list[list[int]] = []

    def solid(self, faces: list[Face], centre: Pt | None = None) -> None:
        pts = np.array([p for poly, _ in faces for p in poly], float)
        c = np.array(centre) if centre is not None else pts.mean(axis=0)
        for poly, colour in faces:
            for k in range(1, len(poly) - 1):
                a, b, d = (np.array(poly[0]), np.array(poly[k]), np.array(poly[k + 1]))
                n = np.cross(b - a, d - a)
                if np.linalg.norm(n) < 1e-12:
                    continue
                if n @ ((a + b + d) / 3 - c) < 0:
                    b, d = d, b
                self.tris.append([tuple(a), tuple(b), tuple(d)])
                self.cols.append(colour)

    def box(self, x0, x1, y0, y1, z0, z1, side, top=None, bottom=None) -> None:
        top = top or side
        bottom = bottom or side
        c = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
        faces: list[Face] = [
            ([(x, y, z0) for x, y in c], bottom),
            ([(x, y, z1) for x, y in c], top),
        ]
        for k in range(4):
            (xa, ya), (xb, yb) = c[k], c[(k + 1) % 4]
            faces.append(([(xa, ya, z0), (xb, yb, z0), (xb, yb, z1), (xa, ya, z1)], side))
        self.solid(faces)

    def gable(self, x0, x1, y0, y1, ze, zr, roof, end, oh=0.0) -> None:
        """Gabled roof, ridge along y. ``oh`` is the eave overhang (the slope continues past the wall)."""
        xm = (x0 + x1) / 2
        drop = oh * (zr - ze) / (xm - x0)
        xa, xb, zl = x0 - oh, x1 + oh, ze - drop
        faces: list[Face] = [
            ([(xa, y0, zl), (xa, y1, zl), (xm, y1, zr), (xm, y0, zr)], roof),
            ([(xb, y0, zl), (xb, y1, zl), (xm, y1, zr), (xm, y0, zr)], roof),
            ([(xa, y1, zl), (xb, y1, zl), (xm, y1, zr)], end),
            ([(xa, y0, zl), (xb, y0, zl), (xm, y0, zr)], end),
            ([(xa, y0, zl), (xb, y0, zl), (xb, y1, zl), (xa, y1, zl)], roof),
        ]
        self.solid(faces)

    def shed(self, x0, x1, y0, y1, z_x0, z_x1, thick, colour) -> None:
        """Lean-to roof slab sloping from height ``z_x0`` at x0 to ``z_x1`` at x1 (top surface)."""
        faces: list[Face] = [
            ([(x0, y0, z_x0), (x1, y0, z_x1), (x1, y1, z_x1), (x0, y1, z_x0)], colour),
            (
                [
                    (x0, y0, z_x0 - thick),
                    (x1, y0, z_x1 - thick),
                    (x1, y1, z_x1 - thick),
                    (x0, y1, z_x0 - thick),
                ],
                colour,
            ),
            (
                [(x0, y0, z_x0 - thick), (x1, y0, z_x1 - thick), (x1, y0, z_x1), (x0, y0, z_x0)],
                colour,
            ),
            (
                [(x0, y1, z_x0 - thick), (x1, y1, z_x1 - thick), (x1, y1, z_x1), (x0, y1, z_x0)],
                colour,
            ),
            (
                [(x0, y0, z_x0 - thick), (x0, y1, z_x0 - thick), (x0, y1, z_x0), (x0, y0, z_x0)],
                colour,
            ),
            (
                [(x1, y0, z_x1 - thick), (x1, y1, z_x1 - thick), (x1, y1, z_x1), (x1, y0, z_x1)],
                colour,
            ),
        ]
        self.solid(faces)

    def pyramid(self, cx, cy, half, z0, z1, colour) -> None:
        c = [
            (cx - half, cy - half),
            (cx + half, cy - half),
            (cx + half, cy + half),
            (cx - half, cy + half),
        ]
        faces: list[Face] = [([(x, y, z0) for x, y in c], colour)]
        for k in range(4):
            (xa, ya), (xb, yb) = c[k], c[(k + 1) % 4]
            faces.append(([(xa, ya, z0), (xb, yb, z0), (cx, cy, z1)], colour))
        self.solid(faces)

    def diamond(self, cx, cy, cz, r, colour) -> None:
        """Small octahedron standing in for the ball under the cross."""
        ring = [(cx + r, cy, cz), (cx, cy + r, cz), (cx - r, cy, cz), (cx, cy - r, cz)]
        faces: list[Face] = []
        for k in range(4):
            faces.append(([ring[k], ring[(k + 1) % 4], (cx, cy, cz + r)], colour))
            faces.append(([ring[k], ring[(k + 1) % 4], (cx, cy, cz - r)], colour))
        self.solid(faces)

    def plate(
        self,
        face: str,
        plane: float,
        centre: float,
        profile: list[tuple[float, float]],
        depth: float,
        colour,
    ) -> None:
        """Flat decal (window, door, ...) standing ``depth`` proud of a wall.

        ``face``: ``front`` (+y), ``back`` (-y), ``left`` (-x), ``right`` (+x); ``plane`` is the wall
        coordinate, ``centre`` the position along it; ``profile`` is a convex polygon of (along, z).
        """
        nx, ny = {"front": (0, 1), "back": (0, -1), "left": (-1, 0), "right": (1, 0)}[face]
        tx, ty = (1, 0) if face in ("front", "back") else (0, 1)

        def at(a: float, z: float, d: float) -> Pt:
            if face in ("front", "back"):
                return (centre + a * tx, plane + d * ny, z)
            return (plane + d * nx, centre + a * ty, z)

        base = [at(a, z, 0.0) for a, z in profile]
        top = [at(a, z, depth) for a, z in profile]
        faces: list[Face] = [(top, colour)]
        for k in range(len(profile)):
            j = (k + 1) % len(profile)
            faces.append(([base[k], base[j], top[j], top[k]], colour))
        self.solid(faces)


def _rect(w: float, z0: float, z1: float) -> list[tuple[float, float]]:
    return [(-w / 2, z0), (w / 2, z0), (w / 2, z1), (-w / 2, z1)]


def _arch(w: float, z0: float, z1: float, rise: float) -> list[tuple[float, float]]:
    """Rectangle z0..z1 with a round-arched top ``rise`` high (convex)."""
    pts = [(-w / 2, z0), (w / 2, z0), (w / 2, z1)]
    for k in range(1, 5):
        a = math.pi * k / 5
        pts.append((w / 2 * math.cos(a), z1 + rise * math.sin(a)))
    pts.append((-w / 2, z1))
    return pts


def _disc(r: float, zc: float, n: int = 12) -> list[tuple[float, float]]:
    return [
        (r * math.cos(2 * math.pi * k / n), zc + r * math.sin(2 * math.pi * k / n))
        for k in range(n)
    ]


# ---- the church of Drycastle -------------------------------------------------------------------
# Layout in (u, v) metres from the footprint centre, read off the 14-corner footprint of w378900220
# (all corners are right angles in this frame): front (tower, porch) at +v.

YELLOW, WHITE = "#e3b53f", "#f1efe8"
ROOF, SPIRE, PLINTH = "#4c4a4f", "#8b8f99", "#7b3f3a"
GLASS, DOOR, STONE, IRON = "#2b2f38", "#3a2a22", "#8a8680", "#3d3d44"


def church_solids(detail: bool = True) -> _Solids:
    S = _Solids()
    y, w, rf, sp = (hex_to_rgb(c) for c in (YELLOW, WHITE, ROOF, SPIRE))
    pl, gl, st, ir = (hex_to_rgb(c) for c in (PLINTH, GLASS, STONE, IRON))
    cu = 0.985  # centre line of the nave, tower and porch
    nx0, nx1 = -2.88, 4.86  # nave
    z_eave, z_ridge = 7.4, 10.9

    def block(x0, x1, y0, y1, z1) -> None:
        S.box(x0, x1, y0, y1, 0.0, z1, y)
        S.box(x0 - 0.03, x1 + 0.03, y0 - 0.03, y1 + 0.03, 0.0, 0.6, pl)

    # nave (hollow: walls, floor, gallery, pews, see church_interior) and its gabled roof
    from drycastle_pipeline.church_interior import build_shell

    build_shell(S, detail, y, pl)
    S.gable(nx0, nx1, -7.3, 14.0, z_eave, z_ridge, rf, y, oh=0.35)
    # side aisles with lean-to roofs, rear apse and sacristy
    for x0, x1, y0, y1, inner_left in (
        (-5.87, nx0, -5.13, 10.76, False),
        (nx1, 8.38, -7.31, 11.39, True),
    ):
        block(x0, x1, y0, y1, 4.1)
        if inner_left:
            S.shed(x0, x1 + 0.35, y0 - 0.2, y1 + 0.2, 5.2, 4.2, 0.2, rf)
        else:
            S.shed(x0 - 0.35, x1, y0 - 0.2, y1 + 0.2, 4.2, 5.2, 0.2, rf)
    S.gable(-1.28, 4.96, -14.25, -7.3, 5.2, 7.6, rf, y, oh=0.3)
    # sacristy: stops at the nave wall, with a flat-roofed link to the apse
    block(-8.17, nx0, -12.08, -5.13, 3.8)
    S.gable(-8.17, nx0, -12.08, -5.13, 3.8, 5.6, rf, y, oh=0.3)
    block(nx0, -1.28, -12.08, -7.3, 3.8)
    S.box(nx0, -1.28, -12.08, -7.3, 3.8, 4.0, rf)
    # porch: floor slab, four white columns, roof slab on a white entablature
    S.box(nx0 - 0.1, nx1 + 0.1, 14.0, 16.6, 0.0, 0.25, st)
    for dx in (-3.5, -1.2, 1.2, 3.5):
        S.box(cu + dx - 0.2, cu + dx + 0.2, 16.0, 16.4, 0.25, 3.3, w)
    S.box(nx0 - 0.15, nx1 + 0.15, 13.9, 16.7, 3.3, 3.7, w)
    S.shed(nx0 - 0.25, nx1 + 0.25, 13.9, 16.9, 3.95, 3.95, 0.25, rf)
    # facade pilasters and cornice
    for dx in (-3.5, -1.3, 1.3, 3.5):
        S.box(cu + dx - 0.28, cu + dx + 0.28, 14.0, 14.12, 0.25, z_eave, w)
    S.box(nx0 - 0.15, nx1 + 0.15, 13.9, 14.25, z_eave - 0.4, z_eave, w)
    # tower: shaft, white corner posts, band and cornice, louvred belfry, spire, ball and cross
    tx0, tx1, ty0, ty1 = cu - 1.95, cu + 1.95, 10.0, 13.9
    z_belfry, z_cornice = 12.9, 15.5
    S.box(tx0, tx1, ty0, ty1, z_eave - 0.4, z_cornice, y)
    for px in (tx0 - 0.08, tx1 - 0.42):
        for py in (ty0 - 0.08, ty1 - 0.42):
            S.box(px, px + 0.5, py, py + 0.5, z_eave - 0.4, z_cornice, w)
    S.box(tx0 - 0.08, tx1 + 0.08, ty0 - 0.08, ty1 + 0.08, z_belfry - 0.2, z_belfry + 0.15, w)
    S.box(tx0 - 0.3, tx1 + 0.3, ty0 - 0.3, ty1 + 0.3, z_cornice, z_cornice + 0.5, w)
    z_sp = z_cornice + 0.5
    S.pyramid(cu, (ty0 + ty1) / 2, 2.0, z_sp, z_sp + 3.4, sp)
    S.diamond(cu, (ty0 + ty1) / 2, z_sp + 3.5, 0.3, sp)
    zc = z_sp + 3.8
    S.box(cu - 0.06, cu + 0.06, (ty0 + ty1) / 2 - 0.06, (ty0 + ty1) / 2 + 0.06, zc, zc + 1.6, ir)
    S.box(
        cu - 0.4, cu + 0.4, (ty0 + ty1) / 2 - 0.05, (ty0 + ty1) / 2 + 0.05, zc + 0.9, zc + 1.05, ir
    )
    # ridge turret with a small cross at the rear of the nave
    S.box(cu - 0.3, cu + 0.3, -3.3, -2.7, z_ridge - 0.5, z_ridge + 1.3, y)
    S.pyramid(cu, -3.0, 0.4, z_ridge + 1.3, z_ridge + 2.0, sp)
    S.box(cu - 0.04, cu + 0.04, -3.04, -2.96, z_ridge + 2.0, z_ridge + 2.9, ir)
    S.box(cu - 0.25, cu + 0.25, -3.03, -2.97, z_ridge + 2.5, z_ridge + 2.6, ir)

    if not detail:
        return S

    # windows, doors and louvres (flat decals on the walls)
    S.plate(
        "front", 14.0, cu, _arch(1.3, 4.5, 6.2, 0.7), 0.04, w
    )  # arched window with a white frame
    S.plate("front", 14.0, cu, _arch(0.9, 4.6, 6.0, 0.45), 0.08, gl)
    S.plate("front", 14.0, cu, _disc(0.62, 9.0), 0.04, w)  # round window in the pediment
    S.plate("front", 14.0, cu, _disc(0.42, 9.0), 0.08, gl)
    S.plate("front", ty1, cu, _rect(0.9, 9.7, 11.3), 0.04, w)  # small tower window
    S.plate("front", ty1, cu, _rect(0.6, 9.85, 11.15), 0.08, gl)
    for dx in (-0.95, 0.95):  # belfry louvres, front and back
        S.plate("front", ty1, cu + dx, _rect(0.75, z_belfry + 0.5, z_cornice - 0.3), 0.06, gl)
        S.plate("back", ty0, cu + dx, _rect(0.75, z_belfry + 0.5, z_cornice - 0.3), 0.06, gl)
    mid = (ty0 + ty1) / 2
    S.plate("left", tx0, mid, _rect(0.75, z_belfry + 0.5, z_cornice - 0.3), 0.06, gl)
    S.plate("right", tx1, mid, _rect(0.75, z_belfry + 0.5, z_cornice - 0.3), 0.06, gl)
    for k in range(7):  # arched windows in the nave walls above the aisle roofs
        v = -5.0 + 2.5 * k
        S.plate("left", nx0, v, _arch(0.8, 5.6, 6.7, 0.55), 0.06, gl)
        S.plate("right", nx1, v, _arch(0.8, 5.6, 6.7, 0.55), 0.06, gl)
    for k in range(5):  # aisle walls: arched windows on one side, round windows on the other
        v = -3.0 + 3.0 * k
        S.plate("left", -5.87, v, _arch(0.7, 1.4, 2.8, 0.5), 0.06, gl)
        S.plate("right", 8.38, v, _disc(0.35, 2.4), 0.06, gl)
    return S


MODELS: dict[str, Callable[[bool], _Solids]] = {"church": church_solids}


def _bunker(b: dict, detail: bool) -> Mesh:
    """Pillbox on the real footprint; ``model_axis`` is the bearing the door faces."""
    from drycastle_pipeline.bunker import bunker_mesh

    a = float(b["model_axis"])
    return bunker_mesh(b["footprint"]["exterior"], (math.cos(a), math.sin(a)), detail)


def _office(b: dict, detail: bool) -> Mesh:
    """Municipal office of P-0288: one hipped roof over three footprints (see ``office.py``)."""
    from drycastle_pipeline.office import office_mesh

    return office_mesh(b, detail)


def _flats(b: dict, detail: bool) -> Mesh:
    """Block of flats Drycastle 295 (P-0561): loggias, balconies, staircase block (see ``flats.py``)."""
    from drycastle_pipeline.flats import flats_mesh

    return flats_mesh(b, detail)


def _loggia_house(b: dict, detail: bool) -> Mesh:
    """Flat-roofed house with corner loggias and a recessed staircase bay (see ``loggia_house.py``)."""
    from drycastle_pipeline.loggia_house import loggia_house_mesh

    return loggia_house_mesh(b, detail)


# Models built straight on the footprint (map metres) instead of in a local frame.
FOOTPRINT_MODELS: dict[str, Callable[[dict, bool], Mesh]] = {
    "bunker": _bunker,
    "office": _office,
    "flats": _flats,
    "loggia_house": _loggia_house,
}


def build_landmark(b: dict, detail: bool = True) -> Mesh:
    """Mesh of the landmark model named by ``b["model"]``, placed on the footprint of ``b``."""
    if b["model"] in FOOTPRINT_MODELS:
        return FOOTPRINT_MODELS[b["model"]](b, detail)
    solids = MODELS[b["model"]](detail)
    ring = np.array(b["footprint"]["exterior"], float)
    origin = ring.mean(axis=0)
    a = float(b["model_axis"])
    v_axis = np.array([math.cos(a), math.sin(a)])
    u_axis = np.array([math.sin(a), -math.cos(a)])
    tris = np.array(solids.tris, float)
    out = np.empty_like(tris)
    out[..., 0:2] = origin + tris[..., 0:1] * u_axis + tris[..., 1:2] * v_axis
    out[..., 2] = tris[..., 2]
    return Mesh(out, np.array(solids.cols, np.uint8))
