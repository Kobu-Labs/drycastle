"""Pilot report: compare the describing models of the pilot before choosing one.

Three parts, each cached under <data_dir>/pilot-report/ so a rerun costs nothing:
1. repeat agreement of one model with itself (Haiku run1 vs run2), no LLM;
2. a blind LLM judge compares the three models' records for the same assets against the
   picture and the measured facts, with the order of the records shuffled per asset;
3. a static HTML page to read the three records side by side and choose by eye.
"""

from __future__ import annotations

import argparse
import base64
import html
import json
import os
import random
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from typing import Literal
from urllib.parse import quote

import anthropic
from pydantic import BaseModel, Field, field_validator

from . import config
from .stages import describe_pilot, ingest, render
from .stages.describe_pilot import PilotOutput, PilotRecord
from .store import Store, make_key

JUDGE = "claude-opus-5-5"
JUDGE_VERSION = 1
JUDGE_MAX_TOKENS = 16000  # thinking counts toward it
CANDIDATES = ("claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5")
HAIKU = "claude-haiku-5-5"
RUN = 1  # the run of each candidate that is judged
TARGET_ASSETS = 4440
# USD per million tokens (input, output) at standard prices; the batch API halves them.
PRICES = {
    "claude-haiku-5-5": (0.10, 0.50),
    "claude-sonnet-5-5": (2.0, 10.0),
    "claude-opus-5-5": (4.0, 20.0),
}
BATCH_FACTOR = 0.5
AGREE_VERSION = 1
LIST_FIELDS = (
    "materials",
    "colours",
    "typical_settings",
    "placements",
    "functions",
    "styles",
    "condition",
    "aliases",
)
SHOWN_FIELDS = (
    "plain_name",
    "kind",
    "category_path",
    "description",
    "usage",
    "aliases",
    "materials",
    "typical_settings",
    "placements",
    "functions",
    "styles",
    "condition",
    "likely_queries",
    "doubts",
)

# ---------------------------------------------------------------- 1. repeat agreement


def _norm(xs: list[str]) -> list[str]:
    return [x.strip().lower() for x in xs]


def jaccard(a: list[str], b: list[str]) -> float:
    """Jaccard of the lowercased sets; two empty lists agree completely."""
    sa, sb = set(_norm(a)), set(_norm(b))
    return 1.0 if not sa and not sb else len(sa & sb) / len(sa | sb)


def shared_prefix(a: list[str], b: list[str]) -> int:
    n = 0
    for x, y in zip(_norm(a), _norm(b), strict=False):
        if x != y:
            break
        n += 1
    return n


# What the overall agreement of one asset averages over; every entry is 0..1.
OVERALL = (
    "kind",
    "category_path[0]",
    "category_path prefix share",
    *LIST_FIELDS,
    "real_front",
    "modular",
)


def record_agreement(a: PilotRecord, b: PilotRecord) -> dict[str, float]:
    pa, pb = _norm(a.category_path), _norm(b.category_path)
    depth = shared_prefix(pa, pb)
    longest = max(len(pa), len(pb))
    out = {
        "kind": float(a.kind.strip().lower() == b.kind.strip().lower()),
        "category_path[0]": float(pa[:1] == pb[:1]),
        "category_path exact": float(pa == pb),
        "category_path prefix depth": float(depth),
        "category_path prefix share": depth / longest if longest else 1.0,
    }
    for f in LIST_FIELDS:
        out[f] = jaccard(getattr(a, f), getattr(b, f))
    out["real_front"] = float(a.real_front.strip().lower() == b.real_front.strip().lower())
    out["modular"] = float(a.modular == b.modular)
    out["overall"] = sum(out[f] for f in OVERALL) / len(OVERALL)
    return out


class WeakAsset(BaseModel):
    id: str
    overall: float
    name_run1: str
    name_run2: str
    worst: list[str]  # the fields that agreed least


class Agreement(BaseModel):
    n: int
    means: dict[str, float]
    lowest: list[WeakAsset]


def compute_agreement(pairs: list[tuple[str, PilotRecord, PilotRecord]], lowest: int = 15):
    per = [(i, a, b, record_agreement(a, b)) for i, a, b in pairs]
    names = list(per[0][3]) if per else []
    means = {k: sum(p[3][k] for p in per) / len(per) for k in names}
    weak = []
    for i, a, b, m in sorted(per, key=lambda p: (p[3]["overall"], p[0]))[:lowest]:
        worst = sorted(OVERALL, key=lambda f: m[f])[:3]
        weak.append(
            WeakAsset(
                id=i,
                overall=m["overall"],
                name_run1=a.plain_name,
                name_run2=b.plain_name,
                worst=[f"{f} {m[f]:.2f}" for f in worst],
            )
        )
    return Agreement(n=len(per), means=means, lowest=weak)


def _stored_ids(st: Store, stage: str) -> set[str]:
    base = st.root / stage
    return {p.relative_to(base).with_suffix("").as_posix() for p in base.rglob("*.json")}


def _records(st: Store, model: str, run: int) -> dict[str, PilotOutput]:
    stage = describe_pilot.stage_name(model, run)
    outs = {i: st.get(stage, i, PilotOutput) for i in _stored_ids(st, stage)}
    return {i: o for i, o in outs.items() if o is not None and o.record is not None}


def agreement(st: Store) -> Agreement:
    stages = [describe_pilot.stage_name(HAIKU, r) for r in (1, 2)]
    files = sorted(f for s in stages for f in (st.root / s).rglob("*.json"))
    key = make_key(AGREE_VERSION, [(str(f), f.stat().st_size, f.stat().st_mtime_ns) for f in files])
    if (cached := st.get("pilot-report", "agreement", Agreement, key)) is not None:
        return cached
    r1, r2 = _records(st, HAIKU, 1), _records(st, HAIKU, 2)
    pairs = [(i, r1[i].record, r2[i].record) for i in sorted(r1.keys() & r2.keys())]
    result = compute_agreement(pairs)
    st.put("pilot-report", "agreement", key, result)
    return result


# ---------------------------------------------------------------- 2. blind judging

JUDGE_SYSTEM = """\
You are the judge in a blind comparison of three catalogue records for one 3D game asset.

Why this matters: a project is choosing which language model will write about 4,600 such \
records. Search over the catalogue (by people and by AI agents) will know nothing about an \
asset except its record, so the record decides whether the asset is found when it should be \
and left alone when it should not be. Your verdicts decide which model gets the job, so be \
exact and be fair.

The three records, labelled A, B and C, were written by three different language models in a \
random order. Do not try to guess which model wrote which, and do not let style, length or \
confidence sway you. Each writer saw exactly what you see: one image with four views of the \
asset (front, three-quarter, top, back, each with a scale bar, and a 1.8 m human figure beside \
the front view for most assets), and a block of file context and measured facts. The writers \
were told to describe with open vocabulary, lowercase singular words, to leave a list empty \
rather than pad it, to write at most three typical settings, and to use the pictures as the \
main evidence and the file name only as a hint.

What counts as evidence:
- The picture is the main evidence for what the asset is and looks like.
- The measured facts are exact. A record that contradicts them is wrong. Where a pack is \
marked as not at real-world scale, measured size is not a real-world size.
- The file name is a hint written by the pack's artist. It may be terse, misspelt or wrong. \
If a record follows a name the picture contradicts, that is an error; a record that notices \
the disagreement and says so under "doubts" did well.

How to judge:
- Check every claim in every record against the picture and the measured facts yourself, \
including claims all three records agree on. Do not prefer the most detailed record. A \
record that says less but is right beats one that says more and is partly wrong.
- A confident wrong label is worse than a gap. Wrong name, kind or category sends the asset \
to the wrong searches and hides it from the right ones.
- Padding is a defect. Entries that are only loosely true (a typical setting it could be in \
but is not mainly for, a style it does not show, a function it barely has, an alias that is \
a different object, a likely query that would be better answered by another asset) make the \
asset appear in searches where it does not belong. List them as misleading entries.
- Verbosity is not quality. Count what helps a searcher, not words.
- Honest doubts are fine and should not be punished; doubts used to hedge something plainly \
visible, or doubts that are themselves wrong, are a small defect.
- Do not invent issues to look thorough. Empty lists are normal and expected for a good record.

For each record fill in:
- factual_errors: claims contradicted by the picture or the measured facts. Short strings \
(under 20 words) that name the field and the claim, and say what is actually true.
- misleading_entries: list entries that are only loosely true and would make the asset show \
up in wrong searches. Short strings naming the field and the entry.
- missing: important things a searcher would need that the record omitted (a main function, \
an obvious name people would use, a visible key feature). Short strings. Not every absent \
detail counts; only what would cost a real search.
- accuracy (1 to 5): how much of what is stated is true. 5: nothing wrong. 4: only trivial \
slips. 3: one clear error, or several loose claims. 2: several errors. 1: mostly wrong.
- findability (1 to 5): would the right searches hit this asset, given its name, aliases, \
categories, functions, settings and likely queries? 5: every natural search for it lands. \
3: the obvious searches land, some natural ones miss. 1: it would rarely be found.
- precision (1 to 5): would searches for other things correctly miss it? 5: no entry pulls it \
into wrong searches. 3: a few loose entries or one misleading category. 1: it would show up \
all over the wrong places.
Use the whole scale and keep scores consistent with your lists. Records that differ in \
quality must get different scores.

Finally rank the three records best to worst for the purpose above (no ties; put each of A, \
B and C exactly once) and give one sentence naming the deciding difference."""


class Verdict(BaseModel):
    factual_errors: list[str]
    misleading_entries: list[str]
    missing: list[str]
    accuracy: int = Field(ge=1, le=5)
    findability: int = Field(ge=1, le=5)
    precision: int = Field(ge=1, le=5)


class Judgement(BaseModel):
    A: Verdict
    B: Verdict
    C: Verdict
    ranking: list[Literal["A", "B", "C"]] = Field(description="best to worst, each label once")
    reason: str = Field(description="one sentence")

    @field_validator("ranking")
    @classmethod
    def _permutation(cls, v: list[str]) -> list[str]:
        if sorted(v) != ["A", "B", "C"]:
            raise ValueError(f"ranking must use A, B, C once each: {v}")
        return v


class JudgeOutput(BaseModel):
    judgement: Judgement | None = None
    error: str | None = None
    judge: str
    order: list[str]  # the model behind label A, B, C
    input_tokens: int = 0
    output_tokens: int = 0


_DROP = {"title", "minimum", "maximum", "default", "$defs"}


def _clean(node, defs: dict):
    """Inline $refs, drop keywords the structured-output schema does not take, close objects."""
    if isinstance(node, list):
        return [_clean(x, defs) for x in node]
    if not isinstance(node, dict):
        return node
    if "$ref" in node:
        return _clean(defs[node["$ref"].rsplit("/", 1)[1]], defs)
    out = {k: _clean(v, defs) for k, v in node.items() if k not in _DROP}
    if out.get("type") == "object":
        out["additionalProperties"] = False
        out["required"] = list(out["properties"])
    return out


def judge_schema() -> dict:
    s = Judgement.model_json_schema()
    return _clean(s, s.get("$defs", {}))


def assign_order(asset_id: str, models: tuple[str, ...] | list[str], seed: int) -> list[str]:
    """Which model sits behind A, B, C for this asset; deterministic, so cache keys hold."""
    order = sorted(models)
    random.Random(f"{seed}:{asset_id}").shuffle(order)
    return order


def judge_params(
    asset_id: str, facts_text: str, records: list[PilotRecord], judge: str, st: Store
) -> dict:
    r = st.get(render.STAGE, asset_id, render.RenderPayload)
    png = (st.root / r.contact_sheet).read_bytes()
    blocks = [
        f"Record {label}:\n{rec.model_dump_json(indent=1)}"
        for label, rec in zip("ABC", records, strict=True)
    ]
    text = "File context and measured facts (the writers saw the same):\n" + facts_text
    text += "\n\n" + "\n\n".join(blocks)
    return {
        "model": judge,
        "max_tokens": JUDGE_MAX_TOKENS,
        "system": [{"type": "text", "text": JUDGE_SYSTEM, "cache_control": {"type": "ephemeral"}}],
        "output_config": {
            "effort": "high",
            "format": {"type": "json_schema", "schema": judge_schema()},
        },
        "messages": [
            {
                "role": "user",
                "content": [
                    {
                        "type": "image",
                        "source": {
                            "type": "base64",
                            "media_type": "image/png",
                            "data": base64.standard_b64encode(png).decode(),
                        },
                    },
                    {"type": "text", "text": text},
                ],
            }
        ],
    }


def _call(client: anthropic.Anthropic, params: dict, order: list[str]) -> JudgeOutput:
    model = params["model"]
    try:
        msg = client.messages.create(**params)
    except anthropic.RateLimitError as e:
        return JudgeOutput(error=f"rate limit: {e}", judge=model, order=order)
    except anthropic.APIStatusError as e:
        return JudgeOutput(error=f"api {e.status_code}: {e.message}", judge=model, order=order)
    except anthropic.APIConnectionError as e:
        return JudgeOutput(error=f"connection: {e}", judge=model, order=order)
    usage = {
        "input_tokens": msg.usage.input_tokens
        + (msg.usage.cache_read_input_tokens or 0)
        + (msg.usage.cache_creation_input_tokens or 0),
        "output_tokens": msg.usage.output_tokens,
    }
    if msg.stop_reason != "end_turn":
        err = f"stop_reason {msg.stop_reason}"
        return JudgeOutput(error=err, judge=model, order=order, **usage)
    text = next((b.text for b in msg.content if b.type == "text"), "")
    try:
        judgement = Judgement.model_validate(json.loads(text))
    except ValueError as e:
        return JudgeOutput(error=f"bad output: {e}", judge=model, order=order, **usage)
    return JudgeOutput(judgement=judgement, judge=model, order=order, **usage)


@dataclass
class Judged:
    asset_id: str
    judge: JudgeOutput
    outs: dict[str, PilotOutput]  # candidate model -> its stored output


def _sibling_groups() -> tuple[dict[str, object], dict[str, list[str]]]:
    everything = ingest.read_index()
    groups: dict[str, list[str]] = {}
    for a in everything:
        if a.variant_group and a.role == "primary":
            groups.setdefault(a.variant_group, []).append(a.name)
    return {a.id: a for a in everything}, groups


def _facts(a, groups: dict[str, list[str]], st: Store) -> str | None:
    siblings = [n for n in groups.get(a.variant_group or "", []) if n != a.name][:15]
    return describe_pilot.facts(a, siblings, st)


def judge_all(
    st: Store, judge: str, seed: int, threads: int, force: bool
) -> tuple[list[Judged], dict[str, int]]:
    stage = f"pilot-report/judge/{judge}"
    outs = {m: _records(st, m, RUN) for m in CANDIDATES}
    ids = sorted(set.intersection(*(set(o) for o in outs.values())))
    by_id, groups = _sibling_groups()
    counts = {"fresh": 0, "done": 0, "failed": 0}
    todo = []
    for i in ids:
        text = _facts(by_id[i], groups, st)
        if text is None:
            continue
        order = assign_order(i, CANDIDATES, seed)
        records = [outs[m][i].record for m in order]
        key = make_key(
            JUDGE_VERSION, JUDGE_SYSTEM, judge, order, text,
            [r.model_dump() for r in records], st.key(render.STAGE, i),
        )  # fmt: skip
        if not force and st.key(stage, i) == key:
            counts["fresh"] += 1
        else:
            todo.append((i, key, text, order, records))
    if todo:
        client = anthropic.Anthropic(api_key=config.api_key(), max_retries=6)
        with ThreadPoolExecutor(threads) as pool:
            futures = {
                pool.submit(_call, client, judge_params(i, text, recs, judge, st), order): (i, key)
                for i, key, text, order, recs in todo
            }
            for fut in as_completed(futures):
                i, key = futures[fut]
                out = fut.result()
                if out.error:
                    counts["failed"] += 1
                    print(f"[{stage}] FAILED {i}: {out.error}", file=sys.stderr)
                else:
                    st.put(stage, i, key, out)
                    counts["done"] += 1
    judged = []
    for i in ids:
        j = st.get(stage, i, JudgeOutput)
        if j is not None and j.judgement is not None:
            judged.append(Judged(i, j, {m: outs[m][i] for m in CANDIDATES}))
    return judged, counts


def verdict_of(j: Judged, model: str) -> tuple[Verdict, int]:
    """The judge's verdict on a model's record and its rank (1 = best)."""
    label = "ABC"[j.judge.order.index(model)]
    g = j.judge.judgement
    return getattr(g, label), g.ranking.index(label) + 1


def cost(model: str, input_tokens: float, output_tokens: float) -> float:
    p_in, p_out = PRICES[model]
    return (input_tokens * p_in + output_tokens * p_out) / 1e6


def aggregate(judged: list[Judged], target: int = TARGET_ASSETS) -> dict[str, dict]:
    result = {}
    for m in CANDIDATES:
        pairs = [verdict_of(j, m) for j in judged]
        n = len(pairs) or 1
        v = [p[0] for p in pairs]
        ranks = [p[1] for p in pairs]
        tok_in = sum(j.outs[m].input_tokens for j in judged) / n
        tok_out = sum(j.outs[m].output_tokens for j in judged) / n
        per_asset = cost(m, tok_in, tok_out)
        result[m] = {
            "n": len(pairs),
            "accuracy": sum(x.accuracy for x in v) / n,
            "findability": sum(x.findability for x in v) / n,
            "precision": sum(x.precision for x in v) / n,
            "factual_errors": sum(len(x.factual_errors) for x in v),
            "assets_with_error": sum(bool(x.factual_errors) for x in v),
            "misleading": sum(len(x.misleading_entries) for x in v),
            "missing": sum(len(x.missing) for x in v),
            "rank1": ranks.count(1),
            "rank2": ranks.count(2),
            "rank3": ranks.count(3),
            "mean_rank": sum(ranks) / n,
            "out_tokens": tok_out,
            "cost_per_asset": per_asset,
            "projected_batch": per_asset * target * BATCH_FACTOR,
        }
    return result


def judge_usage(judged: list[Judged]) -> dict[str, float]:
    return {
        "calls": len(judged),
        "input": sum(j.judge.input_tokens for j in judged),
        "output": sum(j.judge.output_tokens for j in judged),
        "usd": sum(
            cost(j.judge.judge, j.judge.input_tokens, j.judge.output_tokens) for j in judged
        ),
    }


def first_label_counts(judged: list[Judged]) -> dict[str, int]:
    """How often the judge put label A, B, C first; far from even hints at position bias."""
    counts = {"A": 0, "B": 0, "C": 0}
    for j in judged:
        counts[j.judge.judgement.ranking[0]] += 1
    return counts


# ---------------------------------------------------------------- output


def agreement_rows(ag: Agreement) -> list[tuple[str, str]]:
    m = ag.means
    rows = [
        ("kind (exact)", f"{m['kind']:.3f}"),
        ("category_path[0] (exact)", f"{m['category_path[0]']:.3f}"),
        ("category_path (exact)", f"{m['category_path exact']:.3f}"),
        ("category_path (mean shared prefix, levels)", f"{m['category_path prefix depth']:.2f}"),
    ]
    rows += [(f"{f} (Jaccard)", f"{m[f]:.3f}") for f in LIST_FIELDS]
    rows += [
        ("real_front (exact)", f"{m['real_front']:.3f}"),
        ("modular (exact)", f"{m['modular']:.3f}"),
        ("overall (mean of the above, prefix as share)", f"{m['overall']:.3f}"),
    ]
    return rows


def print_report(ag: Agreement, agg: dict[str, dict], judged: list[Judged], judge: str) -> None:
    print(f"\n== Repeat agreement, {HAIKU} run1 vs run2, {ag.n} assets ==")
    for name, val in agreement_rows(ag):
        print(f"  {name:48} {val}")
    print("\n  lowest agreement:")
    for w in ag.lowest:
        print(f"  {w.overall:.2f}  {w.id}")
        print(f"        '{w.name_run1}' vs '{w.name_run2}'; least: {', '.join(w.worst)}")
    print(f"\n== Blind judging by {judge}, {len(judged)} assets, models shuffled as A/B/C ==")
    print(
        f"  NOTE: the judge is {judge} and {judge} is also a candidate: possible self-preference.\n"
        "  Factual errors are the least preference-prone number; read them first."
    )
    print(
        f"  {'model':19} {'acc':>5} {'find':>5} {'prec':>5} | {'ERRORS':>6} {'(assets)':>8} "
        f"{'mislead':>7} {'missing':>7} | {'1st':>4} {'2nd':>4} {'3rd':>4} | {'out tok':>7} "
        f"{'$/asset':>8} {'batch $ for ' + str(TARGET_ASSETS):>17}"
    )
    for m, a in agg.items():
        print(
            f"  {m:19} {a['accuracy']:5.2f} {a['findability']:5.2f} {a['precision']:5.2f} | "
            f"{a['factual_errors']:6d} {a['assets_with_error']:8d} {a['misleading']:7d} "
            f"{a['missing']:7d} | {a['rank1']:4d} {a['rank2']:4d} {a['rank3']:4d} | "
            f"{a['out_tokens']:7.0f} {a['cost_per_asset']:8.4f} {a['projected_batch']:17.2f}"
        )
    u = judge_usage(judged)
    print(
        f"\n  judge usage (cached calls): {u['calls']} calls, {u['input']:,} input + "
        f"{u['output']:,} output tokens, about ${u['usd']:.2f} at standard prices"
    )
    print(f"  label ranked first by the judge (position check): {first_label_counts(judged)}")


# ---------------------------------------------------------------- 3. HTML

CSS = """\
:root{--bg:#fafaf8;--fg:#1b1b1b;--mute:#666;--card:#fff;--line:#d8d8d2;--accent:#2457c5;
--err:#b3261e;--errbg:#fbe9e7;--mis:#8a5a00;--misbg:#fff3d6;--miss:#1d5a8a;--missbg:#e3f0fa;
--best:#1a7f37}
@media (prefers-color-scheme:dark){:root{--bg:#15161a;--fg:#e6e6e3;--mute:#9a9a96;--card:#1e2025;
--line:#35373d;--accent:#8ab4ff;--err:#ff8a80;--errbg:#3a1f1d;--mis:#f0c36a;--misbg:#3a2f14;
--miss:#8cc7f2;--missbg:#16293a;--best:#6fd08c}}
*{box-sizing:border-box}
body{margin:0;padding:1.5rem 2rem 4rem;background:var(--bg);color:var(--fg);
font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
main{max-width:2000px;margin:0 auto}
h1{font-size:1.6rem;margin:0 0 .3rem}h2{font-size:1.2rem;margin:2.2rem 0 .6rem}
h3{font-size:1rem;margin:0}
p.note{color:var(--mute);max-width:110ch;margin:.3rem 0}
table{border-collapse:collapse;margin:.5rem 0 1rem;background:var(--card)}
th,td{border:1px solid var(--line);padding:.3rem .7rem;text-align:right;
font-variant-numeric:tabular-nums}
th{text-align:center;font-weight:600}td:first-child,th:first-child{text-align:left}
td.e{color:var(--err);font-weight:700}
.tables{display:flex;flex-wrap:wrap;gap:2rem;align-items:flex-start}
details{margin:.5rem 0}summary{cursor:pointer;color:var(--accent)}
section.asset{margin:2.5rem 0;padding:1rem 1.2rem;background:var(--card);
border:1px solid var(--line);border-radius:8px}
section.asset>h2{margin:0 0 .6rem;font-size:1.15rem;word-break:break-all}
.top{display:flex;gap:1.5rem;flex-wrap:wrap;align-items:flex-start}
.top a{flex:0 1 520px}
.top img{display:block;max-width:520px;width:100%;height:auto;border:1px solid var(--line);
border-radius:4px;background:#fff}
pre.facts{margin:0;flex:1 1 360px;white-space:pre-wrap;
font:13px/1.45 ui-monospace,Menlo,monospace;color:var(--mute)}
.verdict{margin:.9rem 0 .4rem}
.cols{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:1rem}
@media (max-width:1100px){.cols{grid-template-columns:1fr}}
.col{border:1px solid var(--line);border-radius:6px;padding:.7rem .8rem;min-width:0}
.col.r1{border-color:var(--best);box-shadow:inset 0 0 0 1px var(--best)}
.col h3{display:flex;justify-content:space-between;gap:.5rem;flex-wrap:wrap;margin-bottom:.4rem}
.tag{font-weight:400;color:var(--mute);font-size:.85rem}
.row{display:grid;grid-template-columns:7.5rem minmax(0,1fr);gap:.5rem;padding:.15rem 0;
border-top:1px dotted var(--line)}
.k{color:var(--mute);font-size:.82rem;padding-top:.1rem}.v{overflow-wrap:anywhere}
.scores{margin:.6rem 0 .3rem;font-size:.9rem}
.list{margin:.35rem 0;padding:.3rem .6rem;border-radius:4px;font-size:.9rem}
.list b{display:block;font-size:.8rem;text-transform:uppercase;letter-spacing:.04em}
.list ul{margin:.1rem 0 0;padding-left:1.1rem}
.list.err{background:var(--errbg);color:var(--err)}
.list.mis{background:var(--misbg);color:var(--mis)}
.list.miss{background:var(--missbg);color:var(--miss)}.none{color:var(--mute);font-size:.85rem}
a{color:var(--accent)}
"""


def _e(x: object) -> str:
    return html.escape(str(x))


def _ordinal(n: int) -> str:
    return {1: "1st", 2: "2nd", 3: "3rd"}[n]


def _field_text(rec: PilotRecord, f: str) -> str:
    v = getattr(rec, f)
    if f == "category_path":
        return " > ".join(v)
    if f == "likely_queries":
        return " | ".join(v)
    return ", ".join(v) if isinstance(v, list) else str(v)


def _issue_list(cls: str, title: str, items: list[str]) -> str:
    if not items:
        return ""
    lis = "".join(f"<li>{_e(i)}</li>" for i in items)
    return f'<div class="list {cls}"><b>{title} ({len(items)})</b><ul>{lis}</ul></div>'


def _column(j: Judged, m: str) -> str:
    rec = j.outs[m].record
    v, rank = verdict_of(j, m)
    label = "ABC"[j.judge.order.index(m)]
    rows = "".join(
        f'<div class="row"><div class="k">{f}</div><div class="v">{_e(_field_text(rec, f))}</div>'
        "</div>"
        for f in SHOWN_FIELDS
    )
    issues = _issue_list("err", "Factual errors", v.factual_errors)
    issues += _issue_list("mis", "Misleading entries", v.misleading_entries)
    issues += _issue_list("miss", "Missing", v.missing)
    if not issues:
        issues = '<div class="none">judge found no errors, misleading entries or gaps</div>'
    return (
        f'<div class="col{" r1" if rank == 1 else ""}"><h3><span>{_e(m)}</span>'
        f'<span class="tag">ranked {_ordinal(rank)} (judge saw it as {label})</span></h3>'
        f'<div class="scores">accuracy <b>{v.accuracy}</b> &middot; findability '
        f"<b>{v.findability}</b> &middot; precision <b>{v.precision}</b></div>{issues}{rows}</div>"
    )


def _section(j: Judged, facts_text: str, img: str) -> str:
    g = j.judge.judgement
    ranking = " &gt; ".join(_e(j.judge.order["ABC".index(label)]) for label in g.ranking)
    cols = "".join(_column(j, m) for m in CANDIDATES)
    return (
        f'<section class="asset" id="{_e(j.asset_id)}"><h2>{_e(j.asset_id)}</h2>'
        f'<div class="top"><a href="{img}"><img loading="lazy" src="{img}" '
        f'alt="contact sheet of {_e(j.asset_id)}"></a><pre class="facts">{_e(facts_text)}</pre>'
        f'</div><p class="verdict"><b>Judge ranking:</b> {ranking}<br>{_e(g.reason)}</p>'
        f'<div class="cols">{cols}</div></section>'
    )


def _table(head: list[str], rows: list[list[str]], err_col: int | None = None) -> str:
    th = "".join(f"<th>{_e(h)}</th>" for h in head)
    body = ""
    for r in rows:
        tds = "".join(
            f'<td class="e">{_e(c)}</td>' if i == err_col else f"<td>{_e(c)}</td>"
            for i, c in enumerate(r)
        )
        body += f"<tr>{tds}</tr>"
    return f"<table><tr>{th}</tr>{body}</table>"


def render_html(
    ag: Agreement,
    agg: dict[str, dict],
    judged: list[Judged],
    judge: str,
    facts_by_id: dict[str, str],
    img_by_id: dict[str, str],
) -> str:
    head = [
        "model", "accuracy", "findability", "precision", "factual errors", "assets with error",
        "misleading", "missing", "1st", "2nd", "3rd", "mean output tokens", "$ / asset",
        f"batch $ for {TARGET_ASSETS:,}",
    ]  # fmt: skip
    rows = [
        [
            m, f"{a['accuracy']:.2f}", f"{a['findability']:.2f}", f"{a['precision']:.2f}",
            str(a["factual_errors"]), str(a["assets_with_error"]), str(a["misleading"]),
            str(a["missing"]), str(a["rank1"]), str(a["rank2"]), str(a["rank3"]),
            f"{a['out_tokens']:.0f}", f"{a['cost_per_asset']:.4f}", f"{a['projected_batch']:.2f}",
        ]
        for m, a in agg.items()
    ]  # fmt: skip
    u = judge_usage(judged)
    first = first_label_counts(judged)
    agree = _table(["field", "agreement"], [list(r) for r in agreement_rows(ag)])
    lowest = _table(
        ["asset", "overall", "run1 name", "run2 name", "least agreeing"],
        [
            [w.id, f"{w.overall:.2f}", w.name_run1, w.name_run2, ", ".join(w.worst)]
            for w in ag.lowest
        ],
    )
    nav = _table(
        ["asset", "judge's best", "judge's worst"],
        [
            [
                j.asset_id,
                j.judge.order["ABC".index(j.judge.judgement.ranking[0])],
                j.judge.order["ABC".index(j.judge.judgement.ranking[2])],
            ]
            for j in judged
        ],
    )
    links = "".join(f'<a href="#{_e(j.asset_id)}">{_e(j.asset_id)}</a><br>' for j in judged)
    sections = "".join(_section(j, facts_by_id[j.asset_id], img_by_id[j.asset_id]) for j in judged)
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Catalog pilot: which model describes the assets</title><style>{CSS}</style></head>
<body><main>
<h1>Catalog pilot: which model describes the assets</h1>
<p class="note">{len(judged)} assets described by all three models, judged blind by {_e(judge)}
(the three records shown in a shuffled order, authorship hidden). The judge is {_e(judge)} and
{_e(judge)} is also a candidate, so it may favour its own style; the factual-error count is the
least preference-prone number. Scores are 1 to 5. Haiku is its run1. Costs: the describers'
own measured tokens at the standard price per million tokens, projected for {TARGET_ASSETS:,}
assets at half price (batch API). Judge position check, label ranked first: {_e(first)}.
Judge usage: {u["calls"]} calls, {u["input"]:,} input and {u["output"]:,} output tokens
(about ${u["usd"]:.2f}).</p>
<h2>Judged comparison</h2>
{_table(head, rows, err_col=4)}
<div class="tables"><div><h2>Repeat agreement, {_e(HAIKU)} run1 vs run2 ({ag.n} assets)</h2>
{agree}</div><div><h2>15 assets with the lowest agreement</h2>{lowest}</div></div>
<details><summary>Judge's best and worst per asset, jump list</summary>
<div class="tables"><div>{nav}</div><div>{links}</div></div></details>
{sections}
</main></body></html>
"""


def write_page(st: Store, ag: Agreement, agg: dict, judged: list[Judged], judge: str) -> Path:
    out = st.root / "pilot-report"
    by_id, groups = _sibling_groups()
    facts_by_id, img_by_id = {}, {}
    for j in judged:
        facts_by_id[j.asset_id] = _facts(by_id[j.asset_id], groups, st) or ""
        r = st.get(render.STAGE, j.asset_id, render.RenderPayload)
        img_by_id[j.asset_id] = quote(os.path.relpath(st.root / r.contact_sheet, out))
    page = out / "index.html"
    page.write_text(render_html(ag, agg, judged, judge, facts_by_id, img_by_id))
    return page


def cli(args: argparse.Namespace) -> None:
    st = Store()
    ag = agreement(st)
    judged, counts = judge_all(st, args.judge, args.seed, args.threads, args.force)
    print(f"judging: {counts}")
    agg = aggregate(judged)
    print_report(ag, agg, judged, args.judge)
    print(f"\nreview page: {write_page(st, ag, agg, judged, args.judge)}")
