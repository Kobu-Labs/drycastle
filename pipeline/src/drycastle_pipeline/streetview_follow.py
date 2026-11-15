"""Follow a Google Street View tab live: where you stand and look, for the viewer's character.

Reference only, like ``streetview.py``: nothing is read from the imagery, only the address of the tab
(``.../@48.40,16.86,146a,75y,97h,...``), which holds where you stand and where you look. Every time it
changes the position is written to ``out/streetview-position.json``; the viewer (``--editor``) watches that
file, puts the character there and, in the top-down view (T), draws a red point under it
(``godot/viewer/StreetViewFollow.cs``). No model and no MCP server is involved.

Points (only with ``--points``): when you stop and look at something (the view stays put for ``--dwell``
seconds), a permanent point is dropped in the sketch ``POLES`` of ``out/sketch.json``, at the spot you are
looking at (``--ahead`` metres along the view; ``--at camera`` puts it where you stand instead). Place the poles
on them later (the editor's Sketch tool shows them, Claude reads them with get_sketch).

With ``--sketch`` the position is also kept as one moving point and a short view line in the sketch
``STREETVIEW`` (each move is then one undo step in the editor, so it is off by default).

Needs a Chrome with its debugging port open (the script can start one with its own throw-away profile):

    cd pipeline
    uv run python -m drycastle_pipeline.streetview_follow --launch

Then open Street View in the window that appeared and walk. Without ``--launch`` it attaches to a Chrome
that was started with ``--remote-debugging-port=9222``.
"""

import argparse
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

from pyproj import Transformer

from drycastle_pipeline import sketchfile

SKETCH_NAME = "STREETVIEW"
POINTS_SKETCH = "POLES"
OUT_DIR = Path(__file__).resolve().parents[3] / "out"
DEFAULT_SKETCH_FILE = OUT_DIR / "sketch.json"
DEFAULT_POSITION_FILE = OUT_DIR / "streetview-position.json"
POSITION_FORMAT_VERSION = 1
VIEW_LINE_M = 8.0
_AT = re.compile(r"/@(-?\d+\.\d+),(-?\d+\.\d+)((?:,[^,/]+)*)")
_HEADING = re.compile(r"^(-?\d+(?:\.\d+)?)h$")
_TO_JTSK = Transformer.from_crs(4326, 5514, always_xy=True)


def parse_maps_url(url: str) -> tuple[float, float, float | None] | None:
    """(lat, lon, heading in degrees clockwise from north or None) of a Google Maps address, else None."""
    if "google." not in url or "/maps" not in url:
        return None
    m = _AT.search(url)
    if not m:
        return None
    heading = None
    for part in m.group(3).split(","):
        h = _HEADING.match(part)
        if h:
            heading = float(h.group(1))
    return float(m.group(1)), float(m.group(2)), heading


def to_sketch(
    lat: float, lon: float, heading: float | None, ahead_m: float = VIEW_LINE_M
) -> tuple[list[float], list[list[float]] | None]:
    """The S-JTSK point and, when the heading is known, the two ends of the view line.

    The view line is made on the globe (``ahead_m`` metres along the bearing) and projected, so it points where
    the camera does whatever the grid convergence of the projection.
    """
    x, y = _TO_JTSK.transform(lon, lat)
    here = [round(x, 3), round(y, 3)]
    if heading is None:
        return here, None
    brg = math.radians(heading)
    dlat = ahead_m * math.cos(brg) / 111_320.0
    dlon = ahead_m * math.sin(brg) / (111_320.0 * math.cos(math.radians(lat)))
    fx, fy = _TO_JTSK.transform(lon + dlon, lat + dlat)
    return here, [here, [round(fx, 3), round(fy, 3)]]


def position_doc(lat: float, lon: float, heading: float | None, seq: int) -> dict:
    """What the viewer reads: the S-JTSK position and the view direction as a heading on the map grid.

    ``heading_grid`` is degrees clockwise from the grid's north (the viewer's -z); it differs a little from the
    compass ``heading`` of the address because the projection is turned against true north. ``seq`` goes up with
    every write (milliseconds since the epoch, so it also does across runs).
    """
    point, view = to_sketch(lat, lon, heading)
    grid = None
    if view is not None:
        grid = round(math.degrees(math.atan2(view[1][0] - view[0][0], view[1][1] - view[0][1])) % 360.0, 2)
    return {
        "format_version": POSITION_FORMAT_VERSION,
        "seq": seq,
        "lat": lat,
        "lon": lon,
        "heading": heading,
        "x": point[0],
        "y": point[1],
        "heading_grid": grid,
    }


def write_position(path: Path, doc: dict) -> None:
    """Write the position file atomically (the viewer reads it while we write)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(doc, sort_keys=True, separators=(",", ":")) + "\n")
    os.replace(tmp, path)


def update_sketch(path: Path, point: list[float], view: list[list[float]] | None) -> None:
    """Move the one point (and the view line) of the sketch ``STREETVIEW``, making them when missing."""
    with sketchfile.lock(path):
        try:
            doc = sketchfile.read(path) or sketchfile.empty_doc()
        except sketchfile.SketchFileError:
            doc = sketchfile.empty_doc()
        name = sketchfile.sketch_of(doc, SKETCH_NAME)
        if name is None:
            name = SKETCH_NAME
            doc["sketches"].append(name)
        mine = [p for p in doc["points"] if p["sketch"] == name]
        if mine:
            mine[0]["x"], mine[0]["y"] = point
        else:
            doc["points"].append({"id": sketchfile.new_point_id(doc), "x": point[0], "y": point[1], "sketch": name})
        lines = [ln for ln in doc["lines"] if ln["sketch"] == name]
        if view is None:
            doc["lines"] = [ln for ln in doc["lines"] if ln["sketch"] != name]
        elif lines:
            lines[0]["points"] = view
        else:
            doc["lines"].append({"id": sketchfile.new_line_id(doc), "closed": False, "points": view, "sketch": name})
        sketchfile.write(path, doc)


def add_point(path: Path, point: list[float], sketch: str = POINTS_SKETCH) -> str:
    """Append a permanent point to ``sketch`` (made when new) of the sketch file; its id (A, B, ...)."""
    with sketchfile.lock(path):
        try:
            doc = sketchfile.read(path) or sketchfile.empty_doc()
        except sketchfile.SketchFileError:
            doc = sketchfile.empty_doc()
        name = sketchfile.sketch_of(doc, sketch)
        if name is None:
            name = sketch
            doc["sketches"].append(name)
        pid = sketchfile.new_point_id(doc)
        doc["points"].append({"id": pid, "x": point[0], "y": point[1], "sketch": name})
        sketchfile.write(path, doc)
    return pid


class PointDropper:
    """Decides when a view is a point: it has stayed put for ``dwell_s`` and is not where the last point went.

    ``feed`` is called every poll with the current address of the tab (parsed) and the time; it returns the S-JTSK
    point to drop, or None. A view that changes starts the wait again, so walking or turning drops nothing; stopping
    and looking at a pole does. ``at`` is "aim" (``ahead_m`` along the view, needs a heading) or "camera".
    """

    def __init__(self, dwell_s: float = 2.0, min_gap_m: float = 1.5, at: str = "aim", ahead_m: float = VIEW_LINE_M) -> None:
        self.dwell_s, self.min_gap_m, self.at, self.ahead_m = dwell_s, min_gap_m, at, ahead_m
        self._cur: tuple[float, float, float | None] | None = None
        self._since = 0.0
        self._done = False
        self._last: list[float] | None = None

    def feed(self, parsed: tuple[float, float, float | None] | None, now: float) -> list[float] | None:
        if parsed != self._cur:
            self._cur, self._since, self._done = parsed, now, False
            return None
        if parsed is None or self._done or now - self._since < self.dwell_s:
            return None
        self._done = True
        lat, lon, heading = parsed
        if self.at == "aim" and heading is None:
            return None  # not in Street View: there is no direction to look along
        point, view = to_sketch(lat, lon, heading, self.ahead_m)
        target = view[1] if self.at == "aim" and view is not None else point
        if self._last is not None and math.hypot(target[0] - self._last[0], target[1] - self._last[1]) < self.min_gap_m:
            return None
        self._last = target
        return target


def tab_urls(port: int) -> list[str]:
    """Addresses of the page tabs of the Chrome on ``port`` (its DevTools HTTP endpoint)."""
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/json", timeout=2) as resp:
        return [t["url"] for t in json.load(resp) if t.get("type") == "page"]


def launch_chrome(port: int, lat: float, lon: float) -> subprocess.Popen:
    exe = shutil.which("google-chrome") or shutil.which("chromium-browser") or shutil.which("chromium")
    if not exe:
        sys.exit("no Chrome found; start one with --remote-debugging-port and run without --launch")
    profile = tempfile.mkdtemp(prefix="drycastle-streetview-")  # a clean, signed-out profile, removed by the OS
    start = f"https://www.google.com/maps/@{lat},{lon},17z"
    return subprocess.Popen(
        [exe, f"--remote-debugging-port={port}", f"--user-data-dir={profile}", "--no-first-run",
         "--no-default-browser-check", start],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )


def follow(
    port: int, position_path: Path, sketch_path: Path | None, interval: float,
    points_path: Path | None = None, dropper: PointDropper | None = None, points_sketch: str = POINTS_SKETCH,
) -> None:
    last: tuple[float, float, float | None] | None = None
    warned = False
    while True:
        try:
            urls = tab_urls(port)
            warned = False
        except (urllib.error.URLError, OSError):
            if not warned:
                print(f"no Chrome answers on port {port} (waiting)", file=sys.stderr)
                warned = True
            time.sleep(interval)
            continue
        current = next((p for p in (parse_maps_url(u) for u in urls) if p), None)
        if dropper is not None and points_path is not None:
            spot = dropper.feed(current, time.monotonic())
            if spot is not None:
                pid = add_point(points_path, spot, points_sketch)
                print(f"point {pid} in {points_sketch} at S-JTSK {spot[0]}, {spot[1]}", flush=True)
        for url in urls:
            parsed = parse_maps_url(url)
            if parsed and parsed != last:
                last = parsed
                lat, lon, hd = parsed
                doc = position_doc(lat, lon, hd, time.time_ns() // 1_000_000)
                write_position(position_path, doc)
                if sketch_path is not None:
                    point, view = to_sketch(lat, lon, hd)
                    update_sketch(sketch_path, point, view)
                print(f"{lat:.6f},{lon:.6f} heading {hd if hd is not None else '-'} -> S-JTSK {doc['x']}, {doc['y']}", flush=True)
                break
        time.sleep(interval)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--port", type=int, default=9222, help="Chrome remote debugging port (default 9222)")
    ap.add_argument("--position-file", type=Path, default=DEFAULT_POSITION_FILE, help="where the viewer reads the position")
    ap.add_argument("--sketch", nargs="?", type=Path, const=DEFAULT_SKETCH_FILE, default=None,
                    help="also keep the position as a point and a view line in this sketch.json (default out/sketch.json)")
    ap.add_argument("--interval", type=float, default=0.5, help="seconds between looks at the tab (default 0.5)")
    ap.add_argument("--points", action="store_true", help="drop a permanent point when the view stays put (off by default)")
    ap.add_argument("--points-sketch", default=POINTS_SKETCH, help=f"the sketch the points go to (default {POINTS_SKETCH})")
    ap.add_argument("--dwell", type=float, default=2.0, help="seconds the view must stay put to drop a point (default 2)")
    ap.add_argument("--ahead", type=float, default=VIEW_LINE_M, help=f"metres along the view where the point goes (default {VIEW_LINE_M:g})")
    ap.add_argument("--at", choices=("aim", "camera"), default="aim", help="a point where you look (aim) or where you stand (camera)")
    ap.add_argument("--min-gap", type=float, default=1.5, help="no new point within this many metres of the last one (default 1.5)")
    ap.add_argument("--launch", action="store_true", help="start a Chrome with its own profile on the village")
    ap.add_argument("--start", default="48.4024653,16.8607311", help="lat,lon the launched Chrome opens at")
    args = ap.parse_args(argv)
    chrome = None
    if args.launch:
        lat, lon = (float(v) for v in args.start.split(","))
        chrome = launch_chrome(args.port, lat, lon)
    print(f"following Street View tabs on port {args.port} into {args.position_file}"
          + (f" and {args.sketch}" if args.sketch else "") + " (Ctrl-C stops)")
    if not sketchfile.SKETCH_NAME.fullmatch(args.points_sketch):
        sys.exit(f"{args.points_sketch!r} is not a sketch name: 1 to 24 letters, digits, _ or -")
    dropper = PointDropper(args.dwell, args.min_gap, args.at, args.ahead) if args.points else None
    points_path = args.sketch or DEFAULT_SKETCH_FILE
    if dropper is not None:
        print(f"points: stop and look for {args.dwell:g} s to drop one in sketch {args.points_sketch} ({args.at}) of {points_path}")
    try:
        follow(args.port, args.position_file, args.sketch, args.interval, points_path, dropper, args.points_sketch)
    except KeyboardInterrupt:
        pass
    finally:
        if chrome:
            chrome.terminate()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
