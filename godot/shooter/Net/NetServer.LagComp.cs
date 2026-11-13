using System.Diagnostics;
using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Lag compensation for players (ADR 0013, B-01). Each tick, at the moment the snapshots take the state, every joined player's feet, stance and yaw
/// go into a ring of recent ticks. A hitscan shot, a pellet of a fan or a knife resolves its targets at the poses the shooter's screen showed:
/// <c>RTT/2 + the 3-tick interpolation delay</c> back, capped at 250 ms (<see cref="LagComp"/>). Walls, vehicle hulls, glass and the seats of
/// vehicles stay at their present positions (B-02 is not in this change), and the shooter itself is never rewound.
/// </summary>
public partial class NetServer
{
    /// <summary><c>--no-lagcomp</c>: shots resolve against present positions, as before this change (the A/B baseline for the perf harness).</summary>
    public bool NoLagComp;

    /// <summary>
    /// The tick the shot being resolved rewinds vehicles to (B-02), or null for the present. Set for the length of a shot (<c>FireWeapon</c>, a gun
    /// round, a rocket's step) and cleared after it, so the blasts and everything else see the vehicles where they stand.
    /// </summary>
    uint? _vehicleView;

    /// <summary>Microseconds the last shot spent choosing its targets; only measured in a perf run (<see cref="PerfOut"/>).</summary>
    double _lagResolveUs;

    /// <summary>End of tick: the poses every joined player ends the tick with. A player on foot and alive is a target; anyone else records an invalid pose.</summary>
    void CaptureLagPoses()
    {
        foreach (var o in _byId.Values)
        {
            SyncLife(o);
            var valid = o.Car == null && _match.Get(o.Id)?.Life == PlayerLife.Alive;
            o.Poses.Record(_tick, valid, new HitTarget(o.Id, N(o.Body.GlobalPosition), o.Body.State.Stance, o.Body.Yaw));
        }
    }

    /// <summary>
    /// A teleport (a spawn, a sandbox or debug move, a vehicle's seat or exit) starts a new life for the rewind: no pose before it is a place the
    /// player can be shot at (ADR 0013). Counted on the body, so every way of moving it is covered.
    /// </summary>
    void SyncLife(Client o)
    {
        var n = o.Body.Teleports;
        if (n == o.TeleportsSeen) return;
        o.TeleportsSeen = n;
        o.LifeStart = _tick;
    }

    /// <summary>
    /// The vehicles' poses at the end of the tick, captured where the players' are (ADR 0013, B-02). A sleeping vehicle keeps one run at its
    /// rest pose (<see cref="VehiclePoseRing.RecordRest"/>); a destroyed one is not a target.
    /// </summary>
    void CaptureVehiclePoses()
    {
        foreach (var car in _cars)
        {
            SyncCarLife(car);
            if (car.Destroyed) { car.Poses.Record(_tick, false, default); continue; }
            var pose = PresentPose(car);
            if (car.Asleep) car.Poses.RecordRest(_tick, pose);
            else car.Poses.Record(_tick, true, pose);
        }
    }

    /// <summary>A reset or a repair starts a new life for a vehicle's history: nothing before it is a place the vehicle can be shot at (ADR 0013).</summary>
    void SyncCarLife(ServerCar car)
    {
        if (car.Lives == car.LivesSeen) return;
        car.LivesSeen = car.Lives;
        car.LifeStart = _tick;
        car.Poses.Clear();
    }

    /// <summary>The vehicle as it stands now: its feet, yaw and attitude, and the turret's yaw (the same numbers the hit box and the seats read).</summary>
    static VehiclePose PresentPose(ServerCar car)
    {
        var (_, pitch, roll) = AttitudeOf(car);
        return new VehiclePose(N(car.GlobalPosition), car.State.Yaw, pitch, roll, car.TurretLocalYaw);
    }

    /// <summary>
    /// The ticks a shot from <paramref name="c"/> rewinds vehicles by (the same estimate as the players, <see cref="BackTicksOf"/>), or -1 for the
    /// present: with <c>--no-lagcomp</c>, or for a shooter with no connection (a bot).
    /// </summary>
    int VehicleBackOf(Client c)
    {
        if (NoLagComp || c.Peer == null) return -1;
        SyncLife(c);
        return BackTicksOf(c);
    }

    /// <summary>The tick a shot rewinds vehicles to: <paramref name="back"/> ticks before now, never before the shooter's life; null (the present) for -1.</summary>
    uint? ViewOf(int back, uint lifeStart) => back < 0 ? null : LagComp.ViewTick(_tick, back, lifeStart);

    /// <summary>
    /// Where a vehicle stands for the shot being resolved (<see cref="_vehicleView"/>): its pose at that tick, or where it stands now when it was
    /// not recorded there (or no shot is rewinding). A destroyed vehicle is not asked: it has no hull.
    /// </summary>
    VehiclePose PoseOf(ServerCar car)
    {
        SyncCarLife(car);
        return _vehicleView is { } v && LagComp.TryVehicleAt(car.Poses, v, car.LifeStart, out var past) ? past : PresentPose(car);
    }

    /// <summary>The targets a shot from <paramref name="c"/> resolves against. With <c>--no-lagcomp</c>, or for a shooter with no connection (a bot, which sees the present), the present positions.</summary>
    List<HitTarget> ShotTargets(Client c)
    {
        var t0 = _perf != null ? Stopwatch.GetTimestamp() : 0L;
        var list = NoLagComp || c.Peer == null ? LiveTargets() : RewoundTargets(c);
        if (_perf != null) _lagResolveUs = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
        return list;
    }

    /// <summary>
    /// Every living player and dummy, the players where <paramref name="c"/>'s screen showed them. A target with no valid pose recorded at that tick
    /// (it was seated then, or nothing is recorded yet) is taken where it stands now, as before.
    /// </summary>
    List<HitTarget> RewoundTargets(Client c)
    {
        SyncLife(c);
        var view = LagComp.ViewTick(_tick, BackTicksOf(c), c.LifeStart);
        var list = new List<HitTarget>(_byId.Count);
        foreach (var o in _byId.Values)
        {
            if (_match.Get(o.Id)?.Life != PlayerLife.Alive) continue;
            var now = HitTargetOf(o);
            if (o == c) { list.Add(now); continue; } // the shooter stands where it stands
            SyncLife(o);
            list.Add(o.Car == null && LagComp.TryTargetAt(o.Poses, view, o.LifeStart, out var past) ? past : now);
        }
        var dummies = _match.Targets.All; // living target dummies stand where they stand (not rewound); an index loop, as in LiveTargets
        for (var i = 0; i < dummies.Count; i++)
        {
            var d = dummies[i];
            if (d.Alive) list.Add(new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));
        }
        return list;
    }

    /// <summary>How many ticks back <paramref name="c"/>'s screen is now (<see cref="LagComp.BackTicks"/>, from ENet's round trip).</summary>
    int BackTicksOf(Client c) => LagComp.BackTicks(RttOf(c), Wire.TickRate, (int)NetClient.InterpDelayTicks);

    /// <summary>The round trip of a connected client in milliseconds (0 before ENet has measured one).</summary>
    static float RttOf(Client c) => (float)(c.Peer?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0);

    /// <summary>Where the target stood in the list a shot resolved against (the rewound pose), or null if it was not in it.</summary>
    static Vector3? TargetFeet(List<HitTarget> targets, ushort id)
    {
        foreach (var t in targets)
            if (t.Id == id) return new Vector3(t.Feet.X, t.Feet.Y, t.Feet.Z);
        return null;
    }
}
