"""Tools on the cables between utility poles."""

from __future__ import annotations

import itertools
import math
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.mcp.base import all_objects, read_edits
from drycastle_pipeline.edits.mcp.ids import next_id
from drycastle_pipeline.edits.schema import (
    CONNECTOR_ASSET_PREFIX,
    POLE_ASSET_PREFIX,
    WIRE_ASSET_PREFIXES,
    Edits,
    cable_pair,
)


class CableTools:
    """connect_poles, disconnect_poles, list_cables."""

    def _pole(self, edits: Edits, oid: object) -> dict:
        """The placed utility pole or house connector with this id (what a cable can end on)."""
        for o in all_objects(edits):
            if o["id"] == oid:
                if not o["asset"].startswith(WIRE_ASSET_PREFIXES):
                    raise ToolError(
                        f"{oid} is {o['asset']}, not a utility pole or a house connector. Cables hang between "
                        f"objects placed from the pole assets (list_assets category \"pole\") and house "
                        f"connectors (category \"connector\", placed in the editor's Poles tool)."
                    )
                return o
        poles = [o["id"] for o in all_objects(edits) if o["asset"].startswith(POLE_ASSET_PREFIX)]
        raise ToolError(f"no placed object {oid!r}. Placed poles: {poles[:30] if poles else 'none'}.")

    @staticmethod
    def _cable_row(cid: str, cable: dict, edits: Edits) -> dict:
        pos = {o["id"]: o["position"] for o in all_objects(edits)}
        a, b = pos.get(cable["from"]), pos.get(cable["to"])
        length = r3(math.hypot(a[0] - b[0], a[1] - b[1])) if a and b else None
        return {"id": cid, "from": cable["from"], "to": cable["to"], "length_m": length}

    def connect_poles(self, poles: list[str]) -> dict:
        """String cables along a chain of placed poles: each to the next."""
        if not isinstance(poles, list) or len(poles) < 2:
            raise ToolError("poles must list two or more placed pole ids, in the order the line runs")
        made: list[dict] = []
        skipped: list[str] = []

        def change(edits: Edits) -> None:
            for oid in poles:
                self._pole(edits, oid)
            for a, b in itertools.pairwise(poles):
                if a == b:
                    raise ToolError(f"{a} is listed twice in a row; a cable joins two different poles")
                pair = cable_pair({"from": a, "to": b})
                old = next((c for c, cb in edits.cables.items() if cable_pair(cb) == pair), None)
                if old:
                    skipped.append(f"{a} and {b} are already joined by {old}")
                    continue
                cid = next_id("C", list(edits.cables))
                edits.cables[cid] = {"from": a, "to": b}
                made.append(self._cable_row(cid, edits.cables[cid], edits))

        self._write(change)
        summary = (
            f"strung {len(made)} cable{'' if len(made) == 1 else 's'}: "
            + ", ".join(f"{c['id']} {c['from']} to {c['to']}" for c in made)
            if made else "no new cable"
        )
        return {"summary": summary, "cables": made, "warnings": skipped}

    def disconnect_poles(self, cable: str | None = None, poles: list[str] | None = None) -> dict:
        """Remove one cable, by its id or by the two poles it joins."""
        if (cable is None) == (poles is None):
            raise ToolError("give either cable (an id like C-0001) or poles (the two pole ids)")

        def change(edits: Edits) -> tuple[str, dict]:
            if cable is not None:
                if cable not in edits.cables:
                    raise ToolError(f"no cable {cable!r}. Existing: {sorted(edits.cables)[:30] or 'none'}.")
                return cable, edits.cables.pop(cable)
            if not isinstance(poles, list) or len(poles) != 2:
                raise ToolError("poles must be the two pole ids the cable joins")
            pair = cable_pair({"from": poles[0], "to": poles[1]})
            cid = next((c for c, cb in edits.cables.items() if cable_pair(cb) == pair), None)
            if cid is None:
                raise ToolError(f"{poles[0]} and {poles[1]} are not joined by a cable")
            return cid, edits.cables.pop(cid)

        cid, entry = self._write(change)
        return {"summary": f"removed {cid} ({entry['from']} to {entry['to']})", "id": cid, "entry": entry,
                "warnings": []}

    def list_cables(self, pole: str | None = None) -> dict:
        """The cables in the file, or those of one pole, and the placed poles."""
        edits = read_edits(self.settings.edits_path)
        rows = [self._cable_row(cid, cb, edits) for cid, cb in sorted(edits.cables.items())
                if pole is None or pole in (cb["from"], cb["to"])]
        placed = {o["id"]: o for o in all_objects(edits)}
        warnings = [f"{r['id']}: {end} is not a placed utility pole (the cable is skipped)"
                    for r in rows for end in (r["from"], r["to"])
                    if not placed.get(end, {}).get("asset", "").startswith(WIRE_ASSET_PREFIXES)]
        poles = [{"id": o["id"], "asset": o["asset"], "position": o["position"],
                  "rotation_deg": o.get("rotation_deg", 0),
                  "cables": sum(o["id"] in (cb["from"], cb["to"]) for cb in edits.cables.values())}
                 for o in placed.values() if o["asset"].startswith(POLE_ASSET_PREFIX)]
        connectors = [{"id": o["id"], "asset": o["asset"], "position": o["position"], "z": o.get("z")}
                      for o in placed.values() if o["asset"].startswith(CONNECTOR_ASSET_PREFIX)]
        return {"summary": f"{len(rows)} cable{'' if len(rows) == 1 else 's'}, {len(poles)} placed poles, "
                           f"{len(connectors)} house connectors",
                "cables": rows, "poles": sorted(poles, key=lambda p: p["id"]),
                "connectors": sorted(connectors, key=lambda c: c["id"]), "warnings": warnings}


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "String cables between placed utility poles (objects placed from the prim:pole_* assets). `poles` is "
        "a list of two or more pole object ids in the order the line runs; each pole is joined to the next, "
        "so a whole street is one call. An id may also be a house connector (prim:connector_*, placed on a "
        "wall or roof in the editor's Poles tool): [pole, connector] strings the cable to the house. "
        "A pair that is already joined is left alone (reported in warnings). "
        "The poles keep their rotation: the wires are matched left to right across each span, so they never "
        "cross; turn a pole with move_object so its crossarm stands across the line. list_cables shows the poles."))
    def connect_poles(poles: list[str]) -> dict:
        return tools.connect_poles(poles)

    @server.tool(description="Remove one cable: `cable` (an id like C-0001) or `poles` (the two pole ids it joins).")
    def disconnect_poles(cable: str | None = None, poles: list[str] | None = None) -> dict:
        return tools.disconnect_poles(cable, poles)

    @server.tool(description=(
        "List the cables (id, from, to, length_m), the placed utility poles (id, asset, position, "
        "rotation_deg, number of cables) and the house connectors. Optional `pole`: only that object's cables."))
    def list_cables(pole: str | None = None) -> dict:
        return tools.list_cables(pole)
