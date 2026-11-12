using System.Globalization;
using Godot;
using FileAccess = Godot.FileAccess;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Always-on connection and performance log for real play. One CSV line a second (<c>user://net-telemetry/session-*.csv</c>, the newest few kept),
/// so "it lagged for an hour" can be told apart afterwards: the machine (frame time), the network (RTT, loss, snapshot gaps, skipped ticks) or the
/// prediction (corrections and how far the server moved the player or the helicopter). Cheap: counters in the frame and snapshot paths, a flush a second.
/// </summary>
public partial class NetClient
{
    const string TelemetryDir = "user://net-telemetry";
    const int TelemetryKeep = 10;

    FileAccess? _tlFile;
    double _tlNextAt, _tlStart;
    // per-second window
    int _tlFrames, _tlSlow50, _tlSlow100, _tlSnaps, _tlTickSkips, _tlRecon0, _tlFlight0, _tlFlightCorr;
    double _tlFrameMax, _tlGapMax, _tlLastArr, _tlSnapGapSum;
    uint _tlLastTick;
    float _tlErrMax, _tlFlightMax;

    /// <summary>Every frame: frame-time stats, and once a second the line is written.</summary>
    void TelemetryFrame(double delta)
    {
        if (Bot || _done || !_welcomed) return;
        if (_tlFile == null && !TelemetryOpen()) return;
        _tlFrames++;
        var ms = delta * 1000;
        if (ms > _tlFrameMax) _tlFrameMax = ms;
        if (ms > 50) _tlSlow50++;
        if (ms > 100) _tlSlow100++;
        var now = Time.GetTicksMsec() / 1000.0;
        if (now < _tlNextAt) return;
        _tlNextAt = now + 1;
        TelemetryWrite(now);
    }

    /// <summary>A snapshot arrived: gap since the previous one and any ticks the server skipped or the network lost.</summary>
    void TelemetrySnapshot(uint tick)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (_tlLastArr > 0) _tlGapMax = System.Math.Max(_tlGapMax, (now - _tlLastArr) * 1000);
        if (_tlLastTick != 0 && tick > _tlLastTick + 1) _tlTickSkips += (int)(tick - _tlLastTick - 1);
        _tlLastArr = now; _tlLastTick = tick; _tlSnaps++;
    }

    /// <summary>The server's position of the local player against what was predicted for that input.</summary>
    void TelemetryPredictionError(float err) { if (err > _tlErrMax) _tlErrMax = err; }

    /// <summary>The pilot's prediction was overruled by the server, by this many metres.</summary>
    void TelemetryFlightCorrection(float metres) { _tlFlightCorr++; if (metres > _tlFlightMax) _tlFlightMax = metres; }

    bool TelemetryOpen()
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(TelemetryDir);
            var dir = DirAccess.Open(TelemetryDir);
            if (dir != null)
            {
                var old = new System.Collections.Generic.List<string>(dir.GetFiles());
                old.RemoveAll(f => !f.StartsWith("session-"));
                old.Sort(System.StringComparer.Ordinal);
                for (var i = 0; i < old.Count - (TelemetryKeep - 1); i++) dir.Remove(old[i]);
            }
            var name = $"session-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv";
            _tlFile = FileAccess.Open($"{TelemetryDir}/{name}", FileAccess.ModeFlags.Write);
            if (_tlFile == null) { _tlNextAt = double.MaxValue; return false; }
            _tlStart = Time.GetTicksMsec() / 1000.0; _tlNextAt = _tlStart + 1;
            _tlRecon0 = _recon;
            _tlFile.StoreLine($"# drycastle net telemetry; server {Host}:{Port}; player {PlayerName}; os {OS.GetName()}; gpu {RenderingServer.GetVideoAdapterName()}; cpu {OS.GetProcessorName()}; started {System.DateTime.Now:O}");
            _tlFile.StoreLine("t_s,fps,frame_max_ms,frames_over_50ms,frames_over_100ms,snaps,snap_gap_max_ms,tick_skips,rtt_ms,rtt_var_ms,loss_pct,recon,pred_err_max_m,flight_corr,flight_corr_max_m,driving");
            _tlFile.Flush();
            GD.Print($"client {PlayerName}: net telemetry -> {ProjectSettings.GlobalizePath($"{TelemetryDir}/{name}")}");
            return true;
        }
        catch (System.Exception e) { GD.PushWarning($"net telemetry off: {e.Message}"); _tlFile = null; _tlNextAt = double.MaxValue; return false; }
    }

    void TelemetryWrite(double now)
    {
        var rtt = _peer?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) ?? 0;
        var rttVar = _peer?.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTimeVariance) ?? 0;
        var loss = (_peer?.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) ?? 0) / 65536.0 * 100; // ENet's scale is 1 << 16
        var recon = _recon - _tlRecon0; _tlRecon0 = _recon;
        var inv = CultureInfo.InvariantCulture;
        _tlFile!.StoreLine(string.Format(inv, "{0:F0},{1:F0},{2:F1},{3},{4},{5},{6:F0},{7},{8:F0},{9:F0},{10:F2},{11},{12:F3},{13},{14:F2},{15}",
            now - _tlStart, Engine.GetFramesPerSecond(), _tlFrameMax, _tlSlow50, _tlSlow100, _tlSnaps, _tlGapMax, _tlTickSkips, rtt, rttVar, loss, recon, _tlErrMax, _tlFlightCorr, _tlFlightMax, _driving ? 1 : 0));
        _tlFile.Flush();
        _tlFrames = _tlSlow50 = _tlSlow100 = _tlSnaps = _tlTickSkips = _tlFlightCorr = 0;
        _tlFrameMax = _tlGapMax = 0; _tlErrMax = _tlFlightMax = 0;
    }

    void TelemetryClose() { _tlFile?.Close(); _tlFile = null; }
}
