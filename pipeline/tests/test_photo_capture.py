"""Capturing a Street View tab with its camera (photo_capture): the address, the frame, the building in view."""

import json
from pathlib import Path

import pytest

from drycastle_pipeline import photo_capture as pc

WORK = Path(__file__).parents[2] / "data" / "village" / "work"


def test_the_address_gives_the_camera():
    v = pc.parse_view("https://www.google.com/maps/@48.4041159,16.860072,3a,75y,27.5h,91.7t/data=!3m6!1e1")
    assert v == pytest.approx({"lat": 48.4041159, "lon": 16.860072, "heading": 27.5, "pitch": 1.7, "fov": 75.0})
    assert pc.parse_view("https://www.google.com/maps/@48.4024653,16.8607311,17z") is None  # the map, not Street View
    assert pc.parse_view("https://example.com/@1.0,2.0,3a,75y,10h,90t") is None


def test_a_frame_is_masked_and_carries_its_length():
    mask = bytes([1, 2, 3, 4])
    f = pc.encode_frame(b"hello", mask)
    assert f[:2] == bytes([0x81, 0x80 | 5]) and f[2:6] == mask
    assert bytes(b ^ mask[i % 4] for i, b in enumerate(f[6:])) == b"hello"
    big = pc.encode_frame(b"x" * 70000, mask)
    assert big[1] == 0x80 | 127 and int.from_bytes(big[2:10], "big") == 70000
    assert pc.encode_frame(b"y" * 300, mask)[1] == 0x80 | 126


@pytest.mark.skipif(not (WORK / "buildings.json").exists(), reason="needs the village work data")
def test_the_building_in_the_middle_of_the_view_is_found():
    by_id = {b["id"]: b for b in json.loads((WORK / "buildings.json").read_text())["buildings"]}
    ring = by_id["w378920410"]["footprint"]["exterior"]
    cx, cy = sum(p[0] for p in ring) / 4, sum(p[1] for p in ring) / 4
    bid, edges, dist = pc.guess_building(cx - 4.0, cy - 14.0, 16.0)  # from the south-south-west, looking at it
    assert bid == "w378920410" and 1 in edges and 5 < dist < 15
    assert pc.guess_building(cx, cy - 14.0, 180.0, reach=3.0)[0] is None  # looking away, nothing within 3 m
