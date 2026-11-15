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

One headless Godot server (30 Hz, real map collision from `godot/MapLoader`) and N headless bot clients, one OS process each, pinned with `taskset` (server 4 threads, clients 12). Bots are random walkers sending 30 inputs/s; the server is authoritative. Approach A is built-in (`MultiplayerSpawner` + `MultiplayerSynchronizer`, "always" mode, replicated `Tick`); approach B is a raw `ENetConnection` layer with 8-byte inputs and quantised delta snapshots (full entry on new entities and every 30 ticks, no acks). "-interest" variants use a 50 m radius. Each run: wait for all clients, 4 s warm-up, then a measured window (60 s built-in, 30 s custom). Server metrics come from engine timers, `/proc` and ENet counters; client-side age and divergence are joined against a per-tick server position history. Details are in `godot/netspike/` (`ServerCore.cs`, `Custom*.cs`, `BotInput`).

## Results

All runs connected N/N with 0 failures. N=32 sits between the two shown here (built-in 17.5 ms and 1.8 MB/s, custom 7.0 ms and 0.21 MB/s). Columns: "frame busy" is server main-thread time per frame (budget 33.3 ms); "srv up" is total over all clients.

### N = 16

| variant (window) | tick Hz | frame busy p50/p95/p99 ms | frames > 33 ms | tick work p50/p99 ms | srv CPU % | srv up kB/s | srv down kB/s | client down kB/s | client up kB/s | updates/s/entity | age p50/p95/p99 ms | stale p95 m | div p95/p99/max m |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| builtin (60 s) | 30.00 | 5.4/7.1/9.3 | 0 of 1800 | 0.92/1.72 | 13.3 | 459.9 | 13.4 | 28.75 | 0.84 | 30.0 | 11.6/18.1/19.3 | 0.000 | 0.0000/0.0000/0.000 |
| custom (30 s) | 30.00 | 3.7/9.7/17.4 | 0 of 901 | 1.23/2.61 | 8.4 | 58.4 | 9.1 | 3.65 | 0.57 | 26.9 | 10.9/16.3/16.8 | 0.005 | 0.0054/0.0059/0.007 |
| builtin-interest (60 s) | 30.00 | 3.7/4.7/10.0 | 0 of 1801 | 0.99/2.00 | 9.2 | 88.7 | 13.5 | 5.55 | 0.84 | 30.0 | 12.2/15.7/17.0 | 0.000 | 0.0000/0.0000/0.000 |
| custom-interest (30 s) | 30.01 | 3.9/7.2/9.0 | 0 of 901 | 1.38/3.45 | 9.3 | 16.2 | 9.2 | 1.01 | 0.57 | 24.6 | 10.5/16.1/16.7 | 0.005 | 0.0054/0.0059/0.007 |

### N = 64

| variant (window) | tick Hz | frame busy p50/p95/p99 ms | frames > 33 ms | tick work p50/p99 ms | srv CPU % | srv up kB/s | srv down kB/s | client down kB/s | client up kB/s | updates/s/entity | age p50/p95/p99 ms | stale p95 m | div p95/p99/max m |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| builtin (60 s) | 30.01 | 62.7/70.2/81.8 | 1012 of 1012 | 3.81/5.35 | 99.6 | 4096.9 | 53.6 | 64.03 | 0.84 | 16.8 | 36.1/61.2/67.7 | 0.169 | 0.0000/0.0000/0.000 |
| custom (30 s) | 30.00 | 7.7/28.6/32.7 | 0 of 901 | 3.87/6.40 | 17.5 | 817.9 | 36.4 | 12.79 | 0.57 | 26.7 | 9.6/16.5/17.4 | 0.005 | 0.0054/0.0060/0.525 |
| builtin-interest (60 s) | 30.00 | 21.2/27.4/34.7 | 30 of 1800 | 3.91/7.53 | 48.5 | 1435.3 | 54.4 | 22.43 | 0.85 | 30.0 | 17.0/23.2/26.0 | 0.000 | 0.0000/0.0000/0.000 |
| custom-interest (30 s) | 30.00 | 7.0/22.7/33.8 | 30 of 900 | 3.83/9.32 | 17.3 | 181.2 | 36.3 | 2.83 | 0.57 | 24.8 | 10.5/17.0/17.6 | 0.005 | 0.0054/0.0059/0.007 |

Notes:

- At N=64 built-in fell over its budget (17 Hz instead of 30; replication cost grows about N^2: 3.3, 13.7, 57 ms at 16, 32, 64). Custom stayed at 18 % CPU.
- Per entity update, built-in is about 60 bytes, custom about 8.5 bytes. Custom position error is 0.0054 m p95 (quantisation); a 0.5 m max appeared in two runs (a missed delta, fixed by the next keyframe).
- Interest management cut built-in upload by 65 % and custom by 78 % at N=64, and is what made built-in feasible at 64.
- About one frame per second of unexplained 22-60 ms hitches appears in some runs at N >= 32, outside our own code's p99. Not identified; needs understanding before shipping. It does not change the ranking.

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

## Limits

- Localhost only: no latency, jitter, loss or NAT (`tc netem` is unavailable without root). The custom layer's loss recovery (keyframe every 1 s, no acks) was not exercised under loss.
- Single run per configuration; custom rows are 30 s re-runs after a keyframe bug fix. Differences of a few percent are noise.
- Movement only: no shooting, hit detection, vehicles or game logic, so real tick cost will be higher. No prediction, interpolation or lag compensation was implemented or measured.
- A laptop ran the server and 64 clients together, so absolute numbers will differ on a dedicated box; ratios should hold. Built-in was measured in "always" mode only, because "on change" is broken (above).
- Not measured: long runs, more than 64 clients, an exported dedicated server, multithreaded physics, Windows.

## Reproducing

```sh
export DRYCASTLE_MAP=/path/to/out/village/map
make netspike-quick                  # about 4 minutes
make netspike                        # about 30 minutes: 4 variants x N=16,32,64 x ~75 s, plus the probe
python3 godot/netspike/run_spike.py --ns 64 --variants custom --seconds 30 --skip-probe
```

Results go to `out/netspike/<timestamp>/` (`summary.json` has everything; per-run folders hold the
server and client logs, JSON and sample files).
