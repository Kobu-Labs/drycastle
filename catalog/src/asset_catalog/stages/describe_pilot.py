"""Describe, pilot form: the model looks at an asset and describes it with NO fixed vocabulary.

The point of the pilot is to learn which categories, materials, settings and so on actually
occur, so the closed vocabularies of the real describe stage come from the assets and not from
a guess. It also runs several models on the same assets so the describing model is chosen by
comparison. Outputs are kept per (model, run) and never feed the index.
"""

from __future__ import annotations

import base64
import json
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed

import anthropic
from pydantic import BaseModel, Field

from .. import config
from ..model import Asset
from ..store import Store, make_key
from . import measure, render

PROMPT_VERSION = 1
MAX_TOKENS = 8000  # thinking counts toward it

SYSTEM = """\
You are cataloguing 3D game assets so that people and AI agents can later find the right \
asset by searching. Each record you write is the only thing search will know about the asset, \
so what you write decides whether it can be found. A person looking for "something to put on \
an office desk" or "cover a soldier can crouch behind" must land on the right assets, and must \
not land on wrong ones: a confident wrong label is worse than an honest gap.

You get one image with four views of the asset (front, three-quarter, top, back), each with a \
scale bar, and a 1.8 m human figure beside the front view for most assets. You also get the \
file name, the pack it ships in, the names of sibling variants, and facts measured by code.

How to weigh the evidence:
- The pictures are the main evidence for what the asset is and looks like.
- The file name is a strong hint written by the pack's artist, but names are terse and \
sometimes misleading or misspelt. Where the name and the pictures disagree, describe what you \
see and mention the disagreement under "doubts".
- Measured facts (size, triangle count, flat sides, rig) are exact. Never contradict them and \
never restate them as your own estimate. If a measured size is implausible for the object \
(a 6 m rifle), say so under "doubts"; the input tells you when a pack's scale is known to be off.
- "front" is the file's front axis, which for vehicles is often the rear. Judge the real front \
from what you see.

This is the vocabulary-finding pass: there are NO fixed lists. For every list field use the \
words a careful person would naturally use, lowercase, singular, one concept per entry \
("wood", not "wooden parts and metal"). Prefer the common word over the technical one. Leave a \
list empty when nothing applies; do not pad lists to look thorough, since every extra entry \
that is only loosely true makes the asset show up in searches where it does not belong.

The art style of most packs is low-poly with flat colours. Describe the object, not the style \
of rendering, except in the art_style field."""

FIELDS = """\
Field guide:
- plain_name: what a person would call this object in two to five words ("office swivel chair").
- kind: the broadest type. Usually one of: prop, furniture, building, building piece, vehicle, \
vehicle part, character, character part, animal, plant, terrain piece, weapon, weapon \
attachment, sign, decal, effect, sky. Use another word if none fits.
- category_path: from general to specific, 2 to 4 levels, e.g. \
["furniture", "seating", "office chair"]. The last level is the narrowest group that would \
still hold several different assets.
- secondary_categories: other paths the asset equally belongs to (a soda can is drink and also \
litter). Usually empty.
- description: two or three sentences on what is visibly there: shape, parts, colours, state. \
No guesses about use.
- usage: one or two sentences on where and how a level designer would use it.
- aliases: other names people would search by, including synonyms and regional words.
- materials: what it appears to be made of.
- colours: the main colours as a person would name them, most prominent first.
- typical_settings: at most three places where it most belongs (room types, outdoor places).
- placements: how it sits in a scene: on the floor, against a wall, on a wall, hanging from a \
ceiling, on top of furniture, in a corner, freestanding outdoors, part of the ground, and so on.
- functions: what it does or is for, as verbs or short roles, including set-dressing roles \
(sit, store, light, block a path, give cover, litter, decorate).
- styles: period or theme (modern, medieval, industrial, military, rural, post-apocalyptic).
- condition: state of repair (intact, worn, dirty, rusty, damaged, broken, burnt, bloody, \
overgrown). One or two entries.
- art_style: the rendering style in a few words.
- real_front: which view shows the side a user would face or the direction it travels: \
"front", "back", "left", "right", "top", "none" (no meaningful front) or "unsure".
- modular: true when it is a piece meant to snap together with others (wall section, road \
tile, fence segment), with snaps_with naming what it joins to.
- interactive_parts: parts that would plausibly move or open (door, drawer, wheel, turret).
- distinguishing: one sentence on what sets this one apart from its sibling variants, or from \
similar objects when it has none. Empty if you cannot tell from one asset.
- likely_queries: six to ten different things someone might type when this asset is the right \
answer. Mix short keyword searches with longer need-based ones ("something to block a \
doorway"). Each must be specific enough that this asset is a good answer, not one of hundreds.
- expected_size_m: the real-world size range you would expect for this object's longest side.
- doubts: anything you are unsure of, any disagreement between name, pictures and measurements. \
Empty when there is none."""


class PilotRecord(BaseModel):
    plain_name: str
    kind: str
    category_path: list[str]
    secondary_categories: list[list[str]]
    description: str
    usage: str
    aliases: list[str]
    materials: list[str]
    colours: list[str]
    typical_settings: list[str]
    placements: list[str]
    functions: list[str]
    styles: list[str]
    condition: list[str]
    art_style: str
    real_front: str
    modular: bool
    snaps_with: list[str]
    interactive_parts: list[str]
    distinguishing: str
    likely_queries: list[str]
    expected_size_m: list[float] = Field(description="[min, max] of the longest side")
    doubts: list[str]


class PilotOutput(BaseModel):
    record: PilotRecord | None = None
    error: str | None = None
    model: str
    input_tokens: int = 0
    output_tokens: int = 0


def _schema() -> dict:
    s = PilotRecord.model_json_schema()
    s["additionalProperties"] = False
    s["required"] = list(s["properties"])
    for p in s["properties"].values():
        p.pop("title", None)
    s.pop("title", None)
    return s


def stage_name(model: str, run: int) -> str:
    return f"describe-pilot/{model}/run{run}"


def facts(asset: Asset, siblings: list[str], st: Store) -> str | None:
    """The text half of the request; None when the asset has nothing to look at."""
    m = st.get(measure.STAGE, asset.id, measure.Measure)
    r = st.get(render.STAGE, asset.id, render.RenderPayload)
    if m is None or r is None or r.status != "ok" or m.geometry is None:
        return None
    g = m.geometry
    x, y, z = g.size_m
    lines = [
        f"file name: {asset.name}",
        f"pack: {asset.source}/{asset.pack}",
        f"sibling variants: {', '.join(siblings) if siblings else 'none'}",
        f"measured size: {x:.2f} m wide (x), {y:.2f} m tall (y), {z:.2f} m deep (z)",
        f"triangles: {m.tris}",
        (
            f"shape: {g.form} (axis {g.form_axis}); large flat sides: "
            f"{', '.join(g.flat_faces) or 'none'} (-y is the underside, -z the back)"
        ),
        f"origin: {g.pivot.kind}",
    ]
    if m.has_rig:
        clips = ", ".join(a.name for a in m.animations[:12]) or "none"
        lines.append(f"rigged: yes, {m.joints} joints; animations: {clips}")
    if r.dominant_colors:
        cols = ", ".join(f"{c.name} {round(c.share * 100)}%" for c in r.dominant_colors)
        lines.append(f"measured colours (by area): {cols}")
    if r.placeholder_share >= 0.5:
        lines.append("textures: unresolved, drawn neutral grey. Do not report colours.")
    tilted = [f.view for f in r.frames if f.elevation_deg]
    if tilted:
        lines.append(f"views redrawn tilted because the model is flat or edge-on: {tilted}")
    if r.blank_views:
        lines.append(f"views with nothing visible (hair-thin model): {r.blank_views}")
    return "\n".join(lines)


def image_block(asset: Asset, st: Store) -> dict:
    """The asset's contact sheet as an image content block."""
    r = st.get(render.STAGE, asset.id, render.RenderPayload)
    png = (st.root / r.contact_sheet).read_bytes()
    data = base64.standard_b64encode(png).decode()
    return {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": data}}


def request_params(asset: Asset, facts_text: str, model: str, effort: str, st: Store) -> dict:
    return {
        "model": model,
        "max_tokens": MAX_TOKENS,
        "system": [
            {
                "type": "text",
                "text": SYSTEM + "\n\n" + FIELDS,
                "cache_control": {"type": "ephemeral"},
            }
        ],
        "output_config": {"effort": effort, "format": {"type": "json_schema", "schema": _schema()}},
        "messages": [
            {
                "role": "user",
                "content": [
                    image_block(asset, st),
                    {"type": "text", "text": facts_text},
                ],
            }
        ],
    }


def _call(client: anthropic.Anthropic, params: dict) -> PilotOutput:
    model = params["model"]
    try:
        msg = client.messages.create(**params)
    except anthropic.RateLimitError as e:
        return PilotOutput(error=f"rate limit: {e}", model=model)
    except anthropic.APIStatusError as e:
        return PilotOutput(error=f"api {e.status_code}: {e.message}", model=model)
    except anthropic.APIConnectionError as e:
        return PilotOutput(error=f"connection: {e}", model=model)
    usage = {"input_tokens": msg.usage.input_tokens, "output_tokens": msg.usage.output_tokens}
    usage["input_tokens"] += (msg.usage.cache_read_input_tokens or 0) + (
        msg.usage.cache_creation_input_tokens or 0
    )
    if msg.stop_reason != "end_turn":
        return PilotOutput(error=f"stop_reason {msg.stop_reason}", model=model, **usage)
    text = next((b.text for b in msg.content if b.type == "text"), "")
    try:
        record = PilotRecord.model_validate(json.loads(text))
    except ValueError as e:
        return PilotOutput(error=f"bad output: {e}", model=model, **usage)
    return PilotOutput(record=record, model=model, **usage)


def run(
    assets: list[Asset],
    all_assets: list[Asset],
    model: str,
    run_no: int,
    effort: str,
    threads: int = 8,
    force: bool = False,
) -> dict[str, int]:
    st = Store()
    stage = stage_name(model, run_no)
    groups: dict[str, list[str]] = {}
    for a in all_assets:
        if a.variant_group and a.role == "primary":
            groups.setdefault(a.variant_group, []).append(a.name)
    client = anthropic.Anthropic(api_key=config.api_key(), max_retries=6)
    counts = {"fresh": 0, "done": 0, "failed": 0, "skipped": 0, "in": 0, "out": 0}
    todo = []
    for a in assets:
        siblings = [n for n in groups.get(a.variant_group or "", []) if n != a.name][:15]
        text = facts(a, siblings, st)
        if text is None:
            counts["skipped"] += 1
            continue
        key = make_key(
            a.content_hash, PROMPT_VERSION, SYSTEM, FIELDS, model, effort, run_no, text,
            st.key(render.STAGE, a.id),
        )  # fmt: skip
        if not force and st.key(stage, a.id) == key:
            counts["fresh"] += 1
        else:
            todo.append((a, key, text))
    with ThreadPoolExecutor(threads) as pool:
        futures = {
            pool.submit(_call, client, request_params(a, text, model, effort, st)): (a, key)
            for a, key, text in todo
        }
        for fut in as_completed(futures):
            a, key = futures[fut]
            out = fut.result()
            counts["in"] += out.input_tokens
            counts["out"] += out.output_tokens
            if out.error:
                counts["failed"] += 1
                print(f"[{stage}] FAILED {a.id}: {out.error}", file=sys.stderr)
            else:
                st.put(stage, a.id, key, out)
                counts["done"] += 1
    return counts
