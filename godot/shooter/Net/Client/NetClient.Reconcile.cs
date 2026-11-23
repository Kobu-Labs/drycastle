using Godot;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
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
            _viewFix = Vector3.Zero;
            return;
        }
        Ctx.Me!.Health = rec.Health;
        _myState = rec.State;
        var inCar = rec.State == EntityState.InVehicle;
        if (inCar != Ctx.Driving)
        {
            // entering or leaving a car: no prediction while driving; on exit take the server's position (the free side) as is
            Ctx.Driving = inCar; _pending.Clear(); _reconcile = null; Ctx.Me.InVehicle = inCar;
            if (!inCar) { _reconcile = null; _viewFix = Vector3.Zero; Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); Ctx.Me.EndChase(_carYaw); Ctx.Cars.Seats = null; }
            return;
        }
        if (Mode.HeldOnFoot) { _pending.Clear(); _reconcile = null; _viewFix = Vector3.Zero; Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); return; } // in the Bigfoot's hand: the body is where the server puts it, nothing is predicted (#264)
        if (Mode.ServerHoldsBody) { _pending.Clear(); _reconcile = null; Ctx.Me.SetState(new MoveState { Position = sp, OnFloor = true, FallPeakY = sp.Y }); return; } // seated at the Mass or pinned by the demon: the server holds the body, take its place as is
        if (Mode.SkipsReconcile) { _pending.Clear(); return; }
        _bot?.OnOwnState(rec);
        var idx = _pending.FindIndex(p => p.Input.Seq == ack);
        if (idx < 0) return;
        var predicted = _pending[idx].After;
        var err = (predicted.Position - sp).Length();
        _maxErr = MathF.Max(_maxErr, err);
        PerfPredErr(err, predicted.Position - sp);
        LastNearRemote = NearRemotePlayer(sp);
        LastVertical = MathF.Abs(predicted.Position.Y - sp.Y) > 0.5f * err; // the error is mostly up or down (steps, slopes, jumps, landings)
        if (err > ReconcileEpsilon) _rub.AddOnFoot(err, LastNearRemote, LastVertical);
        _bot?.OnPredictionError(err);
        TelemetryPredictionError(err);
        ReportPredictionError(err);
        _pending.RemoveRange(0, idx + 1);
        if (err <= ReconcileEpsilon)
        {
            // a newer ack agrees with the prediction: a reconcile still waiting for the next physics tick (from an older snapshot) is stale, and replaying it
            // would restart from that older server position while the inputs after this ack are already gone (code review finding)
            _reconcile = null;
            return;
        }
        _recon++;
        // server position wins; our own velocity/floor state for that tick is the best available guess for the rest
        var s = predicted;
        s.Position = sp;
        if (err > SnapDistance) { s.Velocity = default; _pending.Clear(); }
        // C-02: the replay is run by ReplayReconcile in the next physics tick, not here. This runs in _Process, where
        // Godot's MoveAndSlide uses the frame delta (60 Hz) instead of the physics delta (30 Hz): every replayed step
        // moved half a tick, and the next comparison was off by exactly that (8.9 cm, always ahead).
        _reconcile = s;
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

    /// <summary>The inputs waiting for an ack and a reconcile waiting for the next tick are void (the server took over the body).</summary>
    void DropPrediction() { _pending.Clear(); _reconcile = null; }

    /// <summary>A reconcile from a snapshot, waiting for the next physics tick (see <see cref="OnSelf"/>).</summary>
    MoveState? _reconcile;

    /// <summary>Puts the body at the server's position and replays the unacked inputs, in a physics tick so the steps match the server's.</summary>
    void ReplayReconcile()
    {
        if (_reconcile is not { } s) return;
        _reconcile = null;
        var before = Ctx.Me!.State.Position; // where the eye was drawn: the predicted present
        if (Ctx.Me.IsOnFloor() != s.OnFloor) _rub.FloorFlagMismatch++; // measurement: a replay that would start with the wrong floor flag without SetStateForReplay
        Ctx.Me.SetStateForReplay(s); // the body's floor flag must match the state's before the first replayed step (I-rubber-banding round 2)
        for (var i = 0; i < _pending.Count; i++)
        {
            Ctx.Me.Simulate(_pending[i].Input.ToMove(SpeedFor(_pending[i].Input.Slot)), 1f / Wire.TickRate);
            _pending[i].After = Ctx.Me.State;
        }
        // I-rubber-banding: the state moved by the correction; the eye keeps drawing the old place and glides over (FadeOwnCorrection).
        // A respawn or teleport (beyond SnapDistance) is not a correction to glide over.
        var moved = before - Ctx.Me.State.Position;
        _viewFix = moved.Length() <= SnapDistance ? _viewFix + ToVec(moved) : Vector3.Zero;
    }

    /// <summary>The part of an on-foot correction still to be drawn (metres, world), faded out over a few frames (see <see cref="ReplayReconcile"/>).</summary>
    Vector3 _viewFix;

    static Vector3 ToVec(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Fades the drawn part of the last on-foot correction, once per frame, at the same rate as the vehicles' corrections.</summary>
    void FadeOwnCorrection(double delta)
    {
        if (Ctx.Me == null) return;
        _viewFix *= Mathf.Exp(-CorrectionFade * (float)delta);
        if (_viewFix.LengthSquared() < 1e-6f) _viewFix = Vector3.Zero; // under a millimetre: done
        Ctx.Me.ViewOffset = _viewFix + _heldSwing; // in the Bigfoot's hand the eye swings with the hand (#264)
        Ctx.Me.ViewRoll = _heldRoll + _nauseaRoll;
    }
}
