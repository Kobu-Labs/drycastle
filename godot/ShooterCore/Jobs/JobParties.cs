using Drycastle.ShooterCore.Match;

namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// Who runs a Job (docs/cartel.md "Who runs a job"): the owner a player's start would make, the owner's leader now and its members now. In ffa a Job is solo; in tdm a player's
/// Squad runs it, led by the Squad's leader; a player in no Squad runs it alone.
/// </summary>
public interface IJobParties
{
    /// <summary>The owner of a Job this player starts: their Squad (tdm), else themselves.</summary>
    JobOwner OwnerOf(ushort player);
    /// <summary>The owner's leader now: the solo player while in the match, the Squad's leader; 0 when the owner is gone.</summary>
    ushort LeaderOf(JobOwner owner);
    /// <summary>The owner's members now, the leader among them (empty when the owner is gone).</summary>
    IReadOnlyList<ushort> MembersOf(JobOwner owner);
}

/// <summary>
/// <see cref="IJobParties"/> from the match: the players and their Squads. SEAM (#494 / the Squad leader package): MatchSim has no Squad leader yet, so the leader is the Squad's
/// first member in id order (<see cref="MatchSim.SquadMembers"/>); once MatchSim names the leader (the creator, then the longest-standing member), <see cref="SquadLeader"/>
/// reads it and nothing else here changes.
/// </summary>
public sealed class MatchJobParties(MatchSim match) : IJobParties
{
    public JobOwner OwnerOf(ushort player) =>
        match.IsTeamMode && match.Get(player) is { Squad: not 0 } p ? JobOwner.Squad(p.Squad) : JobOwner.Solo(player);

    public ushort LeaderOf(JobOwner owner) => owner.Kind == JobOwnerKind.Solo
        ? (match.Get(owner.Id) != null ? owner.Id : (ushort)0)
        : SquadLeader((byte)owner.Id);

    public IReadOnlyList<ushort> MembersOf(JobOwner owner) => owner.Kind == JobOwnerKind.Solo
        ? (match.Get(owner.Id) != null ? new[] { owner.Id } : Array.Empty<ushort>())
        : match.SquadMembers((byte)owner.Id).Select(p => p.Id).ToList();

    /// <summary>The Squad's leader, 0 when the Squad is gone. SEAM: its first member until MatchSim keeps the leader.</summary>
    ushort SquadLeader(byte squad)
    {
        if (!match.IsTeamMode) return 0;
        var members = match.SquadMembers(squad);
        return members.Count > 0 ? members[0].Id : (ushort)0;
    }
}
