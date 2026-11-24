using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The own body's reconciliation: the server's position at the acked input wins, the unacked inputs are replayed in the next physics tick (<see cref="OwnPrediction"/>), and the
/// eye glides over the correction. Who holds the body (a vehicle, the Mass, the demon, the Bigfoot) decides what the server's record means.
/// </summary>
internal sealed class OwnReconcile
{
    /// <summary>An error under this (metres) is none.</summary>
    const float Epsilon = 0.03f;

    readonly ClientContext _ctx;
    readonly OwnPrediction _prediction;
    readonly StateMachine<LinkPhase> _link;
    readonly PerfLog _perf;
    readonly SessionLog _session;
    readonly ReportClient _report;
    readonly ClientBot? _bot;

    public OwnReconcile(ClientContext ctx, OwnPrediction prediction, StateMachine<LinkPhase> link, PerfLog perf, SessionLog session, ReportClient report, ClientBot? bot)
    {
        _ctx = ctx; _prediction = prediction; _link = link; _perf = perf; _session = session; _report = report; _bot = bot;
    }

    /// <summary>The largest prediction error seen (metres), for the bots' checks.</summary>
    public float MaxErr { get; private set; }

    public void OnState(EntityRecord rec, ushort ack)
    {
        var sp = rec.Position;
        if (!_link.Is(LinkPhase.Spawned))
        {
            _link.Enter(LinkPhase.Spawned);
            _ctx.Me!.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y });
            _prediction.ClearViewFix();
            return;
        }
        _ctx.Me!.Health = rec.Health;
        _ctx.View.MyState = rec.State;
        var inCar = rec.State == EntityState.InVehicle;
        if (inCar != _ctx.Driving)
        {
            // entering or leaving a car: no prediction while driving; on exit take the server's position (the free side) as is
            _ctx.Driving = inCar; _prediction.Drop(); _ctx.Me.InVehicle = inCar;
            if (!inCar) { _prediction.Reset(); _ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); _ctx.Me.EndChase(_ctx.Cars.DrivenYaw); _ctx.Cars.Seats = null; }
            return;
        }
        if (_ctx.View.Mode.HeldOnFoot) { _prediction.Reset(); _ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); return; } // in the Bigfoot's hand: the body is where the server puts it, nothing is predicted (#264)
        if (_ctx.View.Mode.ServerHoldsBody) { _prediction.Drop(); _ctx.Me.SetState(new MoveState { Position = sp, OnFloor = true, FallPeakY = sp.Y }); return; } // seated at the Mass or pinned by the demon: the server holds the body, take its place as is
        if (_ctx.View.Mode.SkipsReconcile) { _prediction.ClearInputs(); return; }
        _bot?.OnOwnState(rec);
        if (_prediction.PredictedAt(ack) is not { } predicted) return;
        var err = (predicted.Position - sp).Length();
        MaxErr = MathF.Max(MaxErr, err);
        _perf.PredictionError(err, predicted.Position - sp);
        var nearRemote = NearRemotePlayer(sp);
        var vertical = MathF.Abs(predicted.Position.Y - sp.Y) > 0.5f * err; // the error is mostly up or down (steps, slopes, jumps, landings)
        if (err > Epsilon) _ctx.Rub.AddOnFoot(err, nearRemote, vertical);
        _bot?.OnPredictionError(err);
        _session.PredictionError(err);
        _report.PredictionError(err);
        _prediction.Acknowledge(ack);
        if (err <= Epsilon)
        {
            // a newer ack agrees with the prediction: a reconcile still waiting for the next physics tick (from an older snapshot) is stale, and replaying it
            // would restart from that older server position while the inputs after this ack are already gone (code review finding)
            _prediction.CancelReplay();
            return;
        }
        _ctx.Reconciles++;
        // server position wins; our own velocity/floor state for that tick is the best available guess for the rest
        var s = predicted;
        s.Position = sp;
        if (err > NetClient.SnapDistance) { s.Velocity = default; _prediction.ClearInputs(); }
        // C-02: the replay is run by OwnPrediction.Replay in the next physics tick, not here. This runs in _Process, where
        // Godot's MoveAndSlide uses the frame delta (60 Hz) instead of the physics delta (30 Hz): every replayed step
        // moved half a tick, and the next comparison was off by exactly that (8.9 cm, always ahead).
        _prediction.ReplayFrom(s);
    }

    bool NearRemotePlayer(NVec3 at)
    {
        foreach (var r in _ctx.Remotes.Values)
        {
            if (r.State == EntityState.Dead || r.Node == null) continue;
            var d = new Vector2(r.LastPos.X - at.X, r.LastPos.Z - at.Z);
            if (d.LengthSquared() < 1f) return true;
        }
        return false;
    }
}
