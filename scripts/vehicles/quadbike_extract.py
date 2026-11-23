#!/usr/bin/env python3
"""Step 1 of the POLYGON QuadBike batch (scripts/vehicles/build_vehicle_packs.sh): pulls the quad bike FBX and its textures out of the paid Synty
Unity package POLYGON_QuadBike_Unity_2022_3_v1_2_1.unitypackage into a small Godot project under assets-private/ (never committed).

    python3 -I scripts/vehicles/quadbike_extract.py <package.unitypackage> <project dir>

Writes into <project dir> (created; project.godot only if missing):
    fbx/SM_Veh_QuadBike_01.fbx          the model, file name unchanged
    textures/PolygonQuadBike_01..04.png + PolygonQuadBike_01_Emissive.png   the four atlases (colour variants) and the emission map, as shipped
    sources.json                        per prefab (SM_Veh_QuadBike_01, SM_Veh_QuadBike_01_Military): the material (texture) of each node, read as text
The package also holds the whole POLYGON Generic pack (a dependency): only the QuadBike files are taken. The package is untrusted data: it is
unpacked with the tarfile "data" filter into a fresh temp directory that is deleted again; nothing in it is executed; the YAML is only
regex-read as text.
"""
import json, os, re, shutil, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from unitypackage import guid_of, objects, unpack, write_godot_project

BASE = "Assets/Synty/PolygonQuadBike/"
FBX = [BASE + "Models/SM_Veh_QuadBike_01.fbx"]
TEXTURES = [BASE + "Textures/PolygonQuadBike_0%d.png" % i for i in (1, 2, 3, 4)] + [BASE + "Textures/PolygonQuadBike_01_Emissive.png"]
PREFABS = [BASE + "Prefabs/SM_Veh_QuadBike_01.prefab", BASE + "Prefabs/SM_Veh_QuadBike_01_Military.prefab"]
MATERIALS = [BASE + "Materials/PolygonQuadBike_0%d.mat" % i for i in (1, 2, 3, 4)]


def main():
    pkg, proj = sys.argv[1], sys.argv[2]
    os.makedirs(proj, exist_ok=True)
    with unpack(pkg, "quadbike_") as (tmp, path_of, guid_for, read):
        os.makedirs(os.path.join(proj, "fbx"), exist_ok=True)
        os.makedirs(os.path.join(proj, "textures"), exist_ok=True)
        for up in FBX:
            shutil.copyfile(os.path.join(tmp, guid_for[up], "asset"), os.path.join(proj, "fbx", os.path.basename(up)))
        for up in TEXTURES:
            shutil.copyfile(os.path.join(tmp, guid_for[up], "asset"), os.path.join(proj, "textures", os.path.basename(up)))
        mat_tex = {}
        for up in MATERIALS:
            mat = read(guid_for[up])
            tex = guid_of(re.search(r"_Albedo_Map:\s+m_Texture: (\{[^}]*\})", mat).group(1))
            mat_tex[guid_for[up]] = {"material": os.path.basename(up)[:-4], "texture": os.path.basename(path_of[tex])}
        sources = {}
        for up in PREFABS:
            text = read(guid_for[up])
            go_name, mr = {}, {}
            for cls, fid, body in objects(text):
                if cls == 1:
                    go_name[fid] = re.search(r"m_Name: (.*)", body).group(1).strip()
                elif cls == 23:
                    mr[re.search(r"m_GameObject: \{fileID: (-?\d+)\}", body).group(1)] = body
            nodes = {}
            for go, body in mr.items():
                g = guid_of(re.search(r"m_Materials:\n\s+- (\{[^}]*\})", body).group(1))
                nodes[go_name[go]] = mat_tex[g]
            sources[os.path.basename(up)[:-7]] = {"unity_path": up, "nodes": nodes}
        with open(os.path.join(proj, "sources.json"), "w") as f:
            json.dump({"fbx": os.path.basename(FBX[0]), "prefabs": sources}, f, indent=1)
        write_godot_project(proj, "synty-quadbike")
        print("extracted", len(FBX), "FBX,", len(TEXTURES), "textures ->", proj)


if __name__ == "__main__":
    main()
