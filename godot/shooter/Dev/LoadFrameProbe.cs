using System;
using System.Diagnostics;
using System.IO;
using Godot;

namespace Drycastle.Shooter.Dev;

/// <summary>
/// The join-time frame log (DRYCASTLE_LOAD_TIMES=1, docs/perf-investigation/join-time.md): every frame over 100 ms while the map loads and the
/// match starts, by its real length and where it went, and every load step over 100 ms. _Process's delta cannot show it: Godot caps a
/// frame's delta at 8 physics steps (about 0.27 s at 30 ticks).
/// Parts: idle (the last draw to the first physics step: script frame callbacks, input, the frame cap), physics, signal (ProcessFrame, where
/// the load's coroutines run; work is their measured steps), process (nodes: every node's _Process; deferred: the deferred calls queued until then,
/// such as a Control's layout and redraw; rest: transforms, the delete queue, timers and tweens, until the draw), draw. The main
/// thread's scheduler counts (/proc/thread-self/schedstat) split the frame into on a CPU, waiting for a CPU, and blocked (a lock, the GPU, the
/// garbage collector).
/// </summary>
public partial class LoadFrameProbe : Node
{
    const double SlowMs = 100;
    static bool _on;
    static long _work;
    static string _stage = "map";
    static float _at;
    static long _stopAt;

    /// <summary>A main-thread load step that ran from <paramref name="since"/> (Stopwatch ticks) until now: counted in the frame, logged when slow.</summary>
    public static void Step(long since, string stage, float at)
    {
        if (!_on) return;
        var t = Stopwatch.GetTimestamp() - since;
        _work += t; _stage = stage; _at = at;
        if (Ms(t) > SlowMs) GD.Print($"loadstep {Ms(t):F0} ms {stage} {at:F3}");
    }

    /// <summary>What the main thread does from now on; "welcome" ends the log 3 s later with a summary.</summary>
    public static void Mark(string stage)
    {
        if (!_on) return;
        _stage = stage; _at = 0;
        if (stage == "welcome") _stopAt = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
    }

    /// <summary>A part of a frame from <paramref name="since"/> (Stopwatch ticks) until now, logged over 20 ms while the probe runs; returns now, for the next part.</summary>
    public static long Part(string name, long since)
    {
        var now = Stopwatch.GetTimestamp();
        if (!_on) return now;
        var ms = Ms(now - since);
        if (ms > 20) GD.Print($"loadpart {ms:F0} ms {name}");
        Parts.TryGetValue(name, out var sum);
        Parts[name] = (sum.Ms + ms, sum.Count + 1);
        return now;
    }

    /// <summary>Whether the probe runs (the frame log is on and the match has not been running 3 s).</summary>
    public static bool On => _on;
    static readonly System.Collections.Generic.Dictionary<string, (double Ms, int Count)> Parts = new();

    long _last, _phys, _sig, _proc, _nodes, _deferred, _pre;
    int _steps;
    double _gcMs;
    int _gc2;
    (long Run, long Wait) _sched;
    int _slow, _over250, _over500, _over1000;
    double _maxMs, _slowMs, _cpu, _runq, _blocked, _gc;

    public override void _Ready()
    {
        _on = true;
        ProcessPriority = int.MinValue;
        GetTree().PhysicsFrame += OnPhysics;
        GetTree().ProcessFrame += OnSignal;
        RenderingServer.FramePreDraw += OnPreDraw;
        RenderingServer.FramePostDraw += OnPostDraw;
        _last = Stopwatch.GetTimestamp(); _gcMs = GC.GetTotalPauseDuration().TotalMilliseconds; _gc2 = GC.CollectionCount(2); _sched = Sched();
    }

    public override void _ExitTree()
    {
        _on = false;
        GetTree().PhysicsFrame -= OnPhysics;
        GetTree().ProcessFrame -= OnSignal;
        RenderingServer.FramePreDraw -= OnPreDraw;
        RenderingServer.FramePostDraw -= OnPostDraw;
    }

    void OnPhysics() { if (_steps++ == 0) _phys = Stopwatch.GetTimestamp(); }
    void OnSignal() => _sig = Stopwatch.GetTimestamp();
    public override void _Process(double delta)
    {
        if (!_on) return;
        _proc = _nodes = _deferred = Stopwatch.GetTimestamp();
        // the first deferred call of the flush after every node's _Process, and one queued behind the deferred calls the nodes made
        Callable.From(() => { _nodes = Stopwatch.GetTimestamp(); Callable.From(() => _deferred = Stopwatch.GetTimestamp()).CallDeferred(); }).CallDeferred();
    }
    void OnPreDraw() => _pre = Stopwatch.GetTimestamp();

    void OnPostDraw()
    {
        var now = Stopwatch.GetTimestamp();
        var gcMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        var gc2 = GC.CollectionCount(2);
        var sched = Sched();
        var wall = Ms(now - _last);
        if (_on && wall > SlowMs)
        {
            var cpu = (sched.Run - _sched.Run) / 1e6;
            var runq = (sched.Wait - _sched.Wait) / 1e6;
            var physStart = _steps > 0 ? _phys : _sig;
            GD.Print($"loadframe {wall:F0} ms {_stage} {_at:F3} @{Ms(_last) / 1000:F3}: idle {Ms(physStart - _last):F0} physics {(_steps > 0 ? Ms(_sig - _phys) : 0):F0} x{_steps} signal {Ms(_proc - _sig):F0} work {Ms(_work):F0} process {Ms(_pre - _proc):F0} (nodes {Ms(_nodes - _proc):F0} deferred {Ms(_deferred - _nodes):F0} rest {Ms(_pre - _deferred):F0}) draw {Ms(now - _pre):F0} | cpu {cpu:F0} runq {runq:F0} blocked {wall - cpu - runq:F0} gc {gcMs - _gcMs:F0}{(gc2 > _gc2 ? " gen2" : "")}");
            _slow++; _slowMs += wall; _maxMs = Math.Max(_maxMs, wall);
            if (wall > 250) _over250++;
            if (wall > 500) _over500++;
            if (wall > 1000) _over1000++;
            _cpu += cpu; _runq += runq; _blocked += wall - cpu - runq; _gc += gcMs - _gcMs;
        }
        if (_on && _stopAt != 0 && now > _stopAt)
        {
            foreach (var (name, sum) in Parts) if (sum.Ms > 20) GD.Print($"loadpart sum {sum.Ms:F0} ms x{sum.Count} {name}");
            GD.Print($"loadframe summary: slow {_slow} ({_slowMs:F0} ms), over 250 ms {_over250}, over 500 ms {_over500}, over 1 s {_over1000}, longest {_maxMs:F0} ms; in them cpu {_cpu:F0} runq {_runq:F0} blocked {_blocked:F0} gc {_gc:F0} ms");
            _on = false;
        }
        _last = now; _steps = 0; _work = 0; _gcMs = gcMs; _gc2 = gc2; _sched = sched;
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The main thread's time on a CPU and waiting for one, in ns, since it started (Linux; zeros elsewhere).</summary>
    static (long Run, long Wait) Sched()
    {
        try
        {
            var p = File.ReadAllText("/proc/thread-self/schedstat").Split(' ');
            return (long.Parse(p[0]), long.Parse(p[1]));
        }
        catch (Exception) { return (0, 0); }
    }
}
