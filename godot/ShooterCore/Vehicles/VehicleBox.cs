using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// A ray hit on a vehicle's hit box, in the vehicle's own frame: the node frame of the vehicle (origin on the ground under its centre,
/// x right, y up, z back, forward is -Z). <see cref="Local"/> and <see cref="LocalNormal"/> let a client pin a decal to the body, so it
/// rides the vehicle as it moves.
/// </summary>
public readonly record struct VehicleBoxHit(float Distance, Vector3 Local, Vector3 LocalNormal);

/// <summary>
/// The hit volume of a vehicle: an upright box over its footprint, from the ground (y 0) to <c>height</c>, with the half extents of
/// vehicles.json. The server's physics box and the client's bullet holes both use these numbers, so a ray that the server stops on a car
/// is the same ray the client draws the hole on.
/// </summary>
public static class VehicleBox
{
    /// <summary>
    /// Metres from <paramref name="origin"/> along <paramref name="dir"/> (any length; only its direction counts) to where the ray first
    /// enters the box, or null: a miss, a hit beyond <paramref name="maxDistance"/>, or an origin already inside the box (as a physics
    /// ray reports nothing from inside a shape).
    /// </summary>
    public static VehicleBoxHit? Ray(Vector3 origin, Vector3 dir, float maxDistance, Vector3 feet, float yaw, float halfWidth, float halfLength, float height)
    {
        var len = dir.Length();
        if (len < 1e-6f) return null;
        dir /= len;
        // node frame: x = right, z = back (the vehicle's local +Z), as Godot's yaw rotation maps them
        var s = MathF.Sin(yaw); var c = MathF.Cos(yaw);
        var right = new Vector3(c, 0, -s);
        var back = new Vector3(s, 0, c);
        var o = origin - feet;
        var lo = new Vector3(Vector3.Dot(o, right), o.Y, Vector3.Dot(o, back));
        var ld = new Vector3(Vector3.Dot(dir, right), dir.Y, Vector3.Dot(dir, back));
        var min = new Vector3(-halfWidth, 0, -halfLength);
        var max = new Vector3(halfWidth, height, halfLength);

        var tEnter = float.NegativeInfinity; var tExit = float.PositiveInfinity;
        var enterAxis = -1; var enterSign = 0f;
        for (var axis = 0; axis < 3; axis++)
        {
            var p = Get(lo, axis); var d = Get(ld, axis); var lmin = Get(min, axis); var lmax = Get(max, axis);
            if (MathF.Abs(d) < 1e-7f)
            {
                if (p < lmin || p > lmax) return null; // parallel to this slab and outside it
                continue;
            }
            var t1 = (lmin - p) / d; var t2 = (lmax - p) / d;
            var near = MathF.Min(t1, t2); var far = MathF.Max(t1, t2);
            if (near > tEnter) { tEnter = near; enterAxis = axis; enterSign = d > 0 ? -1f : 1f; }
            tExit = MathF.Min(tExit, far);
            if (tEnter > tExit) return null;
        }
        if (tExit < 0 || enterAxis < 0 || tEnter < 0) return null; // behind the origin, or the origin is inside the box
        if (tEnter > maxDistance) return null;

        var local = lo + ld * tEnter;
        var ln = enterAxis switch { 0 => new Vector3(enterSign, 0, 0), 1 => new Vector3(0, enterSign, 0), _ => new Vector3(0, 0, enterSign) };
        return new VehicleBoxHit(tEnter, local, ln);
    }

    /// <summary>
    /// Metres along a ray that crosses the box to where it leaves it. The ray is in the box's own frame (<see cref="GlassRules.ToVehicle"/>)
    /// and <paramref name="dir"/> is a unit vector. For a ray that misses the box the number means nothing.
    /// </summary>
    public static float ExitDistance(Vector3 origin, Vector3 dir, float halfWidth, float halfLength, float height)
    {
        var min = new Vector3(-halfWidth, 0, -halfLength);
        var max = new Vector3(halfWidth, height, halfLength);
        var tExit = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = Get(dir, axis);
            if (MathF.Abs(d) < 1e-7f) continue;
            var p = Get(origin, axis);
            tExit = MathF.Min(tExit, MathF.Max((Get(min, axis) - p) / d, (Get(max, axis) - p) / d));
        }
        return tExit;
    }

    static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
