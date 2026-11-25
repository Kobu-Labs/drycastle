"""Where embeddings live: one folder per (model, recipe), a vector cache plus one index per describer.

The cache maps a content key (hash of the exact text, or of the picture file) to its vector, so
nothing unchanged is ever embedded twice. An index lists, per asset, the vectors that stand for
it (kind, label for display, cache key). Retrievers load an index and score its rows.
"""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

import numpy as np
from pydantic import BaseModel

from .. import config

TEXT = "text"
IMAGE = "image"


class Row(BaseModel):
    id: str  # asset id
    kind: str  # what_it_is | what_it_is_for | likely_query | view
    label: str  # the likely query itself, or the view name
    key: str  # cache key of its vector


class Index(BaseModel):
    describer: str  # the model that wrote the records these rows were made from
    rows: list[Row]


def content_key(*parts: str) -> str:
    h = hashlib.sha256()
    for p in parts:
        h.update(p.encode())
        h.update(b"\0")
    return h.hexdigest()[:24]


def file_key(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:24]


def slug(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", text).strip("_")


class Space:
    """The vectors of one embedding model under one recipe."""

    def __init__(self, modality: str, model: str, recipe: str, data_dir: Path | None = None):
        root = data_dir or config.load().data_dir
        self.dir = root / "embed" / f"{modality}--{slug(model)}--{recipe}"
        self.keys: dict[str, int] = {}
        self.vectors = np.zeros((0, 0), np.float32)
        self._new: dict[str, np.ndarray] = {}
        if (self.dir / "keys.json").exists():
            self.keys = {
                k: i for i, k in enumerate(json.loads((self.dir / "keys.json").read_text()))
            }
            self.vectors = np.load(self.dir / "vectors.npy")

    def has(self, key: str) -> bool:
        return key in self.keys or key in self._new

    def add(self, keys: list[str], vectors: np.ndarray) -> None:
        for k, v in zip(keys, vectors, strict=True):
            self._new[k] = v.astype(np.float32)

    def matrix(self, keys: list[str]) -> np.ndarray:
        """Vectors for keys, in order. Fresh additions count even before save()."""
        dim = self.vectors.shape[1] if self.vectors.size else len(next(iter(self._new.values())))
        out = np.zeros((len(keys), dim), np.float32)
        for i, k in enumerate(keys):
            out[i] = self._new[k] if k in self._new else self.vectors[self.keys[k]]
        return out

    def index_path(self, describer: str) -> Path:
        return self.dir / f"index--{slug(describer)}.json"

    def read_index(self, describer: str) -> Index | None:
        p = self.index_path(describer)
        return Index.model_validate_json(p.read_text()) if p.exists() else None

    def save(self, index: Index) -> None:
        """Write the index, then the cache holding exactly the vectors some index still uses."""
        self.dir.mkdir(parents=True, exist_ok=True)
        _atomic(self.index_path(index.describer), index.model_dump_json())
        used = {r.key for p in self.dir.glob("index--*.json") for r in _rows(p)}
        self._write([k for k in {**self.keys, **dict.fromkeys(self._new)} if k in used])

    def checkpoint(self) -> None:
        """Persist everything computed so far, so a crash in a long run loses little."""
        self.dir.mkdir(parents=True, exist_ok=True)
        self._write(list({**self.keys, **dict.fromkeys(self._new)}))

    def _write(self, keep: list[str]) -> None:
        mat = self.matrix(keep) if keep else np.zeros((0, 0), np.float32)
        tmp = self.dir / "vectors.tmp.npy"
        np.save(tmp, mat)
        tmp.replace(self.dir / "vectors.npy")
        _atomic(self.dir / "keys.json", json.dumps(keep))
        self.keys, self.vectors, self._new = {k: i for i, k in enumerate(keep)}, mat, {}


def _rows(path: Path) -> list[Row]:
    return Index.model_validate_json(path.read_text()).rows


def _atomic(path: Path, text: str) -> None:
    tmp = path.with_suffix(".tmp")
    tmp.write_text(text)
    tmp.replace(path)
