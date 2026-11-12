"""Utility poles: the parts kit, the spec generator, the built poles and their primitives."""

import json

import numpy as np
import pytest

from suchohrad_pipeline import poleparts as pp
from suchohrad_pipeline.assets import load_manifest
from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.connectors import CONNECTORS, build_connector, mount_of
from suchohrad_pipeline.poles import (
    ARCHETYPES,
    VARIANTS,
    PoleSpec,
    build_pole,
    build_variant,
    random_spec,
    span_mesh,
)
from suchohrad_pipeline.poles_preview import connector_entries, manifest_entries
from suchohrad_pipeline.primitives import PRIMITIVES, build_primitive


def signed_volume(mesh) -> float:
    a, b, c = mesh.tris[:, 0], mesh.tris[:, 1], mesh.tris[:, 2]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6)


@pytest.mark.parametrize("name", sorted(pp.PARTS))
def test_part_is_a_closed_solid_wound_outwards(name) -> None:
    mesh = pp.PARTS[name][0]().kit.mesh()
    assert mesh.face_count > 0 and np.isfinite(mesh.tris).all()
    assert signed_volume(mesh) > 0
    if name in ("riser", "down_cable_coil"):  # shown on a cut piece of column, which is open
        return
    # closed: every edge is used once in each direction
    edges: dict[tuple, int] = {}
    pts = np.round(mesh.tris, 6)
    for tri in pts:
        for i in range(3):
            a, b = tuple(tri[i]), tuple(tri[(i + 1) % 3])
            if a != b:
                edges[(a, b)] = edges.get((a, b), 0) + 1
    assert all(edges.get((b, a), 0) == n for (a, b), n in edges.items())


def test_crossarm_pins_and_wire_points() -> None:
    arm = pp.crossarm(2.0, 6, layout="paired")
    ys = [arm.points[f"wire_{i}"][1] for i in range(6)]
    assert ys == sorted(ys) and ys[0] == pytest.approx(-ys[-1])
    assert ys[1] - ys[0] == pytest.approx(ys[2] - ys[1]) and ys[3] - ys[2] > 2 * (ys[1] - ys[0])  # two groups
    assert pp.pin_positions(1.2, 3) == pytest.approx([-0.53, 0.0, 0.53])
    assert all(p[2] > 0.15 for p in arm.points.values())  # on top of the insulators


def test_column_surface_follows_the_taper_and_the_twin() -> None:
    col = pp.Column("round", 9.0, 0.16, 0.09)
    assert col.surface(0, 0)[0] == pytest.approx(0.16) and col.surface(90, 9.0)[1] == pytest.approx(0.09)
    twin = pp.Column("twin", 9.0, 0.16, 0.09, axis_deg=0.0)
    assert twin.reach(0, 0) == pytest.approx(0.32)  # along the pair: two radii
    assert twin.reach(90, 0) == pytest.approx(0.16)  # across it: one


@pytest.mark.parametrize("archetype", sorted(ARCHETYPES))
def test_archetype_builds_deterministic_solid_poles(archetype) -> None:
    sizes = set()
    for seed in range(1, 13):
        spec = random_spec(seed, archetype)
        assert spec.archetype == archetype and spec.seed == seed
        a, b = build_pole(spec), build_pole(random_spec(seed, archetype))
        assert np.array_equal(a.mesh.tris, b.mesh.tris) and np.array_equal(a.mesh.colours, b.mesh.colours)
        assert np.isfinite(a.mesh.tris).all() and signed_volume(a.mesh) > 0
        pts = a.mesh.tris.reshape(-1, 3)
        assert -0.8 < pts[:, 2].min() < -0.3  # the column goes into the ground
        assert 6.0 < pts[:, 2].max() < 10.6
        assert a.anchors and all(an.pos[2] > 3.5 for an in a.anchors)
        assert (len(a.lights) == 1) == (spec.lamp is not None)
        sizes.add(a.mesh.face_count)
    assert len(sizes) > 3  # the seeds really differ


def test_archetypes_differ_in_what_they_carry() -> None:
    specs = {name: [random_spec(s, name) for s in range(1, 21)] for name in ARCHETYPES}
    assert all(s.lamp and s.column.kind == "round" for s in specs["lamp"])
    assert not any(s.lamp for s in specs["plain"])
    assert all(s.column.kind == "twin" and s.column.bands for s in specs["twin_lamp"] + specs["twin_riser"])
    assert all(len(s.arms) >= 2 and s.jumpers >= 6 for s in specs["junction"])
    assert any(s.cabinet for s in specs["junction"]) and any(s.column.strut for s in specs["junction"])
    assert all(s.risers and s.boxes for s in specs["twin_riser"])
    assert all(s.column.kind == "slim" and s.lean_deg >= 3 for s in specs["field"])
    assert all(len(s.megaphones) >= 2 for s in specs["megaphone"])
    picked = {random_spec(seed).archetype for seed in range(200)}
    assert picked == set(ARCHETYPES)


def test_spec_round_trips_through_json() -> None:
    for archetype in ARCHETYPES:
        spec = random_spec(4, archetype)
        again = PoleSpec.from_dict(json.loads(json.dumps(spec.to_dict())))
        assert again == spec
        assert np.array_equal(build_pole(again).mesh.tris, build_pole(spec).mesh.tris)


def test_a_hand_written_spec_builds() -> None:
    spec = PoleSpec.from_dict({
        "column": {"kind": "round", "height": 9.0},
        "arms": [{"z": 8.8, "pins": 4}],
        "lamp": {"z": 8.0, "arm_m": 1.2},
        "megaphones": [{"z": 7.0, "az_deg": 0.0}],
    })
    pole = build_pole(spec)
    assert len(pole.wire_points()) == 4 and len(pole.lights) == 1
    assert pole.lights[0][1] < -1.0  # the lamp reaches to the front (-y)
    assert max(p[2] for p in pole.wire_points()) == pytest.approx(8.8 + 0.03 + 0.154, abs=0.01)


def test_lean_tilts_the_pole_but_not_the_cabinet() -> None:
    spec = random_spec(1, "plain")
    spec.lean_deg, spec.lean_az_deg = 10.0, 0.0
    spec.cabinet = None
    pts = build_pole(spec).mesh.tris.reshape(-1, 3)
    top = pts[pts[:, 2] > pts[:, 2].max() - 0.5]
    assert top[:, 0].mean() == pytest.approx(spec.column.height * np.sin(np.radians(10)), abs=0.25)


def test_placed_pole_and_span_between_two() -> None:
    a = build_variant("pole_lamp_1").placed(100.0, 50.0, 200.0, 90.0)
    b = build_variant("pole_plain_1").placed(100.0, 80.0, 201.0, 90.0)
    assert a.mesh.tris.reshape(-1, 3)[:, 2].min() == pytest.approx(199.5, abs=0.02)
    assert all(abs(p[1] - 50.0) < 0.3 for p in a.wire_points())  # the arm now runs along x
    wa, wb = a.wire_points(), b.wire_points()
    wires = span_mesh(wa, wb, seed=1)
    assert signed_volume(wires) > 0
    pts = wires.tris.reshape(-1, 3)
    assert 49.9 < pts[:, 1].min() and pts[:, 1].max() < 80.1
    low = min(p[2] for p in wa + wb)
    assert low - 1.5 < pts[:, 2].min() < low - 0.3  # the wires sag below the seats
    assert span_mesh(wa, []).face_count == 0


@pytest.mark.parametrize("name", sorted(VARIANTS))
def test_pole_primitive_matches_its_record(name) -> None:
    prim = PRIMITIVES[name]
    mesh = build_primitive(name)
    assert not prim.centred and prim.params == dict(zip(("archetype", "seed"), VARIANTS[name], strict=True))
    pts = mesh.tris.reshape(-1, 3)
    x, y_up, z = prim.size_m
    assert 2 * np.abs(pts[:, 0]).max() == pytest.approx(x, abs=1e-3)
    assert 2 * np.abs(pts[:, 1]).max() == pytest.approx(z, abs=1e-3)
    assert pts[:, 2].max() == pytest.approx(y_up, abs=1e-3)
    assert hex_to_rgb(prim.colour) in mesh.colours.tolist()
    other = build_primitive(name, {"seed": VARIANTS[name][1] + 50})
    assert other.face_count != mesh.face_count or not np.array_equal(other.tris, mesh.tris)


def test_manifest_pole_entries_are_in_sync() -> None:
    """``python -m suchohrad_pipeline.poles_preview --sync-manifest`` after changing the poles."""
    shipped = [a for a in load_manifest()["assets"] if a["id"].startswith("prim:pole_")]
    assert shipped == manifest_entries()
    assert all(a["category"] == "pole" for a in shipped)
    for a in shipped:  # the game strings cables from these: every pole has somewhere for a line wire to sit
        line = [s for s in a["seats"] if s["kind"] == "line"]
        assert line and all(len(s["pos"]) == 3 and s["pos"][2] > 5 for s in line), a["id"]
        assert {s["kind"] for s in a["seats"]} <= {"line", "service", "bundle"}
        # a pole with a lamp says where its light is (under the luminaire, out over the road side)
        assert (len(a["lights"]) == 1) == ("street-lamp" in a["tags"]), a["id"]
        assert all(p[2] > 6 and p[1] < -0.3 for p in a["lights"]), a["id"]


# --- house connectors ---


@pytest.mark.parametrize("name", sorted(CONNECTORS))
def test_connector_is_a_small_solid_fitting_with_wire_seats(name) -> None:
    c = build_connector(name)
    assert np.isfinite(c.mesh.tris).all() and signed_volume(c.mesh) > 0
    assert np.array_equal(c.mesh.tris, build_connector(name).mesh.tris)
    pts = c.mesh.tris.reshape(-1, 3)
    seats = c.seats()
    assert seats and {s["kind"] for s in seats} <= {"line", "bundle"}
    assert all(s["pos"][0] > 0 for s in seats)  # the wires come from +x
    if mount_of(name) == "wall":
        assert pts[:, 0].min() >= -1e-9  # nothing goes into the wall
        assert pts[:, 2].min() < -1.0  # the conduit runs down the wall
        assert pts[:, 0].max() < 0.3 and all(abs(s["pos"][2]) < 0.3 for s in seats)
    else:
        assert pts[:, 2].min() == pytest.approx(-0.35)  # the pipe goes through the roof
        assert 1.2 < pts[:, 2].max() < 2.0 and all(s["pos"][2] > 0.9 for s in seats)
    prim = PRIMITIVES[name]
    assert not prim.centred and prim.size_m == c.size_m
    assert hex_to_rgb(prim.colour) in c.mesh.colours.tolist()


def test_connectors_cover_walls_and_roofs_with_wires_and_bundles() -> None:
    kinds = {(mount_of(n), build_connector(n).seats()[0]["kind"]) for n in CONNECTORS}
    assert kinds == {("wall", "line"), ("wall", "bundle"), ("roof", "line"), ("roof", "bundle")}


def test_manifest_connector_entries_are_in_sync() -> None:
    """``python -m suchohrad_pipeline.poles_preview --sync-manifest`` after changing the connectors."""
    shipped = [a for a in load_manifest()["assets"] if a["id"].startswith("prim:connector_")]
    assert shipped == connector_entries()
    assert all(a["category"] == "connector" and a["seats"] for a in shipped)
