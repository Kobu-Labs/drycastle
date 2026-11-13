"""The inside of the church (Holy Mass event, ``docs/events/README.md``): one nave, a chancel, a gallery.

Same local frame as ``landmarks.church_solids`` (u across, v along, front at +v, z up from the building base).
``build_shell`` replaces the solid nave and apse blocks by walls with a doorway; the collision mesh is the real
shell (walls, floor, gallery, stair ramp), decoration is in the visual mesh only.
``church_info`` is what the game needs to know, for ``church.json``.
"""

import itertools
import math

from drycastle_pipeline.buildings import hex_to_rgb

T, F = 0.4, 0.25  # wall thickness, floor height (the porch slab's top)
CU = 0.985  # centre line: door, aisle, altar
NX0, NX1, NV0, NV1, ZE = -2.88, 4.86, -7.3, 14.0, 7.4  # nave, outside
IU0, IU1, IV0, IV1 = NX0 + T, NX1 - T, NV0 + T, NV1 - T  # nave, inside
AX0, AX1, AV0, ZA = -1.28, 4.96, -14.25, 5.2  # apse block, outside
CU0, CU1, CV0 = AX0 + T, 2 * CU - (AX0 + T), AV0 + T  # chancel, inside (symmetric about CU)
DOOR_W, DOOR_TOP = 2.0, 2.9
Z_SPRING, VAULT_RISE = 4.9, 2.05  # the vault stays under the eave
GV0, GZ = 9.6, 3.6  # gallery: front edge, floor
STAIR_W, STAIR_V0, STEPS = 0.9, 3.6, 17  # along the left wall, up towards the gallery
BAYS = (-2.8, 1.3, 5.4, 9.5)  # pilasters and ribs
PEW_W, PEW_PITCH, PEW_V0, AISLE_W = 2.0, 1.25, -4.5, 1.8  # the Synty pew is 2.0 x 0.76 m
ROWS_RIGHT, ROWS_LEFT = 11, 6  # the stairs take the rear of the left block
GALLERY_PEWS = ((0.2, 10.9), (2.6, 10.9))  # centre u, v
SEAT_H, SEATS_PER_PEW, SEAT_GAP = 0.51, 3, 0.6  # the Synty pew's seat is 0.51 m up
# standing spots (u, v, way in along v): the free floor by the stairs, and a kneeler before each side altar
STANDING = ((-0.9, 4.6, 3.1), (-0.3, 5.8, 3.1), (-0.9, 7.0, 3.1), (-0.3, 8.2, 3.1), (-1.7, -5.6, -5.6), (3.65, -5.6, -5.6))

CREAM, MARBLE, RIB, WOOD, CARPET = "#efe6d2", "#d9a99a", "#faf6ee", "#4a2e1c", "#8e1b1f"
FLOOR, GOLD, PIPE, PANE, CANVAS = "#d8cdb4", "#c9a03c", "#b9bcc4", "#bcd3e6", "#3b3027"
BAR = "#5b6470"


def pews() -> list[tuple[float, float, int]]:
    """Every pew as (centre u, centre v, floor); sitters face the altar (-v)."""
    off = AISLE_W / 2 + PEW_W / 2
    out = [(CU + off, PEW_V0 + PEW_PITCH * k, 0) for k in range(ROWS_RIGHT)]
    out += [(CU - off, PEW_V0 + PEW_PITCH * k, 0) for k in range(ROWS_LEFT)]
    return out + [(u, v, 1) for u, v in GALLERY_PEWS]


def _prism(S, profile, v0, v1, colour) -> None:
    """Convex (u, z) polygon extruded along v."""
    a = [(u, v0, z) for u, z in profile]
    b = [(u, v1, z) for u, z in profile]
    faces = [(a, colour), (b, colour)]
    for k in range(len(profile)):
        j = (k + 1) % len(profile)
        faces.append(([a[k], a[j], b[j], b[k]], colour))
    S.solid(faces)


def _arc(v0, v1, z_off, thick, S, colour) -> None:
    """The vault's half-ellipse as eight slabs, ``z_off`` above the vault line."""
    c, hw = (IU0 + IU1) / 2, (IU1 - IU0) / 2
    pts = [
        (c + hw * math.cos(math.pi * k / 8), Z_SPRING + VAULT_RISE * math.sin(math.pi * k / 8))
        for k in range(9)
    ]
    for (ua, za), (ub, zb) in itertools.pairwise(pts):
        _prism(
            S,
            [(ua, za + z_off), (ub, zb + z_off), (ub, zb + z_off + thick), (ua, za + z_off + thick)],
            v0,
            v1,
            colour,
        )


def build_shell(S, detail: bool, yellow, plinth) -> None:
    from drycastle_pipeline.landmarks import _arch, _rect

    cream, marble, rib, wood, carpet = (hex_to_rgb(c) for c in (CREAM, MARBLE, RIB, WOOD, CARPET))
    floor, gold, pipe, pane, canvas = (hex_to_rgb(c) for c in (FLOOR, GOLD, PIPE, PANE, CANVAS))

    def wall(x0, x1, y0, y1, z0, z1, inside: str) -> None:
        """Yellow outside, cream inside; ``inside`` is the side the room is on (+x, -x, +y, -y)."""
        xm, ym = (x0 + x1) / 2, (y0 + y1) / 2
        halves = {
            "+x": ((x0, xm, y0, y1), (xm, x1, y0, y1), (x0 - 0.03, xm, y0, y1)),
            "-x": ((xm, x1, y0, y1), (x0, xm, y0, y1), (xm, x1 + 0.03, y0, y1)),
            "+y": ((x0, x1, y0, ym), (x0, x1, ym, y1), (x0, x1, y0 - 0.03, ym)),
            "-y": ((x0, x1, ym, y1), (x0, x1, y0, ym), (x0, x1, ym, y1 + 0.03)),
        }
        outer, inner, base = halves[inside]
        S.box(*outer, z0, z1, yellow)
        S.box(*inner, z0, z1, cream)
        if z0 == 0.0:
            S.box(*base, 0.0, 0.6, plinth)

    # nave: side walls, front wall round the doorway, rear wall round the chancel arch
    wall(NX0, IU0, NV0, NV1, 0.0, ZE, "+x")
    wall(IU1, NX1, NV0, NV1, 0.0, ZE, "-x")
    wall(IU0, CU - DOOR_W / 2, IV1, NV1, 0.0, ZE, "-y")
    wall(CU + DOOR_W / 2, IU1, IV1, NV1, 0.0, ZE, "-y")
    wall(CU - DOOR_W / 2, CU + DOOR_W / 2, IV1, NV1, DOOR_TOP, ZE, "-y")
    wall(IU0, CU0, NV0, IV0, 0.0, ZE, "+y")
    wall(CU1, IU1, NV0, IV0, 0.0, ZE, "+y")
    wall(CU0, CU1, NV0, IV0, ZA - 0.4, ZE, "+y")
    # chancel in the apse block
    wall(AX0, CU0, AV0, NV0, 0.0, ZA, "+x")
    wall(CU1, AX1, AV0, NV0, 0.0, ZA, "-x")
    wall(CU0, CU1, AV0, CV0, 0.0, ZA, "+y")
    S.box(CU0, CU1, CV0, NV0, ZA - 0.2, ZA, cream)
    # floors
    S.box(IU0, IU1, NV0, NV1, 0.0, F, floor)
    S.box(CU0, CU1, CV0, NV0, 0.0, F, floor)
    # gallery on two columns, parapet (open at the stair head), organ case
    S.box(IU0, IU1, GV0, IV1, GZ - 0.2, GZ, cream)
    S.box(IU0 + STAIR_W, IU1, GV0, GV0 + 0.15, GZ, GZ + 0.8, rib)
    for dx in (-1.6, 1.6):
        S.box(CU + dx - 0.2, CU + dx + 0.2, GV0, GV0 + 0.4, F, GZ - 0.2, marble)
    S.box(CU - 1.9, CU + 1.9, 12.8, IV1, GZ, GZ + 1.3, wood)
    # stairs: steps to look at, a ramp to walk on
    rise, run = (GZ - F) / STEPS, (GV0 - STAIR_V0) / STEPS
    u0, u1 = IU0, IU0 + STAIR_W
    if detail:
        for k in range(STEPS):
            S.box(u0, u1, STAIR_V0 + k * run, STAIR_V0 + (k + 1) * run, F, F + (k + 1) * rise, floor)
    else:
        a, b, c = (STAIR_V0, F), (GV0, F), (GV0, GZ)
        lo = [(u0, v, z) for v, z in (a, b, c)]
        hi = [(u1, v, z) for v, z in (a, b, c)]
        S.solid(
            [(lo, floor), (hi, floor)]
            + [([lo[i], lo[j], hi[j], hi[i]], floor) for i, j in ((0, 1), (1, 2), (2, 0))]
        )
    # high altar backdrop (pews, altar table, lectern and stoup are props the game places, see church_info)
    S.box(CU - 1.3, CU + 1.3, CV0, CV0 + 0.4, F, 3.6, marble)
    # pilasters and side altars by the arch: solid, so nobody walks through them
    for v in BAYS:
        for x0, x1 in ((IU0, IU0 + 0.2), (IU1 - 0.2, IU1)):
            S.box(x0, x1, v - 0.3, v + 0.3, F, Z_SPRING - 0.4, marble)
    for x0, x1 in ((IU0 + 0.2, CU0 - 0.2), (CU1 + 0.2, IU1 - 0.2)):
        S.box(x0 - 0.1, x1 + 0.1, IV0, IV0 + 0.7, F, F + 1.0, rib)
        S.box(x0, x1, IV0, IV0 + 0.4, F + 1.0, 3.2, marble)
    if not detail:
        return

    def window(face, plane, at, w, z0, z1, rise) -> None:
        """Arched window: a white surround, the glass set in it, dark glazing bars."""
        S.plate(face, plane, at, _arch(w + 0.24, z0 - 0.12, z1, rise + 0.12), 0.05, rib)
        S.plate(face, plane, at, _arch(w, z0, z1, rise), 0.08, pane)
        bar = hex_to_rgb(BAR)
        for k in range(1, 3 if w > 0.6 else 2):  # uprights
            a = -w / 2 + w * k / (3 if w > 0.6 else 2)
            S.plate(face, plane, at + a, _rect(0.03, z0, z1 + rise * 0.6), 0.1, bar)
        z = z0 + 0.45
        while z < z1 + 0.01:  # cross bars
            S.plate(face, plane, at, _rect(w, z - 0.015, z + 0.015), 0.1, bar)
            z += 0.45

    # vault, ribs over pilasters, ridge rib
    _arc(IV0, IV1, 0.0, 0.1, S, cream)
    c = (IU0 + IU1) / 2
    S.box(c - 0.1, c + 0.1, IV0, IV1, Z_SPRING + VAULT_RISE - 0.14, Z_SPRING + VAULT_RISE, rib)
    for v in BAYS:
        _arc(v - 0.15, v + 0.15, -0.14, 0.14, S, rib)
        for x0, x1, cap in ((IU0, IU0 + 0.2, IU0 + 0.28), (IU1 - 0.2, IU1, IU1 - 0.28)):
            S.box(min(x0, cap), max(x1, cap), v - 0.38, v + 0.38, Z_SPRING - 0.4, Z_SPRING, rib)
    # windows and paintings between the pilasters
    for k, v in enumerate((-4.85, -0.75, 3.35, 7.45, 11.55)):
        for face, plane in (("right", IU0), ("left", IU1)):
            window(face, plane, v, 0.9, 2.7, 4.0, 0.6)
            if k < 2 or (face == "left" and k < 4):
                S.plate(face, plane, v, _rect(1.1, 1.1, 2.4), 0.05, gold)
                S.plate(face, plane, v, _rect(0.9, 1.2, 2.3), 0.07, canvas)
    # chancel: windows, sacristy door, high altar; side altars by the arch
    for dx in (-1.58, 1.58):
        window("front", CV0, CU + dx, 0.45, 2.2, 3.8, 0.3)
    S.plate("right", CU0, -9.9, _rect(1.1, F, F + 2.1), 0.05, wood)
    S.plate("front", CV0 + 0.4, CU, _arch(1.4, F + 1.1, 3.0, 0.5), 0.05, gold)
    S.plate("front", CV0 + 0.4, CU, _arch(1.0, F + 1.3, 2.8, 0.4), 0.08, canvas)
    S.box(CU - 0.8, CU + 0.8, CV0, CV0 + 0.4, 3.6, 4.3, gold)
    for x0, x1 in ((IU0 + 0.2, CU0 - 0.2), (CU1 + 0.2, IU1 - 0.2)):
        S.plate("front", IV0 + 0.4, (x0 + x1) / 2, _arch(0.7, F + 1.2, 2.6, 0.3), 0.05, gold)
    # organ pipes and gilding, carpet
    for i in range(-9, 10):
        x = CU + 0.19 * i
        S.box(x - 0.07, x + 0.07, 12.9, 13.04, GZ + 1.3, 5.6 + 1.1 * abs(math.cos(i * math.pi / 6)), pipe)
    S.box(CU - 1.95, CU + 1.95, 12.76, 12.8, GZ + 1.15, GZ + 1.3, gold)
    S.box(CU - 0.6, CU + 0.6, -11.4, IV1, F, F + 0.015, carpet)


def church_info(b: dict, dz: float = 0.0) -> dict:
    """``church.json``: door, aisle, seats (``stand``: a standing spot, ``pos`` on the floor) with routes, spots, demon anchors and shelter volumes of church ``b``.

    Points are ``[x east, y north, z up]`` in local metres (as ``parcels.json``; z as ``terrain.json``), yaws are
    compass bearings in degrees (0 north, 90 east), normals are (east, north, up).
    """
    ex = b["footprint"]["exterior"]
    ox, oy = (sum(p[k] for p in ex) / len(ex) for k in (0, 1))
    a = float(b["model_axis"])
    vx, vy = math.cos(a), math.sin(a)  # v axis; the u axis is (vy, -vx)

    def pt(u, v, z):
        return [round(ox + u * vy + v * vx, 3), round(oy - u * vx + v * vy, 3), round(z + dz, 3)]

    def vec(u, v, z):
        return [round(u * vy + v * vx, 4), round(-u * vx + v * vy, 4), z]

    def bearing(du, dv):
        e, n = du * vy + dv * vx, -du * vx + dv * vy
        return round(math.degrees(math.atan2(e, n)) % 360, 2)

    into, out = bearing(0, -1), bearing(0, 1)
    aisle = [pt(CU, 15.2, F), pt(CU, 13.0, F)]
    stair_u = IU0 + STAIR_W / 2
    up = aisle + [
        pt(CU, 3.1, F), pt(stair_u, 3.1, F), pt(stair_u, STAIR_V0, F), pt(stair_u, GV0, GZ),
        pt(stair_u, 10.15, GZ),
    ]  # fmt: skip
    seats, props = [], []
    for u, v, fl in pews():
        z = GZ if fl else F
        props.append({"kind": "pew", "pos": pt(u, v, z), "yaw": into})
        for s in range(SEATS_PER_PEW):
            su = u + SEAT_GAP * (s - (SEATS_PER_PEW - 1) / 2)
            path = up + [pt(su, 10.15, GZ)] if fl else aisle + [pt(CU, v - 0.62, F), pt(su, v - 0.62, F)]
            seats.append({
                "id": len(seats), "pos": pt(su, v, z + SEAT_H), "yaw": into, "path": path, "floor": fl,
            })  # fmt: skip
    for u, v, via in STANDING:
        seats.append({
            "id": len(seats), "pos": pt(u, v, F), "yaw": into, "path": aisle + [pt(CU, via, F), pt(u, via, F)],
            "floor": 0, "stand": True,
        })  # fmt: skip
    props += [
        {"kind": "altar", "pos": pt(CU, -11.9, F), "yaw": out},
        {"kind": "lectern", "pos": pt(CU + 1.4, -8.3, F), "yaw": out},
        {"kind": "holy_water", "pos": pt(CU + 2.3, 12.9, F), "yaw": into},
    ]
    # demon anchors: per bay low wall, high wall, vault, high wall, low wall; linked round the bay and along the nave
    slots = (
        ((IU0, 2.2), (1, 0, 0)), ((IU0, 4.4), (1, 0, 0)), (((IU0 + IU1) / 2, Z_SPRING + VAULT_RISE - 0.1), (0, 0, -1)),
        ((IU1, 4.4), (-1, 0, 0)), ((IU1, 2.2), (-1, 0, 0)),
    )  # fmt: skip
    bays = (-5.0, -1.0, 3.0, 7.0)
    anchors = []
    for i, v in enumerate(bays):
        for j, ((u, z), n) in enumerate(slots):
            links = [i * 5 + k for k in (j - 1, j + 1) if 0 <= k < 5]
            links += [k * 5 + j for k in (i - 1, i + 1) if 0 <= k < len(bays)]
            anchors.append({"id": i * 5 + j, "pos": pt(u, v, z), "normal": vec(*n), "links": sorted(links)})

    def room(x0, x1, y0, y1, z1):
        ring = [pt(x, y, 0)[:2] for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
        return {"polygon": ring, "z0": round(dz - 0.2, 3), "z1": round(dz + z1, 3)}

    return {
        "door": dict(zip("xyz", pt(CU, 17.2, 0.0)), yaw=into),
        "doorway": {"pos": pt(CU, (IV1 + NV1) / 2, F), "yaw": out, "width": DOOR_W, "height": DOOR_TOP - F},
        "exit": {"pos": pt(CU, 18.5, 0.0), "yaw": into},
        "aisle": aisle,
        "seats": seats,
        "pastor": {"pos": pt(CU, -9.3, F), "yaw": out},
        "monks": [{"pos": pt(CU + dx, -8.9, F), "yaw": out} for dx in (-1.2, 1.2)],
        "sacristy_door": {"pos": pt(CU0 + 0.5, -9.9, F), "yaw": bearing(1, 0)},
        "props": props,
        "anchors": anchors,
        "rooms": [room(NX0 + 0.2, NX1 - 0.2, NV0, NV1 - 0.2, ZE), room(AX0 + 0.2, AX1 - 0.2, AV0 + 0.2, NV0, ZA)],
    }
