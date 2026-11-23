using Godot;
using Drycastle.ShooterCore.Corpses;

namespace Drycastle.Shooter;

/// <summary>
/// The motion of a thrown body (the ragdoll) on the client: the whole body spins about its middle while it flies, then falls flat onto its
/// back or front where it lands, and its limbs flail meanwhile (<see cref="RagdollFlail"/>, driven by <see cref="Amount"/>). The position
/// comes from the server; this only turns the body. Shared by corpses and target dummies.
/// </summary>
public sealed class RagdollTumble
{
    /// <summary>Spin in flight (rad/s), settle speed on landing (rad/s), half the body's height (m), and how loose the limbs hang once landed.</summary>
    const float SpinRate = 7f, SettleRate = 9f, HalfBody = 0.9f, LyingHeight = 0.2f, RestAmount = 0.35f;
    const float Quarter = Mathf.Pi / 2f;

    public float Spin, Amount;
    public Vector3 Axis = Vector3.Right;
    public bool Thrown;
    /// <summary>A body that lands alive turns upright and goes slack (a player the Bigfoot threw) instead of lying flat (a corpse, a dummy).</summary>
    public bool StandUp;
    Vector3 _last;
    CorpseCollapse? _collapse;
    /// <summary>The ground's up under the body (the slope it lies on): a collapsed body leans onto it as it goes flat.</summary>
    public Vector3 GroundUp = Vector3.Up;
    bool _seen, _wasFlying;

    /// <summary>The body has landed and finished turning: its limbs can stop moving.</summary>
    public bool AtRest { get; private set; }
    /// <summary>Height of the turning point above the feet: the body's middle upright, just above the ground when lying (so a lying body is on the ground, not above it).</summary>
    public float PivotHeight => LyingHeight + (HalfBody - LyingHeight) * Mathf.Abs(Mathf.Cos(Spin));
    public static float Half => HalfBody;

    public Basis BasisFor(float yaw)
    {
        var b = new Basis(Axis, Spin) * new Basis(Vector3.Up, yaw);
        if (_collapse == null || GroundUp.IsEqualApprox(Vector3.Up)) return b;
        var lean = Mathf.Clamp(Spin / CorpseCollapse.Lying, 0f, 1f); // standing stays upright, lying follows the slope
        return new Basis(new Quaternion(Vector3.Up, GroundUp).Normalized().Slerp(Quaternion.Identity, 1f - lean)) * b;
    }

    /// <summary>A killed body that was not thrown topples toward <paramref name="direction"/> (horizontal, the way the hit pushed it) with <paramref name="strength"/> 0..1 (<see cref="CorpseCollapse"/>).</summary>
    public void BeginCollapse(Vector3 direction, float strength)
    {
        var h = new Vector3(direction.X, 0, direction.Z);
        if (h.LengthSquared() < 1e-4f) h = Vector3.Back;
        Axis = Vector3.Up.Cross(h.Normalized()).Normalized();
        _collapse = new CorpseCollapse(strength);
        Thrown = true; AtRest = false; Spin = 0; Amount = 0;
    }

    public bool Collapsing => _collapse != null;

    /// <summary>Back to standing (a dummy that got up).</summary>
    public void Reset()
    {
        _collapse = null; GroundUp = Vector3.Up;
        Spin = 0; Amount = 0; Thrown = false; StandUp = false; AtRest = false; _wasFlying = false; _seen = false;
    }

    /// <param name="at">The body's feet this frame.</param>
    /// <param name="flying">It is in the air (a corpse's flight flag, a dead dummy moving fast).</param>
    public void Update(Vector3 at, bool flying, float dt)
    {
        var v = _seen && dt > 0 ? (at - _last) / dt : Vector3.Zero;
        _last = at; _seen = true;
        if (_collapse != null)
        {
            if (!flying)
            {
                _collapse.Step(dt);
                Spin = _collapse.Spin; Amount = _collapse.Amount; AtRest = _collapse.AtRest;
                return;
            }
            _collapse = null; // a blast or a car threw it while it fell: the flight takes over from the turn it has
        }
        if (flying)
        {
            Thrown = true; AtRest = false;
            var h = new Vector3(v.X, 0, v.Z);
            if (h.LengthSquared() > 1e-4f) Axis = Vector3.Up.Cross(h.Normalized()).Normalized(); // pitches the body along its flight
            Spin += SpinRate * dt;
            Amount = Mathf.MoveToward(Amount, 1f, dt * 6f);
        }
        else if (Thrown)
        {
            if (_wasFlying) Spin = Mathf.PosMod(Spin, Mathf.Tau); // landed: turn the shortest way to lying
            var rest = StandUp ? 0f : RestAmount;
            var target = StandUp ? Mathf.Round(Spin / Mathf.Tau) * Mathf.Tau // upright again, the nearest turn
                : Mathf.Round((Spin - Quarter) / Mathf.Pi) * Mathf.Pi + Quarter; // flat on the back or the front, whichever is nearer
            Spin = Mathf.MoveToward(Spin, target, SettleRate * dt);
            Amount = Mathf.MoveToward(Amount, rest, dt * (StandUp ? 4f : 2f));
            AtRest = Mathf.IsEqualApprox(Spin, target) && Mathf.IsEqualApprox(Amount, rest);
        }
        _wasFlying = flying;
    }
}
