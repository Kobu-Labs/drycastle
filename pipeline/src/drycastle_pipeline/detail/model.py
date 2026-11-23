"""Data types and constants of the detail stage: patches, results, parcel contexts, the village's detail."""

from dataclasses import dataclass, field

import numpy as np
from shapely.geometry import LineString, Polygon
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.detail import meadow as meadow_mod
from drycastle_pipeline.detail.meshes import KINDS
from drycastle_pipeline.terrain.terrain import Terrain

DETAIL_FORMAT_VERSION = 1
CROP_FORMAT_VERSION = 1
CLUTTER_KINDS = KINDS
# The first layer wins where two overlap (a path crosses lawn, litter lies on lawn, ...).
PATCH_PRIORITY = (
    "dike_track", "gravel", "verge_gravel", "bed", "furrow", "dirt", "verge_dirt", "puddle", "mud", "moss", "litter", "tone_a", "tone_b",
    "swamp", "lawn",
)
# Library texture of a layer; layers without one are flat colour.
PATCH_TEXTURE = {
    "dike_track": "dike_gravel", "gravel": "gravel", "verge_gravel": "gravel", "bed": "soil_rows", "furrow": "soil_rows",
    "dirt": "dirt", "verge_dirt": "dirt", "litter": "leaf_litter", "moss": "moss", "mud": "mud", "puddle": "puddle",
}
_SOFT_LAYERS = ("lawn", "tone_a", "tone_b", "swamp", "litter", "moss", "mud")  # big soft shapes: simplified harder (fewer triangles)
_RECT_LAYERS = ("bed", "furrow")  # textured with their own angle and origin
_NEAR_M = 8.0  # what around a parcel counts as near it
_AROUND_M = 16.0  # the ground a crop patch's edge blends into (detail/crop.py): widest border zone plus the probe
_TILE_M = 24.0
CELL_M = 2.0  # the meadow cell: the tone noise is laid out in these (detail/meadow.py), stored clutter reads it at its position
_MIN_PIECE_M2 = 0.15
_PATCH_ROW = 19  # x0 y0 z0 x1 y1 z1 x2 y2 z2, r g b, u0 v0 u1 v1 u2 v2, texture index (-1 = flat colour)


@dataclass
class Patch:
    layer: str  # one of PATCH_PRIORITY
    geom: BaseGeometry  # local metres, disjoint from every other patch
    colour: list[int] | None = None  # flat colour (no texture)
    texture: str | None = None
    angle: float = 0.0  # texture rotation (radians) about ``origin``
    origin: tuple[float, float] = (0.0, 0.0)
    lift: float | None = None  # height above the terrain when not the stage's ``lift_m`` (the dike track lies over the road)


@dataclass
class DetailResult:
    parcel: str
    patches: list[Patch] = field(default_factory=list)
    clutter: list[dict] = field(default_factory=list)
    crops: list[dict] = field(default_factory=list)  # crop plants and artifacts (detail/crop.py): the crops.json of the map, not clutter

    def counts(self) -> dict[str, int]:
        out = {k: 0 for k in CLUTTER_KINDS}
        for c in self.clutter:
            out[c["kind"]] += 1
        return out


@dataclass
class ParcelContext:
    """What a recipe needs about one parcel (or one part of it), in local metres."""

    id: str
    kind: str
    polygon: Polygon
    buildings: list[Polygon]
    fences: list[LineString]
    trees: list[tuple[float, float, float]]  # x, y, scale (near the parcel, from any parcel)
    road: BaseGeometry | None  # carriageways near the parcel
    terrain: Terrain
    tree_cfg: dict
    colour: list[int] | None = None  # the parcel's flat terrain colour (the lawn palette follows it)
    blocked: BaseGeometry | None = None  # placed objects and water patches
    verge: dict[str, BaseGeometry] | None = None  # "gravel" and "dirt" verge blobs along the roads, clipped
    water: BaseGeometry | None = None  # water parcels near the parcel (grass beside them stands fresh)
    road_lines: list[tuple[LineString, float]] | None = None  # centre lines and widths of the road edges near the parcel
    plain_road: BaseGeometry | None = None  # carriageways of the dike's regular roads (``detail.dike.not_track``): no track, no lawn over them
    crop_neighbours: list[tuple[str, BaseGeometry]] | None = None  # (id, polygon) of the other crop patches touching this one (the baulk)
    ground_around: list[tuple[str, BaseGeometry, list[int], bool]] | None = None  # crop only: (id, polygon, terrain colour, is_crop) of the other ground units near the patch (what a field's edge blends into)
    dike: bool = False  # this part of the parcel is the flood dike: it gets the dike recipe, whatever the parcel kind
    swamp: BaseGeometry | None = None  # the part of this ground within the reach of water (swamp ground), for the types of ``detail.swamp.kinds``
    tracks: list[tuple[str, LineString, float]] | None = None  # id, centre line, width of the path and track edges near the part (forest mud)


@dataclass
class VillageDetail:
    chunk_m: float
    clutter: dict[tuple[int, int], dict[str, list[list[float]]]]
    patches: np.ndarray  # (n, _PATCH_ROW)
    textures: list[str]
    stats: dict = field(default_factory=dict)
    meadow: "meadow_mod.Meadow | None" = None  # density raster of the clutter the game generates (field, forest)
    crops: dict[tuple[int, int], dict[str, list[list[float]]]] = field(default_factory=dict)  # crop instances per chunk and kind
