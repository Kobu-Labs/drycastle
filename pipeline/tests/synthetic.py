"""Synthetic map shared by the terrain, roads, props and export tests (no real data)."""

import json
from pathlib import Path

import numpy as np
from shapely.geometry import Polygon, box, mapping

from drycastle_pipeline.buildings import derive_building, load_building_rules
from drycastle_pipeline.roads import build_graph
from drycastle_pipeline.rules import load_section
from drycastle_pipeline.terrain import flatten_buildings, grid_from_samples, save_terrain

COVERAGE = box(10, 10, 210, 200)
# Absolute S-JTSK position of the local origin (0, 0) of the synthetic map.
SYNTHETIC_ORIGIN = (-500000.0, -1100000.0)


def _outline() -> list[list[float]]:
    """The map outline of the synthetic map: a ring around the local extent (absolute S-JTSK)."""
    ox, oy = SYNTHETIC_ORIGIN
    return [[ox - 50, oy - 50], [ox + 1100, oy - 50], [ox + 1100, oy + 1100], [ox - 50, oy + 1100], [ox - 50, oy - 50]]


def plane(x, y):
    """Height of the synthetic ground: a gentle slope; inside coverage."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    inside = (x >= 10) & (x <= 210) & (y >= 10) & (y <= 200)
    return 100 + 0.05 * x + 0.02 * y, inside


def make_terrain(step: float = 4.0):
    return grid_from_samples(250.0, step, COVERAGE, plane)


def write_ortho(folder: Path, origin: tuple[float, float], canopy_box, standalone: list[dict]) -> None:
    """The detector's output beside the work dir: a 250 x 250 px canopy.png (1 m per pixel, local (0, 0)..(250, 250);
    255 inside ``canopy_box`` = local xmin, ymin, xmax, ymax) and a trees.json."""
    from PIL import Image

    folder.mkdir(parents=True, exist_ok=True)
    mask = np.zeros((250, 250), np.uint8)
    x0, y0, x1, y1 = canopy_box
    mask[250 - int(y1) : 250 - int(y0), int(x0) : int(x1)] = 255  # row 0 is the north edge, y = 250
    Image.fromarray(mask).save(folder / "canopy.png")
    (folder / "trees.json").write_text(json.dumps({
        "canopy": {"file": "canopy.png", "pixel_m": 1.0,
                   "bounds_epsg5514": [origin[0], origin[1], origin[0] + 250, origin[1] + 250]},
        "standalone": standalone,
    }))


def parcel(pid: str, kind: str, geom: Polygon, buildings: list[str] | None = None) -> dict:
    c = geom.representative_point()
    return {
        "id": pid,
        "cadastral_ref": f"X.{pid}",
        "kind": kind,
        "polygon": mapping(geom),
        "anchor": [c.x, c.y],
        "chunk": [0, 0],
        "buildings": buildings or [],
    }


def line_feature(osm_id: str, highway: str, coords: list, **tags) -> dict:
    return {
        "type": "Feature",
        "properties": {"osm_id": osm_id, "highway": highway, **tags},
        "geometry": {"type": "LineString", "coordinates": coords},
    }


def make_workspace(tmp: Path) -> dict[str, Path]:
    work, raw = tmp / "work", tmp / "raw"
    (work / "normalised").mkdir(parents=True)
    raw.mkdir()
    rules = load_building_rules()
    b1 = derive_building("w1", "P-0001", box(60, 55, 75, 70), {"roof:shape": "gabled"}, rules)
    # one list of ground units with the kinds of both parcels and ground areas (ground.py): the stages read the kind only
    parcels = [
        parcel("P-0001", "building_plot", box(40, 40, 95, 100), ["w1"]),
        parcel("P-0002", "garden", box(95, 40, 140, 100)),
        parcel("P-0003", "village", box(20, 100, 200, 112)),
        parcel("P-0004", "field", box(40, 115, 200, 190)),
        parcel("P-0005", "forest", box(145, 40, 200, 100)),
        parcel("P-0006", "field", box(20, 20, 38, 98)),
    ]
    (work / "parcels.json").write_text(
        json.dumps(
            {
                "crs": "synthetic",
                "origin_epsg5514": list(SYNTHETIC_ORIGIN),
                "area_polygon_epsg5514": _outline(),
                "chunk_size_m": 250.0,
                "parcels": parcels,
            }
        )
    )
    (work / "buildings.json").write_text(json.dumps({"buildings": [b1]}))
    # trees come from the orthophoto only: a forest patch on the forest parcel and two single trees
    write_ortho(tmp / "ortho", SYNTHETIC_ORIGIN, (150, 45, 195, 95), [
        {"x": 120.0, "y": 60.0, "radius_m": 3.0, "score": 0.3},
        {"x": 150.0, "y": 150.0, "radius_m": 3.0, "score": 0.3},
    ])
    highways = [
        line_feature("w10", "residential", [[20, 106], [120, 106], [200, 106]], surface="asphalt"),
        line_feature("w11", "track", [[120, 106], [120, 180]]),
    ]
    (work / "normalised" / "highways.geojson").write_text(json.dumps({"features": highways}))
    tree = {
        "type": "Feature",
        "properties": {"osm_id": "n1"},
        "geometry": {"type": "Point", "coordinates": [150, 150]},
    }
    (work / "normalised" / "trees.geojson").write_text(json.dumps({"features": [tree]}))
    (raw / "manifest.json").write_text(
        json.dumps({"attribution": ["A"], "files": [{"file": "osm.json", "sha256": "ab"}]})
    )
    overrides = tmp / "overrides.yaml"
    overrides.write_text("buildings: {}\n")

    terrain = make_terrain()
    elevations = flatten_buildings(terrain, [b1], 2.0, 6.0)
    save_terrain(work, terrain, elevations)
    graph = build_graph(highways, load_section("roads"))
    (work / "roads.json").write_text(json.dumps(graph, sort_keys=True))
    return {"work": work, "raw": raw, "overrides": overrides, "out": tmp / "out"}
