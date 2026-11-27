using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Jobs;

/// <summary>Where a Job is (machine <c>Job.Run</c>). The wire carries the byte (0 is "no job"): do not renumber.</summary>
public enum JobState : byte
{
    Active = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>Why a Job failed (docs/cartel.md "Failure"). The wire carries the byte: do not renumber.</summary>
public enum JobFailReason : byte
{
    None = 0,
    /// <summary>Its timer ran out.</summary>
    Timer = 1,
    /// <summary>The round ended or was restarted.</summary>
    RoundReset = 2,
    /// <summary>The leader quit the giver (the Cartel).</summary>
    LeaderQuit = 3,
    /// <summary>The solo player died, or in a Squad its leader.</summary>
    Death = 4,
    /// <summary>Nobody is left to run it: the solo player left the server, or the whole Squad.</summary>
    Abandoned = 5,
    /// <summary>The kind's own failure (a Heist's Cartel van destroyed).</summary>
    Objective = 6,
}

/// <summary>Who runs a Job: one player alone, or a Squad. The wire carries the byte.</summary>
public enum JobOwnerKind : byte { Solo = 1, Squad = 2 }

/// <summary>The owner of a Job: a player's id (<see cref="JobOwnerKind.Solo"/>) or a Squad's id. One running Job per owner.</summary>
public readonly record struct JobOwner(JobOwnerKind Kind, ushort Id)
{
    public static JobOwner Solo(ushort player) => new(JobOwnerKind.Solo, player);
    public static JobOwner Squad(byte squad) => new(JobOwnerKind.Squad, squad);
    public override string ToString() => Kind == JobOwnerKind.Solo ? $"player #{Id}" : $"squad {Id}";
}

/// <summary>
/// One Job (GLOSSARY "Job", ADR 0045): a task a giver hands out, of a kind, at a tier, run by an owner (a solo player or a Squad, started by its leader) against a timer, with an
/// objective of its kind (<see cref="IJobObjective"/>). Many run at once, beside any Event; a <see cref="JobBook"/> holds them. Not an Event.
/// </summary>
public sealed class Job
{
    public static readonly StateMachineSpec<JobState> RunSpec = StateMachineSpec<JobState>
        .Define("Job.Run", "One Job from its start to its end: active against its timer until its objective succeeds, or it fails (the timer, a round reset, the leader quitting the giver, the solo player's or the leader's death, nobody left, the kind's own failure).", JobState.Active)
        .Allow(JobState.Active, JobState.Succeeded, JobState.Failed)
        .Note(JobState.Active, "the objective runs and the timer counts down")
        .Note(JobState.Succeeded, "the objective was met: every earning member of the party at that moment is paid")
        .Note(JobState.Failed, "pays nothing; the reason says why")
        .Build();

    readonly StateMachine<JobState> _run = new(RunSpec);

    internal Job(int id, string giver, IJobKind kind, int tier, JobOwner owner, ushort leader, float seconds)
    {
        Id = id; Giver = giver; Kind = kind; Tier = tier; Owner = owner; Leader = leader; Seconds = seconds;
        Party = new JobParty(owner);
        _run.Restart(seconds);
    }

    /// <summary>Unique for the server's run, never 0 (0 on the wire is "no job").</summary>
    public int Id { get; }
    /// <summary>Who handed it out (<c>"cartel"</c>; bus and ambulance jobs later).</summary>
    public string Giver { get; }
    public IJobKind Kind { get; }
    public int Tier { get; }
    public JobOwner Owner { get; }
    /// <summary>The owner's members as of the last tick (the book refreshes it): the one set the Job's Duffels, crews and Drill read.</summary>
    public JobParty Party { get; }
    /// <summary>The leader as of the last tick: the solo player, or the Squad's leader (who may change while it runs).</summary>
    public ushort Leader { get; internal set; }
    /// <summary>The timer it started with.</summary>
    public float Seconds { get; }
    public IJobObjective Objective { get; internal set; } = null!;
    public JobState State => _run.Current;
    public bool Active => _run.Is(JobState.Active);
    public JobFailReason Reason { get; private set; }
    /// <summary>Seconds left on the timer (0 or less once it ran out).</summary>
    public float SecondsLeft => _run.Left;
    /// <summary>Seconds since it started (while active).</summary>
    public float Elapsed => _run.Elapsed;

    /// <summary>Advances the timer; true when it ran out.</summary>
    internal bool Tick(float dt) => _run.Tick(dt);

    internal void Finish(JobState end, JobFailReason reason)
    {
        Reason = end == JobState.Failed ? reason : JobFailReason.None;
        _run.Enter(end);
    }

    public override string ToString() => $"job {Id} {Giver}/{Kind.Id} tier {Tier} ({Owner}, {State})";
}
