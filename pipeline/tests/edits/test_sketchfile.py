"""The sketch file writer: unique temp names, atomic replace, versions."""

import os
from pathlib import Path

from drycastle_pipeline.edits import sketchfile


def test_write_uses_unique_temp_and_leaves_none(tmp_path, monkeypatch):
    path = tmp_path / "sketch.json"
    seen = []
    real_replace = sketchfile.os.replace

    def spy(src, dst):
        seen.append(Path(src).name)
        real_replace(src, dst)

    monkeypatch.setattr(sketchfile.os, "replace", spy)
    v1 = sketchfile.write(path, sketchfile.empty_doc())
    v2 = sketchfile.write(path, sketchfile.empty_doc())
    assert (v1, v2) == (1, 2)
    # per-process name: never a fixed one shared with the viewer (which writes sketch.json.<pid>.tmp from its own pid)
    assert seen == [f"sketch.json.{os.getpid()}.tmp"] * 2
    assert not list(tmp_path.glob("*.tmp"))


def test_default_path_is_shared_per_user(monkeypatch, tmp_path):
    monkeypatch.delenv("DRYCASTLE_SKETCH", raising=False)
    monkeypatch.setenv("XDG_STATE_HOME", str(tmp_path / "state"))
    assert sketchfile.default_path() == tmp_path / "state" / "drycastle" / "sketch.json"
    monkeypatch.delenv("XDG_STATE_HOME")
    assert sketchfile.default_path() == Path.home() / ".local" / "state" / "drycastle" / "sketch.json"
    monkeypatch.setenv("DRYCASTLE_SKETCH", str(tmp_path / "mine.json"))
    assert sketchfile.default_path() == tmp_path / "mine.json"
