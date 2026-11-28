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

    /// <summary>The points kept, the oldest (the objective) first.</summary>
    public IReadOnlyList<Vector2> Points => _pts;

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

/// <summary>A chase car's place: its ground point, which way it faces (a Godot yaw: towards the van, or for an ambush along the van's way), how far behind the van's way it is (negative: that far AHEAD of it, on its way to the drop) and the road place when it stands on a road (null: open ground).</summary>
public readonly record struct ChaseSpot(RoadPlace? Place, Vector2 Ground, float Yaw, float BackM);

/// <summary>Where a chase car is put down, pure (docs/cartel.md "Chase cars"): the rules of the spot.</summary>
public static class ChaseSpawn
{
    /// <summary>The open-ground candidates round a way point: itself and this many spawn_spread_m either side of it, across the van's way.</summary>
    static readonly float[] Sides = { 0f, -1f, 1f, -0.5f, 0.5f };

    /// <summary>An ambush car's sides of the van's way: both are tried.</summary>
    static readonly float[] AmbushSides = { 1f, -1f };

    /// <summary>The points of the van's way ahead that are looked at: one every this many metres.</summary>
    public const float AheadStepM = 15f;

    /// <summary>
    /// A spot for a new chase car, or null when none is fit now. AHEAD first, when <paramref name="ahead"/> (the van's way to the drop, planned the way a chase plans a way) is given and open ground can be told
    /// (<paramref name="open"/>): an AMBUSH, a candidate on open ground ambush_side_m to either side of a point spawn_ahead_min_m to spawn_ahead_max_m along that way from the van (past a road's edge; never the
    /// road's centre), facing ALONG the way there (the van's travel: it needs no U-turn once the van has passed). Only <paramref name="aheadBlocked"/> (the server's: a vehicle beside it; <paramref name="blocked"/> when
    /// none is given), the player and car distances and a way to the van count: no sight rule. When nothing ahead fits (the drop is near, or no open ground beside it) it lies spawn_back_min_m to
    /// spawn_back_max_m behind the van along its trail. A candidate is the nearest road (within spawn_road_m) to a point of that way, and, when <paramref name="open"/> says so, the point itself and the ground spawn_spread_m
    /// (and half of it) to either side of it: any open drivable ground, a field or a meadow too. It is at least spawn_player_min_m from every player (<paramref name="players"/>), at least spawn_apart_m from the other
    /// chase cars (<paramref name="cars"/>), not <paramref name="blocked"/> (the server's: in the sight of the party, or beside a vehicle) and one that <paramref name="reach"/> finds a way from to the van. One of the
    /// candidates at random (the next when it has no way); it faces along the road, or straight, towards the van.
    /// </summary>
    public static ChaseSpot? Pick(RoadGraph roads, ChaseTrail trail, Vector2 van, IReadOnlyList<Vector2> players, IReadOnlyList<Vector2> cars, ChaseData d, Func<Vector2, bool> blocked, Random rng,
        Func<Vector2, bool>? open = null, Func<Vector2, bool>? reach = null, RoadPath? ahead = null, Func<Vector2, bool>? aheadBlocked = null)
    {
        if (roads.IsEmpty && open == null) return null;
        var min2 = d.SpawnPlayerMinM * d.SpawnPlayerMinM; var apart2 = d.SpawnApartM * d.SpawnApartM;
        bool Fits(Vector2 ground, Func<Vector2, bool> block)
        {
            foreach (var pl in players) if (Vector2.DistanceSquared(pl, ground) < min2) return false;
            foreach (var c in cars) if (Vector2.DistanceSquared(c, ground) < apart2) return false;
            return !block(ground);
        }
        // the candidates of one way point: the nearest road, and the open ground across the way
        void Candidates(List<ChaseSpot> fit, Vector2 p, Vector2 along, float back)
        {
            if (!roads.IsEmpty && roads.Nearest(p, d.SpawnRoadM) is { } near)
            {
                var ground = roads.PointAt(near.Place);
                if (Fits(ground, blocked))
                {
                    var toVan = RoadPath.Yaw(van - ground);
                    var fwd = roads.YawAt(near.Place, true); var bwd = roads.YawAt(near.Place, false);
                    fit.Add(new ChaseSpot(near.Place, ground, AngleBetween(fwd, toVan) <= AngleBetween(bwd, toVan) ? fwd : bwd, back));
                }
            }
            if (open == null) return;
            var side = along.LengthSquared() > 1e-4f ? Vector2.Normalize(new Vector2(-along.Y, along.X)) : Vector2.UnitX;
            foreach (var k in Sides)
            {
                var ground = p + side * (k * d.SpawnSpreadM);
                if (open(ground) && Fits(ground, blocked)) fit.Add(new ChaseSpot(null, ground, RoadPath.Yaw(van - ground), back));
            }
        }
        ChaseSpot? One(List<ChaseSpot> fit)
        {
            // one at random; the next when it has no way to the van
            while (fit.Count > 0)
            {
                var at = rng.Next(fit.Count);
                var spot = fit[at];
                if (reach == null || reach(spot.Ground)) return spot;
                fit.RemoveAt(at);
            }
            return null;
        }

        if (ahead is { IsEmpty: false } && open != null)
        {
            var seenOnly = aheadBlocked ?? blocked;
            var fitAhead = new List<ChaseSpot>();
            for (var s = d.SpawnAheadMinM; s <= d.SpawnAheadMaxM && s <= ahead.Length; s += AheadStepM)
            {
                var along = ahead.At(MathF.Min(s + AheadStepM, ahead.Length)) - ahead.At(MathF.Max(0f, s - AheadStepM));
                if (along.LengthSquared() < 1e-4f) continue;
                var dir = Vector2.Normalize(along);
                var side = new Vector2(-dir.Y, dir.X);
                var yaw = RoadPath.Yaw(dir); // along the van's way: the van comes up behind it and drives past
                foreach (var k in AmbushSides)
                {
                    var ground = ahead.At(s) + side * (k * d.AmbushSideM);
                    if (open(ground) && Fits(ground, seenOnly)) fitAhead.Add(new ChaseSpot(null, ground, yaw, -s));
                }
            }
            if (One(fitAhead) is { } found) return found;
        }

        var fit = new List<ChaseSpot>();
        var behind = trail.Behind(van, d.SpawnBackMinM, d.SpawnBackMaxM).ToList();
        for (var i = 0; i < behind.Count; i++)
        {
            var (p, back) = behind[i];
            var along = i + 1 < behind.Count ? behind[i + 1].Point - p : p - (i > 0 ? behind[i - 1].Point : van);
            Candidates(fit, p, along, back);
        }
        return One(fit);
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
    public static float LookAheadM(float speed) => Math.Clamp(MathF.Abs(speed) * 0.35f + 4f, 5f, 14f);

    /// <summary>The spacing of the path points a bend's radius is read from, and of the points looked at along the path.</summary>
    public const float CurveStepM = 5f;

    /// <summary>
    /// What a car's speed through a bend comes from: the car type's max_lateral_accel (vehicles.json) times <paramref name="Grip"/> (corner_grip, 0.7), and its brake deceleration (vehicles.json brake) of which
    /// <paramref name="BrakeUse"/> (0.8) is planned on.
    /// </summary>
    public readonly record struct CornerNumbers(float MaxLateralAccel, float Brake, float Grip = 0.7f, float BrakeUse = 0.8f);

    /// <summary>The radius of the circle through three points (infinity when they lie on a line).</summary>
    public static float TurnRadius(Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = Vector2.Distance(a, b); var bc = Vector2.Distance(b, c); var ca = Vector2.Distance(c, a);
        var cross = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)); // twice the triangle's area
        return cross < 1e-4f ? float.PositiveInfinity : ab * bc * ca / (2f * cross);
    }

    /// <summary>
    /// The speed the car may carry now so that it is slow enough at every bend ahead (docs/cartel.md "Chase cars"): along the path from <paramref name="s"/> to the braking distance (v squared over twice the brake) plus the
    /// look-ahead, at every <see cref="CurveStepM"/> the bend's radius is read from the three points a step apart, the speed it allows there is sqrt(grip x max_lateral_accel x radius), and the car may be as fast
    /// as sqrt(that squared + 2 x brake_use x brake x the distance to it): the least over those points. A straight way is no limit (infinity).
    /// </summary>
    public static float CurveSpeed(RoadPath path, float s, float speed, in CornerNumbers c)
    {
        if (path.IsEmpty) return float.PositiveInfinity;
        var v = MathF.Abs(speed);
        var reach = v * v / (2f * c.Brake) + LookAheadM(v);
        var limit = float.PositiveInfinity;
        for (var k = CurveStepM; k <= reach + CurveStepM; k += CurveStepM)
        {
            var at = s + k;
            if (at - CurveStepM >= path.Length) break; // past the end the way goes straight on
            var r = TurnRadius(path.At(at - CurveStepM), path.At(at), path.At(at + CurveStepM));
            if (!float.IsFinite(r)) continue;
            var allowed = c.Grip * c.MaxLateralAccel * r;
            limit = MathF.Min(limit, MathF.Sqrt(allowed + 2f * c.BrakeUse * c.Brake * k));
        }
        return limit;
    }

    /// <summary>
    /// The inputs to follow <paramref name="path"/> from <paramref name="pos"/> facing <paramref name="yaw"/> at <paramref name="speed"/>, aiming at <paramref name="wantSpeed"/> (at most <paramref name="capMps"/>, the narrow way's cap;
    /// slowed for the bends ahead by <see cref="CurveSpeed"/> when <paramref name="corner"/> is given, and for a target far off the car's heading). <paramref name="turnRadiusM"/> is the circle the car steers at this speed (<see cref="DriveModel.TurnRadiusAt"/>). An empty path stops the car.
    /// </summary>
    public static DriveInput Follow(RoadPath path, Vector2 pos, float yaw, float speed, float turnRadiusM, float wantSpeed, CornerNumbers? corner = null, float capMps = float.PositiveInfinity)
    {
        if (path.IsEmpty) return Stop(speed);
        var s = path.Project(pos);
        var ld = LookAheadM(speed);
        var d = Ahead(path, s + ld) - pos;
        var dist = d.Length();
        if (dist < 0.5f) return Stop(speed);
        var err = FlightMath.Wrap(RoadPath.Yaw(d) - yaw); // positive: the point ahead is to the left
        if (BackingRound(err, speed)) return new DriveInput { Throttle = -1f, Steer = MathF.Sign(err) }; // a K-turn: the nose turns to the way while the car backs
        var steer = MathF.Abs(err) > MathF.PI / 2f ? -MathF.Sign(err) : Math.Clamp(-2f * MathF.Sin(err) / dist * turnRadiusM, -1f, 1f);
        var cap = MathF.Min(wantSpeed, capMps);
        if (corner is { } c) cap = MathF.Min(cap, CurveSpeed(path, s, speed, c)); // brakes before a bend, the radius read from the path
        var want = cap * Math.Clamp(1.2f - MathF.Abs(err) / 1.3f, 0.3f, 1f);
        return new DriveInput { Throttle = Math.Clamp((want - speed) * 0.9f, -1f, 1f), Steer = steer };
    }

    /// <summary>A K-turn starts when the way is more than this far off the nose (110 degrees) and the car is slower than <see cref="KTurnSpeedMps"/>...</summary>
    public const float KTurnStartRad = 110f * MathF.PI / 180f;
    /// <summary>...and goes on, while the car backs, until the way is under this far off the nose (60 degrees), then the car drives on.</summary>
    public const float KTurnEndRad = 60f * MathF.PI / 180f;
    public const float KTurnSpeedMps = 4f;
    /// <summary>Backing faster than this (m/s) is a K-turn in progress.</summary>
    const float BackingMps = 0.5f;

    /// <summary>
    /// The pilot's own decision to back round (docs/cartel.md "Chase cars"), from the heading error (<paramref name="err"/>, radians, the way's point against the nose) and the signed speed alone: a car that is slower than
    /// 4 m/s with the way behind it (more than 110 degrees off) backs up while steering, and, once it is backing, goes on until the way is under 60 degrees off; then it drives forward. No state: the car's own motion is the memory.
    /// </summary>
    public static bool BackingRound(float err, float speed)
    {
        var off = MathF.Abs(err);
        return speed < -BackingMps ? off > KTurnEndRad : off > KTurnStartRad && speed < KTurnSpeedMps;
    }

    /// <summary>
    /// The point <paramref name="s"/> metres along the path, and past its end the way goes straight on along the last leg (a path that ends at a van that moved on: the car steers on and does not stop at the end
    /// of a road; the path is planned again each repath_seconds).
    /// </summary>
    static Vector2 Ahead(RoadPath path, float s)
    {
        if (s <= path.Length) return path.At(s);
        var end = path.End;
        var back = path.At(MathF.Max(0f, path.Length - 3f));
        var dir = end - back;
        return dir.LengthSquared() > 1e-6f ? end + Vector2.Normalize(dir) * (s - path.Length) : end;
    }

    /// <summary>
    /// The inputs to back out of where the car is wedged: the key against its heading, steered so that its nose turns towards the way (the opposite of what a forward drive steers: a car turns the other way
    /// backwards). The car sits at <paramref name="pos"/> facing <paramref name="yaw"/>; the way is its path, or the van when it has none.
    /// </summary>
    public static DriveInput Reverse(RoadPath path, Vector2 pos, float yaw, Vector2 van)
    {
        var to = path.IsEmpty ? van : path.At(path.Project(pos) + 15f);
        var err = FlightMath.Wrap(RoadPath.Yaw(to - pos) - yaw); // positive: the point is to the left
        return new DriveInput { Throttle = -1f, Steer = Math.Abs(err) < 0.05f ? 0f : MathF.Sign(err) };
    }

    /// <summary>
    /// Stands aside for a van that is about to pass: brakes to a stop as <see cref="Stop"/> does, the wheel to the right while it still moves (so a car that pulled aside from the road's middle ends on its right edge), the handbrake once it stands.
    /// </summary>
    public static DriveInput Wait(float speed)
    {
        var stop = Stop(speed);
        if (MathF.Abs(speed) > 1f) stop.Steer = 1f;
        return stop;
    }

    /// <summary>Brakes to a stop: the key against the travel while it moves, the handbrake once it nearly stands (so it never reverses).</summary>
    public static DriveInput Stop(float speed) => MathF.Abs(speed) > 1f ? new DriveInput { Throttle = speed > 0 ? -1f : 1f } : new DriveInput { Handbrake = true };
}
