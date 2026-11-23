using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Flight;

/// <summary>
/// The physics space as <see cref="FlightModel"/> sees it (<see cref="IFlightWorld"/>): a hidden kinematic body with the helicopter's hull box and
/// its rotor disc, moved through the world its <see cref="MoverKind.Aircraft"/> row stops on (<see cref="CollisionRules.StaticMask"/>: ground,
/// buildings, tree trunks, placed solids, and tree crowns and bushes, which a car drives through) to find what stops it. The server has one
/// per flying vehicle, the pilot's client one for its prediction, and both ask the same questions of the same colliders (ADR 0006). It is on no
/// layer itself, so nothing hits it: bullets meet the vehicle's own hit box, which has no rotor.
/// </summary>
public sealed partial class FlightProbe : CharacterBody3D, IFlightWorld
{
    /// <summary>The ground ray starts this far above the origin, so ground the skids have sunk a little into is still found.</summary>
    const float ProbeLift = 1f;

    /// <summary>What an aircraft stops on, from the rule table.</summary>
    static readonly uint WorldMask = CollisionRules.StaticMask(MoverKind.Aircraft);
    /// <summary>What its sweep stops on: the world, crowns and bushes, and the demon's body.</summary>
    static readonly uint SweepMask = CollisionRules.SweepMask(MoverKind.Aircraft);

    VehicleDef _def = null!;
    Water _water = Water.Empty;
    float _reach;
    /// <summary>The hull's boxes (<see cref="VehicleDef.HullBoxes"/>) and where each lies in the level body, to be turned by the attitude on every move.</summary>
    readonly List<(CollisionShape3D Shape, Vector3 Centre)> _hull = new();

    public FlightBounds Bounds { get; private set; } = FlightBounds.None;

    public static FlightProbe Make(VehicleDef def, Water water, FlightBounds bounds) =>
        new() { Name = "FlightProbe", _def = def, _water = water, Bounds = bounds, TopLevel = true };

    public override void _Ready()
    {
        var f = _def.Flight!;
        CollisionLayer = 0;
        CollisionMask = SweepMask;
        foreach (var b in _def.HullBoxes) // the one upright box over the footprint, or the boxes of the type's hull list (they tilt: SetTilt)
        {
            var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(b.HalfWidth * 2, b.Y1 - b.Y0, b.HalfLength * 2) }, Position = new Vector3(b.Cx, (b.Y0 + b.Y1) / 2, b.Cz) };
            AddChild(shape);
            _hull.Add((shape, shape.Position));
        }
        AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = f.RotorRadius, Height = f.RotorThickness }, Position = new Vector3(0, f.RotorHeight, 0) });
        _reach = f.Ceiling + f.CeilingFade + 20f;
        SetPhysicsProcess(false);
    }

    public float GroundY(NVec3 position)
    {
        var from = new Vector3(position.X, position.Y + ProbeLift, position.Z);
        _groundRay.From = from; _groundRay.To = from + Vector3.Down * (_reach + ProbeLift);
        using var r = GetWorld3D().DirectSpaceState.IntersectRay(_groundRay); // freed here, not left to the finalizer
        return r.Count == 0 ? float.NegativeInfinity : r[HitPosition].AsVector3().Y;
    }

    /// <summary>The ground ray's query, made once (a new one per ray was an object for the finalizer each), and the key of its answer.</summary>
    readonly PhysicsRayQueryParameters3D _groundRay = new() { CollisionMask = WorldMask };
    static readonly Variant HitPosition = "position";

    public float WaterY(float x, float z) => _water.BodyAt(x, z)?.SurfaceY ?? float.NegativeInfinity;

    /// <summary>Turns the hull's boxes by the pitch and roll inside the body (which carries the yaw); the rotor disc stays level. A type with no hull list stays upright whatever it is given.</summary>
    void SetTilt(float pitch, float roll)
    {
        if (!_def.Tilts) return;
        var tilt = Basis.FromEuler(new Vector3(pitch, 0, roll));
        foreach (var (shape, centre) in _hull) shape.Transform = new Transform3D(tilt, tilt * centre);
    }

    public FlightMove Move(NVec3 from, float yaw, NVec3 motion) => Move(from, yaw, 0, 0, motion);

    public FlightMove Move(NVec3 from, float yaw, float pitch, float roll, NVec3 motion)
    {
        SetTilt(pitch, roll);
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
        SetTilt(0, 0);
        var xf = new Transform3D(new Basis(Vector3.Up, yaw), at + Vector3.Up * 0.3f);
        return !TestMove(xf, Vector3.Zero, null, 0.001f, true);
    }

    /// <summary>
    /// The map's rectangle (the helicopter's flyable one, the Raids' extent): the box around the whole terrain, so a helicopter goes wherever a
    /// player on foot can. A map without terrain: the box around its parcels. No map: no edge.
    /// </summary>
    public static FlightBounds MapBounds(Drycastle.MapLoader.MapLoader? map)
    {
        if (map == null) return FlightBounds.None;
        if (map.TerrainBox is { } t) return new FlightBounds(t.Position.X, t.End.X, t.Position.Z, t.End.Z);
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
