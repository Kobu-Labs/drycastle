"""Parcel registry: stable own IDs (``P-0001``) for cadastral parcels.

The registry (``registry/parcels.registry.json``, committed) keeps, per parcel, its ID, current
cadastral reference, status, earlier references and a geometry signature in EPSG:5514 metres.
On every regeneration the current cadastral parcels are matched to it:

1. exact cadastral reference keeps the ID (a retired entry with that reference is revived);
2. else an unmatched active entry whose outline overlaps with IoU >= ``min_iou`` keeps its ID
   (the parcel was renumbered; the old reference moves to ``previous_refs``);
3. else the parcel is new and gets the next ID after the highest ever used, in the order of
   cadastral reference;
4. active entries nobody claimed become ``retired`` only when their outline lies inside the
   fetched area (``bounds``): a run knows nothing about parcels outside what it fetched, so those
   entries are left untouched. Without ``bounds`` every unclaimed entry is retired. Entries are
   never deleted and IDs never reused.
"""

import json
import re
from dataclasses import dataclass, field
from pathlib import Path

from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry

from suchohrad_pipeline.config import REPO_ROOT

DEFAULT_REGISTRY = REPO_ROOT / "registry" / "parcels.registry.json"
FORMAT_VERSION = 1
ID_PREFIX = "P-"
SIG_DECIMALS = 2  # centimetres


def format_id(n: int) -> str:
    return f"{ID_PREFIX}{n:04d}"


def parse_id(parcel_id: str) -> int:
    return int(parcel_id.removeprefix(ID_PREFIX))


def signature(geom: BaseGeometry) -> dict:
    """Area, centroid and outline (exterior rings, no holes) of a geometry in EPSG:5514 metres."""
    polys = list(geom.geoms) if isinstance(geom, MultiPolygon) else [geom]
    c = geom.centroid
    return {
        "area": round(geom.area, SIG_DECIMALS),
        "centroid": [round(c.x, SIG_DECIMALS), round(c.y, SIG_DECIMALS)],
        "outline": [
            [[round(x, SIG_DECIMALS), round(y, SIG_DECIMALS)] for x, y in p.exterior.coords]
            for p in sorted(polys, key=lambda p: -p.area)
        ],
    }


def outline_polygon(sig: dict) -> BaseGeometry:
    polys = [Polygon(ring).buffer(0) for ring in sig["outline"]]
    return polys[0] if len(polys) == 1 else MultiPolygon([p for p in polys if not p.is_empty])


def iou(a: BaseGeometry, b: BaseGeometry) -> float:
    union = a.union(b).area
    return a.intersection(b).area / union if union > 0 else 0.0


@dataclass
class MatchResult:
    registry: dict
    ids: dict[str, str]  # cadastral reference -> parcel ID
    new: list[str] = field(default_factory=list)
    retired: list[str] = field(default_factory=list)  # newly retired in this run
    renumbered: list[str] = field(default_factory=list)
    revived: list[str] = field(default_factory=list)


def empty_registry() -> dict:
    return {"version": FORMAT_VERSION, "parcels": []}


def load_registry(path: Path = DEFAULT_REGISTRY) -> dict:
    if not path.is_file():
        return empty_registry()
    return json.loads(path.read_text())


def save_registry(registry: dict, path: Path = DEFAULT_REGISTRY) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(registry, indent=1, sort_keys=True)
    # keep each coordinate pair on one line so the committed file diffs readably
    text = re.sub(r"\[\s+(-?[\d.]+),\s+(-?[\d.]+)\s+\]", r"[\1, \2]", text)
    path.write_text(text + "\n")


def match_parcels(
    registry: dict,
    current: dict[str, BaseGeometry],
    min_iou: float = 0.5,
    bounds: BaseGeometry | None = None,
) -> MatchResult:
    """Match current parcels (cadastral reference -> geometry in EPSG:5514) to the registry.

    ``bounds`` is the fetched area in EPSG:5514; only entries inside it can be retired.
    """
    entries = {e["id"]: dict(e) for e in registry.get("parcels", [])}
    by_ref = {e["cadastral_ref"]: e for e in entries.values()}
    ids: dict[str, str] = {}
    res = MatchResult(registry=registry, ids=ids)
    claimed: set[str] = set()

    def claim(entry: dict, ref: str) -> None:
        entry["cadastral_ref"] = ref
        entry["geometry"] = signature(current[ref])
        entry["status"] = "active"
        ids[ref] = entry["id"]
        claimed.add(entry["id"])

    # 1. exact cadastral reference
    for ref in sorted(current):
        entry = by_ref.get(ref)
        if entry is None or entry["id"] in claimed:
            continue
        if entry["status"] == "retired":
            res.revived.append(entry["id"])
        claim(entry, ref)

    # 2. geometry overlap against unmatched active entries (greedy, best IoU first)
    open_refs = [r for r in sorted(current) if r not in ids]
    open_entries = [
        e for _, e in sorted(entries.items()) if e["id"] not in claimed and e["status"] == "active"
    ]
    pairs = []
    for entry in open_entries:
        old = outline_polygon(entry["geometry"])
        for ref in open_refs:
            score = iou(old, current[ref])
            if score >= min_iou:
                pairs.append((-score, entry["id"], ref))
    for _, entry_id, ref in sorted(pairs):
        if entry_id in claimed or ref in ids:
            continue
        entry = entries[entry_id]
        history = entry.setdefault("previous_refs", [])
        history.append(entry["cadastral_ref"])
        claim(entry, ref)
        res.renumbered.append(entry_id)

    # 3. new parcels, deterministic order, after the highest ID ever used
    next_n = max((parse_id(i) for i in entries), default=0) + 1
    for ref in sorted(r for r in current if r not in ids):
        entry = {
            "id": format_id(next_n),
            "cadastral_ref": ref,
            "previous_refs": [],
            "status": "active",
            "geometry": signature(current[ref]),
        }
        entries[entry["id"]] = entry
        ids[ref] = entry["id"]
        claimed.add(entry["id"])
        res.new.append(entry["id"])
        next_n += 1

    # 4. retire the rest (only inside the fetched area), keep them forever
    for entry_id, entry in sorted(entries.items()):
        if entry_id not in claimed and entry["status"] == "active":
            if bounds is not None and not bounds.covers(outline_polygon(entry["geometry"])):
                continue
            entry["status"] = "retired"
            res.retired.append(entry_id)

    for entry in entries.values():
        entry.setdefault("previous_refs", [])
    res.registry = {
        "version": FORMAT_VERSION,
        "parcels": [entries[i] for i in sorted(entries)],
    }
    return res
