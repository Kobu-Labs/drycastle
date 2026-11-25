"""Query understanding: split what the searcher typed into constraints, preferences and text."""

from __future__ import annotations

from pydantic import BaseModel

from .. import vocab
from .llm import ask
from .types import Filters

SYSTEM = """\
You prepare search requests for a catalogue of 3D game assets (props, furniture, buildings, \
vehicles, plants, characters). People and AI agents building game levels type a request; you \
split it into three parts so the search engine can treat each correctly.

1. hard: constraints the searcher stated outright and would not accept a result without. \
Sizes ("under 30 cm", "fits a 2 m gap"), an explicit type ("a chair", "only furniture"), an \
explicit material, placement, pack or condition ("wall-mounted", "rusty", "from the office \
pack"). Hard constraints remove every asset that does not match, including assets whose record \
is slightly off, so use them only for what was actually said. "Something for a kitchen" does \
not state a type. When unsure whether a word is a constraint or a wish, it is a wish.
   Sizes: width is x, height is y, depth is z, in metres. "Small enough to hold" or "fits on \
a desk" are wishes, not numbers; use a number only when the searcher gave one.
   A stated object type becomes a category path or prefix only when it clearly names one \
("a chair" -> furniture/seating/chair; "seating" -> furniture/seating). Never set both kinds \
and categories for the same thing; the category already implies the kind.

2. soft: facet terms the request suggests without demanding. "Clutter for an abandoned \
kitchen" suggests setting kitchen and conditions worn or damaged. These raise matching assets \
in the ranking and remove nothing. Give the few terms that would most help; leave fields empty \
when nothing is suggested.

3. text: the request rewritten as the search text, with the hard constraints you extracted \
removed when they are purely numeric or structural (sizes, pack names), and everything about \
what the thing is or is for kept in the searcher's own words. Do not add synonyms or \
guesses; the engine handles those.

Use only terms from the vocabularies below for facet fields. If the request names something \
no term covers, leave the facet empty and keep the words in text."""


class Parsed(BaseModel):
    text: str
    hard: Filters = Filters()
    soft: Filters = Filters()


_FACET_FIELDS = {
    "kinds": "kind",
    "materials": "material",
    "settings": "setting",
    "placements": "placement",
    "functions": "function",
    "styles": "style",
    "condition": "condition",
}
_SIZE_FIELDS = [
    "min_width_m", "max_width_m", "min_height_m", "max_height_m",
    "min_depth_m", "max_depth_m", "min_longest_m", "max_longest_m",
]  # fmt: skip


def _filters_schema(v: vocab.Vocab, with_sizes: bool) -> dict:
    props: dict = {
        field: {"type": "array", "items": {"type": "string", "enum": list(v.facets[facet])}}
        for field, facet in _FACET_FIELDS.items()
    }
    props["categories"] = {"type": "array", "items": {"type": "string"}}
    if with_sizes:
        props |= {f: {"type": ["number", "null"]} for f in _SIZE_FIELDS}
        props["packs"] = {"type": "array", "items": {"type": "string"}}
        props["rigged"] = {"type": ["boolean", "null"]}
    return {
        "type": "object",
        "additionalProperties": False,
        "required": list(props),
        "properties": props,
    }


def _schema(v: vocab.Vocab) -> dict:
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["text", "hard", "soft"],
        "properties": {
            "text": {"type": "string"},
            "hard": _filters_schema(v, with_sizes=True),
            "soft": _filters_schema(v, with_sizes=False),
        },
    }


def _guide(v: vocab.Vocab) -> str:
    lines = [f"{facet}: {', '.join(terms)}" for facet, terms in v.facets.items()]
    lines.append("category paths (a prefix such as 'furniture/seating' is allowed):")
    lines += sorted(v.categories)
    return "\n".join(lines)


def _valid_categories(paths: list[str], v: vocab.Vocab) -> list[str]:
    """Keep leaves and real prefixes; a made-up path would filter everything out."""
    return [p for p in paths if any(c == p or c.startswith(p + "/") for c in v.categories)]


def parse(query: str, model: str, effort: str = "low") -> Parsed:
    """Raises llm.LlmError when the model gives no usable answer."""
    v = vocab.load()
    raw = ask(model, effort, SYSTEM + "\n\n# Vocabularies\n\n" + _guide(v), query, _schema(v), 2000)
    parsed = Parsed.model_validate(raw)
    parsed.hard.categories = _valid_categories(parsed.hard.categories, v)
    parsed.soft.categories = _valid_categories(parsed.soft.categories, v)
    if not parsed.text.strip():
        parsed.text = query
    return parsed
