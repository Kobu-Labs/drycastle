#!/usr/bin/env python3
"""Builds the private asset pack players get: only the files the game reads at runtime, out of assets-private/.

    assets-private/ (916 MB: Unity/Godot source projects, FBX, unbaked clips, tried variants)
      -> out/private-pack/assets-private.zip   (the runtime subset; unzip INTO a folder named assets-private/ next to the game exe)
      -> out/private-pack/assets-private.json  (name, size, sha256, as map.json)

INCLUDE is the whole definition of the pack: a new private folder the game reads must be added here, or players will not get it.
The zip is reproducible (sorted, fixed timestamps): unchanged files give the same sha256. Never upload it to the public GitHub releases.

    python3 -I scripts/make_private_pack.py [--src DIR] [--out DIR] [--list]
"""
import argparse
import fnmatch
import hashlib
import json
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Globs relative to assets-private/ ("**" = any depth). Who reads each is named so a dead entry can be found and dropped.
INCLUDE = [
    "manifest.json",                      # MapLoader.Objects: ids, files and sizes of pack: objects (scripts/private_pack_glb.gd)
    "animal-shapes.json",                 # Shooter.cs: hit shapes of the private animal models (the SERVER needs this one too)
    "animal-silhouettes/*.png",           # NetClient.Hud: hunt silhouettes
    "animal-kit/*",                       # wildlife.json: crow model (+ NOTICE)
    "paintings/hell_*.png",               # ChurchPaintings
    "music/*.ogg", "music/*.mp3",         # Sfx.LoadPrivateMusic
    "audio/ear_ring/*.ogg",               # sounds.json ear.ring (#481)
    "audio/holy_mass/*.ogg",              # ChurchChant, DemonAudio
    "audio/thieves/voice/*.ogg",          # ThiefAudio
    "audio/thieves/songs/*.ogg",          # ThiefAudio (van song)
    "synty/glb/**",                       # SyntyLib.Instance, ModelLib (pets, office, props, vehicles, atlas/); MapLoader.Prefabs (military/: map objects)
    "synty/anim/manifest.json",           # SyntyLib.Loops
    "synty/anim/baked/*.glb",             # SyntyLib.Clip
    "synty/fantasy-characters/textures/PolygonFantasyCharacters_0[1-5]_[ABC].png",  # CitizenView atlas pick
    "synty/gang-warfare/textures/PolygonGangWarfare_0[1-4]_[ABC].png",              # thieves.json atlases
]
# Bake inputs and discarded takes that sit beside the files the game reads.
EXCLUDE = ["audio/holy_mass/*_src.ogg", "audio/holy_mass/*_v[0-9].ogg", "synty/glb/pets/convert.json", "**/*.import", "**/README.md"]


def matches(rel: str, pattern: str) -> bool:
    if pattern.endswith("/**"):
        return rel.startswith(pattern[:-2])
    if pattern.startswith("**/"):
        return fnmatch.fnmatchcase(rel.rsplit("/", 1)[-1], pattern[3:])
    # fnmatch's "*" crosses "/": compare depth too, so "audio/holy_mass/*.ogg" does not take subfolders
    return rel.count("/") == pattern.count("/") and fnmatch.fnmatchcase(rel, pattern)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--src", type=Path, default=ROOT / "assets-private")
    ap.add_argument("--out", type=Path, default=ROOT / "out/private-pack")
    ap.add_argument("--list", action="store_true", help="print the files and sizes, write nothing")
    args = ap.parse_args()
    if not args.src.is_dir():
        print(f"no private assets at {args.src}", file=sys.stderr)
        return 1

    files, used = [], set()
    for p in sorted(args.src.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(args.src).as_posix()
        hit = next((g for g in INCLUDE if matches(rel, g)), None)
        if hit is None or any(matches(rel, g) for g in EXCLUDE):
            continue
        used.add(hit)
        files.append((rel, p))
    dead = [g for g in INCLUDE if g not in used and not g.endswith(".mp3")]
    if dead:
        print("patterns that match no file (pack incomplete, or the entry is stale): " + ", ".join(dead), file=sys.stderr)
        return 1

    by_top: dict[str, int] = {}
    for rel, p in files:
        parts = rel.split("/")
        top = "/".join(parts[:3]) if parts[0] == "synty" and len(parts) > 3 else "/".join(parts[:2]) if len(parts) > 2 else parts[0]
        by_top[top] = by_top.get(top, 0) + p.stat().st_size
    if args.list:
        for rel, p in files:
            print(f"{p.stat().st_size:>10}  {rel}")
    for top, size in sorted(by_top.items(), key=lambda kv: -kv[1]):
        print(f"{size / 1e6:8.1f} MB  {top}")
    total = sum(p.stat().st_size for _, p in files)
    print(f"{total / 1e6:8.1f} MB  {len(files)} files")
    if args.list:
        return 0

    args.out.mkdir(parents=True, exist_ok=True)
    zip_path = args.out / "assets-private.zip"
    with zipfile.ZipFile(zip_path, "w") as z:
        for rel, p in files:
            info = zipfile.ZipInfo(rel, date_time=(1980, 1, 1, 0, 0, 0))
            info.external_attr = 0o644 << 16
            # ogg and png are compressed already
            info.compress_type = zipfile.ZIP_STORED if p.suffix in (".ogg", ".mp3", ".png") else zipfile.ZIP_DEFLATED
            z.writestr(info, p.read_bytes())
    h = hashlib.sha256()
    with open(zip_path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    size = zip_path.stat().st_size
    (args.out / "assets-private.json").write_text(json.dumps({"name": zip_path.name, "size": size, "sha256": h.hexdigest(), "files": len(files)}))
    print(f"{zip_path}: {size / 1e6:.1f} MB, sha256 {h.hexdigest()[:12]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
