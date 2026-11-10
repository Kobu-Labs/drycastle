# Netcode spike: 64 headless clients on the real map (issue #18)

Status: done. Gate for the shooter PoC (issues #7, #8). Code: `godot/netspike/`. Run it with
`make netspike` (full) or `make netspike-quick`. All numbers below come from `make netspike` runs on a laptop (Intel i7-11850H, 8 cores / 16 threads, Linux 7.0, Godot 4.7.2 mono,
.NET 8, Jolt); the built-in rows are 60 s runs, the custom rows are 30 s re-runs (see Limits). Raw output is not committed (`out/netspike/` is gitignored).

## Recommendation

**Use a thin custom snapshot layer over ENet for the shooter PoC, not Godot's built-in
MultiplayerSpawner/MultiplayerSynchronizer.** Short reasons, all measured here:

- **16 players (PoC target): both work.** Built-in needed 5.4 ms median frame time, 13 % of one
  core and about 29 kB/s per client down; custom needed 3.7 ms, 8 % and 3.7 kB/s. Nobody is in
  trouble at 16, so this is a choice about cost of ownership, not about feasibility.
- **Headroom to 64: only the custom layer has it.** Built-in cost grows with the square of the
  player count (frame time 5.4 -> 17.5 -> 62.7 ms at 16 -> 32 -> 64) and at 64 the server main
  thread saturated (100 % of a core, 17 frames/s instead of 30, snapshot age p95 61 ms). The custom
  layer stayed at 7.7 ms median / 18 % CPU at 64. Built-in with a 50 m interest radius also got
  through 64 (21 ms median, 49 % CPU) but with little headroom left for real game logic.
- **Bandwidth:** at 64 players built-in sent 4.1 MB/s from the server (about 33 Mbit/s, 64 kB/s
  per client), custom 0.82 MB/s (6.5 Mbit/s, 12.8 kB/s per client), custom with interest 0.18 MB/s.
- **Two real defects found in 4.7.2** (details below): `MultiplayerSynchronizer` in "on change"
  mode never replicates a change to the 65th or later watched property (issue #103938 reproduces),
  and "on change" mode did not work at all for spawned players in this setup (client error spam, no
  updates). Always mode works, but it sends every property every frame.
- A shooter needs tick-stamped snapshots (interpolation, lag compensation, input acks). The
  built-in synchronizer has no notion of a tick; we had to replicate our own `Tick` property to
  measure age. The custom layer has the tick and the last processed input in every snapshot header.

Cost of the custom route: about 350 lines (`CustomWire.cs`, `CustomServer.cs`, `CustomClient.cs`
here) that we own and maintain, plus our own reliable message channel for events (spawn, hit, chat,
match state). It bypasses `SceneMultiplayer`, so no `@rpc` on that connection. Recommended shape for
the PoC: keep `ServerCore`-style simulation in plain Godot nodes, take the wire code from this spike,
add a small reliable message API on channel 0, and add interpolation on clients.

If the PoC wants to move faster at first, built-in would carry 16 players, but expect to replace it
before going beyond roughly 32.

## Method

One headless Godot server and N headless bot clients, **one OS process per client** (64 processes for
N=64; about 165 MB RSS each, about 11 GB total, fine on 62 GB). `taskset` pins the server to 4 CPU
threads and the clients to the other 12, so the server is not starved by its own clients.

- **Server** (`ServerCore.cs` plus one approach class): loads the real map with the shared
  `godot/MapLoader` (4 chunks, 58 trimesh colliders, the slice from the pipeline), 30 Hz physics
  tick, Godot frame rate capped at 30. Every player is a `CharacterBody3D` with a capsule on layer
  2 that collides with the world only (not with each other), gravity, jump, walking on the terrain
  and buildings. Players that walk off the edge of the slice are respawned at a random spawn point
  (about 1 per player per minute at this map size).
- **Bots** (`BotInput`): random walkers, new heading every 1 to 3.5 s, sometimes standing still,
  a jump every 2.5 to 5 s. They send 30 inputs/s (move axes, yaw, jump, sequence). They do not look
  at the world; the server is authoritative.
- **Approach A, built-in**: `ENetMultiplayerPeer`, a `MultiplayerSpawner` with a spawn function, a
  `MultiplayerSynchronizer` per player replicating `position`, `rotation`, `velocity` and our `Tick`
  (spawn properties set, mode "always"). Input goes as an unreliable RPC on its own channel. Interest
  management (variant "-interest") uses per-peer synchronizer visibility (`SetVisibilityFor`,
  recomputed every 8 ticks); the spawner despawns nodes that become invisible for a peer.
- **Approach B, custom**: raw `ENetConnection`, two channels. Input is 8 bytes, unreliable-sequenced.
  Snapshots are unreliable-sequenced: 8-byte header (type, tick, last input seq, count) plus per entity
  an id, a flags byte and only the changed fields: position as 3 x u16 in 1/128 m units (or 3 x i8
  delta against the last state sent to that client), yaw u16, velocity 3 x i8 (1/8 m/s). A full entry
  is sent for new/re-entering entities and every 30 ticks (loss recovery; there is no ack-based
  delta). Interest management ("-interest"): entities beyond the radius are not sent, and get a full
  entry when they come back. Snapshots are kept under 1200 bytes so ENet never fragments them.
  Reliable channel: welcome (id, quantisation bounds), go, stop.
- **Interest radius** 50 m in both interest variants. The terrain mesh is about 260 m wide, so a
  player sees roughly a fifth of the others.
- **Timeline** per run: server waits until all N clients joined, 4 s warm-up, then **60 s measured**
  (the server sends "go"/"stop"; clients record only inside that window). Each variant at each N is
  one run (no repeats, see limits).
- **Measurements.** Server: `frame busy` = engine `TIME_PROCESS + TIME_PHYSICS_PROCESS` per frame
  (this includes the physics step, our code and the multiplayer poll/replication, i.e. everything the
  server main thread did); `tick work` = stopwatch around our own per-tick code (simulation and, for
  B, snapshot build and send; it excludes replication done by the engine in approach A); CPU % from
  `/proc/self/stat` over the window (100 % = one core); bandwidth from the ENet host counters
  (`PopStatistic`, includes ENet protocol headers, excludes 28 bytes of UDP/IP per packet) on the
  server for totals and on each client for its own up/down. Clients: every 8th received entity
  update is written to a file as (server tick, entity id, position, local receive time). The harness
  joins these with a per-tick position history the server writes, giving
  - **age** = client receive time minus the time the server simulated that tick (client and server
    share the host clock). Includes the client's 60 Hz frame cadence (up to 16.7 ms of waiting for the
    next poll).
  - **divergence** = distance between the received position and the server's position at that tick.
  - **staleness** = distance between the received position and where that player is on the server
    at the moment of receipt (what a client without interpolation would be wrong by).
- **Dropped/failed connections**: clients that never got a result file or never connected, early
  server disconnects, server-side disconnects during the window.

## Results

All runs: every client connected (N/N), 0 connection failures, 0 disconnects, 0 samples that could
not be matched to server history. Tick rate is the achieved physics rate in the window.
Columns: "frame busy" is the server main thread time per frame (budget 33.3 ms), "srv up/down" is the
total over all clients, "client down/up" the mean per client.

### N = 16

| variant (window) | tick Hz | frame busy p50/p95/p99 ms | frames > 33 ms | tick work p50/p99 ms | srv CPU % | srv up kB/s | srv down kB/s | client down kB/s | client up kB/s | updates/s/entity | age p50/p95/p99 ms | stale p95 m | div p95/p99/max m |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| builtin (60 s) | 30.00 | 5.4/7.1/9.3 | 0 of 1800 | 0.92/1.72 | 13.3 | 459.9 | 13.4 | 28.75 | 0.84 | 30.0 | 11.6/18.1/19.3 | 0.000 | 0.0000/0.0000/0.000 |
| custom (30 s) | 30.00 | 3.7/9.7/17.4 | 0 of 901 | 1.23/2.61 | 8.4 | 58.4 | 9.1 | 3.65 | 0.57 | 26.9 | 10.9/16.3/16.8 | 0.005 | 0.0054/0.0059/0.007 |
| builtin-interest (60 s) | 30.00 | 3.7/4.7/10.0 | 0 of 1801 | 0.99/2.00 | 9.2 | 88.7 | 13.5 | 5.55 | 0.84 | 30.0 | 12.2/15.7/17.0 | 0.000 | 0.0000/0.0000/0.000 |
| custom-interest (30 s) | 30.01 | 3.9/7.2/9.0 | 0 of 901 | 1.38/3.45 | 9.3 | 16.2 | 9.2 | 1.01 | 0.57 | 24.6 | 10.5/16.1/16.7 | 0.005 | 0.0054/0.0059/0.007 |

### N = 32

| variant (window) | tick Hz | frame busy p50/p95/p99 ms | frames > 33 ms | tick work p50/p99 ms | srv CPU % | srv up kB/s | srv down kB/s | client down kB/s | client up kB/s | updates/s/entity | age p50/p95/p99 ms | stale p95 m | div p95/p99/max m |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| builtin (60 s) | 30.00 | 17.5/22.9/25.8 | 0 of 1801 | 2.01/3.23 | 43.5 | 1837.0 | 26.9 | 57.41 | 0.84 | 30.0 | 16.1/27.3/29.0 | 0.000 | 0.0000/0.0000/0.000 |
| custom (30 s) | 30.01 | 7.0/22.1/59.9 | 30 of 901 | 2.49/5.42 | 14.4 | 208.2 | 18.3 | 6.51 | 0.57 | 25.8 | 10.3/16.5/17.1 | 0.005 | 0.0054/0.0062/0.559 |
| builtin-interest (60 s) | 30.00 | 7.2/9.6/14.1 | 0 of 1801 | 1.98/3.43 | 16.8 | 338.1 | 27.0 | 10.57 | 0.84 | 29.9 | 10.7/19.2/20.1 | 0.000 | 0.0000/0.0000/0.000 |
| custom-interest (30 s) | 30.01 | 6.7/14.6/22.0 | 0 of 901 | 2.32/5.56 | 14.0 | 49.1 | 18.3 | 1.53 | 0.57 | 25.0 | 8.5/16.3/17.1 | 0.005 | 0.0054/0.0059/0.006 |

### N = 64

| variant (window) | tick Hz | frame busy p50/p95/p99 ms | frames > 33 ms | tick work p50/p99 ms | srv CPU % | srv up kB/s | srv down kB/s | client down kB/s | client up kB/s | updates/s/entity | age p50/p95/p99 ms | stale p95 m | div p95/p99/max m |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| builtin (60 s) | 30.01 | 62.7/70.2/81.8 | 1012 of 1012 | 3.81/5.35 | 99.6 | 4096.9 | 53.6 | 64.03 | 0.84 | 16.8 | 36.1/61.2/67.7 | 0.169 | 0.0000/0.0000/0.000 |
| custom (30 s) | 30.00 | 7.7/28.6/32.7 | 0 of 901 | 3.87/6.40 | 17.5 | 817.9 | 36.4 | 12.79 | 0.57 | 26.7 | 9.6/16.5/17.4 | 0.005 | 0.0054/0.0060/0.525 |
| builtin-interest (60 s) | 30.00 | 21.2/27.4/34.7 | 30 of 1800 | 3.91/7.53 | 48.5 | 1435.3 | 54.4 | 22.43 | 0.85 | 30.0 | 17.0/23.2/26.0 | 0.000 | 0.0000/0.0000/0.000 |
| custom-interest (30 s) | 30.00 | 7.0/22.7/33.8 | 30 of 900 | 3.83/9.32 | 17.3 | 181.2 | 36.3 | 2.83 | 0.57 | 24.8 | 10.5/17.0/17.6 | 0.005 | 0.0054/0.0059/0.007 |

Reading the tables:

- **Bandwidth per entity update** (N=16): built-in about 60 bytes, custom about 8.5 bytes. Part of the
  built-in cost is that "always" mode sends every property every frame with Variant encoding and node
  path/cache overhead; ours sends only changed fields and quantises.
- **Built-in at N=64 fell over its frame budget**: 1012 frames in 60 s instead of 1800 (17 Hz),
  all of them longer than 33 ms. The physics tick still ran at 30 Hz because the engine catches up
  with several steps per frame, but replication happens once per frame, so entities updated only
  16.8 times per second per client and age/staleness grew. The time is in the replication phase
  (frame process time 57 ms of 63 ms median; physics 5.7 ms), which scales as roughly N^2
  (3.3 -> 13.7 -> 57 ms at 16 -> 32 -> 64).
- **Custom rate is 25 to 27 updates/s/entity** instead of 30 because unchanged entities (standing
  bots) are skipped; at N=64 (30 s) the server sent 3.4 M delta entries and 0.6 M full entries (new entities plus the 1 s keyframes).
- **Client CPU**: built-in clients used 5.9 to 7.7 % of a core each (scene replication on the
  client), custom clients 0.9 to 1.0 %.
- **Divergence** is zero for built-in (positions are exact floats) and 0.0054 m (p95) for custom, i.e. the
  1/128 m quantisation. The custom max was 0.5 m in two runs (a missed delta on the unreliable channel,
  corrected by the next keyframe); with the unreliable channel a few such events are expected even
  on localhost. No desync or missing entities were observed in any run; with interest
  management the average number of visible entities was 2.9 / 5.7 / 12.4 (built-in) and 2.7 / 5.4 /
  12.9 (custom, counted over a 1 s window, so it includes entities just outside the radius) at
  N = 16 / 32 / 64.
- **Staleness p99/max** are not in the tables on purpose: they are dominated by the out-of-bounds
  respawn teleports (a teleported player is far from its last received position for up to one
  tick), not by sync quality.
- **Interest management** helped both: built-in total upload dropped by 65 % at N=64 and frame time
  by 66 %; custom upload by 78 %. For built-in it is also what made 64 feasible.
- **Unexplained hitches.** About one frame per second (30 frames over 33 ms per run in the affected runs, p99 of 22 to 60 ms) shows up in some custom runs at N >= 32 and in built-in-interest at N=64, while the
  per-tick work of our own code stays at p99 below 8 ms. It does not scale with N and is
  not in our code's p99, so it is probably engine/runtime noise (GC settings were tried: no clear
  effect), but it was not identified. A real server needs this understood before shipping. It does
  not change the ranking.

## The 64-watched-property issue (godotengine/godot#103938)

`ProbeRole` (`Probe.cs`): a server and a client share a node with 160 float properties; K of them are
watched by S synchronizers; the server changes watched properties each tick; the client counts which
ones actually change (at least 10 changes in 6 s). Godot 4.7.2 mono, headless, Linux.

| watched K | synchronizers | mode | which properties change on the server | updating on client |
|---|---|---|---|---|
| 32 / 63 / 64 | 1 | always | all | all (32 / 63 / 64) |
| 65 / 80 / 128 | 1 | always | all | all |
| 128 | 2 / 4 | always | all | all |
| 128 | 1 | always | only 64..127 | all 64 |
| 65 | 1 | on change | all | all |
| 128 | 1 | on change | all | all |
| **65** | 1 | on change | **only #64 (the 65th)** | **0** |
| **128** | 1 | on change | **only 64..127** | **0** |
| **128** | 1 | on change | **only #127** | **0** |
| 64 | 1 | on change | only #63 (the 64th) | 1 (works) |

**The issue reproduces in a specific form on 4.7.2.** "Always" mode is not affected: all 128
properties replicate. In "on change" mode, a change to a property with index 64 or higher is never
replicated unless a property among the first 64 changes in the same frame (in which case the later
ones travel along). That fits a 64-bit change mask. It also means the limit is silent: nothing is
logged. I did not look at the engine source; this is the observed behaviour only.
What it means for us: spawned players with `position`, `rotation`, `velocity`, `Tick` are far below
the limit. A player with health, ammo, weapon state and similar could approach it, and the failure
is silent; splitting over several synchronizers is not a safe workaround in on-change mode (not
tested beyond one on-change synchronizer). I did not test the editor or other sync modes beyond the two
above.

Separate finding: **on-change mode failed for the real player setup** (4 properties, spawned by the
spawner, spawn properties set): the server ran, the client logged repeated
`Condition "props.is_empty()" is true ... on_delta_receive (modules/multiplayer/scene_replication_interface.cpp:784)`
and received 0 updates. I tried it with and without spawn properties and with and without the `Tick`
property and got the same result, then used "always" mode for all measurements. I did not find the cause.
This is a second reason the built-in numbers are a lower bound on what built-in costs once an
on-change mode is needed to save bandwidth.

## Limits of this test (be careful with the numbers)

- **Localhost, no latency, no loss, no jitter.** Ages of 6 to 36 ms are mostly the 30 Hz tick and the
  client's 60 Hz frame cadence, not a network. `tc netem` was tried and does not work without root:
  `unshare -Urn` gives an unprivileged network namespace, but `tc qdisc add ... netem` fails with
  "Specified qdisc kind is unknown" (the kernel has no usable `sch_netem` here). **Not tested:** latency,
  jitter, packet loss, bandwidth caps, packet reordering, NAT. In particular, the custom layer's
  loss recovery (full entry every 1 s, no acks) was not exercised under loss.
- **Windows differ**: built-in rows are 60 s runs. The custom rows are 30 s runs, because I found a bug in
  the custom layer's keyframe interval after the first full run (keyframes were never sent for moving
  entities, so loss was never corrected) and re-ran only the custom variants, shorter, for lack of time.
  The first custom numbers (no keyframes) were about 15 % lower in bandwidth and otherwise similar.
- **Single run per configuration**, no repeats, no confidence intervals; differences of a few
  percent are noise. The hitches above show that single frames vary a lot.
- **Workload is only movement.** No shooting, no hit detection, no projectiles, no player-player
  collision, no game logic, no vehicles, no navigation; real server tick cost will be higher than
  `tick work` here. Bots are random walkers, so movement is not like human play (stationary or
  heavily changing inputs would shift the delta compression ratio).
- **Machine**: a laptop with 8 physical cores (16 threads) running the server and 64 clients
  together. The server was pinned to 4 threads, but clients and server share caches, memory bandwidth
  and boost clocks, so absolute numbers (not the ratios) will differ on a dedicated server box. The
  Godot server main thread is effectively single-threaded for this workload (99.6 % CPU at 64
  in built-in), so extra cores on the server do not help.
- **Approach A is measured in "always" mode only**, with a replicated `Tick` property that adds
  bytes; "on change" is broken (above), so A's bandwidth is higher than a working on-change mode would
  produce for standing players. A's input path is an unreliable RPC; a client-authority input
  synchronizer would be a different design and was not measured.
- **Client frame rate is capped at 60 Hz, server at 30 Hz**; at 30 Hz server frames, input may wait up
  to a frame before being polled. Ages include these cadences.
- **Bandwidth excludes UDP/IP headers** (28 bytes per packet; about 54 kB/s extra at N=64 for custom,
  about 7 % of its total, less for built-in). No ENet compression was used in either approach.
- **Samples**: every 8th update per entity per client was recorded for age/divergence, with
  no unmatched samples. Respawn teleports are in the data.
- No client-side prediction, interpolation or lag compensation were implemented or measured.
- Interest management is a plain 50 m radius; no hysteresis, no priority/rate tiers.

## What was not measured

Network latency or loss effects; server memory growth over long runs (peak RSS was about 200 to 230
MB at all N for 60 s); runs longer than 60 s; more than 64 clients; a dedicated-server export
(`--headless` on the editor binary was used, not an export template); multithreaded physics; Windows.

## Reproducing

```sh
export SUCHOHRAD_MAP=/path/to/out/map
make netspike-quick                  # about 4 minutes
make netspike                        # about 30 minutes: 4 variants x N=16,32,64 x ~75 s, plus the probe
python3 godot/netspike/run_spike.py --ns 64 --variants custom --seconds 30 --skip-probe
```

Results go to `out/netspike/<timestamp>/` (`summary.json` has everything; per-run folders hold the
server and client logs, JSON and sample files).
