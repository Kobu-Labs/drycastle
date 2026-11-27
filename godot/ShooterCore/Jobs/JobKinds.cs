namespace Drycastle.ShooterCore.Jobs;

/// <summary>What an objective's step says (<see cref="IJobObjective.Step"/>).</summary>
public enum JobStep : byte { Running, Succeeded, Failed }

/// <summary>
/// A kind of Job (the Cartel's Delivery, Hit, Harvest, Trap house; the test kind): its timer by tier and how it begins. Kinds plug into the <see cref="JobBook"/>
/// (<see cref="JobBook.Register"/>); the core knows none of them. A kind gets what it needs of the world from its constructor.
/// </summary>
public interface IJobKind
{
    /// <summary>The stable id (<c>"test"</c>, <c>"delivery"</c>): what the wire, the Contact's list and <c>/cartel job</c> name.</summary>
    string Id { get; }
    /// <summary>What players read ("Delivery").</summary>
    string Name { get; }
    /// <summary>The timer of a Job of that tier, seconds.</summary>
    float Seconds(int tier);
    /// <summary>
    /// The objective of a new Job, or null with why not (no free drop point). It may claim named things of the world in the book first (<see cref="JobBook.Reserve"/>: a drop
    /// point, a target spot, a house), so two Jobs never share one; a refusal releases them.
    /// </summary>
    IJobObjective? Begin(Job job, JobBook book, out string? refusal);
}

/// <summary>One running Job's objective: the kind's own state (a duffel, a target, a van), stepped by the book while the Job is active.</summary>
public interface IJobObjective
{
    /// <summary>Each tick while active, before the timer: still running, met (success), or lost (the kind's own failure, <see cref="JobFailReason.Objective"/>).</summary>
    JobStep Step(Job job, float dt);
    /// <summary>Respect on top of the tier's, paid at success (a heist's loaded duffels); 0 for most kinds.</summary>
    int BonusRespect => 0;
    /// <summary>The Job ended, whichever way: what the kind put in the world goes (NPCs, duffels, the van).</summary>
    void End(Job job) { }
}
