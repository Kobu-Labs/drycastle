using System.Diagnostics;
using System.Text;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// Per-phase tick timing for the perf investigation (docs/perf-investigation/measurement-plan.md, N5/N6): one CSV row per tick with the
/// microseconds each part of <c>_PhysicsProcess</c> took, the allocation and GC deltas of the tick, and the ray / LiveTargets counters.
/// Off unless the server runs with <c>--perf-out DIR</c>; when off the cost is one null check per phase.
/// </summary>
public sealed class ServerPerf
{
    public enum Ph { Poll, Housekeeping, Match, Streaks, Bots, PlaceSolids, SimClients, Rockets, Corpses, Vehicles, Detonate, LagCapture, Snapshots, Telemetry, Count }

    static readonly string[] Names = { "poll", "housekeeping", "match", "streaks", "bots", "place_solids", "sim_clients", "rockets", "corpses", "vehicles", "detonate_fx_glass", "lag_capture", "snapshots", "telemetry" };

    readonly string _path;
    readonly StringBuilder _sb = new();
    readonly long[] _us = new long[(int)Ph.Count];
    readonly long[] _alloc = new long[(int)Ph.Count];
    long _t0, _last, _allocAtStart, _allocLast;
    int _gc0, _gc1, _gc2;
    long _rays0, _lt0, _lte0;
    double _flushAt;
    bool _headerDone;

    public ServerPerf(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "server-ticks.csv");
        System.IO.File.WriteAllText(_path, "");
    }

    /// <summary>Start of a tick.</summary>
    public void Begin()
    {
        System.Array.Clear(_us);
        System.Array.Clear(_alloc);
        _t0 = _last = Stopwatch.GetTimestamp();
        _allocAtStart = _allocLast = GC.GetAllocatedBytesForCurrentThread();
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        _rays0 = PerfCounters.Rays; _lt0 = PerfCounters.LiveTargetsCalls; _lte0 = PerfCounters.LiveTargetsElems;
    }

    /// <summary>Ends a phase: the time since the previous lap (or Begin) is charged to it.</summary>
    public void Lap(Ph p)
    {
        var now = Stopwatch.GetTimestamp();
        _us[(int)p] += (now - _last) * 1_000_000 / Stopwatch.Frequency;
        _last = now;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        _alloc[(int)p] += alloc - _allocLast; // bytes the phase allocated (the CSV's <phase>_alloc columns)
        _allocLast = alloc;
    }

    /// <summary>End of a tick: one row. <paramref name="extra"/> are the tick's counts (players, bots, moving cars, entities, parked cars).</summary>
    public void End(uint tick, int players, int bots, int carsMoving, int carsTotal, int entities, double nowS, int carsAsleep, int carsParkedAwake)
    {
        if (!_headerDone)
        {
            _sb.Append("tick,total_us,").Append(string.Join(',', Names.Select(n => n + "_us"))).Append(',').Append(string.Join(',', Names.Select(n => n + "_alloc")))
               .Append(",alloc_bytes,gc0,gc1,gc2,rays,live_targets_calls,live_targets_elems,players,bots,cars_moving,cars_total,entities,cars_asleep,cars_parked_awake\n");
            _headerDone = true;
        }
        var total = (Stopwatch.GetTimestamp() - _t0) * 1_000_000 / Stopwatch.Frequency;
        _sb.Append(tick).Append(',').Append(total);
        for (var i = 0; i < _us.Length; i++) _sb.Append(',').Append(_us[i]);
        for (var i = 0; i < _alloc.Length; i++) _sb.Append(',').Append(_alloc[i]);
        _sb.Append(',').Append(GC.GetAllocatedBytesForCurrentThread() - _allocAtStart)
           .Append(',').Append(GC.CollectionCount(0) - _gc0).Append(',').Append(GC.CollectionCount(1) - _gc1).Append(',').Append(GC.CollectionCount(2) - _gc2)
           .Append(',').Append(PerfCounters.Rays - _rays0).Append(',').Append(PerfCounters.LiveTargetsCalls - _lt0).Append(',').Append(PerfCounters.LiveTargetsElems - _lte0)
           .Append(',').Append(players).Append(',').Append(bots).Append(',').Append(carsMoving).Append(',').Append(carsTotal).Append(',').Append(entities)
           .Append(',').Append(carsAsleep).Append(',').Append(carsParkedAwake).Append('\n');
        if (nowS >= _flushAt) { Flush(); _flushAt = nowS + 2; }
    }

    public void Flush()
    {
        if (_sb.Length == 0) return;
        System.IO.File.AppendAllText(_path, _sb.ToString(), Encoding.ASCII);
        _sb.Clear();
    }
}
