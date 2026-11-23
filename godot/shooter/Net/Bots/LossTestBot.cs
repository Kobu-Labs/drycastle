using System.Text.Json;
using Godot;
using Drycastle.Shooter.Dev;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.Shooter.Net.Server;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Sandbox;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// Loss and latency test bot (issue #93). Round-relative timeline (see <see cref="LossTimeline"/>): warm-up, a measured window with scripted walking
/// (shooters also chase and shoot the nearest player), a quiet phase in which everything settles, then one JSON report line and exit.
/// Also writes every decoded snapshot (quantised player states) to a csv so the runner can compare it with the server's per-tick log.
/// The role says what it does in the window: shooter, idle, straight, jumper, turner (movement patterns), streaker or aimer (perf harness), else a walker.
/// </summary>
public sealed class LossTestBot : ClientBot
{
    readonly string _role;
    readonly double _measure;
    readonly string _out;
    readonly string _streaks, _vehicles;
    readonly double _streakPeriod;
    double _lt0 = -1;
    bool _lwin, _lwinDone, _lInit;
    int _lSnaps, _lSnapsTotal, _lRecon0, _lBigErr, _lGaps200, _lGaps500;
    double _lLastArr, _lMaxGap, _lMaxErr;
    uint _lLastTick;
    uint _lMaxTickGap;
    readonly List<double> _lErr = new();
    ushort _lAck;
    int _lHitVictim, _lHitAttacker, _lHitOther, _lDeathSelf, _lKillEvents, _lShotEvents;
    double _lStart;
    StreamWriter? _lLog;
    double _lWindowStartNow;
    Dictionary<string, object?> _lWin = new();

    public LossTestBot(IBotHost host, RozhlasTestBot? chat, string role, double measure, string outDir, string streaks, string vehicles, double streakPeriod) : base(host, chat)
    {
        _role = role; _measure = measure; _out = outDir; _streaks = streaks; _vehicles = vehicles; _streakPeriod = streakPeriod;
    }

    double LossT => _lt0 < 0 ? -1 : Ctx.Now - _lt0;

    void Init()
    {
        _lInit = true;
        _lStart = Ctx.Now;
        Engine.MaxFps = 60;
        if (_out != "") _lLog = new StreamWriter(Path.Combine(_out, $"snap_{Ctx.PlayerName}.csv")) { AutoFlush = false };
    }

    public override void OnEvent(EventKind k, byte[] pl)
    {
        if (!_lInit) return;
        switch (k)
        {
            case EventKind.Hit when WeaponCodec.DecodeHit(pl) is { } h:
                if (h.Victim == Ctx.MyId) _lHitVictim++; else if (h.Attacker == Ctx.MyId) _lHitAttacker++; else _lHitOther++;
                break;
            case EventKind.Death when MatchCodec.DecodeDeath(pl) is { } d: if (d.Victim == Ctx.MyId) _lDeathSelf++; break;
            case EventKind.Kill: _lKillEvents++; break;
            case EventKind.Shot: _lShotEvents++; break;
        }
    }

    public override void OnSnapshot(SnapshotHeader h)
    {
        _lAck = h.Ack;
        _lSnapsTotal++;
        var now = Ctx.Now;
        if (_lwin)
        {
            _lSnaps++;
            if (_lLastArr > 0)
            {
                var gap = (now - _lLastArr) * 1000;
                _lMaxGap = Math.Max(_lMaxGap, gap);
                if (gap > 200) _lGaps200++;
                if (gap > 500) _lGaps500++;
                if (h.Tick > _lLastTick) _lMaxTickGap = Math.Max(_lMaxTickGap, h.Tick - _lLastTick);
            }
            _lLastTick = h.Tick;
        }
        _lLastArr = now;
    }

    /// <summary>Errors above <see cref="NetClient.SnapDistance"/> are respawn or teleport snaps (the position jump can arrive before the spawn event), counted apart so they do not hide the real prediction error.</summary>
    public override void OnPredictionError(float err)
    {
        if (!_lwin) return;
        if (err > NetClient.SnapDistance) { _lBigErr++; return; }
        _lErr.Add(err);
        _lMaxErr = Math.Max(_lMaxErr, err);
    }

    public override void AfterSnapshot(SnapshotHeader h)
    {
        if (_lLog == null) return;
        foreach (var (id, rec) in Host.States)
            if (rec.Type == EntityType.Player) _lLog.WriteLine($"{h.Tick},{id},{rec.X},{rec.Y},{rec.Z},{rec.Health},{(int)rec.State}");
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * s.Count) - 1))];
    }

    /// <summary>The bot's seconds ran out before the round ended: a failed report, and the tick goes on.</summary>
    public override bool TimedOut()
    {
        Fail("timeout: round never reached its end");
        return false;
    }

    void Fail(string why)
    {
        if (Host.LinkEnded) return;
        GD.PrintErr($"client {Ctx.PlayerName}: loss test failure: {why}");
        Report(false, why);
    }

    void Report(bool ok, string why)
    {
        Host.Leave();
        _lLog?.Flush(); _lLog?.Dispose();
        var players = Host.States.Values.Where(r => r.Type == EntityType.Player).OrderBy(r => r.Id)
            .Select(r => new { id = (int)r.Id, x = r.X, y = r.Y, z = r.Z, health = (int)r.Health, state = (int)r.State }).ToList();
        var self = Host.States.TryGetValue(Ctx.MyId, out var me) ? me : default;
        var predErr = Ctx.Me! != null && self.Id == Ctx.MyId ? (Ctx.Me!.State.Position - self.Position).Length() : -1f;
        var rep = new Dictionary<string, object?>
        {
            ["ok"] = ok, ["why"] = why, ["name"] = Ctx.PlayerName, ["id"] = (int)Ctx.MyId, ["role"] = _role,
            ["window"] = _lWin,
            ["snapshots_total"] = _lSnapsTotal, ["final_tick"] = Host.LastTick, ["seconds_since_last_snapshot"] = Host.LastArrival > 0 ? Ctx.Now - Host.LastArrival : -1,
            ["ack_lag_inputs"] = (ushort)(Host.Seq - _lAck),
            ["final_pred_vs_server_m"] = predErr, ["final_players"] = players,
            ["final_scoreboard"] = Ctx.Match.Rows.OrderBy(r => r.Id).Select(r => new { id = (int)r.Id, kills = r.Kills, deaths = r.Deaths }).ToList(),
            ["hit_events_as_victim"] = _lHitVictim, ["hit_events_as_attacker"] = _lHitAttacker, ["hit_events_other"] = _lHitOther,
            ["death_events_self"] = _lDeathSelf, ["kill_events"] = _lKillEvents, ["shot_events"] = _lShotEvents,
            ["sim"] = Host.Sim?.ToString() ?? "none", ["sim_unreliable_sent"] = Host.Sim?.Sent ?? 0, ["sim_unreliable_dropped"] = Host.Sim?.Dropped ?? 0,
        };
        GD.Print($"LOSSTEST_CLIENT {JsonSerializer.Serialize(rep)}");
        GD.Print(ok ? "SHOOTER_LOSS_CLIENT_DONE" : "SHOOTER_LOSS_CLIENT_FAIL");
        Host.Quit(ok);
    }

    public override MoveInput Step()
    {
        if (!_lInit) Init();
        if (_lt0 < 0 && Ctx.Match.Phase == MatchPhase.Running) { _lt0 = Ctx.Now; GD.Print($"client {Ctx.PlayerName}: round running, schedule t0"); }
        var t = LossT;
        var tWin0 = LossTimeline.Warm;
        var tWin1 = tWin0 + _measure;
        if (!_lwin && !_lwinDone && t >= tWin0)
        {
            _lwin = true; _lWindowStartNow = Ctx.Now; _lRecon0 = Host.Recon; _lLastArr = 0; _lErr.Clear(); _lMaxErr = 0; _lBigErr = 0; Host.Rubber = new RubberStats();
            Host.PopTraffic();
            if (Host.Sim != null) { Host.Sim.Sent = 0; Host.Sim.Dropped = 0; }
        }
        if (_lwin && t >= tWin1)
        {
            _lwin = false; _lwinDone = true;
            var secs = Ctx.Now - _lWindowStartNow;
            var (received, sent) = Host.PopTraffic();
            _lWin = new()
            {
                ["seconds"] = secs, ["snapshots"] = _lSnaps, ["snapshots_per_s"] = _lSnaps / secs, ["max_gap_ms"] = _lMaxGap, ["gaps_over_200ms"] = _lGaps200, ["gaps_over_500ms"] = _lGaps500,
                ["max_tick_gap"] = _lMaxTickGap, ["reconciliations"] = Host.Recon - _lRecon0, ["max_pred_error_m"] = _lMaxErr, ["pred_error_p95_m"] = Pct(_lErr, 95), ["teleport_snaps"] = _lBigErr,
                ["recv_kBps"] = received / secs / 1000,
                ["sent_kBps"] = sent / secs / 1000,
            };
            foreach (var kv in Host.Rubber.ToDict()) _lWin[kv.Key] = kv.Value; // I-rubber-banding telemetry of the window
        }
        if (_lt0 >= 0 && t >= tWin1 + LossTimeline.BotReportAfterStop) { Report(true, ""); return default; }
        var quiet = t >= tWin1;
        if (Ctx.Dead || quiet) return new MoveInput { SpeedScale = 1, Yaw = Ctx.Me!.Yaw };
        if (_role == "shooter") return ShooterStep(t);
        if (_role is "idle" or "straight" or "jumper" or "turner") return PatternStep();
        if (_role == "streaker") return StreakerStep();
        if (_role == "aimer") return AimerStep();
        return WalkStep();
    }

    /// <summary>Chases the nearest living player within 40 m, aims at the chest and fires the AK in bursts (reload pulse between them).</summary>
    MoveInput ShooterStep(double t)
    {
        Host.WantSlot = 2; // ak47
        var me = Ctx.Me!.State.Position;
        EntityRecord? best = null; var bestD = 40f;
        foreach (var r in Host.States.Values)
        {
            if (r.Type != EntityType.Player || r.Id == Ctx.MyId || r.State is EntityState.Dead or EntityState.InVehicle) continue;
            var d = (r.Position - me).Length();
            if (d < bestD) { bestD = d; best = r; }
        }
        var cycle = (int)(Host.Elapsed * 10) % 60; // 6 s: fire 0..2.7 s, reload pulse at 2.8, wait
        if (best is not { } tgt)
        {
            var phase = (int)(Host.Elapsed / 1.5);
            return new MoveInput { MoveY = 1, Yaw = (float)(phase * Math.PI / 2 + 1), SpeedScale = 1 };
        }
        var dx = tgt.Position.X - me.X; var dz = tgt.Position.Z - me.Z;
        var yaw = MathF.Atan2(-dx, -dz);
        var horiz = MathF.Sqrt(dx * dx + dz * dz);
        var pitch = MathF.Atan2(tgt.Position.Y + 1.2f - (me.Y + 1.6f), MathF.Max(0.1f, horiz));
        Ctx.Me!.Pitch = Math.Clamp(pitch, -1.4f, 1.4f);
        var btn = MoveButtons.None;
        if (cycle < 27) btn |= MoveButtons.Fire;
        else if (cycle == 28) btn |= MoveButtons.Reload;
        return new MoveInput { MoveY = bestD > 8f ? 1 : 0, Yaw = yaw, Buttons = btn, SpeedScale = 1 };
    }

    /// <summary>Perf-harness role "streaker": a sandbox client that keeps killstreaks (and vehicles) coming, while it walks like a walker. Load only; asserts nothing.</summary>
    int _psIdx, _pvIdx; double _psNext = 4;

    void SendAction(SandboxAction action, byte arg = 0) => Ctx.Send(EventKind.SandboxAction, SandboxEvents.EncodeAction(action, arg));

    MoveInput StreakerStep()
    {
        if (Host.SandboxGranted && Host.Elapsed >= _psNext)
        {
            _psNext = Host.Elapsed + _streakPeriod;
            var streaks = _streaks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (streaks.Length > 0)
            {
                Ctx.Send(EventKind.SandboxGrant, SandboxEvents.EncodeGrant(streaks[_psIdx++ % streaks.Length]));
                SendAction(SandboxAction.Activate);
            }
            SendAction(SandboxAction.Heal);
            var vehicles = _vehicles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (vehicles.Length > 0 && _psIdx % 5 == 0 && byte.TryParse(vehicles[_pvIdx++ % vehicles.Length], out var vi)) SendAction(SandboxAction.SpawnVehicle, vi);
        }
        var phase = (int)(Host.Elapsed / 1.5);
        var yaw = (float)(phase * Math.PI / 2 + (Ctx.PlayerName.GetHashCode() & 3) * Math.PI / 2);
        return new MoveInput { MoveY = 1, Yaw = yaw, SpeedScale = 1, Buttons = MoveButtons.Fire }; // fire: a pilot streak (attack helicopter) shoots on the pilot's fire input
    }

    /// <summary>
    /// Perf-harness role: stands still and shoots the nearest remote where the screen shows it (the interpolated position), as a perfect-aim human
    /// would. Against a server that resolves at present positions, the hit rate of this bot is the cost of the missing lag compensation.
    /// </summary>
    MoveInput AimerStep()
    {
        Host.WantSlot = 2; // ak47
        var me = Ctx.Me!.State.Position;
        BotRemote? best = null; var bestD = 40f;
        foreach (var r in Host.Remotes)
        {
            if (r.State is EntityState.Dead or EntityState.InVehicle) continue;
            var d = (r.LastPos - new Vector3(me.X, me.Y, me.Z)).Length();
            if (d < bestD) { bestD = d; best = r; }
        }
        var stand = new MoveInput { SpeedScale = 1, Yaw = Ctx.Me!.Yaw };
        if (best == null) return stand;
        var dx = best.LastPos.X - me.X; var dz = best.LastPos.Z - me.Z;
        var yaw = MathF.Atan2(-dx, -dz);
        var horiz = MathF.Sqrt(dx * dx + dz * dz);
        Ctx.Me!.Pitch = Math.Clamp(MathF.Atan2(best.LastPos.Y + 1.2f - (me.Y + 1.6f), MathF.Max(0.1f, horiz)), -1.4f, 1.4f);
        var cycle = (int)(Host.Elapsed * 10) % 60;
        var btn = cycle < 27 ? MoveButtons.Fire : cycle == 28 ? MoveButtons.Reload : MoveButtons.None;
        return new MoveInput { Yaw = yaw, Buttons = btn, SpeedScale = 1 };
    }

    /// <summary>Perf-harness roles for finding what makes the client's prediction disagree with the server (C-02): one movement behaviour each.</summary>
    MoveInput PatternStep()
    {
        var yaw = 0.3f;
        switch (_role)
        {
            case "idle": return new MoveInput { SpeedScale = 1, Yaw = yaw };
            case "straight": return new MoveInput { MoveY = 1, Yaw = yaw, SpeedScale = 1 };
            case "jumper": return new MoveInput { MoveY = 1, Yaw = yaw, SpeedScale = 1, Buttons = ((int)(Host.Elapsed * 30) % 45) == 0 ? MoveButtons.Jump : MoveButtons.None };
            default: return new MoveInput { MoveY = 1, Yaw = (float)((int)(Host.Elapsed / 1.5) * Math.PI / 2), SpeedScale = 1 }; // turner
        }
    }
}
