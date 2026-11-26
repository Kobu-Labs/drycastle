namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>Timing budgets of the micro-benchmarks: the contract's numbers in Release, loose in Debug (no JIT optimisation, about 3x slower).</summary>
internal static class Budget
{
#if DEBUG
    public const bool Strict = false;
#else
    public const bool Strict = true;
#endif

    /// <summary><paramref name="releaseMs"/> in Release; in Debug ten times it, at least 10 ms.</summary>
    public static double Ms(double releaseMs) => Strict ? releaseMs : Math.Max(10, releaseMs * 10);
}
