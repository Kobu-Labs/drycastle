"""The closed vocabularies: every filterable value a record may carry, with its definition."""

from __future__ import annotations

from dataclasses import dataclass
from functools import cache

import yaml

from .config import CATALOG_DIR

FACETS = ("kind", "material", "setting", "placement", "function", "style", "condition")


@dataclass(frozen=True)
class Vocab:
    facets: dict[str, dict[str, str]]  # facet -> term -> definition
    categories: dict[str, str]  # leaf path -> definition
    text_hash: str

    def guide(self) -> str:
        """The vocabularies as the describing model reads them."""
        out = []
        for facet, terms in self.facets.items():
            out.append(f"## {facet}\n" + "\n".join(f"- {t}: {d}" for t, d in terms.items()))
        out.append("## category\n" + "\n".join(f"- {p}: {d}" for p, d in self.categories.items()))
        return "\n\n".join(out)


@cache
def load() -> Vocab:
    text = (CATALOG_DIR / "vocab" / "vocab.yaml").read_text()
    raw = yaml.safe_load(text)
    from .store import make_key

    return Vocab(
        facets={f: dict(raw[f]) for f in FACETS},
        categories={p: c["definition"] for p, c in raw["category"].items()},
        text_hash=make_key(text),
    )
