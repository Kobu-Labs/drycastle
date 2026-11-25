"""Spelling correction against the words the catalogue actually contains."""

from __future__ import annotations

from collections import defaultdict


def distance(a: str, b: str, limit: int) -> int:
    """Optimal string alignment distance (insert, delete, substitute, swap neighbours).

    Returns limit + 1 as soon as the distance is certainly above limit.
    """
    if abs(len(a) - len(b)) > limit:
        return limit + 1
    prev2: list[int] = []
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i] + [0] * len(b)
        for j, cb in enumerate(b, 1):
            cost = 0 if ca == cb else 1
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
            if i > 1 and j > 1 and ca == b[j - 2] and a[i - 2] == cb:
                cur[j] = min(cur[j], prev2[j - 2] + 1)
        if min(cur) > limit:
            return limit + 1
        prev2, prev = prev, cur
    return prev[-1]


def allowed_distance(word: str) -> int:
    """Short words are too ambiguous to guess at: 4-7 letters one edit, longer two."""
    if len(word) < 4:
        return 0
    return 1 if len(word) <= 7 else 2


class Speller:
    """Suggests the catalogue's own words for a word it has never seen."""

    def __init__(self, vocabulary: dict[str, int]):
        self.freq = vocabulary
        self._by_first: dict[str, list[str]] = defaultdict(list)
        for w in vocabulary:
            self._by_first[w[0]].append(w)

    def suggest(self, word: str, limit: int = 3) -> list[str]:
        """Closest known words, nearest first then most frequent. Typos rarely change the
        first letter, so only words starting with the first or second letter are compared."""
        max_d = allowed_distance(word)
        if max_d == 0 or word in self.freq:
            return []
        found: list[tuple[int, int, str]] = []
        for first in {word[0], word[1]}:
            for cand in self._by_first.get(first, ()):
                d = distance(word, cand, max_d)
                if 0 < d <= max_d and (cand[0] == word[0] or d == 1 and cand[:2] == word[1::-1]):
                    found.append((d, -self.freq[cand], cand))
        found.sort()
        return [c for _, _, c in found[:limit]]
