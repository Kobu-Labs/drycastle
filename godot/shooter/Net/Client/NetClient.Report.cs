using Godot;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The client's performance report (docs/monitoring.md, "Clients"): every ten seconds of play the frame times, the engine's render counters, memory,
/// the collector, the connection and the client's own scopes (<see cref="ClientProf"/>) are printed ("client: perf", "client: perf scopes") and sent to
/// the server (<see cref="EventKind.ClientReport"/>), which hands them to the metrics store under the player's name. Always on for a player with a
/// window; a bot sends none. Cost: a histogram add a frame, six engine counters every eighth frame, one small reliable message a period.
/// <c>--perf-play</c> (the client load test, perf/vps_client_test.sh): the player walks, turns, jumps and fires by script, the frame cap and vsync are
/// off, the picture is drawn at <c>--perf-res</c> (1920x1080), and the client quits after <c>--perf-seconds</c>.
/// </summary>
public partial class NetClient
{
    public bool PerfPlay;
    public double PerfSeconds = 300;
    public string PerfRes = "1920x1080";

    readonly FrameTimes _rpFrames = new();
    readonly float[] _rpValues = new float[ClientReport.Fields.Length], _rpScopes = new float[ClientProf.ScopeCount];
    double _rpStart = -1, _rpProcess, _rpPhysics, _rpCpu, _rpGpu, _rpDraws, _rpPrims, _rpObjects, _rpGapMax, _rpLastArr, _rpPauseMs;
    int _rpSamples, _rpFrame, _rpSnaps, _rpTickSkips, _rpRecon0, _rpGen0, _rpGen1, _rpGen2;
    uint _rpLastTick;
    float _rpErrMax;
    long _rpAlloc;
    Rid _rpViewport;

    void ReportSnapshot(uint tick)
    {
        var now = Ctx.Now;
        if (_rpLastArr > 0) _rpGapMax = Math.Max(_rpGapMax, (now - _rpLastArr) * 1000);
        if (_rpLastTick != 0 && tick > _rpLastTick + 1) _rpTickSkips += (int)(tick - _rpLastTick - 1);
        _rpLastArr = now; _rpLastTick = tick; _rpSnaps++;
    }

    // what the reliable messages and effects cost, by event kind (0: a message that is not an event): the "client: perf events" line, not sent
    readonly long[] _rpEvTicks = new long[256];
    readonly int[] _rpEvCount = new int[256];

    void ReportEvent(byte kind, long ticks) { _rpEvTicks[kind] += ticks; _rpEvCount[kind]++; }

    /// <summary>The ten costliest event kinds of the period as <c>Kind=ms_per_second|per_second</c>.</summary>
    string ReportEvents(double secs)
    {
        var order = Enumerable.Range(0, 256).Where(k => _rpEvCount[k] > 0).OrderByDescending(k => _rpEvTicks[k]).Take(10);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var line = string.Join(" ", order.Select(k => $"{(k == 0 ? "NotAnEvent" : ((EventKind)k).ToString())}={(_rpEvTicks[k] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / secs).ToString("0.##", inv)}|{(_rpEvCount[k] / secs).ToString("0.#", inv)}"));
        Array.Clear(_rpEvTicks); Array.Clear(_rpEvCount);
        return line;
    }

    void ReportPredictionError(float err) { if (err > _rpErrMax) _rpErrMax = err; }

    void ReportFrame(double delta)
    {
        if (Bot || LinkEnded || !_welcomed || _peer == null) return;
        var now = Ctx.Now;
        if (_rpStart < 0)
        {
            if (DisplayServer.GetName() == "headless") { _rpStart = double.MaxValue; return; }
            _rpViewport = GetViewport().GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_rpViewport, true);
            if (PerfPlay)
            {
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0;
                // the picture is drawn at --perf-res whatever size the window manager gave the window, so two runs draw the same pixels
                var wh = PerfRes.Split('x');
                if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h) && w > 0 && h > 0)
                {
                    var win = GetWindow();
                    win.ContentScaleMode = Window.ContentScaleModeEnum.Viewport; win.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore; win.ContentScaleSize = new Vector2I(w, h);
                }
            }
            ReportBegin(now);
            return;
        }
        if (_rpStart == double.MaxValue) return;
        _rpFrames.Add(delta * 1000);
        if ((_rpFrame++ & 7) == 0)
        {
            _rpSamples++;
            _rpProcess += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
            _rpPhysics += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
            _rpDraws += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            _rpPrims += Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
            _rpObjects += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
            _rpCpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(_rpViewport);
            _rpGpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(_rpViewport);
        }
        if (PerfPlay && _elapsed >= PerfSeconds) { GD.Print("PERF_PLAY_DONE"); PerfPlay = false; GetTree().Quit(0); return; }
        if (now - _rpStart < ClientReport.PeriodSeconds) return;
        ReportSend(now);
        ReportBegin(now);
    }

    void ReportBegin(double now)
    {
        _rpStart = now;
        _rpFrames.Reset();
        _rpSamples = 0; _rpProcess = _rpPhysics = _rpCpu = _rpGpu = _rpDraws = _rpPrims = _rpObjects = 0;
        _rpGapMax = 0; _rpSnaps = _rpTickSkips = 0; _rpErrMax = 0; _rpRecon0 = _recon;
        _rpAlloc = GC.GetTotalAllocatedBytes();
        _rpGen0 = GC.CollectionCount(0); _rpGen1 = GC.CollectionCount(1); _rpGen2 = GC.CollectionCount(2);
        _rpPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _conn.PopStatistic(ENetConnection.HostStatistic.ReceivedData); _conn.PopStatistic(ENetConnection.HostStatistic.SentData);
        ClientProf.Take(1, _rpScopes);
        Array.Clear(_rpEvTicks); Array.Clear(_rpEvCount);
    }

    void ReportSend(double now)
    {
        var secs = Math.Max(now - _rpStart, 1e-3);
        var n = Math.Max(_rpSamples, 1);
        var v = _rpValues;
        void Set(string field, double value) { var i = ClientReport.Index(field); if (i >= 0) v[i] = (float)value; }
        Set("fps", _rpFrames.Count / secs);
        Set("frame_p50_ms", _rpFrames.Percentile(50)); Set("frame_p99_ms", _rpFrames.Percentile(99)); Set("frame_max_ms", _rpFrames.MaxMs);
        Set("frames_over_33ms", _rpFrames.Over33); Set("frames_over_50ms", _rpFrames.Over50); Set("frames_over_100ms", _rpFrames.Over100);
        Set("process_peak_ms", _rpProcess / n); Set("physics_peak_ms", _rpPhysics / n); Set("render_cpu_ms", _rpCpu / n); Set("render_gpu_ms", _rpGpu / n);
        Set("draws", _rpDraws / n); Set("prims_k", _rpPrims / n / 1000); Set("objects", _rpObjects / n);
        Set("nodes", Performance.GetMonitor(Performance.Monitor.ObjectNodeCount));
        Set("video_mem_mb", Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0);
        Set("rss_mb", System.Environment.WorkingSet / 1048576.0);
        Set("gc_heap_mb", GC.GetTotalMemory(false) / 1048576.0);
        Set("alloc_mb_s", (GC.GetTotalAllocatedBytes() - _rpAlloc) / 1048576.0 / secs);
        Set("gc_gen0", GC.CollectionCount(0) - _rpGen0); Set("gc_gen1", GC.CollectionCount(1) - _rpGen1); Set("gc_gen2", GC.CollectionCount(2) - _rpGen2);
        Set("gc_pause_ms", GC.GetTotalPauseDuration().TotalMilliseconds - _rpPauseMs);
        Set("rtt_ms", _peer!.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime));
        Set("rtt_var_ms", _peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTimeVariance));
        Set("loss_pct", _peer.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) / 65536.0 * 100); // ENet's scale is 1 << 16
        Set("snaps_per_s", _rpSnaps / secs); Set("snap_gap_max_ms", _rpGapMax); Set("tick_skips", _rpTickSkips);
        Set("reconciles", _recon - _rpRecon0); Set("pred_err_max_m", _rpErrMax);
        Set("in_kb_s", _conn.PopStatistic(ENetConnection.HostStatistic.ReceivedData) / secs / 1000);
        Set("out_kb_s", _conn.PopStatistic(ENetConnection.HostStatistic.SentData) / secs / 1000);
        Set("remotes", Ctx.Remotes.Count); Set("world_entities", WorldEntities.Entities.Count);
        var size = GetViewport().GetVisibleRect().Size; // what is drawn (the window's size unless the picture is scaled)
        Set("res_w", size.X); Set("res_h", size.Y); Set("driving", Ctx.Driving ? 1 : 0);
        ClientProf.Take(secs, _rpScopes);
        GD.Print("client: perf " + ClientReport.Line(v));
        GD.Print("client: perf scopes ms_per_s " + ClientReport.ScopeLine(_rpScopes));
        GD.Print("client: perf events ms_per_s|per_s " + ReportEvents(secs));
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.ClientReport, ClientReport.Encode(v, _rpScopes, RenderingServer.GetVideoAdapterName(), OS.GetName()))), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>The scripted player of <c>--perf-play</c>: sprints, turns a quarter every 1.5 s, jumps now and then, fires for half a second every two.</summary>
    MoveInput PerfPlayStep()
    {
        var t = _elapsed;
        var yaw = (float)((int)(t / 1.5) * Math.PI / 2);
        var buttons = MoveButtons.Sprint;
        if ((int)(t * 30) % 45 == 0) buttons |= MoveButtons.Jump;
        if (t % 2.0 < 0.5) buttons = (buttons & ~MoveButtons.Sprint) | MoveButtons.Fire;
        Ctx.Me!.Yaw = yaw;
        return new MoveInput { MoveY = 1, Yaw = yaw, Buttons = buttons, SpeedScale = 1 };
    }
}
