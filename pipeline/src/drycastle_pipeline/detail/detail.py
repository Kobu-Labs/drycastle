"""Ground detail of the village: draped patches (lawn tones, bare soil, gravel, leaf litter, moss, mud, puddles, swamp floor, beds, furrows,
road verges) and clutter instances (tufts, flowers, pebbles, rocks, leaves, sticks, ferns).

This is the ``detail`` stage (``make detail``, between ``props`` and ``export``). It reads the work files
(parcels, buildings, roads, terrain, props, objects) and ``edits.json`` and writes

* ``work/detail.json``: the clutter instances, per chunk and kind (the export copies it as ``clutter.json``),
* ``work/detail_patches.npy`` and ``detail_patches.json``: the patch triangles, draped on the terrain (the
  export puts them into the chunk files as ``detail_patches`` nodes).

* **Patches** are polygons on the ground, flat-coloured or textured from the texture library, draped on
  the terrain by :func:`ground.patches.drape` (so they follow the ground triangle by triangle) at ``detail.lift_m``.
  They never overlap: the layers are clipped in the order of ``model.PATCH_PRIORITY`` (first wins), parcels
  are disjoint, so nothing z-fights. Water patches (3 cm) are cut out of every patch, so they never
  fight with a patch either.
* **Clutter** is data only: ``{"kind", "x", "y", "z", "rot", "scale"}`` per instance. The game draws them as
  instanced meshes near the player; nothing in the map holds them as triangles.
* **Area types.** ``recipes.area_type`` is the one place that says what kind of ground a parcel is (garden, field,
  forest, road, or nothing); each type has a recipe (``recipes.RECIPES``). Today it reads the parcel kind; a real
  area-type layer replaces that one function. Road verges are drawn along every carriageway, whatever the
  parcel.
* **Everything is seeded** with ``sha256("<parcel id>:detail:<purpose>")`` (``props.seeded``), one
  generator per purpose, so identical input gives identical output and a change to one recipe step does
  not move the others.
* **Modules**: ``model`` (types, constants), ``tools`` and ``parts`` (shared helpers), ``recipes*`` (one per ground type, ``recipes``
  is the registry), ``swamp``, ``village`` (the indexed work files), ``build`` (the whole map, one parcel), ``patches`` and ``documents``
  (the work files), ``rust`` (the engine the stage runs on); this module is the stage entry and the Python reference build.
* **Exclusion**: nothing lands on a building (plus a wall margin), on a fence, a trunk, a placed object, a
  water patch or the road. Crowns do not exclude: leaf litter belongs under them.
"""

import sys
from pathlib import Path

from drycastle_pipeline.core.config import map_paths
from drycastle_pipeline.detail.build import build_village_detail
from drycastle_pipeline.detail.documents import write_detail
from drycastle_pipeline.edits.schema import DEFAULT_EDITS, EditsError


def run(force: bool = False, edits: str | None = None, work: str | None = None, **_options: object) -> int:
    """The stage runs on the Rust engine (:mod:`detail_rust`) only. :func:`run_reference` is this module's own, slow build,
    kept as the reference the tests compare the engine with."""
    from drycastle_pipeline.detail import rust as detail_rust

    return detail_rust.run(force=force, edits=edits, work=work, **_options)


def run_reference(edits: str | None = None, work: str | None = None) -> int:
    """The Python reference build (minutes on the village map). Not a stage: nothing in the pipeline or the Makefile calls it."""
    edits_path = Path(edits) if edits else DEFAULT_EDITS
    work_dir = Path(work) if work else map_paths().work
    try:
        detail = build_village_detail(work_dir, edits_path=edits_path, log=lambda m: print(f"detail: {m}", file=sys.stderr))
    except FileNotFoundError as exc:
        print(f"detail: error: missing input {Path(str(exc.filename)).name}; run earlier stages")
        return 1
    except EditsError as exc:
        for error in exc.errors:
            print(f"detail: error: {edits_path.name}: {error}", file=sys.stderr)
        return 1
    write_detail(work_dir, detail)
    s = detail.stats
    print(
        f"detail: patch triangles: {s['patch_triangles']}, clutter: "
        + ", ".join(f"{k}: {n}" for k, n in s["clutter"].items())
        + f" ({s['seconds']} s)"
    )
    print(f"detail: meadow: {s['meadow_cells']} cells; expected instances " + ", ".join(f"{k}: {n}" for k, n in s["meadow_expected"].items())
          + "; busiest 70 m disc " + ", ".join(f"{k}: {n}" for k, n in s["meadow_busiest"].items()), file=sys.stderr)
    print("detail: patch triangles by area type: " + "; ".join(f"{a} {l['total']}" for a, l in s["patch_triangles_by_type"].items()), file=sys.stderr)
    return 0
