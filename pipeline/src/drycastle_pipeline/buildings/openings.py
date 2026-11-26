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
        frame_m: 0.06          # optional, windows/doors/garage doors: the opening's own frame, bars this wide of
                               # frame_colour all round the pane, 0.02 m proud of it (default 0 = none)
        posts: [0.5]           # optional, needs frame_m: upright bars at these fractions of the opening's width
        rails: [0.6]           # optional, needs frame_m: level bars at these fractions of its height
        surround_m: 0.12       # optional: a flat band this wide (0..0.4) on the wall round the opening, 0.025 m proud,
                               # in the building's surround_colour (default the frame default)
        sill: true             # optional, windows only: a sill under it, 0.05 m thick, 0.05 m proud of the wall,
                               # 0.06 m wider each side, in frame_colour (default false)

Optional whole-building parts (all absent = the plain look)::

    plinth: {height_m: 0.3, colour: "#6b5a50"}   # a band round the foot of every wall, 0.03 m proud, up to height_m
                                                 # (0.05..1.5); openings whose sill is below it cut through it
    eaves: {colour: "#3a3230", fascia_m: 0.18, gutter: true, level_soffit: false}
                                                 # needs overhang_m > 0: a fascia board fascia_m (0.05..0.6) high
                                                 # closes the rim of the overhang all round; gutter: true adds a
                                                 # 0.12 x 0.10 m gutter along every eave (the level lower edges),
                                                 # in gutter_colour (optional; default the eaves colour)
    downpipes:                                   # a 0.09 m square pipe 0.02 m off the wall, ground to the underside
      - {edge: 0, at_m: 0.1, colour: "#3a3230"}  # of the overhang (or the wall top); at_m = its left side along the
                                                 # edge; colour optional (eaves colour, else #4a4a4a)
    chimneys:                                    # a box in map coordinates, sides along the ridge, from 0.3 m below
      - {x: 123.4, y: -60.2, width_m: 0.5,       # the roof up to top_m above the ground; width_m runs along the
         depth_m: 0.5, top_m: 8.2, colour: "#b0a090"}   # ridge, both 0.2..2.0; colour optional (the wall colour)

More optional parts (the trim; all absent = none)::

    interior: true                               # the house is hollow (buildings/interior.py, ADR 0040): walls 0.3 m thick
                                                 # inward of the footprint, a floor at the lowest door's sill, a ceiling
                                                 # a storey above; windows and doors between them go through the wall
    trim_colour: "#f2f0ea"                       # bands and corner strips (default the frame default)
    bands:                                       # level bands round every wall, 0.04 m proud, cut by openings they
      - {top_m: 3.2, height_m: 0.18}             # cross and by the wall's own top: a cornice under the eave, a string
                                                 # course between two floors
    corners: {width_m: 0.3, top_m: 3.0}          # upright strips this wide at both walls of every outside corner,
                                                 # 0.035 m proud, from the plinth (or the ground) up to top_m
    gable_colour: "#f2f0ea"                      # the walls of a gable above gable_from_m (default the eave height)
    gable_from_m: 3.2
    ridge_caps: true                             # a row of cap tiles along the ridge and the hips, darker than the roof
    balconies:                                   # a slab with a railing on wall `edge`: {edge, at_m, width_m, floor_m,
                                                 # depth_m 1.1, rail_m 1.0, colour}
    skylights:                                   # a roof window on the slope above eave wall `edge`: its middle at_m
      - {edge: 2, at_m: 3.0, up_m: 1.2}          # along the wall, its lower edge up_m in from the wall (level
                                                 # distance); width_m 0.78 and height_m 1.18 (along the slope) optional

    roof_tiles: true                             # the roof laid in courses 0.34 m wide, each a 3 cm step proud of the one
                                                 # above and a shade apart (slopes flatter than 10 degrees stay plain)
    bargeboard_colour: "#5a4030"                 # needs eaves: the boards along a gable's sloping edges in this colour,
                                                 # 0.22 m high (default: the eaves colour, as high as the fascia)
    apron: {width_m: 0.6, colour: "#8f8c85", joints_m: 0.5}  # (joints_m optional: slabs, a dark joint every so much) a paved strip round the foot of the walls, 4 cm above the ground; its
                                                 # outer face goes 0.8 m down, a kerb where the ground falls away

Openings take, besides the above: ``lintel_m`` (0..0.4: a box this high over the head, 0.05 m proud, in frame_colour:
a roller-shutter box), and doors and garage doors ``panels`` (0..8 rows of raised panels on the leaf), doors
``steps`` (0..6 steps in front, up to the door's sill_m; with sill_m 0 a single low slab) and ``canopy_m`` (0..1.5: a
small sloping roof this deep over the door, on two brackets, in the roof colour). A chimney takes ``cap: true`` (a
dark slab on top), a downpipe ``bends: true`` (a kick-out at its foot, and a slanted piece out to the gutter).

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
from dataclasses import dataclass, field, replace
from itertools import pairwise
from pathlib import Path

import numpy as np
import yaml
from shapely.affinity import rotate
from shapely.geometry import Point, Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from drycastle_pipeline.buildings.footprint import footprint_polygon, roof_frame
from drycastle_pipeline.buildings.meshes import _roof_regions, _split_edge
from drycastle_pipeline.core.colour import hex_to_rgb
from drycastle_pipeline.core.config import PIPELINE_DIR
from drycastle_pipeline.core.mesh import Mesh, triangulate
from drycastle_pipeline.core.mesh import solid_polygons as _polygons

DETAILS_DIR = PIPELINE_DIR / "config" / "buildings"
KINDS = ("window", "door", "garage_door", "recess")
CONFIDENCE = ("low", "medium", "high")
GLASS = (150, 190, 215)
DEFAULT_FRAME = "#f2f0ea"
DEFAULT_PANEL = {"door": "#5a4636", "garage_door": "#6b5a48", "recess": "#3a2c22"}
SOFFIT_COLOUR = (240, 236, 226)
ROOF_THICKNESS_M = 0.12
DEFAULT_PLINTH = "#6b5a50"
DEFAULT_EAVES = "#3a3230"
DEFAULT_PIPE = "#4a4a4a"
PLINTH_PROUD_M = 0.03
FRAME_PROUD_M = 0.02  # a window frame stands this far in front of its pane
SILL_THICK_M, SILL_PROUD_M, SILL_EXTRA_M = 0.05, 0.05, 0.06
GUTTER_W_M, GUTTER_H_M = 0.12, 0.10
SURROUND_PROUD_M = 0.025
RAFTER_W_M, RAFTER_H_M, RAFTER_STEP_M = 0.08, 0.12, 0.9  # rafter tails under an open eave
TILE_COURSE_M, TILE_LIFT_M, TILE_MIN_SLOPE = 0.34, 0.03, 0.18  # a roof's courses: width along the slope, step, least slope
BARGE_M = 0.22  # a bargeboard's height
APRON_TOP_M, APRON_DEEP_M = 0.04, 0.8
APRON_JOINT_LIFT_M = 0.008
GUTTER_LEVEL_M = 0.06  # an eave may be this far off level end to end and still carry a gutter
PIPE_M, PIPE_OFF_M = 0.09, 0.02
CHIMNEY_BELOW_M = 0.3
BAND_PROUD_M, CORNER_PROUD_M, LINTEL_PROUD_M, PANEL_PROUD_M = 0.04, 0.035, 0.05, 0.015
STEP_TREAD_M, STEP_SIDE_M, DOORSTEP_M = 0.28, 0.15, 0.06
DEFAULT_STEP = "#9a958c"
CAP_COLOUR = (88, 86, 84)
SKYLIGHT_GLASS, SKYLIGHT_FRAME = (62, 78, 94), (58, 56, 54)
RIDGE_CAP_W_M, RIDGE_CAP_H_M = 0.22, 0.07


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
    frame_m: float = 0.0  # own frame (windows, doors, garage doors): bars this wide round the pane
    posts: list = field(default_factory=list)  # upright bars at these fractions of the width
    rails: list = field(default_factory=list)  # level bars at these fractions of the height
    sill: bool = False  # windows only: a sill under the opening
    surround_m: float = 0.0  # a flat band this wide on the wall round the opening, in the building's surround_colour
    lintel_m: float = 0.0  # a box this high over the head (a roller-shutter box), in frame_colour
    panels: int = 0  # doors, garage doors: rows of raised panels on the leaf
    steps: int = 0  # doors: steps in front, up to sill_m
    canopy_m: float = 0.0  # doors: a small sloping roof this deep over it


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
class Plinth:
    height_m: float = 0.3
    colour: str = DEFAULT_PLINTH


@dataclass
class Eaves:
    colour: str = DEFAULT_EAVES
    fascia_m: float = 0.18
    gutter: bool = False
    level_soffit: bool = False
    gutter_colour: str | None = None  # default: the eaves colour
    rafters: bool = False  # rafter tails showing under the overhang along every eave (an open eave; not with level_soffit)


@dataclass
class Downpipe:
    edge: int
    at_m: float
    colour: str | None = None  # default: the eaves colour, else DEFAULT_PIPE
    bends: bool = False  # a kick-out at the foot and a slanted piece from the wall out to the gutter


@dataclass
class Chimney:
    x: float
    y: float
    top_m: float
    width_m: float = 0.5  # along the ridge
    depth_m: float = 0.5  # across it
    colour: str | None = None  # default: the wall colour
    cap: bool = False  # a dark slab on top


@dataclass
class Band:
    top_m: float
    height_m: float = 0.15


@dataclass
class Corners:
    width_m: float = 0.3
    top_m: float = 3.0


@dataclass
class Skylight:
    edge: int
    at_m: float
    up_m: float
    width_m: float = 0.78
    height_m: float = 1.18


@dataclass
class Balcony:
    edge: int
    at_m: float
    width_m: float
    floor_m: float
    depth_m: float = 1.1
    rail_m: float = 1.0
    colour: str | None = None  # the railing's; the frames' colour when not given


@dataclass
class Detail:
    id: str
    overhang_m: float = 0.0
    frame_colour: str = DEFAULT_FRAME
    surround_colour: str = DEFAULT_FRAME
    source: str = ""
    openings: list[Opening] = field(default_factory=list)
    dormers: list[Dormer] = field(default_factory=list)
    plinth: Plinth | None = None
    eaves: Eaves | None = None
    downpipes: list[Downpipe] = field(default_factory=list)
    chimneys: list[Chimney] = field(default_factory=list)
    trim_colour: str = DEFAULT_FRAME
    bands: list[Band] = field(default_factory=list)
    corners: Corners | None = None
    gable_colour: str | None = None
    gable_from_m: float | None = None
    ridge_caps: bool = False
    skylights: list[Skylight] = field(default_factory=list)
    balconies: list[Balcony] = field(default_factory=list)
    roof_tiles: bool = False  # the roof laid in courses: each a step proud of the one above, a shade apart
    bargeboard_colour: str | None = None  # the boards along a gable's sloping edges in this colour, wider than the fascia
    apron: tuple[float, str, float] | None = None  # (width_m, colour, joints_m): a strip round the foot of the house, its edge a kerb
    interior: bool = False  # the house is hollow: walls with a width, floor, ceiling, openings that go through (buildings/interior.py)


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


def _num(value, where: str, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise DetailError(f"{where}: {name} must be a number, got {value!r}")
    return float(value)


def _block(raw, where: str, name: str, cls, required: tuple = ()):
    """A small mapping of the detail file (plinth, a chimney, ...) checked against the fields of ``cls``."""
    w = f"{where} {name}"
    if not isinstance(raw, dict):
        raise DetailError(f"{w}: needs a mapping")
    missing = set(required) - set(raw)
    extra = set(raw) - set(cls.__dataclass_fields__)
    if missing or extra:
        raise DetailError(f"{w}: missing {sorted(missing)}, unknown {sorted(extra)}")
    return w


def _fractions(values, where: str, name: str, extent: float, bar: float) -> list[float]:
    """Positions of posts/rails as fractions of ``extent``: strictly inside, clear of the frame and of each other."""
    if not isinstance(values, (list, tuple)):
        raise DetailError(f"{where}: {name} must be a list of fractions")
    out = sorted(_num(v, where, name) for v in values)
    for v in out:
        if not 0.0 < v < 1.0:
            raise DetailError(f"{where}: {name} fractions must be strictly between 0 and 1")
        if v * extent - bar / 2 < bar - 1e-9 or v * extent + bar / 2 > extent - bar + 1e-9:
            raise DetailError(f"{where}: a bar of {name} at {v} overlaps the frame")
    if any((b - a) * extent < bar - 1e-9 for a, b in pairwise(out)):
        raise DetailError(f"{where}: {name} overlap each other")
    return out


def parse_detail(raw: dict, where: str = "detail") -> Detail:
    if not isinstance(raw, dict) or "id" not in raw:
        raise DetailError(f"{where}: needs a mapping with an `id`")
    known = {"id", "source", "overhang_m", "frame_colour", "surround_colour", "openings", "dormers", "plinth", "eaves", "downpipes", "chimneys",
             "trim_colour", "bands", "corners", "gable_colour", "gable_from_m", "ridge_caps", "skylights", "balconies",
             "roof_tiles", "bargeboard_colour", "apron", "interior"}  # fmt: skip
    extra = set(raw) - known
    if extra:
        raise DetailError(f"{where}: unknown field(s) {sorted(extra)}")
    d = Detail(
        id=str(raw["id"]),
        overhang_m=float(raw.get("overhang_m", 0.0)),
        frame_colour=_hex(raw.get("frame_colour", DEFAULT_FRAME), where),
        surround_colour=_hex(raw.get("surround_colour", DEFAULT_FRAME), where),
        source=str(raw.get("source", "")),
    )
    if not 0.0 <= d.overhang_m <= 2.0:
        raise DetailError(f"{where}: overhang_m must be 0..2")
    if not isinstance(raw.get("interior", False), bool):
        raise DetailError(f"{where}: interior must be true or false")
    d.interior = raw.get("interior", False)
    d.trim_colour = _hex(raw.get("trim_colour", DEFAULT_FRAME), where)
    for i, raw_b in enumerate(raw.get("bands") or []):
        w = _block(raw_b, where, f"bands[{i}]", Band, ("top_m",))
        band = Band(_num(raw_b["top_m"], w, "top_m"), _num(raw_b.get("height_m", 0.15), w, "height_m"))
        if not (0.03 <= band.height_m <= 0.6 and band.top_m - band.height_m >= 0):
            raise DetailError(f"{w}: height_m must be 0.03..0.6 and the band above the ground")
        d.bands.append(band)
    if raw.get("corners") is not None:
        w = _block(raw["corners"], where, "corners", Corners)
        d.corners = Corners(_num(raw["corners"].get("width_m", 0.3), w, "width_m"), _num(raw["corners"].get("top_m", 3.0), w, "top_m"))
        if not (0.1 <= d.corners.width_m <= 0.8 and d.corners.top_m > 0.5):
            raise DetailError(f"{w}: width_m must be 0.1..0.8 and top_m above 0.5")
    if raw.get("gable_colour") is not None:
        d.gable_colour = _hex(raw["gable_colour"], where)
    if raw.get("gable_from_m") is not None:
        d.gable_from_m = _num(raw["gable_from_m"], where, "gable_from_m")
    d.ridge_caps = raw.get("ridge_caps", False)
    if not isinstance(d.ridge_caps, bool):
        raise DetailError(f"{where}: ridge_caps must be true or false")
    d.roof_tiles = raw.get("roof_tiles", False)
    if not isinstance(d.roof_tiles, bool):
        raise DetailError(f"{where}: roof_tiles must be true or false")
    if raw.get("bargeboard_colour") is not None:
        d.bargeboard_colour = _hex(raw["bargeboard_colour"], where)
    if raw.get("apron") is not None:
        ap = raw["apron"]
        if not isinstance(ap, dict) or set(ap) - {"width_m", "colour", "joints_m"}:
            raise DetailError(f"{where}: apron takes width_m, colour and joints_m")
        d.apron = (_num(ap.get("width_m", 0.6), where, "apron width_m"), _hex(ap.get("colour", "#8d8a84"), where), _num(ap.get("joints_m", 0.0), where, "apron joints_m"))
        if d.apron[2] and not 0.2 <= d.apron[2] <= 2.0:
            raise DetailError(f"{where}: apron joints_m must be 0.2..2")
        if not 0.2 <= d.apron[0] <= 2.0:
            raise DetailError(f"{where}: apron width_m must be 0.2..2")
    for i, raw_b in enumerate(raw.get("balconies") or []):
        _block(raw_b, where, f"balconies[{i}]", Balcony, ("edge", "at_m", "width_m", "floor_m"))
        bal = Balcony(**raw_b)
        if not (isinstance(bal.edge, int) and bal.edge >= 0 and bal.at_m >= 0 and 0.8 <= bal.width_m <= 20 and 1.8 <= bal.floor_m <= 12 and 0.4 <= bal.depth_m <= 2.5 and 0.6 <= bal.rail_m <= 1.3):
            raise DetailError(f"{where}: balconies[{i}]: width_m 0.8..20, floor_m 1.8..12, depth_m 0.4..2.5, rail_m 0.6..1.3")
        if bal.colour is not None:
            _hex(bal.colour, f"{where}: balconies[{i}]")
        d.balconies.append(bal)
    for i, raw_s in enumerate(raw.get("skylights") or []):
        w = _block(raw_s, where, f"skylights[{i}]", Skylight, ("edge", "at_m", "up_m"))
        sk = Skylight(**raw_s)
        if isinstance(sk.edge, bool) or not isinstance(sk.edge, int) or sk.edge < 0:
            raise DetailError(f"{w}: edge must be a footprint edge number")
        for name in ("at_m", "up_m", "width_m", "height_m"):
            setattr(sk, name, _num(getattr(sk, name), w, name))
        if not (0.3 <= sk.width_m <= 2.0 and 0.3 <= sk.height_m <= 2.5 and sk.up_m >= 0.1):
            raise DetailError(f"{w}: width_m must be 0.3..2.0, height_m 0.3..2.5, up_m at least 0.1")
        d.skylights.append(sk)
    if raw.get("plinth") is not None:
        w = _block(raw["plinth"], where, "plinth", Plinth)
        pl = Plinth(**raw["plinth"])
        pl.height_m = _num(pl.height_m, w, "height_m")
        if not 0.05 <= pl.height_m <= 1.5:
            raise DetailError(f"{w}: height_m must be 0.05..1.5")
        d.plinth = Plinth(pl.height_m, _hex(pl.colour, w))
    if raw.get("eaves") is not None:
        w = _block(raw["eaves"], where, "eaves", Eaves)
        ea = Eaves(**raw["eaves"])
        ea.fascia_m = _num(ea.fascia_m, w, "fascia_m")
        if not 0.05 <= ea.fascia_m <= 0.6:
            raise DetailError(f"{w}: fascia_m must be 0.05..0.6")
        if not isinstance(ea.gutter, bool):
            raise DetailError(f"{w}: gutter must be true or false")
        if d.overhang_m <= 0:
            raise DetailError(f"{w}: needs overhang_m > 0 (the fascia closes the rim of the overhang)")
        if not isinstance(ea.level_soffit, bool):
            raise DetailError(f"{w}: level_soffit must be true or false")
        if not isinstance(ea.rafters, bool) or (ea.rafters and ea.level_soffit):
            raise DetailError(f"{w}: rafters must be true or false, and not together with level_soffit")
        d.eaves = Eaves(_hex(ea.colour, w), ea.fascia_m, ea.gutter, ea.level_soffit, _hex(ea.gutter_colour, w) if ea.gutter_colour is not None else None, ea.rafters)
    for i, raw_p in enumerate(raw.get("downpipes") or []):
        w = _block(raw_p, where, f"downpipes[{i}]", Downpipe, ("edge", "at_m"))
        dp = Downpipe(**raw_p)
        if isinstance(dp.edge, bool) or not isinstance(dp.edge, int) or dp.edge < 0:
            raise DetailError(f"{w}: edge must be a footprint edge number")
        dp.at_m = _num(dp.at_m, w, "at_m")
        if dp.at_m < 0:
            raise DetailError(f"{w}: at_m must lie on the edge (>= 0)")
        if dp.colour:
            _hex(dp.colour, w)
        if not isinstance(dp.bends, bool):
            raise DetailError(f"{w}: bends must be true or false")
        d.downpipes.append(dp)
    for i, raw_c in enumerate(raw.get("chimneys") or []):
        w = _block(raw_c, where, f"chimneys[{i}]", Chimney, ("x", "y", "top_m"))
        ch = Chimney(**raw_c)
        for name in ("x", "y", "top_m", "width_m", "depth_m"):
            setattr(ch, name, _num(getattr(ch, name), w, name))
        if not (0.2 <= ch.width_m <= 2.0 and 0.2 <= ch.depth_m <= 2.0):
            raise DetailError(f"{w}: width_m and depth_m must be 0.2..2.0")
        if ch.top_m <= 0:
            raise DetailError(f"{w}: top_m must be above the ground")
        if ch.colour:
            _hex(ch.colour, w)
        if not isinstance(ch.cap, bool):
            raise DetailError(f"{w}: cap must be true or false")
        d.chimneys.append(ch)
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
        op.frame_m = _num(op.frame_m, w, "frame_m")
        if not 0.0 <= op.frame_m <= 0.2:
            raise DetailError(f"{w}: frame_m must be 0..0.2")
        if (op.frame_m or op.posts or op.rails) and op.kind not in ("window", "door", "garage_door"):
            raise DetailError(f"{w}: frame_m, posts and rails are for windows, doors and garage doors")
        op.surround_m = _num(op.surround_m, w, "surround_m")
        if not 0.0 <= op.surround_m <= 0.4:
            raise DetailError(f"{w}: surround_m must be 0..0.4")
        op.lintel_m, op.canopy_m = _num(op.lintel_m, w, "lintel_m"), _num(op.canopy_m, w, "canopy_m")
        if not (0.0 <= op.lintel_m <= 0.4 and 0.0 <= op.canopy_m <= 1.5):
            raise DetailError(f"{w}: lintel_m must be 0..0.4, canopy_m 0..1.5")
        for name, most in (("panels", 8), ("steps", 6)):
            v = getattr(op, name)
            if isinstance(v, bool) or not isinstance(v, int) or not 0 <= v <= most:
                raise DetailError(f"{w}: {name} must be a whole number 0..{most}")
        if op.panels and op.kind not in ("door", "garage_door"):
            raise DetailError(f"{w}: panels are for doors and garage doors")
        if (op.steps or op.canopy_m) and op.kind != "door":
            raise DetailError(f"{w}: steps and canopy_m are for doors")
        if op.sill and op.kind != "window":
            raise DetailError(f"{w}: sill is for windows only")
        if not isinstance(op.sill, bool):
            raise DetailError(f"{w}: sill must be true or false")
        if (op.posts or op.rails) and op.frame_m <= 0:
            raise DetailError(f"{w}: posts and rails need frame_m > 0 (their bars are as wide as the frame)")
        if 2 * op.frame_m >= min(op.width_m, op.height_m):
            raise DetailError(f"{w}: frame_m leaves no room for the pane")
        op.posts = _fractions(op.posts, w, "posts", op.width_m, op.frame_m)
        op.rails = _fractions(op.rails, w, "rails", op.height_m, op.frame_m)
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

    def lean_to(u: float, v: float) -> float:  # flat past the high edge: what stands behind it is as high as the roof's top
        return rise * min(1.0, (v - (cv - b)) / (2 * b))

    return {"gabled": gabled, "lean_to": lean_to}.get(roof, hipped)


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

    def cuboid(self, to3, s: tuple, z: tuple, d: tuple, colour, faces: tuple) -> None:
        """An axis-aligned box of the local frame ``to3(s, z, d)``, only the named faces (``s0 s1 z0 z1 d0 d1``: the low
        and high end of each axis), each wound to face away from the box's centre. Faces that sit against a wall or
        another bar are left out by the caller: nobody sees them, and they would only add triangles."""
        (s0, s1), (z0, z1), (d0, d1) = s, z, d
        sides = {
            "s0": [(s0, z0, d0), (s0, z1, d0), (s0, z1, d1), (s0, z0, d1)],
            "s1": [(s1, z0, d0), (s1, z1, d0), (s1, z1, d1), (s1, z0, d1)],
            "z0": [(s0, z0, d0), (s1, z0, d0), (s1, z0, d1), (s0, z0, d1)],
            "z1": [(s0, z1, d0), (s1, z1, d0), (s1, z1, d1), (s0, z1, d1)],
            "d0": [(s0, z0, d0), (s1, z0, d0), (s1, z1, d0), (s0, z1, d0)],
            "d1": [(s0, z0, d1), (s1, z0, d1), (s1, z1, d1), (s0, z1, d1)],
        }
        centre = np.asarray(to3((s0 + s1) / 2, (z0 + z1) / 2, (d0 + d1) / 2), float)
        for name in faces:
            pts = [np.asarray(to3(*c), float) for c in sides[name]]
            self.quad(pts, colour, 2 * np.mean(pts, axis=0) - centre)

    def mesh(self) -> Mesh:
        if not self.tris:
            return Mesh.empty()
        return Mesh(np.array(self.tris), np.array(self.cols, dtype=np.uint8))


def _place_on_part(ring: list, part_ring: list, edge: int, at_m: float, width: float):
    """(edge, at_m) on the part's own edge numbering of something ``width`` long at ``at_m`` along ``ring[edge]``, or None."""
    n, m = len(ring), len(part_ring)
    p, q = ring[edge], ring[(edge + 1) % n]
    length = math.dist(p, q)
    ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
    for j in range(m):
        a, c = part_ring[j], part_ring[(j + 1) % m]
        sa, sc = (a[0] - p[0]) * ux + (a[1] - p[1]) * uy, (c[0] - p[0]) * ux + (c[1] - p[1]) * uy
        off = max(abs((a[0] - p[0]) * uy - (a[1] - p[1]) * ux), abs((c[0] - p[0]) * uy - (c[1] - p[1]) * ux))
        if off < 0.02 and sc > sa and sa - 0.02 <= at_m and at_m + width <= sc + 0.02:
            return j, round(at_m - sa, 3)
    return None


def _on_part(d: Detail, ring: list, part_ring: list, first: bool) -> Detail:
    """The detail of one part of a split building (``meshes.split_parts``): the openings of ``d`` that lie on walls of
    the part, on the part's own edge numbers. Roof overhang, eaves and dormers stay with the first (main) part; the
    plinth goes round every part; downpipes and chimneys go to the part whose wall / footprint they stand on."""
    out = Detail(id=d.id, overhang_m=d.overhang_m if first else 0.0, frame_colour=d.frame_colour, surround_colour=d.surround_colour, source=d.source,
                 plinth=d.plinth, eaves=d.eaves if first else None, trim_colour=d.trim_colour, bands=d.bands, corners=d.corners,
                 gable_colour=d.gable_colour, gable_from_m=d.gable_from_m, ridge_caps=d.ridge_caps, roof_tiles=d.roof_tiles,
                 bargeboard_colour=d.bargeboard_colour)  # fmt: skip  (the apron goes round the whole house once)
    for o in d.openings:
        at = _place_on_part(ring, part_ring, o.edge, o.at_m, o.width_m)
        if at:
            out.openings.append(replace(o, edge=at[0], at_m=at[1]))
    for dp in d.downpipes:
        if 0 <= dp.edge < len(ring):
            at = _place_on_part(ring, part_ring, dp.edge, dp.at_m, PIPE_M)
            if at:
                out.downpipes.append(replace(dp, edge=at[0], at_m=at[1]))
    part = Polygon(part_ring)
    out.chimneys = [c for c in d.chimneys if part.contains(Point(c.x, c.y))]
    if first:  # on the part's own edge numbers, as the openings are
        for sk in d.skylights:
            at = _place_on_part(ring, part_ring, sk.edge, sk.at_m - sk.width_m / 2, sk.width_m)
            if at:
                out.skylights.append(replace(sk, edge=at[0], at_m=round(at[1] + sk.width_m / 2, 3)))
        for dm in d.dormers:
            at = _place_on_part(ring, part_ring, dm.edge, dm.at_m - dm.width_m / 2, dm.width_m)
            if at:
                out.dormers.append(replace(dm, edge=at[0], at_m=round(at[1] + dm.width_m / 2, 3)))
    return out


def build_detail(b: dict, d: Detail) -> tuple[Mesh, list[WindowTag]]:
    """Visual mesh of building ``b`` with the detail ``d`` applied, and its window panes."""
    if b.get("roof_part"):
        from drycastle_pipeline.buildings.meshes import split_parts

        if d.interior:
            raise DetailError(f"{d.id}: interior: not yet for a building cut in parts (roof_part)")
        ring = list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]
        parts = split_parts(b)
        subs = [_on_part(d, ring, list(orient(footprint_polygon(p["footprint"]), 1.0).exterior.coords)[:-1], k == 0) for k, p in enumerate(parts)]
        placed = {o.id for sub in subs for o in sub.openings}
        lost = [o.id for o in d.openings if o.id not in placed]
        if lost:
            raise DetailError(f"{d.id} opening {lost[0]}: does not fit one wall of a part of the building (it crosses where roof_part cuts the footprint)")
        if any(sub.dormers for sub in subs[1:]):
            raise DetailError(f"{d.id}: dormers go on the main part only")
        if sum(len(sub.downpipes) for sub in subs) < len(d.downpipes):
            raise DetailError(f"{d.id}: a downpipe does not fit one wall of a part of the building")
        if sum(len(sub.chimneys) for sub in subs) < len(d.chimneys):
            raise DetailError(f"{d.id}: a chimney does not lie inside one part of the building")
        # the main part's roof overhangs only where the whole building's would: not over the parts behind it (their flat
        # roof may be as high as its top) and without the spike a mitred corner makes where the cut meets a wall at a slant
        whole = orient(footprint_polygon(b["footprint"]), 1.0)
        outline = whole.buffer(d.overhang_m, join_style=2, mitre_limit=3.0) if d.overhang_m else whole
        others = unary_union([footprint_polygon(p["footprint"]) for p in parts[1:]]) if len(parts) > 1 else None
        # (parts that end below the main part's eave are simply roofed over: nothing to cut)
        # only a flat block as high as the eave cuts the roof: a wing with a roof of its own, or a lower block, is simply
        # roofed over (the verge passes above it)
        tall = [p for p in parts[1:] if p["roof"] == "flat" and float(p["height_m"]) >= float(parts[0]["eave_height_m"]) - 0.05]
        others = unary_union([footprint_polygon(p["footprint"]) for p in tall]) if tall else None
        parts[0] = {**parts[0], "_roof_clip": outline.difference(others) if others is not None else outline, "_roof_cut": others}
        built = [build_detail(p, sub) for p, sub in zip(parts, subs)]
        extra = _Tris()
        if d.apron is not None:
            _add_apron(extra, whole, d.apron)
        return Mesh.concat([m for m, _ in built] + [extra.mesh()]), [t for _, tags in built for t in tags]
    poly = orient(footprint_polygon(b["footprint"]), 1.0)
    eave, rise, roof = float(b["eave_height_m"]), float(b["roof_height_m"]), b["roof"]
    wall_c, roof_c = b["wall_colour"], b["roof_colour"]
    flat = roof == "flat" or rise <= 0

    fitted = None
    if not flat and b.get("roof_fit"):  # a roof fitted to the whole footprint: overlapping rectangles, the highest wins
        from drycastle_pipeline.buildings.roof_fit import fit_building

        fitted = fit_building(b, d.overhang_m)
    rim_boundary = cap_boundary = None
    if flat:
        angle = 0.0

        def top(x: float, y: float) -> float:
            return eave

        boundary = None
        regions: list[Polygon] = []
        height = None
    elif fitted is not None:
        angle, top, boundary = fitted.angle, fitted.top, fitted.wall_lines
        rim_boundary, cap_boundary = fitted.rim_lines, fitted.lines
    else:
        angle, cu, cv, a, bb = roof_frame(poly, b["ridge_dir"], hipped=roof == "hipped")
        # the regions reach as far out as the overhang does: a hipped roof's stop at its rectangle, and its hips run on
        # along the same diagonals when both half-sizes grow by the same amount (the ridge's ends do not move)
        regions = _roof_regions(roof, cu, cv, a + d.overhang_m, bb + d.overhang_m)
        height = _unclamped_height(roof, rise, cu, cv, a, bb)
        ca, sa = math.cos(angle), math.sin(angle)

        def top(x: float, y: float) -> float:
            u, v = x * ca + y * sa, -x * sa + y * ca
            return eave + max(0.0, height(u, v))

        boundary = rotate(
            unary_union([r.exterior for r in regions]), angle, origin=(0, 0), use_radians=True
        )

    from drycastle_pipeline.buildings import interior

    inside = interior.inside_of(b, d)
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

        tops = [z for _, z in shell[2:]]
        faces = [(wall, wall_c)]
        if d.gable_colour and max(tops) - min(tops) > 0.5:  # a gable wall: its top climbs
            g = d.gable_from_m if d.gable_from_m is not None else eave
            faces = [(wall.intersection(box(-1.0, -1.0, length + 1.0, g)), wall_c), (wall.intersection(box(-1.0, g, length + 1.0, max(tops) + 1.0)), hex_to_rgb(d.gable_colour))]
        for face, colour in faces:
            for g_ in _polygons(face):
                for tri in triangulate(orient(g_, 1.0)):
                    a0, a1, a2 = tri
                    area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
                    order = (a0, a1, a2) if area > 0 else (a0, a2, a1)
                    out.add([to3(*c) for c in order], colour)

        edge_ops = by_edge.get(i, [])
        if d.plinth is not None:
            lo, hi = _band_ends(ring, i, PLINTH_PROUD_M)
            _add_plinth(out, to3, d.plinth, lo, hi, length, [o for o in edge_ops if o.parent is None])
        outer_ops = [o for o in edge_ops if o.parent is None]
        wall_top = min(tops)
        trim_c = hex_to_rgb(d.trim_colour)
        for band in d.bands:  # level bands: whole between the openings they cross, none above the wall's top
            z0, z1 = band.top_m - band.height_m, min(band.top_m, wall_top)
            if z1 - z0 < 0.03:
                continue
            lo, hi = _band_ends(ring, i, BAND_PROUD_M)
            cuts = sorted((o.at_m - o.surround_m, o.at_m + o.width_m + o.surround_m) for o in outer_ops if o.sill_m - o.surround_m < z1 and z0 < o.sill_m + o.height_m + max(o.surround_m, o.lintel_m))
            cursor = lo
            for a_, b_ in [*cuts, (hi, hi)]:
                if a_ - cursor > 0.05:
                    out.cuboid(to3, (cursor, a_), (z0, z1), (-BAND_PROUD_M, 0.0), trim_c, ("d0", "z0", "z1", *(("s0",) if cursor > lo else ()), *(("s1",) if a_ < hi else ())))
                cursor = max(cursor, b_)
        if d.corners is not None and length >= 3 * d.corners.width_m:
            lo, hi = _band_ends(ring, i, CORNER_PROUD_M)
            foot_z = d.plinth.height_m if d.plinth is not None else 0.0
            cw, ct = d.corners.width_m, min(d.corners.top_m, wall_top)
            turns = []
            for a0_, b0_, c0_ in ((ring[i - 1], p, q), (p, q, ring[(i + 2) % n_edges])):  # a real outside corner turns left by 30 degrees or more
                (e0x, e0y), (e1x, e1y) = _unit(a0_, b0_), _unit(b0_, c0_)
                turns.append(e0x * e1y - e0y * e1x >= 0.5)
            for a_, b_, convex in ((lo, cw, turns[0]), (length - cw, hi, turns[1])):
                clear = not any(o.at_m - o.surround_m < b_ + 0.05 and a_ - 0.05 < o.at_m + o.width_m + o.surround_m for o in outer_ops)
                if convex and clear and ct - foot_z > 0.5:
                    out.cuboid(to3, (a_, b_), (foot_z, ct), (-CORNER_PROUD_M, 0.0), trim_c, ("d0", "z1", "s1" if a_ == lo else "s0"))

        def emit(o: Opening, base: float, edge=i, ops=edge_ops, to3=to3, out_n=out_n) -> None:
            x0, x1 = o.at_m, o.at_m + o.width_m
            z0, z1 = o.sill_m, o.sill_m + o.height_m
            r = base + o.recess_m
            thru = inside is not None and inside.through(o)  # a hole through the whole wall: its reveals reach the room
            deep = max(r, interior.WALL_M) if thru else r
            mid = to3((x0 + x1) / 2, (z0 + z1) / 2, (base + deep) / 2)
            frame = hex_to_rgb(d.frame_colour)
            for (sa_, za_), (sb_, zb_) in (
                ((x0, z0), (x1, z0)), ((x1, z0), (x1, z1)), ((x1, z1), (x0, z1)), ((x0, z1), (x0, z0))
            ):
                out.quad(
                    [to3(sa_, za_, base), to3(sb_, zb_, base), to3(sb_, zb_, deep), to3(sa_, za_, deep)],
                    hex_to_rgb(o.colour or DEFAULT_PANEL["recess"]) if o.kind == "recess" else frame,
                    mid,
                )
            if o.frame_m:
                _add_frame(out, to3, o, r, max(r - FRAME_PROUD_M, base), frame)
            if o.surround_m:  # a flat band on the wall round the opening (under a sill: on three sides)
                sc, m, dd = hex_to_rgb(d.surround_colour), o.surround_m, (base - SURROUND_PROUD_M, base)
                zlo = z0 if (o.sill or z0 <= 0.0) else z0 - m
                for ss, zz in (((x0 - m, x0), (max(zlo, 0.0), z1 + m)), ((x1, x1 + m), (max(zlo, 0.0), z1 + m)), ((x0, x1), (z1, z1 + m)),
                               *([((x0, x1), (zlo, z0))] if zlo < z0 else [])):  # fmt: skip
                    out.cuboid(to3, ss, zz, dd, sc, ("d0", "s0", "s1", "z0", "z1"))
            if o.sill:
                out.cuboid(
                    to3, (x0 - SILL_EXTRA_M, x1 + SILL_EXTRA_M), (max(z0 - SILL_THICK_M, 0.0), z0), (base - SILL_PROUD_M, base),
                    frame, ("d0", "s0", "s1", "z0", "z1"),
                )
            if o.lintel_m:  # a box over the head
                out.cuboid(to3, (x0 - 0.04, x1 + 0.04), (z1, z1 + o.lintel_m), (base - LINTEL_PROUD_M, base), frame, ("d0", "s0", "s1", "z0", "z1"))
            if o.panels and not thru:  # raised panels on the leaf: two beside each other on a door, one across on a garage door
                leaf = tuple(int(c * 0.82) for c in hex_to_rgb(o.colour or DEFAULT_PANEL[o.kind]))
                m, gap, cols_n = o.frame_m + 0.07, 0.07, 2 if o.kind == "door" else 1
                pw, ph = (o.width_m - 2 * m - (cols_n - 1) * gap) / cols_n, (o.height_m - 2 * m - (o.panels - 1) * gap) / o.panels
                if pw > 0.1 and ph > 0.1:
                    for c_ in range(cols_n):
                        for r_ in range(o.panels):
                            sx, sz = x0 + m + c_ * (pw + gap), z0 + m + r_ * (ph + gap)
                            out.cuboid(to3, (sx, sx + pw), (sz, sz + ph), (r - PANEL_PROUD_M, r), leaf, ("d0", "s0", "s1", "z0", "z1"))
            if o.steps and base == 0.0:
                step_c = hex_to_rgb(d.plinth.colour if d.plinth is not None else DEFAULT_STEP)
                count = o.steps if z0 > 0.02 else 1
                rise_ = (z0 if z0 > 0.02 else DOORSTEP_M) / count
                for k_ in range(count):  # the top step lies against the wall, each lower one further out
                    out.cuboid(to3, (x0 - STEP_SIDE_M, x1 + STEP_SIDE_M), (0.0, rise_ * (count - k_)), (-(k_ + 1) * STEP_TREAD_M - (0.12 if k_ == 0 else 0.0), -k_ * STEP_TREAD_M - (0.12 if k_ else 0.0)), step_c, ("d0", "s0", "s1", "z1"))  # fmt: skip
            if o.canopy_m and base == 0.0:
                zc, drop, cs0, cs1 = z1 + 0.3, 0.2, x0 - 0.25, x1 + 0.25

                def c3(s: float, z: float, dep: float, to3=to3, zc=zc, drop=drop, reach=o.canopy_m):
                    return to3(s, zc + z - drop * dep / reach, -dep)

                out.cuboid(c3, (cs0, cs1), (-0.05, 0.0), (0.0, o.canopy_m), roof_c, ("d1", "s0", "s1", "z0", "z1"))
                for s_ in (cs0 + 0.06, cs1 - 0.06):  # a bracket under each end
                    tri = [to3(s_, zc - 0.05, 0.0), to3(s_, zc - 0.05 - 0.6 * o.canopy_m, 0.0), c3(s_, -0.05, 0.85 * o.canopy_m)]
                    out.add(tri, frame)
                    out.add(tri[::-1], frame)
            kids = [k for k in ops if k.parent == o.id]
            outside = to3((x0 + x1) / 2, (z0 + z1) / 2, -1.0)
            if o.kind == "window":
                pane = _Tris()
                pane.quad([to3(x0, z0, r), to3(x1, z0, r), to3(x1, z1, r), to3(x0, z1, r)], GLASS, outside)
                if thru:  # seen from the room too
                    pane.quad([to3(x0, z0, r), to3(x1, z0, r), to3(x1, z1, r), to3(x0, z1, r)], GLASS, to3((x0 + x1) / 2, (z0 + z1) / 2, r + 1.0))
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
            elif not thru:  # (a door that goes through is an open doorway)
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
        if inside is not None:
            interior.add_inner_wall(out, to3, ring, i, edge_ops, inside)
        for bal in d.balconies:
            if bal.edge == i:
                if bal.at_m + bal.width_m > length + 0.01:
                    raise DetailError(f"{d.id} balcony on edge {i}: does not fit the wall ({length:.2f} m)")
                _add_balcony(out, to3, bal, hex_to_rgb(bal.colour or d.frame_colour), wall_c)

    # roof, with overhang: pieces of the grown footprint, heights continue the roof plane
    grown = poly.buffer(d.overhang_m, join_style=2, mitre_limit=3.0) if d.overhang_m else poly
    if b.get("_roof_clip") is not None:
        grown = max(_polygons(grown.intersection(b["_roof_clip"])), key=lambda g: g.area, default=poly)
    planes: list | None = None  # per piece, the plane it lies in (a fitted roof steps where two parts meet)
    if fitted is not None and fitted.outline is not None:
        grown = fitted.outline
    if flat:
        pieces = [grown]
        roof_z = lambda x, y: eave
    elif fitted is not None:
        pieces, planes, roof_z = [g for g, _ in fitted.pieces], [f for _, f in fitted.pieces], fitted.roof_z
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

    for k_piece, piece in enumerate(pieces):
        for tri in triangulate(piece):
            if planes is not None:
                pa, pb, pc = planes[k_piece]
                v3 = [(x, y, eave + pa * x + pb * y + pc) for x, y in tri]
            else:
                v3 = [(x, y, roof_z(x, y)) for x, y in tri]
            n = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
            if n[2] < 0:
                v3 = [v3[0], v3[2], v3[1]]
                n = -n
            if d.roof_tiles and n[2] > 1e-9 and math.hypot(n[0], n[1]) / n[2] >= TILE_MIN_SLOPE:
                out.add(v3, tuple(int(c * 0.72) for c in roof_c[:3]))  # the deck under the courses: their open ends show it, not the sky
                _add_courses(out, Polygon(tri), (-n[0] / n[2], -n[1] / n[2], v3[0][2] + (n[0] * v3[0][0] + n[1] * v3[0][1]) / n[2]), roof_c)
            else:
                out.add(v3, roof_c)
            if d.overhang_m:  # underside, so the eaves are not see-through
                under = [(x, y, z - ROOF_THICKNESS_M) for x, y, z in reversed(v3)]
                out.add(under, SOFFIT_COLOUR)
    if fitted is not None:  # where one part of the roof stands above the next: the wall between them
        for p, q, lo_p, hi_p, lo_q, hi_q in fitted.steps:
            face = [(*p, eave + lo_p - ROOF_THICKNESS_M), (*q, eave + lo_q - ROOF_THICKNESS_M), (*q, eave + hi_q), (*p, eave + hi_p)]
            nx, ny = q[1] - p[1], p[0] - q[0]
            for side in (1.0, -1.0):
                out.quad(face, wall_c, (p[0] + side * nx, p[1] + side * ny, eave + lo_p))
    if d.eaves is not None and d.overhang_m:
        _add_eaves(out, d.eaves, orient(grown, 1.0), roof_z, rim_boundary if fitted is not None else boundary, flat, b.get("_roof_cut"), d.overhang_m,
                   hex_to_rgb(d.bargeboard_colour) if d.bargeboard_colour else None)
        if d.eaves.level_soffit and roof == "hipped":
            # a boxed eave: a level board from the fascia's lower edge back to the wall, as most hipped houses have. The
            # wall above it and the roof's sloping underside are then out of sight. Only for a hipped roof, whose rim is
            # level all round; the ring is cut in two so neither half has a hole
            z = min(roof_z(x, y) for x, y in grown.exterior.coords) - d.eaves.fascia_m
            x0, y0, x1, y1 = grown.bounds
            mid = (x0 + x1) / 2
            for half in (Polygon([(x0 - 1, y0 - 1), (mid, y0 - 1), (mid, y1 + 1), (x0 - 1, y1 + 1)]),
                         Polygon([(mid, y0 - 1), (x1 + 1, y0 - 1), (x1 + 1, y1 + 1), (mid, y1 + 1)])):  # fmt: skip
                for piece in _polygons(grown.difference(poly).intersection(half)):
                    for tri in triangulate(piece):
                        v3 = [(x, y, z) for x, y in tri]
                        if np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))[2] > 0:
                            v3 = [v3[0], v3[2], v3[1]]  # seen from below
                        out.add(v3, SOFFIT_COLOUR)
    if d.ridge_caps and not flat and boundary is not None:
        _add_ridge_caps(out, cap_boundary if fitted is not None else boundary, grown, roof_z, tuple(int(c * 0.8) for c in roof_c))
    for sk in d.skylights:
        if flat or not 0 <= sk.edge < n_edges:
            raise DetailError(f"{d.id} skylight: needs a pitched roof and an edge in 0..{n_edges - 1}")
        _add_skylight(out, d, sk, ring, roof_z)
    for dm in d.dormers:
        if flat:
            raise DetailError(f"{d.id} dormer {dm.id}: the roof is flat")
        if not 0 <= dm.edge < n_edges:
            raise DetailError(f"{d.id} dormer {dm.id}: edge {dm.edge} not in 0..{n_edges - 1}")
        _add_dormer(out, windows, d, dm, ring, roof_z, roof_c)
    for dp in d.downpipes:
        _add_downpipe(out, d, dp, ring, top, roof_z)
    for ch in d.chimneys:
        _add_chimney(out, d, ch, poly, angle, roof_z, wall_c)
    for tri in triangulate(poly):
        a0, a1, a2 = tri
        area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
        order = (a0, a1, a2) if area < 0 else (a0, a2, a1)
        out.add([(x, y, 0.0) for x, y in order], wall_c)
    if inside is not None:
        interior.add_floor_ceiling(out, poly, inside, inset=interior.WALL_M)
        interior.add_partitions(out, interior.plan_of(b, d, inside), inside)
    if d.apron is not None:
        _add_apron(out, poly, d.apron)
    return out.mesh(), windows


def _add_apron(out: _Tris, poly: Polygon, apron: tuple[float, str, float]) -> None:
    """A paved strip round the foot of the walls; its outer face goes well into the ground, so on a slope the house stands
    on a kerb and not in the air."""
    width, apron_c = apron[0], hex_to_rgb(apron[1])
    outer = orient(poly.buffer(width, join_style=2, mitre_limit=2.0), 1.0)
    x0, y0, x1, y1 = outer.bounds
    ring_area = outer.difference(poly)
    for half in (box(x0 - 1, y0 - 1, (x0 + x1) / 2, y1 + 1), box((x0 + x1) / 2, y0 - 1, x1 + 1, y1 + 1)):  # cut in two: no piece has a hole
        for piece in _polygons(outer.difference(poly).intersection(half)):
            for tri in triangulate(piece):
                v3 = [(x, y, APRON_TOP_M) for x, y in tri]
                n = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
                out.add(v3 if n[2] >= 0 else [v3[0], v3[2], v3[1]], apron_c)
    for p, q in pairwise(list(outer.exterior.coords)):
        face = [(*p, -APRON_DEEP_M), (*q, -APRON_DEEP_M), (*q, APRON_TOP_M), (*p, APRON_TOP_M)]
        out.quad(face, tuple(int(v * 0.8) for v in apron_c), ((p[0] + q[0]) / 2 + (q[1] - p[1]), (p[1] + q[1]) / 2 - (q[0] - p[0]), 0.0))
        step, run = apron[2], math.dist(p, q)
        if step and run > step:  # slabs: a dark joint across the strip every joints_m, and one along its middle
            ux, uy = (q[0] - p[0]) / run, (q[1] - p[1]) / run
            nx, ny = -uy, ux  # into the strip: the outer ring runs counter-clockwise
            joint_c, z = tuple(int(v * 0.68) for v in apron_c), APRON_TOP_M + APRON_JOINT_LIFT_M
            n = max(1, round(run / step))
            for k in range(1, n):
                s = run * k / n
                a, b = (p[0] + ux * (s - 0.012), p[1] + uy * (s - 0.012)), (p[0] + ux * (s + 0.012), p[1] + uy * (s + 0.012))
                piece = Polygon([a, b, (b[0] + nx * width, b[1] + ny * width), (a[0] + nx * width, a[1] + ny * width)]).intersection(ring_area)
                for part in _polygons(piece):
                    for tri in triangulate(part):
                        v3 = [(x, y, z) for x, y in tri]
                        nn = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
                        out.add(v3 if nn[2] >= 0 else [v3[0], v3[2], v3[1]], joint_c)


def _unit(p, q) -> tuple[float, float]:
    length = math.dist(p, q)
    return ((q[0] - p[0]) / length, (q[1] - p[1]) / length) if length > 1e-9 else (0.0, 0.0)


def _band_ends(ring: list, i: int, t: float) -> tuple[float, float]:
    """Where a band standing ``t`` m off wall ``i`` starts and ends along the wall (s = 0 at its first corner).

    Neighbouring bands meet on the diagonal of the corner, so a band runs past the wall's end at a convex corner
    (by ``t`` at a right angle) and stops short of it at a reflex one."""
    n = len(ring)
    p, q = ring[i], ring[(i + 1) % n]
    u = _unit(p, q)

    def shift(a, b, c) -> float:  # the walls a -> b and b -> c meet at b; how far the diagonal is along wall i
        u0, u1 = _unit(a, b), _unit(b, c)
        n0, n1 = (u0[1], -u0[0]), (u1[1], -u1[0])
        k = t / max(1.0 + n0[0] * n1[0] + n0[1] * n1[1], 0.2)  # clamped so a needle corner stays finite
        return k * ((n0[0] + n1[0]) * u[0] + (n0[1] + n1[1]) * u[1])

    return shift(ring[i - 1], p, q), math.dist(p, q) + shift(p, q, ring[(i + 2) % n])


def _add_plinth(out: _Tris, to3, pl: Plinth, lo: float, hi: float, length: float, ops: list) -> None:
    """The band along the foot of one wall (``to3`` is the wall's frame, ``length`` its length): from ``lo`` to ``hi``
    along it, ground to ``pl.height_m``, standing PLINTH_PROUD_M off the wall. Openings whose sill is below the band
    cut through it, with their reveals continued through its thickness."""
    h, t, colour = pl.height_m, PLINTH_PROUD_M, hex_to_rgb(pl.colour)
    cuts = [(o.at_m, o.at_m + o.width_m, o.sill_m, o.sill_m + o.height_m) for o in ops if o.sill_m < h]
    face = box(lo, 0.0, hi, h)
    if cuts:
        face = face.difference(unary_union([box(x0, z0, x1, min(z1, h)) for x0, x1, z0, z1 in cuts]))
    for g in _polygons(face):
        for tri in triangulate(orient(g, 1.0)):
            a0, a1, a2 = tri
            area = (a1[0] - a0[0]) * (a2[1] - a0[1]) - (a1[1] - a0[1]) * (a2[0] - a0[0])
            order = (a0, a1, a2) if area > 0 else (a0, a2, a1)
            out.add([to3(*c, -t) for c in order], colour)
    for x0, x1, z0, z1 in cuts:
        zt = min(z1, h)
        mid = to3((x0 + x1) / 2, (z0 + zt) / 2, -t / 2)
        sides = [((x0, z0), (x0, zt)), ((x1, z0), (x1, zt))]
        if z0 > 0:
            sides.append(((x0, z0), (x1, z0)))  # the ledge under a window cut into the band
        if z1 < h:
            sides.append(((x0, zt), (x1, zt)))
        for (sa, za), (sb, zb) in sides:
            out.quad([to3(sa, za, 0.0), to3(sb, zb, 0.0), to3(sb, zb, -t), to3(sa, za, -t)], colour, mid)
    # the top of the band, in runs between the openings that reach it; at the corners its outer edge is mitred
    spans, cursor = [], lo
    for x0, x1 in sorted((c[0], c[1]) for c in cuts if c[3] >= h):
        if x0 > cursor:
            spans.append((cursor, x0))
        cursor = max(cursor, x1)
    if hi > cursor:
        spans.append((cursor, hi))
    for a, b in spans:
        a0, b0 = (0.0 if a == lo else a), (length if b == hi else b)
        out.quad([to3(a, h, -t), to3(b, h, -t), to3(b0, h, 0.0), to3(a0, h, 0.0)], colour, to3(a, h + 1.0, -t / 2))


def _add_frame(out: _Tris, to3, o: Opening, d_back: float, d_front: float, colour) -> None:
    """The opening's own frame, posts and rails: flat bars ``o.frame_m`` wide in the plane of the pane, standing from
    depth ``d_back`` (the pane) out to ``d_front``. Rails stop at the posts, so no two bars overlap (no z-fighting)."""
    x0, x1, z0, z1, f = o.at_m, o.at_m + o.width_m, o.sill_m, o.sill_m + o.height_m, o.frame_m
    dd = (d_front, d_back)

    def bar(s: tuple, z: tuple, faces: tuple) -> None:  # only the faces nobody sees from outside are left out
        out.cuboid(to3, s, z, dd, colour, faces)

    bar((x0, x0 + f), (z0, z1), ("d0", "s1"))
    bar((x1 - f, x1), (z0, z1), ("d0", "s0"))
    bar((x0 + f, x1 - f), (z1 - f, z1), ("d0", "z0"))
    bar((x0 + f, x1 - f), (z0, z0 + f), ("d0", "z1"))
    posts = [x0 + c * o.width_m for c in o.posts]
    for c in posts:
        bar((c - f / 2, c + f / 2), (z0 + f, z1 - f), ("d0", "s0", "s1"))
    runs = [(x0 + f, x0 + f), *[(c - f / 2, c + f / 2) for c in posts], (x1 - f, x1 - f)]
    for c in o.rails:
        zc = z0 + c * o.height_m
        for (_, a), (b, _) in pairwise(runs):
            if b - a > 1e-6:
                bar((a, b), (zc - f / 2, zc + f / 2), ("d0", "z0", "z1"))


def _add_courses(out: _Tris, piece: Polygon, plane: tuple[float, float, float], colour) -> None:
    """One flat piece of roof (z = a*x + b*y + c) laid in courses: level strips TILE_COURSE_M wide along the slope, each
    one's lower edge TILE_LIFT_M proud of the plane with a darker face under it, every strip a shade apart. The courses
    are cut at heights counted from the plane's own zero, so they meet across a hip."""
    a, b, c = plane
    g = math.hypot(a, b)
    ux, uy = a / g, b / g  # level direction up the slope
    dz = TILE_COURSE_M * g / math.hypot(1.0, g)
    hs = [a * x + b * y + c for x, y in piece.exterior.coords]
    span = max(math.dist(p, q) for p in piece.exterior.coords for q in piece.exterior.coords) + 1.0
    mx, my = piece.centroid.x, piece.centroid.y
    mid = mx * -uy + my * ux  # the piece's place across the slope
    base = tuple(int(v) for v in colour[:3])
    dark = tuple(int(v * 0.72) for v in base)

    def at(s: float, w: float) -> tuple[float, float]:  # s up the slope (level metres from the plane's zero), w across
        return ux * s - uy * w, uy * s + ux * w

    for k in range(math.floor(min(hs) / dz + 1e-6), math.ceil(max(hs) / dz - 1e-6)):
        s0, s1 = (k * dz - c) / g, ((k + 1) * dz - c) / g
        strip = piece.intersection(Polygon([at(s0, mid - span), at(s0, mid + span), at(s1, mid + span), at(s1, mid - span)]))
        shade = 1.0 + 0.045 * (((k * 7919) % 5) - 2) / 2
        col = tuple(min(255, int(v * shade)) for v in base)
        for part in _polygons(strip):
            if part.area < 1e-5:
                continue
            for tri in triangulate(part):
                v3 = [(x, y, a * x + b * y + c + TILE_LIFT_M * (1.0 - min(1.0, max(0.0, (a * x + b * y + c - k * dz) / dz)))) for x, y in tri]
                n = np.cross(np.subtract(v3[1], v3[0]), np.subtract(v3[2], v3[0]))
                out.add(v3 if n[2] >= 0 else [v3[0], v3[2], v3[1]], col)
            ws = [x * -uy + y * ux for x, y in part.exterior.coords if abs(a * x + b * y + c - k * dz) < 1e-6]
            if len(ws) >= 2 and max(ws) - min(ws) > 0.02:  # the butt of the course, facing down the slope
                p0, p1 = at(s0, min(ws)), at(s0, max(ws))
                face = [(*p0, k * dz), (*p1, k * dz), (*p1, k * dz + TILE_LIFT_M), (*p0, k * dz + TILE_LIFT_M)]
                out.quad(face, dark, (p0[0] - ux, p0[1] - uy, k * dz))


def _add_eaves(out: _Tris, e: Eaves, grown: Polygon, roof_z, boundary, flat: bool, cut, overhang: float = 0.0, barge=None) -> None:
    """Fascia all round the rim of the overhang (``grown``, counter-clockwise), and a gutter along each eave.

    The rim is cut where the roof plane kinks (a gable's ridge), so a gable end gets a board that follows the slope.
    The board is two-sided: from inside the eaves it is seen too. An eave is a level run of the rim that the roof
    slopes down to; ``cut`` is where another part of the building takes the roof over, which gets no board."""
    colour = hex_to_rgb(e.colour)
    ring = list(grown.exterior.coords)[:-1]
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        ux, uy = _unit(p, q)
        if ux == uy == 0.0:
            continue
        nx, ny = uy, -ux  # outward
        for c0, c1 in pairwise(_split_edge(p, q, boundary, roof_z)):
            mx, my = (c0[0] + c1[0]) / 2, (c0[1] + c1[1]) / 2
            if cut is not None and not cut.is_empty and cut.distance(Point(mx, my)) < 0.05:
                continue
            z0, z1 = roof_z(*c0), roof_z(*c1)
            sloped = barge is not None and abs(z0 - z1) > GUTTER_LEVEL_M  # a gable's edge: its bargeboard
            deep = max(e.fascia_m, BARGE_M) if sloped else e.fascia_m
            board = [(*c0, z0), (*c1, z1), (*c1, z1 - deep), (*c0, z0 - deep)]
            zm = (z0 + z1) / 2
            out.quad(board, barge if sloped else colour, (mx + nx, my + ny, zm))
            out.quad(board, barge if sloped else colour, (mx - nx, my - ny, zm))
            # level, and the roof falls toward it from the inside (not the high edge of a lean-to)
            # (nearly level: a hipped roof over a footprint that is not quite a rectangle has a rim a few cm off level)
            if e.gutter and abs(z0 - z1) <= GUTTER_LEVEL_M and math.dist(c0, c1) > 0.5 and (flat or roof_z(mx - nx * 0.3, my - ny * 0.3) > max(z0, z1) + 1e-3):
                top = (z0 + z1) / 2 - 0.02

                def to3(s: float, z: float, depth: float, c0=c0, ux=ux, uy=uy, nx=nx, ny=ny):  # depth is outward here
                    return (c0[0] + ux * s + nx * depth, c0[1] + uy * s + ny * depth, z)

                out.cuboid(
                    to3, (0.0, math.dist(c0, c1)), (top - GUTTER_H_M, top), (0.0, GUTTER_W_M),
                    hex_to_rgb(e.gutter_colour) if e.gutter_colour else colour, ("d1", "s0", "s1", "z0", "z1"),
                )
                if e.rafters and not flat and overhang > 0.1:
                    # rafter tails: bars under the roof from the wall out to the fascia, following the slope
                    def under(s: float, z: float, depth: float, c0=c0, ux=ux, uy=uy, nx=nx, ny=ny):
                        x, y = c0[0] + ux * s + nx * depth, c0[1] + uy * s + ny * depth
                        return (x, y, roof_z(x, y) - ROOF_THICKNESS_M - RAFTER_H_M + z)

                    run = math.dist(c0, c1)
                    count = max(2, round((run - 2 * overhang) / RAFTER_STEP_M) + 1)
                    for k in range(count):
                        s = overhang + 0.1 + (run - 2 * overhang - 0.2) * k / (count - 1)
                        out.cuboid(under, (s - RAFTER_W_M / 2, s + RAFTER_W_M / 2), (0.0, RAFTER_H_M), (-overhang, -0.01), colour, ("s0", "s1", "z0"))


def _add_downpipe(out: _Tris, d: Detail, dp: Downpipe, ring: list, top, roof_z) -> None:
    """A square pipe down a wall: ground to the underside of the overhang (the wall top when there is none)."""
    n = len(ring)
    if not 0 <= dp.edge < n:
        raise DetailError(f"{d.id} downpipe: edge {dp.edge} not in 0..{n - 1}")
    p, q = ring[dp.edge], ring[(dp.edge + 1) % n]
    length = math.dist(p, q)
    if dp.at_m + PIPE_M > length:
        raise DetailError(f"{d.id} downpipe: at_m {dp.at_m} does not fit edge {dp.edge} (length {length:.2f} m)")
    ux, uy = _unit(p, q)

    def to3(s: float, z: float, depth: float):  # as the walls' frame: depth is inward
        return (p[0] + ux * s - uy * depth, p[1] + uy * s + ux * depth, z)

    s0, s1 = dp.at_m, dp.at_m + PIPE_M
    far = -(PIPE_OFF_M + PIPE_M)
    if d.overhang_m:  # under the overhang, at its outer face where the roof plane has dropped lowest
        z_top = min(roof_z(*to3(s, 0.0, far)[:2]) for s in (s0, s1)) - ROOF_THICKNESS_M
    else:
        z_top = min(top(*to3(s, 0.0, 0.0)[:2]) for s in (s0, s1))
    if z_top < 0.5:
        raise DetailError(f"{d.id} downpipe: no wall above it at edge {dp.edge}, {dp.at_m} m")
    colour = hex_to_rgb(dp.colour or ((d.eaves.gutter_colour or d.eaves.colour) if d.eaves else DEFAULT_PIPE))
    run = d.overhang_m - (PIPE_OFF_M + PIPE_M)
    if dp.bends:
        out.cuboid(to3, (s0, s1), (0.05, 0.05 + PIPE_M), (far - 0.14, far), colour, ("d0", "s0", "s1", "z0", "z1"))  # the kick-out at the foot
        if d.eaves is not None and d.eaves.gutter and run > 0.1:
            # the pipe leaves the wall under the eave and slants out to the gutter at the rim
            z_rim = min(roof_z(*to3(s, 0.0, -d.overhang_m)[:2]) for s in (s0, s1)) - (d.eaves.fascia_m if d.eaves.level_soffit else GUTTER_H_M + 0.02)
            z_low = z_rim - 0.7 * run
            if z_low > 1.0:
                z_top = z_low

                def slant(s: float, z: float, t: float):
                    return to3(s, z_low + (z_rim - z_low) * t + z, far + (-d.overhang_m - GUTTER_W_M / 2 - far) * t)

                out.cuboid(slant, (s0, s1), (-PIPE_M, 0.0), (0.0, 1.0), colour, ("s0", "s1", "z0", "z1"))
    out.cuboid(to3, (s0, s1), (0.0, z_top), (far, -PIPE_OFF_M), colour, ("d0", "s0", "s1", *(("z1",) if dp.bends else ())))


def _add_chimney(out: _Tris, d: Detail, ch: Chimney, poly: Polygon, angle: float, roof_z, wall_c) -> None:
    """A box standing on the roof at (x, y), its width along the ridge; its foot sinks below the roof surface."""
    ca, sa = math.cos(angle), math.sin(angle)

    def to3(s: float, z: float, depth: float):  # s along the ridge, depth across it
        return (ch.x + ca * s - sa * depth, ch.y + sa * s + ca * depth, z)

    hw, hd = ch.width_m / 2, ch.depth_m / 2
    corners = [to3(s, 0.0, t)[:2] for s in (-hw, hw) for t in (-hd, hd)]
    if not all(poly.covers(Point(c)) for c in corners):
        raise DetailError(f"{d.id} chimney at ({ch.x}, {ch.y}): must lie inside the footprint")
    heights = [roof_z(*c) for c in [*corners, (ch.x, ch.y)]]
    if ch.top_m <= max(heights) + 0.05:
        raise DetailError(f"{d.id} chimney at ({ch.x}, {ch.y}): top_m {ch.top_m} is not above the roof ({max(heights):.2f} m)")
    colour = hex_to_rgb(ch.colour) if ch.colour else wall_c
    # the foot goes below the lowest corner, so no slope leaves it hanging in the air
    out.cuboid(to3, (-hw, hw), (min(heights) - CHIMNEY_BELOW_M, ch.top_m), (-hd, hd), colour, ("s0", "s1", "d0", "d1", "z1"))
    if ch.cap:
        out.cuboid(to3, (-hw - 0.05, hw + 0.05), (ch.top_m, ch.top_m + 0.07), (-hd - 0.05, hd + 0.05), CAP_COLOUR, ("s0", "s1", "d0", "d1", "z0", "z1"))


def _add_balcony(out: _Tris, to3, bal: Balcony, colour, slab_c) -> None:
    """A slab standing out of the wall with a railing of level boards between corner posts, on three sides."""
    s0, s1, z, dep = bal.at_m, bal.at_m + bal.width_m, bal.floor_m, bal.depth_m
    out.cuboid(to3, (s0, s1), (z - 0.16, z), (-dep, 0.0), slab_c, ("d0", "s0", "s1", "z0", "z1"))
    post = 0.08
    for s in (s0, s1 - post):
        out.cuboid(to3, (s, s + post), (z, z + bal.rail_m), (-dep, -dep + post), colour, ("d0", "d1", "s0", "s1", "z1"))
    boards = max(3, round(bal.rail_m / 0.19))
    step = (bal.rail_m - 0.08) / boards
    for k in range(boards):
        z0 = z + 0.08 + k * step
        z1 = z0 + step * 0.72
        out.cuboid(to3, (s0, s1), (z0, z1), (-dep + 0.02, -dep + 0.05), colour, ("d0", "d1", "z0", "z1"))
        for s in (s0, s1 - 0.03):
            out.cuboid(to3, (s, s + 0.03), (z0, z1), (-dep, 0.0), colour, ("s0", "s1", "z0", "z1"))


def _add_ridge_caps(out: _Tris, boundary, grown: Polygon, roof_z, colour) -> None:
    """A row of cap tiles along every line of the roof that both sides fall away from: the ridge and the hips."""
    from shapely.ops import linemerge

    lines = boundary.intersection(grown.buffer(-0.02))
    lines = linemerge(lines) if lines.geom_type == "MultiLineString" else lines
    for line in getattr(lines, "geoms", [lines]):
        if line.geom_type != "LineString":
            continue
        for c0, c1 in pairwise(list(line.coords)):
            run = math.dist(c0, c1)
            if run < 0.3:
                continue
            ux, uy = (c1[0] - c0[0]) / run, (c1[1] - c0[1]) / run
            nx, ny = uy, -ux
            mx, my = (c0[0] + c1[0]) / 2, (c0[1] + c1[1]) / 2
            if not all(roof_z(mx + k * nx * 0.3, my + k * ny * 0.3) < roof_z(mx, my) - 0.02 for k in (-1, 1)):
                continue
            # (heights taken just inside the run: at its very end another part of a fitted roof may stand higher)
            za, zb = roof_z(c0[0] + ux * 0.03, c0[1] + uy * 0.03), roof_z(c1[0] - ux * 0.03, c1[1] - uy * 0.03)
            z0, z1 = za - (zb - za) * 0.03 / max(run - 0.06, 0.01), zb + (zb - za) * 0.03 / max(run - 0.06, 0.01)

            def to3(s: float, z: float, dep: float, c0=c0, ux=ux, uy=uy, nx=nx, ny=ny, z0=z0, z1=z1, run=run):
                return (c0[0] + ux * s + nx * dep, c0[1] + uy * s + ny * dep, z0 + (z1 - z0) * s / run + z)

            # its sides reach down to the slopes, so no daylight shows between the cap and the roof
            fall = roof_z(mx, my) - min(roof_z(mx + k * nx * RIDGE_CAP_W_M / 2, my + k * ny * RIDGE_CAP_W_M / 2) for k in (-1, 1))
            out.cuboid(to3, (0.0, run), (-max(0.03, fall + 0.02), RIDGE_CAP_H_M), (-RIDGE_CAP_W_M / 2, RIDGE_CAP_W_M / 2), colour, ("s0", "s1", "d0", "d1", "z1"))


def _add_skylight(out: _Tris, d: Detail, sk: Skylight, ring: list, roof_z) -> None:
    """A roof window lying on the slope above eave wall ``sk.edge``: a dark frame a little above the tiles, glass in it."""
    p, q = ring[sk.edge], ring[(sk.edge + 1) % len(ring)]
    length = math.dist(p, q)
    ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length

    def xy(s: float, depth: float) -> tuple[float, float]:
        return p[0] + ux * s - uy * depth, p[1] + uy * s + ux * depth

    slope = (roof_z(*xy(sk.at_m, sk.up_m + 0.5)) - roof_z(*xy(sk.at_m, sk.up_m))) / 0.5
    run = sk.height_m / math.hypot(1.0, slope)
    s0, s1, t0, t1 = sk.at_m - sk.width_m / 2, sk.at_m + sk.width_m / 2, sk.up_m, sk.up_m + run
    corners = [roof_z(*xy(s, t)) for s in (s0, s1) for t in (t0, t1)]
    flat_plane = abs(roof_z(*xy(sk.at_m, (t0 + t1) / 2)) - sum(corners) / 4) < 0.01
    if s0 < 0.2 or s1 > length - 0.2 or slope < 0.15 or not flat_plane:
        raise DetailError(f"{d.id} skylight on edge {sk.edge} at {sk.at_m} m: does not lie on one slope of the roof")

    def quad(m: float, lift: float, colour) -> None:
        pts = [(*xy(s, t), roof_z(*xy(s, t)) + lift) for s, t in ((s0 + m, t0 + m), (s1 - m, t0 + m), (s1 - m, t1 - m), (s0 + m, t1 - m))]
        out.quad(pts, colour, (pts[0][0], pts[0][1], pts[0][2] + 5.0))

    quad(0.0, 0.04, SKYLIGHT_FRAME)
    quad(0.06, 0.055, SKYLIGHT_GLASS)


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
