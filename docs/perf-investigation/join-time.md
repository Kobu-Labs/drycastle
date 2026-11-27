# Join time

Measured 2026-11-27 on the dev laptop (16 cores, NVIDIA T1200 in render-offload mode), village map 0.15.0, a headless server and one
windowed client that joins it at once (`--connect`, so no time is spent in the menu). Runs of the old and the new code alternated, because
the machine was busy with other work (load 3 to 15). Bench: `DRYCASTLE_LOAD_TIMES=1 perf/join_bench.py OUT RUNS`; logs under
`out/perf/join-ab-*`.

| | before | after |
|---|---|---|
| launch to in game (client) | 43.2 s, 46.1 s | 15.0 s, 15.5 s |
| map load (client) | 35.7 s, 38.8 s | 10.5 s, 11.2 s |
| server start to listening | 13.2 s, 13.5 s | 6.4 s, 6.6 s |
| longest main-thread block of the map load | (one step per chunk, 0.1-0.3 s each, and frames between) | 1.0 s |
| map ready behind the menu (menu time after which a join waits only for the connection) | n/a | 22.2 s, 27.7 s after launch (machine load 6 to 18) |

The map now loads behind the main menu. The row "map ready behind the menu" is the menu time a player needs before the join waits
only for the connection (about 1 s). Measure it with `perf/join_bench.py OUT RUNS --menu` (the `SHOOTER_MAP_READY` line).

## Where the time went before

- 51 % was the loading screen waiting for frames: two frames after every 12 ms of work, 614 waits.
- Chunks: the glTF files parsed on the main thread (7.8 s), scenes built (5.1 s), every visual mesh read back from the GPU to count its
  triangles for a statistic (3.7 s), and the collision meshes uploaded to the GPU only to be read back for their triangles (0.9 s).
- crops.json (41 MB) parsed twice (once for the hay bales), and 1.5 million clutter and crop instances set with one call each.
- About 145,000 `CollisionShape3D` nodes for the trees' solid parts, half the scene tree.
- The terrain rim planned on the main thread from a 1 m grid (2 to 6 s on the server).

## What changed

- `MapLoader.Prefetch`: the chunk files are read on worker threads (half the cores, 2 to 8) into node trees outside the scene tree; the
  collision nodes get no mesh, their triangles come from the parsed arrays. The small JSON files, the pack-tree plan, trees.json,
  crops.json and clutter.json are parsed on workers too. LoadSteps keeps its order and only puts the results together; without a prefetch
  (viewer, tools) it does the same work itself.
- The models of placed objects, pack trees, vehicles, weapons and worn items are parsed on a worker while the chunks load
  (`MapLoader.PrefetchGltf` / `TryTakeGltf`; `ModelLib` and `CarFx` take them too).
- Triangle counts come from the manifest; clutter and crop cells set their MultiMesh buffer in one call; the trees' and ground objects'
  solids are shape owners of their chunk body, not nodes; the rim, the crop sheets and the water meshes are made on workers.
- The shooter starts the map load (data files, map, warm-up) when the main menu opens and puts it together in time slices: 10 ms a frame
  under the menu, 50 ms under the loading screen, 100 ms when the window is in the background. Vsync is off while the loading screen
  waits, and back to the player's setting after.

Tried and dropped: copying the objects' templates on a worker. It took 6 to 20 times as long.

## What is left (after, main thread unless marked)

| | time |
|---|---|
| frames drawn while loading (about 14 ms a frame on this laptop even idle, the render-offload display) | 2.7 s over 106 frames |
| placed objects: template copies (4,280) | 2.0 s |
| crops: meshes, cells | 1.6 s |
| chunks: colliders, materials, registration | 1.3 s |
| pack trees init (one step: the 1.0 s block) | 1.0 s |
| warm-up | 0.9 s |
| trees: shapes, bodies | 0.8 s |
| connect to welcome | 0.85 s |
| workers, CPU summed: chunk parse 7.6 s, chunk scenes 4.1 s, small glTF 2.6 s, crops 2.8 s, rim 2.4 s, trees 1.7 s | |

Next, by payoff: build the map in the pipeline into Godot's own formats (the chunk parse is most of the workers' time, which a 4-core
machine feels most, and most of the server's start), put repeated placed objects in MultiMeshes, split the pack-tree init, send the hello
while the map loads (the profile then loads on the server meanwhile; needs a ready message, protocol bump).
