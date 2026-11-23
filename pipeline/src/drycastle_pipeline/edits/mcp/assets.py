"""Tool: list_assets."""

from __future__ import annotations

from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

ASSET_NOTE = (
    "Sizes are the raw bounding boxes in metres [x, y, z] (y up). Some packs are authored small "
    "(the Kenney suburban kit: a tree is 0.77 m, a fence section 0.5 m), so a `scale` on "
    "place_object may be needed; cars and primitives are real size. "
    "Any asset can instead be sized to an exact bounding box with place_object `size_m` [length, width, height]."
)


class AssetTools:
    """list_assets."""

    def list_assets(self, category: str | None = None) -> dict:
        """List placeable assets (see the registered tool description)."""
        from drycastle_pipeline.core.assets import CATEGORIES

        if category is not None and category not in CATEGORIES:
            raise ToolError(f"unknown category {category!r}; choose one of {', '.join(CATEGORIES)}")
        rows = [
            {
                "id": a["id"],
                "category": a["category"],
                "name": a["name"],
                "size_m": a.get("size_m"),
                "tags": a.get("tags", []),
            }
            for a in self.manifest["assets"]
            if category is None or a["category"] == category
        ]
        return {"count": len(rows), "assets": rows}


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=f"List assets that place_object can use. Each has id (use as `asset`), "
                 f"category (car, slab, pot, flower, bush, tree, wall, fence, pole, connector, other), name, size_m "
                 f"and tags; primitives (prim:wall, prim:slab, prim:pot, prim:radio_tower, prim:bunker, prim:mound (a 12 m x 4 m terraced hill you can walk up, origin at the "
                 f"centre of its base on the ground; it scales with `scale`, keep scale <= 1.07 so each ledge stays steppable) and the "
                 f"utility poles prim:pole_*, category pole: origin at the foot, the wires run along the object's "
                 f"x axis, the lamp reaches to its -y; and the flood wall pieces prim:floodwall_*, category wall: "
                 f"origin on the wall line, the wall runs along the object's x axis, the ledge is to its -y, "
                 f"see docs/flood-wall.md) are included. "
                 f"Optional filter `category`. {ASSET_NOTE}")
    def list_assets(category: str | None = None) -> dict:
        return tools.list_assets(category)
