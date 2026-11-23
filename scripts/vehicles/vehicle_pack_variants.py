#!/usr/bin/env python3
"""Step 4 of scripts/build_vehicle_packs.sh: makes the colour-variant atlases of the vehicles readable from the private pack (glb/** is shipped).

    python3 -I scripts/vehicle_pack_variants.py <assets-private dir>

- Battle Royale: the pack's six vehicle atlases PolygonBattleRoyale_Vehicles_01..06 (+ the emission map) are copied into glb/battle-royale/tex/ (the
  prefabs only reference _01 and _Damaged) and glb/battle-royale/materials.json gets PolygonBattleRoyale_Veh_02..06_mat next to Veh_01_mat (same
  settings, other texture; the pack's Materials/Alts/*_mat.tres do the same). All six share one UV layout: a variant is an atlas swap.
- Military: the Land_Vehicles_NN atlases the vehicle prefabs do not reference (06 07 09 10) are copied into glb/military/tex/ (ADD ONLY: nothing
  already there, and not glb/military/materials.json, is touched) so every Land_Vehicles_01..10 atlas is available for SM_Veh_Car_Small_01.
"""
import json, shutil, sys
from pathlib import Path


def main():
    priv = Path(sys.argv[1])
    synty = priv / "synty"
    br_tex = synty / "battle-royale/polygon-battle-royale-01/Assets/PolygonBattleRoyale/Textures"
    out = synty / "glb/battle-royale"
    mats_path = out / "materials.json"
    doc = json.loads(mats_path.read_text())
    base = doc["materials"]["PolygonBattleRoyale_Veh_01_mat"]
    for i in range(2, 7):
        shutil.copyfile(br_tex / f"PolygonBattleRoyale_Vehicles_0{i}.png", out / "tex" / f"PolygonBattleRoyale_Vehicles_0{i}.png")
        doc["materials"][f"PolygonBattleRoyale_Veh_0{i}_mat"] = dict(base, albedo_texture=f"tex/PolygonBattleRoyale_Vehicles_0{i}.png")
    shutil.copyfile(br_tex / "PolygonBattleRoyale_Vehicles_01_Emmision.png", out / "tex" / "PolygonBattleRoyale_Vehicles_01_Emmision.png")
    mats_path.write_text(json.dumps(doc, indent=" ") + "\n")
    print("battle-royale: atlases 02..06 + emission copied, materials Veh_02..06 added")
    mil_src = synty / "military/polygon-military-01/Assets/Synty/PolygonMilitary/Textures/Vehicles"
    mil_dst = synty / "glb/military/tex"
    added = []
    for i in range(1, 11):
        name = f"Land_Vehicles_{i:02d}.png"
        if not (mil_dst / name).exists():
            shutil.copyfile(mil_src / name, mil_dst / name)
            added.append(name)
    print("military: Land_Vehicles atlases added to glb/military/tex:", added or "none (all present)")


if __name__ == "__main__":
    main()
