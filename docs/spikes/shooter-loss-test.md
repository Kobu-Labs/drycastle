# Shooter netcode loss and latency test (issue #93)

Status: **gate FAILS on one point, everything else holds.** Under 100 ms RTT and 3 % loss nothing gets stuck, reliable events and the match state stay exact, and the final state converges. But a lost snapshot delta leaves a remote player (and your own server position) wrong until the next keyframe, and that is slower and bigger than acceptable (details and proposed fix below). Gate for ADR 0002's caveat ("test loss recovery before the PoC ships").

Run it: `make shooter-loss-test` (about 2 minutes per scenario; `SCENARIOS=clean,rtt100,rtt200,loss10`, `BOTS=16`, `MEASURE=30`). Code: `godot/shooter/Net/NetSim.cs` (the impairment), `NetServer.LossTest.cs`, `NetClient.LossBot.cs`, runner `godot/shooter/run_loss_test.py`. Raw output goes to `out/shooter-loss/<timestamp>/` (gitignored).

## Method

- **Impairment:** `--net-sim latency_ms=50,jitter_ms=5,loss=0.03` on the server and on every client. It sits in the application send path: each outgoing packet is delayed by latency +/- jitter (one way, per sender), and unreliable datagrams (snapshots and inputs, ENet channel 1) are dropped with the given probability. Reliable packets are delayed but never dropped (ENet's retransmit timing is not simulated). Packets are never reordered (ENet sequences at send time). No tc, no root.
- **Scenario `rtt100`:** 50 ms each way (100 ms RTT), 5 ms jitter, 3 % loss in each direction. Scenarios `rtt200` (100 ms each way) and `loss10` (10 %) exist in the runner but were **not run** for this write-up.
- **Workload:** one headless server (30 Hz, real map collision) and 16 headless bots, one process each, server pinned to 4 cores and bots to 12. The server keeps one flat spawn cluster (19 candidates within 60 m) so the bots meet. 12 bots walk a scripted square path, 4 chase the nearest player and shoot the AK in bursts. Round starts when the 16th bot joins; 3 s warm-up, **30 s measured window**, 8 s quiet phase in which everyone stands still, then final dumps from server and clients are compared.
- **Measured:** snapshot receive rate and max gap per client; reconciliations and prediction error (own player, teleport/respawn snaps over 2 m counted apart); server tick work p50/p95 (the `_PhysicsProcess` body, not Godot's own physics step) and tick interval; bytes/s from ENet host counters; hit, death and scoreboard events per player against the server's counters; every decoded snapshot of every client against the server's per-tick record (exact quantised values) for the "decoded state wrong" rows.

## Results (single run each, `make shooter-loss-test`)

Machine: Intel i7-11850H (8 cores/16 threads), Linux 7.0.8, Godot 4.7.2 mono, Python 3.14; the machine was shared with other agents and a running viewer (load average about 3.7 at start).

| metric | clean | 100 ms RTT, 3 % loss |
|---|---|---|
| snapshots/s per client, min / mean | 30.0 / 30.0 | 28.8 / 29.1 |
| max snapshot gap ms, worst client / mean of clients | 56 / 51 | 117 / 90 |
| gaps over 200 ms / 500 ms | 0 / 0 | 0 / 0 |
| reconciliations total (max per client) | 2205 (770) | 6660 (858) |
| max prediction error m / p95 worst client | 2.000 / 1.511 | 1.284 / 0.485 |
| hits / kills / deaths (server) | 89 / 25 / 26 | 81 / 20 / 20 |
| victim hit-event / own death-event / scoreboard mismatches | 0 / 0 / 0 | 0 / 0 / 0 |
| final state entity mismatches (all clients, all players) | 0 | 0 |
| decoded player states wrong vs server (share of samples) | 0.00 % | 15.4 % |
| longest wrong run, ticks (30 ticks = 1 s) | 0 | **86** |
| decoded position error p99 / max m | 0.000 / 0.000 | 0.63 / 45.5 |
| server tick work p50 / p95 / p99 / max ms | 1.59 / 2.55 / 3.20 / 13.4 | 1.51 / 2.51 / 3.24 / 13.8 |
| server ticks/s | 30.00 | 30.00 |
| server CPU, % of one core | 9 | 9 |
| kB/s per client down / up | 4.24 / 1.12 | 4.80 / 1.51 |

Assertions in the runner (thresholds at the top of `run_loss_test.py`): all bots finish; at least 24 snapshots/s and no gap over 1 s per client; at most 30 inputs unacked at the end; own predicted position within 5 cm of the server at the end; every victim received exactly the hit events, death events and scoreboard the server sent; final decoded state of every player identical to the server's; server tick p95 under 33 ms; at least 20 hits for coverage; **no decoded player state wrong for more than 75 ticks**. Clean passes everything; `rtt100` fails only the last one (86 ticks).

## What the test found

1. **Delta snapshots without acks: one lost packet leaves state wrong until the keyframe (real defect, not fixed).** `SnapshotEncoder` sends each entity as a delta against the last state it *sent*, and omits it when unchanged; nothing tells the server what arrived. A lost delta therefore leaves the client's copy off by that delta, and because later packets omit the entity ("unchanged") it stays off until the next keyframe, up to 1 s later (up to 2 s plus if the keyframe is lost too; 86 ticks was seen). At 3 % loss 15 % of decoded entity samples were wrong, p99 0.6 m; a larger error (max 45 m) occurs when the lost update was a respawn teleport or a status change (health, dead/alive, weapon). The same decoder feeds the local player's reconciliation, so it also pulls the own position. It is bounded and it recovers (final state is exact everywhere), which is why only the staleness bound fails. The spike measured the same mechanism as "0.5 m, fixed by the next keyframe", on loopback.
   *Fix, bigger than a send-path tweak so left for the user:* have the client report the last snapshot tick it applied (2 bytes added to the input packet, protocol version bump) and make the server delta against the newest *acked* state per client (ring of recent states), keyframe only as a fallback. Cheaper stopgap: lower `Wire.KeyEvery` from 30 to about 10 (shorter wrong time, roughly +15 % snapshot bandwidth); not measured here.
2. **Prediction mismatch even on a clean network (pre-existing, cause not found).** In the clean run some bots reconcile on almost every tick by 0.08 to 0.5 m while walking (2205 reconciliations over 16 clients in 30 s; the mismatch is on the same order with 3 % loss, so loss does not make it worse in these numbers). The comparison is clean vs impaired, so the table is still meaningful, but "max prediction error" mostly shows this floor. Worth its own issue; likely server/client movement differences on slopes or steps.
3. Not a bug but worth knowing: respawn and teleport position jumps can reach the client before the spawn event (a snapshot overtakes the reliable event); the client snaps correctly. These are counted separately (`teleport_snaps`) and excluded from the prediction error above.

## Caveats

- Single run per scenario, one machine, shared with other work. Server tick work excludes the engine's physics step. The numbers show orders of magnitude, not benchmarks.
- Loopback with an application-level simulator: no real bursts of loss, no reordering, no bandwidth cap, no reliable-packet loss or retransmit timeouts, and the simulated delay is quantised by the server's tick (30 Hz) and the clients' frame rate (60 Hz).
- 16 bots on a 12-core pin share CPU with the server, so client frame hitches add to the measured gaps.
- Hits come from simple chasing bots: 81 to 89 hits per run, not a full gameplay mix (no bazooka, no cars, no joins or leaves under loss).
- `rtt200` and `loss10` scenarios and the loss-recovery stopgap were not run.
