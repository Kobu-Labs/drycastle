namespace Drycastle.ShooterCore;

/// <summary>Where the local player's death screen is: the beat that drops the camera and spatters the blood, then the hold until the respawn.</summary>
public enum DeathPhase
{
    /// <summary>From the death until <see cref="DeathPlan.FallSeconds"/> ran out: the camera drops to the ground, the blood grows, the scrim waits.</summary>
    Falling,
    /// <summary>Held until the spawn: the scrim fades in and carries the "eliminated by" text and the countdown.</summary>
    Lying,
}

/// <summary>What the death screen does to the view (#283).</summary>
public enum DeathStyle
{
    /// <summary>On foot: the first-person camera falls and rolls to the ground; blood, then the scrim.</summary>
    Fall,
    /// <summary>The camera is not the body's (a seat, a turret, the missile, the Bigfoot's hand, under water) or motion is off: blood and the scrim, the view stays where it is.</summary>
    Blood,
    /// <summary>A plain fade to the scrim: no blood, no fall (a drowning).</summary>
    Fade,
}

/// <summary>How one death is shown: chosen once at the death by <see cref="DeathPresentation.Plan"/>.</summary>
/// <param name="Style">What moves.</param>
/// <param name="FallSeconds">Length of <see cref="DeathPhase.Falling"/> (0: straight to Lying).</param>
/// <param name="RollRadians">Signed roll the camera ends on (0 unless <see cref="DeathStyle.Fall"/>).</param>
/// <param name="Blood">Blood overlay intensity reached at the end of the fall, 0..1 (0: none).</param>
/// <param name="ScrimFadeSeconds">How long the respawn scrim takes to fade in once Lying.</param>
public readonly record struct DeathPlan(DeathStyle Style, float FallSeconds, float RollRadians, float Blood, float ScrimFadeSeconds);

/// <summary>
/// The local player's death presentation (#283): the run-time half is a two-phase <see cref="StateMachine{TState}"/> (Falling, then Lying) the client ticks per
/// frame, the pure half decides how a death looks (<see cref="Plan"/>) and what each phase means for the camera, the blood and the scrim. Godot-free.
/// A spawn ends it from any phase (<see cref="End"/>); a new death starts it over (<see cref="Begin"/>), neither is a transition of the machine.
/// </summary>
public sealed class DeathPresentation
{
    public static readonly StateMachineSpec<DeathPhase> Spec = StateMachineSpec<DeathPhase>.Define("Player.DeathView", "The local player's death screen: the camera falls and the blood spatters, then it lies there under the respawn scrim until the spawn.", DeathPhase.Falling)
        .Allow(DeathPhase.Falling, DeathPhase.Lying)
        .Note(DeathPhase.Falling, "Clock: the fall length (a share of the respawn delay). Camera drops and rolls, blood grows, scrim not yet shown. Zero seconds for a plain fade.")
        .Note(DeathPhase.Lying, "Held until the spawn ends it. Clock (elapsed): the scrim fade-in with the \"eliminated by\" text and the countdown.")
        .Build();

    /// <summary>Longest and shortest fall, seconds, and its share of the respawn delay; the roll's range in degrees.</summary>
    public const float MaxFallSeconds = 0.8f, MinFallSeconds = 0.25f, FallShareOfDelay = 0.3f;
    public const float MinRollDegrees = 70f, MaxRollDegrees = 90f;
    /// <summary>A beat before the scrim for a death that does not move the camera, so the blood lands first.</summary>
    public const float BloodBeatSeconds = 0.3f;
    public const float ScrimFadeSeconds = 0.25f, ScrimFadeReducedSeconds = 0.12f;
    /// <summary>Blood intensity of a death; reduce-motion softens it.</summary>
    public const float FullBlood = 1f, ReducedBlood = 0.5f;
    /// <summary>The camera ends this far above the ground, metres.</summary>
    public const float GroundClearanceM = 0.2f;

    readonly StateMachine<DeathPhase> _fsm = new(Spec);
    DeathPlan _plan;

    /// <summary>A death is being shown (from <see cref="Begin"/> until <see cref="End"/>).</summary>
    public bool Active { get; private set; }
    public DeathPlan Current => _plan;
    public DeathPhase Phase => _fsm.Current;
    public bool Falling => Active && _fsm.Is(DeathPhase.Falling);
    public bool Lying => Active && _fsm.Is(DeathPhase.Lying);
    /// <summary>The camera is the presentation's to move (the style is <see cref="DeathStyle.Fall"/>).</summary>
    public bool OwnsCamera => Active && _plan.Style == DeathStyle.Fall;

    /// <summary>
    /// How this death is shown. <paramref name="before"/> is the mode the player was in until the death. On foot, getting up from a pin and pinned all
    /// fall (a pinned player already lies low, the camera continues from there); every other camera (seat, turret, missile, the Bigfoot's hand, the Mass) and
    /// <paramref name="eyeUnderwater"/> get blood and the scrim only; <paramref name="drowned"/> is a plain fade. Reduce-motion never moves the camera.
    /// </summary>
    public static DeathPlan Plan(ClientMode before, bool eyeUnderwater, bool drowned, bool reduceMotion, float respawnDelay, int seed)
    {
        var blood = reduceMotion ? ReducedBlood : FullBlood;
        var scrim = reduceMotion ? ScrimFadeReducedSeconds : ScrimFadeSeconds;
        if (drowned) return new DeathPlan(DeathStyle.Fade, 0, 0, 0, scrim);
        var onFoot = before is ClientMode.OnFoot or ClientMode.Rising or ClientMode.Pinned or ClientMode.Dead;
        if (!onFoot || eyeUnderwater || reduceMotion) return new DeathPlan(DeathStyle.Blood, BloodBeatSeconds, 0, blood, scrim);
        var fall = Math.Clamp(respawnDelay * FallShareOfDelay, MinFallSeconds, MaxFallSeconds);
        return new DeathPlan(DeathStyle.Fall, fall, RollFor(seed), blood, scrim);
    }

    /// <summary>The roll of a fall: 70-90 degrees, to the left or the right by the seed.</summary>
    public static float RollFor(int seed)
    {
        var s = (uint)seed * 2654435761u;
        var degrees = MinRollDegrees + (MaxRollDegrees - MinRollDegrees) * ((s >> 8) & 0xFF) / 255f;
        return MathF.PI / 180f * degrees * (((s >> 20) & 1) == 0 ? 1f : -1f);
    }

    /// <summary>Starts showing a death (from any state, including one still being shown): Falling, with the plan's clock.</summary>
    public void Begin(DeathPlan plan)
    {
        _plan = plan; Active = true;
        _fsm.Reset(DeathPhase.Falling);
        _fsm.SetLeft(plan.FallSeconds);
    }

    /// <summary>The spawn: stops showing the death, whatever the phase.</summary>
    public void End() { Active = false; _fsm.Reset(DeathPhase.Falling); }

    /// <summary>Advances the clock; moves Falling to Lying when the fall has run out (at once for a zero fall).</summary>
    public void Tick(float dt)
    {
        if (!Active) return;
        var done = _fsm.Tick(dt);
        if (done && _fsm.Is(DeathPhase.Falling)) _fsm.Enter(DeathPhase.Lying);
    }

    /// <summary>How far the fall is, 0..1 eased in (gravity: slow, then fast). 0 when nothing is shown, 1 once Lying.</summary>
    public float Drop
    {
        get
        {
            if (!Active) return 0;
            if (_fsm.Is(DeathPhase.Lying) || _plan.FallSeconds <= 0) return 1;
            var t = Math.Clamp(_fsm.Elapsed / _plan.FallSeconds, 0f, 1f);
            return t * t;
        }
    }

    /// <summary>The blood overlay intensity now: grows linearly over the fall (so it lands quickly), then holds.</summary>
    public float BloodNow
    {
        get
        {
            if (!Active || _plan.Blood <= 0) return 0;
            if (_fsm.Is(DeathPhase.Lying) || _plan.FallSeconds <= 0) return _plan.Blood;
            return _plan.Blood * Math.Clamp(_fsm.Elapsed / _plan.FallSeconds, 0f, 1f);
        }
    }

    /// <summary>The respawn scrim's opacity now, 0..1: nothing while Falling, a fade-in once Lying.</summary>
    public float ScrimNow
    {
        get
        {
            if (!Active || _fsm.Is(DeathPhase.Falling)) return 0;
            return _plan.ScrimFadeSeconds <= 0 ? 1 : Math.Clamp(_fsm.Elapsed / _plan.ScrimFadeSeconds, 0f, 1f);
        }
    }
}
