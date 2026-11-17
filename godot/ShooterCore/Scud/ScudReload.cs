namespace Drycastle.ShooterCore;

/// <summary>A rocket cart as the reload rules see it.</summary>
public readonly record struct ScudCartCandidate(ushort Id, BodyBox Box, bool Loaded, bool Alive);

/// <summary>
/// The pure rules of loading a rocket from a cart onto an empty Scud truck (GLOSSARY "Rocket cart", "Launcher"): which cart is in reach, who may start it. The server decides with them and the client shows its
/// prompt with the same ones. Distances are on the ground plane, between the vehicles' hit boxes (the cart's box is 12.6 m long, so a distance between origins would mean nothing).
/// </summary>
public static class ScudReloadRules
{
    /// <summary>The gap between two boxes, metres: 0 when they touch or overlap. Exact for boxes that do not cross; sampled (five points along each edge) otherwise-near cases.</summary>
    public static float Gap(in BodyBox a, in BodyBox b)
    {
        var best = float.MaxValue;
        foreach (var (x, z) in Samples(a)) best = MathF.Min(best, b.DistanceTo(x, z));
        foreach (var (x, z) in Samples(b)) best = MathF.Min(best, a.DistanceTo(x, z));
        return best;
    }

    /// <summary>The corners, the edges' quarter points and the centre of a box.</summary>
    static IEnumerable<(float X, float Z)> Samples(BodyBox b)
    {
        var (s, c) = MathF.SinCos(b.Yaw);
        float fx = -s, fz = -c, rx = c, rz = -s; // the heading and its right hand
        yield return (b.X, b.Z);
        for (var i = 0; i <= 4; i++)
        {
            var t = -1f + i * 0.5f;
            foreach (var side in new[] { -1f, 1f })
            {
                yield return (b.X + rx * b.HalfW * side + fx * b.HalfL * t, b.Z + rz * b.HalfW * side + fz * b.HalfL * t); // the long edges
                yield return (b.X + fx * b.HalfL * side + rx * b.HalfW * t, b.Z + fz * b.HalfL * side + rz * b.HalfW * t); // the short ends
            }
        }
    }

    /// <summary>The nearest loaded, live cart whose box is within <paramref name="reach"/> of the truck's; null when none.</summary>
    public static ScudCartCandidate? NearestCart(in BodyBox truck, IEnumerable<ScudCartCandidate> carts, float reach)
    {
        ScudCartCandidate? best = null; var bestGap = float.MaxValue;
        foreach (var c in carts)
        {
            if (!c.Loaded || !c.Alive) continue;
            var gap = Gap(truck, c.Box);
            if (gap > reach || gap >= bestGap) continue;
            best = c; bestGap = gap;
        }
        return best;
    }

    /// <summary>
    /// Whether a player may start a loading from the point (<paramref name="x"/>, <paramref name="z"/>): the rack is empty (<paramref name="state"/>), and they sit in the truck (<paramref name="aboard"/>) or stand within
    /// <see cref="ScudReloadData.PlayerReachM"/> of its box.
    /// </summary>
    public static bool MayStart(ScudLauncherState state, bool aboard, in BodyBox truck, float x, float z, ScudReloadData d) =>
        state == ScudLauncherState.Empty && (aboard || truck.DistanceTo(x, z) <= (float)d.PlayerReachM);
}

/// <summary>What the interact key did this tick (<see cref="ScudReloadKey.Tick"/>).</summary>
public enum ScudKeyAction : byte { None, Hold, Tap }

/// <summary>The states of <see cref="ScudReloadKey"/>.</summary>
public enum ScudKeyState : byte { Idle, Holding, Spent }

/// <summary>
/// The interact key while a loading from a rocket cart is on offer (the rack is empty, a loaded cart in reach): the key still gets in or out of the truck (a tap, acted on when it is let go) and, held for
/// <see cref="ScudReloadData.HoldSeconds"/>, starts the loading. The same shape as the tow's <see cref="UnhitchKey"/>, which it never meets: a truck does not tow. When nothing is on offer the key is the
/// plain press (<see cref="Disarm"/>), and a key that is already down as the offer appears must be let go first. Pure; the server ticks one per player with each input's <c>dt</c>.
/// </summary>
public sealed class ScudReloadKey
{
    public static readonly StateMachineSpec<ScudKeyState> Spec = StateMachineSpec<ScudKeyState>.Define("Scud.ReloadKey", "The interact key beside a truck with an empty rack and a loaded cart: a tap gets in or out, a hold starts loading the rocket.", ScudKeyState.Idle)
        .Allow(ScudKeyState.Idle, ScudKeyState.Holding)
        .Allow(ScudKeyState.Holding, ScudKeyState.Idle, ScudKeyState.Spent)
        .Allow(ScudKeyState.Spent, ScudKeyState.Idle)
        .Note(ScudKeyState.Idle, "Key up. A press starts the hold.")
        .Note(ScudKeyState.Holding, "Key down, counting the hold. Let go early: a tap, the key does what it always does (get in or out). Held to the end: the loading starts.")
        .Note(ScudKeyState.Spent, "The hold did its work (or the key was down when the offer appeared): nothing happens until the key is let go.")
        .Build();

    readonly StateMachine<ScudKeyState> _fsm = new(Spec);

    public ScudKeyState State => _fsm.Current;

    /// <summary>Nothing is on offer: the key is not watched, and one that is down must be let go before an offer counts. Call every input while nothing is on offer.</summary>
    public void Disarm(bool keyDown) => _fsm.Reset(keyDown ? ScudKeyState.Spent : ScudKeyState.Idle);

    /// <summary>One input with an offer standing: the key as it is (<paramref name="down"/>) for <paramref name="dt"/> seconds; <paramref name="holdSeconds"/> to start the loading.</summary>
    public ScudKeyAction Tick(bool down, float dt, float holdSeconds)
    {
        switch (_fsm.Current)
        {
            case ScudKeyState.Idle:
                if (down) _fsm.Enter(ScudKeyState.Holding, holdSeconds);
                return ScudKeyAction.None;
            case ScudKeyState.Holding:
                if (!down) { _fsm.Enter(ScudKeyState.Idle); return ScudKeyAction.Tap; }
                if (_fsm.Tick(dt)) { _fsm.Enter(ScudKeyState.Spent); return ScudKeyAction.Hold; }
                return ScudKeyAction.None;
            default:
                if (!down) _fsm.Enter(ScudKeyState.Idle);
                return ScudKeyAction.None;
        }
    }
}
