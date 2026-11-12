"""Styled fences: a fence type is a panel, a column and a cap, built from the texture library.

A fence section (see ``props.make_fences``) with a ``type`` is drawn in that type's style; a section
without one stays the plain flat box of ``propmeshes.fence_mesh``. The names here match
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
"""

import math
from dataclasses import dataclass

import numpy as np

from drycastle_pipeline.buildings import hex_to_rgb
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.propmeshes import box_tris
from drycastle_pipeline.textures import TexturedBuilder, TexturedMesh, texture

_SINK_M = 0.1  # parts start this far below the ground so no gap shows on uneven terrain
_CAP_OVERHANG_M = 0.04
_CAP_HEIGHT_M = 0.06
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
    "wire": FenceStyle("sheet", "wire", "metal_post", rail=True, post_spacing_m=2.5),
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


class _Fence:
    """Accumulates the flat and textured triangles of some sections."""

    def __init__(self) -> None:
        self.tris: list = []
        self.cols: list = []
        self.tex = TexturedBuilder()
        # columns by rounded position: x, y, ground z, top z, direction, column name, fence tint, panel texture
        self.columns: dict[tuple[float, float], list] = {}

    def flat(self, tris: list, colour: list[int]) -> None:
        self.tris += tris
        self.cols += [colour] * len(tris)

    def vquad(self, tid, p, q, z0, z1, up0, height, along_p, along_q, tint, two_sided=False,
              zq: tuple[float, float] | None = None) -> None:
        """Vertical quad from p to q facing the right of p -> q; ``zq`` gives q its own (z0, z1)."""
        tex = texture(tid)
        qz0, qz1 = zq if zq is not None else (z0, z1)
        pts = [(*p, z0), (*q, qz0), (*q, qz1), (*p, z1)]
        lo, hi = tex.uv(0.0, up0, height)[1], tex.uv(0.0, height, height)[1]
        up, uq = along_p / tex.tile_w, along_q / tex.tile_w
        self.tex.quad(tid, pts, [(up, lo), (uq, lo), (uq, hi), (up, hi)], tint, two_sided)

    def column_at(self, xy, ground: float, top: float, d, name: str, tint: list[int], panel_tex: str) -> None:
        key = (round(float(xy[0]), 2), round(float(xy[1]), 2))
        have = self.columns.get(key)
        if have is None:
            self.columns[key] = [float(xy[0]), float(xy[1]), ground, top, d, name, tint, panel_tex]
        else:
            have[2], have[3] = min(have[2], ground), max(have[3], top)

    def sheet(self, s: dict, style: FenceStyle, height: float, tint: list[int], along: float) -> None:
        a, b, za, zb = s["a"], s["b"], s["za"], s["zb"]
        _, length = _unit(a, b)
        tex = texture(style.texture)
        self.vquad(style.texture, a, b, za - _SINK_M, za + height, 0.0 if tex.tile_h == 0 else -_SINK_M,
                   height, along, along + length, tint, two_sided=True, zq=(zb - _SINK_M, zb + height))
        if style.rail:
            half = _RAIL_M / 2
            self.flat(_raked_bar(a, b, (za + height - half, za + height + half),
                                 (zb + height - half, zb + height + half), _RAIL_M), _shade(tint, 0.8))

    def wall(self, s: dict, style: FenceStyle, height: float, thick: float, tint: list[int], along: float,
             ends: bool) -> float:
        """One level-topped wall section; returns the height of its top."""
        a, b = np.asarray(s["a"], float), np.asarray(s["b"], float)
        d, length = _unit(a, b)
        v = np.array([-d[1], d[0]]) * thick / 2
        z0, z1 = min(s["za"], s["zb"]) - _SINK_M, max(s["za"], s["zb"]) + height
        tid, h = style.texture, z1 - z0
        self.vquad(tid, a - v, b - v, z0, z1, 0.0, h, along, along + length, tint)
        self.vquad(tid, b + v, a + v, z0, z1, 0.0, h, -(along + length), -along, tint)
        if ends:
            self.vquad(tid, a + v, a - v, z0, z1, 0.0, h, 0.0, thick, tint)
            self.vquad(tid, b - v, b + v, z0, z1, 0.0, h, 0.0, thick, tint)
        if style.cap:
            cap = _shade(tint, 0.9) if style.cap == "tint" else hex_to_rgb(style.cap)
            self.flat(box_tris(a, b, thick + 2 * _CAP_OVERHANG_M, z1, z1 + _CAP_HEIGHT_M), cap)
            return z1 + _CAP_HEIGHT_M
        tex = texture(tid)
        ring = [a - v, b - v, b + v, a + v]
        us = [along, along + length, along + length, along]
        uvs = [(u / tex.tile_w, w / tex.tile_h) for u, w in zip(us, (0, 0, thick, thick), strict=True)]
        self.tex.quad(tid, [(*p, z1) for p in ring], uvs, tint)
        return z1

    def emit_columns(self) -> None:
        for x, y, ground, top, d, name, tint, panel_tex in self.columns.values():
            col = COLUMN_STYLES[name]
            z0, z1 = ground - _SINK_M, top + col.extra_m
            half = d * col.width_m / 2
            p0, p1 = np.array([x, y]) - half, np.array([x, y]) + half
            if col.texture is None:
                colour = hex_to_rgb(col.colour) if col.colour else _shade(tint, 0.8)
                self.flat(box_tris(p0, p1, col.width_m, z0, z1, bottom=False), colour)
                continue
            # a pier of the same material as the panel takes the fence's tint, another one its own
            ctint = tint if col.texture == panel_tex else hex_to_rgb(col.colour or "#ffffff")
            v = np.array([-d[1], d[0]]) * col.width_m / 2
            ring = [p0 - v, p1 - v, p1 + v, p0 + v]
            for k in range(4):
                self.vquad(col.texture, ring[k], ring[(k + 1) % 4], z0, z1, 0.0, z1 - z0,
                           k * col.width_m, (k + 1) * col.width_m, ctint)
            if col.cap:
                grow = d * _CAP_OVERHANG_M
                self.flat(box_tris(p0 - grow, p1 + grow, col.width_m + 2 * _CAP_OVERHANG_M, z1,
                                   z1 + _CAP_HEIGHT_M), hex_to_rgb(col.cap))
            else:
                self.flat([[(*ring[0], z1), (*ring[1], z1), (*ring[2], z1)],
                           [(*ring[0], z1), (*ring[2], z1), (*ring[3], z1)]], _shade(ctint, 0.9))


def styled_fence_meshes(sections: list[dict], cfg: dict) -> tuple[Mesh, dict[str, TexturedMesh]]:
    """Flat-colour parts and textured parts (by texture id) of the sections that have a fence type.

    A section's own ``type``, ``column``, ``height_m``, ``thickness_m`` and ``colour`` win over the
    rules (``cfg``, the ``props.fences`` section). Sections without a type (in the section or the rules)
    are not drawn here; ``propmeshes.fence_mesh`` draws them.
    """
    out = _Fence()
    prev: dict | None = None
    along = 0.0
    for s in sections:
        ftype = s.get("type", cfg.get("type"))
        if ftype is None:
            prev = None
            continue
        style = FENCE_STYLES[ftype]
        column = s.get("column", cfg.get("column")) or style.column
        height = float(s.get("height_m", cfg["height_m"]))
        thick = float(s.get("thickness_m", cfg["thickness_m"]))
        tint = hex_to_rgb(s.get("colour", cfg["colour"]))
        look = (s.get("parcel"), ftype, column, height, thick, tuple(tint))
        chained = prev is not None and prev["look"] == look and prev["b"] == s["a"]
        along = along + prev["length"] if chained else 0.0
        d, length = _unit(s["a"], s["b"])
        if style.panel == "sheet":
            out.sheet(s, style, height, tint, along)
            top_a, top_b = s["za"] + height, s["zb"] + height
        else:
            top_a = top_b = out.wall(s, style, height, thick, tint, along, ends=column == "none")
        if column != "none":
            out.column_at(s["a"], s["za"], top_a, d, column, tint, style.texture)
            out.column_at(s["b"], s["zb"], top_b, d, column, tint, style.texture)
            n = math.ceil(length / style.post_spacing_m) if style.post_spacing_m else 1
            for i in range(1, n):
                t = i / n
                xy = np.asarray(s["a"], float) * (1 - t) + np.asarray(s["b"], float) * t
                z = s["za"] * (1 - t) + s["zb"] * t
                out.column_at(xy, z, z + height, d, column, tint, style.texture)
        prev = {"look": look, "b": s["b"], "length": length}
    out.emit_columns()
    flat = Mesh(np.array(out.tris, float), np.array(out.cols, np.uint8)) if out.tris else Mesh.empty()
    return flat, out.tex.meshes()
