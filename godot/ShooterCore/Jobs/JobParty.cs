namespace Drycastle.ShooterCore.Jobs;

/// <summary>
/// One Job's party as a set (docs/cartel.md "Who runs a job"): its owner's members as of the last tick. One stable instance per Job (<see cref="Job.Party"/>), what everything of the Job
/// reads: its Duffels belong to it (<see cref="Cartel.Duffel.Party"/>, removed together by reference), its Rival crews attack it on sight, its Drill takes its holds, the Cartel van carries
/// it. The <see cref="JobBook"/> fills it from <see cref="IJobParties"/> when the Job starts and once a tick before the objective steps, so a Squad mate who joins midway is in it from the
/// next tick and one who leaves is out; where a fresh joiner matters at once (a Duffel filled in their hands), <see cref="Refresh"/> it first. Asking it allocates nothing.
/// </summary>
public sealed class JobParty : IReadOnlySet<ushort>
{
    readonly HashSet<ushort> _members = new();

    public JobParty(JobOwner owner) => Owner = owner;

    public JobOwner Owner { get; }

    /// <summary>Counts the refreshes that changed who is in it: what follows the party (the Cartel van's riders) compares it with the one it last saw.</summary>
    public int Version { get; private set; }

    /// <summary>The owner's members now (<see cref="IJobParties.MembersOf"/>). True when that changed who is in it.</summary>
    public bool Refresh(IJobParties parties)
    {
        var now = parties.MembersOf(Owner);
        if (_members.SetEquals(now)) return false;
        _members.Clear();
        _members.UnionWith(now);
        Version++;
        return true;
    }

    public bool Contains(ushort player) => _members.Contains(player);
    public int Count => _members.Count;
    public HashSet<ushort>.Enumerator GetEnumerator() => _members.GetEnumerator();
    IEnumerator<ushort> IEnumerable<ushort>.GetEnumerator() => _members.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _members.GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<ushort> other) => _members.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<ushort> other) => _members.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<ushort> other) => _members.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<ushort> other) => _members.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<ushort> other) => _members.Overlaps(other);
    public bool SetEquals(IEnumerable<ushort> other) => _members.SetEquals(other);

    public override string ToString() => $"{Owner}: {string.Join(", ", _members)}";
}
