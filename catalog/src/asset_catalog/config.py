"""Paths and source roots, read from catalog.yaml."""

from __future__ import annotations

import os
import subprocess
from dataclasses import dataclass, field
from functools import cache
from pathlib import Path

import yaml

CATALOG_DIR = Path(__file__).resolve().parents[2]
REPO_ROOT = CATALOG_DIR.parent


@dataclass(frozen=True)
class Source:
    id: str
    root: Path  # absolute
    pack_depth: int = 1
    pack: str | None = None
    kind_hint: str | None = None
    exclude: tuple[str, ...] = ()


@dataclass(frozen=True)
class TextEmbedding:
    model: str = "Qwen/Qwen3-Embedding-0.6B"
    query_prompt: str = "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:"  # prefix on every search query, as the model card specifies
    document_prompt: str = ""  # prefix on every stored text
    likely_query_prompt: str = "document"  # likely_queries are stored as "query" or "document"
    likely_query_bias: float = -0.1  # added to likely-query scores: they are short and generic
    max_length: int = 512
    batch_size: int = 16
    quantize: str | None = None  # None, "4bit" or "8bit" (bitsandbytes, GPU only)
    dtype: str = "float32"  # fp16 is slower than fp32 on a card without tensor cores
    device: str = "auto"  # auto: the GPU when there is one, else cpu


@dataclass(frozen=True)
class ImageEmbedding:
    model: str = "google/siglip2-so400m-patch14-384"
    views: tuple[str, ...] = ("three_quarter", "front", "top", "back")
    prep: str = "crop"  # raw | clean (overlays erased) | crop (clean, then cut to the model)
    crop_margin: float = 0.08  # share of the model's longer side kept around it
    query_template: str = "{}"  # wraps the query text, e.g. "a 3d render of {}"
    lowercase: bool = True
    batch_size: int = 16
    device: str = "auto"  # the picture tower, for embedding pictures
    query_device: str = "cpu"  # the text tower, for queries: both towers + the text model > 4 GB


@dataclass(frozen=True)
class Embeddings:
    text: TextEmbedding = field(default_factory=TextEmbedding)
    image: ImageEmbedding = field(default_factory=ImageEmbedding)
    likely_queries: bool = True  # one extra text vector per likely query


@dataclass(frozen=True)
class Config:
    data_dir: Path  # absolute
    sources: tuple[Source, ...] = field(default_factory=tuple)
    embeddings: Embeddings = field(default_factory=Embeddings)


def _embeddings(raw: dict) -> Embeddings:
    text = TextEmbedding(**raw.get("text", {}))
    image = raw.get("image", {})
    if "views" in image:
        image = {**image, "views": tuple(image["views"])}
    return Embeddings(
        text=text,
        image=ImageEmbedding(**image),
        likely_queries=raw.get("likely_queries", True),
    )


@cache
def load(path: Path | None = None) -> Config:
    raw = yaml.safe_load((path or CATALOG_DIR / "catalog.yaml").read_text())
    sources = tuple(
        Source(
            id=s["id"],
            root=REPO_ROOT / s["root"],
            pack_depth=s.get("pack_depth", 1),
            pack=s.get("pack"),
            kind_hint=s.get("kind_hint"),
            exclude=tuple(s.get("exclude", ())),
        )
        for s in raw["sources"]
    )
    return Config(
        data_dir=REPO_ROOT / raw["data_dir"],
        sources=sources,
        embeddings=_embeddings(raw.get("embeddings", {})),
    )


def api_key() -> str:
    """ANTHROPIC_API_KEY from the environment, else from .env in this checkout or the main one."""
    if key := os.environ.get("ANTHROPIC_API_KEY"):
        return key
    candidates = [REPO_ROOT / ".env"]
    common = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "rev-parse", "--path-format=absolute", "--git-common-dir"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    if common:
        candidates.append(Path(common).parent / ".env")
    for env in candidates:
        if env.exists():
            for line in env.read_text().splitlines():
                name, _, value = line.partition("=")
                if name.strip() == "ANTHROPIC_API_KEY" and value.strip():
                    return value.strip().strip("'\"")
    raise RuntimeError("ANTHROPIC_API_KEY not set and not found in .env")
