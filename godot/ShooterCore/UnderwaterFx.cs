using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Pure numbers for the water look and sound (the "water visuals and audio" ticket, #142). No Godot here: the client
/// reads these each frame and pushes them into shaders, the audio bus and the particle nodes.
/// </summary>
public static class UnderwaterFx
{
    /// <summary>Metres below the surface at which the camera is fully underwater (the ramp starts at 0).</summary>
    public const float FullMetres = 0.6f;
    /// <summary>Scene-depth distance over which the water surface fades out at the shore (metres).</summary>
    public const float ShoreFadeMetres = 1.2f;
    /// <summary>Low-pass cutoff on the SFX bus when fully underwater (muffled), and when open air.</summary>
    public const float MuffledCutoffHz = 900f, OpenCutoffHz = 20500f;
    /// <summary>Body lean while swimming, degrees forward (the remote swim pose).</summary>
    public const float SwimLeanDegrees = 65f;

    static readonly Vector3 DayTint = new(0.16f, 0.40f, 0.46f), NightTint = new(0.02f, 0.06f, 0.10f);

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>0 at the surface, 1 at <see cref="FullMetres"/> below it (smoothstep, so the change is gentle). Negative depth (above the surface) gives 0.</summary>
    public static float Amount(float depthBelowSurface)
    {
        var t = Clamp01(depthBelowSurface / FullMetres);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Alpha of the surface at a point: 0 where the water is as thin as the shore, 1 once it is <see cref="ShoreFadeMetres"/> out.</summary>
    public static float ShoreFade(float waterDistance) => Clamp01(waterDistance / ShoreFadeMetres);

    /// <summary>Underwater tint colour (linear RGB): teal by day, near black-blue at night. <paramref name="nightFactor"/> is DayNightCycle's 0..1.</summary>
    public static Vector3 Tint(float nightFactor) => Vector3.Lerp(DayTint, NightTint, Clamp01(nightFactor));

    /// <summary>Low-pass cutoff for the SFX bus at an underwater <paramref name="amount"/> (0 open air, 1 fully under).</summary>
    public static float CutoffHz(float amount) => OpenCutoffHz + (MuffledCutoffHz - OpenCutoffHz) * Clamp01(amount);

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
