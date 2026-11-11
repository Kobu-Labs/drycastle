using System.Text.Json;
using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Loss and latency test bot (issue #93). Round-relative timeline (see <see cref="LossTimeline"/>): warm-up, a measured window with scripted walking
/// (shooters also chase and shoot the nearest player), a quiet phase in which everything settles, then one JSON report line and exit.
/// Also writes every decoded snapshot (quantised player states) to a csv so the runner can compare it with the server's per-tick log.
/// </summary>
public partial class NetClient
{
    public double LossMeasure = 30;
    /// <summary>Directory for the per-bot snapshot csv; empty = no log.</summary>
    public string LossOut = "";

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
    double _lWindowStartNow, _lWindowSecs;
    Dictionary<string, object?> _lWin = new();

    double LossT => _lt0 < 0 ? -1 : Now - _lt0;

    void LossInit()
    {
        _lInit = true;
        _lStart = Now;
        Engine.MaxFps = 60;
        EventReceived += (k, pl) =>
        {
            switch (k)
            {
                case EventKind.Hit when WeaponCodec.DecodeHit(pl) is { } h:
                    if (h.Victim == _myId) _lHitVictim++; else if (h.Attacker == _myId) _lHitAttacker++; else _lHitOther++;
                    break;
                case EventKind.Death when MatchCodec.DecodeDeath(pl) is { } d: if (d.Victim == _myId) _lDeathSelf++; break;
                case EventKind.Kill: _lKillEvents++; break;
                case EventKind.Shot: _lShotEvents++; break;
            }
        };
        if (LossOut != "") _lLog = new StreamWriter(Path.Combine(LossOut, $"snap_{PlayerName}.csv")) { AutoFlush = false };
    }

    void LossOnSnapshot(SnapshotHeader h)
    {
        _lAck = h.Ack;
        _lSnapsTotal++;
        var now = Now;
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

    /// <summary>Errors above <see cref="SnapDistance"/> are respawn or teleport snaps (the position jump can arrive before the spawn event), counted apart so they do not hide the real prediction error.</summary>
    void LossOnPredictionError(float err)
    {
        if (!_lwin) return;
        if (err > SnapDistance) { _lBigErr++; return; }
        _lErr.Add(err);
        _lMaxErr = Math.Max(_lMaxErr, err);
    }

    void LossAfterSnapshot(SnapshotHeader h)
    {
        if (_lLog == null) return;
        foreach (var (id, rec) in _dec.States)
            if (rec.Type == EntityType.Player) _lLog.WriteLine($"{h.Tick},{id},{rec.X},{rec.Y},{rec.Z},{rec.Health},{(int)rec.State}");
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * s.Count) - 1))];
    }

    void LossFail(string why)
    {
        if (_done) return;
        GD.PrintErr($"client {PlayerName}: loss test failure: {why}");
        LossReport(false, why);
    }

    void LossReport(bool ok, string why)
    {
        _done = true;
        _lLog?.Flush(); _lLog?.Dispose();
        var players = _dec.States.Values.Where(r => r.Type == EntityType.Player).OrderBy(r => r.Id)
            .Select(r => new { id = (int)r.Id, x = r.X, y = r.Y, z = r.Z, health = (int)r.Health, state = (int)r.State }).ToList();
        var self = _dec.States.TryGetValue(_myId, out var me) ? me : default;
        var predErr = _me != null && self.Id == _myId ? (_me.State.Position - self.Position).Length() : -1f;
        var rep = new Dictionary<string, object?>
        {
            ["ok"] = ok, ["why"] = why, ["name"] = PlayerName, ["id"] = (int)_myId, ["role"] = LossBot,
            ["window"] = _lWin,
            ["snapshots_total"] = _lSnapsTotal, ["final_tick"] = _lastTick, ["seconds_since_last_snapshot"] = _lastArrival > 0 ? Now - _lastArrival : -1,
            ["ack_lag_inputs"] = (ushort)(_seq - _lAck),
            ["final_pred_vs_server_m"] = predErr, ["final_players"] = players,
            ["final_scoreboard"] = Match.Rows.OrderBy(r => r.Id).Select(r => new { id = (int)r.Id, kills = r.Kills, deaths = r.Deaths }).ToList(),
            ["hit_events_as_victim"] = _lHitVictim, ["hit_events_as_attacker"] = _lHitAttacker, ["hit_events_other"] = _lHitOther,
            ["death_events_self"] = _lDeathSelf, ["kill_events"] = _lKillEvents, ["shot_events"] = _lShotEvents,
            ["sim"] = Sim?.ToString() ?? "none", ["sim_unreliable_sent"] = Sim?.Sent ?? 0, ["sim_unreliable_dropped"] = Sim?.Dropped ?? 0,
        };
        GD.Print($"LOSSTEST_CLIENT {JsonSerializer.Serialize(rep)}");
        GD.Print(ok ? "SHOOTER_LOSS_CLIENT_DONE" : "SHOOTER_LOSS_CLIENT_FAIL");
        GetTree().Quit(ok ? 0 : 2);
    }

    MoveInput LossBotStep()
    {
        if (!_lInit) LossInit();
        if (_lt0 < 0 && Match.Phase == MatchPhase.Running) { _lt0 = Now; GD.Print($"client {PlayerName}: round running, schedule t0"); }
        var t = LossT;
        var tWin0 = LossTimeline.Warm;
        var tWin1 = tWin0 + LossMeasure;
        if (!_lwin && !_lwinDone && t >= tWin0)
        {
            _lwin = true; _lWindowStartNow = Now; _lRecon0 = _recon; _lLastArr = 0; _lErr.Clear(); _lMaxErr = 0; _lBigErr = 0;
            _conn.PopStatistic(ENetConnection.HostStatistic.SentData); _conn.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
            if (Sim != null) { Sim.Sent = 0; Sim.Dropped = 0; }
        }
        if (_lwin && t >= tWin1)
        {
            _lwin = false; _lwinDone = true;
            var secs = Now - _lWindowStartNow;
            _lWin = new()
            {
                ["seconds"] = secs, ["snapshots"] = _lSnaps, ["snapshots_per_s"] = _lSnaps / secs, ["max_gap_ms"] = _lMaxGap, ["gaps_over_200ms"] = _lGaps200, ["gaps_over_500ms"] = _lGaps500,
                ["max_tick_gap"] = _lMaxTickGap, ["reconciliations"] = _recon - _lRecon0, ["max_pred_error_m"] = _lMaxErr, ["pred_error_p95_m"] = Pct(_lErr, 95), ["teleport_snaps"] = _lBigErr,
                ["recv_kBps"] = _conn.PopStatistic(ENetConnection.HostStatistic.ReceivedData) / secs / 1000,
                ["sent_kBps"] = _conn.PopStatistic(ENetConnection.HostStatistic.SentData) / secs / 1000,
            };
        }
        if (_lt0 >= 0 && t >= tWin1 + LossTimeline.BotReportAfterStop) { LossReport(true, ""); return default; }
        var quiet = t >= tWin1;
        if (Dead || quiet) return new MoveInput { SpeedScale = 1, Yaw = _me!.Yaw };
        if (LossBot == "shooter") return ShooterStep(t);
        var phase = (int)(_elapsed / 1.5);
        var yaw = (float)(phase * Math.PI / 2 + (PlayerName.GetHashCode() & 3) * Math.PI / 2);
        var jump = ((int)(_elapsed * 30) % 45) == 0 ? MoveButtons.Jump : MoveButtons.None;
        return new MoveInput { MoveY = 1, Yaw = yaw, Buttons = jump, SpeedScale = 1 };
    }

    /// <summary>Chases the nearest living player within 40 m, aims at the chest and fires the AK in bursts (reload pulse between them).</summary>
    MoveInput ShooterStep(double t)
    {
        _wantSlot = 2; // ak47
        var me = _me!.State.Position;
        EntityRecord? best = null; var bestD = 40f;
        foreach (var r in _dec.States.Values)
        {
            if (r.Type != EntityType.Player || r.Id == _myId || r.State is EntityState.Dead or EntityState.InVehicle) continue;
            var d = (r.Position - me).Length();
            if (d < bestD) { bestD = d; best = r; }
        }
        var cycle = (int)(_elapsed * 10) % 60; // 6 s: fire 0..2.7 s, reload pulse at 2.8, wait
        if (best is not { } tgt)
        {
            var phase = (int)(_elapsed / 1.5);
            return new MoveInput { MoveY = 1, Yaw = (float)(phase * Math.PI / 2 + 1), SpeedScale = 1 };
        }
        var dx = tgt.Position.X - me.X; var dz = tgt.Position.Z - me.Z;
        var yaw = MathF.Atan2(-dx, -dz);
        var horiz = MathF.Sqrt(dx * dx + dz * dz);
        var pitch = MathF.Atan2(tgt.Position.Y + 1.2f - (me.Y + 1.6f), MathF.Max(0.1f, horiz));
        _me.Pitch = Math.Clamp(pitch, -1.4f, 1.4f);
        var btn = MoveButtons.None;
        if (cycle < 27) btn |= MoveButtons.Fire;
        else if (cycle == 28) btn |= MoveButtons.Reload;
        return new MoveInput { MoveY = bestD > 8f ? 1 : 0, Yaw = yaw, Buttons = btn, SpeedScale = 1 };
    }
}
