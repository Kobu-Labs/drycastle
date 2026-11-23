namespace Drycastle.ShooterCore.Fsm;

/// <summary>
/// A countdown in seconds, for the "x -= dt; if (x &lt;= 0) fire" fields that sit beside a state (a cooldown, a delay, a grace). A value type: copy it and it is
/// a copy. For a state that owns its clock use <see cref="StateMachine{TState}"/>; for deadlines on an absolute clock (<c>now &gt;= _endsAt</c>) keep the double.
/// <code>
/// Countdown _cooldown;                 // idle: Left 0, Running false
/// _cooldown.Start(2f);
/// if (_cooldown.Tick(dt)) Fire();      // true once, on the tick it runs out
/// if (_cooldown.Ready) ...             // not running: ready again
/// </code>
/// </summary>
public struct Countdown
{
    /// <summary>Seconds left; 0 when idle or run out.</summary>
    public float Left { get; private set; }

    /// <summary>Counting down (started and not yet run out).</summary>
    public readonly bool Running => Left > 0;

    /// <summary>Idle or run out: the thing it guards may happen.</summary>
    public readonly bool Ready => Left <= 0;

    public void Start(float seconds) => Left = seconds > 0 ? seconds : 0;

    /// <summary>Starts only when idle (a cooldown that must not be extended by a second trigger). True when it started.</summary>
    public bool StartIfReady(float seconds)
    {
        if (Running) return false;
        Start(seconds);
        return Running;
    }

    public void Cancel() => Left = 0;

    /// <summary>Advances by <paramref name="dt"/>. True exactly once: on the tick the countdown reaches zero. Idle: false.</summary>
    public bool Tick(float dt)
    {
        if (Left <= 0) return false;
        Left -= dt;
        if (Left > 0) return false;
        Left = 0;
        return true;
    }
}

/// <summary>
/// "Did it change since I last looked?" for a value (the replicated byte, the held key, the phase a view last drew). The first value always counts as a change,
/// so a view that has drawn nothing yet draws. Replaces the <c>_shown = 255</c> / <c>_prev</c> / <c>_wasX</c> fields and their sentinels.
/// </summary>
public struct Change<T>
{
    T _last;
    bool _has;

    /// <summary>The value seen last (default before the first <see cref="Update"/>).</summary>
    public readonly T Last => _last;
    public readonly bool HasValue => _has;

    /// <summary>True when <paramref name="now"/> differs from the last value (or there was none). <paramref name="before"/> is that last value.</summary>
    public bool Update(T now, out T before)
    {
        before = _last;
        if (_has && EqualityComparer<T>.Default.Equals(_last, now)) return false;
        _last = now; _has = true;
        return true;
    }

    public bool Update(T now) => Update(now, out _);

    /// <summary>Forgets the last value: the next <see cref="Update"/> reports a change.</summary>
    public void Reset() { _last = default!; _has = false; }
}

/// <summary>The rising and falling edge of a held input (a key, a button). The first call after construction or <see cref="Reset"/> sees the held state as already held when <c>startHeld</c>: "must release first".</summary>
public struct Edge
{
    bool _held;

    public Edge(bool startHeld) { _held = startHeld; }

    public readonly bool Held => _held;

    /// <summary>True on the update where <paramref name="now"/> turns true.</summary>
    public bool Pressed(bool now)
    {
        var rose = now && !_held;
        _held = now;
        return rose;
    }

    /// <summary>True on the update where <paramref name="now"/> turns false.</summary>
    public bool Released(bool now)
    {
        var fell = !now && _held;
        _held = now;
        return fell;
    }

    public void Reset(bool held = false) => _held = held;
}

/// <summary>Moves a value toward a target by at most a step: the manual fade/ramp that every view re-writes.</summary>
public static class Ramp
{
    public static float MoveToward(float current, float target, float maxStep) =>
        MathF.Abs(target - current) <= maxStep ? target : current + MathF.Sign(target - current) * maxStep;
}

/// <summary>
/// An action waiting for the server's answer: starts on <see cref="Begin"/>, ends on <see cref="End"/> or after <paramref name="giveUpAfter"/> seconds.
/// <see cref="Dimmed"/> turns true after <paramref name="dimAfter"/> so the view can show "working" only when the answer is slow (no flicker for a fast ack).
/// Times are on whatever monotonic clock the caller passes (seconds).
/// </summary>
public struct PendingAnswer
{
    double _since;
    bool _active;

    public readonly bool Active => _active;

    public void Begin(double now) { _since = now; _active = true; }
    public void End() => _active = false;

    public readonly bool Dimmed(double now, double dimAfter) => _active && now - _since >= dimAfter;

    /// <summary>True (once) when the wait has lasted <paramref name="giveUpAfter"/>: the pending state is dropped.</summary>
    public bool GiveUp(double now, double giveUpAfter)
    {
        if (!_active || now - _since < giveUpAfter) return false;
        _active = false;
        return true;
    }
}
