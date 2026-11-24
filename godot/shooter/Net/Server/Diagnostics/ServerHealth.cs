using Drycastle.ShooterCore.Diagnostics;
using Godot;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// Always-on server health (perf audit T2-05, T2-06, T2-07): tick timing with overrun and catch-up handling, the counters of dropped packets and
/// rejected saves, the rate limit on profile saves, and one log line every <see cref="ReportSeconds"/> seconds.
/// </summary>
internal sealed class ServerHealth
{
    public const int ReportSeconds = 10;
    /// <summary>Profile saves (loadout, camo, copy, reset) per peer: a burst, then a steady rate.</summary>
    const double SaveBurst = 10, SavesPerSecond = 2;

    static readonly double TickMs = 1000.0 / Wire.TickRate;

    readonly ServerContext _ctx;
    readonly SmokeStats _smoke;
    readonly TickPhases _phases;
    readonly ProfLines _prof;
    readonly MetricsOut _metrics;
    readonly IProgressionStore? _progression;
    readonly double _quitAfter;
    readonly Func<int, int> _liveEntities;
    readonly List<double> _tickMs = new();
    ulong _lastTickStartUs;
    int _overruns, _rejectedSaves;
    double _reportClock;
    /// <summary>The GC counters at the last report, for the report's deltas.</summary>
    int _gc0Last, _gc1Last, _gc2Last;
    long _allocLast;
    double _pauseLast;

    /// <summary>Ticks whose snapshot was skipped to catch up, and packets read and dropped over a peer's budget, in this report period.</summary>
    public int SkippedSnapshots, DroppedPackets;

    public ServerHealth(ServerContext ctx, SmokeStats smoke, TickPhases phases, ProfLines prof, MetricsOut metrics, IProgressionStore? progression, double quitAfter, Func<int, int> liveEntities)
    {
        _ctx = ctx; _smoke = smoke; _phases = phases; _prof = prof; _metrics = metrics; _progression = progression; _quitAfter = quitAfter; _liveEntities = liveEntities;
    }

    /// <summary>True when this tick started right after the previous one, i.e. the engine is catching up after a slow tick.</summary>
    public bool CatchUpTick(ulong startUs)
    {
        var catchUp = _lastTickStartUs != 0 && (startUs - _lastTickStartUs) / 1000.0 < TickMs * 0.5;
        _lastTickStartUs = startUs;
        return catchUp;
    }

    /// <summary>The tick is over: its length, the save tokens, and every <see cref="ReportSeconds"/> the stats and prof lines.</summary>
    public void TickEnd(ulong startUs, double delta)
    {
        var ms = (Time.GetTicksUsec() - startUs) / 1000.0;
        _tickMs.Add(ms);
        if (ms > TickMs) _overruns++;
        _prof.TickEnd(ms);
        foreach (var c in _ctx.Connections) c.SaveTokens = Math.Min(SaveBurst, c.SaveTokens + delta * SavesPerSecond);
        _reportClock += delta;
        if (_reportClock < ReportSeconds) return;
        GD.Print(Report(_reportClock));
        _prof.Report(_reportClock);
        _reportClock = 0; _tickMs.Clear();
        _overruns = SkippedSnapshots = DroppedPackets = _rejectedSaves = 0;
        foreach (var c in _ctx.Connections) c.BytesOut = 0;
        _phases.NewPeriod();
    }

    /// <summary>The peak player count, the smoke test's moved distances and the 2 s player log.</summary>
    public void Heartbeat()
    {
        var tick = _ctx.Tick;
        var connected = _ctx.Connections.Count();
        if (connected > _smoke.PeakPlayers) _smoke.PeakPlayers = connected;
        if (_quitAfter > 0) foreach (var c in _ctx.Connections) if (c.Joined) _smoke.MovedByName[c.Name] = c.MaxMoved; // only the smoke verdict reads it; a production server keeps no per-name history
        if (tick % (Wire.TickRate * 2) != 0 || connected == 0) return;
        var logT = Prof.Begin(Prof.S.LogPlayers); // after the tick's clock stopped: this line is in no phase
        GD.Print($"server: tick {tick}, players {string.Join(", ", _ctx.Roster.Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Y:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        Prof.End(Prof.S.LogPlayers, logT);
    }

    /// <summary>Spends one save token; false when the peer saves too fast.</summary>
    public bool AllowSave(ServerClient c)
    {
        if (c.SaveTokens >= 1) { c.SaveTokens -= 1; return true; }
        _rejectedSaves++;
        return false;
    }

    string Report(double seconds)
    {
        var s = _tickMs.OrderBy(x => x).ToList();
        var joined = _ctx.Connections.Where(c => c.Joined).ToList();
        var bots = _ctx.Players.Count - joined.Count;
        var perClient = joined.Count == 0 ? 0 : joined.Average(c => c.BytesOut) / seconds;
        var entities = _liveEntities(joined.Count);
        var queue = _progression?.QueueDepth ?? 0;
        var p50 = Percentile.Of(s, 50); var p99 = Percentile.Of(s, 99);
        var line = $"server: stats tick p50 {p50:F2} ms p99 {p99:F2} ms (budget {TickMs:F1}), overruns {_overruns}, skipped snapshots {SkippedSnapshots}, " +
               $"players {joined.Count}, bots {bots}, out {perClient:F0} B/s per client, entities {entities}, db queue {queue}, " +
               $"dropped packets {DroppedPackets}, rejected saves {_rejectedSaves}";
        var metrics = _metrics.Batch;
        metrics?.Begin("drycastle_tick").Field("p50_ms", p50).Field("p99_ms", p99).Field("max_ms", s.Count == 0 ? 0 : s[^1]).Field("budget_ms", TickMs)
            .Field("overruns", _overruns).Field("skipped_snapshots", SkippedSnapshots).End();
        metrics?.Begin("drycastle_match").Field("players", joined.Count).Field("bots", bots).Field("entities", entities)
            .Field("out_bytes_s_per_client", perClient).Field("db_queue", queue).Field("dropped_packets", DroppedPackets).Field("rejected_saves", _rejectedSaves).End();
        return line + " | phases p50/p99 us " + _phases.Summary(metrics) + " | " + GcSummary(seconds);
    }

    /// <summary>GC collections, allocation rate and GC pause since the last report (the GC counters are process-wide).</summary>
    string GcSummary(double seconds)
    {
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var alloc = GC.GetTotalAllocatedBytes(); var pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var s = $"gc gen0 {gc0 - _gc0Last} gen1 {gc1 - _gc1Last} gen2 {gc2 - _gc2Last}, alloc {(alloc - _allocLast) / 1e6 / Math.Max(seconds, 1e-3):F2} MB/s, gc pause {pause - _pauseLast:F1} ms";
        _metrics.Batch?.Begin("drycastle_gc_period").Field("gen0", gc0 - _gc0Last).Field("gen1", gc1 - _gc1Last).Field("gen2", gc2 - _gc2Last)
            .Field("alloc_mb_s", (alloc - _allocLast) / 1e6 / Math.Max(seconds, 1e-3)).Field("pause_ms", pause - _pauseLast).End();
        _gc0Last = gc0; _gc1Last = gc1; _gc2Last = gc2; _allocLast = alloc; _pauseLast = pause;
        return s;
    }
}
