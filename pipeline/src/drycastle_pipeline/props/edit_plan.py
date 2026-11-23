"""Hand edits resolved against this run's parcels (fence specs, groups, objects, cables, water patches), plus asset checks."""

from dataclasses import dataclass, field, replace

import shapely
from shapely.geometry import Point, Polygon, shape
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from drycastle_pipeline.core.assets import load_manifest, validate_asset_ref
from drycastle_pipeline.core.geometry import r3
from drycastle_pipeline.edits.schema import (
    WIRE_ASSET_PREFIXES,
    Edits,
    FenceSpec,
    polygon_ring,
    resolve_fence,
)
from drycastle_pipeline.props.helpers import chunk_of
from drycastle_pipeline.terrain.terrain import Terrain


@dataclass
class EditPlan:
    """Edits resolved against the parcels of this run (see :func:`plan_edits`)."""

    specs: dict[str, FenceSpec] = field(default_factory=dict)  # gaps in local metres
    groups: dict[str, list[str]] = field(default_factory=dict)  # valid member parcels only
    objects: list[dict] = field(default_factory=list)  # objects.json entries, sorted by id
    cables: list[dict] = field(default_factory=list)  # {"id", "from", "to"} between placed poles and connectors, by id
    warnings: list[str] = field(default_factory=list)
    water_patches: list[tuple[Polygon, float]] = field(default_factory=list)  # (outline in local metres, water_y)


def _local_xy(x: float, y: float, origin: list[float] | None) -> tuple[float, float]:
    if origin is None:
        raise ValueError("parcels.json has no origin_epsg5514; run the parcels stage again")
    return x - origin[0], y - origin[1]


def plan_edits(
    edits: Edits, parcels: list[dict], origin: list[float] | None, chunk_m: float
) -> EditPlan:
    """Resolve the edits against this run's parcels.

    Entries for parcels that no longer exist (parcel entries, group members) are skipped with a
    warning (not a parcel's ``fence: none`` or a group with no fence, which have nothing to skip). Every object is placed: an object belongs to no parcel and only needs its position (so a
    pole on a road, or on land a dropped parcel left as ground, is as normal as a car in a yard); its
    ``parcel`` in objects.json is the parcel containing it, or null. A cable is
    kept when both its ends are placed utility poles or house connectors, else skipped with a warning.
    """
    plan = EditPlan()
    polys = {p["id"]: shape(p["polygon"]) for p in parcels}
    for gid in sorted(edits.groups):
        members = []
        # a group with no fence of its own loses nothing with a member that is gone: no warning then
        fenced = (edits.groups[gid].get("fence") or {}).get("mode", "none") != "none"
        for pid in edits.groups[gid].get("parcels", []):
            if pid in polys:
                members.append(pid)
            elif fenced:
                plan.warnings.append(f"groups.{gid}: parcel {pid} not found; skipped")
        if members:
            plan.groups[gid] = sorted(members)
        elif fenced:
            plan.warnings.append(f"groups.{gid}: no member parcels left; group skipped")
    member_group = {pid: gid for gid, pids in plan.groups.items() for pid in pids}
    for pid in sorted(edits.parcels):
        if pid not in polys:
            # "no fence" on a parcel that is gone loses nothing: no warning (the entry applies again if the parcel comes back)
            kept = [k for k in ("fence",) if k in edits.parcels[pid] and edits.parcels[pid][k].get("mode") != "none"]
            if kept:
                plan.warnings.append(f"parcels.{pid}: parcel not found; {', '.join(kept)} skipped")
    for pid in sorted(polys):
        entry = edits.parcels.get(pid, {})
        group_fence = edits.groups[member_group[pid]].get("fence") if pid in member_group else None
        if "fence" in entry or group_fence is not None:
            spec = resolve_fence(entry.get("fence"), group_fence)
            gaps = tuple((*_local_xy(x, y, origin), w) for x, y, w in spec.gaps)
            plan.specs[pid] = replace(spec, gaps=gaps)
    ids = list(polys)
    index = shapely.STRtree([polys[q] for q in ids])
    for obj in edits.objects:
        lx, ly = _local_xy(*obj["position"], origin)
        hits = sorted(ids[i] for i in index.query(Point(lx, ly), predicate="covered_by"))  # the parcels that cover the point
        plan.objects.append(
            {
                "id": obj["id"],
                "asset": obj["asset"],
                "position": [r3(lx), r3(ly)],
                "z": obj.get("z"),
                "rotation_deg": float(obj.get("rotation_deg", 0)),
                "scale": float(obj.get("scale", 1)),
                **({"size_m": [float(c) for c in obj["size_m"]]} if "size_m" in obj else {}),  # key only when set: older maps unchanged
                "colour": obj.get("colour"),
                "parcel": hits[0] if hits else None,
                "chunk": chunk_of(lx, ly, chunk_m),
                **({"solid": False} if obj.get("solid") is False else {}),  # key only when false: older maps unchanged
            }
        )
    plan.objects.sort(key=lambda o: o["id"])
    assets = {o["id"]: o["asset"] for o in plan.objects}
    for cid in sorted(edits.cables):
        cable = edits.cables[cid]
        bad = [
            f"{end} is not placed" if end not in assets else f"{end} is not a utility pole or a house connector"
            for end in (cable["from"], cable["to"])
            if not assets.get(end, "").startswith(WIRE_ASSET_PREFIXES)
        ]
        if bad:
            plan.warnings.append(f"cables.{cid}: {'; '.join(bad)}; cable skipped")
        else:
            plan.cables.append({"id": cid, "from": cable["from"], "to": cable["to"]})
    plan.water_patches = [
        (Polygon([_local_xy(x, y, origin) for x, y in polygon_ring(r["polygon"])]), float(r["water_y"]))
        for _, r in sorted(edits.ground_patches.items())
        if "water_y" in r
    ]
    return plan


def water_zone(plan: EditPlan, terrain: Terrain) -> BaseGeometry | None:
    """Where the water patches of the plan really lie (see :func:`water.wet_area`), or None without any."""
    from drycastle_pipeline.terrain.water import wet_area

    zones = [wet_area(outline, terrain, level) for outline, level in plan.water_patches]
    zones = [z for z in zones if not z.is_empty]
    return unary_union(zones) if zones else None


def unknown_asset_warnings(objects: list[dict]) -> list[str]:
    """Warnings for placed objects whose asset id is not in ``assets/manifest.json``.

    Output is unaffected: the objects are written anyway. Nothing is reported when there is no
    manifest to check against.
    """
    try:
        manifest = load_manifest()
    except (OSError, ValueError):
        return []
    out = []
    for obj in objects:
        problem = validate_asset_ref(obj["asset"], manifest)
        if problem:
            out.append(f"objects {obj['id']}: {problem}")
    return out
