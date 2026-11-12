import math

import numpy as np
import pytest

from suchohrad_pipeline import floodwall as fw
from suchohrad_pipeline.assets import load_manifest
from suchohrad_pipeline.buildings import hex_to_rgb
from suchohrad_pipeline.floodwall_preview import above_ground
from suchohrad_pipeline.primitives import PRIMITIVES, build_primitive

RUN = ("floodwall_straight_4", "floodwall_straight_2", "floodwall_access")  # the pieces with the full profile


def bbox(mesh):
    pts = mesh.tris.reshape(-1, 3)
    return pts.min(axis=0), pts.max(axis=0)


def solid_at(piece, p) -> bool:
    return any(s.contains(p) for s in piece.solids)


@pytest.mark.parametrize("name", sorted(fw.PIECES))
def test_piece_is_registered_and_its_solids_are_inside_it(name) -> None:
    piece = fw.build_piece(name)
    prim = PRIMITIVES[name]
    assert not prim.centred and prim.size_m == piece.size_m
    assert np.array_equal(build_primitive(name).tris, piece.mesh.tris)
    assert hex_to_rgb(prim.colour) in piece.mesh.colours.tolist()
    lo, hi = bbox(piece.mesh)
    assert lo[2] == pytest.approx(fw.FOOT_Z)  # the base goes into the ground
    assert piece.solids
    for s in piece.solids:
        assert all(a < b for a, b in zip(s.lo, s.hi, strict=True))
        slack = 0.01 if s.surface == "metal" else 1e-9  # a rail's box is a little fatter than the pipe
        assert all(a >= m - slack for a, m in zip(s.lo, lo, strict=True)), s
        assert all(b <= m + slack for b, m in zip(s.hi, hi, strict=True)), s
        assert s.surface in ("wall", "metal")


@pytest.mark.parametrize("name", RUN)
def test_run_piece_has_the_profile_and_butts_at_its_ends(name) -> None:
    piece = fw.build_piece(name)
    half = piece.length_m / 2
    lo, hi = bbox(piece.mesh)
    assert (lo[0], hi[0]) == pytest.approx((-half, half))  # nothing sticks out past the ends
    assert lo[1] <= fw.TOE_Y and hi[1] >= fw.BACK_Y
    for x in (-half + 0.05, half - 0.05):
        # the walkway: solid under the feet, free above them, the wall behind, the rail over it
        on_ledge = (x, (fw.LEDGE_Y - fw.WALL_HALF_M) / 2, fw.LEDGE_Z)
        assert solid_at(piece, (*on_ledge[:2], fw.LEDGE_Z - 0.01))
        assert not any(solid_at(piece, (*on_ledge[:2], fw.LEDGE_Z + dz)) for dz in (0.01, 0.9, 1.8))
        assert solid_at(piece, (x, 0.0, fw.TOP_Z - 0.01))
        assert not solid_at(piece, (x, 0.0, fw.TOP_Z + 0.2))  # a shot passes between the wall and the rail
        assert solid_at(piece, (x, 0.0, fw.RAIL_Z))
        assert solid_at(piece, (x, fw.TOE_Y + 0.05, fw.TOE_Z - 0.01))
        assert not solid_at(piece, (x, fw.TOE_Y + 0.05, fw.TOE_Z + 0.01))
        assert not solid_at(piece, (x, fw.TOE_Y - 0.05, 0.5))  # in front of the wall
        assert not solid_at(piece, (x, fw.BACK_Y + 0.05, 0.5)) or name == "floodwall_access"
    # a jump reaches the ledge (ShooterCore MoveConsts: the jump peaks at about 1.1 m)
    assert fw.LEDGE_Z < 1.0
    # the capsule (0.35 m radius) gets past a pier without leaving the ledge
    assert fw.PIER_FRONT_Y - fw.LEDGE_Y >= 0.7


def test_straight_pieces_carry_the_rail_posts_two_metres_apart() -> None:
    rail = hex_to_rgb(fw.RAIL)
    for name, posts in (("floodwall_straight_4", [-1.0, 1.0]), ("floodwall_straight_2", [0.0])):
        mesh = fw.build_piece(name).mesh
        pts = mesh.tris[(mesh.colours == rail).all(axis=1)].reshape(-1, 3)
        assert pts[:, 2].max() == pytest.approx(fw.RAIL_Z + 0.024, abs=0.005)
        feet = pts[np.isclose(pts[:, 2], fw.TOP_Z)]  # where the posts stand on the wall
        assert sorted({round(float(x), 0) for x in feet[:, 0]}) == posts


def test_access_point_has_a_way_through_and_a_landing() -> None:
    piece = fw.build_piece("floodwall_access")
    for z in (fw.LEDGE_Z + 0.05, fw.LEDGE_Z + 1.0, fw.LEDGE_Z + 1.75):  # a standing player, 0.7 m wide
        for x in (-0.35, 0.0, 0.35):
            for y in np.linspace(fw.LEDGE_Y, fw.BACK_Y + 1.0, 12):
                assert not solid_at(piece, (x, float(y), z)), (x, y, z)
    assert solid_at(piece, (0.0, 0.0, fw.LEDGE_Z - 0.01))  # the floor of the opening
    assert solid_at(piece, (0.0, fw.BACK_Y + 0.5, fw.LEDGE_Z - 0.01))  # the landing
    assert not solid_at(piece, (0.0, fw.BACK_Y + 1.2, 0.5))  # beyond it, the river side
    for x in (-1.0, 1.0):  # the piers
        assert solid_at(piece, (x, fw.PIER_FRONT_Y + 0.05, fw.PIER_TOP_Z - 0.05))
    # the plaque: letters on a dark plate on the left pier, facing the village
    letters = piece.mesh.tris[(piece.mesh.colours == hex_to_rgb(fw.LETTER)).all(axis=1)].reshape(-1, 3)
    assert len(letters) > 100
    assert np.ptp(letters[:, 1]) == pytest.approx(0) and letters[:, 1].max() < fw.PIER_FRONT_Y
    assert -1.4 < letters[:, 0].min() and letters[:, 0].max() < -0.6
    # the ladders reach the ground on both sides and stop nothing
    ladder = piece.mesh.tris[(piece.mesh.colours == hex_to_rgb(fw.LADDER)).all(axis=1)].reshape(-1, 3)
    assert ladder[:, 1].min() < fw.TOE_Y and ladder[:, 1].max() > fw.BACK_Y + 1.0 and ladder[:, 2].min() < 0
    assert not solid_at(piece, (0.0, fw.TOE_Y - 0.2, 0.3))


def test_gate_is_two_piers_with_a_road_between() -> None:
    gate = fw.build_piece("floodwall_gate")
    assert gate.length_m == fw.GATE_CLEAR_M + 2 * fw.GATE_PIER_M
    half = fw.GATE_CLEAR_M / 2
    for x in np.linspace(-half + 0.01, half - 0.01, 9):
        for y in (-1.0, 0.0, 0.3):
            assert not solid_at(gate, (float(x), y, 0.1)) and not solid_at(gate, (float(x), y, 1.9))
            assert solid_at(gate, (float(x), -0.5, 0.0))  # the sill, low enough to drive over
    between = [s for s in gate.solids if s.lo[0] >= -half - 1e-9 and s.hi[0] <= half + 1e-9]
    assert len(between) == 1 and between[0].hi[2] <= 0.05
    for x in (-half - 0.5, half + 0.5):
        assert solid_at(gate, (x, 0.0, fw.PIER_TOP_Z - 0.01)) and solid_at(gate, (x, fw.TOE_Y, 0.5))
    pier = fw.build_piece("floodwall_gate_pier")
    lo, hi = bbox(pier.mesh)
    assert (hi - lo)[0] == pytest.approx(fw.GATE_PIER_M) and hi[2] == pytest.approx(fw.PIER_TOP_Z)
    assert lo[1] < fw.TOE_Y and hi[1] > fw.BACK_Y  # it covers the end of the whole profile


def test_joint_fills_the_wedge_of_a_bend_and_never_shows_on_the_river_side() -> None:
    joint = fw.build_piece("floodwall_joint")
    assert joint.length_m == 0.0
    lo, hi = bbox(joint.mesh)
    assert hi[1] == pytest.approx(fw.PIER_BACK_Y) and lo[1] == pytest.approx(fw.TOE_Y)
    assert solid_at(joint, (0.0, -0.8, fw.LEDGE_Z - 0.01)) and not solid_at(joint, (0.0, -0.8, fw.LEDGE_Z + 0.01))
    # what is not the pier (the drum) stays on the village side, inside pieces meeting at any angle
    pts = joint.mesh.tris.reshape(-1, 3)
    drum = pts[(np.abs(pts[:, 0]) > 0.3 + 1e-9)]
    assert len(drum) and drum[:, 1].max() < -0.5
    assert np.hypot(drum[:, 0], drum[:, 1]).max() == pytest.approx(-fw.TOE_Y)


def test_lay_run_steps_by_the_lengths_and_turns_joints_to_the_bisector() -> None:
    names = ["floodwall_straight_4", "floodwall_joint", "floodwall_straight_2", "floodwall_gate"]
    run = fw.lay_run(names, start=(100.0, 50.0), heading_deg=90.0, bends={2: 20.0})
    assert [p.asset for p in run] == [f"prim:{n}" for n in names]
    assert (run[0].x, run[0].y, run[0].rotation_deg) == (100.0, 52.0, 90.0)
    assert (run[1].x, run[1].y, run[1].rotation_deg) == (100.0, 54.0, 100.0)  # on the joint, half the bend
    a = math.radians(110.0)
    assert (run[2].x, run[2].y) == pytest.approx((100.0 + math.cos(a), 54.0 + math.sin(a)), abs=1e-3)
    assert run[2].rotation_deg == 110.0
    assert (run[3].x, run[3].y) == pytest.approx((100.0 + 5 * math.cos(a), 54.0 + 5 * math.sin(a)), abs=1e-3)
    with pytest.raises(ValueError, match="more than one joint"):
        fw.lay_run(names, bends={2: 40.0})
    with pytest.raises(ValueError, match="after the joint"):
        fw.lay_run(names, bends={1: 10.0})
    mesh = fw.run_mesh(run)
    assert mesh.face_count == sum(fw.build_piece(n).mesh.face_count for n in names)


def test_preview_cuts_the_buried_part_away() -> None:
    mesh = above_ground(fw.build_piece("floodwall_straight_2").mesh)
    assert mesh.tris.reshape(-1, 3)[:, 2].min() == pytest.approx(0.0)


def test_manifest_floodwall_entries_are_in_sync() -> None:
    """``make floodwall-manifest`` after changing the kit."""
    shipped = [a for a in load_manifest()["assets"] if a["id"].startswith("prim:floodwall_")]
    assert shipped == fw.manifest_entries()
    assert [a["id"].removeprefix("prim:") for a in shipped] == list(fw.PIECES)
    for a in shipped:
        assert a["category"] == "wall" and a["solids"], a["id"]
        assert all(len(s["min"]) == 3 and len(s["max"]) == 3 for s in a["solids"])
