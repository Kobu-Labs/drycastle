# Client load: one client in a fight of 64 bots (November 2026)

What a frame of the client costs in a full fight: a real client with a window plays by script against 64 bots on the VPS, and reports the same
numbers every player's game reports to the dashboard (`docs/monitoring.md`, "Clients"). The test is repeatable (`perf/vps_client_test.sh`); the
log of every run kept is in `client-logs/`, so a later run can be laid next to it. The server's side of the same fight: `vps-64-bots.md`.

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

The script exports this checkout's client when there is none (`EXPORT=1` to do it again), borrows the map and assets of the launcher's install
(the map must be the server's build), restarts the server with the bots, runs the client for 330 s (`PLAY_SECONDS`; `RES`, `GPU=intel`, `BOTS`),
keeps the `client: perf` lines, and puts the server back. It refuses when somebody plays or an override file is already on the VPS.
`client_summary.py` prints the median over the 10 s windows of each run (the first 3 left out: joining, shaders, the first spawn).

The server must know the client's protocol version (an export of another branch may not match the live server). A server without
`EventKind.ClientReport` still gives a valid run: the log is the client's own; only the dashboard stays empty.

Traps met:
- The client aborts when it quits (after the play, in the engine's renderer teardown); the run is good when the log has its windows.
- The engine's error output lands in the middle of log lines; the summary reads `name=number` pairs wherever they are.
- Without `--perf-play` reaching the client the frame cap of an unfocused window (20 fps) is all the run records: check `fps` is not 20.
- The engine's "process time" counter is the worst frame of each second, not the mean (`process_peak_ms`).

## Results

Medians over the 10 s windows.

| | Baseline |
|---|---|
| Log in `client-logs/` | `2026-11-18-0-baseline` |
| Commit | `77afa0e5` + the client report |
| Windows | 29 |
| Frames per second | 52.1 |
| Frame time p50, ms | 18.3 |
| Frame time p99, ms | 36.8 |
| Slowest frame of 10 s, median, ms | 57.2 |
| Slowest frame of the run, ms | 296 |
| Frames over 33 / 50 / 100 ms per 10 s | 6 / 1 / 0 |
| Render time per frame: processor / graphics card, ms | 4.0 / 10.9 |
| Draw calls per frame | 2317 |
| Primitives per frame, thousands | 3510 |
| Nodes in the scene | 240,757 |
| Process memory, MB | 4256 |
| C# heap, MB | 847 |
| Video memory, MB | 1869 |
| Allocation, MB/s | 7.8 |
| Collections per 10 s (gen0 / gen1 / gen2) | 6 / 6 / 0 |
| Collector pause per 10 s, ms | 109 |
| Traffic in / out, kB/s | 38.9 / 1.9 |
| Reconciles per 10 s | 0 |

The client's scopes, ms per second of play (10 ms/s is 1 % of a core; at 52 fps a frame has 19.2 ms and 1 ms/frame is 52 ms/s):

| Scope | Baseline | What it is |
|---|---|---|
| `net` | 71.4 | packets read and applied: snapshots decoded, events handled (shots, hits, kills) |
| `players` | 44.9 | remote players, corpses, ground items, care packages, turrets |
| `creatures` | 29.6 | animals, flocks, citizens, Gang, Bigfoot, demon |
| `vehicles` | 16.4 | cars, harvester, aircraft |
| `predict` | 13.3 | the physics step: own input, prediction, sending |
| `view` | 8.3 | water, viewmodel, shake, dust, death |
| `projectiles` | 2.4 | |
| `hud` | 0.8 | |
| `reconcile` | 0.0 | |
| all | 187 | 3.6 ms of a frame; the rest of the frame is the engine |

## What it shows (nothing fixed yet)

- The frame is the engine's, not the scripts': 18.3 ms a frame, of which the scripts above are 3.6 ms and the graphics card 10.9 ms. 2317 draw
  calls and 3.5 million primitives per frame at the default settings on this card.
- Every collection is a gen1 (6 of 6 per 10 s), 18 ms each on average, on a heap of 850 MB: a collection is as long as a frame, so each is a
  visible hitch (the 6 frames over 33 ms per 10 s). Allocation is 7.8 MB/s, five times the server's with 64 bots.
- `net` is the costliest scope (71 ms/s) with one client receiving 39 kB/s: what in it (decoding, or the effects of shots and hits) is not split yet.
- `OnWeaponEvent` asks a node that is not in the scene for its position 340 times in the run (`!is_inside_tree()`); each prints an error with a
  backtrace. A bug, and a cost.
- 240,000 nodes in the scene, 4.3 GB of process memory.
- The slowest frame of the run was 296 ms (one, in the windows kept).
- Network: 30 snapshots a second, longest gap 99 ms, no reconciles, loss 0.09 %.

Not measured: another graphics card or settings (MSAA, shadows: `perf/run_route.py` does those, offline), driving and flying, killstreaks, events,
a real player's input, more than one human.
