"""Fetch stage: download raw source data for the slice into ``data/raw/`` with a disk cache."""

import json
import math
from dataclasses import dataclass, field
from itertools import pairwise
from pathlib import Path

import rasterio
from rasterio.io import MemoryFile
from rasterio.merge import merge

from drycastle_pipeline.cache import Source, fetch_cached, write_manifest
from drycastle_pipeline.config import (
    DEFAULT_AREA,
    DEFAULT_CONFIG,
    DEFAULT_RAW_DIR,
    ConfigError,
    SliceConfig,
    area_paths,
    load_slice_config,
)
from drycastle_pipeline.geo import METRIC_CRS, Bbox, centroid_of_geometry, compute_bbox
from drycastle_pipeline.http import HttpError, PoliteClient

OVERPASS_URLS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
]
WFS_URL = "https://inspirews.skgeodesy.sk/geoserver/cp/ows"
WCS_URL = "https://inspirews.skgeodesy.sk/geoserver/el/ows"
PARCEL_TYPE = "cp:CP.CadastralParcel"
DMR_COVERAGE = "el__EL.GridCoverage"
OVERPASS_TIMEOUT_S = 90
WFS_PAGE = 5000  # features per WFS request; more pages are requested only when a page is full
DMR_TILE_M = 1000  # a DMR WCS request covers at most this many metres per side, else tiles

OSM = Source(
    name="OpenStreetMap (Overpass API)",
    licence="ODbL 1.0",
    licence_url="https://opendatacommons.org/licenses/odbl/1-0/",
    attribution="© OpenStreetMap contributors (ODbL)",
)
CADASTRE = Source(
    name="INSPIRE Cadastral Parcels (WFS, UGKK SR)",
    licence="CC BY 4.0",
    licence_url="https://creativecommons.org/licenses/by/4.0/",
    attribution="UGKK SR, GKU Bratislava (CC BY 4.0)",
)
DMR = Source(
    name="DMR 5.0 digital terrain model, 1 m (INSPIRE WCS, UGKK SR)",
    licence="CC BY 4.0",
    licence_url="https://creativecommons.org/licenses/by/4.0/",
    attribution="UGKK SR, GKU Bratislava (CC BY 4.0)",
)


@dataclass
class FetchResult:
    files: list[Path] = field(default_factory=list)
    downloaded: list[str] = field(default_factory=list)
    cached: list[str] = field(default_factory=list)


def osm_query(bbox: Bbox) -> str:
    b = bbox.overpass
    return (
        f"[out:json][timeout:{OVERPASS_TIMEOUT_S}];\n"
        "(\n"
        f'  nwr["building"]({b});\n'
        f'  way["highway"]({b});\n'
        f'  nwr["barrier"]({b});\n'
        f'  nwr["landuse"]({b});\n'
        f'  nwr["natural"~"^(tree|wood|water)$"]({b});\n'
        f'  nwr["highway"="street_lamp"]({b});\n'
        f'  nwr["man_made"="street_lamp"]({b});\n'
        ");\n"
        "out geom;"
    )


def way_query(way_id: int) -> str:
    return f"[out:json][timeout:{OVERPASS_TIMEOUT_S}];\nway({way_id});\nout geom;"


def parse_way_centre(body: bytes) -> tuple[float, float]:
    """(lon, lat) centroid from an Overpass ``out geom`` response holding one way."""
    elements = json.loads(body).get("elements", [])
    ways = [e for e in elements if e.get("type") == "way" and e.get("geometry")]
    if not ways:
        raise ConfigError("the configured OSM way was not found or has no geometry")
    points = [(p["lon"], p["lat"]) for p in ways[0]["geometry"]]
    return centroid_of_geometry(points)


def _overpass(client: PoliteClient, query: str) -> tuple[bytes, str]:
    resp, url = client.request("POST", OVERPASS_URLS, data={"data": query})
    try:
        payload = json.loads(resp.content)
    except json.JSONDecodeError as exc:
        raise HttpError("Overpass returned a non-JSON response") from exc
    if "elements" not in payload or "remark" in payload:
        raise HttpError(f"Overpass reported a problem: {payload.get('remark', 'no elements')}")
    return resp.content, url


def dmr_params(bbox: Bbox) -> dict:
    """WCS 2.0.1 GetCoverage parameters, snapped outwards to whole metres (DMR cell edges)."""
    return _dmr_subset(
        math.floor(bbox.min_e), math.ceil(bbox.max_e), math.floor(bbox.min_n), math.ceil(bbox.max_n)
    )


def dmr_tiles(bbox: Bbox, tile_m: int = DMR_TILE_M) -> list[dict]:
    """GetCoverage parameters covering the box in whole-metre tiles of at most ``tile_m``.

    A box that fits in one tile gives the single request ``dmr_params`` would give.
    """
    e0, e1 = math.floor(bbox.min_e), math.ceil(bbox.max_e)
    n0, n1 = math.floor(bbox.min_n), math.ceil(bbox.max_n)

    def split(a: int, b: int) -> list[tuple[int, int]]:
        parts = max(1, math.ceil((b - a) / tile_m))
        edges = [a + round((b - a) * i / parts) for i in range(parts + 1)]
        return list(pairwise(edges))

    return [_dmr_subset(ea, eb, na, nb) for na, nb in split(n0, n1) for ea, eb in split(e0, e1)]


def _dmr_subset(e0: int, e1: int, n0: int, n1: int) -> dict:
    return {
        "service": "WCS",
        "version": "2.0.1",
        "request": "GetCoverage",
        "coverageId": DMR_COVERAGE,
        "format": "image/tiff",
        "subset": [f"E({e0},{e1})", f"N({n0},{n1})"],
    }


def mosaic_tiffs(tiles: list[bytes]) -> bytes:
    """Merge GeoTIFF tiles (same CRS and resolution) into one GeoTIFF."""
    sources = [MemoryFile(t).open() for t in tiles]
    try:
        data, transform = merge(sources)
        profile = sources[0].profile
        profile.update(
            driver="GTiff", height=data.shape[1], width=data.shape[2], transform=transform
        )
        profile.pop("blockxsize", None)
        profile.pop("blockysize", None)
        profile.pop("tiled", None)
        with MemoryFile() as mem:
            with mem.open(**profile) as out:
                out.write(data)
            return mem.read()
    finally:
        for src in sources:
            src.close()


def parcel_params() -> dict:
    return {
        "service": "WFS",
        "version": "2.0.0",
        "request": "GetFeature",
        "typeNames": PARCEL_TYPE,
        "outputFormat": "application/json",
        "srsName": "EPSG:4326",
        "count": 5000,
    }


def _parcels_paged(client: PoliteClient, params: dict) -> tuple[bytes, str]:
    """WFS GetFeature, sorted; follows ``startIndex`` paging while a page comes back full."""
    page = int(params["count"])
    docs, start = [], 0
    while True:
        resp, url = client.request(
            "GET",
            WFS_URL,
            params={**params, "sortBy": "nationalCadastralReference", "startIndex": start},
        )
        try:
            doc = json.loads(resp.content)
        except json.JSONDecodeError as exc:
            raise HttpError("WFS returned a non-JSON response") from exc
        docs.append(doc)
        if len(doc.get("features", [])) < page:
            break
        start += page
    if len(docs) == 1:
        return resp.content, url
    merged = dict(docs[0])
    merged["features"] = [f for d in docs for f in d.get("features", [])]
    return json.dumps(merged).encode(), url


def _resolve_centre(
    client: PoliteClient, raw_dir: Path, cfg: SliceConfig, force: bool
) -> tuple[float, float]:
    path = raw_dir / "slice_centre.json"
    query = way_query(cfg.osm_way)

    def fetch() -> tuple[bytes, str]:
        body, url = _overpass(client, query)
        lon, lat = parse_way_centre(body)
        return json.dumps({"lon": lon, "lat": lat}).encode(), url

    fetch_cached(path, OSM, {"query": query}, fetch, force=force)
    centre = json.loads(path.read_text())
    return centre["lon"], centre["lat"]


def _wcs(client: PoliteClient, params: dict) -> tuple[bytes, str]:
    resp, url = client.request("GET", WCS_URL, params=params)
    if resp.content[:4] not in (b"II*\x00", b"MM\x00*", b"II+\x00", b"MM\x00+"):
        raise HttpError("WCS did not return a GeoTIFF (exception report?)")
    return resp.content, url


def write_area(
    raw_dir: Path, area: str, cfg: SliceConfig, lon: float, lat: float, bbox: Bbox
) -> None:
    """Record the fetched area (used later to tell which registry entries it covers)."""
    doc = {
        "area": area,
        "lon": lon,
        "lat": lat,
        "radius_m": cfg.radius_m,
        "margin_m": cfg.margin_m,
        "bbox_wgs84": [bbox.west, bbox.south, bbox.east, bbox.north],
    }
    (raw_dir / "area.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n")


def _record(result: FetchResult, path: Path, downloaded: bool) -> None:
    result.files.append(path)
    (result.downloaded if downloaded else result.cached).append(path.name)


def run_fetch(
    cfg: SliceConfig,
    raw_dir: Path = DEFAULT_RAW_DIR,
    *,
    force: bool = False,
    client: PoliteClient | None = None,
    area: str = DEFAULT_AREA,
) -> FetchResult:
    client = client or PoliteClient()
    raw_dir.mkdir(parents=True, exist_ok=True)
    result = FetchResult()

    lon, lat = _resolve_centre(client, raw_dir, cfg, force)
    bbox = compute_bbox(lon, lat, cfg.radius_m, cfg.margin_m)
    write_area(raw_dir, area, cfg, lon, lat, bbox)

    query = osm_query(bbox)
    path = raw_dir / "osm.json"
    _record(
        result,
        path,
        fetch_cached(path, OSM, {"query": query}, lambda: _overpass(client, query), force=force),
    )

    params = {**parcel_params(), "bbox": bbox.wfs}

    def fetch_parcels() -> tuple[bytes, str]:
        return _parcels_paged(client, params)

    path = raw_dir / "parcels.geojson"
    _record(
        result, path, fetch_cached(path, CADASTRE, {"params": params}, fetch_parcels, force=force)
    )

    tiles = dmr_tiles(bbox)
    path = raw_dir / "dmr5.tif"
    if len(tiles) == 1:
        request = {"params": tiles[0], "crs": METRIC_CRS}
        fetch_dmr = lambda: _wcs(client, tiles[0])
    else:
        request = {"tiles": tiles, "crs": METRIC_CRS}

        def fetch_dmr() -> tuple[bytes, str]:
            bodies, url = [], WCS_URL
            for i, tile in enumerate(tiles):
                tpath = raw_dir / "dmr_tiles" / f"tile_{i}.tif"
                fetch_cached(
                    tpath, DMR, {"params": tile, "crs": METRIC_CRS},
                    lambda tile=tile: _wcs(client, tile), force=force,
                )
                bodies.append(tpath.read_bytes())
            return mosaic_tiffs(bodies), url

    _record(result, path, fetch_cached(path, DMR, request, fetch_dmr, force=force))

    write_manifest(raw_dir, [OSM, CADASTRE, DMR], result.files)
    return result


def summarise(raw_dir: Path) -> list[str]:
    """Counts for the log: no coordinates or ids."""
    lines = []
    osm = json.loads((raw_dir / "osm.json").read_text())["elements"]
    buildings = sum(1 for e in osm if "building" in e.get("tags", {}))
    lines.append(f"OSM elements: {len(osm)} (buildings: {buildings})")
    parcels = json.loads((raw_dir / "parcels.geojson").read_text())["features"]
    lines.append(f"cadastral parcels: {len(parcels)}")
    with rasterio.open(raw_dir / "dmr5.tif") as ds:
        lines.append(f"DMR raster: {ds.width}x{ds.height} px, resolution {ds.res[0]:g} m")
    return lines


def run(
    force: bool = False,
    config_path: Path = DEFAULT_CONFIG,
    raw_dir: Path | None = None,
    area: str = DEFAULT_AREA,
    **_options: object,
) -> int:
    raw_dir = raw_dir or area_paths(area).raw
    try:
        cfg = load_slice_config(config_path, area)
        result = run_fetch(cfg, raw_dir, force=force, area=area)
    except (ConfigError, HttpError) as exc:
        print(f"fetch: error: {exc}")
        return 1
    for name in result.downloaded:
        print(f"fetch: downloaded {name}")
    for name in result.cached:
        print(f"fetch: cached {name} (unchanged)")
    for line in summarise(raw_dir):
        print(f"fetch: {line}")
    print(f"fetch: manifest at {raw_dir / 'manifest.json'}")
    return 0
