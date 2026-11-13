using System.Globalization;
using System.Text;
using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Client frame measurement on the real map (perf investigation R1-R6): <c>--role client --offline --perf-route --perf-out DIR</c>. After a warm-up
/// (first-use hitches are logged apart) the camera visits <c>--perf-views</c> viewpoints (spawn points spread over the map, four headings each, standing
/// eye height) for <c>--perf-dwell</c> seconds each, and every frame records its time, draw calls, primitives, objects, CPU and GPU render time.
/// Variants: <c>--perf-msaa 0|2|4|8</c>, <c>--perf-noshadow</c>, <c>--perf-shadow-dist M</c>, <c>--perf-vsync</c> (off by default for measurement).
/// Writes DIR/route-frames.csv and DIR/route-summary.json, then quits. Measurement only.
/// </summary>
public partial class PerfRoute : Node
{
    public Player Player = null!;
    public Drycastle.MapLoader.MapLoader Map = null!;
    public string OutDir = "";
    public int Views = 96, Msaa = -1;
    public double Dwell = 2.0, Settle = 0.6;
    public bool NoShadow, Vsync;
    public float ShadowDist = -1;
    public string Tag = "";

    record struct Sample(int View, double T, double Ms, double Draws, double Prims, double Objects, double CpuMs, double GpuMs, double Nodes);

    readonly List<Sample> _warm = new(), _s = new();
    readonly List<Vector3> _anchors = new();
    int _frame, _view = -1;
    double _viewT;
    ulong _startMs;
    Rid _vp;
    bool _applied;
    double _warmSecs = 6;
    double _tWarm;

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        var sp = Map.SpawnPoints.Select(s => s.Position).OrderBy(p => p.X + p.Z * 0.37f).ToList();
        var n = Math.Max(1, Views / 4);
        for (var i = 0; i < n; i++) _anchors.Add(sp[Math.Min(sp.Count - 1, (int)((i + 0.5) * sp.Count / n))]);
        _vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_vp, true);
    }

    void Apply()
    {
        _applied = true;
        DisplayServer.WindowSetVsyncMode(Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        if (Msaa >= 0) GetTree().Root.Msaa3D = Msaa switch { 2 => Viewport.Msaa.Msaa2X, 4 => Viewport.Msaa.Msaa4X, 8 => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled };
        foreach (var l in GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false).OfType<DirectionalLight3D>())
        {
            if (NoShadow) l.ShadowEnabled = false;
            if (ShadowDist > 0) l.DirectionalShadowMaxDistance = ShadowDist;
        }
    }

    Sample Take(int view, double dt)
    {
        double M(Performance.Monitor m) => Performance.GetMonitor(m);
        return new Sample(view, Time.GetTicksMsec() / 1000.0, dt * 1000, M(Performance.Monitor.RenderTotalDrawCallsInFrame), M(Performance.Monitor.RenderTotalPrimitivesInFrame),
            M(Performance.Monitor.RenderTotalObjectsInFrame), RenderingServer.ViewportGetMeasuredRenderTimeCpu(_vp), RenderingServer.ViewportGetMeasuredRenderTimeGpu(_vp), M(Performance.Monitor.ObjectNodeCount));
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (!_applied && _frame > 2) Apply();
        if (_view < 0)
        {
            _tWarm += delta;
            _warm.Add(Take(-1, delta));
            if (_tWarm < _warmSecs) return;
            _view = 0; _viewT = 0; Goto(0);
            return;
        }
        _viewT += delta;
        if (_viewT >= Settle) _s.Add(Take(_view, delta));
        if (_viewT < Settle + Dwell) return;
        _view++; _viewT = 0;
        if (_view >= _anchors.Count * 4) { Finish(); return; }
        Goto(_view);
    }

    void Goto(int v)
    {
        var a = _anchors[v / 4];
        Player.Teleport(a + Vector3.Up * 0.1f);
        Player.Yaw = (v % 4) * Mathf.Pi / 2;
        Player.Pitch = 0;
    }

    static double P(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        xs.Sort();
        return xs[Math.Min(xs.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * xs.Count) - 1))];
    }

    static Dictionary<string, double> Stats(List<double> xs) => new()
    {
        ["n"] = xs.Count, ["mean"] = xs.Count == 0 ? 0 : xs.Average(), ["p50"] = P(xs.ToList(), 50), ["p90"] = P(xs.ToList(), 90), ["p95"] = P(xs.ToList(), 95), ["p99"] = P(xs.ToList(), 99), ["max"] = xs.Count == 0 ? 0 : xs.Max(),
    };

    void Finish()
    {
        Directory.CreateDirectory(OutDir);
        var sb = new StringBuilder("view,t,frame_ms,draws,prims,objects,render_cpu_ms,render_gpu_ms,nodes\n");
        foreach (var s in _warm.Concat(_s))
            sb.Append(s.View).Append(',').Append(s.T.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(s.Ms.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(s.Draws).Append(',').Append(s.Prims)
              .Append(',').Append(s.Objects).Append(',').Append(s.CpuMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(s.GpuMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(s.Nodes).Append('\n');
        File.WriteAllText(Path.Combine(OutDir, "route-frames.csv"), sb.ToString());
        var perView = _s.GroupBy(x => x.View).Select(g => new { v = g.Key, draws = g.Average(x => x.Draws), ms = g.Average(x => x.Ms), prims = g.Average(x => x.Prims), p99 = P(g.Select(x => x.Ms).ToList(), 99) }).ToList();
        var warmMs = _warm.Select(x => x.Ms).ToList();
        var res = new Dictionary<string, object>
        {
            ["tag"] = Tag, ["views"] = _anchors.Count * 4, ["frames_measured"] = _s.Count, ["renderer"] = ProjectSettings.GetSetting("rendering/renderer/rendering_method").ToString(),
            ["adapter"] = RenderingServer.GetVideoAdapterName(), ["resolution"] = $"{DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}",
            ["msaa"] = GetTree().Root.Msaa3D.ToString(), ["vsync"] = DisplayServer.WindowGetVsyncMode().ToString(),
            ["ms_to_route_ready"] = _startMs, ["frame_ms"] = Stats(_s.Select(x => x.Ms).ToList()), ["fps_mean"] = 1000.0 / Math.Max(0.001, _s.Count == 0 ? 1 : _s.Average(x => x.Ms)),
            ["draws"] = Stats(_s.Select(x => x.Draws).ToList()), ["prims"] = Stats(_s.Select(x => x.Prims).ToList()), ["objects"] = Stats(_s.Select(x => x.Objects).ToList()),
            ["render_cpu_ms"] = Stats(_s.Select(x => x.CpuMs).ToList()), ["render_gpu_ms"] = Stats(_s.Select(x => x.GpuMs).ToList()),
            ["per_view_mean_draws"] = Stats(perView.Select(x => x.draws).ToList()), ["per_view_mean_frame_ms"] = Stats(perView.Select(x => x.ms).ToList()),
            ["worst_views_by_draws"] = perView.OrderByDescending(x => x.draws).Take(5).Select(x => new { view = x.v, draws = x.draws, frame_ms = x.ms }).ToList(),
            ["warmup_frame_ms"] = Stats(warmMs), ["warmup_frames_over_33ms"] = warmMs.Count(x => x > 33.3), ["warmup_frames_over_100ms"] = warmMs.Count(x => x > 100), ["warmup_first_frames_ms"] = warmMs.Take(8).ToList(),
            ["frames_over_16_7ms"] = _s.Count(x => x.Ms > 16.7), ["frames_over_33ms"] = _s.Count(x => x.Ms > 33.3), ["frames_over_50ms"] = _s.Count(x => x.Ms > 50),
            ["nodes"] = _s.Count == 0 ? 0 : _s[^1].Nodes, ["mem_static_mb"] = Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0, ["video_mem_mb"] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
            ["textures_mb"] = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0, ["buffers_mb"] = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0,
            ["gc_heap_mb"] = GC.GetTotalMemory(false) / 1048576.0, ["gc_gen0"] = GC.CollectionCount(0), ["gc_gen1"] = GC.CollectionCount(1), ["gc_gen2"] = GC.CollectionCount(2),
        };
        File.WriteAllText(Path.Combine(OutDir, "route-summary.json"), System.Text.Json.JsonSerializer.Serialize(res, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        GD.Print("PERF_ROUTE_DONE");
        GetTree().Quit(0);
    }
}

public partial class Shooter
{
    void BuildPerfRoute(Config cfg, Player player)
    {
        static int I(string s, int d) => int.TryParse(s, out var v) ? v : d;
        static float F(string s, float d) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        AddChild(new PerfRoute
        {
            Player = player, Map = _map, OutDir = cfg.Str("perf-out", "out/perf/route"), Views = I(cfg.Str("perf-views"), 96), Msaa = I(cfg.Str("perf-msaa"), -1),
            Dwell = F(cfg.Str("perf-dwell"), 2f), NoShadow = cfg.Flag("perf-noshadow"), ShadowDist = F(cfg.Str("perf-shadow-dist"), -1f), Vsync = cfg.Flag("perf-vsync"), Tag = cfg.Str("perf-tag"),
        });
    }
}
