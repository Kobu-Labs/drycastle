using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// A steering wheel measured from the model's own <c>steering_wheel_rim</c> mesh (docs/standards/car-design.md 5), in the hull frame (x right, y up,
/// forward is -Z): what the driver's cockpit rig needs to draw a turning wheel and the hands on it for a vehicle that has no hand-built rig. The
/// wheel's axis lies in the y-z plane (a wheel faces the driver, not a door), so the fit is a line through the rim's points seen from the side.
/// </summary>
/// <param name="Centre">The middle of the rim.</param>
/// <param name="Radius">To the middle of the rim's tube, metres.</param>
/// <param name="LeanDeg">How far the wheel's plane leans from level towards the driver: 0 lies flat (axis up), 90 stands upright (axis at the driver).</param>
public readonly record struct SteeringWheelFit(Vector3 Centre, float Radius, float LeanDeg)
{
    /// <summary>Half the thickness of a rim's tube: the fit measures the rim's outer edge, the rig draws from its middle.</summary>
    const float Tube = 0.02f;

    /// <summary>The smallest and largest wheel a driver holds, metres to the outer edge: outside it the mesh is not a steering wheel's rim.</summary>
    public const float MinRadius = 0.10f, MaxRadius = 0.45f;

    /// <summary>The wheel a rim's vertices describe, or null when they are not a rim (too few, or not wheel-sized).</summary>
    public static SteeringWheelFit? From(IReadOnlyList<Vector3> rim)
    {
        if (rim.Count < 6) return null;
        var mean = Vector3.Zero;
        foreach (var p in rim) mean += p;
        mean /= rim.Count;
        float yy = 0, zz = 0, yz = 0;
        foreach (var p in rim)
        {
            var d = p - mean;
            yy += d.Y * d.Y; zz += d.Z * d.Z; yz += d.Y * d.Z;
        }
        // seen from the side the rim is a line: its direction is the wheel's plane, the normal to it the wheel's axis
        var along = 0.5f * MathF.Atan2(2 * yz, yy - zz);
        var u = new Vector3(0, MathF.Cos(along), MathF.Sin(along));
        var n = new Vector3(0, -u.Z, u.Y);
        if (n.Y + n.Z < 0) n = -n; // out of the wheel's face: up and back, at the driver
        float x0 = float.MaxValue, x1 = float.MinValue, u0 = x0, u1 = x1, n0 = x0, n1 = x1;
        foreach (var p in rim)
        {
            var d = p - mean;
            var (a, b, c) = (d.X, Vector3.Dot(d, u), Vector3.Dot(d, n));
            x0 = MathF.Min(x0, a); x1 = MathF.Max(x1, a);
            u0 = MathF.Min(u0, b); u1 = MathF.Max(u1, b);
            n0 = MathF.Min(n0, c); n1 = MathF.Max(n1, c);
        }
        var outer = (x1 - x0 + u1 - u0) / 4;
        if (outer < MinRadius || outer > MaxRadius) return null;
        var centre = mean + new Vector3((x0 + x1) / 2, 0, 0) + u * ((u0 + u1) / 2) + n * ((n0 + n1) / 2);
        return new SteeringWheelFit(centre, outer - Tube, MathF.Atan2(n.Z, n.Y) * 180 / MathF.PI);
    }

    /// <summary>
    /// Where the driver's elbows rest, between them: under the wheel and back towards the seat, never behind the eye's own shoulders (as the hand-built
    /// rigs have them: the Humvee's are 0.2 m under its wheel and 0.25 m behind its eye).
    /// </summary>
    public Vector3 Elbows(Vector3 eye) => new(Centre.X, Centre.Y - 0.22f, MathF.Min(Centre.Z + 0.75f, eye.Z + 0.25f));
}
