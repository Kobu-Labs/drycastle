using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>An enemy the helicopter may shoot: its id, the point it aims at (chest height) and its stance (for exposure).</summary>
public readonly record struct HelperCandidate(ushort Id, Vector3 Aim, Stance Stance);

/// <summary>Something the gunner decided this tick. The server turns it into a warning event or a tracer and a hit.</summary>
public abstract record HelperAction;

/// <summary>The target has been picked: it gets the red-dot warning now and the burst after <see cref="HelicopterHelperTuning.WarningS"/>.</summary>
public sealed record HelperWarnAction(ushort Target, float Seconds) : HelperAction;

/// <summary>One round of a burst. <see cref="End"/> is the hit point, or where a miss lands.</summary>
public sealed record HelperRoundAction(ushort Target, Vector3 From, Vector3 End, bool Hit, float Damage) : HelperAction;

/// <summary>
/// The helicopter's gun brain (pure, no Godot): every few seconds it picks the best visible enemy, warns that enemy, and
/// fires a burst at it. A burst ends early when the target dies or goes out of sight. Time comes in through
/// <see cref="Tick"/> and randomness through the injected roll, so the tests can run it without a server.
///
/// Target choice: among live enemies within <see cref="HelicopterHelperTuning.SightRangeM"/> with a clear line from the
/// helicopter, the lowest score wins, where score = distance / exposure(stance). A standing player (exposure 1) at the same
/// distance is preferred over a prone one.
/// </summary>
public sealed class HelicopterGunner
{
    public enum Phase : byte { Waiting, Warning, Burst }

    /// <summary>The cycle: wait, pick and warn, fire a burst, back to waiting. A target that hides or dies ends the warning or the burst early.</summary>
    public static readonly StateMachineSpec<Phase> Spec = StateMachineSpec<Phase>.Define(
            "HelicopterGunner.Phase", "The helicopter's gun cycle: wait, warn the picked enemy, fire a burst, wait again.", Phase.Waiting)
        .Allow(Phase.Waiting, Phase.Warning)
        .Allow(Phase.Warning, Phase.Burst, Phase.Waiting)
        .Allow(Phase.Burst, Phase.Waiting)
        .Note(Phase.Waiting, "Counting down the cycle gap; at zero it picks the best visible enemy (none: waits another gap). No target.")
        .Note(Phase.Warning, "The target has its red-dot warning; at zero the burst starts if it is still visible, else back to Waiting.")
        .Note(Phase.Burst, "One round each round interval while the target stays visible; the last round or a lost target goes back to Waiting.")
        .Build();

    readonly HelicopterHelperTuning _t;
    readonly Func<double> _roll;
    /// <summary>
    /// The phase and its clock: the time left in Waiting and Warning, or until the next round in Burst. The clock CARRIES OVER (see <see cref="Carry"/> and
    /// <see cref="Extend"/>): a long step runs several phases in one <see cref="Tick"/>, so the overshoot of one phase counts towards the next.
    /// </summary>
    readonly StateMachine<Phase> _fsm = new(Spec);
    /// <summary>Who is warned or shot at; only meaningful outside Waiting.</summary>
    ushort _target;
    int _roundsLeft;

    /// <param name="tuning">The helper's tunables.</param>
    /// <param name="roll">Uniform random numbers in [0, 1).</param>
    public HelicopterGunner(HelicopterHelperTuning tuning, Func<double> roll)
    {
        _t = tuning;
        _roll = roll;
        _fsm.Restart(NextCycle());
    }

    /// <summary>The player the gunner is warning or firing at, or null while it waits.</summary>
    public ushort? Target => _fsm.Is(Phase.Waiting) ? null : _target;

    /// <summary>True while a burst is in progress.</summary>
    public bool Firing => _fsm.Is(Phase.Burst);

    /// <summary>
    /// Advances the gunner by <paramref name="dt"/> seconds. <paramref name="enemies"/> are the live enemies now and
    /// <paramref name="clear"/> says whether a line from one point to another is free of world surfaces. The decisions are
    /// appended to <paramref name="actions"/>.
    /// </summary>
    public void Tick(float dt, Vector3 muzzle, IReadOnlyList<HelperCandidate> enemies, Func<Vector3, Vector3, bool> clear, List<HelperAction> actions)
    {
        _fsm.Tick(dt);
        while (_fsm.Expired)
        {
            switch (_fsm.Current)
            {
                case Phase.Waiting:
                    if (Pick(muzzle, enemies, clear) is not { } pick)
                    {
                        Extend(NextCycle());
                        break;
                    }
                    _target = pick;
                    Carry(Phase.Warning);
                    Extend(_t.WarningS);
                    actions.Add(new HelperWarnAction(pick, _t.WarningS));
                    break;

                case Phase.Warning:
                    if (Visible(muzzle, enemies, clear, _target) is not null)
                    {
                        Carry(Phase.Burst); // the first round goes at the end of the warning (the clock is still due)
                        _roundsLeft = _t.Rounds;
                    }
                    else EndBurst();
                    break;

                case Phase.Burst:
                    if (Visible(muzzle, enemies, clear, _target) is not { } aim)
                    {
                        EndBurst();
                        break;
                    }
                    var hit = _roll() < _t.Accuracy;
                    var end = hit ? aim : MissPoint(aim);
                    actions.Add(new HelperRoundAction(_target, muzzle, end, hit, _t.Damage));
                    _roundsLeft--;
                    if (_roundsLeft <= 0) EndBurst();
                    else Extend(_t.RoundIntervalS);
                    break;
            }
        }
    }

    /// <summary>Back to waiting for the next cycle (a burst finished, or its target hid or died). Also ends a warning whose target is gone.</summary>
    void EndBurst()
    {
        Carry(Phase.Waiting);
        Extend(NextCycle());
    }

    /// <summary>Changes phase keeping the clock as it is, overshoot included (<c>Enter</c> alone would reset it).</summary>
    void Carry(Phase next)
    {
        var left = _fsm.Left;
        _fsm.Enter(next);
        _fsm.SetLeft(left);
    }

    /// <summary>Adds <paramref name="seconds"/> to the clock: the next event is that much later than the one just handled.</summary>
    void Extend(float seconds) => _fsm.SetLeft(_fsm.Left + seconds);

    float NextCycle() => _t.CycleMinS + (float)_roll() * (_t.CycleMaxS - _t.CycleMinS);

    /// <summary>The aim point of <paramref name="id"/> when it is alive and visible from the muzzle, else null.</summary>
    static Vector3? Visible(Vector3 muzzle, IReadOnlyList<HelperCandidate> enemies, Func<Vector3, Vector3, bool> clear, ushort id)
    {
        foreach (var e in enemies)
            if (e.Id == id) return clear(muzzle, e.Aim) ? e.Aim : null;
        return null;
    }

    /// <summary>Best visible enemy by distance / exposure within sight range; ties keep the lower id. Null when none.</summary>
    ushort? Pick(Vector3 muzzle, IReadOnlyList<HelperCandidate> enemies, Func<Vector3, Vector3, bool> clear)
    {
        ushort? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var e in enemies)
        {
            var d = Vector3.Distance(muzzle, e.Aim);
            if (d > _t.SightRangeM) continue;
            var score = d / _t.Exposure(e.Stance);
            if (score > bestScore || (score == bestScore && best is { } b && e.Id > b)) continue;
            if (!clear(muzzle, e.Aim)) continue;
            best = e.Id;
            bestScore = score;
        }
        return best;
    }

    /// <summary>Where a missed round lands: a random point within <see cref="HelicopterHelperTuning.MissRadiusM"/> of the aim, on the ground plane.</summary>
    Vector3 MissPoint(Vector3 aim)
    {
        var angle = (float)(_roll() * Math.Tau);
        var radius = _t.MissRadiusM * MathF.Sqrt((float)_roll());
        return aim + new Vector3(radius * MathF.Cos(angle), 0f, radius * MathF.Sin(angle));
    }
}
