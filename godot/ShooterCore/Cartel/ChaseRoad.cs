using System.Numerics;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// Where the Cartel van has driven since it left the objective (docs/cartel.md "Chase cars"): a polyline of ground points (x, z), one every <c>stepM</c> metres, the oldest the objective. A chase car is put down
/// on the road near a point of it that lies behind the van by a distance in a range (<see cref="Behind"/>): back along its way from the objective.
/// </summary>
public sealed class ChaseTrail
{
    readonly List<Vector2> _pts = new();
    readonly float _stepM;
    readonly int _maxPoints;

    /// <param name="start">The way begins here: the objective's centre.</param>
    /// <param name="stepM">The van's position is kept when it is this far from the last kept point.</param>
    /// <param name="maxM">The trail keeps about this much of the van's way (the oldest points go).</param>
    public ChaseTrail(Vector2 start, float stepM = 15f, float maxM = 700f)
    {
        _stepM = Math.Max(1f, stepM);
        _maxPoints = Math.Max(2, (int)MathF.Ceiling(maxM / _stepM) + 1);
        _pts.Add(start);
    }

    public int Count => _pts.Count;

    /// <summary>The van is at <paramref name="p"/> now: it joins the trail when it is a step from the last point.</summary>
    public void Note(Vector2 p)
    {
        if (Vector2.Distance(_pts[^1], p) < _stepM) return;
        _pts.Add(p);
        if (_pts.Count > _maxPoints) _pts.RemoveAt(0);
    }

    /// <summary>
    /// The trail's points that lie between <paramref name="minM"/> and <paramref name="maxM"/> behind the van at <paramref name="now"/> along its way, nearest to the van first, with how far behind each is.
    /// </summary>
    public IEnumerable<(Vector2 Point, float BackM)> Behind(Vector2 now, float minM, float maxM)
    {
        var back = Vector2.Distance(now, _pts[^1]);
        for (var i = _pts.Count - 1; i >= 0; i--)
        {
            if (back > maxM) yield break;
            if (back >= minM) yield return (_pts[i], back);
            if (i > 0) back += Vector2.Distance(_pts[i], _pts[i - 1]);
        }
    }
}

/// <summary>A chase car's place: the road place and its ground point, which way it faces (a Godot yaw, towards the van) and how far behind the van's way it is.</summary>
public readonly record struct ChaseSpot(RoadPlace Place, Vector2 Ground, float Yaw, float BackM);

/// <summary>Where a chase car is put down, pure (docs/cartel.md "Chase cars"): the rules of the spot.</summary>
public static class ChaseSpawn
{
    /// <summary>
    /// A spot for a new chase car, or null when none is fit now. A candidate is the nearest road (within spawn_road_m) to a point of the van's trail that lies spawn_back_min_m to spawn_back_max_m behind it:
    /// at least spawn_player_min_m from every player (<paramref name="players"/>), at least spawn_apart_m from the other chase cars (<paramref name="cars"/>), and not <paramref name="blocked"/> (the server's: in the
    /// sight of the party, or beside a vehicle). One of the candidates at random; it faces along the road towards the van.
    /// </summary>
    public static ChaseSpot? Pick(RoadGraph roads, ChaseTrail trail, Vector2 van, IReadOnlyList<Vector2> players, IReadOnlyList<Vector2> cars, ChaseData d, Func<Vector2, bool> blocked, Random rng)
    {
        if (roads.IsEmpty) return null;
        var min2 = d.SpawnPlayerMinM * d.SpawnPlayerMinM; var apart2 = d.SpawnApartM * d.SpawnApartM;
        var fit = new List<ChaseSpot>();
        foreach (var (p, back) in trail.Behind(van, d.SpawnBackMinM, d.SpawnBackMaxM))
        {
            if (roads.Nearest(p, d.SpawnRoadM) is not { } near) continue;
            var ground = roads.PointAt(near.Place);
            var tooNear = false;
            foreach (var pl in players) if (Vector2.DistanceSquared(pl, ground) < min2) { tooNear = true; break; }
            if (tooNear) continue;
            foreach (var c in cars) if (Vector2.DistanceSquared(c, ground) < apart2) { tooNear = true; break; }
            if (tooNear || blocked(ground)) continue;
            var toVan = RoadPath.Yaw(van - ground);
            var fwd = roads.YawAt(near.Place, true); var bwd = roads.YawAt(near.Place, false);
            fit.Add(new ChaseSpot(near.Place, ground, AngleBetween(fwd, toVan) <= AngleBetween(bwd, toVan) ? fwd : bwd, back));
        }
        return fit.Count == 0 ? null : fit[rng.Next(fit.Count)];
    }

    static float AngleBetween(float a, float b) => MathF.Abs(FlightMath.Wrap(a - b));
}

/// <summary>
/// The driver of a chase car, pure (docs/cartel.md "Chase cars"): the inputs a person at the wheel would press to follow a road path at a wanted speed, as the harvester's AI does (steer +1 is right, yaw grows
/// turning left; <see cref="DriveModel"/> steers a circle that widens with speed, so the pursuit's curvature is turned into a steer by the radius it has at this speed). It never touches the drive model.
/// </summary>
public static class ChasePilot
{
    /// <summary>The way ahead is looked at this far along the path, by speed: 0.6 s of road plus 5 m, between 6 and 25 m.</summary>
    public static float LookAheadM(float speed) => Math.Clamp(MathF.Abs(speed) * 0.6f + 5f, 6f, 25f);

    /// <summary>
    /// The inputs to follow <paramref name="path"/> from <paramref name="pos"/> facing <paramref name="yaw"/> at <paramref name="speed"/>, aiming at <paramref name="wantSpeed"/> (eased for a bend ahead and for a
    /// target far off the car's heading). <paramref name="turnRadiusM"/> is the circle the car steers at this speed (<see cref="DriveModel.TurnRadiusAt"/>). An empty path stops the car.
    /// </summary>
    public static DriveInput Follow(RoadPath path, Vector2 pos, float yaw, float speed, float turnRadiusM, float wantSpeed)
    {
        if (path.IsEmpty) return Stop(speed);
        var s = path.Project(pos);
        var ld = LookAheadM(speed);
        var d = path.At(s + ld) - pos;
        var dist = d.Length();
        if (dist < 0.5f) return Stop(speed);
        var err = FlightMath.Wrap(RoadPath.Yaw(d) - yaw); // positive: the point ahead is to the left
        var steer = MathF.Abs(err) > MathF.PI / 2f ? -MathF.Sign(err) : Math.Clamp(-2f * MathF.Sin(err) / dist * turnRadiusM, -1f, 1f);
        var bend = MathF.Abs(FlightMath.Wrap(path.YawAt(s + 2f * ld) - path.YawAt(s)));
        var want = wantSpeed * Math.Clamp(1.1f - bend / 1.2f, 0.35f, 1f) * Math.Clamp(1.2f - MathF.Abs(err) / 1.3f, 0.3f, 1f);
        return new DriveInput { Throttle = Math.Clamp((want - speed) * 0.9f, -1f, 1f), Steer = steer };
    }

    /// <summary>Brakes to a stop: the key against the travel while it moves, the handbrake once it nearly stands (so it never reverses).</summary>
    public static DriveInput Stop(float speed) => MathF.Abs(speed) > 1f ? new DriveInput { Throttle = speed > 0 ? -1f : 1f } : new DriveInput { Handbrake = true };
}
