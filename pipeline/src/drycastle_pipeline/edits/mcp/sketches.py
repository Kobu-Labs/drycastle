"""Tools on the sketch file (the viewer's Sketch tool): read it, draw roads, points and lines into it."""

from __future__ import annotations

import json
import time
from collections.abc import Callable
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError
from shapely.geometry import LineString, Point, Polygon

from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits import sketchfile
from drycastle_pipeline.edits.mcp.base import check_num
from drycastle_pipeline.edits.mcp.mapdata import ROAD_FAR_M, pt
from drycastle_pipeline.edits.mcp.settings import STATE_STALE_S
from drycastle_pipeline.roads.roads import edge_lines

SKETCH_PARCELS_MAX = 30  # parcels listed per sketch line, largest overlap first


class SketchTools:
    """The sketch file: get_sketch, create_sketch, sketch_add_*, sketch_remove."""

    def _sketch_doc(self, guide: str) -> tuple[dict, float, list[str]]:
        """The sketch document (sketch.json; the sketch of an older viewer's editor-state.json as DEFAULT), its age and warnings."""
        path = self.settings.sketch_file
        try:
            doc = sketchfile.read(path)
        except sketchfile.SketchFileError as exc:
            raise ToolError(f"{exc}. {guide}") from exc
        if doc is not None:
            age = max(0.0, time.time() - path.stat().st_mtime)
            warnings = []
            if age > STATE_STALE_S:
                warnings.append(
                    f"the sketch file is {age / 60:.0f} min old; the viewer may no longer be running, "
                    "so the sketch there could differ"
                )
            return doc, age, warnings
        state, age, warnings = self._state_doc(guide, "sketch")
        if not isinstance(state.get("sketch"), dict):
            raise ToolError(f"no sketch file at {path}. {guide}")
        try:
            return sketchfile.normalise(state["sketch"]), age, warnings
        except sketchfile.SketchFileError as exc:
            raise ToolError(f"the sketch in {self.settings.state_path} is unreadable ({exc}). {guide}") from exc

    def _sketch_change(self, change: Callable[[dict], Any]) -> Any:
        """Reload the sketch file under its lock, apply ``change`` to the document, write it (the viewer picks it up as one undo step)."""
        path = self.settings.sketch_file
        with sketchfile.lock(path):
            try:
                doc = sketchfile.read(path)
            except sketchfile.SketchFileError as exc:
                raise ToolError(str(exc)) from exc
            if doc is None:
                doc = sketchfile.empty_doc()
                try:  # an older viewer's sketch in editor-state.json becomes the DEFAULT sketch
                    state = json.loads(self.settings.state_path.read_text())
                    if isinstance(state.get("sketch"), dict):
                        doc = sketchfile.normalise(state["sketch"])
                except (OSError, ValueError, sketchfile.SketchFileError):
                    pass
            result = change(doc)
            sketchfile.tidy(doc)
            sketchfile.write(path, doc)
        return result

    def _sketch_name(self, doc: dict, sketch: str | None, create: bool) -> str:
        """The sketch a mark goes to: ``sketch`` (made when new), else the only one, else DEFAULT when there is none."""
        if sketch is None:
            if len(doc["sketches"]) == 1:
                return doc["sketches"][0]
            if not doc["sketches"]:
                sketch = sketchfile.DEFAULT_SKETCH
            else:
                raise ToolError(f"name the sketch: there are several ({', '.join(doc['sketches'])})")
        if not isinstance(sketch, str) or not sketchfile.SKETCH_NAME.fullmatch(sketch):
            raise ToolError(f"{sketch!r} is not a sketch name: 1 to 24 letters, digits, _ or -")
        known = sketchfile.sketch_of(doc, sketch)
        if known:
            return known
        if not create:
            raise ToolError(f"there is no sketch {sketch!r}; it has {', '.join(doc['sketches']) or 'none'}")
        doc["sketches"].append(sketch)
        return sketch

    def _sketch_corners(self, points: object) -> list[list[float]]:
        if not isinstance(points, list) or len(points) < 2:
            raise ToolError("`points` needs at least 2 [x, y] corners (absolute S-JTSK metres)")
        out = []
        for p in points:
            if not (isinstance(p, (list, tuple)) and len(p) == 2):
                raise ToolError(f"corner {p!r} is not [x, y]")
            out.append(pt(check_num("x", p[0]), check_num("y", p[1])))
        return out

    def create_sketch(self, name: str) -> dict:
        """Create an empty named sketch (see the registered tool description)."""
        def change(doc: dict) -> dict:
            if isinstance(name, str) and sketchfile.sketch_of(doc, name):
                raise ToolError(f"sketch {name!r} exists already")
            if not isinstance(name, str):
                raise ToolError("`name` is the new sketch's name, e.g. ROAD")
            return {"sketch": self._sketch_name(doc, name, True)}

        return self._sketch_change(change)

    def sketch_add_point(self, x: float, y: float, sketch: str | None = None) -> dict:
        """Mark a point in a sketch (see the registered tool description)."""
        px, py = r3(check_num("x", x)), r3(check_num("y", y))

        def change(doc: dict) -> dict:
            name = self._sketch_name(doc, sketch, True)
            pid = sketchfile.new_point_id(doc)
            doc["points"].append({"id": pid, "x": px, "y": py, "sketch": name})
            return {"id": pid, "sketch": name, "position": pt(px, py)}

        return self._sketch_change(change)

    def sketch_add_line(self, points: list[list[float]], closed: bool = False, sketch: str | None = None) -> dict:
        """Draw a line in a sketch (see the registered tool description)."""
        pts = self._sketch_corners(points)
        if closed:
            if len(pts) > 1 and pts[0] == pts[-1]:
                pts.pop()
            if len(pts) < 3:
                raise ToolError("a closed line needs at least 3 corners")
            if not Polygon(pts).is_valid:
                raise ToolError("a closed line must not cross itself")

        def change(doc: dict) -> dict:
            name = self._sketch_name(doc, sketch, True)
            lid = sketchfile.new_line_id(doc)
            doc["lines"].append({"id": lid, "closed": bool(closed), "points": pts, "sketch": name})
            return {"id": lid, "sketch": name, "closed": bool(closed), "corners": len(pts)}

        return self._sketch_change(change)

    def _road_graph(self) -> dict:
        self.map  # noqa: B018  (loads and checks the work files)
        return json.loads((self.settings.work / "roads.json").read_text())

    @staticmethod
    def _road_line(edges: list[dict]) -> list[list[tuple[float, float]]]:
        """The polylines of some edges chained end to end (an OSM way is cut into edges at its junctions)."""
        pieces = [[tuple(p) for p in e["polyline"]] for e in edges]
        chains: list[list[tuple[float, float]]] = []
        while pieces:
            chain = pieces.pop(0)
            grown = True
            while grown:
                grown = False
                for i, piece in enumerate(pieces):
                    if piece[0] == chain[-1]:
                        chain += piece[1:]
                    elif piece[-1] == chain[-1]:
                        chain += piece[::-1][1:]
                    elif piece[-1] == chain[0]:
                        chain = piece[:-1] + chain
                    elif piece[0] == chain[0]:
                        chain = piece[::-1][:-1] + chain
                    else:
                        continue
                    pieces.pop(i)
                    grown = True
                    break
            chains.append(chain)
        return chains

    def sketch_add_road(self, road: str | None = None, near: Any = None, sketch: str | None = None) -> dict:
        """Draw a road of the map as a line in a sketch (see the registered tool description)."""
        graph = self._road_graph()
        edges = graph["edges"]
        if (road is None) == (near is None):
            raise ToolError("give exactly one of `road` (an edge id like E-0130 or an OSM way like w84718108) or `near` ([x, y] or a point of the sketch)")
        if road is not None:
            hit = [e for e in edges if road in (e["id"], e.get("osm_id"))]
            if not hit:
                raise ToolError(f"no road {road!r}; use an edge id (E-0130) or an osm_id (w84718108) from roads.json")
            # an edge id draws that edge only, an OSM way all its edges
            chosen = hit if road != hit[0]["id"] else hit[:1]
        else:
            if isinstance(near, str):
                doc, _age, _w = self._sketch_doc("Draw or ask for the point first.")
                pts = [p for p in doc["points"] if p["id"] == near]
                if not pts:
                    raise ToolError(f"the sketch has no point {near!r}")
                nx, ny = pts[0]["x"], pts[0]["y"]
            elif isinstance(near, (list, tuple)) and len(near) == 2:
                nx, ny = check_num("x", near[0]), check_num("y", near[1])
            else:
                raise ToolError("`near` is [x, y] (absolute S-JTSK metres) or the id of a sketch point such as A")
            here = Point(*self.map.to_local(nx, ny))
            best = min(edge_lines(graph), key=lambda el: el[1].distance(here), default=None)
            if best is None or best[1].distance(here) > ROAD_FAR_M:
                raise ToolError(f"no road within {ROAD_FAR_M:.0f} m of that spot")
            chosen = [best[0]]
        info = {k: chosen[0].get(k) for k in ("id", "osm_id", "highway", "surface", "width", "name")}
        chains = self._road_line(chosen)
        lines = [[self.map.to_abs(x, y) for x, y in chain] for chain in chains]

        def change(doc: dict) -> dict:
            name = self._sketch_name(doc, sketch, True)
            ids = []
            for corners in lines:
                lid = sketchfile.new_line_id(doc)
                doc["lines"].append({"id": lid, "closed": False, "points": corners, "sketch": name})
                ids.append(lid)
            return {"sketch": name, "lines": ids, "road": info, "length_m": round(sum(e["length_m"] for e in chosen), 1)}

        return self._sketch_change(change)

    def sketch_remove(self, target: str) -> dict:
        """Remove one mark, or a whole sketch with its marks (see the registered tool description)."""
        def change(doc: dict) -> dict:
            if any(p["id"] == target for p in doc["points"]) or any(ln["id"] == target for ln in doc["lines"]):
                doc["points"] = [p for p in doc["points"] if p["id"] != target]
                doc["lines"] = [ln for ln in doc["lines"] if ln["id"] != target]
                return {"removed": target}
            name = sketchfile.sketch_of(doc, target) if isinstance(target, str) else None
            if name is None:
                raise ToolError(f"the sketch has no mark or sketch {target!r}; marks: "
                                f"{', '.join(m['id'] for m in doc['points'] + doc['lines']) or 'none'}; sketches: "
                                f"{', '.join(doc['sketches']) or 'none'}")
            gone = [m["id"] for m in doc["points"] + doc["lines"] if m["sketch"] == name]
            doc["points"] = [p for p in doc["points"] if p["sketch"] != name]
            doc["lines"] = [ln for ln in doc["lines"] if ln["sketch"] != name]
            doc["sketches"].remove(name)
            return {"removed_sketch": name, "removed_marks": gone}

        return self._sketch_change(change)

    def get_sketch(self, id: str | None = None) -> dict:
        """The marks the user drew in the viewer's Sketch tool (see the registered tool description)."""
        m = self.map
        path = self.settings.sketch_file
        guide = (
            "Start the viewer in editor mode (godot --path godot/viewer -- --editor ...), open the "
            "Sketch tool (9), mark points (P) or draw lines (L) on the ground, or draw them with "
            "sketch_add_point / sketch_add_line / sketch_add_road, then try again."
        )
        doc, age, warnings = self._sketch_doc(guide)
        sketches = [
            {
                "name": n,
                "points": sum(p["sketch"] == n for p in doc["points"]),
                "lines": sum(ln["sketch"] == n for ln in doc["lines"]),
            }
            for n in doc["sketches"]
        ]
        points = [(p["id"], p["x"], p["y"], p["sketch"]) for p in doc["points"]]
        lines = [(ln["id"], ln["closed"], [tuple(c) for c in ln["points"]], ln["sketch"]) for ln in doc["lines"]]
        names = [p[0] for p in points] + [ln[0] for ln in lines]
        if not names and not sketches:
            raise ToolError(f"nothing is sketched (sketch file {path.name}). {guide}")
        if id is not None:
            whole = sketchfile.sketch_of(doc, id) if id not in names else None
            if whole is not None:
                points = [p for p in points if p[3] == whole]
                lines = [ln for ln in lines if ln[3] == whole]
            elif id in names:
                points = [p for p in points if p[0] == id]
                lines = [ln for ln in lines if ln[0] == id]
            else:
                raise ToolError(
                    f"the sketch has no {id!r}; marks: {', '.join(names) or 'none'}; "
                    f"sketches: {', '.join(doc['sketches']) or 'none'}"
                )

        on_map = False
        point_rows = []
        for pid, x, y, psketch in points:
            parcel = m.parcel_at(*m.to_local(x, y))
            on_map = on_map or parcel is not None
            point_rows.append({
                "id": pid,
                "sketch": psketch,
                "position": pt(x, y),
                "parcel": parcel,
                "kind": m.parcels[parcel]["kind"] if parcel else None,
            })
        line_rows = []
        for lid, closed, pts, lsketch in lines:
            local = [m.to_local(x, y) for x, y in pts]
            closed = closed and len(local) >= 3
            if len(local) < 2:
                warnings.append(f"{lid} has fewer than 2 corners and is left out")
                continue
            geom: Any = Polygon(local) if closed else LineString(local)
            row: dict = {
                "id": lid,
                "sketch": lsketch,
                "closed": closed,
                "points": [pt(x, y) for x, y in pts],
                "length_m": round(geom.length, 1),
                "bbox": [r3(v) for v in LineString(pts).bounds],
            }
            if closed:
                row["area_m2"] = round(geom.area, 1)
            if closed and not geom.is_valid:
                warnings.append(f"{lid} crosses itself, so its area and the parcels it covers are not reliable")
                row["parcels"] = []
                line_rows.append(row)
                continue
            if closed:
                c = geom.centroid
                row["centroid"] = m.to_abs(c.x, c.y)
            # what the mark lies on: the part of each parcel inside a closed line, the length of a line on each parcel
            key = "area_m2" if closed else "length_m"
            hits = []
            for parcel in m.parcels_near(geom, 0.0):
                part = geom.intersection(m.polys[parcel])
                size = part.area if closed else part.length
                if size <= 1e-6:
                    continue
                hit = {"id": parcel, "kind": m.parcels[parcel]["kind"], key: round(size, 1)}
                if closed:
                    hit["share_of_parcel"] = round(part.area / m.polys[parcel].area, 3)
                hits.append(hit)
            hits.sort(key=lambda h: (-h[key], h["id"]))
            on_map = on_map or bool(hits)
            row["parcels"] = hits[:SKETCH_PARCELS_MAX]
            if len(hits) > SKETCH_PARCELS_MAX:
                row["parcels_total"] = len(hits)
            line_rows.append(row)
        if not on_map:
            warnings.append(
                "no mark lies on a parcel of this map: the sketch may come from an older map; "
                "check the --state option"
            )
        out: dict = {
            "count": len(point_rows) + len(line_rows),
            "sketches": sketches,
            "points": point_rows,
            "lines": line_rows,
            "state_age_s": round(age, 1),
        }
        if warnings:
            out["warnings"] = warnings
        return out

    def _sketch_line(self, line: str) -> tuple[list[list[float]], bool]:
        """The points of a sketch line (its id, as get_sketch shows it) and whether it is closed."""
        from drycastle_pipeline.edits import sketchfile

        path = self.settings.sketch_path or sketchfile.default_path()
        doc = json.loads(path.read_text()) if path.is_file() else {}
        for entry in doc.get("lines", []):
            if entry.get("id") == line:
                return [[float(x), float(y)] for x, y in entry["points"]], bool(entry.get("closed"))
        raise ToolError(f"no sketch line {line!r}; see get_sketch")


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "The temporary marks the user drew on the ground in the viewer's Sketch tool, for "
        "requests like 'plant trees in this area', 'a fence along L1', 'put a lamp at A' or just "
        "'here' / 'this line'. A sketch holds points (named A, B, C, ...) and lines (L1, L2, ...; "
        "`closed: true` joins the last corner to the first and means the ground inside). Returns, in absolute "
        "S-JTSK metres ready for place_object `position`, create_ground_patch or create_zone `polygon` or a fence gap "
        "`at`: each point's position and the parcel it is on; each line's corners in drawing "
        "order, length_m, bbox, and for a closed line area_m2 and centroid; plus the parcels a line "
        "runs over (length_m on each) or a closed line covers (area_m2 and share_of_parcel). Optional "
        "`id` returns one mark, or all marks of one sketch when it is a sketch name. Marks are grouped in named "
        "sketches (`sketches` lists them; every mark has its `sketch`). The user draws in the viewer and you draw "
        "with sketch_add_*; the sketch lives in sketch.json and is never part of edits.json."))
    def get_sketch(id: str | None = None) -> dict:
        return tools.get_sketch(id)

    @server.tool(description=(
        "Create an empty named sketch (e.g. ROAD, FENCE_PLAN): a group of marks the user can see and name in the viewer. "
        "1 to 24 letters, digits, _ or -; names match ignoring case. The other sketch_add_* tools create the sketch "
        "they name when it is new, so this is only needed for an empty one."))
    def create_sketch(name: str) -> dict:
        return tools.create_sketch(name)

    @server.tool(description=(
        "Mark a point on the ground in a sketch, in absolute S-JTSK metres (x east, y north). It appears in the "
        "viewer within a frame, as one undo step, named A, B, ... (unique across all sketches). `sketch` is the "
        "sketch's name (made when new); optional when there is at most one sketch."))
    def sketch_add_point(x: float, y: float, sketch: str | None = None) -> dict:
        return tools.sketch_add_point(x, y, sketch)

    @server.tool(description=(
        "Draw a line on the ground in a sketch: `points` is [[x, y], ...] in absolute S-JTSK metres (at least 2). "
        "`closed: true` joins the last corner to the first and means the ground inside (3+ corners, must not "
        "cross itself). Named L1, L2, ... (unique across all sketches). `sketch` as for sketch_add_point."))
    def sketch_add_line(points: list[list[float]], closed: bool = False, sketch: str | None = None) -> dict:
        return tools.sketch_add_line(points, closed, sketch)

    @server.tool(description=(
        "Highlight a road of the map by drawing its centreline as a line in a sketch (\"highlight this road under "
        "sketch ROAD\"). Give exactly one of: `road` (an edge id such as E-0130 draws that edge; an osm_id such as "
        "w84718108 draws the whole OSM way) or `near` ([x, y] in absolute S-JTSK metres, or the id of a sketch point "
        "such as A: the nearest road within 30 m). Returns the new line ids and the road's highway type, surface, "
        "width and length. `sketch` as for sketch_add_point."))
    def sketch_add_road(road: str | None = None, near: list[float] | str | None = None, sketch: str | None = None) -> dict:
        return tools.sketch_add_road(road, near, sketch)

    @server.tool(description=(
        "Remove one mark by id (A, L2), or a whole sketch by name with all its marks. The viewer's undo "
        "(Ctrl+Z) brings it back. When no marks are left the names start again at A and L1."))
    def sketch_remove(target: str) -> dict:
        return tools.sketch_remove(target)
