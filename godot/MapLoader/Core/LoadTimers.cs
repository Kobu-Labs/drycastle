using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Drycastle.MapLoader.Core;

/// <summary>
/// Load-time accounting: named sums of elapsed time (main thread and workers; thread-safe), for the join-time report the shooter prints after
/// a join when DRYCASTLE_LOAD_TIMES=1 (docs/perf-investigation/join-time.md). A lap costs two timestamps and a lock.
/// </summary>
public static class LoadTimers
{
    static readonly Dictionary<string, (long Ticks, int Count)> Sums = new();
    static readonly List<string> Order = new();

    /// <summary>Whether the shooter prints the report (DRYCASTLE_LOAD_TIMES=1).</summary>
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("DRYCASTLE_LOAD_TIMES") == "1";

    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>Stopwatch ticks in milliseconds.</summary>
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Adds the time since <paramref name="since"/> to <paramref name="key"/>; returns now, for the next lap.</summary>
    public static long Lap(string key, long since)
    {
        var now = Stopwatch.GetTimestamp();
        lock (Sums)
        {
            if (!Sums.TryGetValue(key, out var v)) Order.Add(key);
            Sums[key] = (v.Ticks + now - since, v.Count + 1);
        }
        return now;
    }

    public static string Report() { lock (Sums) return ReportLocked(); }

    static string ReportLocked() => string.Join("\n", Order.Select(k => $"loadtime {k} {Sums[k].Ticks * 1000.0 / Stopwatch.Frequency:F0} ms x{Sums[k].Count}"));
}
