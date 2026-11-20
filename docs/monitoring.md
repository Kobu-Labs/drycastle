# Monitoring the server and the clients (Grafana on the VPS)

The game server sends its own numbers to a small metrics store on the same VPS every 10 s; Grafana, also there, draws them. Nothing leaves
the machine and nothing listens on the internet. The players' games report through the server ("Clients" below). Three compose services (`deploy/docker-compose.yml`), each with a memory cap, none needed by the game:

| Service | What | Memory used / cap (measured 2026-11-18) |
|---|---|---|
| `metrics` | VictoriaMetrics, one process: keeps the series 90 days in the `metricsdata` volume | 40 MB / 192 MB |
| `node` | node-exporter: the host's CPU (with steal), memory, disk, load | 23 MB / 48 MB |
| `grafana` | the dashboards, on the VPS's loopback port 3000 only | 168 MB / 256 MB |

The VPS has 3.9 GB and no swap, and the game server takes 1.5 to 2.3 GB. If memory gets short, `docker compose stop grafana` frees most of it; the
store keeps collecting, and the history is there when Grafana starts again.

## Opening it

```
make monitor
```

It opens an ssh tunnel to the VPS (key `~/.ssh/drycastle-vps`) and the dashboard in the browser, at http://localhost:3300; Ctrl+C closes the tunnel.
No login to look (the port is on the VPS's loopback only, so whoever reaches it already has ssh to the machine). To change anything in Grafana:
sign in as `admin` with `GRAFANA_PASSWORD` from `/opt/drycastle/deploy/.env` on the VPS.

## What is on the dashboard

- Now: seconds since the last report (the server sends every 10 s: over two minutes it is down or hung), players, bots, tick p99, memory.
- Tick: p50 / p99 / slowest against the 33.3 ms budget, late ticks, the phases of a tick, the slowest tick of each 10 s with the GC pause in it.
- Where the time goes: CPU by thread, the costliest scopes (only while somebody plays), bot navigation.
- Memory and garbage collection: process / heap, engine object and node counts (a line that only climbs is a leak), allocation, collections.
- Network and database: bytes per client, dropped packets, refused saves, the save queue.
- Host: CPU by mode (steal: the provider gave the core to somebody else), memory available, disk free.

## How the numbers get there

- `NetServer.Metrics.cs`: with `DRYCASTLE_METRICS_URL` set (compose sets `http://metrics:8428/write`), every report period the server posts
  Influx line protocol built from the same text as the `server: stats` / `server: prof` log lines (`ShooterCore/Diagnostics/Metrics.cs`), so the
  log and the dashboard agree. The request runs off the game thread; a store that is down costs one error line and nothing else.
  Without the variable nothing is sent.
- Series are `drycastle_<measurement>_<field>`: `tick`, `match`, `phase{phase}`, `scope{scope}`, `thread{thread}`, `nav{what}`, `worst_tick`,
  `mem`, `gc`, `gc_period`, `up`. About 250 series; none per player (the clients' are below). The fields are the ones described in `docs/shooter-server.md`, "Performance log".
- The host's `node_*` series are fetched by the store itself (`deploy/monitoring/scrape.yml`).

## Clients

Every player's game reports its own numbers too: `make monitor-clients` (the "Drycastle clients" dashboard, a player filter at the top).

- What: frames per second, frame time p50 / p99 / slowest, hitches (frames over 33 / 50 / 100 ms), the engine's render time on the processor and
  on the graphics card, draw calls, primitives, memory (process, C# heap, video), allocation and collector pauses, ping, loss, snapshot gaps,
  reconciles, traffic, and the client's own scopes (ms per second: `net`, `players`, `creatures`, `vehicles`, `projectiles`, `view`, `hud`,
  `predict`, `reconcile`, and `net_snapshot`, `net_world`, `net_events` taken out of `net`).
- How: `NetClient.Report.cs` gathers them and every 10 s of play sends one small reliable message over the game connection
  (`EventKind.ClientReport`, `ShooterCore/Diagnostics/ClientPerf.cs`); the server (`OnClientReport`, `NetServer.Metrics.cs`) adds them to its own
  batch as `drycastle_client_<field>{player, gpu}` and `drycastle_client_scope_ms_per_s{player, scope}`. No new port, nothing in the client
  knows the store. The same numbers are in the player's log as `client: perf` and `client: perf scopes` lines.
- Who: every client with a window, always, while it is in a match. Bots and headless clients send none. About 50 series per player who ever
  played; the label is the player's name with anything but letters and digits made `_`.
- An older server ignores the message (it is not a protocol version); an older client sends none. Fields are added at the end of
  `ClientReport.Fields` only, never moved: the wire carries them by position.
- The server takes one report per client per 5 s and drops what does not decode; nothing in the game reads them.
- The repeatable client load test and its saved numbers: `docs/perf-investigation/client-64-bots.md`.

## Changing it

- A new number: add it to the log line it belongs to (or `Metrics?.Begin(..).Field(..).End()` in the report); it becomes a series by itself.
- A new panel: edit the list in `deploy/monitoring/make_dashboard.py`, run it, commit the JSON. Grafana reads the file again within a minute
  of a deploy. The dashboard cannot be edited in the browser (the file is the source).
- A query by hand: Grafana's Explore, or on the VPS
  `docker compose exec grafana wget -qO- 'http://metrics:8428/api/v1/query?query=drycastle_tick_p99_ms'`.

Not set up: alerts (Grafana can send to a Discord webhook; the "last report" number is the one to alert on).
