"""The MCP server: its settings from the command line, and the tool groups registered on it."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Any

from drycastle_pipeline.core.assets import DEFAULT_MANIFEST
from drycastle_pipeline.edits.mcp import (
    assets,
    cables,
    fences,
    ground_patches,
    ground_types,
    groups,
    objects,
    parcels,
    paths,
    sketches,
    zones,
)
from drycastle_pipeline.edits.mcp.settings import DEFAULT_STATE, Settings
from drycastle_pipeline.edits.mcp.tools import ParcelEditTools
from drycastle_pipeline.edits.schema import DEFAULT_EDITS

TOOL_GROUPS = (assets, parcels, sketches, fences, objects, cables, groups, ground_patches, zones, paths, ground_types)


def build_server(settings: Settings) -> Any:
    """An MCP server (stdio or in-memory) exposing the tools over ``settings``."""
    from mcp.server.mcpserver import MCPServer

    tools = ParcelEditTools(settings)
    server = MCPServer(
        "drycastle-parcel-edits",
        instructions=(
            "Edit parcels of the Drycastle map: fences, placed objects (trees are placed as objects), property "
            "groups, ground patches (hand-drawn ground of one ground type) and zones (what an area means in play), all stored in edits.json (a viewer in --editor --watch "
            "mode shows changes within about a second). Coordinates are absolute S-JTSK metres (x east, y north). "
            "Typical flow: find_parcels (selected=true for what the user selected) -> get_parcel "
            "-> set_fence / place_object; create_ground_patch for ground, create_zone for what an area means in play. When the "
            "user points at something they drew ('this line', 'this area', 'here', 'at A', "
            "'inside L1', 'sketch ROAD'), call get_sketch for its coordinates. To show the user something on the map "
            "(a road, a spot, an outline), draw it with sketch_add_road / sketch_add_point / sketch_add_line under a named sketch."
        ),
    )
    for group in TOOL_GROUPS:
        group.register(server, tools)
    return server


def parse_args(argv: list[str] | None = None) -> Settings:
    ap = argparse.ArgumentParser(
        prog="python -m drycastle_pipeline.edits.mcp",
        description="MCP server (stdio) that edits pipeline/config/edits.json",
    )
    ap.add_argument("--edits", type=Path, default=DEFAULT_EDITS, help="edits file to read and write")
    ap.add_argument("--state", type=Path, default=DEFAULT_STATE, help="editor-state.json of the viewer")
    ap.add_argument("--sketch", type=Path, default=None, help="sketch.json shared with the viewer (default: ~/.local/state/drycastle/sketch.json, or $DRYCASTLE_SKETCH)")
    ap.add_argument("--assets", type=Path, default=DEFAULT_MANIFEST, help="asset manifest")
    ap.add_argument("--work", type=Path, default=None, help="work dir (default: data/village/work)")
    a = ap.parse_args(argv)
    return Settings(
        work_dir=a.work.resolve() if a.work else None,
        edits_path=a.edits.resolve(),
        state_path=a.state.resolve(),
        sketch_path=a.sketch.resolve() if a.sketch else None,
        assets_path=a.assets.resolve(),
    )


def main(argv: list[str] | None = None) -> int:
    settings = parse_args(argv)
    print(
        f"drycastle MCP server: edits={settings.edits_path} "
        f"state={settings.state_path}",
        file=sys.stderr,
    )
    build_server(settings).run("stdio")
    return 0


if __name__ == "__main__":
    sys.exit(main())
