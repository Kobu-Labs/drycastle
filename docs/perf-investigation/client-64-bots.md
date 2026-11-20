# Client load: one client in a fight of 64 bots (November 2026)

What a frame of the client costs in a full fight: a real client with a window plays by script against 64 bots on the VPS, and reports the same
numbers every player's game reports to the dashboard (`docs/monitoring.md`, "Clients"). The test is repeatable (`perf/vps_client_test.sh`); the
log of every run kept is in `client-logs/`, so a later run can be laid next to it. Updated 2026-11-20 with the first batches of fixes. The server's side of the same fight: `vps-64-bots.md`.

## The machine and the load

- Client: laptop, Intel i7-11850H, NVIDIA T1200 Laptop GPU (4 GB), Vulkan Forward+, Linux. The game's default settings (a new profile).
- The picture is drawn at 1920x1080 whatever size the window has; the frame cap (120, or 20 unfocused) and vsync are off, so fps is what the
  machine can do, not what a player sees.
- The player is scripted (`--perf-play`): sprints, turns a quarter every 1.5 s, jumps, fires half a second of every two, dies and respawns.
- Server: the VPS with `--bots 64`, map `0.14.0-village` (145 chunks). Ping about 60 ms.
- Not in the load: other humans, driving or flying, killstreaks, a Bigfoot / Gang / Scud event, the menus, the tactical map.

## How to run it

```
perf/vps_client_test.sh <label>                 # restarts the live server twice: only when nobody plays; opens a game window for 5.5 minutes
perf/client_summary.py docs/perf-investigation/client-logs/*.log.gz out/perf/client/<stamp>-<label>.log
```

- The script exports the committed code to `out/perf/client-build` (again whenever HEAD moved; it refuses a changed `godot/` tree) and uses this
  checkout's `out/village/map`, assets and `assets-private` (the map must be the server's build). `EXPORT=1` exports again regardless.
- The window opens on the hidden workspace (`WORKSPACE=6`) at a fixed 1920x1080.
- It restarts the server with the bots, runs the client for 330 s (`PLAY_SECONDS`; `RES`, `GPU=intel`, `BOTS`), keeps the `client: perf` lines,
  and puts the server back. It refuses when somebody plays or an override file is already on the VPS.
- The card is sampled every second; the log's second line is `# gpu:` (power, clock, temperature, share of time the card was power-capped).
- `client_summary.py` prints the median over the 10 s windows of each run (the first 3 left out: joining, shaders, the first spawn), the card
  state, the scopes in ms per second and per frame, and the cost per event.
- `perf/run_route.py` (offline route, no server) runs on the same export.

The server must know the client's protocol version (an export of another branch may not match the live server). A server without
`EventKind.ClientReport` still gives a valid run: the log is the client's own; only the dashboard stays empty.

Traps met:
- The client aborts when it quits (after the play, in the engine's renderer teardown); the run is good when the log has its windows.
- The engine's error output lands in the middle of log lines; the summary reads `name=number` pairs wherever they are.
- Without `--perf-play` reaching the client the frame cap of an unfocused window (20 fps) is all the run records: check `fps` is not 20.
- The engine's "process time" counter is the worst frame of each second, not the mean (`process_peak_ms`).
- The card's power state decides the frame rate (next section). Read `# gpu:` before comparing anything with a frame time in it.

## The card decides the frame rate

The frame is GPU-bound. The laptop's firmware lets the card run at about 40 W for roughly one route (about 10 ms GPU per frame on the offline
route), then caps it at 20 W (about 30 ms for the same scene). A/B of the baseline build and the batch 3 build on the offline route in the same
capped state: 29.9 ms vs 30.5 ms.

- `fps`, frame times and `render_gpu_ms` must not be compared across the client runs. Card power per run is in the table below: batch 3 ran at
  the lowest power and shows 29 fps for that reason.
- The 2026-11-18 baseline's card state was not recorded: its GPU time and fps do not compare with the 2026-11-20 runs either.
- Batch 2's frame percentiles are also disturbed: compiles were running on the machine during it.
- What compares: nodes, draw calls, memory, allocation, collections and pause, scopes in ms per second for time-driven work, cost per event.
- Scopes in ms per second of per-frame work rise and fall with fps (the frame cap is off); that is why the summary also prints them per frame.

## Results

Medians over the 10 s windows. Batch 1 = commits up to `7d971f10`, batch 2 = up to `8fc369f6` plus `d03c0016`, batch 3 = up to `55aa4bd6`.
(Batch 2's log header names `55aa4bd6`, but its scopes and node count are those of the commits up to `8fc369f6`.)
The 2026-11-20 baseline is `main` before the batches (`aa5f5a79`).

| | 11-18 baseline | 11-20 baseline | Batch 1 | Batch 2 | Batch 3 |
|---|---|---|---|---|---|
| Log in `client-logs/` (`2026-11-..`) | `18-0-baseline` | `20-0-baseline-main` | `20-1-batch1` | `20-2-batch2` | `20-3-batch3` |
| Card state | unknown | 28.9 W, 98 % capped | 33.5 W, 99 % | 33.1 W, 98 % | 25.5 W, 100 % |
| Windows | 29 | 28 | 28 | 29 | 29 |
| **Not comparable (card)** | | | | | |
| Frames per second | 52.1 | 56.2 | 66.9 | 57.8 | 29.4 |
| Frame time p50 / p99, ms | 18.3 / 36.8 | 17.9 / 36.0 | 14.8 / 29.3 | 15.3 / 64.3 | 32.8 / 51.8 |
| Frames over 33 ms per 10 s | 6 | 7 | 4 | 21 | 104 |
| Render time per frame: processor / card, ms | 4.0 / 10.9 | 3.3 / 15.5 | 2.6 / 13.4 | 3.9 / 13.7 | 3.0 / 33.3 |
| **Comparable** | | | | | |
| Draw calls per frame | 2317 | 2319 | 1865 | 1805 | 1790 |
| Primitives per frame, thousands | 3510 | 4219 | 4409 | 4659 | 5166 |
| Nodes in the scene | 240,757 | 308,089 | 305,727 | 305,846 | 52,289 |
| Process memory, MB | 4256 | 4593 | 4435 | 4489 | 3948 |
| C# heap, MB | 847 | 987 | 814 | 885 | 883 |
| Video memory, MB | 1869 | 2006 | 1917 | 2055 | 1955 |
| Allocation, MB/s | 7.8 | 8.2 | 7.6 | 6.5 | 4.5 |
| Collections per 10 s (gen0 / gen1 / gen2) | 6 / 6 / 0 | 6 / 6 / 0 | 6 / 6 / 0 | 5 / 5 / 0 | 4 / 3 / 0 |
| Collector pause per 10 s, ms | 109 | 79 | 95 | 86 | 40 |
| Slowest frame of the run, ms | 296 | 295 | 296 | 285 | 269 |
| Traffic in / out, kB/s | 38.9 / 1.9 | 39.7 / 1.9 | 37.4 / 1.9 | 38.1 / 1.9 | 39.1 / 1.9 |

Primitives per frame went up from run to run (4.2 to 5.2 million); no fix aimed at it, cause not looked at.

### Before and after: 11-20 baseline to batch 3

| | Baseline | Batch 3 |
|---|---|---|
| Nodes | 308,089 | 52,289 |
| Draw calls per frame | 2319 | 1790 |
| Process memory, MB | 4593 | 3948 |
| Allocation, MB/s | 8.2 | 4.5 |
| Collections per 10 s, gen1 | 6 | 3 |
| Collector pause per 10 s, ms | 79 | 40 |
| `net` scope, ms/s (own work only) | 2.2 | 2.6 |
| `net_events`, ms/s | 64.9 | 22.1 |
| `net_snapshot`, ms/s | 5.0 | 7.7 |
| `Shot`, us per event | 226 | 137 |
| `WorldSpawn`, us per event | 8897 | 534 |
| `Explosion`, us per event | 7900 | 2041 |
| `PelletFan`, us per event | 435 | 147 |
| `Launch`, us per event | 1423 | 741 |
| `players`, ms/s (ms per frame) | 36.8 (0.66) | 26.5 (0.90) |
| `vehicles`, ms/s (ms per frame) | 12.2 (0.22) | 10.8 (0.37) |
| all scopes, ms/s (ms per frame) | 163 (2.90) | 112 (3.82) |

Per frame the `players`, `vehicles` and all-scopes numbers are higher in batch 3, with half the fps: per-frame work is not cut by the
batches that touched it, and the ms/s fall is mostly the lower frame rate.

### Per batch

| | Baseline | Batch 1 | Batch 2 | Batch 3 |
|---|---|---|---|---|
| Draw calls | 2319 | 1865 | 1805 | 1790 |
| Nodes | 308,089 | 305,727 | 305,846 | 52,289 |
| Allocation, MB/s | 8.2 | 7.6 | 6.5 | 4.5 |
| `net_events`, ms/s | 64.9 | 63.5 | 30.7 | 22.1 |
| `Shot`, us per event | 226 | 326 | 220 | 137 |
| `WorldSpawn`, us per event | 8897 | 7140 | 667 | 534 |
| `Explosion`, us per event | 7900 | 14600 | 14009 | 2041 |
| `creatures` (animals + flocks), ms per frame | 0.37 | 0.38 | 0.50 | 0.62 |
| `creatures_animals`, ms per frame | - | - | 0.45 | 0.54 |

The client's scopes, ms per second of play (10 ms/s is 1 % of a core; 1 ms per frame is 1000 / fps ms/s), 11-20 baseline and batch 3 (the
2026-11-18 run had no split of `net`):

| Scope | 11-18 baseline | 11-20 baseline | Batch 3 | What it is |
|---|---|---|---|---|
| `net` | 71.4 | 2.2 | 2.6 | packets read and applied; from 11-20 the events and the snapshot are their own scopes |
| `net_events` | - | 64.9 | 22.1 | events handled (shots, hits, kills, spawns, blasts) |
| `net_snapshot` | - | 5.0 | 7.7 | snapshots decoded |
| `net_world` | - | 2.2 | 2.0 | world channel |
| `players` | 44.9 | 36.8 | 26.5 | remote players, corpses, ground items, care packages, turrets |
| `creatures` | 29.6 | 20.7 | 0.14 | batch 3: the part not in the four below |
| `creatures_animals` / `_flocks` | - | - | 16.0 / 2.4 | citizens and Gang under 0.1 each |
| `vehicles` | 16.4 | 12.2 | 10.8 | cars, harvester, aircraft |
| `predict` | 13.3 | 10.2 | 14.2 | the physics step: own input, prediction, sending |
| `view` | 8.3 | 5.8 | 5.6 | water, viewmodel, shake, dust, death |
| `projectiles` | 2.4 | 2.2 | 1.9 | |
| `hud` | 0.8 | 0.7 | 0.5 | |
| all | 187 | 163 | 112 | 3.6 / 2.9 / 3.8 ms of a frame; the rest of the frame is the engine and the card |

## What was done

Issue titles as on GitHub. Commits in `git log aa5f5a79..HEAD`.

| Batch | Issue | Result |
|---|---|---|
| 0 | #386 Client performance: finish measuring what costs GPU time and what is inside net | test on its own export, card state per run, per-event costs, baseline of `main` |
| 1 | #441 Client perf: merge static chunk meshes per material to cut draw calls (2,317 per frame) | draw calls 2319 to 1865; no GPU time gain |
| 1 | #443 Client perf: clutter grouping produces more nodes than intended (F-07) | stored clutter in 64 m cells, MultiMesh from 24 instances; 4,171 to 2,898 nodes |
| 1 | #444 Client perf: limit sun shadow distance and turn off shadow casting on small props and the viewmodel; #458 Client perf: redraw minimap and day-night parameters only on change | shadows to 150 m; minimap and day-night on change |
| 1 | #447 Client perf: opaque explosion particles and cheaper water blending | explosion fire and sparks opaque; water part not done |
| 1 | #448 Client perf: shared materials for characters instead of four per character | shared suit, gear, tuft materials (but see bugs) |
| 1 | #449 Client perf: reduce animation rate for far and off-screen characters; #450 Client perf: cheap corpses (static mesh after ragdoll settles) | 30/15/5 Hz far characters; settled corpses freeze |
| 1 | #451 Client perf: update far creatures and citizens at a lower rate | far creatures update every 2nd to 6th frame |
| 1 | #453 Client perf: pool explosion effects instead of building nodes per blast; #454 Client perf: remove per-shot tween and closure allocation in shot effects | blast kit animates itself; tracers in their own `_Process` |
| 2 | #455 Client perf: split the net scope and cut snapshot and event handling cost | `net` split; shots culled by range before lookups; direct handlers for Shot, PelletFan, WorldSpawn; pooled corpse views; characters built once; creature sub-scopes. `WorldSpawn` 8897 to 667 us per event |
| 2 | #451 Client perf: update far creatures and citizens at a lower rate | pacing without engine calls per creature (camera and planes once a frame, stride every 8th frame) |
| 3 | #442 Client perf: cut scene node count (240k) by merging clutter and props into per-chunk meshes | tree solids are shape owners of their body, not `CollisionShape3D` nodes: 305,846 to 52,289 nodes |
| 3 | #457 Client perf: prewarm shader and material variants at load | effects drawn once in a hidden viewport behind the loading screen |
| 3 | #453 Client perf: pool explosion effects instead of building nodes per blast | dust wave samples its ground over about 6 frames, not 1,089 rays in one; `Explosion` 14009 to 2041 us per event |
| 3 | #452 Client perf: remove per-frame allocations in the hot paths (7.8 MB/s, gen1 hitches) | decoders reuse dictionaries; name tag per remote; no `GetChildren` per frame; `Launch`, `Explosion` on the direct route. Allocation 6.5 to 4.5 MB/s, gen1 5 to 3 per 10 s |
| 3 | #455 Client perf: split the net scope and cut snapshot and event handling cost | muzzle node and flash kept per held weapon; `Shot` 220 to 137 us per event |

### Tried, no gain seen

- Far-creature pacing (#451 Client perf: update far creatures and citizens at a lower rate) and far-character animation rate (#449 Client perf:
  reduce animation rate for far and off-screen characters) do not show in their scopes. `creatures` is almost all animals, 0.45 ms per frame in
  batch 2. The engine's animation time is outside the scopes and cannot be separated while the card state changes.
- Merging chunk meshes (#441 Client perf: merge static chunk meshes per material to cut draw calls (2,317 per frame)) cut draw calls, not GPU time.
- `Shot` cost per event did not move until batch 3 (226, 326, 220, 137 us).

### Bugs found on the way

- The suit shader of #448 Client perf: shared materials for characters instead of four per character did not compile in batch 1; fixed in
  `d03c0016` (batch 2 onwards).
- The `!is_inside_tree()` error: 372 per run at baseline, 270 in batch 1, 0 from batch 2.

## What it shows now

- The frame is the engine's and the card's, not the scripts': scripts are 2.9 to 3.8 ms of a frame. Frame rate follows the card's power state.
- Scene weight: nodes 308k to 52k, draw calls 2319 to 1790, process memory 4.6 to 3.9 GB. Ground-object collision nodes (about 9k) stay: the
  nav bake reads them.
- Events were the costliest scope part (`net_events` 65 ms/s); now 22 ms/s. `WorldSpawn` and `Explosion` per event fell about 17x and 4x.
  `Shot` is now the largest event (137 us, about 117 per second).
- Allocation 8.2 to 4.5 MB/s; gen1 collections 6 to 3 per 10 s; every collection is still a gen1 and as long as a frame on an 880 MB heap.
- `players` is the largest per-frame scope (0.9 ms per frame), `animals` the next (0.54).
- Network: 30 snapshots a second, no reconciles, loss 0.03 to 0.09 %.

### Open

- Nobody has looked at the game since the changes: characters' suits, opaque explosion fire, far creatures and players, the dust wave.
- The slowest frame of a run is still about 270-295 ms; prewarm (#457 Client perf: prewarm shader and material variants at load) did not remove it.
- #447 Client perf: opaque explosion particles and cheaper water blending: the water part is not done.
- GPU cost breakdown: being measured separately on the offline route.

Not measured: another graphics card or settings, driving and flying, killstreaks, events, a real player's input, more than one human.

## GPU time on the offline route

GPU: see below, to be added. (Route runner: `perf/run_route.py`; switches added in `af14785a`.)
