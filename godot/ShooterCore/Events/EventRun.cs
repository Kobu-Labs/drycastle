namespace Drycastle.ShooterCore;

/// <summary>Where an event is: running, concluded as a win (its reward is given), or gone with no reward (it timed out or an admin cancelled it).</summary>
public enum EventPhase : byte { Active, Won, Despawned }

/// <summary>
/// One run of an Event (issue #264, GLOSSARY): a short, time-limited happening with a challenge and a reward. Only the lifecycle lives here, the same for every
/// kind: active, then won or despawned. What the event does (the Bigfoot's fight) belongs to the kind.
/// </summary>
public sealed class EventRun
{
    public EventRun(string kind, double startedAt) { Kind = kind; StartedAt = startedAt; }

    /// <summary>The event's name as the admin types it (<see cref="EventBook.Bigfoot"/>).</summary>
    public string Kind { get; }
    public double StartedAt { get; }
    public EventPhase Phase { get; private set; } = EventPhase.Active;
    public double EndedAt { get; private set; }
    public bool Active => Phase == EventPhase.Active;

    /// <summary>Concludes the event as a win. False when it was already over.</summary>
    public bool Win(double now) => End(EventPhase.Won, now);

    /// <summary>Ends the event with no reward. False when it was already over.</summary>
    public bool Despawn(double now) => End(EventPhase.Despawned, now);

    bool End(EventPhase phase, double now)
    {
        if (!Active) return false;
        Phase = phase; EndedAt = now;
        return true;
    }
}

/// <summary>The events a server can run, and the one that is running: at most one at a time (the first version has no scheduler).</summary>
public sealed class EventBook
{
    public const string Bigfoot = "bigfoot";

    /// <summary>The kinds an admin can start with <c>/event &lt;kind&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> Startable = new[] { Bigfoot };

    /// <summary>The running event, or null.</summary>
    public EventRun? Current { get; private set; }

    /// <summary>The event that ended last, or null; kept so a late "who won" question has an answer.</summary>
    public EventRun? Last { get; private set; }

    public static bool IsStartable(string kind) => Startable.Contains(kind);

    /// <summary>Starts a run of <paramref name="kind"/>; null when one is already running or the kind is unknown.</summary>
    public EventRun? Start(string kind, double now)
    {
        if (Current != null || !IsStartable(kind)) return null;
        return Current = new EventRun(kind, now);
    }

    /// <summary>Ends the running event with <paramref name="outcome"/> (won or despawned); null when none is running.</summary>
    public EventRun? End(EventPhase outcome, double now)
    {
        if (Current is not { } run || outcome == EventPhase.Active) return null;
        if (outcome == EventPhase.Won) run.Win(now); else run.Despawn(now);
        Last = run;
        Current = null;
        return run;
    }
}
