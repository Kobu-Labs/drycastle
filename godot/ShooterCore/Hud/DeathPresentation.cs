namespace Drycastle.ShooterCore;

/// <summary>Where the local player's death screen is: the legs give way, the body goes over, then the hold until the respawn.</summary>
public enum DeathPhase
{
    /// <summary>First beat of a fall on foot: the knees (or the lean) give way, the view drops part of the way. Zero seconds for a death that does not move the camera.</summary>
    Buckling,
    /// <summary>The body goes over (forward, back or to a side) until it hits the ground; the blood grows to its full amount.</summary>
    Falling,
    /// <summary>Held until the spawn: a short impact shake, then the scrim fades in and carries the "eliminated by" text and the countdown.</summary>
    Lying,
}

/// <summary>The way a body goes down: which way the view falls.</summary>
public enum FallKind { Forward, Backward, Left, Right }

/// <summary>What the death screen does to the view (#283).</summary>
public enum DeathStyle
{
    /// <summary>On foot: the first-person camera buckles and falls to the ground (see <see cref="FallKind"/>); blood, then the scrim.</summary>
    Fall,
    /// <summary>The camera is not the body's (a seat, a turret, the missile, the Bigfoot's hand, under water) or motion is off: blood and the scrim, the view stays where it is.</summary>
    Blood,
    /// <summary>A plain fade to the scrim: no blood, no fall (a drowning).</summary>
    Fade,
}

/// <summary>How one death is shown: chosen once at the death by <see cref="DeathPresentation.Plan"/>.</summary>
/// <param name="Style">What moves.</param>
/// <param name="Kind">The way the body goes down (only for <see cref="DeathStyle.Fall"/>).</param>
/// <param name="BuckleSeconds">Length of <see cref="DeathPhase.Buckling"/> (0: skipped).</param>
/// <param name="FallSeconds">Length of <see cref="DeathPhase.Falling"/> (0: straight to Lying).</param>
/// <param name="Blood">Blood overlay intensity reached at the end of the fall, 0..1 (0: none).</param>
/// <param name="ScrimFadeSeconds">How long the respawn scrim takes to fade in once Lying.</param>
public readonly record struct DeathPlan(DeathStyle Style, FallKind Kind, float BuckleSeconds, float FallSeconds, float Blood, float ScrimFadeSeconds);

/// <summary>
/// The view's offset from where the eye was at the death, one instant of a fall. <paramref name="Drop"/> is how far to the ground (0..1 of the eye's height above it);
/// pitch (positive looks up), roll (positive tilts the head to the left) and yaw (positive turns left) are radians about the view's own axes; <paramref name="PushM"/> is metres along
/// the horizontal view direction (negative: backwards); <paramref name="BounceM"/> is the little lift of the impact.
/// </summary>
public readonly record struct DeathPose(float Drop, float Pitch, float Roll, float Yaw, float PushM, float BounceM)
{
    public static readonly DeathPose None = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The local player's death presentation (#283): the run-time half is a three-phase <see cref="StateMachine{TState}"/> (Buckling, Falling, Lying) the client ticks per
/// frame, the pure half decides how a death looks (<see cref="Plan"/>), the camera's pose in each phase (<see cref="Pose"/>) and what each phase means for the blood and the scrim. Godot-free.
/// A spawn ends it from any phase (<see cref="End"/>); a new death starts it over (<see cref="Begin"/>), neither is a transition of the machine.
/// </summary>
public sealed class DeathPresentation
{
    public static readonly StateMachineSpec<DeathPhase> Spec = StateMachineSpec<DeathPhase>.Define("Player.DeathView", "The local player's death screen: the legs give way, the body goes over and the blood spatters, then it lies there under the respawn scrim until the spawn.", DeathPhase.Buckling)
        .Allow(DeathPhase.Buckling, DeathPhase.Falling)
        .Allow(DeathPhase.Falling, DeathPhase.Lying)
        .Note(DeathPhase.Buckling, "Clock: the buckle length. The knees or the lean give way and the view sinks part of the way; a little blood. Zero seconds for a death that does not move the camera.")
        .Note(DeathPhase.Falling, "Clock: the fall length. The body goes over (forward, back, left or right by the plan) and the view hits the ground; the blood grows to full. Zero seconds for a plain fade.")
        .Note(DeathPhase.Lying, "Held until the spawn ends it. Clock (elapsed): the impact shake, then the scrim fade-in with the \"eliminated by\" text and the countdown.")
        .Build();

    /// <summary>The whole fall (buckle and fall together) is this share of the respawn delay, clamped; the buckle is a share of it by kind (a forward fall kneels longer).</summary>
    public const float MinTotalSeconds = 0.9f, MaxTotalSeconds = 1.6f, TotalShareOfDelay = 0.5f;
    public const float BuckleShare = 0.35f, BuckleShareForward = 0.45f;
    /// <summary>A beat before the scrim for a death that does not move the camera, so the blood lands first.</summary>
    public const float BloodBeatSeconds = 0.3f;
    public const float ScrimFadeSeconds = 0.25f, ScrimFadeReducedSeconds = 0.12f;
    /// <summary>Blood intensity of a death (a light spatter at the edges); reduce-motion softens it.</summary>
    public const float FullBlood = 0.35f, ReducedBlood = 0.18f;
    /// <summary>The share of the blood already there at the first moment of the death (the hit), before the body goes over.</summary>
    public const float BloodAtHit = 0.2f;
    /// <summary>The camera ends this far above the ground, metres.</summary>
    public const float GroundClearanceM = 0.2f;
    /// <summary>The longest forward or backward travel of a fall, metres: the client shortens it against a wall.</summary>
    public const float MaxPushM = 0.8f;
    /// <summary>The impact shake lasts about this long once Lying, seconds.</summary>
    public const float ImpactSeconds = 0.5f;

    readonly StateMachine<DeathPhase> _fsm = new(Spec);
    DeathPlan _plan;

    /// <summary>A death is being shown (from <see cref="Begin"/> until <see cref="End"/>).</summary>
    public bool Active { get; private set; }
    public DeathPlan Current => _plan;
    public DeathPhase Phase => _fsm.Current;
    public bool Buckling => Active && _fsm.Is(DeathPhase.Buckling);
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
        if (drowned) return new DeathPlan(DeathStyle.Fade, FallKind.Forward, 0, 0, 0, scrim);
        var onFoot = before is ClientMode.OnFoot or ClientMode.Rising or ClientMode.Pinned or ClientMode.Dead;
        if (!onFoot || eyeUnderwater || reduceMotion) return new DeathPlan(DeathStyle.Blood, FallKind.Forward, 0, BloodBeatSeconds, blood, scrim);
        var kind = KindFor(seed);
        var total = Math.Clamp(respawnDelay * TotalShareOfDelay, MinTotalSeconds, MaxTotalSeconds);
        var buckle = total * (kind == FallKind.Forward ? BuckleShareForward : BuckleShare);
        return new DeathPlan(DeathStyle.Fall, kind, buckle, total - buckle, blood, scrim);
    }

    /// <summary>The way a fall goes by the seed: forward, backward, left or right, evenly.</summary>
    public static FallKind KindFor(int seed)
    {
        var s = (uint)seed * 2654435761u;
        return (FallKind)((s >> 18) & 3);
    }

    /// <summary>Starts showing a death (from any state, including one still being shown): Buckling, with the plan's clock.</summary>
    public void Begin(DeathPlan plan)
    {
        _plan = plan; Active = true;
        _fsm.Reset(DeathPhase.Buckling);
        _fsm.SetLeft(plan.BuckleSeconds);
        Advance(0);
    }

    /// <summary>The spawn: stops showing the death, whatever the phase.</summary>
    public void End() { Active = false; _fsm.Reset(DeathPhase.Buckling); }

    /// <summary>Advances the clock and the phases (a zero-length phase is passed at once).</summary>
    public void Tick(float dt)
    {
        if (!Active) return;
        Advance(dt);
    }

    void Advance(float dt)
    {
        var done = _fsm.Tick(dt);
        while (done && !_fsm.Is(DeathPhase.Lying))
        {
            var over = -_fsm.Left; // the time the finished phase ran past its end belongs to the next
            if (_fsm.Is(DeathPhase.Buckling)) _fsm.Enter(DeathPhase.Falling, _plan.FallSeconds);
            else _fsm.Enter(DeathPhase.Lying);
            done = over > 0 ? _fsm.Tick(over) : _fsm.Expired;
        }
    }

    /// <summary>How far through the current phase, 0..1 (1 once Lying).</summary>
    float PhaseT(float length) => length <= 0 ? 1f : Math.Clamp(_fsm.Elapsed / length, 0f, 1f);

    /// <summary>How far the whole fall is, 0 at the death to 1 at the ground: the buckle's share of the way, then the rest.</summary>
    public float Drop => Pose.Drop;

    /// <summary>The camera's offset now (<see cref="DeathPose.None"/> when nothing is shown or the style does not move the camera).</summary>
    public DeathPose Pose
    {
        get
        {
            if (!OwnsCamera) return DeathPose.None;
            var k = _plan.Kind;
            if (_fsm.Is(DeathPhase.Buckling)) return BucklePose(k, PhaseT(_plan.BuckleSeconds));
            if (_fsm.Is(DeathPhase.Falling)) return FallPose(k, PhaseT(_plan.FallSeconds));
            return LyingPose(k, _fsm.Elapsed);
        }
    }

    // The sign of the sideways kinds: left tilts the head to the left (roll positive), right the other way.
    static float Side(FallKind k) => k == FallKind.Left ? 1f : -1f;

    /// <summary>The knees (or the lean) give way: a fast drop that stops short, with a stagger. t 0..1 over the buckle.</summary>
    public static DeathPose BucklePose(FallKind k, float t)
    {
        var e = 1f - MathF.Pow(1f - t, 3f); // quick, then the legs hold for a moment
        var stagger = MathF.Sin(t * MathF.PI * 3f) * (1f - t) * 0.04f; // a wobble as they give
        return k switch
        {
            FallKind.Forward => new DeathPose(0.42f * e, -0.12f * e, stagger, 0, 0.15f * e, 0),
            FallKind.Backward => new DeathPose(0.32f * e, 0.2f * e, stagger, 0, -0.1f * e, 0),
            _ => new DeathPose(0.38f * e, -0.04f * e, Side(k) * (0.25f * e + stagger), -Side(k) * 0.1f * e, 0, 0),
        };
    }

    /// <summary>The body goes over and meets the ground: slow at first, then fast, and it stops dead. t 0..1 over the fall.</summary>
    public static DeathPose FallPose(FallKind k, float t)
    {
        var g = MathF.Pow(t, 2.4f); // gravity: the last part is the fast one
        var b = BucklePose(k, 1f);
        return k switch
        {
            FallKind.Forward => new DeathPose(Lerp(b.Drop, 1f, g), Lerp(b.Pitch, -1.25f, g), Lerp(0, 0.3f, g), 0, Lerp(b.PushM, 0.7f, g), 0),
            FallKind.Backward => new DeathPose(Lerp(b.Drop, 1f, g), Lerp(b.Pitch, 1.3f, g), Lerp(0, -0.12f, g), 0, Lerp(b.PushM, -0.6f, g), 0),
            _ => new DeathPose(Lerp(b.Drop, 1f, g), Lerp(b.Pitch, -0.15f, g), Lerp(b.Roll, Side(k) * 1.45f, g), Lerp(b.Yaw, -Side(k) * 0.35f, g), Lerp(0, 0.3f, g), 0),
        };
    }

    /// <summary>On the ground: the end pose plus a damped bounce and shake of the impact. <paramref name="seconds"/> since landing.</summary>
    public static DeathPose LyingPose(FallKind k, float seconds)
    {
        var end = FallPose(k, 1f);
        var decay = MathF.Exp(-seconds * 9f);
        var shake = MathF.Sin(seconds * 34f) * decay;
        var bounce = MathF.Abs(MathF.Sin(seconds * 20f)) * MathF.Exp(-seconds * 8f) * 0.07f;
        return end with { Pitch = end.Pitch + shake * 0.05f, Roll = end.Roll + shake * 0.04f, BounceM = bounce };
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// The blood overlay intensity now: a little at the hit, growing through the buckle and the fall to the full amount where the body lands, then held.
    /// </summary>
    public float BloodNow
    {
        get
        {
            if (!Active || _plan.Blood <= 0) return 0;
            if (_fsm.Is(DeathPhase.Lying)) return _plan.Blood;
            var total = _plan.BuckleSeconds + _plan.FallSeconds;
            if (total <= 0) return _plan.Blood;
            var done = _fsm.Is(DeathPhase.Buckling) ? _fsm.Elapsed : _plan.BuckleSeconds + _fsm.Elapsed;
            var t = Math.Clamp(done / total, 0f, 1f);
            return _plan.Blood * (BloodAtHit + (1f - BloodAtHit) * t * t);
        }
    }

    /// <summary>The respawn scrim's opacity now, 0..1: nothing before Lying, a fade-in once Lying (after the impact has settled for a moment).</summary>
    public float ScrimNow
    {
        get
        {
            if (!Active || !_fsm.Is(DeathPhase.Lying)) return 0;
            var wait = _plan.Style == DeathStyle.Fall ? ImpactSeconds * 0.4f : 0f;
            var e = _fsm.Elapsed - wait;
            if (e <= 0) return 0;
            return _plan.ScrimFadeSeconds <= 0 ? 1 : Math.Clamp(e / _plan.ScrimFadeSeconds, 0f, 1f);
        }
    }
}
