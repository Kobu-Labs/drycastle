using System.Diagnostics;
using Drycastle.ShooterCore.Diagnostics;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// Time and allocation of each phase of the tick (always on: a timestamp and a list add, no allocation once the lists have grown), kept for the
/// report period: the stats line's per-phase p50 and p99, the slowest tick's phases and the allocation per phase of the prof lines.
/// </summary>
internal sealed class TickPhases
{
    /// <summary>Microseconds each phase of every tick took since the last report (lists keep their capacity).</summary>
    public readonly List<double>[] Us = MakeLists();
    /// <summary>Bytes the game thread allocated in each phase since the last report.</summary>
    public readonly long[] Alloc = new long[(int)ServerPerf.Ph.Count];
    /// <summary>Stopwatch timestamp, and the thread's allocation count, at the last phase boundary of the current tick.</summary>
    long _clock, _allocAt;

    static List<double>[] MakeLists()
    {
        var lists = new List<double>[(int)ServerPerf.Ph.Count];
        for (var i = 0; i < lists.Length; i++) lists[i] = new List<double>();
        return lists;
    }

    /// <summary>Start of a tick's phases: <see cref="Lap"/> charges each phase from here.</summary>
    public void Begin()
    {
        _clock = Stopwatch.GetTimestamp();
        _allocAt = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Ends a phase of the tick.</summary>
    public void Lap(ServerPerf.Ph p)
    {
        var now = Stopwatch.GetTimestamp();
        Us[(int)p].Add((now - _clock) * 1_000_000.0 / Stopwatch.Frequency);
        _clock = now;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        Alloc[(int)p] += alloc - _allocAt;
        _allocAt = alloc;
    }

    /// <summary>Each phase's p50 and p99 in microseconds over the report period (the report's one allocation per phase, every 10 s).</summary>
    public string Summary(MetricsBatch? metrics)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Us.Length; i++)
        {
            var s = Us[i].ToArray();
            Array.Sort(s);
            if (sb.Length > 0) sb.Append(' ');
            if (s.Length > 0) metrics?.Begin("drycastle_phase", "phase", ((ServerPerf.Ph)i).ToString().ToLowerInvariant()).Field("p50_us", Percentile.Of(s, 50)).Field("p99_us", Percentile.Of(s, 99)).End();
            sb.Append(((ServerPerf.Ph)i).ToString().ToLowerInvariant()).Append(' ')
              .Append(Percentile.Of(s, 50).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
              .Append(Percentile.Of(s, 99).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The report is out: the next period starts empty.</summary>
    public void NewPeriod()
    {
        foreach (var l in Us) l.Clear();
        Array.Clear(Alloc);
    }
}
