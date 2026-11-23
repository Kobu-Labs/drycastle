using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Demon;

/// <summary>
/// Where the demon of the Holy Mass is in its life, as the server runs it. Three enums describe one lifecycle: this one (the server's stages, the scripted
/// part around the brain), <see cref="DemonPhase"/> (what the brain does while <see cref="Fighting"/>) and <see cref="DemonState"/> (the byte on the wire).
/// <see cref="DemonLifecycle.WireStateOf(DemonStage)"/> and <see cref="DemonLifecycle.WireStateOf(DemonPhase)"/> are the only mapping between them.
/// </summary>
public enum DemonStage : byte { None, Delay, Transforming, Climbing, Fighting, Dying }

/// <summary>
/// The demon's lifecycle (docs/events/README.md, "Demon"): the stage machine and its clock, Godot-free. The server (NetServer.Demon) keeps only the world wiring
/// (the entity, the brain, the doors, the fireballs) and asks this for "which stage, how long left, what next". Durations come from
/// <see cref="DemonFightDef"/>; one shared countdown runs the timed stages, exactly like the hand-written <c>_dTime -= dt</c> it replaces.
/// <code>
/// Begin()                  None -> Delay          the pastor turned; the dead pastor still lies there
/// Tick(dt) true: Transform Delay -> Transforming  the pastor's body goes, the demon burns into being (only once its entity exists)
/// Tick(dt) true: Climb     Transforming -> Climbing   the scripted leap to the first anchor
/// Tick(dt) true: Fight     Climbing -> Fighting   the brain starts (it can be hurt from here)
/// Die()                    Fighting -> Dying      from inside a weapon hit, not the tick; the corpse lies a while
/// Tick(dt) true: then End  Dying -> None          the event is over
/// Clear()                  any -> None            the event ended, the entity vanished, the spawn failed
/// </code>
/// </summary>
public sealed class DemonLifecycle
{
    public static readonly StateMachineSpec<DemonStage> Spec = StateMachineSpec<DemonStage>.Define(
            "Demon.Stage", "The server's script around the demon brain: the pastor turns, burns into the demon, climbs the wall, fights, dies; anything can end it early.", DemonStage.None)
        .Allow(DemonStage.None, DemonStage.Delay)
        .Allow(DemonStage.Delay, DemonStage.Transforming, DemonStage.None)
        .Allow(DemonStage.Transforming, DemonStage.Climbing, DemonStage.None)
        .Allow(DemonStage.Climbing, DemonStage.Fighting, DemonStage.None)
        .Allow(DemonStage.Fighting, DemonStage.Dying, DemonStage.None)
        .Allow(DemonStage.Dying, DemonStage.None)
        .Note(DemonStage.None, "No demon: the Mass is not void or the fight is cleared. Nothing ticks.")
        .Note(DemonStage.Delay, "The pastor has turned; his body still lies there for transform_delay_s. A failed spawn (entity cap) clears from here: the doors must still open.")
        .Note(DemonStage.Transforming, "The pastor's body is gone, the monks burn, the demon entity exists and cannot be hit (transform_s).")
        .Note(DemonStage.Climbing, "Scripted leap from the floor to the first anchor (climb_s); the entity is replicated as Crawling. Not yet hittable.")
        .Note(DemonStage.Fighting, "The DemonBrain runs; the demon is a hit target. Ends by its death (a weapon hit) or by a clear.")
        .Note(DemonStage.Dying, "The corpse lies for corpse_s; fireballs in the air still fly. Then the server ends the Mass.")
        .Build();

    readonly Func<DemonFightDef> _fightOf;
    readonly StateMachine<DemonStage> _m = new(Spec);

    /// <param name="fight">The durations; read on every use, so a host that swaps its data file is followed.</param>
    public DemonLifecycle(Func<DemonFightDef> fight) => _fightOf = fight;

    public DemonLifecycle(DemonFightDef fight) : this(() => fight) { }

    DemonFightDef _fight => _fightOf();

    public DemonStage Stage => _m.Current;
    /// <summary>A demon is in the church (any stage): the Mass is void and nothing else can start it.</summary>
    public bool Active => _m.Current != DemonStage.None;
    public bool Is(DemonStage stage) => _m.Is(stage);
    /// <summary>Seconds left of the timed stage (Delay, Transforming, Climbing, Dying); infinite while Fighting.</summary>
    public float Left => _m.Left;

    /// <summary>The pastor turned: Delay starts. False (nothing changes) when a demon is already here.</summary>
    public bool Begin() => !Active && _m.Enter(DemonStage.Delay, _fight.TransformDelayS);

    /// <summary>Why a demon cannot start with these settings, or null: holy_mass_demon.json is not loaded (no hits to kill it).</summary>
    public static string? WhyNot(DemonFile demon) =>
        demon.Hits.Base <= 0 ? "holy_mass_demon.json is not loaded" : null;

    /// <summary>Advances the clock by one tick. True when the current timed stage ran out (the caller then does that stage's work and moves on). Not active: false, nothing runs.</summary>
    public bool Tick(float dt) => Active && _m.Tick(dt);

    /// <summary>Delay is over and the entity exists: the turning starts. (Called only after the spawn worked; a failed spawn clears instead.)</summary>
    public void BeginTransform() => _m.Enter(DemonStage.Transforming, _fight.TransformS);

    /// <summary>The turning is over: the leap to the first anchor starts.</summary>
    public void BeginClimb() => _m.Enter(DemonStage.Climbing, _fight.ClimbS);

    /// <summary>The leap is over: the brain takes over.</summary>
    public void BeginFight() => _m.Enter(DemonStage.Fighting);

    /// <summary>The brain's demon died (called from inside a weapon hit): the corpse lies for the configured time.</summary>
    public void Die() => _m.Enter(DemonStage.Dying, _fight.CorpseS);

    /// <summary>Ends the life from any stage; returns the stage it was in (None: there was nothing to clear).</summary>
    public DemonStage Clear()
    {
        var was = _m.Current;
        _m.Enter(DemonStage.None);
        return was;
    }

    /// <summary>The leap's progress, 0 at its start to 1 at its end (the stage's countdown against <c>climb_s</c>).</summary>
    public float ClimbProgress => 1f - Math.Clamp(_m.Left / _fight.ClimbS, 0f, 1f);

    /// <summary>
    /// The state byte the clients see while the server's own script (not the brain) moves the demon: Transforming; Climbing goes out as Crawling (the leap
    /// is drawn as a crawl); Dying as Dead. Fighting is the brain's: use <see cref="WireStateOf(DemonPhase)"/>.
    /// </summary>
    public static DemonState WireStateOf(DemonStage stage) => stage switch
    {
        DemonStage.Transforming => DemonState.Transforming,
        DemonStage.Climbing => DemonState.Crawling,
        _ => DemonState.Dead,
    };

    /// <summary>The state byte for what the brain does: <see cref="DemonRules.StateOf(DemonPhase)"/>, here beside the stage mapping so the server has one place to ask.</summary>
    public static DemonState WireStateOf(DemonPhase phase) => DemonRules.StateOf(phase);
}
