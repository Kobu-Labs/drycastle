using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Intel;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>Where the Thieves event is: not started, its wave of vans is in the village, or it is over (won or cancelled; what was stolen stays stolen until the round resets).</summary>
public enum ThievesPhase : byte { Idle, Raiding, Over }

/// <summary>What <see cref="ThievesEvent.Tick"/> asks the server to do.</summary>
public enum ThievesStep : byte { None, Launch, Won }

/// <summary>
/// The Thieves event's own phase and wave (GLOSSARY "Thieves event", "Raid"), pure. The lifecycle every Event shares (running, won, despawned) is the
/// <see cref="EventRun"/> of the <see cref="EventBook"/>; this is what the kind adds: one wave of <see cref="RaidsConfig.VansFor"/> vans that arrives in a convoy. The first van is
/// launched at once, each next one <see cref="RaidsConfig.ConvoyGapSeconds"/> after the one before. When every van of the wave is launched and none is left in the village (destroyed or
/// driven off), the event is won. It cannot be lost.
/// </summary>
public sealed class ThievesEvent
{
    /// <summary>The intel board's id of the event's announcement.</summary>
    public const string IntelId = "thieves";

    public static readonly StateMachineSpec<ThievesPhase> Spec = StateMachineSpec<ThievesPhase>
        .Define("Thieves.Phase", "The Thieves event: idle until started, raiding while its wave of vans is in the village, then over until the round resets.", ThievesPhase.Idle)
        .Allow(ThievesPhase.Idle, ThievesPhase.Raiding)
        .Allow(ThievesPhase.Raiding, ThievesPhase.Over, ThievesPhase.Idle)
        .Allow(ThievesPhase.Over, ThievesPhase.Raiding, ThievesPhase.Idle)
        .Note(ThievesPhase.Idle, "Nothing runs: never started, or a round reset cleared it.")
        .Note(ThievesPhase.Raiding, "The convoy's vans are launched one after another (no clock of its own: the convoy-gap clock runs beside) and come and go. Left by Finish (won when every van of the wave is gone, or an admin's end or cancel) or by a round reset.")
        .Note(ThievesPhase.Over, "Won or cancelled; what was stolen stays stolen. A new /event thieves starts it again, a round reset clears it.")
        .Build();

    readonly StateMachine<ThievesPhase> _fsm = new(Spec);
    RaidsConfig _cfg = new();
    Countdown _next;
    int _launched, _vans;

    public ThievesPhase Phase => _fsm.Current;
    public bool Running => Phase == ThievesPhase.Raiding;
    /// <summary>Vans launched so far and how many the wave has.</summary>
    public int Launched => _launched;
    public int Total => _vans;

    /// <summary>Starts a run whose wave has a random number of vans (<see cref="RaidsConfig.VansFor"/>): the first van is due at once. False when one already runs.</summary>
    public bool Begin(RaidsConfig cfg, Random rng)
    {
        if (Running) return false;
        _cfg = cfg; _launched = 0; _vans = cfg.VansFor(rng); _next.Cancel();
        return _fsm.Enter(ThievesPhase.Raiding);
    }

    /// <summary>
    /// Advances the run; <paramref name="activeVans"/> is how many vans are in the village (not driven off or destroyed). <see cref="ThievesStep.Launch"/> once for every van of the wave that is
    /// due (the caller launches it); <see cref="ThievesStep.Won"/> once the whole wave is launched and none is left (the caller finishes the run with <see cref="Finish"/>).
    /// </summary>
    public ThievesStep Tick(float dt, int activeVans)
    {
        if (!Running) return ThievesStep.None;
        _next.Tick(dt);
        if (_launched < _vans)
        {
            if (_launched > 0 && !_next.Ready) return ThievesStep.None;
            _launched++;
            _next.Start(_cfg.ConvoyGapSeconds);
            return ThievesStep.Launch;
        }
        return activeVans == 0 ? ThievesStep.Won : ThievesStep.None;
    }

    /// <summary>The van just asked for by <see cref="ThievesStep.Launch"/> could not be placed (no free stop): the wave is that many vans smaller and no more are launched.</summary>
    public void Shrink()
    {
        _vans = Math.Max(0, _launched - 1);
        _launched = _vans;
    }

    /// <summary>Ends the run (won or cancelled: the book records which). False when none runs.</summary>
    public bool Finish() => Running && _fsm.Enter(ThievesPhase.Over);

    /// <summary>A new round (or the end of the match): back to idle from whatever it was. False when it already was.</summary>
    public bool Reset() => _fsm.Enter(ThievesPhase.Idle);

    /// <summary>The announcement on the intel board: village-wide, under way at once (an admin starts it; there is no lead time). Withdrawn when the run ends.</summary>
    public static IntelEvent Intel(double now) => new(
        IntelId, "thieves", "THIEVES IN THE VILLAGE",
        "Gangs of thieves are driving into the village in a convoy of vans, which split up to steal tires, pets, dishes and power line connectors. They only fight back while you keep attacking them, and the vans run over what is in their way. Whatever they carry off in a van is gone until the round resets.",
        new[] { "Thieves fight back only while you keep attacking them", "The vans come in a convoy of one to three and split in the village", "Enforcers carry rusty pipes", "Loot lost in a van stays lost" },
        now);
}
