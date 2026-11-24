using System.Numerics;

namespace Drycastle.ShooterCore.Events;

/// <summary>Ballistic solutions: the launch velocity of an arc under gravity.</summary>
public static partial class Ballistics
{
    /// <summary>
    /// The velocity of speed <paramref name="speed"/> that carries a body from <paramref name="from"/> to <paramref name="to"/> under gravity
    /// <paramref name="g"/> (m/s², pulling down y). The flat (low) arc, or the lob with <paramref name="lob"/>. False when the target is out of reach at that speed.
    /// <paramref name="flightTime"/> is the time of flight.
    /// </summary>
    public static bool Solve(Vector3 from, Vector3 to, float speed, float g, bool lob, out Vector3 vel, out float flightTime)
    {
        vel = default; flightTime = 0;
        var dx = to.X - from.X; var dz = to.Z - from.Z; var dy = to.Y - from.Y;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        var v2 = speed * speed;
        if (d < 1e-3f)
        {
            // straight above or below
            if (dy <= 0) { vel = new Vector3(0, -speed, 0); flightTime = (-speed + MathF.Sqrt(v2 - 2 * g * dy)) / g; return true; }
            if (v2 < 2 * g * dy) return false;
            vel = new Vector3(0, speed, 0); flightTime = (speed - MathF.Sqrt(v2 - 2 * g * dy)) / g; return true;
        }
        var disc = v2 * v2 - g * (g * d * d + 2 * dy * v2);
        if (disc < 0) return false;
        var root = MathF.Sqrt(disc);
        var tan = (v2 + (lob ? root : -root)) / (g * d);
        var cos = 1f / MathF.Sqrt(1 + tan * tan);
        var sin = tan * cos;
        vel = new Vector3(dx / d * speed * cos, speed * sin, dz / d * speed * cos);
        flightTime = d / (speed * cos);
        return true;
    }

    /// <summary>
    /// The launch velocity at a moving target: solves for where the target will be at the arc's flight time, <paramref name="lead"/> (0..1) of the way
    /// (twice, which settles for the speeds in play). False when it is out of reach.
    /// </summary>
    public static bool Aim(Vector3 from, Vector3 target, Vector3 targetVel, float speed, float g, float lead, out Vector3 vel)
    {
        var aim = target;
        vel = default;
        for (var i = 0; i < 3; i++)
        {
            if (!Solve(from, aim, speed, g, false, out vel, out var t)) return false;
            aim = target + targetVel * (t * lead);
        }
        return Solve(from, aim, speed, g, false, out vel, out _);
    }
}
