"""Describe: a vision model writes each asset's record, constrained to the closed vocabularies.

Facet fields are enums taken from vocab/vocab.yaml, so the output can only hold known terms.
Free-text fields carry everything the vocabularies are too coarse for. Records are kept per
describing model, so models can be compared on the same assets by search results.
"""

from __future__ import annotations

import json
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Literal

import anthropic
from pydantic import BaseModel

from .. import config, vocab
from ..model import Asset
from ..store import Store, make_key
from . import render
from .describe_pilot import facts, image_block

PROMPT_VERSION = 1
MAX_TOKENS = 8000  # thinking counts toward it

SYSTEM = """\
You are cataloguing 3D game assets so that people and AI agents building game levels can find \
the right asset by searching. The record you write is the only thing search will know about \
the asset. Someone looking for "something to put on an office desk" or "cover a soldier can \
crouch behind" must land on the right assets and must not land on wrong ones.

Two failures matter, and the second is the more common one:
- A wrong statement. A confident wrong label sends the asset to people who do not want it and \
hides it from those who do. When you cannot tell, leave the field empty or say so in "doubts".
- A loosely true entry. Every list entry is a promise: someone filtering by that term will be \
shown this asset. Add an entry only if that person would be glad to get this asset. A mug fits \
on a kitchen counter, an office desk, a cafe table and a workbench, yet its settings are the \
one or two places it most belongs. Short, exact lists make search precise; long lists make \
every search return everything.

You get one image with four views of the asset (front, three-quarter, top, back), each with a \
scale bar, and a 1.8 m human figure beside the front view for most assets. You also get the \
file name, the pack, the names of sibling variants, and facts measured by code.

How to weigh the evidence:
- The pictures are the main evidence for what the asset is and how it looks. Check every claim \
against them: say "glass" only if glass is drawn, "open" only if it is shown open.
- The file name is a strong hint from the pack's artist, but names are terse and sometimes \
misleading or misspelt. Where name and pictures disagree, describe what you see and note it in \
"doubts".
- Measured facts are exact. Never contradict them. Colours are measured separately, so mention \
colour only in the description.
- "front" is the file's front axis, which for vehicles is often the rear. Judge the real front \
from what you see.
- Sibling names tell you what varies between variants. Say what sets this one apart only from \
what you can see in this asset; you cannot see the siblings.

Most packs are low-poly with flat colours. Describe the object, not the rendering style."""

FIELDS = """\
# Fields

Closed fields take only terms from the vocabularies below. Read each term's definition: it \
says when the term applies and which neighbour it must not be confused with.
- kind: the one broadest type.
- category: the one leaf this asset most belongs to. Wrecked or damaged versions keep the \
leaf of the intact object.
- secondary_categories: other leaves it belongs to just as much. Usually empty, at most two.
- materials: what it is visibly made of, main material first. At most three.
- settings: where it most belongs. At most three, most specific first; often one or two.
- placements: how it sits in a scene. All that are natural for it, usually one or two.
- functions: what it does for a level designer or player. Often empty for pure set dressing.
- styles: theme or period, only when the asset itself shows it. Empty for an ordinary \
present-day object.
- condition: one term, or two when both clearly show.
- real_front: the view that shows the side a user faces or the direction of travel; "none" \
when it has no meaningful front, "unsure" when you cannot tell.

Open fields:
- plain_name: what a person would call it, two to five words ("office swivel chair").
- description: two or three sentences on what is visibly there: shape, parts, colours, state.
- usage: one or two sentences on where and how a level designer would use it.
- aliases: other names people would search by: synonyms, regional words, the common misnomer. \
Three to eight. No descriptions of use, only names.
- distinguishing: one short sentence on what sets it apart from similar objects, from what is \
visible. Empty if nothing does.
- snaps_with: for a modular piece, what it joins to ("wall panel", "fence post"). Else empty.
- likely_queries: six to eight searches for which this asset is a right answer. Mix short \
keyword searches with need-based ones ("something to block a doorway"). Each must be specific \
enough that this asset is a good answer and not one of hundreds; do not repeat one idea in \
different words.
- doubts: real uncertainties and disagreements between name, pictures and measurements, each \
in one sentence. Empty when there are none; do not list things you checked and found fine.

# Vocabularies

"""

Front = Literal["front", "back", "left", "right", "top", "none", "unsure"]


class Record(BaseModel):
    plain_name: str
    kind: str
    category: str
    secondary_categories: list[str]
    description: str
    usage: str
    aliases: list[str]
    materials: list[str]
    settings: list[str]
    placements: list[str]
    functions: list[str]
    styles: list[str]
    condition: list[str]
    real_front: Front
    distinguishing: str
    snaps_with: list[str]
    likely_queries: list[str]
    doubts: list[str]


class Described(BaseModel):
    record: Record
    model: str
    effort: str
    input_tokens: int = 0
    output_tokens: int = 0


# record field -> the facet whose terms it may hold
CLOSED_LISTS = {
    "materials": "material",
    "settings": "setting",
    "placements": "placement",
    "functions": "function",
    "styles": "style",
    "condition": "condition",
}


def schema(v: vocab.Vocab) -> dict:
    s = Record.model_json_schema()
    props = s["properties"]
    for p in props.values():
        p.pop("title", None)
    props["kind"] = {"type": "string", "enum": list(v.facets["kind"])}
    props["category"] = {"type": "string", "enum": list(v.categories)}
    props["secondary_categories"]["items"] = {"type": "string", "enum": list(v.categories)}
    for field, facet in CLOSED_LISTS.items():
        props[field]["items"] = {"type": "string", "enum": list(v.facets[facet])}
    return {
        "type": "object",
        "additionalProperties": False,
        "required": list(props),
        "properties": props,
    }


def violations(r: Record, v: vocab.Vocab) -> list[str]:
    """Terms outside the vocabularies. The schema should prevent them; this proves it did."""
    bad = [] if r.kind in v.facets["kind"] else [f"kind={r.kind}"]
    bad += [f"category={c}" for c in [r.category, *r.secondary_categories] if c not in v.categories]
    for field, facet in CLOSED_LISTS.items():
        bad += [f"{field}={t}" for t in getattr(r, field) if t not in v.facets[facet]]
    return bad


def stage_name(model: str) -> str:
    return f"describe/{model}"


def request_params(asset: Asset, facts_text: str, model: str, effort: str, st: Store) -> dict:
    v = vocab.load()
    return {
        "model": model,
        "max_tokens": MAX_TOKENS,
        "system": [
            {
                "type": "text",
                "text": SYSTEM + "\n\n" + FIELDS + v.guide(),
                "cache_control": {"type": "ephemeral"},
            }
        ],
        "output_config": {"effort": effort, "format": {"type": "json_schema", "schema": schema(v)}},
        "messages": [
            {
                "role": "user",
                "content": [image_block(asset, st), {"type": "text", "text": facts_text}],
            }
        ],
    }


def _call(client: anthropic.Anthropic, params: dict, effort: str) -> Described | str:
    """The record, or the reason there is none."""
    try:
        msg = client.messages.create(**params)
    except anthropic.RateLimitError as e:
        return f"rate limit: {e}"
    except anthropic.APIStatusError as e:
        return f"api {e.status_code}: {e.message}"
    except anthropic.APIConnectionError as e:
        return f"connection: {e}"
    if msg.stop_reason != "end_turn":
        return f"stop_reason {msg.stop_reason}"
    text = next((b.text for b in msg.content if b.type == "text"), "")
    try:
        record = Record.model_validate(json.loads(text))
    except ValueError as e:
        return f"bad output: {e}"
    if bad := violations(record, vocab.load()):
        return f"terms outside the vocabulary: {bad}"
    u = msg.usage
    return Described(
        record=record,
        model=params["model"],
        effort=effort,
        input_tokens=u.input_tokens
        + (u.cache_read_input_tokens or 0)
        + (u.cache_creation_input_tokens or 0),
        output_tokens=u.output_tokens,
    )


def describable(asset: Asset) -> bool:
    return asset.media == "model" and asset.role == "primary" and asset.kind_hint is None


def run(
    assets: list[Asset],
    all_assets: list[Asset],
    model: str,
    effort: str,
    threads: int = 4,
    force: bool = False,
) -> dict[str, int]:
    st = Store()
    v = vocab.load()
    stage = stage_name(model)
    groups: dict[str, list[str]] = {}
    for a in all_assets:
        if a.variant_group and a.role == "primary":
            groups.setdefault(a.variant_group, []).append(a.name)
    client = anthropic.Anthropic(api_key=config.api_key(), max_retries=8)
    counts = {"fresh": 0, "done": 0, "failed": 0, "skipped": 0, "in": 0, "out": 0}
    todo = []
    for a in assets:
        siblings = [n for n in groups.get(a.variant_group or "", []) if n != a.name][:15]
        text = facts(a, siblings, st) if describable(a) else None
        if text is None:
            counts["skipped"] += 1
            continue
        key = make_key(
            a.content_hash, PROMPT_VERSION, SYSTEM, FIELDS, v.text_hash, model, effort, text,
            st.key(render.STAGE, a.id),
        )  # fmt: skip
        if not force and st.key(stage, a.id) == key:
            counts["fresh"] += 1
        else:
            todo.append((a, key, text))
    with ThreadPoolExecutor(threads) as pool:
        futures = {
            pool.submit(_call, client, request_params(a, text, model, effort, st), effort): (a, key)
            for a, key, text in todo
        }
        for fut in as_completed(futures):
            a, key = futures[fut]
            out = fut.result()
            if isinstance(out, str):
                counts["failed"] += 1
                print(f"[{stage}] FAILED {a.id}: {out}", file=sys.stderr)
                continue
            st.put(stage, a.id, key, out)
            counts["done"] += 1
            counts["in"] += out.input_tokens
            counts["out"] += out.output_tokens
    return counts
