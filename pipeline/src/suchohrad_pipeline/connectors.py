"""House connectors: where the cable from a utility pole ends on a house.

Small fittings of the kind the village has, built from the pole parts kit (``poleparts.py``): J-hooks
with insulators on a wall, a hook for a bundled cable with a box under it, and roof masts. Each is a
placeable object (``prim:connector_*``, category ``connector``, no collision) with wire seats like a
pole, so a cable (``cables`` in edits.json) can run from a pole to it. The editor places one when a
cable is strung to a house, picking a wall or a roof kind from the surface clicked.

Frame: local metres, z up, the origin on the surface the connector sits on. ``+x`` points at the
incoming wires: for a wall connector that is straight out of the wall (so ``rotation_deg`` is the
direction the wall faces), a roof mast is turned towards the pole. Wall connectors reach below the
origin (the conduit down the wall), roof masts a little (the pipe goes through the roof).
"""

from collections.abc import Callable
from dataclasses import dataclass

import numpy as np

from suchohrad_pipeline import poleparts as pp
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.poleparts import UP, Kit, vec
from suchohrad_pipeline.poles import Anchor, seats_of, size_of

GALVANISED = "#8b9096"
LEAD = "#6f7479"


@dataclass
class Connector:
    mesh: Mesh
    anchors: list[Anchor]
    colour: str  # a colour the mesh certainly has (the primitive's listed colour)

    def seats(self) -> list[dict]:
        return seats_of(self.anchors)

    @property
    def size_m(self) -> tuple[float, float, float]:
        return size_of(self.mesh)


def _lead(k: Kit, seat, to, radius: float = 0.007) -> None:
    """The short wire from an insulator into the conduit."""
    k.sweep(pp.hang(seat, to, 0.05, n=5), radius, pp.CABLE)


def _wall_hooks(n: int, style: str, conduit: str, steel: str = pp.STEEL_DARK) -> Connector:
    """A row of ``n`` J-hooks screwed into the wall, an insulator on each, and a conduit with a
    weatherhead under them that takes the wires into the house."""
    k = Kit()
    anchors = []
    gap = 0.28
    head = vec(0.11, 0.0, -0.20)  # where the conduit's weatherhead opens
    for i in range(n):
        y = (i - (n - 1) / 2) * gap
        k.box(vec(0.0, y - 0.03, -0.03), vec(0.006, y + 0.03, 0.03), steel)
        k.sweep([vec(0.0, y, 0.0), vec(0.15, y, 0.0), vec(0.17, y, 0.02), vec(0.17, y, 0.05)], 0.008, steel, sides=5)
        ins = pp.insulator(style, 0.8)
        at = (0.17, y, 0.04)
        k.add(ins.kit, 0.0, at)
        seat = ins.placed(0.0, at)["wire"]
        anchors.append(Anchor(f"hook{i}", "line", 0, seat))
        _lead(k, seat - UP * 0.03, head)
    r = 0.02
    k.sweep([vec(r + 0.004, 0, -1.25), vec(r + 0.004, 0, -0.16), vec(0.06, 0, -0.10), vec(0.10, 0, -0.12), head],
            r, conduit, sides=6)
    for z in (-0.35, -0.95):
        k.box(vec(0.0, -0.04, z - 0.012), vec(r + 0.012, 0.04, z + 0.012), steel)
    return Connector(k.mesh(), anchors, steel)


def _wall_hook(box: str, conduit: str, steel: str = pp.STEEL_DARK) -> Connector:
    """A hook for a bundled cable: the wedge clamp hangs on it, the cable loops down into a small
    box on the wall and a conduit goes on down from there."""
    k = Kit()
    k.box(vec(0.0, -0.04, -0.04), vec(0.008, 0.04, 0.04), steel)
    k.sweep([vec(0.0, 0, 0.0), vec(0.09, 0, 0.0), vec(0.11, 0, -0.02), vec(0.11, 0, -0.05)], 0.009, steel, sides=5)
    seat = vec(0.22, 0.0, -0.03)
    k.beam(vec(0.10, 0, -0.05), seat, 0.026, 0.034, pp.CABLE)
    part = pp.junction_box(0.2, 0.26, 0.1, box)
    k.add(part.kit, 0.0, (0.0, 0.0, -0.62))
    top = part.placed(0.0, (0.0, 0.0, -0.62))["top"]
    k.sweep(pp.hang(vec(0.12, 0, -0.07), top, 0.10, bulge=vec(0.10, 0, 0), n=7), 0.014, pp.CABLE, sides=4)
    r = 0.018
    k.sweep([vec(r + 0.004, 0, -1.7), vec(r + 0.004, 0, -0.76)], r, conduit, sides=6)
    k.box(vec(0.0, -0.035, -1.25), vec(r + 0.012, 0.035, -1.226), steel)
    return Connector(k.mesh(), [Anchor("hook", "bundle", -1, seat)], steel)


def _mast(k: Kit, height: float, steel: str) -> np.ndarray:
    """The roof mast: a pipe through the roof with a lead collar at its foot and a gooseneck on top
    (its opening faces down, away from the wires). Returns where the opening is."""
    r = 0.028
    opening = vec(-0.15, 0, height - 0.02)
    k.sweep([vec(0, 0, -0.35), vec(0, 0, height), vec(-0.03, 0, height + 0.07), vec(-0.10, 0, height + 0.08),
             vec(-0.145, 0, height + 0.04), opening], r, steel, sides=6)
    k.lathe(vec(0, 0, -0.12), UP, [(0.0, 0.17), (0.12, 0.13), (0.26, 0.036)], 8, LEAD)
    return opening


def _roof_mast(pins: int, style: str, height: float, arm: float, steel: str = GALVANISED) -> Connector:
    """A roof mast with a short crossarm of ``pins`` insulators."""
    k = Kit()
    opening = _mast(k, height, steel)
    part = pp.crossarm(arm, pins, style, "even", "none", colour=pp.STEEL_DARK, section=0.04, pin_scale=0.8)
    at = (0.028, 0.0, height - 0.22)
    k.add(part.kit, 0.0, at)
    anchors = []
    for name, p in part.placed(0.0, at).items():
        anchors.append(Anchor(f"arm.{name.removeprefix('wire_')}", "line", 0, p))
        _lead(k, p - UP * 0.03, opening)
    return Connector(k.mesh(), anchors, steel)


def _roof_mast_hook(height: float, steel: str = GALVANISED) -> Connector:
    """A roof mast with a hook for a bundled cable, which loops up into the gooseneck."""
    k = Kit()
    opening = _mast(k, height, steel)
    z = height - 0.2
    k.box(vec(0.02, -0.035, z - 0.03), vec(0.04, 0.035, z + 0.03), pp.STEEL_DARK)
    k.sweep([vec(0.03, 0, z), vec(0.10, 0, z), vec(0.12, 0, z - 0.02), vec(0.12, 0, z - 0.05)], 0.009, pp.STEEL_DARK, sides=5)
    seat = vec(0.23, 0.0, z - 0.03)
    k.beam(vec(0.11, 0, z - 0.05), seat, 0.026, 0.034, pp.CABLE)
    k.sweep(pp.hang(vec(0.13, 0, z - 0.07), opening, 0.16, bulge=vec(0, 0.10, 0), n=8), 0.014, pp.CABLE, sides=4)
    return Connector(k.mesh(), [Anchor("hook", "bundle", -1, seat)], steel)


# prim name -> (builder, what it is). "wall" or "roof" in the name says where it is mounted.
CONNECTORS: dict[str, tuple[Callable[[], Connector], str]] = {
    "connector_wall_2": (lambda: _wall_hooks(2, "white", pp.PLASTIC_GREY), "two wall hooks with insulators and a conduit"),
    "connector_wall_4": (lambda: _wall_hooks(4, "brown", "#5b5e62"), "four wall hooks with insulators and a conduit"),
    "connector_wall_hook": (lambda: _wall_hook(pp.PLASTIC_GREY, pp.PLASTIC_GREY),
                            "wall hook for a bundled cable, with a box and a conduit"),
    "connector_roof_mast_2": (lambda: _roof_mast(2, "white", 1.25, 0.5), "roof mast with two insulators"),
    "connector_roof_mast_4": (lambda: _roof_mast(4, "brown", 1.6, 0.9, "#7a6a5a"), "tall roof mast with four insulators"),
    "connector_roof_mast_hook": (lambda: _roof_mast_hook(1.3), "roof mast with a hook for a bundled cable"),
}


def build_connector(name: str) -> Connector:
    """The connector of ``prim:<name>``."""
    return CONNECTORS[name][0]()


def mount_of(name: str) -> str:
    """``wall`` or ``roof``: the surface a connector is made for."""
    return "roof" if "_roof_" in name else "wall"
