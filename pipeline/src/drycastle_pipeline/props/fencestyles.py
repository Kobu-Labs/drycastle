"""Styled fences: a fence type is a panel, a column and a cap, built from the texture library.

A fence section (see ``props.make_fences``) with a ``type`` is drawn in that type's style; a section
without one is a box with the ``plain`` texture (white: it shows its tint). The names here match
``edits.FENCE_TYPES`` and ``edits.COLUMN_TYPES`` (a test keeps them equal).

Parts
    panel   ``sheet``: one thin two-sided quad with a cut-out texture (picket, wire); it follows the
            ground (raked). ``wall``: a box with a repeating texture on both faces; its top is level,
            so on a slope the sections step and the courses stay horizontal.
    column  a post or pier at every section joint and run end (and every ``post_spacing_m`` inside a
            long section). Flat-colour posts (wood, metal) or textured piers (brick, stone, concrete),
            each pier with a flat cap slab. Chosen by the type, or by the section's ``column``.
    cap     a flat coping slab on top of a wall; ``rail``: a flat bar along the top of a sheet.

Colour: the section colour is a tint. Tintable textures (picket, wire, concrete) are near-white, so
the tint is the paint; brick and stone carry their own colours and default to a white tint.

Along a run of connected sections u continues, so the pattern does not restart at each section.

Pieces (docs/fence-destruction.md): a thin section (picket, wire, plain box) breaks in equal strips along it. The pieces of a
picket section are one board each, their edges in the middle of the gaps between boards (the run-continuous u keeps them on
the drawn boards, so the first piece may start before the section); wire 0.5 m, plain 0.25 m, uniform from the section's
start. A section with an ``index`` (its position in fences.json) gets TEXCOORD_1 = (index + 0.5, (metres from a - piece0_m) /
piece_m) on every vertex of its panel or box; ``section_docs`` gives the fences.json keys. A wall section (brick, stone,
concrete) is drawn apart by ``wall_parts`` (own meshes, own piers) for the house-destruction nodes and grid (``fencewalls``).
"""

import math
from dataclasses import dataclass, field

import numpy as np

from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.core.textures import TexturedBuilder, TexturedMesh, texture
from drycastle_pipeline.props.propmeshes import box_tris

PLAIN_TEXTURE = "plain"  # the library texture of the box of a section without a type
MAX_PIECES = 64
PLAIN_PIECE_M, WIRE_PIECE_M = 0.25, 0.5  # width of a piece of a plain / wire section (a picket section: one board)
PICKETS_PER_TILE = 4
PICKET_GAP_MID = (0.7 + 1.0) / 2  # of a picket pitch: the middle of the gap behind a board (scripts/art/make_textures.py: a board is 0.7 of its pitch)
SINK_M = 0.1  # parts start this far below the ground so no gap shows on uneven terrain
CAP_OVERHANG_M = 0.04
CAP_HEIGHT_M = 0.06
_RAIL_M = 0.04


@dataclass(frozen=True)
class ColumnStyle:
    width_m: float
    extra_m: float  # how far the column stands above the panel
    texture: str | None = None  # None = flat colour
    colour: str | None = None  # flat colour or tint; None = the fence's tint (darkened when flat)
    cap: str | None = None  # flat colour of the cap slab; None = no cap


@dataclass(frozen=True)
class FenceStyle:
    panel: str  # "sheet" | "wall"
    texture: str
    column: str
    cap: str | None = None  # wall coping: a flat colour, or "tint" for a shade of the fence colour
    rail: bool = False  # sheet: a flat bar along the top
    post_spacing_m: float | None = None  # extra columns inside sections longer than this


COLUMN_STYLES: dict[str, ColumnStyle] = {
    "wood_post": ColumnStyle(0.10, 0.04),
    "metal_post": ColumnStyle(0.06, 0.06),
    "brick_red": ColumnStyle(0.44, 0.14, texture="brick_red", colour="#ffffff", cap="#b9b3a7"),
    "brick_white": ColumnStyle(0.44, 0.14, texture="brick_white", colour="#ffffff", cap="#a9a397"),
    "stone": ColumnStyle(0.50, 0.12, texture="stone", colour="#ffffff", cap="#77726a"),
    "concrete": ColumnStyle(0.30, 0.10, texture="concrete", colour="#cfcac0", cap="#b4afa5"),
}

FENCE_STYLES: dict[str, FenceStyle] = {
    "picket": FenceStyle("sheet", "picket", "wood_post", post_spacing_m=2.0),
    "wire": FenceStyle("sheet", "wire", "metal_post", post_spacing_m=2.5),
    "brick_red": FenceStyle("wall", "brick_red", "brick_red", cap="#b9b3a7"),
    "brick_white": FenceStyle("wall", "brick_white", "brick_white", cap="#a9a397"),
    "stone_wall": FenceStyle("wall", "stone", "stone", cap="#77726a"),
    "concrete": FenceStyle("wall", "concrete", "concrete", cap="tint"),
}
FENCE_STYLES["brick_wall"] = FENCE_STYLES["brick_red"]  # the old name of brick_red


def _shade(rgb: list[int], k: float) -> list[int]:
    return [max(0, min(255, round(c * k))) for c in rgb]


def _unit(a, b) -> tuple[np.ndarray, float]:
    d = np.asarray(b, float) - np.asarray(a, float)
    length = float(np.linalg.norm(d))
    return d / max(length, 1e-12), length


def _raked_bar(a, b, za: tuple[float, float], zb: tuple[float, float], width: float) -> list:
    """A bar from a to b whose bottom and top follow (lo, hi) heights at each end; no end faces."""
    d, _ = _unit(a, b)
    v = np.array([-d[1], d[0]]) * width / 2
    a, b = np.asarray(a, float), np.asarray(b, float)
    r0, r1, l1, l0 = a - v, b - v, b + v, a + v  # right side then left side
    tris = []
    for p, q, zp, zq in ((r0, r1, za, zb), (l1, l0, zb, za)):  # the two long sides
        tris.append([(*p, zp[0]), (*q, zq[0]), (*q, zq[1])])
        tris.append([(*p, zp[0]), (*q, zq[1]), (*p, zp[1])])
    top = [(*r0, za[1]), (*r1, zb[1]), (*l1, zb[1]), (*l0, za[1])]
    low = [(*r0, za[0]), (*r1, zb[0]), (*l1, zb[0]), (*l0, za[0])]
    tris += [[top[0], top[1], top[2]], [top[0], top[2], top[3]]]
    tris += [[low[0], low[2], low[1]], [low[0], low[3], low[2]]]
    return tris


@dataclass(frozen=True)
class Resolved:
    """A section with the rules applied: what it is drawn as (``kind``: plain, picket, wire or wall)."""

    ftype: str | None
    kind: str
    style: FenceStyle | None
    column: str | None
    height: float
    thick: float
    tint: tuple[int, ...]

    @property
    def colour(self) -> str:
        return "#{:02x}{:02x}{:02x}".format(*self.tint)

    @property
    def texture(self) -> str:
        """The library texture of the panel (``plain`` for a section without a type)."""
        return self.style.texture if self.style else PLAIN_TEXTURE


def resolve(s: dict, cfg: dict) -> Resolved:
    """The effective type, column, height, thickness and tint of a section: its own values win over the rules (``cfg``)."""
    ftype = s.get("type", cfg.get("type"))
    style = FENCE_STYLES[ftype] if ftype is not None else None
    column = (s.get("column", cfg.get("column")) or style.column) if style else None
    kind = "plain" if style is None else style.texture if style.panel == "sheet" else "wall"
    return Resolved(ftype, kind, style, column, float(s.get("height_m", cfg["height_m"])),
                    float(s.get("thickness_m", cfg["thickness_m"])), tuple(hex_to_rgb(s.get("colour", cfg["colour"]))))


def runs(sections: list[dict], cfg: dict) -> list[tuple[dict, Resolved, float]]:
    """Every section with its resolved style and ``along``: the metres of its run before its first end, so u (and the
    pickets) continue over connected sections of one look instead of restarting at each."""
    out, prev, along = [], None, 0.0
    for s in sections:
        r = resolve(s, cfg)
        look = (s.get("parcel"), r.ftype, r.column, r.height, r.thick, r.tint)
        chained = prev is not None and prev["look"] == look and prev["b"] == s["a"]
        along = along + prev["length"] if chained else 0.0
        out.append((s, r, along))
        prev = {"look": look, "b": s["b"], "length": _unit(s["a"], s["b"])[1]}
    return out


def _floor6(x: float) -> float:
    return math.floor(x * 1e6) / 1e6


def _piece_layout(r: Resolved, length: float, along: float) -> tuple[int, float, float]:
    """(pieces, piece_m, piece0_m) of a thin section (fences.json): see the module docstring."""
    if r.kind == "picket":
        pitch = texture("picket").tile_w / PICKETS_PER_TILE
        k = math.floor(along / pitch - PICKET_GAP_MID + 1e-9)  # the last edge at or before the section's start
        piece0, piece_m = _floor6((k + PICKET_GAP_MID) * pitch - along), pitch
        n = math.ceil((length - piece0) / piece_m - 1e-9)
    else:
        n = max(1, math.ceil(length / (WIRE_PIECE_M if r.kind == "wire" else PLAIN_PIECE_M) - 1e-9))
        piece0, piece_m = 0.0, math.ceil(length / n * 1e6) / 1e6  # rounded up: the pieces cover the whole section
    if n > MAX_PIECES:
        raise ValueError(f"fence section of {length:.2f} m has {n} pieces (at most {MAX_PIECES})")
    return n, piece_m, piece0


def _v_range(tex, height: float) -> tuple[float, float]:
    """The v of the bottom and the top of a sheet panel (and of the plain box)."""
    return tex.uv(0.0, 0.0 if tex.tile_h == 0 else -SINK_M, height)[1], tex.uv(0.0, height, height)[1]


def section_docs(sections: list[dict], cfg: dict) -> dict[int, dict]:
    """The fences.json keys that the drawing decides (kind, thickness_m, colour and, for a thin section, pieces, piece_m,
    piece0_m, uv and posts), by section ``index``; sections without an ``index`` are left out. Give the very list the meshes are
    built from: along (and so the pickets, and which section a joint's post breaks with) depends on its neighbours.
    posts: the posts that break with the section, [metres along from a, width_m, z0, z1, "#rrggbb"] each (map z)."""
    out = {}
    posts = _build(sections, cfg, False)[2]
    for s, r, along in runs(sections, cfg):
        if "index" not in s:
            continue
        doc = {"kind": r.kind, "thickness_m": r.thick, "colour": r.colour}
        if r.kind != "wall":
            length = _unit(s["a"], s["b"])[1]
            tex = texture(r.texture)
            lo, hi = _v_range(tex, r.height)
            pieces, piece_m, piece0 = _piece_layout(r, length, along)
            doc |= {"pieces": pieces, "piece_m": piece_m, "piece0_m": piece0,
                    "uv": [round(along / tex.tile_w, 6), round(1 / tex.tile_w, 6), round(lo, 6), round(hi, 6)]}
            if s["index"] in posts:
                d = _unit(s["a"], s["b"])[0]
                doc["posts"] = [[round(float((p.x - s["a"][0]) * d[0] + (p.y - s["a"][1]) * d[1]), 4), p.col.width_m, round(p.z0, 4),
                                 round(p.z1, 4), "#{:02x}{:02x}{:02x}".format(*p.tint)] for p in posts[s["index"]]]
        out[s["index"]] = doc
    return out


class _Pieces:
    """TEXCOORD_1 of a thin section's vertices: (index + 0.5, piece coordinate), the piece being the floor of the second."""

    def __init__(self, index: int, a, d, piece0: float, piece_m: float, count: int) -> None:
        self.index, self.a, self.d, self.piece0, self.piece_m, self.count = index, np.asarray(a, float), d, piece0, piece_m, count

    def __call__(self, p) -> tuple[float, float]:
        return self.index + 0.5, (float((p[0] - self.a[0]) * self.d[0] + (p[1] - self.a[1]) * self.d[1]) - self.piece0) / self.piece_m


def _pieces(s: dict, r: Resolved, along: float) -> _Pieces | None:
    if "index" not in s or r.kind == "wall":
        return None
    d, length = _unit(s["a"], s["b"])
    n, piece_m, piece0 = _piece_layout(r, length, along)
    return _Pieces(s["index"], s["a"], d, piece0, piece_m, n)


@dataclass(frozen=True)
class Pier:
    """A column as built: a square of ``col.width_m`` round (x, y), from z0 to z1, aligned with direction ``d``; ``tint`` its
    tint (textured) or flat colour."""

    x: float
    y: float
    z0: float
    z1: float
    d: np.ndarray
    col: ColumnStyle
    tint: tuple[int, ...]
    thin: bool = False  # a thin section's post (flat colour): drawn with the plain texture, so it can break
    uv2: tuple[float, float] | None = None  # such a post of an indexed section: TEXCOORD_1 of the piece it stands in (it breaks with it)


@dataclass
class _Column:
    x: float
    y: float
    ground: float
    top: float
    d: np.ndarray
    name: str
    tint: tuple[int, ...]
    panel_tex: str
    owner: int | None  # the part it is drawn into: the section that first met it (walls drawn apart), else the shared one
    thin: bool = False  # first met by a thin section
    breaks: tuple[int, int] | None = None  # first met by a thin section with an index: (that index, the piece it stands in)

    def pier(self) -> Pier:
        col = COLUMN_STYLES[self.name]
        if col.texture is None:  # a post: its own flat colour, else a shade of the fence's
            tint = tuple(hex_to_rgb(col.colour)) if col.colour else tuple(_shade(list(self.tint), 0.8))
        else:  # a pier of the same material as the panel takes the fence's tint, another one its own
            tint = self.tint if col.texture == self.panel_tex else tuple(hex_to_rgb(col.colour or "#ffffff"))
        thin = self.thin and col.texture is None
        uv2 = (self.breaks[0] + 0.5, self.breaks[1] + 0.5) if thin and self.breaks else None
        return Pier(self.x, self.y, self.ground - SINK_M, self.top + col.extra_m, self.d, col, tint, thin, uv2)


@dataclass
class WallSpec:
    """A wall section as built (for its collision and its voxel grid, ``fencewalls``): geometry, look and the piers it owns."""

    index: int
    a: tuple[float, float]
    b: tuple[float, float]
    za: float
    zb: float
    r: Resolved
    cap: tuple[int, ...] | None  # flat colour of its coping, None: no cap
    top: float  # level top of the wall (under the cap)
    piers: list[Pier] = field(default_factory=list)


@dataclass
class WallPart:
    """One wall section's drawing: the flat parts (caps, flat posts), the textured faces and its spec."""

    flat: Mesh
    textured: dict[str, TexturedMesh]
    spec: WallSpec


class _Fence:
    """Accumulates the flat and textured triangles of some sections."""

    def __init__(self) -> None:
        self.tris: list = []
        self.cols: list = []
        self.tex = TexturedBuilder()

    def flat(self, tris: list, colour) -> None:
        self.tris += tris
        self.cols += [colour] * len(tris)

    def vquad(self, tid, p, q, z0, z1, up0, height, along_p, along_q, tint, two_sided=False,
              zq: tuple[float, float] | None = None, pieces: _Pieces | None = None) -> None:
        """Vertical quad from p to q facing the right of p -> q; ``zq`` gives q its own (z0, z1)."""
        tex = texture(tid)
        qz0, qz1 = zq if zq is not None else (z0, z1)
        pts = [(*p, z0), (*q, qz0), (*q, qz1), (*p, z1)]
        lo, hi = tex.uv(0.0, up0, height)[1], tex.uv(0.0, height, height)[1]
        up, uq = along_p / tex.tile_w, along_q / tex.tile_w
        self.tex.quad(tid, pts, [(up, lo), (uq, lo), (uq, hi), (up, hi)], tint, two_sided,
                      uv2=[pieces(pt) for pt in pts] if pieces else None)

    def sheet(self, s: dict, r: Resolved, along: float, pieces: _Pieces | None) -> None:
        a, b, za, zb = s["a"], s["b"], s["za"], s["zb"]
        _, length = _unit(a, b)
        tex = texture(r.texture)
        self.vquad(r.texture, a, b, za - SINK_M, za + r.height, 0.0 if tex.tile_h == 0 else -SINK_M,
                   r.height, along, along + length, r.tint, two_sided=True, zq=(zb - SINK_M, zb + r.height), pieces=pieces)
        if r.style.rail:
            half = _RAIL_M / 2
            self.flat(_raked_bar(a, b, (za + r.height - half, za + r.height + half),
                                 (zb + r.height - half, zb + r.height + half), _RAIL_M), _shade(list(r.tint), 0.8))

    def plain(self, s: dict, r: Resolved, along: float, pieces: _Pieces | None) -> None:
        """The box of a section without a type: the faces of ``propmeshes.fence_mesh`` (no bottom), textured with the plain texture."""
        a, b = np.asarray(s["a"], float), np.asarray(s["b"], float)
        d, _ = _unit(a, b)
        v = np.array([-d[1], d[0]]) * r.thick / 2
        z0, z1 = min(s["za"], s["zb"]) - SINK_M, max(s["za"], s["zb"]) + r.height
        tex = texture(PLAIN_TEXTURE)
        lo, hi = _v_range(tex, r.height)
        ring = [a - v, b - v, b + v, a + v]

        def u(p) -> float:
            return (along + float(np.dot(p - a, d))) / tex.tile_w

        def add(pts, vs) -> None:
            self.tex.quad(PLAIN_TEXTURE, pts, [(u(np.array(p[:2])), w) for p, w in zip(pts, vs, strict=True)], r.tint,
                          uv2=[pieces(p) for p in pts] if pieces else None)

        for k in range(4):
            p, q = ring[k], ring[(k + 1) % 4]
            add([(*p, z0), (*q, z0), (*q, z1), (*p, z1)], (lo, lo, hi, hi))
        add([(*p, z1) for p in ring], (lo, lo, hi, hi))

    def wall(self, s: dict, r: Resolved, along: float, ends: bool) -> tuple[float, tuple[int, ...] | None]:
        """One level-topped wall section; returns the height of its top (cap included) and the cap colour."""
        a, b = np.asarray(s["a"], float), np.asarray(s["b"], float)
        d, length = _unit(a, b)
        v = np.array([-d[1], d[0]]) * r.thick / 2
        z0, z1 = min(s["za"], s["zb"]) - SINK_M, max(s["za"], s["zb"]) + r.height
        tid, h = r.style.texture, z1 - z0
        self.vquad(tid, a - v, b - v, z0, z1, 0.0, h, along, along + length, r.tint)
        self.vquad(tid, b + v, a + v, z0, z1, 0.0, h, -(along + length), -along, r.tint)
        if ends:
            self.vquad(tid, a + v, a - v, z0, z1, 0.0, h, 0.0, r.thick, r.tint)
            self.vquad(tid, b - v, b + v, z0, z1, 0.0, h, 0.0, r.thick, r.tint)
        if r.style.cap:
            cap = _cap_colour(r.style, r.tint)
            self.flat(box_tris(a, b, r.thick + 2 * CAP_OVERHANG_M, z1, z1 + CAP_HEIGHT_M), cap)
            return z1 + CAP_HEIGHT_M, cap
        tex = texture(tid)
        ring = [a - v, b - v, b + v, a + v]
        us = [along, along + length, along + length, along]
        uvs = [(u / tex.tile_w, w / tex.tile_h) for u, w in zip(us, (0, 0, r.thick, r.thick), strict=True)]
        self.tex.quad(tid, [(*p, z1) for p in ring], uvs, r.tint)
        return z1, None

    def pier(self, p: Pier) -> None:
        col = p.col
        half = p.d * col.width_m / 2
        p0, p1 = np.array([p.x, p.y]) - half, np.array([p.x, p.y]) + half
        v = np.array([-p.d[1], p.d[0]]) * col.width_m / 2
        if p.thin:  # a thin fence's post: the plain texture (white: its tint), with TEXCOORD_1 hidden with its piece
            ring = [p0 - v, p1 - v, p1 + v, p0 + v]
            uvs = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]
            uv2 = [p.uv2] * 4 if p.uv2 else None
            for k in range(4):
                a, b = ring[k], ring[(k + 1) % 4]
                self.tex.quad(PLAIN_TEXTURE, [(*a, p.z0), (*b, p.z0), (*b, p.z1), (*a, p.z1)], uvs, p.tint, uv2=uv2)
            self.tex.quad(PLAIN_TEXTURE, [(*q, p.z1) for q in ring], uvs, p.tint, uv2=uv2)
            return
        if col.texture is None:
            self.flat(box_tris(p0, p1, col.width_m, p.z0, p.z1, bottom=False), p.tint)
            return
        ring = [p0 - v, p1 - v, p1 + v, p0 + v]
        for k in range(4):
            self.vquad(col.texture, ring[k], ring[(k + 1) % 4], p.z0, p.z1, 0.0, p.z1 - p.z0,
                       k * col.width_m, (k + 1) * col.width_m, p.tint)
        if col.cap:
            grow = p.d * CAP_OVERHANG_M
            self.flat(box_tris(p0 - grow, p1 + grow, col.width_m + 2 * CAP_OVERHANG_M, p.z1,
                               p.z1 + CAP_HEIGHT_M), hex_to_rgb(col.cap))
        else:
            self.flat([[(*ring[0], p.z1), (*ring[1], p.z1), (*ring[2], p.z1)],
                       [(*ring[0], p.z1), (*ring[2], p.z1), (*ring[3], p.z1)]], _shade(list(p.tint), 0.9))

    def meshes(self) -> tuple[Mesh, dict[str, TexturedMesh]]:
        flat = Mesh(np.array(self.tris, float), np.array(self.cols, np.uint8)) if self.tris else Mesh.empty()
        return flat, self.tex.meshes()


def _cap_colour(style: FenceStyle, tint) -> tuple[int, ...]:
    return tuple(_shade(list(tint), 0.9)) if style.cap == "tint" else tuple(hex_to_rgb(style.cap))


def _build(sections: list[dict], cfg: dict, apart: bool) -> tuple[dict[int | None, _Fence], dict[int, WallSpec], dict[int, list[Pier]]]:
    """The parts (key None: the shared one), the wall specs by index and the breakable posts (flat posts first met by a thin
    section with an index) by that index. ``apart``: only the walls are built, each its own part
    with the piers it owns; else everything goes into the shared part."""
    parts: dict[int | None, _Fence] = {None: _Fence()}
    specs: dict[int, WallSpec] = {}
    columns: dict[tuple[float, float], _Column] = {}  # by rounded position: a joint's column is made once, the first section wins
    posts: dict[int, list[Pier]] = {}  # the breakable posts by the index of the section they break with

    def column_at(owner, xy, ground, top, d, name, tint, panel_tex, pieces=None, thin=False) -> None:
        key = (round(float(xy[0]), 2), round(float(xy[1]), 2))
        have = columns.get(key)
        if have is None:
            breaks = None if pieces is None else (pieces.index, min(max(math.floor(pieces(xy)[1]), 0), pieces.count - 1))
            columns[key] = _Column(float(xy[0]), float(xy[1]), ground, top, d, name, tint, panel_tex, owner, thin, breaks)
        else:
            have.ground, have.top = min(have.ground, ground), max(have.top, top)

    for s, r, along in runs(sections, cfg):
        wall = r.kind == "wall"
        if apart and not wall:
            continue
        owner = s["index"] if apart else None
        part = parts.setdefault(owner, _Fence())
        d, length = _unit(s["a"], s["b"])
        if wall:
            top, cap = part.wall(s, r, along, ends=r.column == "none")
            top_a = top_b = top
            if apart:
                specs[owner] = WallSpec(owner, tuple(s["a"]), tuple(s["b"]), s["za"], s["zb"], r, cap, max(s["za"], s["zb"]) + r.height)
        elif r.kind == "plain":
            part.plain(s, r, along, _pieces(s, r, along))  # a box, no posts
            continue
        pieces = None
        if not wall:
            pieces = _pieces(s, r, along)
            part.sheet(s, r, along, pieces)
            top_a, top_b = s["za"] + r.height, s["zb"] + r.height
        if r.column != "none":
            column_at(owner, s["a"], s["za"], top_a, d, r.column, r.tint, r.style.texture, pieces, not wall)
            column_at(owner, s["b"], s["zb"], top_b, d, r.column, r.tint, r.style.texture, pieces, not wall)
            n = math.ceil(length / r.style.post_spacing_m) if r.style.post_spacing_m else 1
            for i in range(1, n):
                t = i / n
                xy = np.asarray(s["a"], float) * (1 - t) + np.asarray(s["b"], float) * t
                z = s["za"] * (1 - t) + s["zb"] * t
                column_at(owner, xy, z, z + r.height, d, r.column, r.tint, r.style.texture, pieces, not wall)
    for c in columns.values():
        pier = c.pier()
        parts[c.owner].pier(pier)
        if apart:
            specs[c.owner].piers.append(pier)
        if pier.uv2 is not None:
            posts.setdefault(c.breaks[0], []).append(pier)
    return parts, specs, posts


def styled_fence_meshes(sections: list[dict], cfg: dict) -> tuple[Mesh, dict[str, TexturedMesh]]:
    """Flat-colour parts and textured parts (by texture id) of the sections: panels, boxes of the untyped ones (the plain
    texture), columns and caps.

    A section's own ``type``, ``column``, ``height_m``, ``thickness_m`` and ``colour`` win over the rules (``cfg``, the
    ``props.fences`` section). A section with an ``index`` that is thin (not a wall) also gets TEXCOORD_1 (``uv2``):
    (index + 0.5, the piece coordinate of ``section_docs``). Walls are drawn here too, all in these two results: for
    the walls of a map use ``wall_parts``, which keeps them apart.
    """
    return _build(sections, cfg, False)[0][None].meshes()


def wall_parts(sections: list[dict], cfg: dict) -> dict[int, WallPart]:
    """The wall sections (every one with an ``index``; the others are skipped) each drawn apart, by index: its flat and textured
    parts and its spec. A pier at a joint of connected walls belongs to the first of them (the lowest index when
    ``sections`` is in index order) and is drawn and built once."""
    parts, specs, _ = _build(sections, cfg, True)
    return {i: WallPart(*parts[i].meshes(), spec) for i, spec in sorted(specs.items())}
