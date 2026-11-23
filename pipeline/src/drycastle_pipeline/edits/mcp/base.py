"""The plumbing every tool group shares: the map, the asset manifest, the locked write of edits.json."""

from __future__ import annotations

import math
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.core.assets import load_manifest, validate_asset_ref
from drycastle_pipeline.edits.mcp.mapdata import MapData
from drycastle_pipeline.edits.mcp.settings import Settings
from drycastle_pipeline.edits.schema import Edits, EditsError, dump_edits, load_edits, validate
from drycastle_pipeline.edits.store import edits_doc, edits_lock


def bullets(errors: list[str]) -> str:
    return "\n".join(f"  - {e}" for e in errors)


def read_edits(path: Path) -> Edits:
    try:
        return load_edits(path)
    except EditsError as exc:
        raise ToolError(
            f"{path.name} is currently invalid, so nothing was changed. Fix it first:\n"
            + bullets(exc.errors)
        ) from exc


def empty_parcel_cleanup(edits: Edits) -> None:
    for pid in [p for p, e in edits.parcels.items() if not e]:
        del edits.parcels[pid]


def all_objects(edits: Edits) -> list[dict]:
    """Every placed object (they belong to no parcel; see edits/schema.py)."""
    return edits.objects


def check_num(name: str, v: object, *, positive: bool = False, minimum: float | None = None) -> float:
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
        raise ToolError(f"{name} must be a number, got {v!r}")
    if positive and v <= 0:
        raise ToolError(f"{name} must be > 0, got {v}")
    if minimum is not None and v < minimum:
        raise ToolError(f"{name} must be >= {minimum}, got {v}")
    return float(v)


@dataclass
class ToolBase:
    """The state every tool group works on: the settings, the map and the asset manifest (reloaded when their files change)."""

    settings: Settings
    _map: MapData | None = field(default=None, init=False, repr=False)
    _manifest: dict | None = field(default=None, init=False, repr=False)
    _manifest_stamp: int | None = field(default=None, init=False, repr=False)

    @property
    def map(self) -> MapData:
        if self._map is None:
            self._map = MapData(self.settings.work)
        return self._map.ensure()

    @property
    def manifest(self) -> dict:
        path = self.settings.assets_path
        try:
            stamp = path.stat().st_mtime_ns
        except FileNotFoundError as exc:
            raise ToolError(f"asset manifest not found: {path} (see --assets)") from exc
        if self._manifest is None or stamp != self._manifest_stamp:
            self._manifest = load_manifest(path)
            self._manifest_stamp = stamp
        return self._manifest

    def _asset(self, ref: object) -> dict:
        problem = validate_asset_ref(ref, self.manifest)  # type: ignore[arg-type]
        if problem:
            raise ToolError(f"{problem}. Call list_assets to see the valid ids.")
        return next(a for a in self.manifest["assets"] if a["id"] == ref)

    def _write(self, change: Callable[[Edits], Any]) -> Any:
        """Reload edits.json under the lock, apply ``change``, validate everything, write."""
        path = self.settings.edits_path
        with edits_lock(path):
            edits = read_edits(path)
            result = change(edits)
            empty_parcel_cleanup(edits)
            errors = validate(edits_doc(edits))
            if errors:
                raise ToolError(
                    "refused: the change would make edits.json invalid, nothing was written:\n"
                    + bullets(errors)
                )
            dump_edits(edits, path)
        return result

    def _group_of(self, edits: Edits, pid: str) -> str | None:
        for gid, g in edits.groups.items():
            if pid in g.get("parcels", []):
                return gid
        return None
