"""Tools on zones (what an area means in play) and the catalog of zone kinds."""

from __future__ import annotations

from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.edits.mcp.base import read_edits
from drycastle_pipeline.edits.mcp.ids import RULE_ZONE_ID, next_id
from drycastle_pipeline.edits.schema import Edits
from drycastle_pipeline.edits.zones import KIND_NAMES, PLANTATION, PLAYER_SPAWN, RULE_KEYS
from drycastle_pipeline.edits.zones import kind_rows as zone_kind_rows
from drycastle_pipeline.edits.zones import rules as zone_rules


class ZoneTools:
    """The tools on zones."""

    def _zone_kind(self, kind: object) -> str:
        if not isinstance(kind, str) or kind not in KIND_NAMES:
            raise ToolError(f"unknown zone kind {kind!r}; choose one of {', '.join(KIND_NAMES)} (see list_zone_kinds)")
        return kind

    def move_zone_corner(
        self, id: str, index: int, position: list[float] | None = None, offset: dict | None = None, kind: str | None = None
    ) -> dict:
        """Move one corner of a zone to a position or by an offset."""
        return self._move_corner("zones", self._zone_ref(id), index, position, offset, kind)

    def create_zone(
        self,
        kind: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list | None = None,
    ) -> dict:
        """Create a zone (see the registered tool description)."""
        name = self._zone_kind(kind)
        pts = self._points_of(polygon, parcel, points, "zone")

        def change(edits: Edits) -> str:
            zid = next_id("Z", list(edits.zones))
            edits.zones[zid] = {"kind": name, "polygon": pts}
            return zid

        zid = self._write(change)
        row = self._shape_row(zid, {"kind": name, "polygon": pts})
        return {
            "summary": f"created {zid} ({name}, {row['points']} points, {row['area_m2']} m2); the server gets its points after the next full run (make map)",
            **row,
            "polygon": pts,
            "warnings": [],
        }

    def set_zone_kind(self, id: str, kind: str, from_kind: str | None = None) -> dict:
        """Give a zone another kind."""
        zid = self._zone_ref(id)
        name = self._zone_kind(kind)

        def change(edits: Edits) -> tuple[str, str]:
            if RULE_ZONE_ID.fullmatch(zid):  # comes from a rule: it becomes an ordinary zone of the new kind
                new = self._detach(edits, zid, from_kind, new_kind=name)
                assert new is not None
                return new, str(from_kind)
            entry = self._zone_entry(edits, zid)
            old = entry["kind"]
            entry["kind"] = name
            return zid, old

        new, old = self._write(change)
        return {"summary": f"{zid} kind {old} -> {name}" + (f" (now {new})" if new != zid else ""), "id": new, "kind": name, "warnings": []}

    def delete_zone(self, id: str, kind: str | None = None) -> dict:
        """Delete a zone."""
        zid = self._zone_ref(id)

        def change(edits: Edits) -> dict:
            if RULE_ZONE_ID.fullmatch(zid):  # comes from a rule: cut it out of the rule zone
                self._detach(edits, zid, kind, keep=False)
                return {"kind": kind}
            self._zone_entry(edits, zid)
            return edits.zones.pop(zid)

        entry = self._write(change)
        return {"summary": f"deleted {zid} ({entry['kind']})", "id": zid, "entry": entry, "warnings": []}

    def _zone_entries(self, edits: Edits, kind: str | None) -> dict[str, dict]:
        """Every zone of a kind (all kinds: None) as edits entries with absolute polygons: the ``Z-`` zones and the ones that
        come from a rule (``A-``), which the editor treats the same."""
        entries = {zid: e for zid, e in edits.zones.items() if kind is None or e["kind"] == kind}
        if not self.map.work_dir.joinpath("parcels.json").is_file():
            return entries
        for zid, k, poly in self._rule_zones(edits, kind):
            entries[zid + ("" if kind else f"|{k}")] = {"kind": k, "polygon": [self.map.to_abs(x, y) for x, y in list(poly.exterior.coords)[:-1]]}
        return entries

    def list_zones(self, near: dict | None = None, limit: int = 50, kind: str | None = None) -> dict:
        """List the zones, optionally of one kind or near a point or parcel."""
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
            raise ToolError("limit must be an integer >= 1")
        if kind is not None:
            self._zone_kind(kind)
        edits = read_edits(self.settings.edits_path)
        entries = self._zone_entries(edits, kind)
        rows = [{**self._shape_row(key.split("|")[0], e), "_key": key} for key, e in sorted(entries.items())]
        rows = [{k: v for k, v in r.items() if k != "_key"} for r in self._near_rows(rows, entries, near)]
        return {"count": len(rows[:limit]), "total_matching": len(rows), "zones": rows[:limit]}

    def list_zone_kinds(self) -> dict:
        """The catalog of zone kinds with their rules (see the registered tool description)."""
        edits = read_edits(self.settings.edits_path)
        rules = zone_rules(load_section("zones", self.settings.rules_path))
        rows = []
        for row in zone_kind_rows(rules):
            rule = rules.get(row["name"], {})
            rows.append({
                **{k: row[k] for k in ("name", "label", "group", "where_ground")},
                "zones": len(self._zone_entries(edits, row["name"])),
                "points": "none: read by the props stage as spawn candidates" if row["name"] == PLAYER_SPAWN else "none: the export writes its polygons (zone_polygons.json), the game tests who stands inside" if row["name"] == PLANTATION else {k: rule[k] for k in RULE_KEYS if k in rule} or "no rule in rules.yaml zones",
            })
        return {"summary": f"{len(rows)} zone kinds", "kinds": rows}


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "List the zone kinds: what a marked area can mean in play (a fixed catalog: bigfoot, player_spawn, plantation and one per wild "
        "species). Per kind: label, group (event, player, wildlife), `where_ground` (a rule zone: the ground types the kind "
        "applies to wherever they lie; null: only the zones you create), the number of its `zones` and `points` (the density "
        "rule of the candidate points the server picks from; player_spawn has none, the props stage takes spawn "
        "candidates on its ground; plantation has none, the export writes its outlines for the server). Use a name as the `kind` of create_zone."))
    def list_zone_kinds() -> dict:
        return tools.list_zone_kinds()

    @server.tool(description=(
        "Create a zone (id Z-NNNN): a marked area of exactly one zone kind (list_zone_kinds), independent of parcel "
        "boundaries. A zone never changes the terrain (a zone over a forest does not cut the forest) and zones overlap "
        "freely. After the next full run (make map) the export draws candidate points inside it (clear of buildings and "
        "water) for the game server, or, for player_spawn, takes spawn candidates on the ground inside it. Shape forms, "
        "validation and result as create_ground_patch: `polygon` = [[x, y], ...] in absolute S-JTSK metres, or `parcel` + "
        "`points` = [[along, inward], ...]."))
    def create_zone(
        kind: str,
        polygon: list[list[float]] | None = None,
        parcel: str | None = None,
        points: list[list[float]] | None = None,
    ) -> dict:
        return tools.create_zone(kind, polygon, parcel, points)

    @server.tool(description=(
        "Give a zone another kind (see list_zone_kinds). A zone from list_zones with an `A-` id (one the kind has by "
        "rule, wherever some ground lies) works the same: give its current kind as `from_kind`; it is returned as a new Z- zone."))
    def set_zone_kind(id: str, kind: str, from_kind: str | None = None) -> dict:
        return tools.set_zone_kind(id, kind, from_kind)

    @server.tool(description=(
        "Move one corner of a zone's polygon (`index`, 0-based as in the file) to an absolute "
        "`position` [x, y] or by an `offset` {\"dx\": east m, \"dy\": north m}; give exactly one. "
        "A move that would make the polygon self-intersect is refused and nothing is written. For an `A-` zone from "
        "list_zones give its `kind`; it is returned as a new Z- zone."))
    def move_zone_corner(
        id: str, index: int, position: list[float] | None = None, offset: dict | None = None, kind: str | None = None
    ) -> dict:
        return tools.move_zone_corner(id, index, position, offset, kind)

    @server.tool(description=(
        "Delete a zone by id (e.g. Z-0001, or A-0012 from list_zones: then also give its `kind`, since one ground area "
        "is a zone of several kinds)."))
    def delete_zone(id: str, kind: str | None = None) -> dict:
        return tools.delete_zone(id, kind)

    @server.tool(description=(
        "List the zones: id, kind, area_m2, point count, bbox [minx, miny, maxx, maxy] and centroid (absolute "
        "S-JTSK). Every zone is here, the Z- ones you created and the A- ones a kind has wherever some ground lies "
        "(deer in forests); both can be moved, retyped and deleted the same. Optional filters `kind`, "
        "`near` = {\"x\", \"y\", radius_m?} or {\"parcel\", radius_m?} (default radius 100 m; adds distance_m, nearest "
        "first); `limit`."))
    def list_zones(near: dict | None = None, limit: int = 50, kind: str | None = None) -> dict:
        return tools.list_zones(near, limit, kind)
