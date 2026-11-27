using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where the HUD tracker of the player's Job is (machine <c>Cartel.Tracker</c>).</summary>
public enum TrackerPhase : byte { Hidden, Running, Ended }

/// <summary>
/// The HUD tracker of the player's own Job (docs/cartel.md "Contact": the tracker, the marker; GLOSSARY "Job"), fed by the server's messages: <see cref="CartelJobMsg"/> (the
/// Job, its timer, its end) and <see cref="CartelObjectiveMsg"/> (the objective line and the marker). Only the Job's party gets them, so only they see it. Running, it shows the
/// kind and tier, the line and the time left, counted down here from the server's last word, and the marker; at the Job's end the end card (success with the Respect paid,
/// or the failure's reason) stays for a while, then the tracker hides. A player who leaves the party midway is told "no job" and the tracker hides at once. Pure; the
/// client owns one and the HUD draws it.
/// </summary>
public sealed class CartelTracker
{
    public static readonly StateMachineSpec<TrackerPhase> Spec = StateMachineSpec<TrackerPhase>
        .Define("Cartel.Tracker", "The HUD tracker of the player's Job: hidden with no Job, running while it runs (kind, tier, objective line, time left, marker), then its end card for a while.", TrackerPhase.Hidden)
        .Allow(TrackerPhase.Hidden, TrackerPhase.Running, TrackerPhase.Ended)
        .Allow(TrackerPhase.Running, TrackerPhase.Ended, TrackerPhase.Hidden)
        .Allow(TrackerPhase.Ended, TrackerPhase.Hidden, TrackerPhase.Running)
        .Note(TrackerPhase.Hidden, "no Job: nothing shows, no marker")
        .Note(TrackerPhase.Running, "the clock is the time left; each Job message sets it again; the marker shows")
        .Note(TrackerPhase.Ended, "the end card: success and the Respect paid, or the failure's reason; its clock runs the card's seconds, then hidden. A new Job replaces it")
        .Build();

    readonly StateMachine<TrackerPhase> _phase = new(Spec);
    readonly float _cardSeconds;
    Vector3? _marker;
    /// <summary>An objective that came before its Job's message (kept until that Job is known).</summary>
    CartelObjectiveMsg? _early;

    /// <param name="cardSeconds">How long the end card stays.</param>
    public CartelTracker(float cardSeconds) => _cardSeconds = cardSeconds;

    public TrackerPhase Phase => _phase.Current;
    /// <summary>The last Job message (its kind, tier, flags, end state, reason and Respect paid); <see cref="CartelJobMsg.None"/> when none came.</summary>
    public CartelJobMsg Job { get; private set; } = CartelJobMsg.None;
    /// <summary>The objective line while running (empty: the kind's name only).</summary>
    public string Line { get; private set; } = "";
    /// <summary>Where the party's marker points, while running (Godot axes); null for none.</summary>
    public Vector3? Marker => _phase.Is(TrackerPhase.Running) ? _marker : null;
    /// <summary>Seconds left on the Job's timer while running, never below 0.</summary>
    public float SecondsLeft => _phase.Is(TrackerPhase.Running) ? MathF.Max(0f, _phase.Left) : 0f;
    /// <summary>Goes up whenever anything but the clock changes: the HUD rewrites its text only then.</summary>
    public int Revision { get; private set; }

    public void OnJob(CartelJobMsg m)
    {
        if (m.JobId == 0)
        {
            // left the party midway: the tracker goes; an end card that shows stays
            if (_phase.Is(TrackerPhase.Running)) { _phase.Enter(TrackerPhase.Hidden); Forget(); }
            return;
        }
        var fresh = m.JobId != Job.JobId;
        Job = m;
        Revision++;
        if (m.State == JobState.Active)
        {
            if (fresh)
            {
                Line = ""; _marker = null;
                if (_early is { } e && e.JobId == m.JobId) Apply(e);
                _early = null;
            }
            if (!_phase.Enter(TrackerPhase.Running, m.SecondsLeft)) _phase.SetLeft(m.SecondsLeft);
            return;
        }
        _marker = null;
        if (!_phase.Enter(TrackerPhase.Ended, _cardSeconds)) _phase.Restart(_cardSeconds);
    }

    public void OnObjective(CartelObjectiveMsg o)
    {
        if (o.JobId == Job.JobId && _phase.Is(TrackerPhase.Running)) Apply(o);
        else if (o.JobId != Job.JobId) _early = o;
    }

    /// <summary>One frame: the clock runs; the end card hides when its time is up.</summary>
    public void Tick(float dt)
    {
        if (_phase.Is(TrackerPhase.Hidden)) return;
        if (_phase.Tick(dt) && _phase.Is(TrackerPhase.Ended)) { _phase.Enter(TrackerPhase.Hidden); Forget(); }
    }

    /// <summary>The connection ended: no Job, nothing shows.</summary>
    public void Clear()
    {
        _phase.Reset(TrackerPhase.Hidden);
        Job = CartelJobMsg.None;
        Forget();
    }

    /// <summary>Whole seconds as the tracker writes them: "m:ss" (rounded up, so 0:00 shows only at the end).</summary>
    public static string Clock(float seconds)
    {
        var s = (int)MathF.Ceiling(MathF.Max(0f, seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    void Apply(CartelObjectiveMsg o)
    {
        Line = o.Line ?? "";
        _marker = o.Marker;
        Revision++;
    }

    void Forget()
    {
        Line = ""; _marker = null; _early = null;
        Revision++;
    }
}
