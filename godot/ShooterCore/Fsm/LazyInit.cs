namespace Drycastle.ShooterCore;

/// <summary>Where a <see cref="LazyInit{T}"/> is: not tried, tried without a result, or holding one.</summary>
public enum LazyState { Untried, Failed, Ready }

/// <summary>
/// "Load it on first use, once": the <c>T? _x; bool _xTried;</c> pair for a file or a build that may be missing or broken (it is logged once and the feature stays
/// off). <see cref="Begin"/> is true exactly once, and marks the try BEFORE it runs, so a re-entrant call (the init asks for itself) and a failed try both fall
/// through to <see cref="Value"/> (null) without trying again. There is no retry: a thing that failed stays failed until <see cref="Reset"/>.
/// <code>
/// LazyInit&lt;Urad&gt; _urad;
/// Urad? EnsureUrad()
/// {
///     if (!_urad.Begin()) return _urad.Value;
///     var u = Load();                     // may be null: logged, stays Failed
///     if (u != null) _urad.Ready(u);
///     return _urad.Value;
/// }
/// </code>
/// A value type: a field of the owner, not readonly.
/// </summary>
public struct LazyInit<T> where T : class
{
    public LazyState State { get; private set; }

    /// <summary>The loaded value; null until <see cref="Ready"/> (and after a failed try).</summary>
    public T? Value { get; private set; }

    public readonly bool Tried => State != LazyState.Untried;

    /// <summary>True the first time only: the caller must try now. Marks the try as failed until <see cref="Ready"/> says otherwise.</summary>
    public bool Begin()
    {
        if (State != LazyState.Untried) return false;
        State = LazyState.Failed;
        return true;
    }

    /// <summary>The try worked: holds <paramref name="value"/>.</summary>
    public void Ready(T value)
    {
        Value = value;
        State = LazyState.Ready;
    }

    /// <summary>Forgets everything: the next <see cref="Begin"/> tries again (a new map, a test).</summary>
    public void Reset()
    {
        Value = null;
        State = LazyState.Untried;
    }
}
