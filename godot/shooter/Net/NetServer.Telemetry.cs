using System.Diagnostics;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Always-on server health (perf audit T2-05, T2-06, T2-07): tick timing with overrun and catch-up handling, a receive
/// budget per peer, a rate limit on profile saves, and one log line every <see cref="ReportSeconds"/> seconds.
/// </summary>
public partial class NetServer
{
    const int ReportSeconds = 10;
    /// <summary>Packets one peer may send in one tick; excess packets are read and dropped. Inputs arrive at one packet per tick, reliable messages a handful.</summary>
    const int PacketsPerTick = 64;
    /// <summary>Profile saves (loadout, camo, copy, reset) per peer: a burst, then a steady rate.</summary>
    const double SaveBurst = 10, SavesPerSecond = 2;

    static readonly double TickMs = 1000.0 / Wire.TickRate;
    readonly List<double> _tickMs = new();
    ulong _lastTickStartUs;
    int _overruns, _skippedSnapshots, _droppedPackets, _rejectedSaves;
    double _reportClock;

    /// <summary>Microseconds each phase of every tick took since the last report (the stats line's per-phase p50 and p99). Lists keep their capacity.</summary>
    readonly List<double>[] _phaseUs = MakePhaseLists();
    /// <summary>Stopwatch timestamp of the last phase boundary of the current tick.</summary>
    long _phaseClock;
    /// <summary>The GC counters at the last report, for the report's deltas.</summary>
    int _gc0Last, _gc1Last, _gc2Last;
    long _allocLast;
    double _pauseLast;

    static List<double>[] MakePhaseLists()
    {
        var lists = new List<double>[(int)ServerPerf.Ph.Count];
        for (var i = 0; i < lists.Length; i++) lists[i] = new List<double>();
        return lists;
    }

    /// <summary>Start of a tick's phases: <see cref="PhaseLap"/> charges each phase from here.</summary>
    void PhasesBegin() => _phaseClock = Stopwatch.GetTimestamp();

    /// <summary>Ends a phase of the tick (always on, a timestamp and a list add: no allocation once the lists have grown).</summary>
    void PhaseLap(ServerPerf.Ph p)
    {
        var now = Stopwatch.GetTimestamp();
        _phaseUs[(int)p].Add((now - _phaseClock) * 1_000_000.0 / Stopwatch.Frequency);
        _phaseClock = now;
    }

    /// <summary>True when this tick started right after the previous one, i.e. the engine is catching up after a slow tick.</summary>
    bool CatchUpTick(ulong startUs)
    {
        var catchUp = _lastTickStartUs != 0 && (startUs - _lastTickStartUs) / 1000.0 < TickMs * 0.5;
        _lastTickStartUs = startUs;
        return catchUp;
    }

    void TickTelemetry(ulong startUs, double delta)
    {
        var ms = (Time.GetTicksUsec() - startUs) / 1000.0;
        _tickMs.Add(ms);
        if (ms > TickMs) _overruns++;
        foreach (var c in _clients.Values) c.SaveTokens = Math.Min(SaveBurst, c.SaveTokens + delta * SavesPerSecond);
        _reportClock += delta;
        if (_reportClock < ReportSeconds) return;
        GD.Print(Report(_reportClock));
        _reportClock = 0; _tickMs.Clear();
        _overruns = _skippedSnapshots = _droppedPackets = _rejectedSaves = 0;
        foreach (var c in _clients.Values) c.BytesOut = 0;
        foreach (var l in _phaseUs) l.Clear();
    }

    string Report(double seconds)
    {
        var s = _tickMs.OrderBy(x => x).ToList();
        double P(double p) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)Math.Ceiling(p / 100 * s.Count) - 1 < 0 ? 0 : (int)Math.Ceiling(p / 100 * s.Count) - 1)];
        var joined = _clients.Values.Where(c => c.Joined).ToList();
        var perClient = joined.Count == 0 ? 0 : joined.Average(c => c.BytesOut) / seconds;
        var entities = _match.Entities.Snapshot().Count + joined.Count + _cars.Count + _rockets.Count;
        var line = $"server: stats tick p50 {P(50):F2} ms p99 {P(99):F2} ms (budget {TickMs:F1}), overruns {_overruns}, skipped snapshots {_skippedSnapshots}, " +
               $"players {joined.Count}, bots {_byId.Count - joined.Count}, out {perClient:F0} B/s per client, entities {entities}, db queue {Progression?.QueueDepth ?? 0}, " +
               $"dropped packets {_droppedPackets}, rejected saves {_rejectedSaves}";
        return line + " | phases p50/p99 us " + PhaseSummary() + " | " + GcSummary(seconds);
    }

    /// <summary>Each phase's p50 and p99 in microseconds over the report period (the report's one allocation per phase, every 10 s).</summary>
    string PhaseSummary()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _phaseUs.Length; i++)
        {
            var s = _phaseUs[i].ToArray();
            Array.Sort(s);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(((ServerPerf.Ph)i).ToString().ToLowerInvariant()).Append(' ')
              .Append(Pct(s, 50).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
              .Append(Pct(s, 99).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>GC collections, allocation rate and GC pause since the last report (the GC counters are process-wide).</summary>
    string GcSummary(double seconds)
    {
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var alloc = GC.GetTotalAllocatedBytes(); var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var s = $"gc gen0 {gc0 - _gc0Last} gen1 {gc1 - _gc1Last} gen2 {gc2 - _gc2Last}, alloc {(alloc - _allocLast) / 1e6 / Math.Max(seconds, 1e-3):F2} MB/s, gc pause {pause - _pauseLast:F1} ms";
        _gc0Last = gc0; _gc1Last = gc1; _gc2Last = gc2; _allocLast = alloc; _pauseLast = pause;
        return s;
    }

    /// <summary>Spends one save token; false when the peer saves too fast.</summary>
    bool AllowSave(Client c)
    {
        if (c.SaveTokens >= 1) { c.SaveTokens -= 1; return true; }
        _rejectedSaves++;
        return false;
    }
}
