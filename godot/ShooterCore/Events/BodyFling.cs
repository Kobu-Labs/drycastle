using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Events;

/// <summary>
/// Where a player is in the Bigfoot's grab, or after a vehicle threw them alive (#411), as a client draws the body: held in the fist, flying after the throw,
/// or getting up after the landing. None means the normal figure. Cosmetic: the server's movement and damage know nothing of it (#360).
/// </summary>
public enum BodyFling : byte { None, Held, Flying, Lying, Landing }

/// <summary>
/// The ragdoll phase of one player the Bigfoot picked up (client only). Held and thrown follow the Bigfoot's <see cref="BigfootAct"/> (Hold, then
/// Launch); the landing is read from the body's speed, as a launched-and-alive flight cannot be told from a jump any other way on the client.
/// </summary>
public sealed class BodyFlingTracker
{
    /// <summary>Held: grabbed. Flying: the throw (the Bigfoot's Launch). Lying: landed, the ragdoll lies on the ground. Landing: it turns upright again. None: any end (released, died, left, round reset).</summary>
    public static readonly StateMachineSpec<BodyFling> Spec = StateMachineSpec<BodyFling>
        .Define("Bigfoot.BodyFling", "A player the Bigfoot grabbed or a vehicle threw alive, as a client draws them: held in the fist, flying after the throw, getting up after the landing.", BodyFling.None)
        .Allow(BodyFling.None, BodyFling.Held, BodyFling.Flying)
        .Allow(BodyFling.Held, BodyFling.Flying, BodyFling.None)
        .Allow(BodyFling.Flying, BodyFling.Lying, BodyFling.Held, BodyFling.None)
        .Allow(BodyFling.Lying, BodyFling.Landing, BodyFling.Held, BodyFling.Flying, BodyFling.None)
        .Allow(BodyFling.Landing, BodyFling.None, BodyFling.Held, BodyFling.Flying)
        .Note(BodyFling.Held, "In the fist (Bigfoot act Hold): limbs loose, the body swung by the hand. Released without a throw, the Bigfoot dying or the player dying or leaving ends it at None.")
        .Note(BodyFling.Flying, "The throw (Bigfoot act Launch, or a vehicle that ran the player over without killing: from None, or again while lying or getting up): tumbling, limbs flailing, until the body is on the ground and slow (after MinAirSeconds), or has been slow for SettleSeconds where the ground cannot be told (a roof), or MaxAirSeconds.")
        .Note(BodyFling.Lying, "Landed alive: the ragdoll lies limp on the ground for LieSeconds.")
        .Note(BodyFling.Landing, "Getting up: the body turns upright and the limbs go slack, then the normal figure (a dead one is the corpse's, not this machine's).")
        .Build();

    /// <summary>The flight is not judged landed before this (the apex of a throw is slow upwards but not in speed; this is only a guard at the start).</summary>
    public const float MinAirSeconds = 0.4f;
    /// <summary>The speed (m/s) under which a flying body counts as landed. A thrown body moves at ten metres a second or more.</summary>
    public const float LandedSpeedMps = 2.5f;
    /// <summary>Slow for this long without a ground reading (a roof, a ledge the terrain height does not know) also counts as landed. Longer than a steep throw's apex is slow.</summary>
    public const float SettleSeconds = 0.8f;
    /// <summary>How long the body lies limp on the ground before it gets up.</summary>
    public const float LieSeconds = 0.5f;
    /// <summary>A flight never lasts longer (the server's launch grace is five seconds).</summary>
    public const float MaxAirSeconds = 8f;

    readonly StateMachine<BodyFling> _fsm = new(Spec);
    float _slowFor;

    public BodyFling Phase => _fsm.Current;
    /// <summary>Seconds in the phase.</summary>
    public float Elapsed => _fsm.Elapsed;
    /// <summary>The body is drawn as a ragdoll (any phase but None).</summary>
    public bool Active => _fsm.Current != BodyFling.None;

    /// <summary>The Bigfoot's act is Hold on this player.</summary>
    public void Grabbed() => _fsm.Enter(BodyFling.Held);

    /// <summary>The Hold ended in a throw (act Launch). Only a held body is thrown.</summary>
    public void Flung() { if (_fsm.Is(BodyFling.Held)) { _slowFor = 0; _fsm.Enter(BodyFling.Flying); } }

    /// <summary>A vehicle threw the player alive (#411: the server's PlayerFlung, or the driver's own guess of it). A held body stays the Bigfoot's; one already flying flies on.</summary>
    public void Thrown()
    {
        if (_fsm.Is(BodyFling.Held)) return;
        _slowFor = 0;
        if (!_fsm.Enter(BodyFling.Flying)) _fsm.Restart();
    }

    /// <summary>The hold ended with no throw, the Bigfoot is gone, or the player died, left or respawned.</summary>
    public void Ended() => _fsm.Enter(BodyFling.None);

    /// <summary>The upright turn after a landing is done: the normal figure.</summary>
    public void StoodUp() { if (_fsm.Is(BodyFling.Landing)) _fsm.Enter(BodyFling.None); }

    /// <summary>
    /// Advances the clock. <paramref name="speedMps"/> is the body's speed this frame, <paramref name="grounded"/> whether its feet are on the ground.
    /// Slow alone is not a landing: the apex of a steep throw is slow too.
    /// </summary>
    public void Tick(float dt, float speedMps, bool grounded)
    {
        _fsm.Tick(dt);
        if (_fsm.Is(BodyFling.Flying))
        {
            var slow = speedMps < LandedSpeedMps;
            _slowFor = slow ? _slowFor + dt : 0;
            if (_fsm.Elapsed >= MaxAirSeconds || _fsm.Elapsed >= MinAirSeconds && slow && (grounded || _slowFor >= SettleSeconds)) _fsm.Enter(BodyFling.Lying);
        }
        else if (_fsm.Is(BodyFling.Lying) && _fsm.Elapsed >= LieSeconds) _fsm.Enter(BodyFling.Landing);
    }
}

/// <summary><see cref="EventKind.PlayerFlung"/>: u16 player (a vehicle ran them over and they live: every client draws them thrown, <see cref="BodyFlingTracker.Thrown"/>, #411).</summary>
public static class PlayerFlungCodec
{
    public static byte[] Encode(ushort player) => new[] { (byte)player, (byte)(player >> 8) };

    public static ushort? Decode(ReadOnlySpan<byte> p) => p.Length == 2 ? (ushort)(p[0] | p[1] << 8) : null;
}
