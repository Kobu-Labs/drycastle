using System.Diagnostics;
using System.Text.Json;
using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>Round-relative schedule shared by the loss-test server and bots (issue #93): warm-up, measured window, quiet phase, final report.</summary>
public static class LossTimeline
{
    public const double Warm = 3, Stop = 8;
    /// <summary>Server writes its final state this long after the window ends (everything has respawned and settled by then).</summary>
    public const double ServerFinalAfterStop = 7, ServerLateAfterStop = 8.2, BotReportAfterStop = 8.5, ServerEndAfterStop = 11.5;
}

/// <summary>Loss test mode of the server: --loss-test N waits for N players, runs the scripted round and records tick time, bandwidth, per-player counters and exact per-tick states.</summary>
public partial class NetServer
{
    /// <summary>Expected player count; 0 = loss test off.</summary>
    public int LossTest;
    public double LossMeasure = 30;
    /// <summary>Directory for server_states.csv (per-tick quantised states of every player, as sent).</summary>
    public string LossOut = "";

    sealed class LossStat { public string Name = ""; public int HitsTaken, HitsDealt, Deaths, Kills, HitEventsSent; }
    readonly Dictionary<ushort, LossStat> _loss = new();
    double _lossT0 = -1;
    bool _lossWindow, _lossWindowDone, _lossFinalDone, _lossEnded;
    readonly List<double> _tickWork = new(), _tickGap = new();
    ulong _lastTickUs;
    double _cpuStart, _winStartT;
    uint _winStartTick;
    Dictionary<string, object?> _winStats = new();
    Dictionary<string, object?>? _final, _late;
    StreamWriter? _stateLog;
    List<EntityRecord> _lastEnts = new();

    LossStat Ls(ushort id) { if (!_loss.TryGetValue(id, out var s)) _loss[id] = s = new LossStat { Name = _byId.TryGetValue(id, out var c) ? c.Name : "" }; return s; }

    void LossHit(ushort attacker, ushort victim, bool kill)
    {
        if (LossTest == 0) return;
        Ls(victim).HitsTaken++;
        if (attacker != victim) Ls(attacker).HitsDealt++;
        Ls(victim).HitEventsSent++;
        if (attacker != victim) Ls(attacker).HitEventsSent++;
    }

    void LossDeath(ushort victim) { if (LossTest > 0) Ls(victim).Deaths++; }
    void LossKill(ushort killer, ushort victim) { if (LossTest > 0 && killer != victim) Ls(killer).Kills++; }
    void LossRoundStart() { if (LossTest > 0 && _lossT0 < 0) { _lossT0 = _elapsed; GD.Print($"loss-test: round started with {_clients.Count} players, schedule t0={_elapsed:F2}"); } }

    /// <summary>Keeps one dense cluster of spawn candidates so the bots meet and shoot each other (the real map's spawns are spread over the whole village).</summary>
    void LossClusterSpawns()
    {
        if (LossTest == 0 || Spawns.Count == 0) return;
        // flat neighbourhoods only (within 60 m horizontally and 4 m vertically), so prediction error in the report is not dominated by slopes and falls
        bool Near(NVec3 a, NVec3 b) => new System.Numerics.Vector2(a.X - b.X, a.Z - b.Z).Length() < 60f && MathF.Abs(a.Y - b.Y) < 4f;
        var best = Spawns.OrderByDescending(a => Spawns.Count(b => Near(a, b))).First();
        var keep = Spawns.Where(b => Near(best, b)).Take(40).ToList();
        GD.Print($"loss-test: spawn cluster {keep.Count}/{Spawns.Count} candidates, y {keep.Min(v => v.Y):F1}..{keep.Max(v => v.Y):F1}, around ({best.X:F0},{best.Y:F0},{best.Z:F0})");
        Spawns.Clear(); Spawns.AddRange(keep);
    }

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

    void LossTickEnd(ulong startUs)
    {
        if (LossTest == 0) return;
        var end = Time.GetTicksUsec();
        if (_lossWindow)
        {
            _tickWork.Add((double)(end - startUs) / 1000.0);
            if (_lastTickUs > 0) _tickGap.Add((double)(startUs - _lastTickUs) / 1000.0);
        }
        _lastTickUs = startUs;
        LossUpdate();
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * s.Count) - 1))];
    }

    void LossLogStates(List<EntityRecord> ents)
    {
        _lastEnts = ents;
        if (LossOut == "") return;
        _stateLog ??= new StreamWriter(Path.Combine(LossOut, "server_states.csv")) { AutoFlush = false };
        foreach (var e in ents)
            if (e.Type == EntityType.Player) _stateLog.WriteLine($"{_tick},{e.Id},{e.X},{e.Y},{e.Z},{e.Health},{(int)e.State}");
    }

    Dictionary<string, object?> LossPlayers() => new()
    {
        ["players"] = _byId.Values.OrderBy(c => c.Id).Select(c =>
        {
            var e = _lastEnts.FirstOrDefault(x => x.Id == c.Id && x.Type == EntityType.Player);
            var l = Ls(c.Id); var mp = _match.Get(c.Id);
            return new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["name"] = c.Name, ["x"] = e.X, ["y"] = e.Y, ["z"] = e.Z, ["health"] = (int)e.Health, ["state"] = (int)e.State,
                ["kills"] = mp?.Kills ?? 0, ["deaths"] = mp?.Deaths ?? 0, ["hitsTaken"] = l.HitsTaken, ["hitsDealt"] = l.HitsDealt, ["hitEventsSent"] = l.HitEventsSent,
                ["deathEventsSent"] = l.Deaths, ["killsStat"] = l.Kills,
            };
        }).ToList(),
    };

    void LossUpdate()
    {
        if (_lossT0 < 0) return;
        var t = _elapsed - _lossT0;
        if (!_lossWindow && !_lossWindowDone && t >= LossTimeline.Warm)
        {
            _lossWindow = true; _winStartT = _elapsed; _winStartTick = _tick; _cpuStart = CpuSeconds();
            _host.PopStatistic(ENetConnection.HostStatistic.SentData); _host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
            _host.PopStatistic(ENetConnection.HostStatistic.SentPackets); _host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
            if (Sim != null) { Sim.Sent = 0; Sim.Dropped = 0; }
            _tickWork.Clear(); _tickGap.Clear();
            GD.Print("loss-test: window start");
        }
        if (_lossWindow && t >= LossTimeline.Warm + LossMeasure)
        {
            _lossWindow = false; _lossWindowDone = true;
            var secs = _elapsed - _winStartT;
            var n = Math.Max(1, _byId.Count);
            _winStats = new()
            {
                ["window_s"] = secs, ["start_tick"] = _winStartTick, ["end_tick"] = _tick, ["players"] = _byId.Count, ["ticks"] = _tickWork.Count,
                ["tick_work_ms"] = new { p50 = Pct(_tickWork, 50), p95 = Pct(_tickWork, 95), p99 = Pct(_tickWork, 99), max = _tickWork.Count > 0 ? _tickWork.Max() : 0 },
                ["tick_interval_ms"] = new { p50 = Pct(_tickGap, 50), p95 = Pct(_tickGap, 95), p99 = Pct(_tickGap, 99), max = _tickGap.Count > 0 ? _tickGap.Max() : 0 },
                ["ticks_over_33ms_interval"] = _tickGap.Count(g => g > 1000.0 / Wire.TickRate * 1.5),
                ["cpu_percent_of_one_core"] = (CpuSeconds() - _cpuStart) / secs * 100,
                ["sent_kBps_total"] = _host.PopStatistic(ENetConnection.HostStatistic.SentData) / secs / 1000,
                ["recv_kBps_total"] = _host.PopStatistic(ENetConnection.HostStatistic.ReceivedData) / secs / 1000,
                ["sent_packets_per_s_total"] = _host.PopStatistic(ENetConnection.HostStatistic.SentPackets) / secs,
                ["sim_unreliable_sent"] = Sim?.Sent ?? 0, ["sim_unreliable_dropped"] = Sim?.Dropped ?? 0,
            };
            _winStats["sent_kBps_per_client"] = (double)_winStats["sent_kBps_total"]! / n;
            _winStats["recv_kBps_per_client"] = (double)_winStats["recv_kBps_total"]! / n;
            GD.Print("loss-test: window end");
        }
        if (!_lossFinalDone && t >= LossTimeline.Warm + LossMeasure + LossTimeline.ServerFinalAfterStop)
        {
            _lossFinalDone = true;
            _final = LossPlayers();
            _final["tick"] = _tick;
            var ranking = _match.Ranking().Select(p => new { id = p.Id, kills = p.Kills, deaths = p.Deaths }).ToList();
            _final["scoreboard"] = ranking;
            _final["phase"] = _match.Phase.ToString();
            GD.Print($"LOSSTEST_FINAL {JsonSerializer.Serialize(_final)}");
        }
        if (_late == null && t >= LossTimeline.Warm + LossMeasure + LossTimeline.ServerLateAfterStop) _late = LossPlayers();
        if (!_lossEnded && t >= LossTimeline.Warm + LossMeasure + LossTimeline.ServerEndAfterStop) LossEnd(true);
    }

    void LossEnd(bool complete)
    {
        if (_lossEnded) return;
        _lossEnded = true;
        _stateLog?.Flush(); _stateLog?.Dispose();
        var d = new Dictionary<string, object?>
        {
            ["complete"] = complete, ["expected_players"] = LossTest, ["peak_players"] = _peakPlayers, ["net_sim"] = Sim?.ToString() ?? "none",
            ["window"] = _winStats, ["final"] = _final, ["late"] = _late, ["rounds_ended"] = _roundsEnded,
        };
        GD.Print($"LOSSTEST_SERVER {JsonSerializer.Serialize(d)}");
        GD.Print(complete ? "SHOOTER_LOSS_SERVER_DONE" : "SHOOTER_LOSS_SERVER_INCOMPLETE");
        GetTree().Quit(complete ? 0 : 2);
        SetPhysicsProcess(false);
    }
}
