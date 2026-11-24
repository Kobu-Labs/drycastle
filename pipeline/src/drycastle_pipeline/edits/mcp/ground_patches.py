"""Tools on ground patches (hand-drawn ground of one ground type)."""

from __future__ import annotations

from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.edits.mcp.base import check_num, read_edits
from drycastle_pipeline.edits.mcp.ids import next_id
from drycastle_pipeline.edits.schema import MAX_DEPTH_LIMIT_M, Edits


class PatchTools:
    """The tools on ground patches."""

    def create_ground_patch(
        self,
        type: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list | None = None,
        water_y: float | None = None,
        max_depth: float | None = None,
    ) -> dict:
        """Create a ground patch (see the registered tool description)."""
        name = self._ground_type(type)
        water = self._is_water_type(name)
        level = None if water_y is None else check_num("water_y", water_y)
        depth = self._depth(max_depth)
        if depth is not None and not water:
            raise ToolError(f"max_depth only goes with a water type; {name} does not carry the water attribute")
        if water and level is None:
            raise ToolError(f"{name} is a water type: give water_y (metres, terrain datum)")
        if not water and level is not None:
            raise ToolError(f"water_y only goes with a water type; {name} does not carry the water attribute")
        pts = self._points_of(polygon, parcel, points, "ground patch")
        extra = {} if level is None else {"water_y": level}
        if depth is not None:
            extra["max_depth"] = depth

        def change(edits: Edits) -> dict:
            pid = next_id("GP", list(edits.ground_patches))
            edits.ground_patches[pid] = {"type": name, "polygon": pts, **extra}
            return {"id": pid, "warnings": self._overlap_warnings(edits, pid)}

        out = self._write(change)
        row = self._shape_row(out["id"], {"type": name, "polygon": pts, **extra})
        return {
            "summary": f"created {out['id']} ({name}, {row['points']} points, {row['area_m2']} m2)"
            + (f", {len(out['warnings'])} warning(s)" if out["warnings"] else ""),
            **row,
            "polygon": pts,
            "warnings": out["warnings"],
        }

    def set_patch_type(self, id: str, type: str, water_y: float | None = None, max_depth: float | None = None) -> dict:
        """Give a ground patch another ground type (and a water level when the type is water)."""
        pid = self._patch_id(id)
        name = self._ground_type(type)
        water = self._is_water_type(name)
        level = None if water_y is None else check_num("water_y", water_y)
        depth = self._depth(max_depth)
        if not water and level is not None:
            raise ToolError(f"water_y only goes with a water type; {name} does not carry the water attribute")
        if not water and depth is not None:
            raise ToolError(f"max_depth only goes with a water type; {name} does not carry the water attribute")

        def change(edits: Edits) -> object:
            entry = self._patch_entry(edits, pid)
            old = entry["type"]
            if water and level is None and "water_y" not in entry:
                raise ToolError(f"{name} is a water type: give water_y (metres, terrain datum). Nothing was changed.")
            entry["type"] = name
            if not water:
                entry.pop("water_y", None)
                entry.pop("max_depth", None)
            else:
                if level is not None:
                    entry["water_y"] = level
                if depth is not None:
                    entry["max_depth"] = depth
            return old

        old = self._write(change)
        return {
            "summary": f"{pid} type {old} -> {name}; the map shows it after the next full run (make map)",
            "id": pid,
            "type": name,
            "warnings": [],
        }

    @staticmethod
    def _depth(max_depth: float | None) -> float | None:
        """A body's ``max_depth`` (metres, 0 to ``MAX_DEPTH_LIMIT_M``), or None when not given."""
        if max_depth is None:
            return None
        depth = check_num("max_depth", max_depth)
        if not (0 < depth <= MAX_DEPTH_LIMIT_M):
            raise ToolError(f"max_depth must be in metres, between 0 and {MAX_DEPTH_LIMIT_M:g}, got {max_depth!r}")
        return depth

    def set_patch_water(self, id: str, water_y: float, max_depth: float | None = None) -> dict:
        """Change the flat water surface height of a ground patch of a water type, and optionally its deepest point.

        ``max_depth`` (metres, optional) is the body's deepest point; left out it keeps the current one (the class
        default when there is none).
        """
        pid = self._patch_id(id)
        level = check_num("water_y", water_y)
        depth = self._depth(max_depth)

        def change(edits: Edits) -> object:
            entry = self._patch_entry(edits, pid)
            if "water_y" not in entry:
                raise ToolError(f"{pid} is of type {entry['type']}, not a water type; set_patch_type it to a water type first. Nothing was changed.")
            old = entry["water_y"]
            entry["water_y"] = level
            if depth is not None:
                entry["max_depth"] = depth
            return old

        old = self._write(change)
        out = {"summary": f"{pid} water_y {old} -> {level}", "id": pid, "water_y": level, "warnings": []}
        if depth is not None:
            out["max_depth"] = depth
            out["summary"] += f", max_depth {depth}"
        return out

    def move_patch_corner(
        self, id: str, index: int, position: list[float] | None = None, offset: dict | None = None
    ) -> dict:
        """Move one corner of a ground patch to a position or by an offset."""
        return self._move_corner("ground_patches", self._patch_id(id), index, position, offset)

    def delete_ground_patch(self, id: str) -> dict:
        """Delete a ground patch."""
        pid = self._patch_id(id)

        def change(edits: Edits) -> dict:
            self._patch_entry(edits, pid)
            return edits.ground_patches.pop(pid)

        entry = self._write(change)
        return {"summary": f"deleted {pid} ({entry['type']})", "id": pid, "entry": entry, "warnings": []}

    def list_ground_patches(self, near: dict | None = None, limit: int = 50) -> dict:
        """List ground patches, optionally those near a point or parcel."""
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
            raise ToolError("limit must be an integer >= 1")
        edits = read_edits(self.settings.edits_path)
        rows = [self._shape_row(pid, e) for pid, e in sorted(edits.ground_patches.items())]
        rows = self._near_rows(rows, edits.ground_patches, near)
        return {"count": len(rows[:limit]), "total_matching": len(rows), "ground_patches": rows[:limit]}


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Create a ground patch (id GP-NNNN): a hand-drawn area of exactly one ground type, independent of parcel "
        "boundaries (a meadow, a gravel yard, a path). `type` is a ground type of list_ground_types; a colour that no "
        "type has is a ground type of your own (set_ground_type). Outside the parcels the patch is ground of that type "
        "(terrain colour, detail, trees), overriding what the rules put there, after the next full run (make map); "
        "the viewer shows it at once as a preview in the type's first colour. Give "
        "exactly one shape form: `polygon` = [[x, y], ...] in absolute S-JTSK metres, or `parcel` "
        "+ `points` = [[along, inward], ...] metres in that parcel's front-edge frame (same axes "
        "as place_object: along grows right, inward grows away from the road; see get_parcel). "
        "Needs 3+ points; edges may not cross or touch each other. Invalid shapes are refused "
        "and nothing is written. Where patches overlap the higher ID wins (reported in "
        "`warnings`). A water type (carries the water attribute) needs `water_y`, the flat surface of the body of "
        "water in metres on the terrain datum (see set_patch_water), and may give `max_depth` (metres, 0 to 20), the "
        "body's deepest point (its natural bed is scaled to it); other types refuse both. "
        "Returns the id, area, bbox and the final absolute polygon."))
    def create_ground_patch(
        type: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list[list[float]] | None = None,
        water_y: float | None = None,
        max_depth: float | None = None,
    ) -> dict:
        return tools.create_ground_patch(type, polygon, parcel, points, water_y, max_depth)

    @server.tool(description=(
        "Give a ground patch another ground type (see list_ground_types). A water type needs `water_y` unless the "
        "patch has one, and may give `max_depth` (the body's deepest point, metres, 0 to 20); a type that is not water "
        "drops both. Shows after the next full run (make map)."))
    def set_patch_type(id: str, type: str, water_y: float | None = None, max_depth: float | None = None) -> dict:
        return tools.set_patch_type(id, type, water_y, max_depth)

    @server.tool(description=(
        "Change the flat water surface height of a ground patch of a water type (`water_y`, metres on the terrain's "
        "datum, e.g. 185.6): the body of water stays level over the terrain and is also written to water.json "
        "(swimming, the shooter's water surface). `max_depth` (metres, optional, 0 to 20) sets the body's deepest "
        "point: its natural bed is scaled to it. Raise or lower the level by calling this again."))
    def set_patch_water(id: str, water_y: float, max_depth: float | None = None) -> dict:
        return tools.set_patch_water(id, water_y, max_depth)

    @server.tool(description=(
        "Move one corner of a ground patch's polygon (`index`, 0-based as in the file) to an absolute "
        "`position` [x, y] or by an `offset` {\"dx\": east m, \"dy\": north m}; give exactly one. "
        "A move that would make the polygon self-intersect is refused and nothing is written."))
    def move_patch_corner(
        id: str, index: int, position: list[float] | None = None, offset: dict | None = None
    ) -> dict:
        return tools.move_patch_corner(id, index, position, offset)

    @server.tool(description="Delete a ground patch by id (e.g. GP-0001).")
    def delete_ground_patch(id: str) -> dict:
        return tools.delete_ground_patch(id)

    @server.tool(description=(
        "List ground patches: id, ground type, water_y (water types), area_m2, point count, bbox [minx, miny, maxx, maxy] and "
        "centroid (absolute S-JTSK). Optional filter `near` = {\"x\", \"y\", radius_m?} or "
        "{\"parcel\", radius_m?} (default radius 100 m; adds distance_m, nearest first); `limit`."))
    def list_ground_patches(near: dict | None = None, limit: int = 50) -> dict:
        return tools.list_ground_patches(near, limit)
