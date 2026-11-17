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
    const float RayLift = 1.5f, RayReach = 8f;
    /// <summary>A box longer than this (half length, metres) also samples the ground under its middle (a crest between the two ends).</summary>
    const float MidSampleFrom = 4f;

    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.GroundVehicle);

    VehicleDef _def = null!;
    BoxShape3D _box = null!;
    readonly PhysicsShapeQueryParameters3D _query = new();
    readonly PhysicsRayQueryParameters3D _ray = new();

    public static TrailerProbe Make(VehicleDef def) => new() { Name = "TrailerProbe", _def = def };

    public override void _Ready()
    {
        var height = MathF.Max(0.2f, _def.Height - Clearance);
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
        // a long box (the rocket cart's 12.6 m) over a crest between its ends would sit in the ground: the middle's ground lifts the whole box
        var mid = (gf + gr) / 2;
        if (to.HalfL > MidSampleFrom) mid = MathF.Max(mid, GroundY(to.X, to.Z, y));
        var centre = new Vector3(to.X, mid + Clearance + height / 2, to.Z);
        _query.Transform = new Transform3D(basis, centre);
        return GetWorld3D().DirectSpaceState.IntersectShape(_query, 1).Count > 0;
    }
}
