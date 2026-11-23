using Godot;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The own body's prediction bookkeeping: the inputs waiting for the server's ack (each with the state it left the body in), a reconcile waiting for the next physics tick, and the part of a
/// correction the eye still glides over. The server's position at the acked input wins (C-02: the replay of the unacked inputs runs in the physics tick, with the steps the server runs).
/// The decisions (who holds the body, what the error means) stay with the caller; this keeps the lists and does the replay and the fade.
/// </summary>
internal sealed class OwnPrediction
{
    sealed class Pending { public NetInput Input; public MoveState After; }

    /// <summary>The most inputs kept waiting for an ack.</summary>
    const int MaxPending = 120;

    readonly ClientContext _ctx;
    readonly Func<byte, float> _speedFor;
    readonly List<Pending> _pending = new();
    /// <summary>A reconcile from a snapshot, waiting for the next physics tick.</summary>
    MoveState? _reconcile;

    /// <param name="speedFor">The movement speed multiplier of the weapon in a slot (the server applies the same value).</param>
    public OwnPrediction(ClientContext ctx, Func<byte, float> speedFor) { _ctx = ctx; _speedFor = speedFor; }

    /// <summary>The part of an on-foot correction still to be drawn (metres, world), faded out over a few frames.</summary>
    public Vector3 ViewFix { get; private set; }

    /// <summary>A predicted input waits for its ack, with the body's state after it.</summary>
    public void Add(NetInput input, MoveState after)
    {
        _pending.Add(new Pending { Input = input, After = after });
        if (_pending.Count > MaxPending) _pending.RemoveAt(0);
    }

    /// <summary>The newest <paramref name="count"/> inputs (fewer when fewer wait), oldest first.</summary>
    public NetInput[] Recent(int count)
    {
        var recent = new NetInput[Math.Min(count, _pending.Count)];
        for (var i = 0; i < recent.Length; i++) recent[i] = _pending[_pending.Count - recent.Length + i].Input;
        return recent;
    }

    /// <summary>The body's state after the input the server acked; null when that input does not wait any more.</summary>
    public MoveState? PredictedAt(ushort ack)
    {
        var idx = _pending.FindIndex(p => p.Input.Seq == ack);
        return idx < 0 ? null : _pending[idx].After;
    }

    /// <summary>The inputs up to and including <paramref name="ack"/> are settled.</summary>
    public void Acknowledge(ushort ack) => _pending.RemoveRange(0, _pending.FindIndex(p => p.Input.Seq == ack) + 1);

    /// <summary>The next physics tick sets the body to <paramref name="serverState"/> and replays the inputs still waiting.</summary>
    public void ReplayFrom(MoveState serverState) => _reconcile = serverState;

    /// <summary>A newer ack agrees with the prediction: a reconcile still waiting is stale (it would restart from an older server position).</summary>
    public void CancelReplay() => _reconcile = null;

    /// <summary>The waiting inputs are void.</summary>
    public void ClearInputs() => _pending.Clear();

    public void ClearViewFix() => ViewFix = Vector3.Zero;

    /// <summary>The inputs waiting for an ack and a reconcile waiting for the next tick are void (the server took over the body).</summary>
    public void Drop() { _pending.Clear(); _reconcile = null; }

    /// <summary>The server took over the body and the eye starts afresh: no inputs, no reconcile, no glide.</summary>
    public void Reset() { Drop(); ViewFix = Vector3.Zero; }

    /// <summary>Puts the body at the server's position and replays the unacked inputs, in a physics tick so the steps match the server's.</summary>
    public void Replay()
    {
        if (_reconcile is not { } s) return;
        _reconcile = null;
        var me = _ctx.Me!;
        var before = me.State.Position; // where the eye was drawn: the predicted present
        if (me.IsOnFloor() != s.OnFloor) _ctx.Rub.FloorFlagMismatch++; // measurement: a replay that would start with the wrong floor flag without SetStateForReplay
        me.SetStateForReplay(s); // the body's floor flag must match the state's before the first replayed step (I-rubber-banding round 2)
        for (var i = 0; i < _pending.Count; i++)
        {
            me.Simulate(_pending[i].Input.ToMove(_speedFor(_pending[i].Input.Slot)), 1f / Wire.TickRate);
            _pending[i].After = me.State;
        }
        // I-rubber-banding: the state moved by the correction; the eye keeps drawing the old place and glides over (FadeCorrection).
        // A respawn or teleport (beyond SnapDistance) is not a correction to glide over.
        var moved = before - me.State.Position;
        ViewFix = moved.Length() <= NetClient.SnapDistance ? ViewFix + new Vector3(moved.X, moved.Y, moved.Z) : Vector3.Zero;
    }

    /// <summary>Fades the drawn part of the last on-foot correction, once per frame, at the same rate as the vehicles' corrections.</summary>
    public void FadeCorrection(double delta)
    {
        var fix = ViewFix * Mathf.Exp(-Correction.FadePerSecond * (float)delta);
        ViewFix = fix.LengthSquared() < 1e-6f ? Vector3.Zero : fix; // under a millimetre: done
    }
}
