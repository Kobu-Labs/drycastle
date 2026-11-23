"""Settings of the MCP server (file locations) and the viewer's editor-state file format."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from drycastle_pipeline.core.assets import DEFAULT_MANIFEST
from drycastle_pipeline.core.config import REPO_ROOT, map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES
from drycastle_pipeline.edits import sketchfile
from drycastle_pipeline.edits.schema import DEFAULT_EDITS

DEFAULT_STATE = REPO_ROOT / "out" / "editor-state.json"
STATE_FORMAT_VERSION = 1
STATE_STALE_S = 600.0  # a selection older than this probably comes from a viewer that has exited


@dataclass(frozen=True)
class Settings:
    work_dir: Path | None = None
    edits_path: Path = DEFAULT_EDITS
    state_path: Path = DEFAULT_STATE
    assets_path: Path = DEFAULT_MANIFEST
    rules_path: Path = DEFAULT_RULES
    sketch_path: Path | None = None  # default: the shared per-user file (sketchfile.default_path)

    @property
    def work(self) -> Path:
        return self.work_dir or map_paths().work

    @property
    def sketch_file(self) -> Path:
        return self.sketch_path or sketchfile.default_path()
