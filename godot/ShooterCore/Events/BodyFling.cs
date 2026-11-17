namespace Drycastle.ShooterCore;

/// <summary>
/// Where a player is in the Bigfoot's grab, as a client draws the body: held in the fist, flying after the throw, or getting up after the landing.
/// None means the normal figure. Cosmetic: the server's movement and damage know nothing of it (#360).
/// </summary>
public enum BodyFling : byte { None, Held, Flying, Landing }

/// <summary>
/// The ragdoll phase of one player the Bigfoot picked up (client only). Held and thrown follow the Bigfoot's <see cref="BigfootAct"/> (Hold, then
/// Launch); the landing is read from the body's speed, as a launched-and-alive flight cannot be told from a jump any other way on the client.
/// </summary>
public sealed class BodyFlingTracker
{
    /// <summary>Held: grabbed. Flying: the throw (the Bigfoot's Launch). Landing: the flight ended, the body turns upright again. None: any end (released, died, left, round reset).</summary>
    public static readonly StateMachineSpec<BodyFling> Spec = StateMachineSpec<BodyFling>
        .Define("Bigfoot.BodyFling", "A player the Bigfoot grabbed, as a client draws them: held in the fist, flying after the throw, getting up after the landing.", BodyFling.None)
        .Allow(BodyFling.None, BodyFling.Held)
        .Allow(BodyFling.Held, BodyFling.Flying, BodyFling.None)
        .Allow(BodyFling.Flying, BodyFling.Landing, BodyFling.Held, BodyFling.None)
        .Allow(BodyFling.Landing, BodyFling.None, BodyFling.Held)
        .Note(BodyFling.Held, "In the fist (Bigfoot act Hold): limbs loose, the body swung by the hand. Released without a throw, the Bigfoot dying or the player dying or leaving ends it at None.")
        .Note(BodyFling.Flying, "The throw (Bigfoot act Launch): tumbling, limbs flailing, until the body is slow again after MinAirSeconds (or MaxAirSeconds).")
        .Note(BodyFling.Landing, "Landed alive: the body turns upright and the limbs go slack, then the normal figure (a dead one is the corpse's, not this machine's).")
        .Build();

    /// <summary>The flight is not judged landed before this (the apex of a throw is slow upwards but not in speed; this is only a guard at the start).</summary>
    public const float MinAirSeconds = 0.4f;
    /// <summary>The speed (m/s) under which a flying body counts as landed. A thrown body moves at ten metres a second or more.</summary>
    public const float LandedSpeedMps = 2.5f;
    /// <summary>A flight never lasts longer (the server's launch grace is five seconds).</summary>
    public const float MaxAirSeconds = 8f;

    readonly StateMachine<BodyFling> _fsm = new(Spec);

    public BodyFling Phase => _fsm.Current;
    /// <summary>Seconds in the phase.</summary>
    public float Elapsed => _fsm.Elapsed;
    /// <summary>The body is drawn as a ragdoll (any phase but None).</summary>
    public bool Active => _fsm.Current != BodyFling.None;

    /// <summary>The Bigfoot's act is Hold on this player.</summary>
    public void Grabbed() => _fsm.Enter(BodyFling.Held);

    /// <summary>The Hold ended in a throw (act Launch). Only a held body is thrown.</summary>
    public void Flung() { if (_fsm.Is(BodyFling.Held)) _fsm.Enter(BodyFling.Flying); }

    /// <summary>The hold ended with no throw, the Bigfoot is gone, or the player died, left or respawned.</summary>
    public void Ended() => _fsm.Enter(BodyFling.None);

    /// <summary>The upright turn after a landing is done: the normal figure.</summary>
    public void StoodUp() { if (_fsm.Is(BodyFling.Landing)) _fsm.Enter(BodyFling.None); }

    /// <summary>Advances the clock. <paramref name="speedMps"/> is the body's speed this frame (what the flight reads its landing from).</summary>
    public void Tick(float dt, float speedMps)
    {
        _fsm.Tick(dt);
        if (_fsm.Is(BodyFling.Flying) && (_fsm.Elapsed >= MaxAirSeconds || _fsm.Elapsed >= MinAirSeconds && speedMps < LandedSpeedMps)) _fsm.Enter(BodyFling.Landing);
    }
}
