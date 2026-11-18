namespace Drycastle.ShooterCore;

/// <summary>
/// The FPS counter's text (#340). The engine's frame rate is sampled at most <see cref="Interval"/> seconds apart (2 a
/// second), so the number is readable and the label is not rebuilt every frame. Pure: the caller passes the clock and the rate.
/// </summary>
public sealed class FpsReadout
{
    public const double Interval = 0.5;
    /// <summary>Widest text, for the chip's fixed width so the chip never changes size as the number does: "999 FPS".</summary>
    public const int MaxFps = 999;

    double _next = double.NegativeInfinity;

    /// <summary>The text to show now, or null while the last sample still stands. The first call always samples.</summary>
    public string? Sample(double now, double fps)
    {
        if (now < _next) return null;
        _next = now + Interval;
        return Format(fps);
    }

    /// <summary>Restarts the gate so the next <see cref="Sample"/> reads at once (the counter was just switched on).</summary>
    public void Reset() => _next = double.NegativeInfinity;

    public static string Format(double fps) => $"{Math.Clamp((int)Math.Round(fps), 0, MaxFps)} FPS";
}
