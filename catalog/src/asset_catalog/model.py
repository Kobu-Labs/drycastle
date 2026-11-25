"""The asset identity every stage works from. Stage outputs live next to their stage."""

from __future__ import annotations

from pathlib import Path
from typing import Literal

from pydantic import BaseModel

from .config import REPO_ROOT

Media = Literal["model", "audio"]
# A lod or collision file is a helper for another asset: kept in the index so nothing is
# silently lost, but never described or returned by search on its own.
Role = Literal["primary", "lod", "collision"]


class Asset(BaseModel):
    """One file that is one asset. Written by ingest; read-only for every later stage."""

    id: str  # "<source>/<pack>/<stem>", stable across runs
    source: str
    pack: str
    name: str  # file stem, as the pack shipped it
    path: str  # relative to the repo root
    media: Media
    content_hash: str  # sha256 of the file
    bytes: int
    kind_hint: str | None = None  # from the source config, e.g. "animation clip"
    variant_group: str | None = None  # id prefix shared by _01/_02 siblings; None when alone
    role: Role = "primary"
    derived_from: str | None = None  # for a lod or collision mesh: the asset it belongs to

    @property
    def abs_path(self) -> Path:
        return REPO_ROOT / self.path
