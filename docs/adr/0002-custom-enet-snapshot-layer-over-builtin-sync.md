# Custom ENet snapshot layer instead of Godot's built-in state synchronisation

Supersedes the "built-in high-level multiplayer (ENet)" part of ADR 0001. Mods replicate player state with a thin custom layer over raw ENet (unreliable-sequenced, quantised delta snapshots with tick stamps, optional radius interest management, plus a small reliable message channel for events), not `MultiplayerSpawner`/`MultiplayerSynchronizer`. Godot, C#, Jolt, the authoritative headless Linux server and ENet itself are unchanged.

The 64-client spike (`docs/spikes/netcode-spike.md`, localhost, Godot 4.7.2) showed both approaches cope with the PoC's 16 players, but the built-in synchroniser saturated the server main thread at 64 players (cost grows with the square of players), silently never replicated a change to the 65th or later watched property in "on change" mode (godot#103938 reproduces), and did not work in "on change" mode for spawned players in our setup. A shooter also needs tick-stamped snapshots for interpolation and lag compensation, which the built-in layer lacks. Cost: about 350 lines of wire code we own, and no `@rpc` on that connection.

Caveats: single runs on localhost without latency or loss, movement-only workload; recheck if Godot's synchroniser changes, and test loss recovery before the PoC ships.
