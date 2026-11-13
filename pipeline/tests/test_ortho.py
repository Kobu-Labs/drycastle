"""Orthophoto reference download: tile maths and the mosaic (no network)."""

import io
import json
from pathlib import Path

import pytest
from PIL import Image

from drycastle_pipeline import ortho
from drycastle_pipeline.http import HttpError


class FakeResponse:
    def __init__(self, content: bytes) -> None:
        self.content = content


class FakeClient:
    """Serves a 256 px PNG per tile whose red/green channels carry its column and row."""

    def __init__(self, missing: set[tuple[int, int]] = frozenset()) -> None:
        self.urls: list[str] = []
        self.missing = missing

    def request(self, method: str, url: str, **_kw):
        self.urls.append(url)
        row, col = (int(v) for v in url.rsplit("/", 2)[-2:])
        if (row, col) in self.missing:
            raise HttpError(f"HTTP 404 from {url}")
        img = Image.new("RGB", (ortho.TILE_PX, ortho.TILE_PX), (col % 256, row % 256, 7))
        buf = io.BytesIO()
        img.save(buf, "PNG")
        return FakeResponse(buf.getvalue()), url


def test_pixel_size_halves_per_level_and_matches_the_capabilities() -> None:
    assert ortho.pixel_m(12) == pytest.approx(0.2976, abs=1e-4)  # 1063.0188 * 0.00028
    assert ortho.pixel_m(11) == pytest.approx(2 * ortho.pixel_m(12))
    assert ortho.pixel_m(0) == pytest.approx(1219.15, abs=0.01)


def test_pixel_box_and_bounds_cover_the_extent() -> None:
    extent = (-589700.0, -1251800.0, -586900.0, -1247600.0)
    for level in (9, 11, 12):
        box = ortho.pixel_box(extent, level)
        minx, miny, maxx, maxy = ortho.pixel_bounds(box, level)
        assert minx <= extent[0] and miny <= extent[1] and maxx >= extent[2] and maxy >= extent[3]
        res = ortho.pixel_m(level)
        assert maxx - extent[2] < res + 1e-6 and extent[0] - minx < res + 1e-6  # tight, to a pixel
        assert maxy - extent[3] < res + 1e-6 and extent[1] - miny < res + 1e-6


def test_tiles_for_counts_whole_tiles() -> None:
    assert ortho.tiles_for((0, 0, 256, 256)) == [(0, 0)]
    assert ortho.tiles_for((255, 255, 257, 257)) == [(0, 0), (0, 1), (1, 0), (1, 1)]
    assert len(ortho.tiles_for((10, 10, 600, 300))) == 3 * 2


def test_map_extent_is_the_polygon_padded(tmp_path: Path) -> None:
    (tmp_path / "area.json").write_text(json.dumps({
        "bbox_wgs84": [16.85, 48.39, 16.87, 48.41],
        "polygon_epsg5514": [[0, 0], [100, 0], [100, 50], [0, 50]],
    }))
    assert ortho.map_extent(tmp_path, pad_m=10) == (-10, -10, 110, 60)


def test_build_downloads_once_and_places_tiles_in_the_mosaic(tmp_path: Path) -> None:
    raw, out = tmp_path / "raw", tmp_path / "ortho"
    raw.mkdir()
    level = 11
    res = ortho.pixel_m(level)
    # a polygon 300 x 200 px big, starting 100 px right and 150 px down into a tile: 2 x 2 tiles
    x0 = ortho.ORIGIN_X + (1000 * 256 + 100) * res
    y_top = ortho.ORIGIN_Y - (2000 * 256 + 150) * res
    ring = [[x0, y_top - 200 * res], [x0 + 300 * res, y_top - 200 * res], [x0 + 300 * res, y_top]]
    (raw / "area.json").write_text(json.dumps({"polygon_epsg5514": ring}))

    client = FakeClient()
    doc = ortho.build(raw, out, level, client, pad_m=0)
    assert doc["tiles"] == len(client.urls) == doc["tiles_downloaded"] == 4  # 2 columns x 2 rows
    w, h = doc["size_px"]
    assert 299 <= w <= 303 and 199 <= h <= 203
    minx, miny, maxx, maxy = doc["bounds_epsg5514"]
    assert minx <= x0 and maxy >= y_top
    assert maxx - minx == pytest.approx(w * res) and maxy - miny == pytest.approx(h * res)

    image = Image.open(out / "ortho.jpg")
    # pixel (5, 5) lies in tile col 1000 (x 100 + 5), row 2000; far right in col 1001 (jpeg: approximate)
    r, g, _ = image.getpixel((5, 5))
    assert (r, g) == pytest.approx((1000 % 256, 2000 % 256), abs=6)
    r2, _, _ = image.getpixel((w - 5, 5))
    assert r2 == pytest.approx(1001 % 256, abs=6)

    again = FakeClient()
    ortho.build(raw, out, level, again, pad_m=0)  # tiles are on disk: nothing is fetched
    assert again.urls == []


def test_missing_tiles_are_left_blank_and_counted(tmp_path: Path) -> None:
    raw, out = tmp_path / "raw", tmp_path / "ortho"
    raw.mkdir()
    res = ortho.pixel_m(11)
    x0, y1 = ortho.ORIGIN_X + 500 * 256 * res, ortho.ORIGIN_Y - 600 * 256 * res
    ring = [[x0 + 10, y1 - 100], [x0 + 20, y1 - 100], [x0 + 20, y1 - 10]]
    (raw / "area.json").write_text(json.dumps({"polygon_epsg5514": ring}))
    doc = ortho.build(raw, out, 11, FakeClient(missing={(600, 500)}), pad_m=0)
    assert doc["tiles_missing"] == 1 and doc["tiles_downloaded"] == 0
    assert Image.open(out / "ortho.jpg").getpixel((1, 1)) == (0, 0, 0)


def test_too_many_tiles_and_bad_level_are_refused(tmp_path: Path) -> None:
    raw = tmp_path / "raw"
    raw.mkdir()
    (raw / "area.json").write_text(json.dumps({"polygon_epsg5514": [[0, 0], [90000, 0], [0, 90000]]}))
    with pytest.raises(ValueError, match="limit"):
        ortho.build(raw, tmp_path / "o", 12, FakeClient())
    with pytest.raises(ValueError, match="level"):
        ortho.build(raw, tmp_path / "o", 13, FakeClient())
