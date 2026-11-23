"""Tools on placed objects (trees, cars, props, poles): place, move, remove, and where an object may stand."""

from __future__ import annotations

import math
import re
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import nearest_points

from drycastle_pipeline.edits.mcp.base import (
    all_objects,
    bullets,
    check_num,
    empty_parcel_cleanup,
    read_edits,
)
from drycastle_pipeline.edits.mcp.ids import next_id
from drycastle_pipeline.edits.mcp.mapdata import Frame
from drycastle_pipeline.edits.schema import Edits, dump_edits, validate
from drycastle_pipeline.edits.store import edits_doc, edits_lock

ANCHORS = ("front", "back", "left", "right", "centre", "near_building")
ANCHOR_MARGIN_M = 1.5  # anchors sit this far inside the parcel
INTERIOR_M = 0.3  # a moved point lands this far inside the boundary
NEAR_BUILDING_M = 2.0
OVERLAP_SEARCH_M = 25.0


def check_size_m(v: object) -> list[float] | None:
    """A tool's `size_m` argument: None or [length, width, height], three finite numbers > 0."""
    if v is None:
        return None
    if not isinstance(v, (list, tuple)) or len(v) != 3:
        raise ToolError("size_m must be [length, width, height] in metres")
    return [check_num(n, c, positive=True) for n, c in zip(("size_m length", "size_m width", "size_m height"), v)]


def facing_rotation(direction: tuple[float, float]) -> float:
    """``rotation_deg`` that turns an object's front (+Z) toward ``direction`` (map frame)."""
    phi = math.degrees(math.atan2(direction[1], direction[0]))
    return norm_deg(phi + 90.0)


def norm_deg(deg: float) -> float:
    deg = deg % 360.0
    return round(deg, 3) + 0.0


def object_polygon(
    xy: tuple[float, float],
    size_m: list[float] | None,
    rotation_deg: float,
    scale: float,
    fixed_m: list[float] | None = None,
) -> Polygon:
    """Approximate footprint of a placed object (local metres): its bbox about the origin.
    ``fixed_m`` = the object's own [length, width, height] (wins over the manifest size and ``scale``)."""
    sx, sz = (size_m[0], size_m[2]) if size_m else (1.0, 1.0)
    hx, hz = sx * scale / 2, sz * scale / 2
    if fixed_m:
        hx, hz = fixed_m[0] / 2, fixed_m[1] / 2
    th = math.radians(rotation_deg)
    c, s = math.cos(th), math.sin(th)
    pts = []
    for x, z in ((-hx, -hz), (hx, -hz), (hx, hz), (-hx, hz)):
        xr, zr = x * c + z * s, -x * s + z * c  # Godot rotation about Y
        pts.append((xy[0] + xr, xy[1] - zr))  # map y = -z
    return Polygon(pts)


def line_pieces(poly: Polygon, p0: tuple[float, float], d: tuple[float, float]) -> list[tuple]:
    """Parameter ranges (t0, t1) along p0 + t d (unit d) that lie inside ``poly``."""
    reach = 4 * (max(poly.bounds[2] - poly.bounds[0], poly.bounds[3] - poly.bounds[1]) + 10)
    line = LineString(
        [(p0[0] - reach * d[0], p0[1] - reach * d[1]), (p0[0] + reach * d[0], p0[1] + reach * d[1])]
    )
    hit = poly.intersection(line)
    parts = [hit] if hit.geom_type == "LineString" else list(getattr(hit, "geoms", []))
    out = []
    for g in parts:
        if g.geom_type != "LineString" or g.is_empty:
            continue
        ts = [(x - p0[0]) * d[0] + (y - p0[1]) * d[1] for x, y in g.coords]
        out.append((min(ts), max(ts)))
    return sorted(out)


def interior_point(poly: Polygon, x: float, y: float) -> tuple[float, float]:
    inner = poly.buffer(-INTERIOR_M)
    target = poly if inner.is_empty else inner
    p = nearest_points(target, Point(x, y))[0]
    return p.x, p.y


class ObjectTools:
    """place_object, move_object, remove_object."""

    def _place_point(
        self,
        pid: str | None,
        anchor: str | None,
        offset_m: dict | None,
        position: list[float] | None,
        warnings: list[str],
    ) -> tuple[float, float, Frame | None]:
        """Local position for an anchor or exact position; the frame is the parcel's (None without a parcel).

        An exact position needs no parcel. An anchor is a spot relative to a parcel's front edge, so it does.
        """
        m = self.map
        if position is not None:
            if anchor is not None or offset_m is not None:
                raise ToolError("give either position or anchor/offset_m, not both")
            if not (isinstance(position, (list, tuple)) and len(position) == 2):
                raise ToolError("position must be [x, y] in absolute S-JTSK metres")
            px = check_num("position[0]", position[0])
            py = check_num("position[1]", position[1])
            frame = None
            if pid is not None:
                frame, fw = m.frame(pid)
                warnings.extend(fw)
            return (*m.to_local(px, py), frame)
        if pid is None:
            raise ToolError("give an exact `position` [x, y] (S-JTSK metres), or a `parcel` for an anchor")
        poly = m.polys[pid]
        frame, fw = m.frame(pid)
        warnings.extend(fw)
        anchor = anchor or "centre"
        if anchor not in ANCHORS:
            raise ToolError(f"anchor must be one of {', '.join(ANCHORS)} (got {anchor!r})")
        along = inward = 0.0
        if offset_m is not None:
            if not isinstance(offset_m, dict) or set(offset_m) - {"along", "inward"}:
                raise ToolError('offset_m must be {"along": metres, "inward": metres}')
            along = check_num("offset_m.along", offset_m.get("along", 0.0))
            inward = check_num("offset_m.inward", offset_m.get("inward", 0.0))
        pa, pi = self._anchor_ai(pid, frame, anchor, warnings)
        x, y = frame.at(pa + along, pi + inward)
        if not poly.covers(Point(x, y)):
            nx, ny = interior_point(poly, x, y)
            warnings.append(
                f"anchor {anchor} with the offset falls outside {pid}; moved "
                f"{math.hypot(nx - x, ny - y):.1f} m to the nearest interior point"
            )
            x, y = nx, ny
        return x, y, frame

    def _anchor_ai(
        self, pid: str, frame: Frame, anchor: str, warnings: list[str]
    ) -> tuple[float, float]:
        """Anchor as (along, inward) in the parcel's front frame."""
        m = self.map
        poly = m.polys[pid]
        ext = [frame.to_ai(x, y) for x, y in poly.exterior.coords]
        depth = max(i for _, i in ext)
        margin = min(ANCHOR_MARGIN_M, max(depth, 0.0) / 4)
        if anchor == "front":
            return 0.0, margin
        if anchor == "back":
            pieces = line_pieces(poly, frame.origin, frame.inward)
            far = max((t1 for _, t1 in pieces), default=depth)
            return 0.0, max(far - margin, 0.0)
        if anchor in ("left", "right"):
            mid = frame.at(0.0, depth / 2)
            pieces = line_pieces(poly, mid, frame.right)
            if not pieces:
                warnings.append(f"{anchor} of {pid}: no parcel width at mid depth; used centre")
                return self._anchor_ai(pid, frame, "centre", warnings)
            lo, hi = pieces[0][0], pieces[-1][1]
            width_margin = min(ANCHOR_MARGIN_M, (hi - lo) / 4)
            return (lo + width_margin if anchor == "left" else hi - width_margin), depth / 2
        if anchor == "near_building":
            ids = [b for b in m.parcels[pid].get("buildings", []) if b in m.bpolys]
            if not ids:
                warnings.append(f"{pid} has no building; near_building used the centre")
                return self._anchor_ai(pid, frame, "centre", warnings)
            bp = max((m.bpolys[b] for b in ids), key=lambda g: g.area)
            c = bp.centroid
            dx, dy = frame.origin[0] - c.x, frame.origin[1] - c.y
            norm = math.hypot(dx, dy) or 1.0
            d = (dx / norm, dy / norm)  # building centre -> front edge
            pieces = line_pieces(bp, (c.x, c.y), d)
            exit_t = max((t1 for t0, t1 in pieces if t0 <= 0 <= t1), default=0.0)
            t = exit_t + NEAR_BUILDING_M
            return frame.to_ai(c.x + d[0] * t, c.y + d[1] * t)
        # centre
        p = poly.centroid if poly.covers(poly.centroid) else poly.representative_point()
        return frame.to_ai(p.x, p.y)

    def _placement_warnings(
        self,
        pid: str | None,
        xy: tuple[float, float],
        asset: dict,
        rotation: float,
        scale: float,
        edits: Edits,
        skip_id: str | None,
        fixed_m: list[float] | None = None,
    ) -> list[str]:
        m = self.map
        warnings: list[str] = []
        pt = Point(xy)
        if pid is not None and not m.polys[pid].covers(pt):  # only when a parcel was named as the reference
            inside = m.parcel_at(*xy)
            where = f"inside {inside}" if inside else "outside every parcel"
            warnings.append(f"position is outside {pid} ({where}); placed anyway as requested")
        fp = object_polygon(xy, asset.get("size_m"), rotation, scale, fixed_m)
        for bid in m.buildings_near(fp, 0.0):
            warnings.append(
                f"overlaps building {bid} (approximate footprint of {asset['id']} at this "
                "rotation and scale)"
            )
        for other in all_objects(edits):
            if other["id"] == skip_id:
                continue
            ox, oy = m.to_local(*other["position"])
            if math.hypot(ox - xy[0], oy - xy[1]) > OVERLAP_SEARCH_M:
                continue
            osz = next(
                (a.get("size_m") for a in self.manifest["assets"] if a["id"] == other["asset"]), None
            )
            ofp = object_polygon(
                (ox, oy), osz, float(other.get("rotation_deg", 0)), float(other.get("scale", 1)),
                other.get("size_m"),
            )
            if fp.intersects(ofp):
                warnings.append(f"overlaps placed object {other['id']} ({other['asset']})")
        size = asset.get("size_m") or [1, 1, 1]
        if max(size) < 1.0 and scale == 1.0 and not fixed_m:
            warnings.append(
                f"{asset['id']} is small at scale 1 (largest side {max(size):.2f} m); pass scale "
                "if it should be bigger"
            )
        return warnings

    def place_object(
        self,
        asset: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
        z: float | None = None,
        solid: bool | None = None,
        size_m: list[float] | None = None,
    ) -> dict:
        """Place an object (see the registered tool description). It belongs to no parcel."""
        pid = self.map.need(parcel) if parcel is not None else None
        info = self._asset(asset)
        if scale is not None:
            check_num("scale", scale, positive=True)
        fixed = check_size_m(size_m)
        if fixed is not None and scale is not None:
            raise ToolError("give size_m or scale, not both")
        if rotation_deg is not None:
            check_num("rotation_deg", rotation_deg)
        if colour is not None and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(colour)):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        if z is not None:
            check_num("z", z)
        if solid is not None and not isinstance(solid, bool):
            raise ToolError(f"solid must be true or false, got {solid!r}")
        warnings: list[str] = []
        x, y, frame = self._place_point(pid, anchor, offset_m, position, warnings)
        if rotation_deg is not None:
            rot = norm_deg(rotation_deg)
        else:
            rot = facing_rotation(frame.side.normal) if frame is not None else 0.0
        scl = float(scale) if scale is not None else 1.0
        absolute = self.map.to_abs(x, y)

        def change(edits: Edits) -> dict:
            oid = next_id("o", [o["id"] for o in all_objects(edits)])
            entry: dict = {"id": oid, "asset": asset, "position": absolute, "rotation_deg": rot}
            if scale is not None:
                entry["scale"] = scl
            if fixed is not None:
                entry["size_m"] = fixed
            if colour is not None:
                entry["colour"] = colour
            if z is not None:
                entry["z"] = float(z)
            if solid is False:  # true is the default: the key is only written when false
                entry["solid"] = False
            warnings.extend(self._placement_warnings(pid, (x, y), info, rot, scl, edits, None, fixed))
            edits.objects.append(entry)
            return entry

        entry = self._write(change)
        return {
            "summary": f"placed {entry['id']} ({asset}) at {absolute}, rotation {rot} deg"
            + (f", {len(warnings)} warning(s)" if warnings else ""),
            "id": entry["id"],
            "position": absolute,
            "rotation_deg": rot,
            "scale": scl,
            **({"size_m": fixed} if fixed is not None else {}),
            "entry": entry,
            "warnings": warnings,
        }

    def remove_object(self, id: str) -> dict:
        """Remove a placed object by ID; the cables hanging from it (a utility pole) go with it."""
        dropped: list[str] = []

        def change(edits: Edits) -> None:
            for o in edits.objects:
                if o["id"] == id:
                    edits.objects.remove(o)
                    for cid in [c for c, cb in edits.cables.items() if id in (cb["from"], cb["to"])]:
                        dropped.append(cid)
                        del edits.cables[cid]
                    return
            known = [o["id"] for o in all_objects(edits)]
            raise ToolError(
                f"no placed object {id!r}. Existing ids: {known[:20] if known else 'none'}. "
                "get_parcel lists the objects inside a parcel."
            )

        self._write(change)
        note = f" and its cable{'s' if len(dropped) > 1 else ''} {', '.join(dropped)}" if dropped else ""
        return {"summary": f"removed {id}{note}", "id": id, "cables_removed": dropped, "warnings": []}

    def move_object(
        self,
        id: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
        z: float | None = None,
        clear_z: bool = False,
        solid: bool | None = None,
        size_m: list[float] | None = None,
    ) -> dict:
        """Change position, rotation, scale, size_m, colour, z or solid of a placed object (see tool description)."""
        if scale is not None:
            check_num("scale", scale, positive=True)
        fixed = check_size_m(size_m)
        if fixed is not None and scale is not None:
            raise ToolError("give size_m or scale, not both")
        if rotation_deg is not None:
            check_num("rotation_deg", rotation_deg)
        if colour is not None and not re.fullmatch(r"#[0-9a-fA-F]{6}", str(colour)):
            raise ToolError(f'colour must look like "#rrggbb" (got {colour!r})')
        if z is not None:
            check_num("z", z)
            if clear_z:
                raise ToolError("give z or clear_z, not both")
        if solid is not None and not isinstance(solid, bool):
            raise ToolError(f"solid must be true or false, got {solid!r}")
        if not clear_z and all(v is None for v in (parcel, anchor, offset_m, position, rotation_deg, scale, fixed, colour, z, solid)):
            raise ToolError(
                "nothing to change: give a new anchor/offset_m/position/rotation_deg/scale/size_m/colour/z/clear_z/solid"
            )
        relocate = any(v is not None for v in (parcel, anchor, offset_m, position))
        warnings: list[str] = []
        path = self.settings.edits_path
        with edits_lock(path):
            edits = read_edits(path)
            obj = next((o for o in all_objects(edits) if o["id"] == id), None)
            if obj is None:
                raise ToolError(f"no placed object {id!r}; get_parcel lists the objects inside a parcel.")
            # a parcel is only the frame an anchor is measured in; the object keeps belonging to none
            target = self.map.need(parcel) if parcel else None
            new = dict(obj)
            if relocate:
                if position is None and anchor is None and offset_m is not None:
                    anchor = "centre"
                if position is None and target is None:  # an anchor, no parcel named: the one it stands in
                    target = self.map.parcel_at(*self.map.to_local(*obj["position"]))
                x, y, frame = self._place_point(target, anchor, offset_m, position, warnings)
                new["position"] = self.map.to_abs(x, y)
                if rotation_deg is None and frame is not None and (anchor is not None or parcel is not None):
                    new["rotation_deg"] = facing_rotation(frame.side.normal)
            if rotation_deg is not None:
                new["rotation_deg"] = norm_deg(rotation_deg)
            if scale is not None:
                new["scale"] = float(scale)
                new.pop("size_m", None)  # size_m would win over the new scale
            if fixed is not None:
                new["size_m"] = fixed
                new.pop("scale", None)
            if colour is not None:
                new["colour"] = colour
            if z is not None:
                new["z"] = float(z)
            elif clear_z:
                new.pop("z", None)  # back on the terrain
            if solid is True:
                new.pop("solid", None)  # the default
            elif solid is False:
                new["solid"] = False
            info = self._asset(new["asset"])
            lx, ly = self.map.to_local(*new["position"])
            warnings.extend(
                self._placement_warnings(
                    target, (lx, ly), info, float(new.get("rotation_deg", 0)),
                    float(new.get("scale", 1)), edits, id, new.get("size_m"),
                )
            )
            edits.objects[edits.objects.index(obj)] = new
            empty_parcel_cleanup(edits)
            errors = validate(edits_doc(edits))
            if errors:
                raise ToolError(
                    "refused: the change would make edits.json invalid, nothing was written:\n"
                    + bullets(errors)
                )
            dump_edits(edits, path)
        return {
            "summary": f"moved {id} to {new['position']}, rotation {new.get('rotation_deg', 0)} deg"
            + (f", {len(warnings)} warning(s)" if warnings else ""),
            "id": id,
            "entry": new,
            "warnings": warnings,
        }


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "Place an asset (see list_assets). An object belongs to no parcel, it only has a position: "
        "give an exact `position` [x, y] in S-JTSK metres (nothing else is needed, so a pole by a "
        "road is fine), or name a `parcel` and an `anchor` = front | back "
        "| left | right | centre | near_building (default centre) plus optional `offset_m` "
        "{\"along\": m, \"inward\": m}. Anchors "
        "are relative to the parcel's front edge (the side nearest a road; see get_parcel): "
        "left/right as seen standing on the front edge looking into the parcel, along > 0 = "
        "further right, inward > 0 = further from the road. `near_building` is between the "
        "building and the front edge. An anchor-based point is kept inside the parcel (moved to "
        "the nearest interior point with a warning). Default rotation, with a parcel, turns the "
        "object's front toward the road (right for a car parked in front of the house), else 0; "
        "`rotation_deg` is "
        "counter-clockwise seen from above, absolute. `scale` is uniform, default 1 (small pack "
        "models may need it); `size_m` [length, width, height] instead sizes the object to an exact bounding box in "
        "metres (length along the object's own x, east at rotation 0; width along its own map y, north at rotation 0; "
        "height up), scaling the model non-uniformly (e.g. prim:slab as a 1.52 x 2.66 x 0.12 m grave slab); "
        "give `size_m` or `scale`, not both; `colour` is \"#rrggbb\". `z` (optional, metres, absolute above the DMR datum, not "
        "relative to the ground) fixes the height instead of standing on the terrain: set it to lay an object on "
        "a prim:mound tier or a roof. `solid` (default true) false = no collision at all, players, vehicles and "
        "shots pass through: for dressing (trash) laid over a climbable prim. Unknown asset/parcel and bad values are "
        "errors; overlaps with buildings or other objects and positions outside a named parcel are "
        "applied and reported in `warnings`. Returns the new object's id, final position, rotation."))
    def place_object(
        asset: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
        z: float | None = None,
        solid: bool | None = None,
        size_m: list[float] | None = None,
    ) -> dict:
        return tools.place_object(
            asset, parcel, anchor, offset_m, position, rotation_deg, scale, colour, z, solid, size_m
        )

    @server.tool(description="Remove a placed object by its id (e.g. o-0003). The cables hanging from a "
                 "removed utility pole are removed with it.")
    def remove_object(id: str) -> dict:
        return tools.remove_object(id)

    @server.tool(description=(
        "Change a placed object: new position (an exact `position` [x, y], or `anchor` + `offset_m` "
        "relative to `parcel`, default the parcel the object stands in), `rotation_deg`, `scale`, "
        "`size_m` [length, width, height] exact metres (as place_object; setting one of scale/size_m drops the other), "
        "`colour`, `z` (absolute metres above the DMR datum) or `clear_z` true (back on the terrain; a missing "
        "z cannot say \"clear\"), `solid` (false = no collision at all, true = the default). With a new anchor and no rotation_deg the object turns to face the road "
        "again. An object belongs to no parcel; `parcel` is only the frame an anchor is measured in."))
    def move_object(
        id: str,
        parcel: str | None = None,
        anchor: str | None = None,
        offset_m: dict | None = None,
        position: list[float] | None = None,
        rotation_deg: float | None = None,
        scale: float | None = None,
        colour: str | None = None,
        z: float | None = None,
        clear_z: bool = False,
        solid: bool | None = None,
        size_m: list[float] | None = None,
    ) -> dict:
        return tools.move_object(
            id, parcel, anchor, offset_m, position, rotation_deg, scale, colour, z, clear_z, solid, size_m
        )
