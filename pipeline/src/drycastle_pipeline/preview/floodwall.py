"""Pictures of the flood wall kit (matplotlib), for looking at it without the game.

    uv run python -m drycastle_pipeline.floodwall_preview [--out DIR] [--glb]
    uv run python -m drycastle_pipeline.floodwall_preview --sync-manifest

writes into ``out/floodwall`` (by default):

* ``pieces.png``: every piece of ``floodwall.PIECES`` from the village side, at one scale;
* ``pieces_river.png``: the same from the river side;
* ``run.png`` / ``run_river.png`` / ``run_plan.png``: a run laid by ``lay_run`` (a gate, an access
  point, a bend each way), from the village, from the river and from above;
* ``access.png`` / ``access_river.png``: the access point close up, from each side;
* with ``--glb``: ``<name>.glb`` of every piece, as the map export writes them.

``--sync-manifest`` instead rewrites the ``prim:floodwall_*`` entries of ``assets/manifest.json``
from the generator (run it after changing the kit). The drawing is the painter's sort of
``poles_preview``: good enough to judge shapes and colours, not a render of the game. The part of
a piece that is in the ground is cut away in the pictures.
"""

import argparse
from pathlib import Path

import numpy as np

from drycastle_pipeline.assets import DEFAULT_MANIFEST
from drycastle_pipeline.config import REPO_ROOT
from drycastle_pipeline.floodwall import PIECES, build_piece, lay_run, manifest_entries, run_mesh
from drycastle_pipeline.meshes import Mesh
from drycastle_pipeline.poles_preview import render_sheet, replace_entries

DEFAULT_OUT = REPO_ROOT / "out" / "floodwall"
VILLAGE, RIVER = (-62.0, 14.0), (118.0, 14.0)  # where the camera stands: compass direction, elevation


def sync_manifest(path: Path = DEFAULT_MANIFEST) -> int:
    """Replace the ``prim:floodwall_*`` entries of the asset manifest; returns how many were written."""
    return replace_entries(path, ("prim:floodwall_",), manifest_entries())


def above_ground(mesh: Mesh) -> Mesh:
    """The mesh cut at z = 0: what shows once a piece stands in the ground."""
    tris, cols = [], []
    for tri, col in zip(mesh.tris, mesh.colours, strict=True):
        poly = []
        for k in range(3):
            a, b = tri[k], tri[(k + 1) % 3]
            if a[2] >= 0:
                poly.append(a)
            if (a[2] >= 0) != (b[2] >= 0):
                poly.append(a + (b - a) * (a[2] / (a[2] - b[2])))
        for k in range(1, len(poly) - 1):
            tris.append([poly[0], poly[k], poly[k + 1]])
            cols.append(col)
    return Mesh(np.array(tris, float), np.array(cols, np.uint8))


def piece_cells() -> list[tuple[str, Mesh]]:
    return [(name.removeprefix("floodwall_"), above_ground(build_piece(name).mesh)) for name in PIECES]


def demo_run() -> Mesh:
    """Wall, a gate, wall with a bend towards the river, the access point, a bend back, wall."""
    s4, s2, joint = "floodwall_straight_4", "floodwall_straight_2", "floodwall_joint"
    names = [s4, "floodwall_gate", s4, s2, joint, s4, "floodwall_access", joint, s4, s4, "floodwall_gate_pier"]
    return run_mesh(lay_run(names, bends={5: 18.0, 8: -18.0}))


def write_glbs(out_dir: Path) -> None:
    from drycastle_pipeline.export import build_chunk_scene

    for name in PIECES:
        scene = build_chunk_scene([], {name: build_piece(name).mesh})
        (out_dir / f"{name}.glb").write_bytes(scene.export(file_type="glb"))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Pictures of the flood wall kit.")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--glb", action="store_true", help="also write a GLB of every piece")
    ap.add_argument("--sync-manifest", action="store_true",
                    help="rewrite the prim:floodwall_* entries of assets/manifest.json and stop")
    a = ap.parse_args(argv)
    if a.sync_manifest:
        print(f"{sync_manifest()} flood wall entries written to {DEFAULT_MANIFEST}")
        return
    a.out.mkdir(parents=True, exist_ok=True)
    render_sheet(piece_cells(), a.out / "pieces.png", 3, (7.6, 3.4), px_per_m=110.0, view=VILLAGE,
                 title="Flood wall pieces (prim:floodwall_*), from the village side")
    render_sheet(piece_cells(), a.out / "pieces_river.png", 3, (7.6, 3.4), px_per_m=110.0, view=RIVER,
                 title="Flood wall pieces, from the river side")
    run = above_ground(demo_run())
    for file, view, what in (("run.png", (-90.0, 20.0), "from the village side"),
                             ("run_river.png", (90.0, 20.0), "from the river side"),
                             ("run_plan.png", (-90.0, 89.0), "from above (the village at the bottom)")):
        render_sheet([(f"a run laid by lay_run, {what}", run)], a.out / file, 1, (37.0, 6.5), px_per_m=60.0,
                     view=view, centre=True)
    access = [("access point", above_ground(build_piece("floodwall_access").mesh))]
    render_sheet(access, a.out / "access.png", 1, (7.0, 3.6), px_per_m=260.0, view=(-78.0, 10.0),
                 title="From the village side")
    render_sheet(access, a.out / "access_river.png", 1, (7.0, 3.6), px_per_m=260.0, view=(112.0, 16.0),
                 title="From the river side")
    if a.glb:
        write_glbs(a.out)
    for f in sorted(a.out.iterdir()):
        print(f)


if __name__ == "__main__":
    main()
