using Godot;

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
    Vector3 _last;
    bool _seen, _wasFlying;

    /// <summary>The body has landed and finished turning: its limbs can stop moving.</summary>
    public bool AtRest { get; private set; }
    /// <summary>Height of the turning point above the feet: the body's middle upright, just above the ground when lying (so a lying body is on the ground, not above it).</summary>
    public float PivotHeight => LyingHeight + (HalfBody - LyingHeight) * Mathf.Abs(Mathf.Cos(Spin));
    public static float Half => HalfBody;

    public Basis BasisFor(float yaw) => new Basis(Axis, Spin) * new Basis(Vector3.Up, yaw);

    /// <summary>Back to standing (a dummy that got up).</summary>
    public void Reset()
    {
        Spin = 0; Amount = 0; Thrown = false; AtRest = false; _wasFlying = false; _seen = false;
    }

    /// <param name="at">The body's feet this frame.</param>
    /// <param name="flying">It is in the air (a corpse's flight flag, a dead dummy moving fast).</param>
    public void Update(Vector3 at, bool flying, float dt)
    {
        var v = _seen && dt > 0 ? (at - _last) / dt : Vector3.Zero;
        _last = at; _seen = true;
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
            var target = Mathf.Round((Spin - Quarter) / Mathf.Pi) * Mathf.Pi + Quarter; // flat on the back or the front, whichever is nearer
            Spin = Mathf.MoveToward(Spin, target, SettleRate * dt);
            Amount = Mathf.MoveToward(Amount, RestAmount, dt * 2f);
            AtRest = Mathf.IsEqualApprox(Spin, target) && Mathf.IsEqualApprox(Amount, RestAmount);
        }
        _wasFlying = flying;
    }
}
