#!/usr/bin/env python3
"""Run locally before a release: says which separately published pieces are stale and must be published again first.

    python3 -I scripts/release/check_release_assets.py        (make release-check)

Checks, each printed as ok / REPUBLISH / warn:
  private pack  the pack built now from assets-private/ against private-pack.json (the one players would get), and that the
                encrypted file is still served at its URL with the right size        -> scripts/release/publish-private-pack.sh user@host
  vehicle models  every `pack:` model vehicles.json names (`model` and each of `skins`) is a row of assets-private/manifest.json with its file there, and the
                file is in the pack zip built above (a skin that was never standardized, or left out of the pack, would draw a box)
  map           the files of out/village/map against the map named in map-version.txt (tactical-map.png is ignored: it is
                rendered at publish time)                                             -> scripts/release/publish-map.sh
  launcher      godot/launcher, godot/LauncherCore and the launcher readme against the newest launcher-v* tag
                                                                                      -> git tag launcher-vX.Y.Z && git push origin launcher-vX.Y.Z
  committed     map-version.txt and private-pack.json have no uncommitted change (the release reads them from git)

Exit 1 when something must be republished or committed, 0 when the release can go. Needs gh (map release lookup) and network.
CI cannot do this: the map and the pack are built from gitignored data. Standard library only.
"""
import hashlib
import json
import subprocess
import sys
import urllib.request
import zipfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MAP_DIR = ROOT / "out/village/map"
IGNORED_MAP_FILES = ("tactical-map.png", "tactical-map.png.json")
LAUNCHER_PATHS = ["godot/launcher", "godot/LauncherCore", "docs/launcher-readme.txt"]

stale = False


def report(kind: str, name: str, text: str) -> None:
    global stale
    stale |= kind == "REPUBLISH"
    print(f"{kind:<9} {name:<13} {text}")


def run(*cmd: str) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def check_pack() -> None:
    name = "private pack"
    if not (ROOT / "assets-private").is_dir():
        report("warn", name, "no assets-private/ on this machine: cannot tell whether the pack is current")
        return
    built = run(sys.executable, "-I", "scripts/release/make_private_pack.py")
    if built.returncode != 0:
        report("REPUBLISH", name, "the pack does not build: " + (built.stderr.strip().splitlines() or ["?"])[-1])
        return
    now = json.loads((ROOT / "out/private-pack/assets-private.json").read_text())["sha256"]
    desc = ROOT / "private-pack.json"
    if not desc.is_file():
        report("REPUBLISH", name, "never published (no private-pack.json): scripts/release/publish-private-pack.sh user@host")
        return
    pub = json.loads(desc.read_text())
    if pub["plain_sha256"] != now:
        report("REPUBLISH", name, f"assets-private/ changed since pack {pub['id']} (now {now[:12]}): scripts/release/publish-private-pack.sh user@host")
        return
    try:
        with urllib.request.urlopen(urllib.request.Request(pub["file"]["url"], method="HEAD"), timeout=15) as r:
            size = int(r.headers.get("Content-Length", -1))
    except Exception as e:  # any network failure means players cannot fetch it
        report("REPUBLISH", name, f"pack {pub['id']} is not served at {pub['file']['url']} ({e}): start the pack service or publish again")
        return
    if size != pub["file"]["size"]:
        report("REPUBLISH", name, f"the served file is {size} bytes, private-pack.json says {pub['file']['size']}: publish again")
        return
    report("ok", name, f"{pub['id']} is current and served")


def vehicle_model_refs() -> list[str]:
    """Every `pack:` model id the vehicles of vehicles.json draw: `model` and each of `skins`, in file order, once."""
    refs: list[str] = []
    for v in json.loads((ROOT / "godot/shooter/data/vehicles.json").read_text())["vehicles"]:
        for ref in [v.get("model", "")] + list(v.get("skins", [])):
            if ref.startswith("pack:") and ref not in refs:
                refs.append(ref)
    return refs


def check_vehicle_models() -> None:
    """Per-model coverage (#325): the private pack must hold every model (and skin) the vehicles use. Run after check_pack, which builds the zip."""
    name = "vehicle models"
    refs = vehicle_model_refs()
    private = ROOT / "assets-private"
    if not private.is_dir():
        report("warn", name, f"no assets-private/ on this machine: cannot tell whether the {len(refs)} vehicle models are in the pack")
        return
    manifest = {a["id"]: a for a in json.loads((private / "manifest.json").read_text())["assets"]}
    zip_path = ROOT / "out/private-pack/assets-private.zip"
    packed = set(zipfile.ZipFile(zip_path).namelist()) if zip_path.is_file() else None
    missing = []
    for ref in refs:
        row = manifest.get(ref)
        if row is None:
            missing.append(f"{ref} (no manifest row)")
        elif not (private / row["file"]).is_file():
            missing.append(f"{ref} (no file {row['file']})")
        elif packed is not None and row["file"] not in packed:
            missing.append(f"{ref} (not in the pack zip)")
    if missing:
        shown = "; ".join(missing[:4]) + (f" and {len(missing) - 4} more" if len(missing) > 4 else "")
        report("REPUBLISH", name, f"{len(missing)} of {len(refs)} vehicle models are not in the private pack: {shown}. Run scripts/vehicles/build_vehicle_packs.sh, then publish the pack")
    else:
        report("ok", name, f"all {len(refs)} vehicle models (skins included) are in the pack" + ("" if packed is not None else " tree (no zip to check)"))


def published_map_zip(tag: str) -> Path | None:
    """The zip of the published map: the local out/village/map.zip when it is that one, else a cached download."""
    got = run("gh", "release", "download", tag, "--pattern", "map.json", "--output", "-")
    if got.returncode != 0:
        return None
    want = json.loads(got.stdout)["sha256"]
    local = ROOT / "out/village/map.zip"
    if local.is_file() and sha256(local) == want:
        return local
    cache = ROOT / "out/village" / f"published-{tag}.zip"
    if not (cache.is_file() and sha256(cache) == want):
        if run("gh", "release", "download", tag, "--pattern", "map.zip", "--output", str(cache), "--clobber").returncode != 0:
            return None
    return cache if sha256(cache) == want else None


def check_map() -> None:
    name = "map"
    tag = (ROOT / "map-version.txt").read_text().strip()
    if not (MAP_DIR / "manifest.json").is_file():
        report("warn", name, "no out/village/map on this machine: cannot tell whether the map is current")
        return
    zip_path = published_map_zip(tag)
    if zip_path is None:
        report("REPUBLISH", name, f"release {tag} (map-version.txt) has no readable map.zip: scripts/release/publish-map.sh")
        return
    with zipfile.ZipFile(zip_path) as z:
        published = {i.filename: (i.file_size, i.CRC) for i in z.infolist() if not i.is_dir() and i.filename not in IGNORED_MAP_FILES}
    local = {}
    for p in sorted(MAP_DIR.rglob("*")):
        rel = p.relative_to(MAP_DIR).as_posix()
        if not p.is_file() or rel in IGNORED_MAP_FILES:
            continue
        crc = 0
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                crc = zlib.crc32(chunk, crc)
        local[rel] = (p.stat().st_size, crc)
    changed = sorted(k for k in local.keys() | published.keys() if local.get(k) != published.get(k))
    if changed:
        shown = ", ".join(changed[:4]) + (f" and {len(changed) - 4} more" if len(changed) > 4 else "")
        report("REPUBLISH", name, f"out/village/map differs from {tag} in {len(changed)} files ({shown}): scripts/release/publish-map.sh, commit map-version.txt")
    else:
        report("ok", name, f"{tag} is the local map ({len(local)} files)")


def check_launcher() -> None:
    name = "launcher"
    tags = run("git", "tag", "-l", "launcher-v*", "--sort=-v:refname").stdout.split()
    if not tags:
        report("REPUBLISH", name, "no launcher-v* tag: git tag launcher-v0.1.0 && git push origin launcher-v0.1.0")
        return
    if run("git", "diff", "--quiet", tags[0], "--", *LAUNCHER_PATHS).returncode != 0:
        report("REPUBLISH", name, f"launcher code changed since {tags[0]} (working tree included): tag and push a new launcher-vX.Y.Z, tell players to download it")
    elif run("gh", "release", "view", tags[0], "--json", "tagName").returncode != 0:
        report("REPUBLISH", name, f"tag {tags[0]} has no release: push the tag (git push origin {tags[0]}) and wait for the launcher workflow")
    else:
        report("ok", name, f"{tags[0]} is the current launcher")


def check_committed() -> None:
    dirty = run("git", "status", "--porcelain", "--", "map-version.txt", "private-pack.json").stdout.strip()
    if dirty:
        report("REPUBLISH", "committed", "commit before releasing (the release reads them from git): " + ", ".join(line[3:] for line in dirty.splitlines()))
    else:
        report("ok", "committed", "map-version.txt and private-pack.json are committed")


def main() -> int:
    check_pack()
    check_vehicle_models()
    check_map()
    check_launcher()
    check_committed()
    print("\nNOT ready: publish what is marked REPUBLISH, then run this again." if stale else "\nassets are current: the release can go (PR main -> release).")
    return 1 if stale else 0


if __name__ == "__main__":
    sys.exit(main())
