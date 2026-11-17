using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The physics space as <see cref="DriveModel"/> sees it (<see cref="IDriveWorld"/>), as <see cref="FlightProbe"/> is for the helicopter: a hidden
/// kinematic body with the vehicle's hull box, moved through the solid world (layer 1: ground, buildings, tree trunks, placed solids) to find what
/// stops it, and a ray for the ground under a wheel. The server has one per driven vehicle (the amphibious transport), the driver's client one for
/// its prediction, and both ask the same questions of the same colliders (ADR 0010). It is on no layer itself, so nothing hits it: bullets meet the
/// vehicle's own hit box. Shot-only cover (tree crowns, bushes) does not stop it, as it does not stop a car.
/// </summary>
public sealed partial class DriveProbe : CharacterBody3D, IDriveWorld
{
    /// <summary>
    /// The hull is swept this far clear of the ground it stands on, metres: the box is what walls, trunks and buildings stop; kerbs, banks and the
    /// slope itself pass under it and are the wheels' business (the ground rays).
    /// </summary>
    public const float Clearance = 0.5f;
    /// <summary>A wheel's ground ray starts this far above the wheel (so a kerb or a bank ahead is found, a floor overhead is not) and reaches this far down.</summary>
    const float RayLift = 1f, RayReach = 8f;

    /// <summary>What a driven ground vehicle stops on: its row of the rule table (the world without the shot-only cover).</summary>
    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.GroundVehicle);
    /// <summary>What its sweep stops on: the world, and the demon's body.</summary>
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.GroundVehicle);

    VehicleDef _def = null!;
    Water _water = Water.Empty;

    public static DriveProbe Make(VehicleDef def, Water water) => new() { Name = "DriveProbe", _def = def, _water = water, TopLevel = true };

    public override void _Ready()
    {
        CollisionLayer = 0;
        CollisionMask = SweepMask;
        var height = MathF.Max(0.2f, _def.Height - Clearance);
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(_def.HalfWidth * 2, height, _def.HalfLength * 2) }, Position = new Vector3(0, Clearance + height / 2, 0) });
        SetPhysicsProcess(false);
    }

    public float GroundY(NVec3 from)
    {
        var top = new Vector3(from.X, from.Y + RayLift, from.Z);
        _groundRay.From = top; _groundRay.To = top + Vector3.Down * (RayLift + RayReach);
        using var r = GetWorld3D().DirectSpaceState.IntersectRay(_groundRay); // freed here, not left to the finalizer (a wheel asks every tick)
        return r.Count == 0 ? float.NegativeInfinity : r[HitPosition].AsVector3().Y;
    }

    /// <summary>The ground ray's query, made once (a new one per ray was an object for the finalizer each), and the key of its answer.</summary>
    readonly PhysicsRayQueryParameters3D _groundRay = new() { CollisionMask = WorldMask };
    static readonly Variant HitPosition = "position";

    public float WaterY(float x, float z) => _water.BodyAt(x, z)?.SurfaceY ?? float.NegativeInfinity;

    public DriveMove Move(NVec3 from, float yaw, float pitch, float roll, NVec3 motion)
    {
        GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(pitch, yaw, roll)), new Vector3(from.X, from.Y, from.Z));
        var hit = MoveAndCollide(new Vector3(motion.X, motion.Y, motion.Z));
        var at = GlobalPosition;
        if (hit == null) return new DriveMove(new NVec3(at.X, at.Y, at.Z), false, default);
        var n = hit.GetNormal();
        return new DriveMove(new NVec3(at.X, at.Y, at.Z), true, new NVec3(n.X, n.Y, n.Z));
    }
}
