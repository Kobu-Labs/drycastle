"""Utility poles: the concrete electricity poles of the village, generated from parts.

A pole is described by a :class:`PoleSpec` (plain data: the column, the crossarms, the lamp, the
megaphones, boxes, risers, cables, marks) and built by :func:`build_pole` from the parts in
``poleparts.py``. :func:`random_spec` draws a spec for one of the pole types the village has
(``ARCHETYPES``); the same seed always gives the same pole. ``VARIANTS`` names the poles that are
placeable objects (``prim:pole_*``, see ``primitives.py``).

Frame of a pole: local metres, z up, the foot of the column at the origin (the column goes half a
metre into the ground). The line (the wires to the neighbouring poles) runs along ``x``; ``-y`` is
the front, the side of the road, where the lamp reaches out. Directions on the pole are compass
angles in degrees, counter-clockwise seen from above, 0 = ``+x``.

A built :class:`Pole` also carries its anchors (where wires sit) and its light points, so that wires
can be strung between placed poles (:func:`span_mesh`) and lamps lit.
"""

import dataclasses
import math
import random
import zlib
from dataclasses import dataclass, field

import numpy as np

from suchohrad_pipeline import poleparts as pp
from suchohrad_pipeline.meshes import Mesh
from suchohrad_pipeline.poleparts import UP, Column, Kit

# ----------------------------------------------------------------------------- the spec


@dataclass
class StrutSpec:
    az_deg: float  # where its foot stands
    spread_m: float = 1.7
    z_join: float = 7.0


@dataclass
class ColumnSpec:
    kind: str = "round"  # round | twin | slim
    height: float = 9.0
    r_base: float = 0.16
    r_top: float = 0.09
    colour: str = pp.CONCRETE
    axis_deg: float = 0.0  # twin: the direction the two columns stand along
    bands: list[float] = field(default_factory=list)  # twin: heights of the clamps
    band_colour: str = pp.RUST
    strut: StrutSpec | None = None


@dataclass
class ArmSpec:
    z: float
    length: float = 1.2
    pins: int = 4
    side_deg: float = 0.0  # the face of the pole it is bolted to; the arm runs square to it
    offset: float = 0.0
    layout: str = "even"  # even | paired
    brace: str = "none"  # none | one | two
    insulator: str = "brown"
    colour: str = pp.RUST
    pin_scale: float = 1.0


@dataclass
class BracketSpec:
    z: float
    az_deg: float
    kind: str = "pin"  # pin | spool | hook
    insulator: str = "white"


@dataclass
class LampSpec:
    z: float
    az_deg: float = -90.0
    arm_m: float = 1.0
    rise_deg: float = 15.0
    style: str = "straight"  # straight | swan
    head: str = "cobra"  # cobra | led
    arm_colour: str = pp.STEEL


@dataclass
class MegaphoneSpec:
    z: float
    az_deg: float
    tilt_deg: float = 8.0
    length: float = 0.42
    mouth_r: float = 0.19
    colour: str = pp.HORN


@dataclass
class BoxSpec:
    kind: str  # junction | meter
    z: float
    az_deg: float
    colour: str = pp.PLASTIC_GREY


@dataclass
class CabinetSpec:
    az_deg: float  # where it stands, seen from the pole
    distance_m: float = 0.7
    facing_deg: float = -90.0  # where its door faces
    w: float = 0.5
    d: float = 0.26
    h: float = 1.1
    colour: str = pp.PLASTIC_WHITE


@dataclass
class RiserSpec:
    az_deg: float
    z0: float
    z1: float
    radius: float = 0.03
    colour: str = pp.PLASTIC_GREY
    head: bool = True
    cable_radius: float = 0.012  # of the cable from its top to the nearest wire


@dataclass
class CableSpec:
    az_deg: float
    z0: float
    z1: float
    coil_z: float | None = None
    coil_loops: int = 3


@dataclass
class MarkSpec:
    kind: str  # warning | yellow_plate | number_plate
    z: float
    az_deg: float = -90.0


@dataclass
class PoleSpec:
    seed: int = 0  # for the small things: weathering, the way cables hang
    archetype: str = "custom"
    column: ColumnSpec = field(default_factory=ColumnSpec)
    arms: list[ArmSpec] = field(default_factory=list)
    top_pin: str | None = None  # insulator style of a pin on top of each column
    brackets: list[BracketSpec] = field(default_factory=list)
    lamp: LampSpec | None = None
    megaphones: list[MegaphoneSpec] = field(default_factory=list)
    boxes: list[BoxSpec] = field(default_factory=list)
    cabinet: CabinetSpec | None = None
    risers: list[RiserSpec] = field(default_factory=list)
    cables: list[CableSpec] = field(default_factory=list)
    marks: list[MarkSpec] = field(default_factory=list)
    jumpers: int = 0  # cables hanging between wire seats of the pole
    coils: int = 0  # bundles of slack hanging under the crossarms
    slack: float = 1.0  # how deep the jumpers hang
    lean_deg: float = 0.0
    lean_az_deg: float = 0.0

    def to_dict(self) -> dict:
        return dataclasses.asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "PoleSpec":
        d = dict(d)
        col = dict(d.get("column", {}))
        if col.get("strut"):
            col["strut"] = StrutSpec(**col["strut"])
        d["column"] = ColumnSpec(**col)
        for key, typ in _LISTS.items():
            d[key] = [typ(**item) for item in d.get(key, [])]
        for key, typ in (("lamp", LampSpec), ("cabinet", CabinetSpec)):
            d[key] = typ(**d[key]) if d.get(key) else None
        return cls(**d)


_LISTS = {"arms": ArmSpec, "brackets": BracketSpec, "megaphones": MegaphoneSpec, "boxes": BoxSpec,
          "risers": RiserSpec, "cables": CableSpec, "marks": MarkSpec}


# ----------------------------------------------------------------------------- the built pole


@dataclass
class Anchor:
    """A place on the pole where a wire sits. ``kind``: ``line`` (crossarm and top insulators),
    ``service`` (side brackets) or ``bundle`` (hooks). ``tier``: the crossarm's index, -1 otherwise."""

    name: str
    kind: str
    tier: int
    pos: np.ndarray


@dataclass
class Pole:
    spec: PoleSpec
    mesh: Mesh
    anchors: list[Anchor]
    lights: list[np.ndarray]  # under each luminaire

    def placed(self, x: float, y: float, z: float, rotation_deg: float = 0.0) -> "Pole":
        """The pole standing at a map position, turned counter-clockwise by ``rotation_deg``."""
        r, o = pp.rot_z(rotation_deg), np.array([x, y, z], float)
        return Pole(
            self.spec,
            Mesh(self.mesh.tris @ r.T + o, self.mesh.colours),
            [Anchor(a.name, a.kind, a.tier, r @ a.pos + o) for a in self.anchors],
            [r @ p + o for p in self.lights],
        )

    def wire_points(self, kind: str = "line", tier: int | None = 0) -> list[np.ndarray]:
        """Wire seats of one kind; ``line`` wires of crossarm ``tier`` (None: every tier), with the
        top pins counted to the first crossarm."""
        return [a.pos for a in self.anchors
                if a.kind == kind and (tier is None or a.tier == tier or (tier == 0 and a.tier == -1
                                                                             and kind == "line"))]

    def seats(self) -> list[dict]:
        """The wire seats as plain data (``kind``, ``tier``, ``pos`` [x, y, z] in the pole's frame): what
        the asset manifest carries, so the game can string cables between placed poles."""
        return [{"kind": a.kind, "tier": a.tier, "pos": [round(float(v), 3) for v in a.pos]}
                for a in self.anchors]

    @property
    def size_m(self) -> tuple[float, float, float]:
        """``[x, y, z]`` in glTF axes (y up): the box centred on the foot that holds the pole."""
        pts = self.mesh.tris.reshape(-1, 3)
        return (round(2 * float(np.abs(pts[:, 0]).max()), 3), round(float(pts[:, 2].max()), 3),
                round(2 * float(np.abs(pts[:, 1]).max()), 3))


def _lean_matrix(deg: float, az_deg: float) -> np.ndarray:
    t, p = math.radians(deg), math.radians(az_deg)
    a = np.array([-math.sin(p), math.cos(p), 0.0])
    cross = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) * math.cos(t) + math.sin(t) * cross + (1 - math.cos(t)) * np.outer(a, a)


def _nearest(anchors: list[Anchor], p: np.ndarray, kinds=("line", "service", "bundle")) -> Anchor | None:
    cands = [a for a in anchors if a.kind in kinds]
    return min(cands, key=lambda a: float(np.linalg.norm(a.pos - p))) if cands else None


def _away(p: np.ndarray, rng) -> np.ndarray:
    """Horizontal unit vector from the pole axis through ``p`` (a random one on the axis)."""
    h = np.array([p[0], p[1], 0.0])
    n = float(np.linalg.norm(h))
    return h / n if n > 0.05 else Column.out(rng.uniform(0, 360))


def build_pole(spec: PoleSpec) -> Pole:
    """Assemble the pole of a spec. Deterministic: the same spec gives the same triangles."""
    rng = random.Random(spec.seed * 7919 + 13)
    c = spec.column
    col = Column(c.kind, c.height, c.r_base, c.r_top, c.axis_deg)
    k = Kit()
    pp.build_column(k, col, c.colour, rng, tuple(c.bands), c.band_colour)
    if c.strut:
        pp.strut(k, col, c.strut.az_deg, c.strut.spread_m, c.strut.z_join, c.colour, rng)
    anchors: list[Anchor] = []
    lights: list[np.ndarray] = []

    def strap(z: float, yaw: float = 0.0, colour: str = pp.STEEL_DARK) -> None:
        if col.kind != "twin":
            k.add(pp.band(col.radius(z), colour=colour).kit, yaw, (0, 0, z))

    def mount(part: pp.Part, az: float, z: float) -> dict[str, np.ndarray]:
        at = col.surface(az, z)
        k.add(part.kit, az, at)
        return part.placed(az, at)

    for i, a in enumerate(spec.arms):
        part = pp.crossarm(a.length, a.pins, a.insulator, a.layout, a.brace, a.offset, a.colour,
                           pin_scale=a.pin_scale)
        for name, p in mount(part, a.side_deg, a.z).items():
            anchors.append(Anchor(f"arm{i}.{name.removeprefix('wire_')}", "line", i, p))
        strap(a.z, a.side_deg, a.colour)
    if spec.top_pin:
        for i, centre in enumerate(col.centres(col.height)):
            part = pp.top_pin(spec.top_pin)
            k.add(part.kit, 0.0, centre)
            anchors.append(Anchor(f"top{i}", "line", -1, part.placed(0.0, centre)["wire"]))
    for i, b in enumerate(spec.brackets):
        p = mount(pp.side_bracket(b.kind, b.insulator), b.az_deg, b.z)["wire"]
        anchors.append(Anchor(f"bracket{i}", "bundle" if b.kind == "hook" else "service", -1, p))

    # (where a feed cable ends, its radius, the wire kinds it is fed from, best first)
    feeds: list[tuple[np.ndarray, float, tuple[str, ...]]] = []
    if spec.lamp:
        la = spec.lamp
        pts = mount(pp.lamp(la.arm_m, la.rise_deg, la.style, la.head, la.arm_colour), la.az_deg, la.z)
        lights.append(pts["light"])
        feeds.append((pts["feed"], 0.009, ("line",)))
        strap(la.z - (0.4 if la.style == "swan" else 0.3), la.az_deg + 180)
    for m in spec.megaphones:
        pts = mount(pp.megaphone(m.length, m.mouth_r, m.tilt_deg, m.colour), m.az_deg, m.z)
        feeds.append((pts["feed"], 0.008, ("service", "line")))
        strap(m.z)
    for b in spec.boxes:
        part = pp.junction_box(colour=b.colour) if b.kind == "junction" else pp.meter_box(colour=b.colour)
        mount(part, b.az_deg, b.z)
    for r in spec.risers:
        exit_ = pp.riser(k, col, r.az_deg, r.z0, r.z1, r.radius, r.colour, r.head)
        feeds.append((exit_, r.cable_radius, ("bundle", "line") if r.cable_radius > 0.015 else ("line", "service")))
    for cb in spec.cables:
        pts = pp.down_cable(k, col, cb.az_deg, cb.z0, cb.z1, rng)
        feeds.append((pts[0], 0.011, ("line", "service")))
        if cb.coil_z is not None:
            top = min(pts, key=lambda p: abs(p[2] - cb.coil_z))
            pp.slack_coil(k, top, col.out(cb.az_deg + 90), rng, cb.coil_loops, out=col.out(cb.az_deg))
    for m in spec.marks:
        if m.kind == "warning":
            for sgn in (-1, 1):
                mount(pp.warning_mark(flip=sgn < 0), m.az_deg + sgn * 36, m.z)
        elif m.kind == "yellow_plate":
            mount(pp.plate(0.11, 0.15, pp.YELLOW), m.az_deg, m.z)
        else:
            mount(pp.plate(0.16, 0.07, pp.WHITE), m.az_deg, m.z)

    # Cables between things on the pole: each feed to the nearest wire, then the loose jumpers.
    for p, radius, kinds in feeds:
        a = next((n for n in (_nearest(anchors, p, (kind,)) for kind in kinds) if n is not None), None)
        if a is None:
            continue
        gap = float(np.linalg.norm(a.pos - p))
        k.sweep(pp.hang(a.pos, p, min(0.3, 0.12 * gap) * spec.slack, _away((a.pos + p) / 2, rng) * 0.06 * gap),
                radius, pp.CABLE, sides=4 if radius > 0.015 else 3)
    wires = [a for a in anchors if a.kind != "bundle"]
    for _ in range(spec.jumpers):
        for _try in range(20):
            if len(wires) < 2:
                break
            a, b = rng.sample(wires, 2)
            gap = float(np.linalg.norm(a.pos - b.pos))
            if 0.2 < gap < 2.6:
                mid = (a.pos + b.pos) / 2
                k.sweep(pp.hang(a.pos, b.pos, rng.uniform(0.15, 0.5) * spec.slack,
                                _away(mid, rng) * rng.uniform(0.05, 0.3)),
                        0.010, pp.CABLE)
                break
    arm_seats = [a for a in anchors if a.tier >= 0]
    for _ in range(spec.coils):
        if not arm_seats:
            break
        a = rng.choice(arm_seats)
        pp.slack_coil(k, a.pos - UP * 0.1, Column.out(rng.uniform(0, 180)), rng, rng.randint(1, 3),
                      rng.uniform(0.14, 0.26), rng.uniform(0.28, 0.55))

    if spec.lean_deg:
        rot = _lean_matrix(spec.lean_deg, spec.lean_az_deg)
        k.transform(rot)
        anchors = [Anchor(a.name, a.kind, a.tier, rot @ a.pos) for a in anchors]
        lights = [rot @ p for p in lights]
    if spec.cabinet:
        cab = spec.cabinet
        at = Column.out(cab.az_deg) * (cab.distance_m + col.r_base)
        k.add(pp.ground_cabinet(cab.w, cab.d, cab.h, cab.colour).kit, cab.facing_deg, at)
    return Pole(spec, k.mesh(), anchors, lights)


# ----------------------------------------------------------------------------- wires between poles


def span_mesh(a: list[np.ndarray], b: list[np.ndarray], sag_ratio: float = 0.025,
              radius: float = 0.011, colour: str = pp.WIRE, seed: int = 0) -> Mesh:
    """Wires from the seats ``a`` of one placed pole to the seats ``b`` of the next.

    Seats are matched in order across the span (left to left, right to right); when one pole has
    more, the spare ones stay empty. Each wire hangs ``sag_ratio`` of its length, give or take.
    """
    if not a or not b:
        return Mesh.empty()
    rng = random.Random(seed)
    ca, cb = np.mean(a, axis=0), np.mean(b, axis=0)
    d = cb - ca
    side = np.array([-d[1], d[0], 0.0])

    def ordered(pts: list[np.ndarray], n: int) -> list[np.ndarray]:
        pts = sorted(pts, key=lambda p: float(p @ side))
        if len(pts) == n:
            return pts
        return [pts[round(i * (len(pts) - 1) / max(1, n - 1))] for i in range(n)]

    n = min(len(a), len(b))
    k = Kit()
    for p, q in zip(ordered(a, n), ordered(b, n), strict=True):
        length = float(np.linalg.norm(q - p))
        k.sweep(pp.hang(p, q, length * sag_ratio * rng.uniform(0.85, 1.2),
                        n=max(6, min(16, round(length / 3)))), radius, colour)
    return k.mesh()


# ----------------------------------------------------------------------------- random specs

_CONCRETE = ("#a9a69d", "#9c9a92", "#b2ad9f", "#8f8d86", "#a39f93")
_WOOD = ("#7b756b", "#6e675c", "#8a857b")
_STEEL = ("#6b4a35", "#5a4636", "#7a5a40", "#55504a")
_PINS = ("brown", "brown", "white", "glass")


def _round_column(rng, lo: float = 8.4, hi: float = 9.6) -> ColumnSpec:
    return ColumnSpec("round", round(rng.uniform(lo, hi), 2), round(rng.uniform(0.15, 0.17), 3),
                      round(rng.uniform(0.085, 0.10), 3), rng.choice(_CONCRETE))


def _twin_column(rng, axis_deg: float, lo: float = 8.8, hi: float = 10.0) -> ColumnSpec:
    h = round(rng.uniform(lo, hi), 2)
    step = rng.uniform(1.5, 2.0)
    bands = [round(0.8 + step * i, 2) for i in range(int((h - 1.3) / step) + 1)]
    return ColumnSpec("twin", h, round(rng.uniform(0.14, 0.16), 3), round(rng.uniform(0.085, 0.095), 3),
                      rng.choice(_CONCRETE), axis_deg, bands, rng.choice(_STEEL))


def _lamp(rng, z: float, short: bool = False) -> LampSpec:
    style = rng.choice(("straight", "straight", "straight", "swan"))
    arm = rng.uniform(0.35, 0.7) if short else rng.uniform(0.7, 1.5)
    rise = rng.uniform(3, 10) if style == "swan" else rng.uniform(8, 25)
    return LampSpec(round(z, 2), round(-90 + rng.uniform(-12, 12), 1), round(arm, 2), round(rise, 1),
                    style, rng.choice(("cobra", "cobra", "cobra", "led")),
                    rng.choice((pp.STEEL, pp.STEEL, "#70757a", "#9a8f80")))


def _plain(rng, lamp: bool) -> PoleSpec:
    col = _round_column(rng, 8.4 if lamp else 8.0, 9.6 if lamp else 9.2)
    h = col.height
    arm_z = h - rng.uniform(0.18, 0.35)
    length = rng.uniform(1.0, 1.5)
    arm = ArmSpec(round(arm_z, 2), round(length, 2), rng.choice((4, 4, 5, 3) if lamp else (2, 3, 4, 4)),
                  rng.choice((0.0, 180.0)), 0.0, rng.choice(("even", "even", "paired")),
                  "two" if length > 1.3 and rng.random() < 0.5 else "none", rng.choice(_PINS),
                  rng.choice(_STEEL))
    spec = PoleSpec(archetype="lamp" if lamp else "plain", column=col, arms=[arm])
    if rng.random() < 0.55:
        spec.top_pin = arm.insulator
    if lamp:
        spec.lamp = _lamp(rng, arm_z - rng.uniform(0.45, 1.0))
    if rng.random() < 0.75:
        spec.brackets.append(BracketSpec(round(h * rng.uniform(0.58, 0.72), 2),
                                         rng.choice((0.0, 180.0, 90.0)), rng.choice(("pin", "spool")),
                                         rng.choice(("white", "brown"))))
    if rng.random() < (0.6 if lamp else 0.25):
        coil = rng.uniform(2.8, 4.6) if rng.random() < 0.65 else None
        spec.cables.append(CableSpec(round(rng.uniform(20, 160), 1), round(rng.uniform(2.2, 3.2), 2),
                                     round(arm_z - 0.15, 2), None if coil is None else round(coil, 2),
                                     rng.randint(2, 4)))
    if rng.random() < 0.5:
        spec.marks.append(MarkSpec("number_plate", round(rng.uniform(2.2, 2.8), 2), -90.0))
    spec.jumpers = rng.randint(0, 2)
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(0, 1.5), 2), round(rng.uniform(0, 360), 1)
    return spec


def _twin_lamp(rng) -> PoleSpec:
    axis = rng.choice((0.0, 90.0))
    col = _twin_column(rng, axis)
    h = col.height
    steel = col.band_colour
    side = rng.choice((0.0, 180.0))
    arms = [ArmSpec(round(h - rng.uniform(0.15, 0.3), 2), round(rng.uniform(1.6, 2.2), 2),
                    rng.choice((6, 6, 7, 8)), side, 0.0, "even", rng.choice(("none", "two")),
                    rng.choice(_PINS), steel)]
    if rng.random() < 0.5:
        arms.append(ArmSpec(round(arms[0].z - rng.uniform(0.55, 0.8), 2), round(rng.uniform(0.9, 1.3), 2),
                            rng.choice((2, 3, 4)), rng.choice((side, side + 180, 90.0)), 0.0, "even",
                            "none", arms[0].insulator, steel))
    spec = PoleSpec(archetype="twin_lamp", column=col, arms=arms)
    spec.lamp = _lamp(rng, arms[-1].z - rng.uniform(0.4, 0.8))
    for _ in range(rng.randint(1, 2)):
        spec.brackets.append(BracketSpec(round(h * rng.uniform(0.5, 0.74), 2), rng.choice((0.0, 180.0)),
                                         rng.choice(("pin", "spool")), "white"))
    if rng.random() < 0.5:
        spec.cables.append(CableSpec(round(axis + 90 + rng.uniform(-20, 20), 1), 2.6,
                                     round(arms[-1].z - 0.2, 2), round(rng.uniform(3, 5), 2)))
    spec.jumpers = rng.randint(2, 5)
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(0, 1.2), 2), round(rng.uniform(0, 360), 1)
    return spec


def _junction(rng) -> PoleSpec:
    kind = rng.choice(("round", "twin", "strut"))
    col = _twin_column(rng, rng.choice((0.0, 90.0)), 8.6, 9.8) if kind == "twin" else _round_column(rng, 8.6, 9.8)
    h = col.height
    steel = rng.choice(_STEEL)
    pins = rng.choice(_PINS)
    z = h - rng.uniform(0.15, 0.3)
    arms = [ArmSpec(round(z, 2), round(rng.uniform(1.2, 1.8), 2), rng.choice((4, 5, 6)), 0.0, 0.0,
                    "even", rng.choice(("none", "two")), pins, steel)]
    for _ in range(rng.randint(1, 2)):
        z -= rng.uniform(0.5, 0.8)
        arms.append(ArmSpec(round(z, 2), round(rng.uniform(0.8, 1.3), 2), rng.choice((2, 3, 4)),
                            rng.choice((180.0, 90.0, -90.0)), round(rng.uniform(-0.2, 0.2), 2), "even",
                            "none", rng.choice((pins, "white")), steel))
    spec = PoleSpec(archetype="junction", column=col, arms=arms)
    if kind == "strut":
        col.strut = StrutSpec(rng.choice((0.0, 180.0)) + rng.uniform(-15, 15), round(rng.uniform(1.3, 2.0), 2),
                              round(z - rng.uniform(0.3, 0.7), 2))
    for _ in range(rng.randint(1, 3)):
        spec.brackets.append(BracketSpec(round(rng.uniform(4.2, z - 0.4), 2),
                                         rng.choice((0.0, 90.0, 180.0, -90.0)),
                                         rng.choice(("pin", "spool", "hook")), "white"))
    if rng.random() < 0.35:
        spec.lamp = _lamp(rng, z - rng.uniform(0.4, 0.8))
    box_az = rng.choice((-90.0, 90.0, 180.0, 0.0)) + rng.uniform(-10, 10)
    grey = rng.choice((pp.PLASTIC_GREY, "#b6b8b4", "#8f9494", pp.PLASTIC_WHITE))
    low = 0.0
    if rng.random() < 0.8:
        low = rng.uniform(2.2, 3.0)
        spec.boxes.append(BoxSpec("junction", round(low, 2), round(box_az, 1), grey))
    if rng.random() < 0.3:
        spec.boxes.append(BoxSpec("meter", round(rng.uniform(1.3, 1.7), 2), round(box_az + 120, 1), grey))
    if rng.random() < 0.65:
        wide = rng.random() < 0.3
        spec.cabinet = CabinetSpec(round(box_az + rng.uniform(-25, 25), 1), round(rng.uniform(0.45, 0.9), 2),
                                   rng.choice((-90.0, 90.0, 0.0, 180.0)),
                                   round(rng.uniform(0.7, 0.85) if wide else rng.uniform(0.4, 0.55), 2),
                                   round(rng.uniform(0.24, 0.32), 2), round(rng.uniform(0.95, 1.3), 2),
                                   rng.choice((pp.PLASTIC_WHITE, pp.PLASTIC_WHITE, pp.PLASTIC_GREY)))
    for i in range(rng.randint(1, 2)):
        spec.risers.append(RiserSpec(round(box_az + 22 * i + (14 if low else 0), 1),
                                     0.0 if spec.cabinet or not low else round(low, 2),
                                     round(z - rng.uniform(0.2, 0.9), 2), round(rng.uniform(0.022, 0.035), 3),
                                     rng.choice((pp.PLASTIC_GREY, "#7d8084", pp.PLASTIC_WHITE, "#2a2a2c"))))
    for _ in range(rng.randint(1, 3)):
        spec.cables.append(CableSpec(round(box_az + rng.uniform(70, 290), 1), round(rng.uniform(2.0, 3.4), 2),
                                     round(z - rng.uniform(0.0, 1.0), 2), round(rng.uniform(3.0, 6.0), 2),
                                     rng.randint(2, 5)))
    spec.jumpers, spec.coils, spec.slack = rng.randint(6, 12), rng.randint(1, 3), 1.6
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(0.5, 3.5), 2), round(rng.uniform(0, 360), 1)
    return spec


def _twin_riser(rng) -> PoleSpec:
    col = _twin_column(rng, 90.0, 9.0, 10.0)
    h = col.height
    arm_z = h - rng.uniform(0.4, 0.6)
    arm = ArmSpec(round(arm_z, 2), round(rng.uniform(1.8, 2.4), 2), 4, rng.choice((0.0, 180.0)), 0.0,
                  "paired", "none", rng.choice(_PINS), col.band_colour)
    spec = PoleSpec(archetype="twin_riser", column=col, arms=[arm], top_pin=arm.insulator)
    spec.lamp = _lamp(rng, arm_z - rng.uniform(0.3, 0.5), short=True)
    az = arm.side_deg + 180 + rng.uniform(-25, 25)
    box_z = rng.uniform(1.1, 1.5)
    spec.boxes.append(BoxSpec("meter", round(box_z, 2), round(az, 1), rng.choice((pp.PLASTIC_GREY, "#b6b8b4"))))
    top = arm_z - rng.uniform(0.5, 0.9)
    spec.risers.append(RiserSpec(round(az, 1), round(box_z + 0.28, 2), round(top, 2), 0.034,
                                 pp.PLASTIC_WHITE, True, 0.02))
    spec.brackets.append(BracketSpec(round(top + 0.25, 2), round(az + rng.choice((-70, 70)), 1), "hook"))
    if rng.random() < 0.6:
        spec.brackets.append(BracketSpec(round(h * rng.uniform(0.55, 0.7), 2), arm.side_deg, "pin", "white"))
    spec.jumpers = rng.randint(2, 4)
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(0, 1.0), 2), round(rng.uniform(0, 360), 1)
    return spec


def _field(rng) -> PoleSpec:
    h = round(rng.uniform(6.8, 8.0), 2)
    col = ColumnSpec("slim", h, round(rng.uniform(0.10, 0.12), 3), round(rng.uniform(0.065, 0.075), 3),
                     rng.choice(_WOOD))
    arm = ArmSpec(round(h - 0.12, 2), round(rng.uniform(1.6, 2.1), 2), rng.choice((4, 5, 6)), 0.0,
                  round(rng.uniform(-0.15, 0.15), 2), rng.choice(("paired", "even")),
                  rng.choice(("one", "two")), "dark", pp.STEEL_DARK, 1.15)
    spec = PoleSpec(archetype="field", column=col, arms=[arm])
    if rng.random() < 0.8:
        spec.marks.append(MarkSpec("yellow_plate", round(rng.uniform(1.4, 2.0), 2), round(rng.uniform(-120, -60), 1)))
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(3, 11), 2), round(rng.uniform(0, 360), 1)
    return spec


def _megaphone(rng) -> PoleSpec:
    col = _round_column(rng, 8.5, 9.5)
    h = col.height
    spec = PoleSpec(archetype="megaphone", column=col)
    pins = rng.choice(_PINS)
    if rng.random() < 0.5:
        spec.arms.append(ArmSpec(round(h - rng.uniform(0.15, 0.3), 2), round(rng.uniform(0.6, 0.9), 2), 2,
                                 rng.choice((0.0, 180.0)), 0.0, "even", "none", pins, rng.choice(_STEEL)))
    else:
        spec.top_pin = pins
    mz = h * rng.uniform(0.74, 0.84)
    horn = rng.choice((pp.HORN, "#9ea3a6", "#b9bcbe"))
    size = rng.uniform(0.85, 1.15)
    turn = rng.uniform(-15, 15)
    for az in (0.0, 180.0, -90.0)[: 3 if rng.random() < 0.3 else 2]:
        spec.megaphones.append(MegaphoneSpec(round(mz, 2), round(az + turn, 1), round(rng.uniform(4, 12), 1),
                                             round(0.42 * size, 3), round(0.19 * size, 3), horn))
    for i in range(rng.randint(1, 2)):
        spec.brackets.append(BracketSpec(round(mz - rng.uniform(0.3, 0.6), 2), (0.0, 180.0)[i] + turn + 90,
                                         "spool", "white"))
    spec.cables.append(CableSpec(round(turn + 90 + rng.uniform(-20, 20), 1), 2.7, round(mz - 0.1, 2),
                                 round(rng.uniform(3, 4.5), 2) if rng.random() < 0.4 else None))
    if rng.random() < 0.5:
        spec.boxes.append(BoxSpec("junction", 2.55, spec.cables[0].az_deg, pp.PLASTIC_GREY))
    if rng.random() < 0.7:
        spec.marks.append(MarkSpec("warning", round(rng.uniform(0.9, 1.4), 2), round(-90 + rng.uniform(-20, 20), 1)))
    if rng.random() < 0.3:
        spec.lamp = _lamp(rng, mz + rng.uniform(0.6, 1.0))
    spec.jumpers = rng.randint(0, 2)
    spec.lean_deg, spec.lean_az_deg = round(rng.uniform(0, 1.5), 2), round(rng.uniform(0, 360), 1)
    return spec


# name -> (how it is drawn, how often random_spec picks it, what it is)
ARCHETYPES: dict[str, tuple] = {
    "lamp": (lambda rng: _plain(rng, True), 5, "single column, crossarm, street lamp"),
    "plain": (lambda rng: _plain(rng, False), 3, "single column and crossarm, no lamp"),
    "twin_lamp": (_twin_lamp, 2, "two clamped columns, long crossarm, street lamp"),
    "junction": (_junction, 2, "several crossarm tiers, hanging cables, boxes and a cabinet"),
    "twin_riser": (_twin_riser, 1, "two columns with a meter box, riser pipe and bundled cable"),
    "field": (_field, 1, "thin leaning pole with a braced crossarm"),
    "megaphone": (_megaphone, 1, "single column with public address horns"),
}


def random_spec(seed: int, archetype: str | None = None) -> PoleSpec:
    """A pole of ``archetype`` (None: one picked by its weight), drawn from ``seed``."""
    if archetype is None:
        names = sorted(ARCHETYPES)
        archetype = random.Random(seed).choices(names, [ARCHETYPES[n][1] for n in names])[0]
    rng = random.Random(zlib.crc32(f"{archetype}:{seed}".encode()))
    spec = ARCHETYPES[archetype][0](rng)
    spec.seed = seed
    return spec


# The poles that can be placed as objects: prim name -> (archetype, seed).
VARIANTS: dict[str, tuple[str, int]] = {
    "pole_lamp_1": ("lamp", 1),
    "pole_lamp_2": ("lamp", 2),
    "pole_lamp_3": ("lamp", 3),
    "pole_plain_1": ("plain", 1),
    "pole_plain_2": ("plain", 2),
    "pole_twin_lamp_1": ("twin_lamp", 1),
    "pole_twin_lamp_2": ("twin_lamp", 2),
    "pole_junction_1": ("junction", 1),
    "pole_junction_2": ("junction", 2),
    "pole_junction_3": ("junction", 3),
    "pole_twin_riser_1": ("twin_riser", 1),
    "pole_field_1": ("field", 1),
    "pole_field_2": ("field", 2),
    "pole_megaphone_1": ("megaphone", 1),
    "pole_megaphone_2": ("megaphone", 2),
}


def build_variant(name: str, params: dict | None = None) -> Pole:
    """The pole of ``prim:<name>``; ``params`` may override ``archetype`` and ``seed``."""
    archetype, seed = VARIANTS[name]
    p = params or {}
    return build_pole(random_spec(int(p.get("seed", seed)), p.get("archetype", archetype)))
