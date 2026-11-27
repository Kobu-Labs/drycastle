"""Edit one building's override entry and detail file from the viewer's "Photo -> building" tool (the worker's ops, see
:func:`building_run.serve`).

Ops (JSON lines, answered `{"id": N, "ok": true, "result": {...}}` or `{"id": N, "ok": false, "error": "..."}`):

* `state`     what the panel shows: the form (roof, wall top, top), the detail (colours, plinth, openings, ...), the footprint's edges.
* `edit`      one change (see :func:`edit`); answers the new state, the text of both files before and after (a *snapshot*) and the
  opening it touched.
* `snapshot`  the text of both files now. `restore` writes a snapshot back exactly (undo / redo). `reset` writes back what the
  automatic build measured (`data/village/photos/<bid>/measured.json`).

The files are changed with minimal diffs, atomically, under the lock `landmark_photo._upsert_override` takes:

* `overrides.yaml`: only the lines of the building's block (`  <bid>:` and the 4-indented lines under it; the comment lines
  right above it belong to the block). The rest of the file is byte-identical.
* `config/buildings/<bid>.yaml`: split into a head (leading comments) and chunks (a key at column 0 and what follows it up to
  the next such key). An edit rewrites only the chunks it touches; the openings keep their rows as written, only the touched
  row is made again.

Nothing is written before the result passed `openings.parse_detail` and `derive.load_overrides` (an invalid edit changes nothing).
"""

from __future__ import annotations

import contextlib
import fcntl
import json
import math
import os
import re
import tempfile
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

import yaml

from drycastle_pipeline.core.config import PIPELINE_DIR, REPO_ROOT
from drycastle_pipeline.core.rules import YAML_LOADER

OVERRIDES_PATH = PIPELINE_DIR / "config" / "overrides.yaml"
DETAILS_DIR = PIPELINE_DIR / "config" / "buildings"
PHOTOS_DIR = REPO_ROOT / "data" / "village" / "photos"
OPS = ("state", "edit", "restore", "snapshot", "reset")

ROOFS = ("flat", "gabled", "hipped", "lean_to")
MIN_RISE_M = 0.3
DEFAULT_RISE_M = 3.0
EDITED_NOTE = "edited in the viewer"
NEW_DETAIL = "id: {bid}\nsource: edited in the viewer\noverhang_m: 0.3\nopenings: []\n"
OPENING_DEFAULTS = {  # kind: (width, height, sill)
    "window": (1.2, 1.3, 1.0),
    "door": (1.0, 2.1, 0.0),
    "garage_door": (2.4, 2.1, 0.0),
    "recess": (1.2, 2.1, 0.0),
}
ID_PREFIX = {"window": "w", "door": "d", "garage_door": "g", "recess": "r"}
# what only some kinds take (parse_detail refuses it elsewhere): dropped when an opening changes kind
_ONLY_WINDOW = ("sill",)
_ONLY_DOOR = ("steps", "canopy_m")
_ONLY_LEAF = ("panels",)
_NO_FRAME = ("frame_m", "posts", "rails")


class BuildingEditError(ValueError):
    """The edit cannot be made; the message is what the editor shows."""


@dataclass(frozen=True)
class Store:
    """Where the files are: the overrides file, the detail files, the photo folders (`<bid>/measured.json`)."""

    overrides: Path = OVERRIDES_PATH
    details: Path = DETAILS_DIR
    photos: Path = PHOTOS_DIR

    @classmethod
    def of(cls, world) -> Store:
        return cls(world.overrides_path, world.details_dir, getattr(world, "photos_dir", None) or PHOTOS_DIR)

    def detail_path(self, bid: str) -> Path:
        return self.details / f"{bid}.yaml"

    def measured_path(self, bid: str) -> Path:
        return self.photos / bid / "measured.json"


def _load(text: str | None):
    return (yaml.load(text, Loader=YAML_LOADER) if text else None) or {}


# ---- files: lock and atomic write ----


@contextlib.contextmanager
def overrides_lock() -> Iterator[None]:
    """One writer at a time over the overrides and detail files (the lock file `landmark_photo._upsert_override` takes; it lives outside the repo)."""
    with open(Path(tempfile.gettempdir()) / "drycastle-overrides.lock", "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        yield


def atomic_write(path: Path, text: str) -> None:
    """Write `text` as `path`: a temp file in the same folder, then `os.replace` (a watcher never sees half a file)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        tmp.write_text(text)
        os.replace(tmp, path)
    finally:
        tmp.unlink(missing_ok=True)


def _read(path: Path) -> str | None:
    try:
        return path.read_text()
    except FileNotFoundError:
        return None


# ---- the building's block in overrides.yaml ----

_FIELD = re.compile(r"^    ([A-Za-z_]\w*):")


def _is_entry(text: str, bid: str) -> bool:
    """`  <bid>:` alone on its line, or with a flow value after it."""
    m = re.match(rf"^  {re.escape(bid)}:(.*)$", text)
    return bool(m)


def _is_flow(text: str) -> bool:
    rest = text.split(":", 1)[1].strip()
    return bool(rest) and not rest.startswith("#")


def find_block(lines: list[str], bid: str) -> tuple[int, int, int] | None:
    """(first line, entry line, end) of the building's block: its comment lines (`  # ...`) above, the `  <bid>:` line, the
    4-indented lines under it."""
    for i, ln in enumerate(lines):
        if _is_entry(ln.rstrip("\r\n"), bid):
            end = i + 1
            while end < len(lines) and lines[end].startswith("    "):
                end += 1
            start = i
            while start > 0 and lines[start - 1].startswith("  # "):
                start -= 1
            return start, i, end
    return None


def block_text(text: str | None, bid: str) -> str | None:
    """The building's block of an overrides file (comment lines, entry, fields), or None."""
    if text is None:
        return None
    lines = text.splitlines(keepends=True)
    at = find_block(lines, bid)
    return "".join(lines[at[0] : at[2]]) if at else None


def _scalar(v) -> str:
    if isinstance(v, bool):
        return "True" if v else "False"  # as landmark_photo writes them
    if isinstance(v, str):
        if v.startswith("#"):
            return f'"{v}"'
        return v if re.fullmatch(r"[A-Za-z_][\w.\-]*", v) else json.dumps(v)
    if isinstance(v, list | tuple):
        return yaml.safe_dump(json.loads(json.dumps(v)), default_flow_style=True, width=10**6).strip()
    return str(v)


def _ends_with_newline(lines: list[str]) -> None:
    if lines and not lines[-1].endswith("\n"):
        lines[-1] += "\n"


def _buildings_end(lines: list[str]) -> int:
    """Where a new entry goes: the end of the `buildings:` mapping (before the next top-level key, else the file's end), past no blank line."""
    start = next((i for i, ln in enumerate(lines) if ln.startswith("buildings:")), None)
    end = len(lines)
    if start is not None:
        end = next((i for i in range(start + 1, len(lines)) if lines[i][:1] not in ("", " ", "#", "\n", "\r", "\t")), len(lines))
    while end > 0 and not lines[end - 1].strip():
        end -= 1
    return end


def _insert_block(lines: list[str], block: list[str]) -> None:
    if not any(ln.startswith("buildings:") for ln in lines):
        _ends_with_newline(lines)
        lines += ["\n", "buildings:\n"] if lines else ["buildings:\n"]
    at = _buildings_end(lines)
    if at:
        _ends_with_newline(lines[at - 1 : at])
        if not lines[at - 1].endswith("\n"):
            lines[at - 1] += "\n"
    lines[at:at] = ["\n", *block]


def _drop_block(lines: list[str], start: int, end: int) -> None:
    del lines[start:end]
    if start > 0 and not lines[start - 1].strip() and (start >= len(lines) or not lines[start].strip()):
        del lines[start - 1]  # the blank line that set it apart goes with it


def with_block(text: str | None, bid: str, block: str | None) -> str | None:
    """The overrides text with the building's block replaced by `block` (comment lines included), added at the end of `buildings:`
    when there is none, or removed when `block` is None. Nothing else changes."""
    lines = (text or "").splitlines(keepends=True)
    at = find_block(lines, bid)
    new = block.splitlines(keepends=True) if block else []
    _ends_with_newline(new)
    if at and not new:
        _drop_block(lines, at[0], at[2])
    elif at:
        lines[at[0] : at[2]] = new
    elif new:
        _insert_block(lines, new)
    return "".join(lines) if (lines or text is not None) else None


def edit_fields(text: str | None, bid: str, changes: dict, parcel: str) -> str | None:
    """The overrides text with `changes` ({field: value, None removes it}) made in the building's block, line by line. A field
    already at that value is left as written. An entry left with no field goes (with its comments). No block yet: a new one."""
    lines = (text or "").splitlines(keepends=True)
    at = find_block(lines, bid)
    cur: dict = {}
    if at:
        cur = dict((_load("".join(lines[at[1] : at[2]])).get(bid)) or {})

    def differs(k, v) -> bool:
        if v is None:
            return k in cur
        return k not in cur or cur[k] != v or isinstance(cur[k], bool) != isinstance(v, bool)

    todo = {k: v for k, v in changes.items() if differs(k, v)}
    if not todo:
        return text
    if at is None:
        fields = {k: v for k, v in todo.items() if v is not None}
        if not fields:
            return text
        _insert_block(lines, [f"  # {parcel}: {EDITED_NOTE}\n", f"  {bid}:\n", *(f"    {k}: {_scalar(v)}\n" for k, v in fields.items())])
        return "".join(lines)
    start, entry, end = at
    left = (set(cur) - {k for k, v in todo.items() if v is None}) | {k for k, v in todo.items() if v is not None}
    if not left:
        _drop_block(lines, start, end)
        return "".join(lines)
    head, body = lines[entry], lines[entry + 1 : end]
    if _is_flow(head.rstrip("\r\n")):  # a flow mapping on the entry line: written out as a block
        head, body = f"  {bid}:\n", [f"    {k}: {_scalar(v)}\n" for k, v in cur.items()]
    groups: list[list] = []  # [key | None, its lines]
    for ln in body:
        m = _FIELD.match(ln)
        if m or not groups:
            groups.append([m.group(1) if m else None, [ln]])
        else:
            groups[-1][1].append(ln)
    for k, v in todo.items():
        idx = next((i for i, g in enumerate(groups) if g[0] == k), None)
        if v is None:
            del groups[idx]
        elif idx is None:
            if groups:
                _ends_with_newline(groups[-1][1])
            groups.append([k, [f"    {k}: {_scalar(v)}\n"]])
        else:
            groups[idx][1] = [f"    {k}: {_scalar(v)}\n"]
    lines[entry:end] = [head, *(ln for g in groups for ln in g[1])]
    return "".join(lines)


# ---- the detail file ----

_KEY = re.compile(r"^([A-Za-z_]\w*):")
_ROW = re.compile(r"^\s+- \{.*\}\s*$")


class Chunk:
    """One key of the detail file at column 0 with what belongs to it: comment lines above (`lead`), the key line and what follows."""

    def __init__(self, key: str, lead: list[str], lines: list[str]) -> None:
        self.key, self.lead, self.lines = key, lead, lines

    def value(self):
        return _load("".join(self.lines)).get(self.key)

    def all_lines(self) -> list[str]:
        return [*self.lead, *self.lines]


class DetailText:
    """A detail file as a head (the comment lines before the first key) and chunks; `text()` puts them back, byte for byte when
    nothing changed."""

    def __init__(self, text: str) -> None:
        self.head: list[str] = []
        self.chunks: list[Chunk] = []
        pending: list[str] = []  # col-0 comments seen since the last chunk: they lead the next key
        for ln in text.splitlines(keepends=True):
            m = _KEY.match(ln)
            if m:
                if not self.chunks:
                    self.head.extend(pending)  # (the comments before the first key are the file's head)
                    pending = []
                self.chunks.append(Chunk(m.group(1), pending, [ln]))
                pending = []
            elif ln.startswith("#"):
                pending.append(ln)
            else:
                into = self.chunks[-1].lines if self.chunks else self.head
                into.extend(pending)
                pending = []
                into.append(ln)
        (self.chunks[-1].lines if self.chunks else self.head).extend(pending)

    def text(self) -> str:
        out = [*self.head]
        for c in self.chunks:
            _ends_with_newline(out)
            out += c.all_lines()
        return "".join(out)

    def chunk(self, key: str) -> Chunk | None:
        return next((c for c in self.chunks if c.key == key), None)

    def get(self, key: str, default=None):
        c = self.chunk(key)
        return default if c is None else c.value()

    def set(self, key: str, value) -> None:
        """Make the chunk of `key` again from `value` (None removes it). A new key goes before `interior`/`openings`, else at the end."""
        c = self.chunk(key)
        if value is None:
            if c:
                self.chunks.remove(c)
            return
        nested = isinstance(value, dict | list)  # (a mapping of scalars alone would be dumped as one flow mapping: `{overhang_m: 0.5}`)
        lines = yaml.safe_dump({key: value}, sort_keys=False, default_flow_style=None if nested else False, width=200).splitlines(keepends=True)
        if c:
            c.lines = lines
            return
        at = next((i for i, x in enumerate(self.chunks) if x.key in ("interior", "openings")), len(self.chunks))
        self.chunks.insert(at, Chunk(key, [], lines))


def flow(v) -> str:
    """A value of an opening's row as `facade_pipeline.stage_apply` writes it (colours quoted, booleans true/false)."""
    if isinstance(v, bool):
        return "true" if v else "false"
    if v is None:
        return "null"
    if isinstance(v, str):
        if v.startswith("#"):
            return f'"{v}"'
        return v if re.fullmatch(r"[A-Za-z_][\w.\-]*", v) else json.dumps(v)
    if isinstance(v, list | tuple):
        return "[" + ", ".join(flow(x) for x in v) + "]"
    return str(v)


def row_text(o: dict) -> str:
    return "  - {" + ", ".join(f"{k}: {flow(v)}" for k, v in o.items()) + "}\n"


class Rows:
    """The openings chunk as rows: each keeps the line it was written as until it is changed."""

    def __init__(self, dt: DetailText) -> None:
        self.dt = dt
        self.items: list[list] = []  # [its original line | None, its data | None (a line that is no row: a comment)]
        chunk = dt.chunk("openings")
        if chunk is None:
            return
        parsed = chunk.value() or []
        rows = [ln for ln in chunk.lines[1:] if _ROW.match(ln)]
        if len(rows) == len(parsed):
            self.items = [[ln, _load(ln.strip()[2:]) if _ROW.match(ln) else None] for ln in chunk.lines[1:]]
        else:  # block style or the like: every row is made again
            self.items = [[None, dict(o)] for o in parsed]

    def data(self) -> list[dict]:
        return [d for _, d in self.items if d is not None]

    def _find(self, oid: str) -> int:
        i = next((k for k, (_, d) in enumerate(self.items) if d is not None and str(d.get("id")) == oid), None)
        if i is None:
            raise BuildingEditError(f"no opening {oid}")
        return i

    def get(self, oid: str) -> dict:
        return self.items[self._find(oid)][1]

    def replace(self, oid: str, row: dict) -> None:
        self.items[self._find(oid)] = [None, row]

    def delete(self, oid: str) -> None:
        at = self._find(oid)
        del self.items[at]
        while at > 0 and self.items[at - 1][1] is None and self.items[at - 1][0].strip().startswith("#"):  # the comment above it was its own
            at -= 1
            del self.items[at]

    def insert_after(self, oid: str | None, row: dict) -> None:
        if oid is None:  # after the last row, before trailing comments
            at = next((k + 1 for k in range(len(self.items) - 1, -1, -1) if self.items[k][1] is not None), 0)
        else:
            at = self._find(oid) + 1
        self.items.insert(at, [None, row])

    def write(self) -> None:
        rows = [ln if ln is not None else row_text(d) for ln, d in self.items]
        lines = ["openings:\n" if self.data() else "openings: []\n", *rows]
        c = self.dt.chunk("openings")
        if c is None:
            self.dt.chunks.append(Chunk("openings", [], lines))
        else:
            c.lines = lines


def _num(v) -> float:
    try:
        return round(float(v), 2)
    except (TypeError, ValueError) as e:
        raise BuildingEditError(f"{v!r} is not a number") from e


# ---- checks ----


def check_detail(text: str | None, bid: str) -> None:
    from drycastle_pipeline.buildings.openings import DetailError, parse_detail

    if text is None:
        return
    try:
        d = parse_detail(_load(text), f"{bid}.yaml")
    except yaml.YAMLError as e:
        raise BuildingEditError(f"{bid}.yaml: not valid YAML: {e}") from e
    except (DetailError, TypeError, ValueError, KeyError, AttributeError) as e:
        raise BuildingEditError(str(e)) from e
    if d.id != bid:
        raise BuildingEditError(f"{bid}.yaml: id {d.id!r} must match the file name")


def check_overrides(text: str | None, path: Path) -> None:
    from drycastle_pipeline.buildings.derive import RulesError, load_overrides, load_traced

    try:
        data = _load(text)
        load_overrides(path, data)
        load_traced(path, data)
    except yaml.YAMLError as e:
        raise BuildingEditError(f"overrides.yaml: not valid YAML: {e}") from e
    except RulesError as e:
        raise BuildingEditError(str(e)) from e


# ---- snapshots ----


def read_snapshot(bid: str, store: Store) -> dict:
    """{"override": the building's block text (comments, entry, fields) or None, "detail": the detail file's text or None}."""
    return {"override": block_text(_read(store.overrides), bid), "detail": _read(store.detail_path(bid))}


def write_snapshot(bid: str, snap: dict, store: Store) -> None:
    """Put both files back as `snap` says; nothing is written when either would not load. The caller holds the lock."""
    if not isinstance(snap, dict) or not {"override", "detail"} <= set(snap):
        raise BuildingEditError("a snapshot has override and detail (each a text or null)")
    for k in ("override", "detail"):
        if snap[k] is not None and not isinstance(snap[k], str):
            raise BuildingEditError(f"snapshot {k} must be a text or null")
    old_o = _read(store.overrides)
    new_o = old_o if block_text(old_o, bid) == snap["override"] else with_block(old_o, bid, snap["override"])
    check_overrides(new_o, store.overrides)
    check_detail(snap["detail"], bid)
    _commit(store, bid, old_o, new_o, _read(store.detail_path(bid)), snap["detail"])


def _commit(store: Store, bid: str, old_o: str | None, new_o: str | None, old_d: str | None, new_d: str | None) -> None:
    if new_o != old_o and new_o is not None:
        atomic_write(store.overrides, new_o)
    if new_d != old_d:
        if new_d is None:
            store.detail_path(bid).unlink(missing_ok=True)
        else:
            atomic_write(store.detail_path(bid), new_d)


# ---- state ----


def ring_of(b: dict) -> list[tuple[float, float]]:
    """The footprint ring as `build_detail` numbers its edges."""
    from shapely.geometry.polygon import orient

    from drycastle_pipeline.buildings.footprint import footprint_polygon

    return [(x, y) for x, y in list(orient(footprint_polygon(b["footprint"]), 1.0).exterior.coords)[:-1]]


def edges_of(b: dict) -> list[dict]:
    ring = ring_of(b)
    out = []
    for i, p in enumerate(ring):
        q = ring[(i + 1) % len(ring)]
        length = math.dist(p, q)
        ux, uy = (q[0] - p[0]) / length, (q[1] - p[1]) / length
        # counter-clockwise ring: the outside is on the right of the edge's direction
        out.append({"i": i, "p": [p[0], p[1]], "q": [q[0], q[1]], "length": round(length, 3), "normal": [round(uy, 4), round(-ux, 4)]})
    return out


def _form(b: dict) -> dict:
    top = round(float(b["height_m"]), 2)
    wall = top if b["roof"] == "flat" else round(float(b["eave_height_m"]), 2)
    return {"roof": b["roof"], "wall_top_m": wall, "top_m": top}


def _opening_state(o: dict) -> dict:
    base = {"kind": "window", "edge": 0, "at_m": 0.0, "width_m": 1.0, "height_m": 1.0, "sill_m": 0.0, "confidence": "medium"}
    return {"id": str(o.get("id")), **base, **{k: v for k, v in o.items() if k != "id"}}


def _detail_state(raw: dict) -> dict:
    from drycastle_pipeline.buildings.openings import DEFAULT_FRAME, DEFAULT_PLINTH

    plinth = raw.get("plinth")
    return {
        "overhang_m": float(raw.get("overhang_m", 0.0)),
        "frame_colour": raw.get("frame_colour", DEFAULT_FRAME),
        "surround_colour": raw.get("surround_colour", DEFAULT_FRAME),
        "trim_colour": raw.get("trim_colour", DEFAULT_FRAME),
        "plinth": None if plinth is None else {"height_m": float(plinth.get("height_m", 0.3)), "colour": plinth.get("colour", DEFAULT_PLINTH)},
        "ridge_caps": bool(raw.get("ridge_caps", False)),
        "roof_tiles": bool(raw.get("roof_tiles", False)),
        "gutter": bool((raw.get("eaves") or {}).get("gutter", False)),
        "corners": raw.get("corners") is not None,
        "bands": bool(raw.get("bands")),
        "interior": bool(raw.get("interior", False)),
        "openings": [_opening_state(o) for o in raw.get("openings") or []],
    }


def palettes() -> dict:
    from drycastle_pipeline.photos import palettes as p

    return {"wall": dict(p.WALL_COLOURS), "roof": dict(p.ROOF_COLOURS), "frame": dict(p.FRAME_COLOURS), "surround": dict(p.SURROUND_COLOURS)}


def state(world, bid: str, store: Store | None = None) -> dict:
    """What the panel shows of one building (see the module docstring)."""
    store = store or Store.of(world)
    pid = world.parcel_for(bid)
    b = world.derived(bid)
    o_text, d_text = _read(store.overrides), _read(store.detail_path(bid))
    has_override = block_text(o_text, bid) is not None
    entry = (_load(o_text).get("buildings") or {}).get(bid) or {} if has_override else {}
    detail, error = None, None
    if d_text is not None:
        try:
            check_detail(d_text, bid)
            detail = _detail_state(_load(d_text))
        except BuildingEditError as e:
            error = str(e)
    return {
        "building": bid,
        "parcel": pid,
        "has_override": has_override,
        "has_detail": d_text is not None,
        "measured": store.measured_path(bid).is_file(),
        "override": dict(entry),
        "form": _form(b),
        "detail": detail,
        "detail_error": error,
        "edges": edges_of(b),
        "base_z": round(float(world.elevation.get(bid, 0.0)), 3),
        "palettes": palettes(),
    }


# ---- edits ----


def form_fields(cur: dict, form: dict) -> dict:
    """The override fields (height_m, roof_height_m, roof) a `form` change comes to, from the current form (roof, wall_top_m, top_m)."""
    roof = form.get("roof", cur["roof"])
    if roof not in ROOFS:
        raise BuildingEditError(f"roof must be one of {ROOFS}, not {roof!r}")
    wall = None if form.get("wall_top_m") is None else _num(form["wall_top_m"])
    top = None if form.get("top_m") is None else _num(form["top_m"])
    if roof == "flat":
        keep = cur["wall_top_m"] if cur["roof"] != "flat" else cur["top_m"]
        height = top if top is not None else (wall if wall is not None else keep)
        if height <= 0:
            raise BuildingEditError("the height must be above 0")
        out: dict = {"height_m": height, "roof_height_m": None}
    else:
        wall = wall if wall is not None else cur["wall_top_m"]
        if top is None:  # flat -> pitched: a 3 m rise unless the change says; pitched: the top stays
            top = round(wall + DEFAULT_RISE_M, 2) if cur["roof"] == "flat" else cur["top_m"]
        rise = round(top - wall, 2)
        if rise < MIN_RISE_M:
            raise BuildingEditError(f"the wall top must be at least {MIN_RISE_M} m under the ridge")
        out = {"height_m": top, "roof_height_m": rise}
    if roof != cur["roof"] or "roof" in form:
        out["roof"] = roof
    return out


_BOOL_KEYS = ("ridge_caps", "roof_tiles", "interior")
_COLOUR_KEYS = ("frame_colour", "surround_colour", "trim_colour")
_DETAIL_KEYS = {"overhang_m", "plinth_m", "gutter", "corners", "bands", *_BOOL_KEYS, *_COLOUR_KEYS}


def edit_detail(dt: DetailText, change: dict, wall_top: float) -> None:
    """The `detail` change on the chunks of the file: overhang_m (0..2), plinth_m (None removes it), ridge_caps, roof_tiles, interior
    (False removes the key), gutter, corners, bands (True makes them from the wall top), frame_colour, surround_colour, trim_colour."""
    from drycastle_pipeline.buildings.openings import DEFAULT_EAVES, DEFAULT_PLINTH

    unknown = set(change) - _DETAIL_KEYS
    if unknown:
        raise BuildingEditError(f"unknown detail key(s) {sorted(unknown)}")
    if "overhang_m" in change:
        dt.set("overhang_m", None if change["overhang_m"] is None else _num(change["overhang_m"]))
    if "plinth_m" in change:
        v = change["plinth_m"]
        old = dt.get("plinth") or {}
        dt.set("plinth", None if v is None else {**old, "height_m": _num(v), "colour": old.get("colour", DEFAULT_PLINTH)})
    for key in _BOOL_KEYS:
        if key in change:
            dt.set(key, True if change[key] else None)
    if "gutter" in change:
        eaves = dt.get("eaves")
        if change["gutter"]:
            dt.set("eaves", {**eaves, "gutter": True} if eaves else {"colour": DEFAULT_EAVES, "fascia_m": 0.18, "gutter": True})
        elif eaves is not None:
            dt.set("eaves", {**eaves, "gutter": False})
    if "corners" in change:
        if not change["corners"]:
            dt.set("corners", None)
        elif dt.get("corners") is None:
            dt.set("corners", {"width_m": 0.3, "top_m": round(wall_top - 0.18, 2)})
    if "bands" in change:
        if not change["bands"]:
            dt.set("bands", None)
        elif not dt.get("bands"):
            dt.set("bands", [{"top_m": round(wall_top, 2), "height_m": 0.18}])
    for key in _COLOUR_KEYS:
        if key in change:
            dt.set(key, change[key])


def _new_id(rows: Rows, kind: str, dt: DetailText) -> str:
    taken = {str(d.get("id")) for d in rows.data()} | {str(d.get("id")) for d in dt.get("dormers") or []}
    prefix = ID_PREFIX.get(kind, "w")
    n = 1
    while f"{prefix}{n}" in taken:
        n += 1
    return f"{prefix}{n}"


def _fit(row: dict, edges: list[dict]) -> dict:
    """The row on its edge: the edge must exist, `at_m` is moved into 0..(edge length - width)."""
    edge = row.get("edge")
    if isinstance(edge, bool) or not isinstance(edge, int) or not 0 <= edge < len(edges):
        raise BuildingEditError(f"edge {edge!r} does not exist (0..{len(edges) - 1})")
    for k in ("at_m", "width_m", "height_m", "sill_m"):
        if k in row:
            row[k] = _num(row[k])
    row["at_m"] = round(min(max(row["at_m"], 0.0), max(edges[edge]["length"] - row["width_m"], 0.0)), 2)
    return row


def _apply_set(row: dict, changes: dict) -> dict:
    old_kind = row.get("kind")
    for k, v in changes.items():
        if k == "id":
            raise BuildingEditError("an opening's id cannot be changed")
        if v is None:
            row.pop(k, None)
        else:
            row[k] = v
    kind = changes.get("kind")
    if kind not in (None, old_kind):  # what the new kind cannot have goes, unless the same change gives it
        drop = [*(_ONLY_WINDOW if kind != "window" else ()), *(_ONLY_DOOR if kind != "door" else ())]
        drop += [*(_ONLY_LEAF if kind not in ("door", "garage_door") else ()), *(_NO_FRAME if kind not in ("window", "door", "garage_door") else ())]
        for k in drop:
            if k not in changes:
                row.pop(k, None)
    return row


def _new_row(rows: Rows, dt: DetailText, edges: list[dict], spec: dict) -> dict:
    kind = spec.get("kind", "window")
    if kind not in OPENING_DEFAULTS:
        raise BuildingEditError(f"kind must be one of {tuple(OPENING_DEFAULTS)}")
    w, h, sill = OPENING_DEFAULTS[kind]
    edge = spec.get("edge", max(edges, key=lambda e: e["length"])["i"])
    width = _num(spec.get("width_m", w))
    length = edges[edge]["length"] if isinstance(edge, int) and not isinstance(edge, bool) and 0 <= edge < len(edges) else 0.0
    row = {
        "id": str(spec.get("id") or _new_id(rows, kind, dt)),
        "kind": kind,
        "edge": edge,
        "at_m": _num(spec.get("at_m", (length - width) / 2)),
        "width_m": width,
        "height_m": _num(spec.get("height_m", h)),
        "sill_m": _num(spec.get("sill_m", sill)),
        "recess_m": 0.12,
        "confidence": "medium",
    }
    if kind in ("window", "door", "garage_door"):
        row["frame_m"] = 0.06
    if kind == "window":
        row["sill"] = True
    return _apply_set(row, {k: v for k, v in spec.items() if k not in ("id", "kind", "edge", "at_m", "width_m", "height_m", "sill_m")})


def edit_opening(dt: DetailText, op: dict, edges: list[dict]) -> str | None:
    """One `opening` change on the openings chunk. Returns the id of the opening it touched (None for a delete)."""
    rows = Rows(dt)
    keys = [k for k in ("set", "add", "delete", "duplicate") if k in op]
    if len(keys) != 1:
        raise BuildingEditError("opening: give one of set, add, delete, duplicate")
    how = keys[0]
    selected: str | None
    if how == "set":
        oid = str(op.get("id"))
        row = _fit(_apply_set(dict(rows.get(oid)), op["set"] or {}), edges)
        rows.replace(oid, row)
        selected = oid
    elif how == "add":
        row = _fit(_new_row(rows, dt, edges, op["add"] or {}), edges)
        if any(str(d.get("id")) == row["id"] for d in rows.data()):
            raise BuildingEditError(f"opening {row['id']} exists")
        rows.insert_after(None, row)
        selected = row["id"]
    elif how == "delete":
        selected = None
        rows.delete(str(op["delete"]))
    else:
        oid = str(op["duplicate"])
        src = rows.get(oid)
        if not isinstance(src.get("edge"), int) or not 0 <= src["edge"] < len(edges):
            raise BuildingEditError(f"edge {src.get('edge')!r} does not exist (0..{len(edges) - 1})")
        step = float(src["width_m"]) + 0.3
        right = float(src["at_m"]) + step
        fits = right + float(src["width_m"]) <= edges[src["edge"]]["length"]
        row = {**src, "id": _new_id(rows, src.get("kind", "window"), dt), "at_m": right if fits else float(src["at_m"]) - step}
        rows.insert_after(oid, _fit(row, edges))
        selected = row["id"]
    rows.write()
    return selected


def edit(world, bid: str, change: dict, store: Store | None = None) -> dict:
    """One change, any subset of `form` ({roof, wall_top_m, top_m}), `override` ({field: value | null}), `detail` (see
    :func:`edit_detail`) and `opening` ({id, set} | {add} | {delete: id} | {duplicate: id}), in this order. Returns
    `{state, before, after, selected}`; an invalid change writes nothing."""
    store = store or Store.of(world)
    if not isinstance(change, dict):
        raise BuildingEditError("change must be an object")
    unknown = set(change) - {"form", "override", "detail", "opening"}
    if unknown:
        raise BuildingEditError(f"unknown change key(s) {sorted(unknown)}")
    pid = world.parcel_for(bid)
    with overrides_lock():
        old_o, old_d = _read(store.overrides), _read(store.detail_path(bid))
        before = {"override": block_text(old_o, bid), "detail": old_d}
        b = world.derived(bid)
        cur = _form(b)
        fields: dict = {}
        if change.get("form"):
            fields.update(form_fields(cur, change["form"]))
        fields.update(change.get("override") or {})
        wall_top = cur["wall_top_m"]
        if change.get("form"):
            wall_top = round(fields["height_m"] - (fields["roof_height_m"] or 0.0), 2)
        new_o = edit_fields(old_o, bid, fields, pid) if fields else old_o
        new_d, selected = old_d, None
        if change.get("detail") or change.get("opening"):
            opening = change.get("opening")
            if old_d is None and opening and "add" not in opening:
                raise BuildingEditError(f"{bid} has no detail file: add an opening first")
            dt = DetailText(old_d if old_d is not None else NEW_DETAIL.format(bid=bid))
            if change.get("detail"):
                edit_detail(dt, change["detail"], wall_top)
            if opening:
                selected = edit_opening(dt, opening, edges_of(b))
            new_d = dt.text()
        check_overrides(new_o, store.overrides)
        check_detail(new_d, bid)
        _commit(store, bid, old_o, new_o, old_d, new_d)
        after = read_snapshot(bid, store)
    return {"state": state(world, bid, store), "before": before, "after": after, "selected": selected}


def restore(world, bid: str, snapshot: dict, store: Store | None = None) -> dict:
    """Write a snapshot back exactly (a null override removes the building's block with its comments, a null detail the file)."""
    store = store or Store.of(world)
    world.parcel_for(bid)
    with overrides_lock():
        before = read_snapshot(bid, store)
        write_snapshot(bid, snapshot, store)
        after = read_snapshot(bid, store)
    return {"state": state(world, bid, store), "before": before, "after": after}


def reset(world, bid: str, store: Store | None = None) -> dict:
    """Go back to what the automatic build measured (`<photos>/<bid>/measured.json`)."""
    store = store or Store.of(world)
    path = store.measured_path(bid)
    if not path.is_file():
        raise BuildingEditError("nothing measured to go back to")
    try:
        snap = json.loads(path.read_text())
    except ValueError as e:
        raise BuildingEditError(f"measured.json is unreadable: {e}") from e
    return restore(world, bid, snap, store)


def handle_op(world, req: dict) -> dict:
    """One op request of the worker (`state`, `edit`, `restore`, `snapshot`, `reset`); returns its result."""
    op = req.get("op")
    bid = req.get("building")
    if not isinstance(bid, str) or not bid:
        raise BuildingEditError(f"{op}: needs a building")
    store = Store.of(world)
    if op == "state":
        return state(world, bid, store)
    if op == "edit":
        return edit(world, bid, req.get("change"), store)
    if op == "restore":
        return restore(world, bid, req.get("snapshot"), store)
    if op == "snapshot":
        world.parcel_for(bid)
        return {"snapshot": read_snapshot(bid, store)}
    if op == "reset":
        return reset(world, bid, store)
    raise BuildingEditError(f"unknown op {op!r}")
