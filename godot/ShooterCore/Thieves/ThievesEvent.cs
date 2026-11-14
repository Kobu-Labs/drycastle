namespace Drycastle.ShooterCore;

/// <summary>Where the Thieves event is: not started, its Raids are in the village, or it is over (won or cancelled; what was stolen stays stolen until the round resets).</summary>
public enum ThievesPhase : byte { Idle, Raiding, Over }

/// <summary>
/// The Thieves event's own phase (GLOSSARY "Thieves event", "Raid"), pure. The lifecycle every Event shares (running, won, despawned) is the
/// <see cref="EventRun"/> of the <see cref="EventBook"/>; this is what the kind adds. For now it only keeps time: <see cref="Begin"/> starts a placeholder
/// timer and <see cref="Tick"/> ends the run when it runs out. The Raids, the vans and the Loot replace the timer (the event is won when the last van is gone).
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
        .Note(ThievesPhase.Raiding, "The Raids come and go (clock: the placeholder timer until the Raids exist). Left by Finish (won or cancelled) or by a round reset.")
        .Note(ThievesPhase.Over, "Won or cancelled; what was stolen stays stolen. A new /event thieves starts it again, a round reset clears it.")
        .Build();

    readonly StateMachine<ThievesPhase> _fsm = new(Spec);

    public ThievesPhase Phase => _fsm.Current;
    public bool Running => Phase == ThievesPhase.Raiding;

    /// <summary>Starts a run; <paramref name="placeholderSeconds"/> is how long it lasts until the Raids end it. False when one already runs.</summary>
    public bool Begin(float placeholderSeconds) => !Running && _fsm.Enter(ThievesPhase.Raiding, placeholderSeconds);

    /// <summary>Advances the run. True once, on the tick the placeholder timer runs out (the caller then wins the event with <see cref="Finish"/>).</summary>
    public bool Tick(float dt) => Running && _fsm.Tick(dt);

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
