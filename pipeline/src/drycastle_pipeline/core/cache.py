"""Disk cache for raw downloads: each file has a sidecar JSON; a manifest lists everything."""

import hashlib
import json
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

SIDECAR_SUFFIX = ".meta.json"
MANIFEST_NAME = "manifest.json"


@dataclass(frozen=True)
class Source:
    name: str
    licence: str
    licence_url: str
    attribution: str


def sidecar_path(path: Path) -> Path:
    return path.with_name(path.name + SIDECAR_SUFFIX)


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def is_fresh(path: Path, request: dict) -> bool:
    """True when the file and sidecar exist, the request is unchanged and the hash matches."""
    meta_path = sidecar_path(path)
    if not (path.is_file() and meta_path.is_file()):
        return False
    try:
        meta = json.loads(meta_path.read_text())
    except json.JSONDecodeError:
        return False
    return meta.get("request") == request and meta.get("sha256") == sha256_of(path)


def fetch_cached(
    path: Path,
    source: Source,
    request: dict,
    fetch: Callable[[], tuple[bytes, str]],
    *,
    force: bool = False,
) -> bool:
    """Fetch into ``path`` unless cached. Returns True if a download happened.

    ``request`` holds the parameters that define the content (query, bbox, layer...); the
    cache is valid only while it is unchanged. ``fetch`` returns (body, url actually used).
    """
    if not force and is_fresh(path, request):
        return False
    body, url = fetch()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".part")
    tmp.write_bytes(body)
    tmp.replace(path)
    meta = {
        "file": path.name,
        "source": source.name,
        "licence": source.licence,
        "licence_url": source.licence_url,
        "attribution": source.attribution,
        "url": url,
        "request": request,
        "fetched_at": datetime.now(UTC).isoformat(timespec="seconds"),
        "sha256": hashlib.sha256(body).hexdigest(),
        "size_bytes": len(body),
    }
    sidecar_path(path).write_text(json.dumps(meta, indent=2, sort_keys=True) + "\n")
    return True


def write_manifest(raw_dir: Path, sources: list[Source], files: list[Path]) -> Path:
    """List sources, licences, required attribution text and the files they produced."""
    entries = []
    for path in files:
        meta = json.loads(sidecar_path(path).read_text())
        entries.append(
            {
                "file": path.name,
                "source": meta["source"],
                "sha256": meta["sha256"],
                "size_bytes": meta["size_bytes"],
                "fetched_at": meta["fetched_at"],
                "metadata": sidecar_path(path).name,
            }
        )
    manifest = {
        "sources": [
            {
                "name": s.name,
                "licence": s.licence,
                "licence_url": s.licence_url,
                "attribution": s.attribution,
            }
            for s in sources
        ],
        "attribution": list(dict.fromkeys(s.attribution for s in sources)),
        "files": entries,
    }
    out = raw_dir / MANIFEST_NAME
    out.write_text(json.dumps(manifest, indent=2) + "\n")
    return out
