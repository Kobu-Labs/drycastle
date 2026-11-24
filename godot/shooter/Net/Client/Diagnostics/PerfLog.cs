using Godot;
using Drycastle.ShooterCore.Net;
using System.Globalization;

namespace Drycastle.Shooter.Net.Client.Diagnostics;

/// <summary>The perf-harness client log (--perf-out DIR): one CSV line a second per client, also for bots (measurement plan C1/C3/W2/H-03), and the shot, hit and kill events as they arrived.</summary>
public sealed class PerfLog
{
    readonly ClientContext _ctx;
    readonly string _dir;
    readonly Func<bool> _welcomed;
    readonly Func<ENetPacketPeer?> _peer;
    readonly SnapshotDecoder _dec;

    public PerfLog(ClientContext ctx, string dir, Func<bool> welcomed, Func<ENetPacketPeer?> peer, SnapshotDecoder dec)
    {
        _ctx = ctx;
        _dir = dir;
        _welcomed = welcomed;
        _peer = peer;
        _dec = dec;
    }

    StreamWriter? _pcLog;
    double _pcNext;
    int _pcTS, _pcTC, _pcRecon, _pcFrames, _pcSlow33;
    double _pcMaxGap, _pcLastArr, _pcFrameMax;
    double _pcErrSum, _pcErrMax, _pcDx, _pcDy, _pcDz;
    int _pcErrN;

    /// <summary>Every acked-input comparison of prediction vs server: its size and signed mean per axis (a bias shows a systematic cause).</summary>
    public void PredictionError(float err, System.Numerics.Vector3 d)
    {
        if (_dir.Length == 0) return;
        _pcErrSum += err; _pcErrN++; _pcErrMax = Math.Max(_pcErrMax, err); _pcDx += d.X; _pcDy += d.Y; _pcDz += d.Z;
    }

    /// <summary>Called for every snapshot packet that completed a tick (gaps between completed ticks).</summary>
    public void Snapshot()
    {
        if (_dir.Length == 0) return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (_pcLastArr > 0) _pcMaxGap = Math.Max(_pcMaxGap, (now - _pcLastArr) * 1000);
        _pcLastArr = now;
    }

    public void Frame(double delta)
    {
        if (_dir.Length == 0 || !_welcomed()) return;
        if (_pcLog == null)
        {
            Directory.CreateDirectory(_dir);
            _pcLog = new StreamWriter(Path.Combine(_dir, "client-" + _ctx.PlayerName + ".csv")) { AutoFlush = true };
            _pcLog.WriteLine("t,tick,ticks_started,ticks_completed,reconciles,max_snapshot_gap_ms,rtt_ms,frames,frame_max_ms,frames_over_33ms,remote_count,pred_err_mean_m,pred_err_max_m,err_dx_mean_m,err_dy_mean_m,err_dz_mean_m,err_n,pos_x,pos_y,pos_z");
            _pcNext = Time.GetTicksMsec() / 1000.0 + 1; _pcTS = _dec.TicksStarted; _pcTC = _dec.TicksCompleted; _pcRecon = _ctx.Reconciles;
        }
        _pcFrames++;
        var ms = delta * 1000;
        if (ms > _pcFrameMax) _pcFrameMax = ms;
        if (ms > 33.4) _pcSlow33++;
        var now = Time.GetTicksMsec() / 1000.0;
        if (now < _pcNext) return;
        _pcNext = now + 1;
        var rtt = _peer()?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0;
        _pcLog.WriteLine(string.Join(",", now.ToString("F2", CultureInfo.InvariantCulture), _dec.LastTick, _dec.TicksStarted - _pcTS, _dec.TicksCompleted - _pcTC, _ctx.Reconciles - _pcRecon,
            _pcMaxGap.ToString("F0", CultureInfo.InvariantCulture), rtt, _pcFrames, _pcFrameMax.ToString("F1", CultureInfo.InvariantCulture), _pcSlow33, _ctx.Remotes.Count,
            (_pcErrN == 0 ? 0 : _pcErrSum / _pcErrN).ToString("F4", CultureInfo.InvariantCulture), _pcErrMax.ToString("F4", CultureInfo.InvariantCulture),
            (_pcErrN == 0 ? 0 : _pcDx / _pcErrN).ToString("F4", CultureInfo.InvariantCulture), (_pcErrN == 0 ? 0 : _pcDy / _pcErrN).ToString("F4", CultureInfo.InvariantCulture), (_pcErrN == 0 ? 0 : _pcDz / _pcErrN).ToString("F4", CultureInfo.InvariantCulture), _pcErrN,
            (_ctx.Me?.State.Position.X ?? 0).ToString("F2", CultureInfo.InvariantCulture), (_ctx.Me?.State.Position.Y ?? 0).ToString("F2", CultureInfo.InvariantCulture), (_ctx.Me?.State.Position.Z ?? 0).ToString("F2", CultureInfo.InvariantCulture)));
        _pcErrSum = _pcErrMax = _pcDx = _pcDy = _pcDz = 0; _pcErrN = 0;
        _pcTS = _dec.TicksStarted; _pcTC = _dec.TicksCompleted; _pcRecon = _ctx.Reconciles; _pcMaxGap = 0; _pcFrames = 0; _pcFrameMax = 0; _pcSlow33 = 0;
    }

    StreamWriter? _pcEv;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (_dir.Length == 0 || kind is not (EventKind.Shot or EventKind.Hit or EventKind.Kill)) return;
        if (_pcEv == null) { Directory.CreateDirectory(_dir); _pcEv = new StreamWriter(Path.Combine(_dir, "events-" + _ctx.PlayerName + ".csv")) { AutoFlush = true }; }
        _pcEv.WriteLine((DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10 + "," + (int)kind + "," + Convert.ToHexString(payload));
    }
}
