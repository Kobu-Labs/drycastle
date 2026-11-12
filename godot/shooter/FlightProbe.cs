using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The physics space as <see cref="FlightModel"/> sees it (<see cref="IFlightWorld"/>): a hidden kinematic body with the helicopter's hull box and
/// its rotor disc, moved through the solid world (layer 1: ground, buildings, tree trunks, placed solids) to find what stops it. The server has one
/// per flying vehicle, the pilot's client one for its prediction, and both ask the same questions of the same colliders (ADR 0006). It is on no
/// layer itself, so nothing hits it: bullets meet the vehicle's own hit box, which has no rotor. Shot-only cover (tree crowns, bushes) does not
/// stop a helicopter, as it does not stop a car.
/// </summary>
public sealed partial class FlightProbe : CharacterBody3D, IFlightWorld
{
    /// <summary>The ground ray starts this far above the origin, so ground the skids have sunk a little into is still found.</summary>
    const float ProbeLift = 1f;

    VehicleDef _def = null!;
    Water _water = Water.Empty;
    float _reach;

    public FlightBounds Bounds { get; private set; } = FlightBounds.None;

    public static FlightProbe Make(VehicleDef def, Water water, FlightBounds bounds) =>
        new() { Name = "FlightProbe", _def = def, _water = water, Bounds = bounds, TopLevel = true };

    public override void _Ready()
    {
        var f = _def.Flight!;
        CollisionLayer = 0;
        CollisionMask = CollisionLayers.World;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(_def.HalfWidth * 2, _def.Height, _def.HalfLength * 2) }, Position = new Vector3(0, _def.Height / 2, 0) });
        AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = f.RotorRadius, Height = f.RotorThickness }, Position = new Vector3(0, f.RotorHeight, 0) });
        _reach = f.Ceiling + f.CeilingFade + 20f;
        SetPhysicsProcess(false);
    }

    readonly Godot.Collections.Array<Rid> _ignored = new();

    /// <summary>
    /// Player bodies are on the world's layer, and they are not part of the world a helicopter flies through: riders sit inside the hull, and the
    /// pilot's client knows no body but its own. Every one is ignored, by the sweep and by the ground ray, as the vehicles ignore them.
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

    public float GroundY(NVec3 position)
    {
        var from = new Vector3(position.X, position.Y + ProbeLift, position.Z);
        var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * (_reach + ProbeLift));
        q.CollisionMask = CollisionLayers.World;
        q.Exclude = _ignored;
        var r = GetWorld3D().DirectSpaceState.IntersectRay(q);
        return r.Count == 0 ? float.NegativeInfinity : r["position"].AsVector3().Y;
    }

    public float WaterY(float x, float z) => _water.BodyAt(x, z)?.SurfaceY ?? float.NegativeInfinity;

    public FlightMove Move(NVec3 from, float yaw, NVec3 motion)
    {
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(from.X, from.Y, from.Z));
        var hit = MoveAndCollide(new Vector3(motion.X, motion.Y, motion.Z));
        var at = GlobalPosition;
        if (hit == null) return new FlightMove(new NVec3(at.X, at.Y, at.Z), false, default);
        var n = hit.GetNormal();
        return new FlightMove(new NVec3(at.X, at.Y, at.Z), true, new NVec3(n.X, n.Y, n.Z));
    }

    /// <summary>True when the hull and the rotor disc fit at <paramref name="at"/> without touching anything solid (a spawn spot between houses).</summary>
    public bool Fits(Vector3 at, float yaw)
    {
        var xf = new Transform3D(new Basis(Vector3.Up, yaw), at + Vector3.Up * 0.3f);
        return !TestMove(xf, Vector3.Zero, null, 0.001f, true);
    }

    /// <summary>The map's flyable rectangle: the box around every parcel (the pipeline's parcels cover the whole map). No map: no edge.</summary>
    public static FlightBounds MapBounds(Drycastle.MapLoader.MapLoader? map)
    {
        if (map == null) return FlightBounds.None;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (var p in map.Parcels.Values)
        {
            if (p.Rings.Count == 0) continue;
            minX = MathF.Min(minX, p.Bounds.Position.X); maxX = MathF.Max(maxX, p.Bounds.End.X);
            minZ = MathF.Min(minZ, p.Bounds.Position.Y); maxZ = MathF.Max(maxZ, p.Bounds.End.Y);
        }
        return minX < maxX && minZ < maxZ ? new FlightBounds(minX, maxX, minZ, maxZ) : FlightBounds.None;
    }
}
