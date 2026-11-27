using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using System.Diagnostics;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>
/// What a shot can hit and what stops it: the targets a shooter resolves against, the hit target of a player, and the world queries (<see cref="WorldQuery"/>) that a ray asks, vehicle hulls and their
/// glass included (ADR 0028, ADR 0007, ADR 0035). Vehicle hulls are analytic, at the pose the shot's view showed (<see cref="PoseRewind.PoseOf"/>).
/// </summary>
internal sealed class ShotWorld
{
    /// <summary>Vehicles one bullet may pass through (by a pane or an open side in, and one out).</summary>
    const int MaxGlassPasses = 6;

    /// <summary>A vehicle slower than this stands parked: a walker goes round it (a moving one runs the walker over).</summary>
    const float ParkedSpeed = 0.5f;

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly PoseRewind _rewind;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly TargetBook _targets;
    readonly GlassServer _glass;
    readonly Node3D _world;
    readonly bool _timed;
    WorldQuery? _query;

    public ShotWorld(ServerContext ctx, Fleet fleet, PoseRewind rewind, Sitters sitters, PounceServer pounce, TargetBook targets, GlassServer glass, Node3D world, bool timed)
    {
        _ctx = ctx; _fleet = fleet; _rewind = rewind; _sitters = sitters; _pounce = pounce; _targets = targets; _glass = glass; _world = world; _timed = timed;
    }

    /// <summary>Microseconds the last shot spent choosing its targets; only measured in a perf run.</summary>
    public double ResolveUs { get; private set; }

    /// <summary>The one answer to what is under a point, in the way of a mover, or in the way of a shot (ADR 0028).</summary>
    public WorldQuery Query => _query ??= new WorldQuery(_world, NearestHull, ParkedHullDistance);

    // ---- the targets ----

    /// <summary>The targets a shot from <paramref name="c"/> resolves against: where its screen showed them (with <c>--no-lagcomp</c>, or for a bot, which sees the present, where they stand), without the riders no bullet reaches. The caller gives the list back.</summary>
    public List<HitTarget> TargetsFor(ServerClient c)
    {
        var t0 = _timed ? Stopwatch.GetTimestamp() : 0L;
        var list = _rewind.Rewinds(c) ? _rewind.TargetsSeenBy(c) : _targets.Live();
        var armT = Prof.Begin(Prof.S.TargetsArmoured);
        DropArmoured(list); // no bullet or pellet reaches an armoured seat (ADR 0035)
        Prof.End(Prof.S.TargetsArmoured, armT);
        if (_timed) ResolveUs = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
        return list;
    }

    /// <summary>The living players, for the live list: a pinned player is under the demon and untouchable, so no shot stops on them.</summary>
    public void AddPlayers(List<HitTarget> list)
    {
        var partT = Prof.Begin(Prof.S.TargetsPlayers);
        foreach (var o in _ctx.Roster)
            if (_ctx.IsAlive(o.Id) && !_pounce.IsPinned(o.Id)) list.Add(HitTargetOf(o));
        Prof.End(Prof.S.TargetsPlayers, partT);
    }

    /// <summary>The living players, for the rewound list: where the shooter's screen showed them.</summary>
    public void AddPlayersSeen(List<HitTarget> list, in ShotView view)
    {
        foreach (var o in _ctx.Roster)
            if (_ctx.IsAlive(o.Id)) list.Add(_rewind.Seen(view, o, HitTargetOf(o)));
    }

    /// <summary>
    /// A live player as a hit target. A rider in a seat that says it is exposed (the amphibious transport's, ADR 0012) is the figure every client
    /// draws there: as tall as the seat makes them (seated, or standing in the ring), and where the seat is now, since a driven vehicle moves on
    /// its driver's inputs before its riders' bodies are put back on their seats. Everyone else is their body, as before.
    /// </summary>
    HitTarget HitTargetOf(ServerClient o)
    {
        if (o.Car is { } car && car.Def.Seats[o.CarSeat].Exposed) // the seat where the car stood at the shot's view (B-02), or now
            return new HitTarget(o.Id, CarPose.SeatPosAt(car, o.CarSeat, _rewind.PoseOf(car)).ToNumerics(), RiderRules.HitStance(car.Def, o.CarSeat, o.Body.State.Stance), o.Body.Yaw, o.Worn.HeadGuarded);
        return new HitTarget(o.Id, o.Body.GlobalPosition.ToNumerics(), _sitters.StanceOf(o), o.Body.Yaw, o.Worn.HeadGuarded);
    }

    /// <summary>
    /// Takes out of <paramref name="targets"/> the riders of armoured seats (ADR 0035): no bullet, pellet or mounted-gun round reaches them. Their hit
    /// targets stay in the lists blasts read (<see cref="TargetBook.Live"/>), which hurt them as every body in range.
    /// </summary>
    void DropArmoured(List<HitTarget> targets) => targets.RemoveAll(IsArmouredRider);

    public bool IsArmouredRider(HitTarget t) =>
        _ctx.Players.TryGetValue(t.Id, out var o) && o.Car is { } car && o.CarSeat >= 0 && o.CarSeat < car.Def.Seats.Count && !RiderRules.BulletsReach(car.Def, o.CarSeat);

    /// <summary>Where the target stood in the list a shot resolved against (the rewound pose), or null if it was not in it.</summary>
    public static Vector3? FeetIn(List<HitTarget> targets, ushort id)
    {
        foreach (var t in targets)
            if (t.Id == id) return new Vector3(t.Feet.X, t.Feet.Y, t.Feet.Z);
        return null;
    }

    // ---- what stops a ray ----

    /// <summary>Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a shot stops on (<see cref="WorldQuery.Shot"/>), or infinity.</summary>
    public float WorldDistance(Vector3 from, Vector3 dir, float max) => WorldHit(from, dir, max, out _);

    /// <summary>
    /// Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a ray stops on: a wall, or a vehicle's hull (the
    /// nearer of <see cref="WorldQuery.Wall"/> and <see cref="NearestHull"/>), and which vehicle if it was one. The shooter's own vehicle
    /// (<paramref name="skip"/>) is not in the way.
    /// </summary>
    public float WorldHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip = null) => WorldHitSkipping(from, dir, max, out car, skip, null);

    /// <summary>As <see cref="WorldHit"/> with two vehicles left out (the shooter's own, and the one the target rides in: a line of sight to a rider is not blocked by the vehicle they sit in).</summary>
    public float WorldHitSkipping(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip, ServerCar? skipToo)
    {
        var stop = Query.Shot(from, dir, max, skip, skipToo);
        car = stop.Car;
        return stop.Distance;
    }

    /// <summary>The distance the ray stops at with two vehicles left out (the hull it hit and the shooter's own): a wall or another hull.</summary>
    float NextStop(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB) => Query.Shot(from, dir, max, skipA, skipB).Distance;

    /// <summary>
    /// The nearest vehicle hull a ray from <paramref name="from"/> enters within <paramref name="max"/>, among the living vehicles that are not
    /// <paramref name="skipA"/>, <paramref name="skipB"/> or in <paramref name="passed"/>. The hull is the upright box of <see cref="VehicleBox"/>, or the tilted boxes
    /// of a type's <see cref="VehicleDef.Hull"/> (the ones <see cref="VehicleCollider"/> uses), at the pose <see cref="PoseRewind.PoseOf"/> gives it. Distance in metres from <paramref name="from"/>.
    /// </summary>
    ServerCar? NearestHull(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB, List<ServerCar>? passed, out float distance, out VehiclePose pose)
    {
        ServerCar? best = null;
        distance = float.PositiveInfinity; pose = default;
        foreach (var car in _fleet.Cars)
        {
            if (car == skipA || car == skipB || car.Destroyed || passed != null && passed.Contains(car)) continue;
            var p = _rewind.PoseOf(car);
            if (VehicleBox.Ray(from.ToNumerics(), dir.ToNumerics(), max, p.Feet, p.Yaw, p.Pitch, p.Roll, car.Def) is not { } hit || hit.Distance >= distance) continue;
            best = car; distance = hit.Distance; pose = p;
        }
        return best;
    }

    /// <summary>The nearest hull of a vehicle that stands parked along a ray (its hull: the upright box, or the boxes of its type's hull list); infinity when none: <see cref="WorldQuery.Ahead"/> asks it.</summary>
    float ParkedHullDistance(Vector3 from, Vector3 dir, float max)
    {
        var best = float.PositiveInfinity;
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Thrown != null || (car.Flies && !car.Flight.Grounded) || MathF.Abs(car.State.Speed) > ParkedSpeed) continue;
            var p = car.GlobalPosition;
            var reach = max + car.Def.BroadReach;
            if (MathF.Abs(p.X - from.X) > reach || MathF.Abs(p.Z - from.Z) > reach) continue;
            var pose = CarPose.Present(car);
            if (VehicleBox.Ray(from.ToNumerics(), dir.ToNumerics(), max, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def) is { } hit && hit.Distance < best) best = hit.Distance;
        }
        return best;
    }

    /// <summary>
    /// A ray, pellet or rocket segment stopped on <paramref name="hull"/>. When that vehicle's gunner sits in the open (vehicles.json exposed_gunner) and is
    /// alive, returns him as a target and the world distance along the ray with the hull left out, so the shot can reach him through the hull
    /// (<see cref="GunnerCover"/>). A rider behind a pane is reached by <see cref="BulletHit"/>, which lets bullets through glass.
    /// Otherwise no gunner (every rider stays behind the hull).
    /// </summary>
    /// <remarks>
    /// The riders are every rider of <paramref name="hull"/> in the open (<see cref="RiderRules.Exposed"/>): the one gunner of a flak truck or a
    /// utility helicopter, or everyone aboard an amphibious transport (ADR 0012). <paramref name="seatsOnly"/> is for the weapons that never
    /// reached a flak or helicopter gunner through the hull (the Humvee's roof gun, the killstreak guns): they reach only riders whose seat says it
    /// is exposed. <paramref name="skip"/> is the shooter's own vehicle, which the ray passes through as well.
    /// </remarks>
    public (ExposedRiders? Gunner, float PastHull) ExposedGunnerBehind(ServerCar? hull, Vector3 from, Vector3 dir, float max, IReadOnlyList<HitTarget> targets, bool seatsOnly = false, ServerCar? skip = null)
    {
        if (hull is not { Destroyed: false } || !hull.Seat.Occupied) return (null, float.PositiveInfinity);
        if (RiderRules.Exposed(hull.Def, hull.Seat, targets, seatsOnly) is not { } riders) return (null, float.PositiveInfinity);
        return (riders, NextStop(from, dir, max, hull, skip)); // the walls and the other hulls, the hull and the shooter's own left out (B-02)
    }

    /// <summary>
    /// As <see cref="WorldHit"/> for a bullet, pellet or gun round: a vehicle's glass and its open sides do not stop it. A ray that reaches a
    /// vehicle through one of its panes (whole or shattered) or open sides (vehicles.json open_sides: the helicopter's cabin) goes on inside,
    /// so a rider is open to it, and leaves through the next pane or open side it crosses on that vehicle, then on to whatever is beyond
    /// (another window, a player, a wall). With no way out, the hull stops it on its far side (where the ray leaves the hit box), so every rider on the way is open to it; a ray that comes in by neither
    /// is stopped where it met the hull (<see cref="GlassRules.Through"/>). Every pane passed is added to <paramref name="crossed"/> (made
    /// when the first pane is passed, so a shot that meets no glass allocates nothing) for <see cref="GlassServer.DamageCrossed"/>.
    /// <paramref name="ignore"/> is a vehicle the ray does not meet at all (the door gunner's own), <paramref name="solid"/> one whose glass
    /// stops it as its hull does (the roof gunner's own). Returns the distance of the stop, and the vehicle it stopped on (null: the world or nothing).
    /// </summary>
    public float BulletHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ref List<GlassCross>? crossed, ServerCar? ignore = null, ServerCar? solid = null)
    {
        var off = 0f;
        List<ServerCar>? passed = null; // the vehicles this ray has gone through (left out from here on)
        var n = from.ToNumerics(); var nd = dir.ToNumerics();
        for (var pass = 0; ; pass++)
        {
            // the walls, and the hulls of the vehicles at the shot's view (B-02): the nearest of them, from where the ray is now
            var o = from + dir * off;
            var wallD = Query.Wall(o, dir, max - off);
            var hull = NearestHull(o, dir, max - off, ignore, null, passed, out var hullD, out var pose);
            if (hull == null || hullD >= wallD) { car = null; return off + wallD; }
            car = hull;
            var wall = off + hullD;
            if (car == solid || pass >= MaxGlassPasses) return wall;
            var (pitch, roll) = car.Def.TiltOf(pose.Pitch, pose.Roll);
            var lo = GlassRules.ToVehicle(n, pose.Feet, pose.Yaw, pitch, roll); var ld = GlassRules.DirToVehicle(nd, pose.Yaw, pitch, roll);
            var hit = car; // (an out parameter cannot be captured)
            // the pane kinds decide (ADR 0035): a reinforced pane that holds stops the round, a normal one lets it through; the pane it stopped on
            // takes its damage as any pane a round stopped on (GlassServer.Shot), so it is not put in the crossed list
            var way = PaneRules.Pass(car.Def, i => _glass.IsShattered(hit, i), lo, ld, wall, _glass.VehicleReach, max);
            if (way.Kind == HullPassKind.Hull) return wall;
            if (way.EntryPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.EntryPane, way.EntryT)); // (-1: an open side, nothing to damage)
            if (way.Kind != HullPassKind.Through) return way.T;
            if (way.ExitPane >= 0) (crossed ??= new()).Add(new GlassCross(car, way.ExitPane, way.ExitT));
            off = way.ExitT + 0.02f;
            (passed ??= new()).Add(car);
        }
    }
}
