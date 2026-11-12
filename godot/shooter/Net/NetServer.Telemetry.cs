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
    }

    string Report(double seconds)
    {
        var s = _tickMs.OrderBy(x => x).ToList();
        double P(double p) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)Math.Ceiling(p / 100 * s.Count) - 1 < 0 ? 0 : (int)Math.Ceiling(p / 100 * s.Count) - 1)];
        var joined = _clients.Values.Where(c => c.Joined).ToList();
        var perClient = joined.Count == 0 ? 0 : joined.Average(c => c.BytesOut) / seconds;
        var entities = _match.Entities.Snapshot().Count + joined.Count + _cars.Count + _rockets.Count;
        return $"server: stats tick p50 {P(50):F2} ms p99 {P(99):F2} ms (budget {TickMs:F1}), overruns {_overruns}, skipped snapshots {_skippedSnapshots}, " +
               $"players {joined.Count}, bots {_byId.Count - joined.Count}, out {perClient:F0} B/s per client, entities {entities}, db queue {Progression?.QueueDepth ?? 0}, " +
               $"dropped packets {_droppedPackets}, rejected saves {_rejectedSaves}";
    }

    /// <summary>Spends one save token; false when the peer saves too fast.</summary>
    bool AllowSave(Client c)
    {
        if (c.SaveTokens >= 1) { c.SaveTokens -= 1; return true; }
        _rejectedSaves++;
        return false;
    }
}
