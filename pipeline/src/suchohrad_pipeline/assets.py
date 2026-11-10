"""The asset library manifest (``assets/manifest.json``) and asset reference validation.

An asset reference is ``pack:<pack>/<model>`` (a glTF from a curated pack) or ``prim:<name>`` (a
pipeline primitive, see ``primitives.py``). The manifest lists both.
"""

import json
from pathlib import Path

from suchohrad_pipeline.config import REPO_ROOT

ASSETS_DIR = REPO_ROOT / "assets"
DEFAULT_MANIFEST = ASSETS_DIR / "manifest.json"
MANIFEST_FORMAT_VERSION = 1
CATEGORIES = ("car", "slab", "pot", "flower", "bush", "tree", "wall", "fence", "other")


def load_manifest(path: Path = DEFAULT_MANIFEST) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


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
