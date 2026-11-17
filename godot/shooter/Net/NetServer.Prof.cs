using System.Globalization;
using System.Text;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The "server: prof" log lines, printed with the stats line every <see cref="ReportSeconds"/> seconds (always on; docs/shooter-server.md, "Performance log"):
/// the scoped timers of <see cref="Prof"/>, the slowest tick of the period with its phases and scopes, the navigation worker, the CPU of every
/// thread of the process and its memory. All <c>key=value</c>, so a log shipper can read them. The lines about the tick are only printed while
/// somebody plays; the memory line always (it is what shows a leak on an empty server).
/// </summary>
public partial class NetServer
{
    /// <summary>The slowest tick of the period: its length, its number and the microseconds of each of its phases.</summary>
    double _profWorstMs;
    uint _profWorstTick;
    readonly double[] _profWorstPhaseUs = new double[(int)ServerPerf.Ph.Count];
    /// <summary>CPU clock ticks (user + system) of each thread at the last report, by thread id.</summary>
    Dictionary<int, long> _threadCpu = new();
    long _raysLast, _liveTargetsLast;
    /// <summary>The garbage collector's counters at the start of the tick, and of the slowest tick: the collections and the pause that fell in it.</summary>
    int _tickGc0, _tickGc1, _tickGc2, _worstGc0, _worstGc1, _worstGc2;
    double _tickPauseMs, _worstPauseMs;
    /// <summary>Ticks of the period in which a collection ran, and the slowest tick in which none did.</summary>
    int _gcTicks;
    double _worstNoGcMs;

    void ProfTickBegin()
    {
        _tickGc0 = GC.CollectionCount(0); _tickGc1 = GC.CollectionCount(1); _tickGc2 = GC.CollectionCount(2);
        _tickPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
    }
    int _profReports;

    /// <summary>End of a tick that took <paramref name="ms"/>: remembered when it is the slowest of the period.</summary>
    void ProfTickEnd(double ms)
    {
        var gc0 = GC.CollectionCount(0) - _tickGc0;
        if (gc0 > 0) _gcTicks++;
        else if (ms > _worstNoGcMs) _worstNoGcMs = ms;
        if (!Prof.TickEnd(ms)) return;
        _profWorstMs = ms; _profWorstTick = _tick;
        _worstGc0 = gc0; _worstGc1 = GC.CollectionCount(1) - _tickGc1; _worstGc2 = GC.CollectionCount(2) - _tickGc2;
        _worstPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - _tickPauseMs;
        for (var i = 0; i < _profWorstPhaseUs.Length; i++)
            _profWorstPhaseUs[i] = i == (int)ServerPerf.Ph.Telemetry || _phaseUs[i].Count == 0 ? 0 : _phaseUs[i][^1]; // the telemetry phase of this tick is not over yet
    }

    /// <summary>The period's lines, then the next period starts.</summary>
    void ProfReport(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        var active = _byId.Count > 0;
        var nav = Prof.Nav(seconds); // read (and cleared) every period
        var threads = ThreadCpu(seconds); // likewise: the baseline moves every period
        if (active)
        {
            GD.Print("server: prof scopes ms/s|calls/s|kb/s " + Prof.Scopes(seconds));
            var sb = new StringBuilder();
            for (var i = 0; i < _profWorstPhaseUs.Length; i++)
            {
                if (_profWorstPhaseUs[i] < 50) continue;
                sb.Append(' ').Append(((ServerPerf.Ph)i).ToString().ToLowerInvariant()).Append('=').Append(_profWorstPhaseUs[i].ToString("F0", inv));
            }
            GD.Print($"server: prof worst tick={_profWorstTick} ms={_profWorstMs.ToString("F2", inv)} gc0={_worstGc0} gc1={_worstGc1} gc2={_worstGc2} gc_pause_ms={_worstPauseMs.ToString("F1", inv)}" +
                $" ticks_with_gc={_gcTicks} worst_without_gc_ms={_worstNoGcMs.ToString("F2", inv)} phases_us{sb} | scopes_us {Prof.Worst()}");
            var al = new StringBuilder();
            for (var i = 0; i < _phaseAlloc.Length; i++)
            {
                var kb = _phaseAlloc[i] / 1000.0 / Math.Max(seconds, 1e-3);
                if (kb < 1) continue;
                al.Append(' ').Append(((ServerPerf.Ph)i).ToString().ToLowerInvariant()).Append('=').Append(kb.ToString("F0", inv));
            }
            GD.Print("server: prof alloc kb/s" + al);
            GD.Print("server: prof nav ms/s|calls/s " + nav);
        }
        if (threads.Length > 0 && (active || _profReports % 6 == 0)) GD.Print("server: prof threads cpu_pct " + threads);
        GD.Print("server: prof mem " + MemoryLine(seconds));
        _profReports++;
        Prof.Reset();
        _profWorstMs = 0; _gcTicks = 0; _worstNoGcMs = 0;
        Array.Clear(_phaseAlloc);
    }

    /// <summary>
    /// The CPU each thread of the process used in the period, in percent of one core, threads of one kind summed ("WorkerThread 3" and
    /// "WorkerThread 7" are <c>workerthread</c>; the game thread is <c>main</c>). From /proc/self/task: empty where that does not exist, and on the first call.
    /// </summary>
    string ThreadCpu(double seconds)
    {
        try
        {
            if (!System.IO.Directory.Exists("/proc/self/task")) return "";
            var now = new Dictionary<int, long>();
            var groups = new Dictionary<string, long>();
            var first = _threadCpu.Count == 0;
            var pid = System.Environment.ProcessId;
            foreach (var dir in System.IO.Directory.EnumerateDirectories("/proc/self/task"))
            {
                if (!int.TryParse(System.IO.Path.GetFileName(dir), out var tid)) continue;
                string stat;
                try { stat = System.IO.File.ReadAllText(dir + "/stat"); } catch (System.IO.IOException) { continue; } // the thread ended meanwhile
                if (!ProcStat.Parse(stat, out var comm, out var ticks)) continue;
                now[tid] = ticks;
                var used = ticks - (_threadCpu.TryGetValue(tid, out var before) ? before : 0);
                var name = tid == pid ? "main" : ProcStat.Group(comm);
                groups[name] = groups.GetValueOrDefault(name) + used;
            }
            _threadCpu = now;
            if (first) return "";
            var inv = CultureInfo.InvariantCulture;
            var per = 100.0 / ProcStat.ClockTicksPerSecond / Math.Max(seconds, 1e-3);
            var sb = new StringBuilder();
            long total = 0;
            foreach (var g in groups.OrderByDescending(g => g.Value))
            {
                total += g.Value;
                if (g.Value * per < 0.05) continue;
                sb.Append(g.Key).Append('=').Append((g.Value * per).ToString("F1", inv)).Append(' ');
            }
            return sb.Append("total=").Append((total * per).ToString("F1", inv)).ToString();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Memory of the process and the engine's object counts, and the rays and target lists per second.</summary>
    string MemoryLine(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        string Mb(double bytes) => (bytes / 1e6).ToString("F0", inv);
        string Mon(Performance.Monitor m) => Performance.GetMonitor(m).ToString("F0", inv);
        double rss = 0;
        try
        {
            if (System.IO.File.Exists("/proc/self/statm"))
                rss = long.Parse(System.IO.File.ReadAllText("/proc/self/statm").Split(' ')[1], inv) * (double)System.Environment.SystemPageSize;
        }
        catch (Exception e) when (e is System.IO.IOException or FormatException or IndexOutOfRangeException) { }
        seconds = Math.Max(seconds, 1e-3);
        long rays = PerfCounters.Rays, lt = PerfCounters.LiveTargetsCalls;
        var line = $"rss_mb={Mb(rss)} heap_mb={Mb(GC.GetTotalMemory(false))} gc_committed_mb={Mb(GC.GetGCMemoryInfo().TotalCommittedBytes)} godot_static_mb={Mb(Performance.GetMonitor(Performance.Monitor.MemoryStatic))}" +
            $" objects={Mon(Performance.Monitor.ObjectCount)} nodes={Mon(Performance.Monitor.ObjectNodeCount)} orphans={Mon(Performance.Monitor.ObjectOrphanNodeCount)}" +
            $" phys_active={Mon(Performance.Monitor.Physics3DActiveObjects)} phys_pairs={Mon(Performance.Monitor.Physics3DCollisionPairs)} phys_islands={Mon(Performance.Monitor.Physics3DIslandCount)}" +
            $" rays_s={((rays - _raysLast) / seconds).ToString("F0", inv)} live_targets_s={((lt - _liveTargetsLast) / seconds).ToString("F0", inv)}";
        _raysLast = rays; _liveTargetsLast = lt;
        return line;
    }
}
