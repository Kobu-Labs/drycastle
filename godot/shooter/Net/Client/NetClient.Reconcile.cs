using Godot;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The own body's reconciliation: the server's position at the acked input wins, the unacked inputs are replayed in the next physics tick, and the eye glides over the correction.</summary>
public partial class NetClient
{
    void OnSelf(EntityRecord rec, ushort ack)
    {
        var sp = rec.Position;
        if (!OwnSpawned)
        {
            _link.Enter(LinkPhase.Spawned);
            Ctx.Me!.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y });
            _prediction.ClearViewFix();
            return;
        }
        Ctx.Me!.Health = rec.Health;
        Ctx.View.MyState = rec.State;
        var inCar = rec.State == EntityState.InVehicle;
        if (inCar != Ctx.Driving)
        {
            // entering or leaving a car: no prediction while driving; on exit take the server's position (the free side) as is
            Ctx.Driving = inCar; _prediction.Drop(); Ctx.Me.InVehicle = inCar;
            if (!inCar) { _prediction.Reset(); Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); Ctx.Me.EndChase(_carYaw); Ctx.Cars.Seats = null; }
            return;
        }
        if (Mode.HeldOnFoot) { _prediction.Reset(); Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); return; } // in the Bigfoot's hand: the body is where the server puts it, nothing is predicted (#264)
        if (Mode.ServerHoldsBody) { _prediction.Drop(); Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = true, FallPeakY = sp.Y }); return; } // seated at the Mass or pinned by the demon: the server holds the body, take its place as is
        if (Mode.SkipsReconcile) { _prediction.ClearInputs(); return; }
        _bot?.OnOwnState(rec);
        if (_prediction.PredictedAt(ack) is not { } predicted) return;
        var err = (predicted.Position - sp).Length();
        _maxErr = MathF.Max(_maxErr, err);
        _perf.PredictionError(err, predicted.Position - sp);
        LastNearRemote = NearRemotePlayer(sp);
        LastVertical = MathF.Abs(predicted.Position.Y - sp.Y) > 0.5f * err; // the error is mostly up or down (steps, slopes, jumps, landings)
        if (err > ReconcileEpsilon) Ctx.Rub.AddOnFoot(err, LastNearRemote, LastVertical);
        _bot?.OnPredictionError(err);
        _session.PredictionError(err);
        Report.PredictionError(err);
        _prediction.Acknowledge(ack);
        if (err <= ReconcileEpsilon)
        {
            // a newer ack agrees with the prediction: a reconcile still waiting for the next physics tick (from an older snapshot) is stale, and replaying it
            // would restart from that older server position while the inputs after this ack are already gone (code review finding)
            _prediction.CancelReplay();
            return;
        }
        Ctx.Reconciles++;
        // server position wins; our own velocity/floor state for that tick is the best available guess for the rest
        var s = predicted;
        s.Position = sp;
        if (err > SnapDistance) { s.Velocity = default; _prediction.ClearInputs(); }
        // C-02: the replay is run by OwnPrediction.Replay in the next physics tick, not here. This runs in _Process, where
        // Godot's MoveAndSlide uses the frame delta (60 Hz) instead of the physics delta (30 Hz): every replayed step
        // moved half a tick, and the next comparison was off by exactly that (8.9 cm, always ahead).
        _prediction.ReplayFrom(s);
    }

    /// <summary>Set by the last acked comparison: whether another living player stood within 1 m of the body then (I-rubber-banding telemetry).</summary>
    bool LastNearRemote, LastVertical;

    bool NearRemotePlayer(NVec3 at)
    {
        foreach (var r in Ctx.Remotes.Values)
        {
            if (r.State == EntityState.Dead || r.Node == null) continue;
            var d = new Vector2(r.LastPos.X - at.X, r.LastPos.Z - at.Z);
            if (d.LengthSquared() < 1f) return true;
        }
        return false;
    }

    /// <summary>Fades the drawn part of the last on-foot correction, once per frame, at the same rate as the vehicles' corrections.</summary>
    void FadeOwnCorrection(double delta)
    {
        if (Ctx.Me == null) return;
        _prediction.FadeCorrection(delta);
        Ctx.Me.ViewOffset = _prediction.ViewFix + _heldSwing; // in the Bigfoot's hand the eye swings with the hand (#264)
        Ctx.Me.ViewRoll = _heldRoll + High.Roll;
    }
}
