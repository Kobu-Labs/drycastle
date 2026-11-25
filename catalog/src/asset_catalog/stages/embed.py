"""Embed: text and picture vectors for every record, cached by content.

Text vectors per asset: "what it is", "what it is for", and one per likely query (max-sim over
an asset's vectors finds need-based queries that the descriptions only imply). Picture vectors:
one per rendered view, overlays erased and cut to the model. Everything is keyed by the exact
text or picture bytes, in a folder per (model, recipe), so a second model sits beside the
first and unchanged assets are never embedded again.
"""

from __future__ import annotations

import argparse
import sys
import time

from PIL import Image

from .. import config, record, store
from ..config import Embeddings, ImageEmbedding, TextEmbedding
from ..embedding import images, models
from ..embedding.space import IMAGE, TEXT, Index, Row, Space, content_key, file_key
from ..record import CatalogRecord
from . import ingest, render

TEXT_RECIPE = "t1"  # bump when the texts' composition changes in a way their hash cannot show
IMAGE_RECIPE = "i1"  # bump when the picture preparation code changes
PICTURE_BATCH = 16
TEXT_CHUNK = 256


def text_recipe(cfg: TextEmbedding) -> str:
    return f"{TEXT_RECIPE}-l{cfg.max_length}" + (f"-{cfg.quantize}" if cfg.quantize else "")


def image_recipe(cfg: ImageEmbedding) -> str:
    return f"{IMAGE_RECIPE}-{cfg.prep}" + (f"-m{cfg.crop_margin:g}" if cfg.prep == "crop" else "")


def text_space(cfg: TextEmbedding) -> Space:
    return Space(TEXT, cfg.model, text_recipe(cfg))


def image_space(cfg: ImageEmbedding) -> Space:
    return Space(IMAGE, cfg.model, image_recipe(cfg))


class TextItem:
    """One text to embed for an asset, as a query-side or document-side text."""

    def __init__(self, id: str, kind: str, label: str, prompt: str, text: str, is_query: bool):
        self.row = Row(id=id, kind=kind, label=label, key=content_key(prompt, text))
        self.text, self.is_query = text, is_query


def text_items(rec: CatalogRecord, cfg: Embeddings) -> list[TextItem]:
    doc = cfg.text.document_prompt
    items = [
        TextItem(rec.id, "what_it_is", "", doc, rec.what_it_is(), False),
        TextItem(rec.id, "what_it_is_for", "", doc, rec.what_it_is_for(), False),
    ]
    if cfg.likely_queries:
        as_query = cfg.text.likely_query_prompt == "query"
        prompt = cfg.text.query_prompt if as_query else doc
        seen: set[str] = set()
        for q in (q.strip() for q in rec.likely_queries):
            if q and q.lower() not in seen:
                seen.add(q.lower())
                items.append(TextItem(rec.id, "likely_query", q, prompt, q, as_query))
    return items


def merge_rows(old: Index | None, new: list[Row], processed: set[str], keep_others: bool):
    """The new index rows: the processed assets' fresh rows, plus the old rows of the rest."""
    kept = [r for r in old.rows if r.id not in processed] if old and keep_others else []
    return sorted(kept + new, key=lambda r: r.id)


def _log(msg: str) -> None:
    print(f"[embed] {msg}", file=sys.stderr)


class Progress:
    """Prints done/total, rate and time left, at most every few seconds."""

    def __init__(self, label: str, total: int):
        self.label, self.total, self.start, self.last = label, total, time.monotonic(), 0.0

    def step(self, done: int) -> None:
        now = time.monotonic()
        if done < self.total and now - self.last < 10:
            return
        self.last = now
        rate = done / max(now - self.start, 1e-9)
        left = (self.total - done) / rate if rate else 0
        _log(f"{self.label}: {done}/{self.total}, {rate:.2f}/s, {left / 60:.1f} min left")


def embed_texts(records: list[CatalogRecord], describer: str, cfg: Embeddings, **kw) -> dict:
    space = text_space(cfg.text)
    items = [it for rec in records for it in text_items(rec, cfg)]
    force, partial = kw.get("force", False), kw.get("partial", False)
    todo = {it.row.key: it for it in items if force or not space.has(it.row.key)}
    if todo:
        embedder = models.text_embedder(cfg.text)
        for is_query in (True, False):
            group = [it for it in todo.values() if it.is_query == is_query]
            if group:
                encode = embedder.queries if is_query else embedder.documents
                progress = Progress(f"texts ({'queries' if is_query else 'documents'})", len(group))
                for i in range(0, len(group), TEXT_CHUNK):
                    part = group[i : i + TEXT_CHUNK]
                    space.add([it.row.key for it in part], encode([it.text for it in part]))
                    progress.step(i + len(part))
                space.checkpoint()
    rows = merge_rows(space.read_index(describer), [it.row for it in items], _ids(records), partial)
    space.save(Index(describer=describer, rows=rows))
    return {"text_new": len(todo), "text_fresh": len({it.row.key for it in items}) - len(todo)}


def _ids(records: list[CatalogRecord]) -> set[str]:
    return {r.id for r in records}


def embed_images(records: list[CatalogRecord], describer: str, cfg: Embeddings, **kw) -> dict:
    ic = cfg.image
    space = image_space(ic)
    st = store.Store()
    rows, paths = [], {}
    for rec in records:
        for view in ic.views:
            f = st.root / rec.views.get(view, "")
            if rec.views.get(view) and f.exists():
                row = Row(id=rec.id, kind="view", label=view, key=file_key(f))
                rows.append(row)
                paths.setdefault(row.key, (f, rec.id, view))
    force, partial = kw.get("force", False), kw.get("partial", False)
    todo = [k for k in paths if force or not space.has(k)]
    if todo:
        embedder = models.image_embedder(ic, "vision")
        progress = Progress("images", len(todo))
        for n, i in enumerate(range(0, len(todo), PICTURE_BATCH)):
            chunk = todo[i : i + PICTURE_BATCH]
            pics = [_prepared(st, *paths[k], ic) for k in chunk]
            space.add(chunk, embedder.images(pics))
            progress.step(i + len(chunk))
            if n % 10 == 9:
                space.checkpoint()  # a run over thousands of pictures takes hours
    merged = merge_rows(space.read_index(describer), rows, _ids(records), partial)
    space.save(Index(describer=describer, rows=merged))
    return {"image_new": len(todo), "image_fresh": len(paths) - len(todo)}


def _prepared(st: store.Store, path, asset_id: str, view: str, ic: ImageEmbedding) -> Image.Image:
    payload = st.get(render.STAGE, asset_id, render.RenderPayload)
    frame = next((f for f in payload.frames if f.view == view), None) if payload else None
    with Image.open(path) as img:
        return images.prepare(img, frame, view, ic.prep, ic.crop_margin)


def run(
    records: list[CatalogRecord],
    describer: str,
    parts: tuple[str, ...] = (TEXT, IMAGE),
    force: bool = False,
    partial: bool = False,
    cfg: Embeddings | None = None,
) -> dict[str, int]:
    """Embed the records; partial keeps the index rows of assets not in `records`."""
    cfg = cfg or config.load().embeddings
    counts: dict[str, int] = {}
    if TEXT in parts:
        counts |= embed_texts(records, describer, cfg, force=force, partial=partial)
        models.release()
    if IMAGE in parts:
        counts |= embed_images(records, describer, cfg, force=force, partial=partial)
        models.release()
    return counts


def cli(args: argparse.Namespace) -> None:
    records = record.load_all(ingest.read_index(), args.model)
    if args.only:
        records = [r for r in records if args.only in r.id]
    if args.limit:
        records = records[: args.limit]
    parts = (TEXT, IMAGE) if args.part == "all" else (args.part,)
    partial = bool(args.only or args.limit)
    print(run(records, args.model, parts, args.force, partial))
