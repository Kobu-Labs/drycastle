"""The index: every record in a SQLite file, filterable, browsable and keyword-searchable.

catalog.db is rebuilt from the records each time and swapped in atomically. It holds the full
record as JSON, one row per asset with the columns filters need, one row per (asset, facet
term) for the multi-valued facets, the exact-match keys, the word list for spelling, and the
FTS5 table the keyword retriever searches. The records are also written as JSONL per pack,
the human-readable source of truth.
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import threading
from collections import Counter
from pathlib import Path

from . import config, record
from .record import CatalogRecord
from .search.text import key_of, name_key, name_words, words
from .search.types import Filters

SCHEMA_VERSION = 1
# FTS columns in the order bm25() weights are given
FTS_COLUMNS = (
    "plain_name",
    "aliases",
    "name",
    "category",
    "facets",
    "likely_queries",
    "distinguishing",
    "usage",
    "description",
)
LINE_COLUMNS = frozenset({"aliases", "likely_queries"})
# keeps a phrase from matching across the end of one alias and the start of the next
LINE_SEPARATOR = "zzsep"
# facets with several values per asset, as stored in the facets table
LIST_FACETS = {
    "material": "materials",
    "setting": "settings",
    "placement": "placements",
    "function": "functions",
    "style": "styles",
    "condition": "condition",
    "colour": "colours",
}
FACET_NAMES = ("kind", "category", "size_class", "pack", *LIST_FACETS)

_SCHEMA = """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE assets (
    rid INTEGER PRIMARY KEY,
    id TEXT NOT NULL UNIQUE,
    source TEXT NOT NULL, pack TEXT NOT NULL, pack_id TEXT NOT NULL,
    kind TEXT NOT NULL, category TEXT NOT NULL, size_class TEXT NOT NULL,
    variant_group TEXT,
    width_m REAL NOT NULL, height_m REAL NOT NULL, depth_m REAL NOT NULL,
    longest_m REAL NOT NULL, has_rig INTEGER NOT NULL,
    record TEXT NOT NULL
);
CREATE INDEX assets_group ON assets (variant_group);
CREATE TABLE facets (
    facet TEXT NOT NULL, term TEXT NOT NULL, rid INTEGER NOT NULL,
    PRIMARY KEY (facet, term, rid)
) WITHOUT ROWID;
CREATE INDEX facets_rid ON facets (rid);
CREATE TABLE keys (rid INTEGER NOT NULL, field TEXT NOT NULL, key TEXT NOT NULL, ntok INTEGER NOT NULL);
CREATE INDEX keys_key ON keys (key);
CREATE TABLE words (word TEXT PRIMARY KEY, df INTEGER NOT NULL) WITHOUT ROWID;
CREATE VIRTUAL TABLE fts USING fts5(
    plain_name, aliases, name, category, facets, likely_queries, distinguishing, usage,
    description,
    tokenize = 'porter unicode61 remove_diacritics 2'
);
"""


def index_path(model: str, data_dir: Path | None = None) -> Path:
    return (data_dir or config.load().data_dir) / "index" / model / "catalog.db"


def records_dir(model: str, data_dir: Path | None = None) -> Path:
    return (data_dir or config.load().data_dir) / "records" / model


# ---------------------------------------------------------------- what goes into each table


def category_prefixes(category: str) -> list[str]:
    """ "a/b/c" -> ["a", "a/b", "a/b/c"]."""
    parts = category.split("/")
    return ["/".join(parts[: i + 1]) for i in range(len(parts))]


def facet_rows(r: CatalogRecord) -> set[tuple[str, str]]:
    rows = {("kind", r.kind), ("size_class", r.size_class), ("pack", f"{r.source}/{r.pack}")}
    for c in [r.category, *r.secondary_categories]:
        rows |= {("category", p) for p in category_prefixes(c)}
    for facet, field in LIST_FACETS.items():
        rows |= {(facet, t) for t in getattr(r, field)}
    return rows


def _lines(texts: list[str]) -> str:
    return f" {LINE_SEPARATOR} ".join(" ".join(words(t)) for t in texts if words(t))


def fts_row(r: CatalogRecord) -> dict[str, str]:
    """The text of each FTS column. Hyphens and underscores become spaces, so "wall-mounted"
    and "SM_Prop_WaterCooler_01" are indexed as the words a person would type."""
    terms = [r.kind, *r.materials, *r.settings, *r.placements, *r.functions, *r.styles]
    terms += [*r.condition, *r.colours]
    paths = [r.category, *r.secondary_categories]
    row = {
        "plain_name": " ".join(words(r.plain_name)),
        "aliases": _lines(r.aliases),
        "name": " ".join(name_words(r.name)),
        "category": " ".join(w for p in paths for w in words(p)),
        "facets": " ".join(w for t in terms for w in words(t)),
        "likely_queries": _lines(r.likely_queries),
        "distinguishing": " ".join(words(r.distinguishing)),
        "usage": " ".join(words(" ".join([r.usage, *r.snaps_with]))),
        "description": " ".join(words(r.description)),
    }
    return {c: row[c] for c in FTS_COLUMNS}


def key_rows(r: CatalogRecord) -> list[tuple[str, str, int]]:
    """(field, key, token count): phrases a query can equal exactly, for the rank-first bonus."""
    rows = [("plain_name", key_of(words(r.plain_name)))]
    rows += [("alias", key_of(words(a))) for a in r.aliases]
    rows += [("name", name_key(r.name)), ("name_base", name_key(r.name, with_number=False))]
    rows += [("likely_query", key_of(words(q))) for q in r.likely_queries]
    seen: set[tuple[str, str]] = set()
    out = []
    for field, key in rows:
        if key and (field, key) not in seen:
            seen.add((field, key))
            out.append((field, key, len(key.split())))
    return out


# ---------------------------------------------------------------- building


def _insert(db: sqlite3.Connection, rid: int, r: CatalogRecord) -> None:
    w, h, d = r.size_m
    pack_id = f"{r.source}/{r.pack}"
    db.execute(
        "INSERT INTO assets VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (rid, r.id, r.source, r.pack, pack_id, r.kind, r.category, r.size_class, r.variant_group,
         w, h, d, r.longest_m, int(r.has_rig), r.model_dump_json()),
    )  # fmt: skip
    db.executemany(
        "INSERT INTO facets VALUES (?,?,?)", [(f, t, rid) for f, t in sorted(facet_rows(r))]
    )
    db.executemany("INSERT INTO keys VALUES (?,?,?,?)", [(rid, *k) for k in key_rows(r)])
    row = fts_row(r)
    db.execute(
        f"INSERT INTO fts (rowid, {', '.join(FTS_COLUMNS)}) VALUES (?{',?' * len(FTS_COLUMNS)})",
        (rid, *row.values()),
    )


def _word_counts(records: list[CatalogRecord]) -> Counter[str]:
    """Document frequency of every word any text column holds, as typed (no stemming)."""
    df: Counter[str] = Counter()
    for r in records:
        text = " ".join(fts_row(r).values())
        df.update(set(text.split()) - {LINE_SEPARATOR})
    return df


def build_db(records: list[CatalogRecord], model: str, path: Path) -> None:
    """Write a complete index to path (which must not exist)."""
    db = sqlite3.connect(path)
    try:
        db.executescript("PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;" + _SCHEMA)
        for rid, r in enumerate(sorted(records, key=lambda r: r.id), 1):
            _insert(db, rid, r)
        db.executemany("INSERT INTO words VALUES (?,?)", sorted(_word_counts(records).items()))
        meta = {"schema": SCHEMA_VERSION, "model": model, "assets": len(records)}
        db.executemany("INSERT INTO meta VALUES (?,?)", [(k, str(v)) for k, v in meta.items()])
        db.execute("INSERT INTO fts(fts) VALUES ('optimize')")
        db.commit()
    finally:
        db.close()


def write_jsonl(records: list[CatalogRecord], model: str, data_dir: Path | None = None) -> Path:
    """One file per pack, records sorted by id; packs that are gone have their file removed."""
    root = records_dir(model, data_dir)
    by_pack: dict[Path, list[CatalogRecord]] = {}
    for r in sorted(records, key=lambda r: r.id):
        by_pack.setdefault(root / r.source / f"{r.pack}.jsonl", []).append(r)
    for path, recs in by_pack.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(
            "".join(json.dumps(r.model_dump(mode="json"), ensure_ascii=False) + "\n" for r in recs)
        )
        tmp.replace(path)
    for stale in set(root.rglob("*.jsonl")) - set(by_pack):
        stale.unlink()
    return root


def build(records: list[CatalogRecord], model: str, data_dir: Path | None = None) -> Path:
    """Rebuild the index and the JSONL from scratch; the old index stays until the new is whole."""
    write_jsonl(records, model, data_dir)
    path = index_path(model, data_dir)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    tmp.unlink(missing_ok=True)
    try:
        build_db(records, model, tmp)
        tmp.replace(path)
    finally:
        tmp.unlink(missing_ok=True)
    return path


# ---------------------------------------------------------------- filters


def _like_escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


def where(f: Filters, a: str = "a") -> tuple[str, list]:
    """SQL condition (on the assets table aliased a) for a Filters, and its parameters."""
    sql: list[str] = []
    args: list = []

    def has(facet: str, terms: list[str]) -> None:
        marks = ",".join("?" * len(terms))
        sql.append(
            f"EXISTS (SELECT 1 FROM facets x WHERE x.rid = {a}.rid AND x.facet = ? "
            f"AND x.term IN ({marks}))"
        )
        args.extend([facet, *terms])

    def column(col: str, vals: list[str]) -> None:
        sql.append(f"{a}.{col} IN ({','.join('?' * len(vals))})")
        args.extend(vals)

    def clean(vals: list[str]) -> list[str]:
        return [v.strip().lower() for v in vals if v.strip()]

    if kinds := clean(f.kinds):
        column("kind", kinds)
    if cats := [c.strip("/") for c in clean(f.categories)]:
        has("category", cats)
    for facet, field in LIST_FACETS.items():
        if facet != "colour" and (vals := clean(getattr(f, field))):
            has(facet, vals)
    if cols := clean(f.colours):
        # "grey" is what the searcher stated, so it also takes "dark grey" and "light grey"
        each = "(x.term = ? OR x.term LIKE ? ESCAPE '\\')"
        sql.append(
            f"EXISTS (SELECT 1 FROM facets x WHERE x.rid = {a}.rid AND x.facet = 'colour' "
            f"AND ({' OR '.join([each] * len(cols))}))"
        )
        for c in cols:
            args.extend([c, f"% {_like_escape(c)}"])
    if packs := [p.strip().strip("/") for p in f.packs if p.strip()]:
        marks = ",".join("?" * len(packs))
        sql.append(f"({a}.pack_id IN ({marks}) OR {a}.pack IN ({marks}))")
        args.extend(packs * 2)
    if sizes := clean(f.size_classes):
        column("size_class", sizes)
    ranges = [
        ("width_m", f.min_width_m, ">="), ("width_m", f.max_width_m, "<="),
        ("height_m", f.min_height_m, ">="), ("height_m", f.max_height_m, "<="),
        ("depth_m", f.min_depth_m, ">="), ("depth_m", f.max_depth_m, "<="),
        ("longest_m", f.min_longest_m, ">="), ("longest_m", f.max_longest_m, "<="),
    ]  # fmt: skip
    for col, bound, op in ranges:
        if bound is not None:
            sql.append(f"{a}.{col} {op} ?")
            args.append(bound)
    if f.rigged is not None:
        sql.append(f"{a}.has_rig = ?")
        args.append(int(f.rigged))
    if f.exclude_ids:
        sql.append(f"{a}.id NOT IN ({','.join('?' * len(f.exclude_ids))})")
        args.extend(f.exclude_ids)
    return (" AND ".join(sql) or "1"), args


# ---------------------------------------------------------------- reading


class Index:
    """Read access to one model's index. Safe to share between threads."""

    def __init__(self, path: Path):
        if not path.exists():
            raise FileNotFoundError(f"no index at {path}; run: catalog index --model <model>")
        self.path = path
        self._local = threading.local()
        meta = dict(self.db.execute("SELECT key, value FROM meta"))
        if int(meta.get("schema", 0)) != SCHEMA_VERSION:
            raise RuntimeError(f"index {path} has schema {meta.get('schema')}; rebuild it")
        self.model = meta["model"]
        self.size = int(meta["assets"])
        self._words: dict[str, int] | None = None

    @property
    def db(self) -> sqlite3.Connection:
        conn = getattr(self._local, "conn", None)
        if conn is None:
            conn = sqlite3.connect(f"file:{self.path}?mode=ro", uri=True)
            self._local.conn = conn
        return conn

    def _records(self, rows: list[tuple[str]]) -> list[CatalogRecord]:
        return [CatalogRecord.model_validate_json(r[0]) for r in rows]

    def get(self, asset_id: str) -> CatalogRecord:
        """The record; KeyError for an unknown id."""
        rows = self.db.execute("SELECT record FROM assets WHERE id = ?", (asset_id,)).fetchall()
        if not rows:
            raise KeyError(asset_id)
        return self._records(rows)[0]

    def get_many(self, ids: list[str]) -> list[CatalogRecord]:
        """Records in the order of ids; unknown ids are skipped."""
        if not ids:
            return []
        rows = self.db.execute(
            "SELECT id, record FROM assets WHERE id IN (SELECT value FROM json_each(?))",
            (json.dumps(ids),),
        ).fetchall()
        by_id = {i: CatalogRecord.model_validate_json(rec) for i, rec in rows}
        return [by_id[i] for i in dict.fromkeys(ids) if i in by_id]

    def all_ids(self) -> list[str]:
        return [r[0] for r in self.db.execute("SELECT id FROM assets ORDER BY id")]

    def filter_ids(self, filters: Filters) -> set[str]:
        """Ids satisfying every field of filters; OR within a field, AND across fields."""
        cond, args = where(filters)
        return {r[0] for r in self.db.execute(f"SELECT id FROM assets a WHERE {cond}", args)}

    def facet_counts(self, filters: Filters | None = None) -> dict[str, dict[str, int]]:
        """Per facet, term -> number of assets passing the filters that carry it.

        Categories count at every level of the path ("furniture", "furniture/seating", ...),
        for primary and secondary categories alike; an asset counts once per term.
        """
        cond, args = where(filters or Filters())
        rows = self.db.execute(
            f"SELECT f.facet, f.term, COUNT(*) FROM facets f JOIN assets a ON a.rid = f.rid "
            f"WHERE {cond} GROUP BY f.facet, f.term ORDER BY f.facet, COUNT(*) DESC, f.term",
            args,
        )
        out: dict[str, dict[str, int]] = {name: {} for name in FACET_NAMES}
        for facet, term, n in rows:
            out[facet][term] = n
        return out

    def siblings(self, asset_id: str) -> list[str]:
        """Other members of the asset's variant group, by id; empty when it has none."""
        rows = self.db.execute(
            "SELECT b.id FROM assets a JOIN assets b ON b.variant_group = a.variant_group "
            "AND b.rid != a.rid WHERE a.id = ? ORDER BY b.id",
            (asset_id,),
        )
        return [r[0] for r in rows]

    def variant_groups(self) -> dict[str, list[str]]:
        """Variant group -> member ids, for every group with members."""
        out: dict[str, list[str]] = {}
        for gid, aid in self.db.execute(
            "SELECT variant_group, id FROM assets WHERE variant_group IS NOT NULL ORDER BY id"
        ):
            out.setdefault(gid, []).append(aid)
        return out

    def fts_scores(
        self, expr: str, filters: Filters, weights: tuple[float, ...]
    ) -> dict[str, float]:
        """BM25 (higher is better) of every asset passing filters that matches an FTS5 expr.

        The caller builds expr from sanitised words only; the filters run in the same SQL.
        """
        cond, args = where(filters)
        rows = self.db.execute(
            f"SELECT a.id, -bm25(fts, {','.join(map(str, weights))}) FROM fts "
            f"JOIN assets a ON a.rid = fts.rowid WHERE fts MATCH ? AND {cond}",
            [expr, *args],
        )
        return dict(rows)

    def fts_count(self, expr: str) -> int:
        """Number of assets matching an FTS5 expr, filters ignored (document frequency)."""
        return self.db.execute("SELECT COUNT(*) FROM fts WHERE fts MATCH ?", (expr,)).fetchone()[0]

    def fts_highlights(self, expr: str, ids: list[str]) -> dict[str, dict[str, str]]:
        """Per asset, per FTS column that matched: the column text with matches in « »."""
        parts = [f"highlight(fts, {i}, '«', '»')" for i in range(len(FTS_COLUMNS))]
        rows = self.db.execute(
            f"SELECT a.id, {', '.join(parts)} FROM fts JOIN assets a ON a.rid = fts.rowid "
            f"WHERE fts MATCH ? AND a.id IN (SELECT value FROM json_each(?))",
            (expr, json.dumps(ids)),
        )
        return {
            row[0]: {c: t for c, t in zip(FTS_COLUMNS, row[1:], strict=True) if "«" in t}
            for row in rows
        }

    def key_matches(self, key: str, filters: Filters, max_extra: int) -> list[tuple[str, str, int]]:
        """(id, field, extra words): keys equal to key, or containing it as whole words with
        at most max_extra more words. Extra 0 is an exact match."""
        cond, args = where(filters)
        rows = self.db.execute(
            f"SELECT a.id, k.field, k.ntok FROM keys k JOIN assets a ON a.rid = k.rid "
            f"WHERE instr(' ' || k.key || ' ', ?) > 0 AND k.ntok <= ? AND {cond}",
            [f" {key} ", len(key.split()) + max_extra, *args],
        )
        n = len(key.split())
        return [(i, field, ntok - n) for i, field, ntok in rows]

    def words(self) -> dict[str, int]:
        """Every word in the text columns, with its document frequency (cached)."""
        if self._words is None:
            self._words = dict(self.db.execute("SELECT word, df FROM words"))
        return self._words


def cli(args: argparse.Namespace) -> None:
    from .stages import describe, ingest

    everything = ingest.read_index()
    records = record.load_all(everything, args.model)
    todo = sum(1 for a in everything if describe.describable(a))
    path = build(records, args.model)
    packs = len({(r.source, r.pack) for r in records})
    print(f"{len(records)} records of {todo} describable assets, {packs} packs")
    print(f"index -> {path}\nrecords -> {records_dir(args.model)}")


def open_index(model: str, data_dir: Path | None = None) -> Index:
    return Index(index_path(model, data_dir))
