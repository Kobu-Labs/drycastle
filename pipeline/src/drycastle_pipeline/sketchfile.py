"""The sketch file (``out/sketch.json``): temporary named marks on the ground, shared by the viewer and the MCP server.

A *sketch* is a named group of marks (``ROAD``, ``FENCE_PLAN``); a mark is a point (``A``, ``B``, ...) or a line
(``L1``, ``L2``, ...; closed means the ground inside). Mark ids are unique across all sketches, so a prompt can say
"at A" or "along L2" without naming the sketch. Coordinates are absolute S-JTSK metres.

Both programs read and write this one file, atomically (temp file + rename). ``version`` goes up on every write; the
viewer notices a version it did not write and takes the file's marks as one undo step. The MCP server holds a ``flock`` on
``<file>.lock`` while it changes the file; the viewer does not lock, so a viewer edit and an MCP edit within the same
frame can overwrite each other (last writer wins; the viewer's undo brings the lost one back).
"""

from __future__ import annotations

import fcntl
import json
import os
import re
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path
from typing import Any

SKETCH_FORMAT_VERSION = 1
DEFAULT_SKETCH = "DEFAULT"
SKETCH_NAME = re.compile(r"[A-Za-z0-9_-]{1,24}")


class SketchFileError(ValueError):
    """The file exists but is not a sketch file (the message says why)."""


def point_label(index: int) -> str:
    """Point names: A to Z, then AA, AB, ... (the viewer's ``SketchPointLabel``)."""
    s = ""
    n = index + 1
    while n > 0:
        s = chr(ord("A") + (n - 1) % 26) + s
        n = (n - 1) // 26
    return s


def empty_doc() -> dict:
    return {
        "format_version": SKETCH_FORMAT_VERSION,
        "version": 0,
        "sketches": [],
        "next_point": 0,
        "next_line": 0,
        "points": [],
        "lines": [],
    }


def normalise(raw: Any) -> dict:
    """A sketch document from the file's JSON (or from the legacy ``sketch`` value of editor-state.json).

    Marks without a ``sketch`` belong to ``DEFAULT``; every sketch a mark names is listed in ``sketches``.
    """
    if not isinstance(raw, dict):
        raise SketchFileError("not a JSON object")
    try:
        points = [
            {"id": str(p["id"]), "x": float(p["x"]), "y": float(p["y"]), "sketch": str(p.get("sketch") or DEFAULT_SKETCH)}
            for p in raw.get("points", [])
        ]
        lines = [
            {
                "id": str(ln["id"]),
                "closed": bool(ln["closed"]),
                "points": [[float(x), float(y)] for x, y in ln["points"]],
                "sketch": str(ln.get("sketch") or DEFAULT_SKETCH),
            }
            for ln in raw.get("lines", [])
        ]
        names = [str(n) for n in raw.get("sketches", [])]
        version = int(raw.get("version", 0))
        next_point = int(raw.get("next_point", 0))
        next_line = int(raw.get("next_line", 0))
    except (KeyError, TypeError, ValueError) as exc:
        raise SketchFileError(f"bad mark ({exc!r})") from exc
    for name in [m["sketch"] for m in points + lines]:
        if name not in names:
            names.append(name)
    return {
        "format_version": SKETCH_FORMAT_VERSION,
        "version": version,
        "sketches": names,
        "next_point": max(next_point, len(points)),
        "next_line": max(next_line, len(lines)),
        "points": points,
        "lines": lines,
    }


def read(path: Path) -> dict | None:
    """The sketch file as a normalised document; None when there is no file."""
    if not path.is_file():
        return None
    try:
        raw = json.loads(path.read_text())
        if isinstance(raw, dict) and raw.get("format_version") != SKETCH_FORMAT_VERSION:
            raise SketchFileError(f"format_version {raw.get('format_version')!r}")
        return normalise(raw)
    except (OSError, ValueError) as exc:
        raise SketchFileError(f"{path} is unreadable ({exc})") from exc


def write(path: Path, doc: dict) -> int:
    """Write ``doc`` with the next version number (atomic) and return that version."""
    on_disk = 0
    try:
        existing = read(path)
        on_disk = existing["version"] if existing else 0
    except SketchFileError:
        pass
    doc["version"] = max(doc["version"], on_disk) + 1
    text = json.dumps(doc, sort_keys=True, separators=(",", ":")) + "\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text)
    os.replace(tmp, path)
    return doc["version"]


@contextmanager
def lock(path: Path) -> Iterator[None]:
    """Exclusive advisory lock on ``<file>.lock`` while changing the sketch file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path.with_name(path.name + ".lock"), "a+") as fh:
        fcntl.flock(fh, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(fh, fcntl.LOCK_UN)


def sketch_of(doc: dict, name: str) -> str | None:
    """The stored spelling of a sketch name (names match ignoring case), or None."""
    for n in doc["sketches"]:
        if n.lower() == name.lower():
            return n
    return None


def new_point_id(doc: dict) -> str:
    doc["next_point"] += 1
    return point_label(doc["next_point"] - 1)


def new_line_id(doc: dict) -> str:
    doc["next_line"] += 1
    return f"L{doc['next_line']}"


def tidy(doc: dict) -> None:
    """With no marks left, the ids start again at A and L1 (as in the viewer)."""
    if not doc["points"] and not doc["lines"]:
        doc["next_point"] = doc["next_line"] = 0
