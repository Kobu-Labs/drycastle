using Drycastle.ShooterCore.Match;

namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// Who runs a Job (docs/cartel.md "Who runs a job"): the owner a player's start would make, the owner's leader now and its members now. In ffa a Job is solo; in tdm a player's
/// Squad runs it, led by the Squad's leader; a player in no Squad runs it alone. A player is in one party at most: a solo player who joins a Squad leaves their solo Job.
/// </summary>
public interface IJobParties
{
    /// <summary>The owner of a Job this player starts: their Squad (tdm), else themselves.</summary>
    JobOwner OwnerOf(ushort player);
    /// <summary>The owner's leader now: the solo player while in the match (and, in tdm, in no Squad), the Squad's leader; 0 when the owner is gone.</summary>
    ushort LeaderOf(JobOwner owner);
    /// <summary>The owner's members now, the leader among them (empty when the owner is gone).</summary>
    IReadOnlyList<ushort> MembersOf(JobOwner owner);
    /// <summary>
    /// The player is one of the owner's members now. Asked per running Job per player per tick (whose Job a player is in, what the feed tells them): an implementation answers it
    /// without allocating (this default scans <see cref="MembersOf"/>, for hand-built parties in tests). What a Job puts in the world reads its <see cref="Job.Party"/> instead.
    /// </summary>
    bool Has(JobOwner owner, ushort player)
    {
        foreach (var m in MembersOf(owner)) if (m == player) return true;
        return false;
    }
}

/// <summary>
/// <see cref="IJobParties"/> from the match: the players, their Squads and each Squad's leader. A Solo owner who is in a tdm Squad now is gone (their Squad's Job is theirs), so the
/// solo Job fails as <see cref="JobFailReason.Abandoned"/> at the next tick and nobody is in two parties.
/// </summary>
public sealed class MatchJobParties(MatchSim match) : IJobParties
{
    public JobOwner OwnerOf(ushort player) =>
        match.IsTeamMode && match.Get(player) is { Squad: not 0 } p ? JobOwner.Squad(p.Squad) : JobOwner.Solo(player);

    public ushort LeaderOf(JobOwner owner) => owner.Kind == JobOwnerKind.Solo
        ? (SoloPresent(owner.Id) ? owner.Id : (ushort)0)
        : SquadLeader((byte)owner.Id);

    public IReadOnlyList<ushort> MembersOf(JobOwner owner) => owner.Kind == JobOwnerKind.Solo
        ? (SoloPresent(owner.Id) ? new[] { owner.Id } : Array.Empty<ushort>())
        : match.SquadMembers((byte)owner.Id).Select(p => p.Id).ToList();

    public bool Has(JobOwner owner, ushort player) => owner.Kind == JobOwnerKind.Solo
        ? player == owner.Id && SoloPresent(player)
        : match.IsTeamMode && match.Get(player) is { Squad: var s } && s != 0 && s == owner.Id;

    /// <summary>The solo player is in the match and in no tdm Squad (one who joined a Squad left the solo Job).</summary>
    bool SoloPresent(ushort player) => match.Get(player) is { } p && !(match.IsTeamMode && p.Squad != 0);

    /// <summary>The Squad's leader (<see cref="MatchSim.SquadLeader"/>: the creator, then the longest-standing member), 0 when the Squad is gone.</summary>
    ushort SquadLeader(byte squad) => match.IsTeamMode ? match.SquadLeader(squad) : (ushort)0;
}
