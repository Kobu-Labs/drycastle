"""The church's inside: hollow side aisles opened to the nave by arcades, real windows with glass panes, anchors on real surfaces.

Local frame of ``church_solids`` (u across, v along, z up). ``church_info`` / ``church_panes`` of a square footprint round
the origin with ``model_axis`` pi/2 give map metres that are (u, v) exactly.
"""

import itertools
import math
from collections import deque
from types import SimpleNamespace

import numpy as np
import pytest

from drycastle_pipeline import church_interior as ci
from drycastle_pipeline.landmarks import church_solids

UNIT = {"id": "church", "footprint": {"exterior": [[-1, -1], [1, -1], [1, 1], [-1, 1]]}, "model_axis": math.pi / 2}


@pytest.fixture(scope="module")
def collision():
    return SimpleNamespace(tris=np.array(church_solids(False).tris, float))


@pytest.fixture(scope="module")
def visual():
    return SimpleNamespace(tris=np.array(church_solids(True).tris, float))


@pytest.fixture(scope="module")
def info():
    return ci.church_info(UNIT)


def _hits(mesh, a, b) -> np.ndarray:
    """Segment parameters t in (0, 1) where the segment a..b meets a triangle of the mesh."""
    a, b = np.asarray(a, float), np.asarray(b, float)
    d = b - a
    v0, e1, e2 = mesh.tris[:, 0], mesh.tris[:, 1] - mesh.tris[:, 0], mesh.tris[:, 2] - mesh.tris[:, 0]
    p = np.cross(d, e2)
    det = np.einsum("ij,ij->i", e1, p)
    ok = np.abs(det) > 1e-12
    det = np.where(ok, det, 1.0)
    s = a - v0
    u = np.einsum("ij,ij->i", s, p) / det
    q = np.cross(s, e1)
    v = (q @ d) / det
    t = np.einsum("ij,ij->i", e2, q) / det
    return t[ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-9) & (t < 1 - 1e-9)]


def _blocked(mesh, a, b) -> bool:
    return len(_hits(mesh, a, b)) > 0


def test_aisles_are_hollow_and_walkable(collision) -> None:
    for al, u in ((ci.AISLES[0], -4.17), (ci.AISLES[1], 6.42)):
        lo, hi = al.iv
        for z in (0.5, 1.0, 1.8, 2.6):  # the length of the aisle, from past the altar at its rear, at standing height
            assert not _blocked(collision, (u, lo + 1.2, z), (u, hi - 0.3, z))
        assert not _blocked(collision, (u, 1.0, ci.F + 0.1), (u, 1.0, al.ceiling(u) - 0.1))  # floor to ceiling
        assert _blocked(collision, (u, 1.0, 1.0), (u, 1.0, -1.0))  # a floor under it
        assert _blocked(collision, (u, 1.0, al.ceiling(u) - 0.1), (u, 1.0, al.ceiling(u) + 0.3))  # a roof over it
        # the outer wall is solid between its windows, the aisle ends are walled
        out = -1 if al.left else 1
        assert _blocked(collision, (u, 1.5, 1.0), (al.in_u + out * 2.0, 1.5, 1.0))
        assert _blocked(collision, (u, 0.0, 1.0), (u, al.v1 + 1.0, 1.0))
        assert _blocked(collision, (u, 0.0, 1.0), (u, al.v0 - 1.0, 1.0))


def test_nave_opens_to_the_aisles_by_arcades_and_stays_solid_above(collision) -> None:
    for name, al in (("left", ci.AISLES[0]), ("right", ci.AISLES[1])):
        side = -1 if al.left else 1
        for lo, hi in ci.ARCADES[name]:
            mid = (lo + hi) / 2
            assert hi - lo >= 1.2
            for z in (0.5, 1.2, 2.0):  # a standing player passes through every arch
                for dv in (-0.4, 0.0, 0.4) if hi - lo > 1.8 else (0.0,):
                    assert not _blocked(collision, (ci.CU, mid + dv, z), (al.in_u, mid + dv, z))
        for v in ci.BAYS:  # the pillars between the arches are solid
            if any(abs(v - lo) < 0.5 or abs(v - hi) < 0.5 for lo, hi in ci.ARCADES[name]):
                assert _blocked(collision, (ci.CU, v, 1.5), (al.in_u, v, 1.5))
        # above the arcade the nave wall is whole, clerestory windows apart
        probe = (ci.NX0 + ci.NX1) / 2
        assert _blocked(collision, (probe, ci.WIN_V[1], 4.4), (probe + side * 4.0, ci.WIN_V[1], 4.4))
    # the right wall holds everywhere the aisle does not stand: the front bay is solid
    assert _blocked(collision, (ci.CU, 12.5, 1.5), (ci.AISLES[1].u1 + 1, 12.5, 1.5))


def test_door_and_walkways_are_wide(collision, info) -> None:
    assert info["doorway"]["width"] >= 2.0
    samples = set()
    for seat in info["seats"]:
        if seat["floor"]:
            continue  # the gallery stair is a ramp as before
        pts = np.array([p[:3] for p in seat["path"]] + [seat["pos"]])
        for a, b in itertools.pairwise(pts):
            for f in np.linspace(0, 1, max(2, int(np.linalg.norm(b - a) / 0.5) + 1)):
                samples.add(tuple(np.round(a + (b - a) * f, 2)))
    for p in map(np.array, sorted(samples)):
        for ang in np.linspace(0, 2 * math.pi, 8, endpoint=False):
            r = 0.45  # the citizens are 0.5 m wide; the pews are the game's props
            q = p + [r * math.cos(ang), r * math.sin(ang), 0.0]
            assert not _blocked(collision, p + [0, 0, 0.7], q + [0, 0, 0.7]), p.tolist()


def test_window_panes_one_per_opening_and_see_through(collision) -> None:
    panes = ci.church_panes(UNIT)
    assert len(panes) == len(ci.WINDOWS) == 23  # 10 clerestory, 2 chancel, 5 + 5 aisle, 1 facade
    assert len({n for n, _, _ in panes}) == 23
    per_wall: dict[str, int] = {}
    for w in ci.WINDOWS:
        per_wall[w.wall] = per_wall.get(w.wall, 0) + 1
    assert per_wall == {"nave_left": 5, "nave_right": 5, "chancel": 2, "aisle_left": 5, "aisle_right": 5, "front": 1}
    for (_, tris, meta), w in zip(panes, ci.WINDOWS, strict=True):
        n = np.array([*meta["normal"], 0.0])
        c = np.array(meta["centre"])
        assert np.linalg.norm(n) == pytest.approx(1.0)
        # a level line through the pane centre leaves the church through the opening: nothing in it
        assert not _blocked(collision, c - n * 0.6, c + n * 0.6)
        # and the wall is there beside it
        along = np.array([-n[1], n[0], 0.0])
        assert _blocked(collision, c + along * (meta["width_m"] / 2 + 0.3) - n * 0.6, c + along * (meta["width_m"] / 2 + 0.3) + n * 0.6)
        tri = np.array(tris)
        assert np.allclose(tri[:, :, :2] @ n[:2], c[:2] @ n[:2], atol=1e-6)  # the glass is flat, in the wall's middle plane
        assert w.n * (n[0] if w.axis == "v" else n[1]) > 0
    for _, _, meta in panes:
        assert meta["kind"] == "window" and meta["breakable"] and meta["width_m"] > 0.4 and meta["height_m"] > 0.7


def test_window_frames_are_thin_dark_and_hold_the_glass(visual) -> None:
    from drycastle_pipeline.buildings import hex_to_rgb
    from drycastle_pipeline.landmarks import IRON

    iron = np.array(hex_to_rgb(IRON))
    cols = np.array(church_solids(True).cols)
    dark = (cols == iron).all(axis=1)
    assert dark.any()
    pts = visual.tris[dark].reshape(-1, 3)
    # every window has a frame: look for dark geometry around each pane centre, within the wall's middle
    for _, _, meta in ci.church_panes(UNIT):
        c = np.array(meta["centre"])
        near = pts[np.linalg.norm(pts - c, axis=1) < max(meta["width_m"], meta["height_m"])]
        assert len(near) > 8
        n = np.array([*meta["normal"], 0.0])
        depth = np.abs((near - c) @ n)
        assert depth.max() <= ci.FRAME_D / 2 + 1e-6  # centred in the wall, 12 cm deep


def _anchor_on_surface(mesh, an) -> bool:
    p, n = np.array(an["pos"]), np.array(an["normal"])
    t = _hits(mesh, p + n * 0.1, p - n * 0.25)
    return len(t) > 0 and (t * 0.35).min() < 0.1 + 0.12


def test_every_demon_anchor_lies_on_a_solid_surface(visual, info) -> None:
    bad = [an["id"] for an in info["anchors"] if not _anchor_on_surface(visual, an)]
    assert not bad, bad


def test_demon_anchors_cover_nave_walls_vault_and_aisles_and_form_one_graph(info) -> None:
    anchors = info["anchors"]
    pos = np.array([a["pos"][:3] for a in anchors])
    nrm = np.array([a["normal"] for a in anchors])
    # nobody sits in an arcade opening or a window: nave wall anchors are above the arches (the aisle ones are checked by the ray test)
    nave_wall = (np.abs(nrm[:, 0]) > 0.99) & (pos[:, 0] > ci.IU0 - 0.01) & (pos[:, 0] < ci.IU1 + 0.01)
    assert nave_wall.sum() >= 20 and (pos[nave_wall, 2] > ci.ARCADE_Z + 0.3).all()
    assert (nrm[:, 2] < -0.99).sum() >= 8  # the vault
    for al in ci.AISLES:
        inside = (pos[:, 0] > min(al.in_u, al.nave_u) - 0.01) & (pos[:, 0] < max(al.in_u, al.nave_u) + 0.01)
        assert (inside & (nrm[:, 2] < -0.9)).sum() >= 4  # its ceiling
        assert (inside & (np.abs(nrm[:, 0]) > 0.99)).sum() >= 6  # its walls
    # opposite nave walls face each other at the same v and height
    for i, a in enumerate(anchors):
        if nave_wall[i]:
            assert any(
                abs(pos[j, 1] - pos[i, 1]) < 1e-6 and abs(pos[j, 2] - pos[i, 2]) < 1e-6 and nrm[j, 0] == -nrm[i, 0] and nave_wall[j]
                for j in range(len(anchors))
            ), a["id"]
    ids = {a["id"] for a in anchors}
    seen, todo = {0}, deque([0])
    while todo:
        for n in anchors[todo.popleft()]["links"]:
            assert n in ids
            if n not in seen:
                seen.add(n)
                todo.append(n)
    assert seen == ids
    assert all(i in anchors[j]["links"] for i, a in enumerate(anchors) for j in a["links"])  # links both ways


def test_rooms_cover_nave_chancel_and_aisles(info) -> None:
    from shapely.geometry import Point, Polygon

    rooms = [(Polygon(r["polygon"]), r) for r in info["rooms"]]
    assert len(rooms) == 4

    def inside(u, v, z):
        return any(p.contains(Point(u, v)) and r["z0"] < z < r["z1"] for p, r in rooms)

    for u, v in ((ci.CU, 3.0), (ci.CU, -10.0), (-4.2, 1.0), (6.4, 1.0), (6.4, -6.0), (-4.2, 9.5), (6.4, 10.5)):
        assert inside(u, v, 1.0)
    assert inside(-2.78, 1.0, 1.0) and inside(4.76, 1.0, 1.0)  # in the arch itself, between nave and aisle
    assert not inside(-7.0, 1.0, 1.0) and not inside(9.0, 1.0, 1.0)  # outside the walls
    # every standing and sitting spot is in a room
    for s in info["seats"]:
        assert inside(s["pos"][0], s["pos"][1], s["pos"][2] + 0.1), s["id"]


def test_seats_keep_the_pews_and_add_standing_spots_in_the_aisles(info) -> None:
    sitting = [s for s in info["seats"] if not s.get("stand")]
    standing = [s for s in info["seats"] if s.get("stand")]
    assert len(sitting) == (ci.ROWS_RIGHT + ci.ROWS_LEFT + len(ci.GALLERY_PEWS)) * ci.SEATS_PER_PEW
    assert any(s["pos"][0] < ci.NX0 for s in standing) and any(s["pos"][0] > ci.NX1 for s in standing)
    assert [s["id"] for s in info["seats"]] == list(range(len(info["seats"])))
    assert info["doorway"]["width"] == ci.DOOR_W


def _paintings(info):
    return [p for p in info["props"] if p["kind"] == "painting"]


def test_paintings_are_props_with_a_size_and_the_other_props_are_unchanged(info) -> None:
    paintings = _paintings(info)
    assert 8 <= len(paintings) <= 12 and len(paintings) == len(ci.PAINTINGS)
    assert {p["kind"] for p in info["props"]} == {"pew", "altar", "lectern", "holy_water", "painting"}
    assert len([p for p in info["props"] if p["kind"] == "pew"]) == ci.ROWS_RIGHT + ci.ROWS_LEFT + len(ci.GALLERY_PEWS)
    assert all(set(p) == {"kind", "pos", "yaw"} for p in info["props"] if p["kind"] != "painting")
    assert all(set(p) == {"kind", "pos", "yaw", "w", "h"} and 0.8 <= p["w"] <= 1.4 and 0.8 <= p["h"] <= 1.4 for p in paintings)
    assert any(p["h"] > p["w"] for p in paintings) and any(p["w"] > p["h"] for p in paintings) and any(p["w"] == p["h"] for p in paintings)


def test_paintings_hang_at_eye_height_on_solid_wall_with_free_air_before_them(collision, info) -> None:
    for p in _paintings(info):
        c = np.array(p["pos"])
        yaw = math.radians(p["yaw"])
        n = np.array([math.sin(yaw), math.cos(yaw), 0.0])  # facing into the room
        along = np.array([-n[1], n[0], 0.0])
        assert 1.0 < c[2] - p["h"] / 2 and c[2] + p["h"] / 2 < 3.1, p
        for du in np.linspace(-p["w"] / 2 - 0.08, p["w"] / 2 + 0.08, 5):  # canvas and frame: wall behind, nothing (window, arch, pilaster, stair) before
            for dz in np.linspace(-p["h"] / 2 - 0.08, p["h"] / 2 + 0.08, 5):
                q = c + along * du + [0, 0, dz]
                assert _blocked(collision, q + n * 0.02, q - n * 0.2), (p, du, dz)  # backed by the wall
                assert not _blocked(collision, q + n * 0.01, q + n * 0.3), (p, du, dz)  # free in front
