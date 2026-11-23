"""The mesh every generator produces and the map exports.

A mesh is an array of triangles ``tris`` (n, 3, 3) in local metres (x east, y north, z up) with
one flat RGB colour per triangle ``colours`` (n, 3) uint8. Triangles are wound counter-clockwise
seen from outside.
"""

from dataclasses import dataclass

import mapbox_earcut as earcut
import numpy as np
from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry

from drycastle_pipeline.core.geometry import polygons

_MIN_PIECE_AREA = 1e-6


@dataclass
class Mesh:
    tris: np.ndarray  # (n, 3, 3) float64
    colours: np.ndarray  # (n, 3) uint8
    # (n, 2) per triangle: the (x, y) of the tree base the triangle belongs to (local metres), NaN for any other
    # triangle; None = no tree in the mesh. Export writes it per vertex as the second UV set (TEXCOORD_1).
    bases: np.ndarray | None = None

    @classmethod
    def empty(cls) -> "Mesh":
        return cls(np.zeros((0, 3, 3)), np.zeros((0, 3), np.uint8))

    @classmethod
    def concat(cls, meshes: list["Mesh"]) -> "Mesh":
        if not meshes:
            return cls.empty()
        bases = None
        if any(m.bases is not None for m in meshes):
            bases = np.concatenate(
                [m.bases if m.bases is not None else np.full((m.face_count, 2), np.nan) for m in meshes]
            )
        return cls(
            np.concatenate([m.tris for m in meshes]), np.concatenate([m.colours for m in meshes]), bases
        )

    @property
    def face_count(self) -> int:
        return len(self.tris)


def solid_polygons(geom: BaseGeometry) -> list[Polygon]:
    return polygons(geom, _MIN_PIECE_AREA)

def triangulate(poly: Polygon) -> list[tuple[tuple[float, float], ...]]:
    """Triangles (as 2D vertex triples) covering ``poly`` including holes."""
    rings = [poly.exterior, *poly.interiors]
    pts = np.concatenate([np.asarray(r.coords)[:-1] for r in rings]).astype(np.float64)
    ends = np.cumsum([len(r.coords) - 1 for r in rings]).astype(np.uint32)
    idx = earcut.triangulate_float64(pts, ends)
    return [tuple(map(tuple, pts[idx[i : i + 3]])) for i in range(0, len(idx), 3)]
