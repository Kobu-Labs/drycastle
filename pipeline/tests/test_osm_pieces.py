"""A big area is fetched from Overpass in pieces, each cached, then merged."""

import json
from pathlib import Path

import pytest

from drycastle_pipeline import fetch
from drycastle_pipeline.config import SliceConfig
from drycastle_pipeline.geo import bbox_of_polygon
from drycastle_pipeline.http import HttpError
from tests.test_cache_fetch import FakeResponse, RouterClient, _geotiff

RING = [(-589500.0, -1250500.0), (-586500.0, -1250500.0), (-586500.0, -1247500.0)]  # 3 km


def element(kind: str, i: int, **extra) -> dict:
    return {"type": kind, "id": i, **extra}


def test_small_box_is_one_piece_and_big_box_is_a_covering_grid() -> None:
    small = bbox_of_polygon([(0, 0), (1000, 0), (0, 1000)])
    assert fetch.osm_boxes(small) == [small]
    big = bbox_of_polygon(RING, 25)
    pieces = fetch.osm_boxes(big)
    assert len(pieces) == 9  # a little over 3 km each way (box plus margin) at 1.5 km a side: 3 x 3
    assert min(p.west for p in pieces) == big.west and max(p.east for p in pieces) == big.east
    assert min(p.south for p in pieces) == big.south and max(p.north for p in pieces) == big.north
    area = sum((p.east - p.west) * (p.north - p.south) for p in pieces)
    assert area == pytest.approx((big.east - big.west) * (big.north - big.south))  # no gap, no overlap


def test_merge_keeps_each_object_once_in_a_fixed_order() -> None:
    a = json.dumps({"version": 0.6, "elements": [element("way", 9), element("node", 5)]}).encode()
    b = json.dumps({"version": 0.6, "elements": [element("relation", 1), element("way", 9), element("node", 2)]}).encode()
    merged = json.loads(fetch.merge_osm([a, b]))
    assert [(e["type"], e["id"]) for e in merged["elements"]] == [
        ("node", 2), ("node", 5), ("way", 9), ("relation", 1)]
    assert merged["version"] == 0.6
    assert fetch.merge_osm([a, b]) == fetch.merge_osm([b, a])  # the order of the pieces does not matter


class Pieces(RouterClient):
    """Overpass answers each query with one way of its own and one on every seam; terrain tiles are valid."""

    def __init__(self, fail_on_call: int | None = None, first_id: int = 100) -> None:
        super().__init__()
        self.first_id = first_id  # ids of the ways it invents: unique per client, so runs do not collide
        self.overpass_calls = 0
        self.fail_on_call = fail_on_call
        self.tiles = 0

    def request(self, method, urls, *, params=None, data=None):
        url = urls if isinstance(urls, str) else urls[0]
        if "overpass" in url and "way(" not in data["data"]:
            self.overpass_calls += 1
            if self.overpass_calls == self.fail_on_call:
                raise HttpError("giving up after 5 attempts (HTTP 504)")
            body = {"elements": [element("way", self.first_id + self.overpass_calls), element("way", 1)]}
            return FakeResponse(200, json.dumps(body).encode()), url
        if "/el/" in url:
            self.tiles += 1
            return FakeResponse(200, _geotiff(4 * self.tiles, 4, 1.0)), url
        return super().request(method, urls, params=params, data=data)


def test_pieces_are_merged_and_a_failed_run_resumes(tmp_path: Path) -> None:
    cfg = SliceConfig(osm_way=1, margin_m=25, polygon=tuple(RING))
    client = Pieces(fail_on_call=3)
    with pytest.raises(HttpError):
        fetch.run_fetch(cfg, tmp_path, client=client)
    assert len(list((tmp_path / "osm_tiles").glob("tile_?.json"))) == 2  # the two before the failure are kept
    assert not (tmp_path / "osm.json").exists()

    resumed = Pieces(first_id=200)
    result = fetch.run_fetch(cfg, tmp_path, client=resumed)
    assert resumed.overpass_calls == 7  # only the seven missing pieces
    assert "osm.json" in result.downloaded
    ids = [e["id"] for e in json.loads((tmp_path / "osm.json").read_text())["elements"]]
    assert ids == sorted(ids) and ids.count(1) == 1 and len(ids) == 10  # the seam way once, nine own ways

    again = Pieces()
    fetch.run_fetch(cfg, tmp_path, client=again)
    assert again.overpass_calls == 0  # cached
