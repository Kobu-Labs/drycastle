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
    enum Phase : byte { Waiting, Warning, Burst }

    readonly HelicopterHelperTuning _t;
    readonly Func<double> _roll;
    Phase _phase = Phase.Waiting;
    /// <summary>Time left in the current phase (Waiting, Warning) or until the next round (Burst).</summary>
    float _timer;
    ushort _target;
    int _roundsLeft;

    /// <param name="tuning">The helper's tunables.</param>
    /// <param name="roll">Uniform random numbers in [0, 1).</param>
    public HelicopterGunner(HelicopterHelperTuning tuning, Func<double> roll)
    {
        _t = tuning;
        _roll = roll;
        _timer = NextCycle();
    }

    /// <summary>The player the gunner is warning or firing at, or null while it waits.</summary>
    public ushort? Target => _phase == Phase.Waiting ? null : _target;

    /// <summary>True while a burst is in progress.</summary>
    public bool Firing => _phase == Phase.Burst;

    /// <summary>
    /// Advances the gunner by <paramref name="dt"/> seconds. <paramref name="enemies"/> are the live enemies now and
    /// <paramref name="clear"/> says whether a line from one point to another is free of world surfaces. The decisions are
    /// appended to <paramref name="actions"/>.
    /// </summary>
    public void Tick(float dt, Vector3 muzzle, IReadOnlyList<HelperCandidate> enemies, Func<Vector3, Vector3, bool> clear, List<HelperAction> actions)
    {
        _timer -= dt;
        while (_timer <= 0)
        {
            switch (_phase)
            {
                case Phase.Waiting:
                    if (Pick(muzzle, enemies, clear) is not { } pick)
                    {
                        _timer += NextCycle();
                        break;
                    }
                    _target = pick;
                    _phase = Phase.Warning;
                    _timer += _t.WarningS;
                    actions.Add(new HelperWarnAction(pick, _t.WarningS));
                    break;

                case Phase.Warning:
                    if (Visible(muzzle, enemies, clear, _target) is not null)
                    {
                        _phase = Phase.Burst; // the first round goes at the end of the warning (the timer is still due)
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
                    else _timer += _t.RoundIntervalS;
                    break;
            }
        }
    }

    void EndBurst()
    {
        _phase = Phase.Waiting;
        _timer += NextCycle();
    }

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
