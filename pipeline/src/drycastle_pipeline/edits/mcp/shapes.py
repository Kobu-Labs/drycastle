"""What ground patches and zones share: ids, polygons, overlap warnings, corner moves, listing near a point."""

from __future__ import annotations

import re
from typing import Any

import shapely.affinity
from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import Point, Polygon

from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.core.rules import load_section
from drycastle_pipeline.edits.mcp.base import check_num
from drycastle_pipeline.edits.mcp.ids import PATCH_ID, RULE_ZONE_ID, ZONE_ID, next_id
from drycastle_pipeline.edits.mcp.mapdata import pt
from drycastle_pipeline.edits.schema import Edits, polygon_ring
from drycastle_pipeline.edits.zones import KIND_NAMES, rule_part_is_cut, rule_parts, zone_cuts
from drycastle_pipeline.edits.zones import rules as zone_rules
from drycastle_pipeline.ground.patches import patch_rank


class ShapeTools:
    """Helpers of the tools on ground patches and zones (both are polygons drawn on the map)."""

    @staticmethod
    def _id(pattern: re.Pattern, noun: str, example: str, lister: str, id: object) -> str:
        if not (isinstance(id, str) and pattern.fullmatch(id)):
            raise ToolError(f"{id!r} is not a {noun} ID like {example}. {lister} shows the existing ones.")
        return id

    def _patch_id(self, id: object) -> str:
        return self._id(PATCH_ID, "ground patch", "GP-0001", "list_ground_patches", id)

    def _zone_id(self, id: object) -> str:
        return self._id(ZONE_ID, "zone", "Z-0001", "list_zones", id)

    def _zone_ref(self, id: object) -> str:
        """A zone ID: ``Z-0001`` or, for a zone that comes from a rule, ``A-0012`` (``A-0012.2`` for the second outline)."""
        if isinstance(id, str) and RULE_ZONE_ID.fullmatch(id):
            return id
        return self._zone_id(id)

    def _rule_zones(self, edits: Edits, kind: str | None = None) -> list[tuple[str, str, Polygon]]:
        """``(id, kind, polygon)`` of the zones that come from a rule (the ground areas of a kind's ``where_ground``), local
        metres, less those that were cut (edited or deleted before)."""
        m = self.map.ensure()
        section = zone_rules(load_section("zones", self.settings.rules_path))
        cuts = zone_cuts(edits.zone_cuts, m.origin)
        out = []
        for k, rule in section.items():
            if (kind is not None and k != kind) or not rule.get("where_ground"):
                continue
            for area in sorted(m.ground, key=lambda a: a["id"]):
                if area["kind"] not in rule["where_ground"]:
                    continue
                out += [(zid, k, poly) for zid, poly in rule_parts(area) if not rule_part_is_cut(poly, cuts.get(k))]
        return out

    def _detach(self, edits: Edits, rid: str, kind: object, new_kind: str | None = None, keep: bool = True) -> str | None:
        """Make a zone that comes from a rule an ordinary edit: cut its polygon out of the rule zone of ``kind`` and, when
        ``keep``, write it as a drawn zone (of ``new_kind``, default the same). Returns the new ``Z-`` id (None: nothing kept)."""
        if not isinstance(kind, str) or kind not in KIND_NAMES:
            of = sorted({k for z, k, _ in self._rule_zones(edits) if z == rid})
            raise ToolError(
                f"{rid} is a zone of several kinds ({', '.join(of) or 'none'}): give its `kind` (the kind it has now) too. list_zones shows them."
            )
        hit = [p for z, k, p in self._rule_zones(edits, kind) if z == rid]
        if not hit:
            raise ToolError(f"zone {rid} of kind {kind} does not exist (it may be edited already); call list_zones(kind={kind!r}) to see them.")
        ring = [self.map.to_abs(x, y) for x, y in list(hit[0].exterior.coords)[:-1]]
        edits.zone_cuts[next_id("ZC", list(edits.zone_cuts))] = {"kind": kind, "polygon": ring}
        if not keep:
            return None
        zid = next_id("Z", list(edits.zones))
        edits.zones[zid] = {"kind": new_kind or kind, "polygon": [list(p) for p in ring]}
        return zid

    @staticmethod
    def _xy(name: str, p: object) -> list[float]:
        if not (isinstance(p, (list, tuple)) and len(p) == 2):
            raise ToolError(f"{name} must be [x, y] in absolute S-JTSK metres, got {p!r}")
        return pt(check_num(f"{name}[0]", p[0]), check_num(f"{name}[1]", p[1]))

    @staticmethod
    def _patch_entry(edits: Edits, pid: str) -> dict:
        if pid not in edits.ground_patches:
            raise ToolError(
                f"ground patch {pid} does not exist; patches: {sorted(edits.ground_patches) or 'none'}. "
                "Call list_ground_patches to see them."
            )
        return edits.ground_patches[pid]

    @staticmethod
    def _zone_entry(edits: Edits, zid: str) -> dict:
        if zid not in edits.zones:
            raise ToolError(
                f"zone {zid} does not exist; zones: {sorted(edits.zones) or 'none'}. Call list_zones to see them."
            )
        return edits.zones[zid]

    @staticmethod
    def _shape_row(sid: str, entry: dict) -> dict:
        """The row of a patch or a zone: its own keys (type, kind, water_y), the size and where it is."""
        poly = Polygon(polygon_ring(entry["polygon"]))
        c = poly.centroid
        return {
            "id": sid,
            **{k: entry[k] for k in ("type", "kind", "water_y", "max_depth") if k in entry},
            "area_m2": round(poly.area, 1),
            "points": len(entry["polygon"]),
            "bbox": [r3(v) for v in poly.bounds],
            "centroid": pt(c.x, c.y),
        }

    @staticmethod
    def _overlap_warnings(edits: Edits, pid: str) -> list[str]:
        mine = Polygon(polygon_ring(edits.ground_patches[pid]["polygon"]))
        if not mine.is_valid:
            return []
        out = []
        for oid, other in sorted(edits.ground_patches.items()):
            if oid == pid:
                continue
            op = Polygon(polygon_ring(other["polygon"]))
            if op.is_valid and mine.intersection(op).area > 0.01:
                winner = max(pid, oid, key=patch_rank)
                out.append(f"overlaps ground patch {oid}; where they overlap {winner} wins (higher ID)")
        return out

    def _points_of(self, polygon: object, parcel: object, points: object, noun: str) -> list[list[float]]:
        """The corners of a new patch or zone: an absolute polygon, or points in a parcel's frame made absolute."""
        if (polygon is None) == (points is None):
            raise ToolError(
                "give exactly one of polygon (absolute [x, y] points) or parcel + points "
                "(parcel-relative along/inward metres)"
            )
        if polygon is not None:
            if parcel is not None:
                raise ToolError("parcel only goes with points; polygon is absolute S-JTSK metres")
            if not isinstance(polygon, list):
                raise ToolError("polygon must be a list of [x, y] points")
            pts = [self._xy(f"polygon[{i}]", p) for i, p in enumerate(polygon)]
        else:
            if parcel is None:
                raise ToolError("points are relative to a parcel: pass parcel too (e.g. P-0001)")
            pid = self.map.need(parcel)
            if not isinstance(points, list):
                raise ToolError("points must be a list of [along, inward] metres")
            frame, _ = self.map.frame(pid)
            pts = []
            for i, q in enumerate(points):
                if isinstance(q, dict) and set(q) <= {"along", "inward"}:
                    q = [q.get("along", 0.0), q.get("inward", 0.0)]
                if not (isinstance(q, (list, tuple)) and len(q) == 2):
                    raise ToolError(f"points[{i}] must be [along, inward] or {{along, inward}}, got {q!r}")
                a = check_num(f"points[{i}].along", q[0])
                d = check_num(f"points[{i}].inward", q[1])
                pts.append(self.map.to_abs(*frame.at(a, d)))
        if len(pts) > 1 and pts[0] == pts[-1]:
            pts.pop()  # the file does not need the closing point
        if len(pts) < 3:
            raise ToolError(f"a {noun} needs at least 3 points, got {len(pts)}. Nothing was changed.")
        return pts

    def _move_corner(
        self,
        collection: str,
        sid: str,
        index: object,
        position: list[float] | None,
        offset: dict | None,
        kind: str | None = None,
    ) -> dict:
        if isinstance(index, bool) or not isinstance(index, int):
            raise ToolError(f"index must be an integer, got {index!r}")
        if (position is None) == (offset is None):
            raise ToolError('give exactly one of position [x, y] or offset {"dx": m, "dy": m}')
        if position is not None:
            target: list[float] | None = self._xy("position", position)
        else:
            if not isinstance(offset, dict) or set(offset) - {"dx", "dy"}:
                raise ToolError('offset must be {"dx": metres east, "dy": metres north}')
            dx = check_num("offset.dx", offset.get("dx", 0.0))
            dy = check_num("offset.dy", offset.get("dy", 0.0))
            target = None
        patches = collection == "ground_patches"

        def change(edits: Edits) -> dict:
            zid = sid
            if not patches and RULE_ZONE_ID.fullmatch(sid):  # comes from a rule: it becomes an ordinary zone first
                zid = self._detach(edits, sid, kind) or sid
            entry = self._patch_entry(edits, sid) if patches else self._zone_entry(edits, zid)
            poly = entry["polygon"]
            if not 0 <= index < len(poly):
                raise ToolError(
                    f"index {index} is out of range: {sid} has {len(poly)} points (0..{len(poly) - 1}). "
                    f"{'list_ground_patches' if patches else 'list_zones'} shows the point count."
                )
            old = poly[index]
            new = target if target is not None else pt(old[0] + dx, old[1] + dy)
            poly[index] = new
            return {"id": zid, "old": old, "new": new, "warnings": self._overlap_warnings(edits, sid) if patches else []}

        out = self._write(change)
        sid = out["id"]
        return {
            "summary": f"moved {sid} corner {index} {out['old']} -> {out['new']}",
            "id": sid,
            "index": index,
            "old": out["old"],
            "position": out["new"],
            "warnings": out["warnings"],
        }

    def _near_rows(self, rows: list[dict], entries: dict[str, dict], near: dict | None) -> list[dict]:
        """``rows`` kept within ``near`` (a point or a parcel and a radius), nearest first, with ``distance_m``."""
        if near is None:
            return rows
        if not isinstance(near, dict):
            raise ToolError('near must be {"x": .., "y": ..} or {"parcel": "P-0001", "radius_m": ..}')
        radius = check_num("near.radius_m", near.get("radius_m", 100.0), positive=True)
        if "parcel" in near:
            src: Any = shapely.affinity.translate(self.map.polys[self.map.need(near["parcel"])], *self.map.origin)
        elif "x" in near and "y" in near:
            src = Point(check_num("near.x", near["x"]), check_num("near.y", near["y"]))
        else:
            raise ToolError('near needs "x" and "y" (S-JTSK metres) or "parcel"')
        keep = []
        for row in rows:
            d = Polygon(polygon_ring(entries[row.get("_key", row["id"])]["polygon"])).distance(src)
            if d <= radius:
                keep.append({**row, "distance_m": round(d, 1)})
        return sorted(keep, key=lambda r: (r["distance_m"], r["id"]))
