# parcel-run timing (issue #39, the 2 s / 5 s gate)

**Verdict: PASS.** Typical end-to-end time of `uv run python -m suchohrad_pipeline parcel-run --area village ...`
is 0.3 to 0.85 s; the slowest realistic case (the largest forest parcel, 179 000 m2) takes 0.77 to 0.85 s. The
target is about 2 s and the hard gate 5 s, so the live-vs-rebuild decision does not need to be reopened: spawning a
local pipeline subprocess per Apply is fast enough on this machine.

## Machine

- `Linux B2M7QN3-jjakubec.corp.netsuite.com 7.0.8-100.fc43.x86_64 #1 SMP PREEMPT_DYNAMIC Fri May 15 15:13:18 UTC 2026 x86_64 GNU/Linux`
- CPU: 11th Gen Intel Core i7-11850H @ 2.50 GHz (16 threads), 62 GB RAM, local NVMe/SSD. Python 3.13, shapely 2.x.
- Other work (an editor, a desktop) was idle but not stopped; no attempt was made to pin the CPU.

## Method

- Data: `data/village/work` (1165 parcels, village rules profile: `max_per_chunk: 150`, so the per-chunk tree cap is
  active). Default `pipeline/config/edits.json` (empty) except for the group row, which uses a scratch edits file with
  `G-0001 = P-0463, P-0462, P-0472` (three adjacent house plots, 578 to 644 m2).
- Each target: one **cold** run, then three **warm** runs. Cold means the work files (`data/village/work/*`,
  `rules.yaml`) were evicted from the OS page cache with `posix_fadvise(DONTNEED)` just before the run; the Python
  interpreter, `.venv` and shared libraries stay cached (dropping those needs root). So "cold" here is "first run
  after the data files fell out of cache", not a cold boot.
- Wall time is measured by the benchmark script around the subprocess (`uv run python -m suchohrad_pipeline parcel-run
  --area village ...`, stdout captured). "direct" is the same through `.venv/bin/python`, skipping `uv run`.
  "in-process" is `timing_s.total` from the fragment itself (load work files + plan edits + compute; excludes
  interpreter start and imports).
- No `fields` exist in the village data (`kind` is one of building_plot, garden, forest, road, water, other), so the
  "forest/field" rows are forests and the 1.6 km2 `other` parcel.

## Results (seconds)

| Case | Target | parcels / fence sections / trees returned | cold, `uv run` | warm, `uv run` min / median / max | warm, direct (no `uv`) median | in-process total (warm median) |
|---|---|---|---|---|---|---|
| small garden (183 m2) | P-0043 | 1 / 21 / 0 | 0.29 | 0.28 / 0.30 / 0.32 | 0.27 | 0.10 |
| large building plot (10 400 m2, fenced) | P-0349 | 1 / 97 / 5 | 0.72 | 0.68 / 0.70 / 0.73 | 0.67 | 0.49 |
| large building plot (25 271 m2) | P-0089 | 1 / 0 / 6 | 0.69 | 0.63 / 0.64 / 0.66 | 0.66 | 0.45 |
| property group of 3 plots | G-0001 | 3 / 0 / 1 | 0.64 | 0.61 / 0.61 / 0.66 | 0.60 | 0.44 |
| many trees (120, forest-like) | P-0920 | 1 / 0 / 120 | 0.37 | 0.31 / 0.32 / 0.32 | 0.31 | 0.14 |
| many trees (120) | P-0076 | 1 / 0 / 120 | 0.37 | 0.38 / 0.38 / 0.39 | 0.34 | 0.17 |
| forest, largest (178 687 m2) | P-0578 | 1 / 0 / 55 | 0.81 | 0.77 / 0.82 / 0.85 | 0.81 | 0.64 |
| forest, median (3 694 m2) | P-0509 | 1 / 0 / 15 | 0.38 | 0.36 / 0.37 / 0.42 | 0.39 | 0.19 |
| other, 1.6 km2 | P-0182 | 1 / 0 / 120 | 0.65 | 0.63 / 0.65 / 0.70 | 0.64 | 0.46 |
| road, large (4 842 m2) | P-1019 | 1 / 0 / 0 | 0.58 | 0.57 / 0.59 / 0.64 | 0.58 | 0.40 |

Cold and warm differ by at most 0.05 s except for the tree-heavy cases (about +0.05 s): the work files are small
(`parcels.json` 1.2 MB, `terrain_heights.npy` 2 MB, memory-mapped).

An in-process sweep of **all 1165 parcels** (each as a single target, village profile, with a scratch edits file
holding a group, custom fences, tree overrides and an object) had a worst `timing_s.total` of 0.90 s.

### `uv run` startup overhead (median of 5, `-c pass`-style, no pipeline work)

| Command | Seconds |
|---|---|
| `uv run python -c pass` | 0.027 |
| `.venv/bin/python -c pass` | 0.014 |
| `uv run python -c "import suchohrad_pipeline.parcel_run"` | 0.189 |
| `.venv/bin/python -c "import suchohrad_pipeline.parcel_run"` | 0.208 |

`uv run` itself adds only about 0.01 to 0.03 s. The real start-up cost is importing numpy, shapely and PyYAML
(about 0.19 s); the rest of a run is the work.

## What was optimised

The first working version was already fast (0.3 to 0.9 s), so the work focused on structure rather than tuning:

1. **Only the neighbourhood is computed.** Fences: the targets plus, transitively, every lower-ID parcel whose
   boundary is within 0.5 m (these are the only ones that can change the targets through the "already fenced" union),
   plus whole groups. Buildings, road edges and road parcels are cut to the neighbourhood with STRtrees, so the road
   zone, building union and road-parcel union are not rebuilt for the whole village. Trees: only the target parcels'
   rule trees; OSM trees are skipped (they have no parcel and do not count towards the cap). The chunk cap is
   handled by also generating the trees of the other parcels in the affected chunks.
2. **Lazy imports.** `pyproj` (`frame.py`, `geo.py`, `terrain.py`) and `rasterio` (`terrain.py`) are imported when
   first needed, and `cli.py` imports `stages`, `preview` (matplotlib) and the other heavy modules only for the
   command that uses them. `import suchohrad_pipeline.cli` went from about 0.59 s to about 0.19 s (`-X importtime`,
   measured before and after on this machine).
3. **Memory-mapped terrain.** `load_terrain(..., mmap=True)` maps the two `.npy` arrays instead of reading them.
4. Not done, because the gate is met by a wide margin: sharing the parcel polygons between `plan_edits` and the
   neighbourhood search (`shape()` for 1165 parcels is about 0.1 s of the 0.4 to 0.6 s compute), pre-indexing, or a
   long-lived server process. If the editor ever wants sub-0.3 s, those are the next steps, in that order.

## Equivalence

`pipeline/tests/test_parcel_run.py` compares, on the synthetic workspace, the fragment's fences, trees and objects
with the target parcels' share of a full `run_props`, for single parcels of every kind, two parcels, a group and a
group member, with and without edits (custom fence with a gap, tree count and density, seed offset, an object, fence
`none`, a group fence), and with the chunk cap off and on. Two mutation checks confirmed the tests fail if the
cap neighbours or the lower-ID neighbours are left out.

Separately, on the real village the fragment was compared with the full props stage for every one of the 1165 parcels
(with the edits described above, cap 150): fences and trees were identical for all of them. The full props output of
the refactored `props.py` is byte-identical to the `data/village/work/props.json` that was generated before the change.
