namespace Drycastle.ShooterCore.Hud;

/// <summary>
/// The FPS counter's text (#340) and the TPS line under it: the server ticks received per second (snapshots, <c>Wire.TickRate</c>
/// is 30). Both are sampled at most <see cref="Interval"/> seconds apart (2 a second), so the numbers are readable and the labels
/// are not rebuilt every frame. Pure: the caller passes the clock, the engine's frame rate and the running snapshot count.
/// </summary>
public sealed class FpsReadout
{
    public const double Interval = 0.5;
    /// <summary>Widest text, for the chip's fixed width so the chip never changes size as the number does: "999 FPS".</summary>
    public const int MaxFps = 999;
    /// <summary>Shown until a second sample gives a rate (the first sample has no window to count over).</summary>
    public const string NoTps = "-- TPS";

    /// <summary>The two lines of the card: the frame rate and the tick rate.</summary>
    public readonly record struct Reading(string Fps, string Tps);

    double _next = double.NegativeInfinity;
    double _baseAt = double.NaN;
    long _baseTicks;

    /// <summary>The text to show now, or null while the last sample still stands. The first call always samples.</summary>
    public Reading? Sample(double now, double fps, long ticks)
    {
        if (now < _next) return null;
        _next = now + Interval;
        string tps = NoTps;
        if (!double.IsNaN(_baseAt) && ticks >= _baseTicks && now > _baseAt)
            tps = FormatTps((ticks - _baseTicks) / (now - _baseAt));
        _baseAt = now; _baseTicks = ticks;
        return new Reading(Format(fps), tps);
    }

    /// <summary>Restarts the gate so the next <see cref="Sample"/> reads at once (the counter was just switched on).</summary>
    public void Reset()
    {
        _next = double.NegativeInfinity;
        _baseAt = double.NaN;
    }

    public static string Format(double fps) => $"{Clamp(fps)} FPS";

    public static string FormatTps(double tps) => $"{Clamp(tps)} TPS";

    static int Clamp(double rate) => Math.Clamp((int)Math.Round(rate), 0, MaxFps);
}
