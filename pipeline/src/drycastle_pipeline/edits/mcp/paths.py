"""Tools on worn footpaths."""

from __future__ import annotations

from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.edits.mcp.base import read_edits
from drycastle_pipeline.edits.mcp.ids import next_id
from drycastle_pipeline.edits.schema import Edits


class PathTools:
    """create_path, list_paths, delete_path."""

    def create_path(self, points: list | None = None, sketch_line: str | None = None, width_m: float | None = None,
                    closed: bool | None = None) -> dict:
        """Create a worn footpath (see the registered tool description)."""
        if (points is None) == (sketch_line is None):
            raise ToolError("give exactly one of points ([[x, y], ...], absolute S-JTSK metres) or sketch_line (a line id of get_sketch)")
        if sketch_line is not None:
            pts, line_closed = self._sketch_line(sketch_line)
            closed = line_closed if closed is None else closed
        else:
            if not isinstance(points, list):
                raise ToolError("points must be a list of [x, y] points")
            pts = [self._xy(f"points[{i}]", p) for i, p in enumerate(points)]
        entry: dict = {"points": pts}
        if width_m is not None:
            entry["width_m"] = width_m
        if closed:
            entry["closed"] = True

        def change(edits: Edits) -> str:
            pid = next_id("PA", list(edits.paths))
            edits.paths[pid] = entry
            return pid

        pid = self._write(change)
        return {"summary": f"created {pid} ({len(pts)} points); the map shows it after the next make map-update", "id": pid, **entry, "warnings": []}

    def list_paths(self) -> dict:
        """Every worn footpath of the edits."""
        paths = read_edits(self.settings.edits_path).paths
        return {"count": len(paths), "paths": [{"id": pid, **e} for pid, e in sorted(paths.items())]}

    def delete_path(self, id: str) -> dict:
        """Delete a worn footpath."""
        def change(edits: Edits) -> None:
            if id not in edits.paths:
                raise ToolError(f"no path {id!r}; see list_paths")
            del edits.paths[id]

        self._write(change)
        return {"summary": f"deleted {id}; the map shows it after the next make map-update", "id": id, "warnings": []}


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Create a worn footpath (id PA-NNNN): a line people walk until the grass is gone. Give `points` = [[x, y], ...] in "
        "absolute S-JTSK metres, or `sketch_line` = the id of a line the user drew (get_sketch; a closed line makes a loop). "
        "`width_m` 0.3 to 4 (default 0.8) is the average width of the trodden ground; its outline swells, wanders and thins out "
        "at the ends of an open path. It is drawn as bare earth over whatever ground is there (also inside a parcel) and changes "
        "no parcel, ground area or kind. Needs the next make map-update to show on the map."))
    def create_path(points: list[list[float]] | None = None, sketch_line: str | None = None, width_m: float | None = None,
                    closed: bool | None = None) -> dict:
        return tools.create_path(points, sketch_line, width_m, closed)

    @server.tool(description="List the worn footpaths (create_path): id, points, width_m, closed.")
    def list_paths() -> dict:
        return tools.list_paths()

    @server.tool(description="Delete a worn footpath by its id (PA-NNNN). Needs the next make map-update to show on the map.")
    def delete_path(id: str) -> dict:
        return tools.delete_path(id)
