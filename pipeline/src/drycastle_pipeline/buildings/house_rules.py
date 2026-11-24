"""What a house most likely has where no capture saw it: openings on its other walls, rainwater pipes, the style of
its eaves, a chimney. Used by ``facade_pipeline apply`` after the front has been measured.

Nothing here is chosen by a house "type". Each part follows from facts already known (the roof's form, pitch and
direction, the wall's place in the footprint, the windows measured on the front), in this order, so that the parts fit
each other: walls get their role, then openings, then what hangs on the roof's edge, then the chimney. Everything made
here is a guess and is returned with a line saying so; a later capture of that side replaces it.
"""

import hashlib
import math

import numpy as np

NEAR_NEIGHBOUR_M = 1.5  # a wall this close to another building gets no window
MIN_ATTIC_RISE_M = 2.3  # a roof rising at least this much has rooms under it
WINDOW_EVERY_M = 4.5
DOOR_W_M, DOOR_H_M = 0.95, 2.05
ATTIC_W_M, ATTIC_H_M = 0.8, 1.0
PIPE_FROM_END_M = 0.25
SURROUND_M = 0.12


def wall_roles(ring, edges: list[dict], others: list[dict], roof: str, ridge_dir: float | None, part_ring=None) -> dict[int, dict]:
    """Every wall's place in the house: {edge: {length, normal, shared (a neighbour stands on it), tight (one stands
    within NEAR_NEIGHBOUR_M of it), main (under the pitched roof), roof_edge: eave | gable | flat}}."""
    from shapely.geometry import LineString, Point, Polygon

    part = Polygon(part_ring).buffer(0.1) if part_ring else None
    near = [Polygon(o["footprint"]["exterior"]) for o in others if math.dist(o["footprint"]["exterior"][0], ring[0]) < 80]
    out = {}
    for e in edges:
        p, q = np.asarray(e["p"], float), np.asarray(e["q"], float)
        mid = (p + q) / 2
        wall = LineString([tuple(p + (q - p) * 0.2), tuple(q - (q - p) * 0.2)])
        gap = min((wall.distance(o) for o in near), default=99.0)
        main = part is None or part.contains(Point(*mid))
        if roof == "flat" or not main:
            kind = "flat"
        elif roof == "hipped" or ridge_dir is None:
            kind = "eave"
        else:
            u = (q - p) / e["length"]
            kind = "eave" if abs(u[0] * math.cos(ridge_dir) + u[1] * math.sin(ridge_dir)) > 0.7 else "gable"
        out[e["i"]] = {"length": e["length"], "normal": tuple(e["normal"]), "shared": gap < 0.1, "tight": 0.1 <= gap < NEAR_NEIGHBOUR_M, "main": main, "roof_edge": kind, "bay": False}
    # the faces of a bay: short walls that meet their neighbours at a slant, turning outward (a polygonal projection
    # of the front, not a right-angled step of the footprint)
    n = len(ring)
    for i in out:
        if not 1.0 <= out[i]["length"] < 2.4:
            continue
        turns = []
        for a, b, c in ((ring[i - 1], ring[i], ring[(i + 1) % n]), (ring[i], ring[(i + 1) % n], ring[(i + 2) % n])):
            u, v = np.subtract(b, a), np.subtract(c, b)
            turns.append(math.degrees(math.atan2(u[0] * v[1] - u[1] * v[0], u[0] * v[0] + u[1] * v[1])))
        out[i]["bay"] = all(15.0 <= abs(t) <= 75.0 for t in turns) and max(turns) > 0
    return out


def window_like(openings: list[dict]) -> dict | None:
    """The house's usual window, from the measured ones of the ground floor: middle width, height and sill."""
    ground = [o for o in openings if o["kind"] == "window" and o["sill_m"] < 2.0]
    wins = [o for o in ground if o.get("confidence") != "low"] or ground  # roughly measured windows are still this house's
    if not wins:
        return None
    w, h, sill = (round(float(np.median([o[k] for o in wins])), 2) for k in ("width_m", "height_m", "sill_m"))
    # bars as a window has them: posts that divide it evenly (one in the middle, or two at the thirds); a bar read near
    # an edge is something standing in front of the window
    posts = next((list(o["posts"]) for o in wins if o.get("posts") and all(0.25 <= f <= 0.75 for f in o["posts"])), [])
    posts = [0.5] if len(posts) == 1 else posts
    rails = next((list(o["rails"]) for o in wins if o.get("rails")), [])
    return {"width_m": w, "height_m": h, "sill_m": sill, "posts": posts, "rails": rails}


WINDOW_H_M, WINDOW_SILL_M = (0.9, 1.7), (0.6, 1.4)  # what a ground-floor window of a house can be
SMALL_WINDOW_M = 0.6  # the house's second, small window (bathroom, pantry): square, its head on the same line
BAY_M = 3.6  # one window for about every so much wall


def house_window(measured: list[dict], eave: float) -> dict:
    """The ONE window this house has: a house is built with windows of a single size, their heads on one line. From the
    measured ground-floor windows (their middle), brought inside what a window can be: a measured window whose foot was
    hidden reads too short and too high, so the head is kept and the height and sill are corrected. Nothing measured:
    a usual window. {width_m, height_m, sill_m, posts, rails}."""
    like = window_like(measured)
    if like is None:
        return {"width_m": 1.2, "height_m": 1.3, "sill_m": 0.95, "posts": [0.5], "rails": []}
    head = min(like["sill_m"] + like["height_m"], eave - 0.3)
    h = min(max(like["height_m"], WINDOW_H_M[0]), WINDOW_H_M[1])
    sill = min(max(head - h, WINDOW_SILL_M[0]), WINDOW_SILL_M[1])
    h = min(head - sill, WINDOW_H_M[1])
    return {**like, "width_m": min(max(like["width_m"], 0.5), 2.4), "height_m": round(h, 2), "sill_m": round(sill, 2)}


def to_house_window(o: dict, win: dict) -> dict:
    """A measured ground-floor window that is about the house's window IS the house's window: same size, same head,
    its middle where it was measured. One clearly of another size (rare: a second set) is left as measured."""
    if o["kind"] != "window" or o["sill_m"] >= 2.0 or abs(o["width_m"] - win["width_m"]) > 0.3 * win["width_m"]:
        return o
    mid = o["at_m"] + o["width_m"] / 2
    out = {**o, "at_m": round(mid - win["width_m"] / 2, 2), "width_m": win["width_m"], "height_m": win["height_m"], "sill_m": win["sill_m"]}
    for key in ("posts", "rails"):
        if win.get(key):
            out[key] = list(win[key])
    return out


DOOR_MIN_W_M = 0.85  # a house door is not narrower: a narrower rectangle is the leaf seen half hidden or at an angle
WALL_END_M = 0.15  # an opening keeps at least this much wall to the corner
MERGE_GAP_M = 0.45  # boxes nearer than this, side by side, are sashes of one window
SAME_KIND = 0.4  # a window with at least this share of the wall's largest window's area is that kind of window
EVEN_M = 0.35  # gaps of a row, or its two ends' wall, that differ by less are meant to be the same
CORNER_M = 0.5  # a row with less wall than this at one end is pushed into the corner: the wall's end was measured off


def even_row(openings: list[dict]) -> list[dict]:
    """One kind of window per wall. A wall of a house is built with one window, repeated; what the boxes give instead
    (a sash here, a whole window there, one read short behind a bush) is the measuring's. So on each wall:

    * boxes side by side at one height with under MERGE_GAP_M of wall between them are the sashes of one window;
    * every window at least SAME_KIND of the largest one's area takes the wall's one size (the middle width and
      height of those), about its own middle, and the heads of one storey stand on one line;
    * a clearly smaller window (an attic's, a bathroom's) is left as measured."""
    rest = [dict(o) for o in openings if o["kind"] != "window"]
    out: list[dict] = []
    for edge in sorted({o["edge"] for o in openings if o["kind"] == "window"}):
        wins = sorted((dict(o) for o in openings if o["kind"] == "window" and o["edge"] == edge), key=lambda o: o["at_m"])
        merged: list[dict] = []
        for o in wins:

            def shared(k: dict) -> float:
                return min(k["sill_m"] + k["height_m"], o["sill_m"] + o["height_m"]) - max(k["sill_m"], o["sill_m"])

            # (the one beside it at its own height: a window of the floor above may lie between them in this order)
            k = next((k for k in reversed(merged) if shared(k) >= 0.6 * min(k["height_m"], o["height_m"]) and o["at_m"] - (k["at_m"] + k["width_m"]) < MERGE_GAP_M), None)
            if k is not None:
                end = max(k["at_m"] + k["width_m"], o["at_m"] + o["width_m"])
                foot, head = min(k["sill_m"], o["sill_m"]), max(k["sill_m"] + k["height_m"], o["sill_m"] + o["height_m"])
                k.update({"width_m": round(end - k["at_m"], 2), "sill_m": round(foot, 2), "height_m": round(head - foot, 2), "posts": [0.5]})
                k.pop("rails", None)
                continue
            merged.append(o)
        big = max(o["width_m"] * o["height_m"] for o in merged)
        kind = [o for o in merged if o["width_m"] * o["height_m"] >= SAME_KIND * big]
        if len(kind) > 1:
            w, h = (round(float(np.median([o[key] for o in kind])), 2) for key in ("width_m", "height_m"))
            posts = next((list(o["posts"]) for o in kind if o.get("posts") and all(0.25 <= f <= 0.75 for f in o["posts"])), [0.5] if w >= 0.9 else [])
            for o in kind:
                heads = [k["sill_m"] + k["height_m"] for k in kind if abs(k["sill_m"] + k["height_m"] - o["sill_m"] - o["height_m"]) <= 0.4]
                o["_head"] = float(np.median(heads))
            for o in kind:
                mid = o["at_m"] + o["width_m"] / 2
                o.update({"at_m": round(mid - w / 2, 2), "width_m": w, "height_m": h, "sill_m": round(max(0.3, o.pop("_head") - h), 2)})
                o.pop("rails", None)
                if posts:
                    o["posts"] = list(posts)
                else:
                    o.pop("posts", None)
        out += merged
    return rest + out


def tidy_walls(openings: list[dict], lengths: dict[int, float], edges_seen: set[int]) -> list[dict]:
    """The measured walls' openings set as a builder sets them. A door narrower than DOOR_MIN_W_M is widened about its
    middle, and what stands beside it is pushed along to keep WALL_END_M of wall between them (the door stays as
    measured when the wall has no room for that). A row of like windows spaced nearly evenly is spaced evenly. Then a storey's row that stands within CORNER_M of one
    end of its wall, or is within EVEN_M of the middle already, is moved as one, so the wall left of its first
    opening and right of its last is the same: the wall's ends are measured less well than the openings' places among
    themselves, and a row pushed into a corner is that error, not the house. A row with room at both ends stays. Storeys are centred apart,
    so a window in the gable stays under the ridge whatever stands below it."""
    out = [dict(o) for o in openings]
    for i in edges_seen:
        if i not in lengths:
            continue
        length = lengths[i]
        for row in ([o for o in out if o["edge"] == i and o["sill_m"] < 2.0], [o for o in out if o["edge"] == i and o["sill_m"] >= 2.0]):
            if not row:
                continue
            row.sort(key=lambda o: o["at_m"])
            was = [(o["at_m"], o["width_m"]) for o in row]
            for o in row:
                if o["kind"] == "door" and o["width_m"] < DOOR_MIN_W_M:
                    o.update({"at_m": o["at_m"] + (o["width_m"] - DOOR_MIN_W_M) / 2, "width_m": DOOR_MIN_W_M})
            for a, b in zip(row, row[1:]):
                b["at_m"] = max(b["at_m"], a["at_m"] + a["width_m"] + WALL_END_M)
            first, last = row[0]["at_m"], max(o["at_m"] + o["width_m"] for o in row)
            if last - first > length - 2 * WALL_END_M:  # no room for the wider door: as measured
                for o, (at, w) in zip(row, was):
                    o.update({"at_m": at, "width_m": w})
                first, last = row[0]["at_m"], max(o["at_m"] + o["width_m"] for o in row)
            same = all(o["kind"] == "window" and abs(o["width_m"] - row[0]["width_m"]) <= 0.05 for o in row)
            gaps = [b["at_m"] - a["at_m"] - a["width_m"] for a, b in zip(row, row[1:])]
            if same and len(row) > 2 and max(gaps) - min(gaps) <= EVEN_M:  # nearly evenly spaced is evenly spaced
                gap = sum(gaps) / len(gaps)
                for k, o in enumerate(row):
                    o["at_m"] = first + k * (row[0]["width_m"] + gap)
            shift = (length - last - first) / 2
            pushed, nearly = min(first, length - last) < CORNER_M, abs(shift) <= EVEN_M
            if abs(shift) < 0.02 or last - first > length - 2 * WALL_END_M or not (pushed or nearly):
                shift = 0.0
            for o in row:
                o["at_m"] = round(o["at_m"] + shift, 2)
    return out


def pair_sashes(openings: list[dict], lengths: dict[int, float]) -> list[dict]:
    """A window of which only one sash was boxed is the whole window: when a wall has a two-sash window (two boxes that
    were joined, marked _pair) and, in the same row, a window about half as wide, the narrow one is one sash of a window
    like it. It is widened about its middle to the pair's width, with a post in the middle, where the wall and its
    neighbours leave room."""
    out = [dict(o) for o in openings]
    for o in out:
        if o["kind"] != "window" or o.get("_pair"):
            continue
        head = o["sill_m"] + o["height_m"]
        pair = next((k for k in out if k.get("_pair") and k["edge"] == o["edge"] and abs(k["sill_m"] + k["height_m"] - head) <= 0.25
                     and 0.35 <= o["width_m"] / k["width_m"] <= 0.65), None)  # fmt: skip
        if pair is None or o["edge"] not in lengths:
            continue
        at = round(o["at_m"] + (o["width_m"] - pair["width_m"]) / 2, 2)
        end = at + pair["width_m"]
        if at < WALL_END_M or end > lengths[o["edge"]] - WALL_END_M or any(
                k is not o and k["edge"] == o["edge"] and k["at_m"] < end + WALL_END_M and at < k["at_m"] + k["width_m"] + WALL_END_M
                and k["sill_m"] < head and o["sill_m"] < k["sill_m"] + k["height_m"] for k in out):  # fmt: skip
            continue
        o.update({"at_m": at, "width_m": pair["width_m"], "height_m": pair["height_m"], "sill_m": pair["sill_m"], "posts": [0.5], "_pair": True})
    return out


STRIP_COLOURS = {"gravel": "#b5ae9f", "concrete": "#8f8c85", "slabs": "#a6a199"}  # a footing takes the plinth's colour
PLINTH_STONE, PLINTH_BRICK = "#7c7a77", "#87594a"


def base_style(building_id: str) -> dict:
    """What a house stands on, different from house to house but always the same for one house: the strip round its
    foot {strip: footing|gravel|concrete|slabs, width_m, joints_m} (a footing is the foundation's narrow ledge, no
    paving: grass comes up to it) and what a plinth nobody saw is made of {plinth: paint|stone|brick, plinth_m}."""
    r, w = chance(building_id, "strip"), chance(building_id, "strip width")
    if r < 0.25:
        strip = {"strip": "footing", "width_m": 0.2}
    elif r < 0.5:
        strip = {"strip": "gravel", "width_m": round(0.3 + 0.15 * w, 2)}
    elif r < 0.8:
        strip = {"strip": "concrete", "width_m": round(0.5 + 0.25 * w, 2)}
    else:
        strip = {"strip": "slabs", "width_m": round(0.8 + 0.25 * w, 2), "joints_m": 0.5}
    p = chance(building_id, "plinth kind")
    return {**strip, "plinth": "paint" if p < 0.5 else "stone" if p < 0.8 else "brick", "plinth_m": round(0.2 + 0.25 * chance(building_id, "plinth height"), 2)}


def invent_openings(roles: dict[int, dict], seen: set[int], measured: list[dict], eave: float, top: float, rest_height: float, front_normal, building_id: str = "") -> list[dict]:
    """Openings for the walls no capture saw, by how houses are built:

    * one window size for the whole house (``house_window``), every head on one line;
    * evenly spaced along a wall, one for about every BAY_M of it, so a wall's windows sit symmetrically;
    * a wall too short for the house's window gets none, or (for some, decided from the building's id) the house's
      small second window; a wall under a lower roof gets the house's window set lower, or the small one;
    * none on a wall a neighbour stands on or close to;
    * windows of the house's width high in a gable when the roof has rooms under it, two when the gable is wide;
    * one door on the side away from the street when no door was measured."""
    win = house_window(measured, eave)
    head = win["sill_m"] + win["height_m"]
    bars = {k: win[k] for k in ("posts", "rails") if win.get(k)}
    out: list[dict] = []
    free = {i: r for i, r in roles.items() if i not in seen and not r["shared"] and not r["tight"]}
    for i, r in free.items():
        height, length = (eave if r["main"] else rest_height), r["length"]
        w, h, top_at, extra = win["width_m"], win["height_m"], min(head, height - 0.3), bars
        low, short = top_at - h < 0.5, length < w + 1.2
        if r.get("bay") and not low:  # a bay is glazed: each face has a window nearly as wide as the face, the house's height
            bw = round(min(w, length - 0.5), 2)
            out.append({"kind": "window", "edge": i, "at_m": round((length - bw) / 2, 2), "width_m": bw, "height_m": h, "sill_m": round(top_at - h, 2), **(extra if bw > 1.1 else {})})
            continue
        if low or short:  # no room for the house's window: its small one (on a short wall: on some walls only)
            w = h = SMALL_WINDOW_M
            extra = {}
            if top_at - h < 0.5 or length < 2.0 or (short and chance(building_id, f"small{i}") >= 0.5):
                w = 0.0
        if w:
            n = max(1, min(round(length / BAY_M), int((length - 0.7) // (w + 0.7))))
            gap = (length - n * w) / (n + 1)
            out += [{"kind": "window", "edge": i, "at_m": round(gap + k * (w + gap), 2), "width_m": w, "height_m": h, "sill_m": round(top_at - h, 2), **extra} for k in range(n)]
        if r["roof_edge"] == "gable" and r["main"] and top - eave >= MIN_ATTIC_RISE_M and length >= 3.0:
            aw = min(win["width_m"], ATTIC_W_M + 0.2)
            n = 2 if length >= 8.0 else 1
            for k in range(n):  # about the gable's middle
                mid = length / 2 + (k - (n - 1) / 2) * max(aw + 1.0, length * 0.3)
                out.append({"kind": "window", "edge": i, "at_m": round(mid - aw / 2, 2), "width_m": round(aw, 2), "height_m": ATTIC_H_M, "sill_m": round(eave + 0.4, 2)})
    if not any(o["kind"] == "door" for o in measured):
        fits = {i: r for i, r in free.items() if r["length"] >= DOOR_W_M + 1.2 and (eave if r["main"] else rest_height) >= DOOR_H_M + 0.2}
        if fits:
            fn = np.asarray(front_normal, float)
            i = max(fits, key=lambda k: (-float(np.dot(fits[k]["normal"], fn)), fits[k]["length"]))  # the side most turned from the street
            at = 0.7
            out = [o for o in out if not (o["edge"] == i and o["sill_m"] < DOOR_H_M and o["at_m"] < at + DOOR_W_M + 0.4 and at < o["at_m"] + o["width_m"] + 0.4)]
            out.append({"kind": "door", "edge": i, "at_m": at, "width_m": DOOR_W_M, "height_m": DOOR_H_M, "sill_m": 0.0})
    return out


def invent_downpipes(roles: dict[int, dict], openings: list[dict], roof: str) -> list[dict]:
    """Rainwater pipes where a roof like this has them: on walls with a gutter (eaves), near an end, clear of every
    opening; a hipped roof drains at its two longest eaves only. [{edge, at_m}]."""
    eaves = {i: r for i, r in roles.items() if r["roof_edge"] == "eave" and r["main"] and not r["shared"] and r["length"] >= 3.0}
    if roof == "hipped":
        eaves = dict(sorted(eaves.items(), key=lambda kv: -kv[1]["length"])[:2])
    out = []
    for i, r in eaves.items():
        spots = [r["length"] - PIPE_FROM_END_M, PIPE_FROM_END_M - 0.09] if r["length"] <= 10 else [r["length"] - PIPE_FROM_END_M, PIPE_FROM_END_M - 0.09, None]
        placed = 0
        for at in spots:
            want = 2 if r["length"] > 10 else 1
            if at is None or placed >= want:
                continue
            at = max(0.0, at)
            if any(o["edge"] == i and o["at_m"] - 0.3 <= at <= o["at_m"] + o["width_m"] + 0.3 for o in openings):
                continue
            out.append({"edge": i, "at_m": round(at, 2)})
            placed += 1
    return out


def eave_style(roof: str, pitch_deg: float, measured_overhang: float | None) -> dict:
    """How far the roof sticks out and how its edge is finished: a low hipped roof has a wide boxed eave, a steep gabled
    one a short open eave with the rafters showing. {overhang_m, level_soffit, rafters}."""
    if roof == "flat":
        return {"overhang_m": 0.0, "level_soffit": False, "rafters": False}
    out = measured_overhang if measured_overhang is not None else (0.6 if roof == "hipped" else 0.25 if pitch_deg >= 40 else 0.35)
    return {"overhang_m": round(out, 2), "level_soffit": roof == "hipped" and out >= 0.4, "rafters": roof == "gabled" and pitch_deg >= 35 and out <= 0.5}


def invent_chimney(building_id: str, where: str | None, roof: str, middle, ridge_dir: float, ridge_len: float, top: float, left_dir, front_normal, area: float,
                   half: float | None = None) -> dict | None:  # fmt: skip
    """A chimney just behind the ridge. ``where`` is what the front photo showed (left, middle, right, none); when it
    showed none, none is made; when nobody said, a house (not a shed) gets one a third along the ridge, the end taken
    from the building's id so it is the same on every run."""
    if roof == "flat" or where == "none" or ridge_len < 1.0:
        return None
    if where not in ("left", "middle", "right"):
        if area < 35.0:
            return None
        where = "left" if hashlib.sha256(building_id.encode()).digest()[0] % 2 else "right"
    u = np.array([math.cos(ridge_dir), math.sin(ridge_dir)])
    lu = np.asarray(left_dir, float) / max(float(np.linalg.norm(left_dir)), 1e-9)
    if half and abs(float(u @ lu)) < 0.5 and where != "middle":
        # the ridge runs away from the camera (a gable wall was photographed): left and right are the two slopes, not
        # the ridge's two ends. Half-way down that slope, behind the house's middle
        at = np.asarray(middle, float) + lu * (half * 0.5 if where == "left" else -half * 0.5) - np.asarray(front_normal, float) * min(1.5, ridge_len / 4)
        return {"x": round(float(at[0]), 2), "y": round(float(at[1]), 2), "top_m": round(top + 0.3, 2), "width_m": 0.5, "depth_m": 0.5}
    if float(u @ np.asarray(left_dir, float)) < 0:
        u = -u  # u now points to the photo's left
    along = {"left": ridge_len / 2 - max(0.8, ridge_len * 0.2), "middle": 0.0, "right": -(ridge_len / 2 - max(0.8, ridge_len * 0.2))}[where]
    at = np.asarray(middle, float) + u * along - np.asarray(front_normal, float) * 0.6
    return {"x": round(float(at[0]), 2), "y": round(float(at[1]), 2), "top_m": round(top + 0.5, 2), "width_m": 0.5, "depth_m": 0.5}


PLAIN_WALLS = ("brick", "timber", "stone")  # walls that carry no plaster trim
CORNICE_M, SHUTTER_BOX_M, CORNER_W_M = 0.18, 0.18, 0.3
STEP_RISE_M = 0.18


def chance(building_id: str, what: str) -> float:
    """A number 0..1 that is the same on every run for this building and this question."""
    return int.from_bytes(hashlib.sha256(f"{building_id}:{what}".encode()).digest()[:4], "big") / 2**32


def _said(value) -> bool | None:
    v = "" if value is None else str(value).strip().lower()
    return True if v in ("yes", "true") else False if v in ("no", "false", "none") else None


def trim_style(building_id: str, said: dict, wall_name: str | None, wall_light: bool, surround_hex: str | None, gable_hex: str | None,
               roof: str, eave: float, heads: list[float], plinth_m: float) -> dict:  # fmt: skip
    """The plaster trim of the house, one colour for all of it: a cornice under the eave, a string course between two
    floors, corner strips, shutter boxes over the windows, the gable's own colour. What the photo's reader said
    (``said``: cornice, corner_strips, shutter_boxes as yes/no) is kept; what it did not say is decided here, the same on
    every run. A brick, timber or stone wall gets none of the plaster parts; a part with no room is left out.
    ``heads`` are the tops of the measured openings. {trim_colour, bands, corners, lintel_m, gable_colour, made}."""
    plaster = wall_name not in PLAIN_WALLS
    made: list[str] = []

    def want(key: str, odds: float, what: str) -> bool:
        s = _said(said.get(key))
        if s is None:
            s = chance(building_id, key) < odds
            if s:
                made.append(what)
        return s

    ground = [h for h in heads if h < 3.0]
    upper = [h for h in heads if h >= 3.0]
    out: dict = {"trim_colour": surround_hex or ("#d8d2c4" if wall_light else "#f2f0ea"), "bands": [], "corners": None, "lintel_m": 0.0, "gable_colour": None, "made": made}
    room = eave - max([*ground, *upper], default=0.0)  # wall left above the highest opening
    box = room >= SHUTTER_BOX_M + 0.05 and want("shutter_boxes", 0.6, "shutter boxes over the windows")
    if box:
        out["lintel_m"] = SHUTTER_BOX_M
        room -= SHUTTER_BOX_M
    cornice = plaster and roof != "flat" and room >= CORNICE_M + 0.08 and want("cornice", 1.0, "a cornice band under the eave")
    if cornice:
        out["bands"].append({"top_m": round(eave, 2), "height_m": CORNICE_M})
    if plaster and eave >= 4.6 and ground and upper and (cornice or chance(building_id, "string") < 0.5):
        z = max(ground) + (SHUTTER_BOX_M if box else 0.0) + 0.3  # between the floors, clear of the ground floor's heads
        if z + 0.4 < eave:
            out["bands"].append({"top_m": round(z, 2), "height_m": 0.12})
            made.append("a string course between the floors")
    if plaster and cornice and want("corner_strips", 0.45, "corner strips"):
        out["corners"] = {"width_m": CORNER_W_M, "top_m": round(eave - CORNICE_M, 2)}
    if roof == "gabled" and gable_hex:
        out["gable_colour"] = gable_hex
    if not out["bands"] and not out["corners"] and not plinth_m and plaster and roof != "flat" and room >= 0.2:
        out["bands"].append({"top_m": round(eave, 2), "height_m": 0.1})  # no house is left with nothing between ground and roof
        made.append("a thin band under the eave")
    return out


def dress_door(o: dict, role: dict, eave: float, overhang: float, plinth_m: float, invented: bool, canopy: bool | None = None) -> dict:
    """What a door has besides its leaf: panels; steps up to its sill (an invented door is lifted to the plinth's top, a
    measured one stays where it was seen and gets a doorstep); a small canopy where the roof does not already shelter
    it, i.e. on a gable wall or under an eave more than a metre above its head."""
    if o["kind"] == "garage_door":
        return {**o, "panels": 4}
    out = {**o, "panels": 3}
    if invented and 0.2 <= plinth_m <= 0.6 and (eave if role["main"] else 0.0) >= plinth_m + o["height_m"] + 0.3:
        out["sill_m"] = round(plinth_m, 2)
    out["steps"] = max(1, math.ceil(out["sill_m"] / STEP_RISE_M))
    head = out["sill_m"] + out["height_m"]
    if canopy is not None and not invented:  # a door that was seen has the canopy that was seen
        if canopy and eave - head >= 0.3:
            out["canopy_m"] = 0.7
        return out
    if role["roof_edge"] == "gable" or eave - head >= 1.0 or overhang < 0.3:
        if eave - head >= 0.45 or role["roof_edge"] == "gable":
            out["canopy_m"] = 0.7
    return out


def invent_balcony(said: str | None, edge: int, role: dict, eave: float, top: float, openings: list[dict]) -> dict | None:
    """The balcony a capture saw (``said`` yes) on the gable wall it was seen on: at the attic's floor, which is the
    wall's top, nearly as wide as the wall, clear of the ground floor's openings. None when the roof has no room for
    an attic or the wall is too short."""
    if str(said).lower() != "yes" or role["roof_edge"] != "gable" or top - eave < MIN_ATTIC_RISE_M or role["length"] < 4.0:
        return None
    # the slab lies over the ground floor's doors and windows
    heads = [o["sill_m"] + o["height_m"] + (0.25 if o["kind"] == "window" else 0.55) for o in openings if o["sill_m"] < 1.5]
    floor = max(heads) if heads else eave
    return {"edge": edge, "at_m": 0.5, "width_m": round(role["length"] - 1.0, 2), "floor_m": round(floor, 2)}


def invent_skylights(building_id: str, roles: dict[int, dict], seen: set[int], roof: str, eave: float, top: float, half: float, dormer_edges: set[int]) -> list[dict]:
    """Roof windows where a roof has rooms under it and no dormer lights them: a gabled roof high enough to stand in,
    on the slopes no capture saw, one for a short eave and two for a long one. [{edge, at_m, up_m}]."""
    if roof != "gabled" or top - eave < MIN_ATTIC_RISE_M or dormer_edges or chance(building_id, "skylights") >= 0.7:
        return []
    slope = (top - eave) / half
    run = 1.18 / math.hypot(1.0, slope)
    up = round(0.3 * half, 2)
    if slope < 0.5 or up + run > half - 0.4:
        return []
    out = []
    for i, r in roles.items():
        if r["roof_edge"] != "eave" or not r["main"] or r["shared"] or i in seen or r["length"] < 4.0:
            continue
        n = 1 if r["length"] < 8.0 else 2
        out += [{"edge": i, "at_m": round(r["length"] * (k + 1) / (n + 1), 2), "up_m": up} for k in range(n)]
    return out
