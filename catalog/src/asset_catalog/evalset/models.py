"""What an evaluation set holds: queries, and graded judgments of (query, asset) pairs."""

from __future__ import annotations

import hashlib
from typing import Literal, get_args

from pydantic import BaseModel, Field, field_validator, model_validator

from ..search.types import Filters

Style = Literal["keyword", "descriptive", "need", "constraint", "negative"]
STYLES: tuple[str, ...] = get_args(Style)
Answers = Literal["one", "few", "many", "none"]  # how many assets are right answers, roughly

# 0 not relevant, 1 acceptable, 2 good answer, 3 ideal answer
GRADES = (0, 1, 2, 3)
GOOD = 2  # grade from which an asset counts as "a right answer" for recall and MRR
ACCEPTABLE = 1


class Query(BaseModel):
    id: str
    text: str
    style: Style
    filters: Filters = Filters()  # hard constraints, for constraint queries
    answers: Answers | None = None  # drafter's estimate; analysis only, never used to score
    notes: str = ""

    def digest(self) -> str:
        """Identity of what is being asked: judgments are valid only while it is unchanged."""
        limits = self.filters.model_dump_json(
            exclude={"packs", "exclude_ids"}
        )  # the set adds the pack
        payload = self.text.strip() + "\0" + limits
        return hashlib.sha256(payload.encode()).hexdigest()[:16]


class QuerySet(BaseModel):
    name: str
    pack: str  # "<source>/<pack>"; added to every query's filters, so the set stays valid later
    queries: list[Query]

    @model_validator(mode="after")
    def _unique_ids(self) -> QuerySet:
        ids = [q.id for q in self.queries]
        if dupes := sorted({i for i in ids if ids.count(i) > 1}):
            raise ValueError(f"duplicate query ids: {dupes}")
        return self

    def get(self, query_id: str) -> Query:
        return next(q for q in self.queries if q.id == query_id)

    def resolved(self) -> list[Query]:
        """The queries as a search system must run them: restricted to this set's pack."""
        out = []
        for q in self.queries:
            f = q.filters
            if not f.packs:
                f = f.model_copy(update={"packs": [self.pack]})
            out.append(q.model_copy(update={"filters": f}))
        return out


class Judgment(BaseModel):
    query_id: str
    asset_id: str
    grade: int
    judged_by: Literal["model", "human"]
    judge: str  # model id, or the human's name
    reason: str = ""
    query_digest: str = ""  # the query this was judged for; a changed query makes it stale
    rubric_version: int = 0
    at: str = ""

    @field_validator("grade")
    @classmethod
    def _grade(cls, v: int) -> int:
        if v not in GRADES:
            raise ValueError(f"grade must be one of {GRADES}, got {v}")
        return v


class Override(BaseModel):
    """A human's grade for one (query, asset). Wins over any model judgment."""

    query: str
    asset: str
    grade: int = Field(ge=0, le=3)
    reason: str = ""
    by: str = "human"


def limits_text(f: Filters) -> str:
    """The query's hard limits in words; the pack restriction is the set's business, not a limit."""
    parts = []
    for field in ("kinds", "categories", "materials", "settings", "placements", "functions"):
        if vals := getattr(f, field):
            parts.append(f"{field[:-1] if field != 'categories' else 'category'}: "
                         + " or ".join(vals))  # fmt: skip
    if f.size_classes:
        parts.append("size class: " + " or ".join(f.size_classes))
    for axis, name in (("width", "width (x)"), ("height", "height (y)"), ("depth", "depth (z)"),
                       ("longest", "longest side")):  # fmt: skip
        lo, hi = getattr(f, f"min_{axis}_m"), getattr(f, f"max_{axis}_m")
        if lo is not None:
            parts.append(f"{name} at least {lo} m")
        if hi is not None:
            parts.append(f"{name} at most {hi} m")
    return "\n".join(f"- {p}" for p in parts)
