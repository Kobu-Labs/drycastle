#!/usr/bin/env python3
"""Write map.json (name, size, sha256) for a map archive: map_json.py <map.zip> <map.json>."""
import hashlib, json, os, sys

zip_path, out = sys.argv[1], sys.argv[2]
h = hashlib.sha256()
with open(zip_path, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
json.dump({"name": os.path.basename(zip_path), "size": os.path.getsize(zip_path), "sha256": h.hexdigest()}, open(out, "w"))
print(f"{out}: {os.path.getsize(zip_path)} bytes, sha256 {h.hexdigest()[:12]}")
