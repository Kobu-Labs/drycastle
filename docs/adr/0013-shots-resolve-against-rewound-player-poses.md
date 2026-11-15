# Shots resolve against the player poses the shooter's screen showed

Decided for lag compensation of players (B-01 of the perf investigation), and extended to vehicles (B-02, issue #257: see **Vehicles** below). **Amends** the sentence that recurs in ADRs 0006, 0010, 0011 and 0012: that the server resolves a shot "from its own state, as every shot is". Those ADRs stay as written for everything else: the shooter's own position, the walls and the blasts are still resolved from the server's present state. The players a hitscan shot, a pellet of a fan or a knife can hit, and the vehicles a hitscan shot, a pellet, a gun round or a rocket can hit (their hulls, panes and exposed riders), are taken at the pose the shooter's screen showed.

**What changes.** Each tick, at the moment the snapshots take the state, the server records every joined player's feet, stance and yaw in a ring of the last 32 ticks (`PoseRing`, `godot/ShooterCore/Weapons/LagComp.cs`). A shot from a player with a connection resolves its player targets at the tick its screen showed:

    view tick = now - min(round(RTT/2 in ticks) + 3, 7)

`RTT` is ENet's round trip to the shooter. The `+ 3` is the client's interpolation delay (`NetClient.InterpDelayTicks`), since the client draws every remote player that far behind. The rewind is capped at 250 ms, which at 30 Hz is 7 ticks (233 ms): a shooter with a worse ping than about 270 ms gets the cap, and no more.

**Not rewound.** Walls stay at their present positions. Vehicles are rewound too (see **Vehicles** below). The shooter's own position is never rewound. The blasts (splash), the gunship's rounds, the shells, the flak shells and the killstreak missiles keep their present resolution. A rider in a seat that is not exposed is taken where the seat is now (its body is in the seat).

**Vehicles (B-02, issue #257).** A vehicle is rewound the way a player is: each tick, at the moment the snapshots take the state, every vehicle's pose goes into a ring of the last 32 ticks (`VehiclePoseRing`, `godot/ShooterCore/Weapons/LagComp.cs`). The pose is the feet on the ground under the centre, the yaw, the pitch and roll of a helicopter or a driven vehicle (which tilt its seats), and a flak truck's turret yaw. It is captured with the players' poses, at the same point of the tick, and the shot uses the same view tick and cap (`ViewTick` and `BackTicks` above). A shot from a shooter with a connection resolves each vehicle at the pose it had at that tick, else where it stands now.

- **The hull is the analytic box.** A vehicle's hull is the upright box of `VehicleBox` (the box `VehicleCollider` and the client use: yaw only, from the ground to its height), tested by a ray at the rewound pose. The walls are still a physics ray, now without the vehicle layer (World and shot-only cover). The shot takes the nearer of the two, so a vehicle no longer has a physics body on the shot path.
- **Glass and seats.** The panes (ADR 0007, 0008) are tested in the frame of the rewound pose, and so is a rider in an exposed seat (ADR 0011, 0012): the seat is where the car was at the view tick, and the rider is hit there. Every shot that reaches a vehicle this way uses one pose for its hull, its panes and its riders.
- **Rockets** rewind the vehicles at every step, by the shooter's lag at the moment it fired (the rocket keeps that lag). Players in a rocket's path stay at their present positions, as before.
- **Sleeping and destroyed vehicles.** A sleeping vehicle (`ParkedRules`, `FlightRest`, `DriveRest`) has a constant pose, so its ticks are stored as a run at its rest pose, not one sample per tick; a lookup into the run answers from it, and a vehicle that wakes keeps the rest pose for the ticks it slept. A destroyed vehicle has no hull and is not a target. A reset or a repair starts a new life: nothing before it is a place the vehicle can be shot at, as for a player's teleport.
- **Driver fairness.** A driver who has just turned a corner can be hit where they were, for as long as the shooter's lag reaches back, bounded by the cap: at most 233 ms (7 ticks), which is about 10 m of a car at its top speed, and less at a lower speed or a lower ping. The shooter aimed at what its screen showed, and the driver sees the shot after the fact. We accept that, as we accept the same for a player behind cover (above).
- **`--no-lagcomp`** turns the vehicles' rewind off too: every shot resolves against the present poses, the baseline of the perf harness. A bot has no connection and sees the present, as before.

**Cost.** One pose sample per vehicle per tick (about 1.3 kB of ring per vehicle; about 40 kB for 29 vehicles), no allocation, no wire change. The hull test is one box test per living vehicle per ray (the rays of a pellet fan: one per pellet).

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
