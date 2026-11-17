namespace Drycastle.ShooterCore;

/// <summary>Where a killed body is in its fall to the ground (ticket #357): turning over, bouncing to rest, lying still.</summary>
public enum CollapsePhase : byte { Falling, Settling, AtRest }

/// <summary>
/// The collapse of a Corpse that was not thrown (a bullet, a knife, a fall): the whole body topples a quarter turn about its feet's side under gravity,
/// starting from the impulse of the killing hit, bounces a little on the ground and lies still. Pure maths, the client turns the model with
/// <see cref="Spin"/> (see RagdollTumble) and loosens the limbs by <see cref="Amount"/>. A body thrown by a blast or a car does not collapse: it flies.
/// </summary>
public sealed class CorpseCollapse
{
    public static readonly StateMachineSpec<CollapsePhase> Spec = StateMachineSpec<CollapsePhase>
        .Define("Corpse.Collapse", "A killed body that was not thrown topples to the ground from the killing hit's impulse, bounces and lies still (client look only; the server's point does not move).", CollapsePhase.Falling)
        .Allow(CollapsePhase.Falling, CollapsePhase.Settling)
        .Allow(CollapsePhase.Settling, CollapsePhase.AtRest)
        .Note(CollapsePhase.Falling, "Turning over about the feet's side, faster each moment (gravity), from a start speed that grows with the hit's impulse. Ends when the body is flat.")
        .Note(CollapsePhase.Settling, "Rebounds off the ground and damps out for SettleSeconds; the limbs go loose.")
        .Note(CollapsePhase.AtRest, "Flat and still, limbs loose. Stays so until the corpse is removed.")
        .Build();

    /// <summary>Lying flat: a quarter turn from standing (rad). Fall acceleration (rad/s²), rebound kept, spring and damping of the settle, its length (s), and the limb looseness while falling and lying.</summary>
    public const float Lying = MathF.PI / 2f, Accel = 24f, Rebound = 0.22f, Spring = 70f, Damping = 10f, SettleSeconds = 0.55f, FallAmount = 0.7f, RestAmount = 0.35f;

    readonly StateMachine<CollapsePhase> _fsm = new(Spec);
    float _rate;

    /// <param name="strength">The hit's push, 0..1 (a pistol round 0.3, a rifle round or a heavy hit 1): the start speed of the fall.</param>
    public CorpseCollapse(float strength = 0.5f) => _rate = 0.6f + 2.4f * Math.Clamp(strength, 0f, 1f);

    public CollapsePhase Phase => _fsm.Current;
    public bool AtRest => _fsm.Is(CollapsePhase.AtRest);
    /// <summary>Turn from standing (0) to lying (<see cref="Lying"/>), rad.</summary>
    public float Spin { get; private set; }
    /// <summary>How loose the limbs are, 0..1.</summary>
    public float Amount { get; private set; }

    public void Step(float dt)
    {
        if (dt <= 0) return;
        _fsm.Tick(dt);
        switch (_fsm.Current)
        {
            case CollapsePhase.Falling:
                _rate += Accel * dt;
                Spin += _rate * dt;
                Amount = MoveToward(Amount, FallAmount, dt * 6f);
                if (Spin >= Lying)
                {
                    Spin = Lying;
                    _rate = _rate > 1.2f ? -_rate * Rebound : 0f;
                    _fsm.Enter(CollapsePhase.Settling, SettleSeconds);
                }
                break;
            case CollapsePhase.Settling:
                _rate += (-Spring * (Spin - Lying) - Damping * _rate) * dt;
                Spin += _rate * dt;
                if (Spin > Lying) { Spin = Lying; _rate = 0f; } // not through the ground
                Amount = MoveToward(Amount, RestAmount, dt * 2f);
                if (_fsm.Expired) { Spin = Lying; Amount = RestAmount; _fsm.Enter(CollapsePhase.AtRest); }
                break;
        }
    }

    static float MoveToward(float v, float to, float d) => MathF.Abs(to - v) <= d ? to : v + MathF.Sign(to - v) * d;
}
