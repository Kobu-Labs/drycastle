# parcel-run timing (issue #39, the 2 s / 5 s gate)

**Verdict: PASS.** `uv run python -m drycastle_pipeline parcel-run --area village ...` takes 0.3 to 0.85 s end to end (target about 2 s, hard gate 5 s); the slowest realistic case, the largest forest parcel (179 000 m2), takes 0.77 to 0.85 s. Spawning a local pipeline subprocess per Apply is fast enough, so the live-vs-rebuild decision stays as is.

Measured on a laptop (i7-11850H, 62 GB, SSD, Python 3.13) against the village data (1165 parcels, `max_per_chunk: 150`); ten target types (small garden, large plots, a 3-plot group, 120-tree parcels, forests, the 1.6 km2 `other` parcel, a road), one cold run then three warm. Cold differs from warm by at most about 0.05 s. An in-process sweep of all 1165 parcels had a worst time of 0.90 s. `--glb` adds about 0.1 s (0.76 s for P-0349, 0.88 s for P-0578).

| Case | Target | warm, `uv run` median (s) |
|---|---|---|
| small garden (183 m2) | P-0043 | 0.30 |
| large building plot (10 400 m2, fenced) | P-0349 | 0.70 |
| property group of 3 plots | G-0001 | 0.61 |
| 120 trees | P-0076 | 0.38 |
| forest, largest (178 687 m2) | P-0578 | 0.82 |
| other, 1.6 km2 | P-0182 | 0.65 |

`uv run` itself adds only 0.01-0.03 s. The start-up cost is importing numpy, shapely and PyYAML (about 0.19 s).

## Design choices that keep it fast

1. **Only the neighbourhood is computed**: the targets, transitively every lower-ID parcel whose boundary is within 0.5 m (they can change the targets through the "already fenced" union), and whole groups. Buildings and roads are cut with STRtrees. The chunk tree cap is handled by also generating the trees of the other parcels in the affected chunks.
2. **Lazy imports** of `pyproj`, `rasterio`, `matplotlib` and the stage modules (`import drycastle_pipeline.cli` 0.59 s to 0.19 s).
3. **Memory-mapped terrain** (`load_terrain(..., mmap=True)`).

Not done (gate met by a wide margin); if the editor ever wants under 0.3 s, in this order: share parcel polygons between `plan_edits` and the neighbourhood search, pre-index, a long-lived server process.

## Equivalence

`pipeline/tests/test_parcel_run.py` checks that the fragment's fences, trees and objects equal the targets' share of a full `run_props` (every parcel kind, groups, edits, chunk cap off and on). On the real village the fragment matched the full props stage for all 1165 parcels, and the refactored `props.py` output is byte-identical to the earlier `props.json`.

## `--glb`

`parcel-run --glb PATH` also writes a GLB with the targets' `parcel_<ID>_props` and `parcel_<ID>_props_collision` nodes (same mesh code and axes as the chunk export) and adds `glb` and `replaces` to the fragment.
