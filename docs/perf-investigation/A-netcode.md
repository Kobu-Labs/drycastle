# A. Netcode and replication (specialist report)

Scope: `godot/ShooterCore/Net/*`, `godot/shooter/Net/NetServer*.cs`, `NetClient*.cs`, `NetSim.cs`, ADR 0002, `docs/spikes/shooter-loss-test.md`. Read-only. No build, run or test was done; every number below is either derived from code (CONFIRMED), a path that is certain with size or engine behaviour unmeasured (UNVERIFIED), or judgment (OPINION). Budgets used for "fits" are my assumptions and are labelled as such.

## 1. Verdict

**Fit with changes.** The core design is right and, since commit `628eb7a` (#164, protocol 17), the entity channel is no longer the defect the audit describes: entity snapshots are deltas against the newest tick the client acknowledged, unchanged entities are omitted, and idle entities cost nothing. The changes that remain are: the world channel still uses the old last-sent baseline with 30-tick keyframes, high-rate effects ride the ordered reliable channel, the scoreboard is resent in full on every kill, and there is no interest management. On the byte model below, 16 players fit with margin; **32 is the ceiling without interest management** (client about 20 kB/s and server egress about 0.65 MB/s, against my assumed budgets of 20 kB/s per client and 1 MB/s per server); **64 does not fit without interest management plus effect culling**. The first thing to break at 16 is probably not bytes but reliable-channel stalls under loss, which the loss gate cannot see (A-03, A-10). The 16-player design target is not at risk on bandwidth or CPU.

## 2. What is healthy

- **Acked-baseline entity deltas.** Client sends its committed tick with every input (`godot/ShooterCore/Net/Wire.cs:162-164`, `godot/shooter/Net/NetClient.cs:527`); server records it only if it still holds that tick (`godot/shooter/Net/NetServer.cs:371`); encoder deltas against it (`godot/ShooterCore/Net/Snapshot.cs:68-72`, `:84-95`). Protocol history confirms it: `Wire.cs:180` v17 entry.
- **Unchanged entities are omitted; idle entity stream is zero** (`Snapshot.cs:87`), so parked vehicles and dummies cost only their share of the empty-packet floor (A-05).
- **New-to-baseline entities are keyframed, no age timer** (`Snapshot.cs:90-95`). The audit's T2-11 age keyframe is gone for this channel.
- **Multi-packet ticks commit atomically** (`Snapshot.cs:182-186`, `:277-285`). A lost packet costs one tick, and the next tick is computed against the same acked baseline and repairs it (`Snapshot.cs:40-45`).
- **Packets stay under the ENet MTU.** Max entity packet = 14 + 40 records x 23 B = 934 B (`Snapshot.cs:48-50`, `:116`); max world packet about 966 B (`WorldReplication.cs:88`, `:146`). Nothing is fragmented on the unreliable path (the ENet MTU of about 1392 B payload is UNVERIFIED against the bundled build, but the margin is large).
- **Inputs are loss-tolerant:** three redundant inputs per 41-53 B packet, server dedup by sequence (`NetClient.cs:519-527`, `NetServer.cs:372-376`).
- **Channels are independent.** Reliable traffic is on channel 0 and snapshots on channel 1 (`Wire.cs:185`, `NetServer.cs:146`, `:924`), so a stalled reliable channel does not delay snapshots.
- **Event design:** one pellet-fan event (35 B, seed) instead of eight shots (`WeaponCodec.cs:24`, `:39`, `:69-70`); gunship FX coalesced per tick into 121-123 (`Wire.cs:65-70`).
- **Encode once per broadcast** (`NetServer.cs:500`, audit T2-10 fixed); scratch buffers reused (`Snapshot.cs:55-58`, `WorldReplication.cs:87-90`; audit T2-03 fixed).
- **Catch-up:** a late tick skips the snapshot send (`NetServer.cs:212`, `NetServer.Telemetry.cs:25-30`; audit T2-05 fixed). Server inputs are capped at 8 per player and catch-up is credit-limited (`NetServer.cs:379`, `:587-606`, `PlayerIds.cs:33-45`).
- **Ping from ENet RTT** (`NetServer.cs`, `Ping`), so no client clock sync is needed for scoreboard or prediction. Input acks are sequence-wrapped (`NetTypes.cs:95`).
- **Anti-spam:** per-peer packet cap of 64 per tick (`NetServer.Telemetry.cs:14`, `NetServer.cs:343`; audit T2-06 partly fixed); profile-changing messages behind `AllowSave` (`NetServer.Profile.cs:149`); chat behind a token bucket (`NetServer.Chat.cs:42`); reliable decode is bounds-checked (`Wire.cs:232-240`); inputs validated (`Wire.cs:425-436`); silent peers dropped after 5 s (`NetServer.cs:472`, `:474-477`).
- **Telemetry exists:** tick p50/p99, overruns, bytes per client per 10 s (`NetServer.Telemetry.cs:12`, `:43-53`; audit T2-07 fixed).
- **Join is cheap.** The first tick after join is a full set (`Snapshot.cs:70-72`) until the first ack, about one RTT, roughly 1.6 kB per tick for 70 entities, so about 5-8 kB per join at RTT 100 ms (CONFIRMED arithmetic). World catch-up is one event per entity, at most 256 (`WorldEntities.cs:84`).
- **Measured health:** server tick p50/p95 1.6/2.6 ms at 16 bots, 9 % of one core (`docs/spikes/shooter-loss-test.md`, results table); zero hit, death or scoreboard mismatches under 3 % loss (same table).

## 3. Findings

### A-01. World channel still uses the last-sent baseline and 30-tick keyframes
- **Status: fixed in protocol 26 (issue #256).** The world channel now has the same acknowledged baseline as the entity channel: a world ack in the inputs, a per-client world history, deltas against the baseline, no 30-tick keyframe. The audit items T1-2 and T2-11 are fixed for both channels. The mechanism below describes the pre-fix code.
- **Mechanism.** `WorldSnapshotEncoder` diffs against the state last sent to that client (`godot/ShooterCore/Net/WorldReplication.cs:84-90`, `:110-123`), keyframes when `tick - LastFull >= Wire.KeyEvery` (`:115`, `Wire.cs:186`), and the client applies each packet on its own with no tick assembly and no ack (`WorldReplication.cs:236-287`). The input ack never feeds this encoder (`NetServer.cs:924-928`). The class comment (`WorldReplication.cs:74-77`) still describes the pre-#164 behaviour.
- **Cost.** A keyframe is 4 + 12 + 4 + 3 (+1 for hit points) = 23-24 B. Per client, that is 24 B/s per idle world entity. Realistic world population: 16 corpses (`Corpses.cs:13`) plus one streak entity per player with `max_active` 1 (`killstreaks.json:14-18`), so about 30 at 16 players, about 0.7 kB/s per client; about 80 at 64 players, about 1.9 kB/s; cap 256 (`WorldEntities.cs:84`), 6.1 kB/s. Recovery after a lost packet takes up to 1 s (CONFIRMED).
- **Fix.** Give the world encoder the same acked baseline as `SnapshotEncoder` (add a baseline tick to the world header: 4 B per packet, plus a keyframe-on-first-sight rule). Protocol bump, can ride with the next bump.
- **Effort:** S-M. **Confidence:** CONFIRMED (mechanism and arithmetic). **Audit:** T1-2 (world part), T2-11 (world part). The audit's high-ranked T1-2 should be re-triaged to this smaller item.

### A-02. The loss gate and its doc describe the pre-fix defect
- **Mechanism.** The loss test (`dd4897f`) predates the fix (`628eb7a`, same day, later). Its verdict "real defect, not fixed" (`docs/spikes/shooter-loss-test.md:40`) and its gate `MAX_STALE_TICKS = 75` with the comment "a lost delta heals at the next keyframe (30 ticks)" (`godot/shooter/run_loss_test.py:27`) describe the old behaviour. With the acked baseline, a lost entity delta heals on the next tick that arrives after the ack round trip, so the predicted staleness is about RTT/33 ms + 2 ticks, roughly 5-6 ticks at rtt100. The 15.4 % wrong-sample figure and the 86-tick run are therefore stale.
- **Cost.** The current gate would pass a regression up to 75 ticks (2.5 s). A current measurement could confirm the fix or show a remaining path.
- **Fix.** Re-run the loss test on the branch as it is (see section 5), then tighten `MAX_STALE_TICKS` to about RTT/tick + 4 and update the doc. Note in the doc that the old run predates #164.
- **Effort:** S. **Confidence:** UNVERIFIED (the prediction; the code path is CONFIRMED). **Audit:** new (audit T1-2 is stale).

### A-03. High-rate, loss-tolerant effects share the ordered reliable channel
- **Mechanism.** Every `Send` and `BroadcastEvent` uses `ChanReliable` with `FlagReliable` (`NetServer.cs:491-504`). That includes every Shot (`NetServer.cs:776`, `:804`, `NetServer.Vehicles.cs:306`), PelletFan, ImpactFx, Explosion, Launch, and up to three gunship messages per tick (`NetServer.Aircraft.cs:194-196`). ENet delivers reliable commands in order per channel, so a lost datagram holds back every later message on channel 0 until its retransmit. Hit, Kill, Death and Spawn sit behind it.
- **Cost.** Per recipient, a Shot is 27 B payload + 4 B envelope (`WeaponCodec.cs:47-54`, `Wire.cs:153`) + 6 B ENet reliable command = 37 B. Fire rates from `weapons.json` run to 15 shots/s (p90, fire interval 0.066 s). At 16 players firing 3/s: 48 shots/s, so 1.8 kB/s per client; at 10/s each (AK), 5.9 kB/s per client. Stall frequency is loss x reliable datagrams per second: at 3 % loss and roughly 20-40 reliable datagrams/s in combat, that is 0.6-1.2 stalls/s per client, each lasting at least one ENet retransmit timeout (RTO is RTT-adaptive; its value is UNVERIFIED, so the stall length is UNVERIFIED). The loss test cannot measure this (A-10).
- **Fix.** Move Shot, PelletFan, ImpactFx and the gunship kinds 121-123 to channel 1 (unreliable). Keep Hit, Kill, Death, Spawn, MatchState, profile and the world lifecycle reliable. Check that no client path requires a Shot before its Hit (the audit's own caveat). Gunner hit flags move into a reliable Hit. The shooter's own FX is already predicted (`NetClient.Predict.cs:8`), so a lost remote tracer costs only cosmetics.
- **Effort:** M (protocol bump; client handlers must tolerate loss). **Confidence:** channel use CONFIRMED; stall size UNVERIFIED. **Audit:** T2-09 (open). This is consistent with ADR 0002's "small reliable message channel for events".

### A-04. The full scoreboard is resent on every kill
- **Mechanism.** `ScoreboardChanged` fires on join, leave, kill, target kill, round end and sandbox toggle (`MatchSim.cs:193`, `:206`, `:213`, `:252`, `:266`, `:285`, `:290`). Each one rebuilds and broadcasts all rows (`NetServer.cs:534`, `:578-582`, `BroadcastEvent` at `:497-504`), on top of the 1 Hz broadcast (`NetServer.cs:198`).
- **Cost.** A row is about 22-26 B (`MatchCodec.cs:30-44`), so 16 rows is about 0.4 kB and 64 rows about 1.5 kB. The 64-row message exceeds the ENet MTU and is fragmented on the reliable channel. Per client per second: (1 + kills/s) x message size. Using one kill per player per 32 s (assumption): 16 players 0.6 kB/s, 32 players 1.6 kB/s, 64 players 4.6 kB/s per client. At 64 that is 0.3 MB/s of server egress for the scoreboard alone.
- **Fix.** Coalesce to at most one MatchState per tick with a dirty flag (no protocol change), then send only changed rows (protocol bump) if still needed.
- **Effort:** S (coalesce). **Confidence:** CONFIRMED (path and size); kill rate is an assumption. **Audit:** new (T3-012 covers only the join case).

### A-05. An empty snapshot every tick is the idle floor
- **Mechanism.** `Encode` always returns at least one packet (`Snapshot.cs:116`), so every tick sends a 14 B header even when nothing changed. Wire cost is 14 + 40 B of UDP, IP, ENet header and unreliable command (ENet sizes UNVERIFIED) = 54 B, at 30 Hz.
- **Cost.** 1.6 kB/s per client. For a standing 16-player lobby the entity stream is zero, world keyframes about 0.7 kB/s, and the empty-packet floor 1.6 kB/s, so about 70 % of the idle cost is headers. Server egress for 16 idle clients: 26 kB/s.
- **Fix.** Send an empty heartbeat at 10 Hz when nothing changed. The client's interpolator already receives full state per committed tick (`NetClient.cs:369-376`), and a skipped tick with unchanged entities lerps to the same value, so this is compatible. The only visible effect is that the first changed entity after a gap lerps across up to 66 ms, which is at most a few centimetres at walking speed (OPINION). Test it with the loss harness.
- **Effort:** S. **Confidence:** CONFIRMED (arithmetic); render effect OPINION. **Audit:** T3-022 (open).

### A-06. The remote render clock is re-anchored on every arrival; jitter is not smoothed
- **Mechanism.** Render tick is `rt = _lastTick + (now - _lastArrival) x 30 - 3` (`NetClient.cs:536`, same form in `NetClient.Cars.cs:167`, `NetClient.Aircraft.cs:101`, `NetClient.Animals.cs:89`), with `_lastTick` and `_lastArrival` reset on each committed tick (`NetClient.cs:362`). A packet that arrives late by delta seconds moves render time back by 30 x delta ticks at the next arrival. The 100 ms buffer prevents freezes (about 3 ticks of data are kept) but not positional jitter.
- **Cost.** At walking speed (5 m/s) every 10 ms of arrival jitter shows as a 5 cm backward step for every remote player and vehicle at once. Magnitude under real jitter is UNVERIFIED. The loss test's max snapshot gap of 117 ms (`shooter-loss-test.md`, results) exceeds the 100 ms buffer, so freezes of about 17 ms do occur under 3 % loss.
- **Fix.** Client-only: estimate server time as a min-filtered offset of (arrival - tick/30) over about 2 s; drive render time from that estimate, advancing monotonically and slewing; buffer target = 3 ticks + a jitter estimate. No protocol change, because the tick is already in every header.
- **Effort:** S-M. **Confidence:** CONFIRMED (mechanism); magnitude UNVERIFIED. **Audit:** new.

### A-07. No interest management: entity and effect bytes scale with n and with fire rate
- **Mechanism.** `SendSnapshots` builds one list of every player, vehicle, flak turret, helicopter, drive record, target and rocket, and each client's encoder diffs all of it (`NetServer.cs:905-924`). Shots and effects go to every joined client (`NetServer.cs:497-504`). The ADR calls interest management "optional" (`docs/adr/0002`), and nothing implements it.
- **Byte model (30 Hz, per client; CONFIRMED arithmetic on the record sizes above).** Per tick: 54 B + 14 B x moving entities (position delta 6 B + header 4 B, plus 4 B when yaw or pitch changes). Assumptions: all players moving and turning, no vehicles moving, 3 shots/s per player, one kill per player per 32 s.

| players | entity stream | shots | scoreboard | total per client | server egress |
|---|---|---|---|---|---|
| 16 | 8.3 kB/s | 1.8 kB/s | 0.6 kB/s | 10.7 kB/s | 0.17 MB/s |
| 32 | 15.1 kB/s | 3.6 kB/s | 1.6 kB/s | 20.3 kB/s | 0.65 MB/s |
| 64 | 28.5 kB/s | 7.1 kB/s | 4.6 kB/s | 40.2 kB/s | 2.6 MB/s |
| 64, with a 50 m interest radius (spike cut of 78 % on entities) | about 6.3 kB/s | 7.1 kB/s | 4.6 kB/s | about 18 kB/s | 1.2 MB/s |

- **Reconciling the measured number.** The loss test measured 4.24 kB/s per client at 16 bots. The record model gives 16 x 27 updates/s x 10 B = 4.3 kB/s of payload before datagram overhead, so the measurement is consistent with payload only. Whether it includes the 40 B per datagram depends on the counter (UNVERIFIED; A-10 measurement item 2).
- **Spike check.** The 64-client spike measured custom 12.8 kB/s per client and 818 kB/s server egress without interest, 2.83 kB/s and 181 kB/s with a 50 m radius (`docs/spikes/netcode-spike.md:58-63`). CPU was 17.5 % vs 17.3 % at 64, so interest management costs no measurable CPU; its value is bytes.
- **Break-even.** Budgets are assumptions: 20 kB/s per client (about 160 kbit/s) and 1 MB/s server egress (about 8 Mbit/s). Without interest management both are reached at about 32 players. Interest management is worth its client-side churn once per-client entity bytes approach the budget, i.e. from about 24-32 players on this map (OPINION). At 16 it saves about 2.6 kB/s per client in the spike (3.65 to 1.01) but is not needed.
- **Fix.** Per-client relevance by distance with hysteresis, always including the client's own vehicle and streak entities; far entities at 10 Hz; cull Shot and impact effects beyond audible range (audit's proposal). The server side needs no protocol change: an entity left out of `ents` is sent as a removal record. The client side is the cost: a removal calls `QueueFree` on the remote node (`NetClient.cs:364`), and for vehicles it also frees the car and its collision body (`NetClient.cs:364`), which would break prediction. Interest needs a dormant state on the client, not removal.
- **Effort:** L (server filter S; client dormant state and hysteresis M-L). **Confidence:** CONFIRMED (no filter exists; spike figures). **Audit:** T2-08 (open).

### A-08. The server does not step a player whose input has not arrived; this may explain the prediction floor
- **Mechanism.** `SimulateClient` consumes at most one queued input per tick, two when the queue holds more than three and credit allows (`NetServer.cs:587-606`, `PlayerIds.cs:33-45`). With an empty queue the body does not move that tick, while the client has already predicted the movement (`NetClient.cs:519-527`).
- **Cost.** One late input equals one tick of movement, which at 5 m/s is 17 cm. The loss test's clean run shows reconciliations of 0.08-0.5 m on almost every tick for some bots (`shooter-loss-test.md`, finding 2). That is 0.5-3 ticks of walking, which fits this mechanism, but the cause is not confirmed.
- **Fix.** Instrument first: count ticks with an empty queue per player and the reconciliation size. If confirmed, a server input jitter target of 1-2 inputs (start consuming after 2 queued) removes the stall at the cost of 33-66 ms more input latency.
- **Effort:** S (instrumentation); S-M (the target). **Confidence:** UNVERIFIED hypothesis. **Audit:** new. The latency trade is for B (lag compensation) and C (prediction) to price.

### A-09. No incoming byte budget; the packet count cap does not bound memory
- **Mechanism.** `Poll` caps packets per peer per tick at 64 (`NetServer.cs:343`), and `OnPacket` checks only for length zero (`NetServer.cs:353`). Nothing caps the size of a packet. ENet reassembles fragments into a buffer sized from the declared length before the application sees it, and the default maximum packet size is 32 MB (UNVERIFIED for the bundled Godot build). So a client can make the server allocate up to 64 x declared size per tick.
- **Cost.** Worst case per peer per tick: 64 packets x declared size, allocated before the byte check. The byte budget only limits the work after allocation.
- **Fix.** (a) A per-peer byte budget per tick, to drop and count the rest (S). (b) Find out whether Godot's `ENetConnection` exposes a maximum packet size, or whether the bundled ENet has one that can be set (S to check, M if it needs a native change). Also check the per-peer outgoing queue for a stalled client, which is unbounded in `Tx`/`peer.Send` (`NetServer.cs:484-487`).
- **Effort:** S (budget), S-M (ENet cap). **Confidence:** UNVERIFIED (engine limit); the absence of a check is CONFIRMED. **Audit:** extends T2-06.

### A-10. The loss gate cannot see reliable-channel stalls, and per-kind bytes are not counted
- **Mechanism.** `NetSim` delays reliable packets but never drops them, and it never reorders (`NetSim.cs:12`, `:51-64`, `:61` clamp). So the loss test, which is the only netcode gate, cannot measure head-of-line stalls, retransmit latency or reordering. Telemetry counts bytes per client only (`NetServer.Telemetry.cs:51`), not per kind, so the byte model cannot be checked.
- **Cost.** A-03's stall cost is invisible to every gate, and A-06's jitter is only partly exercised (the monotone clamp turns jitter into delay).
- **Fix.** Add to `NetSim`: drop reliable datagrams with the loss probability and re-queue them after an RTO of about 2 x RTT, and optionally allow reordering of unreliable ones (about 20 lines, no root). Add per-kind bytes and per-kind delivery latency to the client telemetry (S).
- **Effort:** S-M. **Confidence:** CONFIRMED (the harness code). **Audit:** extends T2-07.

## 4. Design alternatives considered (each priced)

| alternative | what it buys | cost to move | cheapest test |
|---|---|---|---|
| Keep ENet and the custom layer, fix A-01, A-03, A-04, A-05 together (recommended) | Closes the world channel, the HOL risk and the idle and scoreboard floors | S-M; one protocol bump (v25); client handlers for the moved effects | Loss harness with reliable drops (A-10) before and after |
| Interest management by distance (A-07) | 70-78 % of entity bytes at 64 (spike) | L; client dormant state; server filter S | `make shooter-loss-test BOTS=32` with a radius cull in `SendSnapshots` |
| Coarser position quantisation (1/32 m instead of 1/128 m) | Position delta fits in i8 for walking: 6 B to 3 B, about 30 % of a moving record | M; protocol bump; prediction quantisation changes on both sides; max error 1.6 cm | Offline: replay recorded positions through the quantiser |
| Yaw as a delta (i8 or i16) | Rotation 4 B to 2 B (about 15 % of a record) | S-M; protocol bump | Count rotation bytes in the harness |
| Dead reckoning with velocity and corrections over 5 cm | Likely 50-70 % of position bytes for straight walking (UNVERIFIED) | M-L; remote-only, so prediction is unaffected | Log remote velocity error for walking bots |
| Third ENet channel for Hit, Kill and Death | Separates the critical reliable stream from effects, but effects still block on loss | S-M; protocol bump; host channel count changes | Only if A-03 moves nothing; not preferred |
| Built-in `MultiplayerSynchronizer` (rejected by ADR 0002) | Nothing new | The spike is decisive: 60 B per update vs 8.5 B, 29 kB/s vs 3.7 kB/s per client at 16. Not reconsidered | None |
| GameNetworkingSockets or QUIC (per-message lanes, congestion control) | Lanes without cross-message HOL; better congestion | L; native dependency on every export target; rewrites `Wire`, `NetSim` and both hosts | Only if the loss harness shows reliable stalls over 100 ms p99 after A-03 |
| Empty-tick heartbeat at 10 Hz (A-05) | About 1.1 kB/s per idle client | S | Idle run with 16 bots |
| Server input jitter target (A-08) | Removes stalls that look like prediction error | S-M; 33-66 ms more input latency | Instrument first |

## 5. Needs measurement

Measurement is a plan, not a run. Nothing here was executed.

1. **Re-baseline the loss gate on the current branch.** `make shooter-loss-test SCENARIOS=clean,rtt100,loss10 BOTS=16 MEASURE=30`. Expected under the acked baseline: staleness of about 5-6 ticks at rtt100 (A-02), far below the 86-tick run. Output in `out/shooter-loss/<timestamp>/`. Why not static: the staleness depends on ack round trips and loss timing.
2. **Reconcile bytes per kind.** Add per-kind counters to `Tx` (A-10) and rerun a 16-bot idle run and a 16-bot walking run. Predicted: idle about 2.3 kB/s per client of which 1.6 kB/s empty packets (A-05); walking about 4.3 kB/s payload, with or without the 40 B datagram overhead. Resolves the 4.24 kB/s question. Cheapest: the existing loss harness plus the counters.
3. **Reliable-loss model.** Extend `NetSim` (A-10), then `make shooter-loss-test` with a combat workload (`BOTS=16`). Measure Hit and Kill delivery latency p50/p99 and channel-0 stall count. Predicted if A-03 holds: stalls of about one RTO each, 0.6-1.2 per second per client at 3 % loss. Why not static: the RTO and the datagram packing are engine behaviour.
4. **Jitter and render smoothness.** `--net-sim latency_ms=50,jitter_ms=20,loss=0.01` with a client log of remote render position step sizes (A-06). Predicted: steps of about 5 cm per 10 ms of lateness at walking speed. Cheapest: existing flags plus a log line in `RenderRemotes`.
5. **Scale of the shipping server.** `make shooter-loss-test BOTS=32` and `BOTS=64` (if the machine allows 64 processes; the spike did), recording tick p95, egress and per-client bytes. The spike's frame-busy p95 at 64 was 28.6 ms against a 33 ms budget, but for the spike code, not the shipping server. Predicted: egress 0.65 MB/s at 32 and 2.6 MB/s at 64 without interest (A-07).
6. **Scoreboard cost.** Count MatchState bytes per second at 16 and 32 bots with two kills per second forced by the existing `--match-bot` (A-04).
7. **Input starvation.** Count ticks per player with an empty input queue and the reconciliation size in the same run (A-08). Cheap: one counter in `SimulateClient` and one in the client's reconcile path.
8. **ENet limits (static, no run).** Read the bundled ENet source for `ENET_PROTOCOL_MAXIMUM_PACKET_SIZE`, the default MTU, the RTO minimum and the sizes of the protocol headers, and check whether Godot's `ENetConnection` exposes a packet-size limit (A-09, A-03, A-05). This can be done by reading source, without a build.

## 6. Audit items: status on this branch

| audit id | status here | where |
|---|---|---|
| T1-2 (last-sent baseline) | fixed for entities and world (protocol 26) | `Snapshot.cs:68-95`; `WorldReplication.cs` (A-01) |
| T1-1 (frame cap) | not in scope; not checked | |
| T2-03 (scratch buffers) | fixed | `Snapshot.cs:55-58` |
| T2-05 (catch-up) | fixed | `NetServer.cs:183`, `:212` |
| T2-06 (per-peer budget) | packet count fixed; byte budget open (A-09) | `NetServer.cs:343` |
| T2-07 (telemetry) | fixed (per-kind open, A-10) | `NetServer.Telemetry.cs:43-53` |
| T2-08 (interest management) | open (A-07) | |
| T2-09 (effects on reliable) | open (A-03) | `NetServer.cs:491-504` |
| T2-10 (encode once) | fixed | `NetServer.cs:500` |
| T2-11 (age keyframes) | fixed for entities and world (protocol 26) | `Snapshot.cs:84-95` |
| T3-012 (join scoreboard) | open; scoreboard per kill is A-04 | `NetServer.cs:534` |
| T3-022 (empty packet) | open (A-05) | `Snapshot.cs:116` |
| T3-023 (header 4 B, shot 27 B) | open; priced in section 4 | `Wire.cs:198`; `WeaponCodec.cs:47` |
| T3-024 (interpolator shifts list) | open; trivial (32 samples) | `Interpolator.cs:21` |
| T3-025 (pending list search) | open; trivial (120 entries) | `NetClient.cs:519-523` |

Nothing in section 3 contradicts an ADR. ADR 0002 lists interest management as optional, and A-07 is priced accordingly. A-03 keeps the small reliable channel that the ADR specifies. ADRs 0006, 0010 and 0011 depend on the input-driven server step and the input ack, and every fix above keeps both; ADR 0012 has no netcode content (grepped).
