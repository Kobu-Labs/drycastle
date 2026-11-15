namespace Drycastle.ShooterCore;

/// <summary>Where an event is: running, concluded as a win (its reward is given), or gone with no reward (it timed out or an admin cancelled it).</summary>
public enum EventPhase : byte { Active, Won, Despawned }

/// <summary>
/// One run of an Event (issue #264, GLOSSARY): a short, time-limited happening with a challenge and a reward. Only the lifecycle lives here, the same for every
/// kind: active, then won or despawned. What the event does (the Bigfoot's fight) belongs to the kind.
/// </summary>
public sealed class EventRun
{
    /// <summary>A run is Active until it is won or despawned; both are final.</summary>
    public static readonly StateMachineSpec<EventPhase> Spec = StateMachineSpec<EventPhase>
        .Define("Event.Run", "One run of an event: active, then won (reward given) or despawned (no reward). Both ends are final.", EventPhase.Active)
        .Allow(EventPhase.Active, EventPhase.Won, EventPhase.Despawned)
        .Note(EventPhase.Won, "Concluded: the reward is due.")
        .Note(EventPhase.Despawned, "Timed out, cancelled or cleared by a round: no reward.")
        .Build();

    readonly StateMachine<EventPhase> _fsm = new(Spec);

    public EventRun(string kind, double startedAt) { Kind = kind; StartedAt = startedAt; }

    /// <summary>The event's name as the admin types it (<see cref="EventBook.Bigfoot"/>).</summary>
    public string Kind { get; }
    public double StartedAt { get; }
    public EventPhase Phase => _fsm.Current;
    public double EndedAt { get; private set; }
    public bool Active => Phase == EventPhase.Active;

    /// <summary>Concludes the event as a win. False when it was already over.</summary>
    public bool Win(double now) => End(EventPhase.Won, now);

    /// <summary>Ends the event with no reward. False when it was already over.</summary>
    public bool Despawn(double now) => End(EventPhase.Despawned, now);

    bool End(EventPhase phase, double now)
    {
        if (!Active) return false;
        _fsm.Enter(phase);
        EndedAt = now;
        return true;
    }
}

/// <summary>
/// The events a server can run, and the one that is running: at most one at a time (the first version has no scheduler). Every kind an admin can start with
/// <c>/event &lt;kind&gt;</c> registers its run here (<see cref="Start"/>) and books its end (<see cref="End(EventPhase, double)"/>), so "an event is running"
/// is one fact the Bigfoot, the Holy Mass, the Demon and the Thieves all see: <see cref="WhyNotStart"/> is the one refusal each start command asks before it begins anything.
/// </summary>
public sealed class EventBook
{
    public const string Bigfoot = "bigfoot", Mass = "mass", Demon = "demon", Thieves = "thieves";

    /// <summary>The kinds an admin can start with <c>/event &lt;kind&gt;</c> that take part in "one at a time".</summary>
    public static readonly IReadOnlyList<string> Startable = new[] { Bigfoot, Mass, Demon, Thieves };

    /// <summary>The running event, or null.</summary>
    public EventRun? Current { get; private set; }

    /// <summary>The event that ended last, or null; kept so a late "who won" question has an answer.</summary>
    public EventRun? Last { get; private set; }

    public static bool IsStartable(string kind) => Startable.Contains(kind);

    /// <summary>The player-facing refusal when <paramref name="kind"/> cannot start now (another event runs, or the kind is unknown); null when it may.</summary>
    public string? WhyNotStart(string kind)
    {
        if (!IsStartable(kind)) return $"Unknown event '{kind}'.";
        if (Current is not { } run) return null;
        return $"The {run.Kind} event is already running. /event current end concludes it, /event current cancel removes it (not every kind can be cancelled).";
    }

    /// <summary>Starts a run of <paramref name="kind"/>; null when one is already running or the kind is unknown.</summary>
    public EventRun? Start(string kind, double now)
    {
        if (WhyNotStart(kind) != null) return null;
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

    /// <summary>Ends the running event only if it is of <paramref name="kind"/> (a kind books its own end, never another's). Null when nothing of that kind runs.</summary>
    public EventRun? End(string kind, EventPhase outcome, double now) => Current?.Kind == kind ? End(outcome, now) : null;
}
