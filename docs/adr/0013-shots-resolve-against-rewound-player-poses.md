# Shots resolve against the player poses the shooter's screen showed

Decided for lag compensation of players (B-01 of the perf investigation). **Amends** the sentence that recurs in ADRs 0006, 0010, 0011 and 0012: that the server resolves a shot "from its own state, as every shot is". Those ADRs stay as written for everything else: the shooter's own position, the vehicles, the walls and the glass are still resolved from the server's present state. Only the players a hitscan shot, a pellet of a fan or a knife can hit are now taken at the pose the shooter's screen showed.

**What changes.** Each tick, at the moment the snapshots take the state, the server records every joined player's feet, stance and yaw in a ring of the last 32 ticks (`PoseRing`, `godot/ShooterCore/Weapons/LagComp.cs`). A shot from a player with a connection resolves its player targets at the tick its screen showed:

    view tick = now - min(round(RTT/2 in ticks) + 3, 7)

`RTT` is ENet's round trip to the shooter. The `+ 3` is the client's interpolation delay (`NetClient.InterpDelayTicks`), since the client draws every remote player that far behind. The rewind is capped at 250 ms, which at 30 Hz is 7 ticks (233 ms): a shooter with a worse ping than about 270 ms gets the cap, and no more.

**Not rewound.** Walls, vehicle hulls, glass panes and the seats of vehicles stay at their present positions; vehicles are not rewound in this change (B-02 is a separate, later decision). A rider in a seat is taken where the seat is now. The shooter's own position is never rewound. Rockets, the gunship's rounds and the shells keep their present resolution.

**Lives and teleports.** A player's rewind cannot reach before the start of their current life: each teleport of the body (a spawn, a sandbox or debug move, a vehicle's seat or exit) starts a new life at that tick. The shooter's own life is clamped the same way, so a shot cannot see a world from before its own spawn. A target with no valid pose at the view tick (it was in a seat then, or nothing is recorded) is taken where it stands now, as before.

**Bots and the A/B switch.** A bot has no connection and no round trip; it resolves against present positions, as it did before (it sees the present). `--no-lagcomp` turns the rewind off for every shooter, to measure the difference.

**The trade-off that is accepted.** A target that is behind cover now, and was out of it on the shooter's screen, is hit: the shot is judged where the shooter saw it. The 250 ms cap bounds the window in which a target can have left cover and still be shot at: at a 100 ms round trip that is about 2 m of sprinting, about one body width. A victim who is behind cover on their own screen can be hit by a shot from a shooter with a worse connection. We accept that, since the shooter aims at what it sees and the victim sees the shooter's shot after the fact.

**Hit feedback.** `Deal` still applies damage to the victim's id, so the Hit, Kill and streak events are unchanged, and the tracer's end point is the rewound hit's distance, so the tracer meets the target the shooter saw. The hit marker is still sent by the server only, and not predicted (ADR 0006's rule against a predicted kill).

**Considered.** Extrapolating each victim forward by the shooter's latency and resolving at the present (exact for constant velocity only; the shooter aims at what it sees, so the rewind matches that more directly). Lowering the interpolation delay from 3 ticks to 2 to save 33 ms (not before the snapshot loss issue, T1-2, is fixed). A client-reported hit (a protocol change and a cheat surface, which ADR 0006 rejects). Widening the hit radius (it helps every shot, including the victim who is behind cover).

**Consequences.**
- No wire change: the round trip comes from ENet, the poses from the snapshots the server already sends.
- Cost: one pose record per player per tick (about 1.3 kB of ring per player), and one target list per shot as before.
- A shooter who is slow to be measured (no round trip yet) is treated as zero latency for a tick or two.
- The perf harness logs `hit_used` (what the server did), `hit_now` and `hit_rewound` for every hitscan shot, under `--perf-lagcomp`.
