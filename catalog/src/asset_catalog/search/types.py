"""What every part of search agrees on: filters, hits, and the retriever interface."""

from __future__ import annotations

from typing import Protocol

from pydantic import BaseModel


class Filters(BaseModel):
    """Hard constraints. Values within one field are alternatives (OR); fields combine (AND).

    An empty field does not constrain. Only put here what the searcher stated outright:
    a filter on a guessed facet silently removes right answers.
    """

    kinds: list[str] = []
    categories: list[str] = []  # a leaf or a prefix: "furniture" matches "furniture/seating/chair"
    materials: list[str] = []
    settings: list[str] = []
    placements: list[str] = []
    functions: list[str] = []
    styles: list[str] = []
    condition: list[str] = []
    colours: list[str] = []
    packs: list[str] = []  # "<source>/<pack>" or just "<pack>"
    size_classes: list[str] = []
    min_width_m: float | None = None  # x
    max_width_m: float | None = None
    min_height_m: float | None = None  # y
    max_height_m: float | None = None
    min_depth_m: float | None = None  # z
    max_depth_m: float | None = None
    min_longest_m: float | None = None
    max_longest_m: float | None = None
    rigged: bool | None = None
    exclude_ids: list[str] = []

    def is_empty(self) -> bool:
        return self == Filters()


class Hit(BaseModel):
    id: str
    score: float  # higher is better; comparable only within one retriever's result list
    why: list[str] = []  # short notes on what matched, for the result display


class Retriever(Protocol):
    """One way of finding candidates. Each returns its own best-first list; fusion merges them."""

    name: str

    def retrieve(self, query: str, filters: Filters, k: int) -> list[Hit]: ...
