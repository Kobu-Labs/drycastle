"""Pooled judging: Opus grades candidate assets for a query from their picture and measured size.

The judge never sees the described records, so a judgment does not inherit a describing model's
mistakes. It does not see the query's style or the drafter's notes either, so it cannot lean on
what the query was meant to test. Judgments are cached per (query, asset); only new pairs are
judged. Anything that returns assets for a query (a retriever, a fusion, a drafter's seed list)
extends the pool by calling judge().
"""

from __future__ import annotations

import random
import sys
from collections.abc import Iterable
from concurrent.futures import ThreadPoolExecutor, as_completed

import anthropic
from pydantic import BaseModel

from ..store import Store
from . import io as evalio
from .llm import (
    MODEL,
    THUMB_PX,
    AssetView,
    asset_view,
    call_json,
    closed_object,
    log_usage,
    new_client,
    thumbnail_block,
)
from .models import GRADES, Judgment, Query, limits_text

RUBRIC_VERSION = 1
MAX_TOKENS = 16000  # thinking counts toward it
BATCH = 8
THREADS = 4

SYSTEM = """\
You grade search results for a catalogue of 3D game assets. Level designers and AI agents that \
build game levels search it. For one search query you decide how good each candidate asset is \
as an answer. Your grades are the ground truth used to compare search systems, so be careful, \
consistent and strict: a generous grader makes every system look alike.

For each candidate you get one image with four views (front, three-quarter, top, back) with \
scale bars, a 1.8 m human figure beside the front view for most, and the asset's measured size. \
You get no names and no descriptions: judge from what you see and from the measured size, which \
is exact. Judge each candidate on its own against the query, not against the other candidates: \
several may deserve the same grade, and many candidates are deliberately wrong. The art is \
low-poly with flat colours; ignore that, judge the real-world object.

Grades:
3 ideal: exactly what the searcher would pick first. The right kind of object and everything \
the query states (kind, attributes, size, place, purpose) is met. Equally good alternatives \
all get 3 (variants that differ only in something the query did not mention).
2 good: an answer the searcher would be glad to use. The right kind of thing, or a very good \
fit for the stated need, with a minor mismatch in something the query did not ask for, or one \
of several good options that is clearly not the closest.
1 acceptable: usable but a stretch. A related kind of thing that serves the purpose loosely, or \
the right kind but different from what the wording suggests in a way that matters (not a hard \
limit). The searcher would take it only if nothing better existed.
0 not relevant: the wrong kind of thing; shares only a word, a room, a theme or a look with the \
right answer; or breaks a hard limit of the query.

How to apply them:
- Grade the asset as an answer to what was asked, not by general similarity. An object of a \
different kind is 0 even if it belongs in the same room or is made of the same material. Only \
give 2 or 3 to an asset that really is what was asked for or really does what was needed.
- Short keyword queries: read them as the person meant them, including synonyms and \
misspellings (couch is a sofa, "stappler" is a stapler). A word naming a family (chair) makes \
every member of the family a 3.
- Need-based queries (a purpose or problem, no object named): ask whether a level designer who \
wrote this would be glad to be handed this asset for that need. An object that serves the need \
directly is 3; one that plausibly supports it as part of the scene is 2; one that connects only \
remotely is 1; anything else is 0. The asset need not be named by the query.
- Hard limits: any limit stated in the query text or listed under "Hard limits" that the asset \
breaks makes the grade 0, however good it is otherwise. Compare the measured size with the \
exact numbers (width is x, height is y, depth is z, longest is the largest of the three). For \
terms about placement, setting, kind, material or function, judge whether the asset plainly \
satisfies it as shown; if it plainly does not, 0.
- Variants: if the query asks for a colour, state (open, closed), size or form, check it. If it \
does not, do not mark an asset down for it.
- Scale: a real-world size that would make the asset useless for the stated purpose (a chair \
too small to sit on) counts as the wrong thing.

For every candidate give a grade and a reason: one short, specific sentence saying what matches \
or what is wrong. Answer with the JSON the schema asks for and nothing else."""


class Graded(BaseModel):
    candidate: str
    grade: int
    reason: str


class Batch(BaseModel):
    judgments: list[Graded]


def schema(labels: list[str]) -> dict:
    item = closed_object(
        {
            "candidate": {"type": "string", "enum": labels},
            "grade": {"type": "integer", "enum": list(GRADES)},
            "reason": {"type": "string"},
        }
    )
    return closed_object({"judgments": {"type": "array", "items": item}})


def request_params(query: Query, views: list[AssetView], order: list[int], model: str,
                   effort: str, px: int) -> dict:  # fmt: skip
    """One request: the candidates in the given order, labelled C1..Cn so names stay hidden."""
    content: list[dict] = []
    labels = []
    for pos, i in enumerate(order, 1):
        content.append(thumbnail_block(views[i].sheet, px))
        content.append({"type": "text", "text": f"C{pos}: {views[i].size_text()}"})
        labels.append(f"C{pos}")
    content.append(
        {
            "type": "text",
            "text": f"Query: {query.text}\nHard limits:\n{limits_text(query.filters) or 'none stated beyond the query text'}\n\n"
            f"Grade each of the {len(labels)} candidates above for this query.",
        }
    )
    return {
        "model": model,
        "max_tokens": MAX_TOKENS,
        "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
        "output_config": {
            "effort": effort,
            "format": {"type": "json_schema", "schema": schema(labels)},
        },
        "messages": [{"role": "user", "content": content}],
    }


def shuffled(query_id: str, asset_ids: list[str]) -> list[str]:
    """A fixed pseudo-random order per query, so position in a batch carries no signal."""
    rng = random.Random(query_id)
    ids = sorted(asset_ids)
    rng.shuffle(ids)
    return ids


class Judge:
    def __init__(
        self,
        set_name: str,
        *,
        model: str = MODEL,
        effort: str = "high",
        batch: int = BATCH,
        threads: int = THREADS,
        px: int = THUMB_PX,
        client: anthropic.Anthropic | None = None,
        store: evalio.JudgmentStore | None = None,
        st: Store | None = None,
    ):
        self.set_name = set_name
        self.model, self.effort, self.batch, self.threads, self.px = (
            model,
            effort,
            batch,
            threads,
            px,
        )
        self.client = client or new_client()
        self.store = store or evalio.JudgmentStore(set_name)
        self.st = st or Store()

    def pending(
        self, queries: dict[str, Query], pool: dict[str, Iterable[str]]
    ) -> dict[str, list[str]]:
        """Per query, the pool assets without a current model judgment. Humans' grades count."""
        have = self.store.all()
        overrides = {(o.query, o.asset) for o in evalio.load_overrides(self.set_name)}
        out = {}
        for qid, ids in pool.items():
            q = queries[qid]
            need = []
            for a in dict.fromkeys(ids):
                j = have.get((qid, a))
                fresh = j and j.query_digest == q.digest() and j.rubric_version == RUBRIC_VERSION
                if not fresh and (qid, a) not in overrides:
                    need.append(a)
            if need:
                out[qid] = need
        return out

    def judge_pool(self, queries: dict[str, Query], pool: dict[str, Iterable[str]],
                   force: bool = False) -> dict[str, int]:  # fmt: skip
        """Grade every new (query, asset) pair of the pool; returns counts."""
        todo = {q: list(dict.fromkeys(ids)) for q, ids in pool.items()} if force else \
            self.pending(queries, pool)  # fmt: skip
        counts = {"judged": 0, "failed": 0, "no_picture": 0, "cached": 0}
        counts["cached"] = sum(len(set(ids)) for ids in pool.values()) - sum(
            len(i) for i in todo.values()
        )
        jobs = []
        for qid, ids in todo.items():
            views = {}
            for a in ids:
                if (v := asset_view(a, self.st)) is None:
                    counts["no_picture"] += 1
                    print(f"[judge] no picture or size for {a}", file=sys.stderr)
                else:
                    views[a] = v
            order = shuffled(qid, list(views))
            for i in range(0, len(order), self.batch):
                jobs.append((queries[qid], [views[a] for a in order[i : i + self.batch]]))
        with ThreadPoolExecutor(self.threads) as ex:
            futures = {ex.submit(self._judge_batch, q, vs): (q, vs) for q, vs in jobs}
            for fut in as_completed(futures):
                got = fut.result()
                self.store.add(got)
                counts["judged"] += len(got)
                counts["failed"] += len(futures[fut][1]) - len(got)
        return counts

    def _judge_batch(self, query: Query, views: list[AssetView]) -> list[Judgment]:
        """Judgments for the batch; assets the model did not grade are left for a rerun."""
        out: dict[str, Judgment] = {}
        for _ in range(2):
            left = [v for v in views if v.id not in out]
            if not left:
                break
            order = list(range(len(left)))
            reply = call_json(self.client, request_params(query, left, order, self.model,
                                                          self.effort, self.px))  # fmt: skip
            if isinstance(reply, str):
                print(f"[judge] {query.id}: {reply}", file=sys.stderr)
                continue
            log_usage("judge", self.model, reply)
            try:
                batch = Batch.model_validate(reply.data)
            except ValueError as e:
                print(f"[judge] {query.id}: bad output: {e}", file=sys.stderr)
                continue
            for g in batch.judgments:
                pos = int(g.candidate[1:]) - 1
                if 0 <= pos < len(left) and left[pos].id not in out:
                    out[left[pos].id] = Judgment(
                        query_id=query.id,
                        asset_id=left[pos].id,
                        grade=g.grade,
                        judged_by="model",
                        judge=self.model,
                        reason=g.reason.strip(),
                        query_digest=query.digest(),
                        rubric_version=RUBRIC_VERSION,
                        at=evalio.now(),
                    )
        return list(out.values())

    def judge(self, query: Query, asset_ids: Iterable[str]) -> dict[str, Judgment]:
        """Grades for these assets for this query: cached ones, and new ones judged now."""
        ids = list(dict.fromkeys(asset_ids))
        self.judge_pool({query.id: query}, {query.id: ids})
        have = self.store.all()
        return {a: have[(query.id, a)] for a in ids if (query.id, a) in have}


def judge(query: Query, asset_ids: Iterable[str], set_name: str, **kwargs) -> dict[str, Judgment]:
    """Grade the assets for the query (cached; only new pairs cost anything)."""
    return Judge(set_name, **kwargs).judge(query, asset_ids)
