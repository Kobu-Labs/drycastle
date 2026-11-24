namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>The nearest-rank percentile of values sorted ascending; 0 when there are none.</summary>
internal static class Percentile
{
    public static double Of(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
}
