#!/usr/bin/env python3
"""Step 1 of the SIMPLE Military vehicle batch (scripts/build_simple_military.sh): pulls the vehicle FBX and their textures out of the paid
Synty Unity package SIMPLE_Military_Unity_2021_3_v1_3_8.unitypackage into a small Godot project under assets-private/ (never committed).

    python3 -I scripts/simple_military_extract.py <package.unitypackage> <project dir>

Writes into <project dir> (created; project.godot only if missing):
    fbx/<Name>.fbx         the wanted models, file name unchanged
    textures/<file>        the textures their materials use (png as shipped)
    sources.json           per FBX: the Unity path it came from, and the material (texture) of each prefab node, read as text from the .prefab/.mat
The package is untrusted data: it is unpacked with the tarfile "data" filter into a fresh temp directory that is deleted again; nothing in it
is executed; the YAML is only regex-read as text.
"""
import json, os, re, shutil, sys, tarfile, tempfile

WANT_FBX = [
    "Assets/SimpleMilitary/Optimized/Models/Vehicles/SK_Veh_ScudTruck_01.fbx",
    "Assets/SimpleMilitary/Optimized/Models/Vehicles/SK_Veh_Troop_Car_01.fbx",
    "Assets/SimpleMilitary/Old/Models/Vehicles/Veh_troop_car_z.fbx",
    "Assets/SimpleMilitary/Optimized/Models/SM_Prop_Missile_01.fbx",
    "Assets/SimpleMilitary/Optimized/Models/SM_Prop_Missile_02.fbx",
    "Assets/SimpleMilitary/Optimized/Models/SM_Prop_Missile_03.fbx",
    "Assets/SimpleMilitary/Optimized/Models/SM_Prop_Missile_04.fbx",
    "Assets/SimpleMilitary/Optimized/Models/SM_Prop_Mg_stand_01.fbx",  # sentry turret (#306)
]
WANT_PREFAB = [
    "Assets/SimpleMilitary/Optimized/Prefabs/Vehicles/SK_Veh_ScudTruck_01.prefab",
    "Assets/SimpleMilitary/Optimized/Prefabs/Vehicles/SK_Veh_Troop_Car_01.prefab",
    "Assets/SimpleMilitary/Old/Prefabs/VehiclesZ+/Veh_troop_car_z_a.prefab",
    "Assets/SimpleMilitary/Optimized/Prefabs/Props/SM_Prop_Missile_01.prefab",
    "Assets/SimpleMilitary/Optimized/Prefabs/Props/SM_Prop_Mg_stand_01.prefab",
]


def guid_of(s):
    m = re.search(r"guid: (\w+)", s)
    return m.group(1) if m else None


def main():
    pkg, proj = sys.argv[1], sys.argv[2]
    os.makedirs(proj, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="simple_military_")
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
        sources = {}
        for up in WANT_FBX:
            shutil.copyfile(os.path.join(tmp, guid_for[up], "asset"), os.path.join(proj, "fbx", os.path.basename(up)))
            sources[os.path.basename(up)] = {"unity_path": up, "nodes": {}}
        tex_done = set()
        for up in WANT_PREFAB:
            text = read(guid_for[up])
            # MeshFilter (class 33) names the FBX through its mesh guid, MeshRenderer (class 23) the materials; pair them by GameObject
            go_name, mf, mr = {}, {}, {}
            for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
                cls, fid, body = int(m.group(1)), m.group(2), m.group(3)
                if cls == 1:
                    go_name[fid] = re.search(r"m_Name: (.*)", body).group(1).strip()
                elif cls in (33, 23):
                    go = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", body).group(1)
                    (mf if cls == 33 else mr)[go] = body
            for go, body in mr.items():
                g = guid_of(re.search(r"m_Materials:\n\s+- (\{[^}]*\})", body).group(1))
                mat = read(g)
                tex = guid_of(re.search(r"_MainTex:\s+m_Texture: (\{[^}]*\})", mat).group(1))
                tpath = path_of.get(tex)
                if tpath and tpath not in tex_done:
                    shutil.copyfile(os.path.join(tmp, tex, "asset"), os.path.join(proj, "textures", os.path.basename(tpath)))
                    tex_done.add(tpath)
                mesh_guid = guid_of(mf[go]) if go in mf else None
                fbx = os.path.basename(path_of.get(mesh_guid, "?"))
                sources.setdefault(fbx, {"unity_path": path_of.get(mesh_guid), "nodes": {}})["nodes"][go_name[go]] = {
                    "material": re.search(r"m_Name: (.*)", mat).group(1).strip(), "texture": os.path.basename(tpath) if tpath else None}
        with open(os.path.join(proj, "sources.json"), "w") as f:
            json.dump(sources, f, indent=1)
        gp = os.path.join(proj, "project.godot")
        if not os.path.exists(gp):
            open(gp, "w").write('config_version=5\n\n[application]\n\nconfig/name="synty-simple-military"\nconfig/features=PackedStringArray("4.7", "Forward Plus")\n')
        print("extracted", len(WANT_FBX), "FBX,", len(os.listdir(os.path.join(proj, "textures"))), "textures ->", proj)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
