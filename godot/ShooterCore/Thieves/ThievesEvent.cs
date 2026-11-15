namespace Drycastle.ShooterCore;

/// <summary>Where the Thieves event is: not started, its Raids are in the village, or it is over (won or cancelled; what was stolen stays stolen until the round resets).</summary>
public enum ThievesPhase : byte { Idle, Raiding, Over }

/// <summary>What <see cref="ThievesEvent.Tick"/> asks the server to do.</summary>
public enum ThievesStep : byte { None, Launch, Won }

/// <summary>
/// The Thieves event's own phase and Raid sequence (GLOSSARY "Thieves event", "Raid"), pure. The lifecycle every Event shares (running, won, despawned) is the
/// <see cref="EventRun"/> of the <see cref="EventBook"/>; this is what the kind adds: how many Raids come and when. The first comes at once; the next one when fewer than
/// <see cref="RaidsConfig.MaxVans"/> vans are in the village and either <see cref="RaidsConfig.NextRaidSeconds"/> passed since the last began or the gap after a van left
/// or was destroyed (<see cref="RaidsConfig.GapSeconds"/>) has. After the last Raid is launched and no van is left, the event is won. It cannot be lost.
/// </summary>
public sealed class ThievesEvent
{
    /// <summary>The intel board's id of the event's announcement.</summary>
    public const string IntelId = "thieves";

    public static readonly StateMachineSpec<ThievesPhase> Spec = StateMachineSpec<ThievesPhase>
        .Define("Thieves.Phase", "The Thieves event: idle until started, raiding while its Raids are in the village, then over until the round resets.", ThievesPhase.Idle)
        .Allow(ThievesPhase.Idle, ThievesPhase.Raiding)
        .Allow(ThievesPhase.Raiding, ThievesPhase.Over, ThievesPhase.Idle)
        .Allow(ThievesPhase.Over, ThievesPhase.Raiding, ThievesPhase.Idle)
        .Note(ThievesPhase.Idle, "Nothing runs: never started, or a round reset cleared it.")
        .Note(ThievesPhase.Raiding, "The Raids come and go (no clock of its own: the next-Raid clock runs beside). Left by Finish (won when the last van is gone, or an admin's end or cancel) or by a round reset.")
        .Note(ThievesPhase.Over, "Won or cancelled; what was stolen stays stolen. A new /event thieves starts it again, a round reset clears it.")
        .Build();

    readonly StateMachine<ThievesPhase> _fsm = new(Spec);
    RaidsConfig _cfg = new();
    Countdown _next;
    int _launched, _lastActive;

    public ThievesPhase Phase => _fsm.Current;
    public bool Running => Phase == ThievesPhase.Raiding;
    /// <summary>Raids launched so far and how many the run has (<see cref="RaidsConfig.Count"/>).</summary>
    public int Launched => _launched;
    public int Total => _cfg.Count;

    /// <summary>Starts a run: the first Raid is due at once. False when one already runs.</summary>
    public bool Begin(RaidsConfig cfg)
    {
        if (Running) return false;
        _cfg = cfg; _launched = 0; _lastActive = 0; _next.Cancel();
        return _fsm.Enter(ThievesPhase.Raiding);
    }

    /// <summary>
    /// Advances the run; <paramref name="activeVans"/> is how many Raids have a van in the village (not driven off or destroyed). <see cref="ThievesStep.Launch"/> once for every Raid
    /// that is due (the caller launches it); <see cref="ThievesStep.Won"/> once the last Raid is over (the caller finishes the run with <see cref="Finish"/>).
    /// </summary>
    public ThievesStep Tick(float dt, int activeVans)
    {
        if (!Running) return ThievesStep.None;
        _next.Tick(dt);
        if (activeVans < _lastActive && _launched < _cfg.Count) _next.Start(MathF.Min(_next.Left, _cfg.GapSeconds)); // a van left or was destroyed: the next comes soon
        _lastActive = activeVans;
        if (_launched < _cfg.Count)
        {
            if (activeVans >= _cfg.MaxVans || (_launched > 0 && !_next.Ready)) return ThievesStep.None;
            _launched++;
            _lastActive = activeVans + 1;
            _next.Start(_cfg.NextRaidSeconds);
            return ThievesStep.Launch;
        }
        return activeVans == 0 ? ThievesStep.Won : ThievesStep.None;
    }

    /// <summary>Ends the run (won or cancelled: the book records which). False when none runs.</summary>
    public bool Finish() => Running && _fsm.Enter(ThievesPhase.Over);

    /// <summary>A new round (or the end of the match): back to idle from whatever it was. False when it already was.</summary>
    public bool Reset() => _fsm.Enter(ThievesPhase.Idle);

    /// <summary>The announcement on the intel board: village-wide, under way at once (an admin starts it; there is no lead time). Withdrawn when the run ends.</summary>
    public static IntelEvent Intel(double now) => new(
        IntelId, "thieves", "THIEVES IN THE VILLAGE",
        "Gangs of thieves are driving into the village in vans to steal tires, pets, dishes and power line connectors. They only attack those who get in their way. Whatever they carry off in a van is gone until the round resets.",
        new[] { "Thieves fight back when you shoot one of them or stand in their way", "Enforcers carry rusty pipes", "Loot lost in a van stays lost" },
        now);
}
