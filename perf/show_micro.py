#!/usr/bin/env python3
"""Print out/perf/<dir>/micro.json (argument: the directory) as a table."""
import json, sys
from pathlib import Path

r = json.loads((Path(sys.argv[1]) / "micro.json").read_text())
print("map", r["map_version"], "| spawns", r["spawns"])
cols = ("n", "mean_us", "p50_us", "p95_us", "p99_us", "max_us")
for grp in ("rays", "moves", "cars"):
    print(f"\n== {grp}")
    print(f'{"op":38}' + "".join(f"{c:>10}" for c in cols) + "  extra")
    for k, v in r[grp].items():
        if isinstance(v, dict) and "p50_us" in v:
            extra = f'hit_share={v["hit_share"]:.2f}' if "hit_share" in v else ""
            print(f"{k:38}" + "".join(f"{v[c]:>10.1f}" if c != "n" else f"{v[c]:>10}" for c in cols) + "  " + extra)
        else:
            print(f"{k:38} {v}")
