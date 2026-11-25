"""Ingest: scan the source roots, give every asset file an identity."""

from __future__ import annotations

import hashlib
import json
import re
from collections import defaultdict
from fnmatch import fnmatch
from pathlib import Path

from .. import config
from ..model import Asset

MEDIA = {".glb": "model", ".gltf": "model", ".ogg": "audio", ".wav": "audio", ".mp3": "audio"}
_HELPER = re.compile(r"^(.*?)[_-](LOD_?0*[1-9]\d*|Collision|Collider|Col)$", re.IGNORECASE)
_VARIANT = re.compile(r"^(.*?)(?:[_-]?\d+)(?:[_-]?[A-Za-z])?$")


def _hash(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def variant_stem(stem: str) -> str | None:
    """ "SM_Prop_Can_01" -> "SM_Prop_Can"; "chair_02" -> "chair"; None when no number suffix."""
    m = _VARIANT.match(stem)
    return m.group(1).rstrip("_-") or None if m else None


def scan(cfg: config.Config | None = None) -> list[Asset]:
    cfg = cfg or config.load()
    assets: list[Asset] = []
    for src in cfg.sources:
        if not src.root.exists():
            continue
        for path in sorted(src.root.rglob("*")):
            media = MEDIA.get(path.suffix.lower())
            if not media or not path.is_file():
                continue
            rel = path.relative_to(src.root)
            if any(fnmatch(rel.as_posix(), pat) for pat in src.exclude):
                continue
            if src.pack_depth == 0:
                pack, inner = src.pack or src.id, rel
            elif len(rel.parts) > src.pack_depth:
                pack = "/".join(rel.parts[: src.pack_depth])
                inner = Path(*rel.parts[src.pack_depth :])
            else:
                pack, inner = "misc", rel
            stem = inner.with_suffix("").as_posix()
            assets.append(
                Asset(
                    id=f"{src.id}/{pack}/{stem}",
                    source=src.id,
                    pack=pack,
                    name=path.stem,
                    path=path.relative_to(config.REPO_ROOT).as_posix(),
                    media=media,
                    content_hash=_hash(path),
                    bytes=path.stat().st_size,
                    kind_hint=src.kind_hint,
                )
            )
    by_id = {a.id: a for a in assets}
    for a in assets:
        if m := _HELPER.match(a.id):
            # LOD0 is the full model and stays primary; a helper with no base stays primary too
            base = next((b for b in (m.group(1), f"{m.group(1)}_LOD0") if b in by_id), None)
            if base:
                a.role = "lod" if m.group(2).lower().startswith("lod") else "collision"
                a.derived_from = base
    groups: dict[str, list[Asset]] = defaultdict(list)
    for a in assets:
        if a.role != "primary":
            continue
        if (v := variant_stem(a.id.rsplit("/", 1)[-1])) is not None:
            groups[f"{a.id.rsplit('/', 1)[0]}/{v}"].append(a)
    for gid, members in groups.items():
        if len(members) > 1:
            for a in members:
                a.variant_group = gid
    ids = [a.id for a in assets]
    if len(ids) != len(set(ids)):
        dupes = sorted({i for i in ids if ids.count(i) > 1})
        raise ValueError(f"duplicate asset ids: {dupes[:5]}")
    return assets


def index_path(cfg: config.Config | None = None) -> Path:
    return (cfg or config.load()).data_dir / "assets.jsonl"


def write_index(assets: list[Asset]) -> Path:
    p = index_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text("".join(json.dumps(a.model_dump(), sort_keys=True) + "\n" for a in assets))
    return p


def read_index() -> list[Asset]:
    return [Asset.model_validate_json(line) for line in index_path().read_text().splitlines()]
