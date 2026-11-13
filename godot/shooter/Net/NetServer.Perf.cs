using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using FileAccess = Godot.FileAccess;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>Counters bumped from hot paths while <see cref="NetServer.PerfOut"/> is set (perf-harness only; plain static adds, no allocation).</summary>
public static class PerfCounters
{
    public static long Rays, LiveTargetsCalls, LiveTargetsElems;

    /// <summary>A physics ray through the space, counted. Same result as <c>space.IntersectRay(q)</c>.</summary>
    public static Godot.Collections.Dictionary PerfRay(this PhysicsDirectSpaceState3D space, PhysicsRayQueryParameters3D q)
    {
        Rays++;
        return space.IntersectRay(q);
    }
}

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

public partial class NetServer
{
    /// <summary>--perf-out DIR: write per-phase tick timings to DIR/server-ticks.csv (measurement only).</summary>
    public string PerfOut = "";
    ServerPerf? _perf;

    void PerfInit()
    {
        if (PerfOut.Length == 0) return;
        _perf = new ServerPerf(PerfOut);
        GD.Print($"server: perf phases on, writing {PerfOut}/server-ticks.csv (--perf-out)");
    }

    void PerfLap(ServerPerf.Ph p) { _perf?.Lap(p); PhaseLap(p); }

    void PerfEnd()
    {
        if (_perf == null) return;
        var joined = _clients.Values.Count(c => c.Joined);
        var moving = _cars.Count(c => c.Velocity.LengthSquared() > 0.01f || c.Seat.Occupied);
        var asleep = 0; var parkedAwake = 0; // asleep covers every type (ground, flying, driven); parked-awake = driverless and not destroyed, yet still stepped
        foreach (var c in _cars)
        {
            if (c.Asleep) asleep++;
            else if (!c.Destroyed && !c.Seat.Occupied) parkedAwake++;
        }
        _perf.End(_tick, joined, _byId.Count - joined, moving, _cars.Count, _match.Entities.Snapshot().Count + joined + _cars.Count + _rockets.Count, _elapsed, asleep, parkedAwake);
    }

    // ---- lag-compensation log (measurement plan B1, ADR 0013): --perf-lagcomp. Every hitscan shot is resolved three ways, one CSV row per shot in
    // PerfOut/lagcomp.csv: hit_used is what the server did (rewound, or present with --no-lagcomp); hit_now resolves against the present positions and
    // hit_rewound against the poses the shooter's screen showed. Both comparison columns leave out the walls, as the B1 shadow did.
    public bool PerfLagComp;
    System.IO.StreamWriter? _lcLog;

    void PerfShot(Client c, Vector3 eye, Vector3 dir, float range, ushort? hitUsed)
    {
        if (!PerfLagComp || PerfOut.Length == 0) return;
        if (_lcLog == null)
        {
            _lcLog = new System.IO.StreamWriter(System.IO.Path.Combine(PerfOut, "lagcomp.csv")) { AutoFlush = true };
            _lcLog.WriteLine("tick,shooter,rtt_ms,back_ticks,hit_used,hit_now,hit_rewound,same,n_targets,speeds_mps,aimed_id,aimed_dist_m,lateral_rewound_m,lateral_now_m,resolve_us");
        }
        var rtt = RttOf(c);
        var back = BackTicksOf(c);
        var resolveUs = _lagResolveUs;
        var cur = LiveTargets();
        var rewoundAll = RewoundTargets(c);
        var rewound = new List<HitTarget>(rewoundAll.Count);
        foreach (var t in rewoundAll) if (t.Id != c.Id) rewound.Add(t);
        var speeds = new List<string>();
        foreach (var t in rewound)
            foreach (var now in cur)
                if (now.Id == t.Id) { speeds.Add(((now.Feet - t.Feet).Length() / (back / (float)Wire.TickRate)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)); break; }
        var hr = HitMath.Raycast(N(eye), N(dir), range, float.PositiveInfinity, rewound, c.Id);
        var hn = HitMath.Raycast(N(eye), N(dir), range, float.PositiveInfinity, cur, c.Id);
        // the target the shooter aimed at: the rewound one nearest to the ray (chest height), and how far the ray passes from it then and from the same player now
        NVec3 ne = N(eye), nd = N(dir);
        float Lat(NVec3 feet) { var v = feet + new NVec3(0, 1.0f, 0) - ne; var al = NVec3.Dot(v, nd); return al <= 0 ? float.MaxValue : (v - nd * al).Length(); }
        ushort aimed = 0; var aimedLat = float.MaxValue; var aimedDist = 0f; var latNow = float.MaxValue;
        foreach (var t in rewound)
        {
            var l = Lat(t.Feet);
            if (l >= aimedLat) continue;
            aimed = t.Id; aimedLat = l; aimedDist = (t.Feet - ne).Length();
            latNow = float.MaxValue;
            foreach (var nowT in cur)
                if (nowT.Id == t.Id) { latNow = Lat(nowT.Feet); break; }
        }
        var a = hn?.Id ?? 0; var b = hr?.Id ?? 0; var used = hitUsed ?? 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _lcLog.WriteLine(_tick + "," + c.Id + "," + rtt.ToString("F1", inv) + "," + back + "," + used + "," + a + "," + b + "," + (a == b ? 1 : 0) + "," + rewound.Count + "," + string.Join("|", speeds) + "," + aimed + "," + aimedDist.ToString("F1", inv) + "," + aimedLat.ToString("F2", inv) + "," + latNow.ToString("F2", inv) + "," + resolveUs.ToString("F2", inv));
    }

    // ---- reliable event timestamps (measurement plan W2): the wall-clock time every Shot/Hit/Kill event was queued, for delivery latency under real loss
    System.IO.StreamWriter? _evLog;

    void PerfEventSent(EventKind kind, byte[] payload)
    {
        if (PerfOut.Length == 0 || kind is not (EventKind.Shot or EventKind.Hit or EventKind.Kill)) return;
        _evLog ??= new System.IO.StreamWriter(System.IO.Path.Combine(PerfOut, "events-server.csv")) { AutoFlush = true };
        _evLog.WriteLine((DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10 + "," + (int)kind + "," + Convert.ToHexString(payload));
    }
}
