using Godot;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter;

/// <summary>
/// The wheel animation of every ground vehicle (#295, car-design section 3): every wheel the model names (<see cref="WheelMath.Layouts"/>) rolls about its own X
/// over its own radius, every <c>_steer</c> parent turns about its LOCAL Y (a raked fork's tilted axis too), and <c>steer_bars</c> follow the steer angle. A driven
/// vehicle (a <c>drive</c> block: all of them with seats) is turned from its drive state (<see cref="StepDriven"/>: the speed and the front wheels' angle, by
/// <c>DriveNodes</c>); <see cref="Step"/> reads the same from the DRAWN motion for a model with no state. The Scud and the trailer keep their own. Allocates once,
/// when the model is found; a frame allocates nothing.
/// </summary>
sealed class VehicleWheels
{
    readonly Node3D[] _wheels;
    readonly Basis[] _wheelRest;
    readonly float[] _radius;
    readonly Node3D?[] _steer;
    readonly Basis[] _steerRest;
    readonly float[] _arm;
    readonly Node3D? _bars;
    readonly Basis _barsRest;
    readonly float _lock;
    readonly float[] _rolled;
    float _speed, _curvature, _lastX, _lastZ, _lastYaw;
    bool _hasLast;

    VehicleWheels(Node3D[] wheels, Node3D?[] steer, float[] radius, float[] arm, Node3D? bars, float lockRad)
    {
        _wheels = wheels; _steer = steer; _radius = radius; _arm = arm; _bars = bars; _lock = lockRad;
        _wheelRest = new Basis[wheels.Length]; _rolled = new float[wheels.Length]; _steerRest = new Basis[wheels.Length];
        for (var i = 0; i < wheels.Length; i++) { _wheelRest[i] = wheels[i].Basis; if (steer[i] != null) _steerRest[i] = steer[i]!.Basis; }
        if (bars != null) _barsRest = bars.Basis;
    }

    /// <summary>The model's wheels by the layout table, or null when it has none. <paramref name="lockRad"/> is the steering lock (0 for the default).</summary>
    public static VehicleWheels? Find(Node3D root, float lockRad = 0f)
    {
        var wheels = new List<Node3D>();
        foreach (var name in WheelMath.AllNames()) if (root.FindChild(name, true, false) is Node3D w) wheels.Add(w);
        if (wheels.Count == 0) return null;
        var n = wheels.Count;
        var steer = new Node3D?[n]; var radius = new float[n]; var z = new float[n]; var steered = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var at = RestIn(root, wheels[i]).Origin;
            radius[i] = at.Y > WheelMath.MinRadius ? at.Y : WheelMath.FallbackRadius; // the axle's height at rest is the radius (through the steer parent too)
            z[i] = at.Z;
            if (wheels[i].GetParent() is Node3D p && p != root && p.Name.ToString() == wheels[i].Name + WheelMath.SteerSuffix) { steer[i] = p; steered[i] = true; }
        }
        var pivot = WheelMath.PivotZ(z, steered);
        var arm = new float[n];
        for (var i = 0; i < n; i++) arm[i] = MathF.Max(0f, pivot - z[i]); // how far this axle is ahead of the rear axle (-Z is forward)
        Node3D? bars = root.FindChild("steer_bars", true, false) as Node3D;
        if (bars != null) for (Node? a = bars.GetParent(); a != null && a != root; a = a.GetParent()) if (a.Name.ToString().EndsWith(WheelMath.SteerSuffix)) { bars = null; break; } // under a steer parent it turns with the fork
        return new VehicleWheels(wheels.ToArray(), steer, radius, arm, bars, lockRad > 0 ? lockRad : WheelMath.DefaultLock);
    }

    /// <summary>The node's transform at rest in the model's root frame (parents summed up by hand: the model may not be in the tree yet).</summary>
    static Transform3D RestIn(Node3D root, Node3D node)
    {
        var t = Transform3D.Identity;
        for (var n = node; n != null && n != root; n = n.GetParent() as Node3D) t = n.Transform * t;
        return t;
    }

    /// <summary>
    /// One frame of a driven vehicle, from its drive state: the speed along the heading (m/s) and the angle of the front wheels (radians, about Y). A steered
    /// axle behind the first turns by its own share (its distance ahead of the fixed axle over the first's: an eight-wheeler's second axle turns less).
    /// </summary>
    public void StepDriven(float speed, float frontAngle, float dt)
    {
        var reach = 0f;
        for (var i = 0; i < _arm.Length; i++) if (_steer[i] != null) reach = MathF.Max(reach, _arm[i]);
        var tan = MathF.Tan(frontAngle);
        for (var i = 0; i < _wheels.Length; i++)
        {
            if (!GodotObject.IsInstanceValid(_wheels[i])) continue;
            _rolled[i] = Mathf.Wrap(_rolled[i] + WheelMath.RollDelta(speed, _radius[i], dt), -Mathf.Pi, Mathf.Pi);
            _wheels[i].Basis = new Basis(Vector3.Right, _rolled[i]) * _wheelRest[i];
            if (_steer[i] is { } p && GodotObject.IsInstanceValid(p))
                p.Basis = _steerRest[i] * new Basis(Vector3.Up, reach > 0 && _arm[i] < reach ? MathF.Atan(_arm[i] / reach * tan) : frontAngle);
        }
        if (_bars != null && GodotObject.IsInstanceValid(_bars)) _bars.Basis = _barsRest * new Basis(Vector3.Up, frontAngle);
    }

    /// <summary>One frame, from where and how the vehicle is drawn (its root's place and yaw).</summary>
    public void Step(Vector3 pos, float yaw, float dt)
    {
        if (dt < 1e-4f) return;
        if (_hasLast)
        {
            var (speed, yawRate) = WheelMath.Motion(pos.X - _lastX, pos.Z - _lastZ, yaw, yaw - _lastYaw, dt);
            _speed = WheelMath.Ease(_speed, Mathf.Clamp(speed, -40f, 40f), 1f / 0.08f, dt);
            _curvature = WheelMath.Ease(_curvature, Mathf.Clamp(WheelMath.Curvature(yawRate, _speed), -0.4f, 0.4f), 8f, dt);
        }
        _lastX = pos.X; _lastZ = pos.Z; _lastYaw = yaw; _hasLast = true;
        var front = 0f;
        for (var i = 0; i < _wheels.Length; i++)
        {
            if (!GodotObject.IsInstanceValid(_wheels[i])) continue;
            // each wheel keeps its own roll: its radius is its own (the bike's two differ)
            _rolled[i] = Mathf.Wrap(_rolled[i] + WheelMath.RollDelta(_speed, _radius[i], dt), -Mathf.Pi, Mathf.Pi);
            _wheels[i].Basis = new Basis(Vector3.Right, _rolled[i]) * _wheelRest[i]; // about the parent's X over the rest pose, whichever way the model's wheel is turned
            if (_steer[i] is { } p && GodotObject.IsInstanceValid(p))
            {
                var a = WheelMath.SteerAngle(_arm[i], _curvature, _lock);
                p.Basis = _steerRest[i] * new Basis(Vector3.Up, a); // about the parent's own Y: a raked fork turns about its tilted axis
                if (MathF.Abs(a) > MathF.Abs(front)) front = a;
            }
        }
        if (_bars != null && GodotObject.IsInstanceValid(_bars)) _bars.Basis = _barsRest * new Basis(Vector3.Up, front);
    }
}
