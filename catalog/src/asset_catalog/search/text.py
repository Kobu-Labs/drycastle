"""Text analysis shared by the index and the query: one tokeniser for both sides.

Index and query pass through the same functions, so what is indexed is what a query can match.
Stemming is left to the FTS5 tokenizer (porter); the words here are plain lowercase ASCII.
"""

from __future__ import annotations

import re
import unicodedata

_WORD = re.compile(r"[a-z0-9]+")
_APOSTROPHE = re.compile("(?<=[A-Za-z])['" + chr(0x2019) + "](?=[A-Za-z])")
# "WaterCooler" -> Water|Cooler, "PCMonitor" -> PC|Monitor
_CAMEL = re.compile(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
_NON_ALNUM = re.compile(r"[^A-Za-z0-9]+")
# artist prefixes of the packs ("SM_" static mesh, "SK_" skeletal mesh, ...): not words
NAME_PREFIXES = frozenset({"sm", "sk", "pfb", "fx", "bp"})

_ARTICLES = frozenset({"a", "an", "the"})
_PLAIN = (
    "a an the and or but nor of to in on at by for from with without into onto over under up "
    "down out off as is are was were be been being am do does did has have had it its this that "
    "these those there here which who whom whose what when where why how i me my we our you your "
    "he she they them their his her can could would should will shall may might must also very "
    "too so than then just only more most some any all each every such own same other another "
    "about around near next like if not no yes via per etc "
)
# filler of a request, not of an asset: "something small to put on a reception desk"
_FILLER = (
    "something anything everything thing things stuff looking look looks need needs needed want "
    "wants wanted find finding show give get got put puts place placed placing use used using "
    "kind kinds sort sorts type types asset assets model models object objects item items "
    "please pls good nice cool best suitable appropriate fitting"
)
STOPWORDS = frozenset(_WORD.findall(_PLAIN + _FILLER))
# the word after one of these is excluded, not wanted: "chair without wheels"
NEGATORS = frozenset({"without", "no", "not", "except", "excluding", "minus", "nor"})


def fold(text: str) -> str:
    """Lowercase ASCII: accents stripped, apostrophes inside words removed."""
    text = _APOSTROPHE.sub("", text)
    text = unicodedata.normalize("NFKD", text)
    return "".join(c for c in text if not unicodedata.combining(c)).lower()


def words(text: str) -> list[str]:
    """Every alphanumeric run, lowercase, in order. "wall-mounted" -> ["wall", "mounted"]."""
    return _WORD.findall(fold(text))


def _stem_parts(stem: str) -> list[str]:
    """Words of a file stem or query with camelCase split and a leading artist prefix dropped."""
    parts = [p for c in _NON_ALNUM.split(_APOSTROPHE.sub("", stem)) for p in _CAMEL.split(c) if p]
    out = [w for p in parts for w in words(p)]
    return out[1:] if len(out) > 1 and out[0] in NAME_PREFIXES else out


def name_words(stem: str) -> list[str]:
    """Words of a file stem. "SM_Prop_WaterCooler_01" -> ["prop", "water", "cooler"].

    Number-only parts (variant numbers) are dropped; name_key keeps them for exact matches.
    """
    return [w for w in _stem_parts(stem) if not w.isdigit()]


def query_words(text: str) -> list[str]:
    """Words of a user query. Like words(), and a pasted file name ("SM_Prop_WaterCooler_01")
    or camelCase word is split the way the index splits file names."""
    return _stem_parts(text)


def light_stem(word: str) -> str:
    """Plural folding for exact-match keys only: "coolers" -> "cooler", "shelves" -> "shelf"."""
    if word.isdigit():
        return str(int(word))
    if len(word) <= 3:
        return word
    for suffix, repl in (
        ("ies", "y"),
        ("ves", "f"),
        ("sses", "ss"),
        ("ches", "ch"),
        ("shes", "sh"),
    ):
        if word.endswith(suffix):
            return word[: -len(suffix)] + repl
    if word.endswith("s") and not word.endswith(("ss", "us", "is")):
        return word[:-1]
    return word


def key_of(ws: list[str]) -> str:
    """Normalised phrase for exact matching: plural-folded words, numbers without zeros."""
    return " ".join(light_stem(w) for w in ws)


def name_key(stem: str, with_number: bool = True) -> str:
    """Key of a file stem; "SM_Prop_WaterCooler_01" -> "prop water cooler 1"."""
    ws = _stem_parts(stem)
    return key_of(ws if with_number else [w for w in ws if not w.isdigit()])


def query_key(ws: list[str]) -> str:
    """Key of a user query: leading articles dropped."""
    while ws and ws[0] in _ARTICLES:
        ws = ws[1:]
    return key_of(ws)


class ParsedQuery:
    """A user query reduced to safe tokens. Nothing of the original text survives but words."""

    def __init__(self, tokens: list[str], terms: list[str], breaks: set[int]):
        self.tokens = tokens  # every word, in order
        self.terms = terms  # content words kept for matching, no repeats
        self.breaks = breaks  # indexes into terms after which the phrase is broken

    def adjacent_pairs(self) -> list[tuple[str, str]]:
        """Neighbouring content words that were neighbours in the query."""
        return [
            (self.terms[i], self.terms[i + 1])
            for i in range(len(self.terms) - 1)
            if i not in self.breaks
        ]


def parse_query(text: str, max_terms: int = 24) -> ParsedQuery:
    """Words of a query minus stopwords, bare numbers and negated words; at most max_terms.

    Characters other than letters and digits only separate words, so quotes, operators
    (AND, NEAR, *, ^, :) and brackets can never reach the FTS5 query syntax.
    """
    terms: list[str] = []
    breaks: set[int] = set()
    negate = False
    tokens = query_words(text)
    for w in tokens:
        if w in NEGATORS:
            negate = True
            _break(terms, breaks)
        elif w in STOPWORDS or w.isdigit() or len(w) < 2:
            _break(terms, breaks)
        elif negate:
            negate = False
            _break(terms, breaks)
        elif w not in terms:
            terms.append(w)
        else:
            _break(terms, breaks)
    return ParsedQuery(tokens, terms[:max_terms], {b for b in breaks if b < max_terms})


def _break(terms: list[str], breaks: set[int]) -> None:
    if terms:
        breaks.add(len(terms) - 1)
