"""Keyword retriever: BM25 over weighted FTS5 columns, with the care a bare MATCH lacks.

How a query is searched
- The text is cut into plain words (search.text.parse_query); nothing else reaches FTS5, so
  quotes, operators and punctuation cannot break or steer the query. Stopwords, request filler
  ("something", "put"), bare numbers and negated words ("without wheels") are dropped.
- OR semantics: every remaining word is searched on its own and BM25 scores add up, so an asset
  that matches more (and rarer) words ranks higher, and a long natural-language query still
  finds assets that match only part of it.
- A word the index has never seen is repaired: nearest words of the catalogue (misspelling),
  or split into two catalogue words ("whiteboard" -> "white board"). Repairs score a bit lower.
- Neighbouring words are also searched as a phrase, and as one joined word when the catalogue
  has it ("book case" -> "bookcase"); phrase matches add a bonus.
- An asset must cover enough of the query (min_coverage of its idf mass, unseen words counting
  at unknown_mass) to be returned: a query whose words are mostly nowhere returns few or no hits.
- A query equal to an asset's plain name, alias or file name (or containing it, with few extra
  words) puts that asset first.

The tokenizer is porter + unicode61: "desks"/"desk" and "mounted"/"mounting" meet in the index,
and hyphens and underscores are separators, so "wall-mounted" is the words "wall mounted" on
both sides. Prefix matching is not used: "car" must not return "carpet" and "card".
"""

from __future__ import annotations

import argparse
import itertools
import math
import re
from dataclasses import dataclass, field

from ..index import FTS_COLUMNS, LINE_COLUMNS, LINE_SEPARATOR, Index, open_index
from .spelling import Speller
from .text import STOPWORDS, parse_query, query_key, query_words
from .types import Filters, Hit

# bm25 weight per FTS column: what a person calls it counts most, long prose least
FIELD_WEIGHTS = {
    "plain_name": 10.0,
    "aliases": 8.0,
    "name": 8.0,
    "category": 3.0,
    "facets": 2.5,
    "likely_queries": 3.0,
    "distinguishing": 2.0,
    "usage": 1.5,
    "description": 1.5,
}
# share of the best score added when the query equals / is contained in a key of that field
KEY_BONUS = {
    "plain_name": 1.0,
    "alias": 0.9,
    "name": 0.9,
    "name_base": 0.7,
    "likely_query": 0.4,
}


@dataclass(frozen=True)
class KeywordParams:
    field_weights: dict[str, float] = field(default_factory=lambda: dict(FIELD_WEIGHTS))
    key_bonus: dict[str, float] = field(default_factory=lambda: dict(KEY_BONUS))
    near_share: float = 0.5  # a contained (not equal) key earns this share of the bonus
    near_extra_words: int = 2  # ... when it has at most this many words more than the query
    phrase_weight: float = 0.5  # share of a phrase's own bm25 added on top of its words
    repair_weight: float = 0.8  # a repaired word scores this share of a real one
    fuzzy: bool = True  # misspelling and compound repair
    min_coverage: float = 0.3  # share of the query's idf mass an asset must match; 0 = any
    unknown_mass: float = 0.75  # idf mass of an unseen word, as a share of the largest
    max_terms: int = 24

    def weights(self) -> tuple[float, ...]:
        return tuple(self.field_weights[c] for c in FTS_COLUMNS)


@dataclass
class Alt:
    """One way to match a unit: an FTS5 expression and how much it counts."""

    expr: str
    weight: float = 1.0


@dataclass
class Unit:
    """One query word and what it can be matched as."""

    word: str
    alts: list[Alt]


def quote(word_list: list[str]) -> str:
    """An FTS5 phrase of plain words. Words come from search.text and are letters and digits."""
    return '"' + " ".join(word_list) + '"'


def idf(n_docs: int, df: int) -> float:
    return math.log(1 + (n_docs - df + 0.5) / (df + 0.5))


class KeywordRetriever:
    name = "keyword"

    def __init__(self, index: Index, params: KeywordParams | None = None):
        self.index = index
        self.params = params or KeywordParams()
        self._speller: Speller | None = None
        self._df: dict[str, int] = {}

    # ------------------------------------------------------------ planning

    def _df_of(self, expr: str) -> int:
        if expr not in self._df:
            self._df[expr] = self.index.fts_count(expr)
        return self._df[expr]

    def speller(self) -> Speller:
        if self._speller is None:
            self._speller = Speller(self.index.words())
        return self._speller

    def _repairs(self, word: str) -> list[Alt] | None:
        """Alternatives for a word the catalogue has never seen; None when it is a misspelt
        stopword ("somthing"), which is then dropped like the stopword itself."""
        vocab = self.index.words()
        near = self.speller().suggest(word, limit=6)
        content = [s for s in near if s not in STOPWORDS]
        if near and not content:
            return None  # only stopwords are near
        alts = [Alt(quote([s]), self.params.repair_weight) for s in content[:4]]
        for cut in range(3, len(word) - 2):
            head, tail = word[:cut], word[cut:]
            if head in vocab and tail in vocab:
                alts.append(Alt(quote([head, tail]), self.params.repair_weight))
        return alts

    def _unit(self, word: str) -> Unit | None:
        alts = [Alt(quote([word]))]
        df = self._df_of(alts[0].expr)
        if df == 0 and self.params.fuzzy:
            repairs = self._repairs(word)
            if repairs is None:
                return None
            alts += repairs
        return Unit(word, alts)

    def _singles(self, unit: Unit) -> list[tuple[str, float]]:
        """The one-word alternatives of a unit, and their weights."""
        return [(a.expr.strip('"'), a.weight) for a in unit.alts if " " not in a.expr]

    def _neighbours(self, units: dict[str, Unit], pairs: list[tuple[str, str]]) -> list[Alt]:
        """Words that were neighbours in the query are also searched as a phrase, and as the
        one word the catalogue writes them as ("book case" -> "bookcase"), which counts as a
        match of both. Returns the phrases; the joined words go into the units."""
        vocab = self.index.words()
        phrases = []
        for a, b in pairs:
            if a not in units or b not in units:
                continue
            for (wa, xa), (wb, xb) in itertools.product(
                self._singles(units[a]), self._singles(units[b])
            ):
                if self._df_of(quote([wa])) and self._df_of(quote([wb])):
                    phrases.append(Alt(quote([wa, wb]), self.params.phrase_weight * min(xa, xb)))
                if wa + wb in vocab:
                    joined = Alt(quote([wa + wb]), min(xa, xb))
                    units[a].alts.append(joined)
                    units[b].alts.append(joined)
        return phrases

    def _mass(self, unit: Unit) -> float:
        """How much the word says: idf of its most common alternative; 0 when nothing matches."""
        df = max(self._df_of(a.expr) for a in unit.alts)
        return idf(self.index.size, df) if df else 0.0

    # ------------------------------------------------------------ scoring

    def _coverage_floor(self, units: list[Unit]) -> float:
        unseen = idf(self.index.size, 0) * self.params.unknown_mass
        total = sum(self._mass(u) or unseen for u in units)
        return self.params.min_coverage * total

    def _score(
        self, units: list[Unit], phrases: list[Alt], filters: Filters
    ) -> tuple[dict[str, float], dict[str, float]]:
        """Per asset passing filters: summed bm25, and the idf mass of the words it matched."""
        weights = self.params.weights()
        score: dict[str, float] = {}
        mass: dict[str, float] = {}
        for u in units:
            best: dict[str, float] = {}
            for alt in u.alts:
                for i, s in self.index.fts_scores(alt.expr, filters, weights).items():
                    best[i] = max(best.get(i, 0.0), s * alt.weight)
            for i, s in best.items():
                score[i] = score.get(i, 0.0) + s
                mass[i] = mass.get(i, 0.0) + self._mass(u)
        for p in phrases:
            for i, s in self.index.fts_scores(p.expr, filters, weights).items():
                if i in score:
                    score[i] += s * p.weight
        return score, mass

    def _key_bonuses(
        self, query: str, filters: Filters, top: float
    ) -> tuple[dict[str, float], dict[str, str]]:
        key = query_key(query_words(query))
        if not key:
            return {}, {}
        bonus: dict[str, float] = {}
        note: dict[str, str] = {}
        for i, field_, extra in self.index.key_matches(key, filters, self.params.near_extra_words):
            share = self.params.key_bonus.get(field_, 0.0)
            share *= 1.0 if extra == 0 else self.params.near_share / extra
            if share * top > bonus.get(i, 0.0):
                bonus[i] = share * top
                note[i] = f"{'exact' if extra == 0 else 'near'} {field_.replace('_', ' ')}"
        return bonus, note

    # ------------------------------------------------------------ the protocol

    def retrieve(self, query: str, filters: Filters, k: int) -> list[Hit]:
        parsed = parse_query(query, self.params.max_terms)
        if k <= 0 or not parsed.terms and not parsed.tokens:
            return []
        made = {w: self._unit(w) for w in parsed.terms}
        by_word = {w: u for w, u in made.items() if u is not None}
        phrases = self._neighbours(by_word, parsed.adjacent_pairs())
        units = list(by_word.values())
        score, mass = self._score(units, phrases, filters)
        floor = self._coverage_floor(units)
        kept = {i: s for i, s in score.items() if mass[i] >= floor}
        top = max(kept.values(), default=1.0)
        bonus, note = self._key_bonuses(query, filters, top)
        for i, b in bonus.items():
            kept[i] = kept.get(i, 0.0) + b
        ranked = sorted(kept.items(), key=lambda kv: (-kv[1], kv[0]))[:k]
        why = self._why([i for i, _ in ranked], units, phrases)
        return [
            Hit(id=i, score=s, why=([note[i]] if i in note else []) + why.get(i, []))
            for i, s in ranked
        ]

    # ------------------------------------------------------------ explanation

    def _why(self, ids: list[str], units: list[Unit], phrases: list[Alt]) -> dict[str, list[str]]:
        exprs = [a.expr for u in units for a in u.alts] + [p.expr for p in phrases]
        if not ids or not exprs:
            return {}
        found = self.index.fts_highlights(" OR ".join(exprs), ids)
        return {
            i: [n for c in FTS_COLUMNS if c in cols for n in _notes(c, cols[c])]
            for i, cols in found.items()
        }


_MARK = re.compile("«([^»]*)»")


def _notes(column: str, text: str) -> list[str]:
    """Display notes for one matched column: matched entries for aliases and likely queries
    ("alias: water dispenser"), the matched words for the others."""
    if column in LINE_COLUMNS:
        label = "alias" if column == "aliases" else "query"
        entries = [e.strip() for e in text.split(LINE_SEPARATOR) if "«" in e]
        return [f"{label}: {e.replace('«', '').replace('»', '')}" for e in entries[:2]]
    matched = list(dict.fromkeys(_MARK.findall(text)))
    label = column.replace("_", " ")
    return [f"{label}: {', '.join(matched)}"] if matched else []


def cli(args: argparse.Namespace) -> None:
    index = open_index(args.model)
    filters = Filters(packs=args.pack or [], categories=args.category or [])
    for n, hit in enumerate(KeywordRetriever(index).retrieve(args.query, filters, args.k), 1):
        rec = index.get(hit.id)
        print(f"{n:2} {hit.score:7.2f} {hit.id:34} {rec.plain_name}")
        print(f"{'':11}{'; '.join(hit.why[:4])}")
