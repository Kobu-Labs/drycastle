"""`facade_pipeline auto`: a building from photos in one command, for a UI to drive (the viewer's "Photo -> building").

    uv run python -m drycastle_pipeline.photos.facade_pipeline auto <building> [--captures 0,2] [--replace] [--redo ends|walls]
    uv run python -m drycastle_pipeline.photos.facade_pipeline auto <building> --photo P.png --side NE [--photo Q.png --point A] [--cam-height 1.6]
    uv run python -m drycastle_pipeline.photos.facade_pipeline auto --check

Captures (the default): ends, read ends, walls, read walls, apply, the stages of `facade_pipeline` with the model's two answers asked
by `facade_reader` (one API call each), in this process, in `data/village/photos/<id>/run`. Photos of your own (`--photo`, up to
3, each with where it was taken: `--side`, `--point` or `--view`): prep, read sheet (one API call, `sheet_reader`), apply, in
`.../own_run`. Needs `ANTHROPIC_API_KEY` (the environment or the repo-root `.env`) and `uv sync --extra reader`.

Output is line based and flushed. Lines a program may parse start with these prefixes, each on its own line:

* `STAGE: <name> <i>/<n>` before each stage (`ends`, `read ends`, `walls`, `read walls`, `apply` / `prep`, `read sheet`, `apply`).
* `ERROR: <message>` on any failure, then exit code 1 (2 when the Anthropic API is not available: no key or no package, checked
  before any stage runs).
* `RESULT: {json}` at the end: building, parcel, method (captures | own), run, overlays, compares, renders, result_json, problems,
  invented, roof, eave_m, top_m, openings, can_redo_ends, replaced.
* `CHECK: {"api_key": bool, "reader": bool}` for `--check` (exit 0).

Every other line is what the stages print (`PROBLEM:`, `MADE UP BY RULE`, ...). After a successful apply
`data/village/photos/<id>/measured.json` holds the building's override block and detail file as the build wrote them
({"override": text | null, "detail": text | null}): what "reset to measured" in the editor goes back to.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import re
import sys
import traceback
from pathlib import Path
from types import SimpleNamespace

from drycastle_pipeline.core.config import REPO_ROOT, load_dotenv
from drycastle_pipeline.edits import building_edit as be
from drycastle_pipeline.photos import facade_reader as fr
from drycastle_pipeline.photos import sheet_reader as sr
from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR

OVERRIDES_PATH = be.OVERRIDES_PATH
DETAILS_DIR = be.DETAILS_DIR
RETRIES = 2  # a reply that fails the check is asked for once more
ANSWER_OF = {"ends": "ends.json", "walls": "names.json"}
STALE = ("overlay_*.png", "compare_*.png", "render_*.png", "result.json")


class AutoError(Exception):
    """The run stops here: the message is the ERROR line, `code` the exit code."""

    def __init__(self, message: str, code: int = 1) -> None:
        super().__init__(message)
        self.code = code


def say(line: str) -> None:
    print(line, flush=True)


# ---- arguments ----


class _Seq(argparse.Action):
    """--photo / --side / --point / --view in the order they were given (a locator belongs to the --photo before it)."""

    def __call__(self, parser, namespace, values, option_string=None):
        namespace.photo_seq = [*(getattr(namespace, "photo_seq", None) or []), (self.dest, values)]


def add_arguments(p: argparse.ArgumentParser) -> None:
    p.add_argument("building", nargs="?", help="building id (wNNN); not needed with --check")
    p.add_argument("--captures", help="capture numbers to use, e.g. 0,2 (default: the newest three)")
    p.add_argument("--replace", action="store_true", help="overwrite the building's entry in overrides.yaml")
    p.add_argument("--redo", choices=("ends", "walls"), help="ask a question of the saved run again (needs the run); implies --replace")
    p.add_argument("--photo", action=_Seq, default=argparse.SUPPRESS, help="a photo of your own (up to 3); follow it with --side, --point or --view")
    p.add_argument("--side", action=_Seq, default=argparse.SUPPRESS, help="where the photo before it was taken: N NE E SE S SW W NW")
    p.add_argument("--point", action=_Seq, default=argparse.SUPPRESS, help="... or a sketch point of the editor, e.g. A")
    p.add_argument("--view", action=_Seq, default=argparse.SUPPRESS, help="... or local metres X,Y")
    p.add_argument("--cam-height", type=float, default=1.6, help="metres above the ground for photos of your own (default 1.6)")
    p.add_argument("--check", action="store_true", help="print whether the API is available (CHECK: {...}) and exit")


def parse_photos(seq: list[tuple[str, str]] | None) -> list[sr.PhotoSpec]:
    """The photos of the command line: each `--photo` followed by exactly one of `--side`, `--point`, `--view`."""
    specs: list[list] = []  # [path, kind | None, value]
    for kind, value in seq or []:
        if kind == "photo":
            specs.append([Path(value), None, None])
            continue
        if not specs:
            raise AutoError(f"--{kind} {value} comes before any --photo: it says where the photo before it was taken")
        if specs[-1][1] is not None:
            raise AutoError(f"photo {specs[-1][0]}: give one of --side, --point, --view")
        if kind == "side":
            if value.upper() not in sr.SIDES:
                raise AutoError(f"--side must be one of {', '.join(sr.SIDES)}, not {value!r}")
            specs[-1][1:] = ["side", value.upper()]
        elif kind == "point":
            specs[-1][1:] = ["point", value.upper()]
        else:
            specs[-1][1:] = ["view", _xy(value)]
    for path, kind, _ in specs:
        if kind is None:
            raise AutoError(f"photo {path}: say where it was taken (--side, --point or --view)")
    if len(specs) > sr.MAX_PHOTOS:
        raise AutoError(f"at most {sr.MAX_PHOTOS} photos at once, got {len(specs)}")
    return [sr.PhotoSpec(*s) for s in specs]


def _xy(text: str) -> tuple[float, float]:
    try:
        x, y = text.split(",")
        return (float(x), float(y))
    except ValueError:
        raise AutoError(f"--view must be X,Y in metres, got {text!r}") from None


def parse_captures(text: str | None) -> list[int] | None:
    if text is None:
        return None
    try:
        got = [int(x) for x in text.split(",") if x.strip() != ""]
    except ValueError:
        raise AutoError(f"--captures must be capture numbers like 0,2, got {text!r}") from None
    if not got or any(n < 0 for n in got):
        raise AutoError(f"--captures must be capture numbers like 0,2, got {text!r}")
    return got


# ---- the API ----


def api_state() -> tuple[bool, bool]:
    """(an API key is set, the anthropic package is installed): the key from the environment, which `load_dotenv` filled from `.env`."""
    from importlib.util import find_spec

    return bool(os.environ.get("ANTHROPIC_API_KEY")), find_spec("anthropic") is not None


def make_client():
    import anthropic

    return anthropic.Anthropic()


def check_api() -> None:
    key, reader = api_state()
    if key and reader:
        return
    missing = " and ".join(x for x, ok in (("ANTHROPIC_API_KEY is not set", key), ("the anthropic package is missing", reader)) if not ok)
    raise AutoError(f"{missing}: put ANTHROPIC_API_KEY in {REPO_ROOT / '.env'} and run `uv sync --extra reader` in pipeline/".replace("`", "'"), 2)


# ---- the run ----


def _store() -> be.Store:
    return be.Store(OVERRIDES_PATH, DETAILS_DIR, PHOTOS_DIR)


def _clean(out: Path, patterns) -> None:
    for pat in patterns:
        for f in out.glob(pat):
            f.unlink()


def _answer(client, out: Path, stage: str) -> None:
    """The model's answer to the `ends` or `walls` question, asked again once when the reply fails the template."""
    problem = "no answer was written"
    for _ in range(RETRIES):
        got = fr.read_one(client, out, False, stage=stage)
        if (out / ANSWER_OF[stage]).exists():
            return
        problem = (got or {}).get("problem") or "no question was open"
    raise AutoError(f"the model's answer was not usable: {problem}; try again")


def _stages_for(redo: str | None, out: Path) -> list[str]:
    if redo == "ends":
        return ["read ends", "walls", "read walls", "apply"]
    if redo == "walls":
        return ([] if (out / "walls_ask.md").exists() else ["walls"]) + ["read walls", "apply"]
    return ["ends", "read ends", "walls", "read walls", "apply"]


def run_captures(a, bid: str, replace: bool, stages, client_of) -> dict:
    out = PHOTOS_DIR / bid / "run"
    if a.redo:
        if not (out / "state.json").is_file():
            raise AutoError(f"nothing to ask again: {bid} has no saved run ({out}); build it first")
        if a.redo == "ends" and not (out / "ends_ask.md").is_file():
            raise AutoError("the wall ends were marked by hand: nothing to ask again")
        if a.redo == "walls" and not (out / "ends.json").is_file():
            raise AutoError("there is no ends.json: build it from the start first")
    plan = _stages_for(a.redo, out)
    captures = parse_captures(a.captures)
    client = None
    for i, name in enumerate(plan, 1):
        say(f"STAGE: {name} {i}/{len(plan)}")
        if name == "ends":  # a fresh run: nothing of an earlier one may be taken for an answer
            out.mkdir(parents=True, exist_ok=True)
            _clean(out, ("ends.json", "names.json", "ends_ask.md", "walls_ask.md", *STALE))
            stages.stage_ends(bid, out, captures=captures)
        elif name == "read ends":
            if a.redo == "ends":
                _clean(out, ("ends.json", "names.json"))
            if (out / "ends.json").exists():
                say("every wall end was marked by hand: nothing to ask")
                continue
            client = client or client_of()
            _answer(client, out, "ends")
        elif name == "walls":
            stages.stage_walls(out)
        elif name == "read walls":
            if a.redo:
                _clean(out, ("names.json",))
            if (out / "names.json").exists():
                continue
            client = client or client_of()
            _answer(client, out, "walls")
        else:
            _clean(out, STALE)
            stages.stage_apply(out, False, replace)
    state = _json(out / "state.json")
    result = _json(out / "result.json")
    return {
        "parcel": state.get("parcel"),
        "method": "captures",
        "run": out,
        "result_json": out / "result.json" if result else None,
        "problems": [str(p) for p in result.get("problems", [])],
        "invented": [str(p) for p in result.get("invented", [])],
        "roof": result.get("roof"),
        "eave_m": result.get("eave_m"),
        "top_m": result.get("top_m"),
        "openings": len((result.get("detail") or {}).get("openings", [])),
        "can_redo_ends": (out / "ends_ask.md").is_file(),
    }


_SUMMARY = re.compile(r"^(\S+): (\w+) roof, eave ([\d.]+) m, top ([\d.]+) m, (\d+) opening")


class _Tee(io.TextIOBase):
    """stdout that also keeps what was written (the own-photo path reads its problems and summary from what apply prints)."""

    def __init__(self, real) -> None:
        self.real, self.text = real, io.StringIO()

    def write(self, s: str) -> int:
        self.text.write(s)
        return self.real.write(s)

    def flush(self) -> None:
        self.real.flush()


def run_own(a, bid: str, replace: bool, photos: list[sr.PhotoSpec], client_of) -> dict:
    from drycastle_pipeline.photos import landmark_photo as lp

    for p in photos:
        if not p.path.is_file():
            raise AutoError(f"photo {p.path} does not exist")
    out = PHOTOS_DIR / bid / "own_run"
    say("STAGE: prep 1/3")
    b, others = sr.neighbours(bid)
    if b.get("model"):
        raise AutoError(f"{bid} is a hand-modelled landmark ({b['model']}): not for this tool")
    views = [sr.resolve_view(p, b, others) for p in photos]
    out.mkdir(parents=True, exist_ok=True)
    _clean(out, ("grid_*.png", "prep_*.txt", "sheet.json", "overlay_*.png"))
    prepped = [sr.prep_photo(bid, n, p.path, v, out) for n, (p, v) in enumerate(zip(photos, views, strict=True))]
    say("STAGE: read sheet 2/3")
    md = _sheet_md()
    sheet = sr.read_sheet(client_of(), md, [t for t, _ in prepped], [g for _, g in prepped])
    names = ", ".join(p.path.name for p in photos)
    meta = [{"photo": str(p.path.resolve()), "view": [v[0], v[1]], "cam_height": a.cam_height} for p, v in zip(photos, views, strict=True)]
    sheet = sr.inject(sheet, bid, meta, f"photo of your own ({names})")
    sheet_path = out / "sheet.json"
    sheet_path.write_text(json.dumps(sheet, indent=1) + "\n")
    say("STAGE: apply 3/3")
    tee = _Tee(sys.stdout)
    with contextlib.redirect_stdout(tee):
        lp.apply(sheet_path, out, replace, False)
    text = tee.text.getvalue()
    got = next((m for ln in text.splitlines() if (m := _SUMMARY.match(ln.strip()))), None)
    return {
        "parcel": b.get("parcel"),
        "method": "own",
        "run": out,
        "result_json": None,
        "problems": [ln[len("PROBLEM: ") :] for ln in text.splitlines() if ln.startswith("PROBLEM: ")],
        "invented": [],
        "roof": got.group(2) if got else None,
        "eave_m": float(got.group(3)) if got else None,
        "top_m": float(got.group(4)) if got else None,
        "openings": int(got.group(5)) if got else 0,
        "can_redo_ends": False,
    }


def _sheet_md() -> str:
    try:
        return sr.SKILL_SHEET.read_text()
    except FileNotFoundError:
        raise AutoError(f"{sr.SKILL_SHEET} is missing: the sheet reader takes its rules from it") from None


def _json(path: Path) -> dict:
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, ValueError):
        return {}


def _files(out: Path, pattern: str) -> list[str]:
    return [str(p.resolve()) for p in sorted(out.glob(pattern)) if p.is_file()]


def _run(a, stages, client_of) -> int:
    if a.check:
        key, reader = api_state()
        say("CHECK: " + json.dumps({"api_key": key, "reader": reader}))
        return 0
    if not a.building:
        raise AutoError("give a building id: auto <building> ...")
    photos = parse_photos(getattr(a, "photo_seq", None))
    if photos and (a.captures or a.redo):
        raise AutoError("photos of your own are not mixed with --captures or --redo" if a.captures else "--redo works on a run of captures, not on photos of your own")
    if a.redo and a.captures:
        raise AutoError("--captures starts a fresh run: not with --redo")
    check_api()  # before any stage work
    replace = bool(a.replace or a.redo)
    before = be.block_text(be._read(OVERRIDES_PATH), a.building)
    if before is not None and not replace:
        raise AutoError(f"{a.building} already has an entry in overrides.yaml: look at it, then pass --replace to overwrite it")
    got = run_own(a, a.building, replace, photos, client_of) if photos else run_captures(a, a.building, replace, stages, client_of)
    out = got["run"]
    snap = be.read_snapshot(a.building, _store())
    measured = PHOTOS_DIR / a.building / "measured.json"
    be.atomic_write(measured, json.dumps(snap, indent=1) + "\n")
    result = {
        "building": a.building,
        "parcel": got["parcel"],
        "method": got["method"],
        "run": str(out.resolve()),
        "overlays": _files(out, "overlay_*.png"),
        "compares": _files(out, "compare_*.png") if got["method"] == "captures" else [],
        "renders": _files(out, "render_*.png") if got["method"] == "captures" else [],
        "result_json": str(got["result_json"].resolve()) if got["result_json"] else None,
        "problems": got["problems"],
        "invented": got["invented"],
        "roof": got["roof"],
        "eave_m": got["eave_m"],
        "top_m": got["top_m"],
        "openings": got["openings"],
        "can_redo_ends": got["can_redo_ends"],
        "replaced": before is not None and replace,
    }
    say("RESULT: " + json.dumps(result, separators=(",", ":")))
    return 0


def run(a, stages=None, client_of=None) -> int:
    """`auto`: returns the exit code. `stages` has stage_ends, stage_walls, stage_apply (default: facade_pipeline's own)."""
    load_dotenv()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True)
    if stages is None:
        from drycastle_pipeline.photos import facade_pipeline as stages
    try:
        return _run(a, stages, client_of or make_client)
    except AutoError as e:
        say("ERROR: " + " ".join(str(e).split()))
        return e.code
    except sr.SheetError as e:
        say("ERROR: " + " ".join(str(e).split()))
        return 1
    except be.BuildingEditError as e:
        say("ERROR: " + " ".join(str(e).split()))
        return 1
    except SystemExit as e:  # the stages end with SystemExit("what is wrong")
        if isinstance(e.code, str):
            say("ERROR: " + " ".join(e.code.split()))
            return 1
        raise
    except Exception as e:  # noqa: BLE001  (whatever else: the line says it, stderr has the trace)
        traceback.print_exc(file=sys.stderr)
        say(f"ERROR: {type(e).__name__}: {' '.join(str(e).split())}")
        return 1


def namespace(**kw) -> SimpleNamespace:
    """The arguments as `add_arguments` parses them, for a caller that builds them by hand (the tests)."""
    base = {"building": None, "captures": None, "replace": False, "redo": None, "cam_height": 1.6, "check": False}
    return SimpleNamespace(**{**base, **kw})
