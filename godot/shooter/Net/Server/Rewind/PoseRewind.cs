using Godot;
using Drycastle.Shooter.Net.Client;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Rewind;

/// <summary>
/// Lag compensation (ADR 0013, B-01, B-02). Each tick, at the moment the snapshots take the state, every joined player's feet, stance and yaw and every vehicle's pose go into a ring of
/// recent ticks. A hitscan shot, a pellet of a fan, a knife or a rocket resolves its targets where the shooter's screen showed them: <c>RTT/2 + the 3-tick interpolation delay</c> back,
/// capped at 250 ms (<see cref="LagComp"/>). Walls, glass and the seats of vehicles stay where they are now; the shooter itself is never rewound.
/// While a shot is resolved <see cref="Look"/> says which tick the vehicles are looked at (<see cref="PoseOf"/>), and <see cref="LookNow"/> ends it, so that the blasts and everything else see the vehicles where they stand.
/// </summary>
internal sealed class PoseRewind
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly TargetBook _targets;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly bool _off;

    /// <summary>The tick the shot being resolved rewinds vehicles to, or null for the present.</summary>
    uint? _view;

    /// <param name="off"><c>--no-lagcomp</c>: shots resolve against present positions (the A/B baseline for the perf harness).</param>
    public PoseRewind(ServerContext ctx, Fleet fleet, TargetBook targets, Sitters sitters, PounceServer pounce, bool off)
    {
        _ctx = ctx; _fleet = fleet; _targets = targets; _sitters = sitters; _pounce = pounce; _off = off;
    }

    /// <summary>Whether a shot of <paramref name="c"/> is resolved on a rewound world: not with <c>--no-lagcomp</c>, and not for a shooter with no connection (a bot, which sees the present).</summary>
    public bool Rewinds(ServerClient c) => !_off && c.Peer != null;

    // ---- the poses of the tick ----

    /// <summary>End of tick: the poses every joined player ends the tick with. A player on foot and alive is a target; anyone else records an invalid pose.</summary>
    public void CapturePlayers()
    {
        foreach (var o in _ctx.Roster)
        {
            SyncLife(o);
            var valid = o.Car == null && _ctx.IsAlive(o.Id) && !_pounce.IsPinned(o.Id);
            o.Poses.Record(_ctx.Tick, valid, new HitTarget(o.Id, o.Body.GlobalPosition.ToNumerics(), _sitters.StanceOf(o), o.Body.Yaw));
        }
    }

    /// <summary>
    /// The vehicles' poses at the end of the tick, captured where the players' are. A sleeping vehicle keeps one run at its rest pose
    /// (<see cref="VehiclePoseRing.RecordRest"/>); a destroyed one is not a target.
    /// </summary>
    public void CaptureVehicles()
    {
        foreach (var car in _fleet.Cars)
        {
            SyncCarLife(car);
            if (car.Destroyed) { car.Poses.Record(_ctx.Tick, false, default); continue; }
            var pose = CarPose.Present(car);
            if (car.Asleep) car.Poses.RecordRest(_ctx.Tick, pose);
            else car.Poses.Record(_ctx.Tick, true, pose);
        }
    }

    /// <summary>
    /// A teleport (a spawn, a sandbox or debug move, a vehicle's seat or exit) starts a new life for the rewind: no pose before it is a place the
    /// player can be shot at (ADR 0013). Counted on the body, so every way of moving it is covered.
    /// </summary>
    public void SyncLife(ServerClient o)
    {
        var n = o.Body.Teleports;
        if (n == o.TeleportsSeen) return;
        o.TeleportsSeen = n;
        o.LifeStart = _ctx.Tick;
    }

    /// <summary>A reset or a repair starts a new life for a vehicle's history: nothing before it is a place the vehicle can be shot at (ADR 0013).</summary>
    void SyncCarLife(ServerCar car)
    {
        if (car.Lives == car.LivesSeen) return;
        car.LivesSeen = car.Lives;
        car.LifeStart = _ctx.Tick;
        car.Poses.Clear();
    }

    // ---- the view of the shot being resolved ----

    /// <summary>
    /// The ticks a shot from <paramref name="c"/> rewinds vehicles by (the same estimate as the players, <see cref="BackTicksOf"/>), or -1 for the
    /// present: with <c>--no-lagcomp</c>, or for a shooter with no connection (a bot).
    /// </summary>
    public int BackOf(ServerClient c)
    {
        if (!Rewinds(c)) return -1;
        SyncLife(c);
        return BackTicksOf(c);
    }

    /// <summary>The shot of <paramref name="c"/> looks at the vehicles where its screen showed them.</summary>
    public void Look(ServerClient c) => Look(BackOf(c), c.LifeStart);

    /// <summary>The shot looks at the vehicles <paramref name="back"/> ticks ago, never before the shooter's life; the present for -1.</summary>
    public void Look(int back, uint lifeStart) => _view = back < 0 ? null : LagComp.ViewTick(_ctx.Tick, back, lifeStart);

    /// <summary>The shot is resolved: the vehicles are where they stand now again.</summary>
    public void LookNow() => _view = null;

    /// <summary>
    /// Where a vehicle stands for the shot being resolved: its pose at the tick <see cref="Look"/> chose, or where it stands now when it was
    /// not recorded there (or no shot is rewinding). A destroyed vehicle is not asked: it has no hull.
    /// </summary>
    public VehiclePose PoseOf(ServerCar car)
    {
        SyncCarLife(car);
        return _view is { } v && LagComp.TryVehicleAt(car.Poses, v, car.LifeStart, out var past) ? past : CarPose.Present(car);
    }

    // ---- the players ----

    /// <summary>Every living target, the players where <paramref name="c"/>'s screen showed them (<see cref="TargetBook.Rewound"/>). The caller gives the list back.</summary>
    public List<HitTarget> TargetsSeenBy(ServerClient c)
    {
        SyncLife(c);
        var back = BackTicksOf(c);
        return _targets.Rewound(new ShotView(c, LagComp.ViewTick(_ctx.Tick, back, c.LifeStart), back));
    }

    /// <summary>
    /// <paramref name="now"/> where <paramref name="o"/> stood at the view's tick: a target with no valid pose recorded at that tick (it was seated then, or nothing is recorded yet) is taken where it stands now.
    /// The shooter stands where it stands. What a player wears is not rewound, only where they stood.
    /// </summary>
    public HitTarget Seen(in ShotView view, ServerClient o, HitTarget now)
    {
        if (o == view.Shooter) return now;
        SyncLife(o);
        return o.Car == null && LagComp.TryTargetAt(o.Poses, view.Tick, o.LifeStart, out var past) ? past with { HeadGuarded = now.HeadGuarded } : now;
    }

    /// <summary>How many ticks back <paramref name="c"/>'s screen is now (<see cref="LagComp.BackTicks"/>, from ENet's round trip).</summary>
    public int BackTicksOf(ServerClient c) => LagComp.BackTicks(RttOf(c), Wire.TickRate, (int)NetClient.InterpDelayTicks);

    /// <summary>The round trip of a connected client in milliseconds (0 before ENet has measured one).</summary>
    public static float RttOf(ServerClient c) => (float)(c.Peer?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0);

}
