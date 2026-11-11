using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Street lamp maths, pure and scene-free. The shooter's <c>StreetLamps</c> node feeds it the night factor, the
/// lamp positions and the camera, and applies the results to its lights and materials.
/// </summary>
public static class LampCurve
{
    /// <summary>Night factor where the lamps start to glow, and where they are fully on.</summary>
    public const float OnFrom = 0.2f, OnFull = 0.6f;

    /// <summary>0 while it is day, 1 at full night; smooth, so lamps never flicker through dusk and dawn.</summary>
    public static float Level(float nightFactor) => DayCurve.Smoothstep(OnFrom, OnFull, nightFactor);
}

public static class LampPool
{
    /// <summary>Lamps closer than this share the full light energy; the fade runs from 60% to 100% of the range.</summary>
    public const float FullFraction = 0.6f;

    /// <summary>
    /// Points the light slots at the nearest lamps to <paramref name="eye"/> within <paramref name="maxDistance"/>.
    /// <c>slots[i]</c> is a lamp index, or -1 for an empty slot. A slot keeps its lamp while that lamp is still
    /// among the nearest, so lights do not hop between lamps when the camera moves. Slots that get a new lamp
    /// are the only ones that change. Returns true if any slot changed its lamp.
    /// </summary>
    public static bool Refresh(int[] slots, IReadOnlyList<Vector3> lamps, Vector3 eye, float maxDistance)
    {
        int k = slots.Length;
        if (k == 0) return false;
        Span<int> top = stackalloc int[k];
        Span<float> topD = stackalloc float[k];
        int n = 0;
        float maxSq = maxDistance * maxDistance;
        for (int i = 0; i < lamps.Count; i++)
        {
            float d = Vector3.DistanceSquared(lamps[i], eye);
            if (d > maxSq) continue;
            if (n == k && d >= topD[k - 1]) continue;
            int at = n < k ? n++ : k - 1;
            while (at > 0 && topD[at - 1] > d)
            {
                topD[at] = topD[at - 1];
                top[at] = top[at - 1];
                at--;
            }
            topD[at] = d;
            top[at] = i;
        }

        Span<bool> inTop = stackalloc bool[n];
        Span<bool> kept = stackalloc bool[k];
        for (int s = 0; s < k; s++)
        {
            if (slots[s] < 0) continue;
            for (int j = 0; j < n; j++)
            {
                if (top[j] != slots[s] || inTop[j]) continue;
                inTop[j] = true;
                kept[s] = true;
                break;
            }
        }

        bool changed = false;
        int next = 0;
        for (int s = 0; s < k; s++)
        {
            if (kept[s]) continue;
            while (next < n && inTop[next]) next++;
            int want = next < n ? top[next] : -1;
            if (next < n) { inTop[next] = true; next++; }
            if (slots[s] != want) { slots[s] = want; changed = true; }
        }
        return changed;
    }

    /// <summary>
    /// Light energy for a lamp at <paramref name="distance"/>: <paramref name="level"/> (from <see cref="LampCurve"/>)
    /// scaled down to zero between 60% and 100% of <paramref name="maxDistance"/>.
    /// </summary>
    public static float Energy(float level, float distance, float maxDistance)
    {
        if (maxDistance <= 0f) return 0f;
        float far = 1f - DayCurve.Smoothstep(FullFraction * maxDistance, maxDistance, distance);
        return level * far;
    }
}
