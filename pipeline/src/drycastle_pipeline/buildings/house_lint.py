"""Anomalies of photo-built houses (docs/house-anomalies.md): what the eye catches walking past, found by rule.

    uv run python -m drycastle_pipeline.buildings.house_lint [ids...]        (no ids: every config/buildings/<id>.yaml)

One line per anomaly: building, check, what. The opening checks are the changes house_rules.regularise would make to
the detail file as written (``--fix`` writes them); the geometry checks (buildings/lint_geometry.py) build the house as the
export does. facade_pipeline apply runs the same rules, so a re-applied house comes out right; this finds what is left.
"""

from __future__ import annotations

import math
import re
import sys
from pathlib import Path

import yaml

def wall_top(path: Path, fallback: float) -> float:
    """The wall top the detail's openings are measured against: its header's "Wall top X m" (facade_pipeline writes it),
    else the derived building's eave."""
    m = re.search(r"Wall top ([0-9.]+) m", path.read_text()[:600])
    return float(m.group(1)) if m else fallback


def gable_walls(b: dict) -> set[int] | None:
    """The walls of a gabled house that run across its ridge (a gable over them); None when not known."""
    ring = b["footprint"]["exterior"]
    if b.get("roof") == "hipped" or b.get("roof") == "flat":
        return set()
    if b.get("roof") != "gabled" or b.get("ridge_dir") is None:
        return None
    a = float(b["ridge_dir"])
    out = set()
    for i in range(len(ring)):
        (px, py), (qx, qy) = ring[i], ring[(i + 1) % len(ring)]
        n = math.hypot(qx - px, qy - py) or 1.0
        if abs((qx - px) / n * math.cos(a) + (qy - py) / n * math.sin(a)) <= 0.7:
            out.add(i)
    return out


def opening_checks(openings: list[dict], lengths: dict[int, float], eave: float, gables: set[int] | None = None) -> tuple[list[dict], list[tuple[str, str]]]:
    """(the openings as house_rules.regularise sets them, (check, what) for each change it makes): every change is an
    anomaly of the file as written."""
    from drycastle_pipeline.buildings import house_rules as hr

    measured = {o["edge"] for o in openings if o.get("confidence") != "low"}  # the walls a capture saw, as apply mirrors only those
    fixed, said = hr.regularise(openings, lengths, eave, measured, gables)
    return fixed, [("openings", s) for s in said]


def write_openings(path: Path, openings: list[dict]) -> None:
    """The detail file with its openings (its last block, as facade_pipeline writes it) replaced; new ones get ids."""
    text = path.read_text()
    at = text.rfind("\nopenings:")
    if at < 0 or re.search(r"^\S", text[at + 1 :].split("\n", 1)[1] if "\n" in text[at + 1 :] else "", re.M):
        raise SystemExit(f"{path}: openings is not the file's last block, not rewritten")
    from drycastle_pipeline.buildings.house_rules import give_ids

    give_ids(openings)
    ordered = sorted(openings, key=lambda o: (o["edge"], o["at_m"]))

    def flow(v) -> str:
        if isinstance(v, bool):
            return "true" if v else "false"
        return f'"{v}"' if isinstance(v, str) and v.startswith("#") else str(v)

    rows = "".join("  - {" + ", ".join(f"{k}: {flow(v)}" for k, v in {"id": o["id"], **{k: v for k, v in o.items() if k != "id" and not k.startswith("_")}}.items()) + "}\n" for o in ordered)
    from drycastle_pipeline.edits.building_edit import atomic_write

    atomic_write(path, text[: at + 1] + ("openings:\n" + rows if ordered else "openings: []\n"))


def lint(bid: str, world, geometry: bool = True, fix: bool = False) -> list[tuple[str, str]]:
    b = world.derived(bid)
    ring = b["footprint"]["exterior"]
    lengths = {i: math.dist(ring[i], ring[(i + 1) % len(ring)]) for i in range(len(ring))}
    path = world.details_dir / f"{bid}.yaml"
    raw = yaml.safe_load(path.read_text()) or {}
    if "facade_pipeline" not in str(raw.get("source", "")):  # modelled by hand: its openings are as meant
        fixed, out = raw.get("openings") or [], []
    else:
        fixed, out = opening_checks(raw.get("openings") or [], lengths, wall_top(path, float(b["eave_height_m"])))  # (gables: apply knows them from the fitted roof; one ridge here would be wrong on a roof of parts)
    if fix and out:
        write_openings(path, fixed)
    if geometry:
        try:
            from drycastle_pipeline.buildings import lint_geometry
        except ImportError:
            return out
        from drycastle_pipeline.buildings.openings import build_detail
        from drycastle_pipeline.core.mesh import Mesh

        d = world.detail(bid)
        mesh, tags = build_detail(b, d)
        out += [("geometry", line) for line in lint_geometry.check(b, Mesh.concat([mesh, *(t.mesh for t in tags)]), tags)]
    return out


def main(argv: list[str] | None = None) -> int:
    import argparse

    from drycastle_pipeline.buildings.openings import DetailError
    from drycastle_pipeline.edits.building_run import BuildingWorld, TargetError

    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("ids", nargs="*")
    ap.add_argument("--no-geometry", action="store_true", help="the opening checks only (fast)")
    ap.add_argument("--fix", action="store_true", help="write the openings as the checks would set them (for houses with no run folder to apply again)")
    args = ap.parse_args(argv)
    world = BuildingWorld()
    ids = args.ids or sorted(p.stem for p in Path(world.details_dir).glob("w*.yaml"))
    counts: dict[str, int] = {}
    for bid in ids:
        try:
            found = lint(bid, world, not args.no_geometry, args.fix)
        except (TargetError, KeyError, DetailError) as e:
            print(f"{bid}  skipped: {e}")
            continue
        for check, what in found:
            counts[check] = counts.get(check, 0) + 1
            print(f"{bid}  {check:13} {what}")
    print(f"{len(ids)} house(s): " + ", ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
