"""Tools on fences of parcels and groups."""

from __future__ import annotations

import json
import re
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.edits.mcp.base import check_num
from drycastle_pipeline.edits.mcp.ids import GROUP_ID, PARCEL_ID
from drycastle_pipeline.edits.schema import COLUMN_TYPES, FENCE_TYPES, HOUSE_COLOUR_TYPES, Edits


class FenceTools:
    """set_fence, reset_fence."""

    def _fence_target(self, target: str) -> tuple[str, str]:
        if isinstance(target, str) and GROUP_ID.fullmatch(target):
            return "group", target
        if isinstance(target, str) and PARCEL_ID.fullmatch(target):
            return "parcel", self.map.need(target)
        raise ToolError(f"target {target!r} is neither a parcel ID (P-0412) nor a group ID (G-0001)")

    def _gaps(self, gaps: object, kind: str, target: str) -> list[dict]:
        if not isinstance(gaps, list):
            raise ToolError('gaps must be a list of {"at": [x, y], "width_m": n}')
        out = []
        for i, g in enumerate(gaps):
            if not isinstance(g, dict) or set(g) - {"at", "width_m"}:
                raise ToolError(f'gaps[{i}] must be {{"at": [x, y] or "front", "width_m": n}}')
            at = g.get("at")
            if at == "front":
                if kind != "parcel":
                    raise ToolError(f'gaps[{i}]: "front" works for a single parcel only; give [x, y]')
                frame, _ = self.map.frame(target)
                at = self.map.to_abs(*frame.origin)
            if not (isinstance(at, (list, tuple)) and len(at) == 2):
                raise ToolError(f'gaps[{i}].at must be [x, y] in S-JTSK metres or "front"')
            width = check_num(f"gaps[{i}].width_m", g.get("width_m", 3.0), positive=True)
            out.append(
                {
                    "at": [check_num(f"gaps[{i}].at", at[0]), check_num(f"gaps[{i}].at", at[1])],
                    "width_m": width,
                }
            )
        return out

    def set_fence(
        self,
        target: str,
        mode: str | None = None,
        type: str | None = None,
        height_m: float | None = None,
        thickness_m: float | None = None,
        colour: str | None = None,
        gaps: list[dict] | None = None,
        column: str | None = None,
        house_colour: bool | None = None,
        sides: list[dict] | None = None,
    ) -> dict:
        """Set the fence of a parcel or property group (see the registered tool description)."""
        kind, tid = self._fence_target(target)
        custom = {
            "type": type, "column": column, "height_m": height_m, "thickness_m": thickness_m,
            "colour": colour, "house_colour": house_colour, "gaps": gaps, "sides": sides,
        }
        custom = {k: v for k, v in custom.items() if v is not None}
        if mode is None and not custom:
            raise ToolError(
                "nothing to set: give mode (auto|none|custom) and/or type, column, height_m, "
                "thickness_m, colour, gaps"
            )
        if mode is not None and mode not in ("auto", "none", "custom"):
            raise ToolError(f"mode must be one of auto, none, custom (got {mode!r})")
        if mode in ("auto", "none") and custom:
            raise ToolError(
                f"mode {mode!r} cannot be combined with {', '.join(custom)}: those keys are "
                "custom-only. Use mode 'custom' (or omit mode), or drop the keys."
            )
        if "type" in custom and custom["type"] not in FENCE_TYPES:
            raise ToolError(f"type must be one of {', '.join(FENCE_TYPES)} (got {type!r})")
        if "column" in custom and custom["column"] not in COLUMN_TYPES:
            raise ToolError(f"column must be one of {', '.join(COLUMN_TYPES)} (got {column!r})")
        for key in ("height_m", "thickness_m"):
            if key in custom:
                check_num(key, custom[key], positive=True)
        if "colour" in custom and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(custom["colour"])):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        if "house_colour" in custom:
            if not isinstance(house_colour, bool):
                raise ToolError(f"house_colour must be true or false (got {house_colour!r})")
            if house_colour and "type" in custom and custom["type"] not in HOUSE_COLOUR_TYPES:
                raise ToolError(f"house_colour works with type {', '.join(HOUSE_COLOUR_TYPES[:3])} only (got {type!r})")
        if "sides" in custom:
            if not isinstance(sides, list) or not all(isinstance(s, dict) and isinstance(s.get("at"), (list, tuple)) and len(s["at"]) == 2 for s in sides):
                raise ToolError('sides must be a list of {"at": [x, y], "mode"?: "none", type?, column?, height_m?, thickness_m?, colour?, house_colour?}')
            custom["sides"] = [{**s, "at": [check_num("sides.at", s["at"][0]), check_num("sides.at", s["at"][1])]} for s in sides]
        if "gaps" in custom:
            custom["gaps"] = self._gaps(custom["gaps"], kind, tid)
        warnings: list[str] = []

        def change(edits: Edits) -> dict:
            if kind == "group":
                if tid not in edits.groups:
                    raise ToolError(f"group {tid} does not exist; groups: {sorted(edits.groups)}")
                holder = edits.groups[tid]
            else:
                gid = self._group_of(edits, tid)
                if gid:
                    warnings.append(
                        f"{tid} is in group {gid}: this writes a fence override on the parcel, "
                        f"which wins field by field over the group's fence. To change the whole "
                        f"group use target {gid}."
                    )
                holder = edits.parcels.setdefault(tid, {})
            fence = dict(holder.get("fence", {}))
            if mode in ("auto", "none"):
                fence = {"mode": mode}
            else:
                if mode == "custom":
                    fence["mode"] = "custom"
                else:  # custom keys without a mode imply custom
                    fence.pop("mode", None)
                fence.update(custom)
            holder["fence"] = fence
            return fence

        fence = self._write(change)
        return {
            "summary": f"fence of {tid} set: {json.dumps(fence, sort_keys=True)}",
            "target": tid,
            "fence": fence,
            "warnings": warnings,
        }

    def reset_fence(self, target: str) -> dict:
        """Remove the fence entry of a parcel or group, so the rules (or the group) apply again."""
        kind, tid = self._fence_target(target)

        def change(edits: Edits) -> bool:
            holder = edits.groups.get(tid) if kind == "group" else edits.parcels.get(tid)
            if kind == "group" and holder is None:
                raise ToolError(f"group {tid} does not exist; groups: {sorted(edits.groups)}")
            if not holder or "fence" not in holder:
                return False
            del holder["fence"]
            return True

        changed = self._write(change)
        return {
            "summary": f"fence of {tid} reset to the rules" if changed else f"{tid} had no fence edit",
            "target": tid,
            "changed": changed,
        }


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Set the fence of a parcel (P-0412) or of a property group (G-0001: one fence around the "
        "group's outer edge). `mode`: auto (the generator's rules), none (no fence) or custom "
        "(always fenced). `type` (picket | wire | brick_red | brick_white | stone_wall | concrete; "
        "textured, each with its own posts or piers), `column` (none | wood_post | metal_post | "
        "brick_red | brick_white | stone | concrete: other columns than the type's own, e.g. picket "
        "between brick_red piers), `height_m`, `thickness_m`, `colour` (\"#rrggbb\": a tint; it is the "
        "paint of picket, wire and concrete, while brick and stone have their own colours and default "
        "to white), `house_colour` (true: brick_red, brick_white and concrete take the wall colour of the "
        "parcel's house, else the group's; no house: `colour` applies), `sides` (one side between two corners "
        "looks different: list of {\"at\": [x, y] near that side, \"mode\": \"none\" for no fence there, or any of type, "
        "column, height_m, thickness_m, colour, house_colour}; replaces the whole list) and `gaps` (gates: list of {\"at\": [x, y] or \"front\", \"width_m\": n}) are "
        "custom-only: giving any of them without a mode means custom; combining them with mode "
        "auto/none is an error. Given keys are merged into the existing fence entry. A parcel that "
        "is in a group gets an override on top of the group's fence (a warning says so)."))
    def set_fence(
        target: str,
        mode: str | None = None,
        type: str | None = None,
        height_m: float | None = None,
        thickness_m: float | None = None,
        colour: str | None = None,
        gaps: list[dict] | None = None,
        column: str | None = None,
        house_colour: bool | None = None,
        sides: list[dict] | None = None,
    ) -> dict:
        return tools.set_fence(target, mode, type, height_m, thickness_m, colour, gaps, column, house_colour, sides)

    @server.tool(description="Remove the fence edit of a parcel or group so the rules (or the group's fence) apply again.")
    def reset_fence(target: str) -> dict:
        return tools.reset_fence(target)
