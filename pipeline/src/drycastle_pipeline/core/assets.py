"""The asset library manifest (``assets/manifest.json``) and asset reference validation.

An asset reference is ``pack:<pack>/<model>`` (a glTF from a curated pack) or ``prim:<name>`` (a
pipeline primitive, see ``kits/primitives.py``). The manifest lists both.
"""

import json
from pathlib import Path

from drycastle_pipeline.core.config import REPO_ROOT

ASSETS_DIR = REPO_ROOT / "assets"
DEFAULT_MANIFEST = ASSETS_DIR / "manifest.json"
# Private (paid) packs: a gitignored folder beside assets/ with its own manifest, written by scripts/packs/private_pack_glb.gd.
# Its assets are placed like any other ("pack:<pack>/<model>"); "file" is relative to that folder and "prefab" says the GLB has its own collision.
PRIVATE_MANIFEST = REPO_ROOT / "assets-private" / "manifest.json"
MANIFEST_FORMAT_VERSION = 1
CATEGORIES = ("car", "slab", "pot", "flower", "bush", "tree", "wall", "fence", "pole", "connector", "sign", "other")


def load_manifest(path: Path = DEFAULT_MANIFEST, private: bool = True) -> dict:
    """The manifest at ``path``; the repository manifest also gets the assets of the private packs when they are installed
    (``private=False`` reads the public library alone)."""
    manifest = json.loads(Path(path).read_text(encoding="utf-8"))
    if private and Path(path).resolve() == DEFAULT_MANIFEST.resolve() and PRIVATE_MANIFEST.is_file():
        private = json.loads(PRIVATE_MANIFEST.read_text(encoding="utf-8"))
        known = asset_ids(manifest)
        manifest["assets"] = [*manifest.get("assets", []), *(a for a in private.get("assets", []) if a["id"] not in known)]
    return manifest


def asset_ids(manifest: dict) -> set[str]:
    return {a["id"] for a in manifest.get("assets", [])}


def validate_asset_ref(ref: str, manifest: dict | None = None) -> str | None:
    """Return None when ``ref`` is a known asset id, else a message saying what is wrong.

    ``manifest`` is the loaded manifest; the repository manifest is read when it is omitted.
    """
    if not isinstance(ref, str) or not ref.startswith(("pack:", "prim:")):
        return f'asset {ref!r}: must start with "pack:" or "prim:"'
    if manifest is None:
        manifest = load_manifest()
    if ref in asset_ids(manifest):
        return None
    kind = "pack model" if ref.startswith("pack:") else "primitive"
    return f"asset {ref!r}: unknown {kind} (not in assets/manifest.json)"
