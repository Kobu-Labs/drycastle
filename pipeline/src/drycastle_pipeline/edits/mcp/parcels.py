"""Tools on parcels: find, get, ground type, outline."""

from __future__ import annotations

import json
import math
import time
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import Point, Polygon

from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.edits.mcp.base import check_num, read_edits
from drycastle_pipeline.edits.mcp.ids import PARCEL_ID
from drycastle_pipeline.edits.mcp.mapdata import pt
from drycastle_pipeline.edits.mcp.settings import STATE_FORMAT_VERSION, STATE_STALE_S
from drycastle_pipeline.edits.schema import COLUMN_TYPES, FENCE_TYPES, Edits, resolve_fence
from drycastle_pipeline.parcels.outlines import buildings_outside, splice_outline
from drycastle_pipeline.props.fencestyles import FENCE_STYLES


class ParcelTools:
    """find_parcels, get_parcel, the ground type and outline of a parcel."""

    def find_parcels(
        self,
        near: dict | None = None,
        kind: str | None = None,
        selected: bool = False,
        limit: int = 20,
    ) -> dict:
        """Find parcels (see the registered tool description)."""
        from drycastle_pipeline.parcels.build import KINDS

        m = self.map
        if kind is not None and kind not in KINDS:
            raise ToolError(f"unknown kind {kind!r}; choose one of {', '.join(KINDS)}")
        if isinstance(limit, bool) or not isinstance(limit, int) or limit < 1:
            raise ToolError("limit must be an integer >= 1")
        if selected and near is not None:
            raise ToolError("give either selected or near, not both")
        edits = read_edits(self.settings.edits_path)
        extra: dict = {}
        warnings: list[str] = []
        distances: dict[str, float] = {}
        if selected:
            ids, extra, warnings = self._read_state()
            pool = ids
        elif near is not None:
            if not isinstance(near, dict):
                raise ToolError('near must be {"x": .., "y": ..} or {"parcel": "P-0001", "radius_m": ..}')
            radius = check_num("near.radius_m", near.get("radius_m", 100.0), positive=True)
            if "parcel" in near:
                src = m.polys[m.need(near["parcel"])]
            elif "x" in near and "y" in near:
                x = check_num("near.x", near["x"])
                y = check_num("near.y", near["y"])
                src = Point(*m.to_local(x, y))
            else:
                raise ToolError('near needs "x" and "y" (S-JTSK metres) or "parcel"')
            pool = m.parcels_near(src, radius)
            distances = {p: m.polys[p].distance(src) for p in pool}
            pool = sorted(pool, key=lambda p: (distances[p], p))
        else:
            pool = sorted(m.parcels)
        if kind:
            pool = [p for p in pool if m.parcels[p]["kind"] == kind]
        rows = [self._parcel_row(p, edits, distances.get(p)) for p in pool[:limit]]
        out = {"count": len(rows), "total_matching": len(pool), "parcels": rows, **extra}
        if warnings:
            out["warnings"] = warnings
        return out

    def _parcel_row(self, pid: str, edits: Edits, distance: float | None = None) -> dict:
        m = self.map
        p = m.parcels[pid]
        ax, ay = p["anchor"]
        row = {
            "id": pid,
            "kind": p["kind"],
            "area_m2": round(m.polys[pid].area, 2),
            "anchor": m.to_abs(ax, ay),
            "group": self._group_of(edits, pid),
            "has_edits": bool(edits.parcels.get(pid)) or self._group_of(edits, pid) is not None,
        }
        if distance is not None:
            row["distance_m"] = round(distance, 1)
        return row

    def _state_doc(self, guide: str, what: str) -> tuple[dict, float, list[str]]:
        """The viewer's editor-state.json: the document, its age in seconds and a staleness warning."""
        path = self.settings.state_path
        if not path.is_file():
            raise ToolError(f"no editor state file at {path}. {guide}")
        try:
            doc = json.loads(path.read_text())
            if not isinstance(doc, dict):
                raise TypeError("not a JSON object")
            if doc.get("format_version") != STATE_FORMAT_VERSION:
                raise ValueError(f"format_version {doc.get('format_version')!r}")
        except (OSError, ValueError, TypeError) as exc:
            raise ToolError(f"editor state file {path} is unreadable ({exc}). {guide}") from exc
        age = max(0.0, time.time() - path.stat().st_mtime)
        warnings = []
        if age > STATE_STALE_S:
            warnings.append(
                f"the state file is {age / 60:.0f} min old; the viewer may no longer be running, "
                f"so this {what} could be outdated"
            )
        return doc, age, warnings

    def _read_state(self) -> tuple[list[str], dict, list[str]]:
        path = self.settings.state_path
        guide = (
            "Start the viewer in editor mode (godot --path godot/viewer -- --editor --watch ...), "
            "select parcels by clicking, then try again; or pass parcel IDs explicitly."
        )
        doc, age, warnings = self._state_doc(guide, "selection")
        try:
            ids = list(doc["selected"])
        except (KeyError, TypeError) as exc:
            raise ToolError(f"editor state file {path} is unreadable ({exc}). {guide}") from exc
        if not ids:
            raise ToolError(
                f"nothing is selected in the viewer (state file {path.name}). Click a parcel in "
                "editor mode (Shift+click adds more), or pass IDs explicitly."
            )
        unknown = [i for i in ids if i not in self.map.parcels]
        if unknown:
            raise ToolError(
                f"the viewer's selection {unknown[:5]} is not in this map: the state file comes "
                "from an older map. Check the --state option."
            )
        extra = {"selection_age_s": round(age, 1), "camera": doc.get("camera")}
        return sorted(ids), extra, warnings

    def get_parcel(self, id: str) -> dict:
        """Describe one parcel (see the registered tool description)."""
        m = self.map
        pid = m.need(id)
        p, poly = m.parcels[pid], m.polys[pid]
        edits = read_edits(self.settings.edits_path)
        frame, warnings = m.frame(pid)
        sides = m.sides(pid)
        c = poly.centroid
        minx, miny, maxx, maxy = poly.bounds
        gid = self._group_of(edits, pid)
        entry = edits.parcels.get(pid, {})
        group_fence = edits.groups[gid].get("fence") if gid else None
        fence_rules = load_section("props", self.settings.rules_path)
        fcfg, tcfg = fence_rules["fences"], fence_rules["trees"]
        effective = resolve_fence(entry.get("fence"), group_fence)
        buildings = []
        for bid in p.get("buildings", []):
            if bid not in m.bpolys:
                continue
            bp = m.bpolys[bid]
            bx0, by0, bx1, by1 = bp.bounds
            buildings.append(
                {
                    "id": bid,
                    "footprint_centroid": m.to_abs(bp.centroid.x, bp.centroid.y),
                    "bounds": [*m.to_abs(bx0, by0), *m.to_abs(bx1, by1)],
                    "area_m2": round(bp.area, 1),
                    "height_m": m.buildings[bid].get("height_m"),
                }
            )
        neighbours = [
            {"id": q, "kind": m.parcels[q]["kind"]}
            for q in m.parcels_near(poly, 0.5)
            if q != pid
        ]
        nx, ny = frame.side.normal
        return {
            "id": pid,
            "kind": p["kind"],
            "area_m2": round(m.polys[pid].area, 2),
            "cadastral_ref": p.get("cadastral_ref"),
            "group": gid,
            "polygon": {
                "bounds": [*m.to_abs(minx, miny), *m.to_abs(maxx, maxy)],
                "centroid": m.to_abs(c.x, c.y),
                "vertices": len(poly.exterior.coords) - 1,
            },
            "edges": [
                {
                    "index": s.index,
                    "length_m": round(s.length, 2),
                    "midpoint": m.to_abs(*s.mid),
                    "outward_normal": pt(*s.normal),
                    "road_distance_m": None if math.isinf(s.road_distance) else round(s.road_distance, 1),
                    "is_front": s.index == frame.side.index,
                }
                for s in sides
            ],
            "front_edge": {
                "index": frame.side.index,
                "length_m": round(frame.side.length, 2),
                "midpoint": m.to_abs(*frame.side.mid),
                "outward_normal": pt(nx, ny),
                "road_distance_m": None
                if math.isinf(frame.side.road_distance)
                else round(frame.side.road_distance, 1),
                "inward_axis": pt(*frame.inward),
                "right_axis": pt(*frame.right),
                "note": "along grows to the right (standing on the front edge, facing in), "
                "inward grows away from the road; anchors front/back/left/right/centre are "
                "relative to this",
            },
            "buildings": buildings,
            "neighbours": neighbours,
            "edits": {
                "fence": entry.get("fence"),
                # objects belong to no parcel: these are the ones whose position lies inside this one
                "objects": [
                    o for o in edits.objects
                    if self.map.polys[pid].covers(Point(*self.map.to_local(*o["position"])))
                ],
                "group_fence": group_fence,
            },
            "effective_fence": {
                "mode": effective.mode,
                "type": effective.type,
                "column": effective.column or FENCE_STYLES[effective.type].column,
                "height_m": effective.height_m,
                "thickness_m": effective.thickness_m,
                "colour": effective.colour,
                "gaps": [[g[0], g[1], g[2]] for g in effective.gaps],
            },
            "rule_defaults": {
                "fence": {
                    "kind_gets_fence": p["kind"] in fcfg["kinds"],
                    "probability": fcfg["probability"],
                    "auto_height_m": fcfg["height_m"],
                    "gate_gap_m": fcfg["gate_gap_m"],
                    "custom_types": FENCE_TYPES,
                    "column_types": list(COLUMN_TYPES),
                },
            },
            **({"warnings": warnings} if warnings else {}),
        }

    def set_parcel_ground_type(self, parcel: str, type: str | None = None) -> dict:
        """Give one parcel the ground of a ground type (``ground_type`` of its edits entry); none removes it."""
        if not (isinstance(parcel, str) and PARCEL_ID.fullmatch(parcel)):
            raise ToolError(f"parcel {parcel!r} is not a parcel ID like P-0412")
        pid = self.map.need(parcel)

        def change(edits: Edits) -> bool:
            holder = edits.parcels.setdefault(pid, {})
            if not type:
                return holder.pop("ground_type", None) is not None
            changed = holder.get("ground_type") != type
            holder["ground_type"] = type
            return changed

        changed = self._write(change)  # an emptied entry is dropped by _write; an unknown or water type is refused by the validator
        return {
            "summary": f"ground of {pid} is now {type}" if type else (
                f"ground type of {pid} removed" if changed else f"{pid} had no ground type"),
            "parcel": pid,
            "ground_type": type or None,
            "changed": changed,
            "note": "Needs the next make map-update to show on the map.",
        }

    def set_parcel_outline(self, parcel: str, points: list | None = None, sketch_line: str | None = None) -> dict:
        """Replace one parcel's outline (``outline`` of its edits entry), whole or by splicing a sketch line into it."""
        if not (isinstance(parcel, str) and PARCEL_ID.fullmatch(parcel)):
            raise ToolError(f"parcel {parcel!r} is not a parcel ID like P-0412")
        if (points is None) == (sketch_line is None):
            raise ToolError("give exactly one of points (the full new outline, [[x, y], ...] absolute S-JTSK metres) or sketch_line (an open line id of get_sketch)")
        pid = self.map.need(parcel)
        m = self.map
        if points is not None:
            if not isinstance(points, list):
                raise ToolError("points must be a list of [x, y] points")
            new = [tuple(self._xy(f"points[{i}]", p)) for i, p in enumerate(points)]
            if len(new) > 1 and new[0] == new[-1]:
                new.pop()
        else:
            line, line_closed = self._sketch_line(sketch_line)
            if line_closed:
                raise ToolError(f"sketch line {sketch_line} is closed; give an open line, or its points as the full outline")
            if len(line) < 2:
                raise ToolError(f"sketch line {sketch_line} has fewer than 2 points")
        info: dict = {}

        def change(edits: Edits) -> None:
            holder = edits.parcels.setdefault(pid, {})
            if points is None:
                current = holder.get("outline") or [m.to_abs(x, y) for x, y in list(m.polys[pid].exterior.coords)[:-1]]
                try:
                    ring = splice_outline([(float(x), float(y)) for x, y in current], [(x, y) for x, y in line])
                except ValueError as exc:
                    raise ToolError(f"cannot splice {sketch_line} into the outline of {pid}: {exc}") from exc
            else:
                ring = new
            outline = [pt(x, y) for x, y in ring]
            local = Polygon([m.to_local(x, y) for x, y in outline])
            info["old_area_m2"] = round(m.polys[pid].area, 1)
            info["trimmed"] = {}
            if local.is_valid and local.area > 0:  # otherwise the validator refuses it below
                inside = {b: m.bpolys[b] for b in m.parcels[pid].get("buildings", []) if b in m.bpolys}
                out = buildings_outside(local, inside)
                if out:
                    raise ToolError(f"refused: the new outline of {pid} leaves {', '.join(out)} outside; nothing was written")
                info["new_area_m2"] = round(local.area, 1)
                edited = {p for p, e in edits.parcels.items() if e.get("outline")} | {pid}
                info["trimmed"] = {
                    other: round(m.polys[other].intersection(local).area, 1)
                    for other in m.parcels_near(local, 0.0) if other not in edited
                    and m.polys[other].intersection(local).area > 0.01
                }
            holder["outline"] = outline

        self._write(change)  # a self-crossing outline or one overlapping another edited outline is refused by the validator
        trimmed = info["trimmed"]
        return {
            "summary": f"outline of {pid} replaced: {info['old_area_m2']} m2 -> {info['new_area_m2']} m2"
                       + (f"; neighbours cut back: {trimmed}" if trimmed else ""),
            "parcel": pid,
            **info,
            "warnings": [],
            "note": "Needs make parcels, then make map-update, to show on the map (the outline is applied in the parcels stage).",
        }

    def reset_parcel_outline(self, parcel: str) -> dict:
        """Remove a parcel's outline edit: its cadastral outline applies again."""
        if not (isinstance(parcel, str) and PARCEL_ID.fullmatch(parcel)):
            raise ToolError(f"parcel {parcel!r} is not a parcel ID like P-0412")

        def change(edits: Edits) -> bool:
            return bool(edits.parcels.get(parcel, {}).pop("outline", None))

        changed = self._write(change)
        return {
            "summary": f"outline of {parcel} reset to the cadastre" if changed else f"{parcel} had no outline edit",
            "parcel": parcel,
            "changed": changed,
            "note": "Needs make parcels, then make map-update, to show on the map.",
        }


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Find parcels. Filters: `near` = {\"x\": .., \"y\": ..} in S-JTSK metres with optional "
        "radius_m (default 100), or {\"parcel\": \"P-0412\", \"radius_m\": 50} for parcels around "
        "another one; `kind` (building_plot, road, forest, field, water, garden, other); "
        "`selected: true` returns the parcels currently selected in the viewer's editor mode "
        "(read from editor-state.json; use this for 'the selected parcel' / 'this house' and "
        "also returns the camera). Results (up to `limit`, default 20; nearest first with `near`) "
        "have id, kind, area_m2, anchor (a point inside, S-JTSK), group and has_edits."))
    def find_parcels(
        near: dict | None = None, kind: str | None = None, selected: bool = False, limit: int = 20
    ) -> dict:
        return tools.find_parcels(near, kind, selected, limit)

    @server.tool(description=(
        "Everything needed to edit one parcel: kind, area, cadastral ref, polygon bounds and "
        "centroid, edges with lengths, the FRONT EDGE (the side nearest a road: midpoint, outward "
        "normal and the axes used by place_object: `along` grows to the right when standing on "
        "the front edge looking into the parcel, `inward` grows away from the road), buildings on "
        "it (footprint centroid, bounds), neighbouring parcels, its group, the current edits "
        "(fence, objects), the effective fence after group inheritance, and the rules' "
        "defaults (fence probability, tree density for its kind). Coordinates are S-JTSK metres."))
    def get_parcel(id: str) -> dict:
        return tools.get_parcel(id)

    @server.tool(description=(
        "Give ONE parcel the ground of a ground type (see list_ground_types; make one with set_ground_type, e.g. `graveyard`): its "
        "terrain colours and detail recipe come from that type instead of the yard of its kind (a type without a recipe: bare "
        "ground, no detail). Kind, layout and fence are unchanged. Not a water type. null removes it. Ground patches cannot do this "
        "(they paint only outside parcels). Needs the next make map-update to show on the map."))
    def set_parcel_ground_type(parcel: str, type: str | None = None) -> dict:
        return tools.set_parcel_ground_type(parcel, type)

    @server.tool(description=(
        "Replace the outline of ONE parcel (the cadastral boundary; no edit = untouched). Give `sketch_line` = the id of an open "
        "line the user drew (get_sketch): the stretch of the current boundary between the two points nearest the line's ends is "
        "replaced by the line, keeping the larger part of the parcel, so the user draws only the part that changes. Or give "
        "`points` = [[x, y], ...], absolute S-JTSK metres, the full new outline. The outline must be a simple polygon and the "
        "parcel's buildings must stay inside it. Where it overlaps an unedited parcel the edited one wins and the neighbour is "
        "cut back; two edited outlines must not overlap. Land the parcel gives up becomes ground. Fence, ground, detail and "
        "object clearance follow the new outline. Needs make parcels, then make map-update, to show on the map."))
    def set_parcel_outline(parcel: str, points: list[list[float]] | None = None, sketch_line: str | None = None) -> dict:
        return tools.set_parcel_outline(parcel, points, sketch_line)

    @server.tool(description=(
        "Remove the outline edit of a parcel: its cadastral outline applies again. Needs make parcels, then make map-update."))
    def reset_parcel_outline(parcel: str) -> dict:
        return tools.reset_parcel_outline(parcel)
