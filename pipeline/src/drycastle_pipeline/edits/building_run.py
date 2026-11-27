"""Per-building run: the buildings of some parcels only, as a GLB and a JSON fragment (for the viewer's live loop).

``python -m drycastle_pipeline building-run --parcel P-0004 [--building w378920327] --glb b.glb --out b.json``
derives the buildings of the target parcels again (``derive.derive_parcel``, the rules stage's own loop body) from the
current ``overrides.yaml`` and ``config/buildings/<id>.yaml``, and builds their meshes with the export's own code
(``scene._add_parcels``, ``gltf.build_chunk_scene``, ``gltf.export_glb``): the node names, colours and axes are those of a full
export (``make rules export``), and the GLB is in the chunk files' frame (add it at the map root). Nothing under ``data/`` is written,
``work/buildings.json`` is not touched.

The GLB holds, for each target parcel, ``parcel_<ID>_visual`` / ``parcel_<ID>_collision`` (the buildings that are not hollow),
``house_<bid>_visual`` / ``house_<bid>_collision`` (a hollow house, docs/house-destruction.md) and the ``window_<bid>_<opening>``
panes. The fragment says what the viewer must hide: ``replaces`` (node names, baked or new) and, per parcel, ``buildings`` (every
building id of it, for the window panes, which are matched by their ``window_<bid>_`` prefix).

What a run does not redo: footprints and the terrain carved under them stay as baked (the footprint is the OSM or cadastre outline
of the full run), and so do the documents the game reads (interiors.json, windows.json, house_voxels.bin, landmark rooms).

``building-serve`` keeps one :class:`BuildingWorld` and answers JSON lines on stdin with JSON lines on stdout (see :func:`serve`):
the parcels, buildings, terrain heights, rules, overrides and detail files are read once and read again only when their stat changes.
"""

import json
import sys
import time
from pathlib import Path

import yaml

from drycastle_pipeline.core.config import map_paths
from drycastle_pipeline.core.rules import DEFAULT_RULES, YAML_LOADER

FRAGMENT_FORMAT_VERSION = 1


class TargetError(ValueError):
    """A requested parcel or building does not exist."""


def _stat(path: Path) -> tuple[int, int]:
    try:
        st = path.stat()
        return (st.st_mtime_ns, st.st_size)
    except FileNotFoundError:
        return (-1, -1)


class BuildingWorld:
    """What a building run needs, loaded once; ``refresh`` reads again what changed on disk (stat)."""

    def __init__(
        self,
        work_dir: Path | None = None,
        rules_path: Path = DEFAULT_RULES,
        overrides_path: Path | None = None,
        details_dir: Path | None = None,
        photos_dir: Path | None = None,
    ) -> None:
        from drycastle_pipeline.buildings.derive import DEFAULT_OVERRIDES
        from drycastle_pipeline.buildings.openings import DETAILS_DIR

        self.work_dir = work_dir or map_paths().work
        self.rules_path = rules_path
        self.overrides_path = overrides_path or DEFAULT_OVERRIDES
        self.details_dir = details_dir or DETAILS_DIR
        self.photos_dir = photos_dir  # <bid>/measured.json (building_edit); None: the repo's
        doc = json.loads((self.work_dir / "parcels.json").read_text())
        self.chunk_m = float(doc["chunk_size_m"])
        self.parcels = {p["id"]: p for p in doc["parcels"]}
        self.parcel_of_building = {bid: p["id"] for p in doc["parcels"] for bid in p["buildings"]}
        feats = json.loads((self.work_dir / "normalised" / "buildings.geojson").read_text())["features"]
        self.features = {f["properties"]["osm_id"]: f for f in feats}
        terrain = json.loads((self.work_dir / "terrain.json").read_text()) if (self.work_dir / "terrain.json").is_file() else {}
        self.elevation: dict[str, float] = terrain.get("building_elevation", {})  # a missing terrain: buildings stand at z = 0
        self._plots = None
        self._rules = None
        self._rules_stat = None
        self._parsed: dict[tuple, tuple[dict, set]] = {}  # (path, stat) -> (overrides, kept footprints)
        self._details: dict[str, tuple[tuple[int, int], object]] = {}  # building id -> (stat of its file, Detail or None)
        self._derived: dict[str, tuple[tuple, dict[str, dict]]] = {}  # parcel id -> (stat of overrides and rules, its derived buildings)

    # ---- inputs ----

    @property
    def rules(self):
        from drycastle_pipeline.buildings.derive import load_building_rules

        st = _stat(self.rules_path)
        if self._rules is None or st != self._rules_stat:
            self._rules, self._rules_stat = load_building_rules(self.rules_path), st
        return self._rules

    @property
    def plots(self):
        if self._plots is None:
            from drycastle_pipeline.buildings.cadastre_check import Plots

            self._plots = Plots(list(self.parcels.values()))
        return self._plots

    def overrides(self, path: Path | None = None) -> tuple[dict, set]:
        """(the building overrides, the footprints kept as traced) of an overrides file, read again only when its stat changed."""
        from drycastle_pipeline.buildings.derive import load_overrides, load_traced

        path = path or self.overrides_path
        key = (str(path), _stat(path))
        if key not in self._parsed:
            data = (yaml.load(path.read_text(), Loader=YAML_LOADER) or {}) if path.is_file() else {}
            if len(self._parsed) > 8:
                self._parsed.clear()
            self._parsed[key] = (load_overrides(path, data), load_traced(path, data))
        return self._parsed[key]

    def detail(self, bid: str):
        """The building detail of ``bid`` (config/buildings/<bid>.yaml), or None when it has none; read again when the file changed."""
        from drycastle_pipeline.buildings.openings import load_detail

        path = self.details_dir / f"{bid}.yaml"
        st = _stat(path)
        got = self._details.get(bid)
        if got is None or got[0] != st:
            got = self._details[bid] = (st, load_detail(path) if st != (-1, -1) else None)
        return got[1]

    def derived(self, bid: str) -> dict:
        """The building as the rules and the overrides make it (the rules stage's own derive_parcel: footprint ring, eave and top heights, roof,
        colours), cached until the overrides or the rules change on disk."""
        from drycastle_pipeline.buildings.derive import derive_parcel

        pid = self.parcel_for(bid)
        rules = self.rules
        stamp = (_stat(self.overrides_path), self._rules_stat)
        got = self._derived.get(pid)
        if got is None or got[0] != stamp:
            overrides, kept = self.overrides()
            built = {b["id"]: b for b in derive_parcel(self.parcels[pid], self.features, overrides, rules, self.plots, kept)}
            got = self._derived[pid] = (stamp, built)
        if bid not in got[1]:
            raise TargetError(f"building {bid} is left out of the map by its override (remove)")
        return got[1][bid]

    # ---- targets ----

    def parcel_for(self, building: str) -> str:
        if building not in self.parcel_of_building:
            raise TargetError(f"building {building} not found on any parcel")
        return self.parcel_of_building[building]

    def changed_parcels(self, base: Path, now: Path | None = None) -> tuple[list[str], list[str]]:
        """The parcels whose buildings differ between two overrides files (an entry by parcel ID or by building id, or a footprint
        kept as traced): (parcel ids, warnings for keys that match nothing)."""
        (old, old_kept), (new, new_kept) = self.overrides(base), self.overrides(now)
        keys = {k for k in old.keys() | new.keys() if old.get(k) != new.get(k)} | (old_kept ^ new_kept)
        out, warnings = set(), []
        for key in sorted(keys):
            if key in self.parcels:
                out.add(key)
            elif key in self.parcel_of_building:
                out.add(self.parcel_of_building[key])
            else:
                warnings.append(f"override {key} matches no parcel or building")
        return sorted(out), warnings

    # ---- the run ----

    def build(self, parcels: list[str], glb_path: Path | None = None, overrides_path: Path | None = None) -> dict:
        from drycastle_pipeline.buildings.derive import derive_parcel
        from drycastle_pipeline.buildings.openings import DetailError  # noqa: F401  (raised by detail())
        from drycastle_pipeline.export import scene as sc

        t0 = time.perf_counter()
        for pid in parcels:
            if pid not in self.parcels:
                raise TargetError(f"parcel {pid} not found")
        ids = sorted(set(parcels))
        rows = [self.parcels[pid] for pid in ids]
        overrides, kept = self.overrides(overrides_path)
        rules = self.rules
        t1 = time.perf_counter()
        buildings = {b["id"]: b for p in rows for b in derive_parcel(p, self.features, overrides, rules, self.plots, kept)}
        details = {bid: d for bid in buildings if (d := self.detail(bid)) is not None}
        t2 = time.perf_counter()
        scene = sc.MapScene(building_elevation={bid: self.elevation.get(bid, 0.0) for bid in buildings})
        sc._add_parcels(scene, {"chunk_size_m": self.chunk_m, "parcels": rows}, buildings, details, documents=False)
        t3 = time.perf_counter()
        nodes = {name: key for key, named in scene.nodes.items() for name in named}
        triangles = {name: mesh.face_count for named in scene.nodes.values() for name, mesh in named.items()}
        for key, items in scene.parcels.items():
            nodes.update({f"parcel_{pid}_{sfx}": key for pid, _, _ in items for sfx in ("visual", "collision")})
            triangles.update({f"parcel_{pid}_{sfx}": m.face_count for pid, v, c in items for sfx, m in (("visual", v), ("collision", c))})
        names = {f"parcel_{pid}_{sfx}" for pid in ids for sfx in ("visual", "collision")}
        names |= {f"house_{bid}_{sfx}" for p in rows for bid in p["buildings"] for sfx in ("visual", "collision")}
        glb = {}
        if glb_path is not None:
            glb_path.parent.mkdir(parents=True, exist_ok=True)
            glb_path.write_bytes(self._glb(scene))
            glb = {"glb": str(glb_path.resolve())}
        t4 = time.perf_counter()
        return {
            "format_version": FRAGMENT_FORMAT_VERSION,
            "parcels": ids,
            "buildings": {p["id"]: sorted(p["buildings"]) for p in rows},
            "replaces": sorted(names | set(nodes)),
            "nodes": dict(sorted(nodes.items())),
            "triangles": dict(sorted(triangles.items())),
            "chunks": sorted({tuple(k) for k in nodes.values()}),
            **glb,
            "warnings": list(scene.warnings),
            "timing_s": {
                "load": round(t1 - t0, 3),
                "derive": round(t2 - t1, 3),
                "mesh": round(t3 - t2, 3),
                "glb": round(t4 - t3, 3),
                "total": round(t4 - t0, 3),
            },
        }

    @staticmethod
    def _glb(scene) -> bytes:
        """One GLB with every node of the scene (all chunks): the chunk files' own writer, so names, colours and axes are theirs."""
        from drycastle_pipeline.edits.parcel_run import _EMPTY_GLB
        from drycastle_pipeline.export.gltf import build_chunk_scene, export_glb

        items = [it for chunk_items in scene.parcels.values() for it in chunk_items]
        extra = {name: mesh for named in scene.nodes.values() for name, mesh in named.items()}
        return export_glb(build_chunk_scene(items, extra)) if items or extra else _EMPTY_GLB


def run_building_run(
    parcels: list[str],
    buildings: list[str] | None = None,
    glb_path: Path | None = None,
    work_dir: Path | None = None,
    rules_path: Path = DEFAULT_RULES,
    overrides_path: Path | None = None,
    details_dir: Path | None = None,
) -> dict:
    t0 = time.perf_counter()
    world = BuildingWorld(work_dir, rules_path, overrides_path, details_dir)
    targets = set(parcels) | {world.parcel_for(b) for b in buildings or []}
    frag = world.build(sorted(targets), glb_path)
    spent = time.perf_counter() - t0 - frag["timing_s"]["total"]  # reading the work files
    frag["timing_s"] = {**frag["timing_s"], "load": round(frag["timing_s"]["load"] + spent, 3), "total": round(frag["timing_s"]["total"] + spent, 3)}
    return frag


def run(
    parcels: list[str],
    buildings: list[str] | None = None,
    out: str | None = None,
    glb: str | None = None,
    work: str | None = None,
) -> int:
    if not parcels and not buildings:
        print("building-run: error: give at least one --parcel or --building", file=sys.stderr)
        return 2
    try:
        fragment = run_building_run(parcels, buildings, Path(glb) if glb else None, Path(work) if work else None)
    except FileNotFoundError as exc:
        print(f"building-run: error: missing input {Path(str(exc.filename)).name}; run earlier stages", file=sys.stderr)
        return 1
    except Exception as exc:  # noqa: BLE001  (RulesError, DetailError, TargetError: the message is what the editor shows)
        print(f"building-run: error: {exc}", file=sys.stderr)
        return 1
    for warning in fragment["warnings"]:
        print(f"building-run: warning: {warning}", file=sys.stderr)
    text = json.dumps(fragment, sort_keys=True, separators=(",", ":")) + "\n"
    if out:
        Path(out).write_text(text)
    else:
        sys.stdout.write(text)
    return 0


def handle(world: BuildingWorld, req: dict) -> dict:
    """One request of the worker. Keys: ``glb`` (where to write the GLB), ``parcels``, ``buildings``, ``files`` (changed
    detail files, ``<bid>.yaml``), ``overrides`` (the overrides file to build from, default the live one) with ``overrides_base``
    (the file the viewer last applied: the parcels of the entries that differ are rebuilt too). Returns the fragment."""
    targets = set(req.get("parcels") or [])
    targets |= {world.parcel_for(b) for b in req.get("buildings") or []}
    warnings = []
    for name in req.get("files") or []:
        bid = Path(name).stem
        if bid in world.parcel_of_building:
            targets.add(world.parcel_of_building[bid])
        else:
            warnings.append(f"detail {name}: building {bid} is on no parcel")
    now = Path(req["overrides"]) if req.get("overrides") else None
    if req.get("overrides_base"):
        changed, more = world.changed_parcels(Path(req["overrides_base"]), now)
        targets |= set(changed)
        warnings += more
    glb = Path(req["glb"]) if req.get("glb") and targets else None
    frag = world.build(sorted(targets), glb, now)
    frag["warnings"] = warnings + frag["warnings"]
    return frag


def warm_up(world: BuildingWorld) -> None:
    """Builds one parcel that has a building detail and drops it: the lazy imports and caches are warm for the first real request."""
    try:
        ids = [p.stem for p in sorted(world.details_dir.glob("*.yaml"))]
        parcel = next((world.parcel_of_building[b] for b in ids if b in world.parcel_of_building), None)
        if parcel:
            world.build([parcel])
    except Exception as exc:  # noqa: BLE001  (no detail files, or one is broken: the first request says so)
        print(f"building-serve: warm-up skipped: {exc}", file=sys.stderr)


def serve(inp=None, out=None, work: str | None = None, world: BuildingWorld | None = None) -> int:
    """``building-serve``: JSON lines in, JSON lines out, until stdin closes or a ``{"op": "quit"}`` line.

    Request: ``{"id": 1, "glb": "/tmp/b1.glb", "parcels": ["P-0004"], "buildings": [], "files": ["w378920327.yaml"],
    "overrides": "/tmp/o1.yaml", "overrides_base": "/tmp/o0.yaml"}`` (all but ``id`` optional, see :func:`handle`).
    Answer: ``{"id": 1, "ok": true, "fragment": {...}}`` or ``{"id": 1, "ok": false, "error": "..."}``. The first line out is
    ``{"ready": true, "load_s": ...}``. Everything else printed goes to stderr.

    A request with ``"op"`` (``state``, ``edit``, ``restore``, ``snapshot``, ``reset``, each with a ``"building"``) is the building edit API
    (``building_edit``): ``{"id": 2, "op": "edit", "building": "w1", "change": {...}}`` is answered ``{"id": 2, "ok": true, "result": {...}}``
    once the files are written (the viewer's rebuild of the parcel follows by itself, the files being watched).
    """
    inp = inp or sys.stdin
    out = out or sys.stdout
    sys.stdout = sys.stderr  # stage code that prints must not break the protocol

    def say(doc: dict) -> None:
        out.write(json.dumps(doc, separators=(",", ":")) + "\n")
        out.flush()

    t0 = time.perf_counter()
    try:
        world = world or BuildingWorld(Path(work) if work else None)
        _ = (world.plots, world.rules)  # read now: the first request is not slower for it
        import drycastle_pipeline.export.gltf
        import drycastle_pipeline.export.scene  # noqa: F401
    except Exception as exc:  # noqa: BLE001
        say({"ready": False, "error": str(exc)})
        return 1
    warm_up(world)
    say({"ready": True, "load_s": round(time.perf_counter() - t0, 3)})
    for line in inp:
        if not line.strip():
            continue
        rid = None
        try:
            req = json.loads(line)
            rid = req.get("id")
            op = req.get("op")
            if op == "quit":
                break
            if op is None:
                say({"id": rid, "ok": True, "fragment": handle(world, req)})
            else:  # state | edit | restore | snapshot | reset: the building edit API, answered in order
                from drycastle_pipeline.edits import building_edit

                say({"id": rid, "ok": True, "result": building_edit.handle_op(world, req)})
        except Exception as exc:  # noqa: BLE001  (a bad file or request: this answer fails, the worker stays)
            say({"id": rid, "ok": False, "error": f"{type(exc).__name__}: {exc}"})
    return 0
