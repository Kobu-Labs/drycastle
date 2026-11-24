#!/usr/bin/env python3
"""Which atlases give a Synty vehicle valid colour variants (docs/research/vehicle-models-facts.md).
    python3 scripts/vehicles/vehicle_atlas_variants.py <dump.json> <base atlas.png> <candidate atlas.png> [<candidate> ...]
For every mesh node of the dump (scripts/vehicles/vehicle_model_dump.gd), samples each triangle's centroid UV in the base atlas and in each candidate and
reports the share of triangles (and of area) whose colour changes by more than 12/255 (per channel max), plus the base colours that change and what they become. A candidate that changes some triangles and leaves the rest (tyres, glass, lamps) alone is a valid paint variant.
"""
import json, sys
from collections import Counter
import numpy as np
from PIL import Image


def main():
    nodes = json.load(open(sys.argv[1]))["nodes"]
    base = np.array(Image.open(sys.argv[2]).convert("RGB"))
    cands = [(p, np.array(Image.open(p).convert("RGB"))) for p in sys.argv[3:]]
    tris_uv, areas, names, skip = [], [], [], []
    for n in nodes:
        uv = np.array(n["uv"], float).reshape(-1, 2)
        if len(uv) == 0:
            continue
        I = np.array(n["i"], int).reshape(-1, 3)
        V = np.array(n["v"], float).reshape(-1, 3)
        c = uv[I].mean(1)
        a = np.linalg.norm(np.cross(V[I[:, 1]] - V[I[:, 0]], V[I[:, 2]] - V[I[:, 0]]), axis=1) / 2
        tris_uv.append(c)
        areas.append(a)
        names += [n["name"]] * len(I)
        if "Glass" in n["name"]:
            skip.extend(range(len(names) - len(I), len(names)))
    uv = np.concatenate(tris_uv)
    area = np.concatenate(areas)
    names = np.array(names)
    keep = np.ones(len(names), bool)  # glass panes wear a flat glass material in the game, not the atlas
    keep[skip] = False
    uv, area, names = uv[keep], area[keep], names[keep]

    def sample(img):
        h, w = img.shape[:2]
        x = np.clip((uv[:, 0] % 1.0) * w, 0, w - 1).astype(int)
        y = np.clip((uv[:, 1] % 1.0) * h, 0, h - 1).astype(int)
        return img[y, x].astype(int)
    b = sample(base)
    print(f"{sys.argv[1]}: {len(uv)} tris, area {area.sum():.1f} m2; UV box u {uv[:,0].min():.3f}..{uv[:,0].max():.3f} v {uv[:,1].min():.3f}..{uv[:,1].max():.3f}")
    top = Counter(tuple(int(x) for x in r) for r in b).most_common(60)
    for p, img in cands:
        c = sample(img)
        d = np.abs(c - b).max(1) > 12
        print(f"  {p.split('/')[-1]}: {d.mean()*100:.0f}% of tris, {area[d].sum()/area.sum()*100:.0f}% of area change colour")
        shown = 0
        for col, cnt in top:  # the base colours that change, biggest first (the rest keeps its colour)
            m = (b == np.array(col)).all(1)
            nxt = Counter(tuple(int(x) for x in r) for r in c[m]).most_common(1)[0][0]
            if max(abs(nxt[i] - col[i]) for i in range(3)) > 12 and shown < 4:
                shown += 1
                print(f"      base {col} ({cnt} tris, {area[m].sum()/area.sum()*100:.0f}% area) -> {nxt}")
        for nm in sorted(set(names)):
            m = names == nm
            if m.sum() and d[m].any():
                print(f"      node {nm}: {d[m].mean()*100:.0f}% of its tris change")


if __name__ == "__main__":
    main()
