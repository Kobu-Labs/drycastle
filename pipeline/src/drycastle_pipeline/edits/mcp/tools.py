"""``ParcelEditTools``: every tool implementation of the server, one mixin per tool group."""

from drycastle_pipeline.edits.mcp.assets import AssetTools
from drycastle_pipeline.edits.mcp.base import ToolBase
from drycastle_pipeline.edits.mcp.cables import CableTools
from drycastle_pipeline.edits.mcp.fences import FenceTools
from drycastle_pipeline.edits.mcp.ground_patches import PatchTools
from drycastle_pipeline.edits.mcp.ground_types import GroundTypeTools
from drycastle_pipeline.edits.mcp.groups import GroupTools
from drycastle_pipeline.edits.mcp.objects import ObjectTools
from drycastle_pipeline.edits.mcp.parcels import ParcelTools
from drycastle_pipeline.edits.mcp.paths import PathTools
from drycastle_pipeline.edits.mcp.shapes import ShapeTools
from drycastle_pipeline.edits.mcp.sketches import SketchTools
from drycastle_pipeline.edits.mcp.zones import ZoneTools


class ParcelEditTools(
    AssetTools, ParcelTools, SketchTools, FenceTools, ObjectTools, CableTools, GroupTools,
    PatchTools, GroundTypeTools, ZoneTools, PathTools, ShapeTools, ToolBase,
):
    """The tool implementations; each public method is one MCP tool (JSON in, JSON out)."""
