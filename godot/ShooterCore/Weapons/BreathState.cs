namespace Suchohrad.ShooterCore;

/// <summary>
/// Sniper breath hold (shooter v2 #113), shared by server and client. While scoped, holding the breath key (the sprint
/// binding, Shift by default: aiming already cancels sprint, so the bit carries no movement meaning then) steadies the
/// aim for the breath budget <c>breath_hold_seconds</c> (scaled by Steady Lung's <c>hold_breath_time</c>). The budget is a
/// meter: it drains while holding and refills while not. When it runs out the breath is forced out: heavy sway for
/// <c>breath_recover_seconds</c>, during which the breath cannot be held. Releasing early costs nothing.
///
/// Sway is the aim error in degrees, <c>sway_amount</c> scaled by the phase (steady / ready / exhaled). The server adds one
/// deterministic error per hitscan shot (seeded like recoil, so the same inputs give the same numbers on both sides); the
/// client shows a continuous wobble of the same amplitude (<see cref="VisualSway"/>), which is cosmetic only.
/// Godot-free: time is passed in, seconds.
/// </summary>
public sealed class BreathState
{
    /// <summary>Sway multiplier while the breath is held (steady).</summary>
    public const float SteadyFactor = 0.3f;
    /// <summary>Sway multiplier right after a forced exhale (heavy sway).</summary>
    public const float ExhaledFactor = 2.5f;

    public enum BreathPhase { Ready, Holding, Exhaled }

    readonly ulong _seed;
    double _last = double.NaN;
    int _shot;
    /// <summary>Set by a forced exhale: the breath key must be released before a new hold, so holding through the recovery does not re-engage.</summary>
    bool _needsRelease;

    public BreathState(ulong seed) { _seed = seed; }

    public BreathPhase Phase { get; private set; }
    /// <summary>Breath left as a fraction of the (perk-scaled) budget, 0..1. Meaningless when the weapon has no breath hold.</summary>
    public float Meter { get; private set; } = 1f;
    /// <summary>Seconds of forced-exhale recovery left (only while <see cref="Phase"/> is Exhaled).</summary>
    public float RecoverRemaining { get; private set; }

    public void Reset()
    {
        Phase = BreathPhase.Ready; Meter = 1f; RecoverRemaining = 0; _last = double.NaN; _shot = 0; _needsRelease = false;
    }

    /// <summary>
    /// Advances the breath to <paramref name="now"/>. <paramref name="scoped"/>: the weapon is scoped and aim is held.
    /// <paramref name="breathKey"/>: the breath key is down this tick. <paramref name="holdMultiplier"/>: Steady Lung's
    /// <c>hold_breath_time</c> (1 = none).
    /// </summary>
    public void Tick(double now, bool scoped, bool breathKey, WeaponDef weapon, float holdMultiplier)
    {
        var dt = double.IsNaN(_last) ? 0 : Math.Max(0, now - _last);
        _last = now;
        if (!breathKey) _needsRelease = false;
        var budget = BudgetSeconds(weapon, holdMultiplier);
        if (budget <= 0 || !scoped)
        {
            // no breath to hold (not a scope weapon) or not scoped: nothing is held and the meter refills
            Phase = BreathPhase.Ready;
            if (budget > 0) Meter = Math.Min(1f, Meter + (float)(dt / budget));
            RecoverRemaining = 0;
            _needsRelease = false;
            return;
        }

        if (Phase == BreathPhase.Exhaled)
        {
            RecoverRemaining = (float)Math.Max(0, RecoverRemaining - dt);
            if (RecoverRemaining <= 0) Phase = BreathPhase.Ready;
            return;
        }

        if (breathKey && Meter > 0 && !_needsRelease)
        {
            Phase = BreathPhase.Holding;
            Meter = (float)Math.Max(0, Meter - dt / budget);
            if (Meter <= 0)
            {
                Meter = 0;
                Phase = BreathPhase.Exhaled;
                RecoverRemaining = weapon.BreathRecoverSeconds;
                _needsRelease = true;
                if (RecoverRemaining <= 0) Phase = BreathPhase.Ready;
            }
            return;
        }

        Phase = BreathPhase.Ready;
        Meter = Math.Min(1f, Meter + (float)(dt / budget));
    }

    /// <summary>Breath budget in seconds for this weapon and perk (0 = the weapon has no breath hold).</summary>
    public static float BudgetSeconds(WeaponDef weapon, float holdMultiplier) => weapon.BreathHoldSeconds * holdMultiplier;

    /// <summary>Aim error amplitude in degrees for the current phase: <c>sway_amount</c> times the phase factor (0 = no sway).</summary>
    public float SwayDegrees(WeaponDef weapon) => weapon.SwayAmount * Phase switch
    {
        BreathPhase.Holding => SteadyFactor,
        BreathPhase.Exhaled => ExhaledFactor,
        _ => 1f,
    };

    /// <summary>
    /// The server-known aim error of the next hitscan shot, in radians (pitch positive up, yaw positive left, the
    /// <see cref="RecoilKick"/> convention). Each call is one shot: the same seed and shot count give the same error.
    /// </summary>
    public RecoilKick AimError(float swayDegrees)
    {
        var amp = swayDegrees * MathF.PI / 180f;
        var n = _shot++;
        return new RecoilKick(amp * RecoilState.Unit(_seed, 2 * n), amp * RecoilState.Unit(_seed, 2 * n + 1));
    }

    /// <summary>
    /// Cosmetic client wobble in radians (pitch, yaw) for the given sway amplitude. Two out-of-phase sines, so it looks
    /// like breathing rather than a metronome. Not used by the simulation.
    /// </summary>
    public static System.Numerics.Vector2 VisualSway(double now, float swayDegrees)
    {
        var amp = swayDegrees * MathF.PI / 180f * 0.5f;
        var t = (float)now;
        var pitch = MathF.Sin(t * 1.7f) * 0.6f + MathF.Sin(t * 3.1f + 1.3f) * 0.4f;
        var yaw = MathF.Sin(t * 1.1f + 0.7f) * 0.6f + MathF.Sin(t * 2.3f) * 0.4f;
        return new System.Numerics.Vector2(amp * pitch, amp * yaw);
    }
}
