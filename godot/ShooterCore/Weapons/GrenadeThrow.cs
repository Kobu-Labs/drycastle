using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where the throw key stands: up, or held to aim (the hand has the grenade, the weapon is out of sight, the arc is shown).</summary>
public enum GrenadeThrowPhase : byte { Idle, Aiming }

/// <summary>What one tick of the throw key came to.</summary>
public enum GrenadeThrowStep : byte { None, Throw, Cancel }

/// <summary>
/// The throw key (G by default, <c>throw_grenade</c>): hold to aim, release to throw, release together with the aim key to put the grenade away. The server and the
/// client run the same rule, so what the client previews is what the server throws. The key only starts an aim from a fresh press, and only where
/// <see cref="CanAim"/> says the key is the grenade's (G is also the Scud screen's: in a seat or on the screen it is not).
/// </summary>
public sealed class GrenadeThrower
{
    public static readonly StateMachineSpec<GrenadeThrowPhase> Spec = StateMachineSpec<GrenadeThrowPhase>
        .Define("Grenade.Throw", "The grenade throw key: held to aim (the arc shows), released to throw.", GrenadeThrowPhase.Idle)
        .Allow(GrenadeThrowPhase.Idle, GrenadeThrowPhase.Aiming)
        .Allow(GrenadeThrowPhase.Aiming, GrenadeThrowPhase.Idle)
        .Note(GrenadeThrowPhase.Idle, "Key up (the start state). A press where the key is the grenade's starts the aim; a key already held when the throw became possible does not.")
        .Note(GrenadeThrowPhase.Aiming, "Key held: the grenade is in the hand and the weapon hidden. Releasing throws; releasing with the aim key held, or losing the right to aim (a vehicle, death, a ladder, the Scud screen), puts it away.")
        .Build();

    readonly StateMachine<GrenadeThrowPhase> _fsm = new(Spec);
    Edge _key;

    public bool Aiming => _fsm.Is(GrenadeThrowPhase.Aiming);

    /// <summary>One input. <paramref name="held"/> is the throw key, <paramref name="cancel"/> the aim key, <paramref name="canAim"/> whether the key is the grenade's now.</summary>
    public GrenadeThrowStep Tick(bool held, bool cancel, bool canAim)
    {
        var rose = _key.Pressed(held);
        if (_fsm.Is(GrenadeThrowPhase.Idle))
        {
            if (rose && canAim) _fsm.Enter(GrenadeThrowPhase.Aiming);
            return GrenadeThrowStep.None;
        }
        if (!canAim) { _fsm.Enter(GrenadeThrowPhase.Idle); _key.Reset(held); return GrenadeThrowStep.Cancel; }
        if (held) return GrenadeThrowStep.None;
        _fsm.Enter(GrenadeThrowPhase.Idle);
        return cancel ? GrenadeThrowStep.Cancel : GrenadeThrowStep.Throw;
    }

    /// <summary>Back to key up, the key counted as held (a held key must be let go before it aims again).</summary>
    public void Reset() { _fsm.Enter(GrenadeThrowPhase.Idle); _key.Reset(true); }

    /// <summary>
    /// Whether the throw key is the grenade's: on foot, hands free, off a ladder, a round in hand, and no Scud screen taking the key (a Scud seat's G opens the screen).
    /// </summary>
    public static bool CanAim(bool onFoot, bool handsFree, bool onLadder, bool hasRound, bool scudScreen) =>
        onFoot && handsFree && !onLadder && hasRound && !scudScreen;
}

/// <summary>The flight of a thrown grenade, traced ahead for the aim arc: the same <see cref="GrenadeFlight.Step"/> the server flies, so what is shown is where it goes.</summary>
public static class GrenadeArc
{
    /// <summary>Where a grenade thrown from <paramref name="from"/> with <paramref name="vel"/> goes until its fuse runs out or it lies still.</summary>
    /// <param name="Points">The path, one point per step, the throw point first.</param>
    /// <param name="Bounces">Where it strikes a surface (the last one is where it comes to rest, when it does).</param>
    public readonly record struct Trace(IReadOnlyList<Vector3> Points, IReadOnlyList<Vector3> Bounces);

    public const float Step = 1f / 30f;

    public static Trace Fly(GrenadeDef g, Vector3 from, Vector3 vel, Func<Vector3, Vector3, float, GrenadeStop> cast)
    {
        var points = new List<Vector3> { from };
        var bounces = new List<Vector3>();
        var pos = from;
        var steps = (int)MathF.Ceiling(g.FuseSeconds / Step);
        for (var k = 0; k < steps; k++)
        {
            var s = GrenadeFlight.Step(g, pos, vel, Step, cast);
            pos = s.Pos; vel = s.Vel;
            points.Add(pos);
            if (s.Bounced) bounces.Add(pos);
            if (s.AtRest) break;
        }
        return new Trace(points, bounces);
    }
}
