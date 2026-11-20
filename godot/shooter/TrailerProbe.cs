using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The physics space as a trailer's step sees it (<see cref="ITrailerWorld"/>), as <see cref="DriveProbe"/> is for a driven vehicle: is the place the trailer would stand in solid
/// (ground vehicle row of the rule table: terrain, buildings, trunks, placed solids, closed doors, the demon), and what is the ground under a point. It asks the static world and the demon
/// only, never the vehicles' solid boxes: the trailer's own tractor (whose box its footprint overlaps at a deep articulation) and every other vehicle are the vehicle contact pass's business
/// (<c>NetServer.ResolveVehicleContacts</c>, which skips a hitched pair). The footprint is the whole box (drawbar included) swept clear of the ground it stands on
/// (<see cref="Clearance"/>) and tilted with its slope (the ground under its two ends), so a slope or a kerb passes under it and a wall or a tree does not. It is on no layer: nothing hits it.
/// </summary>
public sealed partial class TrailerProbe : Node3D, ITrailerWorld
{
    /// <summary>The box is swept this far above the ground under it, metres (the wheels' business: kerbs and banks pass under).</summary>
    public const float Clearance = 0.5f;
    float _clearance = Clearance; // the type's own (trailer.probe_clearance), above its low parts
    const float RayLift = 1.5f, RayReach = 8f;
    /// <summary>The ground under a box is sampled this far apart along its length (a crest or a dyke between its two ends), metres.</summary>
    const float SampleEvery = 1.5f;

    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.GroundVehicle);

    VehicleDef _def = null!;
    BoxShape3D _box = null!;
    readonly PhysicsShapeQueryParameters3D _query = new();
    readonly PhysicsRayQueryParameters3D _ray = new();

    public static TrailerProbe Make(VehicleDef def) => new() { Name = "TrailerProbe", _def = def };

    public override void _Ready()
    {
        if (_def.Trailer is { ProbeClearance: > 0 } t) _clearance = t.ProbeClearance;
        var height = MathF.Max(0.2f, _def.Height - _clearance);
        _box = new BoxShape3D { Size = new Vector3(_def.HalfWidth * 2, height, _def.HalfLength * 2) };
        _query.Shape = _box; _query.CollisionMask = SweepMask;
        _ray.CollisionMask = WorldMask;
    }

    /// <summary>The height of the ground at (<paramref name="x"/>, <paramref name="z"/>), searched from <paramref name="hint"/> upwards a little and down a long way; the hint itself when there is none.</summary>
    public float GroundY(float x, float z, float hint)
    {
        _ray.From = new Vector3(x, hint + RayLift, z); _ray.To = new Vector3(x, hint - RayReach, z);
        var r = GetWorld3D().DirectSpaceState.IntersectRay(_ray);
        return r.Count == 0 ? hint : r["position"].AsVector3().Y;
    }

    public bool Blocked(in BodyBox from, in BodyBox to, float y)
    {
        // the ground under the two ends of the box at its new place: the box lies along that slope, clear of it
        var (s, c) = MathF.SinCos(to.Yaw);
        var front = (X: to.X - s * to.HalfL, Z: to.Z - c * to.HalfL);
        var rear = (X: to.X + s * to.HalfL, Z: to.Z + c * to.HalfL);
        var gf = GroundY(front.X, front.Z, y); var gr = GroundY(rear.X, rear.Z, y);
        var pitch = MathF.Atan2(gf - gr, 2 * to.HalfL);
        var height = _box.Size.Y;
        var basis = Basis.FromEuler(new Vector3(pitch, to.Yaw, 0));
        // a long box (the rocket cart's 12.6 m) over a crest or a dyke between its ends would sit in the ground: the ground is sampled along its length and the whole box lifted by the most
        // any sample stands over the line between the two ends
        var lift = 0f;
        var n = Math.Max(1, (int)MathF.Ceiling(2 * to.HalfL / SampleEvery));
        for (var i = 1; i < n; i++)
        {
            var t = i / (float)n; // 0 at the rear, 1 at the front
            var x = rear.X + (front.X - rear.X) * t; var z = rear.Z + (front.Z - rear.Z) * t;
            lift = MathF.Max(lift, GroundY(x, z, y) - (gr + (gf - gr) * t));
        }
        var mid = (gf + gr) / 2 + lift;
        var centre = new Vector3(to.X, mid + _clearance + height / 2, to.Z);
        _query.Transform = new Transform3D(basis, centre);
        return GetWorld3D().DirectSpaceState.IntersectShape(_query, 1).Count > 0;
    }
}
