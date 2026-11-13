# Baseline measurements (before any fix)

2026-11-13. Branch `perf-harness` from `main` (`0db1c7e`) plus the harness only (`perf/`, `NetServer.Perf.cs`, `PerfMicro.cs`, `PerfRoute.cs`, a few counters and bot roles); no behaviour changed.
Raw data: `out/perf/20261113T012920Z/` (gitignored; every experiment folder has its logs, CSVs and `result.json`). Plan: `measurement-plan.md`.

**Machine:** i7-11850H (8C/16T, governor `powersave`), 62 GB, NVIDIA T1200 Laptop (4 GB) + Intel UHD (TGL GT1), Linux 7.0.8, Godot 4.7.2 mono. Shared with other agents: 1-minute load was 2.4-4.4
at the start of most runs, so absolute numbers carry noise (see each section). **Map:** `out/village/map` rebuilt from main with ortho-detected trees: 0.10.0-village, 41 chunks, 90.4 MB
(79.5 MB of chunk GLBs), 20,824 trees, 399 colliders, 147 spawns, 32 vehicles (29 on the ground); the pipeline took 45 s for the stages plus 141 s once for the Rust detail engine.
Server pinned to 4 cores, clients to the other 12 (as the loss test does).

## 1. Headline numbers

| Area | Measured |
|---|---|
| Server boot (map load to listening) | 3.6-4.9 s (one 12.5 s outlier on a loaded machine); load phases chunks 1.0-1.3 s, trees 0.6-0.9 s, boundary 0.9-1.5 s |
| Server memory | RSS 617-640 MB at ready, 660 MB peak, 690-697 MB with 16-32 players and 64 bots |
| Server idle (0 clients) | 7-12 % of one core; tick mean 1.94 ms, **92 % of it is stepping 32 parked cars** (1.78 ms) |
| Tick at 16 players (loss test, 8 cars) | p50 0.9-1.0 ms, p95 1.3-1.5 ms, p99 1.9-2.3 ms, max 17-18 ms (was 1.59/2.55 in the old doc) |
| Tick at 64 players (16 humans + 48 bots) | mean 3.0 ms, p50 2.8, p99 7.5, max 21 ms; 28 kB/s per client |
| Tick at 96 players (32 humans + 64 bots) | mean 6.5 ms, p50 5.9, **p99 24.9, max 49 ms** (over the 33 ms budget); 48.7 kB/s per client |
| Bandwidth per client down | 6.2-6.7 kB/s at 16 players (old doc: 4.24), 11.8 at 32, 28 at 64, 43 at 72 moving, 49 at 96; about 18 B per moving entity per tick |
| Peer cap | 64 clients are refused: "The server is full (32 players)" (`MaxPeers = 32`) |
| Client on NVIDIA T1200, 1080p | frame p50 2.5 ms, p99 4.2-4.4 ms (384 fps) at 4x MSAA; runs vary 300-385 fps between identical configs |
| Client on Intel UHD (TGL GT1), 1080p | **4x MSAA: 63 fps, p50 15.2, p99 25.9 ms; no MSAA: 74 fps, p50 12.5, p99 23.6 ms** |
| Client memory | RSS 1.14-1.15 GB, VRAM 255 MB (211 MB without MSAA), 4.2-5 s to ready |
| Draw calls | per-view mean p50 203, p90 1,245 (worst view 1,645); frame draw p50 159-202, p90 1,068 |
| Download size of the map | 90.4 MB raw, 21.1 MB gzip -6, 19.8 MB zstd -3; terrain collision 85,838 of about 96,000 collision triangles |

## 2. Claims tested

| Finding | Claim | Measured | Verdict |
|---|---|---|---|
| D-01 / G-05 / T2-04 | parked cars cost 0.6-1.5 ms per tick | **28.6 µs per parked car step** (16.4 µs of it `MoveAndSlide`); 32 cars = 0.69 ms in the micro-benchmark, 1.78 ms of an idle server tick (CPU downclocked at idle); 24 µs per car in the loss test (8 cars); scales linearly to 128 cars (3.08 ms) | **Confirmed, largest single tick item at low player counts** |
| D-08 | contacts O(n²) fine below 150 vehicles | parked step is linear to 128 cars; contact cost with moving vehicles not measured | Partly measured |
| G (rays) | `IntersectRay` 5-30 µs | **1.5-5 µs p50, 7-13 µs p99**, 300 m ray 5 µs; terrain `down` ray 2.5 µs | Overestimated by about 5x |
| G-02 | 5,000 rays/s from rockets | measured 119-472 rays/s with many streaks active; 5,000 rays/s would cost about 0.8 ms/tick at 5 µs | CPU cost small; the cost is bandwidth |
| G-01 | `LiveTargets` up to 8,500 calls/s, 3.6 MB/s | 201-928 calls/s, 1.3-2.55 MB/s allocation under all-streak load; idle 0.7 MB/s and 23 KB/tick from the vehicle loop (LINQ) | Direction confirmed, magnitude 10x lower in this load |
| GC | (new) | **gen-1/2 collections inside the tick cost 25-37 ms** (two ticks over 25 ms in 20 s idle; tick max 17-49 ms in every load run) | New finding: GC pauses alone overrun the 33 ms budget |
| C-01 | player bodies block each other on the server only | two `Player` bodies **pass through each other** (min distance 0.03 m) | **Rejected** |
| C-02 | clean-link reconcile floor | clean loss test: 3,856-5,674 reconciles per 16 clients (up to 900 per client in 30 s, i.e. every snapshot). One client alone: **0**. 2-4 clients: some clients **30/s with a constant 8.9 cm error along the walking direction (half a tick of walking)**, others 0; 8 clients: 0. Cause not found | **Confirmed as real, bimodal per client; root cause open** |
| T1-2 | deltas against last sent state | loss test: **0.00 % decoded states wrong, longest wrong run 0 ticks** at rtt100, rtt200, loss10 (was 15.4 %, 86 ticks) | **Fixed on this branch** (commit `628eb7a`) |
| N2 clean | gate | final-state check failed once in 3 clean runs (entity #5 6-12 cm off), passed twice | Flaky harness assertion |
| A-03 | head-of-line blocking on the reliable channel | real UDP loss at RTT 100: extra delay of reliable Shot/Hit/Kill events p99 17.8 ms (0 % loss), 172.7 (1 %), 179.9 (3 %), 272 (5 %), **1,075 ms (10 %)**; max 2.3 s | **Confirmed** (impossible to see with the in-app `NetSim`) |
| H-03 | all-or-nothing tick across packets | one-packet ticks: completion tracks `1 - loss` (99.1 % at 3 %); 2-packet ticks (72 moving entities) at 3 % loss: 87.5 % vs 97.9 % at 0 % loss | Direction confirmed, larger than modelled (the 56-bot run also had load stalls up to 9 s even at 0 % loss: treat its tail as unreliable) |
| A-07 / H-01 | bandwidth model | A: 10.7/20.3/40.2 kB/s per client at 16/32/64. Measured 6.5/11.8/28 (64 = 16 humans + 48 bots). H: 17 kB/s at 64 | **A's model is closer; H's number rejected** |
| H-05 | `MaxPeers = 32` | 64 clients refused | Confirmed |
| B-01 | no lag compensation | perfect-aim bot, hits resolved against what it saw (rewound) vs present: hit rate 9.8 % vs 8.4 % at RTT 27 ms; **13.5 % vs 6.0 % at 72 ms; 11.0 % vs 6.1 % at 130 ms; 10.6 % vs 5.4 % at 232 ms** (about 2x); shots within 0.56 m of the target 21-27 % vs 11-16 % | **Confirmed, about 2x loss of hits at 70-230 ms RTT at 5.3 m/s** |
| B-02 | cars 6.7-8.9 m off | not measured (no driving target bot); arithmetic 44 m/s x 0.13 s = 5.7 m | Open |
| F-01 / F-04..06 | draw-bound client | on the dGPU the client is far from any limit (p99 4.4 ms); on the iGPU it is GPU-bound (p50 12.5-15 ms); MSAA 4x costs about 2.7 ms there (+18 %); 16 characters and effects not in this route | Draw count is not the limit on this hardware; iGPU is the floor concern |
| E-02 / E-03 | server loads the visual data it never draws | boot 3.6-4.9 s, RSS 630 MB; collision-only read not measured | Boot is short; low priority at this map size |
| G-08 | bots never use killstreaks | confirmed; the load tests above drive streaks with sandbox clients; fully faithful rocket floods (167 live rockets) were not reproduced | Partly measured |

## 3. Per-phase tick, 16 players, clean (loss test, mean µs of a 1,104 µs tick)

`sim_clients` 320 (29 %), `snapshots` 288 (26 %), `vehicles` 193 (18 %, 8 cars), `poll` 179 (16 %), `telemetry` 45 (4 %, includes GC pauses), everything else under 20 µs each.
Per player and tick that is about 20 µs of simulation, 18 µs of snapshot encoding and 11 µs of polling. At 64 players: `sim_clients` 1,395, `bots` 668, `snapshots` 402, `poll` 231.
At 96 players: `sim_clients` 2,701, `bots` 1,752, `snapshots` 1,085, `poll` 526.

## 4. Killstreak load (16 sandbox clients), tick mean / p99 / max ms, rays/s, bandwidth

| Run | Tick mean / p99 / max | Rays/s | LiveTargets calls/s | Alloc | Out per client |
|---|---|---|---|---|---|
| all streaks, replaced every 2 s | 1.79 / 4.4 / 20.7 | 472 | 928 | 2.55 MB/s | 19.5 kB/s (entities 105-122) |
| one streak each (round robin) | 1.66 / 2.6 / 18.0 | 174 | 235 | 1.30 MB/s | 13.1 kB/s |
| 16 attack helicopters | 1.53 / 2.6 / 20.0 | 119 | 201 | 1.30 MB/s | 11.8 kB/s |

## 5. Not measured / known limits

- Exported binary sizes (`make shooter-export-check` needs export templates), dotnet build times, ENet source facts (S2), per-kind byte counters (W1), interpolator clamp counts (C3), empty-input ticks (A-08), `ReplaceParcel` solids (M4), node and body counts at load (L5), the collision-only parse benchmark (L4), moving-vehicle contact scaling (N11), persistence under load (N12), driving-target lag-comp (B2), and the Intel/NVIDIA frame-time effect of shadows and renderer variants beyond what is in section 1 (the NVIDIA numbers vary ±30 % between identical runs; the `mobile` and `compat` variants ran but their renderer readback is unreliable).
- The 56-bot proxy runs show multi-second stalls even with no loss, so tails at 72 players through the proxy are not trustworthy.
- Everything is one machine; the 2-vCore/4 GB target (H-14) was not available.
