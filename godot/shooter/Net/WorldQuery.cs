using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>What a ray met: the point, the surface normal and the distance from the ray's start.</summary>
public readonly record struct SurfaceHit(Vector3 Position, Vector3 Normal, float Distance);

/// <summary>
/// What a shot stops on: <see cref="Distance"/> from the start (infinity when nothing is in range), and when it was a vehicle's hull the
/// <see cref="Car"/> and the <see cref="Pose"/> it was tested at. <see cref="Normal"/> is the surface normal of the stop (zero when nothing was hit).
/// </summary>
public readonly record struct ShotStop(float Distance, ServerCar? Car, VehiclePose Pose, Vector3 Normal)
{
    public bool Hit => !float.IsInfinity(Distance);
}

/// <summary>The nearest vehicle hull a ray enters (the server's <c>NearestHull</c>): the vehicle, its distance and the pose the hull was tested at.</summary>
public delegate ServerCar? HullRay(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB, List<ServerCar>? passed, out float distance, out VehiclePose pose);

/// <summary>The distance along a ray (unit <c>dir</c>) to the nearest hull of a vehicle that stands parked, or infinity (the server's <c>ParkedHullDistance</c>).</summary>
public delegate float ParkedRay(Vector3 from, Vector3 dir, float max);

/// <summary>
/// The server's one answer to "what is there" (ADR 0028): <see cref="Ground"/> (what is under a point), <see cref="Blocked"/> (may this mover go from a
/// to b), <see cref="Shot"/> (what a shot meets first) and <see cref="Overlaps"/> (what a volume touches). Every mask comes from
/// <see cref="CollisionRules"/> by the mover asking, so no call site writes its own. Players are on their own layer, so no ray excludes them.
/// <para>
/// Hot: animals and walkers call <see cref="Ground"/> and <see cref="Blocked"/> hundreds of times a tick. The query objects are built once and
/// reused (an allocating ray cost ~11 µs against 1.5 to 5 µs bare); the one allocation left is the dictionary Godot answers a hit with, and a miss
/// allocates nothing but that empty one. Not thread safe: one server thread.
/// </para>
/// </summary>
public sealed class WorldQuery
{
    readonly Node3D _world;
    readonly HullRay _hull;
    readonly ParkedRay? _parked;
    readonly PhysicsRayQueryParameters3D _ray = new();
    readonly PhysicsShapeQueryParameters3D _shape = new();
    readonly SphereShape3D _sphere = new();
    readonly uint[] _static = new uint[Enum.GetValues<MoverKind>().Length];
    readonly uint[] _body = new uint[Enum.GetValues<MoverKind>().Length];
    readonly uint[] _solid = new uint[Enum.GetValues<MoverKind>().Length];
    readonly bool[] _stopsOnParked = new bool[Enum.GetValues<MoverKind>().Length];

    public WorldQuery(Node3D world, HullRay hull, ParkedRay? parked = null)
    {
        _world = world; _hull = hull; _parked = parked;
        foreach (var m in Enum.GetValues<MoverKind>())
        {
            _static[(int)m] = CollisionRules.StaticMask(m); _body[(int)m] = CollisionRules.BodyMask(m); _solid[(int)m] = CollisionRules.SolidMask(m);
            _stopsOnParked[(int)m] = CollisionRules.Collides(m, ObstacleClass.ParkedVehicle);
        }
    }

    PhysicsDirectSpaceState3D Space => _world.GetWorld3D().DirectSpaceState;

    /// <summary>One ray with a mask, through the reused parameters. The result is Godot's dictionary: empty on a miss.</summary>
    Godot.Collections.Dictionary Cast(Vector3 from, Vector3 to, uint mask)
    {
        _ray.From = from; _ray.To = to; _ray.CollisionMask = mask;
        return Space.PerfRay(_ray);
    }

    /// <summary>
    /// The first surface under a point for <paramref name="who"/> (the static world its row stops on: a drop lands on terrain, a building, a trunk, a door;
    /// a player does not see a crown): a ray from <paramref name="up"/> metres above <paramref name="from"/> to <paramref name="down"/> metres below it.
    /// Null when there is none.
    /// </summary>
    public SurfaceHit? Ground(MoverKind who, Vector3 from, float up, float down)
    {
        var a = new Vector3(from.X, from.Y + up, from.Z);
        var r = Cast(a, new Vector3(from.X, from.Y - down, from.Z), _static[(int)who]);
        if (r.Count == 0) return null;
        var p = r["position"].AsVector3();
        return new SurfaceHit(p, r["normal"].AsVector3(), a.DistanceTo(p));
    }

    /// <summary>The height of <see cref="Ground"/>, or null. For callers that want the number only.</summary>
    public float? GroundY(MoverKind who, Vector3 from, float up, float down)
    {
        var r = Cast(new Vector3(from.X, from.Y + up, from.Z), new Vector3(from.X, from.Y - down, from.Z), _static[(int)who]);
        return r.Count == 0 ? null : r["position"].AsVector3().Y;
    }

    /// <summary>Whether a body of <paramref name="who"/> is stopped between <paramref name="a"/> and <paramref name="b"/>: a ray against everything its body mask stops on (<see cref="CollisionRules.BodyMask"/>).</summary>
    public bool Blocked(MoverKind who, Vector3 a, Vector3 b) => Cast(a, b, _body[(int)who]).Count > 0;

    /// <summary>As <see cref="Blocked(MoverKind, Vector3, Vector3)"/>, and where it is stopped.</summary>
    public bool Blocked(MoverKind who, Vector3 a, Vector3 b, out SurfaceHit hit)
    {
        var r = Cast(a, b, _body[(int)who]);
        if (r.Count == 0) { hit = default; return false; }
        var p = r["position"].AsVector3();
        hit = new SurfaceHit(p, r["normal"].AsVector3(), a.DistanceTo(p));
        return true;
    }

    /// <summary>
    /// How far a body of <paramref name="who"/> can go from <paramref name="a"/> towards <paramref name="b"/> before something solid stops it: the static world
    /// and the vehicles' solid boxes its row Collides with, not the players' bodies (<see cref="CollisionRules.SolidMask"/>). Infinity when nothing is in the way.
    /// One bare ray; the caller throttles it.
    /// </summary>
    public float SolidDistance(MoverKind who, Vector3 a, Vector3 b)
    {
        var r = Cast(a, b, _solid[(int)who]);
        return r.Count == 0 ? float.PositiveInfinity : a.DistanceTo(r["position"].AsVector3());
    }

    /// <summary>
    /// How far a body of <paramref name="who"/> can go from <paramref name="a"/> towards <paramref name="b"/> before it meets what its row stops on and a navigation mesh does not hold: the static world its
    /// row Collides with (trunks and a door that is closed now are in it, <see cref="CollisionRules.StaticMask"/>) and, when its row Collides with a parked vehicle, a parked vehicle's hull (analytic, so a
    /// moving vehicle, which runs a walker over, is not one). Not players, other bodies or animals. Infinity when nothing is in the way. One ray; the caller keeps them rare (<see cref="WalkGuard"/>).
    /// </summary>
    public float Ahead(MoverKind who, Vector3 a, Vector3 b)
    {
        var r = Cast(a, b, _static[(int)who]);
        var d = r.Count == 0 ? float.PositiveInfinity : a.DistanceTo(r["position"].AsVector3());
        if (_parked == null || !_stopsOnParked[(int)who]) return d;
        var len = a.DistanceTo(b);
        if (len < 0.01f) return d;
        return MathF.Min(d, _parked(a, (b - a) / len, MathF.Min(len, d)));
    }

    /// <summary>
    /// Distance along <paramref name="dir"/> (unit) to the first static wall a shot stops on: the world and the shot-only cover (crowns, bushes). Not the
    /// vehicles: their hulls are analytic (<see cref="Shot"/>). Infinity when nothing is within <paramref name="max"/>. For a caller that routes its own vehicles (the gunship).
    /// </summary>
    public float Wall(Vector3 from, Vector3 dir, float max)
    {
        var r = Cast(from, from + dir * max, _static[(int)MoverKind.Shot]);
        return r.Count == 0 ? float.PositiveInfinity : from.DistanceTo(r["position"].AsVector3());
    }

    /// <summary>
    /// What a shot meets first along <paramref name="dir"/> (unit) within <paramref name="max"/>: terrain, building, fence, placed solid, trunk, crown,
    /// bush, closed door, or the analytic hull of a living vehicle other than <paramref name="skipA"/>, <paramref name="skipB"/> and those in
    /// <paramref name="passed"/> (at the pose the shot's view shows, ADR 0013). Players and NPCs are not here: a shot hits them by their hit shapes.
    /// </summary>
    public ShotStop Shot(Vector3 from, Vector3 dir, float max, ServerCar? skipA = null, ServerCar? skipB = null, List<ServerCar>? passed = null)
    {
        var r = Cast(from, from + dir * max, _static[(int)MoverKind.Shot]);
        var wall = r.Count == 0 ? float.PositiveInfinity : from.DistanceTo(r["position"].AsVector3());
        var car = _hull(from, dir, max, skipA, skipB, passed, out var d, out var pose);
        if (car != null && d < wall) return new ShotStop(d, car, pose, HullNormal(from, dir, max, car, pose));
        return r.Count == 0 ? new ShotStop(float.PositiveInfinity, null, default, Vector3.Zero) : new ShotStop(wall, null, default, r["normal"].AsVector3());
    }

    /// <summary>The world normal of the face of the hull a ray enters (only asked for a hull that stops a shot).</summary>
    static Vector3 HullNormal(Vector3 from, Vector3 dir, float max, ServerCar car, VehiclePose pose)
    {
        if (VehicleBox.Ray(new System.Numerics.Vector3(from.X, from.Y, from.Z), new System.Numerics.Vector3(dir.X, dir.Y, dir.Z), max, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def) is not { } hit)
            return -dir;
        var (pitch, roll) = car.Def.TiltOf(pose.Pitch, pose.Roll);
        var w = FlightMath.Rotate(hit.LocalNormal, pose.Yaw, pitch, roll); // the vehicle's frame (x right, y up, z back) in the world
        return new Vector3(w.X, w.Y, w.Z);
    }

    /// <summary>Whether a sphere touches anything <paramref name="who"/>'s body stops on.</summary>
    public bool Overlaps(MoverKind who, Vector3 centre, float radius)
    {
        _sphere.Radius = radius;
        return Overlaps(who, _sphere, new Transform3D(Basis.Identity, centre));
    }

    /// <summary>Whether <paramref name="shape"/> at <paramref name="at"/> touches anything <paramref name="who"/>'s body stops on. The caller reuses its shape.</summary>
    public bool Overlaps(MoverKind who, Shape3D shape, Transform3D at)
    {
        _shape.Shape = shape; _shape.Transform = at; _shape.CollisionMask = _body[(int)who];
        return Space.IntersectShape(_shape, 1).Count > 0;
    }
}
