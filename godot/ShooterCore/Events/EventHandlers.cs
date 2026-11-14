namespace Drycastle.ShooterCore;

/// <summary>What an admin's <c>/event current end|cancel</c> came to (<see cref="EventHandlers{TWho}.End"/>, <see cref="EventHandlers{TWho}.Cancel"/>).</summary>
public enum EventHandled : byte
{
    /// <summary>The running kind's handler ran.</summary>
    Done,
    /// <summary>No event runs, or the running kind has no handler.</summary>
    NoneRunning,
    /// <summary>The running kind cannot be cancelled, only ended (the Holy Mass).</summary>
    CannotCancel,
}

/// <summary>
/// The per-kind handler table of the Events (GLOSSARY "Event") a server can run: what <c>/event &lt;kind&gt;</c> starts and what <c>/event current end</c> (as a
/// win) and <c>/event current cancel</c> (no reward) do to the running one, looked up by the kind of <see cref="EventBook.Current"/> so the chat commands know no
/// kind. A kind registers once (<see cref="Register"/>); its handlers book their own end in the <see cref="EventBook"/>. <typeparamref name="TWho"/> is whoever
/// typed the command (the server's client), passed through to the handler.
/// </summary>
public sealed class EventHandlers<TWho>
{
    sealed record Entry(string Name, Action<TWho> Start, Action<TWho> End, Action<TWho>? Cancel);

    readonly Dictionary<string, Entry> _kinds = new();

    /// <summary>Adds a kind. <paramref name="name"/> is how a refusal names it ("the Holy Mass"); a null <paramref name="cancel"/> means the kind can only end.</summary>
    public void Register(string kind, string name, Action<TWho> start, Action<TWho> end, Action<TWho>? cancel = null)
    {
        if (!EventBook.IsStartable(kind)) throw new ArgumentException($"'{kind}' is not a startable event kind of the EventBook", nameof(kind));
        if (!_kinds.TryAdd(kind, new Entry(name, start, end, cancel))) throw new ArgumentException($"event kind '{kind}' is registered twice", nameof(kind));
    }

    public bool Has(string kind) => _kinds.ContainsKey(kind);

    /// <summary>Runs the start of <paramref name="kind"/> (the handler asks the book whether it may). False when the kind has no handler.</summary>
    public bool Start(string kind, TWho who)
    {
        if (!_kinds.TryGetValue(kind, out var e)) return false;
        e.Start(who);
        return true;
    }

    /// <summary>Runs the end (as a win) of the running event: the handler of the kind of <paramref name="events"/>' current run.</summary>
    public EventHandled End(EventBook events, TWho who)
    {
        if (events.Current is not { } run || !_kinds.TryGetValue(run.Kind, out var e)) return EventHandled.NoneRunning;
        e.End(who);
        return EventHandled.Done;
    }

    /// <summary>Runs the cancel (no reward) of the running event. <paramref name="name"/> is the running kind's name, for a refusal.</summary>
    public EventHandled Cancel(EventBook events, TWho who, out string name)
    {
        name = "";
        if (events.Current is not { } run || !_kinds.TryGetValue(run.Kind, out var e)) return EventHandled.NoneRunning;
        name = e.Name;
        if (e.Cancel is not { } cancel) return EventHandled.CannotCancel;
        cancel(who);
        return EventHandled.Done;
    }
}
