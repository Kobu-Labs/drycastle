"""Captures of one building taken by driving a Chrome tab through Street View: no key press, no API image.

    uv run python -m drycastle_pipeline.photos.streetview_shoot w378920508               (up to 3 sides)
    uv run python -m drycastle_pipeline.photos.streetview_shoot w378920508 --views 4 --size 2560x1600 --show

Finds the panoramas around the building (``streetview.views_for``: free metadata requests, needs
``GOOGLE_MAPS_KEY``), opens each in a Chrome of its own aimed at the building, waits until the picture stops
changing, hides the page's controls and saves the tab as ``photo_capture`` does on the C key:
``data/village/photos/<building>/<n>.png`` and ``.json`` with the camera read back from the tab's address.
So ``landmark_photo prep`` takes them as they are. Same terms as every capture (``docs/streetview.md``).
"""

import argparse
import base64
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
from pathlib import Path
from urllib.parse import urlencode

from drycastle_pipeline.photos import streetview
from drycastle_pipeline.core.config import load_dotenv, map_paths
from drycastle_pipeline.photos.photo_capture import PHOTOS_DIR, Cdp, parse_view, save_capture, tabs

PORT = 9333  # not 9222: a follower's Chrome may be running
SETTLE_S = 1.0  # two pictures this far apart that are the same: the panorama has loaded
LOAD_TIMEOUT_S = 30.0
MARGIN = 1.6  # the view is this many times as wide as the building
# a signed-out Chrome in the EU gets the consent page first; this cookie is the answer "reject all"
CONSENT_COOKIE = {"name": "SOCS", "value": "CAESEwgDEgk0ODE3Nzk3MjQaAmVuIAEaBgiA_LyaBg", "domain": ".google.com", "path": "/", "secure": True}
CONSENT_CLICK = """(() => { const b = [...document.querySelectorAll('button')].find(b => /reject all|odmietnu|odmítnout|alle ablehnen/i.test(b.textContent));
  if (b) b.click(); return !!b; })()"""
# everything but the panorama (the largest canvas) out of the picture
HIDE_UI = """(() => { const c = [...document.querySelectorAll('canvas')].sort((a, b) => b.width * b.height - a.width * a.height)[0];
  if (!c || c.width < 300) return false;
  c.classList.add('__dcPano');
  if (!document.getElementById('__dcHide')) { const s = document.createElement('style'); s.id = '__dcHide';
    s.textContent = 'body * { visibility: hidden !important } canvas.__dcPano { visibility: visible !important }';
    document.head.appendChild(s); }
  return true; })()"""


def pano_url(pano_id: str, heading: float, pitch: float, fov: float) -> str:
    """The Maps address that opens one panorama looking one way (Maps URLs: fov 10 to 100)."""
    q = {"api": 1, "map_action": "pano", "pano": pano_id, "heading": f"{heading % 360:.2f}",
         "pitch": f"{pitch:.2f}", "fov": f"{max(10.0, min(100.0, fov)):.1f}"}  # fmt: skip
    return "https://www.google.com/maps/@?" + urlencode(q)


def aim(radius_m: float, dist_m: float, cam_height: float = 2.5, wall_height: float = 6.0) -> tuple[float, float]:
    """(pitch, horizontal fov) that put a building of this size in the middle of the view with room around it."""
    fov = math.degrees(2 * math.atan(radius_m * MARGIN / max(dist_m, 1.0)))
    pitch = math.degrees(math.atan2(wall_height / 2 - cam_height, max(dist_m - radius_m, 1.0)))
    return round(pitch, 1), max(25.0, min(90.0, fov))


def launch(port: int, size: tuple[int, int], show: bool) -> subprocess.Popen:
    exe = shutil.which("google-chrome") or shutil.which("chromium-browser") or shutil.which("chromium")
    if not exe:
        sys.exit("no Chrome found")
    profile = tempfile.mkdtemp(prefix="drycastle-shoot-")
    cmd = [exe, f"--remote-debugging-port={port}", f"--user-data-dir={profile}", "--no-first-run",
           "--no-default-browser-check", f"--window-size={size[0]},{size[1]}", "--lang=en", "about:blank"]  # fmt: skip
    if not show:
        cmd[1:1] = ["--headless=new", "--hide-scrollbars"]
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        try:
            if tabs(port):
                return proc
        except (urllib.error.URLError, OSError):
            pass
        time.sleep(0.2)
    proc.terminate()
    sys.exit(f"Chrome did not answer on port {port}")


def _eval(cdp: Cdp, js: str):
    return cdp.call("Runtime.evaluate", {"expression": js, "returnByValue": True})["result"].get("value")


def shoot(cdp: Cdp, url: str, note=print) -> tuple[bytes, str] | None:
    """Open one address and return (png, the tab's address once it names the camera). None when it never loads."""
    cdp.call("Page.navigate", {"url": url})
    deadline = time.monotonic() + LOAD_TIMEOUT_S
    here = ""
    while time.monotonic() < deadline:
        time.sleep(0.5)
        here = _eval(cdp, "location.href") or ""
        if "consent." in here:
            _eval(cdp, CONSENT_CLICK)
        elif parse_view(here) and _eval(cdp, HIDE_UI):
            break
    else:
        note(f"shoot: no panorama after {LOAD_TIMEOUT_S:.0f} s (tab is at {here[:90]})")
        return None
    last = None
    while time.monotonic() < deadline:
        time.sleep(SETTLE_S)
        png = cdp.call("Page.captureScreenshot", {"format": "png"})["data"]
        if png == last:
            break
        last = png
    else:
        note("shoot: the picture was still changing at the time limit; saved as it was")
    return base64.b64decode(last), _eval(cdp, "location.href")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("building", help="building id as in buildings.json, e.g. w378920508")
    ap.add_argument("--views", type=int, default=3, help="sides to take (default 3)")
    ap.add_argument("--size", default="2560x1600", help="the tab's size in px (default 2560x1600)")
    ap.add_argument("--fov", type=float, help="horizontal field of view in degrees (default: fits the building)")
    ap.add_argument("--show", action="store_true", help="a visible Chrome window instead of a headless one")
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--photos", type=Path, default=PHOTOS_DIR, help="where captures go (default data/village/photos)")
    args = ap.parse_args(argv)
    load_dotenv()
    key = os.environ.get(streetview.KEY_ENV)
    if not key:
        print(f"{streetview.KEY_ENV} is not set; see docs/streetview.md")
        return 2
    points = streetview.building_points(json.loads((map_paths().raw / "osm.json").read_text()))
    if args.building not in points:
        print(f"unknown building {args.building}")
        return 2
    lon, lat, radius = points[args.building]
    views = streetview.views_for(streetview.StreetView(key), lon, lat, radius, args.views)
    if not views:
        print("no panorama near this building")
        return 1
    size = tuple(int(v) for v in args.size.split("x"))
    chrome = launch(args.port, size, args.show)
    saved = 0
    try:
        cdp = Cdp(tabs(args.port)[0]["webSocketDebuggerUrl"], timeout=60.0)
        cdp.call("Network.setCookie", CONSENT_COOKIE)
        for v in views:
            pitch, fov = aim(radius, v["distance_m"])
            got = shoot(cdp, pano_url(v["pano_id"], v["heading"], pitch, args.fov or fov))
            if got and save_capture(got[0], got[1], args.photos):
                saved += 1
        cdp.close()
    finally:
        chrome.terminate()
    print(f"{saved} of {len(views)} views saved")
    return 0 if saved else 1


if __name__ == "__main__":
    raise SystemExit(main())
