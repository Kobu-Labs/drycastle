"""Patterns of the ids the tools take, and the next free id of a kind."""

from __future__ import annotations

import re

PARCEL_ID = re.compile(r"P-\d{4,}")
GROUP_ID = re.compile(r"G-\d{4,}")
PATCH_ID = re.compile(r"GP-\d{4,}")
ZONE_ID = re.compile(r"Z-\d{4,}")
RULE_ZONE_ID = re.compile(r"A-\d{4,}(?:\.\d+)?")  # a zone that comes from a rule: a ground area (or one outline of it)
GENERATED_ID = re.compile(r"(?:[oGCZ]|GP|ZC|PA)-(\d+)")


def next_id(prefix: str, ids: list[str]) -> str:
    top = 0
    for i in ids:
        m = GENERATED_ID.fullmatch(i)
        if m and i.startswith(prefix + "-"):
            top = max(top, int(m.group(1)))
    return f"{prefix}-{top + 1:04d}"
