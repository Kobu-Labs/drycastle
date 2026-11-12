#!/usr/bin/env python3
"""Write manifest.json for a game release (read by the launcher).

make_manifest.py --repo R --tag T --channel stable|dev --commit SHA --protocol N --windows-zip W.zip --linux-zip L.zip --map-tag map-X --map-json map.json --out manifest.json
"""
import argparse, hashlib, json, os

ap = argparse.ArgumentParser()
for a in ("repo", "tag", "channel", "commit", "windows-zip", "linux-zip", "map-tag", "map-json", "out"):
    ap.add_argument("--" + a, required=True)
ap.add_argument("--protocol", type=int, required=True)
a = ap.parse_args()

def build(path, exe):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    name = os.path.basename(path)
    return {"exe": exe, "game": {"name": name, "url": f"{base}/{a.tag}/{name}", "sha256": h.hexdigest(), "size": os.path.getsize(path)}}


base = f"https://github.com/{a.repo}/releases/download"
m = json.load(open(a.map_json))
manifest = {
    "schema": 2, "version": a.tag, "channel": a.channel, "commit": a.commit, "protocol": a.protocol,
    "platforms": {
        "windows": build(getattr(a, "windows_zip"), "drycastle-shooter.exe"),
        "linux": build(getattr(a, "linux_zip"), "drycastle-shooter.x86_64"),
    },
    "map": {"id": a.map_tag, "file": {"name": m["name"], "url": f"{base}/{a.map_tag}/{m['name']}", "sha256": m["sha256"], "size": m["size"]}},
}
json.dump(manifest, open(a.out, "w"), indent=2)
print(json.dumps(manifest, indent=2))
