#!/usr/bin/env python3
"""Makes the runtime models of the mechanic garage's props (godot/shooter/MechanicGarage.cs) out of the Synty POLYGON Construction Unity package.

    python3 -I scripts/construction_pack_glb.py <POLYGON_Construction_Unity_*.unitypackage> [--private DIR]

The models are the `SM_...` names in MechanicGarage.cs (a new prop there is picked up by running this again). The package's FBX files and its atlas go into a scratch Godot
project, Godot imports them, scripts/construction_pack_glb.gd writes `synty/glb/construction/<Model>.glb` and `synty/glb/atlas/construction_01_A.png` under the private assets
folder (default: assets-private/ of the repository, gitignored). The pack is paid: nothing it contains is committed, and the files travel with the private asset pack
(scripts/make_private_pack.py takes `synty/glb/**`; publishing a new pack is a separate step). Without them the garage's solid props are drawn as boxes.
Needs `godot` (4.x) on the PATH.
"""
import argparse
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "godot" / "shooter" / "MechanicGarage.cs"
PACK = "Assets/Synty/PolygonConstruction"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("package", type=Path)
    ap.add_argument("--private", type=Path, default=ROOT / "assets-private")
    args = ap.parse_args()
    wanted = sorted(set(re.findall(r'"(SM_[A-Za-z0-9_]+)"', SOURCE.read_text())))
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        raw = tmp / "unity"
        raw.mkdir()
        with tarfile.open(args.package) as tar:
            tar.extractall(raw, filter="data")
        paths = {}  # path in the package -> its folder of the extracted archive
        for d in raw.iterdir():
            if (d / "pathname").is_file():
                paths[(d / "pathname").read_text().strip()] = d
        proj = tmp / "project"
        (proj / PACK / "Models").mkdir(parents=True)
        (proj / PACK / "Textures").mkdir(parents=True)
        (proj / "project.godot").write_text('config_version=5\n[application]\nconfig/name="synty-construction"\n')
        missing = []
        for name in wanted:
            src = paths.get(f"{PACK}/Models/{name}.fbx")
            if src is None:
                missing.append(name)
                continue
            shutil.copy(src / "asset", proj / PACK / "Models" / f"{name}.fbx")
        atlas = paths.get(f"{PACK}/Textures/PolygonConstruction_Texture_01_A.png")
        if atlas is None or missing:
            print(f"not in the package: {missing or ['the atlas']}", file=sys.stderr)
            return 1
        shutil.copy(atlas / "asset", proj / PACK / "Textures" / "PolygonConstruction_Texture_01_A.png")
        subprocess.run(["godot", "--headless", "--import", "--path", str(proj)], check=True, capture_output=True)
        subprocess.run(["godot", "--headless", "--path", str(proj), "--script", str(ROOT / "scripts" / "construction_pack_glb.gd"), "--", str(args.private)], check=True)
    print(f"{len(wanted)} models -> {args.private}/synty/glb/construction/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
