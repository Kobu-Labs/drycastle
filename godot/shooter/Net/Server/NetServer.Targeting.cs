using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>What a shot can hit and what stops it: the live targets of the tick, the hit target of a player, and the world queries (<see cref="WorldQuery"/>) that a ray asks, vehicle hulls included (ADR 0028).</summary>
public partial class NetServer
{
    WorldQuery? _query;
    /// <summary>The one answer to what is under a point, in the way of a mover, or in the way of a shot (ADR 0028). Built on first use: <see cref="World"/> is set by then.</summary>
    WorldQuery Query => _query ??= new WorldQuery(World, NearestHull, _parked.Distance);

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>What a shot can hit, kind by kind in the order the list has them (<see cref="TargetBook"/>); built once in <c>_Ready</c>.</summary>
    TargetBook _targets = null!;

    void RegisterTargetSources()
    {
        _targets = new TargetBook(() => _byId.Count);
        _targets.AddSource(AddPlayerTargets);
        _targets.AddSource(AddDummyTargets);
        _targets.AddSource(list => { if (BigfootTarget() is { } bigfoot) list.Add(bigfoot); }); // the Bigfoot is a hit target like a player (issue #264)
        _targets.AddSource(list => _citizens.Crowd?.AddTargets(list)); // the citizens of the Holy Mass
        _targets.AddSource(list => _thieves.AddTargets(list)); // the Gang members of the Thieves event and their Raid vans
        _targets.AddSource(AddAnimalTargets);
        _targets.AddSource(list => _rozhlas.AddTargets(list)); // the workers of the Urad
        _targets.AddSource(list => _demon.AddTarget(list));
    }

    void AddPlayerTargets(List<HitTarget> list)
    {
        var partT = Prof.Begin(Prof.S.TargetsPlayers);
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive && !_pounce.IsPinned(o.Id)) list.Add(HitTargetOf(o)); // a pinned player is under the demon and untouchable: not a target, so no shot stops on them
        Prof.End(Prof.S.TargetsPlayers, partT);
    }

    void AddDummyTargets(List<HitTarget> list)
    {
        var dummies = _match.Targets.All; // living target dummies: an index loop, no enumerator object
        for (var i = 0; i < dummies.Count; i++)
        {
            var d = dummies[i];
            if (d.Alive) list.Add(new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));
        }
    }

    void AddAnimalTargets(List<HitTarget> list)
    {
        var partT = Prof.Begin(Prof.S.TargetsAnimals);
        _pets.AddTargets(list);
        _wildlife.AddTargets(list);
        Prof.End(Prof.S.TargetsAnimals, partT);
    }

    /// <summary>
    /// A live player as a hit target. A rider in a seat that says it is exposed (the amphibious transport's, ADR 0012) is the figure every client
    /// draws there: as tall as the seat makes them (seated, or standing in the ring), and where the seat is now, since a driven vehicle moves on
    /// its driver's inputs before its riders' bodies are put back on their seats. Everyone else is their body, as before.
    /// </summary>
    HitTarget HitTargetOf(ServerClient o)
    {
        if (o.Car is { } car && car.Def.Seats[o.CarSeat].Exposed) // the seat where the car stood at the shot's view (B-02), or now
            return new HitTarget(o.Id, N(SeatPosAt(car, o.CarSeat, PoseOf(car))), RiderRules.HitStance(car.Def, o.CarSeat, o.Body.State.Stance), o.Body.Yaw, o.Worn.HeadGuarded);
        return new HitTarget(o.Id, N(o.Body.GlobalPosition), _sitters.StanceOf(o), o.Body.Yaw, o.Worn.HeadGuarded);
    }

    /// <summary>Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a shot stops on (<see cref="WorldQuery.Shot"/>), or infinity.</summary>
    float WorldDistance(Vector3 from, Vector3 dir, float max) => WorldHit(from, dir, max, out _);

    /// <summary>
    /// The walls a ray stops on: the world and the shot-only cover (tree crowns, bushes), by physics (<see cref="WorldQuery.Wall"/>). Vehicles are not in it: their hulls are
    /// <see cref="NearestHull"/>, analytic, at the pose the shot's view showed (ADR 0013, B-02).
    /// </summary>
    float WallDistance(Vector3 from, Vector3 dir, float max) => Query.Wall(from, dir, max);

    /// <summary>
    /// The nearest vehicle hull a ray from <paramref name="from"/> enters within <paramref name="max"/>, among the living vehicles that are not
    /// <paramref name="skipA"/>, <paramref name="skipB"/> or in <paramref name="passed"/>. The hull is the upright box of <see cref="VehicleBox"/>, or the tilted boxes
    /// of a type's <see cref="VehicleDef.Hull"/> (the ones <see cref="VehicleCollider"/> uses), at the pose <see cref="PoseOf"/> gives it. Distance in metres from <paramref name="from"/>.
    /// </summary>
    ServerCar? NearestHull(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB, List<ServerCar>? passed, out float distance, out VehiclePose pose)
    {
        ServerCar? best = null;
        distance = float.PositiveInfinity; pose = default;
        foreach (var car in _fleet.Cars)
        {
            if (car == skipA || car == skipB || car.Destroyed || passed != null && passed.Contains(car)) continue;
            var p = PoseOf(car);
            if (VehicleBox.Ray(N(from), N(dir), max, p.Feet, p.Yaw, p.Pitch, p.Roll, car.Def) is not { } hit || hit.Distance >= distance) continue;
            best = car; distance = hit.Distance; pose = p;
        }
        return best;
    }

    /// <summary>
    /// Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a ray stops on: a wall, or a vehicle's hull (the
    /// nearer of <see cref="WallDistance"/> and <see cref="NearestHull"/>), and which vehicle if it was one. The shooter's own vehicle
    /// (<paramref name="skip"/>) is not in the way.
    /// </summary>
    float WorldHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip = null)
    {
        var stop = Query.Shot(from, dir, max, skip);
        car = stop.Car;
        return stop.Distance;
    }

    /// <summary>The distance the ray stops at with two vehicles left out (the hull it hit and the shooter's own): a wall or another hull.</summary>
    float NextStop(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB) => Query.Shot(from, dir, max, skipA, skipB).Distance;

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
    (ExposedRiders? Gunner, float PastHull) ExposedGunnerBehind(ServerCar? hull, Vector3 from, Vector3 dir, float max, IReadOnlyList<HitTarget> targets, bool seatsOnly = false, ServerCar? skip = null)
    {
        if (hull is not { Destroyed: false } || !hull.Seat.Occupied) return (null, float.PositiveInfinity);
        if (RiderRules.Exposed(hull.Def, hull.Seat, targets, seatsOnly) is not { } riders) return (null, float.PositiveInfinity);
        return (riders, NextStop(from, dir, max, hull, skip)); // the walls and the other hulls, the hull and the shooter's own left out (B-02)
    }
}
