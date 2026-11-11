using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Distance culling for client-only effects (tracers, sparks, splashes; #169). An effect is built only when the listener
/// (the camera) is within <see cref="EffectRange"/> of the effect's span, so far-away shots cost no nodes or tweens.
/// Sounds have their own range (<see cref="SoundRules.Audible"/>).
/// </summary>
public static class EffectCull
{
    /// <summary>Metres from the camera within which a small effect is built (the longest shot is 300 m).</summary>
    public const float EffectRange = 300f;

    /// <summary>Shortest distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var len2 = Vector3.Dot(ab, ab);
        var t = len2 <= 1e-12f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>True when the segment <paramref name="a"/>-<paramref name="b"/> comes within <paramref name="range"/> of the listener.</summary>
    public static bool Near(Vector3 listener, Vector3 a, Vector3 b, float range = EffectRange) => DistanceToSegment(listener, a, b) <= range;
}
