"""Tools on property groups."""

from __future__ import annotations

import json
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError
from shapely.ops import unary_union

from drycastle_pipeline.edits.mcp.ids import GROUP_ID, next_id
from drycastle_pipeline.edits.schema import CUSTOM_FENCE_KEYS, Edits, load_edits


class GroupTools:
    """create_group, merge_groups, dissolve_group."""

    def _group_fence(self, fence: dict | None) -> dict | None:
        """Validate a group fence given to a tool call; None when none was given."""
        if fence is None:
            return None
        if not isinstance(fence, dict):
            raise ToolError("fence must be an object like {mode, type, height_m, ...}")
        mode = fence.get("mode")
        custom = {k: v for k, v in fence.items() if k != "mode"}
        if "gaps" in custom:
            custom["gaps"] = self._gaps(custom["gaps"], "group", "")
        if mode in ("auto", "none") and custom:
            raise ToolError(f"mode {mode!r} cannot be combined with {', '.join(custom)}")
        unknown = set(custom) - set(CUSTOM_FENCE_KEYS)
        if unknown:
            raise ToolError(f"unknown fence keys: {sorted(unknown)}")
        return {**({"mode": mode} if mode else {}), **custom}

    def create_group(self, parcels: list[str], fence: dict | None = None) -> dict:
        """Create a property group (see the registered tool description)."""
        if not isinstance(parcels, list) or len(parcels) < 2:
            raise ToolError("a group needs 2 or more parcels (give a list of parcel IDs)")
        ids = sorted({self.map.need(p) for p in parcels})
        if len(ids) < 2:
            raise ToolError("a group needs 2 or more different parcels")
        fence_entry = self._group_fence(fence)
        warnings: list[str] = []
        union = unary_union([self.map.polys[p].buffer(0.3) for p in ids])
        if union.geom_type != "Polygon":
            warnings.append(
                "the parcels are not all touching each other; the group fence runs around each "
                "separate piece"
            )

        def change(edits: Edits) -> str:
            taken = {p: self._group_of(edits, p) for p in ids if self._group_of(edits, p)}
            if taken:
                raise ToolError(
                    "already in a group: "
                    + ", ".join(f"{p} ({g})" for p, g in sorted(taken.items()))
                    + ". dissolve_group first."
                )
            gid = next_id("G", list(edits.groups))
            group: dict = {"parcels": ids}
            if fence_entry is not None:
                group["fence"] = fence_entry
            edits.groups[gid] = group
            return gid

        gid = self._write(change)
        entry = load_edits(self.settings.edits_path).groups[gid]
        return {
            "summary": f"created {gid} from {', '.join(ids)}",
            "id": gid,
            "entry": entry,
            "warnings": warnings,
        }

    def merge_groups(
        self, ids: list[str], fence: dict | None = None, parcels: list[str] | None = None
    ) -> dict:
        """Merge property groups (and loose parcels) into a new group (see the registered tool description)."""
        extra = sorted({self.map.need(p) for p in (parcels or [])})
        if not isinstance(ids, list) or len(ids) < (1 if extra else 2):
            raise ToolError("merging needs 2 or more group IDs like G-0001 (or 1 plus `parcels`)")
        for i in ids:
            if not (isinstance(i, str) and GROUP_ID.fullmatch(i)):
                raise ToolError(f"{i!r} is not a group ID like G-0001")
        if len(set(ids)) < len(ids):
            raise ToolError("merging needs 2 or more different groups (or 1 plus `parcels`)")
        given = self._group_fence(fence)
        warnings: list[str] = []

        def change(edits: Edits) -> tuple[str, dict, list[str]]:
            missing = [i for i in ids if i not in edits.groups]
            if missing:
                raise ToolError(f"no such group: {', '.join(missing)}; groups: {sorted(edits.groups) or 'none'}")
            taken = {p: self._group_of(edits, p) for p in extra if self._group_of(edits, p) not in (None, *ids)}
            if taken:
                raise ToolError(
                    "already in another group: "
                    + ", ".join(f"{p} ({g})" for p, g in sorted(taken.items()))
                )
            members = sorted({p for i in ids for p in edits.groups[i]["parcels"]} | set(extra))
            if given is not None:
                chosen = given
            else:
                fences = [edits.groups[i].get("fence") for i in ids]
                distinct = {json.dumps(f, sort_keys=True) for f in fences if f is not None}
                chosen = next((f for f in fences if f is not None), None)
                if len(distinct) > 1:
                    warnings.append(
                        f"the groups had different fences; kept the one of "
                        f"{next(i for i in ids if edits.groups[i].get('fence') is not None)} "
                        "(pass `fence` to choose)"
                    )
            for i in ids:
                del edits.groups[i]
            gid = next_id("G", list(edits.groups))
            group: dict = {"parcels": members}
            if chosen is not None:
                group["fence"] = chosen
            edits.groups[gid] = group
            return gid, group, members

        gid, group, members = self._write(change)
        union = unary_union([self.map.polys[p].buffer(0.3) for p in members])
        if union.geom_type != "Polygon":
            warnings.append(
                "the parcels are not all touching each other; the group fence runs around each "
                "separate piece"
            )
        return {
            "summary": f"merged {', '.join(ids)} into {gid} ({', '.join(members)})",
            "id": gid,
            "entry": group,
            "merged": ids,
            "warnings": warnings,
        }

    def dissolve_group(self, id: str) -> dict:
        """Remove a property group (parcel fences of its members stay as they are)."""
        if not (isinstance(id, str) and GROUP_ID.fullmatch(id)):
            raise ToolError(f"{id!r} is not a group ID like G-0001")

        def change(edits: Edits) -> dict:
            if id not in edits.groups:
                raise ToolError(f"group {id} does not exist; groups: {sorted(edits.groups) or 'none'}")
            return edits.groups.pop(id)

        group = self._write(change)
        return {
            "summary": f"dissolved {id} ({', '.join(group['parcels'])})",
            "id": id,
            "entry": group,
            "warnings": [],
        }


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Create a property group (id G-NNNN) from 2 or more parcels that belong to one owner: "
        "one fence runs around their combined outer edge (none on the boundaries between "
        "them). A parcel can be in one group only. Optional `fence` takes the set_fence keys "
        "(mode, type, height_m, thickness_m, colour, gaps)."))
    def create_group(parcels: list[str], fence: dict | None = None) -> dict:
        return tools.create_group(parcels, fence)

    @server.tool(description=(
        "Merge 2 or more property groups (G-0001, G-0002, ...) into one new group (id G-NNNN) "
        "holding all their parcels; the old groups disappear. With `parcels` (loose parcels that "
        "are in no other group) one group is enough: group + parcels -> new group. Parcel-level fence edits stay on "
        "the parcels. The new group keeps the fence of the old ones when they agree; otherwise "
        "the first listed group's fence is kept and a warning says so. Optional `fence` "
        "(mode, type, height_m, thickness_m, colour, gaps) replaces it."))
    def merge_groups(ids: list[str], fence: dict | None = None, parcels: list[str] | None = None) -> dict:
        return tools.merge_groups(ids, fence, parcels)

    @server.tool(description="Dissolve a property group (id G-0001). Parcel ids, boundaries and the members' own fence edits stay.")
    def dissolve_group(id: str) -> dict:
        return tools.dissolve_group(id)
