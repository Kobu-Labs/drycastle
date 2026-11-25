"""A page for a human to review the set: each query with its judged assets, pictures and reasons.

The pictures are referenced where they live (the private render folder), not copied.
"""

from __future__ import annotations

import html
import os
from pathlib import Path

from .. import config
from ..stages import render
from ..store import Store
from . import io as evalio
from .models import Judgment, QuerySet

CSS = """
body{font:14px system-ui;margin:2em;max-width:1200px;background:#fafafa;color:#222}
h2{margin:1.6em 0 .2em}.q{color:#555}.meta{color:#777;font-size:12px}
.row{display:flex;gap:12px;align-items:center;padding:6px 0;border-top:1px solid #ddd}
img{width:150px;height:150px;object-fit:contain;background:#ddd}
.g{font-weight:700;width:2em;text-align:center;font-size:20px}
.g3{color:#0a7d2c}.g2{color:#4b8b00}.g1{color:#b58100}.g0{color:#b00020}
.h{background:#e8f0ff}code{background:#eee;padding:1px 4px}
"""


def _sheet(asset_id: str, st: Store, out: Path) -> str:
    r = st.get(render.STAGE, asset_id, render.RenderPayload)
    if r is None:
        return ""
    return os.path.relpath(st.root / r.contact_sheet, out.parent)


def page(qs: QuerySet, judgments: dict[tuple[str, str], Judgment], out: Path, st: Store) -> str:
    body = [
        f"<h1>{html.escape(qs.name)} evaluation set</h1>",
        (
            "<p class=meta>Grades: 3 ideal, 2 good, 1 acceptable, 0 not relevant. To correct one, "
            f"add it to <code>{evalio.overrides_path(qs.name).name}</code>.</p>"
        ),
    ]
    for q in qs.queries:
        rows = sorted((j for (qid, _), j in judgments.items() if qid == q.id),
                      key=lambda j: (-j.grade, j.asset_id))  # fmt: skip
        flt = q.filters.model_dump(exclude_defaults=True)
        body.append(f"<h2>{q.id} <span class=meta>{q.style}"
                    f"{' / answers: ' + q.answers if q.answers else ''}</span></h2>")  # fmt: skip
        body.append(f"<div class=q>{html.escape(q.text)}</div>")
        if flt:
            body.append(f"<div class=meta>filters: {html.escape(str(flt))}</div>")
        if q.notes:
            body.append(f"<div class=meta>notes: {html.escape(q.notes)}</div>")
        for j in rows:
            body.append(
                f"<div class='row{' h' if j.judged_by == 'human' else ''}'>"
                f"<img src='{html.escape(_sheet(j.asset_id, st, out))}'>"
                f"<div class='g g{j.grade}'>{j.grade}</div>"
                f"<div><code>{html.escape(j.asset_id)}</code><br>{html.escape(j.reason)}"
                f"<br><span class=meta>{j.judged_by}: {html.escape(j.judge)}</span></div></div>"
            )
    return f"<!doctype html><meta charset=utf-8><style>{CSS}</style>" + "\n".join(body)


def write_page(qs: QuerySet, judgments: dict[tuple[str, str], Judgment]) -> Path:
    out = evalio.data_root() / "review" / f"{qs.name}.html"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(page(qs, judgments, out, Store(config.load().data_dir)))
    return out
