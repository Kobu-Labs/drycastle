"""Normalise stage: raw OSM + cadastral parcels (WGS84) -> local metres.

Reads ``data/village/raw/{osm.json,parcels.geojson,centre.json}`` and writes
``data/village/work/normalised/{buildings,highways,landuse,parcels,trees}.geojson`` plus ``frame.json``.
Coordinates in the output are local metres (EPSG:5514 axes, origin at the map centre).
"""

import json
from dataclasses import dataclass
from pathlib import Path

from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import linemerge, polygonize, unary_union

from drycastle_pipeline.config import DEFAULT_RAW_DIR, REPO_ROOT, map_paths
from drycastle_pipeline.frame import Frame

DEFAULT_WORK_DIR = REPO_ROOT / "data" / "village" / "work"
LANDUSE_CLASSES = ("landuse", "natural")
HIGHWAY_TAGS = ("width", "lanes", "surface", "oneway", "tracktype", "bridge", "tunnel")
LOCAL_CRS_NAME = "local metres: EPSG:5514 axes, origin at the map centre"


@dataclass
class OsmFeature:
    osm_id: str
    tags: dict
    geom: BaseGeometry  # WGS84 (lon, lat)


def _points(geometry: list[dict]) -> list[tuple[float, float]]:
    return [(p["lon"], p["lat"]) for p in geometry]


def _way_polygon(points: list[tuple[float, float]]) -> Polygon | None:
    if len(points) < 4 or points[0] != points[-1]:
        return None
    poly = Polygon(points)
    if not poly.is_valid:
        poly = poly.buffer(0)
    return poly if isinstance(poly, Polygon) and not poly.is_empty else None


def _rings(members: list[dict]) -> list[Polygon]:
    lines = [LineString(_points(m["geometry"])) for m in members if len(m.get("geometry", [])) > 1]
    if not lines:
        return []
    merged = linemerge(lines)
    return list(polygonize(merged))


def _relation_polygon(rel: dict) -> BaseGeometry | None:
    """Multipolygon relation with member geometry: outers minus inners."""
    members = rel.get("members", [])
    outer = _rings([m for m in members if m.get("role") == "outer"])
    inner = _rings([m for m in members if m.get("role") == "inner"])
    if not outer:
        return None
    geom = unary_union(outer)
    if inner:
        geom = geom.difference(unary_union(inner))
    return geom if not geom.is_empty else None


def is_street_lamp(tags: dict) -> bool:
    return tags.get("highway") == "street_lamp" or tags.get("man_made") == "street_lamp"


def parse_osm(raw: dict) -> dict[str, list[OsmFeature]]:
    """Group OSM elements (``out geom`` JSON) into buildings, highways, landuse, trees and street lamps."""
    out: dict[str, list[OsmFeature]] = {
        "buildings": [],
        "highways": [],
        "landuse": [],
        "trees": [],
        "street_lamps": [],
    }
    for el in raw.get("elements", []):
        tags = el.get("tags", {})
        if el["type"] == "node":
            if "lon" in el and "lat" in el:
                if tags.get("natural") == "tree":
                    out["trees"].append(OsmFeature(f"n{el['id']}", {}, Point(el["lon"], el["lat"])))
                if is_street_lamp(tags):
                    out["street_lamps"].append(
                        OsmFeature(f"n{el['id']}", {}, Point(el["lon"], el["lat"]))
                    )
            continue
        if el["type"] == "way" and el.get("geometry"):
            pts = _points(el["geometry"])
            osm_id = f"w{el['id']}"
            if "building" in tags:
                poly = _way_polygon(pts)
                if poly:
                    out["buildings"].append(OsmFeature(osm_id, tags, poly))
            if "highway" in tags and len(pts) > 1:
                out["highways"].append(OsmFeature(osm_id, tags, LineString(pts)))
            if any(c in tags for c in LANDUSE_CLASSES):
                poly = _way_polygon(pts)
                if poly:
                    out["landuse"].append(OsmFeature(osm_id, tags, poly))
        elif el["type"] == "relation" and any(c in tags for c in LANDUSE_CLASSES):
            geom = _relation_polygon(el)
            if geom:
                out["landuse"].append(OsmFeature(f"r{el['id']}", tags, geom))
    for feats in out.values():
        feats.sort(key=lambda f: f.osm_id)
    return out


def _collection(features: list[dict]) -> dict:
    return {
        "type": "FeatureCollection",
        "crs": {"type": "name", "properties": {"name": LOCAL_CRS_NAME}},
        "features": features,
    }


def _feature(geom: BaseGeometry, props: dict) -> dict:
    return {"type": "Feature", "properties": props, "geometry": geom.__geo_interface__}


def _landuse_props(f: OsmFeature) -> dict:
    cls = next(c for c in LANDUSE_CLASSES if c in f.tags)
    return {"osm_id": f.osm_id, "class": cls, "value": f.tags[cls]}


def _highway_props(f: OsmFeature) -> dict:
    props = {"osm_id": f.osm_id, "highway": f.tags["highway"]}
    props.update({k: f.tags[k] for k in HIGHWAY_TAGS if k in f.tags})
    return props


def _dump(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, indent=1, sort_keys=True) + "\n")


def area_polygon(raw_dir: Path) -> Polygon:
    """The outline everything is clipped to (absolute EPSG:5514), from ``area.json`` (written by fetch)."""
    path = raw_dir / "area.json"
    ring = json.loads(path.read_text()).get("polygon_epsg5514") if path.is_file() else None
    if not ring:
        raise FileNotFoundError(2, "no map outline", "area.json")
    return Polygon(ring)


def _inside_test(outline: Polygon, frame: Frame):
    """Predicate on local geometries: True when they touch the outline."""
    import shapely

    local = shapely.transform(
        outline, lambda c: c - [frame.origin_x, frame.origin_y]
    )
    shapely.prepare(local)
    return local.intersects


def run_normalise(
    raw_dir: Path = DEFAULT_RAW_DIR,
    work_dir: Path = DEFAULT_WORK_DIR,
) -> dict[str, int]:
    centre = json.loads((raw_dir / "centre.json").read_text())
    frame = Frame.at(centre["lon"], centre["lat"])
    osm = parse_osm(json.loads((raw_dir / "osm.json").read_text()))

    out_dir = work_dir / "normalised"
    out_dir.mkdir(parents=True, exist_ok=True)
    counts: dict[str, int] = {}

    def write(name: str, features: list[dict]) -> None:
        _dump(out_dir / f"{name}.geojson", _collection(features))
        counts[name] = len(features)

    outline = area_polygon(raw_dir)
    ring = [[round(x, 2), round(y, 2)] for x, y in outline.exterior.coords]
    inside = _inside_test(outline, frame)

    def local(features, props) -> list[dict]:
        """OSM features in local metres that touch the outline, as GeoJSON features."""
        out = []
        for f in features:
            geom = frame.to_local(f.geom)
            if inside(geom):
                out.append(_feature(geom, props(f)))
        return out

    write("buildings", local(osm["buildings"], lambda f: {"osm_id": f.osm_id, "tags": f.tags}))
    write("highways", local(osm["highways"], _highway_props))
    write("trees", local(osm["trees"], lambda f: {"osm_id": f.osm_id}))
    write("street_lamps", local(osm["street_lamps"], lambda f: {"osm_id": f.osm_id}))
    write("landuse", local(osm["landuse"], _landuse_props))

    parcels = json.loads((raw_dir / "parcels.geojson").read_text())["features"]
    parcel_features = []
    for feat in parcels:
        props = feat.get("properties", {})
        ref = props.get("nationalCadastralReference")
        if not ref or not feat.get("geometry"):
            continue
        geom = frame.to_local(shape(feat["geometry"]))
        if not inside(geom):
            continue
        parcel_features.append(
            _feature(
                geom,
                {
                    "cadastral_ref": ref,
                    "label": props.get("label"),
                    "area_value": props.get("areaValue"),
                },
            )
        )
    parcel_features.sort(key=lambda f: f["properties"]["cadastral_ref"])
    write("parcels", parcel_features)

    _dump(
        work_dir / "frame.json",
        {
            "crs": LOCAL_CRS_NAME,
            "lon": frame.lon,
            "lat": frame.lat,
            "origin_epsg5514": [frame.origin_x, frame.origin_y],
            # the map outline in absolute EPSG:5514 (terrain clips to it; the registry only retires entries inside it)
            "area_polygon_epsg5514": ring,
        },
    )
    return counts


def run(force: bool = False, **_options: object) -> int:
    paths = map_paths()
    try:
        counts = run_normalise(paths.raw, paths.work)
    except FileNotFoundError as exc:
        print(f"normalise: error: missing input {Path(str(exc.filename)).name}; run fetch first")
        return 1
    print("normalise: " + ", ".join(f"{k}: {v}" for k, v in counts.items()))
    return 0
