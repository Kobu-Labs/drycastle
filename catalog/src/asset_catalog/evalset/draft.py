"""Draft evaluation queries for a pack with Opus, from the thumbnails and realistic needs.

The drafter sees every asset's picture and measured size, never the described records, so the
queries cannot favour whichever model described the assets. One request per query style, run in
sequence: each later request is told which assets earlier queries already cover, so the set
spreads over the whole pack instead of clustering on the obvious objects.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass
from typing import get_args

import anthropic
from pydantic import BaseModel

from .. import vocab
from ..search.types import Filters
from ..stages import ingest, measure
from ..store import Store
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
from .models import Answers, Query, QuerySet, limits_text

PROMPT_VERSION = 1
MAX_TOKENS = 20000  # thinking counts toward it; stays under the SDK's non-streaming limit
GROUP_SIZE = 12
# Order matters: negatives go last so they see everything the other styles used.
ORDER = ("keyword", "descriptive", "need", "constraint", "negative")
MAX_CANDIDATES = 10

SYSTEM = """\
You are helping build an evaluation set for the search of a catalogue of 3D game assets (props, \
furniture, buildings, vehicles, ...). The catalogue is searched by two kinds of people: level \
designers typing into a search box while they build a scene, and AI agents that furnish and \
dress a level and call the search with a request in their own words. Every candidate search \
design (keyword matching, text embeddings, image embeddings, reranking by a model, how and when \
to answer "nothing fits") will be scored on the queries you write, so the queries decide which \
design is chosen. A weak or lopsided query set picks the wrong design.

What makes a good query set:
- It sounds like real use. Real people type short, lazy, sometimes misspelt words; use synonyms \
and regional words (couch for sofa, bin for rubbish bin or trash can, cellphone, photocopier, \
xerox); do not know the artist's file names; and write what they need, not what a catalogue \
would label. Do not use words that only a metadata schema would use.
- It has a defensible answer. Someone looking at the same pictures must be able to say which \
assets answer the query. Do not rely on things the pictures cannot show.
- It is hard in different ways. Include queries that one asset alone answers and queries that \
many answer; queries where a near neighbour is the wrong answer (an open one versus a closed \
one, a tall versus a low one, a wall-mounted versus a standing one); words that name a family \
and words that name a single object.
- It covers the pack, not the obvious. Real packs hold many unglamorous objects; spread the \
queries over the many different kinds of object you see, and use the less obvious ones as much \
as the popular ones.
- Each query is its own idea. No two queries may be paraphrases of each other.

What you are shown: for every asset of the pack, one image and a line of text. The image holds \
four views (front, three-quarter, top, back) with a scale bar in each, and a 1.8 m human figure \
beside the front view for most assets. The text gives the asset's reference (the only way to \
point at it in your answer; people searching never see it) and its measured size in metres, \
width (x) by height (y) by depth (z). The size is exact; trust it over your eye. The art style is \
low-poly with flat colours; ignore that, the objects are ordinary real-world objects.

Answer with the JSON the schema asks for and nothing else."""

# Style tasks. {k} is the number of queries, {typos} how many keyword queries carry a typo.
TASKS = {
    "keyword": """\
Write {k} KEYWORD queries: what a level designer types into the search box, 1 to 3 words.
- Mix plain object names, synonyms and regional variants (couch/sofa, bin/trash can, \
cupboard/cabinet, rug/carpet/mat), and broad family words (a single word that many assets \
answer) next to words that name one asset.
- About {typos} of them carry a realistic typo or odd spelling a person really makes (dropped or \
swapped letter, missing space, wrong plural, British or American spelling). The typo must still \
be recoverable by a person.
- Never use an artist's file name pattern (no "_01", no underscores).
For each, candidates are the assets that answer it, best first (see below).""",
    "descriptive": """\
Write {k} DESCRIPTIVE queries: a full sentence, 10 to 25 words, as an AI agent furnishing a room \
writes when it calls the search: what it needs and why, with the role in the scene, and where it \
goes or how big it must be. For example (illustration only, make your own): "a low piece of \
furniture to put a printer on in a small office".
- Name the kind of object in natural words, but describe it; the sentence is a request, not a \
file name.
- Include a discriminating detail in some (the open one, a colour, with a lid, tall, wall-mounted).
- Some are answered by exactly one asset, others by several different assets.
For each, candidates are the assets that answer it, best first (see below).""",
    "need": """\
Write {k} NEED-BASED queries: the searcher states a purpose or a problem in the scene and NEVER \
names the object it needs, nor an obvious synonym: "something to make a waiting area feel less \
empty", "where staff can leave their coats", "a way to tell people which way out is". 8 to 25 \
words.
- The right answers are the objects a level designer would be glad to be handed for that need. \
For some needs that is several different kinds of object, for others one clear object.
- Use needs that require a little reasoning about what objects are for.
For each, candidates are the assets that serve the need, best first (see below).""",
    "constraint": """\
Write {k} CONSTRAINT queries: a natural request that also states a hard limit on size, \
placement, material or kind, and the same limits as structured filters.
- "text" is what the person typed, with the limit in words ("a small cabinet, under 1 m \
tall"). "filters" holds exactly the limits the text states outright as hard, and nothing the \
text only implies. Numeric limits in metres (width = x, height = y, depth = z, longest = the \
biggest of the three). Facet terms only from the lists below.
- Build limits that actually split the pack, using the measured sizes: some where the limit \
removes assets that match the request in every other way (ignoring the limit would give a wrong \
answer), some where only one asset survives, some where the limit is loose. At least one asset \
must satisfy every limit of a query.
- Do not put in "filters" anything the person did not say. Leave unused fields empty or null.

Facet terms for filters:
{facets}

Size classes (by the longest side): {bands}.
For each, candidates are the assets that satisfy the text and the limits PLUS up to three assets \
that fit the request but break a limit (a search that ignores the limit would return them), see \
below.""",
    "negative": """\
Write {k} NEGATIVE queries: realistic requests for which the correct answer is that nothing in \
this pack fits, so that a good search says so instead of returning something.
- Mix clearly foreign objects (a medieval sword, a bathtub, a tractor) with near misses from \
the same world: things someone furnishing this kind of place would plausibly ask for that this \
pack does not contain. Look through EVERY image before you commit: if a person would accept any \
asset shown as what was asked for, the query is not negative; replace it.
- Do not ask for an object that exists in another colour or form here; that is a different test.
- Write them like the other styles: some single keywords, some full sentences, some a need.
For each, candidates are the up to five assets a careless search would most plausibly return \
for it (the closest lookalikes), best first. They are meant to be graded as wrong answers.""",
}

OUTPUT_RULES = """\

Output rules.
- "answers" is how many assets of the pack are right answers: "one", "few" (2 to 4), "many" \
(5 or more), or "none" (negative queries).
- "notes" is one or two sentences for the human reviewer: what a perfect answer looks like, why \
the query is a good test (the synonym, typo, near-neighbour trap, or reasoning it needs).
- "candidates" are asset references exactly as given, at most {max_c}, best first: the right \
answers first (as many as are right, up to the limit), then, if room is left, the closest wrong \
ones a search might return. Only references of assets you were shown."""

COVERAGE = """\

Coverage so far. Queries already written (do not repeat their ideas):
{done}

Assets no earlier query has used as a candidate yet (prefer building this batch around these, \
and around different kinds of object than the earlier queries, but a natural query comes before \
coverage): {uncovered}"""


class DraftFilters(BaseModel):
    kinds: list[str]
    materials: list[str]
    settings: list[str]
    placements: list[str]
    functions: list[str]
    size_classes: list[str]
    min_width_m: float | None
    max_width_m: float | None
    min_height_m: float | None
    max_height_m: float | None
    min_depth_m: float | None
    max_depth_m: float | None
    min_longest_m: float | None
    max_longest_m: float | None

    def to_filters(self) -> Filters:
        data = {k: v for k, v in self.model_dump().items() if v not in (None, [])}
        return Filters(**data)


class DraftQuery(BaseModel):
    text: str
    answers: Answers
    notes: str
    candidates: list[str]
    filters: DraftFilters | None = None


class Drafted(BaseModel):
    queries: list[DraftQuery]


@dataclass(frozen=True)
class Item:
    id: str
    ref: str  # the id without "<source>/<pack>/": what the drafter points at
    view: AssetView


def pack_items(pack: str, st: Store | None = None) -> list[Item]:
    """The pack's primary models that have a picture and a measured size, in id order."""
    st = st or Store()
    items = []
    for a in sorted(ingest.read_index(), key=lambda a: a.id):
        in_pack = f"{a.source}/{a.pack}" == pack
        if not (in_pack and a.media == "model" and a.role == "primary" and a.kind_hint is None):
            continue
        if (view := asset_view(a.id, st)) is not None:
            items.append(Item(a.id, a.id.removeprefix(pack + "/"), view))
    return items


def style_counts(n: int) -> dict[str, int]:
    """n split evenly over the styles; the remainder goes to the first styles in ORDER."""
    base, extra = divmod(n, len(ORDER))
    return {s: base + (i < extra) for i, s in enumerate(ORDER)}


def satisfies_bounds(f: Filters, size_m: tuple[float, float, float]) -> bool:
    """Whether measured size meets f's numeric limits (the other fields are not checked)."""
    x, y, z = size_m
    checks = [
        (f.min_width_m, f.max_width_m, x),
        (f.min_height_m, f.max_height_m, y),
        (f.min_depth_m, f.max_depth_m, z),
        (f.min_longest_m, f.max_longest_m, max(size_m)),
    ]
    return all((lo is None or v >= lo) and (hi is None or v <= hi) for lo, hi, v in checks)


def filters_schema(v: vocab.Vocab) -> dict:
    number = {"anyOf": [{"type": "number"}, {"type": "null"}]}

    def terms(facet: str) -> dict:
        return {"type": "array", "items": {"type": "string", "enum": list(v.facets[facet])}}

    sizes = list(get_args(measure.SizeClass))
    props: dict = {
        "kinds": terms("kind"),
        "materials": terms("material"),
        "settings": terms("setting"),
        "placements": terms("placement"),
        "functions": terms("function"),
        "size_classes": {"type": "array", "items": {"type": "string", "enum": sizes}},
    }
    for axis in ("width", "height", "depth", "longest"):
        props[f"min_{axis}_m"] = number
        props[f"max_{axis}_m"] = number
    return closed_object(props)


def schema(style: str, v: vocab.Vocab) -> dict:
    props: dict = {
        "text": {"type": "string"},
        "answers": {"type": "string", "enum": ["one", "few", "many", "none"]},
        "notes": {"type": "string"},
        "candidates": {"type": "array", "items": {"type": "string"}},
    }
    if style == "constraint":
        props["filters"] = filters_schema(v)
    return closed_object({"queries": {"type": "array", "items": closed_object(props)}})


def facet_guide(v: vocab.Vocab) -> str:
    return (
        "\n".join(
            f"{facet}: " + ", ".join(v.facets[facet])
            for facet in ("kind", "material", "setting", "placement", "function")
        )
        + "\nplacement meanings:\n"
        + "\n".join(f"- {t}: {d}" for t, d in v.facets["placement"].items())
    )


def inventory(items: list[Item], group_size: int, px: int) -> list[dict]:
    """Content blocks: every asset's picture then its reference and measured size, in groups."""
    groups = [items[i : i + group_size] for i in range(0, len(items), group_size)]
    content: list[dict] = [
        {
            "type": "text",
            "text": f"The pack holds {len(items)} assets, shown in {len(groups)} groups. "
            "Each image is followed by its reference and measured size.",
        }
    ]
    for g, group in enumerate(groups, 1):
        content.append({"type": "text", "text": f"Group {g} of {len(groups)}"})
        for it in group:
            content.append(thumbnail_block(it.view.sheet, px))
            content.append({"type": "text", "text": f"{it.ref}: {it.view.size_text()}"})
    content[-1] = {**content[-1], "cache_control": {"type": "ephemeral"}}
    return content


def task_text(style: str, k: int, v: vocab.Vocab, done: list[str], uncovered: list[str]) -> str:
    bands = ", ".join(f"{name} (below {hi} m)" for hi, name in measure.SIZE_BANDS)
    text = TASKS[style].format(k=k, typos=max(1, round(k / 4)), facets=facet_guide(v), bands=bands)
    text += OUTPUT_RULES.format(max_c=MAX_CANDIDATES)
    if done:
        text += COVERAGE.format(
            done="\n".join(f"- {t}" for t in done),
            uncovered=", ".join(uncovered) or "(all assets are covered)",
        )
    return text


def request_params(
    style: str,
    k: int,
    inv: list[dict],
    v: vocab.Vocab,
    done: list[str],
    uncovered: list[str],
    model: str,
    effort: str,
) -> dict:
    task = {"type": "text", "text": task_text(style, k, v, done, uncovered)}
    return {
        "model": model,
        "max_tokens": MAX_TOKENS,
        "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
        "output_config": {
            "effort": effort,
            "format": {"type": "json_schema", "schema": schema(style, v)},
        },
        "messages": [{"role": "user", "content": [*inv, task]}],
    }


def parse(
    style: str, data: dict, k: int, refs: dict[str, Item]
) -> tuple[list[DraftQuery], list[str]]:
    """Validated queries with unknown candidate references dropped, and notes on what was fixed."""
    drafted = Drafted.model_validate(data)
    issues: list[str] = []
    out = []
    for q in drafted.queries:
        known = [r for r in dict.fromkeys(q.candidates) if r in refs][:MAX_CANDIDATES]
        if len(known) != len(q.candidates):
            issues.append(f"{style}: {q.text!r}: dropped unknown or repeated candidates")
        out.append(q.model_copy(update={"candidates": known}))
    if len(out) != k:
        issues.append(f"{style}: asked for {k}, got {len(out)}")
    return out, issues


def check_constraint(q: DraftQuery, f: Filters, refs: dict[str, Item]) -> list[str]:
    ok = [it for it in refs.values() if satisfies_bounds(f, it.view.size_m)]
    if not ok:
        return [f"constraint: {q.text!r}: no asset in the pack meets the numeric limits"]
    if not any(satisfies_bounds(f, refs[r].view.size_m) for r in q.candidates):
        return [f"constraint: {q.text!r}: no candidate meets the numeric limits"]
    return []


@dataclass
class Draft:
    queries: QuerySet
    pool: dict[str, list[str]]  # query id -> candidate asset ids, seeds for judging
    issues: list[str]
    raw: dict[str, dict]  # style -> the model's reply as received


def draft(
    pack: str,
    n: int,
    set_name: str,
    *,
    group_size: int = GROUP_SIZE,
    px: int = THUMB_PX,
    model: str = MODEL,
    effort: str = "high",
    client: anthropic.Anthropic | None = None,
    st: Store | None = None,
) -> Draft:
    st = st or Store()
    v = vocab.load()
    items = pack_items(pack, st)
    if not items:
        raise ValueError(f"no rendered, measured assets in pack {pack!r}")
    refs = {it.ref: it for it in items}
    inv = inventory(items, group_size, px)
    client = client or new_client()
    queries: list[Query] = []
    pool: dict[str, list[str]] = {}
    issues: list[str] = []
    raw: dict[str, dict] = {}
    covered: set[str] = set()
    for style, k in style_counts(n).items():
        if k == 0:
            continue
        done = [q.text for q in queries]
        uncovered = [r for r in refs if r not in covered]
        params = request_params(style, k, inv, v, done, uncovered, model, effort)
        got, notes = _ask(client, params, style, k, refs, model)
        issues += notes
        raw[style] = {"queries": [q.model_dump() for q in got]}
        for q in got:
            qid = f"{set_name}-{len(queries) + 1:03d}"
            f = q.filters.to_filters() if q.filters else Filters()
            if style == "constraint":
                issues += check_constraint(q, f, refs)
            queries.append(
                Query(
                    id=qid,
                    text=q.text.strip(),
                    style=style,
                    filters=f,
                    answers=q.answers,
                    notes=q.notes.strip(),
                )
            )
            pool[qid] = [refs[r].id for r in q.candidates]
            if style != "negative":
                covered.update(q.candidates)
        print(f"[draft] {style}: {len(got)} queries", file=sys.stderr)
    return Draft(QuerySet(name=set_name, pack=pack, queries=queries), pool, issues, raw)


def _ask(client, params, style, k, refs, model) -> tuple[list[DraftQuery], list[str]]:
    for attempt in (1, 2):
        reply = call_json(client, params)
        if isinstance(reply, str):
            print(f"[draft] {style} attempt {attempt} failed: {reply}", file=sys.stderr)
            continue
        log_usage("draft", model, reply)
        try:
            return parse(style, reply.data, k, refs)
        except ValueError as e:
            print(f"[draft] {style} attempt {attempt}: bad output: {e}", file=sys.stderr)
    raise RuntimeError(f"drafting {style} queries failed")


def coverage(qs: QuerySet, pool: dict[str, list[str]], pack_ids: list[str]) -> float:
    """Share of the pack's assets that are a candidate of at least one non-negative query."""
    used = {a for q in qs.queries if q.style != "negative" for a in pool.get(q.id, [])}
    return len(used & set(pack_ids)) / len(pack_ids) if pack_ids else 0.0


NOMINATE_TASK = """\
New task: nominate candidates for existing queries. Below are search queries (some with hard \
limits). For each query list up to {max_c} assets of the pack that a careful grader should look \
at: first every asset you think is a right answer, then the assets that are arguable or that a \
search system could plausibly return by mistake (the closest lookalikes, same-room objects, \
near variants that miss a stated attribute, objects that only share a word). Be inclusive with \
arguable ones, strict with right ones. A negative-looking query may have no right answer; still \
list the closest lookalikes. Use only references of assets you were shown.

Queries:
{queries}"""


def nominate_schema(ids: list[str]) -> dict:
    item = closed_object(
        {
            "query": {"type": "string", "enum": ids},
            "candidates": {"type": "array", "items": {"type": "string"}},
        }
    )
    return closed_object({"nominations": {"type": "array", "items": item}})


def nominate(
    qs: QuerySet,
    *,
    per_request: int = 20,
    group_size: int = GROUP_SIZE,
    px: int = THUMB_PX,
    model: str = MODEL,
    effort: str = "high",
    client: anthropic.Anthropic | None = None,
    st: Store | None = None,
) -> dict[str, list[str]]:
    """A second, independent pass that widens the judging pool: per query, up to 10 asset ids.

    Sees only each query's text and hard limits, not the drafter's candidates or notes.
    """
    st = st or Store()
    items = pack_items(qs.pack, st)
    refs = {it.ref: it for it in items}
    inv = inventory(items, group_size, px)
    client = client or new_client()
    out: dict[str, list[str]] = {}
    queries = qs.resolved()
    for i in range(0, len(queries), per_request):
        chunk = queries[i : i + per_request]
        listing = "\n".join(
            f"- {q.id}: {q.text}" + (f" [hard limits: {lim}]" if (lim := limits_text(q.filters)) else "")
            for q in chunk
        )  # fmt: skip
        task = {"type": "text", "text": NOMINATE_TASK.format(max_c=MAX_CANDIDATES, queries=listing)}
        params = {
            "model": model,
            "max_tokens": MAX_TOKENS,
            "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
            "output_config": {
                "effort": effort,
                "format": {"type": "json_schema", "schema": nominate_schema([q.id for q in chunk])},
            },
            "messages": [{"role": "user", "content": [*inv, task]}],
        }
        for attempt in (1, 2):
            reply = call_json(client, params)
            if isinstance(reply, str):
                print(f"[nominate] attempt {attempt} failed: {reply}", file=sys.stderr)
                continue
            log_usage("nominate", model, reply)
            for n in reply.data.get("nominations", []):
                ids = [refs[r].id for r in dict.fromkeys(n["candidates"]) if r in refs]
                out[n["query"]] = ids[:MAX_CANDIDATES]
            break
        else:
            raise RuntimeError("nominating candidates failed")
        print(f"[nominate] {min(i + per_request, len(queries))}/{len(queries)}", file=sys.stderr)
    return out
