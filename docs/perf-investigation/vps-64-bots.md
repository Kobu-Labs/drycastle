# Server load on the VPS: 64 bots (November 2026)

What the live server costs with 64 bots and one human in, measured on the VPS itself, before and after three rounds of fixes.
The test is repeatable (`perf/vps_bots_test.sh`); the raw logs of every run are in `vps-logs/`, so a later run can be laid next to them.

## The machine and the load

- VPS: 2 vCPU AMD EPYC-Rome (KVM), 3921 MB RAM, no swap, Debian 13, Docker compose (`deploy/`). Server container about 2.1 GB RSS.
- Map `0.14.0-village` (145 chunks, 723 spawns), 30 vehicles, 147 wild animals + 15 flocks, pets. 30 Hz tick: budget 33.3 ms.
- Load: `--bots 64` and one headless walker client (bots only play while a human is in). Free-for-all, the bots fight all the time.
- Not in the load: real players (their lag compensation, their snapshots), anyone driving or flying, killstreaks, a Bigfoot / Gang / Scud event.

## How to run it

```
perf/vps_bots_test.sh <label>                 # restarts the live server twice: only when nobody plays
perf/vps_summary.py docs/perf-investigation/vps-logs/*.log.gz out/perf/vps/<stamp>-<label>.log
```

The script: writes a compose override with `--bots 64`, restarts, waits for `server: bot navigation from`, runs the launcher-installed
client as a walker for 330 s (`PLAY_SECONDS`), saves the `server: stats` / `server: prof` lines, removes the override and restarts.
It refuses when somebody plays or an override is already there. A run with no kill-feed lines on the walker is void (the bots did not play).

`vps_summary.py` prints the median over the 10 s windows of each run (the first 3 windows left out). The lines are described in
`docs/shooter-server.md`, "Performance log".

Trying a build that is not released: export it (`godot --headless --path godot/shooter --export-release "Linux Server"`), copy
`shooter.dll`, `ShooterCore.dll` (and `MapLoader.dll` when it changed) from `out/shooter/linux-server/data_shooter_linuxbsd_x86_64/` to the
same folder under `/opt/drycastle/out/shooter/linux-server/` on the VPS (keep the old ones; `chown 1001:1001`), then
`docker compose up -d --build`. The next CI deploy puts the released ones back.

Function-level profile: see "Performance log" in `docs/shooter-server.md` (`perf record`; it slows the server, so its windows are not comparable).

Traps met:
- The walker must be the protocol version of the server (the launcher's client is; a local build of another branch may not be).
- `pkill -f "name perf-walker"` kills the shell that runs it; use a pattern that does not match itself (`loss-bot walke[r]`).
- A local `godot --path` server is a Debug build: its C# costs are overstated. Measure the export.
- The walker client may crash on exit (after the play); the run is still good.

## Results

Medians over the 10 s windows. ms/s is milliseconds of the game thread per second of play: 10 ms/s is 1 % of a core, and the tick has 1000.
Scopes nest (`entities` contains `animal_step`, which contains `animal_body_ahead`, which contains `animal_crowd`).

| | Baseline | Round 1 | Round 2 | Round 3 |
|---|---|---|---|---|
| Log in `vps-logs/` | `2026-11-17-0-baseline` | `...-1-nav-targets-misscheck` | `...-2-animals` | `2026-11-18-3-crowd-gc-rays` |
| Commit (`perf-fixes-1`) | `a6f6605e` | `de367a2a` | `ddd9ee42` | `58cbbda9` |
| Windows | 12 (see note) | 33 | 34 | 25 |
| Tick p50, ms | 15.6 | 15.7 | 10.6 | 10.0 |
| Tick p99, ms | 29.1 | 30.8 | 23.2 | 16.7 |
| Overrun ticks per 10 s | 1.8 | 2.6 | 0.85 | 0 |
| Slowest tick of 10 s, median, ms | 39.5 | 42.1 | 35.7 | 23.8 |
| Slowest tick of the run, ms | - | 111.6 | 92.9 | 29.6 |
| CPU, % of one core: all threads | 90.6 | 65.8 | 54.2 | 51.1 |
| CPU: game thread | 49.2 | 50.0 | 35.4 | 32.9 |
| CPU: bot navigation thread | 38.8 | 16.5 | 17.2 | 15.9 |
| Allocation, MB/s | 11.6 | 2.56 | 2.67 | 1.39 |
| GC pause per 10 s, ms | 67 | 41 | 46 | 21 |
| Collections per 10 s | 14 | 3 | 3 | 13 |
| One collection's pause, ms | - | 13-30 | 13-30 | 1.5 |
| Objects waiting for the finalizer per collection | - | - | 4763 (round 3, before the ray fix) | 73 |

Note on the baseline: its log has six windows with `perf record` attached (tick p50 27-32 ms, over 100 overruns each). The baseline column is
the 12 windows without it; `vps_summary.py` over the whole file gives worse numbers (p50 16.6, p99 41.7), and so are its scope rows below.

Scopes, ms/s (rows of 3 ms/s or more):

| Scope | Baseline | Round 1 | Round 2 | Round 3 |
|---|---|---|---|---|
| `entities` (animals, citizens, pets) | 213.8 | 206.3 | 58.9 | 44.0 |
| `animal_step` | 165.4 | 155.2 | 49.5 | 33.6 |
| `animal_body_ahead` | 101.3 | 89.3 | 28.5 | 6.7 |
| `animal_crowd` | 65.0 | 58.0 | 25.1 | 3.0 |
| `animal_ground` | 41.8 | 40.2 | 8.9 | 9.5 |
| `animal_players_near` | 32.1 | 28.0 | 0.9 | 1.0 |
| `animal_threat` | 33.6 | 33.7 | 0.9 | 0.9 |
| `animal_solid` | 20.9 | 22.5 | 9.7 | 13.6 |
| `player_move` | 143.5 | 131.8 | 122.3 | 114.3 |
| `move_slide` (Godot `MoveAndSlide`) | - | 75.0 | 70.2 | 72.7 |
| `move_step_up` | - | 30.3 | 30.3 | 28.7 |
| `move_bodies` (player against standing animals) | - | 14.7 | 10.5 | 1.3 |
| `move_logic` | - | 5.9 | 6.0 | 6.7 |
| `fire` | 90.8 | 60.2 | 54.7 | 64.1 |
| `shot_targets` | 29.1 | 19.5 | 16.7 | 23.8 |
| `live_targets` | 30.5 | 22.2 | 21.8 | 27.0 |
| `targets_animals` (inside `live_targets`) | - | - | - | 20.6 |
| `targets_players` (inside `live_targets`) | - | - | - | 6.1 |
| `fire_raycast` | - | 15.1 | 11.9 | 18.0 |
| `fire_pellets` | - | 13.5 | 16.7 | 7.5 |
| `fire_bullet_hit` | - | 6.6 | 5.4 | 7.4 |
| `bot_brain` | 26.2 | 23.6 | 23.4 | 25.7 |
| `bot_sight` | 18.2 | 15.5 | 15.2 | 17.1 |
| `world_shot` | 18.8 | 16.4 | 16.8 | 18.1 |
| `physics_ray` | 24.8 | 23.4 | 17.9 | 21.7 |
| `nearest_hull` | 10.0 | 8.8 | 9.2 | 10.1 |
| `vehicle_step` | 7.5 | 7.6 | 9.8 | 3.9 |
| `match_update` | 5.7 | 5.7 | 6.2 | 5.5 |

Allocation of the game thread by tick phase, kB/s:

| Phase | Round 1 | Round 2 | Round 3 |
|---|---|---|---|
| snapshots | 916 | 1015 | 348 |
| match | 477 | 466 | 126 |
| vehicles | 413 | 465 | 192 |
| simclients | 259 | 250 | 264 |
| bots | 240 | 251 | 212 |
| detonate | 79 | 83 | 77 |
| corpses | 67 | 69 | 68 |

Navigation: a route cost 23.5 ms before round 1 and 10.0 ms after it.

Run-to-run noise: how much the bots fight differs between runs (`fire`, `physics_ray`, `fire_pellets` move by 10-20 % with no change behind
them). A difference under about 10 % in one scope is not a finding.

## What was found, and what was done

Round 1 (`de367a2a`):
- A bot route snapped both ends to the mesh before the search (`MapGetClosestPoint` twice); the search does it itself. Same routes, 60 % of the navigation thread.
- Every shot built a new list of every target and left it to the collector: 80 % of all allocation. It goes back to its pool.
- A missed shot was traced a second time for a counter only the weapons smoke test reads. Now only under that test.

Round 2 (`ddd9ee42`):
- Every animal, every tick, walked the list of all players three times (near, threat, watched), each read a call into the engine. The players are read once per tick; the copy is dropped when a player is hurt or teleported.
- The terrain grid's keys (`long` of two cell numbers) hashed to cx XOR cz: a lookup walked a chain of about a hundred cells. A multiplicative hash.
- The crowd test did its cheap distance test last.

Round 3 (`58cbbda9`):
- The crowd (animal against animal, player against standing animal) asked every animal. A grid of 4 m cells kept as they move; same answers, same order (test).
- A collection paused 13-30 ms, and nearly every slow tick had one. Cause: about 4800 objects per collection waited for the finalizer, which makes
  each one survive a collection: Godot's ray answers (a dictionary, and a variant per string key, 1300 rays a second) and a new query object per
  vehicle ground ray. They are freed at once and the keys are made once: 73 per collection.
- `DOTNET_GCgen0size=100000` (1 MB, in the Dockerfile): more collections, each short. 64 MB was tried: fewer collections of 33-224 ms each, worse.
- The history a snapshot is diffed against made a dictionary of every entity per tick, alive for two seconds. They are reused.
- A respawn compared 723 spawn points with 64 players through LINQ (1-2 ms each; a round start does 65). Plain loops, same choice (test).
- The bots' navigation was baked again (47 s, the server answers nobody) after every new image, because its cache was inside the container. A volume: 0.3 s.

## What is left

By cost, at 64 bots:
- `player_move` 114 ms/s: Godot's `MoveAndSlide` (73) and the step-up probe (29). No change found that leaves the movement identical.
- `fire` 64 ms/s. `targets_animals` 21: a hit target is made for every living animal for every shot. A per-tick copy would be valid only
  while no animal changes in the tick (a hit, a blast, a run-over, a round start); not done, because that was not proven.
- `animal_step` 34 ms/s, of which ground 9.5, solid 13.6.
- `bot_brain` 26 ms/s, of which sight rays 17.
- Bot navigation thread: 16 % of a core. Not looked at: how often a bot asks again (`RepathS`).
- Allocation: snapshots 348 kB/s per connected client (it grows with real players), simclients 264, bots 212, vehicles 192.

Stalls seen once each, not fixed: a round start (89 ms: the world is made again), an event starting (42 ms), one full (gen2) collection (102 ms).

Not measured at all: real players, vehicles in use, killstreaks, events running, more than one human.
