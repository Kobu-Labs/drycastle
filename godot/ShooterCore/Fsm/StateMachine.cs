namespace Drycastle.ShooterCore.Fsm;

/// <summary>Where illegal transitions go. Production: <see cref="Report"/> is called and the transition still happens (a rare bad edge must not take a match down). Tests turn <see cref="Strict"/> on and it throws.</summary>
public static class StateMachineDiagnostics
{
    /// <summary>Throw on an illegal transition instead of reporting it. The unit tests set this.</summary>
    public static bool Strict { get; set; }

    /// <summary>Called with a one-line message for every illegal transition (when not <see cref="Strict"/>). The server wires it to its log.</summary>
    public static Action<string>? Report { get; set; }

    [ThreadStatic] static List<string>? _captured;

    /// <summary>
    /// For a test that drives an undeclared transition on purpose: on THIS thread only, illegal transitions are collected into <paramref name="into"/> (taken, not thrown)
    /// until the scope is disposed. Other threads, and tests running in parallel, keep the global <see cref="Strict"/> behaviour.
    /// </summary>
    public static IDisposable Capture(List<string> into)
    {
        var before = _captured;
        _captured = into;
        return new Scope(() => _captured = before);
    }

    sealed class Scope(Action undo) : IDisposable { public void Dispose() => undo(); }

    internal static void Illegal(string machine, string from, string to)
    {
        var msg = $"illegal transition {machine}: {from} -> {to} (not declared in its spec)";
        if (_captured != null) { _captured.Add(msg); return; }
        if (Strict) throw new InvalidOperationException(msg);
        Report?.Invoke(msg);
    }
}

/// <summary>
/// A state with a clock: the run-time half of a <see cref="StateMachineSpec{TState}"/>. It replaces the usual <c>Phase = X; _phaseLeft = n;</c> pair and
/// the <c>if ((_phaseLeft -= dt) &lt;= 0)</c> check scattered through an Update:
/// <code>
/// _fsm.Enter(MassPhase.Gather, seconds);       // state + countdown in one call, validated against the spec
/// if (_fsm.Tick(dt)) _fsm.Enter(MassPhase.Mass); // true once the countdown ran out
/// </code>
/// Godot-free and allocation-free after construction. One instance belongs to one owner; it is not thread-safe.
/// <para>
/// Time: <see cref="Elapsed"/> is the seconds since the state was entered, <see cref="Left"/> the countdown given to <see cref="Enter"/> (infinite when none).
/// <see cref="Tick"/> does exactly <c>Left -= dt</c> and returns <c>Left &lt;= 0</c>, so a conversion from the hand-written form changes no float arithmetic.
/// </para>
/// </summary>
public sealed class StateMachine<TState> where TState : struct, Enum
{
    readonly StateMachineSpec<TState> _spec;

    public StateMachine(StateMachineSpec<TState> spec)
    {
        _spec = spec;
        Current = Previous = spec.Initial;
        Left = float.PositiveInfinity;
    }

    public StateMachineSpec<TState> Spec => _spec;
    public TState Current { get; private set; }
    /// <summary>The state before the last change (the initial state until the first).</summary>
    public TState Previous { get; private set; }
    /// <summary>Seconds since the state was entered (advanced by <see cref="Tick"/>).</summary>
    public float Elapsed { get; private set; }
    /// <summary>Seconds of the countdown left; infinite when the state has none.</summary>
    public float Left { get; private set; }
    /// <summary>The state has a countdown and it has run out.</summary>
    public bool Expired => Left <= 0;

    /// <summary>Raised after every change: (from, to). Not raised by <see cref="Restart"/>.</summary>
    public event Action<TState, TState>? Changed;

    public bool Is(TState state) => EqualityComparer<TState>.Default.Equals(Current, state);
    public bool IsAny(TState a, TState b) => Is(a) || Is(b);
    public bool IsAny(TState a, TState b, TState c) => Is(a) || Is(b) || Is(c);
    public bool IsAny(params TState[] states) { foreach (var s in states) if (Is(s)) return true; return false; }

    /// <summary>
    /// Goes to <paramref name="next"/> and starts its clock (<paramref name="seconds"/> of countdown; none by default). Already there: nothing happens (the clock
    /// is not restarted; use <see cref="Restart"/>) and the result is false. An undeclared transition is reported (<see cref="StateMachineDiagnostics"/>) and still taken.
    /// </summary>
    public bool Enter(TState next, float seconds = float.PositiveInfinity)
    {
        if (Is(next)) return false;
        if (!_spec.Allows(Current, next)) StateMachineDiagnostics.Illegal(_spec.Name, Current.ToString(), next.ToString());
        var from = Current;
        Previous = from; Current = next; Elapsed = 0; Left = seconds;
        Changed?.Invoke(from, next);
        return true;
    }

    /// <summary>Starts the current state's clock over (new countdown, elapsed 0) without a change event: a state that repeats (the next burst of a volley).</summary>
    public void Restart(float seconds = float.PositiveInfinity) { Elapsed = 0; Left = seconds; }

    /// <summary>Changes the countdown only, keeping <see cref="Elapsed"/> (extend or cut the state short).</summary>
    public void SetLeft(float seconds) => Left = seconds;

    /// <summary>Advances the clock. True when the countdown has run out (and stays true until the next <see cref="Enter"/> or <see cref="Restart"/>).</summary>
    public bool Tick(float dt)
    {
        Elapsed += dt;
        Left -= dt;
        return Left <= 0;
    }

    /// <summary>Jumps to <paramref name="state"/> with no legality check and no event: loading a snapshot, resetting an owner. Clock cleared.</summary>
    public void Reset(TState state)
    {
        Current = Previous = state; Elapsed = 0; Left = float.PositiveInfinity;
    }

    public override string ToString() => $"{_spec.Name}={Current}";
}
