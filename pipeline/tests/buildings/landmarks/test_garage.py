"""The bus garage (garage): the game's contract and the meshes."""

import json
from pathlib import Path

import numpy as np
import pytest
from shapely import affinity
from shapely.geometry import Point, Polygon, box

from drycastle_pipeline import garage as g
from drycastle_pipeline.landmarks import build_landmark

BUILDINGS = Path(__file__).parents[2] / "data" / "village" / "work" / "buildings.json"
BUS = (2.56, 12.06, 3.1)  # vehicles.json: width, length, height


def _building(bid):
    return {"id": bid, "model": "bus_garage", "footprint": {"exterior": [list(p) for p in g.RINGS[bid]], "holes": []}}


def test_the_bus_fits_the_bay_and_the_door():
    info = g.garage_info({g.DEEP: 100.0})
    bay, door, park = Polygon(info["bay"]["footprint"]), info["doorway"], info["parking"]
    assert bay.is_valid and Polygon(g.RINGS[g.DEEP]).contains(bay)
    assert door["width"] >= BUS[0] + 0.8 and door["height"] >= BUS[2] + 0.3
    assert info["bay"]["ceiling_z"] - info["bay"]["floor_z"] >= BUS[2] + 0.3
    # the parked bus: its footprint, turned to the door, lies inside the bay with room at the walls
    bus = affinity.rotate(box(-BUS[0] / 2, -BUS[1] / 2, BUS[0] / 2, BUS[1] / 2), -park["yaw"], origin=(0, 0))
    bus = affinity.translate(bus, park["pos"][0], park["pos"][1])
    assert bay.contains(bus.buffer(0.3))
    assert park["vehicle"] == "bus" and park["yaw"] == door["yaw"] == pytest.approx(30.7, abs=0.5)  # out to the yard, north-east
    assert door["pos"][2] == park["pos"][2] == pytest.approx(100.0 + g.F)
    assert Polygon(g.RINGS[g.DEEP]).exterior.distance(Point(door["pos"][:2])) == pytest.approx(g.T / 2, abs=0.02)  # in the front wall


def test_the_doorway_is_open_in_the_collision_and_the_walls_are_not():
    m = build_landmark(_building(g.DEEP), detail=False)
    info = g.garage_info({})
    d = info["doorway"]
    out = np.array([np.sin(np.radians(d["yaw"])), np.cos(np.radians(d["yaw"]))])

    def hits(origin, direction, reach):  # triangles a horizontal ray meets within ``reach``
        o, dr = np.array(origin, float), np.array([*direction, 0.0])
        a, e1, e2 = m.tris[:, 0], m.tris[:, 1] - m.tris[:, 0], m.tris[:, 2] - m.tris[:, 0]
        p = np.cross(dr, e2)
        det = (e1 * p).sum(1)
        ok = np.abs(det) > 1e-9
        det = np.where(ok, det, 1.0)
        t = o - a
        u = (t * p).sum(1) / det
        q = np.cross(t, e1)
        v = (q @ dr) / det
        w = (e2 * q).sum(1) / det
        return int((ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (w > 1e-6) & (w < reach)).sum())

    start = np.array(d["pos"][:2]) + out * 2.0
    for across in (-d["width"] / 2 + 0.1, 0.0, d["width"] / 2 - 0.1):  # through the doorway, at bus height
        side = np.array([out[1], -out[0]]) * across
        assert hits([*(start + side), g.F + 3.2], -out, 6.0) == 0
    assert hits([*start, g.F + g.DOOR_H + 0.3], -out, 6.0) > 0  # the lintel
    assert hits([*(start + np.array([out[1], -out[0]]) * (d["width"] / 2 + 0.4)), 1.0], -out, 6.0) > 0  # the wall beside it


def test_meshes_build_and_refuse_a_changed_footprint():
    for bid in (g.DEEP, g.SIDE):
        vis, col = build_landmark(_building(bid), True), build_landmark(_building(bid), False)
        assert vis.face_count > col.face_count >= 24
        assert vis.tris[..., 2].max() > col.tris[..., 2].max() - 0.5
        assert build_landmark(_building(bid), True).tris.tobytes() == vis.tris.tobytes()  # the rust pattern is fixed
    with pytest.raises(ValueError):
        g.garage_mesh({"id": g.DEEP, "footprint": {"exterior": [[0, 0], [1, 0], [1, 1]], "holes": []}})


@pytest.mark.skipif(not BUILDINGS.exists(), reason="needs the village work data")
def test_the_footprints_are_the_villages():
    by_id = {b["id"]: b for b in json.loads(BUILDINGS.read_text())["buildings"]}
    for bid in (g.DEEP, g.SIDE):
        assert g.garage_mesh(by_id[bid], True).face_count > 100
