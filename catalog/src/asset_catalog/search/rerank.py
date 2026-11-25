"""Rerank: a model reads the request and each candidate's record and grades the match."""

from __future__ import annotations

from ..record import CatalogRecord
from .llm import ask

SYSTEM = """\
You are the final judge in a search engine for 3D game assets. A person or an AI agent \
building a game level made a request; earlier stages collected candidate assets, some right \
and many only superficially similar. You read each candidate's catalogue record and grade how \
well it answers the request. The searcher will act on the top result, often without looking \
further, so the order you produce is what they experience as the quality of the search.

Grades:
3 - what was asked for. The searcher would take it without hesitation.
2 - a good answer: fits the request, with a minor difference from the ideal.
1 - usable if nothing better exists: related, or fits only part of the request.
0 - not an answer. Shares words or a theme with the request but is a different thing, or \
breaks something the request required (a size, a place, a type).

Judge against what the request means, not which words it shares with the record. For a \
request that describes a need ("something to make a waiting area feel less empty") ask whether \
a level designer with that need would be glad to be handed this asset. Use the measured size \
when the request implies one: a 2 m cabinet is not something to put on a desk. Records can \
contain mistakes; when the description and the facet terms disagree, trust the description.

If nothing fits, give every candidate 0. Returning nothing is the correct result for a request \
the catalogue cannot answer, and far better than a confident wrong one.

Grade every candidate exactly once, by its number."""


def card(n: int, r: CatalogRecord) -> str:
    """A candidate as the judge reads it: compact, but everything a decision could turn on."""
    w, h, d = r.size_m
    lines = [
        f"[{n}] {r.plain_name} ({r.name}, pack {r.pack})",
        f"category: {r.category}" + "".join(f"; also {c}" for c in r.secondary_categories),
        f"size: {w:.2f} m wide, {h:.2f} m tall, {d:.2f} m deep; colours: {', '.join(r.colours)}",
        f"description: {r.description}",
        f"usage: {r.usage}",
        f"also called: {', '.join(r.aliases)}",
        (
            f"materials: {', '.join(r.materials)}; condition: {', '.join(r.condition)}; "
            f"styles: {', '.join(r.styles) or '-'}"
        ),
        (
            f"settings: {', '.join(r.settings) or '-'}; placement: {', '.join(r.placements) or '-'}; "
            f"functions: {', '.join(r.functions) or '-'}"
        ),
    ]
    if r.distinguishing:
        lines.append(f"distinguishing: {r.distinguishing}")
    return "\n".join(lines)


_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["grades"],
    "properties": {
        "grades": {
            "type": "array",
            "items": {
                "type": "object",
                "additionalProperties": False,
                "required": ["n", "grade"],
                "properties": {
                    "n": {"type": "integer"},
                    "grade": {"type": "integer", "enum": [0, 1, 2, 3]},
                },
            },
        }
    },
}


def grade(
    query: str, constraints: str, candidates: list[CatalogRecord], model: str, effort: str
) -> dict[str, int]:
    """Asset id -> grade 0..3. A candidate the model skipped gets no entry.

    Raises llm.LlmError when the model gives no usable answer.
    """
    if not candidates:
        return {}
    user = f"Request: {query}\n"
    if constraints:
        user += f"Constraints already enforced on every candidate: {constraints}\n"
    user += "\nCandidates:\n\n" + "\n\n".join(card(i, r) for i, r in enumerate(candidates, 1))
    raw = ask(model, effort, SYSTEM, user, _SCHEMA, 4000 + 40 * len(candidates))
    out: dict[str, int] = {}
    for g in raw["grades"]:
        if 1 <= g["n"] <= len(candidates):
            out[candidates[g["n"] - 1].id] = g["grade"]
    return out
