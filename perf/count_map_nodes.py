#!/usr/bin/env python3
"""Counts the clutter nodes MapLoader.Clutter builds from out/village/map/clutter.json (same grouping rule as ClutterSettings).
Usage: perf/count_map_nodes.py [map_dir] [cell_m] [multimesh_min]"""
import json, math, sys, collections
d = sys.argv[1] if len(sys.argv) > 1 else "out/village/map"
cell_m = float(sys.argv[2]) if len(sys.argv) > 2 else 48.0
mmin = int(sys.argv[3]) if len(sys.argv) > 3 else 16
doc = json.load(open(f"{d}/clutter.json"))
kinds = doc["kinds"]
mm = 0; small = collections.defaultdict(int); inst = 0; per_kind = collections.Counter()
for ck, byk in doc["chunks"].items():
    for k, lst in byk.items():
        if k not in kinds: continue
        cells = collections.defaultdict(int)
        for e in lst: cells[(math.floor(e[0]/cell_m), math.floor(e[1]/cell_m))] += 1
        for c, n in cells.items():
            inst += n
            if n >= mmin: mm += 1; per_kind[k] += 1
            else: small[(ck, c)] += 1
print(f"instances {inst}; multimesh nodes {mm}; merged nodes {len(small)}; total {mm+len(small)}")
print("multimesh per kind", dict(per_kind))
print("small groups merged", sum(small.values()))
