#!/usr/bin/env python3
"""Step 1 of the POLYGON QuadBike batch (scripts/build_vehicle_packs.sh): pulls the quad bike FBX and its textures out of the paid Synty
Unity package POLYGON_QuadBike_Unity_2022_3_v1_2_1.unitypackage into a small Godot project under assets-private/ (never committed).

    python3 -I scripts/quadbike_extract.py <package.unitypackage> <project dir>

Writes into <project dir> (created; project.godot only if missing):
    fbx/SM_Veh_QuadBike_01.fbx          the model, file name unchanged
    textures/PolygonQuadBike_01..04.png + PolygonQuadBike_01_Emissive.png   the four atlases (colour variants) and the emission map, as shipped
    sources.json                        per prefab (SM_Veh_QuadBike_01, SM_Veh_QuadBike_01_Military): the material (texture) of each node, read as text
The package also holds the whole POLYGON Generic pack (a dependency): only the QuadBike files are taken. The package is untrusted data: it is
unpacked with the tarfile "data" filter into a fresh temp directory that is deleted again; nothing in it is executed; the YAML is only
regex-read as text.
"""
import json, os, re, shutil, sys, tarfile, tempfile

BASE = "Assets/Synty/PolygonQuadBike/"
FBX = [BASE + "Models/SM_Veh_QuadBike_01.fbx"]
TEXTURES = [BASE + "Textures/PolygonQuadBike_0%d.png" % i for i in (1, 2, 3, 4)] + [BASE + "Textures/PolygonQuadBike_01_Emissive.png"]
PREFABS = [BASE + "Prefabs/SM_Veh_QuadBike_01.prefab", BASE + "Prefabs/SM_Veh_QuadBike_01_Military.prefab"]
MATERIALS = [BASE + "Materials/PolygonQuadBike_0%d.mat" % i for i in (1, 2, 3, 4)]


def guid_of(s):
    m = re.search(r"guid: (\w+)", s)
    return m.group(1) if m else None


def main():
    pkg, proj = sys.argv[1], sys.argv[2]
    os.makedirs(proj, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="quadbike_")
    try:
        with tarfile.open(pkg, "r:gz") as t:
            t.extractall(tmp, filter="data")
        path_of, guid_for = {}, {}
        for g in os.listdir(tmp):
            p = os.path.join(tmp, g, "pathname")
            if os.path.isfile(p):
                up = open(p, encoding="utf-8").read().split("\n")[0].strip()
                path_of[g] = up
                guid_for[up] = g

        def read(g):
            return open(os.path.join(tmp, g, "asset"), encoding="utf-8", errors="replace").read()

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
            for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
                cls, fid, body = int(m.group(1)), m.group(2), m.group(3)
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
        gp = os.path.join(proj, "project.godot")
        if not os.path.exists(gp):
            open(gp, "w").write('config_version=5\n\n[application]\n\nconfig/name="synty-quadbike"\nconfig/features=PackedStringArray("4.7", "Forward Plus")\n')
        print("extracted", len(FBX), "FBX,", len(TEXTURES), "textures ->", proj)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
