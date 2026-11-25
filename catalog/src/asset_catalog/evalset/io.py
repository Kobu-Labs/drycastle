"""Reading and writing the evaluation set.

In the repo (human-edited): catalog/evalset/<set>.yaml and <set>.overrides.yaml.
In the data dir (private, generated): <data_dir>/evalset/judgments/<set>.jsonl, pools/, drafts/.
"""

from __future__ import annotations

import json
import sys
import threading
from datetime import UTC, datetime
from pathlib import Path

import yaml
from pydantic import BaseModel

from .. import config
from .models import Judgment, Override, QuerySet

SET_DIR = config.CATALOG_DIR / "evalset"

# query id -> asset id -> grade
Qrels = dict[str, dict[str, int]]


def data_root() -> Path:
    return config.load().data_dir / "evalset"


def queryset_path(name: str) -> Path:
    return SET_DIR / f"{name}.yaml"


def overrides_path(name: str) -> Path:
    return SET_DIR / f"{name}.overrides.yaml"


def pool_path(name: str) -> Path:
    return data_root() / "pools" / f"{name}.json"


def now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


def load_queryset(name: str, path: Path | None = None) -> QuerySet:
    raw = yaml.safe_load((path or queryset_path(name)).read_text())
    return QuerySet.model_validate(raw)


def dump_queryset(qs: QuerySet) -> str:
    """YAML for people: defaults left out, one query per block, order kept."""
    data = qs.model_dump(mode="json", exclude_defaults=True)
    data["queries"] = [q.model_dump(mode="json", exclude_defaults=True) for q in qs.queries]
    header = (
        "# Evaluation queries. Edit freely: fix wording, delete bad queries, add your own.\n"
        "# Changing a query's text or filters makes its model judgments stale (they are redone).\n"
        "# Correct a grade in <set>.overrides.yaml, not here.\n"
    )
    return header + yaml.safe_dump(data, sort_keys=False, allow_unicode=True, width=96)


def save_queryset(qs: QuerySet, path: Path | None = None, overwrite: bool = False) -> Path:
    path = path or queryset_path(qs.name)
    if path.exists() and not overwrite:
        raise FileExistsError(f"{path} exists; it may hold human edits (use --force to replace)")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(dump_queryset(qs))
    return path


def load_overrides(name: str, path: Path | None = None) -> list[Override]:
    p = path or overrides_path(name)
    if not p.exists():
        return []
    raw = yaml.safe_load(p.read_text()) or {}
    return [Override.model_validate(o) for o in raw.get("overrides") or []]


def write_json(path: Path, data: BaseModel | dict | list) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, BaseModel):
        data = data.model_dump(mode="json")
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, indent=1, ensure_ascii=False))
    tmp.replace(path)


def read_candidates(src: str) -> dict[str, list[str]]:
    """Query id -> asset ids, from a JSON/YAML file or '-' for stdin."""
    text = sys.stdin.read() if src == "-" else Path(src).read_text()
    raw = yaml.safe_load(text) or {}
    return {str(q): [str(a) for a in ids] for q, ids in raw.items()}


class JudgmentStore:
    """Append-only JSON lines. Later lines win, so a re-judgment never needs a rewrite."""

    def __init__(self, name: str, root: Path | None = None):
        self.path = (root or data_root()) / "judgments" / f"{name}.jsonl"
        self._lock = threading.Lock()

    def all(self) -> dict[tuple[str, str], Judgment]:
        out: dict[tuple[str, str], Judgment] = {}
        if self.path.exists():
            for line in self.path.read_text().splitlines():
                if line.strip():
                    j = Judgment.model_validate_json(line)
                    out[(j.query_id, j.asset_id)] = j
        return out

    def add(self, judgments: list[Judgment]) -> None:
        if not judgments:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        lines = "".join(j.model_dump_json() + "\n" for j in judgments)
        with self._lock, self.path.open("a") as f:
            f.write(lines)


def effective_judgments(
    qs: QuerySet, store: JudgmentStore, overrides: list[Override] | None = None
) -> dict[tuple[str, str], Judgment]:
    """Model judgments for the current wording of each query, then human overrides on top."""
    digests = {q.id: q.digest() for q in qs.queries}
    out = {key: j for key, j in store.all().items() if digests.get(j.query_id) == j.query_digest}
    for o in load_overrides(qs.name) if overrides is None else overrides:
        if o.query not in digests:
            raise ValueError(f"override for unknown query {o.query!r} ({o.asset})")
        out[(o.query, o.asset)] = Judgment(
            query_id=o.query,
            asset_id=o.asset,
            grade=o.grade,
            judged_by="human",
            judge=o.by,
            reason=o.reason,
            query_digest=digests[o.query],
        )
    return out


def load_qrels(
    qs: QuerySet, store: JudgmentStore | None = None, overrides: list[Override] | None = None
) -> Qrels:
    qrels: Qrels = {q.id: {} for q in qs.queries}
    for (qid, aid), j in effective_judgments(
        qs, store or JudgmentStore(qs.name), overrides
    ).items():
        qrels[qid][aid] = j.grade
    return qrels
