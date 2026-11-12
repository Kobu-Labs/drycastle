using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The physics space as <see cref="DriveModel"/> sees it (<see cref="IDriveWorld"/>), as <see cref="FlightProbe"/> is for the helicopter: a hidden
/// kinematic body with the vehicle's hull box, moved through the solid world (layer 1: ground, buildings, tree trunks, placed solids) to find what
/// stops it, and a ray for the ground under a wheel. The server has one per driven vehicle (the amphibious transport), the driver's client one for
/// its prediction, and both ask the same questions of the same colliders (ADR 0007). It is on no layer itself, so nothing hits it: bullets meet the
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

    VehicleDef _def = null!;
    Water _water = Water.Empty;

    public static DriveProbe Make(VehicleDef def, Water water) => new() { Name = "DriveProbe", _def = def, _water = water, TopLevel = true };

    public override void _Ready()
    {
        CollisionLayer = 0;
        CollisionMask = CollisionLayers.World;
        var height = MathF.Max(0.2f, _def.Height - Clearance);
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(_def.HalfWidth * 2, height, _def.HalfLength * 2) }, Position = new Vector3(0, Clearance + height / 2, 0) });
        SetPhysicsProcess(false);
    }

    readonly Godot.Collections.Array<Rid> _ignored = new();

    /// <summary>
    /// Player bodies are on the world's layer, and they are not part of the world a vehicle drives through: riders sit inside the hull, the driver's
    /// client knows no body but its own, and running someone over is the server's overlap test. Every one is ignored, by the sweep and by the rays.
    /// </summary>
    public void Ignore(PhysicsBody3D body)
    {
        AddCollisionExceptionWith(body);
        if (!_ignored.Contains(body.GetRid())) _ignored.Add(body.GetRid());
    }

    public void Unignore(PhysicsBody3D body)
    {
        RemoveCollisionExceptionWith(body);
        _ignored.Remove(body.GetRid());
    }

    public float GroundY(NVec3 from)
    {
        var top = new Vector3(from.X, from.Y + RayLift, from.Z);
        var q = PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * (RayLift + RayReach));
        q.CollisionMask = CollisionLayers.World;
        q.Exclude = _ignored;
        var r = GetWorld3D().DirectSpaceState.IntersectRay(q);
        return r.Count == 0 ? float.NegativeInfinity : r["position"].AsVector3().Y;
    }

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
