using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Pure numbers for the water look (the "water visuals and audio" ticket, #142) that the shooter still uses: splashes on entering and leaving the water, and the swim pose. No Godot here.
/// </summary>
public static class UnderwaterFx
{
    /// <summary>Body lean while swimming, degrees forward (the remote swim pose).</summary>
    public const float SwimLeanDegrees = 65f;

    /// <summary>Entering or leaving the water between two ticks (or frames). Splashes play on the change only.</summary>
    public enum Crossing { None, Enter, Leave }

    public static Crossing WaterCrossing(bool wasIn, bool nowIn) => (wasIn, nowIn) switch
    {
        (false, true) => Crossing.Enter,
        (true, false) => Crossing.Leave,
        _ => Crossing.None,
    };

    /// <summary>
    /// The first point where the segment <paramref name="a"/> to <paramref name="b"/> passes through a water surface inside
    /// a body (a bullet or rocket going into the lake). False when the segment never meets water.
    /// </summary>
    public static bool FirstSurfaceCrossing(Water water, Vector3 a, Vector3 b, out Vector3 hit)
    {
        hit = Vector3.Zero;
        var found = false;
        var bestT = float.MaxValue;
        foreach (var body in water.Bodies)
        {
            var da = a.Y - body.SurfaceY;
            var db = b.Y - body.SurfaceY;
            if (da == db || da * db > 0f) continue; // no crossing: both ends on one side of the plane
            var t = da / (da - db);
            if (t < 0f || t > 1f || t >= bestT) continue;
            var p = a + (b - a) * t;
            if (!body.Contains(p.X, p.Z)) continue;
            bestT = t;
            hit = p;
            found = true;
        }
        return found;
    }
}
