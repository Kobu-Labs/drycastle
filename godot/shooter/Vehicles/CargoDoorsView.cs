using Godot;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Vehicles;

/// <summary>
/// The swinging doors of one vehicle's cargo area as this client draws them (vehicles.json cargo_volume "doors": the car's boot lid, the Golf's hatch, the van's two rear doors; the nodes' origins are on
/// their hinge lines, docs/standards/car-design.md node table). <see cref="Find"/> takes the nodes the model has (a build without the private pack has none of a pack model's: then there is no view and
/// nothing swings); <see cref="SetOpen"/> says where they should stand and <see cref="Step"/> (once a frame, <c>VehiclesClient.RenderCar</c>) swings them there in <see cref="CargoDoorSwing.Seconds"/>.
/// A door turns about its own local axis over its rest pose, by its open angle times the eased swing. Allocates once, when the model is found; a frame allocates nothing.
/// </summary>
sealed class CargoDoorsView
{
    readonly Node3D[] _nodes;
    readonly Basis[] _rest;
    readonly Vector3[] _axis;
    readonly float[] _openRad;
    readonly CargoDoorSwing _swing = new();

    CargoDoorsView(Node3D[] nodes, Vector3[] axis, float[] openRad)
    {
        _nodes = nodes; _axis = axis; _openRad = openRad;
        _rest = new Basis[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) _rest[i] = nodes[i].Basis;
    }

    /// <summary>The doors <paramref name="def"/> declares that the model <paramref name="root"/> has, or null when the type has none or the model has none of them (they stay shut, nothing is drawn wrong).</summary>
    public static CargoDoorsView? Find(Node3D root, VehicleDef def)
    {
        if (def.CargoVolume is not { Doors.Count: > 0 } cargo) return null;
        var nodes = new List<Node3D>(); var axis = new List<Vector3>(); var open = new List<float>();
        foreach (var d in cargo.Doors)
        {
            if (root.FindChild(d.Node, true, false) is not Node3D n) continue;
            nodes.Add(n);
            axis.Add(new Vector3(d.Axis[0], d.Axis[1], d.Axis[2]).Normalized());
            open.Add(Mathf.DegToRad(d.OpenDeg));
        }
        return nodes.Count == 0 ? null : new CargoDoorsView(nodes.ToArray(), axis.ToArray(), open.ToArray());
    }

    /// <summary>Wants the doors open or closed; they swing there. <paramref name="instant"/>: they stand there at once (a vehicle that comes into view already open).</summary>
    public void SetOpen(bool open, bool instant = false)
    {
        _swing.SetOpen(open, instant);
        if (instant) Apply();
    }

    /// <summary>One frame: the doors swing on toward their end (nothing is touched while they stand still).</summary>
    public void Step(float dt)
    {
        if (_swing.Step(dt)) Apply();
    }

    void Apply()
    {
        var eased = _swing.Eased;
        for (var i = 0; i < _nodes.Length; i++)
            if (GodotObject.IsInstanceValid(_nodes[i])) _nodes[i].Basis = _rest[i] * new Basis(_axis[i], _openRad[i] * eased);
    }
}
