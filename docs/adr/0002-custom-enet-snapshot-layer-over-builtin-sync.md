# Custom ENet snapshot layer instead of Godot's built-in state synchronisation

Supersedes the "built-in high-level multiplayer (ENet)" part of ADR 0001. Mods replicate player state with a thin custom layer over raw ENet (unreliable-sequenced, quantised delta snapshots with tick stamps, optional radius interest management, plus a small reliable message channel for events), not `MultiplayerSpawner`/`MultiplayerSynchronizer`. Godot, C#, Jolt, the authoritative headless Linux server and ENet itself are unchanged.

The 64-client spike (`docs/spikes/netcode-spike.md`, localhost, Godot 4.7.2) showed both approaches cope with the PoC's 16 players, but the built-in synchroniser saturated the server main thread at 64 players (cost grows with the square of players), silently never replicated a change to the 65th or later watched property in "on change" mode (godot#103938 reproduces), and did not work in "on change" mode for spawned players in our setup. A shooter also needs tick-stamped snapshots for interpolation and lag compensation, which the built-in layer lacks. Cost: about 350 lines of wire code we own, and no `@rpc` on that connection.

Caveats: single runs on localhost without latency or loss, movement-only workload; recheck if Godot's synchroniser changes, and test loss recovery before the PoC ships.

## Channel layout (protocol 26; perf investigation A-01, A-03, A-04, A-05, E-06)

Three ENet channels per connection, `Wire.Channels = 3`:

| channel | delivery | carries |
|---|---|---|
| 0 `ChanReliable` | reliable, ordered | control (hello, welcome, join, leave, profile, errors) and every state event: Hit, Kill, Death, Spawn, MatchState, Explosion, Launch, the gunship blasts, the world lifecycle |
| 1 `ChanData` | unreliable, sequenced | inputs (client to server), entity and world snapshots (server to client) |
| 2 `ChanEffects` | unreliable | lossy effects only (`Wire.IsEffect`): Shot, PelletFan, ImpactFx, GunshipShots, GunshipImpacts |

Why: ENet delivers a reliable channel in order, so one lost datagram held every later reliable event until its retransmit. Under real UDP loss at RTT 100 (`docs/perf-investigation/results-baseline.md`), the extra delay of reliable events had a p99 of 180 ms at 3 % loss, because every Shot shared that channel. A lost tracer is cosmetic, so effects moved to channel 2, where a lost datagram drops only that effect. The client dispatches on the first byte of a packet, not on the channel, so its receive path did not change. `NetSim` classifies by the reliable flag, so an impaired effect is dropped like any other unreliable datagram. Gunship blasts stay reliable because they carry the gunner's hit markers, which are state.

Same bump: the hello carries the map build hash (FNV-1a of the map's `manifest.json`, which names every chunk's hash), and a server refuses a different build by name (E-06). An entity tick with nothing to send sends no packet except every `Wire.EmptyTickHeartbeat` (3) ticks, which carries the input ack (A-05). The scoreboard goes out at most once a tick (A-04).

The world channel shares the acknowledged baseline (protocol 26, A-01, G-03). The inputs carry a world ack next to the entity ack, the server keeps a per-client history of world states (the same depth as the entity history) and every world packet names its baseline tick. Unchanged world entities are omitted; a keyframe goes only to an entity new to the baseline (first sight, or after a spawn), and there are no periodic keyframes. A lost world packet heals on the next tick against the same baseline, and a tick commits only when all its packets arrive. Spawn and despawn stay on the reliable channel with the spawn's full state. A baseline older than the history falls back to a full set.
