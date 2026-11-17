#!/usr/bin/env python3
"""Step 5 of the SIMPLE Military vehicle batch (scripts/build_simple_military.sh): adds the pack "synty-simple-military" to assets-private/manifest.json
(the shared manifest of the private packs; the entries of the other packs are kept, this pack's are replaced), so `pack:synty-simple-military/<name>` ids
resolve (MapLoader.ReadAssetEntries; vehicles.json `model`). The sizes are measured on the built GLBs by the model scripts (docs/research/scud-models-facts.d).

    python3 -I scripts/simple_military_manifest.py [assets-private dir]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACK = "synty-simple-military"
DIR = "synty/glb/simple-military"
MODELS = [  # (file stem, facts file, display name, tags)
    ("humvee", "humvee", "Humvee (troop car, windows, interior, roof gun)", ["vehicles", "humvee"]),
    ("scud", "scud", "Scud launcher truck", ["vehicles", "scud"]),
    ("scud_missile", "scud", "Scud missile (flying entity)", ["props", "scud"]),
    ("rocket_cart", "rocket_cart", "Rocket cart (towable, one Scud missile)", ["vehicles", "scud"]),
]


def main():
    private = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/home/jjakubec/coding/drycastle/assets-private")
    path = private / "manifest.json"
    doc = json.loads(path.read_text())
    assets = [a for a in doc["assets"] if a.get("pack") != PACK]
    packs = [p for p in doc["packs"] if p.get("id") != PACK]
    mine = []
    for stem, facts_name, name, tags in MODELS:
        facts = json.loads((ROOT / "docs" / "research" / "scud-models-facts.d" / f"{facts_name}.json").read_text())
        if stem == "scud_missile":
            size = facts["rack"]["missile_size"]
        else:
            lo, hi = facts["bounds"]["min"], facts["bounds"]["max"]
            size = [round(hi[i] - lo[i], 2) for i in range(3)]
        mine.append({"category": "other", "file": f"{DIR}/{stem}.glb", "id": f"pack:{PACK}/{stem}", "name": name, "pack": PACK, "prefab": True,
                     "size_m": list(size), "tags": ["private", PACK] + tags})
    doc["assets"] = assets + mine
    doc["packs"] = packs + [{"assets": len(mine), "dir": DIR, "id": PACK}]
    path.write_text(json.dumps(doc, indent=1) + "\n")
    print(f"manifest: {len(mine)} entries for {PACK}, {len(assets)} kept from other packs -> {path}")


if __name__ == "__main__":
    main()
