using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The two grips of a handlebar, measured from the model's own <c>steer_bars</c> mesh in that node's frame (docs/standards/car-design.md 5): where the
/// driver's cockpit rig puts the hands of a quad's or a motorcycle's rider. The node's x is the vehicle's (its frame only leans back along the steering
/// axis), and nothing on it (the stem, a fork) is as wide as the bars, so the grips are its two ends in x.
/// </summary>
/// <param name="Left">The middle of the left grip (-x).</param>
/// <param name="Right">The middle of the right grip (+x).</param>
public readonly record struct HandlebarFit(Vector3 Left, Vector3 Right)
{
    /// <summary>How much of each end of the bar is the grip's end cap and how far in from it the hand's middle is, metres, and the narrowest and widest bar a rider holds: outside it the mesh is not a handlebar.</summary>
    public const float End = 0.04f, Inset = 0.04f, MinWidth = 0.35f, MaxWidth = 1.3f;

    /// <summary>The grips a handlebar's vertices describe, or null when they are not a handlebar (too few, or not that wide).</summary>
    public static HandlebarFit? From(IReadOnlyList<Vector3> bars)
    {
        if (bars.Count < 6) return null;
        float x0 = float.MaxValue, x1 = float.MinValue;
        foreach (var p in bars) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); }
        if (x1 - x0 < MinWidth || x1 - x0 > MaxWidth) return null;
        Vector3 left = Vector3.Zero, right = Vector3.Zero;
        int nl = 0, nr = 0;
        foreach (var p in bars)
        {
            if (p.X <= x0 + End) { left += p; nl++; }
            if (p.X >= x1 - End) { right += p; nr++; }
        }
        // the hand sits on the grip, not on its end cap
        return new HandlebarFit(left / nl + new Vector3(Inset, 0, 0), right / nr - new Vector3(Inset, 0, 0));
    }

    /// <summary>
    /// Where the rider's elbows rest, between them, in the hull frame: under the eye and a little behind it (the rider leans to the bars, the arms reach
    /// forward and down from the shoulders).
    /// </summary>
    public static Vector3 Elbows(Vector3 eye) => new(eye.X, eye.Y - 0.42f, eye.Z + 0.12f);
}
