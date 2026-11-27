"""A building from photos of your own, read by one API call: the model writes the measurement sheet (`SHEET.md` of the photo-to-building
skill, the single source of truth for what a sheet holds and how points, profile, openings and colours are read off the grid),
`landmark_photo.apply` measures it.

    `auto <building> --photo PATH --side NE [--photo PATH --point A] [--photo PATH --view X,Y] [--cam-height 1.6]`

Where each photo was taken is one of: a compass side (the wall of the footprint facing that way, 10 m out from its middle),
a point of the sketch file (`sketchfile.py`, absolute S-JTSK, as the viewer's editor drops them), or local metres. Pure helpers
(`view_from_side`, `view_from_point`, `validate_sheet`, `inject`) need no API and no map; `read_sheet` takes the client as an argument.
"""

from __future__ import annotations

import contextlib
import io
import math
import re
from dataclasses import dataclass
from pathlib import Path

from drycastle_pipeline.core.config import REPO_ROOT

SKILL_SHEET = REPO_ROOT / ".claude" / "skills" / "photo-to-building" / "SHEET.md"
SIDES = ("N", "NE", "E", "SE", "S", "SW", "W", "NW")
MAX_PHOTOS = 3
MIN_POINTS = 5
MIN_EDGE_M = 2.0
VIEW_DISTANCE_M = 10.0
TOP_KEYS = ("what", "source", "photos", "colours", "overhang_m", "not_modelled", "roof", "rest_height_m")
_NUM = r"-?\d+(?:\.\d+)?"
_POINT = re.compile(rf"^[cr]\d+:(?:ground|eave|top|{_NUM}):{_NUM},{_NUM}$")


class SheetError(ValueError):
    """The photos or the model's sheet are not usable; the message is what the user sees."""


@dataclass(frozen=True)
class PhotoSpec:
    """One own photo and where it was taken: `kind` side (value "NE"), point (value "A") or view (value (x, y) in metres)."""

    path: Path
    kind: str
    value: object


# ---- where the photo was taken ----


def _bearing_gap(a: float, b: float) -> float:
    return abs((a - b + 180.0) % 360.0 - 180.0)


def _shared(edge: dict, others: list) -> bool:
    """An edge whose middle lies on another building's wall (an inner wall: no photo sees it)."""
    from drycastle_pipeline.photos.landmark_tools import _seg_dist

    mid = ((edge["p"][0] + edge["q"][0]) / 2, (edge["p"][1] + edge["q"][1]) / 2)
    return any(_seg_dist(mid, tuple(ring[j]), tuple(ring[(j + 1) % len(ring)])) < 0.1 for ring in others for j in range(len(ring)))


def view_from_side(b: dict, side: str, others: list | None = None) -> tuple[float, float]:
    """A viewpoint for "the photo shows the `side` of the building": the middle of the footprint edge whose outward normal points
    closest to that compass side (edges of 2 m or more, not shared with another building when there is another), 10 m out along it.
    `others`: the rings of the neighbouring buildings."""
    from drycastle_pipeline.photos.landmark_tools import _edges

    side = str(side).upper()
    if side not in SIDES:
        raise SheetError(f"side must be one of {', '.join(SIDES)}, not {side!r}")
    edges = [e for e in _edges(b) if e["length"] >= MIN_EDGE_M]
    pool = [e for e in edges if not _shared(e, others or [])] or edges
    if not pool:
        raise SheetError(f"no wall of {b.get('id', 'the building')} is {MIN_EDGE_M:.0f} m long: give --view")
    want = SIDES.index(side) * 45.0
    e = min(pool, key=lambda e: _bearing_gap(e["bearing"], want))
    mid = ((e["p"][0] + e["q"][0]) / 2, (e["p"][1] + e["q"][1]) / 2)
    return (round(mid[0] + VIEW_DISTANCE_M * e["normal"][0], 2), round(mid[1] + VIEW_DISTANCE_M * e["normal"][1], 2))


def view_from_point(label: str, sketch_path: Path | None = None, local=None) -> tuple[float, float]:
    """Local metres of a sketch point of the editor's sketch file (absolute S-JTSK there; `local` turns it, default the map origin)."""
    from drycastle_pipeline.edits import sketchfile

    path = sketch_path or sketchfile.default_path()
    try:
        doc = sketchfile.read(path)
    except sketchfile.SketchFileError as e:
        raise SheetError(str(e)) from e
    if doc is None:
        raise SheetError(f"no sketch file {path}: drop point {label} in the editor first")
    point = next((p for p in doc["points"] if p["id"].upper() == str(label).upper()), None)
    if point is None:
        have = ", ".join(p["id"] for p in doc["points"]) or "none"
        raise SheetError(f"sketch point {label} is not in {path} (it has: {have})")
    if local is None:
        from drycastle_pipeline.photos.landmark_tools import _local as local
    return local((point["x"], point["y"]))


def resolve_view(spec: PhotoSpec, b: dict, others: list | None = None, sketch_path: Path | None = None, local=None) -> tuple[float, float]:
    if spec.kind == "side":
        return view_from_side(b, str(spec.value), others)
    if spec.kind == "point":
        return view_from_point(str(spec.value), sketch_path, local)
    x, y = spec.value
    return (float(x), float(y))


def neighbours(bid: str) -> tuple[dict, list]:
    """(the building, the footprint rings of the other buildings of the map)."""
    import json

    from drycastle_pipeline.photos import landmark_tools as lt

    b = lt._buildings([bid])[0]
    others = [o["footprint"]["exterior"] for o in json.loads((lt.WORK / "buildings.json").read_text())["buildings"] if o["id"] != bid]
    return b, others


# ---- prep, the prompt ----


def prep_photo(bid: str, n: int, photo: Path, view: tuple[float, float], out: Path) -> tuple[str, Path]:
    """What `landmark_photo prep` does for a photo of your own: the footprint seen from `view` as text (also kept as
    `prep_<n>.txt`) and `grid_<n>.png`, the photo with its labelled grid. Returns (the text, the grid)."""
    from drycastle_pipeline.photos import landmark_photo as lp
    from drycastle_pipeline.photos import landmark_tools as lt

    out.mkdir(parents=True, exist_ok=True)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        lt.footprint([bid], view)
    (out / f"prep_{n}.txt").write_text(buf.getvalue())
    return buf.getvalue(), lp.grid(photo, out, name=f"grid_{n}.png")


def _section(md: str, number: str) -> str:
    for part in re.split(r"(?m)^(?=## )", md):
        if part.startswith(f"## {number}."):
            return part
    raise SheetError(f"{SKILL_SHEET.name} has no section {number}: the sheet reader needs it")


def rules_text(md: str) -> str:
    """The reading rules of SHEET.md: how a grid is read and which wall is which (section 1, from what prep prints), the sheet
    and its points, profile, openings and colours (section 2, up to the command that applies it)."""
    one, two = _section(md, "1"), _section(md, "2")
    one = one[one.index("It prints, per photo") :] if "It prints, per photo" in one else one
    two = two.split("\nThen:\n", 1)[0]
    return one.strip() + "\n\n" + two.strip()


def sheet_prompt(md: str, n_photos: int) -> str:
    return (
        "You read photos of one building of a village and write the measurement sheet for it. The rules below come from a guide "
        "written for an agent that runs commands; you run nothing: ignore the commands and file names in it and only answer.\n\n"
        f"<rules>\n{rules_text(md)}\n</rules>\n\n"
        f"You get {n_photos} photo(s), each as its grid image (every grid line carries its number in pixels of the full photo) "
        "preceded by the footprint as seen from where the photo was taken (the corners c0.., the edges and which of them are VISIBLE).\n\n"
        'Reply with ONE JSON object and nothing before or after it, with only these keys: "what", "source" (optional), '
        f'"photos" (a list with exactly {n_photos} entries, one per photo in the order given, each only "points", "profile" (optional) '
        'and "openings"), "colours", "overhang_m" (optional), "not_modelled" (optional), "roof" (optional). '
        "Do not write building, photo, view, cam_height or capture: the program adds them. "
        f"Every photo needs {MIN_POINTS} or more points. Read every pixel off that photo's own grid."
    )


# ---- the sheet ----


def _is_px(v, n: int) -> bool:
    return (
        isinstance(v, list)
        and len(v) == n
        and all(isinstance(p, list) and len(p) == 2 and all(isinstance(c, int | float) and not isinstance(c, bool) and math.isfinite(c) for c in p) for p in v)
    )


def validate_sheet(sheet, n_photos: int) -> dict:
    """The model's sheet, checked for shape and cut to the keys it may give; SheetError says what is wrong."""
    if not isinstance(sheet, dict):
        raise SheetError("the reply is not a JSON object")
    out = {k: sheet[k] for k in TOP_KEYS if k in sheet}
    if not isinstance(out.get("what", "house"), str):
        raise SheetError("what must be a text")
    out.setdefault("what", "house")
    if not isinstance(out.get("colours"), dict):
        raise SheetError("colours is missing (an object: wall, roof, frame)")
    photos = out.get("photos")
    if not isinstance(photos, list) or len(photos) != n_photos:
        raise SheetError(f"photos must be a list of {n_photos} entries (one per photo), got {len(photos) if isinstance(photos, list) else photos!r}")
    if "overhang_m" in out and (isinstance(out["overhang_m"], bool) or not isinstance(out["overhang_m"], int | float)):
        raise SheetError("overhang_m must be a number")
    clean = []
    for i, ph in enumerate(photos):
        if not isinstance(ph, dict):
            raise SheetError(f"photo {i}: not an object")
        pts = ph.get("points")
        if not isinstance(pts, list) or not all(isinstance(p, str) for p in pts):
            raise SheetError(f"photo {i}: points must be a list of WHERE:LEVEL:X,Y texts")
        if len(pts) < MIN_POINTS:
            raise SheetError(f"photo {i}: {MIN_POINTS} or more points are needed, got {len(pts)}")
        bad = [p for p in pts if not _POINT.match(p.replace(" ", ""))]
        if bad:
            raise SheetError(f"photo {i}: point {bad[0]!r} is not WHERE:LEVEL:X,Y (c3:eave:547,485)")
        ops = ph.get("openings", [])
        if not isinstance(ops, list) or not all(isinstance(o, dict) and _is_px(o.get("px"), 2) and isinstance(o.get("edge"), int) for o in ops):
            raise SheetError(f"photo {i}: openings must be a list of {{edge, kind, px: [[x, y], [x, y]]}}")
        entry = {"points": [p.replace(" ", "") for p in pts], "openings": ops}
        prof = ph.get("profile")
        if prof is not None:
            if not (isinstance(prof, dict) and isinstance(prof.get("edge"), int) and (_is_px(prof.get("px"), 1) or _is_px(prof.get("px"), 2))):
                raise SheetError(f"photo {i}: profile must be {{edge, px: [[x, y], [x, y]]}}")
            entry["profile"] = prof
        clean.append(entry)
    out["photos"] = clean
    return out


def inject(sheet: dict, bid: str, meta: list[dict], source: str) -> dict:
    """The validated sheet with what only the program knows: `building`, `source` (unless given) and, per photo, `photo` (absolute
    path), `view` and `cam_height`. `meta`: one {photo, view, cam_height} per photo."""
    photos = [{**m, **ph} for m, ph in zip(meta, sheet["photos"], strict=True)]
    return {"building": bid, **{k: v for k, v in sheet.items() if k != "photos"}, "source": sheet.get("source") or source, "photos": photos}


def read_sheet(client, md: str, texts: list[str], grids: list[Path], attempts: int = 2) -> dict:
    """One API call (a second one when the reply was no good): the validated sheet, before `inject`. `texts`: what prep printed per photo."""
    from drycastle_pipeline.photos import facade_reader as fr

    content: list[dict] = [{"type": "text", "text": sheet_prompt(md, len(grids))}]
    for i, (text, grid) in enumerate(zip(texts, grids, strict=True)):
        content.append({"type": "text", "text": f"Photo {i}: the footprint as seen from where it was taken:\n{text}\nThe grid of photo {i} ({grid.name}):"})
        content.append(fr.image_block(grid))
    content.append({"type": "text", "text": "Reply with the JSON object only, nothing before or after it."})
    problem = ""
    for _ in range(attempts):
        text, usage, stop = fr.ask_model(client, content, "sheet")
        if stop != "end_turn":
            problem = f"the model stopped with {stop}"
            continue
        try:
            sheet = validate_sheet(fr.json_object(text), len(grids))
        except (ValueError, TypeError) as e:  # (a reply that is no JSON is a ValueError too)
            problem = str(e)
            print(f"PROBLEM: the sheet was not usable: {problem}")
            continue
        print(f"sheet read  ({usage['in']} in, {usage['out']} out)")
        return sheet
    raise SheetError(f"the model's answer was not usable: {problem}; try again")
