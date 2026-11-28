using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Rival vehicle may do about being lost or stalled right now (see <see cref="RelocateGate"/>).</summary>
public enum RelocateVerdict { No, Remove }

/// <summary>
/// When a lost or stalled Rival vehicle (Assault van or chase car) tries to be relocated (docs/cartel.md "Chase cars"), pure: nothing while it does not want to move; an attempt at once when it does and then
/// at most one every relocate_retry_seconds. A try that cannot go on (a player sees the vehicle or stands near it) is only put off (<see cref="Deferred"/>: it keeps trying where it is, no count); a try that
/// found no fresh spot counts (<see cref="Failed"/>): relocate_max_tries in a row remove the vehicle with its crew.
/// </summary>
public sealed class RelocateGate
{
    readonly float _retryS;
    readonly int _maxTries;
    Countdown _retry;
    int _failed;

    public RelocateGate(float retrySeconds, int maxTries) { _retryS = retrySeconds; _maxTries = maxTries; }

    /// <summary>Failures in a row so far.</summary>
    public int Failures => _failed;

    /// <summary>One tick: <paramref name="wants"/> it should be moved now (Lost, or stalled). True when an attempt is due. It no longer wanting forgets the failures.</summary>
    public bool Due(bool wants, float dt)
    {
        if (!wants) { _retry.Cancel(); _failed = 0; return false; }
        _retry.Tick(dt);
        return _retry.Ready;
    }

    /// <summary>The attempt could not be made (seen, or a player near): ask again after the retry time, no failure counted.</summary>
    public void Deferred() => _retry.Start(_retryS);

    /// <summary>The attempt found no fresh spot: ask again after the retry time; <see cref="RelocateVerdict.Remove"/> once relocate_max_tries in a row failed.</summary>
    public RelocateVerdict Failed()
    {
        _failed++;
        if (_failed >= _maxTries) return RelocateVerdict.Remove;
        _retry.Start(_retryS);
        return RelocateVerdict.No;
    }

    /// <summary>It was moved: a fresh start.</summary>
    public void Moved() { _failed = 0; _retry.Cancel(); }
}
