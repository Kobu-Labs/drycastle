import json
from pathlib import Path

import pytest
import requests

from suchohrad_pipeline import fetch
from suchohrad_pipeline.cache import Source, fetch_cached, sidecar_path, write_manifest
from suchohrad_pipeline.config import SliceConfig
from suchohrad_pipeline.http import HttpError, PoliteClient

SRC = Source("Test source", "CC0", "https://example.invalid/licence", "Test attribution")
TIFF = b"II*\x00" + b"\x00" * 16


def test_cache_miss_hit_force_and_param_change(tmp_path: Path) -> None:
    path = tmp_path / "a.json"
    calls: list[int] = []

    def fetcher() -> tuple[bytes, str]:
        calls.append(1)
        return b'{"x": 1}', "https://example.invalid/api"

    req = {"q": "one"}
    assert fetch_cached(path, SRC, req, fetcher) is True  # miss
    assert fetch_cached(path, SRC, req, fetcher) is False  # hit
    assert len(calls) == 1
    assert fetch_cached(path, SRC, req, fetcher, force=True) is True  # force
    assert len(calls) == 2
    assert fetch_cached(path, SRC, {"q": "two"}, fetcher) is True  # params changed
    assert len(calls) == 3


def test_corrupt_or_missing_files_trigger_refetch(tmp_path: Path) -> None:
    path = tmp_path / "a.json"
    n = []

    def fetcher() -> tuple[bytes, str]:
        n.append(1)
        return b"data", "u"

    fetch_cached(path, SRC, {}, fetcher)
    path.write_bytes(b"tampered")
    assert fetch_cached(path, SRC, {}, fetcher) is True
    sidecar_path(path).unlink()
    assert fetch_cached(path, SRC, {}, fetcher) is True
    assert len(n) == 3


def test_sidecar_metadata(tmp_path: Path) -> None:
    path = tmp_path / "a.bin"
    fetch_cached(path, SRC, {"p": 1}, lambda: (b"hello", "https://example.invalid/x"))
    meta = json.loads(sidecar_path(path).read_text())
    assert meta["sha256"] == "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
    assert meta["url"] == "https://example.invalid/x"
    assert meta["request"] == {"p": 1}
    assert meta["source"] == "Test source"
    assert meta["licence"] == "CC0"
    assert meta["attribution"] == "Test attribution"
    assert meta["size_bytes"] == 5
    assert meta["fetched_at"].endswith("+00:00")


def test_manifest_lists_sources_and_deduplicated_attribution(tmp_path: Path) -> None:
    a, b = tmp_path / "a.json", tmp_path / "b.json"
    other = Source("Other", "CC0", "u", "Test attribution")
    fetch_cached(a, SRC, {}, lambda: (b"1", "u"))
    fetch_cached(b, other, {}, lambda: (b"2", "u"))
    out = write_manifest(tmp_path, [SRC, other], [a, b])
    m = json.loads(out.read_text())
    assert [s["name"] for s in m["sources"]] == ["Test source", "Other"]
    assert m["attribution"] == ["Test attribution"]
    assert {f["file"] for f in m["files"]} == {"a.json", "b.json"}


class FakeResponse:
    def __init__(self, status: int, content: bytes) -> None:
        self.status_code = status
        self.content = content


class FakeSession:
    def __init__(self, responses: list) -> None:
        self.responses = list(responses)
        self.calls: list[tuple] = []
        self.headers: dict[str, str] = {}

    def request(self, method, url, **kw):
        self.calls.append((method, url, kw))
        item = self.responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


def make_client(session: FakeSession, **kw) -> tuple[PoliteClient, list[float]]:
    sleeps: list[float] = []
    client = PoliteClient(
        session=session,  # type: ignore[arg-type]
        sleep=sleeps.append,
        retries=kw.pop("retries", 3),
        backoff=2.0,
        min_interval=0,
        **kw,
    )
    return client, sleeps


def test_client_retries_with_backoff_and_rotates_urls() -> None:
    session = FakeSession([FakeResponse(502, b""), requests.Timeout(), FakeResponse(200, b"ok")])
    client, sleeps = make_client(session)
    resp, url = client.request("GET", ["https://a.invalid", "https://b.invalid"])
    assert resp.content == b"ok"
    assert url == "https://a.invalid"
    assert [c[1] for c in session.calls] == ["https://a.invalid", "https://b.invalid"] + [
        "https://a.invalid"
    ]
    assert sleeps == [2.0, 4.0]
    assert session.headers["User-Agent"] == "suchohrad-pipeline"
    assert all(c[2]["timeout"] for c in session.calls)


def test_client_gives_up_and_fails_fast_on_4xx() -> None:
    client, _ = make_client(FakeSession([FakeResponse(503, b"")] * 2), retries=1)
    with pytest.raises(HttpError, match="giving up"):
        client.request("GET", "https://a.invalid")
    client, _ = make_client(FakeSession([FakeResponse(404, b"")]))
    with pytest.raises(HttpError, match="404"):
        client.request("GET", "https://a.invalid")


# --- full stage with a fake HTTP layer and synthetic data -------------------------------

WAY = {
    "elements": [
        {
            "type": "way",
            "id": 1,
            "geometry": [
                {"lat": 48.5, "lon": 17.0},
                {"lat": 48.5, "lon": 17.0002},
                {"lat": 48.5002, "lon": 17.0002},
                {"lat": 48.5002, "lon": 17.0},
                {"lat": 48.5, "lon": 17.0},
            ],
        }
    ]
}
OSM_DATA = {
    "elements": [
        {"type": "way", "id": 2, "tags": {"building": "yes"}, "geometry": []},
        {"type": "way", "id": 3, "tags": {"highway": "residential"}, "geometry": []},
    ]
}
PARCELS = {"type": "FeatureCollection", "features": [{"type": "Feature", "geometry": None}]}


class RouterClient(PoliteClient):
    """Routes requests by URL to canned bodies; counts calls; never touches the network."""

    def __init__(self) -> None:
        super().__init__(session=FakeSession([]), min_interval=0)  # type: ignore[arg-type]
        self.calls: list[str] = []

    def request(self, method, urls, *, params=None, data=None):
        url = urls if isinstance(urls, str) else urls[0]
        self.calls.append(url)
        if "overpass" in url:
            body = WAY if "way(" in data["data"] else OSM_DATA
            return FakeResponse(200, json.dumps(body).encode()), url
        if "/cp/" in url:
            return FakeResponse(200, json.dumps(PARCELS).encode()), url
        return FakeResponse(200, TIFF), url


def test_stage_downloads_then_skips_then_forces(tmp_path: Path) -> None:
    cfg = SliceConfig(osm_way=1, radius_m=50, margin_m=10)
    client = RouterClient()
    first = fetch.run_fetch(cfg, tmp_path, client=client)
    assert sorted(first.downloaded) == ["dmr5.tif", "osm.json", "parcels.geojson"]
    assert len(client.calls) == 4  # centre, osm, parcels, dmr
    for name in first.downloaded:
        assert (tmp_path / name).is_file()
        assert sidecar_path(tmp_path / name).is_file()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert "© OpenStreetMap contributors (ODbL)" in manifest["attribution"]
    assert "UGKK SR, GKU Bratislava (CC BY 4.0)" in manifest["attribution"]
    assert len(manifest["files"]) == 3

    client.calls.clear()
    second = fetch.run_fetch(cfg, tmp_path, client=client)
    assert second.downloaded == []
    assert client.calls == []  # fully cached, no network at all

    forced = fetch.run_fetch(cfg, tmp_path, client=client, force=True)
    assert len(forced.downloaded) == 3
    assert len(client.calls) == 4

    client.calls.clear()
    fetch.run_fetch(SliceConfig(osm_way=1, radius_m=80, margin_m=10), tmp_path, client=client)
    assert len(client.calls) == 3  # centre cached; the three data files refetched for new bbox


def test_dmr_params_snap_outwards_and_osm_query_covers_layers() -> None:
    from suchohrad_pipeline.geo import compute_bbox

    bbox = compute_bbox(17.0, 48.5, 100, 25)
    params = fetch.dmr_params(bbox)
    assert params["coverageId"] == "el__EL.GridCoverage"
    e0, e1 = (int(v) for v in params["subset"][0][2:-1].split(","))
    assert e0 <= bbox.min_e and e1 >= bbox.max_e
    q = fetch.osm_query(bbox)
    for key in ("building", "highway", "barrier", "landuse", "tree|wood|water", "street_lamp"):
        assert key in q


def test_bad_dmr_response_is_rejected(tmp_path: Path) -> None:
    class Bad(RouterClient):
        def request(self, method, urls, *, params=None, data=None):
            url = urls if isinstance(urls, str) else urls[0]
            if "/el/" in url:
                return FakeResponse(200, b"<ExceptionReport/>"), url
            return super().request(method, urls, params=params, data=data)

    with pytest.raises(HttpError, match="GeoTIFF"):
        fetch.run_fetch(SliceConfig(osm_way=1), tmp_path, client=Bad())
    assert not (tmp_path / "dmr5.tif").exists()


# --- village-sized fetches: DMR tiling and mosaic, WFS paging ------------------------------


def test_dmr_tiles_single_when_small_and_cover_box_when_large() -> None:
    from suchohrad_pipeline.geo import compute_bbox

    small = compute_bbox(17.0, 48.5, 100, 25)
    assert fetch.dmr_tiles(small) == [fetch.dmr_params(small)]
    big = compute_bbox(17.0, 48.5, 1400, 25)
    tiles = fetch.dmr_tiles(big)
    assert len(tiles) > 1

    def rng(p: dict, i: int) -> tuple[int, int]:
        a, b = (int(v) for v in p["subset"][i][2:-1].split(","))
        return a, b

    full_e, full_n = rng(fetch.dmr_params(big), 0), rng(fetch.dmr_params(big), 1)
    assert min(rng(t, 0)[0] for t in tiles) == full_e[0]
    assert max(rng(t, 0)[1] for t in tiles) == full_e[1]
    assert min(rng(t, 1)[0] for t in tiles) == full_n[0]
    assert max(rng(t, 1)[1] for t in tiles) == full_n[1]
    assert all(rng(t, 0)[1] - rng(t, 0)[0] <= fetch.DMR_TILE_M for t in tiles)
    assert all(rng(t, 1)[1] - rng(t, 1)[0] <= fetch.DMR_TILE_M for t in tiles)


def _geotiff(x0: float, y0: float, value: float) -> bytes:
    import numpy as np
    from rasterio.io import MemoryFile
    from rasterio.transform import from_origin

    with MemoryFile() as mem:
        with mem.open(driver="GTiff", height=4, width=4, count=1, dtype="float32",
                      crs="EPSG:3046", transform=from_origin(x0, y0, 1, 1)) as dst:
            dst.write(np.full((1, 4, 4), value, dtype="float32"))
        return mem.read()


def test_mosaic_merges_adjacent_tiles() -> None:
    from rasterio.io import MemoryFile

    merged = fetch.mosaic_tiffs([_geotiff(0, 4, 1.0), _geotiff(4, 4, 2.0)])
    with MemoryFile(merged) as mem, mem.open() as src:
        assert (src.width, src.height) == (8, 4)
        row = src.read(1)[0]
        assert list(row[:4]) == [1.0] * 4 and list(row[4:]) == [2.0] * 4
        assert src.crs.to_epsg() == 3046


def test_large_dmr_is_fetched_per_tile_cached_and_mosaicked(tmp_path: Path, monkeypatch) -> None:
    tile_calls: list[dict] = []

    class Tiled(RouterClient):
        def request(self, method, urls, *, params=None, data=None):
            url = urls if isinstance(urls, str) else urls[0]
            if "/el/" in url:
                tile_calls.append(params)
                i = len(tile_calls) - 1
                return FakeResponse(200, _geotiff(4 * i, 4, float(i))), url
            return super().request(method, urls, params=params, data=data)

    monkeypatch.setattr(fetch, "DMR_TILE_M", 800)
    cfg = SliceConfig(osm_way=1, radius_m=700, margin_m=10)
    client = Tiled()
    fetch.run_fetch(cfg, tmp_path, client=client)
    n = len(tile_calls)
    assert n > 1
    assert (tmp_path / "dmr5.tif").is_file()
    assert len(list((tmp_path / "dmr_tiles").glob("*.tif"))) == n
    tile_calls.clear()
    fetch.run_fetch(cfg, tmp_path, client=client)
    assert tile_calls == []  # cached
    fetch.run_fetch(cfg, tmp_path, client=client, force=True)
    assert len(tile_calls) == n


def test_wfs_paging_follows_full_pages_only() -> None:
    def page(n: int) -> bytes:
        feats = [{"type": "Feature", "geometry": None, "properties": {"n": i}} for i in range(n)]
        return json.dumps({"type": "FeatureCollection", "features": feats}).encode()

    class Paged(PoliteClient):
        def __init__(self, sizes: list[int]) -> None:
            super().__init__(session=FakeSession([]), min_interval=0)  # type: ignore[arg-type]
            self.sizes, self.starts = sizes, []

        def request(self, method, urls, *, params=None, data=None):
            self.starts.append(params["startIndex"])
            return FakeResponse(200, page(self.sizes.pop(0))), "https://x.invalid"

    params = {**fetch.parcel_params(), "count": 3}
    one = Paged([2])
    body, _ = fetch._parcels_paged(one, params)
    assert one.starts == [0] and len(json.loads(body)["features"]) == 2
    many = Paged([3, 3, 1])
    body, _ = fetch._parcels_paged(many, params)
    assert many.starts == [0, 3, 6] and len(json.loads(body)["features"]) == 7
