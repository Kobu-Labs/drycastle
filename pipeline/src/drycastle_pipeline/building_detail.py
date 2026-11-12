"""Hand-described building detail: wall openings (windows, doors), roof overhang -> mesh + window tags.

A building opts in with ``config/buildings/<osm id>.yaml``. The shell is the rule-generated one (same
footprint, eave, roof), rebuilt so that:

* every opening is a real hole in its wall, with recessed reveals and a recessed panel behind it;
* the roof overhangs the walls by ``overhang_m`` and has a visible underside;
* every opening of kind ``window`` gets its pane as a separate node ``window_<building>_<id>`` (glass
  material, own mesh), and is listed in ``windows.json`` with its position, size and facade, so the
  game can later make it transparent and breakable. Doors and garage doors stay part of the shell.

File format::

    id: w378920362
    source: "Street View 2022-09 frames 0,1"     # free text, where the facts come from
    overhang_m: 0.4                              # optional, default 0
    frame_colour: "#f2f0ea"                      # optional, reveals (window and door frames)
    openings:
      - id: g1                 # unique per building
        kind: garage_door      # window | door | garage_door | recess
        edge: 2                # footprint edge: vertex i -> i+1 of the counter-clockwise exterior ring
        at_m: 1.0              # distance along the edge from vertex i to the opening's left side
        width_m: 2.4
        height_m: 2.1
        sill_m: 0.0            # bottom of the opening above the ground
        recess_m: 0.15         # optional, depth of the panel behind the wall plane
        colour: "#5a4636"      # optional, panel colour
        confidence: low        # optional: low | medium | high
        breakable: true        # optional, windows only (default true)
        parent: r1             # optional: id of a `recess` on the same edge this opening is cut into
                               # (the recess is a bay set back by its recess_m, e.g. a porch; children
                               # sit in its back wall and their recess_m adds to the parent's)

A building may also list ``dormers`` (small gabled boxes on a roof slope, the roof side of an eave wall)::

    dormers:
      - id: d1
        edge: 3                # eave wall whose roof slope carries the dormer
        at_m: 3.3              # centre of the dormer along the edge
        width_m: 1.4           # front face width (optional, default 1.4)
        bottom_m: 3.85         # height of the front face's foot above the ground (it stands on the roof)
        height_m: 1.35         # front face height up to the dormer's eave (optional)
        rise_m: 0.55           # rise of the dormer's own gable roof (optional)
        window_m: [0.8, 0.95]  # window width, height (optional), centred on the front face
        colour: "#6b4a35"      # cladding colour (optional); the roof uses the building's roof colour

Edges are numbered on ``orient(footprint, 1.0)`` of ``buildings.json``; ``facade_overlay`` prints and
draws exactly these numbers. Everything is in the map frame of ``meshes.building_mesh``.
"""

import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import yaml
from shapely.affinity import rotate
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings import footprint_polygon, hex_to_rgb, roof_frame
from drycastle_pipeline.config import PIPELINE_DIR
from drycastle_pipeline.meshes import (
    Mesh,
    _polygons,
    _roof_regions,
    _split_edge,
    triangulate,
)

DETAILS_DIR = PIPELINE_DIR / "config" / "buildings"
KINDS = ("window", "door", "garage_door", "recess")
CONFIDENCE = ("low", "medium", "high")
GLASS = (150, 190, 215)
DEFAULT_FRAME = "#f2f0ea"
DEFAULT_PANEL = {"door": "#5a4636", "garage_door": "#6b5a48", "recess": "#3a2c22"}
SOFFIT_COLOUR = (240, 236, 226)
ROOF_THICKNESS_M = 0.12


class DetailError(Exception):
    """Invalid building detail file."""


@dataclass
class Opening:
    id: str
    kind: str
    edge: int
    at_m: float
    width_m: float
    height_m: float
    sill_m: float = 0.0
    recess_m: float = 0.12
    colour: str | None = None
    confidence: str = "medium"
    breakable: bool = True
    parent: str | None = None  # id of a `recess` this opening is cut into (nested one level or more)


@dataclass
class Dormer:
    id: str
    edge: int
    at_m: float
    bottom_m: float
    width_m: float = 1.4
    height_m: float = 1.35
    rise_m: float = 0.55
    window_m: tuple = (0.8, 0.95)
    colour: str = "#6b4a35"


@dataclass
class Detail:
    id: str
    overhang_m: float = 0.0
    frame_colour: str = DEFAULT_FRAME
    source: str = ""
    openings: list[Opening] = field(default_factory=list)
    dormers: list[Dormer] = field(default_factory=list)


@dataclass
class WindowTag:
    """One window pane: its own mesh plus the metadata written to ``windows.json``."""

    building: str
    id: str
    mesh: Mesh
    meta: dict

    @property
    def node(self) -> str:
        return f"window_{self.building}_{self.id}"


def _hex(value: str, where: str) -> str:
    try:
        hex_to_rgb(value)
    except Exception as exc:
        raise DetailError(f"{where}: bad colour {value!r}") from exc
    return value


def parse_detail(raw: dict, where: str = "detail") -> Detail:
    if not isinstance(raw, dict) or "id" not in raw:
        raise DetailError(f"{where}: needs a mapping with an `id`")
    known = {"id", "source", "overhang_m", "frame_colour", "openings", "dormers"}
    extra = set(raw) - known
    if extra:
        raise DetailError(f"{where}: unknown field(s) {sorted(extra)}")
    d = Detail(
        id=str(raw["id"]),
        overhang_m=float(raw.get("overhang_m", 0.0)),
        frame_colour=_hex(raw.get("frame_colour", DEFAULT_FRAME), where),
        source=str(raw.get("source", "")),
    )
    if not 0.0 <= d.overhang_m <= 2.0:
        raise DetailError(f"{where}: overhang_m must be 0..2")
    seen = set()
    for i, o in enumerate(raw.get("openings") or []):
        w = f"{where} openings[{i}]"
        missing = {"id", "kind", "edge", "at_m", "width_m", "height_m"} - set(o)
        if missing:
            raise DetailError(f"{w}: missing {sorted(missing)}")
        extra = set(o) - set(Opening.__dataclass_fields__)
        if extra:
            raise DetailError(f"{w}: unknown field(s) {sorted(extra)}")
        op = Opening(**{**o, "id": str(o["id"])})
        if op.kind not in KINDS:
            raise DetailError(f"{w}: kind must be one of {KINDS}")
        if op.confidence not in CONFIDENCE:
            raise DetailError(f"{w}: confidence must be one of {CONFIDENCE}")
        if op.id in seen:
            raise DetailError(f"{w}: duplicate id {op.id!r}")
        if op.width_m <= 0.2 or op.height_m <= 0.2 or op.sill_m < 0 or op.recess_m <= 0:
            raise DetailError(f"{w}: width, height and recess must be positive, sill >= 0")
        if op.colour:
            _hex(op.colour, w)
        seen.add(op.id)
        d.openings.append(op)
    for i, raw_d in enumerate(raw.get("dormers") or []):
        w = f"{where} dormers[{i}]"
        missing = {"id", "edge", "at_m", "bottom_m"} - set(raw_d)
        extra = set(raw_d) - set(Dormer.__dataclass_fields__)
        if missing or extra:
            raise DetailError(f"{w}: missing {sorted(missing)}, unknown {sorted(extra)}")
        dm = Dormer(**{**raw_d, "id": str(raw_d["id"]), "window_m": tuple(raw_d.get("window_m", (0.8, 0.95)))})
        if dm.id in seen or dm.width_m < 0.5 or dm.height_m < 0.5 or dm.rise_m <= 0:
            raise DetailError(f"{w}: duplicate id or too small")
        if dm.window_m[0] >= dm.width_m - 0.1 or dm.window_m[1] >= dm.height_m - 0.1:
            raise DetailError(f"{w}: window_m must fit inside the front face")
        _hex(dm.colour, w)
        seen.add(dm.id)
        d.dormers.append(dm)
    by_id = {o.id: o for o in d.openings}
    for o in d.openings:
        if o.parent is None:
            continue
        par = by_id.get(o.parent)
        if par is None or par.kind != "recess" or par.edge != o.edge:
            raise DetailError(f"{where}: opening {o.id} parent {o.parent!r} must be a recess on the same edge")
        if not (par.at_m <= o.at_m and o.at_m + o.width_m <= par.at_m + par.width_m
                and par.sill_m <= o.sill_m and o.sill_m + o.height_m <= par.sill_m + par.height_m):
            raise DetailError(f"{where}: opening {o.id} does not lie inside its parent {o.parent!r}")
    return d


def load_details(directory: Path = DETAILS_DIR) -> dict[str, Detail]:
    out: dict[str, Detail] = {}
    if not directory.is_dir():
        return out
    for path in sorted(directory.glob("*.yaml")):
        d = parse_detail(yaml.safe_load(path.read_text()), path.name)
        if d.id != path.stem:
            raise DetailError(f"{path.name}: id {d.id!r} must match the file name")
        out[d.id] = d
    return out


def _unclamped_height(roof: str, rise: float, cu: float, cv: float, a: float, b: float):
    """Roof height that keeps sloping outside the walls (the overhang continues the plane)."""

    def gabled(u: float, v: float) -> float:
        return rise * (1.0 - abs(v - cv) / b)

    def hipped(u: float, v: float) -> float:
        return rise * min(1.0, (b - abs(v - cv)) / b, (a - abs(u - cu)) / b)

    return gabled if roof == "gabled" else hipped


class _Tris:
    def __init__(self) -> None:
        self.tris: list[np.ndarray] = []
        self.cols: list = []

    def add(self, tri, colour) -> None:
        self.tris.append(np.asarray(tri, dtype=np.float64))
        self.cols.append(colour)

    def quad(self, pts, colour, toward) -> None:
        """Two triangles of a planar quad, wound so the normal points toward ``toward``."""
        p = [np.asarray(x, float) for x in pts]
        n = np.cross(p[1] - p[0], p[2] - p[0])
        if n @ (np.asarray(toward, float) - p[0]) < 0:
            p = p[::-1]
        self.add([p[0], p[1], p[2]], colour)
        self.add([p[0], p[2], p[3]], colour)

    def mesh(self) -> Mesh:
        if not self.tris:
            return Mesh.empty()
        return Mesh(np.array(self.tris), np.array(self.cols, dtype=np.uint8))


def build_detail(b: dict, d: Detail) -> tuple[Mesh, list[WindowTag]]:
    """Visual mesh of building ``b`` with the detail ``d`` applied, and its window panes."""
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    eave, rise, roof = float(b["eave_height_m"]), float(b["roof_height_m"]), b["roof"]
    wall_c, roof_c = b["wall_colour"], b["roof_colour"]
    flat = roof == "flat" or rise <= 0

    if flat:
        angle = 0.0

        def top(x: float, y: float) -> float:
            return eave

        boundary = None
        regions: list[Polygon] = []
        height = None
    else:
        angle, cu, cv, a, bb = roof_frame(poly, b["ridge_dir"], hipped=roof == "hipped")
        regions = _roof_regions(roof, cu, cv, a, bb)
        height = _unclamped_height(roof, rise, cu, cv, a, bb)
        ca, sa = math.cos(angle), math.sin(angle)

        def top(x: float, y: float) -> float:
            u, v = x * ca + y * sa, -x * sa + y * ca
            return eave + max(0.0, height(u, v))

        boundary = rotate(
            unary_union([r.exterior for r in regions]), angle, origin=(0, 0), use_radians=True
        )

    out = _Tris()
    windows: list[WindowTag] = []
    ring = list(poly.exterior.coords)[:-1]
    by_edge: dict[int, list[Opening]] = {}
    n_edges = len(ring)
    for o in d.openings:
        if not 0 <= o.edge < n_edges:
            raise DetailError(f"{d.id} opening {o.id}: edge {o.edge} not in 0..{n_edges - 1}")
        by_edge.setdefault(o.edge, []).append(o)

    for i, p in enumerate(ring):
        q = ring[(i + 1) % n_edges]
        length = math.dist(p, q)
        if length < 1e-6:
            continue
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        out_n = (uy, -ux)  # outward normal of a counter-clockwise ring
        chain = _split_edge(p, q, boundary, top)
        shell = [(0.0, 0.0), (length, 0.0)]
        shell += [(math.dist(p, c), top(*c)) for c in reversed(chain)]
        holes = []
        for o in by_edge.get(i, []):
            x0, x1 = o.at_m, o.at_m + o.width_m  # every opening must fit the wall (children too)
            z0, z1 = o.sill_m, o.sill_m + o.height_m
            local_top = min(top(p[0] + ux * s, p[1] + uy * s) for s in (x0, x1))
            if x0 < 0.05 or x1 > length - 0.05 or z1 > local_top - 0.05:
                raise DetailError(
                    f"{d.id} opening {o.id}: does not fit edge {i} "
                    f"(length {length:.2f} m, wall height {local_top:.2f} m)"
                )
            if o.parent is None:
                holes.append([(x0, z0), (x1, z0), (x1, z1), (x0, z1)])
        wall = orient(Polygon(shell, holes), 1.0)

        def to3(s: float, z: float, depth: float = 0.0, p=p, ux=ux, uy=uy, out_n=out_n):
            return (p[0] + ux * s - out_n[0] * depth, p[1] + uy * s - out_n[1] * depth, z)

        for tri in triangulate(wall):
            a0, a1, a2 = tri
            area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
            order = (a0, a1, a2) if area > 0 else (a0, a2, a1)
            out.add([to3(*c) for c in order], wall_c)

        edge_ops = by_edge.get(i, [])

        def emit(o: Opening, base: float, edge=i, ops=edge_ops, to3=to3, out_n=out_n) -> None:
            x0, x1 = o.at_m, o.at_m + o.width_m
            z0, z1 = o.sill_m, o.sill_m + o.height_m
            r = base + o.recess_m
            mid = to3((x0 + x1) / 2, (z0 + z1) / 2, (base + r) / 2)
            frame = hex_to_rgb(d.frame_colour)
            for (sa_, za_), (sb_, zb_) in (
                ((x0, z0), (x1, z0)), ((x1, z0), (x1, z1)), ((x1, z1), (x0, z1)), ((x0, z1), (x0, z0))
            ):
                out.quad(
                    [to3(sa_, za_, base), to3(sb_, zb_, base), to3(sb_, zb_, r), to3(sa_, za_, r)],
                    hex_to_rgb(o.colour or DEFAULT_PANEL["recess"]) if o.kind == "recess" else frame,
                    mid,
                )
            kids = [k for k in ops if k.parent == o.id]
            outside = to3((x0 + x1) / 2, (z0 + z1) / 2, -1.0)
            if o.kind == "window":
                pane = _Tris()
                pane.quad([to3(x0, z0, r), to3(x1, z0, r), to3(x1, z1, r), to3(x0, z1, r)], GLASS, outside)
                centre = to3((x0 + x1) / 2, (z0 + z1) / 2, r)
                meta = {
                    "building": d.id,
                    "id": o.id,
                    "node": f"window_{d.id}_{o.id}",
                    "kind": "window",
                    "edge": edge,
                    "breakable": o.breakable,
                    "confidence": o.confidence,
                    "width_m": o.width_m,
                    "height_m": o.height_m,
                    "centre": [round(centre[0], 3), round(centre[1], 3), round(centre[2], 3)],
                    "normal": [round(out_n[0], 4), round(out_n[1], 4)],
                }
                windows.append(WindowTag(d.id, o.id, pane.mesh(), meta))
            else:
                colour = hex_to_rgb(o.colour or DEFAULT_PANEL[o.kind])
                back = Polygon(
                    [(x0, z0), (x1, z0), (x1, z1), (x0, z1)],
                    [[(k.at_m, k.sill_m), (k.at_m + k.width_m, k.sill_m),
                      (k.at_m + k.width_m, k.sill_m + k.height_m), (k.at_m, k.sill_m + k.height_m)]
                     for k in kids],
                )
                for tri in triangulate(orient(back, 1.0)):
                    a0, a1, a2 = tri
                    area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
                    order = (a0, a1, a2) if area > 0 else (a0, a2, a1)
                    out.add([to3(*c, r) for c in order], colour)
            for k in kids:
                emit(k, r)

        for o in edge_ops:
            if o.parent is None:
                emit(o, 0.0)

    # roof, with overhang: pieces of the grown footprint, heights continue the roof plane
    grown = poly.buffer(d.overhang_m, join_style=2, mitre_limit=3.0) if d.overhang_m else poly
    if flat:
        pieces = [grown]
        roof_z = lambda x, y: eave
    else:
        local = rotate(grown, -angle, origin=(0, 0), use_radians=True)
        pieces = []
        for r in regions:
            pieces += [
                rotate(g, angle, origin=(0, 0), use_radians=True)
                for g in _polygons(local.intersection(r))
            ]

        def roof_z(x: float, y: float) -> float:
            u, v = x * ca + y * sa, -x * sa + y * ca
            return eave + height(u, v)

    for piece in pieces:
        for tri in triangulate(piece):
            v3 = [(x, y, roof_z(x, y)) for x, y in tri]
            n = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
            if n[2] < 0:
                v3 = [v3[0], v3[2], v3[1]]
            out.add(v3, roof_c)
            if d.overhang_m:  # underside, so the eaves are not see-through
                under = [(x, y, z - ROOF_THICKNESS_M) for x, y, z in reversed(v3)]
                out.add(under, SOFFIT_COLOUR)
    for dm in d.dormers:
        if flat:
            raise DetailError(f"{d.id} dormer {dm.id}: the roof is flat")
        if not 0 <= dm.edge < n_edges:
            raise DetailError(f"{d.id} dormer {dm.id}: edge {dm.edge} not in 0..{n_edges - 1}")
        _add_dormer(out, windows, d, dm, ring, roof_z, roof_c)
    for tri in triangulate(poly):
        a0, a1, a2 = tri
        area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
        order = (a0, a1, a2) if area < 0 else (a0, a2, a1)
        out.add([(x, y, 0.0) for x, y in order], wall_c)
    return out.mesh(), windows


def _add_dormer(out: _Tris, windows: list, d: Detail, dm: Dormer, ring: list, roof_z, roof_c) -> None:
    """A small gabled dormer on the roof slope above eave wall ``dm.edge``; its window is a pane tag."""
    p, q = ring[dm.edge], ring[(dm.edge + 1) % len(ring)]
    length = math.dist(p, q)
    ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
    nx, ny = uy, -ux  # outward

    def xy(s: float, depth: float) -> tuple[float, float]:
        return p[0] + ux * s - nx * depth, p[1] + uy * s - ny * depth

    def depth_at(z: float) -> float:  # inward distance at which the roof plane (at the centre) reaches z
        lo, hi = 0.0, max(np.linspace(0.0, 10.0, 201), key=lambda t: roof_z(*xy(dm.at_m, t)))  # up to the ridge
        if roof_z(*xy(dm.at_m, hi)) < z:
            raise DetailError(f"{d.id} dormer {dm.id}: {z:.2f} m is above the roof")
        for _ in range(40):
            mid = (lo + hi) / 2
            lo, hi = (mid, hi) if roof_z(*xy(dm.at_m, mid)) < z else (lo, mid)
        return hi

    def to3(s: float, z: float, depth: float) -> tuple[float, float, float]:
        return (*xy(s, depth), z)

    half = dm.width_m / 2
    s0, s1 = dm.at_m - half, dm.at_m + half
    if s0 < 0.3 or s1 > length - 0.3:
        raise DetailError(f"{d.id} dormer {dm.id}: does not fit edge {dm.edge}")
    z_top = dm.bottom_m + dm.height_m
    z_ridge = z_top + dm.rise_m
    front, back = depth_at(dm.bottom_m), depth_at(z_ridge) + 0.2
    wood, roof = hex_to_rgb(dm.colour), roof_c
    frame = hex_to_rgb(d.frame_colour)

    def foot(s: float, depth: float) -> float:  # the front/cheek foot sinks into the roof plane
        return roof_z(*xy(s, depth)) - 0.1

    zf0, zf1 = foot(s0, front), foot(s1, front)
    ww, wh = dm.window_m
    ws0, ws1 = dm.at_m - ww / 2, dm.at_m + ww / 2
    wz0 = dm.bottom_m + (dm.height_m - wh) / 2
    wz1 = wz0 + wh
    shell = [(s0, min(zf0, zf1)), (s1, min(zf0, zf1)), (s1, z_top), (s0, z_top)]
    face = orient(Polygon(shell, [[(ws0, wz0), (ws1, wz0), (ws1, wz1), (ws0, wz1)]]), 1.0)
    outside = to3(dm.at_m, dm.bottom_m, front - 3.0)
    for tri in triangulate(face):
        pts = [to3(a, b, front) for a, b in tri]
        n = np.cross(np.subtract(pts[1], pts[0]), np.subtract(pts[2], pts[0]))
        if n @ (np.asarray(outside) - pts[0]) < 0:
            pts = [pts[0], pts[2], pts[1]]
        out.add(pts, wood)
    r = 0.1  # window reveal and pane
    mid = to3(dm.at_m, (wz0 + wz1) / 2, front + r / 2)
    for (a, b), (c, e) in (((ws0, wz0), (ws1, wz0)), ((ws1, wz0), (ws1, wz1)), ((ws1, wz1), (ws0, wz1)), ((ws0, wz1), (ws0, wz0))):
        out.quad([to3(a, b, front), to3(c, e, front), to3(c, e, front + r), to3(a, b, front + r)], frame, mid)
    pane = _Tris()
    pane.quad([to3(ws0, wz0, front + r), to3(ws1, wz0, front + r), to3(ws1, wz1, front + r), to3(ws0, wz1, front + r)], GLASS, outside)
    centre = to3(dm.at_m, (wz0 + wz1) / 2, front + r)
    windows.append(WindowTag(d.id, dm.id, pane.mesh(), {
        "building": d.id, "id": dm.id, "node": f"window_{d.id}_{dm.id}", "kind": "window", "edge": dm.edge,
        "breakable": True, "confidence": "medium", "width_m": ww, "height_m": wh,
        "centre": [round(c, 3) for c in centre], "normal": [round(nx, 4), round(ny, 4)],
    }))
    # cheeks (vertical sides), foot sunk into the roof
    for s_, zf in ((s0, zf0), (s1, zf1)):
        zb = foot(s_, back)
        out.quad([to3(s_, zf, front), to3(s_, z_top, front), to3(s_, z_top, back), to3(s_, zb, back)], wood, to3(s_ + (-1 if s_ == s0 else 1), z_top, (front + back) / 2))
    # front gable triangle and the dormer roof (two slopes + overhang along the front)
    ov = 0.15
    out.add(_wound([to3(s0, z_top, front), to3(s1, z_top, front), to3(dm.at_m, z_ridge, front)], outside), wood)
    for sgn, se in ((-1, s0), (1, s1)):
        sx = se + sgn * ov
        out.quad(
            [to3(sx, z_top - 0.08, front - ov), to3(sx, z_top - 0.08, back), to3(dm.at_m, z_ridge, back), to3(dm.at_m, z_ridge, front - ov)],
            roof, to3(dm.at_m + sgn * 0.0, z_ridge + 2.0, (front + back) / 2),
        )
    out.add(_wound([to3(s0 - ov, z_top - 0.08, back), to3(s1 + ov, z_top - 0.08, back), to3(dm.at_m, z_ridge, back)], to3(dm.at_m, z_top, back + 2.0)), wood)


def _wound(tri: list, toward) -> list:
    n = np.cross(np.subtract(tri[1], tri[0]), np.subtract(tri[2], tri[0]))
    return tri if n @ (np.asarray(toward, float) - np.asarray(tri[0], float)) >= 0 else [tri[0], tri[2], tri[1]]
