# Measurement plan (Phase 2)

Goal: a concrete number for every claim in `docs/perf-investigation/*.md` before any fix is written. Every experiment has:
a finding it settles, the **prediction** taken from the report (so a result is a pass/fail, not just a number), the metric, the
command or harness, and what it needs built first. Results go to `out/perf/<UTC timestamp>/<experiment>/` as JSON and CSV, and
are summarised in `docs/perf-investigation/results.md` (one row per experiment, raw files not committed).

Branch `perf-harness` (from `main` at `0db1c7e`). Main contains `areas-not-parcels`; the shooter code differs only by a 22-line
change in `MapLoader.Objects.cs`, so the reports apply. Instrumentation lives behind flags and is never on by default.

## 0. Method (applies to every run)

- **Machine:** i7-11850H (8C/16T), 62 GB, NVIDIA T1200 Laptop 4 GB (about GTX 1650 class; F's floor is a GTX 1060 / RX 580, so
  this machine is roughly at or a little above the floor for GPU, well above it for CPU), Linux 7.0.8, Godot 4.7.x mono. Every
  result is tagged with this machine. Numbers on target hardware (the 2-vCore / 4 GB VPS, H-14) need a separate run.
- **Quiet machine.** The machine is shared with other worktrees and agents. Before a run: record `uptime` load average and the top
  five CPU processes; a run with 1-minute load above 4 at the start is repeated. Server pinned with `taskset` to 4 cores, bots/clients
  to the other 12, as the loss test does.
- **Power:** governor `performance` if the user allows it (needs root; otherwise record the governor and `intel_pstate` status).
  Laptop thermals: each run records CPU package temperature at start and end where `sensors` exists.
- **Repeats:** 3 runs per condition, report each run plus median; 5 for anything within 20 % of a threshold. Warm-up 3 s for
  server runs, 10 s for client runs. Percentiles from raw per-tick or per-frame samples, never from averages of averages.
- **Map:** the rebuilt village map in this worktree (`out/village/map`), tagged with chunk count, tree count and
  `map-version.txt`. A second build is measured only where the claim depends on map size (L, M groups).
- **No screenshots** unless asked. Client runs open a window on this machine's GPU.
- **Controls:** each experiment states its control (e.g. parked cars on vs off). A change in a number without a control is not a result.

## 1. Experiment catalogue

Status column: `existing` = runs today with existing tools; `instr` = needs counters/Stopwatches behind a flag; `harness` = needs a new
scripted load or tool; `static` = read or measure files, no run.

### S. Static (files, sizes, data)

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| S1 | E-13, E-02, H-06 | map folder is large; chunk GLBs dominate; collision-only data about 7.5 % of chunk bytes (E-03) | `du -b` per file class under `out/village/map`; chunk count; manifest triangles (`terrain`, `terrain_collision`, props); `zstd`/`zip` of the folder for download size (`make map-package` if it works offline) | static |
| S2 | A-03, A-05, A-09 | ENet: max packet 4096? MTU 1392; RTO min 500 ms | read the Godot ENet source bundled in the engine version in use (modules/enet) | static |
| S3 | D-13, H-04, G-02 | vehicles.json spawn sum 32; gunship fire rates in killstreaks.json | script over `godot/shooter/data/*.json`: sum spawns by type, rocket life and rate | static |
| S4 | the whole audit | count of nodes/bodies/shapes created at load | grep + the load log (L1) | static |

### P. Pipeline and build

| id | settles | metric | status |
|---|---|---|---|
| P1 | pipeline cost for a full rebuild | wall time per stage, total, peak RSS: `rebuild-map.sh` log (done once at set-up) | existing |
| P2 | dev loop | `dotnet build godot/shooter/shooter.csproj` clean and incremental, `make shooter-test` time | existing |

### L. Load, memory and bundle

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| L1 | E-02, E-03, E-12, T2-26, T2-29, H-06 | server load is seconds, dominated by chunk GLB parse; RSS is hundreds of MB | headless server start; parse the `map load phases` log line and `server: ready`; sample RSS (`/proc/PID/status` VmRSS/VmHWM) every 100 ms to ready +30 s; 5 cold (drop caches not possible without root; first run after build is "cold") and 5 warm runs | existing + a small shell sampler |
| L2 | E-12, F | client map load, time to menu, time to first in-world frame, RSS and VRAM at idle | client run with telemetry; `--role client --connect`; time from process start to `welcomed`; Godot monitors `memory/static`, `render/video_mem_used`; `nvidia-smi --query-gpu=memory.used` | instr (log markers) |
| L3 | E-13 | exported server/client binary sizes | `make shooter-export-check` then `make shooter-export-server` (needs export templates; if missing, record that) | existing, may be blocked |
| L4 | E-03 | what a collision-only read would save: time to parse only collision meshes vs whole GLB | micro-benchmark in the loader: parse each chunk twice, once building everything, once skipping visual nodes (counts nodes/triangles skipped) | instr |
| L5 | E-05, H-08 | scene-tree node count and body count at load (server, headless) | Godot monitors `object/node_count`, `physics_3d/active_objects`; the counts in the load log | instr |

### N. Server tick and scale

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| N1 | T1-1, G-09 | idle server uses a few % of one core with the frame cap | 0 clients, 60 s, `pidstat -p PID 1` (CPU %), tick interval | existing |
| N2 | the reference numbers; A-02, A-10 | tick p50/p95 1.6/2.6 ms; 4.24 kB/s per client down; staleness about 5-6 ticks at rtt100 (not 86) | `make shooter-loss-test MAP=out/village/map SCENARIOS=clean,rtt100,rtt200,loss10 BOTS=16 MEASURE=30`, x3 | existing |
| N3 | A-07, H-01, H-02, H-14 | 10.7/20.3/40.2 kB/s per client at 16/32/64 (A) vs about 17 at 64 (H); send loop dominates | `BOTS=8,16,32,48,64` clean, 3 runs: tick p50/p95/p99/max, server CPU %, egress, per-client bytes; split of the tick into sim and send (needs N5) | existing + instr |
| N4 | D-01, G-05, T2-04 | 0.6-1.5 ms of the tick | tick p50/p95 with all 32 vehicles parked (default) vs `--vehicle-set` with none, same bots, 3 runs each | existing |
| N5 | G tick budget table, T2-07 | per-system times sum to the tick; sim/send split | Stopwatch per system in `_PhysicsProcess`, accumulated per tick, written as columns to the existing tick log; flag `--perf-phases` | instr |
| N6 | G-01, T3-001/002 | up to 8,500 LiveTargets calls/s, 3.6 MB/s allocation under all-streak load | counters in `LiveTargets()` (calls, elements); `dotnet-counters collect` for `alloc-rate`, `gen-0/1/2-count`, `time-in-gc`; 60 s | instr + existing tool |
| N7 | G, D-01, E-07, C-01 | `IntersectRay` 5-30 us; `MoveAndSlide` at most 35 us | in-process micro-benchmark scene on the real map (`--perf-micro`): 100,000 random rays (origins over the map, directions horizontal and downward), 10,000 `MoveAndSlide` on a Player, on a parked ServerCar, on 32 ServerCars; per-call mean/p50/p99 with Stopwatch ticks; rays against 399 trimeshes and against the trees only (layer masks) | harness |
| N8 | G-08, G-02, G tick budget | all-killstreak tick p50 about 4-13 ms | sandbox clients (`--sandbox` + server `--allow-sandbox`) scripted to call every killstreak at once at 16 players: UAV, turrets, helpers, attack helicopters, gunship, missiles, MOAB; 60 s; tick percentiles with N5 phases; rays/s and LiveTargets/s with N6 counters | harness |
| N9 | G-02, B-04 | 16 helicopters give about 167 live rockets, 5,000 rays/s, about 50 kB/s per client | N8 restricted to the attack-helicopter killstreak; count live rockets per tick; bytes per kind (W1) | harness |
| N10 | D-05, H-04, A (vehicle bytes) | parked 0 B; moving car about 14 B/tick (420 B/s); 32 moving about 13 kB/s per client | car bots (`NetClient.CarBot.cs`) driving all 32 vehicles; bytes per entity type from W1 | harness |
| N11 | D-08 | contacts O(n²), fine below 150 vehicles | spawn 32/64/128/256 synthetic vehicles in a bench world (`--perf-micro`); time `ResolveVehicleContacts` per tick | harness |
| N12 | G-10, G-11, H-07 | 5-10 writes/s at 16 players; queue bounded at 0 outage | SQLite default: kills per second from bots -> `DbLane` queue depth, writes/s, commit time; Postgres outage test only if Docker is available (skip otherwise) | instr |
| N13 | G-09, T2-13 | GC settings: gen-0 rate | `dotnet-counters` as N6, with `DOTNET_gcServer`/`TieredPGO` as shipped | existing tool |
| N14 | G-12, E-07 | bot sight rays scale as players x bots | rays/s counter in `BotWorld.Clear` at BOTS=16/32/64 | instr |

### W. Wire and network

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| W1 | A-04, A-05, A-10 | idle 2.3 kB/s per client, of which 1.6 empty packets; walking 4.3 kB/s | per-kind byte counters on every `Send` (entity snapshot, world, events, scoreboard, empty); dumped per second | instr |
| W2 | A-03, A-06, A-10, H-03 | reliable-channel stalls of about one RTO; whole-tick loss compounding by packet count | **UDP impairment proxy** (`perf/udp_proxy.py`, userspace, no root): sits between real ENet endpoints, adds delay, jitter and loss to real datagrams in both directions, so ENet's real retransmit and sequencing apply (the in-app `NetSim` never drops reliable packets). Measure Hit/Kill delivery latency p50/p99, channel-0 stall count, ticks completed vs entity count (40/80/120) at loss 1/3/5/10 % | harness |
| W3 | A-01, G-03 | world-channel idle cost about 24 B/s per entity per client; 6 kB/s at 256 | idle world entities (corpses, turrets, crates) at 16/64/256 entities; W1 counters | instr + harness |
| W4 | A-09 | incoming byte budget absent | a client that floods oversize/inputs at 1,000/s: server CPU and memory growth | harness |
| W5 | A-08 | server does not step a player with an empty input queue | counter per player: ticks with empty queue; at clean, jitter 20 ms and loss 3 % | instr |
| W6 | join/leave cost (A) | full-state bytes grows with entities | join one client into a running 16-bot match with 0/50/150 entities: bytes and time to first complete snapshot; leave and reconnect | instr + harness |

### B. Hit registration and lag compensation (shadow mode)

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| B1 | B-01, B-03, B-05 | walker 0.75 m off at RTT 50; 1.5 m at 100; sprinter more | server keeps a position history ring (instr only: positions at each of the last 15 ticks, per player and vehicle). On every shot compute, in shadow, whether the shot would hit at the tick the shooter saw (`ack tick - interp delay`) vs at present; log both results. Targets: bots walking (1.4 m/s), running (about 5 m/s), cars (up to 44 m/s). Latency via the W2 proxy at 0/25/50/100/200 ms one way. Output: hit rate present vs rewound, and the lateral miss distance distribution | harness |
| B2 | B-02 | cars 6.7-8.9 m off at RTT 50-100 | same as B1 with a car bot at speed; analytic oriented-box test on the rewound pose | harness |
| B3 | B-01 cost | about 1-2 us per shot, 6 kB of history for players, 21 kB with cars | measure history capture per tick and the shadow test per shot with Stopwatch | instr |
| B4 | B-06, C-05 | shots in one tick see mixed times; recoil on two clocks | log per-shot: tick index of the resolved target positions; client vs server recoil values for the same shot | instr |

### C. Prediction

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| C1 | C-02, C-01 | 2205 clean-link reconciles (770 max per client) | clean loss test with bots spread 200 m apart (`--cluster-spawns` off) vs clustered: reconcile count; per-reconcile error and cause tag (collision with a player, floor state, perk, input starvation) | instr |
| C2 | C-01 | Jolt collides two kinematic bodies on layer 1 | headless scene: two Player bodies 1 m apart, walk one into the other; does `MoveAndSlide` stop? Same with the layer fix | harness |
| C3 | C-04 | worst gap 117 ms at rtt100 exceeds the 100 ms delay | Interpolator clamp counter per remote per second at rtt100/rtt200 and jitter 20 ms via W2 | instr |
| C4 | D-02, D-12 | ground car about 100 ms late; driver prediction corrections per bump | driven helicopter vs car: input-to-visible latency from logs; reconcile count per bump | instr |
| C5 | C-07 | one MoveAndSlide per player per tick | N7 numbers x players | harness (N7) |

### M. Map and physics world

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| M1 | E-09, E-10, H-06 | about 3 nodes per tree; tree-solid nodes in the tens of thousands | counts at load (L5), per class: static bodies, shapes, tree solids | instr |
| M2 | E-01, E-08 | terrain is 89 % of collision triangles; 45 character moves per tick are against the same trimesh | triangle counts from manifest; N7 ray and move cost with terrain vs without (layer mask) | harness (N7) |
| M3 | E-04, T2-28 | terrain rim sampling about 4 M cells on the main thread | time of the rim step from the load log (add Stopwatch) | instr |
| M4 | E-05 | a parcel swap leaves trees/fences solid | headless: `ReplaceParcel` on a parcel with trees; count solids before/after; time of the swap | harness |
| M5 | E-12 | load is all at start, no streaming | L1 broken down by phase, per chunk, on both map builds | existing |

### R. Client rendering and frame

All on this machine, 1920x1080, VSync off for measurement (on for the second pass), `--rendering-method forward_plus` default and `mobile` as a variant.

| id | settles | prediction | metric / command | status |
|---|---|---|---|---|
| R1 | F-01, F-06 | about 580 draws median and 1,025 at p90 on 1,959 nodes (0.10 build) | scripted camera route (`--perf-route`): a fixed set of 12 viewpoints (village centre, edge, forest, river, bunker chunks, plus the p50 and p90 draw-count locations found by a first sweep), 10 s each at standing height; per frame: `render/total_draw_calls_in_frame`, `render/total_primitives_in_frame`, `time/process`, `time/physics_process`, frame time; CSV | harness |
| R2 | F-04, F-05 | shadows unbounded; 4x MSAA | same route with: shadows off/on and with `directional_shadow_max_distance`; MSAA off/2x/4x; the numbers as frame-time deltas and GPU time (`RenderingServer` viewport render time) | harness |
| R3 | F-02, T2-15..17 | cannon gunner: 6.7 blasts/s, about 15 live explosions, 210 nodes, 105 draws | gunship client stationary under fire: node count (`object/node_count`), draws, frame time p95/p99 over 60 s; same with 4 gunners | harness (N8) |
| R4 | F (spikes), T2-15 | first-use hitches on first explosion/glass/impact | frame-time max and count of frames > 33 ms / > 50 ms / > 100 ms in the first 120 s of a session with a scripted firefight; the existing `NetClient.Telemetry.cs` CSV already records `slow50`/`slow100` | existing + R1 route |
| R5 | F-12-type, T2-24 | 16 characters cost 20-25 draws each (unverified) | 16 bots on screen in a ring at 15 m: draws with 0/4/8/16 characters | harness |
| R6 | F (GC, leaks) | no growth over time | 10-minute soak: managed heap (`GC.GetTotalMemory`), `memory/static`, node count every 10 s | harness |
| R7 | hardware floor | n/a | repeat R1/R2 with the iGPU (Intel UHD / TigerLake GT1, `DRI_PRIME=0`) as the stress case F names | harness |

### H. Growth

| id | settles | metric | status |
|---|---|---|---|
| H1 | H-01..H-03, H-09 | N3 results fitted to a per-client byte model: bytes = a + b*players + c*entities + d*events; fitted coefficients vs A's and H's model | analysis of N3/N10/W1 |
| H2 | H-05, H-08 | cost per additional bot and per additional vehicle (slope of tick vs count) | analysis of N3/N11 |

## 2. Build order (harness work)

1. **Tier 1, no code:** S1-S3, L1 (server), N1, N2. Gives the reference numbers.
2. **Tier 2, instrumentation behind flags** (one commit each): `--perf-phases` (N5, N6, N14, W1, W5, B3, B4, C1 tags, L1 stopwatches, M3), a position-history shadow for B1/B2.
3. **Tier 3, tools:** `perf/udp_proxy.py` (W2), `perf/run.py` (one runner producing the folder layout, metadata and repeats), `--perf-micro` scene (N7, N11, C2, M4), scripted sandbox killstreak client (N8, N9), car-bot driving all vehicles (N10).
4. **Tier 4, client:** `--perf-route` and the monitor CSV (R1-R7, L2).
5. **Analysis:** `results.md` with predicted vs measured per finding, and a re-ranking of the top-10 actions by measured impact.

## 3. What is deliberately not measured

- Postgres on a remote host and a real 2-vCore VPS (no such machine here); noted in `results.md` as "needs target hardware".
- Real internet paths. The userspace proxy models loss and delay; it does not model reordering across routes or bufferbloat.
- Anything needing root (`tc netem`, governor changes, page-cache drops). Where it matters the plan says so and the result is flagged.
