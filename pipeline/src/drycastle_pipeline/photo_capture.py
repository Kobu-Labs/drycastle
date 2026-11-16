"""Capture what a Street View tab shows, with the camera it was taken with, for ``photo-to-building``.

    uv run python -m drycastle_pipeline.streetview_follow --launch --capture      (walk; press C on a building)
    uv run python -m drycastle_pipeline.photo_capture list                        (what was captured, per building)
    uv run python -m drycastle_pipeline.photo_capture set <building>/<n> <building>   (a capture filed under the wrong building)
    uv run python -m drycastle_pipeline.photo_capture todo                        (buildings with captures and no detail yet)

While ``streetview_follow --capture`` runs, pressing the capture key (C) in the Street View tab saves the picture of the
tab and, next to it, where the camera stood and looked: both come from Chrome's debugging port, as the address the
follower already reads. The building is guessed as the first footprint the middle of the view hits; the follower prints
it and shows it in the tab (a short banner, and the page's console), and ``set`` moves a capture that was filed wrong. Files: ``data/village/photos/<building>/<n>.png`` and ``<n>.json``
(gitignored, never shipped; ``docs/streetview.md`` on the terms).

The address gives the heading (``h``), the tilt (``t``, 90 is level) and the zoom (``y``, the vertical field of view of the tab in degrees), so the fit of ``landmark_photo`` starts from a known camera
and needs four points instead of six (one must be where a wall meets the ground: the camera's height is not in the
address).
"""

import argparse
import base64
import json
import math
import os
import re
import shutil
import socket
import struct
import sys
import urllib.request
from pathlib import Path
from urllib.parse import urlparse

from drycastle_pipeline.config import REPO_ROOT

PHOTOS_DIR = REPO_ROOT / "data" / "village" / "photos"
_AT = re.compile(r"/@(-?\d+\.\d+),(-?\d+\.\d+)((?:,[^,/]+)*)")
_PART = re.compile(r"^(-?\d+(?:\.\d+)?)([a-z])$")
KEY_HOOK = """(() => { if (window.__dcCap === undefined) { window.__dcCap = 0;
  addEventListener('keydown', e => { const t = e.target && e.target.tagName;
    if (e.key.toLowerCase() === %s && !e.ctrlKey && !e.metaKey && !e.altKey && t !== 'INPUT' && t !== 'TEXTAREA') {
      window.__dcCap++; console.log('[drycastle] capture key pressed, saving...'); } }, true);
  console.log('[drycastle] capture is on: aim the middle of the view at a building and press the capture key'); }
  return window.__dcCap; })()"""


def parse_view(url: str) -> dict | None:
    """The camera of a Street View address: lat, lon, heading (compass degrees), pitch (degrees up), fov (degrees). None
    for an address that is not Street View (the map has no heading)."""
    if "google." not in url or "/maps" not in url:
        return None
    m = _AT.search(url)
    if not m:
        return None
    parts = {}
    for part in m.group(3).split(","):
        p = _PART.match(part)
        if p:
            parts[p.group(2)] = float(p.group(1))
    if "h" not in parts:
        return None
    return {
        "lat": float(m.group(1)), "lon": float(m.group(2)), "heading": parts["h"] % 360,
        "pitch": parts.get("t", 90.0) - 90.0, "fov": parts.get("y", 75.0),
    }  # fmt: skip


def encode_frame(payload: bytes, mask: bytes) -> bytes:
    """One masked WebSocket text frame (client to server)."""
    n = len(payload)
    head = bytes([0x81]) + (bytes([0x80 | n]) if n < 126 else bytes([0x80 | 126]) + struct.pack(">H", n) if n < 65536 else bytes([0x80 | 127]) + struct.pack(">Q", n))
    return head + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload))


class Cdp:
    """A minimal Chrome DevTools connection to one tab: ``call(method, params)`` and its result. Standard library only."""

    def __init__(self, ws_url: str, timeout: float = 15.0) -> None:
        u = urlparse(ws_url)
        self.sock = socket.create_connection((u.hostname, u.port), timeout=timeout)
        key = base64.b64encode(os.urandom(16)).decode()
        # no Origin header: Chrome refuses DevTools connections that carry one it does not know
        self.sock.sendall(f"GET {u.path} HTTP/1.1\r\nHost: {u.hostname}:{u.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n".encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("Chrome closed the DevTools connection")
            head += chunk
        if b" 101 " not in head.split(b"\r\n", 1)[0]:
            raise ConnectionError(f"DevTools refused the connection: {head[:80]!r}")
        self.buf = head.split(b"\r\n\r\n", 1)[1]
        self.next_id = 0

    def _read(self, n: int) -> bytes:
        while len(self.buf) < n:
            chunk = self.sock.recv(1 << 16)
            if not chunk:
                raise ConnectionError("Chrome closed the DevTools connection")
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def _message(self) -> bytes:
        data = b""
        while True:
            b0, b1 = self._read(2)
            n = b1 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._read(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._read(8))[0]
            payload = self._read(n)
            op = b0 & 0x0F
            if op == 0x8:
                raise ConnectionError("Chrome closed the DevTools connection")
            if op == 0x9:  # ping: answer, keep waiting
                self.sock.sendall(bytes([0x8A, 0x80]) + os.urandom(4))
                continue
            if op in (0x0, 0x1, 0x2):
                data += payload
                if b0 & 0x80:
                    return data

    def call(self, method: str, params: dict | None = None) -> dict:
        self.next_id += 1
        self.sock.sendall(encode_frame(json.dumps({"id": self.next_id, "method": method, "params": params or {}}).encode(), os.urandom(4)))
        while True:
            msg = json.loads(self._message())
            if msg.get("id") == self.next_id:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error'].get('message')}")
                return msg.get("result", {})

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


def tabs(port: int) -> list[dict]:
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/json", timeout=2) as resp:
        return [t for t in json.load(resp) if t.get("type") == "page"]


def guess_building(x: float, y: float, heading: float, reach: float = 60.0) -> tuple[str | None, list[int], float]:
    """The building the middle of the view hits first, the edges of it that face the camera, and the distance. (x, y)
    are local metres. No footprint within ``reach``: (None, [], inf)."""
    from shapely.geometry import LineString, Point, Polygon

    from drycastle_pipeline.landmark_tools import WORK, _edges

    d = (math.sin(math.radians(heading)), math.cos(math.radians(heading)))
    ray = LineString([(x, y), (x + d[0] * reach, y + d[1] * reach)])
    eye = Point(x, y)
    best: tuple[float, dict] | None = None
    for b in json.loads((WORK / "buildings.json").read_text())["buildings"]:
        ring = b["footprint"]["exterior"]
        if min(math.dist((x, y), p) for p in ring) > reach + 30:
            continue
        hit = ray.intersection(Polygon(ring))
        if hit.is_empty:
            continue
        dist = eye.distance(hit)
        if best is None or dist < best[0]:
            best = (dist, b)
    if best is None:
        return None, [], float("inf")
    facing = [e["i"] for e in _edges(best[1]) if (x - e["p"][0]) * e["normal"][0] + (y - e["p"][1]) * e["normal"][1] > 0.05]
    return best[1]["id"], facing, best[0]


def _next_free(folder: Path) -> int:
    used = [int(p.stem) for p in folder.glob("*.json") if p.stem.isdigit()]
    return max(used, default=-1) + 1


def save_capture(png: bytes, url: str, photos: Path = PHOTOS_DIR, note=print) -> Path | None:
    """File one picture with its camera under the building it shows. Returns the sidecar's path, or None when the tab
    is not in Street View."""
    from io import BytesIO

    from PIL import Image

    from drycastle_pipeline.landmark_tools import WORK
    from drycastle_pipeline.streetview_follow import _TO_JTSK

    view = parse_view(url)
    if view is None:
        note("capture: this tab is not in Street View (the address has no heading); nothing saved")
        return None
    ex, ey = _TO_JTSK.transform(view["lon"], view["lat"])
    ox, oy = json.loads((WORK / "parcels.json").read_text())["origin_epsg5514"]
    x, y = ex - ox, ey - oy
    building, edges, dist = guess_building(x, y, view["heading"])
    folder = photos / (building or "unknown")
    folder.mkdir(parents=True, exist_ok=True)
    n = _next_free(folder)
    (folder / f"{n}.png").write_bytes(png)
    w, h = Image.open(BytesIO(png)).size
    side = folder / f"{n}.json"
    side.write_text(json.dumps({
        "photo": f"{n}.png", "width": w, "height": h, "building": building, "edges_facing": edges,
        "distance_m": None if building is None else round(dist, 1), "x": round(x, 2), "y": round(y, 2),
        "heading": round(view["heading"], 2), "pitch": round(view["pitch"], 2), "fov": view["fov"], "cam_height": 2.5,
        "lat": view["lat"], "lon": view["lon"], "url": url,
    }, indent=1))  # fmt: skip
    note(f"capture: {side.relative_to(photos.parent.parent.parent) if photos == PHOTOS_DIR else side}  "
         + (f"{building}, {dist:.0f} m away, walls {edges}" if building else "NO BUILDING in the middle of the view (filed under unknown/)"))  # fmt: skip
    return side


TOAST = """(() => { const m = %s, ok = %s; console.log('[drycastle] ' + m);
  let d = document.getElementById('__dcToast'); if (!d) { d = document.createElement('div'); d.id = '__dcToast';
    d.style.cssText = 'position:fixed;left:50%%;top:18px;transform:translateX(-50%%);z-index:2147483647;padding:10px 18px;' +
      'border-radius:8px;font:600 15px system-ui,sans-serif;color:#fff;box-shadow:0 2px 10px rgba(0,0,0,.4);pointer-events:none';
    document.body.appendChild(d); }
  d.textContent = m; d.style.background = ok ? '#1b7f3b' : '#a33a1a'; d.style.display = 'block';
  clearTimeout(window.__dcToastT); window.__dcToastT = setTimeout(() => { d.style.display = 'none'; }, 3500); })()"""


def describe(side: Path | None) -> tuple[str, bool]:
    """The line shown in the tab after a capture, and whether it went well."""
    if side is None:
        return "Not saved: this tab is not in Street View", False
    c = json.loads(side.read_text())
    if c["building"] is None:
        return f"Saved as unknown/{side.stem}: no building in the middle of the view. Aim at the house and press again", False
    n = len(list(side.parent.glob("*.json")))
    return f"Saved {c['building']} #{side.stem}: {c['distance_m']:.0f} m away, walls {c['edges_facing']} ({n} capture{'s' if n != 1 else ''} of it)", True


class Capturer:
    """Watches one Chrome for presses of the capture key in its Street View tab; ``poll()`` once per look at the tabs."""

    def __init__(self, port: int, key: str = "c", photos: Path = PHOTOS_DIR) -> None:
        self.port, self.key, self.photos = port, key.lower(), photos
        self.seen: dict[str, int] = {}

    def poll(self) -> None:
        try:
            for t in tabs(self.port):
                if parse_view(t.get("url", "")) is None and "google." not in t.get("url", ""):
                    continue
                cdp = Cdp(t["webSocketDebuggerUrl"])
                try:
                    count = cdp.call("Runtime.evaluate", {"expression": KEY_HOOK % json.dumps(self.key), "returnByValue": True})["result"].get("value", 0)
                    was = self.seen.get(t["id"], count if t["id"] not in self.seen else 0)
                    self.seen[t["id"]] = count
                    if count > was:
                        cdp.call("Runtime.evaluate", {"expression": "(() => { const d = document.getElementById('__dcToast'); if (d) d.style.display = 'none'; })()"})
                        shot = cdp.call("Page.captureScreenshot", {"format": "png"})
                        text, ok = describe(save_capture(base64.b64decode(shot["data"]), t["url"], self.photos))
                        cdp.call("Runtime.evaluate", {"expression": TOAST % (json.dumps(text), "true" if ok else "false")})
                finally:
                    cdp.close()
        except (OSError, RuntimeError, KeyError) as e:
            print(f"capture: {e}", file=sys.stderr)


def captures(photos: Path = PHOTOS_DIR) -> dict[str, list[dict]]:
    out: dict[str, list[dict]] = {}
    for side in sorted(photos.glob("*/*.json"), key=lambda p: (p.parent.name, int(p.stem) if p.stem.isdigit() else 0)):
        out.setdefault(side.parent.name, []).append({**json.loads(side.read_text()), "sidecar": str(side)})
    return out


def main(argv: list[str] | None = None) -> int:
    from drycastle_pipeline.building_detail import DETAILS_DIR

    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list")
    sub.add_parser("todo")
    s = sub.add_parser("set")
    s.add_argument("capture", help="<building>/<n>")
    s.add_argument("building")
    a = ap.parse_args(argv)
    all_caps = captures()
    if a.cmd == "set":
        src = PHOTOS_DIR / f"{a.capture}.json"
        if not src.exists():
            sys.exit(f"no capture {a.capture}")
        dst = PHOTOS_DIR / a.building
        dst.mkdir(parents=True, exist_ok=True)
        n = _next_free(dst)
        doc = {**json.loads(src.read_text()), "building": a.building, "photo": f"{n}.png", "edges_facing": []}
        shutil.move(src.with_suffix(".png"), dst / f"{n}.png")
        (dst / f"{n}.json").write_text(json.dumps(doc, indent=1))
        src.unlink()
        print(f"{a.capture} -> {a.building}/{n}")
        return 0
    for building, caps in all_caps.items():
        done = (DETAILS_DIR / f"{building}.yaml").exists()
        if a.cmd == "todo" and (done or building == "unknown"):
            continue
        print(f"{building}  {len(caps)} capture(s)" + ("  (has a detail)" if done else ""))
        for c in caps:
            print(f"  {Path(c['sidecar']).stem}: from ({c['x']}, {c['y']}) looking {c['heading']:.0f} deg, {c.get('distance_m')} m away, walls {c.get('edges_facing')}")
    if not all_caps:
        print("no captures yet: run streetview_follow --capture and press C on a building")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
