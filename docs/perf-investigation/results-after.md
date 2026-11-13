# Before / after

2026-11-13. Same machine, same map (`out/village/map`, 0.10.0-village, 41 chunks, built from main with ortho trees), same harness (`perf/`), same commands as `results-baseline.md`.
**Before** = `perf-harness` at `ec0a2ea` (harness only, no behaviour change). **After** = branch `perf-merged`: `ec0a2ea` + `fix-tick` + `fix-lagcomp` + `fix-net` + `fix-render` + `fix-pred` (batch `out/perf/after1/`, `perf/run_after_batch.sh`, 1,695 unit tests) + `fix-sleep2` (idle and loss-test re-run in `out/perf/after2/`, 1,710 unit tests). Loss test on the final build: PASS, 0.00 % wrong states, reconciliations 429 (clean) and 520 (rtt100).
Machine: i7-11850H, powersave governor, shared with other agents' builds (all measurement runs serialised through one lock; builds and unit tests were not). Numbers carry that noise: differences below ~10 % are not claims.

## 1. Summary

| # | Finding | Before | After | Verdict |
|---|---|---|---|---|
| 1 | **C-02 reconcile floor** (new root cause: reconcile replay ran in `_Process`, so `MoveAndSlide` used the render-frame delta and moved each replayed step half a tick) | straight x2/x4 clients: 30 reconciles/s at a constant 8.9 cm error (bimodal per client); 16-bot loss test: 3,856-5,674 reconciles / 30 s (up to 900 per client) | **0 reconciles/s** at x2, x4, x8; loss test **349** (clean), 643 (rtt100), 810 (rtt200), 1,045 (loss10); 17-19/s per client through the lossy proxy -> 1.5-2.1/s | **Fixed** (-92 to -94 %) |
| 2 | **B-01 no lag compensation** | real hit rate of a perfect-aim bot: 6.0 % at RTT 72, 6.1 % at 130, 5.4 % at 232 (8.4 % at 27) | with rewind on (same build, vs `--no-lagcomp` in the same batch): **8.7 % vs 2.8 %** at 68-71 ms, **4.6 % vs 1.0 %** at 127-132, **4.0 % vs 3.1 %** at 230-233; shots within 0.56 m of the target 17-29 % vs 3-11 % | **Works; the 11 % absolute target was not reached** (the bot's own accuracy varied between batches; trust the A/B, not the absolute) |
| 3 | **A-03 head-of-line blocking** | reliable Shot/Hit/Kill extra delay p99: 172 ms (1 % loss), **180 ms (3 %)**, 272 (5 %), 1,075 (10 %); p95 138 ms at 3 % | effects (Shot) on an unreliable channel: p99 **17.5 ms** at 3 % loss, 18.7 at 10 % (no collateral delay; 3.7 % / 9.9 % of shots never arrive, by design); Kill (stays reliable): p99 180 ms at 3 %, 504 ms at 10 % | **Collateral delay removed.** Kill's own retransmit delay (about one RTO) is inherent; the "<= 60 ms" target in the brief was mis-specified |
| 4 | **D-01 / T2-04 parked vehicles stepped every tick** | idle (0 clients) tick mean **1,940 us**; `vehicles` phase **1,783 us** (92 %); 29 ground cars + 3 flying/amphibious stepped every tick | after `fix-tick` (ground cars): tick 948-1,011 us, `vehicles` 740-788 us. After `fix-sleep2` (flying and amphibious too), 3 runs on the final build (`out/perf/after2/`): tick mean **322-331 us** (-83 %), `vehicles` phase **95-106 us** (-94 %), **32 of 32 asleep**, idle CPU 2.4 % (was 7-12 %) | **Fixed.** Risk: no smoke test covers entering, flying, driving, shooting or bumping a helicopter or amphibious transport; the wake paths are unit-tested at rule level only |
| 5 | **G-01 allocations** | idle 23.4 KB/tick (0.70 MB/s); all-streak load 1.30 MB/s | idle **11.5 KB/tick (0.35 MB/s)**; all-streak **1.14 MB/s** (vehicle loop 352 B/tick; the rest is per-client encode and input work: `sim_clients` 14 KB/tick, `snapshots` 13 KB/tick) | **Partly fixed**; the 0.6 MB/s target was not reached |
| 6 | **GC pauses in the tick** | 2 ticks > 25 ms in 20 s idle (25.6 and 36.8 ms) | 1 tick > 20 ms (22-34 ms) in 51 s idle | **Rarer (about 3x), not gone** |
| 7 | **G-07 watchability** | stats line had tick p50/p99 only | stats line now has per-phase p50/p99 and GC counts | **Done** (checked in `server: stats` lines) |
| 8 | **E-06 map build not checked at join** | any map folder joined silently | map hash in Hello, mismatch rejected, protocol 24 -> 25 | **Done** (unit tests; two-folder manual check not run) |
| 9 | **A-04 / A-05** (scoreboard per kill, empty packets) | 6.5 kB/s per client down, 16 players | 6.6 kB/s (6.19-6.63 over the scenarios) | **No measurable bandwidth change** (the 5.5 target was not reached) |
| 10 | **F-05 default MSAA** | Intel UHD iGPU, 1080p, 4x MSAA: p50 **15.2 ms**, p99 **26-28 ms** (4 runs) | new-install default 2x MSAA: p50 **15.3-16.2 ms**, p99 **28.6-29.2 ms** (3 runs) | **No gain on this GPU** (no-MSAA gave 12.5 / 23.6 ms in the baseline; 2x does not buy it). Needs a decision, not more code |
| 11 | **F-04 shadow distance** | n/a | sweep on the iGPU: shadow 60 m p50 15.15 / p99 33.3 ms, 40 m 16.67 / 30.3 vs base 15.15 / 27.8 | **No gain; left unchanged** (Godot's default max distance is already 100 m, so the audit's "no max distance" claim was wrong) |
| 12 | **F-02 explosion nodes** | 15 live blasts: +135 scene nodes | pooled kits (4 x 9 nodes): +4 nodes at the sample; blast bench p99 27.8 -> 23.3 ms | **Fixed** (visual review of kit reuse is open) |
| 13 | **T1-2** | (fixed before this work, commit `628eb7a`) | 0.00 % wrong decoded states in all four loss scenarios | **Confirmed** |

## 2. Unchanged by design (re-measured, no regression)

| | Before | After |
|---|---|---|
| Tick at 16 players (loss test, 8 cars): p50 / p95 / p99 | 1.0 / 1.4 / 2.0 ms | 1.03 / 1.53 / 2.23 (clean); 0.87-0.91 / 1.3-1.45 / 2.05-2.58 (impaired) |
| Tick at 64 players (16 humans + 48 bots), mean / p99 | 3.0 / 7.5 ms | 3.3 / 8.1 ms (noise; unchanged), 26.5 kB/s per client (was 28) |
| Tick at 96 players, mean / p99 / max | 6.5 / 24.9 / 49 ms | 7.1 / 27.7 / 48 ms (still over the 33 ms budget at p99) |
| Killstreak load (16 clients) tick mean / p99 | 1.66 / 2.6 ms | 1.66 / 2.96 ms |
| Raw micro-benchmark costs (ray 2.5-5 us, player step 17-19 us, parked car step 25 us) | same | same (the micro-benchmark calls the step directly, so the sleep rule does not show there) |
| Server boot, RSS | 3.6-4.9 s, 617-640 MB | 2.7 s, 644 MB |
| Loss-test decoded-state correctness | 0.00 % wrong | 0.00 % wrong |

## 3. Open after this round

1. **End-to-end check of the sleep rules for helicopters and the amphibious transport** (enter, fly/drive, shoot, bump, leave): unit-tested only; `make shooter-bike-smoke` and `shooter-blast-smoke` fail identically on the baseline code (pre-existing, not caused by this work).
2. **Allocation** in `sim_clients` (14 KB/tick) and `snapshots` (13 KB/tick): per-client encode and input work; needs pooled packet buffers; would cut the GC pauses further.
3. **A-01 world channel** (acked baseline) and the scoreboard row delta were not done (protocol risk, unverified).
4. **96 players**: tick p99 27.7 ms; `sim_clients` 3.1 ms, `bots` 1.9 ms, `snapshots` 1.2 ms per tick: interest management or a worker thread for encoding are the next levers (A-07/H-01). 32 humans is the peer cap anyway.
5. **Client**: the iGPU is GPU-bound; MSAA 2x does not help; MSAA off does (+18 %); a decision on the default (and a Mobile renderer test) is open.
6. **D-02** (ground-vehicle driver prediction), **E-01/E-03** (heightfield terrain, collision-only server artifact) were deliberately not attempted: no measured need (boot 2.7-4.9 s).

## 4. How this was measured

Commands are in `perf/run_after_batch.sh` (every experiment of the baseline batches with the same parameters) and `docs/perf-investigation/measurement-plan.md`. Raw output: `out/perf/20261113T012920Z/` (before) and `out/perf/after1/` (after), gitignored. One batch per state, so differences under about 10 % are inside the noise; the large effects (reconciles, vehicle phase, collateral reliable delay, explosion nodes) are far outside it. Lock-queue contention with the fixing agents' own builds ran during the after batch only for builds and unit tests, not for the measured runs.
