using System.Text.Json;
using Godot;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>Loss test mode of the server: --loss-test N waits for N players, runs the scripted round and records tick time, bandwidth, per-player counters and exact per-tick states.</summary>
internal sealed class LossRun
{
    sealed class LossStat { public string Name = ""; public int HitsTaken, HitsDealt, Deaths, Kills, HitEventsSent; }

    readonly ServerConfig _config;
    readonly ServerContext _ctx;
    readonly ENetConnection _host;
    readonly SmokeStats _smoke;
    readonly Action<bool> _quit;
    readonly Dictionary<ushort, LossStat> _loss = new();
    double _t0 = -1;
    bool _window, _windowDone, _finalDone, _ended;
    readonly List<double> _tickWork = new(), _tickGap = new();
    ulong _lastTickUs;
    double _cpuStart, _winStartT;
    uint _winStartTick;
    Dictionary<string, object?> _winStats = new();
    Dictionary<string, object?>? _final, _late;
    StreamWriter? _stateLog;
    List<EntityRecord> _lastEnts = new();

    public LossRun(ServerConfig config, ServerContext ctx, ENetConnection host, SmokeStats smoke, Action<bool> quit)
    {
        _config = config; _ctx = ctx; _host = host; _smoke = smoke; _quit = quit;
    }

    /// <summary>--loss-test N is set.</summary>
    public bool On => _config.LossTest > 0;

    LossStat Ls(ushort id) { if (!_loss.TryGetValue(id, out var s)) _loss[id] = s = new LossStat { Name = _ctx.Players.TryGetValue(id, out var c) ? c.Name : "" }; return s; }

    public void Hit(ushort attacker, ushort victim, bool kill)
    {
        if (!On) return;
        Ls(victim).HitsTaken++;
        if (attacker != victim) Ls(attacker).HitsDealt++;
        Ls(victim).HitEventsSent++;
        if (attacker != victim) Ls(attacker).HitEventsSent++;
    }

    public void Death(ushort victim) { if (On) Ls(victim).Deaths++; }
    public void Kill(ushort killer, ushort victim) { if (On && killer != victim) Ls(killer).Kills++; }
    public void RoundStart() { if (On && _t0 < 0) { _t0 = _ctx.Now; GD.Print($"loss-test: round started with {_ctx.Connections.Count()} players, schedule t0={_ctx.Now:F2}"); } }

    /// <summary>Keeps one dense cluster of spawn candidates so the bots meet and shoot each other (the real map's spawns are spread over the whole village).</summary>
    public void KeepSpawnCluster(List<NVec3> spawns)
    {
        if ((!On && !_config.ClusterSpawns) || spawns.Count == 0) return;
        // flat neighbourhoods only (within 60 m horizontally and 4 m vertically), so prediction error in the report is not dominated by slopes and falls
        bool Near(NVec3 a, NVec3 b) => new System.Numerics.Vector2(a.X - b.X, a.Z - b.Z).Length() < 60f && MathF.Abs(a.Y - b.Y) < 4f;
        var best = spawns.OrderByDescending(a => spawns.Count(b => Near(a, b))).First();
        var keep = spawns.Where(b => Near(best, b)).Take(40).ToList();
        GD.Print($"server: spawn cluster {keep.Count}/{spawns.Count} candidates, y {keep.Min(v => v.Y):F1}..{keep.Max(v => v.Y):F1}, around ({best.X:F0},{best.Y:F0},{best.Z:F0})");
        spawns.Clear(); spawns.AddRange(keep);
    }

    public void TickEnd(ulong startUs)
    {
        if (!On) return;
        var end = Time.GetTicksUsec();
        if (_window)
        {
            _tickWork.Add((double)(end - startUs) / 1000.0);
            if (_lastTickUs > 0) _tickGap.Add((double)(startUs - _lastTickUs) / 1000.0);
        }
        _lastTickUs = startUs;
        Update();
    }

    /// <summary>The per-tick quantised states of every player, as sent; written to server_states.csv when the test has an output directory.</summary>
    public void LogStates(List<EntityRecord> ents)
    {
        _lastEnts = ents;
        if (_config.LossOut == "") return;
        _stateLog ??= new StreamWriter(Path.Combine(_config.LossOut, "server_states.csv")) { AutoFlush = false };
        foreach (var e in ents)
            if (e.Type == EntityType.Player) _stateLog.WriteLine($"{_ctx.Tick},{e.Id},{e.X},{e.Y},{e.Z},{e.Health},{(int)e.State}");
    }

    /// <summary>The test ends with its verdict: complete when the schedule ran to its end, incomplete when the quit deadline came first.</summary>
    public void End(bool complete)
    {
        if (_ended) return;
        _ended = true;
        _stateLog?.Flush(); _stateLog?.Dispose();
        var d = new Dictionary<string, object?>
        {
            ["complete"] = complete, ["expected_players"] = _config.LossTest, ["peak_players"] = _smoke.PeakPlayers, ["net_sim"] = _config.Sim?.ToString() ?? "none",
            ["window"] = _winStats, ["final"] = _final, ["late"] = _late, ["rounds_ended"] = _smoke.RoundsEnded,
        };
        GD.Print($"LOSSTEST_SERVER {JsonSerializer.Serialize(d)}");
        GD.Print(complete ? "SHOOTER_LOSS_SERVER_DONE" : "SHOOTER_LOSS_SERVER_INCOMPLETE");
        _quit(complete);
    }

    void Update()
    {
        if (_t0 < 0) return;
        var t = _ctx.Now - _t0;
        var measure = _config.LossMeasure;
        if (!_window && !_windowDone && t >= LossTimeline.Warm) StartWindow();
        if (_window && t >= LossTimeline.Warm + measure) EndWindow();
        if (!_finalDone && t >= LossTimeline.Warm + measure + LossTimeline.ServerFinalAfterStop)
        {
            _finalDone = true;
            _final = Players();
            _final["tick"] = _ctx.Tick;
            var ranking = _ctx.Match.Ranking().Select(p => new { id = p.Id, kills = p.Kills, deaths = p.Deaths }).ToList();
            _final["scoreboard"] = ranking;
            _final["phase"] = _ctx.Match.Phase.ToString();
            GD.Print($"LOSSTEST_FINAL {JsonSerializer.Serialize(_final)}");
        }
        if (_late == null && t >= LossTimeline.Warm + measure + LossTimeline.ServerLateAfterStop) _late = Players();
        if (!_ended && t >= LossTimeline.Warm + measure + LossTimeline.ServerEndAfterStop) End(true);
    }

    void StartWindow()
    {
        _window = true; _winStartT = _ctx.Now; _winStartTick = _ctx.Tick; _cpuStart = CpuSeconds();
        _host.PopStatistic(ENetConnection.HostStatistic.SentData); _host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
        _host.PopStatistic(ENetConnection.HostStatistic.SentPackets); _host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
        if (_config.Sim is { } sim) { sim.Sent = 0; sim.Dropped = 0; }
        _tickWork.Clear(); _tickGap.Clear();
        GD.Print("loss-test: window start");
    }

    void EndWindow()
    {
        _window = false; _windowDone = true;
        var secs = _ctx.Now - _winStartT;
        var n = Math.Max(1, _ctx.Players.Count);
        var sim = _config.Sim;
        _winStats = new()
        {
            ["window_s"] = secs, ["start_tick"] = _winStartTick, ["end_tick"] = _ctx.Tick, ["players"] = _ctx.Players.Count, ["ticks"] = _tickWork.Count,
            ["tick_work_ms"] = Spread(_tickWork),
            ["tick_interval_ms"] = Spread(_tickGap),
            ["ticks_over_33ms_interval"] = _tickGap.Count(g => g > 1000.0 / Wire.TickRate * 1.5),
            ["cpu_percent_of_one_core"] = (CpuSeconds() - _cpuStart) / secs * 100,
            ["sent_kBps_total"] = _host.PopStatistic(ENetConnection.HostStatistic.SentData) / secs / 1000,
            ["recv_kBps_total"] = _host.PopStatistic(ENetConnection.HostStatistic.ReceivedData) / secs / 1000,
            ["sent_packets_per_s_total"] = _host.PopStatistic(ENetConnection.HostStatistic.SentPackets) / secs,
            ["sim_unreliable_sent"] = sim?.Sent ?? 0, ["sim_unreliable_dropped"] = sim?.Dropped ?? 0,
        };
        _winStats["sent_kBps_per_client"] = (double)_winStats["sent_kBps_total"]! / n;
        _winStats["recv_kBps_per_client"] = (double)_winStats["recv_kBps_total"]! / n;
        GD.Print("loss-test: window end");
    }

    static object Spread(List<double> ms)
    {
        var sorted = ms.OrderBy(x => x).ToList();
        return new { p50 = Percentile.Of(sorted, 50), p95 = Percentile.Of(sorted, 95), p99 = Percentile.Of(sorted, 99), max = sorted.Count > 0 ? sorted.Max() : 0 };
    }

    Dictionary<string, object?> Players() => new()
    {
        ["players"] = _ctx.Roster.OrderBy(c => c.Id).Select(c =>
        {
            var e = _lastEnts.FirstOrDefault(x => x.Id == c.Id && x.Type == EntityType.Player);
            var l = Ls(c.Id); var mp = _ctx.Match.Get(c.Id);
            return new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["name"] = c.Name, ["x"] = e.X, ["y"] = e.Y, ["z"] = e.Z, ["health"] = (int)e.Health, ["state"] = (int)e.State,
                ["kills"] = mp?.Kills ?? 0, ["deaths"] = mp?.Deaths ?? 0, ["hitsTaken"] = l.HitsTaken, ["hitsDealt"] = l.HitsDealt, ["hitEventsSent"] = l.HitEventsSent,
                ["deathEventsSent"] = l.Deaths, ["killsStat"] = l.Kills, ["stepTicks"] = c.StepTicks, ["starvedTicks"] = c.StarvedTicks,
            };
        }).ToList(),
    };

    static double CpuSeconds()
    {
        try
        {
            var s = File.ReadAllText("/proc/self/stat");
            var f = s[(s.LastIndexOf(')') + 2)..].Split(' ');
            return (double.Parse(f[11]) + double.Parse(f[12])) / 100.0;
        }
        catch { return 0; }
    }
}
