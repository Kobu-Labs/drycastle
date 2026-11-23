using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Companions;

/// <summary>What a <see cref="HeelCompanion"/> is doing.</summary>
public enum HeelState : byte { Stay, Follow, Run, Sit }

/// <summary>The numbers of the "heel" kind (the <c>rules</c> of its companions.json entry). Lengths in metres, times in seconds.</summary>
public sealed class HeelRules
{
    /// <summary>Its owner farther than this: it gets up and follows.</summary>
    public float FollowM { get; set; } = 3.5f;
    /// <summary>Its owner nearer than this: it has arrived and stays.</summary>
    public float StopM { get; set; } = 1.8f;
    /// <summary>Its owner farther than this: it runs; it walks again once within <see cref="FollowM"/>.</summary>
    public float RunM { get; set; } = 7f;
    /// <summary>Its owner farther than this: it is put beside them at once.</summary>
    public float RecallM { get; set; } = 30f;
    /// <summary>Unable to take a step for this long while following: it is put beside its owner.</summary>
    public float StuckSeconds { get; set; } = 2f;
    /// <summary>Walk and run speed, m/s; 0: the species' own (animals.json).</summary>
    public float WalkSpeed { get; set; }
    public float RunSpeed { get; set; }
    /// <summary>Staying for a while between these, it sits down. Both 0: it never sits.</summary>
    public float SitAfterMinSeconds { get; set; } = 6f;
    public float SitAfterMaxSeconds { get; set; } = 15f;

    public void Check(string what)
    {
        if (!(StopM > 0) || !(FollowM > StopM) || !(RunM > FollowM) || !(RecallM > RunM)) throw new DataFileException($"{what}: 0 < stop_m < follow_m < run_m < recall_m");
        if (!(StuckSeconds > 0)) throw new DataFileException($"{what}: stuck_seconds must be positive");
        if (WalkSpeed < 0 || RunSpeed < 0) throw new DataFileException($"{what}: walk_speed and run_speed are 0 (the species') or positive");
        if (SitAfterMinSeconds < 0 || SitAfterMaxSeconds < SitAfterMinSeconds) throw new DataFileException($"{what}: sit_after_min_seconds is not negative and not above sit_after_max_seconds");
    }
}

/// <summary>
/// The "heel" kind: a land Companion that keeps to its owner's heel. It stays where its owner stands (and sits down after a while), walks after them when they move off, runs
/// when they get away, and is put beside them when it falls too far behind or cannot get there. All of its rules are here.
/// </summary>
public sealed class HeelCompanion : Companion
{
    public static readonly CompanionKind Kind = new("heel", s => new HeelCompanion(s), d => RulesOf(d));

    public static readonly StateMachineSpec<HeelState> Spec = StateMachineSpec<HeelState>.Define(
            "Companion.Heel", "What a land Companion of the heel kind is doing: stay by its owner, sit, walk or run after them. Falling too far behind or being stuck puts it beside its owner at once (no state: it then stays).", HeelState.Stay)
        .Allow(HeelState.Stay, HeelState.Follow, HeelState.Sit)
        .Allow(HeelState.Sit, HeelState.Follow)
        .Allow(HeelState.Follow, HeelState.Stay, HeelState.Run)
        .Allow(HeelState.Run, HeelState.Follow, HeelState.Stay)
        .Note(HeelState.Stay, "Stands near its owner (clock: a roll of sit_after seconds, then it sits; no clock when it never sits). Its owner farther than follow_m: Follow.")
        .Note(HeelState.Sit, "Sits (no clock) until its owner is farther than follow_m: Follow.")
        .Note(HeelState.Follow, "Walks to its owner. Within stop_m: Stay. Farther than run_m: Run. Farther than recall_m, or no step for stuck_seconds: put beside its owner, Stay.")
        .Note(HeelState.Run, "Runs to its owner. Within follow_m: Follow. Farther than recall_m, or no step for stuck_seconds: put beside its owner, Stay.")
        .Build();

    readonly StateMachine<HeelState> _fsm = new(Spec);
    readonly HeelRules _rules;
    Lifetime _stuck; // seconds in a row without a step while following

    static HeelRules RulesOf(CompanionDef def)
    {
        var r = DataFile.Section<HeelRules>(def.Rules, $"companions.json: {def.Id}: rules");
        r.Check($"companions.json: {def.Id}: rules");
        return r;
    }

    HeelCompanion(CompanionSpawn s) : base(s)
    {
        _rules = RulesOf(s.Def);
        Stay();
    }

    public HeelState State => _fsm.Current;
    public override string StateName => State.ToString();

    float WalkSpeed => _rules.WalkSpeed > 0 ? _rules.WalkSpeed : Animal.WalkSpeed;
    float RunSpeed => _rules.RunSpeed > 0 ? _rules.RunSpeed : Animal.RunSpeed;

    protected override void Think(float dt)
    {
        var d = OwnerDistance;
        switch (State)
        {
            case HeelState.Stay:
                if (d > _rules.FollowM) { Go(HeelState.Follow); break; }
                if (_fsm.Tick(dt)) Go(HeelState.Sit);
                break;
            case HeelState.Sit:
                if (d > _rules.FollowM) Go(HeelState.Follow);
                break;
            case HeelState.Follow:
                if (d <= _rules.StopM) { Stay(); break; }
                if (d > _rules.RunM) { Go(HeelState.Run); break; }
                Chase(WalkSpeed, d, dt);
                break;
            case HeelState.Run:
                if (d <= _rules.FollowM) { Go(HeelState.Follow); break; }
                Chase(RunSpeed, d, dt);
                break;
        }
        Pose = State switch { HeelState.Follow => CompanionPose.Walk, HeelState.Run => CompanionPose.Run, HeelState.Sit => CompanionPose.Sit, _ => CompanionPose.Idle };
    }

    /// <summary>A step towards the owner; too far behind, or no step for too long, it is put beside them.</summary>
    void Chase(float speed, float distance, float dt)
    {
        var moved = StepTo(Master.Position, speed, dt);
        if (moved) _stuck.Reset();
        if (distance <= _rules.RecallM && (moved || !_stuck.Tick(dt, _rules.StuckSeconds))) return;
        Recall();
        Stay();
    }

    void Go(HeelState s)
    {
        _stuck.Reset();
        _fsm.Enter(s);
    }

    /// <summary>Stands, with the clock to its next sit (none when it never sits).</summary>
    void Stay()
    {
        _stuck.Reset();
        var sits = _rules.SitAfterMaxSeconds > 0;
        var seconds = sits ? Between(_rules.SitAfterMinSeconds, _rules.SitAfterMaxSeconds) : float.PositiveInfinity;
        if (_fsm.Is(HeelState.Stay)) _fsm.Restart(seconds);
        else _fsm.Enter(HeelState.Stay, seconds);
    }
}
