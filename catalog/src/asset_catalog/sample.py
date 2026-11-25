"""Pilot sample: a small, deterministic, spread-out subset of the model assets.

Strata are (pack, name family), where the family is the pack's own prefix ("SM_Prop",
"SM_Bld", ...) where it has one. Each stratum gets a share proportional to the square root
of its size, so small packs and rare families are still seen. At most one asset per variant
group: siblings teach the vocabulary nothing new.
"""

from __future__ import annotations

import hashlib
import math
import re
from collections import defaultdict

from .model import Asset

_FAMILY = re.compile(r"^((?:SM|SK|FX|PF)_[A-Za-z]+)")


def family(name: str) -> str:
    m = _FAMILY.match(name)
    return m.group(1).lower() if m else "-"


def _rank(asset_id: str, seed: int) -> str:
    return hashlib.sha256(f"{seed}:{asset_id}".encode()).hexdigest()


def pilot_sample(assets: list[Asset], n: int = 300, seed: int = 1) -> list[Asset]:
    models = sorted(
        (a for a in assets if a.media == "model" and a.kind_hint is None and a.role == "primary"),
        key=lambda a: _rank(a.id, seed),
    )
    seen_groups: set[str] = set()
    strata: dict[tuple[str, str], list[Asset]] = defaultdict(list)
    for a in models:
        if a.variant_group:
            if a.variant_group in seen_groups:
                continue
            seen_groups.add(a.variant_group)
        strata[(f"{a.source}/{a.pack}", family(a.name))].append(a)
    weights = {k: math.sqrt(len(v)) for k, v in strata.items()}
    total = sum(weights.values())
    picked: list[Asset] = []
    for k in sorted(strata):
        quota = max(1, round(n * weights[k] / total))
        picked.extend(strata[k][:quota])
    return sorted(picked, key=lambda a: a.id)
