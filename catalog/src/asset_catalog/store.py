"""Per-stage cache. One JSON file per (stage, asset); fresh while its key matches.

A stage's key is a hash of everything its output depends on: the asset's content hash, the
stage's VERSION, the keys of the stages it reads, and any parameters. Change one and only
that stage and the ones after it rerun.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import TypeVar

from pydantic import BaseModel

from . import config

T = TypeVar("T", bound=BaseModel)


def make_key(*parts: object) -> str:
    h = hashlib.sha256()
    for p in parts:
        h.update(json.dumps(p, sort_keys=True, default=str).encode())
        h.update(b"\0")
    return h.hexdigest()[:20]


class Store:
    def __init__(self, data_dir: Path | None = None):
        self.root = data_dir or config.load().data_dir

    def dir(self, stage: str, asset_id: str) -> Path:
        """Folder for a stage's files for one asset (JSON record, images, ...)."""
        return self.root / stage / asset_id

    def _file(self, stage: str, asset_id: str) -> Path:
        return self.root / stage / f"{asset_id}.json"

    def key(self, stage: str, asset_id: str) -> str | None:
        f = self._file(stage, asset_id)
        if not f.exists():
            return None
        return json.loads(f.read_text()).get("key")

    def get(self, stage: str, asset_id: str, model: type[T], key: str | None = None) -> T | None:
        """The stored output; None when missing, or when `key` is given and differs."""
        f = self._file(stage, asset_id)
        if not f.exists():
            return None
        raw = json.loads(f.read_text())
        if key is not None and raw.get("key") != key:
            return None
        return model.model_validate(raw["data"])

    def put(self, stage: str, asset_id: str, key: str, data: BaseModel) -> None:
        f = self._file(stage, asset_id)
        f.parent.mkdir(parents=True, exist_ok=True)
        tmp = f.with_suffix(".tmp")
        tmp.write_text(json.dumps({"key": key, "data": data.model_dump(mode="json")}, indent=1))
        tmp.replace(f)
